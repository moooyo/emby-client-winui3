using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EmbyClient.NativeProbe;

/// <summary>A private, deliberately invalid MP4 representation for decoder-failure injection only.</summary>
internal sealed class InvalidMediaFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly byte[] _body = new byte[4096];
    private readonly string _path = "/" + Guid.NewGuid().ToString("N") + "/invalid.mp4";
    private readonly Task _serving;
    private int _requests;
    private int _credentialRejections;
    private int _disposed;
    internal int Requests => Volatile.Read(ref _requests);
    internal int CredentialRejections => Volatile.Read(ref _credentialRejections);
    internal Uri MediaUri { get; }

    internal InvalidMediaFixture()
    {
        Encoding.ASCII.GetBytes("SYNTHETIC INVALID MEDIA FOR NATIVE DECODER FAILURE").CopyTo(_body, 0);
        _listener.Server.ExclusiveAddressUse = true;
        _listener.Start(4);
        MediaUri = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_path}");
        _serving = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !IPAddress.IsLoopback(endpoint.Address)) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { await RespondAsync(client.GetStream(), timeout.Token); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (SocketException) when (_shutdown.IsCancellationRequested) { }
    }

    private async Task RespondAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = new byte[8192];
        var used = 0;
        while (used < bytes.Length)
        {
            if (await stream.ReadAsync(bytes.AsMemory(used, 1), ct) == 0) return;
            used++;
            if (used >= 4 && bytes[used - 4] == 13 && bytes[used - 3] == 10 && bytes[used - 2] == 13 && bytes[used - 1] == 10) break;
        }
        var lines = Encoding.ASCII.GetString(bytes, 0, used).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Any(value => value.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("X-Emby-Token:", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("X-Emby-Authorization:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("api_key", StringComparison.OrdinalIgnoreCase)))
        {
            Interlocked.Increment(ref _credentialRejections);
            await WriteAsync(stream, "403 Forbidden", "", [], ct);
            return;
        }
        if (lines.Length == 0 || lines[0] != $"GET {_path} HTTP/1.1")
        {
            await WriteAsync(stream, "404 Not Found", "", [], ct);
            return;
        }
        var range = lines.FirstOrDefault(value => value.StartsWith("Range:", StringComparison.OrdinalIgnoreCase));
        if (range is not null && !range[6..].Trim().StartsWith("bytes=0-", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(stream, "416 Range Not Satisfiable", "Content-Range: bytes */4096\r\n", [], ct);
            return;
        }
        Interlocked.Increment(ref _requests);
        await WriteAsync(stream, range is null ? "200 OK" : "206 Partial Content",
            range is null ? "" : "Content-Range: bytes 0-4095/4096\r\n", _body, ct);
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string extra, byte[] body, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Type: video/mp4\r\nX-Synthetic-Fixture: true\r\nContent-Length: {body.Length}\r\n{extra}\r\n");
        await stream.WriteAsync(header, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _shutdown.CancelAsync();
        _listener.Stop();
        await _serving;
        _shutdown.Dispose();
    }
}
