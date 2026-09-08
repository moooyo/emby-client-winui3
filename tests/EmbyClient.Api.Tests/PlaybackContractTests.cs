using System.Net;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class PlaybackContractTests
{
    [Fact]
    public async Task Negotiation_serializes_the_selected_profile_and_preserves_source_capabilities_without_reflection()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {
              "PlaySessionId":"play-session-a",
              "MediaSources":[{
                "Id":"source-a","SupportsDirectPlay":false,"SupportsDirectStream":true,
                "SupportsTranscoding":true,"DirectStreamUrl":"/emby/Videos/movie-a/stream?Static=true",
                "AddApiKeyToDirectStreamUrl":true,"RunTimeTicks":9007199254740993,
                "DefaultAudioStreamIndex":7,"RequiredHttpHeaders":{"Referer":"https://source.example/"},
                "MediaStreams":[{"Index":7,"Type":"Audio","Codec":"aac"},{"Index":11,"Type":"Subtitle","DeliveryMethod":"FutureDelivery"}],
                "FutureSourceMetadata":{"Enabled":true}
              }]
            }
            """);

        var result = await context.Client.GetPlaybackInfoAsync("movie-a", new PlaybackInfoRequest
        {
            MediaSourceId = "source-a",
            MaxStreamingBitrate = 20000000,
            StartTimeTicks = 9007199254740993,
            AudioStreamIndex = 7,
            SubtitleStreamIndex = -1,
            EnableDirectPlay = false,
            EnableDirectStream = true,
            EnableTranscoding = true,
            IsPlayback = true,
            DeviceProfile = new DeviceProfile
            {
                Name = "AVC AAC Contract Fixture",
                DirectPlayProfiles = [new() { Type = "Video", Container = "mp4", VideoCodec = "h264", AudioCodec = "aac" }],
                TranscodingProfiles = [new() { Type = "Video", Container = "ts", Protocol = "hls", VideoCodec = "h264", AudioCodec = "aac", MaxAudioChannels = "2" }],
                SubtitleProfiles = [new() { Format = "vtt", Method = "External" }],
                CodecProfiles = [new()
                {
                    Type = "Video",
                    Codec = "h264",
                    Conditions = [new() { Condition = "LessThanEqual", Property = "VideoBitDepth", Value = "8", IsRequired = true }]
                }]
            }
        }, TestContext.Current.CancellationToken);

        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal("play-session-a", result.PlaySessionId);
        var source = Assert.Single(result.MediaSources);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.Equal(9007199254740993L, source.RunTimeTicks);
        Assert.Equal(7, source.DefaultAudioStreamIndex);
        Assert.Equal("FutureDelivery", source.MediaStreams[1].DeliveryMethod);
        Assert.Equal("https://source.example/", source.RequiredHttpHeaders?["Referer"]);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/proxy/emby/Items/movie-a/PlaybackInfo", request.Uri.AbsolutePath);
        Assert.Equal("application/json", request.ContentType);
        using var document = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        var body = document.RootElement;
        Assert.Equal("user-a", body.GetProperty("UserId").GetString());
        Assert.Equal(9007199254740993L, body.GetProperty("StartTimeTicks").GetInt64());
        Assert.Equal(-1, body.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.False(body.GetProperty("EnableDirectPlay").GetBoolean());
        Assert.False(body.TryGetProperty("LiveStreamId", out _));
        var profile = body.GetProperty("DeviceProfile");
        Assert.Equal("h264", profile.GetProperty("DirectPlayProfiles")[0].GetProperty("VideoCodec").GetString());
        Assert.Equal("2", profile.GetProperty("TranscodingProfiles")[0].GetProperty("MaxAudioChannels").GetString());
        Assert.Equal("External", profile.GetProperty("SubtitleProfiles")[0].GetProperty("Method").GetString());
        Assert.Equal("8", profile.GetProperty("CodecProfiles")[0].GetProperty("Conditions")[0].GetProperty("Value").GetString());
    }

    [Fact]
    public async Task Negotiation_error_payload_is_retained_for_the_playback_coordinator()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"MediaSources\":[],\"ErrorCode\":\"NoCompatibleStream\",\"PlaySessionId\":\"failed-session\"}");

        var result = await context.Client.GetPlaybackInfoAsync("movie-a", new PlaybackInfoRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("NoCompatibleStream", result.ErrorCode);
        Assert.Empty(result.MediaSources);
        Assert.Equal("failed-session", result.PlaySessionId);
    }

    [Fact]
    public async Task Reports_and_cleanup_use_the_owned_playback_ids_and_distinct_stop_contract()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        await context.Client.ReportPlaybackStartAsync(new PlaybackStartInfo
        {
            ItemId = "movie-a", MediaSourceId = "source-a", PlaySessionId = "play/a+&",
            LiveStreamId = "live/a+&", PositionTicks = 600000000, PlayMethod = "DirectStream",
            AudioStreamIndex = 7, SubtitleStreamIndex = -1, CanSeek = true, IsPaused = false, VolumeLevel = 0
        }, TestContext.Current.CancellationToken);
        await context.Client.ReportPlaybackProgressAsync(new PlaybackProgressInfo
        {
            ItemId = "movie-a", MediaSourceId = "source-a", PlaySessionId = "play/a+&",
            LiveStreamId = "live/a+&", PositionTicks = 610000000, PlayMethod = "DirectStream",
            IsPaused = true, EventName = "Pause"
        }, TestContext.Current.CancellationToken);
        await context.Client.ReportPlaybackStoppedAsync(new PlaybackStopInfo
        {
            ItemId = "movie-a", MediaSourceId = "source-a", PlaySessionId = "play/a+&",
            LiveStreamId = "live/a+&", PositionTicks = 610000000, Failed = false
        }, TestContext.Current.CancellationToken);
        await context.Client.StopActiveEncodingsAsync("play/a+&", TestContext.Current.CancellationToken);
        await context.Client.CloseLiveStreamAsync("live/a+&", TestContext.Current.CancellationToken);

        var requests = context.Handler.Requests;
        Assert.Equal(5, requests.Length);
        Assert.Equal("/proxy/emby/Sessions/Playing", requests[0].Uri.AbsolutePath);
        Assert.Equal("/proxy/emby/Sessions/Playing/Progress", requests[1].Uri.AbsolutePath);
        Assert.Equal("/proxy/emby/Sessions/Playing/Stopped", requests[2].Uri.AbsolutePath);
        foreach (var request in requests.Take(3))
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            using var document = JsonDocument.Parse(Assert.IsType<string>(request.Body));
            Assert.Equal("movie-a", document.RootElement.GetProperty("ItemId").GetString());
            Assert.Equal("play/a+&", document.RootElement.GetProperty("PlaySessionId").GetString());
            Assert.Equal("live/a+&", document.RootElement.GetProperty("LiveStreamId").GetString());
        }
        using var started = JsonDocument.Parse(Assert.IsType<string>(requests[0].Body));
        Assert.Equal(0, started.RootElement.GetProperty("VolumeLevel").GetInt32());
        Assert.False(started.RootElement.GetProperty("IsPaused").GetBoolean());
        using var progress = JsonDocument.Parse(Assert.IsType<string>(requests[1].Body));
        Assert.Equal("Pause", progress.RootElement.GetProperty("EventName").GetString());
        Assert.True(progress.RootElement.GetProperty("IsPaused").GetBoolean());
        using var stopped = JsonDocument.Parse(Assert.IsType<string>(requests[2].Body));
        Assert.Equal(610000000, stopped.RootElement.GetProperty("PositionTicks").GetInt64());
        Assert.False(stopped.RootElement.GetProperty("Failed").GetBoolean());
        Assert.False(stopped.RootElement.TryGetProperty("IsPaused", out _));
        Assert.Equal(HttpMethod.Delete, requests[3].Method);
        Assert.Equal("/proxy/emby/Videos/ActiveEncodings", requests[3].Uri.AbsolutePath);
        Assert.Equal("device-a", requests[3].Query()["DeviceId"]);
        Assert.Equal("play/a+&", requests[3].Query()["PlaySessionId"]);
        Assert.Equal(2, requests[3].Query().Count);
        Assert.Null(requests[3].Body);
        Assert.Equal(HttpMethod.Post, requests[4].Method);
        Assert.Equal("/proxy/emby/LiveStreams/Close", requests[4].Uri.AbsolutePath);
        Assert.Equal("live/a+&", requests[4].Query()["LiveStreamId"]);
        Assert.All(requests, request => Assert.Equal("token-a", request.Header("X-Emby-Token")));
    }

    [Fact]
    public async Task Opening_a_source_sends_the_numeric_item_id_and_returns_the_updated_resource_handle()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"MediaSource\":{\"Id\":\"opened-source\",\"RequiresClosing\":true,\"LiveStreamId\":\"live-owned\",\"SupportsDirectStream\":true}}");

        var result = await context.Client.OpenLiveStreamAsync(new LiveStreamRequest
        {
            OpenToken = "opaque-open-token",
            ItemId = 9007199254740993,
            PlaySessionId = "play-owned",
            AudioStreamIndex = 7,
            EnableDirectPlay = false,
            EnableDirectStream = true
        }, TestContext.Current.CancellationToken);

        Assert.Equal("live-owned", result.MediaSource?.LiveStreamId);
        Assert.True(result.MediaSource?.RequiresClosing);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/proxy/emby/LiveStreams/Open", request.Uri.AbsolutePath);
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        Assert.Equal("user-a", body.RootElement.GetProperty("UserId").GetString());
        Assert.Equal(9007199254740993L, body.RootElement.GetProperty("ItemId").GetInt64());
        Assert.Equal("opaque-open-token", body.RootElement.GetProperty("OpenToken").GetString());
        Assert.Equal("play-owned", body.RootElement.GetProperty("PlaySessionId").GetString());
    }

    [Fact]
    public async Task Unsafe_playback_requests_fail_before_sending_network_calls()
    {
        using var context = new ApiTestContext();

        await Assert.ThrowsAsync<ArgumentException>(() => context.Client.GetPlaybackInfoAsync("movie-a",
            new PlaybackInfoRequest { UserId = "other-user" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Client.OpenLiveStreamAsync(
            new LiveStreamRequest { OpenToken = "open", PlaySessionId = "play", UserId = "other-user" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Client.StopActiveEncodingsAsync(" ", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => context.Client.CloseLiveStreamAsync(" ", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Client.ReportPlaybackProgressAsync(
            new PlaybackProgressInfo { ItemId = "movie-a", PlaySessionId = "play", PositionTicks = -1 }, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public void Original_stream_fallback_keeps_source_playback_and_device_scope_without_a_query_token()
    {
        using var context = new ApiTestContext();

        var uri = context.Client.BuildVideoStreamUri("movie/a", "source & a", "play/+a");
        var request = new RecordedRequest(HttpMethod.Get, uri, [], null, null);

        Assert.Equal("/proxy/emby/Videos/movie%2Fa/stream", uri.AbsolutePath);
        var query = request.Query();
        Assert.Equal("true", query["Static"]);
        Assert.Equal("source & a", query["MediaSourceId"]);
        Assert.Equal("play/+a", query["PlaySessionId"]);
        Assert.Equal("device-a", query["DeviceId"]);
        Assert.False(query.ContainsKey("api_key"));
        Assert.False(query.ContainsKey("StartTimeTicks"));
    }
}
