using EmbyClient.Api;

namespace EmbyClient.Playback;

public enum PlaybackDeliveryMethod { DirectStream, Transcode }
public enum PlaybackTimelineKind { FullSource, ProgressiveSegment }
public enum AudioPassthroughMode { Off = 0, DolbyDts = 1, All = 2 }
public enum PlaybackEngineState { Idle, Opening, Playing, Paused, Buffering, Seeking, Stopped, Ended, Failed }
public enum PlaybackEngineEventKind { StateChanged, PositionChanged, Ended, Failed }
public enum PlaybackStatus { Idle, Negotiating, Opening, Playing, Paused, Buffering, Seeking, Stopping, Ended, Failed }

/// <summary>
/// FullSource timelines require an actual initial seek to InitialPositionTicks and have no reporting offset.
/// ProgressiveSegment timelines start at engine zero and add the confirmed server-side trim offset when reporting.
/// Absolute item time is always TimelineOffsetTicks plus engine time.
/// </summary>
public sealed record PlaybackEngineRequest
{
    public required Guid PlaybackId { get; init; }
    public required Uri MediaUri { get; init; }
    public required IReadOnlyDictionary<string, string> Headers { get; init; }
    public required PlaybackDeliveryMethod DeliveryMethod { get; init; }
    public PlaybackTimelineKind TimelineKind { get; init; } = PlaybackTimelineKind.FullSource;
    public required MediaSourceInfo Source { get; init; }
    public long InitialPositionTicks { get; init; }
    public long TimelineOffsetTicks { get; init; }
    public long? ItemRunTimeTicks { get; init; }
    /// <summary>Requires the direct HDR10 source to retain HDR metadata on an eligible display.</summary>
    public bool RequiresHdr10Output { get; init; }
    /// <summary>Permits delivered HDR10 only for an eligible, non-forced opening with explicitly identified input.</summary>
    public bool Hdr10OutputAllowed { get; init; }
    /// <summary>Permits a validated embedded bitmap track in an unchanged original container.</summary>
    public bool NativeEmbeddedSubtitlesEligible { get; init; }
    /// <summary>Permits original ASS/SSA delivery only when the full native renderer is available.</summary>
    public bool NativeAssSubtitlesEligible { get; init; }
    /// <summary>Identifies an available native output API; endpoint format support is checked when opening.</summary>
    public bool NativeAudioPassthroughEligible { get; init; }
    public AudioPassthroughMode AudioPassthroughMode { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public Uri? ExternalSubtitleUri { get; init; }
    /// <summary>The authenticated external subtitle's negotiated format, rather than a guessed conversion.</summary>
    public string? ExternalSubtitleFormat { get; init; }
    public IReadOnlyDictionary<string, string> ExternalSubtitleHeaders { get; init; } = new Dictionary<string, string>();
    public override string ToString() => $"{nameof(PlaybackEngineRequest)} {{ PlaybackId = {PlaybackId}, DeliveryMethod = {DeliveryMethod} }}";
}

/// <summary>An immutable, thread-safe snapshot. PositionTicks and DurationTicks use the engine URL's timeline.</summary>
public sealed record PlaybackEngineSnapshot
{
    public required Guid PlaybackId { get; init; }
    public PlaybackEngineState State { get; init; }
    public long PositionTicks { get; init; }
    public long? DurationTicks { get; init; }
    public bool CanSeek { get; init; }
    public bool IsMuted { get; init; }
    public int VolumeLevel { get; init; } = 100;
    public double PlaybackRate { get; init; } = 1;
}

public sealed class PlaybackEngineEventArgs(PlaybackEngineEventKind kind, PlaybackEngineSnapshot snapshot, string? errorCode = null) : EventArgs
{
    public PlaybackEngineEventKind Kind { get; } = kind;
    public PlaybackEngineSnapshot Snapshot { get; } = snapshot;
    public string? ErrorCode { get; } = errorCode;
}

/// <summary>
/// Implementations own dispatcher marshalling and must ignore operations for retired playback IDs.
/// OpenAsync completes only after actual playback starts, including the requested initial seek.
/// Cancellation must interrupt opening. StopAsync must quiesce callbacks and network requests before returning.
/// Emby VOD HLS exposes the full source timeline; apply InitialPositionTicks as a real seek before completing OpenAsync.
/// Only a confirmed ProgressiveSegment uses a segment-relative engine timeline and a nonzero reporting offset.
/// Do not infer the timeline from a Transcode delivery label or from a StartTimeTicks URL parameter alone.
/// Headers are scoped to their original origin and must not leak through cross-origin redirects.
/// </summary>
public interface IPlaybackEngine : IAsyncDisposable
{
    event EventHandler<PlaybackEngineEventArgs>? EventReceived;
    PlaybackEngineSnapshot? Snapshot { get; }
    Task OpenAsync(PlaybackEngineRequest request, CancellationToken cancellationToken = default);
    Task PauseAsync(Guid playbackId, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid playbackId, CancellationToken cancellationToken = default);
    Task SeekAsync(Guid playbackId, long positionTicks, CancellationToken cancellationToken = default);
    Task SetVolumeAsync(Guid playbackId, int volumeLevel, bool isMuted, CancellationToken cancellationToken = default);
    Task StopAsync(Guid playbackId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optionally changes a previously mapped embedded bitmap subtitle without replacing its decoder.
/// Returning false must leave the current selection untouched. Implementations retain the same playback
/// clock and source, ignore retired IDs, and never synthesize an image for an unread subtitle interval.
/// </summary>
public interface IPlaybackSubtitleSelectionEngine
{
    Task<bool> TrySelectEmbeddedBitmapSubtitleAsync(Guid playbackId, int subtitleStreamIndex,
        CancellationToken cancellationToken = default);
}

public sealed record PlaybackSelection
{
    public required string ItemId { get; init; }
    public string? MediaSourceId { get; init; }
    public long StartPositionTicks { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public long MaxStreamingBitrate { get; init; } = 20_000_000;
    public bool ForceTranscoding { get; init; }

    /// <summary>A current, window-specific output capability supplied before negotiation; never a saved preference.</summary>
    public bool Hdr10OutputEligible { get; init; }

    /// <summary>Allows the native subtitle interface; opening must still verify the actual embedded stream mapping.</summary>
    public bool NativeEmbeddedSubtitlesEligible { get; init; }
    public bool NativeAssSubtitlesEligible { get; init; }
    /// <summary>Allows native bitstream attempts; this flag never asserts that an endpoint accepts a codec.</summary>
    public bool NativeAudioPassthroughEligible { get; init; }
    public AudioPassthroughMode AudioPassthroughMode { get; init; }

    /// <summary>
    /// Requests server-rendered subtitles for the current source and track after external delivery fails.
    /// Independent of video conversion; changing the source or subtitle track clears this fallback.
    /// </summary>
    public bool ForceSubtitleBurnIn { get; init; }
}

/// <summary>
/// Changes playback without changing its current position or pause state. Null normally preserves the current selection.
/// When MediaSourceId changes, omitted audio/subtitle indexes instead request the new source's defaults;
/// explicitly supplied indexes must belong to that new source. SubtitleStreamIndex = -1 explicitly disables subtitles.
/// A different source or subtitle track clears its previous subtitle-delivery fallback; other changes preserve it.
/// </summary>
public sealed record PlaybackSelectionChange
{
    public string? MediaSourceId { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public long? MaxStreamingBitrate { get; init; }
    public bool? ForceTranscoding { get; init; }
    public bool? Hdr10OutputEligible { get; init; }
    public bool? NativeEmbeddedSubtitlesEligible { get; init; }
    public bool? NativeAssSubtitlesEligible { get; init; }
    public bool? NativeAudioPassthroughEligible { get; init; }
    public AudioPassthroughMode? AudioPassthroughMode { get; init; }
}

public sealed record PlaybackContext
{
    public required Guid PlaybackId { get; init; }
    public required PlaybackSelection Selection { get; init; }
    public required MediaSourceInfo Source { get; init; }
    public required string PlaySessionId { get; init; }
    public required PlaybackDeliveryMethod DeliveryMethod { get; init; }
    public PlaybackTimelineKind TimelineKind { get; init; } = PlaybackTimelineKind.FullSource;
    public long TimelineOffsetTicks { get; init; }
    public long PositionTicks { get; init; }
    public bool CanSeek { get; init; }
    public override string ToString() => $"{nameof(PlaybackContext)} {{ PlaybackId = {PlaybackId}, DeliveryMethod = {DeliveryMethod} }}";
}

/// <summary>A one-use, immutable target for an explicit retry after a recoverable playback failure.</summary>
public sealed record PlaybackRecovery
{
    public required Guid RecoveryId { get; init; }
    public required Guid FailedPlaybackId { get; init; }
    public required PlaybackSelection Selection { get; init; }
    public required bool IsPaused { get; init; }
    public required string ErrorCode { get; init; }
}

public sealed class PlaybackStatusChangedEventArgs(PlaybackStatus status, PlaybackContext? context, string? errorCode = null,
    PlaybackRecovery? recovery = null) : EventArgs
{
    public PlaybackStatus Status { get; } = status;
    public PlaybackContext? Context { get; } = context;
    public string? ErrorCode { get; } = errorCode;
    public PlaybackRecovery? Recovery { get; } = recovery;
}

/// <summary>Codes contain no server URLs, credentials, or raw exception messages.</summary>
public sealed class PlaybackDiagnosticEventArgs(Guid playbackId, string operation, string errorCode) : EventArgs
{
    public Guid PlaybackId { get; } = playbackId;
    public string Operation { get; } = operation;
    public string ErrorCode { get; } = errorCode;
}

public sealed class PlaybackException(string errorCode) : Exception($"Playback operation failed ({errorCode}).")
{
    public string ErrorCode { get; } = errorCode;
}

public sealed record PlaybackCoordinatorOptions
{
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CleanupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan ReportTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan StateChangeTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public bool EnableExternalWebVtt { get; init; }
}
