using System.Diagnostics;
using System.Text.Json;
using EmbyClient.App.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyClient.App.Views;

// Included only by LibraryObservation=true. The normal app has no implementation of these partial hooks.
public sealed partial class LibraryView
{
    private const int ObservationWeakCapacity = 2048;
    private const long ObservationByteLimit = 1024 * 1024;
    private static readonly TimeSpan ObservationDuration = TimeSpan.FromMinutes(30);
    private readonly ObservationWeakRing<Image> _observedImages = new(ObservationWeakCapacity, unique: true);
    private readonly ObservationWeakRing<BitmapImage> _observedBitmaps = new(ObservationWeakCapacity, unique: false);
    private DispatcherQueueTimer? _observationTimer;
    private FileStream? _observationOutput;
    private long _observationStarted;
    private long _observationBytes;
    private long _observedLoaded;
    private long _observedUnloaded;
    private long _observedTagChanged;
    private long _observedAssigned;
    private long _observedCleared;
    private long _observedLoadRequested;
    private long _observedLoadRejected;
    private long _observedLoadDeduplicated;
    private long _observedDecodeStarted;
    private long _observedDecodeCompleted;
    private long _observedDecodeCanceled;
    private long _observedDecodeFailed;
    private int _observedDecodeActive;
    private int _observedDecodePeak;
    private long _observationAllocatedBytes;
    private long _observationSamples;
    private bool _observationActive;

    partial void ObservationInitialize()
    {
        // Only the separately published observation directory receives a log. Never overwrite a prior run.
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "library-observation-build.json"))) return;
        try
        {
            _observationOutput = new FileStream(Path.Combine(AppContext.BaseDirectory, "library-observation.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _observationStarted = Stopwatch.GetTimestamp();
            _observationActive = true;
            LibraryRuntimeObservation.Enabled = true;
            Unloaded += ObservationViewUnloaded;
            _observationTimer = DispatcherQueue.CreateTimer();
            _observationTimer.Interval = TimeSpan.FromSeconds(5);
            _observationTimer.Tick += ObservationTick;
            WriteObservationSample();
            if (_observationActive) _observationTimer.Start();
        }
        catch (Exception) { StopObservation(); }
    }

    partial void ObservationPosterLoaded(Image image)
    {
        if (!_observationActive) return;
        _observedLoaded++;
        _observedImages.Observe(image);
    }

    partial void ObservationPosterUnloaded(Image image)
    {
        if (!_observationActive) return;
        _observedUnloaded++;
        _observedImages.Observe(image);
    }

    partial void ObservationPosterTagChanged(DependencyObject sender)
    {
        if (!_observationActive || sender is not Image image) return;
        _observedTagChanged++;
        _observedImages.Observe(image);
    }

    partial void ObservationBitmapDecoded(BitmapImage bitmap)
    {
        if (_observationActive) _observedBitmaps.Observe(bitmap);
    }

    partial void ObservationPosterAssigned(Image image)
    {
        if (!_observationActive) return;
        _observedAssigned++;
        _observedImages.Observe(image);
    }

    partial void ObservationPosterCleared(Image image)
    {
        if (!_observationActive) return;
        _observedCleared++;
        _observedImages.Observe(image);
    }

    partial void ObservationPosterLoadRequested()
    {
        if (_observationActive) _observedLoadRequested++;
    }

    partial void ObservationPosterLoadRejected()
    {
        if (_observationActive) _observedLoadRejected++;
    }

    partial void ObservationPosterLoadDeduplicated()
    {
        if (_observationActive) _observedLoadDeduplicated++;
    }

    private async Task ObservePosterDecodeAsync(byte[] bytes, int width, int height, CancellationToken cancellationToken,
        Action<BitmapImage> apply)
    {
        if (!_observationActive)
        {
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, height, cancellationToken, apply);
            return;
        }
        _observedDecodeStarted++;
        _observedDecodeActive++;
        _observedDecodePeak = Math.Max(_observedDecodePeak, _observedDecodeActive);
        try
        {
            // Observe the original helper, including stream creation, decoding, and its guarded apply callback.
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, width, height, cancellationToken, apply);
            _observedDecodeCompleted++;
        }
        catch (OperationCanceledException) { _observedDecodeCanceled++; throw; }
        catch { _observedDecodeFailed++; throw; }
        finally { _observedDecodeActive--; }
    }

    private void ObservationTick(DispatcherQueueTimer sender, object args) => WriteObservationSample();

    private void ObservationViewUnloaded(object sender, RoutedEventArgs args)
    {
        WriteObservationSample();
        StopObservation();
    }

    private void WriteObservationSample()
    {
        if (!_observationActive || _observationOutput is null) return;
        var elapsed = Stopwatch.GetElapsedTime(_observationStarted);
        if (elapsed >= ObservationDuration) { StopObservation(); return; }
        var sampleAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            // These methods observe normal collection activity; they do not request garbage collection.
            var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
            var gcInfo = GC.GetGCMemoryInfo();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var scale = XamlRoot?.RasterizationScale ?? 0;
            if (!double.IsFinite(scale) || scale < 0) scale = 0;
            // Read the current source without retaining an image or bitmap beyond this sample.
            var boundPosterSourcesCount = 0;
            var loadedPosterControlsCount = 0;
            var attachedPosterControlsCount = 0;
            var realizedPosterControlsCount = 0;
            foreach (var image in _posterSubscriptions.Keys)
            {
                if (image.Source is not null) boundPosterSourcesCount++;
                if (image.IsLoaded) loadedPosterControlsCount++;
                if (ReferenceEquals(image, DetailPoster)) continue;
                if (FindPosterContainer(image) is not { } container) continue;
                attachedPosterControlsCount++;
                if (_posterContainers.TryGetValue(container, out _)) realizedPosterControlsCount++;
            }
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("SchemaVersion", 2);
                writer.WriteNumber("UtcUnixTimeMilliseconds", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                writer.WriteNumber("ElapsedMilliseconds", (long)elapsed.TotalMilliseconds);
                writer.WriteNumber("ProcessId", Environment.ProcessId);
                writer.WriteNumber("ItemsCount", ViewModel.Items.Count);
                writer.WriteNumber("PosterSubscriptionsCount", _posterSubscriptions.Count);
                writer.WriteNumber("PosterRequestsCount", _posterRequests.Count);
                writer.WriteNumber("BoundPosterSourcesCount", boundPosterSourcesCount);
                writer.WriteNumber("LoadedPosterControlsCount", loadedPosterControlsCount);
                writer.WriteNumber("AttachedPosterControlsCount", attachedPosterControlsCount);
                writer.WriteNumber("RealizedPosterControlsCount", realizedPosterControlsCount);
                writer.WriteNumber("DetailPosterBound", DetailPoster.Source is null ? 0 : 1);
                writer.WriteNumber("LibraryIsHome", ViewModel.IsHome ? 1 : 0);
                writer.WriteNumber("LibraryHasDetails", ViewModel.HasDetails ? 1 : 0);
                writer.WriteNumber("LibraryIsBusy", ViewModel.IsBusy ? 1 : 0);
                writer.WriteNumber("LibraryVisibility", (int)Visibility);
                writer.WriteNumber("LoadedTotal", _observedLoaded);
                writer.WriteNumber("UnloadedTotal", _observedUnloaded);
                writer.WriteNumber("TagChangedTotal", _observedTagChanged);
                writer.WriteNumber("AssignedTotal", _observedAssigned);
                writer.WriteNumber("ClearedTotal", _observedCleared);
                writer.WriteNumber("PosterLoadRequestedTotal", _observedLoadRequested);
                writer.WriteNumber("PosterLoadRejectedTotal", _observedLoadRejected);
                writer.WriteNumber("PosterLoadDeduplicatedTotal", _observedLoadDeduplicated);
                writer.WriteNumber("DecodeStartedTotal", _observedDecodeStarted);
                writer.WriteNumber("DecodeCompletedTotal", _observedDecodeCompleted);
                writer.WriteNumber("DecodeCanceledTotal", _observedDecodeCanceled);
                writer.WriteNumber("DecodeFailedTotal", _observedDecodeFailed);
                writer.WriteNumber("DecodeActive", _observedDecodeActive);
                writer.WriteNumber("DecodePeak", _observedDecodePeak);
                writer.WriteNumber("PresentationClockEnabled", LibraryRuntimeObservation.ClockEnabled ? 1 : 0);
                writer.WriteNumber("PresentationClockTicks", LibraryRuntimeObservation.ClockTicks);
                writer.WriteNumber("PositionUpdateCalls", LibraryRuntimeObservation.PositionUpdateCalls);
                writer.WriteNumber("WeakImageTrackedCount", _observedImages.Count);
                writer.WriteNumber("WeakImageAliveCount", _observedImages.CountAlive());
                writer.WriteNumber("WeakImageEvictions", _observedImages.Evictions);
                writer.WriteNumber("WeakBitmapTrackedCount", _observedBitmaps.Count);
                writer.WriteNumber("WeakBitmapAliveCount", _observedBitmaps.CountAlive());
                writer.WriteNumber("WeakBitmapEvictions", _observedBitmaps.Evictions);
                writer.WriteNumber("Gen0Collections", gen0);
                writer.WriteNumber("Gen1Collections", gen1);
                writer.WriteNumber("Gen2Collections", gen2);
                writer.WriteNumber("ManagedBytes", managedBytes);
                writer.WriteNumber("ManagedAllocatedBytesApproximate", allocatedBytes);
                writer.WriteNumber("LastGcIndex", gcInfo.Index);
                writer.WriteNumber("LastGcHeapSizeBytes", gcInfo.HeapSizeBytes);
                writer.WriteNumber("LastGcFragmentedBytes", gcInfo.FragmentedBytes);
                writer.WriteNumber("LastGcCommittedBytes", gcInfo.TotalCommittedBytes);
                writer.WriteNumber("ProcessPrivateBytes", process.PrivateMemorySize64);
                writer.WriteNumber("ProcessWorkingSetBytes", process.WorkingSet64);
                writer.WriteNumber("ProcessHandleCount", process.HandleCount);
                writer.WriteNumber("ProcessCpuMilliseconds", (long)process.TotalProcessorTime.TotalMilliseconds);
                writer.WriteNumber("ObserverCompletedSamples", _observationSamples);
                writer.WriteNumber("ObserverCompletedSampleAllocatedBytes", _observationAllocatedBytes);
                writer.WriteNumber("RasterizationScale", scale);
                writer.WriteEndObject();
            }
            if (_observationBytes + buffer.Length + 1 > ObservationByteLimit) { StopObservation(); return; }
            buffer.Position = 0;
            buffer.CopyTo(_observationOutput);
            _observationOutput.WriteByte((byte)'\n');
            _observationOutput.Flush();
            _observationBytes += buffer.Length + 1;
            _observationSamples++;
        }
        catch (Exception) { StopObservation(); }
        finally { _observationAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - sampleAllocatedBefore; }
    }

    private void StopObservation()
    {
        _observationActive = false;
        LibraryRuntimeObservation.Enabled = false;
        Unloaded -= ObservationViewUnloaded;
        if (_observationTimer is { } timer)
        {
            _observationTimer = null;
            try { timer.Stop(); timer.Tick -= ObservationTick; }
            catch (Exception) { }
        }
        var output = _observationOutput;
        _observationOutput = null;
        try { output?.Dispose(); }
        catch (Exception) { }
    }

    private sealed class ObservationWeakRing<T>(int capacity, bool unique) where T : class
    {
        private readonly WeakReference<T>?[] _entries = new WeakReference<T>?[capacity];
        private int _next;
        public int Count { get; private set; }
        public long Evictions { get; private set; }

        public void Observe(T target)
        {
            if (unique)
            {
                for (var index = 0; index < Count; index++)
                    if (_entries[index]!.TryGetTarget(out var existing) && ReferenceEquals(existing, target)) return;
            }
            if (Count == _entries.Length) Evictions++;
            else Count++;
            _entries[_next] = new WeakReference<T>(target);
            _next = (_next + 1) % _entries.Length;
        }

        public int CountAlive()
        {
            var alive = 0;
            for (var index = 0; index < Count; index++)
                if (_entries[index]!.TryGetTarget(out _)) alive++;
            return alive;
        }
    }
}

// All hooks and this numeric state are compiled only into the explicit observation build.
internal static class LibraryRuntimeObservation
{
    internal static bool Enabled;
    internal static bool ClockEnabled;
    internal static long ClockTicks;
    internal static long PositionUpdateCalls;
}

public sealed partial class PlayerView
{
    partial void ObservationClockTick()
    {
        if (LibraryRuntimeObservation.Enabled) LibraryRuntimeObservation.ClockTicks++;
    }

    partial void ObservationPositionUpdate()
    {
        if (LibraryRuntimeObservation.Enabled) LibraryRuntimeObservation.PositionUpdateCalls++;
    }

    partial void ObservationClockState(bool enabled)
    {
        if (LibraryRuntimeObservation.Enabled) LibraryRuntimeObservation.ClockEnabled = enabled;
    }
}
