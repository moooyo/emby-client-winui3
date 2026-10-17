using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class BitmapSubtitleSelectionReport
{
    public string Scope { get; init; } = "One hash-bound synthetic embedded PGS track through the unchanged product PlaybackCoordinator and NativePlaybackEngine. Actual FFmpegMediaSource, MediaSource, MediaPlaybackItem, MediaPlayer, and MediaPlaybackSession identities are compared through IInspectable and canonical IUnknown while paused. Independently acquired native bitmap pixels establish cached composition restoration. This tool does not establish presented UI pixels or real Emby compatibility.";
    public string IdentityEvidence { get; init; } = "Each baseline retains the actual projected native object's canonical IUnknown reference until all comparisons finish, preventing pointer reuse. Only equality and process-local opaque ordinals are serialized. Every observer-owned COM, projection, and bitmap reference is released before product Stop.";
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Preparing";
    public string? ErrorCode { get; set; }
    public string? ErrorType { get; set; }
    public int? ErrorHResult { get; set; }
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public string? MediaSha256 { get; set; }
    public string? MetadataSha256 { get; set; }
    public string? FfprobeSha256 { get; set; }
    public string? DecoderRuntimeSha256 { get; set; }
    public bool DecoderRuntimeLoadedFromProbeDirectory { get; set; }
    public int SubtitleStreamIndex { get; set; } = -1;
    public string? PlaybackIdHash { get; set; }
    public string? PlaySessionIdHash { get; set; }
    public string? MediaSourceIdHash { get; set; }
    public bool ActualApiBindingVerified { get; set; }
    public bool LogicalPlaybackAndServerSessionPreserved { get; set; }
    public bool AllNativeIdentitiesPreserved { get; set; }
    public bool PausedPositionPreserved { get; set; }
    public long MaximumAllowedPauseDriftTicks { get; init; } = TimeSpan.TicksPerMillisecond * 50;
    public bool OffClearedNativeImage { get; set; }
    public bool CachedCompositionPixelsRestored { get; set; }
    public bool CachedCueSourceTimestampRestored { get; set; }
    public bool NoNegotiationStartOrStopDuringSelection { get; set; }
    public int ImageEvents { get; set; }
    public int ClearEvents { get; set; }
    public int ImageEventsBeforeOff { get; set; }
    public int ClearEventsBeforeOff { get; set; }
    public int ImageEventsAfterOff { get; set; }
    public int ClearEventsAfterOff { get; set; }
    public int ImageEventsAfterReselect { get; set; }
    public int ClearEventsAfterReselect { get; set; }
    public bool ProductPresentationEventsRestored { get; set; }
    public BitmapSubtitleNativeSample? BeforeOff { get; set; }
    public BitmapSubtitleNativeSample? Off { get; set; }
    public BitmapSubtitleNativeSample? Reselected { get; set; }
    public BitmapSubtitleNativeSample? ReselectedStable { get; set; }
    public List<BitmapFixtureObservation> FixtureObservations { get; init; } = [];
    public HlsApiEvent[] ApiEvents { get; set; } = [];
    public List<BitmapSubtitleEngineEvent> EngineEvents { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public bool ObserverReferencesReleasedBeforeStop { get; set; }
    public int ObserverNativeReferencesAtStop { get; set; } = -1;
    public int ObserverProjectionReferencesAtStop { get; set; } = -1;
    public int ObserverBitmapLeasesAtStop { get; set; } = -1;
    public bool StopCompleted { get; set; }
    public bool NativeSessionDrainedAfterStop { get; set; }
    public bool CoordinatorDisposed { get; set; }
    public bool NativeDisposed { get; set; }
    public bool NativeOwnerCleared { get; set; }
    public bool LogoutCompleted { get; set; }
    public bool ExactFinalApiCleanup { get; set; }
    public bool NoMediaOrProgressAfterStop { get; set; }
    public List<string> CleanupErrors { get; init; } = [];
}

internal sealed class BitmapSubtitleNativeSample
{
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool ActivePlaybackMatches { get; set; }
    public bool ProductSessionMatchesBaseline { get; set; }
    public bool ActualNativeDecoderConfigured { get; set; }
    public bool DefaultPlayerOwnerPresent { get; set; }
    public bool FullSourceDirectStream { get; set; }
    public bool HasExternalSubtitle { get; set; }
    public string NativeState { get; set; } = "Missing";
    public long PositionTicks { get; set; } = -1;
    public int? RequestedSubtitleStreamIndex { get; set; }
    public int? RetainedNativeSubtitleStreamIndex { get; set; }
    public string? RetainedNativeSubtitleCodec { get; set; }
    public bool SubtitlePresentationActive { get; set; }
    public bool HasPresentationTimer { get; set; }
    public bool HasPresentationTrack { get; set; }
    public long PresentationEpoch { get; set; }
    public bool HasImage { get; set; }
    public uint PresentationMode { get; set; }
    public ulong TrackId { get; set; }
    public ulong Generation { get; set; }
    public ulong Sequence { get; set; }
    public long CueSourceTimestampTicks { get; set; } = -1;
    public long ScriptPositionTicks { get; set; } = -1;
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public double PixelAspectRatio { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public ulong NativeNonzeroAlphaPixels { get; set; }
    public bool IndependentlyAcquiredBitmap { get; set; }
    public int BitmapWidth { get; set; }
    public int BitmapHeight { get; set; }
    public ulong CountedNonzeroAlphaPixels { get; set; }
    public string? BgraPixelSha256 { get; set; }
    public List<BitmapNativeIdentityObservation> NativeIdentities { get; init; } = [];
}

internal sealed class BitmapNativeIdentityObservation
{
    public required string Role { get; init; }
    public int BaselineOrdinal { get; init; }
    public int ObservedOrdinal { get; init; }
    public bool IInspectableAndIUnknownQueriesSucceeded { get; init; }
    public bool CanonicalIdentityMatchesBaseline { get; init; }
    public bool ProjectionMatchesBaseline { get; init; }
}

internal sealed class BitmapFixtureObservation
{
    public required string Phase { get; init; }
    public DateTimeOffset ReceivedUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string RawHttpResponseSha256 { get; init; }
    public bool HashBoundSyntheticPgsFixture { get; init; }
    public int LoginCount { get; init; }
    public int AuthenticationFailures { get; init; }
    public int PlaybackInfoCount { get; init; }
    public int StartCount { get; init; }
    public int ProgressCount { get; init; }
    public int StopCount { get; init; }
    public int EncodingCleanups { get; init; }
    public int MediaRequests { get; init; }
    public BitmapFixtureEvent[] Events { get; init; } = [];
    public HlsApiEvent[] ApiAttempts { get; init; } = [];
}

internal sealed class BitmapFixtureEvent
{
    public required string Kind { get; init; }
    public required string SessionHash { get; init; }
    public required string ItemId { get; init; }
    public long PositionTicks { get; init; }
    public string? EventName { get; init; }
    public bool? Failed { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

internal sealed class BitmapSubtitleEngineEvent
{
    public required string Kind { get; init; }
    public required string State { get; init; }
    public required string PlaybackIdHash { get; init; }
    public long PositionTicks { get; init; }
    public string? ErrorCode { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BitmapSubtitleSelectionReport))]
internal partial class BitmapSubtitleSelectionJsonContext : JsonSerializerContext;
