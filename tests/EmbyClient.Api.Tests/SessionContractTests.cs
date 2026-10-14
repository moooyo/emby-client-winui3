using System.Net;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class SessionContractTests
{
    [Fact]
    public async Task Current_session_is_device_scoped_and_preserves_server_transcode_scalars()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            [{
                "Id":"session/a?scope=all&#%","UserId":"user-a","DeviceId":"device-a",
                "NowPlayingItem":{"Id":"item-a"},"PlayState":{"MediaSourceId":"source-a","PlayMethod":"Transcode"},
                "TranscodingInfo":{"Bitrate":5000000,"AudioBitrate":192000,"VideoBitrate":4808000,
                    "VideoCodec":"h264","AudioCodec":"aac","Container":"ts","Framerate":23.976}
            }]
            """);

        var session = Assert.IsType<PlaybackSessionInfo>(await context.Client.GetCurrentPlaybackSessionAsync(
            "item-a", "play-a", "session/a?scope=all&#%", TestContext.Current.CancellationToken));

        var transcode = Assert.IsType<TranscodingInfo>(session.TranscodingInfo);
        Assert.Equal(5000000, transcode.Bitrate);
        Assert.Equal(192000, transcode.AudioBitrate);
        Assert.Equal(4808000, transcode.VideoBitrate);
        Assert.Equal("h264", transcode.VideoCodec);
        Assert.Equal("source-a", session.PlayState?.MediaSourceId);
        Assert.Null(session.ExtensionData);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Sessions", request.Uri.AbsolutePath);
        Assert.Equal("device-a", request.Query()["DeviceId"]);
        Assert.Equal("session/a?scope=all&#%", request.Query()["Id"]);
        Assert.Equal(2, request.Query().Count);
        Assert.Equal("token-a", request.Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", request.Header("X-Emby-Authorization"));
        Assert.Equal(string.Empty, request.Uri.Fragment);
    }

    [Theory]
    [InlineData("device-b", "user-a", "item-a", "session-a")]
    [InlineData("DEVICE-A", "user-a", "item-a", "session-a")]
    [InlineData("device-a", "user-b", "item-a", "session-a")]
    [InlineData("device-a", "USER-A", "item-a", "session-a")]
    [InlineData("device-a", "user-a", "item-b", "session-a")]
    [InlineData("device-a", "user-a", "ITEM-A", "session-a")]
    [InlineData("device-a", "user-a", "item-a", "session-b")]
    [InlineData("device-a", "user-a", "item-a", "")]
    public async Task Server_filters_are_not_trusted_as_proof_of_session_identity(
        string deviceId, string userId, string itemId, string sessionId)
    {
        using var context = new ApiTestContext();
        context.ReturnJson($$$"""
            [{"Id":"{{{sessionId}}}","DeviceId":"{{{deviceId}}}","UserId":"{{{userId}}}",
                "NowPlayingItem":{"Id":"{{{itemId}}}"},"TranscodingInfo":{"Bitrate":5000000}}]
            """);

        var session = await context.Client.GetCurrentPlaybackSessionAsync("item-a", "play-a", "session-a",
            TestContext.Current.CancellationToken);

        Assert.Null(session);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{\"Id\":\"session-a\",\"DeviceId\":\"device-a\",\"UserId\":\"user-a\"}]")]
    [InlineData("[{\"DeviceId\":\"device-a\",\"UserId\":\"user-a\",\"NowPlayingItem\":{\"Id\":\"item-a\"}}]")]
    [InlineData("[{\"Id\":\"session-a\",\"UserId\":\"user-a\",\"NowPlayingItem\":{\"Id\":\"item-a\"}}]")]
    [InlineData("[{\"Id\":\"session-a\",\"DeviceId\":\"device-a\",\"NowPlayingItem\":{\"Id\":\"item-a\"}}]")]
    public async Task Missing_server_identity_does_not_create_a_playback_session(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        Assert.Null(await context.Client.GetCurrentPlaybackSessionAsync("item-a", "play-a",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Multiple_matching_sessions_are_ambiguous_even_when_one_has_transcode_data()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            [
                {"Id":"session-a","DeviceId":"device-a","UserId":"user-a","NowPlayingItem":{"Id":"item-a"}},
                {"Id":"session-b","DeviceId":"device-a","UserId":"user-a","NowPlayingItem":{"Id":"item-a"},
                    "TranscodingInfo":{"Bitrate":5000000}}
            ]
            """);

        Assert.Null(await context.Client.GetCurrentPlaybackSessionAsync("item-a", "play-a",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\"PlaySessionId\":\"play-b\"")]
    [InlineData("\"playSessionId\":\"PLAY-A\"")]
    [InlineData("\"PlaySessionId\":42")]
    [InlineData("\"PlayState\":{\"PlaySessionId\":\"play-b\"}")]
    [InlineData("\"PlaySessionId\":\"play-a\",\"PlayState\":{\"PlaySessionId\":\"play-b\"}")]
    public async Task A_returned_playback_identifier_must_match_in_every_available_location(string responseFields)
    {
        using var context = new ApiTestContext();
        context.ReturnJson($$$"""
            [{"Id":"session-a","DeviceId":"device-a","UserId":"user-a","NowPlayingItem":{"Id":"item-a"},
                {{{responseFields}}},"TranscodingInfo":{"Bitrate":5000000}}]
            """);

        Assert.Null(await context.Client.GetCurrentPlaybackSessionAsync("item-a", "play-a",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\"PlaySessionId\":\"play-a\"")]
    [InlineData("\"PlayState\":{\"PlaySessionId\":\"play-a\"}")]
    public async Task A_server_playback_identifier_can_confirm_the_match_without_becoming_a_required_sdk_field(
        string responseFields)
    {
        using var context = new ApiTestContext();
        context.ReturnJson($$$"""
            [{"Id":"session-a","DeviceId":"device-a","UserId":"user-a","NowPlayingItem":{"Id":"item-a"},
                {{{responseFields}}},"TranscodingInfo":{"VideoCodec":"h264"}}]
            """);

        var session = Assert.IsType<PlaybackSessionInfo>(await context.Client.GetCurrentPlaybackSessionAsync(
            "item-a", "play-a", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Null(session.TranscodingInfo?.Bitrate);
        Assert.Null(session.TranscodingInfo?.AudioBitrate);
        Assert.Null(session.TranscodingInfo?.VideoBitrate);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Unsupported_session_metadata_remains_absent(HttpStatusCode statusCode)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(RecordingHandler.Json("{}", statusCode));

        Assert.Null(await context.Client.GetCurrentPlaybackSessionAsync("item-a", "play-a",
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Authentication_failures_are_not_hidden_as_missing_transcode_metadata()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(RecordingHandler.Json("{}", HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() => context.Client.GetCurrentPlaybackSessionAsync(
            "item-a", "play-a", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }
}
