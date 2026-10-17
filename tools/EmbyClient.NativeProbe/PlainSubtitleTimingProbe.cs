using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using EmbyClient.Playback;
using FFmpegInteropX;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _plainSubtitleTimingMediaDirectory;
    private const string PlainSubtitleFirstText = "SRT timing cue one: source 2 to 8 seconds.";
    private const string PlainSubtitleSecondText = "SRT timing cue two: source 35 to 45 seconds.";

    private void ConfigurePlainSubtitleTimingMode(string mediaDir)
    {
        if (!Path.IsPathFullyQualified(mediaDir))
            throw new ArgumentException("The measured embedded SRT directory must be an explicit absolute path.");
        _plainSubtitleTimingMediaDirectory = Path.GetFullPath(mediaDir);
    }

    private async Task RunPlainSubtitleTimingAsync()
    {
        var report = new PlainSubtitleTimingReport();
        var reportPath = Path.Combine(Path.GetDirectoryName(_resultPath!)!, "plain-subtitle-timing-report.json");
        FileStream stream;
        try { stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (Exception error)
        {
            Console.Error.WriteLine("Plain subtitle timing output reservation failed: " + error.GetType().Name);
            Environment.ExitCode = 2;
            _window!.Close();
            Exit();
            return;
        }
        using var reportStream = stream;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        FFmpegMediaSource? decoder = null;
        MediaPlaybackItem? item = null;
        MediaSource? source = null;
        MediaPlayer? player = null;
        MediaPlaybackSession? nativeSession = null;
        PlainSubtitleTimingObservation? observation = null;
        void Save()
        {
            byte[] bytes;
            lock (report)
            {
                report.UpdatedUtc = DateTimeOffset.UtcNow;
                bytes = JsonSerializer.SerializeToUtf8Bytes(report, PlainSubtitleTimingJsonContext.Default.PlainSubtitleTimingReport);
            }
            reportStream.Position = 0;
            reportStream.Write(bytes);
            reportStream.SetLength(bytes.Length);
            reportStream.Flush(flushToDisk: true);
        }
        void SetStage(string stage)
        {
            report.Stage = stage;
            observation?.SetPhase(stage);
            Save();
        }
        void Cleanup(string operation, Action action)
        {
            try { action(); }
            catch (Exception error) { report.CleanupErrors.Add(operation + ":" + ErrorCode(error)); }
        }
        try
        {
            report.OutputReservedWithCreateNew = true;
            Save();
            Require(report.NativeAot, "NativeAotRequired");
            Require(_plainSubtitleTimingMediaDirectory is not null, "PlainSubtitleMediaDirectoryMissing");
            SetStage("BindMeasuredEmbeddedSrt");
            var mediaPath = await LoadPlainSubtitleTimingFixtureAsync(report, deadline.Token);

            SetStage("CreateFreshNativeSourceWithPositiveDelay");
            var config = new MediaSourceConfig();
            config.Video.VideoDecoderMode = VideoDecoderMode.ForceFFmpegSoftwareDecoder;
            config.General.FastSeek = false;
            config.General.KeepMetadataOnMediaSourceClosed = false;
            config.Subtitles.AutoSelectForcedSubtitles = false;
            config.Subtitles.DefaultSubtitleDelay = TimeSpan.FromTicks(report.RequestedSubtitleDelayTicks);
            config.Subtitles.MinimumSubtitleDuration = TimeSpan.Zero;
            config.Subtitles.AdditionalSubtitleDuration = TimeSpan.Zero;
            report.DefaultDelayConfiguredBeforeSourceCreation = config.Subtitles.DefaultSubtitleDelay.Ticks == report.RequestedSubtitleDelayTicks;
            // This fresh source receives no preroll seek and no dynamic delay adjustment.
            decoder = await FFmpegMediaSource.CreateFromFileAsync(mediaPath, config).AsTask(deadline.Token);
            report.DecoderDurationTicks = decoder.Duration.Ticks;
            report.ActualConfigurationSubtitleDelayTicks = decoder.Configuration.Subtitles.DefaultSubtitleDelay.Ticks;
            report.ActualConfigurationFastSeek = decoder.Configuration.General.FastSeek;
            report.ActualVideoDecoderMode = decoder.Configuration.Video.VideoDecoderMode.ToString();
            Require(report.DefaultDelayConfiguredBeforeSourceCreation
                && report.ActualConfigurationSubtitleDelayTicks == report.RequestedSubtitleDelayTicks
                && !report.ActualConfigurationFastSeek
                && decoder.Configuration.Video.VideoDecoderMode == VideoDecoderMode.ForceFFmpegSoftwareDecoder,
                "PlainSubtitleNativeConfigurationMismatch");
            Require(Math.Abs(report.DecoderDurationTicks - report.FixtureDurationTicks) <= TimeSpan.TicksPerSecond,
                "PlainSubtitleDecoderDurationMismatch");
            item = decoder.CreateMediaPlaybackItem();
            source = item.Source;
            report.NativePlaybackItemCreated = true;
            player = new MediaPlayer { AutoPlay = false, IsMuted = true, Volume = 1 };
            player.CommandManager.IsEnabled = false;
            nativeSession = player.PlaybackSession;
            decoder.PlaybackSession = nativeSession;
            report.PlaybackSessionBoundBeforeSourceAssignment = decoder.PlaybackSession == nativeSession;
            Require(report.PlaybackSessionBoundBeforeSourceAssignment, "PlainSubtitleNativeSessionBindingMissing");
            observation = new PlainSubtitleTimingObservation(report, player, nativeSession);
            observation.AttachPlayer();
            BindPlainSubtitleTimingTrack(decoder, observation, report);
            _element!.SetMediaPlayer(player);
            player.Source = item;
            await PlainSubtitleWaitAsync(() => observation.IsOpened, observation,
                "PlainSubtitleNativeMediaOpenedMissing", deadline.Token);
            await PlainSubtitleWaitAsync(() => observation.TryApplyApplicationPresentation(item), observation,
                "PlainSubtitleUniqueNativeTrackMissing", deadline.Token);
            report.OpenedAtZero = observation.Observe(item, "OpenedAtZero");
            report.NativeDurationTicks = report.OpenedAtZero.NativeDurationTicks;
            report.InitialMutePreserved = report.OpenedAtZero.Muted;
            report.FreshSourceStartedAtZeroWithoutSeek = report.OpenedAtZero.NativePositionTicks == 0
                && observation.SeekCompletedCount == 0 && report.OpenedAtZero.ActiveCues.Count == 0;
            Require(report.InitialMutePreserved && report.FreshSourceStartedAtZeroWithoutSeek
                && report.OpenedAtZero.PresentationMode == nameof(TimedMetadataTrackPresentationMode.ApplicationPresented)
                && Math.Abs(report.NativeDurationTicks - report.FixtureDurationTicks) <= TimeSpan.TicksPerSecond,
                "PlainSubtitleFreshNativeZeroRequired");
            await CapturePlainSubtitleLoadedRuntimeAsync(report, deadline.Token);

            SetStage("FreshPlayback");
            player.Play();
            await PlainSubtitleWaitAsync(() => nativeSession.PlaybackState == MediaPlaybackState.Playing
                && nativeSession.Position.Ticks >= 31_000_000, observation,
                "PlainSubtitleFreshClockDidNotAdvance", deadline.Token);
            report.BeforeFirstDelayedCue = observation.Observe(item, "BeforeFirstDelayedCue");
            report.NativeClockAdvancedBeforeFirstCue = report.BeforeFirstDelayedCue.NativePositionTicks is >= 31_000_000 and < 45_000_000
                && report.BeforeFirstDelayedCue.VideoWidth == 1280 && report.BeforeFirstDelayedCue.VideoHeight == 720;
            Require(report.NativeClockAdvancedBeforeFirstCue && report.BeforeFirstDelayedCue.ActiveCues.Count == 0
                && observation.FirstEntered(PlainSubtitleFirstText, "FreshPlayback") is null,
                "PlainSubtitleFirstCuePresentedBeforePositiveDelay");
            report.FreshDecode = await ObservePlainSubtitlePhaseAsync(observation, item, PlainSubtitleFirstText,
                "FreshPlayback", "FreshDecode", 20_000_000, 80_000_000, 50_000_000, 110_000_000,
                52_000_000, 65_000_000, report.MaximumCueEventClockErrorTicks, deadline.Token);
            player.Pause();
            await PlainSubtitleWaitAsync(() => nativeSession.PlaybackState == MediaPlaybackState.Paused,
                observation, "PlainSubtitleFirstPauseMissing", deadline.Token);
            await Task.Delay(150, deadline.Token);
            var firstPausedPosition = nativeSession.Position.Ticks;
            await Task.Delay(300, deadline.Token);
            report.FirstPauseDriftTicks = Math.Abs(nativeSession.Position.Ticks - firstPausedPosition);
            report.FreshDecode.PausedStable = observation.Observe(item, "FreshPausedStable");
            Require(report.FirstPauseDriftTicks <= report.MaximumPausedClockDriftTicks
                && report.FreshDecode.PausedStable.NativePositionTicks is >= 50_000_000 and < 70_000_000,
                "PlainSubtitleFirstPausedClockMismatch");
            RequirePlainSubtitleActiveCue(report.FreshDecode.PausedStable, PlainSubtitleFirstText, 50_000_000, 110_000_000);
            report.ActualVideoDecoderEngine = decoder.CurrentVideoStream.DecoderEngine.ToString();
            Require(decoder.CurrentVideoStream.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder,
                "PlainSubtitleSoftwareDecodeNotConfirmed");
            report.FreshDecodeTimingVerified = true;
            Save();

            SetStage("AccurateSeekFromPaused");
            report.SeekCompletedCountBeforeRequest = observation.SeekCompletedCount;
            nativeSession.Position = TimeSpan.FromTicks(report.SeekTargetTicks);
            await PlainSubtitleWaitAsync(() => observation.SeekCompletedCount > report.SeekCompletedCountBeforeRequest
                && nativeSession.PlaybackState == MediaPlaybackState.Paused
                && Math.Abs(nativeSession.Position.Ticks - report.SeekTargetTicks) <= report.MaximumSeekErrorTicks,
                observation, "PlainSubtitleAccurateNativeSeekDidNotComplete", deadline.Token, 20);
            report.SeekCompletedCountAfterRequest = observation.SeekCompletedCount;
            await Task.Delay(150, deadline.Token);
            report.AccurateSeekCompleted = observation.Observe(item, "AccurateSeekCompleted");
            report.NativeSeekCompletedVerified = report.SeekCompletedCountAfterRequest > report.SeekCompletedCountBeforeRequest
                && report.AccurateSeekCompleted.NativeState == nameof(MediaPlaybackState.Paused)
                && Math.Abs(report.AccurateSeekCompleted.NativePositionTicks - report.SeekTargetTicks) <= report.MaximumSeekErrorTicks;
            Require(report.NativeSeekCompletedVerified && report.AccurateSeekCompleted.ActiveCues.Count == 0,
                "PlainSubtitleNativeSeekOrCaptionClearMismatch");
            Save();

            SetStage("AfterSeekPlayback");
            player.Play();
            await PlainSubtitleWaitAsync(() => nativeSession.PlaybackState == MediaPlaybackState.Playing
                && nativeSession.Position.Ticks >= 361_000_000, observation,
                "PlainSubtitlePostSeekClockDidNotAdvance", deadline.Token);
            report.BeforeSecondDelayedCue = observation.Observe(item, "BeforeSecondDelayedCue");
            report.NativeClockAdvancedAfterSeek = report.BeforeSecondDelayedCue.NativePositionTicks is >= 361_000_000 and < 375_000_000
                && report.BeforeSecondDelayedCue.NativePositionTicks > report.AccurateSeekCompleted.NativePositionTicks + 20_000_000
                && report.BeforeSecondDelayedCue.VideoWidth == 1280 && report.BeforeSecondDelayedCue.VideoHeight == 720;
            Require(report.NativeClockAdvancedAfterSeek && report.BeforeSecondDelayedCue.ActiveCues.Count == 0
                && observation.FirstEntered(PlainSubtitleSecondText, "AfterSeekPlayback") is null,
                "PlainSubtitleSecondCuePresentedBeforePositiveDelay");
            report.AfterSeek = await ObservePlainSubtitlePhaseAsync(observation, item, PlainSubtitleSecondText,
                "AfterSeekPlayback", "AfterSeek", 350_000_000, 450_000_000, 380_000_000, 480_000_000,
                382_000_000, 390_000_000, report.MaximumCueEventClockErrorTicks, deadline.Token);
            player.Pause();
            await PlainSubtitleWaitAsync(() => nativeSession.PlaybackState == MediaPlaybackState.Paused,
                observation, "PlainSubtitlePostSeekPauseMissing", deadline.Token);
            await Task.Delay(150, deadline.Token);
            report.AfterSeek.PausedStable = observation.Observe(item, "AfterSeekPausedStable");
            Require(report.AfterSeek.PausedStable.NativePositionTicks is >= 380_000_000 and < 390_000_000,
                "PlainSubtitlePostSeekObservationOutside38To39Seconds");
            RequirePlainSubtitleActiveCue(report.AfterSeek.PausedStable, PlainSubtitleSecondText, 380_000_000, 480_000_000);
            observation.ThrowIfFailed();
            report.PostSeekTimingVerified = true;
            Require(player.IsMuted && !report.MediaFailedObserved && !report.TrackFailedObserved
                && report.DroppedCueEvents == 0 && report.DroppedNativeEvents == 0,
                "PlainSubtitleUnexpectedNativeFailure");
            report.Status = "TimingObservationsComplete";
        }
        catch (Exception error)
        {
            report.FailureStage = report.Stage;
            report.Status = "Failed";
            report.ErrorCode = ErrorCode(error);
            report.ErrorType = error.GetType().Name;
            report.ErrorHResult = error.HResult;
        }
        finally
        {
            report.Stage = "Cleanup";
            if (observation is not null)
            {
                try { await observation.ReleaseObserversAsync(); }
                catch (Exception error) { report.CleanupErrors.Add("ReleaseObservers:" + ErrorCode(error)); }
                report.ObserversRetired = observation.Retired;
                report.ObserverProjectionReferencesAtStop = observation.ProjectionReferenceCount;
                report.ObserverNativeLeasesAtStop = observation.NativeLeaseCount;
                report.ObserverActiveCapturesAtStop = observation.ActiveCaptureCount;
            }
            else
            {
                report.ObserversRetired = true;
                report.ObserverProjectionReferencesAtStop = 0;
                report.ObserverNativeLeasesAtStop = 0;
                report.ObserverActiveCapturesAtStop = 0;
            }
            observation = null;
            // This local was used only to inspect/control the real native clock; release it before stopping.
            nativeSession = null;
            report.ObserverReferencesReleasedBeforeStop = report.ObserversRetired
                && report.ObserverProjectionReferencesAtStop == 0 && report.ObserverNativeLeasesAtStop == 0
                && report.ObserverActiveCapturesAtStop == 0;
            if (!report.ObserverReferencesReleasedBeforeStop) report.CleanupErrors.Add("ObserverReferencesRemainAtStop");
            Cleanup("DetachElement", () =>
            {
                _element!.SetMediaPlayer(null);
                report.PlayerDetachedBeforeStop = _element.MediaPlayer is null;
                Require(report.PlayerDetachedBeforeStop, "PlainSubtitlePlayerDidNotDetach");
            });
            if (player is not null)
            {
                Cleanup("PausePlayer", () => { player.Pause(); report.PlayerPauseRequested = true; });
                Cleanup("ClearPlayerSource", () =>
                {
                    player.Source = null;
                    report.PlayerSourceCleared = player.Source is null;
                    Require(report.PlayerSourceCleared, "PlainSubtitlePlayerSourceNotCleared");
                });
            }
            if (decoder is not null)
            {
                Cleanup("UnbindDecoderPlaybackSession", () =>
                {
                    decoder.PlaybackSession = null;
                    report.DecoderPlaybackSessionCleared = decoder.PlaybackSession is null;
                    Require(report.DecoderPlaybackSessionCleared, "PlainSubtitleDecoderSessionNotCleared");
                });
            }
            report.StopCompleted = report.PlayerDetachedBeforeStop && report.PlayerPauseRequested && report.PlayerSourceCleared;
            item = null;
            if (source is not null)
                Cleanup("DisposeMediaSource", () => { source.Dispose(); report.MediaSourceDisposed = true; });
            source = null;
            if (decoder is not null)
                Cleanup("DisposeDecoder", () => { decoder.Dispose(); report.DecoderDisposed = true; });
            decoder = null;
            if (player is not null)
                Cleanup("DisposePlayer", () => { player.Dispose(); report.PlayerDisposed = true; });
            player = null;
            report.AllOwningReferencesReleased = item is null && source is null && decoder is null && player is null
                && nativeSession is null && observation is null;
            if (report.Status == "TimingObservationsComplete" && report.FreshDecodeTimingVerified && report.PostSeekTimingVerified
                && report.ObserverReferencesReleasedBeforeStop && report.StopCompleted && report.DecoderPlaybackSessionCleared
                && report.MediaSourceDisposed && report.DecoderDisposed && report.PlayerDisposed
                && report.AllOwningReferencesReleased && report.CleanupErrors.Count == 0)
                report.Status = "Passed";
            else
            {
                report.Status = "Failed";
                report.ErrorCode ??= "PlainSubtitleFinalCleanupOrTimingGateFailed";
            }
            report.Stage = "Complete";
            report.FinishedUtc = DateTimeOffset.UtcNow;
            report.RequestedProcessExitCode = report.Status == "Passed" ? 0 : 1;
            Environment.ExitCode = report.RequestedProcessExitCode.Value;
            try { Save(); }
            catch (Exception error)
            {
                Environment.ExitCode = 2;
                Console.Error.WriteLine("Plain subtitle timing final output failed: " + error.GetType().Name);
            }
            finally { _window!.Close(); Exit(); }
        }
    }

    private async Task<string> LoadPlainSubtitleTimingFixtureAsync(PlainSubtitleTimingReport report, CancellationToken ct)
    {
        var directory = _plainSubtitleTimingMediaDirectory!;
        var metadataPath = Path.Combine(directory, "fixture-h264-aac-srt.json");
        var mediaPath = Path.Combine(directory, "fixture-h264-aac-srt.mkv");
        var probePath = Path.Combine(directory, "fixture-h264-aac-srt.ffprobe.json");
        var packetsPath = Path.Combine(directory, "fixture-h264-aac-srt.subtitle-packets.json");
        var srtPath = Path.Combine(directory, "fixture-h264-aac-srt.srt");
        Require(new FileInfo(metadataPath).Length is > 0 and <= 64 * 1024
            && new FileInfo(probePath).Length is > 0 and <= 64 * 1024
            && new FileInfo(packetsPath).Length is > 0 and <= 64 * 1024
            && new FileInfo(srtPath).Length is > 0 and <= 4096
            && new FileInfo(mediaPath).Length is > 0 and <= 64 * 1024 * 1024,
            "PlainSubtitleFixtureSizeInvalid");
        var metadataBytes = await File.ReadAllBytesAsync(metadataPath, ct);
        var probeBytes = await File.ReadAllBytesAsync(probePath, ct);
        var packetBytes = await File.ReadAllBytesAsync(packetsPath, ct);
        var srtBytes = await File.ReadAllBytesAsync(srtPath, ct);
        using var metadata = JsonDocument.Parse(metadataBytes);
        using var probe = JsonDocument.Parse(probeBytes);
        using var packets = JsonDocument.Parse(packetBytes);
        var input = metadata.RootElement;
        Require(input.GetProperty("FormatVersion").GetInt32() == 1 && input.GetProperty("Synthetic").GetBoolean()
            && PlainSubtitleJsonText(input, "Container") == "mkv" && PlainSubtitleJsonText(input, "FileName") == "fixture-h264-aac-srt.mkv"
            && input.GetProperty("FileLength").GetInt64() == new FileInfo(mediaPath).Length
            && input.GetProperty("DurationTicks").GetInt64() is >= 990_000_000 and <= 1_010_000_000,
            "PlainSubtitleMeasuredSynthetic100SecondFixtureRequired");
        var embedded = input.GetProperty("EmbeddedSubtitle");
        Require(embedded.GetProperty("Index").GetInt32() == 2 && PlainSubtitleJsonText(embedded, "Codec") == "subrip"
            && PlainSubtitleJsonText(embedded, "DeliveryMethod") == "Embed" && !embedded.GetProperty("IsExternal").GetBoolean()
            && embedded.GetProperty("IsTextSubtitleStream").GetBoolean(), "PlainSubtitleEmbeddedOrdinarySrtRequired");
        var inputCues = input.GetProperty("SourceCues");
        Require(inputCues.GetArrayLength() == 2
            && inputCues[0].GetProperty("StartTicks").GetInt64() == 20_000_000 && inputCues[0].GetProperty("EndTicks").GetInt64() == 80_000_000
            && PlainSubtitleJsonText(inputCues[0], "Text") == PlainSubtitleFirstText
            && inputCues[1].GetProperty("StartTicks").GetInt64() == 350_000_000 && inputCues[1].GetProperty("EndTicks").GetInt64() == 450_000_000
            && PlainSubtitleJsonText(inputCues[1], "Text") == PlainSubtitleSecondText, "PlainSubtitleExactSourceCuesRequired");
        await using (var file = File.OpenRead(mediaPath))
            report.MediaSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        report.MetadataSha256 = Convert.ToHexStringLower(SHA256.HashData(metadataBytes));
        report.FfprobeSha256 = Convert.ToHexStringLower(SHA256.HashData(probeBytes));
        report.SubtitlePacketsSha256 = Convert.ToHexStringLower(SHA256.HashData(packetBytes));
        report.SubtitleSourceSha256 = Convert.ToHexStringLower(SHA256.HashData(srtBytes));
        Require(report.MediaSha256.Equals(PlainSubtitleJsonText(input, "FileSha256"), StringComparison.OrdinalIgnoreCase)
            && report.FfprobeSha256.Equals(PlainSubtitleJsonText(input, "ProbeSha256"), StringComparison.OrdinalIgnoreCase)
            && report.SubtitlePacketsSha256.Equals(PlainSubtitleJsonText(input, "SubtitlePacketsSha256"), StringComparison.OrdinalIgnoreCase)
            && report.SubtitleSourceSha256.Equals(PlainSubtitleJsonText(input, "SubtitleSourceSha256"), StringComparison.OrdinalIgnoreCase),
            "PlainSubtitleFixtureHashMismatch");
        var streams = probe.RootElement.GetProperty("streams");
        Require(streams.GetArrayLength() == 3 && streams[0].GetProperty("index").GetInt32() == 0
            && PlainSubtitleJsonText(streams[0], "codec_name") == "h264" && streams[1].GetProperty("index").GetInt32() == 1
            && PlainSubtitleJsonText(streams[1], "codec_name") == "aac" && streams[2].GetProperty("index").GetInt32() == 2
            && PlainSubtitleJsonText(streams[2], "codec_name") == "subrip" && PlainSubtitleJsonText(streams[2], "time_base") == "1/1000"
            && PlainSubtitleJsonText(probe.RootElement.GetProperty("format"), "start_time") == "0.000000",
            "PlainSubtitleMeasuredStreamMappingMismatch");
        var nativePackets = packets.RootElement.GetProperty("packets");
        Require(nativePackets.GetArrayLength() == 2
            && nativePackets[0].GetProperty("stream_index").GetInt32() == 2 && nativePackets[0].GetProperty("pts").GetInt64() == 2000
            && nativePackets[0].GetProperty("duration").GetInt64() == 6000
            && nativePackets[1].GetProperty("stream_index").GetInt32() == 2 && nativePackets[1].GetProperty("pts").GetInt64() == 35000
            && nativePackets[1].GetProperty("duration").GetInt64() == 10000,
            "PlainSubtitleExactNativeInputPacketsRequired");
        report.HashBoundFixtureVerified = true;
        report.ExactInputSubtitlePacketsVerified = true;
        report.FixtureDurationTicks = input.GetProperty("DurationTicks").GetInt64();
        report.SubtitleStreamIndex = 2;
        return mediaPath;
    }

    private static void BindPlainSubtitleTimingTrack(FFmpegMediaSource decoder, PlainSubtitleTimingObservation observation,
        PlainSubtitleTimingReport report)
    {
        var streams = decoder.SubtitleStreams;
        Require(streams.Count == 1, "PlainSubtitleSingleEmbeddedSrtTrackRequired");
        var selected = streams.SingleOrDefault(value => value.StreamIndex == 2);
        Require(selected is not null && !selected.IsExternal
            && selected.CodecName.Equals("subrip", StringComparison.OrdinalIgnoreCase)
            && selected.SubtitleTrack.TimedMetadataKind == TimedMetadataKind.Subtitle,
            "PlainSubtitleActualNativeSrtBindingMismatch");
        report.NativeSubtitleCodecName = selected!.CodecName;
        report.NativeTimedMetadataKind = selected.SubtitleTrack.TimedMetadataKind.ToString();
        report.ActualEmbeddedStreamDelayTicks = decoder.GetStreamDelay(selected).Ticks;
        Require(report.ActualEmbeddedStreamDelayTicks == report.RequestedSubtitleDelayTicks,
            "PlainSubtitleActualEmbeddedStreamDelayMismatch");
        observation.AttachTrack(selected.SubtitleTrack);
        report.NativeEmbeddedTextTrackVerified = true;
    }

    private static async Task CapturePlainSubtitleLoadedRuntimeAsync(PlainSubtitleTimingReport report, CancellationToken ct)
    {
        var runtimePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FFmpegInteropX.dll"));
        using (var process = Process.GetCurrentProcess())
            report.DecoderRuntimeLoadedFromProbeDirectory = process.Modules.Cast<ProcessModule>().Any(module =>
                string.Equals(Path.GetFullPath(module.FileName), runtimePath, StringComparison.OrdinalIgnoreCase));
        Require(report.DecoderRuntimeLoadedFromProbeDirectory, "PlainSubtitleLoadedRuntimeBindingMismatch");
        await using (var file = File.OpenRead(runtimePath))
            report.DecoderRuntimeSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
        Require(Environment.ProcessPath is not null, "PlainSubtitleActualProcessExecutableMissing");
        await using (var file = File.OpenRead(Environment.ProcessPath!))
            report.ProbeExecutableSha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }

    private static async Task<PlainSubtitleTimingPhase> ObservePlainSubtitlePhaseAsync(PlainSubtitleTimingObservation observation,
        MediaPlaybackItem item, string text, string eventPhase, string phaseName, long sourceStartTicks, long sourceEndTicks,
        long expectedStartTicks, long expectedEndTicks, long sampleAtTicks, long latestSampleTicks, long clockErrorTicks,
        CancellationToken ct)
    {
        var result = new PlainSubtitleTimingPhase
        {
            Name = phaseName, OriginalCueStartTicks = sourceStartTicks, OriginalCueEndTicks = sourceEndTicks,
            ExpectedCueStartTicks = expectedStartTicks, ExpectedCueEndTicks = expectedEndTicks
        };
        await PlainSubtitleWaitAsync(() =>
        {
            var snapshot = observation.Observe(item, phaseName);
            if (snapshot.NativePositionTicks >= latestSampleTicks)
                throw new PlaybackException("PlainSubtitleDelayedCueMissingInRequiredWindow_" + phaseName);
            var entered = observation.FirstEntered(text, eventPhase);
            if (entered is not null)
            {
                Require(PlainSubtitleCueMatches(entered.Cue, text, expectedStartTicks, expectedEndTicks),
                    "PlainSubtitleEnteredCueDelayAppliedMoreThanOnce_" + phaseName);
                Require(entered.NativeState == nameof(MediaPlaybackState.Playing)
                    && Math.Abs(entered.NativePositionTicks - expectedStartTicks) <= clockErrorTicks,
                    "PlainSubtitleCueEnteredNativeClockMismatch_" + phaseName);
            }
            if (snapshot.NativePositionTicks < sampleAtTicks || entered is null || snapshot.ActiveCues.Count == 0) return false;
            Require(snapshot.NativeState == nameof(MediaPlaybackState.Playing), "PlainSubtitleCueObservedOutsidePlaying_" + phaseName);
            RequirePlainSubtitleActiveCue(snapshot, text, expectedStartTicks, expectedEndTicks);
            result.Entered = entered;
            result.AtCue = snapshot;
            result.ExactCueStartAndDurationVerified = true;
            result.ActualTextVerified = true;
            result.NativeEnterClockVerified = true;
            return true;
        }, observation, "PlainSubtitleRealTimedTextCueMissing_" + phaseName, ct);
        return result;
    }

    private static bool PlainSubtitleCueMatches(PlainSubtitleCueValue value, string text, long startTicks, long endTicks) =>
        value.CueType == nameof(TimedTextCue) && value.StartTicks == startTicks && value.EndTicks == endTicks
        && value.DurationTicks == endTicks - startTicks && value.LineCount == 1 && value.Text == text;

    private static void RequirePlainSubtitleActiveCue(PlainSubtitleClockSnapshot snapshot, string text, long startTicks, long endTicks)
    {
        Require(snapshot.PresentationMode == nameof(TimedMetadataTrackPresentationMode.ApplicationPresented)
            && snapshot.ActiveCues.Count == 1 && PlainSubtitleCueMatches(snapshot.ActiveCues[0], text, startTicks, endTicks),
            "PlainSubtitleActiveCueStartDurationOrTextMismatch");
        Require(snapshot.DecodedCues.Any(value => PlainSubtitleCueMatches(value, text, startTicks, endTicks)),
            "PlainSubtitleDecodedCueMetadataMismatch");
    }

    private static async Task PlainSubtitleWaitAsync(Func<bool> condition, PlainSubtitleTimingObservation observation,
        string failure, CancellationToken ct, int timeoutSeconds = 12)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            observation.ThrowIfFailed();
            if (condition()) return;
            if (watch.Elapsed > TimeSpan.FromSeconds(timeoutSeconds)) throw new PlaybackException(failure);
            await Task.Delay(40, ct);
        }
    }

    private static string PlainSubtitleJsonText(JsonElement value, string name) => value.GetProperty(name).GetString() ?? "";
}

internal sealed class PlainSubtitleTimingObservation
{
    private const int MaximumNativeEvents = 256;
    private const int MaximumCueEvents = 64;
    private const int MaximumTrackCues = 16;
    private readonly PlainSubtitleTimingReport _report;
    private MediaPlayer? _player;
    private MediaPlaybackSession? _session;
    private TimedMetadataTrack? _track;
    private TypedEventHandler<MediaPlayer, object>? _opened;
    private TypedEventHandler<MediaPlayer, object>? _ended;
    private TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? _failed;
    private TypedEventHandler<MediaPlaybackSession, object>? _stateChanged;
    private TypedEventHandler<MediaPlaybackSession, object>? _seekCompleted;
    private TypedEventHandler<TimedMetadataTrack, MediaCueEventArgs>? _cueEntered;
    private TypedEventHandler<TimedMetadataTrack, MediaCueEventArgs>? _cueExited;
    private TypedEventHandler<TimedMetadataTrack, TimedMetadataTrackFailedEventArgs>? _trackFailed;
    private string _phase = "OpenFreshNativeSource";
    private bool _retired;
    private bool _isOpened;
    private int _seekCompletedCount;
    private int _activeCaptures;

    internal PlainSubtitleTimingObservation(PlainSubtitleTimingReport report, MediaPlayer player, MediaPlaybackSession session)
    {
        _report = report;
        _player = player;
        _session = session;
        _opened = (_, _) => RecordNative("MediaOpened", () => _isOpened = true);
        _ended = (_, _) => RecordNative("MediaEnded", () => _report.FirstObserverFailureCode ??= "PlainSubtitleUnexpectedMediaEnded");
        _failed = (_, args) => RecordNative("MediaFailed", () =>
        {
            _report.MediaFailedObserved = true;
            _report.FirstObserverFailureCode ??= "PlainSubtitleNativeMediaFailed";
        }, args);
        _stateChanged = (_, _) => RecordNative("PlaybackStateChanged");
        _seekCompleted = (_, _) => RecordNative("SeekCompleted", () => _seekCompletedCount++);
    }

    internal void AttachPlayer()
    {
        // The outer owner retains this observer before any subscription can fail partway through.
        if (_player is null || _session is null) throw new PlaybackException("PlainSubtitleObserverPlayerMissing");
        _player.MediaOpened += _opened;
        _player.MediaEnded += _ended;
        _player.MediaFailed += _failed;
        _session.PlaybackStateChanged += _stateChanged;
        _session.SeekCompleted += _seekCompleted;
    }

    internal bool IsOpened { get { lock (_report) return _isOpened; } }
    internal int SeekCompletedCount { get { lock (_report) return _seekCompletedCount; } }
    internal bool Retired { get { lock (_report) return _retired; } }
    internal int ActiveCaptureCount { get { lock (_report) return _activeCaptures; } }
    internal int NativeLeaseCount => 0;
    internal int ProjectionReferenceCount
    {
        get { lock (_report) return (_player is null ? 0 : 1) + (_session is null ? 0 : 1) + (_track is null ? 0 : 1); }
    }

    internal void SetPhase(string phase) { lock (_report) _phase = phase; }

    internal void AttachTrack(TimedMetadataTrack track)
    {
        if (_track is not null) throw new PlaybackException("PlainSubtitleObserverTrackAlreadyAttached");
        _track = track;
        _cueEntered = (_, args) => RecordCue("Entered", args);
        _cueExited = (_, args) => RecordCue("Exited", args);
        _trackFailed = (_, _) => RecordNative("TrackFailed", () =>
        {
            _report.TrackFailedObserved = true;
            _report.FirstObserverFailureCode ??= "PlainSubtitleNativeTrackFailed";
        });
        track.CueEntered += _cueEntered;
        track.CueExited += _cueExited;
        track.TrackFailed += _trackFailed;
    }

    internal bool TryApplyApplicationPresentation(MediaPlaybackItem item)
    {
        TimedMetadataTrack? selected;
        lock (_report) selected = _track;
        if (selected is null) throw new PlaybackException("PlainSubtitleObserverTrackMissing");
        var tracks = item.TimedMetadataTracks;
        var matches = Enumerable.Range(0, tracks.Count).Where(index => tracks[index] == selected).ToArray();
        if (matches.Length == 0) return false;
        if (matches.Length != 1) throw new PlaybackException("PlainSubtitleNativeTrackIdentityAmbiguous");
        var index = checked((uint)matches[0]);
        if (tracks.GetPresentationMode(index) != TimedMetadataTrackPresentationMode.ApplicationPresented)
            tracks.SetPresentationMode(index, TimedMetadataTrackPresentationMode.ApplicationPresented);
        return tracks.GetPresentationMode(index) == TimedMetadataTrackPresentationMode.ApplicationPresented;
    }

    internal PlainSubtitleClockSnapshot Observe(MediaPlaybackItem item, string phase)
    {
        MediaPlaybackSession? session;
        MediaPlayer? player;
        TimedMetadataTrack? track;
        lock (_report)
        {
            if (_retired || _session is null || _player is null || _track is null)
                throw new PlaybackException("PlainSubtitleObserverAlreadyReleased");
            _activeCaptures++;
            session = _session;
            player = _player;
            track = _track;
        }
        try
        {
            var tracks = item.TimedMetadataTracks;
            var matches = Enumerable.Range(0, tracks.Count).Where(index => tracks[index] == track).ToArray();
            if (matches.Length != 1) throw new PlaybackException("PlainSubtitleNativeTrackIdentityMismatch");
            return new PlainSubtitleClockSnapshot
            {
                Phase = phase, NativeState = session.PlaybackState.ToString(), NativePositionTicks = session.Position.Ticks,
                NativeDurationTicks = session.NaturalDuration.Ticks, VideoWidth = session.NaturalVideoWidth,
                VideoHeight = session.NaturalVideoHeight, Muted = player.IsMuted, TimedMetadataTrackIndex = matches[0],
                PresentationMode = tracks.GetPresentationMode(checked((uint)matches[0])).ToString(),
                DecodedCues = ReadCues(track.Cues), ActiveCues = ReadCues(track.ActiveCues)
            };
        }
        finally
        {
            session = null; player = null; track = null;
            lock (_report) _activeCaptures--;
        }
    }

    internal PlainSubtitleCueEvent? FirstEntered(string text, string phase)
    {
        lock (_report) return _report.CueEvents.FirstOrDefault(value => value.Kind == "Entered" && value.Phase == phase && value.Cue.Text == text);
    }

    internal void ThrowIfFailed()
    {
        lock (_report)
            if (_report.FirstObserverFailureCode is { } code) throw new PlaybackException(code);
    }

    private void RecordNative(string kind, Action? update = null, MediaPlayerFailedEventArgs? failureArgs = null)
    {
        MediaPlaybackSession? session;
        string phase;
        lock (_report)
        {
            if (_retired) return;
            _activeCaptures++;
            session = _session;
            phase = _phase;
        }
        try
        {
            var position = session?.Position.Ticks ?? -1;
            var state = session?.PlaybackState.ToString() ?? "Released";
            var errorCode = failureArgs?.Error.ToString();
            var errorHResult = failureArgs?.ExtendedErrorCode?.HResult;
            lock (_report)
            {
                update?.Invoke();
                if (_report.NativeEvents.Count >= MaximumNativeEvents)
                {
                    _report.DroppedNativeEvents++;
                    _report.FirstObserverFailureCode ??= "PlainSubtitleNativeEventBudgetExceeded";
                    return;
                }
                _report.NativeEvents.Add(new PlainSubtitleNativeEvent
                {
                    Ordinal = _report.NativeEvents.Count + 1, Phase = phase, Kind = kind,
                    NativePositionTicks = position, NativeState = state,
                    ErrorCode = errorCode, ErrorHResult = errorHResult
                });
            }
        }
        catch (Exception error) { lock (_report) _report.FirstObserverFailureCode ??= "PlainSubtitleNativeObserver_" + FailureCode(error); }
        finally { session = null; failureArgs = null; lock (_report) _activeCaptures--; }
    }

    private void RecordCue(string kind, MediaCueEventArgs args)
    {
        MediaPlaybackSession? session;
        string phase;
        lock (_report)
        {
            if (_retired) return;
            _activeCaptures++;
            session = _session;
            phase = _phase;
        }
        try
        {
            // Copy the native clock synchronously with the event, before inspecting cue text.
            var position = session?.Position.Ticks ?? -1;
            var state = session?.PlaybackState.ToString() ?? "Released";
            var cue = ReadCue(args.Cue);
            lock (_report)
            {
                if (_report.CueEvents.Count >= MaximumCueEvents)
                {
                    _report.DroppedCueEvents++;
                    _report.FirstObserverFailureCode ??= "PlainSubtitleCueEventBudgetExceeded";
                    return;
                }
                _report.CueEvents.Add(new PlainSubtitleCueEvent
                {
                    Ordinal = _report.CueEvents.Count + 1, Phase = phase, Kind = kind,
                    NativePositionTicks = position, NativeState = state, Cue = cue
                });
            }
        }
        catch (Exception error) { lock (_report) _report.FirstObserverFailureCode ??= "PlainSubtitleCueObserver_" + FailureCode(error); }
        finally { session = null; lock (_report) _activeCaptures--; }
    }

    private static List<PlainSubtitleCueValue> ReadCues(IReadOnlyList<IMediaCue> cues)
    {
        if (cues.Count > MaximumTrackCues) throw new PlaybackException("PlainSubtitleTrackCueBudgetExceeded");
        var result = new List<PlainSubtitleCueValue>(cues.Count);
        foreach (var cue in cues) result.Add(ReadCue(cue));
        return result;
    }

    // IMediaCue resolves its concrete RCW by runtime class name; retain both NativeAOT factories.
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextCue))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields, typeof(TimedTextLine))]
    private static PlainSubtitleCueValue ReadCue(IMediaCue cue)
    {
        if (cue is not TimedTextCue text) throw new PlaybackException("PlainSubtitleActualCueIsNotTimedText");
        if (text.Lines.Count is < 1 or > 4) throw new PlaybackException("PlainSubtitleTextLineBudgetExceeded");
        var content = string.Join("\n", text.Lines.Select(line => line.Text)).Trim();
        if (content.Length is < 1 or > 256) throw new PlaybackException("PlainSubtitleTextBudgetExceeded");
        var start = text.StartTime.Ticks;
        var duration = text.Duration.Ticks;
        return new PlainSubtitleCueValue
        {
            CueType = nameof(TimedTextCue), StartTicks = start, DurationTicks = duration,
            EndTicks = checked(start + duration), LineCount = text.Lines.Count, Text = content
        };
    }

    internal async Task ReleaseObserversAsync()
    {
        // Retire subscriptions first, then drain every capture before releasing the native projections.
        lock (_report) _retired = true;
        void Unsubscribe(string operation, Action action)
        {
            try { action(); }
            catch (Exception error) { _report.CleanupErrors.Add(operation + ":" + FailureCode(error)); }
        }
        if (_track is not null)
        {
            if (_cueEntered is not null) Unsubscribe("RemoveCueEntered", () => _track.CueEntered -= _cueEntered);
            if (_cueExited is not null) Unsubscribe("RemoveCueExited", () => _track.CueExited -= _cueExited);
            if (_trackFailed is not null) Unsubscribe("RemoveTrackFailed", () => _track.TrackFailed -= _trackFailed);
        }
        if (_player is not null)
        {
            if (_opened is not null) Unsubscribe("RemoveMediaOpened", () => _player.MediaOpened -= _opened);
            if (_ended is not null) Unsubscribe("RemoveMediaEnded", () => _player.MediaEnded -= _ended);
            if (_failed is not null) Unsubscribe("RemoveMediaFailed", () => _player.MediaFailed -= _failed);
        }
        if (_session is not null)
        {
            if (_stateChanged is not null) Unsubscribe("RemovePlaybackStateChanged", () => _session.PlaybackStateChanged -= _stateChanged);
            if (_seekCompleted is not null) Unsubscribe("RemoveSeekCompleted", () => _session.SeekCompleted -= _seekCompleted);
        }
        var watch = Stopwatch.StartNew();
        while (ActiveCaptureCount != 0)
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new PlaybackException("PlainSubtitleObserverCapturesDidNotDrain");
            await Task.Delay(10);
        }
        lock (_report)
        {
            _cueEntered = null; _cueExited = null; _trackFailed = null; _opened = null; _ended = null; _failed = null;
            _stateChanged = null; _seekCompleted = null; _track = null; _session = null; _player = null;
        }
    }

    private static string FailureCode(Exception error) => error is PlaybackException playback ? playback.ErrorCode : error.GetType().Name;
}
