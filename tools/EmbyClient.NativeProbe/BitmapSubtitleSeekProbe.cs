using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private bool _bitmapSubtitleSeekMode;
    private string _bitmapSubtitleSeekCase = "original";
    private int _bitmapSubtitleSeekDelaySeconds;

    private void ConfigureBitmapSubtitleSeekCase(string caseName, string delaySeconds)
    {
        if (caseName is not ("original" or "sparse-long" or "long-cue")
            || !int.TryParse(delaySeconds, System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture, out var delay) || delay is not (-10 or 0 or 3))
            throw new ArgumentException("Select an owned bitmap seek case and its supported subtitle delay: -10, 0, or 3 seconds.");
        _bitmapSubtitleSeekCase = caseName;
        _bitmapSubtitleSeekDelaySeconds = delay;
    }

    private async Task RunBitmapSubtitleSeekAsync()
    {
        var report = new BitmapSubtitleSeekReport
        {
            CaseName = _bitmapSubtitleSeekCase,
            SubtitleDelayTicks = TimeSpan.FromSeconds(_bitmapSubtitleSeekDelaySeconds).Ticks
        };
        var collector = new BitmapSubtitleSelectionReport();
        report.FixtureObservations = collector.FixtureObservations;
        var reportPath = Path.Combine(Path.GetDirectoryName(_resultPath!)!, "bitmap-subtitle-seek-report.json");
        FileStream stream;
        try { stream = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read); }
        catch (Exception error)
        {
            Console.Error.WriteLine("Bitmap seek output reservation failed: " + error.GetType().Name);
            Environment.ExitCode = 2; _window!.Close(); Exit(); return;
        }
        using var reportStream = stream;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var observation = new BitmapSubtitleHttpObservationHandler();
        using var http = new HttpClient(observation) { Timeout = Timeout.InfiniteTimeSpan };
        NativePlaybackEngine? engine = null;
        PlaybackCoordinator? coordinator = null;
        EmbyApiClient? authenticated = null;
        BitmapSubtitleIdentityLease? identity = null;
        EventHandler<NativeAssSubtitleFrameChangedEventArgs>? frameObserver = null;
        EventHandler<PlaybackDiagnosticEventArgs>? diagnosticObserver = null;
        var playbackId = Guid.Empty;
        string? serverSession = null;
        string? mediaHash = null;
        string? probeHash = null;
        void Save()
        {
            report.ApiAttempts = observation.Events;
            byte[] bytes;
            lock (report.Diagnostics)
                bytes = JsonSerializer.SerializeToUtf8Bytes(report, BitmapSubtitleSeekJsonContext.Default.BitmapSubtitleSeekReport);
            reportStream.Position = 0; reportStream.Write(bytes); reportStream.SetLength(bytes.Length); reportStream.Flush(true);
        }
        bool SameLogical() => coordinator?.ActiveContext is { } active && active.PlaybackId == playbackId
            && active.PlaySessionId == serverSession;
        async Task<BitmapFixtureObservation> Stats(string phase, CancellationToken ct) =>
            await ReadBitmapFixtureStatsAsync(http, observation, collector, phase, mediaHash!, probeHash!, ct);
        async Task Seek(string name, long target, long? cueTimestamp)
        {
            report.Stage = name;
            var step = new BitmapSubtitleSeekStep
            {
                Name = name, TargetTicks = target, ExpectedImage = cueTimestamp is not null,
                ExpectedCueSourceTimestampTicks = cueTimestamp, ImageEventsBefore = report.ImageEvents, ClearEventsBefore = report.ClearEvents
            };
            report.Steps.Add(step); Save();
            var seekTimer = Stopwatch.StartNew();
            await coordinator!.SeekAsync(target, deadline.Token);
            step.SeekMilliseconds = seekTimer.Elapsed.TotalMilliseconds;
            var timer = Stopwatch.StartNew();
            while (true)
            {
                step.Native = await engine!.ObserveBitmapSelectionForProbeAsync(playbackId, identity);
                step.Tracks = await engine.ObserveBitmapSeekTracksForProbeAsync(playbackId, deadline.Token);
                report.TrackSnapshotRetries = engine.BitmapSeekTrackSnapshotRetries;
                step.CorrectPausedClock = step.Native.NativeState == "Paused" && Math.Abs(step.Native.PositionTicks - target) <= 500_000;
                step.NativeSourceAndSelectionPreserved = step.Native.ActivePlaybackMatches && step.Native.ActualNativeDecoderConfigured
                    && step.Native.DefaultPlayerOwnerPresent && step.Native.FullSourceDirectStream && !step.Native.HasExternalSubtitle
                    && step.Native.RequestedSubtitleStreamIndex == 2 && step.Native.RetainedNativeSubtitleStreamIndex == 2
                    && step.Native.RetainedNativeSubtitleCodec == "pgssub" && step.Native.SubtitlePresentationActive
                    && step.Native.HasPresentationTimer && step.Native.HasPresentationTrack;
                step.ExpectedCompositionObserved = cueTimestamp is { } cue
                    ? BitmapSeekFixtureCompositionMatches(step.Native, cue)
                    : !step.Native.HasImage && !step.Native.IndependentlyAcquiredBitmap;
                step.ImageEventsAfter = report.ImageEvents; step.ClearEventsAfter = report.ClearEvents;
                step.ProductFrameEventObserved = cueTimestamp is null
                    ? step.ClearEventsAfter > step.ClearEventsBefore : step.ImageEventsAfter > step.ImageEventsBefore;
                if (step.CorrectPausedClock && step.NativeSourceAndSelectionPreserved
                    && step.ExpectedCompositionObserved && step.ProductFrameEventObserved) break;
                if (timer.Elapsed >= TimeSpan.FromSeconds(3) || engine.Snapshot?.State == PlaybackEngineState.Failed) break;
                await Task.Delay(50, deadline.Token);
            }
            await Stats(name, deadline.Token);
            var passed = step.CorrectPausedClock && step.NativeSourceAndSelectionPreserved
                && step.ExpectedCompositionObserved && step.ProductFrameEventObserved
                && SameLogical() && BitmapAllIdentitiesMatch(step.Native!);
            step.ObservationMilliseconds = seekTimer.Elapsed.TotalMilliseconds;
            step.Status = passed ? "Passed" : "Failed";
            Save();
            if (!passed) throw new PlaybackException("BitmapSeekPausedCompositionMissing");
        }
        try
        {
            Save(); Require(report.NativeAot, "NativeAotRequired");
            report.Stage = "BindOriginalMeasuredPgs";
            var mediaPath = Path.Combine(_bitmapSubtitleMediaDirectory!, "fixture-h264-aac-pgs.mkv");
            var metadataPath = Path.Combine(_bitmapSubtitleMediaDirectory!, "fixture-h264-aac.json");
            var probePath = Path.Combine(_bitmapSubtitleMediaDirectory!, "fixture-h264-aac.ffprobe.json");
            Require(new FileInfo(mediaPath).Length is > 0 and <= 64 * 1024 * 1024
                && new FileInfo(metadataPath).Length is > 0 and <= 64 * 1024 && new FileInfo(probePath).Length is > 0 and <= 64 * 1024,
                "BitmapSeekFixtureSizeInvalid");
            using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(metadataPath, deadline.Token));
            await using (var file = File.OpenRead(mediaPath)) mediaHash = BitmapHash(await SHA256.HashDataAsync(file, deadline.Token));
            probeHash = BitmapHash(SHA256.HashData(await File.ReadAllBytesAsync(probePath, deadline.Token)));
            var expectedHash = _bitmapSubtitleSeekCase switch
            {
                "sparse-long" => "d4ec6fa7bdbd8906cf172197328359ab77b86cb86936b5d26f2df5933b134b07",
                "long-cue" => "885ee296c41aa6d935c738d9b88a93179c4227807760cb1426a05205f2aa1e30",
                _ => "bc908d917a6a8728fd840b9eb3cc7a1a7c7de54f6e5232a73811fbe84672c3c5"
            };
            Require(mediaHash == expectedHash
                && mediaHash.Equals(BitmapText(metadata.RootElement, "FileSha256"), StringComparison.OrdinalIgnoreCase)
                && probeHash.Equals(BitmapText(metadata.RootElement, "ProbeSha256"), StringComparison.OrdinalIgnoreCase)
                && metadata.RootElement.GetProperty("Synthetic").GetBoolean(), "BitmapSeekOriginalFixtureBindingRequired");
            report.MediaSha256 = mediaHash; report.FfprobeSha256 = probeHash;
            var pristine = await Stats("Pristine", deadline.Token);
            Require(pristine.LoginCount == 0 && pristine.PlaybackInfoCount == 0 && pristine.StartCount == 0
                && pristine.StopCount == 0 && pristine.Events.Length == 0 && pristine.ApiAttempts.Length == 0,
                "BitmapSeekExclusiveFreshFixtureRequired");
            var api = new EmbyApiClient(http, _bitmapSubtitleFixtureRoot!, new ClientIdentity(
                "SYNTHETIC Native PGS Seek Probe", "Windows native probe", "synthetic-bitmap-seek-" + Guid.NewGuid().ToString("N"), "0.1.0"));
            var publicInfo = await api.GetPublicSystemInfoAsync(deadline.Token);
            Require(publicInfo.Id == "synthetic-server-0001" && publicInfo.Version == "synthetic-1.0", "BitmapSeekSyntheticServerRequired");
            var authentication = await api.AuthenticateByNameAsync("demo", "demo", deadline.Token);
            Require(authentication.AccessToken is not null && authentication.User?.Id == "synthetic-user-demo"
                && authentication.ServerId == publicInfo.Id, "BitmapSeekSyntheticAuthenticationFailed");
            authenticated = api.WithAuthentication(authentication.AccessToken!, authentication.User!.Id!);
            var item = await authenticated.GetItemAsync("1001", deadline.Token);
            var source = item.MediaSources?.SingleOrDefault();
            Require(source is { Container: "mkv", SupportsDirectStream: true, SupportsTranscoding: false }
                && source.MediaStreams.SingleOrDefault(value => value.Index == 2) is
                    { Type: "Subtitle", Codec: "pgssub", DeliveryMethod: "Embed", IsExternal: false, IsTextSubtitleStream: false },
                "BitmapSeekActualApiBindingMismatch");
            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true);
            Require(engine.NativeEmbeddedSubtitlesEligible && NativeAssInterop.IsBitmapSelectionAvailable, "BitmapSeekNativeApiRequired");
            await engine.ConfigureNextPlaybackPreferencesAsync(100, true, 1, 0, _bitmapSubtitleSeekDelaySeconds, deadline.Token);
            frameObserver = (_, frame) => { if (frame.HasImage) report.ImageEvents++; else report.ClearEvents++; };
            diagnosticObserver = (_, value) =>
            {
                lock (report.Diagnostics)
                {
                    if (report.Diagnostics.Count < 1000) report.Diagnostics.Add(BitmapSafeCode(value.Operation) + ":" + BitmapSafeCode(value.ErrorCode));
                    if (value.ErrorCode == "NativeReleaseFailed") report.CleanupErrors.Add("NativeRelease:" + BitmapSafeCode(value.Operation));
                }
            };
            engine.SubtitleFrameChanged += frameObserver; engine.Diagnostic += diagnosticObserver;
            coordinator = new PlaybackCoordinator(authenticated, engine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(10), CleanupTimeout = TimeSpan.FromSeconds(10)
            });
            report.Stage = "DecodeFirstCueFromZero"; Save();
            await coordinator.PlayAsync(new PlaybackSelection
            {
                ItemId = "1001", MediaSourceId = source!.Id, SubtitleStreamIndex = 2,
                NativeEmbeddedSubtitlesEligible = engine.NativeEmbeddedSubtitlesEligible
            }, deadline.Token);
            var active = coordinator.ActiveContext ?? throw new PlaybackException("BitmapSeekContextMissing");
            Require(active.DeliveryMethod == PlaybackDeliveryMethod.DirectStream, "BitmapSeekOriginalFullSourceRequired");
            playbackId = active.PlaybackId; serverSession = active.PlaySessionId;
            report.PlaybackIdHash = BitmapStringHash(playbackId.ToString("N")); report.PlaySessionIdHash = BitmapStringHash(serverSession);
            var initialImageExpected = _bitmapSubtitleSeekDelaySeconds >= 0 || _bitmapSubtitleSeekCase == "long-cue";
            await WaitForBitmapSampleAsync(engine, playbackId, null, value => initialImageExpected
                ? BitmapSeekFixtureCompositionMatches(value, 20_000_000)
                : value.NativeState == "Playing" && value.PositionTicks > 2_000_000 && value.VideoWidth == 1280 && value.VideoHeight == 720,
                "BitmapSeekInitialNativeEvidenceMissing", deadline.Token);
            await coordinator.PauseAsync(deadline.Token);
            await WaitForBitmapSampleAsync(engine, playbackId, null, value => value.NativeState == "Paused"
                && (!initialImageExpected || BitmapSeekFixtureCompositionMatches(value, 20_000_000)), "BitmapSeekInitialPauseMissing", deadline.Token);
            await Task.Delay(250, deadline.Token);
            identity = await engine.CaptureBitmapIdentityForProbeAsync(playbackId);
            report.InitialNative = await engine.ObserveBitmapSelectionForProbeAsync(playbackId, identity);
            if (initialImageExpected) report.FirstDecodedCue = report.InitialNative;
            var decoderPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "FFmpegInteropX.dll"));
            using (var process = Process.GetCurrentProcess())
                report.DecoderRuntimeLoadedFromProbeDirectory = process.Modules.Cast<ProcessModule>().Any(module =>
                    string.Equals(Path.GetFullPath(module.FileName), decoderPath, StringComparison.OrdinalIgnoreCase));
            Require(report.DecoderRuntimeLoadedFromProbeDirectory, "BitmapSeekLoadedRuntimeBindingMismatch");
            await using (var file = File.OpenRead(decoderPath)) report.DecoderRuntimeSha256 = BitmapHash(await SHA256.HashDataAsync(file, deadline.Token));
            if (_bitmapSubtitleSeekCase == "long-cue")
            {
                if (_bitmapSubtitleSeekDelaySeconds < 0)
                {
                    report.ExpectedStepCount = 5;
                    await Seek("PausedNegativeCrossZero1", 10_000_000, 20_000_000);
                    await Seek("PausedNegativeLongCue75", 750_000_000, 20_000_000);
                    await Seek("PausedNegativeLongExpired85", 850_000_000, null);
                    await Seek("PausedNegativeLongBackward75", 750_000_000, 20_000_000);
                    await Seek("PausedNegativeCrossZeroAgain1", 10_000_000, 20_000_000);
                }
                else
                {
                    report.ExpectedStepCount = 4;
                    await Seek("PausedLongCue85", 850_000_000, 20_000_000);
                    await Seek("PausedLongExpired95", 950_000_000, null);
                    await Seek("PausedLongBackward85", 850_000_000, 20_000_000);
                    await Seek("PausedLongBeforeFirst1", 10_000_000, null);
                }
            }
            else if (_bitmapSubtitleSeekDelaySeconds != 0)
            {
                report.ExpectedStepCount = _bitmapSubtitleSeekCase == "sparse-long" ? 6 : 5;
                if (_bitmapSubtitleSeekDelaySeconds < 0)
                {
                    await Seek("PausedNegativeUnread30", 300_000_000, 350_000_000);
                    await Seek("PausedNegativeBeforeSecond20", 200_000_000, null);
                    await Seek("PausedNegativeAfterClear40", 400_000_000, null);
                    await Seek("PausedNegativeBackward30", 300_000_000, 350_000_000);
                    await Seek("PausedNegativeFirstExpired1", 10_000_000, null);
                }
                else
                {
                    await Seek("PausedPositiveUnread40", 400_000_000, 350_000_000);
                    await Seek("PausedPositiveBeforeSecond36", 360_000_000, null);
                    await Seek("PausedPositiveAfterClear50", 500_000_000, null);
                    await Seek("PausedPositiveFirst6", 60_000_000, 20_000_000);
                    await Seek("PausedPositiveBeforeFirst1", 10_000_000, null);
                }
                if (_bitmapSubtitleSeekCase == "sparse-long") await Seek("PausedSignedLongSilence85", 850_000_000, null);
            }
            else
            {
                report.ExpectedStepCount = _bitmapSubtitleSeekCase == "sparse-long" ? 8 : 7;
                await Seek("PausedGap17", 170_000_000, null);
                report.UnknownCuePreconditionApplicable = true;
                report.BeforeUnknownForwardSeek = await engine.ObserveBitmapSeekTracksForProbeAsync(playbackId, deadline.Token);
                report.TargetCueUnknownBeforeForwardSeek = report.BeforeUnknownForwardSeek.ActivePlaybackMatches
                    && report.BeforeUnknownForwardSeek.Tracks.Count(track => track.Origin == "SelectedNativeSubtitle" && track.Kind == "ImageSubtitle") == 1
                    && report.BeforeUnknownForwardSeek.Tracks.Count(track => track.Origin == "PendingNativeReferenceTrack" && track.Kind == "Custom") == 1
                    && report.BeforeUnknownForwardSeek.Tracks.Length == 2
                    && !report.BeforeUnknownForwardSeek.Tracks.SelectMany(track => track.Cues).Any(cue => cue.StartTicks == 350_000_000);
                Require(report.TargetCueUnknownBeforeForwardSeek, "BitmapSeekUnknownCuePreconditionMissing");
                await Seek("PausedUnknownForward40", 400_000_000, 350_000_000);
                await Seek("PausedExpired48", 480_000_000, null);
                await Seek("PausedKnownBackward38", 380_000_000, 350_000_000);
                await Seek("PausedBackwardFirst4", 40_000_000, 20_000_000);
                await Seek("PausedEmptyGap25", 250_000_000, null);
                await Seek("PausedBeforeFirst1", 10_000_000, null);
                if (_bitmapSubtitleSeekCase == "sparse-long") await Seek("PausedLongSilence85", 850_000_000, null);
            }
            report.NativeIdentitiesPreserved = report.Steps.All(step => BitmapAllIdentitiesMatch(step.Native!));
            report.LogicalAndServerSessionsPreserved = SameLogical();
            report.NoNegotiationStartOrStopDuringSeeks = report.FixtureObservations.Where(value => value.Phase != "Pristine")
                .All(value => BitmapSelectionApiUnchanged(value, report.PlaySessionIdHash!));
            Require(report.NativeIdentitiesPreserved && report.LogicalAndServerSessionsPreserved
                && report.NoNegotiationStartOrStopDuringSeeks, "BitmapSeekSourceOrServerSessionChanged");
            report.Status = "ObservationsPassed";
        }
        catch (Exception error)
        {
            report.Status = "Failed"; report.ErrorCode = BitmapSafeCode(ErrorCode(error));
            report.ErrorType = error.GetType().Name; report.ErrorHResult = error.HResult;
            if (engine is not null && coordinator is not null && report.Steps.LastOrDefault() is
                { Status: "Failed", ExpectedCueSourceTimestampTicks: { } expectedCue } && SameLogical())
            {
                try
                {
                    report.DiagnosticResumeAttempted = true; Save();
                    using var diagnosticDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await coordinator.ResumeAsync(diagnosticDeadline.Token);
                    var timer = Stopwatch.StartNew();
                    do
                    {
                        report.DiagnosticResumedSample = await engine.ObserveBitmapSelectionForProbeAsync(playbackId, identity);
                        report.DiagnosticResumedTracks = await engine.ObserveBitmapSeekTracksForProbeAsync(playbackId, diagnosticDeadline.Token);
                        report.DiagnosticResumeRevealedExpectedCue = BitmapHasActualPixels(report.DiagnosticResumedSample)
                            && report.DiagnosticResumedSample.CueSourceTimestampTicks == expectedCue;
                        if (report.DiagnosticResumeRevealedExpectedCue) break;
                        await Task.Delay(50, diagnosticDeadline.Token);
                    } while (timer.Elapsed < TimeSpan.FromSeconds(2));
                    await coordinator.PauseAsync(diagnosticDeadline.Token);
                }
                catch (Exception diagnosticError) { report.Diagnostics.Add("DiagnosticResume:" + BitmapSafeCode(ErrorCode(diagnosticError))); }
            }
        }
        finally
        {
            if (engine is not null)
            {
                report.TrackSnapshotRetries = engine.BitmapSeekTrackSnapshotRetries;
                if (frameObserver is not null) engine.SubtitleFrameChanged -= frameObserver;
                frameObserver = null;
                try
                {
                    report.ObserversReleasedBeforeStop = identity is null || await engine.ReleaseBitmapIdentityForProbeAsync(identity);
                    report.ObserverNativeReferencesAtStop = identity?.HeldNativeReferences ?? 0;
                    report.ObserverProjectionReferencesAtStop = identity?.HeldProjectionReferences ?? 0;
                    report.ObserverBitmapLeasesAtStop = engine.BitmapProbeOutstandingBitmapLeases;
                    identity = null;
                }
                catch (Exception error) { report.CleanupErrors.Add("ReleaseObservers:" + BitmapSafeCode(ErrorCode(error))); }
            }
            if (coordinator is not null)
            {
                try
                {
                    await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(35)); report.StopCompleted = true;
                    report.NativeSessionDrained = engine is not null && await engine.BitmapProbeSessionDrainedAsync();
                }
                catch (Exception error) { report.CleanupErrors.Add("Stop:" + BitmapSafeCode(ErrorCode(error))); }
                try { await coordinator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(35)); report.CoordinatorDisposed = true; }
                catch (Exception error) { report.CleanupErrors.Add("CoordinatorDispose:" + BitmapSafeCode(ErrorCode(error))); }
            }
            if (engine is not null)
            {
                try
                {
                    await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(20)); report.NativeDisposed = true;
                    report.NativeOwnerCleared = await engine.BitmapProbeOwnerClearedAsync();
                }
                catch (Exception error) { report.CleanupErrors.Add("NativeDispose:" + BitmapSafeCode(ErrorCode(error))); }
                finally { if (diagnosticObserver is not null) engine.Diagnostic -= diagnosticObserver; diagnosticObserver = null; }
            }
            if (mediaHash is not null && probeHash is not null && playbackId != Guid.Empty)
            {
                try
                {
                    using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var stopped = await Stats("Stopped", cleanupDeadline.Token); await Task.Delay(300, cleanupDeadline.Token);
                    var settled = await Stats("StoppedSettled", cleanupDeadline.Token);
                    report.ExactFinalApiCleanup = settled.PlaybackInfoCount == 1 && settled.StartCount == 1 && settled.StopCount == 1
                        && settled.EncodingCleanups == 0 && settled.AuthenticationFailures == 0
                        && BitmapApiAttemptsMatch(settled.ApiAttempts, report.PlaySessionIdHash!, 1)
                        && settled.Events.FirstOrDefault()?.Kind == "Start" && settled.Events.LastOrDefault()?.Kind == "Stop"
                        && settled.Events.LastOrDefault()?.Failed == false
                        && settled.Events.All(value => value.ItemId == "1001" && value.SessionHash == report.PlaySessionIdHash);
                    report.NoMediaOrProgressAfterStop = settled.MediaRequests == stopped.MediaRequests && settled.ProgressCount == stopped.ProgressCount;
                }
                catch (Exception error) { report.CleanupErrors.Add("FinalHttpObservation:" + BitmapSafeCode(ErrorCode(error))); }
            }
            if (authenticated is not null)
            {
                try
                {
                    using var logoutDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await authenticated.LogoutAsync(logoutDeadline.Token); report.LogoutCompleted = true;
                }
                catch (Exception error) { report.CleanupErrors.Add("Logout:" + BitmapSafeCode(ErrorCode(error))); }
            }
            if (report.Status == "ObservationsPassed")
            {
                if (report.ObserversReleasedBeforeStop && report.ObserverNativeReferencesAtStop == 0
                    && report.ObserverProjectionReferencesAtStop == 0 && report.ObserverBitmapLeasesAtStop == 0
                    && report.StopCompleted && report.NativeSessionDrained && report.CoordinatorDisposed && report.NativeDisposed
                    && report.NativeOwnerCleared && report.LogoutCompleted && report.ExactFinalApiCleanup
                    && report.NoMediaOrProgressAfterStop && report.CleanupErrors.Count == 0)
                { report.Status = "Passed"; report.Stage = "Complete"; }
                else { report.Status = "Failed"; report.ErrorCode ??= "BitmapSeekCleanupNotEstablished"; }
            }
            report.FinishedUtc = DateTimeOffset.UtcNow; Save();
            Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
            _window!.Close(); Exit();
        }
    }

    private bool BitmapSeekFixtureCompositionMatches(BitmapSubtitleNativeSample value, long sourceTimestamp) =>
        BitmapHasActualPixels(value) && value.CueSourceTimestampTicks == Math.Max(0, sourceTimestamp + TimeSpan.FromSeconds(_bitmapSubtitleSeekDelaySeconds).Ticks)
        && value.VideoWidth == 1280 && value.VideoHeight == 720 && value.Y == 646 && value.Height == 38
        && (sourceTimestamp == 20_000_000
            ? value.X == 442 && value.Width == 396 && value.CountedNonzeroAlphaPixels == 10_541
                && value.BgraPixelSha256 == "afda909ec10e65a76831aeb2442584731534e73d971825c0926c295a145f5765"
            : sourceTimestamp == 350_000_000 && value.X == 431 && value.Width == 418 && value.CountedNonzeroAlphaPixels == 11_215);
}
