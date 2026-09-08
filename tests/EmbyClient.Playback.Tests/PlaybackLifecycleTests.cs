using System.Net;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackLifecycleTests
{


    [Fact(Timeout = 15000)]
    public async Task Start_is_reported_only_after_the_engine_has_started_and_completed_its_resume_seek()
    {
        await using var context = new PlaybackTestContext();
        var entered = Signal();
        var release = Signal();
        context.Engine.OnOpenAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };

        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PlaybackStatus.Opening, context.Coordinator.Status);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Empty(context.Handler.At("Sessions/Playing/Progress"));
        release.TrySetResult();
        await play;

        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(600_000_001, opened.InitialPositionTicks);
        Assert.Equal(0, opened.TimelineOffsetTicks);
        var report = Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody;
        Assert.Equal(600_000_001, report.GetProperty("PositionTicks").GetInt64());
        Assert.Equal("DirectStream", report.GetProperty("PlayMethod").GetString());
        Assert.False(report.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact(Timeout = 15000)]
    public async Task Stop_finishes_an_in_flight_progress_report_before_reporting_the_final_position()
    {
        await using var context = new PlaybackTestContext();
        var progressEntered = Signal();
        var releaseProgress = Signal();
        context.Handler.RespondAsync = async (request, token) =>
        {
            if (request.Uri.AbsolutePath.EndsWith("/Playing/Progress", StringComparison.Ordinal))
            {
                progressEntered.TrySetResult();
                await releaseProgress.Task.WaitAsync(token);
            }
            return context.Respond(request);
        };
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        context.Engine.SetPosition(713_579_246);
        var pause = context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        await progressEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        using var cancelledStop = new CancellationTokenSource();
        cancelledStop.Cancel();
        var stop = context.Coordinator.StopAsync(cancelledStop.Token);
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.False(stop.IsCompleted);
        releaseProgress.TrySetResult();
        await Task.WhenAll(pause, stop);

        var reports = context.Handler.Requests.Where(request => request.Uri.AbsolutePath.Contains("/Sessions/Playing", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "/emby/Sessions/Playing", "/emby/Sessions/Playing/Progress", "/emby/Sessions/Playing/Stopped" },
            reports.Select(request => request.Uri.AbsolutePath));
        Assert.Equal("Pause", reports[1].JsonBody.GetProperty("EventName").GetString());
        Assert.True(reports[1].JsonBody.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(713_579_246, reports[2].JsonBody.GetProperty("PositionTicks").GetInt64());
        Assert.Single(context.Engine.Stopped);
        Assert.Null(context.Coordinator.ActiveContext);
    }

    [Fact(Timeout = 15000)]
    public async Task Failing_stop_reporting_and_encoding_cleanup_do_not_prevent_live_stream_cleanup()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { RequiresClosing = true, LiveStreamId = "live-owned" }];
        var diagnostics = new List<string>();
        context.Coordinator.Diagnostic += (_, args) => diagnostics.Add(args.Operation);
        context.Handler.RespondAsync = (request, _) => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/Stopped", StringComparison.Ordinal)
            || request.Uri.AbsolutePath.EndsWith("/ActiveEncodings", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : context.Respond(request));
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken);

        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(context.Engine.Stopped);
        Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Equal("session-1", Assert.Single(context.Handler.At("Videos/ActiveEncodings")).Query["PlaySessionId"]);
        Assert.Equal("live-owned", Assert.Single(context.Handler.At("LiveStreams/Close")).Query["LiveStreamId"]);
        Assert.Contains("StopReport", diagnostics);
        Assert.Contains("StopEncoding", diagnostics);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_during_engine_open_retires_native_and_server_resources_without_claiming_playback_started()
    {
        await using var context = new PlaybackTestContext();
        using var lifetime = new CancellationTokenSource();
        var entered = Signal();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { RequiresClosing = true, LiveStreamId = "opening-live" }];
        context.Engine.OnOpenAsync = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };

        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), lifetime.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);

        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Single(context.Engine.Stopped);
        Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
        Assert.Equal("opening-live", Assert.Single(context.Handler.At("LiveStreams/Close")).Query["LiveStreamId"]);
        Assert.Equal(PlaybackStatus.Idle, context.Coordinator.Status);
        Assert.Null(context.Coordinator.ActiveContext);
    }

    [Fact(Timeout = 15000)]
    public async Task New_playback_cancels_an_open_in_progress_before_starting_the_replacement()
    {
        await using var context = new PlaybackTestContext();
        var entered = Signal();
        context.Engine.OnOpenAsync = async (_, token) =>
        {
            if (context.Engine.Opened.Length == 1)
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var first = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { ItemId = "movie-b" }, TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(context.Engine.Opened[0].PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Equal("movie-b", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("ItemId").GetString());
        Assert.Equal("movie-b", context.Coordinator.ActiveContext?.Selection.ItemId);
    }

    [Fact(Timeout = 15000)]
    public async Task Events_from_a_retired_playback_cannot_end_or_restart_its_replacement()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var old = context.Engine.Snapshot!;
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { ItemId = "movie-b" }, TestContext.Current.CancellationToken);
        var replacementId = context.Coordinator.ActiveContext!.PlaybackId;

        context.Engine.Emit(PlaybackEngineEventKind.PositionChanged, old with { PositionTicks = long.MaxValue });
        context.Engine.Emit(PlaybackEngineEventKind.Ended, old with { State = PlaybackEngineState.Ended });
        context.Engine.Emit(PlaybackEngineEventKind.Failed, old with { State = PlaybackEngineState.Failed }, "UnsupportedFormat");
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(replacementId, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Equal(PlaybackStatus.Paused, context.Coordinator.Status);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Single(context.Engine.Stopped);
        var progress = Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody;
        Assert.Equal("movie-b", progress.GetProperty("ItemId").GetString());
        Assert.Equal("session-2", progress.GetProperty("PlaySessionId").GetString());
        Assert.Equal(0, progress.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Engine_open_failure_releases_its_transcode_and_live_stream_without_retrying_unrelated_errors()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { RequiresClosing = true, LiveStreamId = "failed-live" }];
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("EngineFailed");

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken));

        Assert.Equal("EngineFailed", error.ErrorCode);
        Assert.Single(context.Engine.Opened);
        Assert.Single(context.Engine.Stopped);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
        Assert.Single(context.Handler.At("LiveStreams/Close"));
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
    }

    [Fact(Timeout = 15000)]
    public async Task Failure_to_report_start_stops_the_already_running_player_and_reports_the_final_session_state()
    {
        await using var context = new PlaybackTestContext();
        context.Handler.RespondAsync = (request, _) => Task.FromResult(
            request.Uri.AbsolutePath == "/emby/Sessions/Playing"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : context.Respond(request));

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));

        Assert.Equal("ServerRejected", error.ErrorCode);
        Assert.Single(context.Engine.Stopped);
        Assert.True(Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("Failed").GetBoolean());
        Assert.Null(context.Coordinator.ActiveContext);
    }

    [Fact(Timeout = 15000)]
    public async Task Ended_event_reports_the_last_position_once_and_allows_replay_from_zero()
    {
        await using var context = new PlaybackTestContext();
        var ended = WaitForStatus(context.Coordinator, PlaybackStatus.Ended);
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(100_000_000), TestContext.Current.CancellationToken);
        var snapshot = context.Engine.Snapshot! with { State = PlaybackEngineState.Ended, PositionTicks = TimeSpan.FromHours(2).Ticks };

        context.Engine.Emit(PlaybackEngineEventKind.Ended, snapshot);
        await ended.WaitAsync(TestContext.Current.CancellationToken);
        context.Engine.Emit(PlaybackEngineEventKind.Ended, snapshot);
        await context.Coordinator.ReplayAsync(TestContext.Current.CancellationToken);

        Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Equal(snapshot.PositionTicks, context.Handler.At("Sessions/Playing/Stopped")[0].JsonBody.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(0, context.Engine.Opened[1].InitialPositionTicks);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
    }

    [Fact(Timeout = 15000)]
    public async Task Disposal_stops_the_active_session_and_disposes_the_owned_engine_once()
    {
        var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);

        await context.DisposeAsync();
        await context.Coordinator.DisposeAsync();

        Assert.True(context.Engine.IsDisposed);
        Assert.Single(context.Engine.Stopped);
        Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));
    }

    internal static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static Task WaitForStatus(PlaybackCoordinator coordinator, PlaybackStatus status)
    {
        var signal = Signal();
        coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == status) signal.TrySetResult();
        };
        return signal.Task;
    }
}
