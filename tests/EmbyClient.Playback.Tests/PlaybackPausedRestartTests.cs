using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackPausedRestartTests
{
    [Theory(Timeout = 15000)]
    [InlineData("Quality")]
    [InlineData("Subtitle")]
    public async Task Changing_a_paused_selection_pauses_the_replacement_engine_at_the_original_position(string changeKind)
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        context.Engine.SetPosition(800_000_009);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var change = changeKind == "Quality"
            ? new PlaybackSelectionChange { MaxStreamingBitrate = 4_000_000 }
            : new PlaybackSelectionChange { SubtitleStreamIndex = 3 };

        await context.Coordinator.ChangeSelectionAsync(change, TestContext.Current.CancellationToken);

        var replacement = context.Engine.Opened[1];
        Assert.Equal(new[] { originalId, replacement.PlaybackId }, context.Engine.Paused);
        Assert.Equal(changeKind == "Quality" ? PlaybackDeliveryMethod.DirectStream : PlaybackDeliveryMethod.Transcode, replacement.DeliveryMethod);
        AssertPausedReplacement(context, "session-2", 800_000_009);
        Assert.Equal(800_000_009, Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Seeking_a_paused_transcode_restores_pause_after_restarting_at_the_requested_absolute_ticks()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        context.Engine.SetPosition(200_000_007);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.SeekAsync(4_200_000_009, TestContext.Current.CancellationToken);

        var replacement = context.Engine.Opened[1];
        Assert.Equal(new[] { originalId, replacement.PlaybackId }, context.Engine.Paused);
        Assert.Equal(0, replacement.InitialPositionTicks);
        Assert.Equal(4_200_000_009, replacement.TimelineOffsetTicks);
        Assert.Empty(context.Engine.Sought);
        AssertPausedReplacement(context, "session-2", 4_200_000_009);
        Assert.Equal(800_000_008, Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task A_paused_selection_change_preserves_pause_through_a_direct_open_failure_and_transcode_fallback()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        context.Engine.SetPosition(800_000_009);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        context.Engine.OnOpenAsync = (_, _) => context.Engine.Opened.Length == 2
            ? Task.FromException(new PlaybackException("UnsupportedFormat")) : Task.CompletedTask;

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MaxStreamingBitrate = 4_000_000 },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, context.Engine.Opened.Length);
        var fallback = context.Engine.Opened[2];
        Assert.Equal(PlaybackDeliveryMethod.Transcode, fallback.DeliveryMethod);
        Assert.Equal(800_000_009, fallback.TimelineOffsetTicks);
        Assert.Equal(new[] { originalId, fallback.PlaybackId }, context.Engine.Paused);
        Assert.DoesNotContain(context.Handler.At("Sessions/Playing"), report => report.JsonBody.GetProperty("PlaySessionId").GetString() == "session-2");
        AssertPausedReplacement(context, "session-3", 800_000_009);
    }

    [Fact(Timeout = 15000)]
    public async Task A_runtime_fallback_after_a_paused_engine_failure_restores_the_pause_intent()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        context.Engine.SetPosition(800_000_009);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var restored = PlaybackLifecycleTests.Signal();
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Paused && args.Context?.PlaySessionId == "session-2") restored.TrySetResult();
        };

        context.Engine.Emit(PlaybackEngineEventKind.Failed, context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "UnsupportedFormat");
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { originalId, context.Engine.Opened[1].PlaybackId }, context.Engine.Paused);
        AssertPausedReplacement(context, "session-2", 800_000_009);
    }

    [Fact(Timeout = 15000)]
    public async Task Replay_of_a_paused_session_starts_playing_from_zero_without_restoring_pause()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.ReplayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(originalId, Assert.Single(context.Engine.Paused));
        Assert.Equal(0, context.Engine.Opened[1].InitialPositionTicks);
        Assert.Equal(PlaybackEngineState.Playing, context.Engine.Snapshot?.State);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        var reports = ReportsFor(context, "session-2");
        var start = Assert.Single(reports);
        Assert.Equal("/emby/Sessions/Playing", start.Uri.AbsolutePath);
        Assert.False(start.JsonBody.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(0, start.JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task A_paused_restart_waits_for_the_actual_paused_event_after_the_native_command_has_completed()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var pauseCommandCompleted = PlaybackLifecycleTests.Signal();
        context.Engine.OnPauseAsync = (_, _) =>
        {
            pauseCommandCompleted.TrySetResult();
            return Task.CompletedTask;
        };

        var change = context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MaxStreamingBitrate = 4_000_000 },
            TestContext.Current.CancellationToken);
        await pauseCommandCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(change.IsCompleted);
        Assert.Equal(PlaybackEngineState.Playing, context.Engine.Snapshot?.State);
        var pendingReport = Assert.Single(ReportsFor(context, "session-2"));
        Assert.Equal("/emby/Sessions/Playing", pendingReport.Uri.AbsolutePath);
        Assert.False(pendingReport.JsonBody.GetProperty("IsPaused").GetBoolean());

        context.Engine.Emit(PlaybackEngineEventKind.StateChanged, context.Engine.Snapshot! with { State = PlaybackEngineState.Paused });
        await change;

        AssertPausedReplacement(context, "session-2", 600_000_001);
        Assert.Equal(context.Engine.Opened[1].PlaybackId, context.Engine.Paused[1]);
    }

    [Fact(Timeout = 15000)]
    public async Task An_unconfirmed_pause_is_bounded_and_releases_the_replacement_transcode_and_live_stream()
    {
        var time = new ManualTimeProvider();
        await using var context = new PlaybackTestContext(time);
        context.Sources = (_, count) => [PlaybackTestContext.Source() with { RequiresClosing = true, LiveStreamId = "live-" + count }];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true), TestContext.Current.CancellationToken);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var pauseCommandCompleted = PlaybackLifecycleTests.Signal();
        context.Engine.OnPauseAsync = (_, _) =>
        {
            pauseCommandCompleted.TrySetResult();
            return Task.CompletedTask;
        };
        var change = context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MaxStreamingBitrate = 4_000_000 },
            TestContext.Current.CancellationToken);
        await pauseCommandCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<PlaybackException>(() => change);

        Assert.Equal("PauseNotConfirmed", error.ErrorCode);
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Equal(context.Engine.Opened.Select(request => request.PlaybackId), context.Engine.Stopped);
        Assert.Equal(new[] { "session-1", "session-2" }, context.Handler.At("Videos/ActiveEncodings").Select(request => request.Query["PlaySessionId"]));
        Assert.Equal(new[] { "live-1", "live-2" }, context.Handler.At("LiveStreams/Close").Select(request => request.Query["LiveStreamId"]));
        var reports = ReportsFor(context, "session-2");
        Assert.Equal(new[] { "/emby/Sessions/Playing", "/emby/Sessions/Playing/Stopped" }, reports.Select(request => request.Uri.AbsolutePath));
        Assert.False(reports[0].JsonBody.GetProperty("IsPaused").GetBoolean());
        Assert.True(reports[1].JsonBody.GetProperty("Failed").GetBoolean());
        Assert.Equal(600_000_001, reports[1].JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    private static void AssertPausedReplacement(PlaybackTestContext context, string sessionId, long positionTicks)
    {
        Assert.Equal(PlaybackEngineState.Paused, context.Engine.Snapshot?.State);
        Assert.Equal(PlaybackStatus.Paused, context.Coordinator.Status);
        Assert.Equal(sessionId, context.Coordinator.ActiveContext?.PlaySessionId);
        Assert.Equal(positionTicks, context.Coordinator.ActiveContext?.PositionTicks);
        var reports = ReportsFor(context, sessionId);
        Assert.True(reports.Length >= 2);
        Assert.Equal("/emby/Sessions/Playing", reports[0].Uri.AbsolutePath);
        Assert.False(reports[0].JsonBody.GetProperty("IsPaused").GetBoolean());
        Assert.Equal("/emby/Sessions/Playing/Progress", reports[1].Uri.AbsolutePath);
        Assert.Equal("Pause", reports[1].JsonBody.GetProperty("EventName").GetString());
        Assert.All(reports.Skip(1), report => Assert.True(report.JsonBody.GetProperty("IsPaused").GetBoolean()));
        Assert.All(reports, report => Assert.Equal(positionTicks, report.JsonBody.GetProperty("PositionTicks").GetInt64()));
    }

    private static RecordedRequest[] ReportsFor(PlaybackTestContext context, string sessionId) => context.Handler.Requests
        .Where(request => request.Uri.AbsolutePath.StartsWith("/emby/Sessions/Playing", StringComparison.Ordinal)
            && request.JsonBody.GetProperty("PlaySessionId").GetString() == sessionId).ToArray();
}
