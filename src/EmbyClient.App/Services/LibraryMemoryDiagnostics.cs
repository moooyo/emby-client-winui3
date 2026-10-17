using System.Diagnostics;
using System.Text.Json;
using EmbyClient.App.Views.Lumen;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;

namespace EmbyClient.App.Services;

/// <summary>Opt-in, bounded scalar observations without changing collection or artwork policy.</summary>
/// <remarks>
/// Enable only with EMBY_CLIENT_LIBRARY_MEMORY_DIAGNOSTICS=1 and an existing, non-default
/// EMBY_CLIENT_DATA_ROOT. Weak wrapper counts and bitmap dimensions do not measure native or GPU bytes.
/// The cache counters measure in-memory compressed response arrays; disk caches are not observed.
/// </remarks>
internal static class LibraryMemoryDiagnostics
{
    private const string EnableEnvironmentVariable = "EMBY_CLIENT_LIBRARY_MEMORY_DIAGNOSTICS";
    private static readonly string? DiagnosticRoot = ResolveDiagnosticRoot();
    private static Session? _session;
    private static int _startAttempted;

    internal static bool Enabled => DiagnosticRoot is not null;

    internal static bool TrackArtwork(LumenArtwork artwork)
    {
        if (DiagnosticRoot is null) return false;
        try
        {
            if (_session is null && Interlocked.CompareExchange(ref _startAttempted, 1, 0) == 0)
            {
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                if (dispatcher is null) throw new InvalidOperationException("DiagnosticDispatcherUnavailable");
                var session = new Session(DiagnosticRoot, dispatcher);
                try { session.Start(); }
                catch { session.Stop("InitializationFailure", truncated: true); throw; }
                _session = session;
            }
            return _session?.TrackArtwork(artwork) == true;
        }
        catch (Exception exception)
        {
            WriteFailureMarker(DiagnosticRoot, "InitializationFailure", exception);
            return false;
        }
    }

    internal static void TrackCache(ImageCache cache)
    {
        try { _session?.TrackCache(cache); }
        catch (Exception exception) { ReadFailure(exception); }
    }

    internal static void SourceBound()
    {
        try { _session?.SourceBound(); }
        catch (Exception exception) { ReadFailure(exception); }
    }

    internal static void SourceClearing(bool hadSource)
    {
        try { _session?.SourceClearing(hadSource); }
        catch (Exception exception) { ReadFailure(exception); }
    }

    internal static void ReadFailure(Exception exception)
    {
        try { _session?.ReadFailure(exception); }
        catch (Exception) { /* Diagnostics must not change product error handling. */ }
    }

    private static string? ResolveDiagnosticRoot()
    {
        try
        {
            if (!string.Equals(Environment.GetEnvironmentVariable(EnableEnvironmentVariable), "1", StringComparison.Ordinal)) return null;
            var configured = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                ConfigurationWarning("isolated data root is missing");
                return null;
            }
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured));
            var normal = Path.TrimEndingDirectorySeparator(AppDataPaths.Resolve(null,
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            if (!Directory.Exists(root) || string.Equals(root, normal, StringComparison.OrdinalIgnoreCase))
            {
                ConfigurationWarning("an existing, non-default data root is required");
                return null;
            }
            return root;
        }
        catch (Exception exception)
        {
            ConfigurationWarning($"{exception.GetType().Name} 0x{exception.HResult:X8}");
            return null;
        }
    }

    private static void ConfigurationWarning(string reason)
    {
        try { Trace.TraceWarning("Library memory diagnostics were not enabled: {0}.", reason); }
        catch (Exception) { /* Diagnostic listeners must not affect application initialization. */ }
    }

    private static void WriteFailureMarker(string root, string reason, Exception exception)
    {
        try
        {
            var path = Path.Combine(root, "library-memory-diagnostics-errors.log");
            if (File.Exists(path) && new FileInfo(path).Length >= 64 * 1024) return;
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} stop={reason} type={exception.GetType().Name} hresult=0x{exception.HResult:X8}\n");
        }
        catch (Exception) { /* A missing or unwritable diagnostic root must not affect the app. */ }
    }

    private sealed class Session(string root, DispatcherQueue dispatcher)
    {
        private const int MaximumSamples = 240;
        private const long MaximumLogBytes = 4 * 1024 * 1024;
        private const int StopRecordReserveBytes = 16 * 1024;
        private const int DimensionBucketCapacity = 16;
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(20);
        private readonly object _gate = new();
        private readonly WeakRing<LumenArtwork> _artworks = new(2048);
        private readonly WeakRing<BitmapImage> _bitmaps = new(2048);
        private readonly WeakRing<ImageCache> _caches = new(8);
        private readonly DimensionBucket[] _dimensions = new DimensionBucket[DimensionBucketCapacity];
        private readonly MemoryStream _buffer = new(16 * 1024);
        private readonly long _started = Stopwatch.GetTimestamp();
        private FileStream? _output;
        private DispatcherQueueTimer? _timer;
        private Action<NativePosterDecodeObservation>? _decodeObserver;
        private bool _running;
        private int _sampleCount;
        private long _logBytes;
        private long _sampleAllocatedBytes;
        private long _sourceBoundTotal;
        private long _sourceClearAttempts;
        private long _sourceClearedTotal;
        private long _decodeStarted;
        private long _decodeSourceSet;
        private long _decodeCompleted;
        private long _decodeCanceled;
        private long _decodeFailed;
        private long _decodeActive;
        private long _decodeInputCompressedBytes;
        private long _bitmapCreated;
        private long _dimensionMeasurements;
        private long _unbucketedDimensionMeasurements;
        private int _dimensionCount;
        private long _readErrors;
        private long _detachErrors;
        private string? _lastReadErrorType;
        private int _lastReadErrorHResult;
        private string? _lastDecodeErrorType;
        private int _lastDecodeErrorHResult;

        internal void Start()
        {
            _output = new FileStream(Path.Combine(root, "library-memory-diagnostics.jsonl"),
                FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _running = true;
            WriteStart();
            _decodeObserver = ObserveDecode;
            NativePosterDecoder.DiagnosticObserver = _decodeObserver;
            _timer = dispatcher.CreateTimer();
            _timer.Interval = Interval;
            _timer.Tick += Tick;
            dispatcher.ShutdownStarting += DispatcherShutdownStarting;
            AppDomain.CurrentDomain.ProcessExit += ProcessExit;
            _timer.Start();
        }

        internal bool TrackArtwork(LumenArtwork artwork)
        {
            lock (_gate)
            {
                if (!_running) return false;
                _artworks.Add(artwork);
                if (_sampleCount == 0) Sample();
                return _running;
            }
        }

        internal void TrackCache(ImageCache cache)
        {
            lock (_gate)
            {
                if (_running && !_caches.Contains(cache)) _caches.Add(cache);
            }
        }

        internal void SourceBound()
        {
            lock (_gate) { if (_running) _sourceBoundTotal++; }
        }

        internal void SourceClearing(bool hadSource)
        {
            lock (_gate)
            {
                if (!_running) return;
                _sourceClearAttempts++;
                if (hadSource) _sourceClearedTotal++;
            }
        }

        internal void ReadFailure(Exception exception)
        {
            lock (_gate) RecordReadFailure(exception);
        }

        private void RecordReadFailure(Exception exception)
        {
            if (!_running) return;
            _readErrors++;
            _lastReadErrorType = exception.GetType().Name;
            _lastReadErrorHResult = exception.HResult;
        }

        private void ObserveDecode(NativePosterDecodeObservation observation)
        {
            try
            {
                lock (_gate)
                {
                    if (!_running) return;
                    switch (observation.Stage)
                    {
                        case NativePosterDecodeStage.Started:
                            _decodeStarted++;
                            _decodeActive++;
                            _decodeInputCompressedBytes += observation.InputCompressedBytes;
                            break;
                        case NativePosterDecodeStage.BitmapCreated:
                            _bitmapCreated++;
                            if (observation.Bitmap is { } created) _bitmaps.Add(created);
                            MeasureDimensions(observation);
                            break;
                        case NativePosterDecodeStage.SourceSet:
                            _decodeSourceSet++;
                            MeasureDimensions(observation);
                            break;
                        case NativePosterDecodeStage.Completed:
                            _decodeCompleted++;
                            _decodeActive--;
                            break;
                        case NativePosterDecodeStage.Canceled:
                            _decodeCanceled++;
                            _decodeActive--;
                            break;
                        case NativePosterDecodeStage.Failed:
                            _decodeFailed++;
                            _decodeActive--;
                            _lastDecodeErrorType = observation.Error?.GetType().Name;
                            _lastDecodeErrorHResult = observation.Error?.HResult ?? 0;
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                ReadFailure(exception);
            }
        }

        private void MeasureDimensions(NativePosterDecodeObservation observation)
        {
            if (observation.Bitmap is not { } bitmap) return;
            try
            {
                var width = bitmap.PixelWidth;
                var height = bitmap.PixelHeight;
                _dimensionMeasurements++;
                for (var index = 0; index < _dimensionCount; index++)
                {
                    ref var bucket = ref _dimensions[index];
                    if (bucket.Stage != observation.Stage || bucket.RequestedWidth != observation.RequestedWidth
                        || bucket.RequestedHeight != observation.RequestedHeight || bucket.PixelWidth != width
                        || bucket.PixelHeight != height) continue;
                    bucket.Count++;
                    return;
                }
                if (_dimensionCount == _dimensions.Length) { _unbucketedDimensionMeasurements++; return; }
                _dimensions[_dimensionCount++] = new DimensionBucket
                {
                    Stage = observation.Stage, RequestedWidth = observation.RequestedWidth,
                    RequestedHeight = observation.RequestedHeight, PixelWidth = width, PixelHeight = height, Count = 1
                };
            }
            catch (Exception exception) { RecordReadFailure(exception); }
        }

        private void Tick(DispatcherQueueTimer sender, object args)
        {
            lock (_gate)
            {
                if (!_running) return;
                if (_sampleCount >= MaximumSamples) { Stop("SampleLimit", truncated: true); return; }
                if (Stopwatch.GetElapsedTime(_started) >= MaximumDuration) { Stop("DurationLimit", truncated: true); return; }
                Sample();
            }
        }

        private void Sample()
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                // Observe natural collection activity only. No collection, trim, dump, or UI tree walk is requested.
                var managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                var gc = GC.GetGCMemoryInfo();
                long? privateBytes = null;
                long? workingSetBytes = null;
                try
                {
                    using var process = Process.GetCurrentProcess();
                    process.Refresh();
                    privateBytes = process.PrivateMemorySize64;
                    workingSetBytes = process.WorkingSet64;
                }
                catch (Exception exception) { RecordReadFailure(exception); }
                var owners = ReadOwners();
                var cache = ReadCaches();
                var bitmapAlive = _bitmaps.CountAlive();
                _buffer.SetLength(0);
                using (var writer = new Utf8JsonWriter(_buffer))
                {
                    writer.WriteStartObject();
                    WriteCommon(writer, "sample");
                    writer.WriteNumber("Sample", _sampleCount + 1);
                    writer.WriteNumber("ManagedBytesApproximate", managedBytes);
                    writer.WriteNumber("ManagedAllocatedBytesApproximate", GC.GetTotalAllocatedBytes(precise: false));
                    writer.WriteNumber("Gen0Collections", GC.CollectionCount(0));
                    writer.WriteNumber("Gen1Collections", GC.CollectionCount(1));
                    writer.WriteNumber("Gen2Collections", GC.CollectionCount(2));
                    writer.WriteNumber("LastGcIndex", gc.Index);
                    writer.WriteBoolean("LastGcAvailable", gc.Index != 0);
                    writer.WriteNumber("LastGcHeapSizeBytes", gc.HeapSizeBytes);
                    writer.WriteNumber("LastGcCommittedBytes", gc.TotalCommittedBytes);
                    writer.WriteNumber("LastGcFragmentedBytes", gc.FragmentedBytes);
                    WriteNullableNumber(writer, "ProcessPrivateBytes", privateBytes);
                    WriteNullableNumber(writer, "ProcessWorkingSetBytes", workingSetBytes);
                    writer.WriteNumber("WeakArtworkAliveCount", owners.Alive);
                    writer.WriteNumber("WeakArtworkLoadedCount", owners.Loaded);
                    writer.WriteNumber("WeakArtworkSourceBoundCount", owners.SourceBound);
                    writer.WriteNumber("WeakArtworkRequestActiveCount", owners.RequestActive);
                    writer.WriteNumber("WeakArtworkSubscribedCount", owners.Subscribed);
                    writer.WriteNumber("WeakArtworkSnapshotReadFailures", owners.ReadFailures);
                    writer.WriteNumber("WeakBitmapAliveCount", bitmapAlive);
                    writer.WriteNumber("WeakImageCacheAliveCount", cache.Alive);
                    writer.WriteNumber("TrackedCachesCompressedManagedBytes", cache.Bytes);
                    writer.WriteNumber("TrackedCachesCompressedEntryCount", cache.Entries);
                    writer.WriteNumber("TrackedCachesMaximumCompressedBytes", cache.MaximumBytes);
                    writer.WriteBoolean("DiskCacheBytesMeasured", false);
                    writer.WriteNull("DiskCacheBytes");
                    WriteCounters(writer);
                    writer.WriteNumber("ObserverCompletedSamples", _sampleCount);
                    writer.WriteNumber("ObserverCompletedSampleThreadAllocatedBytes", _sampleAllocatedBytes);
                    writer.WriteBoolean("CoverageTruncated", CoverageTruncated);
                    writer.WriteEndObject();
                }
                if (_logBytes + _buffer.Length + 1 > MaximumLogBytes - StopRecordReserveBytes)
                {
                    Stop("ByteLimit", truncated: true);
                    return;
                }
                CommitRecord();
                _sampleCount++;
            }
            catch (Exception exception)
            {
                WriteFailureMarker(root, "SamplingFailure", exception);
                Stop("SamplingFailure", truncated: true);
            }
            finally { _sampleAllocatedBytes += GC.GetAllocatedBytesForCurrentThread() - allocatedBefore; }
        }

        private OwnerSnapshot ReadOwners()
        {
            var result = new OwnerSnapshot();
            for (var index = 0; index < _artworks.Count; index++)
            {
                if (!_artworks.TryGetTarget(index, out var artwork)) continue;
                result.Alive++;
                try
                {
                    var state = artwork!.ReadMemorySnapshot();
                    if (state.Loaded) result.Loaded++;
                    if (state.SourceBound) result.SourceBound++;
                    if (state.RequestActive) result.RequestActive++;
                    if (state.Subscribed) result.Subscribed++;
                }
                catch (Exception exception) { result.ReadFailures++; RecordReadFailure(exception); }
            }
            return result;
        }

        private CacheSnapshot ReadCaches()
        {
            var result = new CacheSnapshot();
            for (var index = 0; index < _caches.Count; index++)
            {
                if (!_caches.TryGetTarget(index, out var cache)) continue;
                result.Alive++;
                var usage = cache!.Usage;
                result.Bytes += usage.Bytes;
                result.Entries += usage.EntryCount;
                result.MaximumBytes += usage.MaximumBytes;
            }
            return result;
        }

        private bool CoverageTruncated => _artworks.Evictions != 0 || _bitmaps.Evictions != 0 || _caches.Evictions != 0
            || _unbucketedDimensionMeasurements != 0 || _readErrors != 0 || _detachErrors != 0;

        private void WriteStart()
        {
            _buffer.SetLength(0);
            using (var writer = new Utf8JsonWriter(_buffer))
            {
                writer.WriteStartObject();
                WriteCommon(writer, "start");
                writer.WriteNumber("IntervalSeconds", Interval.TotalSeconds);
                writer.WriteNumber("MaximumSamples", MaximumSamples);
                writer.WriteNumber("MaximumDurationSeconds", MaximumDuration.TotalSeconds);
                writer.WriteNumber("MaximumLogBytes", MaximumLogBytes);
                writer.WriteString("CollectionPolicy", "NaturalGcOnly; no forced collection, trim, dump, or cache mutation");
                writer.WriteString("OwnerScope", "Bounded weak LumenArtwork wrappers; LumenAmbientArtwork owners are not tracked");
                writer.WriteString("BitmapScope", "NativePosterDecoder bitmap wrappers created after diagnostic start");
                writer.WriteString("DecodeCompletionScope", "DecodeAndApplyAsync including its apply callback; completion does not mean a source was bound");
                writer.WriteString("SourceCounterScope", "LumenArtwork load and cancel paths; direct external source assignments may bypass cumulative totals");
                writer.WriteString("CacheScope", "Weakly observed ImageCache instances; in-memory compressed response arrays only; disk caches are not measured");
                writer.WriteString("BitmapMeasurementScope", "Reported PixelWidth/PixelHeight at creation and after SetSourceAsync; these are not allocated byte measurements");
                writer.WriteBoolean("BitmapPixelBufferCapacityMeasured", false);
                writer.WriteNull("BitmapPixelBufferMeasuredCapacityBytes");
                writer.WriteString("BitmapPixelBufferCapacityUnavailableReason", "BitmapImage does not expose PixelBuffer");
                writer.WriteString("WeakAliveMeaning", "Managed wrapper reachable at sample time; not proof of native or GPU ownership");
                writer.WriteString("LastGcMeaning", "Most recent natural GC; values may precede this sample");
                writer.WriteString("ObserverAllocationScope", "Completed sampler-thread calls only; excludes registration and decode notification overhead");
                writer.WriteBoolean("NativeAllocatedBytesMeasured", false);
                writer.WriteBoolean("GpuAllocatedBytesMeasured", false);
                writer.WriteBoolean("PrivateMinusManagedIsNative", false);
                writer.WriteEndObject();
            }
            CommitRecord();
        }

        private void WriteCounters(Utf8JsonWriter writer)
        {
            writer.WriteNumber("WeakArtworkCapacity", _artworks.Capacity);
            writer.WriteNumber("WeakArtworkTrackedCount", _artworks.Count);
            writer.WriteNumber("WeakArtworkObservedTotal", _artworks.Observed);
            writer.WriteNumber("WeakArtworkEvictions", _artworks.Evictions);
            writer.WriteNumber("WeakBitmapCapacity", _bitmaps.Capacity);
            writer.WriteNumber("WeakBitmapTrackedCount", _bitmaps.Count);
            writer.WriteNumber("WeakBitmapEvictions", _bitmaps.Evictions);
            writer.WriteNumber("WeakImageCacheCapacity", _caches.Capacity);
            writer.WriteNumber("WeakImageCacheTrackedCount", _caches.Count);
            writer.WriteNumber("WeakImageCacheEvictions", _caches.Evictions);
            writer.WriteNumber("SourceBoundAssignmentsTotal", _sourceBoundTotal);
            writer.WriteNumber("SourceClearAttemptsTotal", _sourceClearAttempts);
            writer.WriteNumber("SourceClearedAssignmentsTotal", _sourceClearedTotal);
            writer.WriteNumber("DecodeStartedTotal", _decodeStarted);
            writer.WriteNumber("DecodeSourceSetTotal", _decodeSourceSet);
            writer.WriteNumber("DecodeCompletedTotal", _decodeCompleted);
            writer.WriteNumber("DecodeCanceledTotal", _decodeCanceled);
            writer.WriteNumber("DecodeFailedTotal", _decodeFailed);
            writer.WriteNumber("DecodeActive", _decodeActive);
            writer.WriteNumber("DecodeInputCompressedBytesTotal", _decodeInputCompressedBytes);
            writer.WriteNumber("BitmapCreatedTotal", _bitmapCreated);
            writer.WriteNumber("BitmapDimensionMeasurementsTotal", _dimensionMeasurements);
            writer.WriteNumber("BitmapDimensionBucketCapacity", DimensionBucketCapacity);
            writer.WriteNumber("UnbucketedBitmapDimensionMeasurementsTotal", _unbucketedDimensionMeasurements);
            writer.WriteNumber("DiagnosticReadErrors", _readErrors);
            writer.WriteNumber("DiagnosticDetachErrors", _detachErrors);
            writer.WriteString("LastReadErrorType", _lastReadErrorType);
            writer.WriteNumber("LastReadErrorHResult", _lastReadErrorHResult);
            writer.WriteString("LastDecodeErrorType", _lastDecodeErrorType);
            writer.WriteNumber("LastDecodeErrorHResult", _lastDecodeErrorHResult);
            writer.WriteStartArray("BitmapDimensionBuckets");
            for (var index = 0; index < _dimensionCount; index++)
            {
                ref var bucket = ref _dimensions[index];
                writer.WriteStartObject();
                writer.WriteString("Stage", bucket.Stage == NativePosterDecodeStage.BitmapCreated ? "Created" : "AfterSetSource");
                writer.WriteNumber("RequestedDecodeWidth", bucket.RequestedWidth);
                writer.WriteNumber("RequestedDecodeHeight", bucket.RequestedHeight);
                writer.WriteNumber("ReportedPixelWidth", bucket.PixelWidth);
                writer.WriteNumber("ReportedPixelHeight", bucket.PixelHeight);
                writer.WriteNumber("Count", bucket.Count);
                writer.WriteBoolean("PixelBufferCapacityMeasured", false);
                writer.WriteNull("PixelBufferMeasuredCapacityBytes");
                writer.WriteString("PixelBufferCapacityUnavailableReason", "BitmapImageDoesNotExposePixelBuffer");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private void WriteCommon(Utf8JsonWriter writer, string eventName)
        {
            writer.WriteNumber("SchemaVersion", 1);
            writer.WriteString("Event", eventName);
            writer.WriteString("TimestampUtc", DateTimeOffset.UtcNow);
            writer.WriteNumber("ElapsedMilliseconds", Stopwatch.GetElapsedTime(_started).TotalMilliseconds);
            writer.WriteNumber("ProcessId", Environment.ProcessId);
        }

        private static void WriteNullableNumber(Utf8JsonWriter writer, string name, long? value)
        {
            if (value.HasValue) writer.WriteNumber(name, value.Value);
            else writer.WriteNull(name);
        }

        private void CommitRecord()
        {
            if (_output is null) throw new InvalidOperationException("DiagnosticOutputUnavailable");
            _output.Write(_buffer.GetBuffer().AsSpan(0, checked((int)_buffer.Length)));
            _output.WriteByte((byte)'\n');
            _output.Flush();
            _logBytes += _buffer.Length + 1;
        }

        private void DispatcherShutdownStarting(DispatcherQueue sender, DispatcherQueueShutdownStartingEventArgs args)
            => Stop("DispatcherShutdownStarting", truncated: false);

        private void ProcessExit(object? sender, EventArgs args) => Stop("ProcessExit", truncated: false);

        internal void Stop(string reason, bool truncated)
        {
            lock (_gate)
            {
                if (!_running) return;
                _running = false;
                if (ReferenceEquals(NativePosterDecoder.DiagnosticObserver, _decodeObserver))
                    NativePosterDecoder.DiagnosticObserver = null;
                if (_timer is { } timer)
                {
                    try { timer.Stop(); }
                    catch (Exception) { _detachErrors++; }
                    try { timer.Tick -= Tick; }
                    catch (Exception) { _detachErrors++; }
                }
                _timer = null;
                try { dispatcher.ShutdownStarting -= DispatcherShutdownStarting; }
                catch (Exception) { _detachErrors++; }
                try { AppDomain.CurrentDomain.ProcessExit -= ProcessExit; }
                catch (Exception) { _detachErrors++; }
                try
                {
                    _buffer.SetLength(0);
                    using (var writer = new Utf8JsonWriter(_buffer))
                    {
                        writer.WriteStartObject();
                        WriteCommon(writer, "stop");
                        writer.WriteString("StopReason", reason);
                        writer.WriteNumber("CompletedSamples", _sampleCount);
                        writer.WriteBoolean("IncludesFreshMemoryOrOwnerSample", false);
                        writer.WriteBoolean("CoverageTruncated", truncated || CoverageTruncated);
                        WriteCounters(writer);
                        writer.WriteEndObject();
                    }
                    if (_logBytes + _buffer.Length + 1 <= MaximumLogBytes) CommitRecord();
                    else WriteFailureMarker(root, "StopRecordByteLimit", new InvalidOperationException("DiagnosticByteLimit"));
                }
                catch (Exception exception) { WriteFailureMarker(root, "StopRecordFailure", exception); }
                finally
                {
                    try { _output?.Dispose(); }
                    catch (Exception exception) { WriteFailureMarker(root, "OutputDisposeFailure", exception); }
                    _output = null;
                    _buffer.Dispose();
                }
            }
        }

        private struct OwnerSnapshot
        {
            internal int Alive, Loaded, SourceBound, RequestActive, Subscribed, ReadFailures;
        }

        private struct CacheSnapshot
        {
            internal int Alive, Entries;
            internal long Bytes, MaximumBytes;
        }

        private struct DimensionBucket
        {
            internal NativePosterDecodeStage Stage;
            internal int RequestedWidth, RequestedHeight, PixelWidth, PixelHeight;
            internal long Count;
        }
    }

    private sealed class WeakRing<T>(int capacity) where T : class
    {
        private readonly WeakReference<T>?[] _entries = new WeakReference<T>?[capacity];
        private int _next;
        internal int Capacity => _entries.Length;
        internal int Count { get; private set; }
        internal long Observed { get; private set; }
        internal long Evictions { get; private set; }

        internal void Add(T value)
        {
            Observed++;
            if (Count == _entries.Length) Evictions++;
            else Count++;
            _entries[_next] = new WeakReference<T>(value);
            _next = (_next + 1) % _entries.Length;
        }

        internal bool Contains(T value)
        {
            for (var index = 0; index < Count; index++)
                if (TryGetTarget(index, out var target) && ReferenceEquals(target, value)) return true;
            return false;
        }

        internal bool TryGetTarget(int index, out T? value)
        {
            value = null;
            return _entries[index]?.TryGetTarget(out value) == true;
        }

        internal int CountAlive()
        {
            var alive = 0;
            for (var index = 0; index < Count; index++)
                if (TryGetTarget(index, out _)) alive++;
            return alive;
        }
    }
}

internal readonly record struct ArtworkMemorySnapshot(bool Loaded, bool SourceBound, bool RequestActive, bool Subscribed);
