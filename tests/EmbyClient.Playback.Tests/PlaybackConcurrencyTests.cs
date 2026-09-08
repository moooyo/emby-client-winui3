using System.Net;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackConcurrencyTests
{


    [Fact(Timeout = 15000)]
    public async Task Cancellation_during_start_reporting_finishes_that_report_before_stopping()
    {
        await using var context = new PlaybackTestContext();
        using var owner = new CancellationTokenSource();
        var entered = PlaybackLifecycleTests.Signal();
        var release = PlaybackLifecycleTests.Signal();
        CancellationToken reportToken = default;
        context.Handler.RespondAsync = async (request, token) =>
        {
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing")
            {
                reportToken = token;
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return context.Respond(request);
        };
        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), owner.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        owner.Cancel();
        Assert.False(reportToken.IsCancellationRequested);
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);

        var reports = context.Handler.Requests.Where(request => request.Uri.AbsolutePath.Contains("/Sessions/Playing", StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "/emby/Sessions/Playing", "/emby/Sessions/Playing/Stopped" }, reports.Select(request => request.Uri.AbsolutePath));
        Assert.Single(context.Engine.Stopped);
        Assert.Null(context.Coordinator.ActiveContext);
    }

    [Fact(Timeout = 15000)]
    public async Task Changing_selection_during_start_reporting_replaces_the_original_session_at_its_current_position()
    {
        await using var context = new PlaybackTestContext();
        var entered = PlaybackLifecycleTests.Signal();
        var release = PlaybackLifecycleTests.Signal();
        var reportsStarted = 0;
        context.Handler.RespondAsync = async (request, token) =>
        {
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing" && Interlocked.Increment(ref reportsStarted) == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return context.Respond(request);
        };
        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var change = context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MaxStreamingBitrate = 5_000_000 }, TestContext.Current.CancellationToken);
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
        await change;

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(600_000_001, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(5_000_000, context.Coordinator.ActiveContext?.Selection.MaxStreamingBitrate);
        Assert.Equal("session-2", context.Coordinator.ActiveContext?.PlaySessionId);
    }

    [Fact(Timeout = 15000)]
    public async Task Runtime_fallback_keeps_the_callers_cancellation_scope()
    {
        await using var context = new PlaybackTestContext();
        using var owner = new CancellationTokenSource();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), owner.Token);
        var replacementStarted = PlaybackLifecycleTests.Signal();
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Playing && args.Context?.PlaySessionId == "session-2")
                replacementStarted.TrySetResult();
        };
        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed, PositionTicks = 123_456_789 }, "UnsupportedFormat");
        await replacementStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var stopped = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Idle);

        owner.Cancel();
        await stopped.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Equal(2, context.Engine.Stopped.Length);
        Assert.Equal(2, context.Handler.At("Sessions/Playing/Stopped").Length);
        Assert.Equal("session-2", Assert.Single(context.Handler.At("Videos/ActiveEncodings")).Query["PlaySessionId"]);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_native_stop_blocks_new_playback_until_the_same_engine_id_can_be_released()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { RequiresClosing = true, LiveStreamId = "owned-live" }];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken);
        var oldId = context.Coordinator.ActiveContext!.PlaybackId;
        context.Engine.OnStopAsync = (_, _) => throw new PlaybackException("EngineStopFailed");

        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));

        Assert.Equal("EngineStopFailed", error.ErrorCode);
        Assert.Equal(new[] { oldId, oldId }, context.Engine.Stopped);
        Assert.Single(context.Engine.Opened);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
        Assert.Single(context.Handler.At("LiveStreams/Close"));

        context.Engine.OnStopAsync = null;
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { oldId, oldId, oldId }, context.Engine.Stopped);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
    }

    [Fact(Timeout = 15000)]
    public async Task Periodic_progress_samples_the_current_player_position_on_the_original_timeline()
    {
        var time = new ManualTimeProvider();
        await using var context = new PlaybackTestContext(time, TimeSpan.FromSeconds(10));
        var progress = PlaybackLifecycleTests.Signal();
        context.Handler.RespondAsync = (request, _) =>
        {
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing/Progress") progress.TrySetResult();
            return Task.FromResult(context.Respond(request));
        };
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true), TestContext.Current.CancellationToken);
        context.Engine.SetPosition(800_000_008);

        time.Advance(TimeSpan.FromSeconds(10));
        await progress.Task.WaitAsync(TestContext.Current.CancellationToken);
        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);

        var report = Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody;
        Assert.Equal("TimeUpdate", report.GetProperty("EventName").GetString());
        Assert.Equal(800_000_008, report.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(800_000_008, Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task A_native_stop_that_ignores_cancellation_is_bounded_and_cannot_block_server_cleanup()
    {
        var time = new ManualTimeProvider();
        await using var context = new PlaybackTestContext(time);
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { RequiresClosing = true, LiveStreamId = "owned-live" }];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken);
        var stopEntered = PlaybackLifecycleTests.Signal();
        var releaseNativeStop = PlaybackLifecycleTests.Signal();
        context.Engine.OnStopAsync = async (_, _) =>
        {
            stopEntered.TrySetResult();
            await releaseNativeStop.Task;
        };

        try
        {
            var stop = context.Coordinator.StopAsync(TestContext.Current.CancellationToken);
            await stopEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(3));
            await stop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
            Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
            Assert.Equal("owned-live", Assert.Single(context.Handler.At("LiveStreams/Close")).Query["LiveStreamId"]);
            Assert.Null(context.Coordinator.ActiveContext);
            Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
        }
        finally
        {
            releaseNativeStop.TrySetResult();
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Queued_pause_and_unpause_events_preserve_each_state_while_another_report_is_in_flight()
    {
        await using var context = new PlaybackTestContext();
        var firstReportEntered = PlaybackLifecycleTests.Signal();
        var releaseFirstReport = PlaybackLifecycleTests.Signal();
        var unpauseReported = PlaybackLifecycleTests.Signal();
        context.Handler.RespondAsync = async (request, token) =>
        {
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing/Progress")
            {
                var eventName = request.JsonBody.GetProperty("EventName").GetString();
                if (eventName == "VolumeChange")
                {
                    firstReportEntered.TrySetResult();
                    await releaseFirstReport.Task.WaitAsync(token);
                }
                if (eventName == "Unpause") unpauseReported.TrySetResult();
            }
            return context.Respond(request);
        };
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var volume = context.Coordinator.SetVolumeAsync(50, false, TestContext.Current.CancellationToken);
        await firstReportEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var snapshot = context.Engine.Snapshot!;
        context.Engine.Emit(PlaybackEngineEventKind.StateChanged, snapshot with { State = PlaybackEngineState.Paused, PositionTicks = 100_000_003 });
        context.Engine.Emit(PlaybackEngineEventKind.StateChanged, snapshot with { State = PlaybackEngineState.Playing, PositionTicks = 100_000_009 });
        releaseFirstReport.TrySetResult();
        await volume;
        await unpauseReported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);

        var reports = context.Handler.At("Sessions/Playing/Progress").Select(request => request.JsonBody).ToArray();
        Assert.Equal(new[] { "VolumeChange", "Pause", "Unpause" }, reports.Select(report => report.GetProperty("EventName").GetString()));
        Assert.True(reports[1].GetProperty("IsPaused").GetBoolean());
        Assert.False(reports[2].GetProperty("IsPaused").GetBoolean());
        Assert.Equal(100_000_003, reports[1].GetProperty("PositionTicks").GetInt64());
        Assert.Equal(100_000_009, reports[2].GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task A_pause_event_during_start_reporting_is_reported_after_the_original_start_snapshot()
    {
        await using var context = new PlaybackTestContext();
        var startEntered = PlaybackLifecycleTests.Signal();
        var releaseStart = PlaybackLifecycleTests.Signal();
        var pauseReported = PlaybackLifecycleTests.Signal();
        context.Handler.RespondAsync = async (request, token) =>
        {
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing")
            {
                startEntered.TrySetResult();
                await releaseStart.Task.WaitAsync(token);
            }
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing/Progress") pauseReported.TrySetResult();
            return context.Respond(request);
        };
        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);
        await startEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        context.Engine.Emit(PlaybackEngineEventKind.StateChanged,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Paused, PositionTicks = 610_000_007 });
        releaseStart.TrySetResult();
        await play;
        await pauseReported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);

        var started = Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody;
        var progress = Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody;
        Assert.False(started.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(600_000_001, started.GetProperty("PositionTicks").GetInt64());
        Assert.Equal("Pause", progress.GetProperty("EventName").GetString());
        Assert.True(progress.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(610_000_007, progress.GetProperty("PositionTicks").GetInt64());
    }
}
