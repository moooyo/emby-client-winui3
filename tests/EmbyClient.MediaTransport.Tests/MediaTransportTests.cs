using System.Globalization;
using System.Net;
using System.Text;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Xunit;

namespace EmbyClient.MediaTransport.Tests;

public sealed class MediaTransportTests
{
    private const int BlockSize = 256 * 1024;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);
    private static CancellationToken TestCancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Range_stream_seeks_reads_exact_bytes_reuses_cached_blocks_and_reaches_eof()
    {
        var data = CreateData(BlockSize * 2 + 13);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var stream = await HttpRangeStream.OpenAsync(transport, server.BaseUri, TestCancellation);

        Assert.True(stream.CanRead);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(data.LongLength, stream.Length);
        Assert.Equal("video/mp4", stream.ContentType);
        Assert.Equal(BlockSize + 7, stream.Seek(BlockSize + 7, SeekOrigin.Begin));
        var buffer = new byte[40];
        Assert.Equal(40, await stream.ReadAsync(buffer, TestCancellation));
        Assert.Equal(data.AsSpan(BlockSize + 7, 40).ToArray(), buffer);

        stream.Position = 5;
        Assert.Equal(13, stream.Read(buffer, 0, 13));
        Assert.Equal(data.AsSpan(5, 13).ToArray(), buffer.AsSpan(0, 13).ToArray());
        Assert.Equal(2, server.Requests.Count);

        Assert.Equal(data.Length - 5, stream.Seek(-5, SeekOrigin.End));
        Assert.Equal(5, await stream.ReadAsync(buffer, TestCancellation));
        Assert.Equal(data.AsSpan(data.Length - 5, 5).ToArray(), buffer.AsSpan(0, 5).ToArray());
        Assert.Equal(0, await stream.ReadAsync(buffer, TestCancellation));
        Assert.Equal(data.Length, stream.Position);
        Assert.Equal(3, server.Requests.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.Seek(-1, SeekOrigin.Begin));
    }

    [Theory]
    [InlineData("ignored-range", "UnsupportedFormat")]
    [InlineData("wrong-start", "UnsupportedFormat")]
    [InlineData("unknown-total", "UnsupportedFormat")]
    [InlineData("truncated-body", "NetworkFailure")]
    public async Task Range_stream_rejects_unreliable_http_representations(string failure, string expectedCode)
    {
        await using var server = new LoopbackServer((_, _) => Task.FromResult(failure switch
        {
            "ignored-range" => new LoopbackResponse(200, [1, 2, 3, 4]),
            "wrong-start" => new LoopbackResponse(206, [1, 2, 3, 4], new Dictionary<string, string>
            {
                ["Content-Range"] = "bytes 1-4/5"
            }),
            "unknown-total" => new LoopbackResponse(206, new byte[BlockSize], new Dictionary<string, string>
            {
                ["Content-Range"] = $"bytes 0-{BlockSize - 1}/*"
            }),
            "truncated-body" => new LoopbackResponse(206, [1, 2, 3, 4], new Dictionary<string, string>
            {
                ["Content-Range"] = "bytes 0-7/8"
            }, DeclaredContentLength: 8),
            _ => throw new InvalidOperationException("Unknown response scenario.")
        }));
        await using var transport = CreateTransport(server.BaseUri);

        var error = await Assert.ThrowsAsync<PlaybackException>(() =>
            HttpRangeStream.OpenAsync(transport, new Uri(server.BaseUri, "media?api_key=query-secret"), TestCancellation));

        Assert.Equal(expectedCode, error.ErrorCode);
        AssertSanitized(error);
    }

    [Fact]
    public async Task A_changed_total_length_cannot_mix_two_representations_in_one_stream()
    {
        var data = CreateData(BlockSize * 2);
        var calls = 0;
        await using var server = new LoopbackServer((request, _) =>
        {
            var response = RangeResponse(request, data);
            if (Interlocked.Increment(ref calls) > 1)
            {
                response = response with
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Content-Range"] = $"bytes {BlockSize}-{data.Length - 1}/{data.Length + 1}",
                        ["ETag"] = "\"stable-version\""
                    }
                };
            }
            return Task.FromResult(response);
        });
        await using var transport = CreateTransport(server.BaseUri);
        await using var stream = await HttpRangeStream.OpenAsync(transport, server.BaseUri, TestCancellation);
        stream.Position = BlockSize;

        var error = await Assert.ThrowsAsync<PlaybackException>(() => stream.ReadAsync(new byte[4], TestCancellation).AsTask());

        Assert.Equal("NetworkFailure", error.ErrorCode);
        Assert.Equal(BlockSize, stream.Position);
        Assert.Equal("\"stable-version\"", server.Requests.Last().Headers["If-Match"]);
    }

    [Fact]
    public async Task Same_origin_redirects_reapply_explicit_headers_and_preserve_authenticated_queries()
    {
        await using var server = new LoopbackServer((request, _) => Task.FromResult(request.Target.StartsWith("/start", StringComparison.Ordinal)
            ? new LoopbackResponse(307, [], new Dictionary<string, string> { ["Location"] = "/media?api_key=query-secret" })
            : new LoopbackResponse(200, [1, 2, 3], new Dictionary<string, string> { ["Content-Type"] = "video/mp4" })));
        await using var transport = CreateTransport(server.BaseUri);

        var resource = await transport.DownloadAsync(new Uri(server.BaseUri, "start"), ct: TestCancellation);

        Assert.Equal(HttpStatusCode.OK, resource.StatusCode);
        Assert.Equal("video/mp4", resource.ContentType);
        Assert.Equal("?api_key=query-secret", resource.EffectiveUri.Query);
        Assert.All(server.Requests, request =>
        {
            Assert.Equal("header-secret", request.Headers["X-Emby-Token"]);
            Assert.Equal("Bearer authorization-secret", request.Headers["Authorization"]);
            Assert.Equal("identity", request.Headers["Accept-Encoding"]);
        });
    }

    [Fact]
    public async Task Cross_origin_redirects_strip_all_scoped_headers_and_encoded_credential_query_names()
    {
        await using var destination = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [7])));
        var target = new Uri(destination.BaseUri,
            "child?api_key=query-secret&%58-Emby-Token=second-secret&AccessToken=third-secret&keep=value");
        await using var origin = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(302, [],
            new Dictionary<string, string> { ["Location"] = target.AbsoluteUri })));
        await using var transport = CreateTransport(origin.BaseUri);

        var resource = await transport.DownloadAsync(origin.BaseUri, ct: TestCancellation);

        Assert.Equal("header-secret", Assert.Single(origin.Requests).Headers["X-Emby-Token"]);
        var external = Assert.Single(destination.Requests);
        Assert.False(external.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(external.Headers.ContainsKey("Authorization"));
        Assert.False(external.Headers.ContainsKey("X-Scoped-Header"));
        Assert.Equal("/child?keep=value", external.Target);
        Assert.Equal("?keep=value", resource.EffectiveUri.Query);
        Assert.DoesNotContain("secret", resource.ToString());
    }

    [Fact]
    public async Task Manifest_children_use_their_resolved_origin_for_header_and_query_scoping()
    {
        await using var external = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, [8, 9])));
        var externalChild = new Uri(external.BaseUri, "external.ts?api_key=query-secret&quality=original");
        var manifest = Encoding.UTF8.GetBytes($"#EXTM3U\nlocal.ts?api_key=query-secret\n{externalChild.AbsoluteUri}\n");
        await using var origin = new LoopbackServer((request, _) => Task.FromResult(request.Target.StartsWith("/playlist", StringComparison.Ordinal)
            ? new LoopbackResponse(200, manifest, new Dictionary<string, string> { ["Content-Type"] = "application/vnd.apple.mpegurl" })
            : new LoopbackResponse(200, [3, 4])));
        await using var transport = CreateTransport(origin.BaseUri);

        var playlist = await transport.DownloadAsync(new Uri(origin.BaseUri, "playlist.m3u8"), ct: TestCancellation);
        foreach (var line in Encoding.UTF8.GetString(playlist.Data).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')))
        {
            var child = await transport.DownloadAsync(new Uri(playlist.EffectiveUri, line), ct: TestCancellation);
            Assert.Equal(2, child.Data.Length);
        }

        Assert.Equal(2, origin.Requests.Count);
        Assert.All(origin.Requests, request => Assert.Equal("header-secret", request.Headers["X-Emby-Token"]));
        Assert.Equal("/local.ts?api_key=query-secret", origin.Requests.Last().Target);
        var externalRequest = Assert.Single(external.Requests);
        Assert.Equal("/external.ts?quality=original", externalRequest.Target);
        Assert.False(externalRequest.Headers.ContainsKey("X-Emby-Token"));
        Assert.False(externalRequest.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task Set_cookie_is_not_replayed_on_redirects_or_later_downloads()
    {
        await using var server = new LoopbackServer((request, _) => Task.FromResult(request.Target == "/set"
            ? new LoopbackResponse(302, [], new Dictionary<string, string>
            {
                ["Location"] = "/target",
                ["Set-Cookie"] = "session=cookie-secret; Path=/"
            })
            : new LoopbackResponse(200, [1])));
        await using var transport = CreateTransport(server.BaseUri);

        await transport.DownloadAsync(new Uri(server.BaseUri, "set"), ct: TestCancellation);
        await transport.DownloadAsync(new Uri(server.BaseUri, "again"), ct: TestCancellation);

        Assert.Equal(3, server.Requests.Count);
        Assert.All(server.Requests, request => Assert.False(request.Headers.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task Redirect_loops_stop_after_five_redirects_without_disclosing_the_target()
    {
        await using var server = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(302, [],
            new Dictionary<string, string> { ["Location"] = "/loop?api_key=query-secret" })));
        await using var transport = CreateTransport(server.BaseUri);

        var error = await Assert.ThrowsAsync<PlaybackException>(() => transport.DownloadAsync(server.BaseUri, ct: TestCancellation));

        Assert.Equal("NetworkFailure", error.ErrorCode);
        Assert.Equal(6, server.Requests.Count);
        AssertSanitized(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Downloads_enforce_the_byte_limit_with_or_without_content_length(bool omitLength)
    {
        await using var server = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(200, new byte[128],
            DeclaredContentLength: omitLength ? -1 : 128)));
        await using var transport = CreateTransport(server.BaseUri);

        var error = await Assert.ThrowsAsync<PlaybackException>(() =>
            transport.DownloadAsync(server.BaseUri, maximumBytes: 64, ct: TestCancellation));

        Assert.Equal("UnsupportedFormat", error.ErrorCode);
    }

    [Theory]
    [InlineData(401, "AuthenticationRequired")]
    [InlineData(403, "NotAllowed")]
    [InlineData(500, "NetworkFailure")]
    public async Task Http_errors_expose_only_stable_error_codes(int status, string expectedCode)
    {
        await using var server = new LoopbackServer((_, _) => Task.FromResult(new LoopbackResponse(status,
            Encoding.UTF8.GetBytes("server-private-message header-secret query-secret"))));
        await using var transport = CreateTransport(server.BaseUri);

        var error = await Assert.ThrowsAsync<PlaybackException>(() =>
            transport.DownloadAsync(new Uri(server.BaseUri, "media?api_key=query-secret"), ct: TestCancellation));

        Assert.Equal(expectedCode, error.ErrorCode);
        AssertSanitized(error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_transport_disposal_interrupt_pending_http_without_hanging(bool disposeTransport)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer((_, _) =>
        {
            arrived.TrySetResult();
            return Task.FromResult(new LoopbackResponse(200, [], DeclaredContentLength: 1024, HoldBodyOpen: true));
        });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestCancellation);
        await using var transport = CreateTransport(server.BaseUri);
        var pending = transport.DownloadAsync(server.BaseUri, ct: cancellation.Token);
        await arrived.Task.WaitAsync(Deadline, TestCancellation);

        if (disposeTransport)
            await transport.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
        else
            cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline, TestCancellation));
        await transport.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);
    }

    [Fact]
    public async Task Stream_disposal_drains_a_pending_range_read_and_leaves_the_shared_transport_usable()
    {
        var data = CreateData(BlockSize * 2);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LoopbackServer((request, _) =>
        {
            if (request.Target == "/health") return Task.FromResult(new LoopbackResponse(200, [5]));
            var response = RangeResponse(request, data);
            if (request.Headers["Range"].StartsWith($"bytes={BlockSize}-", StringComparison.Ordinal))
            {
                arrived.TrySetResult();
                response = response with { Body = [], DeclaredContentLength = BlockSize, HoldBodyOpen = true };
            }
            return Task.FromResult(response);
        });
        await using var transport = CreateTransport(server.BaseUri);
        await using var stream = await HttpRangeStream.OpenAsync(transport, server.BaseUri, TestCancellation);
        stream.Position = BlockSize;
        var pending = stream.ReadAsync(new byte[4], TestCancellation).AsTask();
        await arrived.Task.WaitAsync(Deadline, TestCancellation);

        await stream.DisposeAsync().AsTask().WaitAsync(Deadline, TestCancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Deadline, TestCancellation));
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        var health = await transport.DownloadAsync(new Uri(server.BaseUri, "health"), ct: TestCancellation);
        Assert.Equal(new byte[] { 5 }, health.Data);
    }

    private static ScopedMediaTransport CreateTransport(Uri origin)
        => new(origin, new Dictionary<string, string>
        {
            ["X-Emby-Token"] = "header-secret",
            ["Authorization"] = "Bearer authorization-secret",
            ["X-Scoped-Header"] = "origin-only"
        }, TestCancellation);

    [Fact]
    public async Task Cloned_cursors_keep_independent_positions_caches_and_disposal_without_prefetching()
    {
        var data = CreateData(BlockSize * 2);
        await using var server = new LoopbackServer((request, _) => Task.FromResult(RangeResponse(request, data)));
        await using var transport = CreateTransport(server.BaseUri);
        await using var original = await HttpRangeStream.OpenAsync(transport, server.BaseUri, TestCancellation);
        original.Position = 17;

        await using var clone = original.CloneCursor();

        Assert.Equal(0, clone.Position);
        Assert.Equal(original.Length, clone.Length);
        Assert.Equal(original.ContentType, clone.ContentType);
        Assert.Single(server.Requests);
        var buffer = new byte[5];
        Assert.Equal(5, await clone.ReadAsync(buffer, TestCancellation));
        Assert.Equal(data.AsSpan(0, 5).ToArray(), buffer);
        Assert.Equal(17, original.Position);
        Assert.Equal(5, clone.Position);
        Assert.Equal(2, server.Requests.Count);

        await original.DisposeAsync();
        clone.Position = 0;

        Assert.Equal(5, await clone.ReadAsync(buffer, TestCancellation));
        Assert.Equal(data.AsSpan(0, 5).ToArray(), buffer);
        Assert.Equal(2, server.Requests.Count);
        Assert.True(clone.CanRead);
        Assert.False(original.CanRead);
    }

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

    private static void AssertSanitized(PlaybackException error)
    {
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("query-secret", error.ToString());
        Assert.DoesNotContain("header-secret", error.ToString());
        Assert.DoesNotContain("authorization-secret", error.ToString());
        Assert.DoesNotContain("server-private-message", error.ToString());
        Assert.DoesNotContain("http://", error.ToString());
    }
}
