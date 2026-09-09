using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Xunit;

namespace EmbyClient.MediaTransport.Tests;

public sealed class MediaDeliveryClassifierTests
{
    [Fact]
    public void Selected_direct_mp4_keeps_the_full_source_without_using_its_hls_fallback()
    {
        var request = CreateSelectedDirectRequest();

        Assert.Equal(request.Source.DirectStreamUrl, request.MediaUri.AbsoluteUri);
        Assert.True(request.Source.SupportsTranscoding);
        Assert.Equal("hls", request.Source.TranscodingSubProtocol);
        Assert.NotNull(request.Source.TranscodingUrl);
        Assert.False(MediaDeliveryClassifier.IsHls(request));
    }

    [Fact]
    public void Selecting_the_same_sources_hls_conversion_uses_adaptive_delivery()
    {
        var direct = CreateSelectedDirectRequest();
        var conversion = direct with
        {
            DeliveryMethod = PlaybackDeliveryMethod.Transcode,
            MediaUri = new Uri(direct.Source.TranscodingUrl!)
        };

        Assert.Same(direct.Source, conversion.Source);
        Assert.True(MediaDeliveryClassifier.IsHls(conversion));
    }

    [Fact]
    public void An_explicit_playlist_remains_hls_when_selected_for_direct_delivery()
    {
        var direct = CreateSelectedDirectRequest();
        var playlist = direct with { MediaUri = new Uri("https://server.example/emby/Videos/movie-a/MASTER.M3U8?Static=true") };

        Assert.Equal(PlaybackDeliveryMethod.DirectStream, playlist.DeliveryMethod);
        Assert.Same(direct.Source, playlist.Source);
        Assert.True(MediaDeliveryClassifier.IsHls(playlist));
    }

    [Fact]
    public void Direct_mp4_retains_native_in_place_seek_despite_an_available_hls_conversion()
    {
        var direct = CreateSelectedDirectRequest();

        Assert.Equal("hls", direct.Source.TranscodingSubProtocol);
        Assert.True(MediaDeliveryClassifier.CanSeekInPlace(direct, nativeCanSeek: true));
    }

    [Fact]
    public void Selected_hls_requires_reopening_even_when_native_seek_is_advertised()
    {
        var direct = CreateSelectedDirectRequest();
        var conversion = direct with
        {
            DeliveryMethod = PlaybackDeliveryMethod.Transcode,
            MediaUri = new Uri(direct.Source.TranscodingUrl!)
        };

        Assert.False(MediaDeliveryClassifier.CanSeekInPlace(conversion, nativeCanSeek: true));
    }

    [Fact]
    public void Delivery_classification_does_not_enable_seek_when_native_seek_is_unavailable()
    {
        var direct = CreateSelectedDirectRequest();

        Assert.False(MediaDeliveryClassifier.CanSeekInPlace(direct, nativeCanSeek: false));
    }

    private static PlaybackEngineRequest CreateSelectedDirectRequest()
    {
        // PlaybackInfo retains both delivery alternatives on the selected source passed to the engine.
        var source = new MediaSourceInfo
        {
            Id = "source-a",
            Container = "mp4",
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            DirectStreamUrl = "https://server.example/emby/Videos/movie-a/stream.mp4?Static=true&MediaSourceId=source-a",
            TranscodingUrl = "https://server.example/emby/Videos/movie-a/master.m3u8?MediaSourceId=source-a",
            TranscodingSubProtocol = "hls",
            TranscodingContainer = "ts",
            RunTimeTicks = TimeSpan.FromMinutes(5).Ticks,
            MediaStreams =
            [
                new() { Index = 0, Type = "Video", Codec = "h264", VideoRange = "SDR" },
                new() { Index = 1, Type = "Audio", Codec = "aac" }
            ]
        };
        return new PlaybackEngineRequest
        {
            PlaybackId = Guid.NewGuid(),
            MediaUri = new Uri(source.DirectStreamUrl),
            Headers = new Dictionary<string, string>(),
            DeliveryMethod = PlaybackDeliveryMethod.DirectStream,
            Source = source
        };
    }
}
