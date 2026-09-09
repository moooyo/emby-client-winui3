using System.Collections.Concurrent;
using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.MediaTransport.Tests;

public sealed class UpstreamFailureNotificationTests
{
    [Fact]
    public void A_connection_cancelled_before_commit_does_not_consume_the_session_notification()
    {
        var received = new List<string>();
        var owner = new UpstreamFailureNotificationOwner(CancellationToken.None, received.Add);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Null(owner.TryCommit("NetworkFailure", cancelled.Token));
        Assert.Empty(received);
        var survivingConnection = Assert.IsType<CommittedUpstreamFailure>(owner.TryCommit("NotAllowed", CancellationToken.None));
        survivingConnection.Deliver();

        Assert.Equal("NotAllowed", Assert.Single(received));
    }

    [Fact]
    public void Cancellation_between_commit_and_delivery_cannot_drop_the_winner_or_let_another_connection_replace_it()
    {
        var received = new List<string>();
        var owner = new UpstreamFailureNotificationOwner(CancellationToken.None, received.Add);
        using var connection = new CancellationTokenSource();

        // Hold the committed notification at the exact boundary that previously lost the callback.
        var committed = Assert.IsType<CommittedUpstreamFailure>(owner.TryCommit("NetworkFailure", connection.Token));
        connection.Cancel();
        Assert.Null(owner.TryCommit("AuthenticationRequired", CancellationToken.None));
        Assert.Empty(received);
        committed.Deliver();
        committed.Deliver();

        Assert.Equal("NetworkFailure", Assert.Single(received));
    }

    [Fact(Timeout = 15000)]
    public async Task Concurrent_connections_commit_exactly_one_notification()
    {
        var received = new ConcurrentQueue<string>();
        var owner = new UpstreamFailureNotificationOwner(CancellationToken.None, received.Enqueue);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CommittedUpstreamFailure?> Attempt(string code) => Task.Run(async () =>
        {
            await start.Task;
            return owner.TryCommit(code, CancellationToken.None);
        });
        var first = Attempt("NetworkFailure");
        var second = Attempt("AuthenticationRequired");
        start.SetResult();

        var results = await Task.WhenAll(first, second).WaitAsync(TestContext.Current.CancellationToken);
        var committed = Assert.Single(results, value => value is not null);
        committed!.Deliver();

        Assert.Single(received);
        Assert.Null(owner.TryCommit("NotAllowed", CancellationToken.None));
    }

    [Fact]
    public void Unsupported_codes_and_shutdown_before_commit_cannot_notify_or_poison_a_live_owner()
    {
        var received = new List<string>();
        var owner = new UpstreamFailureNotificationOwner(CancellationToken.None, received.Add);
        Assert.Null(owner.TryCommit("Cancelled", CancellationToken.None));
        Assert.Null(owner.TryCommit("RangeNotSatisfiable", CancellationToken.None));
        var committed = Assert.IsType<CommittedUpstreamFailure>(owner.TryCommit("UnsupportedFormat", CancellationToken.None));
        committed.Deliver();
        Assert.Equal("UnsupportedFormat", Assert.Single(received));

        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        var closed = new UpstreamFailureNotificationOwner(shutdown.Token, received.Add);
        Assert.Null(closed.TryCommit("NetworkFailure", CancellationToken.None));
        Assert.Single(received);
    }

    [Fact]
    public void A_throwing_or_reentrant_consumer_cannot_repeat_a_committed_failure()
    {
        var calls = 0;
        CommittedUpstreamFailure? reentrant = null;
        UpstreamFailureNotificationOwner? owner = null;
        owner = new UpstreamFailureNotificationOwner(CancellationToken.None, _ =>
        {
            calls++;
            reentrant = owner!.TryCommit("NotAllowed", CancellationToken.None);
            throw new InvalidOperationException("The observer rejected its notification.");
        });
        var committed = Assert.IsType<CommittedUpstreamFailure>(owner.TryCommit("NetworkFailure", CancellationToken.None));

        committed.Deliver();
        committed.Deliver();

        Assert.Equal(1, calls);
        Assert.Null(reentrant);
        Assert.Null(owner.TryCommit("AuthenticationRequired", CancellationToken.None));
    }
}
