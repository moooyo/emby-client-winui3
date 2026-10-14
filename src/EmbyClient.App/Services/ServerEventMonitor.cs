using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

public enum ServerEventConnectionStatus
{
    Connecting, Connected, Reconnecting, AuthenticationFailed, PermissionDenied, Stopped
}

public sealed class ServerEventConnectionChangedEventArgs(ServerEventConnectionStatus status,
    int consecutiveFailures, TimeSpan? retryDelay = null) : EventArgs
{
    public ServerEventConnectionStatus Status { get; } = status;
    public int ConsecutiveFailures { get; } = consecutiveFailures;
    public TimeSpan? RetryDelay { get; } = retryDelay;
}

/// <summary>Owns one account's cancellable event connection without owning its HTTP availability state.</summary>
/// <remarks>Events are raised on a background thread. Consumers must dispatch and check their session generation.</remarks>
public sealed class ServerEventMonitor : IAsyncDisposable
{
    private const int MaximumRememberedMessageIds = 256;
    private static readonly TimeSpan HealthyConnectionInterval = TimeSpan.FromSeconds(30);
    private readonly EmbyApiClient _api;
    private readonly HttpMessageInvoker _transport;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);
    private readonly Queue<string> _messageIdOrder = new();
    private CancellationTokenSource? _lifetime;
    private ClientWebSocket? _socket;
    private Task _run = Task.CompletedTask;
    private Task? _dispose;
    private int _status = (int)ServerEventConnectionStatus.Stopped;
    private int _autoReconnect = 1;
    private int _explicitRetry;
    private bool _stopRequested;
    private bool _disposed;

    public ServerEventMonitor(EmbyApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        if (!api.IsAuthenticated)
            throw new ArgumentException("Server events require an authenticated user context.", nameof(api));
        _api = api;
        _transport = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        });
    }

    public event EventHandler<ServerEventConnectionChangedEventArgs>? ConnectionStatusChanged;
    public event EventHandler<LibraryChangedEventArgs>? LibraryChanged;
    public ServerEventConnectionStatus Status => (ServerEventConnectionStatus)Volatile.Read(ref _status);

    public bool AutoReconnect
    {
        get => Volatile.Read(ref _autoReconnect) != 0;
        set
        {
            lock (_gate)
            {
                if (_disposed || _stopRequested) return;
                var next = value ? 1 : 0;
                if (Interlocked.Exchange(ref _autoReconnect, next) == next) return;
                if (value && (Status != ServerEventConnectionStatus.Connected || _socket?.State != WebSocketState.Open))
                    Interlocked.Exchange(ref _explicitRetry, 1);
                WakeCore();
            }
        }
    }

    public void Start(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopRequested) throw new InvalidOperationException("A stopped event monitor cannot be restarted.");
            if (_lifetime is not null) return;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _lifetime = lifetime;
            _run = Task.Run(() => RunAsync(lifetime.Token));
        }
    }

    /// <summary>Coalesces manual retries, including a retry after a rejected upgrade.</summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (_disposed || _stopRequested) return;
            if (Status == ServerEventConnectionStatus.Connected && _socket?.State == WebSocketState.Open) return;
            Interlocked.Exchange(ref _explicitRetry, 1);
            WakeCore();
        }
    }

    public async Task StopAsync()
    {
        Task run;
        ClientWebSocket? socket;
        CancellationTokenSource? lifetime;
        lock (_gate)
        {
            run = _run;
            socket = _socket;
            lifetime = _stopRequested ? null : _lifetime;
            _stopRequested = true;
        }
        // A concurrent disposal can retire an already completed run before this caller resumes.
        try { lifetime?.Cancel(); }
        catch (ObjectDisposedException) { }
        try { socket?.Abort(); }
        catch (ObjectDisposedException) { }
        await run.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            _dispose = DisposeCoreAsync();
            return new(_dispose);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        finally
        {
            _lifetime?.Dispose();
            _transport.Dispose();
            _wake.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var failures = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var terminalFailure = false;
                var failureStatus = ServerEventConnectionStatus.Reconnecting;
                long? connectedAt = null;
                SetStatus(failures == 0 ? ServerEventConnectionStatus.Connecting
                    : ServerEventConnectionStatus.Reconnecting, failures);
                using (var socket = new ClientWebSocket())
                {
                    // Use standard WebSocket PING/PONG. No undocumented Emby application ping is sent.
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                    lock (_gate) _socket = socket;
                    try
                    {
                        await _api.ConnectServerEventSocketAsync(socket, cancellationToken, _transport).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        connectedAt = Stopwatch.GetTimestamp();
                        Interlocked.Exchange(ref _explicitRetry, 0);
                        SetStatus(ServerEventConnectionStatus.Connected, 0);
                        await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                    catch (EmbyApiException exception) when (exception.IsAuthenticationFailure
                        && exception.ApplicationErrorCode != "ParentalControl")
                    {
                        terminalFailure = true;
                        failureStatus = ServerEventConnectionStatus.AuthenticationFailed;
                    }
                    catch (EmbyApiException exception) when (exception.IsPermissionDenied
                        || exception.ApplicationErrorCode == "ParentalControl")
                    {
                        terminalFailure = true;
                        failureStatus = ServerEventConnectionStatus.PermissionDenied;
                    }
                    catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException
                        or EmbyProtocolException or HttpRequestException or WebSocketException or IOException
                        or TimeoutException or OperationCanceledException or ObjectDisposedException)
                    {
                        // Only sanitized state is published. Socket exceptions may contain credentials.
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_socket, socket)) _socket = null;
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (connectedAt.HasValue && Stopwatch.GetElapsedTime(connectedAt.Value) >= HealthyConnectionInterval)
                    failures = 0;
                failures = Math.Min(failures + 1, 6);
                SetStatus(failureStatus, failures, !terminalFailure && AutoReconnect
                    ? ServerEventRetryPolicy.GetDelay(failures) : null);
                await WaitForRetryAsync(failures, terminalFailure, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { SetStatus(ServerEventConnectionStatus.Stopped, 0); }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];
        using var message = new MemoryStream();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, deadline.Token)
                        .ConfigureAwait(false);
                }
                return;
            }
            if (result.MessageType != WebSocketMessageType.Text
                || message.Length + result.Count > ServerEventProtocol.MaximumMessageBytes)
                throw new EmbyProtocolException("The server returned an unsupported or oversized event message.");

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            // Decode only a complete message, so a multi-byte UTF-8 character can cross any fragment boundary.
            var change = ServerEventProtocol.ParseLibraryChanged(message.GetBuffer().AsMemory(0, (int)message.Length));
            message.SetLength(0);
            if (change is null || AlreadyReceived(change.MessageId)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            Publish(LibraryChanged, change);
        }
    }

    private bool AlreadyReceived(string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return false;
        if (!_messageIds.Add(messageId)) return true;
        _messageIdOrder.Enqueue(messageId);
        if (_messageIdOrder.Count > MaximumRememberedMessageIds)
            _messageIds.Remove(_messageIdOrder.Dequeue());
        return false;
    }

    private async Task WaitForRetryAsync(int failures, bool terminalFailure, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _explicitRetry, 0) != 0) return;
            var delay = terminalFailure || !AutoReconnect ? Timeout.InfiniteTimeSpan
                : ServerEventRetryPolicy.GetDelay(failures);
            var signaled = await _wake.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            if (!signaled && AutoReconnect && !terminalFailure) return;
        }
    }

    private void WakeCore()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void SetStatus(ServerEventConnectionStatus status, int failures, TimeSpan? delay = null)
    {
        Volatile.Write(ref _status, (int)status);
        Publish(ConnectionStatusChanged, new ServerEventConnectionChangedEventArgs(status, failures, delay));
    }

    private void Publish<T>(EventHandler<T>? handlers, T args) where T : EventArgs
    {
        if (handlers is null) return;
        foreach (EventHandler<T> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch { /* A consumer failure must not close another consumer's event connection. */ }
        }
    }
}
