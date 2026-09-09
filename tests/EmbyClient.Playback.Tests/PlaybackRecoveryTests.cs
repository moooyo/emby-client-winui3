using System.Net;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackRecoveryTests
{
    [Fact(Timeout = 15000)]
    public async Task Retrying_a_network_interruption_renegotiates_the_actual_selection_after_old_cleanup()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) => [PlaybackTestContext.ProgressiveSource() with { RequiresClosing = true, LiveStreamId = "live-" + count }];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001) with
        {
            MediaSourceId = "source-a", AudioStreamIndex = 2, SubtitleStreamIndex = -1, MaxStreamingBitrate = 4_000_000
        }, TestContext.Current.CancellationToken);
        var oldId = context.Coordinator.ActiveContext!.PlaybackId;
        var cleanupEntered = PlaybackLifecycleTests.Signal();
        var releaseCleanup = PlaybackLifecycleTests.Signal();
        context.Engine.OnStopAsync = async (_, token) =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task.WaitAsync(token);
        };
        var failed = FailureSignal(context);
        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed, PositionTicks = 120_000_007 }, "NetworkFailure");
        await cleanupEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Null(context.Coordinator.Recovery);
        releaseCleanup.TrySetResult();
        var failure = await failed.WaitAsync(TestContext.Current.CancellationToken);

        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.Equal(recovery, failure.Recovery);
        Assert.True(context.Coordinator.CanRetry);
        Assert.Equal(oldId, recovery.FailedPlaybackId);
        Assert.Equal(720_000_008, recovery.Selection.StartPositionTicks);
        Assert.Equal("source-a", recovery.Selection.MediaSourceId);
        Assert.Equal(2, recovery.Selection.AudioStreamIndex);
        Assert.Equal(-1, recovery.Selection.SubtitleStreamIndex);
        Assert.Equal(4_000_000, recovery.Selection.MaxStreamingBitrate);
        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        Assert.Null(context.Coordinator.Recovery);
        Assert.False(context.Coordinator.CanRetry);
        Assert.NotEqual(oldId, context.Coordinator.ActiveContext?.PlaybackId);
        Assert.Equal("session-2", context.Coordinator.ActiveContext?.PlaySessionId);
        Assert.Equal(720_000_008, context.Coordinator.ActiveContext?.PositionTicks);
        var requests = context.Handler.Requests;
        var negotiationIndexes = requests.Select((request, index) => (request, index))
            .Where(value => value.request.Uri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)).Select(value => value.index).ToArray();
        Assert.Equal(2, negotiationIndexes.Length);
        var secondNegotiation = negotiationIndexes[1];
        Assert.True(Array.FindIndex(requests, request => request.Uri.AbsolutePath == "/emby/Sessions/Playing/Stopped") < secondNegotiation);
        Assert.True(Array.FindIndex(requests, request => request.Uri.AbsolutePath == "/emby/Videos/ActiveEncodings") < secondNegotiation);
        Assert.True(Array.FindIndex(requests, request => request.Uri.AbsolutePath == "/emby/LiveStreams/Close") < secondNegotiation);
        Assert.Equal(720_000_008, requests[secondNegotiation].JsonBody.GetProperty("StartTimeTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Retry_restores_pause_and_position_but_replay_explicitly_starts_from_zero()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.ProgressiveSource()];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true), TestContext.Current.CancellationToken);
        context.Engine.SetPosition(120_000_007);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var failed = FailureSignal(context);
        context.Engine.Emit(PlaybackEngineEventKind.Failed, context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "NetworkFailure");
        await failed.WaitAsync(TestContext.Current.CancellationToken);
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.True(recovery.IsPaused);

        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", 720_000_008);
        Assert.Equal(context.Engine.Opened[1].PlaybackId, context.Engine.Paused[1]);
        await context.Coordinator.ReplayAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.Equal(0, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(2, context.Engine.Paused.Length);
        Assert.False(context.Handler.At("Sessions/Playing")[2].JsonBody.GetProperty("IsPaused").GetBoolean());
    }

    [Theory(Timeout = 15000)]
    [InlineData("NetworkFailure")]
    [InlineData("Timeout")]
    [InlineData("ServerUnavailable")]
    public async Task A_recoverable_opening_failure_preserves_requested_resume_instead_of_native_initial_zero(string errorCode)
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException(errorCode);
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.Equal(600_000_001, recovery.Selection.StartPositionTicks);
        Assert.False(recovery.IsPaused);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        Assert.Equal(600_000_001, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.Equal(600_000_001, Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Theory(Timeout = 15000)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task A_confirmed_transient_http_failure_can_be_explicitly_retried(HttpStatusCode status)
    {
        await using var context = new PlaybackTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(status));
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(600_000_001) with { MediaSourceId = "source-a" }, TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.Equal("ServerRejected", recovery.ErrorCode);
        Assert.Equal("source-a", recovery.Selection.MediaSourceId);
        context.Handler.RespondAsync = (request, _) => Task.FromResult(context.Respond(request));

        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Single(context.Engine.Opened);
        Assert.Equal(600_000_001, context.Coordinator.ActiveContext?.PositionTicks);
    }

    [Theory(Timeout = 15000)]
    [InlineData("Unauthorized")]
    [InlineData("Forbidden")]
    [InlineData("Cancelled")]
    [InlineData("UnsupportedFormat")]
    [InlineData("ServerRejected")]
    public async Task Authentication_permission_cancellation_and_unclassified_failures_do_not_offer_retry(string failureCase)
    {
        await using var context = new PlaybackTestContext();
        if (failureCase is "Unauthorized" or "Forbidden")
            context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(
                failureCase == "Unauthorized" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden));
        else
            context.Engine.OnOpenAsync = (_, _) => failureCase == "Cancelled"
                ? Task.FromException(new OperationCanceledException()) : Task.FromException(new PlaybackException(failureCase));

        Assert.NotNull(await Record.ExceptionAsync(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken)));
        var requestCount = context.Handler.Requests.Length;
        await context.Coordinator.RetryAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Null(context.Coordinator.Recovery);
        Assert.False(context.Coordinator.CanRetry);
        Assert.Equal(requestCount, context.Handler.Requests.Length);
    }

    [Fact(Timeout = 15000)]
    public async Task A_retry_target_is_single_use_and_repeated_failure_requires_a_new_explicit_retry()
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));
        var first = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.Single(context.Engine.Opened);
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.RetryAsync(first.RecoveryId, TestContext.Current.CancellationToken));
        var second = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.NotEqual(first.RecoveryId, second.RecoveryId);
        Assert.Equal(2, context.Engine.Opened.Length);

        await context.Coordinator.RetryAsync(first.RecoveryId, TestContext.Current.CancellationToken);
        Assert.Equal(second, context.Coordinator.Recovery);
        Assert.Equal(2, context.Engine.Opened.Length);
        context.Engine.OnOpenAsync = null;
        await Task.WhenAll(context.Coordinator.RetryAsync(second.RecoveryId, TestContext.Current.CancellationToken),
            context.Coordinator.RetryAsync(second.RecoveryId, TestContext.Current.CancellationToken));
        Assert.Equal(3, context.Engine.Opened.Length);
        Assert.Null(context.Coordinator.Recovery);
    }

    [Fact(Timeout = 15000)]
    public async Task An_old_retry_queued_before_a_new_play_request_cannot_cancel_or_replace_the_new_playback()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var scheduled = PlaybackLifecycleTests.Signal();
        Task? retry = null;
        Task? newPlay = null;
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status != PlaybackStatus.Failed) return;
            var recovery = context.Coordinator.Recovery;
            if (recovery is null) { scheduled.TrySetResult(); return; }
            retry = context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);
            newPlay = context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { ItemId = "movie-b" }, TestContext.Current.CancellationToken);
            scheduled.TrySetResult();
        };
        context.Engine.Emit(PlaybackEngineEventKind.Failed, context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "NetworkFailure");
        await scheduled.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(retry);
        Assert.NotNull(newPlay);

        await Task.WhenAll(retry, newPlay);

        Assert.Equal("movie-b", context.Coordinator.ActiveContext?.Selection.ItemId);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Single(context.Handler.At("Items/movie-b/PlaybackInfo"));
        Assert.Null(context.Coordinator.Recovery);
    }

    [Theory(Timeout = 15000)]
    [InlineData("Stop")]
    [InlineData("ScopedStop")]
    [InlineData("Dispose")]
    [InlineData("Cancel")]
    public async Task Stop_dispose_and_owner_cancellation_invalidate_saved_recovery(string action)
    {
        await using var context = new PlaybackTestContext();
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), owner.Token));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        if (action == "Stop") await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);
        else if (action == "ScopedStop") await context.Coordinator.StopAsync(recovery.FailedPlaybackId, TestContext.Current.CancellationToken);
        else if (action == "Dispose") await context.Coordinator.DisposeAsync();
        else owner.Cancel();
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        Assert.Null(context.Coordinator.Recovery);
        Assert.False(context.Coordinator.CanRetry);
        Assert.Single(context.Engine.Opened);
        Assert.Null(context.Coordinator.ActiveContext);
    }

    private static Task<PlaybackStatusChangedEventArgs> FailureSignal(PlaybackTestContext context)
    {
        var signal = new TaskCompletionSource<PlaybackStatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Failed) signal.TrySetResult(args);
        };
        return signal.Task;
    }
}
