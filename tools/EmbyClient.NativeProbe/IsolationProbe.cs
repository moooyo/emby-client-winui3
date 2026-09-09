using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Media.Playback;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private async Task RunIsolationAsync()
    {
        var report = new IsolationReport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var observation = new HlsApiObservationHandler();
        using var http = new HttpClient(observation) { Timeout = Timeout.InfiniteTimeSpan };
        await using var invalid = new InvalidMediaFixture();
        EmbyApiClient? authenticated = null;
        PlaybackCoordinator? coordinator = null;
        NativePlaybackEngine? engine = null;
        Action<PlaybackEngineEventArgs>? failureObserver = null;
        try
        {
            SaveIsolation(report);
            Require(report.NativeAot, "NativeAotRequired");
            await GetStatsAsync(http, deadline.Token);
            var api = new EmbyApiClient(http, FixtureRoot, new ClientIdentity("SYNTHETIC Native Isolation",
                "Windows native probe", "synthetic-isolation-" + Guid.NewGuid().ToString("N"), "0.1.0"));
            var info = await api.GetPublicSystemInfoAsync(deadline.Token);
            Require(info.Id == "synthetic-server-0001" && info.ServerName == "SYNTHETIC Emby Client Fixture"
                && info.Version == "synthetic-1.0", "SyntheticServerRequired");
            var auth = await api.AuthenticateByNameAsync("demo", "demo", deadline.Token);
            Require(auth.AccessToken is not null && auth.User?.Id is not null, "SyntheticAuthenticationFailed");
            authenticated = api.WithAuthentication(auth.AccessToken!, auth.User!.Id!);
            var item = await authenticated.GetItemAsync("1001", deadline.Token);
            Require(item.Name?.StartsWith("Synthetic", StringComparison.Ordinal) == true
                && item.RunTimeTicks is >= 590_000_000 and <= 610_000_000, "ExpectedSixtySecondSyntheticMedia");
            engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true);
            engine.EventReceived += (_, args) =>
            {
                report.EngineEvents.Add(new IsolationEngineEvent
                {
                    PlaybackHash = IsolationHash(args.Snapshot.PlaybackId.ToString("N")), Kind = args.Kind.ToString(),
                    State = args.Snapshot.State.ToString(), PositionTicks = args.Snapshot.PositionTicks, ErrorCode = args.ErrorCode
                });
                failureObserver?.Invoke(args);
            };
            engine.Diagnostic += (_, value) => report.Diagnostics.Add("Native:" + value.Operation + ":" + value.ErrorCode);
            var injectingEngine = new IsolationFailureEngine(engine);
            coordinator = new PlaybackCoordinator(authenticated, injectingEngine, new PlaybackCoordinatorOptions
            {
                ProgressInterval = TimeSpan.FromSeconds(1), CleanupTimeout = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(5)
            });
            coordinator.StatusChanged += (_, value) => engine.UpdateMediaControls(value.Context, value.Status, "Synthetic isolation media");
            coordinator.Diagnostic += (_, value) => report.Diagnostics.Add(value.Operation + ":" + value.ErrorCode);
            await OpenIsolationHealthyAsync(coordinator, 0, deadline.Token);
            await coordinator.StopAsync(deadline.Token);

            for (var number = 1; number <= report.RequiredCycles; number++)
            {
                var cycle = new IsolationCycle { Number = number };
                report.Cycles.Add(cycle);
                SaveIsolation(report);
                Func<Task>? cancelledReplay = null;
                Func<Task>? failedReplay = null;
                try
                {
                    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    var openingObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Task? opening = null;
                    var cancelledId = Guid.Empty;
                    engine.ArmOpeningCancellationForProbe(() =>
                    {
                        try
                        {
                            cycle.CancelNativeState = engine.OpeningStateObservedForProbe;
                            cycle.CancelSourceWasBound = engine.OpeningSourceBoundForProbe;
                            cycle.CancelOpenTaskWasPending = opening is { IsCompleted: false };
                            var context = coordinator.ActiveContext ?? throw new PlaybackException("OpeningContextMissing");
                            cancelledId = context.PlaybackId;
                            cycle.CancelledSessionHash = IsolationHash(context.PlaySessionId);
                            cancelledReplay = engine.CaptureCurrentCallbackReplayForProbe();
                            cancellation.Cancel();
                            openingObserved.TrySetResult();
                        }
                        catch (Exception error) { cancellation.Cancel(); openingObserved.TrySetException(error); }
                    });
                    opening = coordinator.PlayAsync(IsolationSelection(170_000_000), cancellation.Token);
                    await Task.WhenAny(openingObserved.Task, opening, Task.Delay(TimeSpan.FromSeconds(10), deadline.Token));
                    if (!openingObserved.Task.IsCompleted)
                    {
                        cancellation.Cancel();
                        try { await opening; } catch (OperationCanceledException) { }
                        throw new PlaybackException("OpeningCancellationInstrumentationMissed");
                    }
                    await openingObserved.Task;
                    try { await opening; }
                    catch (OperationCanceledException) { cycle.CancellationObserved = true; }
                    finally { engine.ClearOpeningCancellationForProbe(); }
                    await coordinator.StopAsync(deadline.Token);
                    cycle.CancellationDetached = _element!.MediaPlayer is null && coordinator.ActiveContext is null;
                    Require(cycle.CancelNativeState == "Opening" && cycle.CancelSourceWasBound && cycle.CancelOpenTaskWasPending
                        && cycle.CancellationObserved && cycle.CancellationDetached, "NativeOpeningCancellationFailed");
                    Require(!observation.Events.Any(value => value.SessionHash == cycle.CancelledSessionHash && value.Operation == "Start"),
                        "CancelledOpeningReportedStart");
                    await CheckIsolationRecoveryAsync(cycle, "AfterOpeningCancellation", cancelledId,
                        cancelledReplay!, coordinator, engine, observation, report, deadline.Token);
                    cancelledReplay = null;

                    cycle.Stage = "NativeDecoderFailure";
                    var failedId = Guid.Empty;
                    failureObserver = args =>
                    {
                        if (args.Kind != PlaybackEngineEventKind.Failed || cycle.NativeDecoderFailureCode is not null) return;
                        cycle.NativeDecoderFailureCode = args.ErrorCode;
                        cycle.DecoderFailureSourceWasBound = engine.HasBoundNativeSourceForProbe;
                        failedId = args.Snapshot.PlaybackId;
                        cycle.FailedSessionHash = coordinator.ActiveContext is { } context ? IsolationHash(context.PlaySessionId) : null;
                        failedReplay = engine.CaptureCurrentCallbackReplayForProbe();
                    };
                    injectingEngine.NextInvalidMedia = invalid.MediaUri;
                    try { await coordinator.PlayAsync(IsolationSelection(0), deadline.Token); }
                    catch (PlaybackException error) { cycle.DecoderFailureTerminalCode = error.ErrorCode; }
                    finally { failureObserver = null; }
                    await coordinator.StopAsync(deadline.Token);
                    cycle.FailureDetached = _element!.MediaPlayer is null && coordinator.ActiveContext is null;
                    Require(cycle.NativeDecoderFailureCode is "UnsupportedFormat" or "NativePlaybackFailure"
                        && cycle.DecoderFailureTerminalCode is not null && cycle.DecoderFailureSourceWasBound
                        && cycle.FailureDetached && failedId != Guid.Empty && failedReplay is not null, "ExpectedNativeDecoderFailureMissing");
                    await CheckIsolationRecoveryAsync(cycle, "AfterNativeDecoderFailure", failedId,
                        failedReplay!, coordinator, engine, observation, report, deadline.Token);
                    failedReplay = null;
                    cycle.Stage = "Complete";
                    cycle.Status = "Passed";
                    report.CompletedCycles++;
                }
                catch (Exception error) { cycle.Status = "Failed"; cycle.ErrorCode = ErrorCode(error); throw; }
                finally
                {
                    engine.ClearOpeningCancellationForProbe();
                    failureObserver = null;
                    cancelledReplay = null;
                    failedReplay = null;
                    report.ApiEvents = observation.Events.ToList();
                    SaveIsolation(report);
                }
            }
            await coordinator.DisposeAsync(); coordinator = null;
            await CheckConcurrentOpeningDisposeAsync(authenticated, observation, report, deadline.Token);
            await authenticated.LogoutAsync(deadline.Token); authenticated = null;
            report.FinalPlayerDetached = _element!.MediaPlayer is null;
            var stopped = await GetStatsAsync(http, deadline.Token);
            var invalidStopped = invalid.Requests;
            await Task.Delay(1300, deadline.Token);
            var settled = await GetStatsAsync(http, deadline.Token);
            report.FinalNetworkQuiet = stopped.MediaRequests == settled.MediaRequests && stopped.RangeRequests == settled.RangeRequests
                && stopped.PlaybackInfoCount == settled.PlaybackInfoCount && stopped.Events.Length == settled.Events.Length
                && invalidStopped == invalid.Requests;
            Require(report.FinalPlayerDetached && report.FinalNetworkQuiet && invalid.Requests > 0
                && invalid.CredentialRejections == 0, "IsolationFinalCleanupFailed");
            Require(report.Diagnostics.All(value => value is "Fallback:UnsupportedFormat" or "Fallback:NativePlaybackFailure"),
                "UnexpectedIsolationDiagnostic");
            report.Status = "IsolationPassed";
        }
        catch (Exception error) { report.Status = "Failed"; report.ErrorCode = ErrorCode(error); report.ErrorHResult = error.HResult; }
        finally
        {
            failureObserver = null;
            try
            {
                engine?.ClearOpeningCancellationForProbe();
                if (coordinator is not null) await coordinator.DisposeAsync();
                if (authenticated is not null) await authenticated.LogoutAsync();
                await invalid.DisposeAsync();
            }
            catch (Exception error) { report.Status = "Failed"; report.ErrorCode ??= "FinalCleanup_" + ErrorCode(error); }
            report.InvalidMediaRequests = invalid.Requests;
            report.InvalidMediaCredentialRejections = invalid.CredentialRejections;
            report.ApiEvents = observation.Events.ToList();
            report.FinishedAt = DateTimeOffset.UtcNow;
            SaveIsolation(report);
            Environment.ExitCode = report.Status == "IsolationPassed" ? 0 : 1;
            _window!.Close(); Exit();
        }
    }

    private async Task CheckConcurrentOpeningDisposeAsync(EmbyApiClient api, HlsApiObservationHandler observation,
        IsolationReport report, CancellationToken ct)
    {
        var result = report.ConcurrentDispose;
        var engine = new NativePlaybackEngine(_window!.DispatcherQueue, _element!, initialMuted: true);
        engine.Diagnostic += (_, value) => report.Diagnostics.Add("Native:" + value.Operation + ":" + value.ErrorCode);
        await using var coordinator = new PlaybackCoordinator(api, engine, new PlaybackCoordinatorOptions
        {
            ProgressInterval = TimeSpan.FromSeconds(1), CleanupTimeout = TimeSpan.FromSeconds(10), ReportTimeout = TimeSpan.FromSeconds(5)
        });
        coordinator.Diagnostic += (_, value) => report.Diagnostics.Add(value.Operation + ":" + value.ErrorCode);
        coordinator.StatusChanged += (_, value) => engine.UpdateMediaControls(value.Context, value.Status, "Synthetic disposal isolation");
        await OpenIsolationHealthyAsync(coordinator, 0, ct);
        await coordinator.StopAsync(ct);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? opening = null;
        Task? first = null;
        Task? second = null;
        Task? secondObservation = null;
        string? sessionHash = null;
        engine.ArmOpeningCancellationForProbe(() =>
        {
            try
            {
                result.NativeOpeningObserved = engine.OpeningStateObservedForProbe == "Opening";
                result.SourceWasBound = engine.OpeningSourceBoundForProbe;
                result.OpenWasPending = opening is { IsCompleted: false };
                sessionHash = coordinator.ActiveContext is { } context ? IsolationHash(context.PlaySessionId) : null;
                first = engine.DisposeAsync().AsTask();
                second = engine.DisposeAsync().AsTask();
                result.BothDisposeCallsInitiallyPending = !first.IsCompleted && !second.IsCompleted;
                secondObservation = second.ContinueWith(_ =>
                {
                    result.SecondDisposeObservedCompleteDrain = engine.ProductDisposalCompletedForProbe
                        && engine.ProductPlayerOwnerClearedForProbe;
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                observed.TrySetResult();
            }
            catch (Exception error) { observed.TrySetException(error); }
        });
        try
        {
            opening = coordinator.PlayAsync(IsolationSelection(170_000_000), ct);
            await Task.WhenAny(observed.Task, opening, Task.Delay(TimeSpan.FromSeconds(10), ct));
            Require(observed.Task.IsCompleted, "ConcurrentDisposeOpeningInstrumentationMissed");
            await observed.Task;
            try { await opening; }
            catch (OperationCanceledException) { result.OpenCancelled = true; }
            await Task.WhenAll(first!, second!, secondObservation!).WaitAsync(TimeSpan.FromSeconds(15), ct);
            result.FirstDisposeCompleted = first!.IsCompletedSuccessfully;
            result.SecondDisposeCompleted = second!.IsCompletedSuccessfully;
            result.OwnerCleared = engine.ProductPlayerOwnerClearedForProbe;
            result.PlayerDetached = _element!.MediaPlayer is null;
            result.CancelledOpeningDidNotReportStart = sessionHash is not null
                && !observation.Events.Any(value => value.SessionHash == sessionHash && value.Operation == "Start");
            Require(result.NativeOpeningObserved && result.SourceWasBound && result.OpenWasPending
                && result.BothDisposeCallsInitiallyPending && result.OpenCancelled && result.FirstDisposeCompleted
                && result.SecondDisposeCompleted && result.SecondDisposeObservedCompleteDrain && result.OwnerCleared
                && result.PlayerDetached && result.CancelledOpeningDidNotReportStart, "ConcurrentOpeningDisposeFailed");
        }
        finally { engine.ClearOpeningCancellationForProbe(); }
    }

    private async Task CheckIsolationRecoveryAsync(IsolationCycle cycle, string kind, Guid retiredId,
        Func<Task> replay, PlaybackCoordinator coordinator, NativePlaybackEngine engine,
        HlsApiObservationHandler observation, IsolationReport report, CancellationToken ct)
    {
        cycle.Stage = kind;
        var recovery = new IsolationRecovery { Kind = kind };
        cycle.Recoveries.Add(recovery);
        var context = await OpenIsolationHealthyAsync(coordinator, 50_000_000, ct);
        recovery.SessionHash = IsolationHash(context.PlaySessionId);
        recovery.NativeInitialTicks = _element!.MediaPlayer.PlaybackSession.Position.Ticks;
        Require(context.PlaybackId != retiredId, "RecoveryReusedPlaybackId");
        await coordinator.PauseAsync(ct);
        await WaitAsync(() => _element.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Paused, "RecoveryPauseFailed", ct);
        await WaitForIsolationApiSettlementAsync(observation, ct);
        recovery.ApiSettledBeforeReplay = true;
        var paused = _element.MediaPlayer.PlaybackSession.Position.Ticks;
        var firstEngineEvent = report.EngineEvents.Count;
        var firstApiEvent = observation.Events.Length;
        recovery.ApiReplayStartCount = firstApiEvent;
        recovery.EngineReplayStartCount = firstEngineEvent;
        await replay();
        recovery.ReplayedCallbacks = 7;
        await engine.PauseAsync(retiredId, ct);
        await engine.ResumeAsync(retiredId, ct);
        await engine.SeekAsync(retiredId, 450_000_000, ct);
        await engine.SetVolumeAsync(retiredId, 31, true, ct);
        await engine.StopAsync(retiredId, ct);
        await coordinator.PauseAsync(retiredId, ct);
        await coordinator.ResumeAsync(retiredId, ct);
        await coordinator.SeekAsync(retiredId, 450_000_000, ct);
        await coordinator.StopAsync(retiredId, ct);
        recovery.StaleIdCommands = 9;
        await Task.Delay(650, ct);
        recovery.CurrentSourceSurvived = coordinator.ActiveContext?.PlaybackId == context.PlaybackId
            && engine.Snapshot?.PlaybackId == context.PlaybackId && _element.MediaPlayer?.Source is not null;
        Require(recovery.CurrentSourceSurvived, "RetiredCallbackChangedCurrentSource");
        var player = _element.MediaPlayer!;
        recovery.PausedDriftTicks = Math.Abs(player.PlaybackSession.Position.Ticks - paused);
        recovery.MutedVolumePreserved = player.IsMuted && Math.Abs(player.Volume - 1) < 0.0001;
        recovery.NoRetiredEngineEvents = !report.EngineEvents.Skip(firstEngineEvent).Any(value =>
            value.PlaybackHash == IsolationHash(retiredId.ToString("N")) || value.Kind is "Failed" or "Ended");
        recovery.NoUnexpectedApiControls = observation.Events.Skip(firstApiEvent).All(value => value.Operation == "Progress"
            && value.EventName is "TimeUpdate" or null);
        Require(player.PlaybackSession.PlaybackState == MediaPlaybackState.Paused && recovery.PausedDriftTicks <= 500_000
            && recovery.MutedVolumePreserved && recovery.NoRetiredEngineEvents && recovery.NoUnexpectedApiControls,
            "RetiredCallbackOrCommandAffectedRecovery");
        await coordinator.ResumeAsync(ct);
        await WaitAsync(() => player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && player.PlaybackSession.Position.Ticks > paused + 2_000_000, "RecoveryResumeFailed", ct);
        recovery.ResumedNativeTicks = player.PlaybackSession.Position.Ticks;
        await coordinator.StopAsync(ct);
        recovery.DetachedAfterStop = _element.MediaPlayer is null && coordinator.ActiveContext is null;
        var events = observation.Events.Where(value => value.SessionHash == recovery.SessionHash).ToArray();
        recovery.ValidReportOrder = events.Length >= 4 && events[0].Operation == "Start" && events[^1].Operation == "Stop"
            && events.Count(value => value.Operation == "Start") == 1 && events.Count(value => value.Operation == "Stop") == 1
            && events.All(value => value.StatusCode is >= 200 and < 300);
        Require(recovery.DetachedAfterStop && recovery.ValidReportOrder, "RecoveryReportOrRetirementFailed");
    }

    private async Task<PlaybackContext> OpenIsolationHealthyAsync(PlaybackCoordinator coordinator, long position, CancellationToken ct)
    {
        await coordinator.PlayAsync(IsolationSelection(position), ct);
        var context = coordinator.ActiveContext ?? throw new PlaybackException("HealthyIsolationContextMissing");
        await WaitAsync(() => _element!.MediaPlayer is { } player && player.IsMuted
            && player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing
            && player.PlaybackSession.Position.Ticks > position + 2_000_000
            && player.PlaybackSession.NaturalVideoWidth > 0 && player.PlaybackSession.NaturalVideoHeight > 0,
            "HealthyIsolationClockDidNotAdvance", ct);
        Require(Math.Abs(_element!.MediaPlayer.PlaybackSession.Position.Ticks - position) <= 15_000_000,
            "HealthyIsolationInitialPositionMismatch");
        return context;
    }

    private static PlaybackSelection IsolationSelection(long position) => new()
    {
        ItemId = "1001", MediaSourceId = "synthetic-mp4", SubtitleStreamIndex = -1, StartPositionTicks = position
    };
    private static async Task WaitForIsolationApiSettlementAsync(HlsApiObservationHandler observation, CancellationToken ct)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var quiet = System.Diagnostics.Stopwatch.StartNew();
        var previous = observation.CompletedRequests;
        while (quiet.Elapsed < TimeSpan.FromMilliseconds(200))
        {
            if (total.Elapsed > TimeSpan.FromSeconds(3)) throw new PlaybackException("IsolationApiSettlementInstrumentationFailed");
            await Task.Delay(25, ct);
            var current = observation.CompletedRequests;
            if (current != previous) { previous = current; quiet.Restart(); }
        }
    }
    private static string IsolationHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private void SaveIsolation(IsolationReport report)
    {
        var temporary = _resultPath! + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(report, IsolationJsonContext.Default.IsolationReport));
        File.Move(temporary, _resultPath!, overwrite: true);
    }
}
