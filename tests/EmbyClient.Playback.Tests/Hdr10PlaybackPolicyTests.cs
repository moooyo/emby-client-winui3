using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class Hdr10PlaybackPolicyTests
{
    private const long ResumePositionTicks = 600_000_001;

    [Fact]
    public void Hdr_output_eligibility_defaults_to_false_and_selection_changes_leave_it_unspecified()
    {
        Assert.False(PlaybackTestContext.Selection().Hdr10OutputEligible);
        Assert.Null(new PlaybackSelectionChange().Hdr10OutputEligible);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Unconfirmed_output_and_forced_conversion_keep_the_avc_aac_baseline(
        bool hdr10OutputEligible, bool forceTranscoding)
    {
        var profile = DeviceProfileFactory.Create(PlaybackTestContext.Selection(transcode: forceTranscoding) with
        {
            Hdr10OutputEligible = hdr10OutputEligible
        });

        var direct = Assert.Single(profile.DirectPlayProfiles!);
        Assert.Equal("mp4", direct.Container);
        Assert.Equal("h264", direct.VideoCodec);
        Assert.Equal("aac", direct.AudioCodec);
        Assert.DoesNotContain(profile.CodecProfiles!, value => value.Codec == "hevc");
        var avc = Assert.Single(profile.CodecProfiles!, value => value.Codec == "h264");
        AssertCondition(avc, "VideoBitDepth", "LessThanEqual", "8");
        AssertCondition(avc, "IsInterlaced", "Equals", "false");
        AssertAacConversionBaseline(profile);
        AssertNoVideoRangeCondition(profile);
    }

    [Fact]
    public void Eligible_output_adds_only_a_progressive_ten_bit_hevc_aac_direct_profile()
    {
        var profile = DeviceProfileFactory.Create(PlaybackTestContext.Selection() with { Hdr10OutputEligible = true });

        Assert.Equal(2, profile.DirectPlayProfiles!.Length);
        var avc = Assert.Single(profile.DirectPlayProfiles!, value => value.VideoCodec == "h264");
        Assert.Equal("mp4", avc.Container);
        Assert.Equal("aac", avc.AudioCodec);
        var hevc = Assert.Single(profile.DirectPlayProfiles!, value => value.VideoCodec == "hevc");
        Assert.Equal("Video", hevc.Type);
        Assert.Equal("aac", hevc.AudioCodec);
        var containers = Assert.IsType<string>(hevc.Container)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.NotEmpty(containers);
        Assert.All(containers, value => Assert.Contains(value, new[] { "mkv", "mp4" }));
        var codec = Assert.Single(profile.CodecProfiles!, value => value.Codec == "hevc");
        AssertCondition(codec, "VideoBitDepth", "Equals", "10");
        AssertCondition(codec, "IsInterlaced", "Equals", "false");
        var baseline = Assert.Single(profile.CodecProfiles!, value => value.Codec == "h264");
        AssertCondition(baseline, "VideoBitDepth", "LessThanEqual", "8");
        var aac = Assert.Single(profile.CodecProfiles!, value => value.Codec == "aac");
        AssertCondition(aac, "AudioChannels", "LessThanEqual", "2");
        AssertAacConversionBaseline(profile);
        AssertNoVideoRangeCondition(profile);
    }

    [Theory(Timeout = 15000)]
    [InlineData("Main 10", "mkv", "HDR", null)]
    [InlineData("main10", "mp4", null, "")]
    [InlineData("Main 10", "mp4", "HDR", "Hdr10")]
    public async Task Explicit_hdr10_with_confirmed_output_reaches_the_engine_as_a_direct_hdr_request(
        string profile, string container, string? videoRange, string? extendedVideoType)
    {
        await using var context = new PlaybackTestContext();
        var video = Hdr10Video() with
        {
            Profile = profile, VideoRange = videoRange, ExtendedVideoType = extendedVideoType
        };
        context.Sources = (_, _) => [SourceWithVideo(video, container: container)];

        Assert.True(Hdr10PlaybackPolicy.IsExplicitHdr10(video));
        Assert.False(Hdr10PlaybackPolicy.RequiresServerConversion(video, true));
        Assert.True(Hdr10PlaybackPolicy.RequiresServerConversion(video, false));
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks) with
        {
            Hdr10OutputEligible = true
        }, TestContext.Current.CancellationToken);

        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, opened.DeliveryMethod);
        Assert.True(opened.RequiresHdr10Output);
        Assert.True(opened.Hdr10OutputAllowed);
        Assert.Equal(ResumePositionTicks, opened.InitialPositionTicks);
        Assert.Equal(container, opened.Source.Container);
        Assert.True(HasHevcDirectProfile(Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"))));
        Assert.True(context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        Assert.False(context.Coordinator.ActiveContext?.Selection.ForceTranscoding);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sdr_avc_does_not_require_hdr_output_or_server_conversion(bool hdr10OutputEligible)
    {
        await using var context = new PlaybackTestContext();
        var video = SdrVideo();
        context.Sources = (_, _) => [SourceWithVideo(video, container: "mp4")];

        Assert.False(Hdr10PlaybackPolicy.IsExplicitHdr10(video));
        Assert.False(Hdr10PlaybackPolicy.RequiresServerConversion(video, hdr10OutputEligible));
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            Hdr10OutputEligible = hdr10OutputEligible
        }, TestContext.Current.CancellationToken);

        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, opened.DeliveryMethod);
        Assert.False(opened.RequiresHdr10Output);
        Assert.False(opened.Hdr10OutputAllowed);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Fact(Timeout = 15000)]
    public async Task Explicit_audio_selection_allows_hdr_output_for_an_hdr_transcode_with_video_copy_enabled()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [SourceWithVideo(Hdr10Video())];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks) with
        {
            Hdr10OutputEligible = true,
            AudioStreamIndex = 2
        }, TestContext.Current.CancellationToken);

        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened.DeliveryMethod);
        Assert.False(opened.RequiresHdr10Output);
        Assert.True(opened.Hdr10OutputAllowed);
        Assert.True(Hdr10PlaybackPolicy.IsExplicitHdr10(Assert.Single(opened.Source.MediaStreams, value => value.Type == "Video")));
        var negotiation = Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.False(negotiation.JsonBody.GetProperty("EnableDirectStream").GetBoolean());
        Assert.True(negotiation.JsonBody.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.True(negotiation.JsonBody.GetProperty("AllowAudioStreamCopy").GetBoolean());
        Assert.True(HasHevcDirectProfile(negotiation));
    }

    [Fact(Timeout = 15000)]
    public async Task Hdr10_without_confirmed_output_support_negotiates_a_fresh_sdr_conversion_before_engine_open()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) => [count == 1 ? SourceWithVideo(Hdr10Video()) : SdrSource()];

        // Off preferences and unavailable output capabilities both enter playback with eligibility false.
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks), TestContext.Current.CancellationToken);

        AssertFreshSdrFallback(context, expectedEligibility: false);
        Assert.False(HasHevcDirectProfile(context.Handler.At("Items/movie-a/PlaybackInfo")[0]));
        Assert.Empty(context.Engine.Stopped);
    }

    [Theory(Timeout = 15000)]
    [InlineData("UnknownVideoRange")]
    [InlineData("UnknownSubtype")]
    [InlineData("Hlg")]
    [InlineData("DoviProfile50")]
    [InlineData("DoviProfile81")]
    [InlineData("DoviProfile50SdrLabel")]
    [InlineData("DoviProfile81SdrLabel")]
    [InlineData("Hdr10Plus")]
    [InlineData("MissingSubtype")]
    [InlineData("MissingCodec")]
    [InlineData("WrongCodec")]
    [InlineData("MissingBitDepth")]
    [InlineData("EightBit")]
    [InlineData("MissingProfile")]
    [InlineData("WrongProfile")]
    [InlineData("MissingScanType")]
    [InlineData("Interlaced")]
    [InlineData("MissingTransfer")]
    [InlineData("NonPqTransfer")]
    [InlineData("MissingPrimaries")]
    [InlineData("NonBt2020Primaries")]
    [InlineData("ConflictingSdrRange")]
    [InlineData("ConflictingDolbyVisionType")]
    [InlineData("ConflictingHyperLogGammaType")]
    [InlineData("ConflictingHdr10PlusType")]
    [InlineData("ConflictingUnknownType")]
    [InlineData("ConflictingNoneType")]
    [InlineData("AvcSdrWithDolbyVisionType")]
    [InlineData("UnspecifiedHevc")]
    [InlineData("HevcTenBitSdr")]
    public async Task Unknown_conflicting_or_incomplete_hdr_metadata_requires_a_fresh_sdr_conversion(string metadataCase)
    {
        await using var context = new PlaybackTestContext();
        var video = UnsupportedVideo(metadataCase);
        context.Sources = (_, count) => [count == 1 ? SourceWithVideo(video) : SdrSource()];

        Assert.False(Hdr10PlaybackPolicy.IsExplicitHdr10(video));
        Assert.True(Hdr10PlaybackPolicy.RequiresServerConversion(video, true));
        Assert.True(Hdr10PlaybackPolicy.RequiresServerConversion(video, false));
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks) with
        {
            Hdr10OutputEligible = true
        }, TestContext.Current.CancellationToken);

        AssertFreshSdrFallback(context, expectedEligibility: true);
        Assert.Empty(context.Engine.Stopped);
    }

    [Theory(Timeout = 15000)]
    [InlineData(null)]
    [InlineData("ts")]
    public async Task Hdr10_outside_the_supported_progressive_containers_requires_fresh_conversion(string? container)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) => [count == 1 ? SourceWithVideo(Hdr10Video(), container: container) : SdrSource()];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks) with
        {
            Hdr10OutputEligible = true
        }, TestContext.Current.CancellationToken);

        AssertFreshSdrFallback(context, expectedEligibility: true);
        Assert.Empty(context.Engine.Stopped);
    }

    [Fact(Timeout = 15000)]
    public async Task Forced_conversion_of_an_hdr_source_never_advertises_hevc_or_requires_hdr_output()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [SourceWithVideo(Hdr10Video())];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks, transcode: true) with
        {
            Hdr10OutputEligible = true
        }, TestContext.Current.CancellationToken);

        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened.DeliveryMethod);
        Assert.False(opened.RequiresHdr10Output);
        Assert.False(opened.Hdr10OutputAllowed);
        Assert.True(Hdr10PlaybackPolicy.IsExplicitHdr10(Assert.Single(opened.Source.MediaStreams, value => value.Type == "Video")));
        AssertSdrNegotiation(Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo")), ResumePositionTicks);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_hdr_engine_open_failure_permits_only_one_fresh_sdr_negotiation(bool sdrOpenAlsoFails)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) => [count == 1 ? SourceWithVideo(Hdr10Video()) : SdrSource()];
        context.Engine.OnOpenAsync = (request, _) => request.RequiresHdr10Output || sdrOpenAlsoFails
            ? Task.FromException(new PlaybackException("UnsupportedFormat")) : Task.CompletedTask;
        var selection = PlaybackTestContext.Selection(ResumePositionTicks) with { Hdr10OutputEligible = true };

        if (sdrOpenAlsoFails)
        {
            var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
                selection, TestContext.Current.CancellationToken));
            Assert.Equal("UnsupportedFormat", error.ErrorCode);
            Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
            Assert.Empty(context.Handler.At("Sessions/Playing"));
            Assert.Null(context.Coordinator.Recovery);
        }
        else
        {
            await context.Coordinator.PlayAsync(selection, TestContext.Current.CancellationToken);
            Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
            Assert.Equal("session-2", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
        }

        var opened = context.Engine.Opened;
        Assert.Equal(2, opened.Length);
        Assert.True(opened[0].RequiresHdr10Output);
        Assert.True(opened[0].Hdr10OutputAllowed);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, opened[0].DeliveryMethod);
        Assert.False(opened[1].RequiresHdr10Output);
        Assert.False(opened[1].Hdr10OutputAllowed);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened[1].DeliveryMethod);
        Assert.NotEqual(opened[0].PlaybackId, opened[1].PlaybackId);
        Assert.Equal(ResumePositionTicks, opened[1].InitialPositionTicks);
        Assert.Contains(opened[0].PlaybackId, context.Engine.Stopped);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.True(HasHevcDirectProfile(negotiations[0]));
        AssertSdrNegotiation(negotiations[1], ResumePositionTicks);
    }

    [Fact(Timeout = 15000)]
    public async Task Changing_output_eligibility_enables_hdr_and_disabling_it_renegotiates_sdr_at_the_current_position()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, count) => [count == 1 || !AllowsVideoCopy(request)
            ? SdrSource() : SourceWithVideo(Hdr10Video())];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        context.Engine.SetPosition(ResumePositionTicks);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { Hdr10OutputEligible = true },
            TestContext.Current.CancellationToken);

        Assert.True(context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        Assert.True(context.Engine.Opened[1].RequiresHdr10Output);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, context.Engine.Opened[1].DeliveryMethod);
        Assert.True(HasHevcDirectProfile(context.Handler.At("Items/movie-a/PlaybackInfo")[1]));

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { Hdr10OutputEligible = false },
            TestContext.Current.CancellationToken);

        var replacement = context.Engine.Opened.Last();
        Assert.False(replacement.RequiresHdr10Output);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, replacement.DeliveryMethod);
        Assert.False(context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        Assert.True(context.Coordinator.ActiveContext?.Selection.ForceTranscoding);
        Assert.Equal(ResumePositionTicks, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(3, context.Engine.Opened.Length);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(4, negotiations.Length);
        Assert.False(HasHevcDirectProfile(negotiations[2]));
        AssertSdrNegotiation(negotiations[3], ResumePositionTicks);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edited_retry_output_eligibility_takes_effect_and_uses_the_new_source_defaults(bool hdr10OutputEligible)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) => [RequestedSourceId(request) == "source-b"
            ? SourceWithVideo(AllowsVideoCopy(request) ? Hdr10Video() : SdrVideo(), "source-b", audioIndex: 101)
            : SdrSource()];
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(PlaybackTestContext.Selection(ResumePositionTicks) with
        {
            MediaSourceId = "source-a",
            AudioStreamIndex = 2,
            Hdr10OutputEligible = !hdr10OutputEligible
        }, TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, new PlaybackSelectionChange
        {
            MediaSourceId = "source-b",
            Hdr10OutputEligible = hdr10OutputEligible
        }, TestContext.Current.CancellationToken);

        Assert.Equal(!hdr10OutputEligible, recovery.Selection.Hdr10OutputEligible);
        var replacement = context.Engine.Opened.Last();
        Assert.Equal("source-b", replacement.Source.Id);
        Assert.Equal(101, replacement.AudioStreamIndex);
        Assert.Equal(hdr10OutputEligible, replacement.RequiresHdr10Output);
        Assert.Equal(hdr10OutputEligible ? PlaybackDeliveryMethod.DirectStream : PlaybackDeliveryMethod.Transcode,
            replacement.DeliveryMethod);
        Assert.Equal(hdr10OutputEligible, context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        Assert.Equal(ResumePositionTicks, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Null(context.Coordinator.Recovery);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(hdr10OutputEligible ? 2 : 3, negotiations.Length);
        Assert.Equal(hdr10OutputEligible, HasHevcDirectProfile(negotiations[1]));
        Assert.All(negotiations.Skip(1), request =>
        {
            Assert.Equal("source-b", RequestedSourceId(request));
            Assert.False(request.JsonBody.TryGetProperty("AudioStreamIndex", out _));
        });
        if (!hdr10OutputEligible) AssertSdrNegotiation(negotiations.Last(), ResumePositionTicks, "source-b");
    }

    [Fact(Timeout = 15000)]
    public async Task Replay_after_an_open_failure_refreshes_capabilities_and_preserves_exact_choices_at_position_zero()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [SourceWithVideo(SdrVideo(), "source-edition", container: "mp4")];
        var selection = PlaybackTestContext.Selection(ResumePositionTicks) with
        {
            ItemId = "movie-replay",
            MediaSourceId = "source-edition",
            AudioStreamIndex = 2,
            SubtitleStreamIndex = 3,
            MaxStreamingBitrate = 4_000_000,
            Hdr10OutputEligible = false,
            NativeEmbeddedSubtitlesEligible = false
        };
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(selection, TestContext.Current.CancellationToken));
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.ReplayAsync(new PlaybackSelectionChange
        {
            Hdr10OutputEligible = true,
            NativeEmbeddedSubtitlesEligible = true
        }, TestContext.Current.CancellationToken);

        var replay = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.Equal(selection.ItemId, replay.Selection.ItemId);
        Assert.Equal(selection.MediaSourceId, replay.Selection.MediaSourceId);
        Assert.Equal(selection.AudioStreamIndex, replay.Selection.AudioStreamIndex);
        Assert.Equal(selection.SubtitleStreamIndex, replay.Selection.SubtitleStreamIndex);
        Assert.Equal(selection.MaxStreamingBitrate, replay.Selection.MaxStreamingBitrate);
        Assert.False(replay.Selection.ForceTranscoding);
        Assert.True(replay.Selection.Hdr10OutputEligible);
        Assert.True(replay.Selection.NativeEmbeddedSubtitlesEligible);
        Assert.Equal(0, replay.Selection.StartPositionTicks);
        Assert.Equal(0, replay.PositionTicks);
        Assert.Null(context.Coordinator.Recovery);
        var negotiations = context.Handler.At("Items/movie-replay/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.False(HasHevcDirectProfile(negotiations[0]));
        Assert.True(HasHevcDirectProfile(negotiations[1]));
        Assert.Equal("source-edition", RequestedSourceId(negotiations[1]));
        Assert.Equal(2, negotiations[1].JsonBody.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(3, negotiations[1].JsonBody.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(0, negotiations[1].JsonBody.GetProperty("StartTimeTicks").GetInt64());
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(0, context.Engine.Opened[1].InitialPositionTicks);
        Assert.NotEqual(context.Engine.Opened[0].PlaybackId, context.Engine.Opened[1].PlaybackId);

        var sourceWithoutChosenAudio = SourceWithVideo(SdrVideo(), "source-edition", container: "mp4");
        context.Sources = (_, _) => [sourceWithoutChosenAudio with
        {
            MediaStreams = sourceWithoutChosenAudio.MediaStreams.Where(value => value.Index != 2).ToArray()
        }];
        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.ReplayAsync(
            new PlaybackSelectionChange { Hdr10OutputEligible = false, NativeEmbeddedSubtitlesEligible = false },
            TestContext.Current.CancellationToken));

        Assert.Equal("InvalidTrackSelection", error.ErrorCode);
        Assert.Equal(2, context.Engine.Opened.Length);
        var rejectedReplay = context.Handler.At("Items/movie-replay/PlaybackInfo").Last();
        Assert.False(HasHevcDirectProfile(rejectedReplay));
        Assert.Equal("source-edition", RequestedSourceId(rejectedReplay));
        Assert.Equal(2, rejectedReplay.JsonBody.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(3, rejectedReplay.JsonBody.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(0, rejectedReplay.JsonBody.GetProperty("StartTimeTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Source_changes_preserve_output_capability_and_recheck_each_sources_hdr_metadata()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) => [RequestedSourceId(request) switch
        {
            "source-b" => SourceWithVideo(SdrVideo(), "source-b", container: "mp4", audioIndex: 101),
            "source-c" => SourceWithVideo(Hdr10Video(), "source-c", audioIndex: 201),
            _ => SourceWithVideo(Hdr10Video())
        }];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { Hdr10OutputEligible = true },
            TestContext.Current.CancellationToken);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MediaSourceId = "source-b" },
            TestContext.Current.CancellationToken);
        Assert.True(context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MediaSourceId = "source-c" },
            TestContext.Current.CancellationToken);

        Assert.True(context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        Assert.Equal(new[] { "source-a", "source-b", "source-c" }, context.Engine.Opened.Select(value => value.Source.Id));
        Assert.Equal(new[] { true, false, true }, context.Engine.Opened.Select(value => value.RequiresHdr10Output));
        Assert.Equal(101, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal(201, context.Engine.Opened[2].AudioStreamIndex);
        Assert.All(context.Engine.Opened, value => Assert.Equal(PlaybackDeliveryMethod.DirectStream, value.DeliveryMethod));
        Assert.Equal(3, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Changing_hdr_eligibility_never_bypasses_the_new_sources_explicit_track_validation(bool retry)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) => [RequestedSourceId(request) == "source-b"
            ? SourceWithVideo(Hdr10Video(), "source-b", audioIndex: 101) : SdrSource()];
        var initial = PlaybackTestContext.Selection() with { AudioStreamIndex = 2 };
        PlaybackRecovery? recovery = null;
        if (retry)
        {
            context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
            await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(initial, TestContext.Current.CancellationToken));
            recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
            context.Engine.OnOpenAsync = null;
        }
        else await context.Coordinator.PlayAsync(initial, TestContext.Current.CancellationToken);
        var change = new PlaybackSelectionChange
        {
            MediaSourceId = "source-b",
            AudioStreamIndex = 2,
            Hdr10OutputEligible = true
        };

        var error = await Assert.ThrowsAsync<PlaybackException>(() => retry
            ? context.Coordinator.RetryAsync(recovery!.RecoveryId, change, TestContext.Current.CancellationToken)
            : context.Coordinator.ChangeSelectionAsync(change, TestContext.Current.CancellationToken));

        Assert.Equal("InvalidTrackSelection", error.ErrorCode);
        Assert.Equal("source-a", Assert.Single(context.Engine.Opened).Source.Id);
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Null(context.Coordinator.ActiveContext);
    }

    private static MediaStream Hdr10Video() => new()
    {
        Index = 0,
        Type = "Video",
        Codec = "hevc",
        BitDepth = 10,
        Profile = "Main 10",
        IsInterlaced = false,
        ExtendedVideoSubType = "Hdr10",
        VideoRange = "HDR",
        ColorTransfer = "smpte2084",
        ColorPrimaries = "bt2020"
    };

    private static MediaStream SdrVideo() => new()
    {
        Index = 0,
        Type = "Video",
        Codec = "h264",
        BitDepth = 8,
        Profile = "High",
        IsInterlaced = false,
        ExtendedVideoSubType = "None",
        VideoRange = "SDR",
        ColorTransfer = "bt709",
        ColorPrimaries = "bt709"
    };

    private static MediaStream UnsupportedVideo(string metadataCase) => metadataCase switch
    {
        "UnknownVideoRange" => Hdr10Video() with { ExtendedVideoSubType = null, VideoRange = "Unknown" },
        "UnknownSubtype" => Hdr10Video() with { ExtendedVideoSubType = "Unknown" },
        "Hlg" => Hdr10Video() with { ExtendedVideoSubType = "Hlg", VideoRange = "HLG", ColorTransfer = "arib-std-b67" },
        "DoviProfile50" => Hdr10Video() with { ExtendedVideoSubType = "DoviProfile50" },
        "DoviProfile81" => Hdr10Video() with { ExtendedVideoSubType = "DoviProfile81" },
        "DoviProfile50SdrLabel" => Hdr10Video() with { ExtendedVideoSubType = "DoviProfile50", VideoRange = "SDR" },
        "DoviProfile81SdrLabel" => Hdr10Video() with { ExtendedVideoSubType = "DoviProfile81", VideoRange = "SDR" },
        "Hdr10Plus" => Hdr10Video() with { ExtendedVideoSubType = "Hdr10Plus", VideoRange = "HDR10+" },
        "MissingSubtype" => Hdr10Video() with { ExtendedVideoSubType = null },
        "MissingCodec" => Hdr10Video() with { Codec = null },
        "WrongCodec" => Hdr10Video() with { Codec = "h264" },
        "MissingBitDepth" => Hdr10Video() with { BitDepth = null },
        "EightBit" => Hdr10Video() with { BitDepth = 8 },
        "MissingProfile" => Hdr10Video() with { Profile = null },
        "WrongProfile" => Hdr10Video() with { Profile = "Main" },
        "MissingScanType" => Hdr10Video() with { IsInterlaced = null },
        "Interlaced" => Hdr10Video() with { IsInterlaced = true },
        "MissingTransfer" => Hdr10Video() with { ColorTransfer = null },
        "NonPqTransfer" => Hdr10Video() with { ColorTransfer = "bt709" },
        "MissingPrimaries" => Hdr10Video() with { ColorPrimaries = null },
        "NonBt2020Primaries" => Hdr10Video() with { ColorPrimaries = "bt709" },
        "ConflictingSdrRange" => Hdr10Video() with { VideoRange = "SDR" },
        "ConflictingDolbyVisionType" => Hdr10Video() with { ExtendedVideoType = "DolbyVision" },
        "ConflictingHyperLogGammaType" => Hdr10Video() with { ExtendedVideoType = "HyperLogGamma" },
        "ConflictingHdr10PlusType" => Hdr10Video() with { ExtendedVideoType = "Hdr10Plus" },
        "ConflictingUnknownType" => Hdr10Video() with { ExtendedVideoType = "Unknown" },
        "ConflictingNoneType" => Hdr10Video() with { ExtendedVideoType = "None" },
        "AvcSdrWithDolbyVisionType" => SdrVideo() with { ExtendedVideoType = "DolbyVision", ExtendedVideoSubType = null },
        "UnspecifiedHevc" => Hdr10Video() with
        {
            ExtendedVideoSubType = null, VideoRange = null, ColorTransfer = null, ColorPrimaries = null
        },
        "HevcTenBitSdr" => Hdr10Video() with
        {
            ExtendedVideoSubType = "None", VideoRange = "SDR", ColorTransfer = "bt709", ColorPrimaries = "bt709"
        },
        _ => throw new ArgumentOutOfRangeException(nameof(metadataCase))
    };

    private static MediaSourceInfo SourceWithVideo(MediaStream video, string id = "source-a", string? container = "mkv", int audioIndex = 1) =>
        PlaybackTestContext.Source(id) with
        {
            Container = container,
            DefaultAudioStreamIndex = audioIndex,
            DefaultSubtitleStreamIndex = -1,
            MediaStreams =
            [
                video,
                new() { Index = audioIndex, Type = "Audio", Codec = "aac", Channels = 2 },
                new() { Index = audioIndex + 1, Type = "Audio", Codec = "aac", Channels = 2 },
                new() { Index = audioIndex + 2, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode", IsTextSubtitleStream = true }
            ]
        };

    private static MediaSourceInfo SdrSource() => SourceWithVideo(SdrVideo(), container: "mp4");

    private static bool AllowsVideoCopy(RecordedRequest request) => request.JsonBody.GetProperty("AllowVideoStreamCopy").GetBoolean();

    private static string? RequestedSourceId(RecordedRequest request) =>
        request.JsonBody.TryGetProperty("MediaSourceId", out var sourceId) ? sourceId.GetString() : null;

    private static bool HasHevcDirectProfile(RecordedRequest request) => request.JsonBody.GetProperty("DeviceProfile")
        .GetProperty("DirectPlayProfiles").EnumerateArray().Any(value => value.GetProperty("VideoCodec").GetString() == "hevc");

    private static void AssertFreshSdrFallback(PlaybackTestContext context, bool expectedEligibility)
    {
        var opened = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened.DeliveryMethod);
        Assert.False(opened.RequiresHdr10Output);
        Assert.False(opened.Hdr10OutputAllowed);
        Assert.Equal(ResumePositionTicks, opened.InitialPositionTicks);
        Assert.Equal("SDR", Assert.Single(opened.Source.MediaStreams, value => value.Type == "Video").VideoRange);
        Assert.True(context.Coordinator.ActiveContext?.Selection.ForceTranscoding);
        Assert.Equal(expectedEligibility, context.Coordinator.ActiveContext?.Selection.Hdr10OutputEligible);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        AssertSdrNegotiation(negotiations[1], ResumePositionTicks);
        Assert.Equal("session-2", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
    }

    private static void AssertSdrNegotiation(RecordedRequest request, long positionTicks, string? sourceId = null)
    {
        var body = request.JsonBody;
        Assert.False(body.GetProperty("EnableDirectPlay").GetBoolean());
        Assert.False(body.GetProperty("EnableDirectStream").GetBoolean());
        Assert.False(body.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.False(body.GetProperty("AllowAudioStreamCopy").GetBoolean());
        Assert.True(body.GetProperty("EnableTranscoding").GetBoolean());
        Assert.Equal(positionTicks, body.GetProperty("StartTimeTicks").GetInt64());
        if (sourceId is not null) Assert.Equal(sourceId, RequestedSourceId(request));
        var profile = body.GetProperty("DeviceProfile");
        var direct = Assert.Single(profile.GetProperty("DirectPlayProfiles").EnumerateArray());
        Assert.Equal("h264", direct.GetProperty("VideoCodec").GetString());
        Assert.Equal("aac", direct.GetProperty("AudioCodec").GetString());
        Assert.Equal("mp4", direct.GetProperty("Container").GetString());
        Assert.DoesNotContain(profile.GetProperty("CodecProfiles").EnumerateArray(), value => value.GetProperty("Codec").GetString() == "hevc");
        var conditions = profile.GetProperty("CodecProfiles").EnumerateArray()
            .SelectMany(value => value.GetProperty("Conditions").EnumerateArray()).ToArray();
        var bitDepth = Assert.Single(conditions, value => value.GetProperty("Property").GetString() == "VideoBitDepth");
        Assert.Equal("LessThanEqual", bitDepth.GetProperty("Condition").GetString());
        Assert.Equal("8", bitDepth.GetProperty("Value").GetString());
        Assert.True(bitDepth.GetProperty("IsRequired").GetBoolean());
        Assert.DoesNotContain(conditions, value => value.GetProperty("Property").GetString() == "VideoRange");
    }

    private static void AssertCondition(CodecProfile profile, string property, string condition, string value)
    {
        var actual = Assert.Single(profile.Conditions!, item => item.Property == property);
        Assert.Equal(condition, actual.Condition);
        Assert.Equal(value, actual.Value);
        Assert.True(actual.IsRequired);
    }

    private static void AssertAacConversionBaseline(DeviceProfile profile)
    {
        var transcode = Assert.Single(profile.TranscodingProfiles!);
        Assert.Equal("ts", transcode.Container);
        Assert.Equal("hls", transcode.Protocol);
        Assert.Equal("h264", transcode.VideoCodec);
        Assert.Equal("aac", transcode.AudioCodec);
        Assert.Equal("2", transcode.MaxAudioChannels);
    }

    private static void AssertNoVideoRangeCondition(DeviceProfile profile)
    {
        var conditions = (profile.CodecProfiles ?? []).SelectMany(value => value.Conditions ?? [])
            .Concat((profile.ContainerProfiles ?? []).SelectMany(value => value.Conditions ?? []));
        Assert.DoesNotContain(conditions, value => value.Property == "VideoRange");
    }
}
