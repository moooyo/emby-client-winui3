using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class NativeOutputNegotiationTests
{
    private const int SubtitleIndex = 3;
    private const long PositionTicks = 600_000_001;

    [Fact]
    public void Native_output_flags_default_to_false_and_do_not_replace_the_conservative_baseline()
    {
        var selection = PlaybackTestContext.Selection();
        var change = new PlaybackSelectionChange();
        Assert.False(selection.NativeAssSubtitlesEligible);
        Assert.False(selection.NativeAudioPassthroughEligible);
        Assert.Equal(AudioPassthroughMode.Off, selection.AudioPassthroughMode);
        Assert.Null(change.NativeAssSubtitlesEligible);
        Assert.Null(change.NativeAudioPassthroughEligible);
        Assert.Null(change.AudioPassthroughMode);
        var profile = DeviceProfileFactory.Create(selection);
        Assert.True(PreservedAudioCodecs(profile).SetEquals(["aac"]));
        Assert.All(profile.SubtitleProfiles!, value => Assert.Equal("Encode", value.Method));
        Assert.Single(profile.DirectPlayProfiles!);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void Missing_ass_renderer_or_requested_conversion_keeps_ass_and_ssa_on_encode(
        bool rendererAvailable, bool forceTranscoding, bool forceSubtitleBurnIn)
    {
        var profile = DeviceProfileFactory.Create(PlaybackTestContext.Selection(transcode: forceTranscoding) with
        {
            NativeAssSubtitlesEligible = rendererAvailable,
            ForceSubtitleBurnIn = forceSubtitleBurnIn
        });
        foreach (var format in new[] { "ass", "ssa" })
            Assert.Equal(new[] { "Encode" }, profile.SubtitleProfiles!.Where(value => value.Format == format)
                .Select(value => value.Method));
        Assert.True(PreservedAudioCodecs(profile).SetEquals(["aac"]));
        Assert.DoesNotContain(profile.DirectPlayProfiles!, value => value.VideoCodec == "hevc");
    }

    [Fact]
    public void Full_ass_renderer_promises_original_delivery_and_keeps_explicit_burn_in_fallbacks()
    {
        var profile = DeviceProfileFactory.Create(PlaybackTestContext.Selection() with
        {
            NativeAssSubtitlesEligible = true
        }, enableExternalWebVtt: true);
        foreach (var format in new[] { "ass", "ssa" })
            Assert.Equal(new[] { "External", "Embed", "Encode" }, profile.SubtitleProfiles!
                .Where(value => value.Format == format).Select(value => value.Method));
        Assert.DoesNotContain(profile.SubtitleProfiles!, value => value.Method == "Embed"
            && value.Format is "pgssub" or "dvdsub");
        Assert.Contains(profile.SubtitleProfiles!, value => value.Format == "vtt" && value.Method == "External");
        Assert.True(PreservedAudioCodecs(profile).SetEquals(["aac"]));
        Assert.Contains(profile.DirectPlayProfiles!, value => value.VideoCodec == "h264"
            && Assert.IsType<string>(value.Container).Split(',').Contains("mkv", StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("ass", "Subtitle", false, true, true)]
    [InlineData("SSA", "subtitle", false, null, true)]
    [InlineData("ass", "Subtitle", true, true, false)]
    [InlineData("ass", "Subtitle", null, true, false)]
    [InlineData("ass", "Subtitle", false, false, false)]
    [InlineData("ass", "Video", false, true, false)]
    [InlineData("srt", "Subtitle", false, true, false)]
    [InlineData("FutureCodec", "Subtitle", false, true, false)]
    public void Embedded_ass_requires_a_known_text_codec_and_a_confirmed_original_subtitle_stream(
        string codec, string type, bool? external, bool? text, bool expected)
    {
        var stream = Subtitle(codec, "Embed") with { Type = type, IsExternal = external, IsTextSubtitleStream = text };
        Assert.Equal(expected, NativeSubtitlePlaybackPolicy.IsSupportedEmbeddedAss(stream));
        Assert.Equal(expected, NativeSubtitlePlaybackPolicy.IsSupportedEmbeddedSubtitle(stream, true));
        Assert.False(NativeSubtitlePlaybackPolicy.IsSupportedEmbeddedSubtitle(stream, false));
    }

    [Theory(Timeout = 15000)]
    [InlineData("ass")]
    [InlineData("ssa")]
    public async Task Original_ass_and_ssa_embedding_reaches_the_engine_only_with_the_full_renderer_flag(string codec)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource(Subtitle(codec, "Embed"))];
        await context.Coordinator.PlayAsync(SubtitleSelection(), TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, request.DeliveryMethod);
        Assert.True(request.NativeAssSubtitlesEligible);
        Assert.False(request.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(SubtitleIndex, request.SubtitleStreamIndex);
        Assert.Null(request.ExternalSubtitleUri);
        Assert.Null(request.ExternalSubtitleFormat);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Theory(Timeout = 15000)]
    [InlineData("NoRenderer")]
    [InlineData("UnknownOriginal")]
    [InlineData("ExternalInput")]
    [InlineData("BitmapInput")]
    [InlineData("Remux")]
    public async Task Unverified_ass_embedding_requests_one_fresh_encode_source_before_engine_open(string failure)
    {
        await using var context = new PlaybackTestContext();
        var track = Subtitle("ass", "Embed");
        var original = OriginalSource(track);
        original = failure switch
        {
            "UnknownOriginal" => OriginalSource(track with { IsExternal = null }),
            "ExternalInput" => OriginalSource(track with { IsExternal = true }),
            "BitmapInput" => OriginalSource(track with { IsTextSubtitleStream = false }),
            "Remux" => original with { DirectStreamUrl = "/emby/Videos/movie-a/stream.ts?Static=false" },
            _ => original
        };
        context.Sources = (_, count) =>
        {
            if (count == 1) return [original];
            Assert.Equal(2, count);
            Assert.Empty(context.Engine.Opened);
            return [BurnInSource("ass")];
        };
        await context.Coordinator.PlayAsync(SubtitleSelection() with
        {
            NativeAssSubtitlesEligible = failure != "NoRenderer",
            NativeEmbeddedSubtitlesEligible = true
        }, TestContext.Current.CancellationToken);

        AssertBurnIn(context);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ass", "/emby/Videos/movie-a/source-a/Subtitles/3/Stream.ass?signature=opaque", true, "ass")]
    [InlineData("ssa", "/emby/Videos/movie-a/source-a/Subtitles/3/Stream.SSA", true, "ssa")]
    [InlineData("ass", "/emby/Videos/movie-a/source-a/Subtitles/3/Stream.vtt", false, "vtt")]
    [InlineData("srt", "/emby/Videos/movie-a/source-a/Subtitles/3/Stream.ass", true, "ass")]
    public async Task External_format_follows_the_actual_delivery_url_instead_of_the_original_codec_label(
        string codec, string url, bool rendererAvailable, string format)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) => [OriginalSource(Subtitle(codec, "External") with { DeliveryUrl = url })];
        await context.Coordinator.PlayAsync(SubtitleSelection() with { NativeAssSubtitlesEligible = rendererAvailable },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(context.Api.ResolveMediaUri(url), request.ExternalSubtitleUri);
        Assert.Equal(format, request.ExternalSubtitleFormat);
        Assert.Equal(rendererAvailable, request.NativeAssSubtitlesEligible);
        Assert.False(request.NativeEmbeddedSubtitlesEligible);
        Assert.Equal("test-token", request.ExternalSubtitleHeaders["X-Emby-Token"]);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ass", true, "ass")]
    [InlineData("ssa", true, "ssa")]
    [InlineData("ass", false, "vtt")]
    [InlineData("ssa", false, "vtt")]
    public async Task Missing_external_url_requests_a_documented_format_supported_by_the_current_renderer(
        string codec, bool rendererAvailable, string format)
    {
        await using var context = new PlaybackTestContext(apiRoot: new Uri("https://server.example/team/media/emby/"),
            enableExternalWebVtt: true);
        context.Sources = (_, _) => [OriginalSource(Subtitle(codec, "External"))];
        await context.Coordinator.PlayAsync(SubtitleSelection() with { NativeAssSubtitlesEligible = rendererAvailable },
            TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(format, request.ExternalSubtitleFormat);
        Assert.Equal($"/team/media/emby/Videos/movie-a/source-a/Subtitles/3/Stream.{format}", request.ExternalSubtitleUri!.AbsolutePath);
        Assert.Equal(0, request.TimelineOffsetTicks);
        Assert.Equal(PositionTicks, request.InitialPositionTicks);
    }

    [Theory(Timeout = 15000)]
    [InlineData("ass")]
    [InlineData("ssa")]
    public async Task Explicit_ass_external_bytes_without_a_full_renderer_use_server_burn_in(string format)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, count) => [count == 1
            ? OriginalSource(Subtitle(format, "External") with { DeliveryUrl = $"/emby/Videos/movie-a/source-a/Subtitles/3/Stream.{format}" })
            : BurnInSource(format)];
        await context.Coordinator.PlayAsync(SubtitleSelection() with { NativeAssSubtitlesEligible = false },
            TestContext.Current.CancellationToken);

        AssertBurnIn(context);
    }

    [Theory]
    [InlineData(false, AudioPassthroughMode.All)]
    [InlineData(false, AudioPassthroughMode.DolbyDts)]
    [InlineData(true, AudioPassthroughMode.Off)]
    public void A_saved_bitstream_choice_or_interface_availability_alone_does_not_expand_audio_formats(
        bool interfaceAvailable, AudioPassthroughMode mode)
    {
        var profile = DeviceProfileFactory.Create(AudioSelection(mode) with { NativeAudioPassthroughEligible = interfaceAvailable });
        Assert.True(PreservedAudioCodecs(profile).SetEquals(["aac"]));
        Assert.Single(profile.DirectPlayProfiles!);
    }

    [Theory]
    [InlineData(AudioPassthroughMode.DolbyDts)]
    [InlineData(AudioPassthroughMode.All)]
    public void Available_bitstream_interface_expands_only_the_requested_source_codec_families(AudioPassthroughMode mode)
    {
        var profile = DeviceProfileFactory.Create(AudioSelection(mode));
        var expected = mode == AudioPassthroughMode.All
            ? new[] { "aac", "ac3", "eac3", "dts", "dtshd", "truehd" }
            : new[] { "aac", "ac3", "eac3", "dts" };
        Assert.True(PreservedAudioCodecs(profile).SetEquals(expected));
        Assert.All(profile.DirectPlayProfiles!, value => Assert.Equal("h264", value.VideoCodec));
        var avc = Assert.Single(profile.CodecProfiles!, value => value.Codec == "h264");
        Assert.Contains(avc.Conditions!, value => value.Property == "VideoBitDepth" && value.Value == "8");
        Assert.Equal("aac", Assert.Single(profile.TranscodingProfiles!).AudioCodec);
    }

    [Theory(Timeout = 15000)]
    [InlineData(AudioPassthroughMode.Off, "aac")]
    [InlineData(AudioPassthroughMode.DolbyDts, "ac3")]
    [InlineData(AudioPassthroughMode.DolbyDts, "eac3")]
    [InlineData(AudioPassthroughMode.DolbyDts, "dts")]
    [InlineData(AudioPassthroughMode.All, "dtshd")]
    [InlineData(AudioPassthroughMode.All, "truehd")]
    public async Task Original_direct_sources_pass_the_requested_audio_policy_without_asserting_endpoint_acceptance(
        AudioPassthroughMode mode, string codec)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource(audioCodec: codec)];
        await context.Coordinator.PlayAsync(AudioSelection(mode), TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, request.DeliveryMethod);
        Assert.True(request.NativeAudioPassthroughEligible);
        Assert.Equal(mode, request.AudioPassthroughMode);
        Assert.Equal(1, request.AudioStreamIndex);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Fact(Timeout = 15000)]
    public async Task Remux_direct_streams_keep_the_requested_audio_mode_without_claiming_original_stream_mapping()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource(audioCodec: "truehd") with
        {
            DirectStreamUrl = "/emby/Videos/movie-a/stream.ts?Static=true&MediaSourceId=source-a"
        }];
        await context.Coordinator.PlayAsync(AudioSelection(AudioPassthroughMode.All), TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, request.DeliveryMethod);
        Assert.False(request.NativeAudioPassthroughEligible);
        Assert.Equal(AudioPassthroughMode.All, request.AudioPassthroughMode);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(AudioPassthroughMode.DolbyDts, true, false)]
    [InlineData(AudioPassthroughMode.All, true, false)]
    [InlineData(AudioPassthroughMode.All, false, true)]
    public async Task Every_transcoded_or_burned_delivery_disables_native_audio_bitstream_requests(
        AudioPassthroughMode mode, bool forceTranscoding, bool forceSubtitleBurnIn)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [forceSubtitleBurnIn ? BurnInSource("ass") : OriginalSource()];
        await context.Coordinator.PlayAsync(AudioSelection(mode) with
        {
            NativeAssSubtitlesEligible = true,
            ForceTranscoding = forceTranscoding,
            ForceSubtitleBurnIn = forceSubtitleBurnIn,
            SubtitleStreamIndex = forceSubtitleBurnIn ? SubtitleIndex : -1
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.False(request.NativeAudioPassthroughEligible);
        Assert.Equal(AudioPassthroughMode.Off, request.AudioPassthroughMode);
        if (forceSubtitleBurnIn)
        {
            Assert.False(request.NativeAssSubtitlesEligible);
            Assert.Null(request.ExternalSubtitleFormat);
            Assert.Null(request.ExternalSubtitleUri);
        }
        if (forceTranscoding)
            Assert.True(PreservedAudioCodecs(DeviceProfileFactory.Create(context.Coordinator.ActiveContext!.Selection)).SetEquals(["aac"]));
    }

    [Fact(Timeout = 15000)]
    public async Task Capability_changes_and_replay_refresh_full_renderer_and_audio_policy_without_losing_selection()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource(Subtitle("ass", "External") with
        {
            DeliveryUrl = "/emby/Videos/movie-a/source-a/Subtitles/3/Stream.vtt"
        }, audioCodec: "aac")];
        await context.Coordinator.PlayAsync(SubtitleSelection() with { NativeAssSubtitlesEligible = false },
            TestContext.Current.CancellationToken);
        context.Engine.SetPosition(PositionTicks);
        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            NativeAssSubtitlesEligible = true,
            NativeAudioPassthroughEligible = true,
            AudioPassthroughMode = AudioPassthroughMode.All
        }, TestContext.Current.CancellationToken);
        var changed = context.Engine.Opened[1];
        Assert.True(changed.NativeAssSubtitlesEligible);
        Assert.True(changed.NativeAudioPassthroughEligible);
        Assert.Equal(AudioPassthroughMode.All, changed.AudioPassthroughMode);
        Assert.Equal(PositionTicks, context.Coordinator.ActiveContext!.PositionTicks);

        await context.Coordinator.ReplayAsync(new PlaybackSelectionChange
        {
            NativeAssSubtitlesEligible = false,
            NativeAudioPassthroughEligible = false,
            AudioPassthroughMode = AudioPassthroughMode.Off
        }, TestContext.Current.CancellationToken);
        var replayed = context.Engine.Opened[2];
        Assert.False(replayed.NativeAssSubtitlesEligible);
        Assert.False(replayed.NativeAudioPassthroughEligible);
        Assert.Equal(AudioPassthroughMode.Off, replayed.AudioPassthroughMode);
        Assert.Equal(SubtitleIndex, replayed.SubtitleStreamIndex);
        Assert.Equal("source-a", replayed.Source.Id);
        Assert.Equal(0, replayed.InitialPositionTicks);
        Assert.Equal("vtt", replayed.ExternalSubtitleFormat);
        Assert.Equal(3, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
    }

    private static PlaybackSelection SubtitleSelection() => PlaybackTestContext.Selection(PositionTicks) with
    {
        SubtitleStreamIndex = SubtitleIndex,
        NativeAssSubtitlesEligible = true
    };

    private static PlaybackSelection AudioSelection(AudioPassthroughMode mode) => PlaybackTestContext.Selection(PositionTicks) with
    {
        NativeAudioPassthroughEligible = true,
        AudioPassthroughMode = mode
    };

    private static MediaStream Subtitle(string codec, string method) => new()
    {
        Index = SubtitleIndex,
        Type = "Subtitle",
        Codec = codec,
        DeliveryMethod = method,
        IsExternal = false,
        IsTextSubtitleStream = true
    };

    private static MediaSourceInfo OriginalSource(MediaStream? subtitle = null, string audioCodec = "aac") =>
        PlaybackTestContext.Source() with
        {
            Container = "mkv",
            DirectStreamUrl = "/emby/Videos/movie-a/stream.mkv?Static=true&MediaSourceId=source-a",
            DefaultSubtitleStreamIndex = subtitle?.Index ?? -1,
            MediaStreams = subtitle is null ? [Video(), Audio(audioCodec)] : [Video(), Audio(audioCodec), subtitle]
        };

    private static MediaStream Video() => new()
    {
        Index = 0, Type = "Video", Codec = "h264", BitDepth = 8, Profile = "High", IsInterlaced = false, VideoRange = "SDR"
    };

    private static MediaStream Audio(string codec) => new() { Index = 1, Type = "Audio", Codec = codec, Channels = codec == "aac" ? 2 : 8 };

    private static MediaSourceInfo BurnInSource(string codec) => OriginalSource(Subtitle(codec, "Encode")) with
    {
        SupportsDirectStream = false,
        DirectStreamUrl = null,
        TranscodingUrl = "/emby/Videos/movie-a/master.m3u8?MediaSourceId=source-a&SubtitleMethod=Encode"
    };

    private static HashSet<string> PreservedAudioCodecs(DeviceProfile profile) => profile.DirectPlayProfiles!
        .SelectMany(value => Assert.IsType<string>(value.AudioCodec).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        .ToHashSet(StringComparer.Ordinal);

    private static void AssertBurnIn(PlaybackTestContext context)
    {
        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.False(request.NativeAssSubtitlesEligible);
        Assert.False(request.NativeEmbeddedSubtitlesEligible);
        Assert.Null(request.ExternalSubtitleUri);
        Assert.Null(request.ExternalSubtitleFormat);
        Assert.Equal(PositionTicks, context.Coordinator.ActiveContext!.PositionTicks);
        Assert.True(context.Coordinator.ActiveContext.Selection.ForceSubtitleBurnIn);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.False(negotiations[1].JsonBody.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.All(negotiations[1].JsonBody.GetProperty("DeviceProfile").GetProperty("SubtitleProfiles").EnumerateArray(),
            value => Assert.Equal("Encode", value.GetProperty("Method").GetString()));
    }
}
