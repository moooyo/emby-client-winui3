using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class NetworkRetryMediaMetadata
{
    public bool Synthetic { get; init; }
    public string? FileName { get; init; }
    public long FileLength { get; init; }
    public long DurationTicks { get; init; }
}

internal sealed class NetworkRetryReport
{
    public string Scope { get; init; } = "SYNTHETIC 180-second media on the fixed owned loopback fixture, with a bounded cold-range HTTP proxy. The proxy injects HTTP 503 responses, never engine failure events. Explicit RetryAsync recovery is separate from prior twenty-loop resource results.";
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "ValidateFixture";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public long MediaLength { get; set; }
    public long MediaDurationTicks { get; set; }
    public string? MediaSha256 { get; set; }
    public string? MetadataSha256 { get; set; }
    public long AllowedPrefixBytes { get; set; }
    public long MetadataRangeStart { get; set; }
    public long MetadataRangeEndExclusive { get; set; }
    public long ColdByteProbe { get; set; }
    public bool ColdByteUndeliveredBeforeFault { get; set; }
    public long PlayingNativeTicksBeforeFault { get; set; }
    public long PausedNativeTicksBeforeFault { get; set; }
    public int? OriginalRequestedAudioIndex { get; set; }
    public int? OriginalDefaultAudioIndex { get; set; }
    public int? RecoveryRequestedAudioIndex { get; set; }
    public int? OriginalRequestedSubtitleIndex { get; set; }
    public int? RecoveryRequestedSubtitleIndex { get; set; }
    public long RequestedColdSeekTicks { get; init; } = 900_000_000;
    public List<NativeTimeRangeSummary> NativeBufferedBeforeFault { get; set; } = [];
    public bool ColdSeekTargetOutsideBuffer { get; set; }
    public List<long> NativeSeekCompletedPositionsBeforeFailure { get; init; } = [];
    public string? ColdSeekOutcome { get; set; }
    public string? NativeFailureCode { get; set; }
    public string? ConfirmedRelayFailureCode { get; set; }
    public string? RawNativeFailureKind { get; set; }
    public int? RawNativeFailureHResult { get; set; }
    public long? NativeFailureSnapshotTicks { get; set; }
    public bool ActualColdHttp503Observed { get; set; }
    public bool? RecoveryOffered { get; set; }
    public string? RecoveryErrorCode { get; set; }
    public long? RecoveryTargetTicks { get; set; }
    public string? RecoveryPositionSource { get; set; }
    public bool RecoveryPausedIntent { get; set; }
    public bool RetryInvoked { get; set; }
    public bool RetryPlaybackIdChanged { get; set; }
    public bool RetryServerSessionChanged { get; set; }
    public bool RetrySourceBound { get; set; }
    public bool RetryNativePaused { get; set; }
    public bool RecoveryConsumed { get; set; }
    public long? RetryNativePositionTicks { get; set; }
    public long? RetrySnapshotPositionTicks { get; set; }
    public long? RetryPausedDriftTicks { get; set; }
    public long? RetryResumedNativeTicks { get; set; }
    public bool ValidSessionReports { get; set; }
    public bool NoOldBindingRequestsAfterRetry { get; set; }
    public bool FinalDetached { get; set; }
    public bool FinalMediaRequestsQuiet { get; set; }
    public int ProxyBindings { get; set; }
    public int CredentialRejections { get; set; }
    public List<ColdRangeRecord> Ranges { get; set; } = [];
    public List<HlsApiEvent> ApiEvents { get; set; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public string[] Limitations { get; init; } = ["A failed seek may update the native Position cursor without a decoded frame; recovery-target provenance is recorded separately", "Native open/clock/dimensions and pause are observed; presented pixels and audible output are not", "This single bounded recovery scenario is not a resource test or broad network-fault certification"];
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NetworkRetryReport))]
[JsonSerializable(typeof(NetworkRetryMediaMetadata))]
internal partial class NetworkRetryJsonContext : JsonSerializerContext;
