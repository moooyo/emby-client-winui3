using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Streams one negotiated progressive source through a private, bounded loopback endpoint.</summary>
/// <remarks>The engine owns the transport. Each accepted client owns one response lease and one copy buffer.</remarks>
internal sealed partial class ProgressiveHttpRelay : IAsyncDisposable
{
    private const int MaximumClients = 4;
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly byte[] ChunkTerminator = "\r\n"u8.ToArray();
    private static readonly byte[] CompleteChunkedBody = "0\r\n\r\n"u8.ToArray();
    private readonly ScopedMediaTransport _transport;
    private readonly Uri _upstream;
    private readonly string _fallbackContentType;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _slots = new(MaximumClients, MaximumClients);
    private readonly object _clientsLock = new();
    private readonly HashSet<RelayConnection> _clients = [];
    private readonly string _target;
    private readonly string _authority;
    private readonly UpstreamFailureNotificationOwner _failures;
    private readonly Task _acceptLoop;
    private readonly Lazy<Task> _dispose;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private int _stopRequested;

    private ProgressiveHttpRelay(ScopedMediaTransport transport, Uri upstream, string? transcodingContainer,
        CancellationToken lifetime, Action<string>? failed)
    {
        _transport = transport;
        _upstream = upstream;
        _shutdownToken = _shutdown.Token;
        _failures = new UpstreamFailureNotificationOwner(_shutdownToken, failed);
        var extension = SelectExtension(transcodingContainer, upstream);
        _fallbackContentType = extension is "mp4" or "m4v" ? "video/mp4" : "application/octet-stream";
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _target = $"/{nonce}/stream.{extension}";
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(MaximumClients);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _authority = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            LocalUri = new Uri($"http://{_authority}{_target}");
            _dispose = new Lazy<Task>(DisposeCoreAsync);
            _acceptLoop = AcceptLoopAsync();
            _lifetimeRegistration = lifetime.UnsafeRegister(static state => ((ProgressiveHttpRelay)state!).RequestStop(), this);
        }
        catch
        {
            _listener.Stop();
            _shutdown.Dispose();
            _slots.Dispose();
            throw;
        }
    }

    internal Uri LocalUri { get; }

    internal static Task<ProgressiveHttpRelay> OpenAsync(ScopedMediaTransport transport, Uri upstream,
        string? transcodingContainer, CancellationToken lifetime, Action<string>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(upstream);
        lifetime.ThrowIfCancellationRequested();
        if (!upstream.IsAbsoluteUri || upstream.Scheme is not ("http" or "https") || upstream.UserInfo.Length != 0)
            throw new PlaybackException("NotAllowed");
        try { return Task.FromResult(new ProgressiveHttpRelay(transport, upstream, transcodingContainer, lifetime, failed)); }
        catch (SocketException) { throw new PlaybackException("NetworkFailure"); }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                await _slots.WaitAsync(_shutdownToken).ConfigureAwait(false);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_shutdownToken).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                if (_shutdownToken.IsCancellationRequested || client.Client.RemoteEndPoint is not IPEndPoint remote
                    || !IPAddress.IsLoopback(remote.Address))
                {
                    client.Dispose();
                    _slots.Release();
                    _shutdownToken.ThrowIfCancellationRequested();
                    continue;
                }
                var connection = new RelayConnection(client);
                lock (_clientsLock) _clients.Add(connection);
                _ = ServeConnectionAsync(connection);
            }
        }
        catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_shutdownToken.IsCancellationRequested) { }
        catch { RequestStop(); }
    }

    private async Task ServeConnectionAsync(RelayConnection connection)
    {
        NetworkStream? network = null;
        Task? disconnectMonitor = null;
        var responseStarted = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        try
        {
            connection.Client.NoDelay = true;
            network = connection.Client.GetStream();
            operation.CancelAfter(HeaderTimeout);
            if (await ReadRequestAsync(network, operation.Token).ConfigureAwait(false) is not { } request) return;
            operation.CancelAfter(Timeout.InfiniteTimeSpan);
            disconnectMonitor = MonitorResetAsync(network, operation);
            MediaStreamLease upstream;
            try
            {
                upstream = await _transport.OpenStreamAsync(_upstream, request.IsHead, request.Range, operation.Token)
                    .ConfigureAwait(false);
            }
            catch (PlaybackException error)
            {
                ReportUpstreamFailure(error.ErrorCode, operation.Token);
                throw;
            }
            await using (upstream.ConfigureAwait(false))
            {
                if (upstream.FailureCode is { } failure) ReportUpstreamFailure(failure, operation.Token);
                if (upstream.IsSuccess && !request.IsHead && request.IsHttp10 && !upstream.ContentLength.HasValue)
                {
                    // A close-delimited HTTP/1.0 body cannot distinguish a failed upstream read
                    // from successful EOF. Reject before sending success rather than hide a lost tail.
                    ReportUpstreamFailure("UnsupportedFormat", operation.Token);
                    throw new PlaybackException("UnsupportedFormat");
                }
                var chunked = upstream.IsSuccess && !request.IsHead && !upstream.ContentLength.HasValue && !request.IsHttp10;
                operation.CancelAfter(WriteIdleTimeout);
                responseStarted = true;
                await WriteHeadersAsync(network, (int)upstream.StatusCode,
                    upstream.IsSuccess ? upstream.ContentLength : 0, upstream.ContentRange,
                    upstream.IsSuccess ? SelectContentType(upstream.ContentType) : null,
                    upstream.AcceptRanges, chunked, operation.Token).ConfigureAwait(false);
                if (request.IsHead || !upstream.IsSuccess) return;

                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
                try
                {
                    while (true)
                    {
                        // Read and write have separate deadlines: a slow downstream must not be
                        // reported as an upstream network failure. The lease bounds upstream idle time.
                        operation.CancelAfter(Timeout.InfiniteTimeSpan);
                        int read;
                        try { read = await upstream.ReadAsync(buffer.AsMemory(0, CopyBufferSize), operation.Token).ConfigureAwait(false); }
                        catch (PlaybackException error)
                        {
                            ReportUpstreamFailure(error.ErrorCode, operation.Token);
                            throw;
                        }
                        operation.CancelAfter(WriteIdleTimeout);
                        if (read == 0)
                        {
                            // Only a real, successful EOF emits the terminal chunk. Errors abort the
                            // connection so an incomplete media response cannot masquerade as complete.
                            if (chunked) await network.WriteAsync(CompleteChunkedBody, operation.Token).ConfigureAwait(false);
                            break;
                        }
                        if (chunked)
                        {
                            var prefix = Encoding.ASCII.GetBytes(read.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
                            await network.WriteAsync(prefix, operation.Token).ConfigureAwait(false);
                        }
                        await network.WriteAsync(buffer.AsMemory(0, read), operation.Token).ConfigureAwait(false);
                        if (chunked) await network.WriteAsync(ChunkTerminator, operation.Token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
            }
        }
        catch (RejectedRequestException rejected)
        {
            if (network is not null && !responseStarted)
            {
                await TryWriteErrorAsync(network, rejected.StatusCode).ConfigureAwait(false);
                await FinishRejectedRequestAsync(connection.Client, network).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (network is not null && !responseStarted && !_shutdownToken.IsCancellationRequested)
                await TryWriteErrorAsync(network, 408).ConfigureAwait(false);
        }
        catch (PlaybackException)
        {
            if (network is not null && !responseStarted) await TryWriteErrorAsync(network, 502).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            if (network is not null && !responseStarted) await TryWriteErrorAsync(network, 502).ConfigureAwait(false);
        }
        finally
        {
            operation.Cancel();
            network?.Dispose();
            connection.Client.Dispose();
            if (disconnectMonitor is not null) await disconnectMonitor.ConfigureAwait(false);
            lock (_clientsLock)
            {
                _slots.Release();
                connection.Completed.TrySetResult();
                _clients.Remove(connection);
            }
        }
    }

    private static async Task MonitorResetAsync(NetworkStream network, CancellationTokenSource operation)
    {
        try
        {
            // HTTP clients may half-close their send direction and still consume a response.
            // A clean EOF is therefore not a reset; extra request bytes are disallowed pipelining.
            var count = await network.ReadAsync(new byte[1], operation.Token).ConfigureAwait(false);
            if (count != 0) operation.Cancel();
        }
        catch (OperationCanceledException) { }
        catch (IOException) { operation.Cancel(); }
        catch (SocketException) { operation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task<RelayRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaximumHeaderBytes);
        try
        {
            var length = 0;
            while (length < MaximumHeaderBytes)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length, MaximumHeaderBytes - length), ct).ConfigureAwait(false);
                if (read == 0) return null;
                var searchStart = Math.Max(0, length - 3);
                length += read;
                for (var index = searchStart; index <= length - 4; index++)
                {
                    if (buffer[index] != '\r' || buffer[index + 1] != '\n' || buffer[index + 2] != '\r' || buffer[index + 3] != '\n') continue;
                    if (index + 4 != length) throw new RejectedRequestException(400);
                    for (var character = 0; character < index; character++)
                    {
                        var value = buffer[character];
                        if (value > 126 || value < 32 && value is not (9 or 10 or 13)) throw new RejectedRequestException(400);
                    }
                    return ParseRequest(Encoding.ASCII.GetString(buffer, 0, index));
                }
            }
            throw new RejectedRequestException(431);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private RelayRequest ParseRequest(string text)
    {
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', StringSplitOptions.None);
        if (first.Length != 3 || first[2] is not ("HTTP/1.1" or "HTTP/1.0")) throw new RejectedRequestException(400);
        if (first[0] is not ("GET" or "HEAD")) throw new RejectedRequestException(405);
        if (!string.Equals(first[1], _target, StringComparison.Ordinal)) throw new RejectedRequestException(404);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || !IsHeaderName(line[..separator])) throw new RejectedRequestException(400);
            var value = line[(separator + 1)..];
            if (value.Any(character => (character < ' ' || character > '~') && character != '\t')
                || !headers.TryAdd(line[..separator], value.Trim(' ', '\t'))) throw new RejectedRequestException(400);
        }
        if (!headers.TryGetValue("Host", out var host) || !string.Equals(host, _authority, StringComparison.Ordinal))
            throw new RejectedRequestException(400);
        if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Expect") || headers.ContainsKey("Upgrade"))
            throw new RejectedRequestException(400);
        if (headers.TryGetValue("Content-Length", out var length) && (!TryUnsigned(length, out var bodyLength) || bodyLength != 0))
            throw new RejectedRequestException(400);
        headers.TryGetValue("Range", out var range);
        var isHead = first[0] == "HEAD";
        if (isHead) range = null;
        else if (range is not null) ValidateRange(range);
        return new RelayRequest(isHead, range, first[2] == "HTTP/1.0");
    }

    private static void ValidateRange(string range)
    {
        if (!range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) throw new RejectedRequestException(416);
        var value = range[6..];
        var dash = value.IndexOf('-');
        if (dash < 0 || value.Contains(',') || value.IndexOf('-', dash + 1) >= 0) throw new RejectedRequestException(416);
        var first = value[..dash];
        var last = value[(dash + 1)..];
        if (first.Length == 0)
        {
            if (!TryUnsigned(last, out var suffix) || suffix == 0) throw new RejectedRequestException(416);
            return;
        }
        if (!TryUnsigned(first, out var start) || last.Length != 0 && (!TryUnsigned(last, out var end) || end < start))
            throw new RejectedRequestException(416);
    }

    private static async Task WriteHeadersAsync(NetworkStream stream, int status, long? length,
        string? contentRange, string? contentType, string? acceptRanges, bool chunked, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK", 206 => "Partial Content", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden",
            404 => "Not Found", 405 => "Method Not Allowed", 408 => "Request Timeout", 415 => "Unsupported Media Type",
            416 => "Range Not Satisfiable", 431 => "Request Header Fields Too Large", 500 => "Internal Server Error",
            503 => "Service Unavailable", _ => "Bad Gateway"
        };
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason)
            .Append("\r\nConnection: close\r\nCache-Control: no-store\r\n");
        if (length.HasValue) builder.Append("Content-Length: ").Append(length.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        else if (chunked) builder.Append("Transfer-Encoding: chunked\r\n");
        if (contentType is not null) builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentRange is not null) builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        if (acceptRanges is not null) builder.Append("Accept-Ranges: ").Append(acceptRanges).Append("\r\n");
        if (status == 405) builder.Append("Allow: GET, HEAD\r\n");
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
    }

    private async Task TryWriteErrorAsync(NetworkStream stream, int status)
    {
        if (_shutdownToken.IsCancellationRequested) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { await WriteHeadersAsync(stream, status, 0, null, null, null, false, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task FinishRejectedRequestAsync(TcpClient client, NetworkStream network)
    {
        // Closing a Windows socket with unread request bytes can reset the connection and discard
        // the rejection response. Send its FIN first, then drain a small, strictly bounded remainder.
        try { client.Client.Shutdown(SocketShutdown.Send); }
        catch (SocketException) { return; }
        catch (ObjectDisposedException) { return; }
        using var grace = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        grace.CancelAfter(TimeSpan.FromMilliseconds(100));
        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            var remaining = MaximumHeaderBytes;
            while (remaining > 0)
            {
                var read = await network.ReadAsync(buffer.AsMemory(0, Math.Min(1024, remaining)), grace.Token).ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private void RequestStop()
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
        _shutdown.Cancel();
        _listener.Stop();
        RelayConnection[] clients;
        lock (_clientsLock) clients = _clients.ToArray();
        foreach (var client in clients) client.Client.Dispose();
    }

    private void ReportUpstreamFailure(string code, CancellationToken connectionToken)
    {
        _failures.TryCommit(code, connectionToken)?.Deliver();
    }

    public ValueTask DisposeAsync() => new(_dispose.Value);

    private async Task DisposeCoreAsync()
    {
        RequestStop();
        await _acceptLoop.ConfigureAwait(false);
        Task[] pending;
        lock (_clientsLock) pending = _clients.Select(client => client.Completed.Task).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        await _lifetimeRegistration.DisposeAsync().ConfigureAwait(false);
        _slots.Dispose();
        _shutdown.Dispose();
    }

    private string SelectContentType(string actual) => actual.Contains('/')
        && actual.All(character => character is >= ' ' and <= '~')
        && !actual.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) ? actual : _fallbackContentType;

    private static string SelectExtension(string? container, Uri upstream)
    {
        var extension = container?.Trim().TrimStart('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(extension)) extension = Path.GetExtension(upstream.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return extension is "mp4" or "m4v" or "mov" or "ts" or "m2ts" or "webm" ? extension : "bin";
    }

    private static bool TryUnsigned(string text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    private static bool IsHeaderName(string value) => value.Length > 0
        && value.All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character));
    public override string ToString() => nameof(ProgressiveHttpRelay);
    private readonly record struct RelayRequest(bool IsHead, string? Range, bool IsHttp10);
    private sealed partial class RejectedRequestException(int statusCode) : Exception("The local media request was rejected.")
    {
        internal int StatusCode { get; } = statusCode;
    }
    private sealed partial class RelayConnection(TcpClient client)
    {
        internal TcpClient Client { get; } = client;
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>Separates a session's one failure commitment from delivery outside its admission lock.</summary>
internal sealed class UpstreamFailureNotificationOwner(CancellationToken shutdown, Action<string>? failed)
{
    private readonly object _gate = new();
    private bool _committed;

    internal CommittedUpstreamFailure? TryCommit(string code, CancellationToken connectionToken)
    {
        if (code is not ("NetworkFailure" or "AuthenticationRequired" or "NotAllowed" or "UnsupportedFormat")) return null;
        var notification = new CommittedUpstreamFailure(code, failed);
        lock (_gate)
        {
            if (_committed || shutdown.IsCancellationRequested || connectionToken.IsCancellationRequested) return null;
            // The successful eligibility decision under this lock is the linearization point.
            // Cancellation observed before it consumes nothing. Once committed, delivery cannot
            // be revoked by a later connection cancellation or steal another caller's opportunity.
            _committed = true;
        }
        return notification;
    }
}

internal sealed class CommittedUpstreamFailure(string code, Action<string>? failed)
{
    private int _delivered;

    internal void Deliver()
    {
        if (Interlocked.Exchange(ref _delivered, 1) != 0) return;
        try { failed?.Invoke(code); }
        catch (Exception) { }
    }
}
