using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _nativeAudioOutputMediaDirectory;

    private void ConfigureNativeAudioOutputMode(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The generated native audio fixture directory must be an explicit absolute path.");
        _nativeAudioOutputMediaDirectory = Path.GetFullPath(directory);
    }

    private async Task RunNativeAudioOutputAsync()
    {
        var report = new NativeAudioOutputProbeReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        try
        {
            SaveNativeAudioOutput(report);
            Require(report.NativeAot, "NativeAotRequired");
            report.NativeAudioApiAvailable = NativePlaybackEngine.AudioPassthroughAvailable;
            Require(report.NativeAudioApiAvailable, "NativeAudioApiUnavailable");
            var root = _nativeAudioOutputMediaDirectory!;
            Require(Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0,
                "NativeAudioFixtureDirectoryInvalid");
            var manifest = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(Path.Combine(root, "native-audio-fixtures.json"), deadline.Token),
                NativeAudioOutputProbeJsonContext.Default.NativeAudioFixtureManifest);
            Require(manifest is { Synthetic: true, QaOnly: true, Fixtures.Length: 3 }
                && manifest.OriginalFileSha256.Equals("cff6e01f40ca1478eadba80d4570645e979ad6ee7bd32b326fb5152982a37459", StringComparison.OrdinalIgnoreCase),
                "ExplicitQaAudioManifestRequired");
            var fixtures = manifest!.Fixtures;
            Require(fixtures.Select(value => value.AudioCodec).Order().SequenceEqual(new[] { "aac", "ac3", "eac3" }),
                "ExpectedQaAudioCodecsRequired");
            foreach (var configuration in new[] { ("ac3", AudioPassthroughMode.Off), ("ac3", AudioPassthroughMode.All),
                ("eac3", AudioPassthroughMode.All), ("aac", AudioPassthroughMode.All) })
            {
                var fixture = fixtures.Single(value => value.AudioCodec == configuration.Item1);
                report.Stage = configuration.Item1.ToUpperInvariant() + "_" + configuration.Item2;
                var round = new NativeAudioOutputProbeCase
                {
                    Name = report.Stage, RequestedMode = configuration.Item2, Fixture = fixture
                };
                report.Cases.Add(round);
                SaveNativeAudioOutput(report);
                await RunNativeAudioOutputCaseAsync(report, round, root, deadline.Token);
            }
            report.Stage = "Complete";
            report.Status = "Passed";
        }
        catch (Exception exception)
        {
            report.Status = "Failed";
            report.ErrorCode = ErrorCode(exception);
            report.ErrorHResult = exception.HResult;
        }
        finally
        {
            report.FinishedAt = DateTimeOffset.UtcNow;
            SaveNativeAudioOutput(report);
            Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
            _window!.Close();
            Exit();
        }
    }

    private async Task RunNativeAudioOutputCaseAsync(NativeAudioOutputProbeReport report,
        NativeAudioOutputProbeCase round, string root, CancellationToken ct)
    {
        SyntheticHlsEndpoint? upstream = null;
        NativePlaybackEngine? engine = null;
        PlaybackCoordinator? coordinator = null;
        NativeAudioPlaybackApiHandler? apiReceipts = null;
        HttpClient? http = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? sampleTimer = null;
        var observer = new NativeAudioOutputProbeObserver();
        var cleanupOwners = new List<Func<bool>>();
        try
        {
            upstream = SyntheticHlsEndpoint.CreateNativeAudioMp4(root, round.Fixture);
            apiReceipts = new NativeAudioPlaybackApiHandler(upstream.ManifestUri, round.Fixture);
            http = new HttpClient(apiReceipts) { Timeout = TimeSpan.FromSeconds(10) };
            var api = new EmbyApiClient(http, new Uri(upstream.ManifestUri.GetLeftPart(UriPartial.Authority) + "/emby/"),
                new ClientIdentity("SYNTHETIC Native Audio Output", "Windows native probe", "synthetic-native-audio", "0.1.0"))
                .WithAuthentication(SyntheticHlsEndpoint.SyntheticHeaderValue, "synthetic-audio-user");
            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialVolumeLevel: 100, initialMuted: false)
            {
                NativeAudioOutputProbeObserver = observer
            };
            engine.Diagnostic += (_, value) =>
            {
                lock (report.Diagnostics)
                {
                    if (report.Diagnostics.Count < 128) report.Diagnostics.Add(round.Name + ":Native:" + value.Operation + ":" + value.ErrorCode);
                }
            };
            coordinator = new PlaybackCoordinator(api, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromMilliseconds(500), ReportTimeout = TimeSpan.FromSeconds(5),
                CleanupTimeout = TimeSpan.FromSeconds(10)
            });
            coordinator.Diagnostic += (_, value) =>
            {
                lock (report.Diagnostics)
                {
                    if (report.Diagnostics.Count < 128) report.Diagnostics.Add(round.Name + ":Coordinator:" + value.Operation + ":" + value.ErrorCode);
                }
            };
            sampleTimer = _window.DispatcherQueue.CreateTimer();
            sampleTimer.Interval = TimeSpan.FromMilliseconds(200);
            sampleTimer.Tick += (_, _) => CaptureNativeAudioOutput(round, engine, report.Stage);
            sampleTimer.Start();
            await coordinator.PlayAsync(new PlaybackSelection
            {
                ItemId = NativeAudioPlaybackApiHandler.ItemId, MediaSourceId = NativeAudioPlaybackApiHandler.SourceId,
                SubtitleStreamIndex = -1, NativeAudioPassthroughEligible = engine.NativeAudioPassthroughEligible,
                AudioPassthroughMode = round.RequestedMode
            }, ct);
            var original = coordinator.ActiveContext ?? throw new ProbeFailure("NativeAudioLogicalContextMissing");
            round.PlaybackId = original.PlaybackId;
            Require(original.DeliveryMethod == PlaybackDeliveryMethod.DirectStream && original.TimelineOffsetTicks == 0,
                "OriginalStaticNativeAudioSourceRequired");
            cleanupOwners.Add(engine.RetainNativeAudioCleanupObservationForProbe());
            await WaitAsync(() => engine.Snapshot is { State: PlaybackEngineState.Playing, PositionTicks: > 3_000_000 }
                && _element!.MediaPlayer?.PlaybackSession.NaturalVideoWidth > 0, "NativeAudioClockDidNotAdvance", ct);
            round.PlayingPositionTicks = engine.Snapshot!.PositionTicks;
            CaptureNativeAudioOutput(round, engine, "Playing");
            var nativeBeforeControls = engine.CaptureNativeAudioStateForProbe();
            round.NativeSourceCodecObserved = NormalizeNativeAudioProbeCodec(nativeBeforeControls.NativeAudioCodec);
            Require(round.NativeSourceCodecObserved == round.Fixture.AudioCodec
                && nativeBeforeControls.NativeAudioStreamIndex == round.Fixture.AudioStreamIndex,
                "MeasuredFixtureAndActualNativeAudioStreamMustMatch");
            var output = engine.AudioOutput ?? throw new ProbeFailure("NativeAudioOutputObservationMissing");
            var playbackItem = _element!.MediaPlayer!.Source as Windows.Media.Playback.MediaPlaybackItem
                ?? throw new ProbeFailure("NativeAudioPlaybackItemMissing");
            round.NativePcmAudioTrackCount = playbackItem.AudioTracks.Count;
            if (round.RequestedMode == AudioPassthroughMode.Off)
            {
                Require(output.State == AudioOutputState.Pcm && !output.IsBitstream && !engine.CaptureNativeAudioStateForProbe().ExclusiveOutputOpened,
                    "OffPolicyMustRetainPcm");
                round.Outcome = "PcmPolicyOff";
            }
            else if (round.Fixture.AudioCodec == "aac")
            {
                Require(output.State == AudioOutputState.Pcm && output.FallbackReason == "CodecUnsupported" && !output.IsBitstream,
                    "UnsupportedAacCodecMustUsePcm");
                round.Outcome = "UnsupportedCodecPcmFallback";
            }
            else
            {
                var attempts = observer.Assignments.Select(value => value.Native).Concat(round.Samples.Select(value => value.Native));
                round.ActualNativeFormatAttemptObserved = attempts.Any(value => value.HasDecoderSource
                    && value.RawOutput is { RequestedMode: AudioPassthroughMode.All } raw
                    && raw.StreamIndex == round.Fixture.AudioStreamIndex);
                Require(round.ActualNativeFormatAttemptObserved, "NativeAudioFormatAttemptWasNotObserved");
                Require(output.IsBitstream || output.State == AudioOutputState.Pcm && !string.IsNullOrEmpty(output.FallbackReason),
                    "NativeAudioAttemptMustResolveToCarrierOrExplicitPcmFallback");
                round.Outcome = NativeAudioProbeOutcome(round, observer, output);
            }
            Require(output.IsBitstream || round.NativePcmAudioTrackCount > 0, "DecodedPcmAudioDescriptorMissing");
            Require(output.PlaybackId == round.PlaybackId, "AudioOutputLogicalPlaybackIdMismatch");
            var checkNativeControls = nativeBeforeControls.ExclusiveOutputOpened
                && nativeBeforeControls.RawOutput is { State: AudioOutputState.Bitstream, ActualFormatConfirmed: true, SubmittedFrameCount: > 0 };
            if (checkNativeControls)
            {
                round.CarrierGenerationBeforeControls = nativeBeforeControls.RawOutput!.Generation;
                round.NativeCarrierControlOutcome = "Running";
            }
            report.Stage = round.Name + "_PauseSeekResume";
            await coordinator.PauseAsync(round.PlaybackId, ct);
            await WaitAsync(() => engine.Snapshot?.State == PlaybackEngineState.Paused, "NativeAudioPauseFailed", ct);
            await Task.Delay(150, ct);
            round.PauseStartTicks = engine.Snapshot!.PositionTicks;
            if (checkNativeControls)
                round.PauseSubmittedFramesBefore = engine.CaptureNativeAudioStateForProbe().RawOutput?.SubmittedFrameCount;
            await Task.Delay(350, ct);
            round.PauseEndTicks = engine.Snapshot!.PositionTicks;
            Require(Math.Abs(round.PauseEndTicks - round.PauseStartTicks) <= 500_000, "NativeAudioPausedClockAdvanced");
            CaptureNativeAudioOutput(round, engine, "Paused");
            if (checkNativeControls)
            {
                var pausedNative = engine.CaptureNativeAudioStateForProbe();
                if (SameNativeAudioProbeOwner(round, pausedNative))
                {
                    round.PauseSubmittedFramesAfter = pausedNative.RawOutput!.SubmittedFrameCount;
                    round.NativePauseSubmissionStable = round.PauseSubmittedFramesBefore == round.PauseSubmittedFramesAfter;
                    Require(round.NativePauseSubmissionStable == true, "NativeAudioCarrierSubmittedWhilePaused");
                }
                else
                {
                    Require(engine.AudioOutput is { State: AudioOutputState.Pcm, FallbackReason: not null },
                        "NativeAudioOwnerLostDuringPauseWithoutPcmRecovery");
                    round.NativeCarrierControlOutcome = "RecoveredToPcmDuringPause_NativeControlsNotVerified";
                    checkNativeControls = false;
                }
            }
            await coordinator.SeekAsync(round.PlaybackId, round.SeekTargetTicks, ct);
            await WaitAsync(() => engine.Snapshot is { State: PlaybackEngineState.Paused } paused
                && Math.Abs(paused.PositionTicks - round.SeekTargetTicks) <= 7_500_000, "NativeAudioPausedSeekMismatch", ct);
            round.SeekObservedTicks = engine.Snapshot!.PositionTicks;
            CaptureNativeAudioOutput(round, engine, "PausedSeek");
            if (checkNativeControls)
            {
                var seekNative = engine.CaptureNativeAudioStateForProbe();
                round.NativeSeekRetainedOwner = SameNativeAudioProbeOwner(round, seekNative);
                if (round.NativeSeekRetainedOwner == true) round.SeekSubmittedFrames = seekNative.RawOutput!.SubmittedFrameCount;
                else
                {
                    Require(engine.AudioOutput is { State: AudioOutputState.Pcm, FallbackReason: not null },
                        "NativeAudioOwnerLostDuringSeekWithoutPcmRecovery");
                    round.NativeCarrierControlOutcome = "RecoveredToPcmDuringSeek_NativeControlsNotVerified";
                    checkNativeControls = false;
                }
            }
            await coordinator.ResumeAsync(round.PlaybackId, ct);
            await WaitAsync(() => engine.Snapshot is { State: PlaybackEngineState.Playing } resumed
                && resumed.PositionTicks > round.SeekObservedTicks + 2_000_000, "NativeAudioResumeFailed", ct);
            round.ResumePositionTicks = engine.Snapshot!.PositionTicks;
            CaptureNativeAudioOutput(round, engine, "Resumed");
            if (checkNativeControls)
            {
                await WaitAsync(() => engine.AudioOutput is { State: AudioOutputState.Pcm, FallbackReason: not null }
                    || engine.CaptureNativeAudioStateForProbe().RawOutput is
                        { State: AudioOutputState.Bitstream, ActualFormatConfirmed: true } nativeCandidate
                        && nativeCandidate.Generation == round.CarrierGenerationBeforeControls
                        && nativeCandidate.SubmittedFrameCount > round.SeekSubmittedFrames.GetValueOrDefault(),
                    "NativeAudioCarrierDidNotResumeSubmitting", ct);
                var resumedNative = engine.CaptureNativeAudioStateForProbe();
                if (SameNativeAudioProbeOwner(round, resumedNative)
                    && resumedNative.RawOutput is { State: AudioOutputState.Bitstream, ActualFormatConfirmed: true } raw)
                {
                    round.ResumeSubmittedFrames = raw.SubmittedFrameCount;
                    round.NativeResumeSubmissionGrowth = round.ResumeSubmittedFrames > round.SeekSubmittedFrames;
                    Require(round.NativeResumeSubmissionGrowth == true, "NativeAudioCarrierResumeSubmissionDidNotGrow");
                    round.NativeCarrierControlOutcome = "Passed_PauseSubmissionStable_SeekOwnerRetained_ResumeSubmissionGrew";
                }
                else
                {
                    Require(engine.AudioOutput is { State: AudioOutputState.Pcm, FallbackReason: not null },
                        "NativeAudioOwnerLostAfterResumeWithoutPcmRecovery");
                    round.NativeCarrierControlOutcome = "RecoveredToPcmDuringResume_NativeControlsNotVerified";
                }
                CaptureNativeAudioOutput(round, engine, "NativeCarrierControlResult");
            }
            if (observer.Assignments.Any(value => value.Native.ExclusiveOutputOpened))
                await WaitAsync(() => engine.AudioOutput?.IsBitstream == true
                    || engine.AudioOutput is { State: AudioOutputState.Pcm, FallbackReason: not null },
                    "NativeAudioOutputDidNotSettleAfterResume", ct);
            if (engine.AudioOutput?.IsBitstream == true)
            {
                report.Stage = round.Name + "_SoftwareVolumePcmReplacement";
                var assignmentsBefore = observer.Assignments.Length;
                var negotiationsBefore = apiReceipts.Negotiations;
                var startsBefore = apiReceipts.Receipts.Count(value => value.Operation == "Start");
                await coordinator.SetVolumeAsync(75, isMuted: false, ct);
                await WaitAsync(() => engine.Snapshot is { State: PlaybackEngineState.Playing, VolumeLevel: 75 }
                    && engine.AudioOutput is { State: AudioOutputState.Pcm, FallbackReason: "SoftwareVolumeRequired" },
                    "NativeAudioSoftwareVolumeDidNotRecoverPcm", ct);
                round.PcmReplacementKeptLogicalPlaybackId = coordinator.ActiveContext?.PlaybackId == round.PlaybackId
                    && engine.Snapshot?.PlaybackId == round.PlaybackId;
                round.PcmReplacementKeptSyntheticServerSession = coordinator.ActiveContext?.PlaySessionId == original.PlaySessionId
                    && apiReceipts.Negotiations == negotiationsBefore
                    && apiReceipts.Receipts.Count(value => value.Operation == "Start") == startsBefore;
                Require(round.PcmReplacementKeptLogicalPlaybackId == true && round.PcmReplacementKeptSyntheticServerSession == true
                    && observer.Assignments.Length == assignmentsBefore + 1, "PcmReplacementCreatedAnotherLogicalSession");
                cleanupOwners.Add(engine.RetainNativeAudioCleanupObservationForProbe());
                round.PcmReplacementAudioTrackCount = (_element!.MediaPlayer?.Source as Windows.Media.Playback.MediaPlaybackItem)?.AudioTracks.Count;
                Require(round.PcmReplacementAudioTrackCount is > 0, "ReplacementPcmAudioDescriptorMissing");
                CaptureNativeAudioOutput(round, engine, "SoftwareVolumePcmReplacement");
                round.PcmReplacementControl = "PassedWithOneLocalBackendReplacement";
            }
            else round.PcmReplacementControl = "NotExercised_NoActiveBitstreamOnThisEndpoint";
            if (round.RequestedMode == AudioPassthroughMode.All && round.Fixture.AudioCodec != "aac")
                round.Outcome = NativeAudioProbeOutcome(round, observer, engine.AudioOutput!);
            report.Stage = round.Name + "_StopCleanup";
            cleanupOwners.Add(engine.RetainNativeAudioCleanupObservationForProbe());
            await coordinator.StopAsync(round.PlaybackId, ct);
            sampleTimer.Stop();
            CaptureNativeAudioOutput(round, engine, "Stopped");
            round.NativeCleanupSucceeded = cleanupOwners.All(value => value());
            round.PlayerDetachedAfterStop = _element!.MediaPlayer is null && engine.Snapshot?.State == PlaybackEngineState.Stopped;
            Require(round.NativeCleanupSucceeded && round.PlayerDetachedAfterStop, "NativeAudioStopCleanupFailed");
            await WaitAsync(() => upstream.ActiveRequests == 0, "NativeAudioUpstreamRequestsDidNotDrain", ct);
            var stoppedRequests = upstream.RequestCount;
            await Task.Delay(350, ct);
            round.NoUpstreamRequestsAfterStop = upstream.RequestCount == stoppedRequests && upstream.ActiveRequests == 0;
            Require(round.NoUpstreamRequestsAfterStop, "NativeAudioRequestsContinuedAfterStop");
            Require(upstream.Requests() is { Length: > 0 } traffic
                && traffic.All(value => value.Authenticated && value.Completed && value.Resource == "Mp4" && value.StatusCode is 200 or 206),
                "AuthenticatedNativeAudioTrafficEvidenceMissing");
            round.SyntheticNegotiations = apiReceipts.Negotiations;
            round.SyntheticApiReceipts = apiReceipts.Receipts;
            round.SyntheticStartReports = round.SyntheticApiReceipts.Count(value => value.Operation == "Start");
            round.SyntheticStopReports = round.SyntheticApiReceipts.Count(value => value.Operation == "Stop");
            round.DistinctSyntheticServerSessions = round.SyntheticApiReceipts.Select(value => value.SessionHash).Distinct().Count();
            Require(round.SyntheticNegotiations == 1 && round.SyntheticStartReports == 1 && round.SyntheticStopReports == 1
                && round.DistinctSyntheticServerSessions == 1, "UnexpectedSyntheticServerPlaybackSessionCount");
            round.Status = "Passed";
        }
        catch (Exception exception)
        {
            round.Status = "Failed";
            round.ErrorCode = ErrorCode(exception);
            round.ErrorHResult = exception.HResult;
            throw;
        }
        finally
        {
            sampleTimer?.Stop();
            round.BackendAssignments = observer.Assignments;
            round.NativeBackendAssignments = round.BackendAssignments.Length;
            round.DistinctNativePlaybackIds = round.BackendAssignments.Select(value => value.PlaybackId).Distinct().Count();
            round.ExclusiveCleanupExercised = round.BackendAssignments.Any(value => value.Native.ExclusiveOutputOpened);
            if (apiReceipts is not null) round.SyntheticApiReceipts = apiReceipts.Receipts;
            if (upstream is not null) round.MediaRequests = upstream.Requests();
            Exception? cleanupFailure = null;
            try
            {
                if (coordinator is not null) await coordinator.DisposeAsync();
                else if (engine is not null) await engine.DisposeAsync();
                round.EngineDisposed = engine is not null;
            }
            catch (Exception exception) { cleanupFailure = exception; }
            try
            {
                if (upstream is not null) { await upstream.DisposeAsync(); round.ListenerClosed = true; }
            }
            catch (Exception exception) { cleanupFailure ??= exception; }
            finally { http?.Dispose(); }
            if (apiReceipts is not null) round.SyntheticApiReceipts = apiReceipts.Receipts;
            if (upstream is not null) round.MediaRequests = upstream.Requests();
            if (cleanupFailure is not null)
            {
                round.Status = "Failed";
                round.ErrorCode ??= "FinalCleanup_" + ErrorCode(cleanupFailure);
                round.ErrorHResult ??= cleanupFailure.HResult;
                SaveNativeAudioOutput(report);
                throw new PlaybackException("NativeAudioProbeFinalCleanupFailed");
            }
            SaveNativeAudioOutput(report);
        }
    }

    private static void CaptureNativeAudioOutput(NativeAudioOutputProbeCase round, NativePlaybackEngine engine, string stage)
    {
        if (round.Samples.Count >= 128) round.Samples.RemoveAt(0);
        var sample = new NativeAudioOutputProbeSample
        {
            Stage = stage, Engine = engine.Snapshot, ManagedOutput = engine.AudioOutput,
            Native = engine.CaptureNativeAudioStateForProbe()
        };
        round.Samples.Add(sample);
        if (sample.ManagedOutput?.IsBitstream == true
            && sample.Native.RawOutput is { State: AudioOutputState.Bitstream, ActualFormatConfirmed: true, SubmittedFrameCount: > 0 })
        {
            // Evidence remains monotonic even when the bounded rolling sample list drops old observations.
            round.NativeCarrierSubmissionObserved = true;
            round.FirstCarrierSubmissionSample ??= sample;
        }
    }

    private static string? NormalizeNativeAudioProbeCodec(string? codec) => codec?.ToLowerInvariant() switch
    {
        "ac3" or "ac3_fixed" or "ac3_float" => "ac3", "eac3" => "eac3", "aac" => "aac", _ => null
    };

    private static bool SameNativeAudioProbeOwner(NativeAudioOutputProbeCase round, NativeAudioProbeState state) =>
        state.ExclusiveOutputOpened && state.RawOutput is { RequestedMode: AudioPassthroughMode.All } raw
        && raw.Generation == round.CarrierGenerationBeforeControls && raw.StreamIndex == round.Fixture.AudioStreamIndex;

    private static string NativeAudioProbeOutcome(NativeAudioOutputProbeCase round,
        NativeAudioOutputProbeObserver observer, AudioOutputSnapshot output)
    {
        if (output.IsBitstream) return "RealNativeCarrierActive_PhysicalReceiverUnverified";
        if (output.State != AudioOutputState.Pcm) return "NativeCarrierOutputPendingOrUnavailable_PhysicalReceiverUnverified";
        if (round.NativeCarrierSubmissionObserved)
            return output.FallbackReason == "SoftwareVolumeRequired" ? "RealNativeCarrierSubmitted_SoftwareVolumeRecoveredToPcm"
                : "RealNativeCarrierRuntimeFailureRecoveredToPcm";
        return observer.Assignments.Any(value => value.Native.ExclusiveOutputOpened)
            ? "NativeExclusiveOpenedWithoutCarrierRecoveredToPcm" : "NativeOutputOpenRejected_PcmFallback";
    }

    private void SaveNativeAudioOutput(NativeAudioOutputProbeReport report)
    {
        report.UpdatedAt = DateTimeOffset.UtcNow;
        lock (report.Diagnostics)
        {
            var temporary = _resultPath! + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report,
                NativeAudioOutputProbeJsonContext.Default.NativeAudioOutputProbeReport));
            File.Move(temporary, _resultPath!, overwrite: true);
        }
    }
}

internal sealed partial class SyntheticHlsEndpoint
{
    internal static SyntheticHlsEndpoint CreateNativeAudioMp4(string directory, NativeAudioFixture fixture)
    {
        var root = Path.GetFullPath(directory);
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || fixture.FileName is not ("fixture-h264-ac3.mp4" or "fixture-h264-eac3.mp4" or "fixture-h264-aac.mp4")
            || fixture.FileName != "fixture-h264-" + fixture.AudioCodec + ".mp4"
            || fixture.AudioCodec == "aac" && !fixture.Sha256.Equals("cff6e01f40ca1478eadba80d4570645e979ad6ee7bd32b326fb5152982a37459", StringComparison.OrdinalIgnoreCase)
            || fixture.Container != "mp4" || fixture.VideoCodec != "h264"
            || fixture.DurationTicks is < 590_000_000 or > 610_000_000
            || fixture.AudioStreamIndex < 0 || fixture.AudioChannels != 2 || fixture.AudioSampleRate != 48000)
            throw new PlaybackException("MeasuredSyntheticAudioFixtureRequired");
        var media = ReadFixtureFile(root, fixture.FileName, 64 * 1024 * 1024);
        var input = InputFile(fixture.FileName, media);
        if (media.Length < 1024 || !media.AsSpan(4, 4).SequenceEqual("ftyp"u8)
            || input.Length != fixture.FileLength || !input.Sha256.Equals(fixture.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PlaybackException("SyntheticAudioFixtureHashMismatch");
        return new SyntheticHlsEndpoint(media, input, fixture.DurationTicks);
    }

    private SyntheticHlsEndpoint(byte[] media, HlsInputFile input, long durationTicks)
    {
        const string resource = "/emby/Videos/synthetic-audio-item/stream.mp4?Static=true";
        _files.Add(resource, media);
        InputFiles = [input];
        DurationTicks = durationTicks;
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(4);
        ManifestUri = new Uri(FormattableString.Invariant($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{resource}"));
        _accepting = AcceptAsync();
    }
}

/// <summary>In-process QA negotiation and report receipts; every media byte still comes from the owned real loopback origin.</summary>
internal sealed partial class NativeAudioPlaybackApiHandler(Uri mediaUri, NativeAudioFixture fixture) : HttpMessageHandler
{
    internal const string ItemId = "synthetic-audio-item";
    internal const string SourceId = "synthetic-audio-source";
    private readonly object _gate = new();
    private readonly List<NativeAudioApiReceipt> _receipts = [];
    private string? _sessionId;
    private int _negotiations;
    internal int Negotiations => Volatile.Read(ref _negotiations);
    internal NativeAudioApiReceipt[] Receipts { get { lock (_gate) return [.. _receipts]; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new PlaybackException("SyntheticAudioApiUriMissing");
        if (uri.Scheme != mediaUri.Scheme || uri.Host != mediaUri.Host || uri.Port != mediaUri.Port || !uri.IsLoopback)
            throw new PlaybackException("OwnedSyntheticAudioApiOriginRequired");
        if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/emby/Items/" + ItemId + "/PlaybackInfo")
        {
            Interlocked.Increment(ref _negotiations);
            _sessionId = "synthetic-audio-session-" + Guid.NewGuid().ToString("N");
            var response = new PlaybackInfoResponse
            {
                PlaySessionId = _sessionId,
                MediaSources = [new MediaSourceInfo
                {
                    Id = SourceId, Container = fixture.Container, Size = fixture.FileLength, RunTimeTicks = fixture.DurationTicks,
                    SupportsDirectStream = true, SupportsDirectPlay = false, SupportsTranscoding = false,
                    DirectStreamUrl = mediaUri.AbsoluteUri, Protocol = "http", IsInfiniteStream = false,
                    DefaultAudioStreamIndex = fixture.AudioStreamIndex, DefaultSubtitleStreamIndex = -1,
                    MediaStreams = [new MediaStream
                    {
                        Index = fixture.VideoStreamIndex, Type = "Video", Codec = fixture.VideoCodec,
                        Width = fixture.VideoWidth, Height = fixture.VideoHeight, VideoRange = "SDR"
                    }, new MediaStream
                    {
                        Index = fixture.AudioStreamIndex, Type = "Audio", Codec = fixture.AudioCodec,
                        Profile = fixture.AudioProfile, Channels = fixture.AudioChannels, SampleRate = fixture.AudioSampleRate
                    }]
                }]
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(response, EmbyJsonContext.Default.PlaybackInfoResponse), Encoding.UTF8, "application/json")
            };
        }
        var operation = uri.AbsolutePath switch
        {
            "/emby/Sessions/Playing" => "Start", "/emby/Sessions/Playing/Progress" => "Progress",
            "/emby/Sessions/Playing/Stopped" => "Stop", _ => null
        };
        if (request.Method != HttpMethod.Post || operation is null || request.Content is null)
            throw new PlaybackException("UnexpectedSyntheticAudioApiOperation");
        using var document = JsonDocument.Parse(await request.Content.ReadAsByteArrayAsync(ct));
        var body = document.RootElement;
        var session = body.TryGetProperty("PlaySessionId", out var sessionValue) ? sessionValue.GetString() : null;
        if (session is null || session != _sessionId) throw new PlaybackException("SyntheticAudioReportSessionMismatch");
        var receipt = new NativeAudioApiReceipt
        {
            Operation = operation, SessionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session))),
            PositionTicks = body.TryGetProperty("PositionTicks", out var position) && position.TryGetInt64(out var ticks) ? ticks : null,
            EventName = body.TryGetProperty("EventName", out var eventName) ? eventName.GetString() : null,
            IsPaused = body.TryGetProperty("IsPaused", out var paused) && paused.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? paused.GetBoolean() : null,
            StatusCode = 204
        };
        lock (_gate)
        {
            if (_receipts.Count >= 512) throw new PlaybackException("SyntheticAudioReportLimitExceeded");
            _receipts.Add(receipt);
        }
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }
}
