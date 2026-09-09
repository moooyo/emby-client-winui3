using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ImageCacheTests
{
    private static readonly TimeSpan GateDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task At_most_four_distinct_image_downloads_enter_HTTP_while_the_remaining_requests_wait()
    {
        using var fixture = new ImageHarness();
        var enteredFour = NewGate();
        var release = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 4) enteredFour.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ImageHandler.Png;
        };
        using var deadline = NewDeadline();
        var token = deadline.Token;
        var downloads = Enumerable.Range(0, 12).Select(index => fixture.GetAsync($"image-{index}", token)).ToArray();

        try
        {
            await enteredFour.Task.WaitAsync(GateDeadline, token);
            Assert.Equal(4, fixture.Handler.Requests.Length);
            Assert.Equal(4, fixture.Handler.PeakConcurrency);
            Assert.All(downloads, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(downloads);
        }

        Assert.Equal(12, fixture.Handler.Requests.Length);
        Assert.Equal(4, fixture.Handler.PeakConcurrency);
    }

    [Fact]
    public async Task Entry_capacity_evicts_the_least_recently_used_image_and_preserves_a_recent_hit()
    {
        using var fixture = new ImageHarness();
        var token = TestContext.Current.CancellationToken;
        for (var index = 0; index < 128; index++) await fixture.GetAsync($"image-{index}", token);
        Assert.Equal(128, fixture.Handler.Requests.Length);

        var recentlyUsed = await fixture.GetAsync("image-0", token);
        Assert.Equal(128, fixture.Handler.Requests.Length);
        await fixture.GetAsync("image-128", token);
        Assert.Same(recentlyUsed, await fixture.GetAsync("image-0", token));
        Assert.Equal(129, fixture.Handler.Requests.Length);

        await fixture.GetAsync("image-1", token);

        Assert.Equal(130, fixture.Handler.Requests.Length);
        Assert.Equal(1, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/Items/image-0/Images/Primary", StringComparison.Ordinal)));
        Assert.Equal(2, fixture.Handler.Requests.Count(request => request.Uri.AbsolutePath.EndsWith("/Items/image-1/Images/Primary", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Matching_item_ids_never_share_cached_images_between_users_or_server_identities()
    {
        using var fixture = new ImageHarness();
        var token = TestContext.Current.CancellationToken;
        var otherUser = fixture.CreateClient("https://synthetic.example/proxy/emby/", "user-b", "synthetic-token-b");

        var firstUserImage = await fixture.GetAsync("shared-image", token);
        var otherUserImage = await fixture.GetAsync("shared-image", token, otherUser);
        var otherServerImage = await fixture.GetAsync("shared-image", token, serverId: "server-b");

        Assert.NotSame(firstUserImage, otherUserImage);
        Assert.NotSame(firstUserImage, otherServerImage);
        Assert.Same(firstUserImage, await fixture.GetAsync("shared-image", token));
        Assert.Same(otherUserImage, await fixture.GetAsync("shared-image", token, otherUser));
        Assert.Same(otherServerImage, await fixture.GetAsync("shared-image", token, serverId: "server-b"));
        Assert.Equal(3, fixture.Handler.Requests.Length);
        Assert.Equal("synthetic-token-a", fixture.Handler.Requests[0].Token);
        Assert.Equal("synthetic-token-b", fixture.Handler.Requests[1].Token);
        Assert.Equal("synthetic-token-a", fixture.Handler.Requests[2].Token);
    }

    [Fact]
    public async Task API_origins_and_proxy_roots_have_independent_cache_entries_for_the_same_server_and_user()
    {
        using var fixture = new ImageHarness();
        var token = TestContext.Current.CancellationToken;
        var alternatePath = fixture.CreateClient("https://synthetic.example/other/emby/", "user-a", "synthetic-token-a");
        var alternateOrigin = fixture.CreateClient("https://other-synthetic.example/proxy/emby/", "user-a", "synthetic-token-a");

        var original = await fixture.GetAsync("shared-image", token);
        var pathImage = await fixture.GetAsync("shared-image", token, alternatePath);
        var originImage = await fixture.GetAsync("shared-image", token, alternateOrigin);

        Assert.NotSame(original, pathImage);
        Assert.NotSame(original, originImage);
        Assert.Same(pathImage, await fixture.GetAsync("shared-image", token, alternatePath));
        Assert.Same(originImage, await fixture.GetAsync("shared-image", token, alternateOrigin));
        Assert.Same(original, await fixture.GetAsync("shared-image", token));
        Assert.Equal(3, fixture.Handler.Requests.Length);
        Assert.Equal("/other/emby/Items/shared-image/Images/Primary", fixture.Handler.Requests[1].Uri.AbsolutePath);
        Assert.Equal("other-synthetic.example", fixture.Handler.Requests[2].Uri.Host);
    }

    [Fact]
    public async Task Clear_prevents_an_in_flight_old_download_from_repopulating_the_cache()
    {
        using var fixture = new ImageHarness();
        var entered = NewGate();
        var release = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return ImageHandler.Png;
        };
        using var deadline = NewDeadline();
        var token = deadline.Token;
        var oldDownload = fixture.GetAsync("shared-image", token);
        byte[]? oldBytes = null;

        try
        {
            await entered.Task.WaitAsync(GateDeadline, token);
            fixture.Cache.Clear();
        }
        finally
        {
            release.TrySetResult();
            oldBytes = await oldDownload;
        }
        Assert.NotNull(oldBytes);

        var freshBytes = await fixture.GetAsync("shared-image", token);

        Assert.Equal(2, fixture.Handler.Requests.Length);
        Assert.NotSame(oldBytes, freshBytes);
        Assert.Same(freshBytes, await fixture.GetAsync("shared-image", token));
        Assert.Equal(2, fixture.Handler.Requests.Length);
    }

    [Fact]
    public async Task Canceling_a_request_queued_behind_four_downloads_never_sends_it_to_HTTP()
    {
        using var fixture = new ImageHarness();
        var enteredFour = NewGate();
        var release = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 4) enteredFour.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ImageHandler.Png;
        };
        using var deadline = NewDeadline();
        var token = deadline.Token;
        var active = Enumerable.Range(0, 4).Select(index => fixture.GetAsync($"active-{index}", token)).ToArray();
        using var queuedCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);

        try
        {
            await enteredFour.Task.WaitAsync(GateDeadline, token);
            var queued = fixture.GetAsync("canceled-image", queuedCancellation.Token);
            queuedCancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.Equal(4, fixture.Handler.Requests.Length);
            Assert.DoesNotContain(fixture.Handler.Requests, request => request.Uri.AbsolutePath.Contains("canceled-image", StringComparison.Ordinal));
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(active);
        }

        Assert.Equal(4, fixture.Handler.Requests.Length);
        Assert.NotNull(await fixture.GetAsync("canceled-image", token));
        Assert.Equal(5, fixture.Handler.Requests.Length);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CancellationTokenSource NewDeadline()
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(15));
        return source;
    }

    private sealed class ImageHarness : IDisposable
    {
        private readonly HttpClient _http;
        private readonly EmbyApiClient _api;
        public ImageCache Cache { get; } = new();
        public ImageHandler Handler { get; } = new();

        public ImageHarness()
        {
            _http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            _api = CreateClient("https://synthetic.example/proxy/emby/", "user-a", "synthetic-token-a");
        }

        public EmbyApiClient CreateClient(string root, string userId, string accessToken) => new(
            _http, new Uri(root), new ClientIdentity("Synthetic image tests", "Windows", "synthetic-device", "0.1"),
            accessToken, userId) { RequestTimeout = TimeSpan.FromSeconds(15) };

        public Task<byte[]?> GetAsync(string imageId, CancellationToken cancellationToken,
            EmbyApiClient? api = null, string serverId = "server-a")
        {
            api ??= _api;
            var item = new BaseItemDto
            {
                Id = imageId,
                ImageTags = new Dictionary<string, string> { ["Primary"] = "synthetic-tag-v1" }
            };
            return Cache.GetAsync(api, serverId, api.UserId!, item, 176, 264, cancellationToken);
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed record ImageRequest(int Sequence, Uri Uri, string? Token);

    private sealed class ImageHandler : HttpMessageHandler
    {
        public static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jfUYAAAAASUVORK5CYII=");
        private readonly ConcurrentQueue<ImageRequest> _requests = new();
        private int _sequence;
        private int _active;
        private int _peakConcurrency;

        public Func<ImageRequest, CancellationToken, Task<byte[]>> RespondAsync { get; set; } =
            (_, _) => Task.FromResult(Png);
        public ImageRequest[] Requests => _requests.ToArray();
        public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var recorded = new ImageRequest(Interlocked.Increment(ref _sequence), request.RequestUri!,
                request.Headers.TryGetValues("X-Emby-Token", out var tokens) ? Assert.Single(tokens) : null);
            _requests.Enqueue(recorded);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var peak = Volatile.Read(ref _peakConcurrency);
                if (active <= peak || Interlocked.CompareExchange(ref _peakConcurrency, active, peak) == peak) break;
            }
            try
            {
                var bytes = await RespondAsync(recorded, cancellationToken);
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
