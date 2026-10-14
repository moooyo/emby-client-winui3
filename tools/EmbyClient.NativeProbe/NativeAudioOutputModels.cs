using System.Text.Json.Serialization;
using EmbyClient.App.Playback;
using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

internal sealed class NativeAudioFixtureManifest
{
    public bool Synthetic { get; init; }
    public bool QaOnly { get; init; }
    public string OriginalFileSha256 { get; init; } = "";
    public NativeAudioFixture[] Fixtures { get; init; } = [];
}

internal sealed class NativeAudioFixture
{
    public string FileName { get; init; } = "";
    public long FileLength { get; init; }
    public string Sha256 { get; init; } = "";
    public long DurationTicks { get; init; }
    public string Container { get; init; } = "";
    public int VideoStreamIndex { get; init; }
    public string VideoCodec { get; init; } = "";
    public int VideoWidth { get; init; }
    public int VideoHeight { get; init; }
    public int AudioStreamIndex { get; init; }
    public string AudioCodec { get; init; } = "";
    public string? AudioProfile { get; init; }
    public int AudioChannels { get; init; }
    public int AudioSampleRate { get; init; }
}

internal sealed class NativeAudioOutputProbeReport
{
    public string Status { get; set; } = "Running";
    public string ExecutionMode { get; init; } = "SyntheticNativeAudioOutput";
    public string Scope { get; init; } = "QA-only measured 60-second H.264 with AC3, EAC3 and AAC; owned ephemeral loopback media origin; linked product NativePlaybackEngine, NativeAudioInterop and PlaybackCoordinator. Synthetic in-process API negotiation/report receipts are recorded separately from real native backend assignments. No substitute playback implementation or user media is used.";
    public string[] NotVerified { get; init; } = ["Physical receiver bitstream decoding or audible output", "Real Emby server compatibility", "Other endpoint formats, codecs, adapters or drivers", "DTS, DTS-HD or TrueHD carrier delivery", "Device hotplug", "Long-running resource regression"];
    public bool PhysicalReceiverVerified { get; init; }
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public bool NativeAudioApiAvailable { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string Stage { get; set; } = "Initialization";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public List<NativeAudioOutputProbeCase> Cases { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

internal sealed class NativeAudioOutputProbeCase
{
    public string Name { get; init; } = "";
    public string Status { get; set; } = "Running";
    public string? Outcome { get; set; }
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public NativeAudioFixture Fixture { get; init; } = new();
    public AudioPassthroughMode RequestedMode { get; init; }
    public Guid PlaybackId { get; set; }
    public bool ActualNativeFormatAttemptObserved { get; set; }
    public bool NativeCarrierSubmissionObserved { get; set; }
    public NativeAudioOutputProbeSample? FirstCarrierSubmissionSample { get; set; }
    public string? NativeSourceCodecObserved { get; set; }
    public string NativeCarrierControlOutcome { get; set; } = "NotExercised_NoActiveNativeCarrier";
    public ulong? CarrierGenerationBeforeControls { get; set; }
    public ulong? PauseSubmittedFramesBefore { get; set; }
    public ulong? PauseSubmittedFramesAfter { get; set; }
    public ulong? SeekSubmittedFrames { get; set; }
    public ulong? ResumeSubmittedFrames { get; set; }
    public bool? NativePauseSubmissionStable { get; set; }
    public bool? NativeSeekRetainedOwner { get; set; }
    public bool? NativeResumeSubmissionGrowth { get; set; }
    public bool PhysicalReceiverVerified { get; init; }
    public long PlayingPositionTicks { get; set; }
    public long PauseStartTicks { get; set; }
    public long PauseEndTicks { get; set; }
    public long SeekTargetTicks { get; init; } = TimeSpan.FromSeconds(8).Ticks;
    public long SeekObservedTicks { get; set; }
    public long ResumePositionTicks { get; set; }
    public int NativePcmAudioTrackCount { get; set; }
    public string PcmReplacementControl { get; set; } = "NotRun";
    public bool? PcmReplacementKeptLogicalPlaybackId { get; set; }
    public bool? PcmReplacementKeptSyntheticServerSession { get; set; }
    public int? PcmReplacementAudioTrackCount { get; set; }
    public int NativeBackendAssignments { get; set; }
    public int DistinctNativePlaybackIds { get; set; }
    public int SyntheticNegotiations { get; set; }
    public int SyntheticStartReports { get; set; }
    public int SyntheticStopReports { get; set; }
    public int DistinctSyntheticServerSessions { get; set; }
    public bool ExclusiveCleanupExercised { get; set; }
    public bool NativeCleanupSucceeded { get; set; }
    public bool PlayerDetachedAfterStop { get; set; }
    public bool NoUpstreamRequestsAfterStop { get; set; }
    public bool EngineDisposed { get; set; }
    public bool ListenerClosed { get; set; }
    public List<NativeAudioOutputProbeSample> Samples { get; init; } = [];
    public NativeAudioBackendAssignment[] BackendAssignments { get; set; } = [];
    public NativeAudioApiReceipt[] SyntheticApiReceipts { get; set; } = [];
    public SyntheticHlsRequest[] MediaRequests { get; set; } = [];
}

internal sealed class NativeAudioOutputProbeSample
{
    public string Stage { get; init; } = "";
    public DateTimeOffset SampledAt { get; init; } = DateTimeOffset.UtcNow;
    public PlaybackEngineSnapshot? Engine { get; init; }
    public AudioOutputSnapshot? ManagedOutput { get; init; }
    public NativeAudioProbeState Native { get; init; } = new();
}

internal sealed class NativeAudioProbeState
{
    public bool HasDecoderSource { get; init; }
    public bool ExclusiveOutputOpened { get; init; }
    public bool CleanupSucceeded { get; init; }
    public bool Retired { get; init; }
    public int NativeAudioStreamIndex { get; init; } = -1;
    public string? NativeAudioCodec { get; init; }
    public NativeAudioObservation? RawOutput { get; init; }
}

internal sealed class NativeAudioBackendAssignment
{
    public int Number { get; init; }
    public Guid PlaybackId { get; init; }
    public AudioPassthroughMode BackendRequestedMode { get; init; }
    public NativeAudioProbeState Native { get; init; } = new();
}

internal sealed class NativeAudioApiReceipt
{
    public string Operation { get; init; } = "";
    public string SessionHash { get; init; } = "";
    public long? PositionTicks { get; init; }
    public string? EventName { get; init; }
    public bool? IsPaused { get; init; }
    public int StatusCode { get; init; }
}

internal sealed class NativeAudioOutputProbeObserver
{
    private readonly List<NativeAudioBackendAssignment> _assignments = [];
    internal NativeAudioBackendAssignment[] Assignments => [.. _assignments];

    internal void RecordBackendAssignment(Guid playbackId, AudioPassthroughMode requestedMode, NativeAudioProbeState state)
    {
        if (_assignments.Count >= 8) throw new PlaybackException("NativeAudioBackendAssignmentLimitExceeded");
        _assignments.Add(new NativeAudioBackendAssignment
        {
            Number = _assignments.Count + 1, PlaybackId = playbackId, BackendRequestedMode = requestedMode, Native = state
        });
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(NativeAudioFixtureManifest))]
[JsonSerializable(typeof(NativeAudioOutputProbeReport))]
internal partial class NativeAudioOutputProbeJsonContext : JsonSerializerContext;
