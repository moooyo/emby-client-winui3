using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using EmbyClient.Playback;

namespace EmbyClient.App.Playback;

/// <summary>Rewrites authenticated HLS resources to bounded, private loopback capabilities.</summary>
/// <remarks>The engine owns the transport. The relay retains URI mappings, never segment bodies.</remarks>
internal sealed partial class HlsHttpRelay : IAsyncDisposable
{
    private const int MaximumClients = 4;
    private const int MaximumResources = 16384;
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumSegmentBytes = 64 * 1024 * 1024;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteIdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ResourceGracePeriod = TimeSpan.FromMinutes(2);
    private static readonly UTF8Encoding ManifestEncoding = new(false, true);
    private readonly ScopedMediaTransport _transport;
    private readonly Uri _origin;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SemaphoreSlim _slots = new(MaximumClients, MaximumClients);
    private readonly object _clientsLock = new();
    private readonly HashSet<RelayConnection> _clients = [];
    private readonly object _resourcesLock = new();
    private readonly Dictionary<string, RelayResource> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<(Uri Uri, bool IsManifest), string> _resourceTargets = [];
    private readonly Dictionary<string, HashSet<string>> _manifestChildren = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly int _maximumResources;
    private readonly string _rootTarget;
    private readonly string _nonce;
    private readonly string _authority;
    private readonly UpstreamFailureNotificationOwner _failures;
    private readonly Task _acceptLoop;
    private readonly Lazy<Task> _dispose;
    private readonly CancellationTokenRegistration _lifetimeRegistration;
    private int _stopRequested;
    private ulong _nextResourceId = 1;

    private HlsHttpRelay(ScopedMediaTransport transport, Uri upstream, CancellationToken lifetime, Action<string>? failed,
        TimeProvider clock, int maximumResources)
    {
        _transport = transport;
        _origin = upstream;
        _clock = clock;
        _maximumResources = maximumResources;
        _shutdownToken = _shutdown.Token;
        _failures = new UpstreamFailureNotificationOwner(_shutdownToken, failed);
        _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            _listener.Server.ExclusiveAddressUse = true;
            _listener.Start(MaximumClients);
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _authority = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
            var target = $"/{_nonce}/stream.m3u8";
            _rootTarget = target;
            _resources.Add(target, new RelayResource(upstream, true, _clock.GetTimestamp()));
            _resourceTargets.Add((upstream, true), target);
            LocalUri = new Uri($"http://{_authority}{target}");
            _dispose = new Lazy<Task>(DisposeCoreAsync);
            _acceptLoop = AcceptLoopAsync();
            _lifetimeRegistration = lifetime.UnsafeRegister(static state => ((HlsHttpRelay)state!).RequestStop(), this);
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
    internal int TrackedResourceCount { get { lock (_resourcesLock) return _resources.Count; } }

    internal static Task<HlsHttpRelay> OpenAsync(ScopedMediaTransport transport, Uri upstream,
        CancellationToken lifetime, Action<string>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(upstream);
        lifetime.ThrowIfCancellationRequested();
        ValidateUri(upstream);
        try { return Task.FromResult(new HlsHttpRelay(transport, upstream, lifetime, failed, TimeProvider.System, MaximumResources)); }
        catch (SocketException) { throw new PlaybackException("NetworkFailure"); }
    }

    // Inject only the clock and capacity for deterministic, long-running sliding-window tests.
    internal static Task<HlsHttpRelay> OpenForTestingAsync(ScopedMediaTransport transport, Uri upstream,
        CancellationToken lifetime, TimeProvider clock, int maximumResources, Action<string>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(upstream);
        ArgumentNullException.ThrowIfNull(clock);
        if (maximumResources is < 2 or > MaximumResources) throw new ArgumentOutOfRangeException(nameof(maximumResources));
        lifetime.ThrowIfCancellationRequested();
        ValidateUri(upstream);
        return Task.FromResult(new HlsHttpRelay(transport, upstream, lifetime, failed, clock, maximumResources));
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
        RelayRequest? request = null;
        var responseStarted = false;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
        try
        {
            connection.Client.NoDelay = true;
            network = connection.Client.GetStream();
            operation.CancelAfter(HeaderTimeout);
            request = await ReadRequestAsync(network, operation.Token).ConfigureAwait(false);
            if (request is null) return;
            operation.CancelAfter(Timeout.InfiniteTimeSpan);
            disconnectMonitor = MonitorResetAsync(network, operation);
            RelayResponse response;
            try { response = await DownloadAsync(request, operation.Token).ConfigureAwait(false); }
            catch (PlaybackException error)
            {
                _failures.TryCommit(error.ErrorCode, operation.Token)?.Deliver();
                throw;
            }

            operation.CancelAfter(WriteIdleTimeout);
            responseStarted = true;
            await WriteHeadersAsync(network, response.Status, response.Length, response.ContentRange,
                response.ContentType, operation.Token).ConfigureAwait(false);
            if (request.IsHead) return;
            var remaining = response.Length;
            var offset = response.Offset;
            while (remaining > 0)
            {
                operation.CancelAfter(WriteIdleTimeout);
                var count = Math.Min(remaining, CopyBufferSize);
                await network.WriteAsync(response.Data.AsMemory(offset, count), operation.Token).ConfigureAwait(false);
                offset += count;
                remaining -= count;
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
            // Capture before closing the socket: a later continuation delay is not a new client access.
            var completedAt = _clock.GetTimestamp();
            operation.Cancel();
            network?.Dispose();
            connection.Client.Dispose();
            if (disconnectMonitor is not null) await disconnectMonitor.ConfigureAwait(false);
            if (request is not null)
            {
                lock (_resourcesLock)
                {
                    request.Resource.ActiveRequests--;
                    request.Resource.LastUsed = Math.Max(request.Resource.LastUsed, completedAt);
                }
            }
            lock (_clientsLock)
            {
                _slots.Release();
                connection.Completed.TrySetResult();
                _clients.Remove(connection);
            }
        }
    }

    private async Task<RelayResponse> DownloadAsync(RelayRequest request, CancellationToken ct)
    {
        var isLocalRange = request.Resource.IsManifest || request.Range?.Suffix is not null;
        var range = isLocalRange ? null : request.Range;
        var resource = await _transport.DownloadHlsAsync(request.Resource.Uri, _origin, range?.Offset, range?.Length,
            request.Resource.IsManifest ? MaximumManifestBytes : MaximumSegmentBytes, ct).ConfigureAwait(false);
        EnsureSameOrigin(resource.EffectiveUri);
        var data = request.Resource.IsManifest ? RewriteManifest(resource, request.Target) : resource.Data;
        var contentType = request.Resource.IsManifest ? "application/vnd.apple.mpegurl" : SelectContentType(resource.ContentType);
        if (request.IsHead) return new RelayResponse(data, 0, data.Length, 200, null, contentType);
        if (isLocalRange && request.Range is { } localRange)
        {
            var (offset, length) = SelectLocalRange(localRange, data.Length);
            return new RelayResponse(data, offset, length, 206,
                FormattableString.Invariant($"bytes {offset}-{offset + length - 1}/{data.Length}"), contentType);
        }
        return new RelayResponse(data, 0, data.Length, (int)resource.StatusCode, resource.ContentRange, contentType);
    }

    private byte[] RewriteManifest(MediaResource resource, string manifestTarget)
    {
        string manifest;
        try { manifest = ManifestEncoding.GetString(resource.Data).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new PlaybackException("UnsupportedFormat"); }
        if (manifest.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new PlaybackException("UnsupportedFormat");
        using var reader = new StringReader(manifest);
        if (reader.ReadLine() is not "#EXTM3U") throw new PlaybackException("UnsupportedFormat");
        lock (_resourcesLock)
        {
            return RewriteManifestLocked(reader, resource.EffectiveUri, manifestTarget);
        }
    }

    private byte[] RewriteManifestLocked(StringReader reader, Uri effectiveUri, string manifestTarget)
    {
        ReclaimRetiredResources();
        var children = new HashSet<string>(StringComparer.Ordinal);
        var output = new StringBuilder("#EXTM3U\n");
        var nextIsManifest = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#'))
            {
                var separator = line.IndexOf(':');
                var tag = separator < 0 ? line : line[..separator];
                var attributeIsManifest = tag is "#EXT-X-MEDIA" or "#EXT-X-I-FRAME-STREAM-INF"
                    or "#EXT-X-RENDITION-REPORT" or "#EXT-X-IMAGE-STREAM-INF";
                output.Append(RewriteAttributes(line, effectiveUri, attributeIsManifest, children));
                if (tag == "#EXT-X-STREAM-INF") nextIsManifest = true;
            }
            else if (line.Length > 0)
            {
                output.Append(MapReference(line, effectiveUri, nextIsManifest, children));
                nextIsManifest = false;
            }
            output.Append('\n');
            // Bound the rewritten representation too, even when short references expand substantially.
            if (output.Length > MaximumManifestBytes) throw new PlaybackException("UnsupportedFormat");
        }
        var bytes = ManifestEncoding.GetBytes(output.ToString());
        if (bytes.Length > MaximumManifestBytes) throw new PlaybackException("UnsupportedFormat");
        // Commit a complete snapshot only after parsing succeeds. VOD future segments remain referenced.
        if (_manifestChildren.TryGetValue(manifestTarget, out var previousChildren))
        {
            var retiredAt = _clock.GetTimestamp();
            foreach (var removed in previousChildren.Except(children))
                if (_resources.TryGetValue(removed, out var resource)) resource.LastUsed = retiredAt;
        }
        _manifestChildren[manifestTarget] = children;
        ReclaimRetiredResources();
        return bytes;
    }

    private string RewriteAttributes(string line, Uri effectiveUri, bool isManifest, HashSet<string> children)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return line;
        var output = new StringBuilder(line.Length);
        output.Append(line.AsSpan(0, colon + 1));
        var position = colon + 1;
        while (position < line.Length)
        {
            var start = position;
            while (position < line.Length && line[position] is ' ' or '\t') position++;
            var nameStart = position;
            while (position < line.Length && (char.IsAsciiLetterOrDigit(line[position]) || line[position] == '-')) position++;
            var name = line[nameStart..position];
            while (position < line.Length && line[position] is ' ' or '\t') position++;
            if (position >= line.Length || line[position] != '=')
            {
                output.Append(line.AsSpan(start));
                break;
            }
            position++;
            while (position < line.Length && line[position] is ' ' or '\t') position++;
            var isUri = name.Equals("URI", StringComparison.Ordinal) || name.EndsWith("-URI", StringComparison.Ordinal);
            if (position < line.Length && line[position] == '"')
            {
                var valueStart = ++position;
                var valueEnd = line.IndexOf('"', valueStart);
                if (valueEnd < 0) throw new PlaybackException("UnsupportedFormat");
                output.Append(line.AsSpan(start, valueStart - start));
                output.Append(isUri ? MapReference(line[valueStart..valueEnd], effectiveUri, isManifest, children) : line[valueStart..valueEnd]);
                output.Append('"');
                position = valueEnd + 1;
            }
            else
            {
                if (isUri) throw new PlaybackException("UnsupportedFormat");
                var comma = line.IndexOf(',', position);
                position = comma < 0 ? line.Length : comma;
                output.Append(line.AsSpan(start, position - start));
            }
            while (position < line.Length && line[position] is ' ' or '\t') output.Append(line[position++]);
            if (position < line.Length)
            {
                if (line[position] != ',') throw new PlaybackException("UnsupportedFormat");
                output.Append(line[position++]);
            }
        }
        return output.ToString();
    }

    private string MapReference(string reference, Uri effectiveUri, bool isManifest, HashSet<string> children)
    {
        if (reference.Length == 0 || reference.Any(char.IsWhiteSpace) || reference.Contains("{$", StringComparison.Ordinal)
            || !Uri.TryCreate(effectiveUri, reference, out var remote)) throw new PlaybackException("UnsupportedFormat");
        EnsureSameOrigin(remote);
        remote = new UriBuilder(remote) { Fragment = string.Empty }.Uri;
        lock (_resourcesLock)
        {
            if (!_resourceTargets.TryGetValue((remote, isManifest), out var target))
            {
                if (_resources.Count >= _maximumResources) throw new PlaybackException("UnsupportedFormat");
                if (_nextResourceId == ulong.MaxValue) throw new PlaybackException("UnsupportedFormat");
                var extension = isManifest ? "m3u8" : SelectExtension(remote);
                target = $"/{_nonce}/{(_nextResourceId++).ToString("x", CultureInfo.InvariantCulture)}.{extension}";
                _resources.Add(target, new RelayResource(remote, isManifest, _clock.GetTimestamp()));
                _resourceTargets.Add((remote, isManifest), target);
            }
            _resources[target].LastUsed = _clock.GetTimestamp();
            children.Add(target);
            return $"http://{_authority}{target}";
        }
    }

    private void ReclaimRetiredResources()
    {
        // Call under the resource lock. Root reachability preserves every VOD future reference.
        // Recently used or in-flight playlists preserve their last window for buffered consumers.
        var now = _clock.GetTimestamp();
        var retained = new HashSet<string>(StringComparer.Ordinal) { _rootTarget };
        var pending = new Stack<string>();
        pending.Push(_rootTarget);
        foreach (var (target, resource) in _resources)
        {
            if (resource.IsManifest && (resource.ActiveRequests > 0
                || _clock.GetElapsedTime(resource.LastUsed, now) < ResourceGracePeriod) && retained.Add(target))
                pending.Push(target);
        }
        while (pending.TryPop(out var parent))
        {
            if (!_manifestChildren.TryGetValue(parent, out var children)) continue;
            foreach (var child in children)
                if (retained.Add(child)) pending.Push(child);
        }
        var retired = _resources.Where(pair => !retained.Contains(pair.Key) && pair.Value.ActiveRequests == 0
            && _clock.GetElapsedTime(pair.Value.LastUsed, now) >= ResourceGracePeriod).Select(pair => pair.Key).ToArray();
        foreach (var target in retired)
        {
            var resource = _resources[target];
            _resources.Remove(target);
            _resourceTargets.Remove((resource.Uri, resource.IsManifest));
            _manifestChildren.Remove(target);
        }
    }

    private void EnsureSameOrigin(Uri uri)
    {
        ValidateUri(uri);
        if (!uri.Scheme.Equals(_origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !uri.IdnHost.Equals(_origin.IdnHost, StringComparison.OrdinalIgnoreCase) || uri.Port != _origin.Port)
            throw new PlaybackException("NotAllowed");
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
            throw new PlaybackException("NotAllowed");
    }

    private static string SelectExtension(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath).TrimStart('.').ToLowerInvariant();
        return extension is "ts" or "m2ts" or "mp4" or "m4s" or "m4a" or "aac" or "mp3"
            or "vtt" or "webvtt" or "key" or "jpg" or "jpeg" ? extension : "ts";
    }

    private static string SelectContentType(string value) => value.Contains('/')
        && value.All(character => character is >= ' ' and <= '~') ? value : "application/octet-stream";

    private static async Task MonitorResetAsync(NetworkStream network, CancellationTokenSource operation)
    {
        try
        {
            // A half-close may still consume a response. Extra bytes are disallowed pipelining.
            if (await network.ReadAsync(new byte[1], operation.Token).ConfigureAwait(false) != 0) operation.Cancel();
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
        RelayResource resource;
        lock (_resourcesLock)
        {
            // Exact matching rejects alternate escaping, query strings, and absolute-form requests.
            if (!_resources.TryGetValue(first[1], out resource!)) throw new RejectedRequestException(404);
        }
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
        if (headers.TryGetValue("Content-Length", out var length) && (!TryUnsigned(length, out var declared) || declared != 0))
            throw new RejectedRequestException(400);
        var isHead = first[0] == "HEAD";
        var range = !isHead && headers.TryGetValue("Range", out var rawRange) ? ParseRange(rawRange) : null;
        lock (_resourcesLock)
        {
            // Admission refreshes access and pins the entry until the response has fully drained.
            if (!_resources.TryGetValue(first[1], out resource!)) throw new RejectedRequestException(404);
            resource.ActiveRequests++;
            resource.LastUsed = _clock.GetTimestamp();
        }
        return new RelayRequest(isHead, first[1], resource, range);
    }

    private static RelayRange ParseRange(string range)
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
            return new RelayRange(null, null, suffix);
        }
        if (!TryUnsigned(first, out var start)) throw new RejectedRequestException(416);
        if (last.Length == 0) return new RelayRange(start, null, null);
        if (!TryUnsigned(last, out var end) || end < start || end == long.MaxValue) throw new RejectedRequestException(416);
        return new RelayRange(start, end - start + 1, null);
    }

    private static (int Offset, int Length) SelectLocalRange(RelayRange range, int total)
    {
        if (total == 0) throw new RejectedRequestException(416);
        if (range.Suffix is { } suffix)
        {
            var length = (int)Math.Min(suffix, total);
            return (total - length, length);
        }
        if (range.Offset is not { } offset || offset >= total) throw new RejectedRequestException(416);
        return ((int)offset, (int)Math.Min(range.Length ?? total, total - offset));
    }

    private static async Task WriteHeadersAsync(NetworkStream stream, int status, long length,
        string? contentRange, string? contentType, CancellationToken ct)
    {
        var reason = status switch
        {
            200 => "OK", 206 => "Partial Content", 400 => "Bad Request", 404 => "Not Found", 405 => "Method Not Allowed",
            408 => "Request Timeout", 416 => "Range Not Satisfiable", 431 => "Request Header Fields Too Large", _ => "Bad Gateway"
        };
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(reason)
            .Append("\r\nContent-Length: ").Append(length.ToString(CultureInfo.InvariantCulture))
            .Append("\r\nConnection: close\r\nCache-Control: no-store\r\nAccept-Ranges: bytes\r\n");
        if (contentType is not null) builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
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
        try { await WriteHeadersAsync(stream, status, 0, null, null, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task FinishRejectedRequestAsync(TcpClient client, NetworkStream network)
    {
        // Sending FIN before a bounded drain preserves the rejection if Windows has unread bytes.
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
        lock (_clientsLock) clients = [.. _clients];
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
        await _lifetimeRegistration.DisposeAsync().ConfigureAwait(false);
        lock (_resourcesLock)
        {
            _resources.Clear();
            _resourceTargets.Clear();
            _manifestChildren.Clear();
        }
        _slots.Dispose();
        _shutdown.Dispose();
    }

    private static bool TryUnsigned(string text, out long value) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    private static bool IsHeaderName(string value) => value.Length > 0
        && value.All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character));
    public override string ToString() => nameof(HlsHttpRelay);
    private sealed class RelayResource(Uri uri, bool isManifest, long lastUsed)
    {
        internal Uri Uri { get; } = uri;
        internal bool IsManifest { get; } = isManifest;
        internal long LastUsed { get; set; } = lastUsed;
        internal int ActiveRequests { get; set; }
    }
    private sealed record RelayRange(long? Offset, long? Length, long? Suffix);
    private sealed record RelayRequest(bool IsHead, string Target, RelayResource Resource, RelayRange? Range);
    private sealed record RelayResponse(byte[] Data, int Offset, int Length, int Status, string? ContentRange, string ContentType);
    private sealed partial class RejectedRequestException(int statusCode) : Exception("The local HLS request was rejected.")
    {
        internal int StatusCode { get; } = statusCode;
    }
    private sealed partial class RelayConnection(TcpClient client)
    {
        internal TcpClient Client { get; } = client;
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
