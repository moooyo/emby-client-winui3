using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class TextSubtitleContractTests
{
    [Theory]
    [InlineData("Videos/movie-a/source-a/Subtitles/9/Stream.vtt?api_key=negotiated&StartPositionTicks=0")]
    [InlineData("/Videos/movie-a/source-a/Subtitles/9/Stream.vtt?api_key=negotiated&StartPositionTicks=0")]
    [InlineData("/emby/Videos/movie-a/source-a/Subtitles/9/Stream.vtt?api_key=negotiated&StartPositionTicks=0")]
    public async Task Internal_subtitle_routes_preserve_the_proxy_mount_and_negotiated_query(string deliveryUrl)
    {
        using var context = new ApiTestContext();
        const string content = "WEBVTT\n\n00:01.000 --> 00:02.000\nCaption";
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Text(content));

        var bytes = await context.Client.GetTextSubtitleAsync(deliveryUrl, TestContext.Current.CancellationToken);

        Assert.Equal(content, Encoding.UTF8.GetString(bytes));
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Videos/movie-a/source-a/Subtitles/9/Stream.vtt", request.Uri.AbsolutePath);
        Assert.Equal("negotiated", request.Query()["api_key"]);
        Assert.Equal("0", request.Query()["StartPositionTicks"]);
        Assert.Equal("token-a", request.Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", request.Header("X-Emby-Authorization"));
        Assert.Contains("text/vtt", request.Header("Accept"));
        Assert.DoesNotContain("application/json", request.Header("Accept"));
    }

    [Theory]
    [InlineData("https://other.example/Stream.vtt?api_key=token-a")]
    [InlineData("http://server.example/Stream.vtt")]
    [InlineData("https://server.example:444/Stream.vtt")]
    [InlineData("//other.example/Stream.vtt")]
    [InlineData("https://token-a@server.example/Stream.vtt")]
    [InlineData("https://server.example/Stream.vtt#token-a")]
    [InlineData("file:///C:/captions.vtt")]
    public async Task Unsafe_subtitle_targets_are_rejected_before_any_credentials_are_sent(string deliveryUrl)
    {
        using var context = new ApiTestContext();

        await Assert.ThrowsAsync<ArgumentException>(() => context.Client.GetTextSubtitleAsync(deliveryUrl,
            TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task A_redirect_is_not_replayed_at_an_external_subtitle_host()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Headers = { Location = new Uri("https://other.example/Stream.vtt") }
        });

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() => context.Client.GetTextSubtitleAsync("captions.vtt",
            TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, exception.StatusCode);
        Assert.Single(context.Handler.Requests);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/json")]
    [InlineData("image/png")]
    public async Task A_successful_non_subtitle_response_is_rejected(string contentType)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Text("not subtitles", contentType));

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetTextSubtitleAsync("captions.vtt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_advertised_file_limit_is_checked_before_reading_the_body()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var response = Text("small body");
            response.Content.Headers.ContentLength = EmbyApiClient.MaximumTextSubtitleBytes + 1;
            return Task.FromResult(response);
        };

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetTextSubtitleAsync("captions.vtt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_read_limit_also_applies_when_the_body_has_no_content_length()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var body = new StreamContent(new NonSeekableStream(new byte[EmbyApiClient.MaximumTextSubtitleBytes + 1]));
            body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            Assert.Null(body.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        };

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetTextSubtitleAsync("captions.srt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Empty_subtitles_do_not_look_like_a_successful_timeline_download()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(Text(string.Empty));

        await Assert.ThrowsAsync<EmbyProtocolException>(() => context.Client.GetTextSubtitleAsync("captions.vtt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_instead_of_becoming_a_timeout()
    {
        using var context = new ApiTestContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Text("unused");
        };
        var task = context.Client.GetTextSubtitleAsync("captions.vtt", cancellation.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task A_subtitle_timeout_remains_a_retryable_request_failure()
    {
        using var context = new ApiTestContext(requestTimeout: TimeSpan.FromMilliseconds(100));
        context.Handler.RespondAsync = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Text("unused");
        };

        await Assert.ThrowsAsync<TimeoutException>(() => context.Client.GetTextSubtitleAsync("captions.vtt",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Default_credential_headers_cannot_bypass_the_immutable_user_context()
    {
        using var context = new ApiTestContext();
        context.Http.DefaultRequestHeaders.Add("X-Emby-Token", "other-account");

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Client.GetTextSubtitleAsync("captions.vtt",
            TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    private static HttpResponseMessage Text(string content, string contentType = "text/vtt") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, contentType)
    };

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
