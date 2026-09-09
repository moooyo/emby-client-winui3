using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Exposes one authenticated media representation through a private loopback capability URL.</summary>
/// <remarks>The relay owns its cursors and sockets, but the playback engine owns the shared transport.</remarks>
internal sealed partial class SessionHttpRelay : IAsyncDisposable
{
    private const int MaximumClients = 4;
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TransferIdleTimeout = TimeSpan.FromSeconds(60);
    private readonly HttpRangeStream _source;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _slots = new(MaximumClients, MaximumClients);
    private readonly object _clientsLock = new();
    private readonly HashSet<RelayConnection> _clients = [];
    private readonly string _target;
    private readonly string _authority;
    private readonly Task _acceptLoop;
    private readonly Lazy<Task> _dispose;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private int _stopRequested;

    private SessionHttpRelay(HttpRangeStream source, string? container, CancellationToken lifetime)
    {
        _source = source;
        _shutdownToken = _shutdown.Token;
        SourceLength = source.Length;
        var extension = SelectExtension(container, source.ContentType);
        ContentType = SelectContentType(source.ContentType, extension);
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
            _lifetimeRegistration = lifetime.UnsafeRegister(static state => ((SessionHttpRelay)state!).RequestStop(), this);
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
    internal long SourceLength { get; }
    internal string ContentType { get; }

    internal static async Task<SessionHttpRelay> OpenAsync(ScopedMediaTransport transport, Uri upstream,
        string? container, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(upstream);
        lifetime.ThrowIfCancellationRequested();
        var source = await HttpRangeStream.OpenAsync(transport, upstream, lifetime).ConfigureAwait(false);
        try
        {
            lifetime.ThrowIfCancellationRequested();
            return new SessionHttpRelay(source, container, lifetime);
        }
        catch (OperationCanceledException)
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw new OperationCanceledException(lifetime);
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw new PlaybackException("NetworkFailure");
        }
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (true)
            {
                // Reserve capacity before accepting, including clients that have not finished their headers.
                await _slots.WaitAsync(_shutdownToken).ConfigureAwait(false);
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_shutdownToken).ConfigureAwait(false);
                }
                catch
                {
                    _slots.Release();
                    throw;
                }

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
        catch (SocketException) { RequestStop(); }
        catch (ObjectDisposedException) when (_shutdownToken.IsCancellationRequested) { }
        catch
        {
            // Listener failures close the private endpoint. No exception or capability URL is logged.
            RequestStop();
        }
    }

    private async Task ServeConnectionAsync(RelayConnection connection)
    {
        NetworkStream? network = null;
        var responseStarted = false;
        try
        {
            connection.Client.NoDelay = true;
            network = connection.Client.GetStream();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            operation.CancelAfter(HeaderTimeout);
            if (await ReadRequestAsync(network, operation.Token).ConfigureAwait(false) is not { } request) return;
            var range = SelectRange(request);
            operation.CancelAfter(TransferIdleTimeout);
            if (request.IsHead)
            {
                responseStarted = true;
                await WriteHeadersAsync(network, 200, SourceLength, null, operation.Token).ConfigureAwait(false);
                return;
            }

            await using var cursor = _source.CloneCursor();
            cursor.Position = range.Offset;
            var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
            try
            {
                var remaining = range.Length;
                while (remaining > 0)
                {
                    operation.CancelAfter(TransferIdleTimeout);
                    var read = await cursor.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, CopyBufferSize)), operation.Token)
                        .ConfigureAwait(false);
                    if (read == 0) throw new PlaybackException("NetworkFailure");
                    if (!responseStarted)
                    {
                        responseStarted = true;
                        var contentRange = range.IsPartial
                            ? FormattableString.Invariant($"bytes {range.Offset}-{range.Offset + range.Length - 1}/{SourceLength}") : null;
                        await WriteHeadersAsync(network, range.IsPartial ? 206 : 200, range.Length, contentRange, operation.Token)
                            .ConfigureAwait(false);
                    }

                    operation.CancelAfter(TransferIdleTimeout);
                    await network.WriteAsync(buffer.AsMemory(0, read), operation.Token).ConfigureAwait(false);
                    remaining -= read;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }
        catch (RejectedRequestException rejected)
        {
            if (network is not null && !responseStarted)
                await TryWriteErrorAsync(network, rejected.StatusCode).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (network is not null && !responseStarted && !_shutdownToken.IsCancellationRequested)
                await TryWriteErrorAsync(network, 408).ConfigureAwait(false);
        }
        catch (PlaybackException)
        {
            if (network is not null && !responseStarted)
                await TryWriteErrorAsync(network, 502).ConfigureAwait(false);
        }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        catch
        {
            if (network is not null && !responseStarted)
                await TryWriteErrorAsync(network, 502).ConfigureAwait(false);
        }
        finally
        {
            network?.Dispose();
            connection.Client.Dispose();
            lock (_clientsLock)
            {
                _slots.Release();
                connection.Completed.TrySetResult();
                _clients.Remove(connection);
            }
        }
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
                    if (buffer[index] != '\r' || buffer[index + 1] != '\n'
                        || buffer[index + 2] != '\r' || buffer[index + 3] != '\n') continue;
                    if (index + 4 != length) throw new RejectedRequestException(400);
                    for (var character = 0; character < index; character++)
                    {
                        var value = buffer[character];
                        if (value > 126 || (value < 32 && value is not (9 or 10 or 13)))
                            throw new RejectedRequestException(400);
                    }
                    return ParseRequest(Encoding.ASCII.GetString(buffer, 0, index));
                }
            }
            throw new RejectedRequestException(431);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private RelayRequest ParseRequest(string text)
    {
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', StringSplitOptions.None);
        if (first.Length != 3 || first[2] is not ("HTTP/1.1" or "HTTP/1.0"))
            throw new RejectedRequestException(400);
        if (first[0] is not ("GET" or "HEAD")) throw new RejectedRequestException(405);
        // Exact wire comparison rejects alternate escaping, absolute-form, query strings, and fragments.
        if (!string.Equals(first[1], _target, StringComparison.Ordinal)) throw new RejectedRequestException(404);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 || !IsHeaderName(line[..separator])) throw new RejectedRequestException(400);
            var value = line[(separator + 1)..];
            if (value.Any(character => (character < ' ' || character > '~') && character != '\t'))
                throw new RejectedRequestException(400);
            if (!headers.TryAdd(line[..separator], value.Trim(' ', '\t'))) throw new RejectedRequestException(400);
        }

        if (!headers.TryGetValue("Host", out var host) || !string.Equals(host, _authority, StringComparison.Ordinal))
            throw new RejectedRequestException(400);
        if (headers.ContainsKey("Transfer-Encoding") || headers.ContainsKey("Expect") || headers.ContainsKey("Upgrade"))
            throw new RejectedRequestException(400);
        if (headers.TryGetValue("Content-Length", out var contentLength)
            && (!TryUnsigned(contentLength, out var declaredLength) || declaredLength != 0))
            throw new RejectedRequestException(400);
        headers.TryGetValue("Range", out var range);
        return new RelayRequest(first[0] == "HEAD", range);
    }

    private RelayRange SelectRange(RelayRequest request)
    {
        // RFC 9110 defines Range for GET; HEAD describes the complete selected representation.
        if (request.IsHead || request.Range is null) return new RelayRange(0, SourceLength, false);
        if (!request.Range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) throw new RejectedRequestException(416);
        var value = request.Range[6..];
        var dash = value.IndexOf('-');
        if (dash < 0 || value.Contains(',') || value.IndexOf('-', dash + 1) >= 0) throw new RejectedRequestException(416);
        var first = value[..dash];
        var last = value[(dash + 1)..];
        if (first.Length == 0)
        {
            if (!TryUnsigned(last, out var suffix) || suffix == 0) throw new RejectedRequestException(416);
            var count = Math.Min(suffix, SourceLength);
            return new RelayRange(SourceLength - count, count, true);
        }

        if (!TryUnsigned(first, out var start) || start >= SourceLength) throw new RejectedRequestException(416);
        var end = SourceLength - 1;
        if (last.Length != 0)
        {
            if (!TryUnsigned(last, out var suppliedEnd) || suppliedEnd < start) throw new RejectedRequestException(416);
            end = Math.Min(suppliedEnd, end);
        }
        return new RelayRange(start, end - start + 1, true);
    }

    private async Task WriteHeadersAsync(NetworkStream stream, int status, long length, string? contentRange, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK", 206 => "Partial Content", 400 => "Bad Request", 404 => "Not Found",
            405 => "Method Not Allowed", 408 => "Request Timeout", 416 => "Range Not Satisfiable",
            431 => "Request Header Fields Too Large", _ => "Bad Gateway"
        };
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason)
            .Append("\r\nContent-Length: ").Append(length.ToString(CultureInfo.InvariantCulture))
            .Append("\r\nAccept-Ranges: bytes\r\nConnection: close\r\nCache-Control: no-store\r\n");
        if (status is 200 or 206) builder.Append("Content-Type: ").Append(ContentType).Append("\r\n");
        if (contentRange is not null) builder.Append("Content-Range: ").Append(contentRange).Append("\r\n");
        if (status == 405) builder.Append("Allow: GET, HEAD\r\n");
        builder.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(builder.ToString()), ct).ConfigureAwait(false);
    }

    private async Task TryWriteErrorAsync(NetworkStream stream, int status)
    {
        if (_shutdownToken.IsCancellationRequested) return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var range = status == 416 ? FormattableString.Invariant($"bytes */{SourceLength}") : null;
            await WriteHeadersAsync(stream, status, 0, range, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
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

    public ValueTask DisposeAsync() => new(_dispose.Value);

    private async Task DisposeCoreAsync()
    {
        RequestStop();
        await _acceptLoop.ConfigureAwait(false);
        Task[] pending;
        lock (_clientsLock) pending = _clients.Select(client => client.Completed.Task).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
        await _lifetimeRegistration.DisposeAsync().ConfigureAwait(false);
        _slots.Dispose();
        _shutdown.Dispose();
    }

    private static bool TryUnsigned(string text, out long value)
        => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static bool IsHeaderName(string text)
        => text.Length > 0 && text.All(character => char.IsAsciiLetterOrDigit(character)
            || "!#$%&'*+-.^_`|~".Contains(character));

    private static string SelectExtension(string? container, string contentType)
    {
        var requested = container?.Trim().TrimStart('.').ToLowerInvariant();
        if (requested is "mp4" or "m4v" or "mov" or "mkv" or "webm" or "ts" or "m2ts" or "mpg" or "mpeg"
            or "avi" or "wmv" or "asf" or "mp3" or "aac" or "flac" or "ogg" or "wav" or "wma" or "m4a")
            return requested;
        return contentType.ToLowerInvariant() switch
        {
            "video/mp4" or "audio/mp4" => "mp4", "video/x-matroska" => "mkv", "video/webm" => "webm",
            "video/mp2t" => "ts", "video/quicktime" => "mov", "audio/mpeg" => "mp3", "audio/flac" => "flac",
            _ => "bin"
        };
    }

    private static string SelectContentType(string contentType, string extension)
    {
        if (contentType.Contains('/') && contentType.All(character => character is >= ' ' and <= '~')
            && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)) return contentType;
        return extension switch
        {
            "mp4" or "m4v" => "video/mp4", "m4a" => "audio/mp4", "mkv" => "video/x-matroska",
            "webm" => "video/webm", "ts" or "m2ts" => "video/mp2t", "mov" => "video/quicktime",
            "mp3" => "audio/mpeg", "flac" => "audio/flac", _ => "application/octet-stream"
        };
    }

    public override string ToString() => $"{nameof(SessionHttpRelay)} {{ SourceLength = {SourceLength} }}";

    private readonly record struct RelayRequest(bool IsHead, string? Range);
    private readonly record struct RelayRange(long Offset, long Length, bool IsPartial);
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
