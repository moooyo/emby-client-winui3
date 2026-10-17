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
    public string Scope { get; init; } = "SYNTHETIC 180-second media on the fixed owned loopback fixture. A real cold-range HTTP 503 must cause NetworkFailure while the original native source remains bound. Explicit RetryAsync is followed by replay of seven captured retired handlers and nine old-ID commands against the paused replacement. Separate from resource acceptance.";
    public string PostBindLateFailureGateStatus { get; set; } = "NotRun";
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
    public bool OriginalSourceBoundBeforeFault { get; set; }
    public bool OriginalSourceBoundAtFailedEvent { get; set; }
    public bool FailedEventMatchedOriginalPlayback { get; set; }
    public string? NativeFailureObservationStage { get; set; }
    public string? NativeFailureObservationErrorCode { get; set; }
    public bool CallbacksCapturedBeforeFault { get; set; }
    public string? CallbackCaptureStage { get; set; }
    public string? OriginalPlaybackHash { get; set; }
    public string? OriginalSessionHash { get; set; }
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
    public bool CoordinatorDisposed { get; set; }
    public bool NativeDisposed { get; set; }
    public bool NativeOwnerCleared { get; set; }
    public bool LogoutCompleted { get; set; }
    public bool ProxyDisposed { get; set; }
    public NetworkRetryIsolation RetiredIsolation { get; init; } = new();
    public List<IsolationEngineEvent> EngineEvents { get; init; } = [];
    public List<string> CleanupErrors { get; init; } = [];
    public List<ColdRangeRecord> Ranges { get; set; } = [];
    public List<HlsApiEvent> ApiEvents { get; set; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public string[] Limitations { get; init; } = ["The original failure must be caused by real HTTP 503; retired-handler replay occurs only after successful explicit retry and is deliberate injection, not naturally observed late callbacks", "A failed seek may update the native Position cursor without a decoded frame; recovery-target provenance is recorded separately", "Native open/clock/dimensions and pause are observed; presented pixels and audible output are not", "This single bounded recovery and isolation scenario is not a resource test or broad network-fault certification"];
}

internal sealed class NetworkRetryIsolation
{
    public string Status { get; set; } = "NotRun";
    public string Stage { get; set; } = "NotRun";
    public string? ErrorCode { get; set; }
    public string? ReplacementPlaybackHash { get; set; }
    public string? ReplacementSessionHash { get; set; }
    public int ExpectedReplayedCallbacks { get; init; } = 7;
    public int ReplayedCallbacks { get; set; }
    public int StaleIdCommands { get; set; }
    public bool ApiSettledBeforeReplay { get; set; }
    public int ApiReplayStartCount { get; set; }
    public int EngineReplayStartCount { get; set; }
    public int CompletedRequestsBeforeReplay { get; set; }
    public int CompletedRequestsAfterReplay { get; set; }
    public int NegotiationsBeforeReplay { get; set; }
    public int NegotiationsAfterReplay { get; set; }
    public long? NativeBeforeTicks { get; set; }
    public long? NativeAfterTicks { get; set; }
    public long? PausedDriftTicks { get; set; }
    public long? SnapshotBeforeTicks { get; set; }
    public long? SnapshotAfterTicks { get; set; }
    public bool LogicalSessionPreserved { get; set; }
    public bool PlayerIdentityPreserved { get; set; }
    public bool SourceIdentityPreserved { get; set; }
    public bool PausedStatePreserved { get; set; }
    public bool SnapshotPositionPreserved { get; set; }
    public bool VolumeAndMutePreserved { get; set; }
    public bool RecoveryStillConsumed { get; set; }
    public bool NoRetiredEngineEvents { get; set; }
    public bool NoEngineEventsDuringReplay { get; set; }
    public int TotalEngineEventsDuringReplay { get; set; }
    public int AllowedEngineNotifications { get; set; }
    public int UnexpectedEngineNotifications { get; set; }
    public bool NoFailedOrEndedEngineEvents { get; set; }
    public bool NoUnexpectedEngineEvents { get; set; }
    public List<HlsApiEvent> ApiEventsDuringReplay { get; set; } = [];
    public bool NoUnexpectedApiControls { get; set; }
    public bool NoPlaybackNegotiationDuringReplay { get; set; }
    public bool ApiCompletionDeltaMatchesObservedProgress { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NetworkRetryReport))]
[JsonSerializable(typeof(NetworkRetryMediaMetadata))]
internal partial class NetworkRetryJsonContext : JsonSerializerContext;
