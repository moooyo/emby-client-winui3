using System.Security.Cryptography;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private async Task RunNetworkRetryAsync()
    {
        var report = new NetworkRetryReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var observation = new HlsApiObservationHandler();
        using var http = new HttpClient(observation) { Timeout = Timeout.InfiniteTimeSpan };
        EmbyApiClient? authenticated = null;
        PlaybackCoordinator? coordinator = null;
        ColdRangeProxy? proxy = null;
        MediaPlayer? failedPlayer = null;
        MediaPlaybackSession? failedNativeSession = null;
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? rawFailure = null;
        TypedEventHandler<MediaPlaybackSession, object>? seekCompleted = null;
        Task? coldSeek = null;
        var observeColdSeek = false;
        try
        {
            SaveNetworkRetry(report);
            Require(report.NativeAot, "NativeAotRequired");
            var metadataPath = Path.Combine(_networkRetryMediaDirectory!, "fixture-h264-aac.json");
            var filePath = Path.Combine(_networkRetryMediaDirectory!, "fixture-h264-aac.mp4");
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath, deadline.Token);
            var metadata = JsonSerializer.Deserialize(metadataBytes, NetworkRetryJsonContext.Default.NetworkRetryMediaMetadata);
            Require(metadata is { Synthetic: true, FileName: "fixture-h264-aac.mp4" }
                && metadata.DurationTicks is >= 1_790_000_000 and <= 1_810_000_000
                && metadata.FileLength == new FileInfo(filePath).Length, "ExpectedLongSyntheticMedia");
            report.MediaLength = metadata!.FileLength;
            report.MediaDurationTicks = metadata.DurationTicks;
            report.MetadataSha256 = Convert.ToHexString(SHA256.HashData(metadataBytes)).ToLowerInvariant();
            await using (var file = File.OpenRead(filePath))
                report.MediaSha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, deadline.Token)).ToLowerInvariant();
            proxy = new ColdRangeProxy(filePath);
            report.AllowedPrefixBytes = ColdRangeProxy.PrefixLimit;
            report.MetadataRangeStart = proxy.MetadataStart;
            report.MetadataRangeEndExclusive = proxy.MetadataEnd;
            report.ColdByteProbe = report.MediaLength / 2;
            Require(report.ColdByteProbe >= ColdRangeProxy.PrefixLimit
                && !(report.ColdByteProbe >= proxy.MetadataStart && report.ColdByteProbe < proxy.MetadataEnd), "ColdRangeMiddleNotAvailable");
            await GetStatsAsync(http, deadline.Token);
            var api = new EmbyApiClient(http, FixtureRoot, new ClientIdentity("SYNTHETIC Native Network Retry",
                "Windows native probe", "synthetic-network-retry-" + Guid.NewGuid().ToString("N"), "0.1.0"));
            var publicInfo = await api.GetPublicSystemInfoAsync(deadline.Token);
            Require(publicInfo.Id == "synthetic-server-0001" && publicInfo.Version == "synthetic-1.0", "SyntheticServerRequired");
            var auth = await api.AuthenticateByNameAsync("demo", "demo", deadline.Token);
            Require(auth.AccessToken is not null && auth.User?.Id is not null, "SyntheticAuthenticationFailed");
            authenticated = api.WithAuthentication(auth.AccessToken!, auth.User!.Id!);
            var item = await authenticated.GetItemAsync("1001", deadline.Token);
            Require(item.RunTimeTicks == metadata.DurationTicks, "LongFixtureServerMediaMismatch");
            var engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true);
            var nativeFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.EventReceived += (_, args) =>
            {
                if (args.Kind != PlaybackEngineEventKind.Failed || report.NativeFailureCode is not null) return;
                report.NativeFailureCode = args.ErrorCode;
                report.ConfirmedRelayFailureCode = engine.ConfirmedRelayFailureForProbe;
                report.NativeFailureSnapshotTicks = args.Snapshot.PositionTicks;
                nativeFailure.TrySetResult();
            };
            engine.Diagnostic += (_, value) => report.Diagnostics.Add("Native:" + value.Operation + ":" + value.ErrorCode);
            coordinator = new PlaybackCoordinator(authenticated, new NetworkRetryEngine(engine, proxy), new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(1), CleanupTimeout = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(5)
            });
            coordinator.Diagnostic += (_, value) => report.Diagnostics.Add(value.Operation + ":" + value.ErrorCode);
            coordinator.StatusChanged += (_, value) => engine.UpdateMediaControls(value.Context, value.Status, "Synthetic network retry media");
            report.Stage = "PlayPrefixWithColdMiddleWithheld";
            SaveNetworkRetry(report);
            await coordinator.PlayAsync(IsolationSelection(0), deadline.Token);
            await WaitAsync(() => _element!.MediaPlayer is { } player && player.IsMuted
                && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                && player.PlaybackSession.Position.Ticks > 2_000_000
                && player.PlaybackSession.NaturalVideoWidth > 0, "NetworkRetryInitialClockFailed", deadline.Token);
            var original = coordinator.ActiveContext ?? throw new PlaybackException("NetworkRetryInitialContextMissing");
            report.OriginalRequestedAudioIndex = original.Selection.AudioStreamIndex;
            report.OriginalDefaultAudioIndex = original.Source.DefaultAudioStreamIndex;
            report.OriginalRequestedSubtitleIndex = original.Selection.SubtitleStreamIndex;
            failedPlayer = _element!.MediaPlayer;
            failedNativeSession = failedPlayer.PlaybackSession;
            report.PlayingNativeTicksBeforeFault = failedNativeSession.Position.Ticks;
            rawFailure = (_, args) =>
            {
                report.RawNativeFailureKind ??= args.Error.ToString();
                report.RawNativeFailureHResult ??= args.ExtendedErrorCode?.HResult;
            };
            seekCompleted = (_, _) =>
            {
                if (observeColdSeek && report.NativeFailureCode is null)
                    report.NativeSeekCompletedPositionsBeforeFailure.Add(failedNativeSession.Position.Ticks);
            };
            failedPlayer.MediaFailed += rawFailure;
            failedNativeSession.SeekCompleted += seekCompleted;
            await coordinator.PauseAsync(deadline.Token);
            await WaitAsync(() => failedNativeSession.PlaybackState == MediaPlaybackState.Paused, "NetworkRetryPauseFailed", deadline.Token);
            await Task.Delay(150, deadline.Token);
            report.PausedNativeTicksBeforeFault = failedNativeSession.Position.Ticks;
            report.NativeBufferedBeforeFault = failedNativeSession.GetBufferedRanges().Select(value => new NativeTimeRangeSummary
                { StartTicks = value.Start.Ticks, EndTicks = value.End.Ticks }).ToList();
            report.ColdSeekTargetOutsideBuffer = !report.NativeBufferedBeforeFault.Any(value =>
                value.StartTicks <= report.RequestedColdSeekTicks && value.EndTicks >= report.RequestedColdSeekTicks);
            report.ColdByteUndeliveredBeforeFault = !proxy.Records().Any(value => value.BytesForwarded > 0
                && value.Offset <= report.ColdByteProbe && value.Offset + value.BytesForwarded > report.ColdByteProbe);
            Require(report.PlayingNativeTicksBeforeFault > 0 && report.ColdSeekTargetOutsideBuffer
                && report.ColdByteUndeliveredBeforeFault, "ColdRangeInstrumentationPreconditionFailed");
            report.Stage = "InjectColdHttp503";
            observeColdSeek = true;
            SaveNetworkRetry(report);
            coldSeek = coordinator.SeekAsync(report.RequestedColdSeekTicks, deadline.Token);
            proxy.FailColdRequests();
            try { await coldSeek.WaitAsync(TimeSpan.FromSeconds(20), deadline.Token); report.ColdSeekOutcome = "SeekCompleted"; }
            catch (PlaybackException error) { report.ColdSeekOutcome = error.ErrorCode; }
            catch (OperationCanceledException) { report.ColdSeekOutcome = "Canceled"; }
            catch (TimeoutException) { report.ColdSeekOutcome = "ObservationTimeout"; }
            await Task.WhenAny(nativeFailure.Task, Task.Delay(TimeSpan.FromSeconds(5), deadline.Token));
            observeColdSeek = false;
            report.ActualColdHttp503Observed = proxy.Records().Any(value => value.IsCold && value.StatusCode == 503
                && value.BytesForwarded == 0 && value.Completed);
            Require(report.ActualColdHttp503Observed && report.NativeFailureCode == "NetworkFailure"
                && report.ConfirmedRelayFailureCode == "NetworkFailure", "ExpectedNativeNetworkFailureNotObserved");
            await WaitAsync(() => coordinator.Recovery is not null, "NetworkFailureDidNotOfferRetry", deadline.Token);
            var recovery = coordinator.Recovery!;
            report.RecoveryOffered = true;
            report.RecoveryErrorCode = recovery.ErrorCode;
            report.RecoveryTargetTicks = recovery.Selection.StartPositionTicks;
            report.RecoveryPausedIntent = recovery.IsPaused;
            report.RecoveryRequestedAudioIndex = recovery.Selection.AudioStreamIndex;
            report.RecoveryRequestedSubtitleIndex = recovery.Selection.SubtitleStreamIndex;
            report.RecoveryPositionSource = Math.Abs(recovery.Selection.StartPositionTicks - report.RequestedColdSeekTicks) <= 7_500_000
                ? report.NativeSeekCompletedPositionsBeforeFailure.Count == 0 ? "UncompletedSeekTargetOrNativeCursorNotDecodedFrame" : "SeekCompletedNativeCursorNotPixelEvidence"
                : Math.Abs(recovery.Selection.StartPositionTicks - report.PausedNativeTicksBeforeFault) <= 7_500_000
                    ? "LastKnownNonzeroPlayingPausedPosition" : "FailureSnapshotNativeCursorNotDecodedFrame";
            Require(recovery.ErrorCode == "NetworkFailure" && recovery.IsPaused && recovery.Selection.StartPositionTicks > 0,
                "NetworkRetryRecoveryTargetInvalid");
            Require(recovery.Selection.AudioStreamIndex == original.Selection.AudioStreamIndex
                && recovery.Selection.SubtitleStreamIndex == original.Selection.SubtitleStreamIndex,
                "NetworkRetryChangedTrackSelectionIntent");
            failedPlayer.MediaFailed -= rawFailure; rawFailure = null;
            failedNativeSession.SeekCompleted -= seekCompleted; seekCompleted = null;
            failedPlayer = null; failedNativeSession = null;
            report.Stage = "RestoreAndExplicitRetry";
            var requestsBeforeRetry = proxy.Records().Length;
            proxy.Restore();
            report.RetryInvoked = true;
            SaveNetworkRetry(report);
            await coordinator.RetryAsync(recovery.RecoveryId, deadline.Token);
            var replacement = coordinator.ActiveContext ?? throw new PlaybackException("NetworkRetryReplacementMissing");
            var retryPlayer = _element!.MediaPlayer;
            report.RetryPlaybackIdChanged = replacement.PlaybackId != original.PlaybackId;
            report.RetryServerSessionChanged = replacement.PlaySessionId != original.PlaySessionId;
            report.RetrySourceBound = retryPlayer?.Source is not null;
            report.RetryNativePositionTicks = retryPlayer!.PlaybackSession.Position.Ticks;
            report.RetrySnapshotPositionTicks = engine.Snapshot?.PositionTicks;
            report.RetryNativePaused = retryPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Paused;
            report.RecoveryConsumed = !coordinator.CanRetry && coordinator.Recovery is null;
            Require(report.RetryPlaybackIdChanged && report.RetryServerSessionChanged && report.RetrySourceBound
                && report.RetryNativePaused && retryPlayer.IsMuted && report.RecoveryConsumed
                && Math.Abs(report.RetryNativePositionTicks.Value - recovery.Selection.StartPositionTicks) <= 7_500_000,
                "NetworkRetryActualReplacementInvalid");
            var paused = retryPlayer.PlaybackSession.Position.Ticks;
            await Task.Delay(650, deadline.Token);
            report.RetryPausedDriftTicks = Math.Abs(retryPlayer.PlaybackSession.Position.Ticks - paused);
            Require(report.RetryPausedDriftTicks <= 500_000, "NetworkRetryPauseNotPreserved");
            await coordinator.ResumeAsync(deadline.Token);
            await WaitAsync(() => retryPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
                && retryPlayer.PlaybackSession.Position.Ticks > paused + 2_000_000, "NetworkRetryResumeDidNotAdvance", deadline.Token);
            report.RetryResumedNativeTicks = retryPlayer.PlaybackSession.Position.Ticks;
            await coordinator.StopAsync(deadline.Token);
            report.FinalDetached = _element.MediaPlayer is null && coordinator.ActiveContext is null;
            report.NoOldBindingRequestsAfterRetry = proxy.Records().Skip(requestsBeforeRetry).All(value => value.Binding != 1);
            var sessionHashes = new[] { IsolationHash(original.PlaySessionId), IsolationHash(replacement.PlaySessionId) };
            report.ValidSessionReports = sessionHashes.All(hash =>
            {
                var events = observation.Events.Where(value => value.SessionHash == hash).ToArray();
                return events.Length >= 2 && events[0].Operation == "Start" && events[^1].Operation == "Stop"
                    && events.Count(value => value.Operation == "Start") == 1 && events.Count(value => value.Operation == "Stop") == 1
                    && events.All(value => value.StatusCode is >= 200 and < 300);
            });
            await coordinator.DisposeAsync(); coordinator = null;
            await authenticated.LogoutAsync(deadline.Token); authenticated = null;
            var stoppedRequests = proxy.Records().Length;
            var stoppedStats = await GetStatsAsync(http, deadline.Token);
            await Task.Delay(1300, deadline.Token);
            var settledStats = await GetStatsAsync(http, deadline.Token);
            report.FinalMediaRequestsQuiet = stoppedRequests == proxy.Records().Length
                && stoppedStats.MediaRequests == settledStats.MediaRequests && stoppedStats.Events.Length == settledStats.Events.Length;
            Require(report.FinalDetached && report.NoOldBindingRequestsAfterRetry && report.ValidSessionReports
                && report.FinalMediaRequestsQuiet && proxy.BindingCount == 2 && proxy.CredentialRejections == 0
                && report.Diagnostics.Count == 0, "NetworkRetryFinalValidationFailed");
            report.Stage = "Complete";
            report.Status = "NetworkRetryPassed";
        }
        catch (Exception error) { report.Status = "Failed"; report.ErrorCode = ErrorCode(error); report.ErrorHResult = error.HResult; }
        finally
        {
            observeColdSeek = false;
            try
            {
                if (failedPlayer is not null && rawFailure is not null) failedPlayer.MediaFailed -= rawFailure;
                if (failedNativeSession is not null && seekCompleted is not null) failedNativeSession.SeekCompleted -= seekCompleted;
                if (coordinator is not null) await coordinator.DisposeAsync();
                if (coldSeek is not null) { try { await coldSeek; } catch { } }
                if (authenticated is not null) await authenticated.LogoutAsync();
                if (proxy is not null) await proxy.DisposeAsync();
            }
            catch (Exception error) { report.Status = "Failed"; report.ErrorCode ??= "FinalCleanup_" + ErrorCode(error); }
            report.ProxyBindings = proxy?.BindingCount ?? 0;
            report.CredentialRejections = proxy?.CredentialRejections ?? 0;
            report.Ranges = proxy?.Records().ToList() ?? [];
            report.ApiEvents = observation.Events.ToList();
            report.FinishedAt = DateTimeOffset.UtcNow;
            SaveNetworkRetry(report);
            Environment.ExitCode = report.Status == "NetworkRetryPassed" ? 0 : 1;
            _window!.Close(); Exit();
        }
    }

    private void SaveNetworkRetry(NetworkRetryReport report)
    {
        var temporary = _resultPath! + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report, NetworkRetryJsonContext.Default.NetworkRetryReport));
        File.Move(temporary, _resultPath!, overwrite: true);
    }
}
