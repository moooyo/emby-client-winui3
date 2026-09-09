using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class RealHlsCredentials
{
    public string? ServerUrl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
}

internal sealed class RealHlsReport
{
    public string Scope { get; init; } = "Owned official Emby 4.9.5.0 loopback validation server, fixed item 5, forced HLS, Windows native engine and real PlaybackCoordinator. Separate from synthetic direct-stream evidence.";
    public string ServerId { get; init; } = "cf4feb10df224135877fc61204a28212";
    public string ServerVersion { get; set; } = "";
    public string ItemId { get; init; } = "5";
    public string Status { get; set; } = "Running";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int RequiredLoops { get; init; } = 20;
    public string ExecutionMode { get; init; } = "NormalRealHlsLifecycle";
    public int CompletedLoops { get; set; }
    public long? ItemDurationTicks { get; set; }
    public long? PostWarmupPrivateBytesGrowth { get; set; }
    public int? PostWarmupHandleGrowth { get; set; }
    public string ResourceThreshold { get; init; } = "Last five versus loops 5-9 median: <=64 MiB private bytes and <=32 handles. Last eight samples must not all increase in handles or all increase by >1 MiB private bytes. Thirty additional seconds idle are recorded.";
    public string[] Limitations { get; init; } = ["Native clock and dimensions do not prove a presented pixel frame or subtitle pixels", "Audible output and physical media keys are not tested", "API traffic is counted; native HLS media requests and packet-level quiescence are not instrumented", "Other Emby versions, servers, codecs, and media items are outside this result"];
    public List<RealHlsLoop> Loops { get; init; } = [];
    public List<ResourceSample> FinalIdleSamples { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public List<HlsNegotiationDiagnostic> Negotiations { get; set; } = [];
}

internal sealed class RealHlsLoop
{
    public int Number { get; init; }
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Open";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public long InitialPositionTicks { get; set; }
    public long OpenedNativePositionTicks { get; set; }
    public long OpenedSnapshotPositionTicks { get; set; }
    public long TimelineOffsetTicks { get; set; }
    public long OpenedNativeDurationTicks { get; set; }
    public long SourceDurationTicks { get; set; }
    public uint VideoWidth { get; set; }
    public uint VideoHeight { get; set; }
    public long PausedDriftTicks { get; set; }
    public long SeekTargetTicks { get; set; }
    public long SeekObservedNativeTicks { get; set; }
    public long SeekObservedSnapshotTicks { get; set; }
    public long SeekBeforeNativePositionTicks { get; set; }
    public long SeekBeforeNativeDurationTicks { get; set; }
    public string? SeekBeforeNativeState { get; set; }
    public bool SeekBeforeCanSeek { get; set; }
    public bool SeekBeforeEngineCanSeekInPlace { get; set; }
    public bool PlaybackReplacedBySeek { get; set; }
    public bool PausePreservedAfterSeek { get; set; }
    public long SeekLastNativePositionTicks { get; set; }
    public long SeekLastNativeDurationTicks { get; set; }
    public string? SeekLastNativeState { get; set; }
    public bool SeekLastCanSeek { get; set; }
    public long? SeekLastSnapshotPositionTicks { get; set; }
    public string? SeekLastSnapshotState { get; set; }
    public string? SeekObservationPoint { get; set; }
    public int? SeekObservationHResult { get; set; }
    public List<long> NativeSeekCompletedPositions { get; init; } = [];
    public List<NativeTimeRangeSummary> SeekBeforeSeekableRanges { get; set; } = [];
    public List<NativeTimeRangeSummary> SeekLastSeekableRanges { get; set; } = [];
    public List<NativeTimeRangeSummary> SeekLastBufferedRanges { get; set; } = [];
    public bool? TargetInsideLastSeekableRange { get; set; }
    public List<HlsPlaylistSummary> PlaylistSummaries { get; init; } = [];
    public long ResumeNativePositionTicks { get; set; }
    public bool PlayerDetached { get; set; }
    public bool NoApiRequestsAfterStop { get; set; }
    public bool ValidReportOrder { get; set; }
    public int SuccessfulEncodingCleanups { get; set; }
    public ResourceSample? Resources { get; set; }
    public List<HlsApiEvent> ApiEvents { get; set; } = [];
}

internal sealed class HlsApiEvent
{
    public string Operation { get; init; } = "";
    public string? SessionHash { get; init; }
    public long? PositionTicks { get; init; }
    public string? EventName { get; init; }
    public int StatusCode { get; set; }
}

internal sealed class HlsNegotiationDiagnostic
{
    public string Container { get; init; } = "";
    public string Protocol { get; init; } = "";
    public string SubProtocol { get; init; } = "";
    public bool SourceIdMatchesRequestedItem { get; init; }
    public bool SourceItemIdMatchesRequestedItem { get; init; }
    public bool HasTranscodingUrl { get; init; }
    public bool UrlWasAbsolute { get; init; }
    public bool? SameOrigin { get; init; }
    public string PathShape { get; init; } = "";
    public string[] QueryKeys { get; init; } = [];
    public string? CopyTimestamps { get; init; }
    public long? StartTimeTicks { get; init; }
}

internal sealed class HlsPlaylistSummary
{
    public string Kind { get; init; } = "";
    public string ObservationPoint { get; init; } = "";
    public string? PlaylistType { get; init; }
    public int SegmentCount { get; init; }
    public double DurationSeconds { get; init; }
    public long? MediaSequence { get; init; }
    public bool HasEndList { get; init; }
    public double? StartOffsetSeconds { get; init; }
}

internal sealed class NativeTimeRangeSummary
{
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RealHlsCredentials))]
[JsonSerializable(typeof(RealHlsReport))]
internal partial class RealHlsJsonContext : JsonSerializerContext;
