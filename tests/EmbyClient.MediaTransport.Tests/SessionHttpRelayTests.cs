using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.MediaTransport.Tests;

public sealed class SessionHttpRelayTests
{
    private const int BlockSize = 256 * 1024;
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Local_endpoint_uses_loopback_an_ephemeral_port_and_a_private_nonce_without_prefetching_the_file()
    {
        var data = CreateData(BlockSize * 4 + 17);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        var upstream = new Uri(server.BaseUri, "media?api_key=query-secret");

        await using var relay = await SessionHttpRelay.OpenAsync(transport, upstream, "mp4", TestCancellation);

        Assert.Equal("http", relay.LocalUri.Scheme);
        Assert.Equal("127.0.0.1", relay.LocalUri.Host);
        Assert.InRange(relay.LocalUri.Port, 1, 65535);
        Assert.NotEqual(server.BaseUri.Port, relay.LocalUri.Port);
        Assert.Matches("(^|/)[0-9a-fA-F]{64}(/|$)", relay.LocalUri.AbsolutePath);
        Assert.Empty(relay.LocalUri.Query);
        Assert.Empty(relay.LocalUri.Fragment);
        Assert.Empty(relay.LocalUri.UserInfo);
        Assert.DoesNotContain("query-secret", relay.LocalUri.AbsoluteUri);
        Assert.DoesNotContain("header-secret", relay.LocalUri.AbsoluteUri);
        Assert.DoesNotContain("api_key", relay.LocalUri.AbsoluteUri);
        Assert.Equal(data.LongLength, relay.SourceLength);
        Assert.Equal("video/mp4", relay.ContentType);
        var probe = Assert.Single(server.Requests);
        Assert.Equal($"bytes=0-{BlockSize - 1}", probe.Headers["Range"]);
        Assert.Equal("header-secret", probe.Headers["X-Emby-Token"]);
    }

    [Fact]
    public async Task Get_returns_the_complete_representation_with_explicit_length_and_range_support()
    {
        var data = CreateData(BlockSize + 23);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);

        var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(data.Length.ToString(CultureInfo.InvariantCulture), response.Headers["Content-Length"]);
        Assert.Equal("video/mp4", response.Headers["Content-Type"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.Equal("close", response.Headers["Connection"]);
        Assert.False(response.Headers.ContainsKey("Content-Range"));
        Assert.False(response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.False(response.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(response.Headers.ContainsKey("Authorization"));
        Assert.Equal(data, response.Body);
        Assert.All(server.Requests, request => Assert.True(request.Headers.ContainsKey("Range")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bytes=5-9")]
    [InlineData("bytes=999999-")]
    public async Task Head_ignores_range_and_returns_full_length_without_a_body_or_an_extra_upstream_read(string? range)
    {
        var data = CreateData(BlockSize + 23);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);

        var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri, method: "HEAD",
            extraHeaders: range is null ? null : $"Range: {range}\r\n"));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(data.Length.ToString(CultureInfo.InvariantCulture), response.Headers["Content-Length"]);
        Assert.Equal("video/mp4", response.Headers["Content-Type"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.False(response.Headers.ContainsKey("Content-Range"));
        Assert.Empty(response.Body);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("bytes=4-11", 4, 8)]
    [InlineData("bytes=4-", 4, 60)]
    [InlineData("bytes=-9", 55, 9)]
    [InlineData("bytes=60-999", 60, 4)]
    [InlineData("bytes=-999", 0, 64)]
    public async Task Single_byte_ranges_return_exact_partial_content(string range, int start, int length)
    {
        var data = CreateData(64);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);

        var response = await SendAsync(relay.LocalUri,
            BuildRequest(relay.LocalUri, extraHeaders: $"Range: {range}\r\n"));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal($"bytes {start}-{start + length - 1}/{data.Length}", response.Headers["Content-Range"]);
        Assert.Equal(length.ToString(CultureInfo.InvariantCulture), response.Headers["Content-Length"]);
        Assert.Equal("video/mp4", response.Headers["Content-Type"]);
        Assert.Equal(data.AsSpan(start, length).ToArray(), response.Body);
    }

    [Theory]
    [InlineData("bytes=64-")]
    [InlineData("bytes=10-9")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("items=0-1")]
    [InlineData("bytes=9223372036854775808-")]
    public async Task Unsatisfiable_malformed_or_multiple_ranges_return_416_and_the_source_length(string range)
    {
        var data = CreateData(64);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);

        var response = await SendAsync(relay.LocalUri,
            BuildRequest(relay.LocalUri, extraHeaders: $"Range: {range}\r\n"));

        Assert.Equal(416, response.StatusCode);
        Assert.Equal($"bytes */{data.Length}", response.Headers["Content-Range"]);
        Assert.Empty(response.Body);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("wrong-nonce", 404)]
    [InlineData("path-suffix", 404)]
    [InlineData("encoded-nonce", 404)]
    [InlineData("query", 404)]
    [InlineData("absolute-form", 404)]
    [InlineData("method", 405)]
    [InlineData("localhost-host", 400)]
    [InlineData("wrong-host", 400)]
    [InlineData("missing-port", 400)]
    [InlineData("missing-host", 400)]
    public async Task Only_the_exact_nonce_path_method_and_host_are_accepted(string scenario, int status)
    {
        var data = CreateData(64);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);
        var uri = relay.LocalUri;
        var nonce = Regex.Match(uri.AbsolutePath, "[0-9a-fA-F]{64}").Value;
        Assert.Equal(64, nonce.Length);
        var alteredNonce = (nonce[0] == '0' ? "1" : "0") + nonce[1..];
        var request = scenario switch
        {
            "wrong-nonce" => BuildRequest(uri, target: uri.AbsolutePath.Replace(nonce, alteredNonce, StringComparison.Ordinal)),
            "path-suffix" => BuildRequest(uri, target: uri.AbsolutePath + "/extra"),
            "encoded-nonce" => BuildRequest(uri, target: uri.AbsolutePath.Replace(nonce,
                $"%{(int)nonce[0]:X2}{nonce[1..]}", StringComparison.Ordinal)),
            "query" => BuildRequest(uri, target: uri.AbsolutePath + "?key=value"),
            "absolute-form" => BuildRequest(uri, target: uri.AbsoluteUri),
            "method" => BuildRequest(uri, method: "POST"),
            "localhost-host" => BuildRequest(uri, host: $"localhost:{uri.Port}"),
            "wrong-host" => BuildRequest(uri, host: $"127.0.0.2:{uri.Port}"),
            "missing-port" => BuildRequest(uri, host: "127.0.0.1"),
            "missing-host" => $"GET {uri.AbsolutePath} HTTP/1.1\r\nConnection: close\r\n\r\n",
            _ => throw new InvalidOperationException("Unknown request scenario.")
        };

        var response = await SendAsync(uri, request);

        Assert.Equal(status, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("duplicate-host")]
    [InlineData("duplicate-range")]
    [InlineData("duplicate-header")]
    [InlineData("content-length-body")]
    [InlineData("transfer-encoding")]
    [InlineData("non-ascii")]
    [InlineData("malformed-header")]
    [InlineData("folded-header")]
    public async Task Ambiguous_headers_bodies_and_non_ascii_requests_are_rejected_without_upstream_reads(string scenario)
    {
        var data = CreateData(64);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);
        var headers = scenario switch
        {
            "duplicate-host" => $"Host: {relay.LocalUri.Authority}\r\n",
            "duplicate-range" => "Range: bytes=0-1\r\nRange: bytes=4-5\r\n",
            "duplicate-header" => "X-Test: first\r\nx-test: second\r\n",
            "content-length-body" => "Content-Length: 1\r\n",
            "transfer-encoding" => "Transfer-Encoding: chunked\r\n",
            "non-ascii" => "X-Test: caf\u00e9\r\n",
            "malformed-header" => "Missing-Colon\r\n",
            "folded-header" => "X-Test: first\r\n second\r\n",
            _ => throw new InvalidOperationException("Unknown header scenario.")
        };
        var body = scenario switch
        {
            "content-length-body" => "x",
            "transfer-encoding" => "0\r\n\r\n",
            _ => string.Empty
        };

        var response = await SendAsync(relay.LocalUri,
            BuildRequest(relay.LocalUri, extraHeaders: headers) + body);

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(16 * 1024, 200)]
    [InlineData(16 * 1024 + 1, 431)]
    public async Task The_16_kib_header_limit_accepts_the_boundary_and_rejects_the_next_byte(int headerBytes, int status)
    {
        var data = CreateData(64);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);

        var emptyRequest = BuildRequest(relay.LocalUri, extraHeaders: "X-Padding: \r\n");
        var padding = new string('a', headerBytes - Encoding.Latin1.GetByteCount(emptyRequest));
        var request = BuildRequest(relay.LocalUri, extraHeaders: $"X-Padding: {padding}\r\n");
        Assert.Equal(headerBytes, Encoding.Latin1.GetByteCount(request));

        var response = await SendAsync(relay.LocalUri, request);

        Assert.Equal(status, response.StatusCode);
        if (status == 200)
            Assert.Equal(data, response.Body);
        else
        {
            Assert.Empty(response.Body);
            Assert.Single(server.Requests);
        }
    }

    [Fact]
    public async Task Four_active_clients_hold_the_capacity_and_disposal_drains_pending_reads_without_owning_the_transport()
    {
        var data = CreateData(BlockSize * 2);
        var allReadsArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaRequests = 0;
        await using var server = new LoopbackServer((request, _) =>
        {
            if (request.Target == "/health") return Task.FromResult(new LoopbackResponse(200, [7]));
            var response = RangeResponse(request, data);
            var count = Interlocked.Increment(ref mediaRequests);
            if (count == 1) return Task.FromResult(response);
            if (count == 5) allReadsArrived.TrySetResult();
            return Task.FromResult(response with
            {
                Body = [],
                DeclaredContentLength = response.Body.Length,
                HoldBodyOpen = true
            });
        });
        await using var transport = CreateTransport(server.BaseUri);
        var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation);
        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 4; index++)
                clients.Add(await ConnectAndWriteAsync(relay.LocalUri,
                    BuildRequest(relay.LocalUri, extraHeaders: $"Range: bytes={BlockSize}-{BlockSize + 3}\r\n")));
            await allReadsArrived.Task.WaitAsync(Deadline, TestCancellation);
            Assert.Equal(5, Volatile.Read(ref mediaRequests));
            var waiting = await ConnectAndWriteAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
            clients.Add(waiting);

            using (var observation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation))
            {
                observation.CancelAfter(TimeSpan.FromMilliseconds(500));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    waiting.GetStream().ReadAsync(new byte[1], observation.Token).AsTask());
                Assert.False(TestCancellation.IsCancellationRequested);
            }
            Assert.Equal(5, Volatile.Read(ref mediaRequests));

            var connectionEnds = clients.Select(client => ReadToEndWithDeadlineAsync(client.GetStream())).ToArray();
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
            await Task.WhenAll(connectionEnds).WaitAsync(Deadline, TestCancellation);
            Assert.Equal(5, Volatile.Read(ref mediaRequests));
            var health = await transport.DownloadAsync(new Uri(server.BaseUri, "health"), ct: TestCancellation);
            Assert.Equal(new byte[] { 7 }, health.Data);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_or_lifetime_cancellation_closes_clients_with_incomplete_headers(bool cancelLifetime)
    {
        var data = CreateData(64);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", lifetime.Token);
        try
        {
            using var client = await ConnectAndWriteAsync(relay.LocalUri,
                $"GET {relay.LocalUri.AbsolutePath} HTTP/1.1\r\nHost: {relay.LocalUri.Authority}\r\nX-Pending: ");
            var connectionEnd = ReadToEndWithDeadlineAsync(client.GetStream());

            if (cancelLifetime)
            {
                lifetime.Cancel();
                await connectionEnd.WaitAsync(Deadline, TestCancellation);
            }
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);

            await connectionEnd.WaitAsync(Deadline, TestCancellation);
            Assert.Single(server.Requests);
        }
        finally
        {
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
        }
    }

    [Theory]
    [InlineData(503, "NetworkFailure")]
    [InlineData(401, "AuthenticationRequired")]
    [InlineData(403, "NotAllowed")]
    [InlineData(416, "UnsupportedFormat")]
    public async Task A_cold_upstream_range_failure_preserves_its_category_and_notifies_only_once(int status, string expectedCode)
    {
        var data = CreateData(BlockSize * 2);
        var failures = new ConcurrentQueue<string>();
        await using var server = new LoopbackServer((request, _) => Task.FromResult(
            request.Headers["Range"].StartsWith($"bytes={BlockSize}-", StringComparison.Ordinal)
                ? new LoopbackResponse(status, []) : RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation, failures.Enqueue);
        Assert.Empty(failures);

        var request = BuildRequest(relay.LocalUri, extraHeaders: $"Range: bytes={BlockSize}-{BlockSize + 3}\r\n");
        var first = await SendAsync(relay.LocalUri, request);
        var second = await SendAsync(relay.LocalUri, request);

        Assert.Equal(502, first.StatusCode);
        Assert.Equal(502, second.StatusCode);
        Assert.Empty(first.Body);
        Assert.Equal(expectedCode, Assert.Single(failures));
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task An_upstream_failure_after_local_headers_and_bytes_still_reports_the_confirmed_cause()
    {
        var data = CreateData(BlockSize * 2);
        var failures = new ConcurrentQueue<string>();
        await using var server = new LoopbackServer((request, _) => Task.FromResult(
            request.Headers["Range"].StartsWith($"bytes={BlockSize}-", StringComparison.Ordinal)
                ? new LoopbackResponse(503, []) : RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri,
            BuildRequest(relay.LocalUri, extraHeaders: $"Range: bytes=0-{data.Length - 1}\r\n"));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal(data.Length.ToString(CultureInfo.InvariantCulture), response.Headers["Content-Length"]);
        Assert.Equal(data.AsSpan(0, BlockSize).ToArray(), response.Body);
        Assert.Equal("NetworkFailure", Assert.Single(failures));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_a_pending_upstream_read_does_not_report_a_playback_failure(bool cancelLifetime)
    {
        var data = CreateData(BlockSize * 2);
        var failures = new ConcurrentQueue<string>();
        var readArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer((request, _) =>
        {
            var response = RangeResponse(request, data);
            if (!request.Headers["Range"].StartsWith($"bytes={BlockSize}-", StringComparison.Ordinal))
                return Task.FromResult(response);
            readArrived.TrySetResult();
            return Task.FromResult(response with { Body = [], DeclaredContentLength = response.Body.Length, HoldBodyOpen = true });
        });
        await using var transport = CreateTransport(server.BaseUri);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", lifetime.Token, failures.Enqueue);
        try
        {
            using var client = await ConnectAndWriteAsync(relay.LocalUri,
                BuildRequest(relay.LocalUri, extraHeaders: $"Range: bytes={BlockSize}-{BlockSize + 3}\r\n"));
            var connectionEnd = ReadToEndWithDeadlineAsync(client.GetStream());
            await readArrived.Task.WaitAsync(Deadline, TestCancellation);
            if (cancelLifetime) lifetime.Cancel();
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
            await connectionEnd;
            Assert.Empty(failures);
        }
        finally { await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation); }
    }

    [Fact]
    public async Task Downstream_resets_release_connection_capacity_without_reporting_an_upstream_failure()
    {
        var data = CreateData(BlockSize * 8);
        var failures = new ConcurrentQueue<string>();
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var relay = await SessionHttpRelay.OpenAsync(transport, server.BaseUri, "mp4", TestCancellation, failures.Enqueue);
        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var client = new TcpClient(AddressFamily.InterNetwork) { ReceiveBufferSize = 1024 };
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, relay.LocalUri.Port, TestCancellation).AsTask().WaitAsync(Deadline, TestCancellation);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(BuildRequest(relay.LocalUri)), TestCancellation);
                Assert.Equal(1, await stream.ReadAsync(new byte[1], TestCancellation).AsTask().WaitAsync(Deadline, TestCancellation));
            }
            var waitingHead = SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri, method: "HEAD"));
            Assert.NotSame(waitingHead, await Task.WhenAny(waitingHead, Task.Delay(100, TestCancellation)));
            foreach (var client in clients)
            {
                client.LingerState = new LingerOption(true, 0);
                client.Dispose();
            }
            Assert.Equal(200, (await waitingHead.WaitAsync(Deadline, TestCancellation)).StatusCode);
            Assert.Empty(failures);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    private static string BuildRequest(Uri uri, string method = "GET", string? target = null,
        string? extraHeaders = null, string? host = null)
        => $"{method} {target ?? uri.AbsolutePath} HTTP/1.1\r\nHost: {host ?? uri.Authority}\r\n"
            + extraHeaders + "Connection: close\r\n\r\n";

    private static async Task<RawResponse> SendAsync(Uri uri, string request)
    {
        using var client = await ConnectAndWriteAsync(uri, request);
        var bytes = await ReadToEndWithDeadlineAsync(client.GetStream());
        var headerEnd = bytes.AsSpan().IndexOf("\r\n\r\n"u8);
        Assert.True(headerEnd >= 0, "The relay did not return a complete HTTP response header.");
        var lines = Encoding.Latin1.GetString(bytes, 0, headerEnd).Split("\r\n", StringSplitOptions.None);
        var statusLine = lines[0].Split(' ', 3);
        Assert.Equal(3, statusLine.Length);
        Assert.Equal("HTTP/1.1", statusLine[0]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var separator = line.IndexOf(':');
            Assert.True(separator > 0, "The relay returned an invalid header line.");
            Assert.True(headers.TryAdd(line[..separator], line[(separator + 1)..].Trim(' ', '\t')),
                "The relay returned a duplicate response header.");
        }
        return new RawResponse(int.Parse(statusLine[1], CultureInfo.InvariantCulture), headers,
            bytes.AsSpan(headerEnd + 4).ToArray());
    }

    private static async Task<TcpClient> ConnectAndWriteAsync(Uri uri, string request)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        timeout.CancelAfter(Deadline);
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, uri.Port, timeout.Token);
            await client.GetStream().WriteAsync(Encoding.Latin1.GetBytes(request), timeout.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> ReadToEndWithDeadlineAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        timeout.CancelAfter(Deadline);
        using var result = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                Assert.True(result.Length + read <= MaximumResponseBytes, "The relay response exceeded the test limit.");
                result.Write(buffer, 0, read);
            }
        }
        catch (IOException exception) when (exception.InnerException is SocketException)
        {
            // Closing a rejected request or an active relay can reset the peer connection.
        }
        return result.ToArray();
    }

    private static ScopedMediaTransport CreateTransport(Uri origin)
        => new(origin, new Dictionary<string, string> { ["X-Emby-Token"] = "header-secret" }, TestCancellation);

    private static byte[] CreateData(int length)
        => Enumerable.Range(0, length).Select(index => (byte)(index % 251)).ToArray();

    private static LoopbackResponse RangeResponse(LoopbackRequest request, byte[] data)
    {
        var bounds = request.Headers["Range"]["bytes=".Length..].Split('-');
        var start = int.Parse(bounds[0], CultureInfo.InvariantCulture);
        var end = string.IsNullOrEmpty(bounds[1]) ? data.Length - 1
            : Math.Min(int.Parse(bounds[1], CultureInfo.InvariantCulture), data.Length - 1);
        return new LoopbackResponse(206, data.AsSpan(start, end - start + 1).ToArray(), new Dictionary<string, string>
        {
            ["Content-Range"] = $"bytes {start}-{end}/{data.Length}",
            ["Content-Type"] = "video/mp4",
            ["ETag"] = "\"stable-version\""
        });
    }

    private sealed record RawResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body);
}
