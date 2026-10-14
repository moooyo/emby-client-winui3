using System.Net;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

public enum ServerAvailabilityStatus
{
    Unknown, Online, Offline, AuthenticationRequired, AccountRestricted, ServerChanged, Incompatible, Stopped
}

public sealed class ServerAvailabilityChangedEventArgs(ServerAvailabilityStatus status, bool wasOffline) : EventArgs
{
    public ServerAvailabilityStatus Status { get; } = status;
    public bool WasOffline { get; } = wasOffline;
}

/// <summary>Checks authenticated HTTP availability independently of the optional event socket.</summary>
public sealed class ServerAvailabilityMonitor : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<ServerAvailabilityStatus>> _probe;
    private readonly TimeSpan _onlineInterval;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _lifetime;
    private Task _run = Task.CompletedTask;
    private int _status;
    private int _autoReconnect = 1;
    private int _explicitProbe;
    private bool _disposed;

    public ServerAvailabilityMonitor(Func<CancellationToken, Task<ServerAvailabilityStatus>> probe,
        TimeSpan? onlineInterval = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _onlineInterval = onlineInterval ?? TimeSpan.FromSeconds(30);
        if (_onlineInterval <= TimeSpan.Zero && _onlineInterval != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(onlineInterval));
    }

    public event EventHandler<ServerAvailabilityChangedEventArgs>? StatusChanged;
    public event EventHandler? OnlineConfirmed;
    public ServerAvailabilityStatus Status => (ServerAvailabilityStatus)Volatile.Read(ref _status);

    public bool AutoReconnect
    {
        get => Volatile.Read(ref _autoReconnect) != 0;
        set
        {
            var previous = Interlocked.Exchange(ref _autoReconnect, value ? 1 : 0);
            if (previous != (value ? 1 : 0)) Wake();
        }
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_lifetime is not null) return;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _run = RunAsync(_lifetime.Token);
        }
    }

    /// <summary>Coalesces explicit retries without starting concurrent health requests.</summary>
    public void RequestProbe()
    {
        Interlocked.Exchange(ref _explicitProbe, 1);
        Wake();
    }

    private void Wake()
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_wake.CurrentCount == 0) _wake.Release();
        }
    }

    public async Task StopAsync()
    {
        Task run;
        lock (_gate)
        {
            _lifetime?.Cancel();
            run = _run;
        }
        await run.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var status = await ProbeAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var wasOffline = Status is ServerAvailabilityStatus.Offline or ServerAvailabilityStatus.Incompatible;
                SetStatus(status, wasOffline);
                if (status == ServerAvailabilityStatus.Online) OnlineConfirmed?.Invoke(this, EventArgs.Empty);
                if (status is ServerAvailabilityStatus.AuthenticationRequired or ServerAvailabilityStatus.AccountRestricted
                    or ServerAvailabilityStatus.ServerChanged)
                    return;
                failures = status == ServerAvailabilityStatus.Online ? 0 : Math.Min(failures + 1, 5);
                while (true)
                {
                    var delay = status == ServerAvailabilityStatus.Incompatible ? Timeout.InfiniteTimeSpan
                        : status == ServerAvailabilityStatus.Online ? _onlineInterval
                        : AutoReconnect ? TimeSpan.FromSeconds(Math.Min(30, 1 << failures))
                        : Timeout.InfiniteTimeSpan;
                    var signaled = await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                    if (Interlocked.Exchange(ref _explicitProbe, 0) != 0) break;
                    if (status == ServerAvailabilityStatus.Offline && !AutoReconnect) continue;
                    if (!signaled || status == ServerAvailabilityStatus.Offline) break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            if (cancellationToken.IsCancellationRequested) SetStatus(ServerAvailabilityStatus.Stopped, false);
        }
    }

    private async Task<ServerAvailabilityStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { return await _probe(deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return ServerAvailabilityStatus.Offline; }
        catch (EmbyApiException exception) when (exception.IsAuthenticationFailure && exception.ApplicationErrorCode != "ParentalControl")
        { return ServerAvailabilityStatus.AuthenticationRequired; }
        catch (EmbyApiException exception) when (exception.IsPermissionDenied || exception.ApplicationErrorCode == "ParentalControl")
        { return ServerAvailabilityStatus.AccountRestricted; }
        catch (EmbyApiException exception) when ((int)exception.StatusCode >= 500 || exception.StatusCode == HttpStatusCode.RequestTimeout)
        { return ServerAvailabilityStatus.Offline; }
        catch (Exception exception) when (exception is EmbyTransportException or HttpRequestException or IOException or TimeoutException)
        { return ServerAvailabilityStatus.Offline; }
        catch (Exception exception) when (exception is EmbyProtocolException or EmbyApiException)
        { return ServerAvailabilityStatus.Incompatible; }
    }

    private void SetStatus(ServerAvailabilityStatus status, bool wasOffline)
    {
        if (Interlocked.Exchange(ref _status, (int)status) == (int)status) return;
        StatusChanged?.Invoke(this, new(status, wasOffline));
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime?.Cancel();
        }
        await _run.ConfigureAwait(false);
        _lifetime?.Dispose();
        _wake.Dispose();
    }
}
