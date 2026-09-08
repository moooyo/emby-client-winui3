using System.Net;
using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackNegotiationTests
{


    [Fact(Timeout = 15000)]
    public async Task Forced_transcoding_disables_direct_delivery_and_uses_the_returned_transcode_url()
    {
        await using var context = new PlaybackTestContext();

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.Equal("/emby/Videos/movie-a/master.m3u8", request.MediaUri.AbsolutePath);
        var negotiation = Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo")).JsonBody;
        Assert.False(negotiation.GetProperty("EnableDirectPlay").GetBoolean());
        Assert.False(negotiation.GetProperty("EnableDirectStream").GetBoolean());
        Assert.False(negotiation.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.False(negotiation.GetProperty("AllowAudioStreamCopy").GetBoolean());
        Assert.True(negotiation.GetProperty("EnableTranscoding").GetBoolean());
        Assert.Equal("Transcode", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlayMethod").GetString());
    }

    [Fact(Timeout = 15000)]
    public async Task Forced_transcoding_fails_when_the_server_offers_only_a_direct_source()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { SupportsTranscoding = false, TranscodingUrl = null }];

        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(transcode: true), TestContext.Current.CancellationToken));

        Assert.Empty(context.Engine.Opened);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
    }

    [Fact(Timeout = 15000)]
    public async Task Progressive_segment_reports_add_the_confirmed_trim_offset_and_seeking_renegotiates_original_ticks()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.ProgressiveSource()];
        const long initialPosition = 600_000_001;
        const long enginePosition = 120_000_007;
        const long targetPosition = 4_200_000_009;
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(initialPosition, transcode: true), TestContext.Current.CancellationToken);
        var first = Assert.Single(context.Engine.Opened);
        Assert.Equal(0, first.InitialPositionTicks);
        Assert.Equal(initialPosition, first.TimelineOffsetTicks);
        Assert.Equal(PlaybackTimelineKind.ProgressiveSegment, first.TimelineKind);
        Assert.Equal(initialPosition.ToString(), new RecordedRequest(HttpMethod.Get, first.MediaUri, null).Query["StartTimeTicks"]);
        context.Engine.SetPosition(enginePosition);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.SeekAsync(targetPosition, TestContext.Current.CancellationToken);

        var stopped = Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody;
        Assert.Equal(initialPosition + enginePosition, stopped.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(initialPosition + enginePosition, context.Handler.At("Sessions/Playing/Progress")[0].JsonBody.GetProperty("PositionTicks").GetInt64());
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.Equal(targetPosition, negotiations[1].JsonBody.GetProperty("StartTimeTicks").GetInt64());
        Assert.Equal(targetPosition, context.Engine.Opened[1].TimelineOffsetTicks);
        Assert.Equal(0, context.Engine.Opened[1].InitialPositionTicks);
        Assert.Empty(context.Engine.Sought);
        Assert.Equal(targetPosition, context.Handler.At("Sessions/Playing")[1].JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Direct_stream_seeking_uses_the_original_item_timeline_without_creating_another_session()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(100_000_001), TestContext.Current.CancellationToken);

        await context.Coordinator.SeekAsync(2_345_678_901, TestContext.Current.CancellationToken);

        Assert.Equal(2_345_678_901, Assert.Single(context.Engine.Sought).PositionTicks);
        Assert.Single(context.Engine.Opened);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Equal(2_345_678_901, Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody.GetProperty("PositionTicks").GetInt64());
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
    }

    [Fact(Timeout = 15000)]
    public async Task Audio_and_quality_changes_keep_the_absolute_position_and_negotiate_the_new_selection()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        context.Engine.SetPosition(720_000_007);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            AudioStreamIndex = 2,
            MaxStreamingBitrate = 4_000_000
        }, TestContext.Current.CancellationToken);

        var request = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal(720_000_007, request.GetProperty("StartTimeTicks").GetInt64());
        Assert.Equal(2, request.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(4_000_000, request.GetProperty("MaxStreamingBitrate").GetInt64());
        Assert.Equal(PlaybackDeliveryMethod.Transcode, context.Engine.Opened[1].DeliveryMethod);
        Assert.Equal(0, context.Engine.Opened[1].TimelineOffsetTicks);
        Assert.Equal(720_000_007, context.Engine.Opened[1].InitialPositionTicks);
        Assert.Equal(2, context.Engine.Opened[1].AudioStreamIndex);
        Assert.NotEqual(context.Engine.Opened[0].PlaybackId, context.Engine.Opened[1].PlaybackId);
        var report = Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody;
        Assert.Equal("AudioTrackChange", report.GetProperty("EventName").GetString());
        Assert.Equal("session-2", report.GetProperty("PlaySessionId").GetString());
        Assert.Equal(720_000_007, report.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Subtitle_selection_uses_burn_in_delivery_and_can_be_explicitly_disabled()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        context.Engine.SetPosition(400_000_003);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = 3 }, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, context.Engine.Opened[1].DeliveryMethod);
        Assert.Equal(3, context.Engine.Opened[1].SubtitleStreamIndex);
        Assert.Equal("SubtitleTrackChange", context.Handler.At("Sessions/Playing/Progress")[0].JsonBody.GetProperty("EventName").GetString());

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, context.Engine.Opened[2].SubtitleStreamIndex);
        Assert.Null(context.Engine.Opened[2].ExternalSubtitleUri);
        Assert.Equal(-1, context.Handler.At("Items/movie-a/PlaybackInfo")[2].JsonBody.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(400_000_003, context.Handler.At("Sessions/Playing")[2].JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Fact(Timeout = 15000)]
    public async Task Changing_media_editions_discards_track_indexes_from_the_old_container()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, count) => count == 1
            ? [PlaybackTestContext.Source()]
            : [PlaybackTestContext.Source("source-b") with
            {
                DefaultAudioStreamIndex = 8,
                DefaultSubtitleStreamIndex = -1,
                MediaStreams = [new MediaStream { Index = 8, Type = "Audio", Codec = "aac" }]
            }];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            AudioStreamIndex = 2,
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);
        context.Engine.SetPosition(300_000_011);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MediaSourceId = "source-b" }, TestContext.Current.CancellationToken);

        var negotiation = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal("source-b", negotiation.GetProperty("MediaSourceId").GetString());
        Assert.False(negotiation.TryGetProperty("AudioStreamIndex", out _));
        Assert.False(negotiation.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.Equal(8, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal(-1, context.Engine.Opened[1].SubtitleStreamIndex);
        Assert.Equal(300_000_011, context.Coordinator.ActiveContext?.PositionTicks);
    }

    [Fact(Timeout = 15000)]
    public async Task Explicit_open_uses_the_updated_source_and_closes_the_returned_live_stream_handle()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [new MediaSourceInfo { Id = "unopened-source", RequiresOpening = true, OpenToken = "opaque-open-token" }];
        context.Handler.RespondAsync = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/emby/LiveStreams/Open"
            ? RecordingHandler.OpenedSource(PlaybackTestContext.Source("opened-source") with
            {
                RequiresOpening = false,
                RequiresClosing = true,
                LiveStreamId = "new-live-handle",
                DirectStreamUrl = "/emby/Videos/1234/opened-stream?Static=true"
            }) : context.Respond(request));

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { ItemId = "1234" }, TestContext.Current.CancellationToken);
        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);

        var openBody = Assert.Single(context.Handler.At("LiveStreams/Open")).JsonBody;
        Assert.Equal(1234, openBody.GetProperty("ItemId").GetInt64());
        Assert.Equal("opaque-open-token", openBody.GetProperty("OpenToken").GetString());
        Assert.Equal("session-1", openBody.GetProperty("PlaySessionId").GetString());
        var engine = Assert.Single(context.Engine.Opened);
        Assert.Equal("opened-source", engine.Source.Id);
        Assert.Equal("/emby/Videos/1234/opened-stream", engine.MediaUri.AbsolutePath);
        var started = Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody;
        Assert.Equal("opened-source", started.GetProperty("MediaSourceId").GetString());
        Assert.Equal("new-live-handle", started.GetProperty("LiveStreamId").GetString());
        Assert.Equal("new-live-handle", Assert.Single(context.Handler.At("LiveStreams/Close")).Query["LiveStreamId"]);
    }

    [Fact(Timeout = 15000)]
    public async Task One_unsupported_direct_open_falls_back_to_a_new_transcode_session()
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (request, _) => request.DeliveryMethod == PlaybackDeliveryMethod.DirectStream
            ? Task.FromException(new PlaybackException("UnsupportedFormat")) : Task.CompletedTask;

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, context.Engine.Opened[0].DeliveryMethod);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, context.Engine.Opened[1].DeliveryMethod);
        Assert.Equal(0, context.Engine.Opened[1].TimelineOffsetTicks);
        Assert.Equal(600_000_001, context.Engine.Opened[1].InitialPositionTicks);
        Assert.NotEqual(context.Engine.Opened[0].PlaybackId, context.Engine.Opened[1].PlaybackId);
        Assert.Equal(context.Engine.Opened[0].PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Equal("session-2", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
        Assert.True(context.Coordinator.ActiveContext?.Selection.ForceTranscoding);
    }

    [Fact(Timeout = 15000)]
    public async Task Unsupported_transcode_open_ends_after_one_fallback_without_retrying_forever()
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("UnsupportedFormat");

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));

        Assert.Equal("UnsupportedFormat", error.ErrorCode);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(2, context.Engine.Stopped.Length);
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Single(context.Handler.At("Videos/ActiveEncodings"));
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
    }

    [Fact(Timeout = 15000)]
    public async Task Runtime_format_failure_falls_back_at_the_last_known_absolute_position()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        var replacementStarted = PlaybackLifecycleTests.Signal();
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Playing && args.Context?.PlaySessionId == "session-2")
                replacementStarted.TrySetResult();
        };

        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed, PositionTicks = 755_000_013 }, "UnsupportedFormat");
        await replacementStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(0, context.Engine.Opened[1].TimelineOffsetTicks);
        Assert.Equal(755_000_013, context.Engine.Opened[1].InitialPositionTicks);
        Assert.True(Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("Failed").GetBoolean());
        Assert.Equal("session-2", context.Coordinator.ActiveContext?.PlaySessionId);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, context.Coordinator.ActiveContext?.DeliveryMethod);
    }

    [Fact(Timeout = 15000)]
    public async Task A_progressive_url_with_a_different_trim_offset_is_rejected_before_engine_open()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.ProgressiveSource() with { TranscodingUrl = "/emby/Videos/movie-a/stream.mp4?StartTimeTicks=0" }];

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(600_000_001, transcode: true), TestContext.Current.CancellationToken));

        Assert.Equal("UnexpectedTranscodeTimeline", error.ErrorCode);
        Assert.Empty(context.Engine.Opened);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
    }

    [Fact(Timeout = 15000)]
    public async Task An_explicit_audio_choice_requires_transcoding_even_when_the_server_makes_it_the_new_default()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { DefaultAudioStreamIndex = 2 }];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with { AudioStreamIndex = 2 }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(2, request.AudioStreamIndex);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.False(Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo")).JsonBody.GetProperty("EnableDirectStream").GetBoolean());
    }

    [Fact(Timeout = 15000)]
    public async Task Default_tracks_remain_implicit_when_only_quality_changes()
    {
        await using var context = new PlaybackTestContext();
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MaxStreamingBitrate = 8_000_000 }, TestContext.Current.CancellationToken);

        var body = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.False(body.TryGetProperty("AudioStreamIndex", out _));
        Assert.False(body.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.True(body.GetProperty("EnableDirectStream").GetBoolean());
        Assert.Equal(1, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal("QualityChange", Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody.GetProperty("EventName").GetString());
    }

    [Fact(Timeout = 15000)]
    public async Task An_opened_live_source_is_closed_when_its_returned_capabilities_cannot_be_played()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [new MediaSourceInfo { Id = "unopened-source", RequiresOpening = true, OpenToken = "open-token" }];
        context.Handler.RespondAsync = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/emby/LiveStreams/Open"
            ? RecordingHandler.OpenedSource(new MediaSourceInfo
            {
                Id = "opened-source",
                RequiresClosing = true,
                LiveStreamId = "opened-but-unplayable",
                SupportsDirectStream = false,
                SupportsTranscoding = false
            }) : context.Respond(request));

        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection() with { ItemId = "1234" }, TestContext.Current.CancellationToken));

        Assert.Single(context.Handler.At("LiveStreams/Open"));
        Assert.Equal("opened-but-unplayable", Assert.Single(context.Handler.At("LiveStreams/Close")).Query["LiveStreamId"]);
        Assert.Empty(context.Engine.Opened);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Null(context.Coordinator.ActiveContext);
    }

    [Theory(Timeout = 15000)]
    [InlineData("HDR10")]
    [InlineData("HLG")]
    [InlineData("FutureVideoRange")]
    public async Task Explicit_non_sdr_metadata_requires_fresh_transcoding_with_stream_copy_disabled(string videoRange)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) =>
        {
            var source = PlaybackTestContext.Source();
            return [source with { MediaStreams = source.MediaStreams.Select(stream => stream.Type == "Video" ? stream with { VideoRange = videoRange } : stream).ToArray() }];
        };

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);

        var engine = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, engine.DeliveryMethod);
        Assert.Equal(0, engine.TimelineOffsetTicks);
        Assert.Equal(600_000_001, engine.InitialPositionTicks);
        Assert.Equal("/emby/Videos/movie-a/master.m3u8", engine.MediaUri.AbsolutePath);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        var retry = negotiations[1].JsonBody;
        Assert.False(retry.GetProperty("EnableDirectStream").GetBoolean());
        Assert.False(retry.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.False(retry.GetProperty("AllowAudioStreamCopy").GetBoolean());
        Assert.True(retry.GetProperty("EnableTranscoding").GetBoolean());
        Assert.Equal("session-2", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
    }

    [Theory(Timeout = 15000)]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SDR")]
    public async Task Missing_or_sdr_metadata_keeps_the_baseline_without_advertising_an_unverified_video_range_condition(string? videoRange)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) =>
        {
            var source = PlaybackTestContext.Source();
            return [source with { MediaStreams = source.MediaStreams.Select(stream => stream.Type == "Video" ? stream with { VideoRange = videoRange } : stream).ToArray() }];
        };

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);

        Assert.Equal(PlaybackDeliveryMethod.DirectStream, Assert.Single(context.Engine.Opened).DeliveryMethod);
        var negotiation = Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo")).JsonBody;
        var profile = negotiation.GetProperty("DeviceProfile");
        var conditions = profile.GetProperty("CodecProfiles").EnumerateArray()
            .SelectMany(codec => codec.GetProperty("Conditions").EnumerateArray()).ToArray();
        Assert.DoesNotContain(conditions, condition => condition.GetProperty("Property").GetString() == "VideoRange");
        var bitDepth = Assert.Single(conditions, condition => condition.GetProperty("Property").GetString() == "VideoBitDepth");
        Assert.True(bitDepth.GetProperty("IsRequired").GetBoolean());
        Assert.Equal("8", bitDepth.GetProperty("Value").GetString());
        Assert.False(negotiation.GetProperty("EnableDirectPlay").GetBoolean());
        Assert.Equal("h264", Assert.Single(profile.GetProperty("DirectPlayProfiles").EnumerateArray()).GetProperty("VideoCodec").GetString());
    }
}
