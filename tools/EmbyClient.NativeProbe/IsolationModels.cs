using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class IsolationReport
{
    public string Scope { get; init; } = "SYNTHETIC loopback item 1001, default product player owner. Actual native Opening cancellation and invalid-media decoder failure are followed by recovery. Retired handler replay and old-ID commands are explicit fault injection, not naturally observed late callbacks. Separate from resource acceptance.";
    public string Status { get; set; } = "Running";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public int RequiredCycles { get; init; } = 3;
    public int CompletedCycles { get; set; }
    public int InvalidMediaRequests { get; set; }
    public int InvalidMediaCredentialRejections { get; set; }
    public bool FinalPlayerDetached { get; set; }
    public bool FinalNetworkQuiet { get; set; }
    public IsolationConcurrentDispose ConcurrentDispose { get; init; } = new();
    public string[] Limitations { get; init; } = ["No resource gate is evaluated in this fault-injection process", "Synthetic development API is not a real Emby server", "No presented-pixel, audio-device, physical-key, or exhaustive concurrency claim"];
    public List<IsolationCycle> Cycles { get; init; } = [];
    public List<IsolationEngineEvent> EngineEvents { get; init; } = [];
    public List<HlsApiEvent> ApiEvents { get; set; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

internal sealed class IsolationConcurrentDispose
{
    public bool NativeOpeningObserved { get; set; }
    public bool SourceWasBound { get; set; }
    public bool OpenWasPending { get; set; }
    public bool BothDisposeCallsInitiallyPending { get; set; }
    public bool OpenCancelled { get; set; }
    public bool FirstDisposeCompleted { get; set; }
    public bool SecondDisposeCompleted { get; set; }
    public bool SecondDisposeObservedCompleteDrain { get; set; }
    public bool OwnerCleared { get; set; }
    public bool PlayerDetached { get; set; }
    public bool CancelledOpeningDidNotReportStart { get; set; }
}

internal sealed class IsolationCycle
{
    public int Number { get; init; }
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "OpeningCancellation";
    public string? ErrorCode { get; set; }
    public string? CancelNativeState { get; set; }
    public bool CancelSourceWasBound { get; set; }
    public bool CancelOpenTaskWasPending { get; set; }
    public bool CancellationObserved { get; set; }
    public bool CancellationDetached { get; set; }
    public string? CancelledSessionHash { get; set; }
    public string? NativeDecoderFailureCode { get; set; }
    public string? DecoderFailureTerminalCode { get; set; }
    public bool DecoderFailureSourceWasBound { get; set; }
    public bool FailureDetached { get; set; }
    public string? FailedSessionHash { get; set; }
    public List<IsolationRecovery> Recoveries { get; init; } = [];
}

internal sealed class IsolationRecovery
{
    public string Kind { get; init; } = "";
    public string? SessionHash { get; set; }
    public long NativeInitialTicks { get; set; }
    public long PausedDriftTicks { get; set; }
    public long ResumedNativeTicks { get; set; }
    public int ReplayedCallbacks { get; set; }
    public int StaleIdCommands { get; set; }
    public bool CurrentSourceSurvived { get; set; }
    public bool MutedVolumePreserved { get; set; }
    public bool NoRetiredEngineEvents { get; set; }
    public bool NoUnexpectedApiControls { get; set; }
    public bool ApiSettledBeforeReplay { get; set; }
    public int ApiReplayStartCount { get; set; }
    public int EngineReplayStartCount { get; set; }
    public bool ValidReportOrder { get; set; }
    public bool DetachedAfterStop { get; set; }
}

internal sealed class IsolationEngineEvent
{
    public string PlaybackHash { get; init; } = "";
    public string Kind { get; init; } = "";
    public string State { get; init; } = "";
    public long PositionTicks { get; init; }
    public string? ErrorCode { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(IsolationReport))]
internal partial class IsolationJsonContext : JsonSerializerContext;
