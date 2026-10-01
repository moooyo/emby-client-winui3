using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.MediaTransport.Tests;

public sealed class HlsHttpRelayTests
{
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumSegmentBytes = 64 * 1024 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Opening_creates_a_private_loopback_manifest_capability_without_prefetching_upstream()
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest("#EXTM3U\n")));
        await using var transport = CreateTransport(upstream.BaseUri);

        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "stream.m3u8?api_key=query-secret"), TestCancellation);

        Assert.Empty(upstream.Requests);
        Assert.Equal("http", relay.LocalUri.Scheme);
        Assert.Equal("127.0.0.1", relay.LocalUri.Host);
        Assert.InRange(relay.LocalUri.Port, 1, 65535);
        Assert.NotEqual(upstream.BaseUri.Port, relay.LocalUri.Port);
        Assert.Matches("(^|/)[0-9a-fA-F]{64}(/|$)", relay.LocalUri.AbsolutePath);
        Assert.EndsWith("/stream.m3u8", relay.LocalUri.AbsolutePath);
        AssertCapability(relay.LocalUri, relay.LocalUri);
    }

    [Fact]
    public async Task Every_manifest_get_refreshes_live_content_and_reuses_capabilities_without_exposing_credentials()
    {
        var manifestRequests = 0;
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(
            request.Target.StartsWith("/live.m3u8", StringComparison.Ordinal)
                ? Manifest($"#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:{Interlocked.Increment(ref manifestRequests)}\n"
                    + "#EXTINF:2,\nsegment.ts?api_key=query-secret&quality=original\n")
                : new LoopbackResponse(200, [3, 4, 5], new Dictionary<string, string>
                {
                    ["Content-Type"] = "video/mp2t",
                    ["Set-Cookie"] = "session=cookie-secret"
                })));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "live.m3u8?api_key=manifest-secret"), TestCancellation);

        var first = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var second = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, second.StatusCode);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:1", first.Text);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:2", second.Text);
        var firstSegment = Assert.Single(ReferencedUris(first.Text));
        var secondSegment = Assert.Single(ReferencedUris(second.Text));
        Assert.Equal(firstSegment, secondSegment);
        AssertCapability(firstSegment, relay.LocalUri);
        AssertSanitized(first);
        AssertSanitized(second);

        var segment = await SendAsync(firstSegment, BuildRequest(firstSegment));

        Assert.Equal(200, segment.StatusCode);
        Assert.Equal(new byte[] { 3, 4, 5 }, segment.Body);
        Assert.Equal("video/mp2t", segment.Headers["Content-Type"]);
        Assert.False(segment.Headers.ContainsKey("Set-Cookie"));
        Assert.False(segment.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(segment.Headers.ContainsKey("Authorization"));
        Assert.Equal(3, upstream.Requests.Count);
        Assert.Equal("/segment.ts?api_key=query-secret&quality=original", upstream.Requests.Last().Target);
        Assert.All(upstream.Requests, request =>
        {
            Assert.Equal("header-secret", request.Headers["X-Emby-Token"]);
            Assert.Equal("Bearer authorization-secret", request.Headers["Authorization"]);
            Assert.Equal("identity", request.Headers["Accept-Encoding"]);
        });
    }

    [Fact]
    public async Task Manifest_reference_tags_are_rewritten_recursively_even_without_playlist_filename_extensions()
    {
        const string master = "#EXTM3U\n"
            + "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"English\",URI=\"audio?api_key=audio-secret\"\n"
            + "#EXT-X-I-FRAME-STREAM-INF:BANDWIDTH=1000,URI=\"iframes?api_key=iframe-secret\"\n"
            + "#EXT-X-RENDITION-REPORT:URI=\"rendition?api_key=rendition-secret\",LAST-MSN=5\n"
            + "#EXT-X-STREAM-INF:BANDWIDTH=4000,AUDIO=\"audio\"\nvariant?api_key=variant-secret\n";
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(
            request.Target == "/master.m3u8" ? Manifest(master)
                : request.Target.StartsWith("/segment.ts", StringComparison.Ordinal)
                    ? new LoopbackResponse(200, [7, 8])
                    : Manifest("#EXTM3U\n#EXTINF:1,\nsegment.ts?api_key=segment-secret\n")));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "master.m3u8"), TestCancellation);

        var root = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(200, root.StatusCode);
        Assert.Contains("TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"English\"", root.Text);
        Assert.Contains("LAST-MSN=5", root.Text);
        AssertSanitized(root);
        var children = ReferencedUris(root.Text);
        Assert.Equal(4, children.Count);
        Assert.All(children, child => AssertCapability(child, relay.LocalUri));
        foreach (var child in children)
        {
            var playlist = await SendAsync(child, BuildRequest(child));
            Assert.Equal(200, playlist.StatusCode);
            Assert.StartsWith("#EXTM3U", playlist.Text);
            AssertSanitized(playlist);
            var segment = Assert.Single(ReferencedUris(playlist.Text));
            AssertCapability(segment, relay.LocalUri);
            Assert.Equal(new byte[] { 7, 8 }, (await SendAsync(segment, BuildRequest(segment))).Body);
        }

        Assert.Contains(upstream.Requests, request => request.Target == "/audio?api_key=audio-secret");
        Assert.Contains(upstream.Requests, request => request.Target == "/iframes?api_key=iframe-secret");
        Assert.Contains(upstream.Requests, request => request.Target == "/rendition?api_key=rendition-secret");
        Assert.Contains(upstream.Requests, request => request.Target == "/variant?api_key=variant-secret");
    }

    [Fact]
    public async Task Key_and_map_attributes_are_binary_resources_even_when_their_names_end_in_m3u8()
    {
        byte[] key = [0, 255, 13, 10, 17];
        byte[] initialization = [0, 0, 0, 12, 102, 116, 121, 112];
        const string manifest = "#EXTM3U\n"
            + "#EXT-X-KEY:METHOD=AES-128,URI=\"key.m3u8?api_key=key-secret\",IV=0x01\n"
            + "#EXT-X-MAP:URI=\"init.m3u8?api_key=map-secret\",BYTERANGE=\"8@0\"\n"
            + "#EXTINF:2,\nsegment.ts\n";
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target switch
        {
            "/playlist.m3u8" => Manifest(manifest),
            "/key.m3u8?api_key=key-secret" => new LoopbackResponse(200, key),
            "/init.m3u8?api_key=map-secret" => new LoopbackResponse(200, initialization),
            "/segment.ts" => new LoopbackResponse(200, [9]),
            _ => new LoopbackResponse(404, [])
        }));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation);

        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(200, playlist.StatusCode);
        Assert.Contains("METHOD=AES-128", playlist.Text);
        Assert.Contains("IV=0x01", playlist.Text);
        Assert.Contains("BYTERANGE=\"8@0\"", playlist.Text);
        var resources = ReferencedUris(playlist.Text);
        Assert.Equal(3, resources.Count);
        Assert.All(resources, resource => AssertCapability(resource, relay.LocalUri));
        Assert.Equal(key, (await SendAsync(resources[0], BuildRequest(resources[0]))).Body);
        Assert.Equal(initialization, (await SendAsync(resources[1], BuildRequest(resources[1]))).Body);
        Assert.Equal(new byte[] { 9 }, (await SendAsync(resources[2], BuildRequest(resources[2]))).Body);
    }

    [Fact]
    public async Task Relative_children_resolve_against_the_effective_manifest_uri_after_a_same_origin_redirect()
    {
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target switch
        {
            "/start.m3u8?api_key=root-secret" => new LoopbackResponse(307, [], new Dictionary<string, string>
            {
                ["Location"] = "/redirected/live/index.m3u8?api_key=redirect-secret"
            }),
            "/redirected/live/index.m3u8?api_key=redirect-secret" => Manifest("#EXTM3U\n../media/part.ts?api_key=child-secret&part=2\n"),
            "/redirected/media/part.ts?api_key=child-secret&part=2" => new LoopbackResponse(200, [1, 2, 3]),
            _ => new LoopbackResponse(404, [])
        }));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "start.m3u8?api_key=root-secret"), TestCancellation);

        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var child = Assert.Single(ReferencedUris(playlist.Text));
        var segment = await SendAsync(child, BuildRequest(child));

        Assert.Equal(200, playlist.StatusCode);
        AssertCapability(child, relay.LocalUri);
        Assert.Equal(200, segment.StatusCode);
        Assert.Equal(new byte[] { 1, 2, 3 }, segment.Body);
        Assert.Equal("/redirected/media/part.ts?api_key=child-secret&part=2", upstream.Requests.Last().Target);
        Assert.All(upstream.Requests, request => Assert.Equal("header-secret", request.Headers["X-Emby-Token"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cross_origin_manifest_references_fail_without_fetching_the_external_resource(bool attribute)
    {
        var failures = new ConcurrentQueue<string>();
        await using var external = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [7])));
        var foreignUri = new Uri(external.BaseUri, "external.ts?api_key=external-secret").AbsoluteUri;
        var manifest = attribute ? $"#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"{foreignUri}\"\n"
            : $"#EXTM3U\n{foreignUri}\n";
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest(manifest)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("NotAllowed", Assert.Single(failures));
        Assert.Empty(external.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cross_origin_effective_uri_cannot_return_a_manifest_or_segment_to_the_player(bool segmentRedirect)
    {
        var failures = new ConcurrentQueue<string>();
        await using var external = new LoopbackServer((_, _) => Task.FromResult(segmentRedirect
            ? new LoopbackResponse(200, [7, 8, 9]) : Manifest("#EXTM3U\nchild.ts\n")));
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(
            segmentRedirect && request.Target == "/playlist.m3u8" ? Manifest("#EXTM3U\nsegment.ts\n")
                : new LoopbackResponse(302, [], new Dictionary<string, string>
                {
                    ["Location"] = new Uri(external.BaseUri, "external?api_key=external-secret&keep=value").AbsoluteUri
                })));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation, failures.Enqueue);
        var target = relay.LocalUri;
        if (segmentRedirect)
        {
            var playlist = await SendAsync(target, BuildRequest(target));
            Assert.Equal(200, playlist.StatusCode);
            target = Assert.Single(ReferencedUris(playlist.Text));
        }

        var response = await SendAsync(target, BuildRequest(target));

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("NotAllowed", Assert.Single(failures));
        Assert.Empty(external.Requests);
    }

    [Theory]
    [InlineData("bytes=4-11", 4, 8)]
    [InlineData("bytes=4-", 4, 60)]
    [InlineData("bytes=-9", 55, 9)]
    [InlineData("bytes=60-999", 60, 4)]
    [InlineData("bytes=-999", 0, 64)]
    public async Task Segment_byte_ranges_return_the_exact_requested_representation(string range, int start, int length)
    {
        var data = Enumerable.Range(0, 64).Select(index => (byte)index).ToArray();
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/playlist.m3u8"
            ? Manifest("#EXTM3U\nsegment.ts\n")
            : RangeResponse(request, data)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation);
        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var segment = Assert.Single(ReferencedUris(playlist.Text));

        var response = await SendAsync(segment, BuildRequest(segment, extraHeaders: $"Range: {range}\r\n"));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal($"bytes {start}-{start + length - 1}/64", response.Headers["Content-Range"]);
        Assert.Equal(length.ToString(CultureInfo.InvariantCulture), response.Headers["Content-Length"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.Equal(data.AsSpan(start, length).ToArray(), response.Body);
    }

    [Theory]
    [InlineData("bytes=10-9")]
    [InlineData("bytes=-0")]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("items=0-1")]
    [InlineData("bytes=9223372036854775808-")]
    public async Task Invalid_segment_ranges_return_416_without_a_playback_failure(string range)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/playlist.m3u8"
            ? Manifest("#EXTM3U\nsegment.ts\n") : new LoopbackResponse(200, new byte[64])));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation, failures.Enqueue);
        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var segment = Assert.Single(ReferencedUris(playlist.Text));

        var response = await SendAsync(segment, BuildRequest(segment, extraHeaders: $"Range: {range}\r\n"));

        Assert.Equal(416, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task An_upstream_unsatisfiable_segment_range_stays_416_without_a_playback_failure()
    {
        var failures = new ConcurrentQueue<string>();
        var data = new byte[64];
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/playlist.m3u8"
            ? Manifest("#EXTM3U\nsegment.ts\n") : RangeResponse(request, data)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation, failures.Enqueue);
        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var segment = Assert.Single(ReferencedUris(playlist.Text));

        var response = await SendAsync(segment, BuildRequest(segment, extraHeaders: "Range: bytes=64-\r\n"));

        Assert.Equal(416, response.StatusCode);
        Assert.Equal("bytes */64", response.Headers["Content-Range"]);
        Assert.Empty(response.Body);
        Assert.Empty(failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("bytes 0-0/64")]
    [InlineData("bytes */*")]
    [InlineData("items */64")]
    public async Task An_upstream_416_requires_an_unsatisfied_byte_range_with_a_known_length(string? contentRange)
    {
        var failures = new ConcurrentQueue<string>();
        var headers = contentRange is null ? null : new Dictionary<string, string> { ["Content-Range"] = contentRange };
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/playlist.m3u8"
            ? Manifest("#EXTM3U\nsegment.ts\n") : new LoopbackResponse(416, [], headers)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation, failures.Enqueue);
        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var segment = Assert.Single(ReferencedUris(playlist.Text));

        var response = await SendAsync(segment, BuildRequest(segment, extraHeaders: "Range: bytes=64-\r\n"));

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
    }

    [Fact]
    public async Task A_valid_upstream_416_does_not_forward_its_private_error_body()
    {
        var failures = new ConcurrentQueue<string>();
        var privateBody = Encoding.UTF8.GetBytes("server-private-message query-secret header-secret");
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/playlist.m3u8"
            ? Manifest("#EXTM3U\nsegment.ts\n") : new LoopbackResponse(416, privateBody,
                new Dictionary<string, string> { ["Content-Range"] = "bytes */64" })));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation, failures.Enqueue);
        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var segment = Assert.Single(ReferencedUris(playlist.Text));

        var response = await SendAsync(segment, BuildRequest(segment, extraHeaders: "Range: bytes=64-\r\n"));

        Assert.Equal(416, response.StatusCode);
        Assert.Equal("bytes */64", response.Headers["Content-Range"]);
        Assert.Equal("0", response.Headers["Content-Length"]);
        Assert.Empty(response.Body);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Head_returns_the_rewritten_manifest_length_without_a_body_and_ignores_range()
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest("#EXTM3U\nsegment.ts\n")));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation);

        var full = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var head = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri, method: "HEAD",
            extraHeaders: "Range: bytes=999999-\r\n"));

        Assert.Equal(200, head.StatusCode);
        Assert.Empty(head.Body);
        Assert.Equal(full.Headers["Content-Length"], head.Headers["Content-Length"]);
        Assert.Equal(full.Headers["Content-Type"], head.Headers["Content-Type"]);
        Assert.False(head.Headers.ContainsKey("Content-Range"));
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
    public async Task Only_the_exact_capability_path_method_and_host_are_accepted(string scenario, int status)
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest("#EXTM3U\n")));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation);
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
        Assert.Empty(upstream.Requests);
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
    public async Task Ambiguous_headers_request_bodies_and_non_ascii_requests_are_rejected_before_upstream_fetches(string scenario)
    {
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest("#EXTM3U\n")));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation);
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

        var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri, extraHeaders: headers) + body);

        Assert.Equal(400, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_manifest_and_segment_content_lengths_fail_before_reading_the_body(bool segment)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(
            segment && request.Target == "/playlist.m3u8" ? Manifest("#EXTM3U\nsegment.ts\n")
                : new LoopbackResponse(200, [], DeclaredContentLength:
                    (segment ? MaximumSegmentBytes : MaximumManifestBytes) + 1, HoldBodyOpen: true)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8"), TestCancellation, failures.Enqueue);
        var target = relay.LocalUri;
        if (segment)
        {
            var playlist = await SendAsync(target, BuildRequest(target));
            target = Assert.Single(ReferencedUris(playlist.Text));
        }

        var response = await SendAsync(target, BuildRequest(target));

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
    }

    [Fact]
    public async Task An_unknown_length_manifest_still_enforces_the_byte_limit()
    {
        var failures = new ConcurrentQueue<string>();
        var oversized = Encoding.UTF8.GetBytes("#EXTM3U\n#" + new string('a', MaximumManifestBytes));
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, oversized,
            DeclaredContentLength: -1)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
    }

    [Fact]
    public async Task A_manifest_cannot_allocate_unbounded_resource_capabilities()
    {
        var failures = new ConcurrentQueue<string>();
        var manifest = "#EXTM3U\n" + string.Join('\n', Enumerable.Range(0, 20000).Select(index => $"segment-{index}.ts")) + "\n";
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest(manifest)));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation, failures.Enqueue);

        var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Body);
        Assert.Equal("UnsupportedFormat", Assert.Single(failures));
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData(401, "AuthenticationRequired")]
    [InlineData(403, "NotAllowed")]
    [InlineData(503, "NetworkFailure")]
    public async Task Repeated_upstream_failures_notify_once_with_a_stable_category_and_no_private_body(int status, string expectedCode)
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(status,
            Encoding.UTF8.GetBytes("server-private-message header-secret query-secret"))));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenAsync(transport,
            new Uri(upstream.BaseUri, "playlist.m3u8?api_key=query-secret"), TestCancellation, failures.Enqueue);

        var first = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var second = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(502, first.StatusCode);
        Assert.Equal(502, second.StatusCode);
        Assert.Empty(first.Body);
        Assert.Empty(second.Body);
        Assert.Equal(expectedCode, Assert.Single(failures));
        Assert.Equal(2, upstream.Requests.Count);
    }

    [Fact]
    public async Task Four_pending_upstream_reads_hold_capacity_and_disposal_drains_clients_without_owning_the_transport()
    {
        var failures = new ConcurrentQueue<string>();
        var readsArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var upstream = new LoopbackServer((request, _) =>
        {
            if (request.Target == "/health") return Task.FromResult(new LoopbackResponse(200, [7]));
            if (Interlocked.Increment(ref reads) == 4) readsArrived.TrySetResult();
            return Task.FromResult(new LoopbackResponse(200, [], DeclaredContentLength: 1024, HoldBodyOpen: true));
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, TestCancellation, failures.Enqueue);
        var clients = new List<TcpClient>();
        try
        {
            for (var index = 0; index < 4; index++)
                clients.Add(await ConnectAndWriteAsync(relay.LocalUri, BuildRequest(relay.LocalUri)));
            await readsArrived.Task.WaitAsync(Deadline, TestCancellation);
            var waiting = await ConnectAndWriteAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
            clients.Add(waiting);
            using (var observation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation))
            {
                observation.CancelAfter(TimeSpan.FromMilliseconds(300));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    waiting.GetStream().ReadAsync(new byte[1], observation.Token).AsTask());
                Assert.False(TestCancellation.IsCancellationRequested);
            }
            Assert.Equal(4, Volatile.Read(ref reads));
            var connectionEnds = clients.Select(client => ReadToEndWithDeadlineAsync(client.GetStream())).ToArray();

            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
            await Task.WhenAll(connectionEnds).WaitAsync(Deadline, TestCancellation);

            Assert.Equal(4, Volatile.Read(ref reads));
            Assert.Empty(failures);
            var health = await transport.DownloadAsync(new Uri(upstream.BaseUri, "health"), ct: TestCancellation);
            Assert.Equal(new byte[] { 7 }, health.Data);
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
        }
    }

    [Fact]
    public async Task Lifetime_cancellation_closes_incomplete_requests_without_an_upstream_fetch_or_failure()
    {
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((_, _) => Task.FromResult(Manifest("#EXTM3U\n")));
        await using var transport = CreateTransport(upstream.BaseUri);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        await using var relay = await HlsHttpRelay.OpenAsync(transport, upstream.BaseUri, lifetime.Token, failures.Enqueue);
        using var client = await ConnectAndWriteAsync(relay.LocalUri,
            $"GET {relay.LocalUri.AbsolutePath} HTTP/1.1\r\nHost: {relay.LocalUri.Authority}\r\nX-Pending: ");
        var connectionEnd = ReadToEndWithDeadlineAsync(client.GetStream());

        lifetime.Cancel();
        await relay.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
        await connectionEnd.WaitAsync(Deadline, TestCancellation);

        Assert.Empty(upstream.Requests);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Sliding_live_windows_reclaim_expired_resources_and_keep_playing_beyond_the_lifetime_cap()
    {
        const int maximumResources = 128;
        var clock = new ManualTimeProvider();
        var sequence = 0;
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer((request, _) =>
        {
            if (request.Target != "/live.m3u8")
                return Task.FromResult(new LoopbackResponse(200, Encoding.UTF8.GetBytes(request.Target)));
            var current = Volatile.Read(ref sequence);
            return Task.FromResult(Manifest($"#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:{current}\n"
                + string.Join('\n', Enumerable.Range(current, 4).Select(index => $"#EXTINF:2,\nsegment-{index}.ts")) + "\n"));
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenForTestingAsync(transport,
            new Uri(upstream.BaseUri, "live.m3u8"), TestCancellation, clock, maximumResources, failures.Enqueue);
        var historicalCapabilities = new HashSet<Uri>();
        Uri? firstSegment = null;
        Uri? latestSegment = null;

        for (var index = 0; index < 300; index++)
        {
            Volatile.Write(ref sequence, index);
            var response = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
            Assert.Equal(200, response.StatusCode);
            var currentSegments = ReferencedUris(response.Text);
            Assert.Equal(4, currentSegments.Count);
            firstSegment ??= currentSegments[0];
            latestSegment = currentSegments[^1];
            historicalCapabilities.UnionWith(currentSegments);
            Assert.InRange(relay.TrackedResourceCount, 5, maximumResources);
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.True(historicalCapabilities.Count > maximumResources);
        Assert.NotNull(firstSegment);
        Assert.NotNull(latestSegment);
        Assert.Equal(404, (await SendAsync(firstSegment, BuildRequest(firstSegment))).StatusCode);
        var latest = await SendAsync(latestSegment, BuildRequest(latestSegment));
        Assert.Equal(200, latest.StatusCode);
        Assert.Equal("/segment-302.ts", latest.Text);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task Recent_access_refreshes_the_grace_period_and_expired_capability_paths_are_never_reused()
    {
        var clock = new ManualTimeProvider();
        var version = 0;
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/live.m3u8"
            ? Manifest(Volatile.Read(ref version) switch
            {
                0 => "#EXTM3U\nold-a.ts\nold-b.ts\n",
                1 => "#EXTM3U\ncurrent.ts\n",
                _ => "#EXTM3U\nold-a.ts\n"
            })
            : new LoopbackResponse(200, Encoding.UTF8.GetBytes(request.Target))));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenForTestingAsync(transport,
            new Uri(upstream.BaseUri, "live.m3u8"), TestCancellation, clock, 16);
        var initial = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var oldSegments = ReferencedUris(initial.Text);
        Assert.Equal(2, oldSegments.Count);

        clock.Advance(TimeSpan.FromSeconds(30));
        Volatile.Write(ref version, 1);
        Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(200, (await SendAsync(oldSegments[0], BuildRequest(oldSegments[0]))).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(32));
        Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);

        Assert.Equal(404, (await SendAsync(oldSegments[1], BuildRequest(oldSegments[1]))).StatusCode);
        Assert.Equal(200, (await SendAsync(oldSegments[0], BuildRequest(oldSegments[0]))).StatusCode);

        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);
        Assert.Equal(404, (await SendAsync(oldSegments[0], BuildRequest(oldSegments[0]))).StatusCode);
        Volatile.Write(ref version, 2);
        var renewed = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var renewedSegment = Assert.Single(ReferencedUris(renewed.Text));

        Assert.NotEqual(oldSegments[0], renewedSegment);
        Assert.Equal(404, (await SendAsync(oldSegments[0], BuildRequest(oldSegments[0]))).StatusCode);
        var media = await SendAsync(renewedSegment, BuildRequest(renewedSegment));
        Assert.Equal(200, media.StatusCode);
        Assert.Equal("/old-a.ts", media.Text);
    }

    [Fact]
    public async Task Reachable_nested_vod_playlists_preserve_unrequested_future_segments_across_long_idle_periods()
    {
        var clock = new ManualTimeProvider();
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target switch
        {
            "/master.m3u8" => Manifest("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nvod.m3u8\n"),
            "/vod.m3u8" => Manifest("#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n"
                + "#EXTINF:2,\nfirst.ts\n#EXTINF:2,\nsecond.ts\n#EXTINF:2,\nfuture.ts\n#EXT-X-ENDLIST\n"),
            _ => new LoopbackResponse(200, Encoding.UTF8.GetBytes(request.Target))
        }));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenForTestingAsync(transport,
            new Uri(upstream.BaseUri, "master.m3u8"), TestCancellation, clock, 16);
        var master = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var vod = Assert.Single(ReferencedUris(master.Text));
        var playlist = await SendAsync(vod, BuildRequest(vod));
        var future = ReferencedUris(playlist.Text)[^1];

        clock.Advance(TimeSpan.FromHours(1));
        var refreshedMaster = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));

        Assert.Equal(200, refreshedMaster.StatusCode);
        Assert.Equal(vod, Assert.Single(ReferencedUris(refreshedMaster.Text)));
        var futureMedia = await SendAsync(future, BuildRequest(future));
        Assert.Equal(200, futureMedia.StatusCode);
        Assert.Equal("/future.ts", futureMedia.Text);
        var refreshedPlaylist = await SendAsync(vod, BuildRequest(vod));
        Assert.Equal(200, refreshedPlaylist.StatusCode);
        Assert.Equal(future, ReferencedUris(refreshedPlaylist.Text)[^1]);
    }

    [Fact]
    public async Task A_removed_nested_playlist_and_its_buffered_children_survive_the_grace_period_then_are_reclaimed()
    {
        var clock = new ManualTimeProvider();
        var removed = 0;
        await using var upstream = new LoopbackServer((request, _) => Task.FromResult(request.Target switch
        {
            "/master.m3u8" => Manifest(Volatile.Read(ref removed) == 0
                ? "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nchild.m3u8\n"
                : "#EXTM3U\nreplacement.ts\n"),
            "/child.m3u8" => Manifest("#EXTM3U\n#EXTINF:2,\nbuffered.ts\n"),
            _ => new LoopbackResponse(200, Encoding.UTF8.GetBytes(request.Target))
        }));
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenForTestingAsync(transport,
            new Uri(upstream.BaseUri, "master.m3u8"), TestCancellation, clock, 16);
        var master = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var child = Assert.Single(ReferencedUris(master.Text));
        var playlist = await SendAsync(child, BuildRequest(child));
        var buffered = Assert.Single(ReferencedUris(playlist.Text));

        clock.Advance(TimeSpan.FromSeconds(30));
        Volatile.Write(ref removed, 1);
        Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);
        clock.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);
        var stillBuffered = await SendAsync(child, BuildRequest(child));

        Assert.Equal(200, stillBuffered.StatusCode);
        Assert.Equal(buffered, Assert.Single(ReferencedUris(stillBuffered.Text)));
        Assert.Equal(200, (await SendAsync(buffered, BuildRequest(buffered))).StatusCode);

        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);
        Assert.Equal(404, (await SendAsync(child, BuildRequest(child))).StatusCode);
        Assert.Equal(404, (await SendAsync(buffered, BuildRequest(buffered))).StatusCode);
    }

    [Fact]
    public async Task An_in_flight_segment_remains_addressable_after_its_window_and_grace_period_have_expired()
    {
        var clock = new ManualTimeProvider();
        var version = 0;
        var oldRequests = 0;
        var readArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new ConcurrentQueue<string>();
        await using var upstream = new LoopbackServer(async (request, token) =>
        {
            if (request.Target == "/live.m3u8")
                return Manifest(Volatile.Read(ref version) == 0 ? "#EXTM3U\nold.ts\n" : "#EXTM3U\ncurrent.ts\n");
            if (request.Target == "/old.ts" && Interlocked.Increment(ref oldRequests) == 1)
            {
                readArrived.TrySetResult();
                await finishRead.Task.WaitAsync(token);
            }
            return new LoopbackResponse(200, [1, 2, 3]);
        });
        await using var transport = CreateTransport(upstream.BaseUri);
        await using var relay = await HlsHttpRelay.OpenForTestingAsync(transport,
            new Uri(upstream.BaseUri, "live.m3u8"), TestCancellation, clock, 16, failures.Enqueue);
        var playlist = await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri));
        var old = Assert.Single(ReferencedUris(playlist.Text));
        var pending = SendAsync(old, BuildRequest(old));
        try
        {
            await readArrived.Task.WaitAsync(Deadline, TestCancellation);
            clock.Advance(TimeSpan.FromSeconds(30));
            Volatile.Write(ref version, 1);
            Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);
            clock.Advance(TimeSpan.FromSeconds(121));
            Assert.Equal(200, (await SendAsync(relay.LocalUri, BuildRequest(relay.LocalUri))).StatusCode);

            var concurrent = await SendAsync(old, BuildRequest(old));

            Assert.Equal(200, concurrent.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, concurrent.Body);
            Assert.Equal(2, Volatile.Read(ref oldRequests));
            finishRead.TrySetResult();
            var completed = await pending.WaitAsync(Deadline, TestCancellation);
            Assert.Equal(200, completed.StatusCode);
            Assert.Equal(new byte[] { 1, 2, 3 }, completed.Body);
            Assert.Empty(failures);
        }
        finally
        {
            finishRead.TrySetResult();
            await pending.WaitAsync(Deadline, TestCancellation);
        }
    }

    private static LoopbackResponse Manifest(string content)
        => new(200, Encoding.UTF8.GetBytes(content), new Dictionary<string, string>
        {
            ["Content-Type"] = "application/vnd.apple.mpegurl"
        });

    private static LoopbackResponse RangeResponse(LoopbackRequest request, byte[] data)
    {
        var headers = new Dictionary<string, string> { ["Content-Type"] = "video/mp2t" };
        if (!request.Headers.TryGetValue("Range", out var range)) return new LoopbackResponse(200, data, headers);
        var bounds = range["bytes=".Length..].Split('-');
        var start = int.Parse(bounds[0], CultureInfo.InvariantCulture);
        var end = bounds[1].Length == 0 ? data.Length - 1
            : Math.Min(int.Parse(bounds[1], CultureInfo.InvariantCulture), data.Length - 1);
        if (start >= data.Length)
        {
            headers["Content-Range"] = $"bytes */{data.Length}";
            return new LoopbackResponse(416, [], headers);
        }
        headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
        return new LoopbackResponse(206, data.AsSpan(start, end - start + 1).ToArray(), headers);
    }

    private static List<Uri> ReferencedUris(string manifest)
    {
        var result = new List<Uri>();
        foreach (var line in manifest.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith('#')) result.Add(new Uri(line.Trim(), UriKind.Absolute));
            else foreach (Match match in Regex.Matches(line, "(?:^|[:,])URI=\"([^\"]+)\""))
                result.Add(new Uri(match.Groups[1].Value, UriKind.Absolute));
        }
        return result;
    }

    private static void AssertCapability(Uri resource, Uri root)
    {
        Assert.Equal(root.Scheme, resource.Scheme);
        Assert.Equal(root.Authority, resource.Authority);
        Assert.Empty(resource.Query);
        Assert.Empty(resource.Fragment);
        Assert.Empty(resource.UserInfo);
        Assert.DoesNotContain("secret", resource.AbsoluteUri);
        Assert.DoesNotContain("api_key", resource.AbsoluteUri);
        Assert.DoesNotContain("X-Emby-Token", resource.AbsoluteUri);
    }

    private static void AssertSanitized(RawResponse response)
    {
        Assert.DoesNotContain("secret", response.Text);
        Assert.DoesNotContain("api_key", response.Text);
        Assert.DoesNotContain("server-private-message", response.Text);
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
                Assert.True(result.Length + read <= 8 * 1024 * 1024, "The relay response exceeded the test limit.");
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
        => new(origin, new Dictionary<string, string>
        {
            ["X-Emby-Token"] = "header-secret",
            ["Authorization"] = "Bearer authorization-secret"
        }, TestCancellation);

    private sealed record RawResponse(int StatusCode, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        internal string Text => Encoding.UTF8.GetString(Body);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
