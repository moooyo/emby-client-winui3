using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class ExternalSubtitleCredentials
{
    public string? ServerUrl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public override string ToString() => nameof(ExternalSubtitleCredentials);
}

internal sealed class ExternalSubtitleReport
{
    public string Scope { get; init; } = "One native external WebVTT cue on the owned official Emby 4.9.5.0 server, default NativePlaybackEngine, real PlaybackCoordinator, and Fixture item 5. No rendering controls or synthetic engine events are injected.";
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Status { get; set; } = "Preparing";
    public string? ErrorCode { get; set; }
    public string ServerVersion { get; set; } = "";
    public string? ServerIdHash { get; set; }
    public string? DeviceIdHash { get; set; }
    public string ItemId { get; init; } = "5";
    public int RequestedSubtitleStreamIndex { get; init; } = 2;
    public long PauseTargetTicks { get; init; } = TimeSpan.FromSeconds(41).Ticks;
    public string ExpectedCue { get; init; } = "Seek and subtitle delivery check.";
    public string DeliveryMethod { get; set; } = "NotObserved";
    public string? SubtitleDeliveryMethod { get; set; }
    public long? NativePausedPositionTicks { get; set; }
    public bool AutomatedChecksPassed { get; set; }
    public ExternalSubtitleNativeObservation? NativeObservation { get; set; }
    public bool PresentedCueConfirmed { get; set; }
    public string? ConfirmedBy { get; set; }
    public string? ScreenshotFileName { get; set; }
    public string? ScreenshotSha256 { get; set; }
    public string VisualEvidenceMeaning { get; init; } = "Only a matching RootAgentScreenshotInspection finish file can confirm pixels of this one expected cue. Downloaded WebVTT, resolved tracks, presentation modes, native dimensions, and media-clock properties are not pixel evidence.";
    public string[] Diagnostics { get; set; } = [];
    public ExternalSubtitleApiEvent[] ApiEvents { get; set; } = [];
    public bool StopCompleted { get; set; }
    public bool CoordinatorDisposed { get; set; }
    public bool NativeDisposed { get; set; }
    public bool NativeOwnerCleared { get; set; }
    public bool LogoutCompleted { get; set; }
    public string[] CleanupErrors { get; set; } = [];
}

internal sealed class ExternalSubtitleNativeObservation
{
    public bool ActivePlaybackMatches { get; set; }
    public string DeliveryMethod { get; set; } = "NotObserved";
    public bool HasDefaultProductPlayerOwner { get; set; }
    public bool HasIndependentSubtitleTransport { get; set; }
    public bool HasSeparateSubtitleUri { get; set; }
    public bool SubtitleOriginIsOwnedLoopback { get; set; }
    public bool HasScopedSubtitleAuthentication { get; set; }
    public ulong DownloadedSubtitleBytes { get; set; }
    public bool WebVttHeaderObserved { get; set; }
    public string? DownloadedSubtitleSha256 { get; set; }
    public string DownloadEvidence { get; init; } = "Read-only inspection of the product-owned subtitle transport and its independently filled memory stream after resolution. The inspected bytes came from NativePlaybackEngine.LoadAsync; this probe does not perform a second subtitle download. HTTP status/content-type are not captured by this observation.";
    public bool TimedTextSourceCreated { get; set; }
    public int AttachedTimedTextSourceCount { get; set; }
    public bool TimedTextResolvedSuccessfully { get; set; }
    public int ResolvedExternalTrackCount { get; set; }
    public int MatchingMetadataTrackCount { get; set; }
    public int PlatformPresentedExternalTrackCount { get; set; }
    public bool ExpectedCueFoundInDownloadedText { get; set; }
    public long? ExpectedCueStartTicks { get; set; }
    public long? ExpectedCueEndTicks { get; set; }
    public long NativePositionTicks { get; set; }
    public string NativeState { get; set; } = "NotObserved";
    public uint NativeVideoWidth { get; set; }
    public uint NativeVideoHeight { get; set; }
}

internal sealed class ExternalSubtitleReady
{
    public required string RunId { get; init; }
    public int ProcessId { get; init; }
    public string Status { get; init; } = "AwaitingVisualInspection";
    public required string ExpectedCue { get; init; }
    public long PausedPositionTicks { get; init; }
    public DateTimeOffset DeadlineUtc { get; init; }
    public string FinishFileName { get; init; } = "external-subtitle-finish.json";
    public string RequiredConfirmedBy { get; init; } = "RootAgentScreenshotInspection";
}

internal sealed class ExternalSubtitleFinish
{
    public string? RunId { get; set; }
    public bool PresentedCueConfirmed { get; set; }
    public string? ConfirmedBy { get; set; }
    public string? ObservedCue { get; set; }
    public string? ScreenshotFileName { get; set; }
}

internal sealed class ExternalSubtitleApiEvent
{
    public string Operation { get; init; } = "";
    public int StatusCode { get; init; }
    public long? PositionTicks { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ExternalSubtitleCredentials))]
[JsonSerializable(typeof(ExternalSubtitleReport))]
[JsonSerializable(typeof(ExternalSubtitleReady))]
[JsonSerializable(typeof(ExternalSubtitleFinish))]
internal partial class ExternalSubtitleJsonContext : JsonSerializerContext;
