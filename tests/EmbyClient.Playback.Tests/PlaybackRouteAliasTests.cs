using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackRouteAliasTests
{
    [Theory(Timeout = 15000)]
    [InlineData("https://server.example/emby/", "/videos/5/master.m3u8")]
    [InlineData("https://server.example/emby/", "/videos/5/main.m3u8")]
    [InlineData("https://server.example/emby/", "/emby/videos/5/master.m3u8")]
    [InlineData("https://server.example/team/media/emby/", "/team/media/videos/5/master.m3u8")]
    [InlineData("https://server.example/team/media/emby/", "/team/media/emby/videos/5/main.m3u8")]
    public async Task Canonical_and_optional_emby_prefix_routes_share_the_same_configured_server_mount(string apiRoot, string mediaPath)
    {
        await using var context = new PlaybackTestContext(apiRoot: new Uri(apiRoot));
        var returnedUrl = mediaPath + "?MediaSourceId=source-5&AudioStreamIndex=1";
        context.Sources = (_, _) => [ObservedSource(returnedUrl)];

        await context.Coordinator.PlayAsync(new PlaybackSelection
        {
            ItemId = "5", SubtitleStreamIndex = -1, StartPositionTicks = 170_000_000,
            ForceTranscoding = true, MaxStreamingBitrate = 2_000_000
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(returnedUrl, request.MediaUri.PathAndQuery);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.Equal(PlaybackTimelineKind.FullSource, request.TimelineKind);
        Assert.Equal(0, request.TimelineOffsetTicks);
        Assert.Equal(170_000_000, request.InitialPositionTicks);
        Assert.Equal(600_106_460, request.ItemRunTimeTicks);
        Assert.DoesNotContain("StartTimeTicks", request.MediaUri.Query, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("test-token", request.Headers["X-Emby-Token"]);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        var start = Assert.Single(context.Handler.Requests,
            call => call.Uri.AbsolutePath.EndsWith("/Sessions/Playing", StringComparison.Ordinal)).JsonBody;
        Assert.Equal("5", start.GetProperty("ItemId").GetString());
        Assert.Equal("source-5", start.GetProperty("MediaSourceId").GetString());
        Assert.Equal(170_000_000, start.GetProperty("PositionTicks").GetInt64());
    }

    [Theory(Timeout = 15000)]
    [InlineData("https://server.example/team/media/emby/", "/videos/5/master.m3u8")]
    [InlineData("https://server.example/team/media/emby/", "https://server.example/emby/videos/5/master.m3u8")]
    [InlineData("https://server.example/team/media/emby/", "/other/videos/5/master.m3u8")]
    [InlineData("https://server.example/team/media/emby/", "/team/mediator/videos/5/master.m3u8")]
    [InlineData("https://server.example/Team/Media/emby/", "/team/media/videos/5/master.m3u8")]
    [InlineData("https://server.example/emby/", "https://cdn.example/videos/5/master.m3u8")]
    [InlineData("https://server.example/emby/", "/videos/5/master.m3u8/extra")]
    [InlineData("https://server.example/emby/", "/videos/a%2Fb/master.m3u8")]
    [InlineData("https://server.example/emby/", "/videos/5/not-a-playlist.m3u8")]
    public async Task Optional_emby_alias_does_not_escape_the_proxy_mount_or_accept_noncanonical_media_routes(string apiRoot, string returnedUrl)
    {
        await using var context = new PlaybackTestContext(apiRoot: new Uri(apiRoot));
        context.Sources = (_, _) => [ObservedSource(returnedUrl)];

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(new PlaybackSelection
        {
            ItemId = "5", ForceTranscoding = true
        }, TestContext.Current.CancellationToken));

        Assert.Equal("UnknownTranscodeTimeline", error.ErrorCode);
        Assert.Empty(context.Engine.Opened);
        Assert.DoesNotContain(context.Handler.Requests,
            call => call.Uri.AbsolutePath.EndsWith("/Sessions/Playing", StringComparison.Ordinal));
    }

    private static MediaSourceInfo ObservedSource(string returnedUrl) => PlaybackTestContext.Source("source-5") with
    {
        Protocol = "file",
        Container = "mp4",
        RunTimeTicks = 600_106_460,
        IsInfiniteStream = false,
        SupportsTranscoding = true,
        TranscodingSubProtocol = "hls",
        TranscodingUrl = returnedUrl
    };
}
