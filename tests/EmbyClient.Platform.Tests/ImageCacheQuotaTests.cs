using System.Net;
using System.Net.Http.Headers;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ImageCacheQuotaTests
{
    [Fact]
    public async Task Lowering_the_budget_immediately_evicts_the_least_recently_used_artwork()
    {
        using var fixture = new QuotaHarness();
        var token = TestContext.Current.CancellationToken;
        fixture.Cache.MaximumBytes = QuotaHandler.Image.Length * 3;
        var first = await fixture.GetAsync("first", token);
        await fixture.GetAsync("second", token);
        var third = await fixture.GetAsync("third", token);
        Assert.Same(first, await fixture.GetAsync("first", token));

        fixture.Cache.MaximumBytes = QuotaHandler.Image.Length * 2;

        Assert.Equal(new ImageCacheUsage(QuotaHandler.Image.Length * 2, 2, QuotaHandler.Image.Length * 2), fixture.Cache.Usage);
        Assert.Same(first, await fixture.GetAsync("first", token));
        Assert.Same(third, await fixture.GetAsync("third", token));
        Assert.Equal(3, fixture.Handler.RequestCount);
        await fixture.GetAsync("second", token);
        Assert.Equal(4, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task Disabling_retention_clears_usage_and_still_delivers_downloaded_artwork()
    {
        using var fixture = new QuotaHarness();
        var token = TestContext.Current.CancellationToken;
        await fixture.GetAsync("poster", token);

        fixture.Cache.MaximumBytes = 0;
        var firstUncached = await fixture.GetAsync("poster", token);
        var secondUncached = await fixture.GetAsync("poster", token);

        Assert.NotNull(firstUncached);
        Assert.NotNull(secondUncached);
        Assert.NotSame(firstUncached, secondUncached);
        Assert.Equal(new ImageCacheUsage(0, 0, 0), fixture.Cache.Usage);
        Assert.Equal(3, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task A_request_retired_by_disabling_retention_cannot_repopulate_a_reenabled_cache()
    {
        using var fixture = new QuotaHarness();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.BeforeResponse = async (sequence, token) =>
        {
            if (sequence != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var original = fixture.GetAsync("poster", deadline.Token);
        byte[]? retired = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            fixture.Cache.MaximumBytes = 0;
            fixture.Cache.MaximumBytes = 32L * 1024 * 1024;
        }
        finally
        {
            release.TrySetResult();
            retired = await original;
        }

        Assert.NotNull(retired);
        Assert.Equal(0, fixture.Cache.Usage.Bytes);
        var fresh = await fixture.GetAsync("poster", deadline.Token);
        Assert.NotSame(retired, fresh);
        Assert.Same(fresh, await fixture.GetAsync("poster", deadline.Token));
        Assert.Equal(2, fixture.Handler.RequestCount);
    }

    [Fact]
    public async Task An_in_flight_download_uses_the_current_smaller_budget()
    {
        using var fixture = new QuotaHarness();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.BeforeResponse = async (sequence, token) =>
        {
            if (sequence != 1) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var original = fixture.GetAsync("poster", deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            fixture.Cache.MaximumBytes = QuotaHandler.Image.Length - 1;
        }
        finally
        {
            release.TrySetResult();
            Assert.NotNull(await original);
        }

        Assert.Equal(0, fixture.Cache.Usage.EntryCount);
        Assert.NotNull(await fixture.GetAsync("poster", deadline.Token));
        Assert.Equal(2, fixture.Handler.RequestCount);
        Assert.Equal(0, fixture.Cache.Usage.Bytes);
    }

    [Fact]
    public async Task Usage_notifications_report_retained_bytes_capacity_and_clear_without_affecting_downloads()
    {
        using var fixture = new QuotaHarness();
        var observed = new List<ImageCacheUsage>();
        fixture.Cache.UsageChanged += (_, usage) => observed.Add(usage);
        fixture.Cache.UsageChanged += (_, _) => throw new InvalidOperationException("Synthetic UI feedback failure.");
        var token = TestContext.Current.CancellationToken;

        fixture.Cache.MaximumBytes = 64L * 1024 * 1024;
        Assert.NotNull(await fixture.GetAsync("poster", token));
        fixture.Cache.Clear();

        Assert.Equal(3, observed.Count);
        Assert.Equal(0, observed[0].EntryCount);
        Assert.Equal(64L * 1024 * 1024, observed[0].MaximumBytes);
        Assert.Equal(QuotaHandler.Image.Length, observed[1].Bytes);
        Assert.Equal(1, observed[1].EntryCount);
        Assert.Equal(new ImageCacheUsage(0, 0, 64L * 1024 * 1024), observed[2]);
    }

    [Fact]
    public void Negative_budgets_leave_the_last_capacity_unchanged()
    {
        var cache = new ImageCache();
        var original = cache.Usage;

        Assert.Throws<ArgumentOutOfRangeException>(() => cache.MaximumBytes = -1);

        Assert.Equal(original, cache.Usage);
    }

    private sealed class QuotaHarness : IDisposable
    {
        private readonly HttpClient _http;
        private readonly EmbyApiClient _api;
        public ImageCache Cache { get; } = new();
        public QuotaHandler Handler { get; } = new();

        public QuotaHarness()
        {
            _http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            _api = new EmbyApiClient(_http, new Uri("https://synthetic.example/emby/"),
                new ClientIdentity("Synthetic quota tests", "Windows", "synthetic-device", "0.1"),
                "synthetic-token", "synthetic-user") { RequestTimeout = TimeSpan.FromSeconds(15) };
        }

        public Task<byte[]?> GetAsync(string id, CancellationToken token) => Cache.GetAsync(_api,
            "synthetic-server", "synthetic-user", new BaseItemDto
            {
                Id = id,
                ImageTags = new Dictionary<string, string> { ["Primary"] = "synthetic-tag" }
            }, 176, 264, token);

        public void Dispose() => _http.Dispose();
    }

    private sealed class QuotaHandler : HttpMessageHandler
    {
        public static readonly byte[] Image = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jfUYAAAAASUVORK5CYII=");
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);
        public Func<int, CancellationToken, Task>? BeforeResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var sequence = Interlocked.Increment(ref _requests);
            if (BeforeResponse is not null) await BeforeResponse(sequence, token);
            var content = new ByteArrayContent(Image);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}
