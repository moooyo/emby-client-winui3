using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class ProbeReport
{
    public string Status { get; set; } = "Running";
    public string Scope { get; set; } = "SYNTHETIC loopback H.264/AAC MP4; product NativePlaybackEngine and PlaybackCoordinator; not real Emby compatibility.";
    public string ExecutionMode { get; set; } = "NormalProductLifecycle";
    public long? NoGcRegionBudgetBytes { get; set; }
    public string[] NotVerified { get; init; } = ["Real Emby server compatibility", "Audible audio output or device switching", "HLS/transcoding", "External subtitles", "Pixel capture or first presented frame", "Physical system media keys"];
    public string FrameEvidence { get; init; } = "Normal MediaPlayerElement rendering is retained. A positive native media clock and native video dimensions are recorded; neither is asserted to prove a presented pixel frame.";
    public string ResourceObservation { get; set; } = "Pending";
    public string? ErrorCode { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int RequiredLoops { get; set; } = 20;
    public int CompletedLoops { get; set; }
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public long? FixtureDurationTicks { get; set; }
    public List<LoopResult> Loops { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public long? PostWarmupPrivateBytesGrowth { get; set; }
    public int? PostWarmupHandleGrowth { get; set; }
    public List<ResourceSample> FinalIdleSamples { get; init; } = [];
}

internal sealed class LoopResult
{
    public int Number { get; init; }
    public string Status { get; set; } = "Running";
    public string? Stage { get; set; }
    public string? ErrorCode { get; set; }
    public string? PlaySessionId { get; set; }
    public int? ErrorHResult { get; set; }
    public double OpenMilliseconds { get; set; }
    public long PlayingPositionTicks { get; set; }
    public uint VideoWidth { get; set; }
    public uint VideoHeight { get; set; }
    public long PauseStartTicks { get; set; }
    public long PauseEndTicks { get; set; }
    public long SeekTargetTicks { get; set; }
    public long SeekObservedTicks { get; set; }
    public long ResumePositionTicks { get; set; }
    public bool Muted { get; set; }
    public bool PlayerDetachedAfterStop { get; set; }
    public bool NoNetworkAfterStop { get; set; }
    public bool ValidReportOrder { get; set; }
    public int StartReports { get; set; }
    public int ProgressReports { get; set; }
    public int StopReports { get; set; }
    public long StopReportPositionTicks { get; set; }
    public int MediaRequests { get; set; }
    public int PartialResponses { get; set; }
    public long ClosedReadOperationsDuringStop { get; set; }
    public bool MediaControlsPlaying { get; set; }
    public bool MediaControlsPaused { get; set; }
    public bool MediaControlsRetired { get; set; }
    public bool MediaControlsRetirementObserved { get; set; }
    public int? MediaControlsRetirementHResult { get; set; }
    public string? MediaControlsRetirementGetter { get; set; }
    public bool? MediaControlsEnabledAfterRetire { get; set; }
    public string? MediaControlsStatusAfterRetire { get; set; }
    public string? MediaControlsTypeAfterRetire { get; set; }
    public bool? MediaControlsVideoTitleEmptyAfterRetire { get; set; }
    public bool NoGcRegionStarted { get; set; }
    public bool NoGcRegionEnded { get; set; }
    public ResourceSample? Resources { get; set; }
}

internal sealed class ResourceSample
{
    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;
    public long PrivateBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ManagedBytes { get; init; }
    public int HandleCount { get; init; }
    public int ThreadCount { get; init; }
}

internal sealed class FixtureStats
{
    public bool Synthetic { get; init; }
    public int AuthenticationFailures { get; init; }
    public int PlaybackInfoCount { get; init; }
    public int MediaRequests { get; init; }
    public int RangeRequests { get; init; }
    public int PartialResponses { get; init; }
    public FixtureEvent[] Events { get; init; } = [];
}

internal sealed class ControlReport
{
    public string Mode { get; init; } = "MediaPlayerCreateDispose";
    public string[] PlayerConfiguration { get; init; } = [];
    public string Scope { get; init; } = "Isolated Windows MediaPlayer allocation control. No source, no MediaPlayerElement binding, no transport, no server access, no media playback. Not the 20-loop product lifecycle result.";
    public string Status { get; set; } = "Running";
    public string? ErrorCode { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int RequiredCycles { get; init; } = 20;
    public int CompletedCycles { get; set; }
    public int MediaRequests { get; set; }
    public int PartialResponses { get; set; }
    public ResourceSample? Baseline { get; set; }
    public List<ResourceSample> CycleSamples { get; init; } = [];
    public List<ResourceSample> FinalIdleSamples { get; init; } = [];
    public long? PostWarmupPrivateBytesGrowth { get; set; }
    public int? PostWarmupHandleGrowth { get; set; }
    public List<NativeControlCycle> NativeCycles { get; init; } = [];
}

internal sealed class NativeControlCycle
{
    public int Number { get; init; }
    public uint VideoWidth { get; set; }
    public uint VideoHeight { get; set; }
    public long PlayingPositionTicks { get; set; }
    public long PauseDriftTicks { get; set; }
    public long SeekTargetTicks { get; set; }
    public long SeekObservedTicks { get; set; }
    public long ResumePositionTicks { get; set; }
    public bool PlayerDetached { get; set; }
    public int NativeEvents { get; set; }
}

internal sealed class SyntheticMediaMetadata
{
    public bool Synthetic { get; init; }
    public string? FileName { get; init; }
    public long FileLength { get; init; }
    public long DurationTicks { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
}

internal sealed class FixtureEvent
{
    public string Kind { get; init; } = "";
    public string PlaySessionId { get; init; } = "";
    public long PositionTicks { get; init; }
    public string? EventName { get; init; }
    public bool? Failed { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ProbeReport))]
[JsonSerializable(typeof(FixtureStats))]
[JsonSerializable(typeof(ControlReport))]
[JsonSerializable(typeof(SyntheticMediaMetadata))]
internal partial class ProbeJsonContext : JsonSerializerContext;
