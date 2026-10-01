using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class HardwareDecodeReport
{
    public string Status { get; set; } = "Running";
    public string Scope { get; init; } = "Verified SYNTHETIC loopback H.264/AAC media through the linked product NativePlaybackEngine and PlaybackCoordinator. Actual decoder snapshots and native lifecycle observations; not real Emby compatibility or an Intel/AMD/NVIDIA test matrix.";
    public string ExecutionMode { get; init; } = "ProductHardwareDecoding";
    public string[] NotVerified { get; init; } = ["Pixel capture or first presented frame", "Audible audio output", "Other graphics adapters or driver versions", "Other codecs or profiles", "Long-running resource regression", "Real Emby server compatibility"];
    public bool HardwareRequested { get; init; }
    public bool HardwareRequired { get; init; }
    public string? RequiredVendor { get; init; }
    public string RequestedApi { get; init; } = "Auto";
    public bool ExpectedApiFallback { get; init; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Stage { get; set; } = "Initialization";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public long FixtureDurationTicks { get; set; }
    public string? DeliveryMethod { get; set; }
    public double OpenMilliseconds { get; set; }
    public List<HardwareDecodeObservation> DecodingSamples { get; init; } = [];
    public long PauseStartTicks { get; set; }
    public long PauseEndTicks { get; set; }
    public long SeekTargetTicks { get; init; } = TimeSpan.FromSeconds(20).Ticks;
    public long SeekObservedTicks { get; set; }
    public long ResumePositionTicks { get; set; }
    public bool InitialMuted { get; set; }
    public bool SourceTimelinePreserved { get; set; }
    public bool PlayerDetachedAfterStop { get; set; }
    public bool StoppedSnapshotObserved { get; set; }
    public bool LastDecoderSnapshotRetainedAfterStop { get; set; }
    public bool NoNetworkAfterStop { get; set; }
    public bool ReportOrderValid { get; set; }
    public int StartReports { get; set; }
    public int ProgressReports { get; set; }
    public int StopReports { get; set; }
    public long StopReportPositionTicks { get; set; }
    public int MediaRequests { get; set; }
    public int PartialResponses { get; set; }
    public ResourceSample? ResourcesAfterCleanup { get; set; }
    public List<string> Diagnostics { get; init; } = [];
}

internal sealed class HardwareDecodeObservation
{
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Stage { get; init; } = "Playing";
    public bool HardwareRequested { get; init; }
    public string Decoder { get; init; } = "Pending";
    public bool HardwareFallback { get; init; }
    public string RequestedApi { get; init; } = "Auto";
    public string ActualApi { get; init; } = "Pending";
    public string? FallbackReason { get; init; }
    public string? NativeDecoderName { get; init; }
    public ulong DecodedFrames { get; init; }
    public ulong HardwareDecodedFrames { get; init; }
    public string Codec { get; init; } = "Unknown";
    public string? GpuName { get; init; }
    public uint? GpuVendorId { get; init; }
    public string? GpuVendor { get; init; }
    public long? AdapterLuid { get; init; }
    public long NativePositionTicks { get; init; }
    public long EnginePositionTicks { get; init; }
    public long TimelineOffsetTicks { get; init; }
    public uint VideoWidth { get; init; }
    public uint VideoHeight { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(HardwareDecodeReport))]
internal partial class HardwareDecodeJsonContext : JsonSerializerContext;
