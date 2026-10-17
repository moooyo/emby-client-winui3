using System.Text.Json.Serialization;

namespace EmbyClient.NativeProbe;

internal sealed class PlainSubtitleTimingReport
{
    public string Scope { get; init; } = "One hash-bound synthetic 100-second H.264/AAC MKV with an embedded ordinary SubRip/SRT stream, opened by the loaded FFmpegMediaSource through CreateFromFileAsync and a real MediaPlaybackItem/MediaPlayer. A +3-second DefaultSubtitleDelay is configured before creating a fresh source. Actual TimedTextCue text, start, duration, active-cue metadata, synchronous CueEntered native-clock samples, and native SeekCompleted establish the fresh-decode and post-seek timing result. ApplicationPresented metadata is not evidence of rendered glyphs, product coordinator behavior, ordinary product UI pixels, or real Emby compatibility. Negative delay is outside this run.";
    public string CleanupScope { get; init; } = "Only primitive cue and clock copies enter this report. Observer event subscriptions and observer-owned WinRT projections are retired and released before the private player is paused and its source is cleared. The owning playback item, media source, decoder, and player are subsequently released. No COM identity lease, cue lease, bitmap, or product session is manufactured or retained.";
    public string ExecutionMode { get; init; } = "NativeEmbeddedPlainSrtTiming";
    public string Status { get; set; } = "Running";
    public string Stage { get; set; } = "Preparing";
    public string? FailureStage { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorType { get; set; }
    public int? ErrorHResult { get; set; }
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public int ProcessId { get; init; } = Environment.ProcessId;
    public bool NativeAot { get; init; } = !System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported;
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }
    public bool OutputReservedWithCreateNew { get; set; }
    public string? MediaSha256 { get; set; }
    public string? MetadataSha256 { get; set; }
    public string? FfprobeSha256 { get; set; }
    public string? SubtitlePacketsSha256 { get; set; }
    public string? SubtitleSourceSha256 { get; set; }
    public string? ProbeExecutableSha256 { get; set; }
    public string? DecoderRuntimeSha256 { get; set; }
    public bool DecoderRuntimeLoadedFromProbeDirectory { get; set; }
    public bool HashBoundFixtureVerified { get; set; }
    public bool ExactInputSubtitlePacketsVerified { get; set; }
    public long FixtureDurationTicks { get; set; }
    public long DecoderDurationTicks { get; set; }
    public long NativeDurationTicks { get; set; }
    public int SubtitleStreamIndex { get; set; } = -1;
    public string? NativeSubtitleCodecName { get; set; }
    public string? NativeTimedMetadataKind { get; set; }
    public bool NativeEmbeddedTextTrackVerified { get; set; }
    public bool NativePlaybackItemCreated { get; set; }
    public bool PlaybackSessionBoundBeforeSourceAssignment { get; set; }
    public bool DefaultDelayConfiguredBeforeSourceCreation { get; set; }
    public long RequestedSubtitleDelayTicks { get; init; } = TimeSpan.TicksPerSecond * 3;
    public long ActualConfigurationSubtitleDelayTicks { get; set; }
    public long ActualEmbeddedStreamDelayTicks { get; set; }
    public bool ActualConfigurationFastSeek { get; set; }
    public string? ActualVideoDecoderMode { get; set; }
    public string? ActualVideoDecoderEngine { get; set; }
    public bool InitialMutePreserved { get; set; }
    public bool FreshSourceStartedAtZeroWithoutSeek { get; set; }
    public bool FreshDecodeTimingVerified { get; set; }
    public bool PostSeekTimingVerified { get; set; }
    public bool NativeClockAdvancedBeforeFirstCue { get; set; }
    public bool NativeClockAdvancedAfterSeek { get; set; }
    public long SeekTargetTicks { get; init; } = TimeSpan.TicksPerSecond * 33;
    public int SeekCompletedCountBeforeRequest { get; set; }
    public int SeekCompletedCountAfterRequest { get; set; }
    public bool NativeSeekCompletedVerified { get; set; }
    public long MaximumSeekErrorTicks { get; init; } = TimeSpan.TicksPerMillisecond * 750;
    public long MaximumCueEventClockErrorTicks { get; init; } = TimeSpan.TicksPerMillisecond * 750;
    public long MaximumPausedClockDriftTicks { get; init; } = TimeSpan.TicksPerMillisecond * 50;
    public long FirstPauseDriftTicks { get; set; } = -1;
    public PlainSubtitleClockSnapshot? OpenedAtZero { get; set; }
    public PlainSubtitleClockSnapshot? BeforeFirstDelayedCue { get; set; }
    public PlainSubtitleTimingPhase? FreshDecode { get; set; }
    public PlainSubtitleClockSnapshot? AccurateSeekCompleted { get; set; }
    public PlainSubtitleClockSnapshot? BeforeSecondDelayedCue { get; set; }
    public PlainSubtitleTimingPhase? AfterSeek { get; set; }
    public List<PlainSubtitleNativeEvent> NativeEvents { get; init; } = [];
    public List<PlainSubtitleCueEvent> CueEvents { get; init; } = [];
    public int DroppedNativeEvents { get; set; }
    public int DroppedCueEvents { get; set; }
    public bool MediaFailedObserved { get; set; }
    public bool TrackFailedObserved { get; set; }
    public string? FirstObserverFailureCode { get; set; }
    public bool ObserversRetired { get; set; }
    public bool ObserverReferencesReleasedBeforeStop { get; set; }
    public int ObserverProjectionReferencesAtStop { get; set; } = -1;
    public int ObserverNativeLeasesAtStop { get; set; } = -1;
    public int ObserverActiveCapturesAtStop { get; set; } = -1;
    public bool PlayerDetachedBeforeStop { get; set; }
    public bool PlayerPauseRequested { get; set; }
    public bool PlayerSourceCleared { get; set; }
    public bool DecoderPlaybackSessionCleared { get; set; }
    public bool StopCompleted { get; set; }
    public bool MediaSourceDisposed { get; set; }
    public bool DecoderDisposed { get; set; }
    public bool PlayerDisposed { get; set; }
    public bool AllOwningReferencesReleased { get; set; }
    public List<string> CleanupErrors { get; init; } = [];
    public int? RequestedProcessExitCode { get; set; }
}

internal sealed class PlainSubtitleTimingPhase
{
    public string Name { get; init; } = "";
    public long OriginalCueStartTicks { get; init; }
    public long OriginalCueEndTicks { get; init; }
    public long ExpectedCueStartTicks { get; init; }
    public long ExpectedCueEndTicks { get; init; }
    public PlainSubtitleCueEvent? Entered { get; set; }
    public PlainSubtitleClockSnapshot? AtCue { get; set; }
    public PlainSubtitleClockSnapshot? PausedStable { get; set; }
    public bool ExactCueStartAndDurationVerified { get; set; }
    public bool ActualTextVerified { get; set; }
    public bool NativeEnterClockVerified { get; set; }
}

internal sealed class PlainSubtitleClockSnapshot
{
    public string Phase { get; init; } = "";
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string NativeState { get; init; } = "Missing";
    public long NativePositionTicks { get; init; } = -1;
    public long NativeDurationTicks { get; init; } = -1;
    public uint VideoWidth { get; init; }
    public uint VideoHeight { get; init; }
    public bool Muted { get; init; }
    public int TimedMetadataTrackIndex { get; init; } = -1;
    public string PresentationMode { get; init; } = "Missing";
    public List<PlainSubtitleCueValue> DecodedCues { get; init; } = [];
    public List<PlainSubtitleCueValue> ActiveCues { get; init; } = [];
}

internal sealed class PlainSubtitleCueValue
{
    public string CueType { get; init; } = "";
    public long StartTicks { get; init; }
    public long DurationTicks { get; init; }
    public long EndTicks { get; init; }
    public int LineCount { get; init; }
    public string Text { get; init; } = "";
}

internal sealed class PlainSubtitleCueEvent
{
    public int Ordinal { get; init; }
    public string Phase { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public long NativePositionTicks { get; init; } = -1;
    public string NativeState { get; init; } = "Missing";
    public PlainSubtitleCueValue Cue { get; init; } = new();
}

internal sealed class PlainSubtitleNativeEvent
{
    public int Ordinal { get; init; }
    public string Phase { get; init; } = "";
    public string Kind { get; init; } = "";
    public DateTimeOffset CapturedUtc { get; init; } = DateTimeOffset.UtcNow;
    public long NativePositionTicks { get; init; } = -1;
    public string NativeState { get; init; } = "Missing";
    public string? ErrorCode { get; init; }
    public int? ErrorHResult { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PlainSubtitleTimingReport))]
internal sealed partial class PlainSubtitleTimingJsonContext : JsonSerializerContext;
