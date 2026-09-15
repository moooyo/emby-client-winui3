using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class MediaInformationContractTests
{
    [Fact]
    public async Task Media_information_fields_deserialize_without_reflection_and_keep_server_precision()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {"MediaSources":[{"Id":"source-a","Bitrate":24000000,"MediaStreams":[
              {"Index":0,"Type":"Video","Codec":"hevc","BitRate":20000000,"AverageFrameRate":23.976,
               "RealFrameRate":24,"AspectRatio":"16:9","PixelFormat":"yuv420p10le","ColorSpace":"bt2020nc",
               "ColorTransfer":"smpte2084","ColorPrimaries":"bt2020","CodecTag":"hvc1","RefFrames":4,
               "ExtendedVideoSubTypeDescription":"HDR10"},
              {"Index":7,"Type":"Audio","DisplayLanguage":"English","SampleRate":48000,"BitRate":640000}
            ]}]}
            """);

        var response = await context.Client.GetPlaybackInfoAsync("movie-a", new(), TestContext.Current.CancellationToken);

        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var source = Assert.Single(response.MediaSources);
        Assert.Equal(24000000, source.Bitrate);
        Assert.Equal(20000000, source.MediaStreams[0].BitRate);
        Assert.Equal(23.976f, source.MediaStreams[0].AverageFrameRate);
        Assert.Equal("16:9", source.MediaStreams[0].AspectRatio);
        Assert.Equal("bt2020nc", source.MediaStreams[0].ColorSpace);
        Assert.Equal("smpte2084", source.MediaStreams[0].ColorTransfer);
        Assert.Equal("HDR10", source.MediaStreams[0].ExtendedVideoSubTypeDescription);
        Assert.Equal(48000, source.MediaStreams[1].SampleRate);
        Assert.Equal("English", source.MediaStreams[1].DisplayLanguage);
    }
}
