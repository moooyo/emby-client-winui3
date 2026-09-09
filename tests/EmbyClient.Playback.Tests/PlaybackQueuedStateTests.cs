using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackQueuedStateTests
{
    [Theory(Timeout = 15000)]
    [InlineData(PlaybackEngineState.Seeking)]
    [InlineData(PlaybackEngineState.Paused)]
    public async Task State_events_queued_before_seek_completion_cannot_replay_the_pre_seek_position(PlaybackEngineState queuedState)
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var seekEntered = PlaybackLifecycleTests.Signal();
        var releaseSeek = PlaybackLifecycleTests.Signal();
        context.Engine.OnSeekAsync = async (_, _, token) =>
        {
            seekEntered.TrySetResult();
            await releaseSeek.Task.WaitAsync(token);
        };
        var seek = context.Coordinator.SeekAsync(450_660_000, TestContext.Current.CancellationToken);
        await seekEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var drained = WaitForPositionNotifications(context.Coordinator, 450_660_000, 2);

        context.Engine.Emit(PlaybackEngineEventKind.StateChanged,
            context.Engine.Snapshot! with { State = queuedState, PositionTicks = 2_660_000 });
        context.Engine.Emit(PlaybackEngineEventKind.PositionChanged,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Playing, PositionTicks = 450_660_000 });
        releaseSeek.TrySetResult();
        await seek;
        await drained.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var forwardReports = context.Handler.At("Sessions/Playing/Progress");
        Assert.NotEmpty(forwardReports);
        Assert.Equal("TimeUpdate", forwardReports[0].JsonBody.GetProperty("EventName").GetString());
        Assert.All(forwardReports, report => Assert.Equal(450_660_000, report.JsonBody.GetProperty("PositionTicks").GetInt64()));
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);

        context.Engine.OnSeekAsync = null;
        await context.Coordinator.SeekAsync(50_000_000, TestContext.Current.CancellationToken);

        var backward = context.Handler.At("Sessions/Playing/Progress")[^1].JsonBody;
        Assert.Equal("TimeUpdate", backward.GetProperty("EventName").GetString());
        Assert.Equal(50_000_000, backward.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(50_000_000, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(new long[] { 450_660_000, 50_000_000 }, context.Engine.Sought.Select(request => request.PositionTicks));
    }

    internal static Task WaitForPositionNotifications(PlaybackCoordinator coordinator, long positionTicks, int expectedNotifications)
    {
        var completed = PlaybackLifecycleTests.Signal();
        var notifications = 0;
        coordinator.StatusChanged += (_, args) =>
        {
            if (args.Context?.PositionTicks == positionTicks && Interlocked.Increment(ref notifications) == expectedNotifications)
                completed.TrySetResult();
        };
        return completed.Task;
    }
}
