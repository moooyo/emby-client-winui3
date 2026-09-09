using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class ComplexSubtitleManifest
{
    public int FormatVersion { get; set; }
    public bool Synthetic { get; set; }
    public string? ServerId { get; set; }
    public string? ServerVersion { get; set; }
    public ComplexSubtitleCase[] Cases { get; set; } = [];
}

internal sealed class ComplexSubtitleCase
{
    public string? CaseId { get; set; }
    public string? Kind { get; set; }
    public string? ItemId { get; set; }
    public string? MediaSourceId { get; set; }
    public int SubtitleStreamIndex { get; set; } = -1;
    public string? ExpectedSubtitleCodec { get; set; }
    public string? ExpectedItemName { get; set; }
    public long RunTimeTicks { get; set; }
    public string? MediaSha256 { get; set; }
    public long PauseTargetTicks { get; set; }
    public long CueStartTicks { get; set; }
    public long CueEndTicks { get; set; }
    public string? ExpectedCue { get; set; }
    public string[] ExpectedVisualFeatureIds { get; set; } = [];
    public string? VisualReferenceFileName { get; set; }
    public string? VisualReferenceSha256 { get; set; }
    public string? EmbeddedFontFamily { get; set; }
    public string? EmbeddedFontSha256 { get; set; }
}

internal sealed class ComplexSubtitleReport
{
    public string ExecutionMode { get; init; } = "DefaultSubtitleProfile";
    public bool ProfileControl { get; init; }
    public string Scope { get; init; } = "One manifest-bound complex subtitle on the owned official Emby server, using the unchanged default server-encoding profile, actual NativePlaybackEngine and PlaybackCoordinator. Separate enabled and disabled screenshot confirmations are required. No external WebVTT capability, custom profile, engine injection, OCR, or resource-loop acceptance is used.";
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Status { get; set; } = "Preparing";
    public string? ErrorCode { get; set; }
    public string? FailureStage { get; set; }
    public string? ErrorType { get; set; }
    public int? ErrorHResult { get; set; }
    public string? OutputFailureOperation { get; set; }
    public string? ServerIdHash { get; set; }
    public string? DeviceIdHash { get; set; }
    public string? FixtureManifestSha256 { get; set; }
    public ComplexSubtitleCase? Fixture { get; set; }
    public string FixtureHashMeaning { get; init; } = "Media and embedded-font hashes are generator-manifest evidence. This probe verifies actual API item/source/track bindings and the local visual-reference hash; it does not download and rehash the server media or extract its font attachment.";
    public bool ActualApiBindingVerified { get; set; }
    public bool DefaultEncodingProfileUsed { get; init; } = true;
    public bool ForceTranscodingRequested { get; init; } = false;
    public bool ExternalWebVttEnabled { get; init; } = false;
    public bool NativeOwnerCleared { get; set; }
    public bool StopCompleted { get; set; }
    public bool CoordinatorDisposed { get; set; }
    public bool NativeDisposed { get; set; }
    public bool LogoutCompleted { get; set; }
    public bool ApiSessionOrderAndCleanupVerified { get; set; }
    public bool SubtitleDisableReplacedPlayback { get; set; }
    public bool SubtitleDisablePreservedPauseAndPosition { get; set; }
    public bool TailUsedNewEncodedSession { get; set; }
    public bool TailNativeEndedObserved { get; set; }
    public bool TailCoordinatorEndedObserved { get; set; }
    public bool TailDrainCompleted { get; set; }
    public ComplexSubtitleNativeObservation? TailInitialNative { get; set; }
    public long? TailEndedNativePositionTicks { get; set; }
    public long? TailEndedSourcePositionTicks { get; set; }
    public List<ComplexSubtitlePhase> Phases { get; init; } = [];
    public HlsApiEvent[] ApiEvents { get; set; } = [];
    public ComplexSubtitlePlaybackEvent[] PlaybackEvents { get; set; } = [];
    public string? FirstPlaybackFailureCode { get; set; }
    public int DroppedPlaybackEvents { get; set; }
    public string[] Diagnostics { get; set; } = [];
    public List<string> CleanupErrors { get; init; } = [];
    public string VisualEvidenceMeaning { get; init; } = "API Encode selection and native state are prerequisites, not pixel proof. RootAgentScreenshotInspection compares the enabled screenshot against the reference for the listed style/font or bitmap features, then confirms their absence in the disabled screenshot. PGS text is not verified by OCR.";
}

internal sealed class ComplexSubtitlePhase
{
    public string Phase { get; init; } = "";
    public string PlaybackIdHash { get; init; } = "";
    public string SessionHash { get; init; } = "";
    public string DeliveryMethod { get; init; } = "";
    public ComplexSubtitleNativeObservation? Native { get; set; }
    public ComplexSubtitleNativeObservation? NativeBeforePauseStability { get; set; }
    public ComplexSubtitleCoordinatorObservation? CoordinatorBeforePauseStability { get; set; }
    public ComplexSubtitleCoordinatorObservation? CoordinatorAfterPauseStability { get; set; }
    public long? PauseDriftTicks { get; set; }
    public bool AutomatedChecksPassed { get; set; }
    public bool VisualConfirmed { get; set; }
    public string? ConfirmedBy { get; set; }
    public string[] ConfirmedFeatureIds { get; set; } = [];
    public string? ScreenshotFileName { get; set; }
    public string? ScreenshotSha256 { get; set; }
}

internal sealed class ComplexSubtitleNativeObservation
{
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool ActivePlaybackMatches { get; set; }
    public bool DefaultPlayerOwnerPresent { get; set; }
    public string DeliveryMethod { get; set; } = "Missing";
    public int? SelectedSubtitleStreamIndex { get; set; }
    public string SelectedSubtitleDeliveryMethod { get; set; } = "Missing";
    public string MediaRouteKind { get; set; } = "Missing";
    public bool MediaOriginIsOwnedLoopback { get; set; }
    public string RequestSubtitleMethod { get; set; } = "Missing";
    public int? RequestSubtitleStreamIndex { get; set; }
    public bool HasExternalSubtitleUri { get; set; }
    public bool HasTimedTextSource { get; set; }
    public bool HasProgressiveRelay { get; set; }
    public bool HasFullSourceRangeRelay { get; set; }
    public string TranscodingContainer { get; set; } = "Missing";
    public string TimelineKind { get; set; } = "Missing";
    public long TimelineOffsetTicks { get; set; }
    public long InitialPositionTicks { get; set; }
    public long? SourcePositionTicks { get; set; }
    public string State { get; set; } = "Missing";
    public long PositionTicks { get; set; } = -1;
    public long? NaturalDurationTicks { get; set; }
    public uint VideoWidth { get; set; }
    public uint VideoHeight { get; set; }
}

internal sealed class ComplexSubtitleCoordinatorObservation
{
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Status { get; init; } = "Missing";
    public bool ActivePlaybackMatches { get; init; }
    public long? PositionTicks { get; init; }
    public string? TimelineKind { get; init; }
    public string? RecoveryErrorCode { get; init; }
    public bool? RecoveryIsPaused { get; init; }
    public long? RecoveryPositionTicks { get; init; }
}

internal sealed class ComplexSubtitlePlaybackEvent
{
    public int Ordinal { get; set; }
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
    public string Observer { get; init; } = "";
    public string Kind { get; init; } = "";
    public string State { get; init; } = "";
    public string? ErrorCode { get; init; }
    public string? PlaybackIdHash { get; init; }
    public long? PositionTicks { get; init; }
    public long? DurationTicks { get; init; }
    public string? RecoveryErrorCode { get; init; }
}

internal sealed class ComplexSubtitleReady
{
    public required string RunId { get; init; }
    public required string CaseId { get; init; }
    public required string Phase { get; init; }
    public required string EvidenceKind { get; init; }
    public int ProcessId { get; init; }
    public bool ProfileControl { get; init; }
    public long PausedPositionTicks { get; init; }
    public long NativePositionTicks { get; init; }
    public long TimelineOffsetTicks { get; init; }
    public string TimelineKind { get; init; } = "Missing";
    public string? ExpectedCue { get; init; }
    public required string[] ExpectedFeatureIds { get; init; }
    public required string ReferenceFileName { get; init; }
    public required string ReferenceSha256 { get; init; }
    public string? EnabledScreenshotFileName { get; init; }
    public string? EnabledScreenshotSha256 { get; init; }
    public required string FinishFileName { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public string RequiredConfirmedBy { get; init; } = "RootAgentScreenshotInspection";
}

internal sealed class ComplexSubtitleFinish
{
    public string? RunId { get; set; }
    public string? CaseId { get; set; }
    public string? Phase { get; set; }
    public string? ConfirmedBy { get; set; }
    public bool VisualConfirmed { get; set; }
    public bool ReferenceCompared { get; set; }
    public string[] ConfirmedFeatureIds { get; set; } = [];
    public string? ScreenshotFileName { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ComplexSubtitleManifest))]
[JsonSerializable(typeof(ComplexSubtitleReport))]
[JsonSerializable(typeof(ComplexSubtitleReady))]
[JsonSerializable(typeof(ComplexSubtitleFinish))]
internal partial class ComplexSubtitleJsonContext : JsonSerializerContext;
