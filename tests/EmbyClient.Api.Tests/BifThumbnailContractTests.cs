using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class BifThumbnailContractTests
{
    [Fact]
    public async Task The_official_binary_route_uses_only_the_required_width_and_same_account_headers()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Binary(BifTestData.Create(500, 0, 7)));

        var result = Assert.IsType<BifThumbnailSet>(await context.Client.GetBifThumbnailsAsync(
            "item/a?Width=1&#%", 320, 40_000_000, TestContext.Current.CancellationToken));

        Assert.Equal(new long[] { 0, 35_000_000 }, result.Thumbnails.Select(sample => sample.PositionTicks));
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Videos/item%2Fa%3FWidth%3D1%26%23%25/index.bif", request.Uri.AbsolutePath);
        Assert.Equal("320", request.Query()["Width"]);
        Assert.Single(request.Query());
        Assert.Equal("token-a", request.Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", request.Header("X-Emby-Authorization"));
        Assert.Equal("application/octet-stream", request.Header("Accept"));
        Assert.Null(request.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Unavailable_archives_do_not_trigger_guessed_image_routes(HttpStatusCode statusCode)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(statusCode));

        Assert.Null(await context.Client.GetBifThumbnailsAsync("item-a", 240,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("/proxy/emby/Videos/item-a/index.bif", Assert.Single(context.Handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Authentication_failures_are_not_hidden_as_a_missing_dataset()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task A_redirect_is_an_error_without_replaying_credentials_at_the_destination()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://other.example/index.bif?api_key=secret") }
        });

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, exception.StatusCode);
        Assert.Single(context.Handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4097)]
    public async Task Invalid_widths_are_rejected_before_a_request(int width)
    {
        using var context = new ApiTestContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", width, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task A_negative_duration_is_rejected_before_a_request()
    {
        using var context = new ApiTestContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, -1, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("image/jpeg")]
    public async Task A_non_archive_content_type_is_rejected(string contentType)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Binary(BifTestData.Create(1000, 0), contentType));

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_missing_content_type_still_requires_a_valid_BIF_archive()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Binary(BifTestData.Create(1000, 3), null));

        var result = Assert.IsType<BifThumbnailSet>(await context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(30_000_000, Assert.Single(result.Thumbnails).PositionTicks);
    }

    [Fact]
    public async Task An_empty_BIF_archive_stays_empty()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Binary(BifTestData.Create(1000)));

        var result = Assert.IsType<BifThumbnailSet>(await context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(result.Thumbnails);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(71)]
    public async Task An_empty_or_truncated_binary_body_is_a_protocol_failure(int length)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Binary(new byte[length]));

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_advertised_package_limit_is_checked_before_reading_the_body()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var response = Binary(BifTestData.Create(1000));
            response.Content.Headers.ContentLength = BifThumbnailReader.MaximumPackageBytes + 1;
            return Task.FromResult(response);
        };

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_package_limit_applies_to_chunked_or_missing_length_responses()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var content = new StreamContent(new RepeatingStream(BifThumbnailReader.MaximumPackageBytes + 1));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var context = new ApiTestContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Binary(BifTestData.Create(1000));
        };
        var task = context.Client.GetBifThumbnailsAsync("item-a", 240, cancellationToken: cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_request_deadline_remains_a_retryable_timeout()
    {
        using var context = new ApiTestContext(requestTimeout: TimeSpan.FromMilliseconds(100));
        context.Handler.RespondAsync = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Binary(BifTestData.Create(1000));
        };

        await Assert.ThrowsAsync<TimeoutException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Default_credentials_cannot_replace_the_immutable_user_context()
    {
        using var context = new ApiTestContext();
        context.Http.DefaultRequestHeaders.Add("X-Emby-Token", "other-account");

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Client.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task The_binary_operation_requires_an_authenticated_user()
    {
        using var context = new ApiTestContext();
        var publicClient = new EmbyApiClient(context.Http, context.Client.ApiRoot, ApiTestContext.Identity);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publicClient.GetBifThumbnailsAsync(
            "item-a", 240, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    private static HttpResponseMessage Binary(byte[] bytes, string? contentType = "application/octet-stream")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        if (contentType is not null) response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private sealed class RepeatingStream(int length) : Stream
    {
        private int remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(remaining, count);
            buffer.AsSpan(offset, read).Clear();
            remaining -= read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(remaining, buffer.Length);
            buffer.Span[..read].Clear();
            remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
