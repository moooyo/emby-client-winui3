using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class NativeSubtitlePlaybackPolicyTests
{
    private const int SubtitleIndex = 3;
    private const long ResumePositionTicks = 600_000_001;

    [Fact]
    public void Native_subtitles_default_to_unavailable_and_keep_the_avc_aac_burn_in_baseline()
    {
        var selection = PlaybackTestContext.Selection();
        Assert.False(selection.NativeEmbeddedSubtitlesEligible);
        Assert.Null(new PlaybackSelectionChange().NativeEmbeddedSubtitlesEligible);
        var profile = DeviceProfileFactory.Create(selection);

        var direct = Assert.Single(profile.DirectPlayProfiles!);
        Assert.Equal("mp4", direct.Container);
        Assert.Equal("h264", direct.VideoCodec);
        Assert.Equal("aac", direct.AudioCodec);
        Assert.All(profile.SubtitleProfiles!, value => Assert.Equal("Encode", value.Method));
        Assert.Contains(profile.SubtitleProfiles!, value => value.Format == "ass");
        AssertAvcAacConditions(profile);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Eligible_native_subtitles_add_bitmap_embedding_without_widening_avc_or_granting_hdr(
        bool hdr10OutputEligible, bool enableExternalWebVtt)
    {
        var profile = DeviceProfileFactory.Create(PlaybackTestContext.Selection() with
        {
            NativeEmbeddedSubtitlesEligible = true,
            Hdr10OutputEligible = hdr10OutputEligible,
            MaxStreamingBitrate = 8_000_000
        }, enableExternalWebVtt);

        var avc = profile.DirectPlayProfiles!.Where(value => value.VideoCodec == "h264").ToArray();
        Assert.Equal(2, avc.Length);
        Assert.Single(avc, value => value.Container == "mp4");
        var originalContainers = avc.SelectMany(value => Assert.IsType<string>(value.Container)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
        Assert.True(originalContainers.SetEquals(["mp4", "mkv"]));
        Assert.All(avc, value => Assert.Equal("aac", value.AudioCodec));
        Assert.Equal(hdr10OutputEligible, profile.DirectPlayProfiles.Any(value => value.VideoCodec == "hevc"));
        Assert.Equal(8_000_000L, profile.MaxStreamingBitrate);
        Assert.Equal(8_000_000L, profile.MaxStaticBitrate);
        AssertAvcAacConditions(profile);

        var subtitles = profile.SubtitleProfiles!;
        Assert.Equal(2, subtitles.Count(value => value.Method == "Embed"));
        foreach (var format in new[] { "pgssub", "dvdsub" })
            Assert.Equal(new[] { "Embed", "Encode" }, subtitles.Where(value => value.Format == format).Select(value => value.Method));
        Assert.Equal(new[] { "Encode" }, subtitles.Where(value => value.Format == "ass").Select(value => value.Method));
        Assert.Equal(enableExternalWebVtt, subtitles.Any(value => value.Format == "vtt" && value.Method == "External"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Forced_conversion_or_subtitle_burn_in_removes_native_embedding(bool forceTranscoding, bool forceSubtitleBurnIn)
    {
        var profile = DeviceProfileFactory.Create(PlaybackTestContext.Selection(transcode: forceTranscoding) with
        {
            NativeEmbeddedSubtitlesEligible = true,
            ForceSubtitleBurnIn = forceSubtitleBurnIn
        });

        Assert.DoesNotContain(profile.SubtitleProfiles!, value => value.Method == "Embed");
        Assert.All(profile.SubtitleProfiles!, value => Assert.Equal("Encode", value.Method));
        var direct = Assert.Single(profile.DirectPlayProfiles!);
        Assert.Equal("mp4", direct.Container);
        Assert.Equal("h264", direct.VideoCodec);
        Assert.Equal("aac", direct.AudioCodec);
        AssertAvcAacConditions(profile);
    }

    [Theory(Timeout = 15000)]
    [InlineData("pgssub", "/emby/Videos/movie-a/stream.mkv?Static=true&MediaSourceId=source-a")]
    [InlineData("hdmv_pgs_subtitle", "/emby/Videos/movie-a/stream?Static=1&MediaSourceId=source-a")]
    [InlineData("dvdsub", "/emby/Videos/movie-a/stream.mkv?Static=true&MediaSourceId=source-a")]
    [InlineData("dvd_subtitle", "/Videos/movie-a/stream.mkv?Static=true&MediaSourceId=source-a")]
    [InlineData("vobsub", "/emby/Videos/movie-a/stream.mkv?Static=true&MediaSourceId=source-a")]
    public async Task Known_embedded_bitmap_tracks_in_original_static_sources_reach_the_native_engine_request(
        string codec, string directUrl)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource(codec) with { DirectStreamUrl = directUrl }];

        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);

        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, opened.DeliveryMethod);
        Assert.True(opened.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(context.Api.ResolveMediaUri(directUrl), opened.MediaUri);
        Assert.Equal(SubtitleIndex, opened.SubtitleStreamIndex);
        Assert.Equal(ResumePositionTicks, opened.InitialPositionTicks);
        Assert.Equal(codec, Assert.Single(opened.Source.MediaStreams, value => value.Type == "Subtitle").Codec);
        Assert.Null(opened.ExternalSubtitleUri);
        Assert.True(HasNativeSubtitleProfile(Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"))));
        Assert.False(context.Coordinator.ActiveContext!.Selection.ForceSubtitleBurnIn);
    }

    [Theory(Timeout = 15000)]
    [InlineData("UnavailableCapability")]
    [InlineData("ExternalTrack")]
    [InlineData("UnknownEmbedding")]
    [InlineData("TextTrack")]
    [InlineData("AssTrack")]
    [InlineData("UnknownCodec")]
    [InlineData("MissingCodec")]
    [InlineData("RemuxContainer")]
    [InlineData("HlsDirectRoute")]
    [InlineData("TranscodedSource")]
    [InlineData("CrossOrigin")]
    [InlineData("MissingStatic")]
    [InlineData("StaticFalse")]
    [InlineData("DuplicateStatic")]
    [InlineData("UnknownRoute")]
    public async Task Unavailable_or_unverifiable_embedded_delivery_negotiates_burn_in_before_opening_the_engine(string deliveryCase)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) =>
        {
            if (count == 1) return [UnverifiableSource(deliveryCase)];
            Assert.Equal(2, count);
            Assert.Empty(context.Engine.Opened);
            return [BurnInSource()];
        };
        var eligible = deliveryCase != "UnavailableCapability";

        await context.Coordinator.PlayAsync(NativeSelection() with
        {
            NativeEmbeddedSubtitlesEligible = eligible
        }, TestContext.Current.CancellationToken);

        AssertBurnInReplacement(context, Assert.Single(context.Engine.Opened), ResumePositionTicks, eligible);
        Assert.Empty(context.Engine.Stopped);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.Equal(eligible, HasNativeSubtitleProfile(negotiations[0]));
        AssertBurnInNegotiation(negotiations[1], ResumePositionTicks);
        Assert.Equal("session-2", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_engine_subtitle_rejection_has_one_fresh_burn_in_attempt_at_the_same_position(bool burnInAlsoFails)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) => [count == 1 ? OriginalSource() : BurnInSource()];
        context.Engine.OnOpenAsync = (request, _) => request.NativeEmbeddedSubtitlesEligible || burnInAlsoFails
            ? Task.FromException(new PlaybackException("UnsupportedSubtitle")) : Task.CompletedTask;

        if (burnInAlsoFails)
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
                NativeSelection(), TestContext.Current.CancellationToken));
            Assert.Equal("UnsupportedSubtitle", error.ErrorCode);
            Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
            Assert.Null(context.Coordinator.Recovery);
            Assert.Empty(context.Handler.At("Sessions/Playing"));
        }
        else
        {
            await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
            AssertBurnInReplacement(context, context.Engine.Opened[1], ResumePositionTicks, true);
            Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        }

        var opened = context.Engine.Opened;
        Assert.Equal(2, opened.Length);
        Assert.True(opened[0].NativeEmbeddedSubtitlesEligible);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, opened[0].DeliveryMethod);
        Assert.False(opened[1].NativeEmbeddedSubtitlesEligible);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened[1].DeliveryMethod);
        Assert.Equal(ResumePositionTicks, opened[1].InitialPositionTicks);
        Assert.NotEqual(opened[0].PlaybackId, opened[1].PlaybackId);
        Assert.Contains(opened[0].PlaybackId, context.Engine.Stopped);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        AssertBurnInNegotiation(negotiations[1], ResumePositionTicks);
    }

    [Fact(Timeout = 15000)]
    public async Task A_server_that_keeps_embed_delivery_after_burn_in_is_rejected_without_a_third_negotiation()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource()];

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            NativeSelection() with { NativeEmbeddedSubtitlesEligible = false }, TestContext.Current.CancellationToken));

        Assert.Equal("UnsupportedSubtitle", error.ErrorCode);
        Assert.Empty(context.Engine.Opened);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Null(context.Coordinator.Recovery);
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        AssertBurnInNegotiation(negotiations[1], ResumePositionTicks);
    }

    [Fact(Timeout = 15000)]
    public async Task Selection_changes_enable_native_subtitles_and_loss_of_capability_burns_in_at_the_current_position()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) => [AllowsVideoCopy(request) ? OriginalSource() : BurnInSource()];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { SubtitleStreamIndex = -1 },
            TestContext.Current.CancellationToken);
        context.Engine.SetPosition(ResumePositionTicks);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            SubtitleStreamIndex = SubtitleIndex,
            NativeEmbeddedSubtitlesEligible = true
        }, TestContext.Current.CancellationToken);

        Assert.True(context.Engine.Opened[1].NativeEmbeddedSubtitlesEligible);
        Assert.True(context.Coordinator.ActiveContext!.Selection.NativeEmbeddedSubtitlesEligible);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            NativeEmbeddedSubtitlesEligible = false
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, context.Engine.Opened.Length);
        AssertBurnInReplacement(context, context.Engine.Opened[2], ResumePositionTicks, false);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(4, negotiations.Length);
        Assert.True(HasNativeSubtitleProfile(negotiations[1]));
        Assert.False(HasNativeSubtitleProfile(negotiations[2]));
        AssertBurnInNegotiation(negotiations[3], ResumePositionTicks);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edited_retry_uses_current_native_capability_and_the_new_sources_default_subtitle(bool nativeEligible)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) =>
        {
            var id = RequestedSourceId(request) ?? "source-a";
            return [AllowsVideoCopy(request) ? OriginalSource(id: id) : BurnInSource(id)];
        };
        context.Engine.OnOpenAsync = (_, _) => Task.FromException(new PlaybackException("NetworkFailure"));
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(NativeSelection() with
        {
            MediaSourceId = "source-a",
            SubtitleStreamIndex = -1,
            NativeEmbeddedSubtitlesEligible = !nativeEligible
        }, TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, new PlaybackSelectionChange
        {
            MediaSourceId = "source-b",
            NativeEmbeddedSubtitlesEligible = nativeEligible
        }, TestContext.Current.CancellationToken);

        var replacement = context.Engine.Opened.Last();
        Assert.Equal("source-b", replacement.Source.Id);
        Assert.Equal(SubtitleIndex, replacement.SubtitleStreamIndex);
        Assert.Equal(nativeEligible, replacement.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(nativeEligible ? PlaybackDeliveryMethod.DirectStream : PlaybackDeliveryMethod.Transcode, replacement.DeliveryMethod);
        Assert.Equal(ResumePositionTicks, context.Coordinator.ActiveContext!.PositionTicks);
        Assert.Equal(nativeEligible, context.Coordinator.ActiveContext.Selection.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(!nativeEligible, recovery.Selection.NativeEmbeddedSubtitlesEligible);
        Assert.Null(context.Coordinator.Recovery);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(nativeEligible ? 2 : 3, negotiations.Length);
        Assert.Equal(nativeEligible, HasNativeSubtitleProfile(negotiations[1]));
        Assert.All(negotiations.Skip(1), request =>
        {
            Assert.Equal("source-b", RequestedSourceId(request));
            Assert.False(request.JsonBody.TryGetProperty("SubtitleStreamIndex", out _));
        });
        if (!nativeEligible)
        {
            AssertBurnInReplacement(context, replacement, ResumePositionTicks, false);
            AssertBurnInNegotiation(negotiations[2], ResumePositionTicks, "source-b");
        }
    }

    [Fact(Timeout = 15000)]
    public async Task Replay_refreshes_native_capability_and_retains_the_exact_source_at_position_zero()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, count) => count == 1
            ? [OriginalSource(id: "source-b")]
            : AllowsVideoCopy(request)
                ? [OriginalSource(), OriginalSource(id: "source-b")]
                : [BurnInSource(), BurnInSource("source-b")];
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);

        await context.Coordinator.ReplayAsync(new PlaybackSelectionChange
        {
            NativeEmbeddedSubtitlesEligible = false
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        var replacement = context.Engine.Opened[1];
        Assert.Equal("source-b", replacement.Source.Id);
        AssertBurnInReplacement(context, replacement, 0, false);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(3, negotiations.Length);
        Assert.All(negotiations.Skip(1), request =>
        {
            Assert.Equal("source-b", RequestedSourceId(request));
            Assert.Equal(0, request.JsonBody.GetProperty("StartTimeTicks").GetInt64());
        });
        AssertBurnInNegotiation(negotiations[2], 0, "source-b");
    }

    [Theory(Timeout = 15000)]
    [InlineData(99)]
    [InlineData(1)]
    public async Task Missing_or_non_subtitle_indexes_remain_invalid_track_selections(int index)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource()];

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            NativeSelection() with { SubtitleStreamIndex = index }, TestContext.Current.CancellationToken));

        Assert.Equal("InvalidTrackSelection", error.ErrorCode);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Empty(context.Engine.Opened);
        Assert.Null(context.Coordinator.Recovery);
    }

    private static PlaybackSelection NativeSelection() => PlaybackTestContext.Selection(ResumePositionTicks) with
    {
        SubtitleStreamIndex = SubtitleIndex,
        NativeEmbeddedSubtitlesEligible = true
    };

    private static MediaStream BitmapSubtitle(string codec = "pgssub") => new()
    {
        Index = SubtitleIndex,
        Type = "Subtitle",
        Codec = codec,
        DeliveryMethod = "Embed",
        IsExternal = false,
        IsTextSubtitleStream = false
    };

    private static MediaSourceInfo OriginalSource(string codec = "pgssub", string id = "source-a") =>
        PlaybackTestContext.Source(id) with
        {
            Container = "mkv",
            DirectStreamUrl = "/emby/Videos/movie-a/stream.mkv?Static=true&MediaSourceId=" + id,
            DefaultSubtitleStreamIndex = SubtitleIndex,
            MediaStreams =
            [
                new() { Index = 0, Type = "Video", Codec = "h264", BitDepth = 8, Profile = "High", IsInterlaced = false, VideoRange = "SDR" },
                new() { Index = 1, Type = "Audio", Codec = "aac", Channels = 2 },
                BitmapSubtitle(codec)
            ]
        };

    private static MediaSourceInfo BurnInSource(string id = "source-a")
    {
        var source = OriginalSource(id: id) with
        {
            SupportsDirectStream = false,
            DirectStreamUrl = null,
            TranscodingUrl = "/emby/Videos/movie-a/master.m3u8?MediaSourceId=" + id + "&SubtitleMethod=Encode"
        };
        return WithSubtitle(source, BitmapSubtitle() with { DeliveryMethod = "Encode" });
    }

    private static MediaSourceInfo WithSubtitle(MediaSourceInfo source, MediaStream subtitle) => source with
    {
        MediaStreams = source.MediaStreams.Select(stream => stream.Index == SubtitleIndex ? subtitle : stream).ToArray()
    };

    private static MediaSourceInfo UnverifiableSource(string deliveryCase)
    {
        var source = OriginalSource();
        return deliveryCase switch
        {
            "UnavailableCapability" => source,
            "ExternalTrack" => WithSubtitle(source, BitmapSubtitle() with { IsExternal = true }),
            "UnknownEmbedding" => WithSubtitle(source, BitmapSubtitle() with { IsExternal = null }),
            "TextTrack" => WithSubtitle(source, BitmapSubtitle() with { IsTextSubtitleStream = true }),
            "AssTrack" => OriginalSource("ass"),
            "UnknownCodec" => OriginalSource("future-bitmap"),
            "MissingCodec" => WithSubtitle(source, BitmapSubtitle() with { Codec = null }),
            "RemuxContainer" => source with { DirectStreamUrl = "/emby/Videos/movie-a/stream.mp4?Static=true" },
            "HlsDirectRoute" => source with { DirectStreamUrl = "/emby/Videos/movie-a/master.m3u8?Static=true" },
            "TranscodedSource" => source with { SupportsDirectStream = false },
            "CrossOrigin" => source with { DirectStreamUrl = "https://cdn.example/emby/Videos/movie-a/stream.mkv?Static=true" },
            "MissingStatic" => source with { DirectStreamUrl = "/emby/Videos/movie-a/stream.mkv?MediaSourceId=source-a" },
            "StaticFalse" => source with { DirectStreamUrl = "/emby/Videos/movie-a/stream.mkv?Static=false" },
            "DuplicateStatic" => source with { DirectStreamUrl = "/emby/Videos/movie-a/stream.mkv?Static=true&static=true" },
            "UnknownRoute" => source with { DirectStreamUrl = "/emby/Items/movie-a/Download?Static=true" },
            _ => throw new ArgumentOutOfRangeException(nameof(deliveryCase))
        };
    }

    private static bool AllowsVideoCopy(RecordedRequest request) => request.JsonBody.GetProperty("AllowVideoStreamCopy").GetBoolean();

    private static string? RequestedSourceId(RecordedRequest request) =>
        request.JsonBody.TryGetProperty("MediaSourceId", out var id) ? id.GetString() : null;

    private static bool HasNativeSubtitleProfile(RecordedRequest request) => request.JsonBody.GetProperty("DeviceProfile")
        .GetProperty("SubtitleProfiles").EnumerateArray().Any(value => value.GetProperty("Method").GetString() == "Embed");

    private static void AssertBurnInReplacement(PlaybackTestContext context, PlaybackEngineRequest replacement,
        long positionTicks, bool nativeEligible)
    {
        Assert.Equal(PlaybackDeliveryMethod.Transcode, replacement.DeliveryMethod);
        Assert.False(replacement.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(SubtitleIndex, replacement.SubtitleStreamIndex);
        Assert.Equal("Encode", Assert.Single(replacement.Source.MediaStreams, value => value.Type == "Subtitle").DeliveryMethod);
        Assert.Equal(positionTicks, replacement.InitialPositionTicks);
        Assert.Null(replacement.ExternalSubtitleUri);
        var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.True(active.Selection.ForceSubtitleBurnIn);
        Assert.False(active.Selection.ForceTranscoding);
        Assert.Equal(nativeEligible, active.Selection.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(positionTicks, active.PositionTicks);
    }

    private static void AssertBurnInNegotiation(RecordedRequest request, long positionTicks, string id = "source-a")
    {
        var body = request.JsonBody;
        Assert.Equal(id, RequestedSourceId(request));
        Assert.Equal(positionTicks, body.GetProperty("StartTimeTicks").GetInt64());
        Assert.False(body.GetProperty("EnableDirectStream").GetBoolean());
        Assert.False(body.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.True(body.GetProperty("AllowAudioStreamCopy").GetBoolean());
        Assert.All(body.GetProperty("DeviceProfile").GetProperty("SubtitleProfiles").EnumerateArray(),
            value => Assert.Equal("Encode", value.GetProperty("Method").GetString()));
    }

    private static void AssertAvcAacConditions(DeviceProfile profile)
    {
        var avc = Assert.Single(profile.CodecProfiles!, value => value.Codec == "h264");
        Assert.Contains(avc.Conditions!, value => value.Property == "VideoBitDepth" && value.Condition == "LessThanEqual"
            && value.Value == "8" && value.IsRequired == true);
        Assert.Contains(avc.Conditions!, value => value.Property == "IsInterlaced" && value.Condition == "Equals"
            && value.Value == "false" && value.IsRequired == true);
        var aac = Assert.Single(profile.CodecProfiles!, value => value.Codec == "aac");
        Assert.Contains(aac.Conditions!, value => value.Property == "AudioChannels" && value.Condition == "LessThanEqual"
            && value.Value == "2" && value.IsRequired == true);
        var conversion = Assert.Single(profile.TranscodingProfiles!);
        Assert.Equal("h264", conversion.VideoCodec);
        Assert.Equal("aac", conversion.AudioCodec);
        Assert.Equal("2", conversion.MaxAudioChannels);
    }
}
