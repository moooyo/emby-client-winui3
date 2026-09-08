using System.Net;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class JsonContentLengthTests
{
    [Theory]
    [InlineData("login", "/emby/Users/AuthenticateByName")]
    [InlineData("playback-info", "/emby/Items/movie-a/PlaybackInfo")]
    [InlineData("playback-start", "/emby/Sessions/Playing")]
    [InlineData("playback-progress", "/emby/Sessions/Playing/Progress")]
    [InlineData("playback-stop", "/emby/Sessions/Playing/Stopped")]
    [InlineData("live-open", "/emby/LiveStreams/Open")]
    [InlineData("capabilities", "/emby/Sessions/Capabilities/Full")]
    public async Task Json_posts_advertise_the_exact_UTF8_content_length_before_any_body_read(string operation, string expectedPath)
    {
        using var handler = new ContentLengthRequiredHandler();
        using var http = new HttpClient(handler);
        var client = new EmbyApiClient(http, new Uri("https://fixture.example/emby/"),
            ApiTestContext.Identity, "synthetic-token", "user-a");
        var cancellationToken = TestContext.Current.CancellationToken;

        switch (operation)
        {
            case "login":
                await client.AuthenticateByNameAsync("synthetic-user", "synthetic-p\u00E4ssword-\uD83D\uDD12", cancellationToken);
                break;
            case "playback-info":
                await client.GetPlaybackInfoAsync("movie-a", new PlaybackInfoRequest
                {
                    StartTimeTicks = 0,
                    EnableDirectPlay = false,
                    EnableDirectStream = true,
                    DeviceProfile = new DeviceProfile { Name = "Synthetic \u00E9 profile" }
                }, cancellationToken);
                break;
            case "playback-start":
                await client.ReportPlaybackStartAsync(new PlaybackStartInfo
                {
                    ItemId = "movie-a", PlaySessionId = "play-a", PositionTicks = 0, IsPaused = false
                }, cancellationToken);
                break;
            case "playback-progress":
                await client.ReportPlaybackProgressAsync(new PlaybackProgressInfo
                {
                    ItemId = "movie-a", PlaySessionId = "play-a", PositionTicks = 9007199254740993,
                    EventName = "TimeUpdate"
                }, cancellationToken);
                break;
            case "playback-stop":
                await client.ReportPlaybackStoppedAsync(new PlaybackStopInfo
                {
                    ItemId = "movie-a", PlaySessionId = "play-a", PositionTicks = 0, Failed = false
                }, cancellationToken);
                break;
            case "live-open":
                await client.OpenLiveStreamAsync(new LiveStreamRequest
                {
                    OpenToken = "synthetic-open-token", PlaySessionId = "play-a", ItemId = 123
                }, cancellationToken);
                break;
            case "capabilities":
                await client.SetCapabilitiesAsync("session-a", new ClientCapabilities(), cancellationToken);
                break;
            default:
                throw new InvalidOperationException("The test operation is not implemented.");
        }

        Assert.Equal(expectedPath, handler.RequestPath);
        Assert.Equal(1, handler.RequestCount);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    private sealed class ContentLengthRequiredHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? RequestPath { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var content = Assert.IsAssignableFrom<HttpContent>(request.Content);

            // Inspect the outgoing headers before buffering: reading JsonContent first would hide the regression.
            var declaredLengthBeforeReading = content.Headers.ContentLength;
            Assert.True(declaredLengthBeforeReading is > 0,
                "Emby JSON requests must have a known Content-Length before the transport reads the body.");
            Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", content.Headers.ContentType?.CharSet);
            Assert.False(request.Headers.TransferEncodingChunked == true);

            var bytes = await content.ReadAsByteArrayAsync(cancellationToken);
            Assert.Equal(bytes.LongLength, declaredLengthBeforeReading!.Value);
            using var document = JsonDocument.Parse(bytes);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            RequestCount++;
            RequestPath = request.RequestUri!.AbsolutePath;

            return RequestPath switch
            {
                "/emby/Users/AuthenticateByName" => RecordingHandler.Json("{\"AccessToken\":\"synthetic-token\",\"User\":{\"Id\":\"user-a\"}}"),
                "/emby/Items/movie-a/PlaybackInfo" => RecordingHandler.Json("{\"PlaySessionId\":\"play-a\",\"MediaSources\":[]}"),
                "/emby/LiveStreams/Open" => RecordingHandler.Json("{\"MediaSource\":{\"Id\":\"source-a\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent)
            };
        }
    }
}
