using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ImageCacheConcurrencyTests
{
    private static readonly TimeSpan GateDeadline = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Concurrent_misses_for_the_same_image_share_one_HTTP_request_and_one_result_array()
    {
        await using var fixture = new ImageHarness();
        var entered = NewGate();
        var release = NewGate();
        fixture.Handler.RespondAsync = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ImageHandler.Image();
        };

        var owner = fixture.GetAsync("shared-image");
        await entered.Task.WaitAsync(GateDeadline, fixture.Token);
        var waiters = Enumerable.Range(0, 11).Select(_ => fixture.GetAsync("shared-image")).ToArray();

        Assert.Single(fixture.Handler.Requests);
        Assert.All(waiters, task => Assert.False(task.IsCompleted));
        release.TrySetResult();
        var results = await Task.WhenAll(waiters.Prepend(owner)).WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(results[0]);
        Assert.All(results, bytes => Assert.Same(results[0], bytes));
        Assert.Same(results[0], await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Canceling_one_same_image_waiter_does_not_cancel_the_owner_or_another_waiter()
    {
        await using var fixture = new ImageHarness();
        var entered = NewGate();
        var release = NewGate();
        fixture.Handler.RespondAsync = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ImageHandler.Image();
        };
        using var waiterCancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);

        var owner = fixture.GetAsync("shared-image");
        await entered.Task.WaitAsync(GateDeadline, fixture.Token);
        var canceledWaiter = fixture.GetAsync("shared-image", waiterCancellation.Token);
        var survivingWaiter = fixture.GetAsync("shared-image");
        waiterCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter).WaitAsync(GateDeadline, fixture.Token);
        Assert.False(Assert.Single(fixture.Handler.Requests).CancellationToken.IsCancellationRequested);
        Assert.False(owner.IsCompleted);
        Assert.False(survivingWaiter.IsCompleted);
        release.TrySetResult();
        var results = await Task.WhenAll(owner, survivingWaiter).WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(results[0]);
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Canceling_the_HTTP_owner_allows_same_image_waiters_to_share_one_retry()
    {
        await using var fixture = new ImageHarness();
        var ownerEntered = NewGate();
        var ownerRelease = NewGate();
        var retryEntered = NewGate();
        var retryRelease = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 1)
            {
                ownerEntered.TrySetResult();
                await ownerRelease.Task.WaitAsync(cancellationToken);
            }
            else
            {
                retryEntered.TrySetResult();
                await retryRelease.Task.WaitAsync(cancellationToken);
            }
            return ImageHandler.Image();
        };
        using var ownerCancellation = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);

        var owner = fixture.GetAsync("shared-image", ownerCancellation.Token);
        await ownerEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        var waiters = new[] { fixture.GetAsync("shared-image"), fixture.GetAsync("shared-image") };
        Assert.Single(fixture.Handler.Requests);
        ownerCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner).WaitAsync(GateDeadline, fixture.Token);
        await retryEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        Assert.Equal(2, fixture.Handler.Requests.Length);
        Assert.All(waiters, task => Assert.False(task.IsCompleted));
        retryRelease.TrySetResult();
        var results = await Task.WhenAll(waiters).WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(results[0]);
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token));
        Assert.Equal(2, fixture.Handler.Requests.Length);
    }

    [Fact]
    public async Task An_HTTP_failure_reaches_its_owner_and_allows_same_image_waiters_to_share_one_retry()
    {
        await using var fixture = new ImageHarness();
        var ownerEntered = NewGate();
        var failureRelease = NewGate();
        var retryEntered = NewGate();
        var retryRelease = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 1)
            {
                ownerEntered.TrySetResult();
                await failureRelease.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            retryEntered.TrySetResult();
            await retryRelease.Task.WaitAsync(cancellationToken);
            return ImageHandler.Image();
        };

        var owner = fixture.GetAsync("shared-image");
        await ownerEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        var waiters = new[] { fixture.GetAsync("shared-image"), fixture.GetAsync("shared-image") };
        Assert.Single(fixture.Handler.Requests);
        failureRelease.TrySetResult();

        var failure = await Assert.ThrowsAsync<EmbyApiException>(() => owner).WaitAsync(GateDeadline, fixture.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
        await retryEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        Assert.Equal(2, fixture.Handler.Requests.Length);
        Assert.All(waiters, task => Assert.False(task.IsCompleted));
        retryRelease.TrySetResult();
        var results = await Task.WhenAll(waiters).WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(results[0]);
        Assert.Same(results[0], results[1]);
        Assert.Same(results[0], await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token));
        Assert.Equal(2, fixture.Handler.Requests.Length);
    }

    [Fact]
    public async Task Clear_allows_the_same_image_in_a_new_generation_to_finish_before_the_old_owner()
    {
        await using var fixture = new ImageHarness();
        var oldEntered = NewGate();
        var oldRelease = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 1)
            {
                oldEntered.TrySetResult();
                await oldRelease.Task.WaitAsync(cancellationToken);
            }
            return ImageHandler.Image();
        };

        var oldOwner = fixture.GetAsync("shared-image");
        await oldEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        fixture.Cache.Clear();

        var freshBytes = await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(freshBytes);
        Assert.Equal(2, fixture.Handler.Requests.Length);
        Assert.False(oldOwner.IsCompleted);
        oldRelease.TrySetResult();
        var oldBytes = await oldOwner.WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(oldBytes);
        Assert.NotSame(oldBytes, freshBytes);
        Assert.Same(freshBytes, await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token));
        Assert.Equal(2, fixture.Handler.Requests.Length);
    }

    [Fact]
    public async Task Clear_prevents_both_an_old_owner_and_its_queued_waiter_from_repopulating_the_cache()
    {
        await using var fixture = new ImageHarness();
        var oldEntered = NewGate();
        var oldRelease = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 1)
            {
                oldEntered.TrySetResult();
                await oldRelease.Task.WaitAsync(cancellationToken);
            }
            return ImageHandler.Image();
        };

        var oldOwner = fixture.GetAsync("shared-image");
        await oldEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        var oldWaiter = fixture.GetAsync("shared-image");
        Assert.Single(fixture.Handler.Requests);
        Assert.False(oldWaiter.IsCompleted);
        fixture.Cache.Clear();
        oldRelease.TrySetResult();
        var oldResults = await Task.WhenAll(oldOwner, oldWaiter).WaitAsync(GateDeadline, fixture.Token);
        Assert.All(oldResults, bytes => Assert.NotNull(bytes));
        var requestsBeforeFreshDownload = fixture.Handler.Requests.Length;

        var freshBytes = await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(freshBytes);
        Assert.All(oldResults, bytes => Assert.NotSame(bytes, freshBytes));
        Assert.Equal(requestsBeforeFreshDownload + 1, fixture.Handler.Requests.Length);
        Assert.Same(freshBytes, await fixture.GetAsync("shared-image").WaitAsync(GateDeadline, fixture.Token));
        Assert.Equal(requestsBeforeFreshDownload + 1, fixture.Handler.Requests.Length);
    }

    [Fact]
    public async Task Same_image_waiters_leave_HTTP_slots_available_for_three_other_images()
    {
        await using var fixture = new ImageHarness();
        var ownerEntered = NewGate();
        var fourEntered = NewGate();
        var release = NewGate();
        fixture.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Sequence == 1) ownerEntered.TrySetResult();
            if (request.Sequence == 4) fourEntered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return ImageHandler.Image();
        };

        var owner = fixture.GetAsync("shared-image");
        await ownerEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        var sameImageWaiters = Enumerable.Range(0, 8).Select(_ => fixture.GetAsync("shared-image")).ToArray();
        var distinctImages = Enumerable.Range(0, 3).Select(index => fixture.GetAsync($"distinct-{index}")).ToArray();
        await fourEntered.Task.WaitAsync(GateDeadline, fixture.Token);
        var queuedDistinctImage = fixture.GetAsync("distinct-3");

        Assert.Equal(4, fixture.Handler.Requests.Length);
        Assert.Equal(4, fixture.Handler.PeakConcurrency);
        Assert.Equal(4, fixture.Handler.Requests.Select(request => request.Uri.AbsolutePath).Distinct().Count());
        Assert.False(queuedDistinctImage.IsCompleted);
        release.TrySetResult();
        var sharedResults = await Task.WhenAll(sameImageWaiters.Prepend(owner)).WaitAsync(GateDeadline, fixture.Token);
        var distinctResults = await Task.WhenAll(distinctImages.Append(queuedDistinctImage)).WaitAsync(GateDeadline, fixture.Token);

        Assert.NotNull(sharedResults[0]);
        Assert.All(sharedResults, bytes => Assert.Same(sharedResults[0], bytes));
        Assert.All(distinctResults, bytes => Assert.NotNull(bytes));
        Assert.Equal(5, fixture.Handler.Requests.Length);
        Assert.Equal(4, fixture.Handler.PeakConcurrency);
    }

    private static TaskCompletionSource NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ImageHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline =
            CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly List<Task<byte[]?>> _downloads = [];
        private readonly HttpClient _http;
        private readonly EmbyApiClient _api;

        public ImageCache Cache { get; } = new();
        public ImageHandler Handler { get; } = new();
        public CancellationToken Token => _deadline.Token;

        public ImageHarness()
        {
            _deadline.CancelAfter(TimeSpan.FromSeconds(15));
            _http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            _api = new EmbyApiClient(_http, new Uri("https://synthetic.example/proxy/emby/"),
                new ClientIdentity("Synthetic image concurrency tests", "Windows", "synthetic-device", "0.1"),
                "synthetic-token", "user-a") { RequestTimeout = TimeSpan.FromSeconds(15) };
        }

        public Task<byte[]?> GetAsync(string imageId, CancellationToken? cancellationToken = null)
        {
            var item = new BaseItemDto
            {
                Id = imageId,
                ImageTags = new Dictionary<string, string> { ["Primary"] = "synthetic-tag-v1" }
            };
            var download = Cache.GetAsync(_api, "server-a", "user-a", item, 176, 264, cancellationToken ?? Token);
            _downloads.Add(download);
            return download;
        }

        public async ValueTask DisposeAsync()
        {
            _deadline.Cancel();
            _http.CancelPendingRequests();
            try
            {
                await Task.WhenAll(_downloads).WaitAsync(GateDeadline);
            }
            catch (Exception)
            {
                // Each test observes its expected outcomes; cleanup must preserve any original assertion failure.
            }
            finally
            {
                _http.Dispose();
                _deadline.Dispose();
            }
        }
    }

    private sealed record ImageRequest(int Sequence, Uri Uri, CancellationToken CancellationToken);

    private sealed class ImageHandler : HttpMessageHandler
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jfUYAAAAASUVORK5CYII=");
        private readonly ConcurrentQueue<ImageRequest> _requests = new();
        private int _sequence;
        private int _active;
        private int _peakConcurrency;

        public Func<ImageRequest, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; } =
            (_, _) => Task.FromResult(Image());
        public ImageRequest[] Requests => _requests.ToArray();
        public int PeakConcurrency => Volatile.Read(ref _peakConcurrency);

        public static HttpResponseMessage Image()
        {
            var content = new ByteArrayContent(Png);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var recorded = new ImageRequest(Interlocked.Increment(ref _sequence), request.RequestUri!, cancellationToken);
            _requests.Enqueue(recorded);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var peak = Volatile.Read(ref _peakConcurrency);
                if (active <= peak || Interlocked.CompareExchange(ref _peakConcurrency, active, peak) == peak) break;
            }
            try
            {
                return await RespondAsync(recorded, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
