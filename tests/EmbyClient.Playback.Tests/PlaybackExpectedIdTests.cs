using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackExpectedIdTests
{
    [Theory(Timeout = 15000)]
    [InlineData("Pause")]
    [InlineData("Resume")]
    [InlineData("Seek")]
    [InlineData("Stop")]
    public async Task A_command_waiting_behind_a_replacement_cannot_control_the_new_playback(string command)
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        var reportEntered = PlaybackLifecycleTests.Signal();
        var releaseReport = PlaybackLifecycleTests.Signal();
        context.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Uri.AbsolutePath == "/emby/Sessions/Playing/Progress"
                && request.JsonBody.GetProperty("EventName").GetString() == "VolumeChange")
            {
                reportEntered.TrySetResult();
                await releaseReport.Task.WaitAsync(cancellationToken);
            }
            return context.Respond(request);
        };
        var volume = context.Coordinator.SetVolumeAsync(50, false, TestContext.Current.CancellationToken);
        await reportEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The new intent is queued first, while the old session still owns _current and the reporting gate.
        var replacement = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(111_000_003), TestContext.Current.CancellationToken);
        var oldCommand = Execute(context.Coordinator, command, originalId, TestContext.Current.CancellationToken);
        Assert.False(oldCommand.IsCompleted);
        releaseReport.TrySetResult();
        await Task.WhenAll(volume, replacement, oldCommand);

        Assert.Equal(2, context.Engine.Opened.Length);
        var replacementId = context.Engine.Opened[1].PlaybackId;
        Assert.NotEqual(originalId, replacementId);
        Assert.Equal(replacementId, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.Equal(PlaybackEngineState.Playing, context.Engine.Snapshot?.State);
        Assert.Equal(111_000_003, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(originalId, Assert.Single(context.Engine.Stopped));
        Assert.Empty(context.Engine.Paused);
        Assert.Empty(context.Engine.Sought);
        Assert.DoesNotContain(context.Handler.At("Sessions/Playing/Progress"),
            request => request.JsonBody.GetProperty("PlaySessionId").GetString() == "session-2");
    }

    [Fact(Timeout = 15000)]
    public async Task An_old_stop_during_a_new_open_does_not_cancel_the_new_session_or_global_play_intent()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        var entered = PlaybackLifecycleTests.Signal();
        var release = PlaybackLifecycleTests.Signal();
        CancellationToken newOpenToken = default;
        context.Engine.OnOpenAsync = async (_, token) =>
        {
            newOpenToken = token;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var replacement = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(222_000_007), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.StopAsync(originalId, TestContext.Current.CancellationToken);

        Assert.False(newOpenToken.IsCancellationRequested);
        Assert.False(replacement.IsCompleted);
        release.TrySetResult();
        await replacement;
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.Equal(context.Engine.Opened[1].PlaybackId, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Equal(originalId, Assert.Single(context.Engine.Stopped));
    }

    [Fact(Timeout = 15000)]
    public async Task Matching_commands_still_control_the_expected_engine_and_report_their_real_effects()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var id = context.Coordinator.ActiveContext!.PlaybackId;

        await context.Coordinator.PauseAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackEngineState.Paused, context.Engine.Snapshot?.State);
        await context.Coordinator.ResumeAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackEngineState.Playing, context.Engine.Snapshot?.State);
        await context.Coordinator.SeekAsync(id, 333_000_009, TestContext.Current.CancellationToken);
        await context.Coordinator.StopAsync(id, TestContext.Current.CancellationToken);

        Assert.Equal(id, Assert.Single(context.Engine.Paused));
        Assert.Equal((id, 333_000_009L), Assert.Single(context.Engine.Sought));
        Assert.Equal(id, Assert.Single(context.Engine.Stopped));
        Assert.Equal(new[] { "Pause", "Unpause", "TimeUpdate" }, context.Handler.At("Sessions/Playing/Progress")
            .Select(request => request.JsonBody.GetProperty("EventName").GetString()));
        Assert.Equal(333_000_009, Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("PositionTicks").GetInt64());
        Assert.Null(context.Coordinator.ActiveContext);
    }

    [Fact(Timeout = 15000)]
    public async Task Missing_foreign_and_retired_playback_ids_are_silent_no_ops()
    {
        await using var context = new PlaybackTestContext();
        var unknown = Guid.NewGuid();
        await ExecuteAll(context.Coordinator, unknown, TestContext.Current.CancellationToken);
        Assert.Empty(context.Engine.Opened);
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var id = context.Coordinator.ActiveContext!.PlaybackId;
        await ExecuteAll(context.Coordinator, unknown, TestContext.Current.CancellationToken);
        Assert.Equal(id, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Empty(context.Engine.Paused);
        Assert.Empty(context.Engine.Sought);
        Assert.Empty(context.Engine.Stopped);
        await context.Coordinator.StopAsync(id, TestContext.Current.CancellationToken);
        await ExecuteAll(context.Coordinator, id, TestContext.Current.CancellationToken);
        Assert.Equal(id, Assert.Single(context.Engine.Stopped));
        Assert.Empty(context.Handler.At("Sessions/Playing/Progress"));
    }

    [Fact(Timeout = 15000)]
    public async Task Media_commands_for_a_disposed_coordinator_are_silent_while_unscoped_controls_keep_their_disposal_error()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var id = context.Coordinator.ActiveContext!.PlaybackId;
        await context.Coordinator.DisposeAsync();

        await ExecuteAll(context.Coordinator, id, CancellationToken.None);
        await context.Coordinator.SeekAsync(id, -1, CancellationToken.None);

        Assert.Equal(id, Assert.Single(context.Engine.Stopped));
        Assert.Empty(context.Engine.Paused);
        Assert.Empty(context.Engine.Sought);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.Coordinator.PauseAsync(TestContext.Current.CancellationToken));
    }

    private static Task Execute(PlaybackCoordinator coordinator, string command, Guid id, CancellationToken token) => command switch
    {
        "Pause" => coordinator.PauseAsync(id, token),
        "Resume" => coordinator.ResumeAsync(id, token),
        "Seek" => coordinator.SeekAsync(id, 900_000_001, token),
        "Stop" => coordinator.StopAsync(id, token),
        _ => throw new ArgumentOutOfRangeException(nameof(command))
    };

    private static async Task ExecuteAll(PlaybackCoordinator coordinator, Guid id, CancellationToken token)
    {
        foreach (var command in new[] { "Pause", "Resume", "Seek", "Stop" })
            await Execute(coordinator, command, id, token);
    }
}
