using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackDisposalTests
{
    [Fact(Timeout = 15000)]
    public async Task Concurrent_and_reentrant_disposals_share_completion_until_the_engine_has_drained()
    {
        await using var context = new PlaybackTestContext(new ManualTimeProvider());
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken);
        var entered = Signal();
        var release = Signal();
        Task? reentrant = null;
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Stopping) reentrant = context.Coordinator.DisposeAsync().AsTask();
        };
        context.Engine.OnDisposeAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        var first = context.Coordinator.DisposeAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = context.Coordinator.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.Same(first, reentrant);
            Assert.False(first.IsCompleted);
            Assert.False(context.Engine.IsDisposed);
            Assert.Equal(1, context.Engine.DisposalCalls);
            release.TrySetResult();
            await Task.WhenAll(first, second, reentrant!).WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(context.Engine.IsDisposed);
            Assert.Equal(PlaybackStatus.Idle, context.Coordinator.Status);
            Assert.Same(first, context.Coordinator.DisposeAsync().AsTask());
            Assert.Single(context.Engine.Stopped);
            Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
            Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
        }
        finally { release.TrySetResult(); }
    }

    [Fact(Timeout = 15000)]
    public async Task Disposals_during_opening_wait_for_cancelled_open_retirement_and_the_same_completion()
    {
        await using var context = new PlaybackTestContext(new ManualTimeProvider());
        var opening = Signal();
        var stopping = Signal();
        var releaseStop = Signal();
        context.Engine.OnOpenAsync = async (_, token) =>
        {
            opening.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        context.Engine.OnStopAsync = async (_, _) =>
        {
            stopping.TrySetResult();
            await releaseStop.Task;
        };
        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken);
        await opening.Task.WaitAsync(TestContext.Current.CancellationToken);
        var first = context.Coordinator.DisposeAsync().AsTask();
        try
        {
            await stopping.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = context.Coordinator.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
            Assert.False(context.Engine.IsDisposed);
            releaseStop.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);
            await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(context.Engine.IsDisposed);
            Assert.Equal(1, context.Engine.DisposalCalls);
            Assert.Empty(context.Handler.At("Sessions/Playing"));
            Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
            Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
        }
        finally { releaseStop.TrySetResult(); }
    }

    [Fact(Timeout = 15000)]
    public async Task Engine_disposal_failure_is_shared_and_cannot_be_reported_as_successful_idle_cleanup()
    {
        var context = new PlaybackTestContext(new ManualTimeProvider());
        var entered = Signal();
        var release = Signal();
        var diagnostics = new List<PlaybackDiagnosticEventArgs>();
        var states = new List<PlaybackStatus>();
        context.Coordinator.Diagnostic += (_, args) => diagnostics.Add(args);
        context.Coordinator.StatusChanged += (_, args) => states.Add(args.Status);
        context.Engine.OnDisposeAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            throw new PlaybackException("NativePlayerCloseFailed");
        };
        try
        {
            var first = context.Coordinator.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = context.Coordinator.DisposeAsync().AsTask();
            Assert.Same(first, second);
            Assert.False(second.IsCompleted);
            release.TrySetResult();
            var firstError = await Assert.ThrowsAsync<PlaybackException>(() => first);
            var secondError = await Assert.ThrowsAsync<PlaybackException>(() => second);

            Assert.Same(firstError, secondError);
            Assert.Equal("EngineDisposeFailed", firstError.ErrorCode);
            Assert.Equal(1, context.Engine.DisposalCalls);
            Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
            Assert.DoesNotContain(PlaybackStatus.Idle, states);
            var diagnostic = Assert.Single(diagnostics);
            Assert.Equal("EngineDispose", diagnostic.Operation);
            Assert.Equal("NativePlayerCloseFailed", diagnostic.ErrorCode);
            Assert.Same(first, context.Coordinator.DisposeAsync().AsTask());
        }
        finally
        {
            release.TrySetResult();
            context.Http.Dispose();
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Engine_disposal_timeout_completes_shutdown_with_a_shared_failure_not_a_false_drain_success()
    {
        var time = new ManualTimeProvider();
        var context = new PlaybackTestContext(time);
        var entered = Signal();
        var release = Signal();
        context.Engine.OnDisposeAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        try
        {
            var first = context.Coordinator.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var second = context.Coordinator.DisposeAsync().AsTask();
            Assert.Same(first, second);
            time.Advance(TimeSpan.FromSeconds(3));
            var error = await Assert.ThrowsAsync<PlaybackException>(() => first.WaitAsync(TestContext.Current.CancellationToken));

            Assert.Equal("EngineDisposeFailed", error.ErrorCode);
            Assert.False(context.Engine.IsDisposed);
            var engineDisposal = context.Engine.DisposalCompletion ?? throw new InvalidOperationException("The engine disposal task was not captured.");
            Assert.False(engineDisposal.IsCompleted);
            Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
            Assert.Same(first, context.Coordinator.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<PlaybackException>(() => second);
            release.TrySetResult();
            await engineDisposal.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(context.Engine.IsDisposed);
            Assert.Same(first, context.Coordinator.DisposeAsync().AsTask());
            var repeated = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.DisposeAsync().AsTask());
            Assert.Same(error, repeated);
        }
        finally
        {
            release.TrySetResult();
            if (context.Engine.DisposalCompletion is { } pending) await pending;
            context.Http.Dispose();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
