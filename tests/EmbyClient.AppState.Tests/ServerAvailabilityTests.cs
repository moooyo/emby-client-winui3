using System.Net;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.AppState.Tests;

public sealed class ServerAvailabilityTests
{
    [Fact]
    public async Task Manual_retry_recovers_cached_offline_state_without_concurrent_probes()
    {
        var calls = 0;
        var offline = Completion();
        var online = Completion();
        await using var monitor = new ServerAvailabilityMonitor(_ =>
        {
            return Interlocked.Increment(ref calls) == 1
                ? Task.FromException<ServerAvailabilityStatus>(new EmbyTransportException("Unavailable."))
                : Task.FromResult(ServerAvailabilityStatus.Online);
        }, Timeout.InfiniteTimeSpan) { AutoReconnect = false };
        monitor.StatusChanged += (_, args) =>
        {
            if (args.Status == ServerAvailabilityStatus.Offline) offline.TrySetResult();
            if (args.Status == ServerAvailabilityStatus.Online)
            {
                Assert.True(args.WasOffline);
                online.TrySetResult();
            }
        };
        monitor.Start(TestContext.Current.CancellationToken);
        await Await(offline.Task);
        Assert.Equal(1, calls);
        monitor.RequestProbe();
        await Await(online.Task);
        Assert.Equal(2, calls);
        Assert.Equal(ServerAvailabilityStatus.Online, monitor.Status);
    }

    [Fact]
    public async Task A_successful_probe_confirms_recovery_work_even_when_online_status_does_not_change()
    {
        var probes = 0;
        var transitions = 0;
        var confirmedTwice = Completion();
        await using var monitor = new ServerAvailabilityMonitor(_ => Task.FromResult(ServerAvailabilityStatus.Online),
            Timeout.InfiniteTimeSpan);
        monitor.StatusChanged += (_, args) =>
        {
            if (args.Status == ServerAvailabilityStatus.Online) Interlocked.Increment(ref transitions);
        };
        monitor.OnlineConfirmed += (_, _) =>
        {
            if (Interlocked.Increment(ref probes) == 2) confirmedTwice.TrySetResult();
        };
        monitor.Start(TestContext.Current.CancellationToken);
        monitor.RequestProbe();
        await Await(confirmedTwice.Task);
        Assert.Equal(1, transitions);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task Enabling_automatic_reconnect_wakes_a_paused_offline_monitor()
    {
        var calls = 0;
        var offline = Completion();
        var online = Completion();
        await using var monitor = new ServerAvailabilityMonitor(_ => Task.FromResult(
            Interlocked.Increment(ref calls) == 1 ? ServerAvailabilityStatus.Offline : ServerAvailabilityStatus.Online),
            Timeout.InfiniteTimeSpan) { AutoReconnect = false };
        monitor.StatusChanged += (_, args) =>
        {
            if (args.Status == ServerAvailabilityStatus.Offline) offline.TrySetResult();
            if (args.Status == ServerAvailabilityStatus.Online) online.TrySetResult();
        };
        monitor.Start(TestContext.Current.CancellationToken);
        await Await(offline.Task);
        monitor.AutoReconnect = true;
        await Await(online.Task);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ServerAvailabilityStatus.AuthenticationRequired)]
    [InlineData(HttpStatusCode.Forbidden, ServerAvailabilityStatus.AccountRestricted)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ServerAvailabilityStatus.Offline)]
    public async Task Health_response_classifies_authentication_and_server_failures(HttpStatusCode response,
        ServerAvailabilityStatus expected)
    {
        var changed = Completion();
        await using var monitor = new ServerAvailabilityMonitor(
            _ => Task.FromException<ServerAvailabilityStatus>(new EmbyApiException(response)), Timeout.InfiniteTimeSpan)
        { AutoReconnect = false };
        monitor.StatusChanged += (_, args) => { if (args.Status == expected) changed.TrySetResult(); };
        monitor.Start(TestContext.Current.CancellationToken);
        await Await(changed.Task);
        Assert.Equal(expected, monitor.Status);
    }

    [Fact]
    public async Task Parental_control_rejection_preserves_the_distinction_from_an_expired_token()
    {
        var changed = Completion();
        await using var monitor = new ServerAvailabilityMonitor(_ =>
            Task.FromException<ServerAvailabilityStatus>(new EmbyApiException(HttpStatusCode.Unauthorized, "ParentalControl")))
        { AutoReconnect = false };
        monitor.StatusChanged += (_, args) =>
        {
            if (args.Status == ServerAvailabilityStatus.AccountRestricted) changed.TrySetResult();
        };
        monitor.Start(TestContext.Current.CancellationToken);
        await Await(changed.Task);
        Assert.Equal(ServerAvailabilityStatus.AccountRestricted, monitor.Status);
    }

    [Fact]
    public async Task Protocol_failure_waits_for_explicit_retry_without_erasing_the_cached_view()
    {
        var calls = 0;
        var incompatible = Completion();
        var online = Completion();
        await using var monitor = new ServerAvailabilityMonitor(_ => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<ServerAvailabilityStatus>(new EmbyProtocolException("Unexpected response."))
            : Task.FromResult(ServerAvailabilityStatus.Online), Timeout.InfiniteTimeSpan);
        monitor.StatusChanged += (_, args) =>
        {
            if (args.Status == ServerAvailabilityStatus.Incompatible) incompatible.TrySetResult();
            if (args.Status == ServerAvailabilityStatus.Online) online.TrySetResult();
        };
        monitor.Start(TestContext.Current.CancellationToken);
        await Await(incompatible.Task);
        monitor.RequestProbe();
        await Await(online.Task);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Disposal_cancels_a_pending_health_request_and_releases_its_owner()
    {
        var entered = Completion();
        var monitor = new ServerAvailabilityMonitor(async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ServerAvailabilityStatus.Online;
        });
        monitor.Start(TestContext.Current.CancellationToken);
        await Await(entered.Task);
        await monitor.DisposeAsync();
        Assert.Equal(ServerAvailabilityStatus.Stopped, monitor.Status);
        monitor.RequestProbe();
        Assert.Throws<ObjectDisposedException>(() => monitor.Start());
    }

    private static TaskCompletionSource Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Await(Task task) => task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
}
