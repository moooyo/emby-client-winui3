using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackExternalSubtitleTests
{
    [Fact]
    public void The_external_profile_promises_only_webvtt_and_preserves_complex_subtitle_burn_in()
    {
        var profile = ConservativeDeviceProfile.Create(enableExternalWebVtt: true);

        var external = Assert.Single(profile.SubtitleProfiles!, value => value.Method == "External");
        Assert.Equal("vtt", external.Format);
        foreach (var format in new[] { "srt", "vtt", "ass", "ssa", "sub", "pgssub", "dvdsub" })
            Assert.Contains(profile.SubtitleProfiles!, value => value.Format == format && value.Method == "Encode");
        Assert.DoesNotContain(profile.SubtitleProfiles!, value => value.Method is "Embed" or "Hls");
        Assert.All(ConservativeDeviceProfile.Create().SubtitleProfiles!, value => Assert.Equal("Encode", value.Method));
    }

    [Theory(Timeout = 15000)]
    [InlineData("srt")]
    [InlineData("subrip")]
    public async Task Source_srt_metadata_uses_the_negotiated_external_webvtt_url_on_the_full_hls_timeline(string codec)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        const string subtitleUrl = "/emby/Videos/parent-video/source-a/Subtitles/3/Stream.vtt?api_key=subtitle-query-token&DeviceId=subtitle-device";
        context.Sources = (_, _) => [WithSubtitle(codec, "External", subtitleUrl) with
        {
            SupportsDirectStream = false,
            RequiredHttpHeaders = new Dictionary<string, string>
            {
                ["Referer"] = "https://source.example/",
                ["X-Emby-Token"] = "untrusted-source-token",
                ["Host"] = "untrusted.example"
            }
        }];
        const long position = 600_000_001;

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(position) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.Equal(PlaybackTimelineKind.FullSource, request.TimelineKind);
        Assert.Equal(0, request.TimelineOffsetTicks);
        Assert.Equal(position, request.InitialPositionTicks);
        Assert.Equal(subtitleUrl, request.ExternalSubtitleUri!.PathAndQuery);
        Assert.Equal("https://source.example/", request.ExternalSubtitleHeaders["Referer"]);
        Assert.Equal("test-token", request.ExternalSubtitleHeaders["X-Emby-Token"]);
        Assert.Contains("X-Emby-Authorization", request.ExternalSubtitleHeaders.Keys);
        Assert.DoesNotContain("Host", request.ExternalSubtitleHeaders.Keys);
        Assert.Equal(3, request.SubtitleStreamIndex);
        var profile = Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo")).JsonBody.GetProperty("DeviceProfile");
        Assert.Equal("vtt", Assert.Single(profile.GetProperty("SubtitleProfiles").EnumerateArray(),
            value => value.GetProperty("Method").GetString() == "External").GetProperty("Format").GetString());
    }

    [Theory(Timeout = 15000)]
    [InlineData("https://server.example/emby/", "/emby/Videos/movie-a/source-a/Subtitles/3/Stream.vtt")]
    [InlineData("https://server.example/team/media/emby/", "/team/media/emby/Videos/movie-a/source-a/Subtitles/3/Stream.vtt")]
    public async Task Text_srt_without_a_delivery_url_uses_the_documented_webvtt_route_and_preserves_the_server_mount(
        string apiRoot, string expectedPath)
    {
        await using var context = new PlaybackTestContext(apiRoot: new Uri(apiRoot), enableExternalWebVtt: true);
        context.Sources = (_, _) => [WithSubtitle("srt", "External", null)];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, request.DeliveryMethod);
        Assert.Equal(expectedPath, request.ExternalSubtitleUri!.AbsolutePath);
        Assert.Empty(request.ExternalSubtitleUri.Query);
        Assert.Equal("test-token", request.ExternalSubtitleHeaders["X-Emby-Token"]);
    }

    [Theory(Timeout = 15000)]
    [InlineData("https://cdn.example/captions/movie.vtt?signature=opaque")]
    [InlineData("https://server.example:8443/captions/movie.vtt")]
    [InlineData("http://server.example/captions/movie.vtt")]
    public async Task An_external_subtitle_origin_does_not_receive_emby_authentication_or_the_media_sources_headers(string subtitleUrl)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) => [WithSubtitle("srt", "External", subtitleUrl) with
        {
            RequiredHttpHeaders = new Dictionary<string, string>
            {
                ["Referer"] = "https://source.example/private",
                ["Authorization"] = "Bearer source-only-token"
            }
        }];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(new Uri(subtitleUrl), request.ExternalSubtitleUri);
        Assert.Empty(request.ExternalSubtitleHeaders);
        Assert.Equal("test-token", request.Headers["X-Emby-Token"]);
        Assert.Equal("Bearer source-only-token", request.Headers["Authorization"]);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ass", true)]
    [InlineData("ssa", true)]
    [InlineData("pgssub", false)]
    [InlineData("dvdsub", false)]
    public async Task Complex_subtitles_keep_server_burn_in_with_external_webvtt_enabled(string codec, bool isText)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) => [WithSubtitle(codec, "Encode", null, isText)];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.Equal(3, request.SubtitleStreamIndex);
        Assert.Null(request.ExternalSubtitleUri);
        Assert.Empty(request.ExternalSubtitleHeaders);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Fact(Timeout = 15000)]
    public async Task Unsupported_external_subtitles_retry_once_with_only_burn_in_capabilities()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) => [WithSubtitle("srt", HasExternalProfile(request) ? "External" : "Encode", null)];
        context.Engine.OnOpenAsync = (request, _) => request.ExternalSubtitleUri is not null
            ? Task.FromException(new PlaybackException("UnsupportedSubtitle")) : Task.CompletedTask;
        const long position = 700_000_003;

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(position) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        var original = context.Engine.Opened[0];
        var fallback = context.Engine.Opened[1];
        Assert.NotNull(original.ExternalSubtitleUri);
        Assert.Null(fallback.ExternalSubtitleUri);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, fallback.DeliveryMethod);
        Assert.Equal(3, fallback.SubtitleStreamIndex);
        Assert.Equal(position, fallback.InitialPositionTicks);
        Assert.NotEqual(original.PlaybackId, fallback.PlaybackId);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.True(HasExternalProfile(negotiations[0]));
        Assert.False(HasExternalProfile(negotiations[1]));
        Assert.Equal("session-2", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
    }

    [Fact(Timeout = 15000)]
    public async Task Trimmed_progressive_delivery_rejects_external_subtitles_before_opening_and_negotiates_burn_in()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) => [WithSubtitle("srt", HasExternalProfile(request) ? "External" : "Encode", null) with
        {
            SupportsDirectStream = false,
            TranscodingUrl = "/emby/Videos/movie-a/stream.mp4?MediaSourceId=source-a",
            TranscodingSubProtocol = "http",
            TranscodingContainer = "mp4"
        }];
        const long position = 800_000_007;

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(position) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackTimelineKind.ProgressiveSegment, request.TimelineKind);
        Assert.Equal(position, request.TimelineOffsetTicks);
        Assert.Equal(0, request.InitialPositionTicks);
        Assert.Null(request.ExternalSubtitleUri);
        Assert.Equal("Encode", request.Source.MediaStreams.Single(value => value.Index == 3).DeliveryMethod);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.True(HasExternalProfile(negotiations[0]));
        Assert.False(HasExternalProfile(negotiations[1]));
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_an_external_subtitle_open_does_not_retry_with_a_new_burn_in_session()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var opening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Sources = (_, _) => [WithSubtitle("srt", "External", null)];
        context.Engine.OnOpenAsync = async (_, token) =>
        {
            opening.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var play = context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            SubtitleStreamIndex = 3
        }, cancellation.Token);
        await opening.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);

        var request = Assert.Single(context.Engine.Opened);
        Assert.NotNull(request.ExternalSubtitleUri);
        Assert.Equal(request.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Null(context.Coordinator.ActiveContext);
    }

    private static MediaSourceInfo WithSubtitle(string codec, string deliveryMethod, string? deliveryUrl, bool isText = true)
    {
        var source = PlaybackTestContext.Source();
        return source with
        {
            MediaStreams = source.MediaStreams.Select(stream => stream.Index == 3 ? stream with
            {
                Codec = codec,
                DeliveryMethod = deliveryMethod,
                DeliveryUrl = deliveryUrl,
                IsTextSubtitleStream = isText
            } : stream).ToArray()
        };
    }

    private static bool HasExternalProfile(RecordedRequest request) => request.JsonBody.GetProperty("DeviceProfile")
        .GetProperty("SubtitleProfiles").EnumerateArray().Any(value => value.GetProperty("Method").GetString() == "External");
}
