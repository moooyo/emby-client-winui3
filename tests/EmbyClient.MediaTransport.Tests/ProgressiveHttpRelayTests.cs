using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.MediaTransport.Tests;

public sealed class ProgressiveHttpRelayTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [Fact(Timeout = 15000)]
    public async Task Opening_creates_a_private_loopback_capability_without_prefetching_upstream()
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [1])));
        await using var transport = CreateTransport(upstream.BaseUri);

        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "stream.mp4?api_key=query-secret"), "mp4", TestContext.Current.CancellationToken);

        Assert.Empty(upstream.Requests);
        Assert.Equal("http", relay.LocalUri.Scheme);
        Assert.Equal("127.0.0.1", relay.LocalUri.Host);
        Assert.InRange(relay.LocalUri.Port, 1, 65535);
        Assert.NotEqual(upstream.BaseUri.Port, relay.LocalUri.Port);
        Assert.Matches("(^|/)[0-9a-fA-F]{64}(/|$)", relay.LocalUri.AbsolutePath);
        Assert.Empty(relay.LocalUri.Query);
        Assert.Empty(relay.LocalUri.UserInfo);
        Assert.DoesNotContain("secret", relay.LocalUri.AbsoluteUri);
    }

    [Theory(Timeout = 15000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_unknown_length_stream_delivers_its_first_bytes_before_the_upstream_body_can_finish(bool upstreamChunked)
    {
        var first = Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray();
        byte[] last = [9, 8, 7, 6];
        var finish = Signal();
        var firstWritten = Signal();
        await using var upstream = new StreamingServer(async (_, stream, token) =>
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nConnection: close\r\n"
                + (upstreamChunked ? "Transfer-Encoding: chunked\r\n" : "") + "\r\n", token);
            if (upstreamChunked) await WriteChunkAsync(stream, first, token);
            else await stream.WriteAsync(first, token);
            await stream.FlushAsync(token);
            firstWritten.TrySetResult();
            await finish.Task.WaitAsync(token);
            if (upstreamChunked)
            {
                await WriteChunkAsync(stream, last, token);
                await WriteAsciiAsync(stream, "0\r\n\r\n", token);
            }
            else await stream.WriteAsync(last, token);
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        var failures = new ConcurrentQueue<string>();
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);
        using var client = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));
        await firstWritten.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        var headers = await ReadHeadersAsync(client.GetStream());
        Assert.Equal(200, headers.Status);
        Assert.False(headers.Headers.ContainsKey("Content-Length"));
        Assert.Equal("chunked", headers.Headers["Transfer-Encoding"]);
        Assert.Equal("video/mp4", headers.Headers["Content-Type"]);
        using var delivered = new MemoryStream();
        while (delivered.Length < first.Length)
        {
            var chunk = await ReadChunkAsync(client.GetStream());
            Assert.NotEmpty(chunk);
            delivered.Write(chunk);
        }
        Assert.Equal(first, delivered.ToArray());
        Assert.False(finish.Task.IsCompleted);

        finish.TrySetResult();
        using var tail = new MemoryStream();
        while (true)
        {
            var chunk = await ReadChunkAsync(client.GetStream());
            if (chunk.Length == 0) break;
            tail.Write(chunk);
        }
        Assert.Equal(last, tail.ToArray());
        Assert.Empty(failures);
    }

    [Fact(Timeout = 15000)]
    public async Task An_upstream_ignoring_range_stays_200_and_preserves_the_complete_representation()
    {
        byte[] data = [0, 1, 2, 3, 4, 5];
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, data,
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" })));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", TestContext.Current.CancellationToken);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, headers: "Range: bytes=3-\r\n"));

        Assert.Equal(200, response.Status);
        Assert.Equal(data, response.Body);
        Assert.Equal("6", response.Headers["Content-Length"]);
        Assert.False(response.Headers.ContainsKey("Content-Range"));
        Assert.False(response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Equal("bytes=3-", Assert.Single(upstream.Requests).Headers["Range"]);
    }

    [Theory(Timeout = 15000)]
    [InlineData("bytes=4-7")]
    [InlineData("bytes=4-")]
    [InlineData("bytes=-4")]
    public async Task Valid_partial_content_preserves_the_upstream_status_range_length_and_bytes(string range)
    {
        byte[] data = [4, 5, 6, 7];
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(206, data,
            new Dictionary<string, string> { ["Content-Range"] = "bytes 4-7/8", ["Content-Type"] = "video/mp4" })));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", TestContext.Current.CancellationToken);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, headers: $"Range: {range}\r\n"));

        Assert.Equal(206, response.Status);
        Assert.Equal("bytes 4-7/8", response.Headers["Content-Range"]);
        Assert.Equal("4", response.Headers["Content-Length"]);
        Assert.Equal(data, response.Body);
        Assert.Equal(range, Assert.Single(upstream.Requests).Headers["Range"]);
    }

    [Theory(Timeout = 15000)]
    [InlineData(null)]
    [InlineData("bytes=4-7")]
    public async Task Head_uses_an_upstream_head_without_range_and_returns_metadata_without_a_body(string? range)
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [],
            new Dictionary<string, string> { ["Content-Type"] = "video/mp4" }, DeclaredContentLength: 123)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", TestContext.Current.CancellationToken);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, "HEAD", headers: range is null ? null : $"Range: {range}\r\n"));

        Assert.Equal(200, response.Status);
        Assert.Equal("123", response.Headers["Content-Length"]);
        Assert.Empty(response.Body);
        var request = Assert.Single(upstream.Requests);
        Assert.Equal("HEAD", request.Method);
        Assert.False(request.Headers.ContainsKey("Range"));
    }

    [Fact(Timeout = 15000)]
    public async Task An_upstream_unsatisfiable_range_remains_416_without_becoming_a_playback_failure()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(416, [],
            new Dictionary<string, string> { ["Content-Range"] = "bytes */64" })));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, headers: "Range: bytes=128-\r\n"));

        Assert.Equal(416, response.Status);
        Assert.Equal("bytes */64", response.Headers["Content-Range"]);
        Assert.Empty(response.Body);
        Assert.Empty(failures);
    }

    [Theory(Timeout = 15000)]
    [InlineData("conflicting-length")]
    [InlineData("unsupported-coding")]
    public async Task Ambiguous_or_unsupported_transfer_framing_is_rejected_before_success_headers_or_media_bytes(string framing)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200,
            "6\r\nabcdef\r\n0\r\n\r\n"u8.ToArray(), new Dictionary<string, string>
            {
                ["Transfer-Encoding"] = framing == "conflicting-length" ? "chunked" : "gzip, chunked"
            }, DeclaredContentLength: framing == "conflicting-length" ? 3 : -1)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri));

        Assert.Equal(502, response.Status);
        Assert.Empty(response.Body);
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
        Assert.Single(upstream.Requests);
    }

    [Theory(Timeout = 15000)]
    [InlineData("chunked,chunked")]
    [InlineData("chunked;foo=bar")]
    [InlineData("chunked\r\nTransfer-Encoding: @")]
    [InlineData("chunked\r\nTransfer-Encoding: gzip;broken=")]
    public async Task Raw_duplicate_parameterized_and_invalid_transfer_codings_cannot_hide_behind_a_valid_chunked_value(string rawCoding)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new StreamingServer(async (_, stream, token) =>
        {
            await WriteAsciiAsync(stream, $"HTTP/1.1 200 OK\r\nTransfer-Encoding: {rawCoding}\r\n"
                + "Connection: close\r\n\r\n6\r\nabcdef\r\n0\r\n\r\n", token);
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri));

        Assert.Equal(502, response.Status);
        Assert.Empty(response.Body);
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
    }

    [Theory(Timeout = 15000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Http_10_unknown_length_get_is_rejected_before_an_ambiguous_connection_close_can_claim_success(bool chunked)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200,
            chunked ? "6\r\nabcdef\r\n"u8.ToArray() : "abcdef"u8.ToArray(),
            chunked ? new Dictionary<string, string> { ["Transfer-Encoding"] = "chunked" } : null,
            DeclaredContentLength: -1)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, version: "HTTP/1.0"));

        Assert.Equal(502, response.Status);
        Assert.Empty(response.Body);
        Assert.False(response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
    }

    [Fact(Timeout = 15000)]
    public async Task Http_10_known_length_get_keeps_the_real_length_and_complete_body()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, "abcdef"u8.ToArray())));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, version: "HTTP/1.0"));

        Assert.Equal(200, response.Status);
        Assert.Equal("6", response.Headers["Content-Length"]);
        Assert.Equal("abcdef"u8.ToArray(), response.Body);
        Assert.False(response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Empty(failures);
    }

    [Fact(Timeout = 15000)]
    public async Task Http_10_head_may_return_metadata_without_a_known_length_because_it_has_no_body()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [], DeclaredContentLength: -1)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, "HEAD", version: "HTTP/1.0"));

        Assert.Equal(200, response.Status);
        Assert.Empty(response.Body);
        Assert.False(response.Headers.ContainsKey("Transfer-Encoding"));
        Assert.Equal("HEAD", Assert.Single(upstream.Requests).Method);
        Assert.Empty(failures);
    }

    [Theory(Timeout = 15000)]
    [InlineData("path", 404)]
    [InlineData("query", 404)]
    [InlineData("absolute", 404)]
    [InlineData("method", 405)]
    [InlineData("host", 400)]
    [InlineData("duplicate-host", 400)]
    [InlineData("duplicate-range", 400)]
    [InlineData("body", 400)]
    [InlineData("transfer-encoding", 400)]
    public async Task Requests_outside_the_local_capability_are_rejected_before_contacting_upstream(string scenario, int status)
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [1])));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", TestContext.Current.CancellationToken);
        var request = scenario switch
        {
            "path" => Request(relay.LocalUri, target: "/wrong-nonce/stream.mp4"),
            "query" => Request(relay.LocalUri, target: relay.LocalUri.AbsolutePath + "?unexpected=true"),
            "absolute" => Request(relay.LocalUri, target: relay.LocalUri.AbsoluteUri),
            "method" => Request(relay.LocalUri, "POST"),
            "host" => Request(relay.LocalUri, host: $"localhost:{relay.LocalUri.Port}"),
            "duplicate-host" => Request(relay.LocalUri, headers: $"Host: {relay.LocalUri.Authority}\r\n"),
            "duplicate-range" => Request(relay.LocalUri, headers: "Range: bytes=0-1\r\nRange: bytes=2-3\r\n"),
            "body" => Request(relay.LocalUri, headers: "Content-Length: 1\r\n") + "x",
            "transfer-encoding" => Request(relay.LocalUri, headers: "Transfer-Encoding: chunked\r\n") + "0\r\n\r\n",
            _ => throw new InvalidOperationException("Unknown request scenario.")
        };

        var response = await SendAsync(relay.LocalUri, request);

        Assert.Equal(status, response.Status);
        Assert.Empty(upstream.Requests);
    }

    [Theory(Timeout = 15000)]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=7-4")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=9223372036854775808-")]
    [InlineData("items=0-1")]
    public async Task Invalid_or_multiple_ranges_do_not_start_an_upstream_stream(string range)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [1])));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri, headers: $"Range: {range}\r\n"));

        Assert.Contains(response.Status, new[] { 400, 416 });
        Assert.Empty(upstream.Requests);
        Assert.Empty(failures);
    }

    [Theory(Timeout = 15000)]
    [InlineData(16 * 1024, 200)]
    [InlineData(16 * 1024 + 1, 431)]
    public async Task The_request_header_budget_has_an_exact_16_kib_boundary(int bytes, int status)
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [1])));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", TestContext.Current.CancellationToken);
        var empty = Request(relay.LocalUri, headers: "X-Padding: \r\n");
        var request = Request(relay.LocalUri, headers: "X-Padding: " + new string('a', bytes - Encoding.ASCII.GetByteCount(empty)) + "\r\n");

        var response = await SendAsync(relay.LocalUri, request);

        Assert.Equal(status, response.Status);
        Assert.Equal(status == 200 ? 1 : 0, upstream.Requests.Count);
    }

    [Theory(Timeout = 15000)]
    [InlineData(503, "NetworkFailure")]
    [InlineData(401, "AuthenticationRequired")]
    [InlineData(403, "NotAllowed")]
    public async Task Upstream_errors_keep_the_status_report_one_sanitized_failure_and_hide_the_error_body(int status, string code)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(status,
            Encoding.UTF8.GetBytes("server-error-with-api_key=private-secret"))));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var first = await SendAsync(relay.LocalUri, Request(relay.LocalUri));
        var second = await SendAsync(relay.LocalUri, Request(relay.LocalUri));

        Assert.Equal(status, first.Status);
        Assert.Equal(status, second.Status);
        Assert.Empty(first.Body);
        Assert.Empty(second.Body);
        Assert.Equal(code, Assert.Single(failures));
        Assert.Equal(2, upstream.Requests.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task A_truncated_successful_representation_reports_failure_after_the_real_partial_body()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [1, 2, 3],
            DeclaredContentLength: 64)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri));

        Assert.Equal(200, response.Status);
        Assert.Equal("64", response.Headers["Content-Length"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, response.Body);
        Assert.Equal("NetworkFailure", Assert.Single(failures));
    }

    [Fact(Timeout = 15000)]
    public async Task A_truncated_chunked_upstream_is_aborted_without_forging_a_successful_terminal_chunk()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200,
            "3\r\nabc\r\n"u8.ToArray(), new Dictionary<string, string> { ["Transfer-Encoding"] = "chunked" }, DeclaredContentLength: -1)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);
        using var client = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));

        var headers = await ReadHeadersAsync(client.GetStream());
        var wireBody = await ReadToEndAsync(client.GetStream());

        Assert.Equal(200, headers.Status);
        Assert.Equal("chunked", headers.Headers["Transfer-Encoding"]);
        Assert.Contains("abc", Encoding.ASCII.GetString(wireBody));
        Assert.False(wireBody.AsSpan().EndsWith("0\r\n\r\n"u8));
        Assert.Equal("NetworkFailure", Assert.Single(failures));
    }

    [Fact(Timeout = 15000)]
    public async Task A_client_may_half_close_its_request_side_and_still_receive_the_complete_response()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [1, 2, 3, 4])));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);
        using var client = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));

        var stream = client.GetStream();
        client.Client.Shutdown(SocketShutdown.Send);
        var headers = await ReadHeadersAsync(stream);
        var body = await ReadToEndAsync(stream);

        Assert.Equal(200, headers.Status);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, body);
        Assert.Empty(failures);
    }

    [Fact(Timeout = 15000)]
    public async Task Redirects_scope_credentials_at_every_hop_and_do_not_forward_downstream_identity()
    {
        await using var destination = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [7, 8, 9])));
        var target = new Uri(destination.BaseUri, "media?api_key=external-secret&%58-Emby-Token=other-secret&keep=value");
        await using var origin = new LoopbackServer((request, _) => Task.FromResult(request.Target.StartsWith("/start", StringComparison.Ordinal)
            ? new LoopbackResponse(307, [], new Dictionary<string, string> { ["Location"] = "/same?api_key=origin-secret" })
            : new LoopbackResponse(302, [], new Dictionary<string, string> { ["Location"] = target.AbsoluteUri, ["Set-Cookie"] = "credential=cookie-secret" })));
        await using var transport = CreateTransport(origin.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, new Uri(origin.BaseUri, "start"), "mp4", TestContext.Current.CancellationToken);

        var response = await SendAsync(relay.LocalUri, Request(relay.LocalUri,
            headers: "Authorization: malicious-client\r\nCookie: client-cookie\r\nX-Emby-Token: malicious-token\r\n"));

        Assert.Equal(new byte[] { 7, 8, 9 }, response.Body);
        Assert.Equal(2, origin.Requests.Count);
        Assert.All(origin.Requests, request =>
        {
            Assert.Equal("header-secret", request.Headers["X-Emby-Token"]);
            Assert.Equal("Bearer origin-secret", request.Headers["Authorization"]);
            Assert.False(request.Headers.ContainsKey("Cookie"));
        });
        var external = Assert.Single(destination.Requests);
        Assert.Equal("/media?keep=value", external.Target);
        Assert.False(external.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(external.Headers.ContainsKey("Authorization"));
        Assert.False(external.Headers.ContainsKey("Cookie"));
        Assert.False(external.Headers.ContainsKey("X-Scoped"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_concurrent_disposal_drain_a_live_stream_and_transport_leases_without_reporting_failure(bool cancelLifetime)
    {
        var entered = Signal();
        await using var upstream = new StreamingServer(async (_, stream, token) =>
        {
            await WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n", token);
            await WriteChunkAsync(stream, new byte[] { 1, 2, 3 }, token);
            entered.TrySetResult();
            await ReadPeerClosureAsync(stream, token);
        });
        var failures = new ConcurrentQueue<string>();
        await using var transport = CreateTransport(upstream.BaseUri);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", lifetime.Token, failures.Enqueue);
        using var client = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));
        await entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await ReadHeadersAsync(client.GetStream());
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadChunkAsync(client.GetStream()));
        var downstreamClosed = ReadToEndAsync(client.GetStream());

        if (cancelLifetime) lifetime.Cancel();
        await Task.WhenAll(relay.DisposeAsync().AsTask(), relay.DisposeAsync().AsTask()).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await downstreamClosed.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await upstream.WaitForClosedAsync(1);
        await transport.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);

        Assert.Equal(0, upstream.ActiveConnections);
        Assert.Empty(failures);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancellation_before_upstream_headers_drains_the_pending_request_without_a_failure()
    {
        var entered = Signal();
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new StreamingServer(async (_, stream, token) =>
        {
            entered.TrySetResult();
            await ReadPeerClosureAsync(stream, token);
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4", lifetime.Token, failures.Enqueue);
        using var client = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));
        var downstreamClosed = ReadToEndAsync(client.GetStream());
        await entered.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);

        lifetime.Cancel();
        await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await transport.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await downstreamClosed;
        await upstream.WaitForClosedAsync(1);

        Assert.Equal(0, upstream.ActiveConnections);
        Assert.Empty(failures);
    }

    [Fact(Timeout = 15000)]
    public async Task Four_streams_fill_capacity_and_downstream_resets_release_a_slot_without_an_upstream_failure()
    {
        var fourth = Signal();
        var fifth = Signal();
        var failures = new ConcurrentQueue<string>();
        var seen = 0;
        await using var upstream = new StreamingServer(async (_, stream, token) =>
        {
            var count = Interlocked.Increment(ref seen);
            if (count == 4) fourth.TrySetResult();
            if (count == 5) fifth.TrySetResult();
            await WriteAsciiAsync(stream, "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n", token);
            await WriteChunkAsync(stream, new byte[] { 7 }, token);
            await ReadPeerClosureAsync(stream, token);
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await ProgressiveHttpRelay.OpenAsync(transport, upstream.BaseUri, "mp4",
            TestContext.Current.CancellationToken, failures.Enqueue);
        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var client = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));
                clients.Add(client);
                await ReadHeadersAsync(client.GetStream());
                Assert.Equal(new byte[] { 7 }, await ReadChunkAsync(client.GetStream()));
            }
            await fourth.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            var waiting = await ConnectAsync(relay.LocalUri, Request(relay.LocalUri));
            clients.Add(waiting);
            // All four admitted streams are held open by an upstream barrier before this negative observation.
            await Assert.ThrowsAsync<TimeoutException>(() => fifth.Task.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
            Assert.Equal(4, Volatile.Read(ref seen));

            clients[0].LingerState = new LingerOption(true, 0);
            // Close the socket directly: disposing a NetworkStream can first issue a graceful shutdown.
            clients[0].Client.Close(0);
            await fifth.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
            Assert.Equal(200, (await ReadHeadersAsync(waiting.GetStream())).Status);
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await upstream.WaitForClosedAsync(5);
            await transport.DisposeAsync().AsTask().WaitAsync(Deadline, TestContext.Current.CancellationToken);
            Assert.Equal(0, upstream.ActiveConnections);
            Assert.Empty(failures);
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    private static ScopedMediaTransport CreateTransport(Uri origin) => new(origin, new Dictionary<string, string>
    {
        ["X-Emby-Token"] = "header-secret", ["Authorization"] = "Bearer origin-secret", ["X-Scoped"] = "private-value"
    }, TestContext.Current.CancellationToken);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string Request(Uri uri, string method = "GET", string? headers = null, string? target = null,
        string? host = null, string version = "HTTP/1.1")
        => $"{method} {target ?? uri.AbsolutePath} {version}\r\nHost: {host ?? uri.Authority}\r\n{headers}Connection: close\r\n\r\n";

    private static async Task<TcpClient> ConnectAsync(Uri uri, string request)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { ReceiveBufferSize = 1024 };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, uri.Port, TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(Deadline, TestContext.Current.CancellationToken);
            await client.GetStream().WriteAsync(Encoding.Latin1.GetBytes(request), TestContext.Current.CancellationToken);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<RawResponse> SendAsync(Uri uri, string request)
    {
        using var client = await ConnectAsync(uri, request);
        var headers = await ReadHeadersAsync(client.GetStream());
        var body = await ReadToEndAsync(client.GetStream());
        if (headers.Headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            body = DecodeChunks(body);
        return headers with { Body = body };
    }

    private static async Task<RawResponse> ReadHeadersAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Deadline);
        using var header = new MemoryStream();
        var one = new byte[1];
        while (header.Length < 32 * 1024)
        {
            var read = await stream.ReadAsync(one, timeout.Token);
            Assert.Equal(1, read);
            header.WriteByte(one[0]);
            if (header.Length >= 4 && header.GetBuffer().AsSpan((int)header.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        var lines = Encoding.Latin1.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var headers = lines.Skip(1).Select(line => line.Split(':', 2))
            .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
        return new RawResponse(int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), headers, []);
    }

    private static async Task<byte[]> ReadChunkAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Deadline);
        using var line = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, timeout.Token);
            line.WriteByte(one[0]);
            if (line.Length >= 2 && line.GetBuffer().AsSpan((int)line.Length - 2, 2).SequenceEqual("\r\n"u8)) break;
            Assert.True(line.Length <= 64);
        }
        var length = int.Parse(Encoding.ASCII.GetString(line.ToArray()).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        Assert.InRange(length, 0, 128 * 1024);
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, timeout.Token);
        var terminator = new byte[2];
        await stream.ReadExactlyAsync(terminator, timeout.Token);
        Assert.Equal(new byte[] { 13, 10 }, terminator);
        return data;
    }

    private static async Task<byte[]> ReadToEndAsync(NetworkStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Deadline);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0) break;
                output.Write(buffer, 0, read);
                Assert.True(output.Length < 2 * 1024 * 1024);
            }
        }
        catch (IOException exception) when (exception.InnerException is SocketException) { }
        return output.ToArray();
    }

    private static byte[] DecodeChunks(byte[] bytes)
    {
        using var body = new MemoryStream();
        var offset = 0;
        while (true)
        {
            var lineLength = bytes.AsSpan(offset).IndexOf("\r\n"u8);
            Assert.True(lineLength >= 0);
            var length = int.Parse(Encoding.ASCII.GetString(bytes, offset, lineLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            offset += lineLength + 2;
            if (length == 0) return body.ToArray();
            Assert.True(length <= bytes.Length - offset - 2);
            body.Write(bytes, offset, length);
            offset += length + 2;
        }
    }

    private static Task WriteAsciiAsync(NetworkStream stream, string value, CancellationToken token)
        => stream.WriteAsync(Encoding.ASCII.GetBytes(value), token).AsTask();

    private static async Task WriteChunkAsync(NetworkStream stream, byte[] value, CancellationToken token)
    {
        await WriteAsciiAsync(stream, value.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n", token);
        await stream.WriteAsync(value, token);
        await WriteAsciiAsync(stream, "\r\n", token);
    }

    private static async Task ReadPeerClosureAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[1];
        try { while (await stream.ReadAsync(buffer, token) != 0) { } }
        catch (IOException exception) when (exception.InnerException is SocketException) { }
    }

    private sealed record RawResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    private sealed class StreamingServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new();
        private readonly SemaphoreSlim closed = new(0);
        private readonly List<Task> connections = [];
        private readonly ConcurrentQueue<Exception> errors = new();
        private readonly Func<LoopbackRequest, NetworkStream, CancellationToken, Task> handler;
        private readonly Task accepting;
        private int activeConnections;

        internal StreamingServer(Func<LoopbackRequest, NetworkStream, CancellationToken, Task> handler)
        {
            this.handler = handler;
            listener.Start();
            BaseUri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            accepting = AcceptAsync();
        }

        internal Uri BaseUri { get; }
        internal int ActiveConnections => Volatile.Read(ref activeConnections);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync(shutdown.Token);
                    client.SendBufferSize = 4096;
                    connections.Add(ServeAsync(client));
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (SocketException) when (shutdown.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (shutdown.IsCancellationRequested) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            Interlocked.Increment(ref activeConnections);
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var raw = new MemoryStream();
                    var one = new byte[1];
                    while (raw.Length < 32 * 1024)
                    {
                        if (await stream.ReadAsync(one, shutdown.Token) == 0) return;
                        raw.WriteByte(one[0]);
                        if (raw.Length >= 4 && raw.GetBuffer().AsSpan((int)raw.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
                    }
                    var lines = Encoding.ASCII.GetString(raw.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    var requestLine = lines[0].Split(' ');
                    var headers = lines.Skip(1).Select(line => line.Split(':', 2))
                        .ToDictionary(pair => pair[0], pair => pair[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    await handler(new LoopbackRequest(requestLine[0], requestLine[1], headers), stream, shutdown.Token);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (Exception exception) { errors.Enqueue(exception); }
            finally { Interlocked.Decrement(ref activeConnections); closed.Release(); }
        }

        internal async Task WaitForClosedAsync(int count)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(Deadline);
            for (var index = 0; index < count; index++) await closed.WaitAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            listener.Stop();
            await accepting;
            await Task.WhenAll(connections).WaitAsync(Deadline, TestContext.Current.CancellationToken);
            closed.Dispose();
            shutdown.Dispose();
            if (!errors.IsEmpty) throw new AggregateException(errors);
        }
    }
}
