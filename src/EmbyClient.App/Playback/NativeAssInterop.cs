using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpegInteropX;
using Windows.Media.Core;
using WinRT;

namespace EmbyClient.App.Playback;

/// <summary>Versioned access to the native libass renderer without changing the WinRT projection.</summary>
internal static unsafe partial class NativeAssInterop
{
    private const uint ExpectedLibassVersion = 0x01705000;
    private const uint ObservationVersion = 1;

    internal static bool IsAvailable
    {
        get
        {
            try { return IsFrameBridgeAvailable && LibassVersion() == ExpectedLibassVersion; }
            catch (Exception exception) when (IsUnavailable(exception)) { return false; }
        }
    }

    internal static bool IsFrameBridgeAvailable
    {
        get
        {
            try { return ApiVersion() == 1 && FrameApiVersion() == 1; }
            catch (Exception exception) when (IsUnavailable(exception)) { return false; }
        }
    }

    internal static bool TryValidateDocument(ReadOnlySpan<byte> utf8, out int eventCount)
    {
        eventCount = 0;
        if (utf8.IsEmpty || !IsAvailable) return false;
        try
        {
            fixed (byte* bytes = utf8)
            {
                uint count = 0;
                if (ValidateDocument(bytes, checked((uint)utf8.Length), &count) < 0 || count > int.MaxValue)
                    return false;
                eventCount = checked((int)count);
                return true;
            }
        }
        catch (Exception exception) when (IsUnavailable(exception)) { return false; }
    }

    internal static NativeAssTrackOwner CreateLocalTrack(FFmpegMediaSource source, ReadOnlySpan<byte> utf8,
        string name, long timelineOffsetTicks, long delayTicks)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(name);
        if (utf8.IsEmpty) throw new ArgumentOutOfRangeException(nameof(utf8));
        if (timelineOffsetTicks < 0) throw new ArgumentOutOfRangeException(nameof(timelineOffsetTicks));
        if (!IsAvailable) throw new PlatformNotSupportedException("The native libass subtitle API is unavailable.");
        nint sourcePointer = 0;
        nint trackPointer = 0;
        ulong ownerId = 0;
        var retained = false;
        try
        {
            sourcePointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            fixed (byte* bytes = utf8)
            fixed (char* label = name)
            {
                Marshal.ThrowExceptionForHR(CreateLocalSubtitle(sourcePointer, bytes, checked((uint)utf8.Length),
                    label, timelineOffsetTicks, delayTicks, &ownerId, &trackPointer));
            }
            if (ownerId == 0 || trackPointer == 0)
                throw new InvalidOperationException("The native libass renderer returned incomplete track ownership.");
            var track = MarshalInspectable<TimedMetadataTrack>.FromAbi(trackPointer);
            if (track is not { TimedMetadataKind: TimedMetadataKind.ImageSubtitle })
                throw new InvalidOperationException("The native libass renderer did not return an image subtitle track.");
            var owner = new NativeAssTrackOwner(source, ownerId, track);
            retained = true;
            return owner;
        }
        finally
        {
            try
            {
                if (!retained && ownerId != 0 && sourcePointer != 0)
                    _ = RemoveLocalSubtitle(sourcePointer, ownerId);
            }
            finally
            {
                if (trackPointer != 0) MarshalInspectable<TimedMetadataTrack>.DisposeAbi(trackPointer);
                if (sourcePointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(sourcePointer);
            }
        }
    }

    internal static void SetDelay(FFmpegMediaSource source, ulong ownerId, long delayTicks)
    {
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            Marshal.ThrowExceptionForHR(SetLocalSubtitleDelay(pointer, ownerId, delayTicks));
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    internal static void Remove(FFmpegMediaSource source, ulong ownerId)
    {
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            Marshal.ThrowExceptionForHR(RemoveLocalSubtitle(pointer, ownerId));
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    internal static void Refresh(FFmpegMediaSource source, long mediaPositionTicks, long frameDurationTicks)
    {
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            Marshal.ThrowExceptionForHR(RefreshSubtitles(pointer, mediaPositionTicks, frameDurationTicks));
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    internal static bool TryReadFailure(FFmpegMediaSource source, out int failure)
    {
        failure = 0;
        if (!IsAvailable) return false;
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            var observed = 0;
            if (GetSubtitleFailure(pointer, &observed) < 0) return false;
            failure = observed;
            return true;
        }
        catch (Exception exception) when (IsUnavailable(exception) || exception is COMException or ObjectDisposedException)
        {
            // A retired source cannot classify an unrelated or replacement player's error.
            return false;
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    /// <summary>Reads numeric renderer and cue observations without altering playback or subtitle selection.</summary>
    internal static NativeAssObservation? ReadObservation(FFmpegMediaSource source)
    {
        if (!IsAvailable) return null;
        nint pointer = 0;
        try
        {
            pointer = MarshalInspectable<FFmpegMediaSource>.FromManaged(source);
            var value = new Observation { Size = (uint)sizeof(Observation), Version = ObservationVersion };
            if (GetObservation(pointer, &value, (uint)sizeof(Observation)) < 0
                || value.Size != sizeof(Observation) || value.Version != ObservationVersion || value.Reserved != 0
                || value.PlatformPresentedTrackCount > value.OwnedTrackCount
                || value.EnabledTrackCount > value.OwnedTrackCount || value.CurrentImageCount > value.OwnedTrackCount
                || value.PlatformActiveImageCount > value.CurrentImageCount
                || value.HiddenReferenceTrackCount > value.OwnedTrackCount)
                return null;
            return new(value.Failure, value.OwnedTrackCount, value.PlatformPresentedTrackCount,
                value.EnabledTrackCount, value.CurrentImageCount, value.PlatformActiveImageCount,
                value.HiddenReferenceTrackCount, value.VideoWidth, value.VideoHeight, value.VideoSampleCount,
                value.RefreshCount, value.ScheduledFrameCount, value.ReferenceCueEnteredCount, value.RenderCallCount,
                value.ImageFrameCount, value.ImageCueAddedCount, value.ImageCueEnteredCount, value.ImageCueExitedCount,
                value.ClearCount, value.StaleFrameCount, value.PendingScheduledFrameCount,
                value.LastBitmapNonzeroAlphaPixels, value.LastSamplePositionTicks, value.LastRenderPositionTicks,
                value.LastScriptPositionTicks, value.LastImageX, value.LastImageY, value.LastImageWidth, value.LastImageHeight);
        }
        catch (Exception exception) when (IsUnavailable(exception) || exception is COMException or ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (pointer != 0) MarshalInspectable<FFmpegMediaSource>.DisposeAbi(pointer);
        }
    }

    private static bool IsUnavailable(Exception exception) => exception is DllNotFoundException
        or EntryPointNotFoundException or BadImageFormatException;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct Observation
    {
        public uint Size;
        public uint Version;
        public int Failure;
        public uint OwnedTrackCount;
        public uint PlatformPresentedTrackCount;
        public uint EnabledTrackCount;
        public uint CurrentImageCount;
        public uint PlatformActiveImageCount;
        public uint HiddenReferenceTrackCount;
        public uint VideoWidth;
        public uint VideoHeight;
        public uint Reserved;
        public ulong VideoSampleCount;
        public ulong RefreshCount;
        public ulong ScheduledFrameCount;
        public ulong ReferenceCueEnteredCount;
        public ulong RenderCallCount;
        public ulong ImageFrameCount;
        public ulong ImageCueAddedCount;
        public ulong ImageCueEnteredCount;
        public ulong ImageCueExitedCount;
        public ulong ClearCount;
        public ulong StaleFrameCount;
        public ulong PendingScheduledFrameCount;
        public ulong LastBitmapNonzeroAlphaPixels;
        public long LastSamplePositionTicks;
        public long LastRenderPositionTicks;
        public long LastScriptPositionTicks;
        public int LastImageX;
        public int LastImageY;
        public int LastImageWidth;
        public int LastImageHeight;
    }

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbySubtitleApiVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint ApiVersion();

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyLibassVersion")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint LibassVersion();

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyValidateAssSubtitleDocument")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ValidateDocument(byte* utf8, uint bytes, uint* eventCount);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyCreateLocalAssSubtitle")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int CreateLocalSubtitle(nint source, byte* utf8, uint bytes, char* name,
        long timelineOffsetTicks, long delayTicks, ulong* ownerId, nint* track);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbySetLocalAssSubtitleDelay")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SetLocalSubtitleDelay(nint source, ulong ownerId, long delayTicks);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyRemoveLocalAssSubtitle")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RemoveLocalSubtitle(nint source, ulong ownerId);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyRefreshAssSubtitles")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int RefreshSubtitles(nint source, long mediaPositionTicks, long frameDurationTicks);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyGetAssSubtitleFailure")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetSubtitleFailure(nint source, int* failure);

    [LibraryImport("FFmpegInteropX.dll", EntryPoint = "EmbyGetAssSubtitleObservation")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetObservation(nint source, Observation* observation, uint size);
}

/// <summary>Owned renderer work and platform cue state; only captured pixels establish visual acceptance.</summary>
internal sealed record NativeAssObservation(int Failure, uint OwnedTrackCount, uint PlatformPresentedTrackCount,
    uint EnabledTrackCount, uint CurrentImageCount, uint PlatformActiveImageCount, uint HiddenReferenceTrackCount,
    uint VideoWidth, uint VideoHeight, ulong VideoSampleCount, ulong RefreshCount, ulong ScheduledFrameCount,
    ulong ReferenceCueEnteredCount, ulong RenderCallCount, ulong ImageFrameCount, ulong ImageCueAddedCount,
    ulong ImageCueEnteredCount, ulong ImageCueExitedCount, ulong ClearCount, ulong StaleFrameCount,
    ulong PendingScheduledFrameCount, ulong LastBitmapNonzeroAlphaPixels, long LastSamplePositionTicks,
    long LastRenderPositionTicks, long LastScriptPositionTicks, int LastImageX, int LastImageY,
    int LastImageWidth, int LastImageHeight);

/// <summary>The native source owns the renderer; this object releases one source-scoped local import.</summary>
internal sealed partial class NativeAssTrackOwner(FFmpegMediaSource source, ulong ownerId, TimedMetadataTrack track) : IDisposable
{
    private FFmpegMediaSource? _source = source;

    internal TimedMetadataTrack Track { get; } = track;
    internal bool IsAlive => Volatile.Read(ref _source) is not null;

    internal void SetDelay(long delayTicks) => NativeAssInterop.SetDelay(
        Volatile.Read(ref _source) ?? throw new ObjectDisposedException(nameof(NativeAssTrackOwner)), ownerId, delayTicks);

    public void Dispose()
    {
        // Closing the decoder also releases all renderers, including a canceled import still being drained.
        if (Interlocked.Exchange(ref _source, null) is { } owner) NativeAssInterop.Remove(owner, ownerId);
    }
}
