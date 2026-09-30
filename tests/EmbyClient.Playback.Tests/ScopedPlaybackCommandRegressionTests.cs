using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class ScopedPlaybackCommandRegressionTests
{
    [Theory(Timeout = 15000)]
    [InlineData("Pause")]
    [InlineData("Resume")]
    [InlineData("RelativeSeek")]
    public async Task A_command_captured_before_replacement_cannot_control_B_after_waiting_for_the_engine_gate(string command)
    {
        await using var context = new PlaybackTestContext();
        var token = TestContext.Current.CancellationToken;
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(100_000_000), token);
        var captured = context.Coordinator.ActiveContext!;
        var entered = PlaybackLifecycleTests.Signal();
        var release = PlaybackLifecycleTests.Signal();
        context.Engine.OnPauseAsync = async (id, _) =>
        {
            if (id != captured.PlaybackId) return;
            entered.TrySetResult();
            await release.Task;
        };

        // A native command already in flight can hold the gate until the engine completes it.
        var holdingPause = context.Coordinator.PauseAsync(captured.PlaybackId, token);
        await entered.Task.WaitAsync(token);
        var replacement = context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(111_000_003) with { ItemId = "movie-b" }, token);
        var delayedCommand = ExecuteScoped(context.Coordinator, captured, command, token);
        try
        {
            Assert.False(replacement.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(holdingPause, replacement, delayedCommand);

        var current = context.Coordinator.ActiveContext!;
        Assert.Equal("movie-b", current.Selection.ItemId);
        Assert.NotEqual(captured.PlaybackId, current.PlaybackId);
        Assert.Equal(current.PlaybackId, context.Engine.Snapshot?.PlaybackId);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.Equal(PlaybackEngineState.Playing, context.Engine.Snapshot?.State);
        Assert.Equal(111_000_003, current.PositionTicks);
        Assert.Equal(captured.PlaybackId, Assert.Single(context.Engine.Paused));
        Assert.Empty(context.Engine.Sought);
        Assert.DoesNotContain(context.Handler.At("Sessions/Playing/Progress"),
            request => request.JsonBody.GetProperty("PlaySessionId").GetString() == "session-2");
    }

    [Theory(Timeout = 15000)]
    [InlineData("Pause", PlaybackEngineState.Paused, "Pause")]
    [InlineData("Resume", PlaybackEngineState.Playing, "Unpause")]
    [InlineData("RelativeSeek", PlaybackEngineState.Playing, "TimeUpdate")]
    public async Task A_scoped_command_still_controls_and_reports_its_matching_playback(
        string command, PlaybackEngineState expectedState, string expectedReport)
    {
        await using var context = new PlaybackTestContext();
        var token = TestContext.Current.CancellationToken;
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(100_000_000), token);
        var captured = context.Coordinator.ActiveContext!;
        if (command == "Resume")
        {
            await context.Coordinator.PauseAsync(captured.PlaybackId, token);
            Assert.Equal(PlaybackEngineState.Paused, context.Engine.Snapshot?.State);
        }

        await ExecuteScoped(context.Coordinator, captured, command, token);

        Assert.Equal(captured.PlaybackId, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Equal(expectedState, context.Engine.Snapshot?.State);
        var report = context.Handler.At("Sessions/Playing/Progress").Last();
        Assert.Equal(expectedReport, report.JsonBody.GetProperty("EventName").GetString());
        Assert.Equal("session-1", report.JsonBody.GetProperty("PlaySessionId").GetString());
        if (command == "RelativeSeek")
        {
            var target = captured.PositionTicks + 30 * TimeSpan.TicksPerSecond;
            Assert.Equal((captured.PlaybackId, target), Assert.Single(context.Engine.Sought));
            Assert.Equal(target, context.Coordinator.ActiveContext?.PositionTicks);
        }
        else Assert.Equal(captured.PlaybackId, Assert.Single(context.Engine.Paused));
    }

    private static Task ExecuteScoped(PlaybackCoordinator coordinator, PlaybackContext captured,
        string command, CancellationToken token) => command switch
    {
        "Pause" => coordinator.PauseAsync(captured.PlaybackId, token),
        "Resume" => coordinator.ResumeAsync(captured.PlaybackId, token),
        "RelativeSeek" => coordinator.SeekAsync(captured.PlaybackId,
            Math.Max(0, checked(captured.PositionTicks + 30 * TimeSpan.TicksPerSecond)), token),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };
}
