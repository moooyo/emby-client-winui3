using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class PosterLifecycleReport
{
    public string Scope { get; init; } = "Isolated 80-cycle baseline using the linked product NativePosterDecoder, one Image, and the same synthetic PNG bytes. No library paging, card models, cache, media playback, UI automation, forced GC, or explicit WinRT operation Close is introduced. This is a resource observation, not library acceptance.";
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string Status { get; set; } = "Preparing";
    public string Stage { get; set; } = "Preparing";
    public string? ErrorCode { get; set; }
    public int? ErrorHResult { get; set; }
    public int RequiredCycles { get; init; } = 80;
    public int CompletedCycles { get; set; }
    public string ServerId { get; init; } = "synthetic-large-server-5000";
    public string ServerVersion { get; set; } = "NotObserved";
    public string ItemId { get; init; } = "large-000001";
    public bool SyntheticIdentityVerified { get; set; }
    public int ImageDownloadCount { get; set; }
    public bool LogoutCompleted { get; set; }
    public bool HttpDisposedBeforeBaseline { get; set; }
    public string InputFileName { get; init; } = "poster-input.png";
    public string? InputSha256 { get; set; }
    public int InputBytes { get; set; }
    public int InputPixelWidth { get; set; }
    public int InputPixelHeight { get; set; }
    public double RasterizationScale { get; set; }
    public int DecodePixelWidth { get; set; }
    public int DecodePixelHeight { get; set; }
    public bool ForcedGcRequested { get; init; } = false;
    public string RenderingEvidence { get; init; } = "Two CompositionTarget.Rendering callbacks after binding and two after clearing each Image.Source. This observes render boundaries, not screenshot pixels or compositor presentation.";
    public string ResourceInterpretation { get; init; } = "Raw natural-runtime trends only. No playback resource gate or library pass is applied. Compare late ten-cycle medians with cycles 11-20 and retain final 2/10/30-second idle samples and natural GC collection counts.";
    public PosterResourceSample? Baseline { get; set; }
    public List<PosterCycle> Cycles { get; init; } = [];
    public List<PosterResourceSample> FinalIdle { get; init; } = [];
    public double? LateMedianHandleDelta { get; set; }
    public double? LateMedianPrivateBytesDelta { get; set; }
    public double? LastFortyHandleSlopePerCycle { get; set; }
    public double? LastFortyPrivateBytesSlopePerCycle { get; set; }
    public bool FinalSourceCleared { get; set; }
    public List<string> CleanupErrors { get; init; } = [];
}

internal sealed class PosterCycle
{
    public int Cycle { get; init; }
    public int BitmapPixelWidth { get; set; }
    public int BitmapPixelHeight { get; set; }
    public bool BoundInDecoderCallback { get; set; }
    public int BoundRenderCallbacks { get; set; }
    public int ClearedRenderCallbacks { get; set; }
    public bool SourceCleared { get; set; }
    public double DecodeMilliseconds { get; set; }
    public PosterResourceSample? Resources { get; set; }
}

internal sealed class PosterResourceSample
{
    public string Label { get; init; } = "";
    public double ElapsedSeconds { get; init; }
    public DateTimeOffset Utc { get; init; } = DateTimeOffset.UtcNow;
    public long PrivateBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public int Handles { get; init; }
    public int Threads { get; init; }
    public long ManagedBytes { get; init; }
    public long ManagedTotalAllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PosterLifecycleReport))]
internal partial class PosterLifecycleJsonContext : JsonSerializerContext;
