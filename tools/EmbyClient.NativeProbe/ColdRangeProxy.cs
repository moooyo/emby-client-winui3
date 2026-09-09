using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

internal sealed class ColdRangeProxy : IAsyncDisposable
{
    private const int BlockSize = 256 * 1024;
    internal const int PrefixLimit = 512 * 1024;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slots = new(8, 8);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null, ConnectTimeout = TimeSpan.FromSeconds(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly object _gate = new();
    private readonly Dictionary<string, Binding> _bindings = [];
    private readonly List<ColdRangeRecord> _records = [];
    private readonly HashSet<Task> _clients = [];
    private readonly TaskCompletionSource _releaseCold = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _accepting;
    private int _phase;
    private int _disposed;
    private int _credentialRejections;
    private int _bindingCount;
    internal long Length { get; }
    internal long MetadataStart { get; }
    internal long MetadataEnd { get; }
    internal int CredentialRejections => Volatile.Read(ref _credentialRejections);
    internal int BindingCount => Volatile.Read(ref _bindingCount);

    internal ColdRangeProxy(string filePath)
    {
        using var file = File.OpenRead(filePath);
        Length = file.Length;
        if (Length is < 2 * 1024 * 1024 or > 64 * 1024 * 1024) throw new PlaybackException("ColdRangeMediaSizeInvalid");
        var header = new byte[16];
        long position = 0;
        var found = false;
        while (position + 8 <= Length)
        {
            file.Position = position;
            file.ReadExactly(header.AsSpan(0, 8));
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = 8;
            if (size == 1)
            {
                file.ReadExactly(header.AsSpan(8, 8));
                size = checked((long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)));
                headerLength = 16;
            }
            if (size == 0) size = Length - position;
            if (size < headerLength || size > Length - position) throw new PlaybackException("ColdRangeMp4StructureInvalid");
            if (header.AsSpan(4, 4).SequenceEqual("moov"u8))
            {
                MetadataStart = position / BlockSize * BlockSize;
                MetadataEnd = Math.Min(Length, ((position + size + BlockSize - 1) / BlockSize) * BlockSize);
                found = true;
                break;
            }
            position += size;
        }
        if (!found) throw new PlaybackException("ColdRangeMp4MetadataMissing");
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(8);
        _accepting = AcceptAsync();
    }

    internal Uri Register(PlaybackEngineRequest request)
    {
        var uri = request.MediaUri;
        if (request.DeliveryMethod != PlaybackDeliveryMethod.DirectStream || !uri.IsLoopback || uri.Host != "127.0.0.1"
            || uri.Port != 18961 || uri.Scheme != "http" || uri.UserInfo.Length != 0
            || !uri.AbsolutePath.Equals("/emby/Videos/1001/stream", StringComparison.OrdinalIgnoreCase))
            throw new PlaybackException("ColdRangeOwnedSourceRequired");
        lock (_gate)
        {
            var path = "/" + Guid.NewGuid().ToString("N") + "/source.mp4";
            _bindings.Add(path, new Binding(++_bindingCount, uri, new Dictionary<string, string>(request.Headers)));
            return new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}");
        }
    }

    internal void FailColdRequests() { Volatile.Write(ref _phase, 1); _releaseCold.TrySetResult(); }
    internal void Restore() { Volatile.Write(ref _phase, 2); _releaseCold.TrySetResult(); }
    internal ColdRangeRecord[] Records() { lock (_gate) return _records.Select(value => value.Copy()).ToArray(); }

    private async Task AcceptAsync()
    {
        try
        {
            while (true)
            {
                await _slots.WaitAsync(_shutdown.Token);
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_shutdown.Token); }
                catch { _slots.Release(); throw; }
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_gate) _clients.Add(completion.Task);
                _ = ServeAsync(client, completion);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task ServeAsync(TcpClient client, TaskCompletionSource completion)
    {
        ColdRangeRecord? record = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using (client)
            {
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !IPAddress.IsLoopback(endpoint.Address)) return;
                var stream = client.GetStream();
                var header = new byte[16384];
                var used = 0;
                while (used < header.Length)
                {
                    if (await stream.ReadAsync(header.AsMemory(used, 1), timeout.Token) == 0) return;
                    used++;
                    if (used >= 4 && header[used - 4] == 13 && header[used - 3] == 10 && header[used - 2] == 13 && header[used - 1] == 10) break;
                }
                var lines = Encoding.ASCII.GetString(header, 0, used).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Any(value => value.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
                    || value.StartsWith("X-Emby-", StringComparison.OrdinalIgnoreCase)
                    || value.Contains("api_key", StringComparison.OrdinalIgnoreCase)))
                {
                    Interlocked.Increment(ref _credentialRejections);
                    await WriteHeaderAsync(stream, 403, 0, null, timeout.Token);
                    return;
                }
                var requestLine = lines[0].Split(' ');
                Binding? binding;
                lock (_gate) _bindings.TryGetValue(requestLine.Length == 3 ? requestLine[1] : "", out binding);
                if (requestLine.Length != 3 || requestLine[0] != "GET" || binding is null) return;
                var rangeText = lines.FirstOrDefault(value => value.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))?[6..].Trim();
                if (rangeText is null || !rangeText.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || rangeText.Contains(',')) return;
                var bounds = rangeText[6..].Split('-', 2);
                if (bounds.Length != 2 || !long.TryParse(bounds[0], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                    || !long.TryParse(bounds[1], NumberStyles.None, CultureInfo.InvariantCulture, out var requestedEnd)
                    || offset < 0 || requestedEnd < offset || requestedEnd - offset >= 1024 * 1024 || offset >= Length) return;
                var end = Math.Min(requestedEnd, Length - 1);
                var cold = end + 1 > PrefixLimit && !(offset >= MetadataStart && end + 1 <= MetadataEnd);
                record = new ColdRangeRecord { Binding = binding.Number, Offset = offset, EndInclusive = end,
                    IsCold = cold, PhaseAtArrival = PhaseName(Volatile.Read(ref _phase)) };
                lock (_gate)
                {
                    if (_records.Count >= 512) throw new PlaybackException("ColdRangeRequestLimitExceeded");
                    _records.Add(record);
                }
                if (cold && Volatile.Read(ref _phase) == 0)
                {
                    record.WaitedForFaultGate = true;
                    await _releaseCold.Task.WaitAsync(timeout.Token);
                }
                record.PhaseAtResponse = PhaseName(Volatile.Read(ref _phase));
                if (Volatile.Read(ref _phase) == 1)
                {
                    record.StatusCode = 503;
                    await WriteHeaderAsync(stream, 503, 0, null, timeout.Token);
                    record.Completed = true;
                    return;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, binding.Upstream);
                foreach (var pair in binding.Headers) request.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                request.Headers.Range = new RangeHeaderValue(offset, requestedEnd);
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var actual = response.Content.Headers.ContentRange;
                if (response.StatusCode != HttpStatusCode.PartialContent || actual is null || actual.From != offset || actual.To != end
                    || actual.Length != Length || !response.Headers.Contains("X-Synthetic-Fixture"))
                    throw new PlaybackException("ColdRangeUpstreamRepresentationMismatch");
                record.StatusCode = (int)response.StatusCode;
                await WriteHeaderAsync(stream, 206, end - offset + 1, $"bytes {offset}-{end}/{Length}", timeout.Token);
                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[65536];
                while (true)
                {
                    var read = await body.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                    record.BytesForwarded += read;
                }
                if (record.BytesForwarded != end - offset + 1) throw new PlaybackException("ColdRangeForwardLengthMismatch");
                record.Completed = true;
            }
        }
        catch (OperationCanceledException) { if (record is not null) record.ErrorCode = "ProxyRequestCanceled"; }
        catch (IOException) { if (record is not null) record.ErrorCode = "ProxyClientClosed"; }
        catch (SocketException) { if (record is not null) record.ErrorCode = "ProxyClientClosed"; }
        catch (PlaybackException error) { if (record is not null) record.ErrorCode = error.ErrorCode; }
        catch { if (record is not null) record.ErrorCode = "ProxyRequestFailed"; }
        finally
        {
            client.Dispose();
            _slots.Release();
            lock (_gate) _clients.Remove(completion.Task);
            completion.TrySetResult();
        }
    }

    private static Task WriteHeaderAsync(NetworkStream stream, int status, long length, string? range, CancellationToken ct)
    {
        var reason = status switch { 206 => "Partial Content", 503 => "Service Unavailable", _ => "Forbidden" };
        var extra = range is null ? "" : "Content-Range: " + range + "\r\n";
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nConnection: close\r\nCache-Control: no-store\r\nContent-Type: video/mp4\r\nContent-Length: {length}\r\n{extra}\r\n");
        return stream.WriteAsync(bytes, ct).AsTask();
    }
    private static string PhaseName(int phase) => phase switch { 0 => "PrefixAndMetadataOnly", 1 => "InjectedHttp503", _ => "Restored" };
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _accepting;
        Task[] clients;
        lock (_gate) clients = [.. _clients];
        await Task.WhenAll(clients);
        _http.Dispose();
        lock (_gate) _bindings.Clear();
        _shutdown.Dispose();
        _slots.Dispose();
    }
    private sealed record Binding(int Number, Uri Upstream, Dictionary<string, string> Headers);
}

internal sealed class ColdRangeRecord
{
    public int Binding { get; init; }
    public long Offset { get; init; }
    public long EndInclusive { get; init; }
    public bool IsCold { get; init; }
    public string PhaseAtArrival { get; init; } = "";
    public string? PhaseAtResponse { get; set; }
    public bool WaitedForFaultGate { get; set; }
    public int StatusCode { get; set; }
    public long BytesForwarded { get; set; }
    public bool Completed { get; set; }
    public string? ErrorCode { get; set; }
    internal ColdRangeRecord Copy() => (ColdRangeRecord)MemberwiseClone();
}
