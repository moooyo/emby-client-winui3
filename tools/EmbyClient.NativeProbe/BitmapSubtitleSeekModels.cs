using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class BitmapSubtitleSeekReport
{
    public string Scope { get; init; } = "Actual product coordinator and native engine, hash-bound original synthetic Matroska PGS. A paused seek into a cue that was absent from both image and pending reference tracks before seek must decode and publish that composition immediately. Sparse packet reconstruction and paused product presentation are separate observations. No UI input or test engine is used.";
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Preparing";
    public string CaseName { get; set; } = "original";
    public long SubtitleDelayTicks { get; set; }
    public int ExpectedStepCount { get; set; }
    public bool UnknownCuePreconditionApplicable { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorType { get; set; }
    public int? ErrorHResult { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedUtc { get; set; }
    public string? MediaSha256 { get; set; }
    public string? FfprobeSha256 { get; set; }
    public string? DecoderRuntimeSha256 { get; set; }
    public bool DecoderRuntimeLoadedFromProbeDirectory { get; set; }
    public string? PlaybackIdHash { get; set; }
    public string? PlaySessionIdHash { get; set; }
    public bool TargetCueUnknownBeforeForwardSeek { get; set; }
    public int ImageEvents { get; set; }
    public int ClearEvents { get; set; }
    public int TrackSnapshotRetries { get; set; }
    public BitmapSubtitleNativeSample? FirstDecodedCue { get; set; }
    public BitmapSubtitleNativeSample? InitialNative { get; set; }
    public BitmapSubtitleSeekTrackObservation? BeforeUnknownForwardSeek { get; set; }
    public List<BitmapSubtitleSeekStep> Steps { get; init; } = [];
    public bool DiagnosticResumeAttempted { get; set; }
    public bool DiagnosticResumeRevealedExpectedCue { get; set; }
    public BitmapSubtitleNativeSample? DiagnosticResumedSample { get; set; }
    public BitmapSubtitleSeekTrackObservation? DiagnosticResumedTracks { get; set; }
    public List<BitmapFixtureObservation> FixtureObservations { get; set; } = [];
    public HlsApiEvent[] ApiAttempts { get; set; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public bool NativeIdentitiesPreserved { get; set; }
    public bool LogicalAndServerSessionsPreserved { get; set; }
    public bool NoNegotiationStartOrStopDuringSeeks { get; set; }
    public int ObserverNativeReferencesAtStop { get; set; } = -1;
    public int ObserverProjectionReferencesAtStop { get; set; } = -1;
    public int ObserverBitmapLeasesAtStop { get; set; } = -1;
    public bool ObserversReleasedBeforeStop { get; set; }
    public bool StopCompleted { get; set; }
    public bool NativeSessionDrained { get; set; }
    public bool CoordinatorDisposed { get; set; }
    public bool NativeDisposed { get; set; }
    public bool NativeOwnerCleared { get; set; }
    public bool LogoutCompleted { get; set; }
    public bool ExactFinalApiCleanup { get; set; }
    public bool NoMediaOrProgressAfterStop { get; set; }
    public List<string> CleanupErrors { get; init; } = [];
}

internal sealed class BitmapSubtitleSeekStep
{
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Name { get; init; }
    public long TargetTicks { get; init; }
    public long? ExpectedCueSourceTimestampTicks { get; init; }
    public bool ExpectedImage { get; init; }
    public string Status { get; set; } = "Running";
    public BitmapSubtitleNativeSample? Native { get; set; }
    public BitmapSubtitleSeekTrackObservation? Tracks { get; set; }
    public int ImageEventsBefore { get; set; }
    public int ImageEventsAfter { get; set; }
    public int ClearEventsBefore { get; set; }
    public int ClearEventsAfter { get; set; }
    public bool CorrectPausedClock { get; set; }
    public bool NativeSourceAndSelectionPreserved { get; set; }
    public bool ExpectedCompositionObserved { get; set; }
    public bool ProductFrameEventObserved { get; set; }
    public double SeekMilliseconds { get; set; }
    public double ObservationMilliseconds { get; set; }
}

internal sealed class BitmapSubtitleSeekTrackObservation
{
    public bool ActivePlaybackMatches { get; set; }
    public long NativePositionTicks { get; set; } = -1;
    public string NativeState { get; set; } = "Missing";
    public BitmapSubtitleSeekTrack[] Tracks { get; set; } = [];
}

internal sealed class BitmapSubtitleSeekTrack
{
    public required string Origin { get; init; }
    public required string Kind { get; init; }
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int ActiveCueCount { get; init; }
    public BitmapSubtitleSeekCue[] Cues { get; init; } = [];
}

internal sealed class BitmapSubtitleSeekCue
{
    public long StartTicks { get; init; }
    public long DurationTicks { get; init; }
    public bool IsImageCue { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BitmapSubtitleSeekReport))]
internal partial class BitmapSubtitleSeekJsonContext : JsonSerializerContext;
