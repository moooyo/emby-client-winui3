using System.Globalization;
using System.Net;
using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackTimelineTests
{
    // Sanitized HTTP media-playlist structure observed from official Emby Server 4.9.5.0.
    // Its ffmpeg disk playlist omitted VOD/START tags; the HTTP response added them without trimming segments.
    private const string CompleteVodPlaylist = """
        #EXTM3U
        #EXT-X-VERSION:3
        #EXT-X-PLAYLIST-TYPE:VOD
        #EXT-X-MEDIA-SEQUENCE:0
        #EXT-X-START:TIME-OFFSET=17
        #EXT-X-ALLOW-CACHE:YES
        #EXT-X-TARGETDURATION:3
        #EXTINF:3.000000,
        0.ts
        #EXTINF:3.000000,
        1.ts
        #EXTINF:3.000000,
        2.ts
        #EXTINF:3.000000,
        3.ts
        #EXTINF:3.000000,
        4.ts
        #EXTINF:3.000000,
        5.ts
        #EXTINF:3.000000,
        6.ts
        #EXTINF:3.000000,
        7.ts
        #EXTINF:3.000000,
        8.ts
        #EXTINF:3.000000,
        9.ts
        #EXTINF:3.000000,
        10.ts
        #EXTINF:3.000000,
        11.ts
        #EXTINF:3.000000,
        12.ts
        #EXTINF:3.000000,
        13.ts
        #EXTINF:3.000000,
        14.ts
        #EXTINF:3.000000,
        15.ts
        #EXTINF:3.000000,
        16.ts
        #EXTINF:3.000000,
        17.ts
        #EXTINF:3.000000,
        18.ts
        #EXTINF:3.000000,
        19.ts
        #EXTINF:0.100000,
        20.ts
        #EXT-X-ENDLIST
        """;

    [Fact(Timeout = 15000)]
    public async Task Complete_emby_vod_hls_seeks_the_engine_to_segment_five_instead_of_adding_a_display_offset()
    {
        await using var context = new PlaybackTestContext();
        const long requestedTicks = 170_000_000;
        const long observedTicks = 176_430_000;
        context.Sources = (_, _) => [ObservedVodSource("master.m3u8", requestedTicks)];
        var segmentDurations = CompleteVodSegmentDurations();
        Assert.Equal(21, segmentDurations.Length);
        Assert.Equal(601_000_000, segmentDurations.Sum());
        Assert.Equal(0, SegmentAt(segmentDurations, 0));
        context.Engine.OnOpenAsync = (request, _) =>
        {
            Assert.Equal(PlaybackTimelineKind.FullSource, request.TimelineKind);
            Assert.Equal(0, request.TimelineOffsetTicks);
            Assert.Equal(requestedTicks, request.InitialPositionTicks);
            Assert.Equal(5, SegmentAt(segmentDurations, request.InitialPositionTicks));
            return Task.CompletedTask;
        };

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(requestedTicks, transcode: true) with
        {
            MediaSourceId = "8",
            AudioStreamIndex = 2
        }, TestContext.Current.CancellationToken);
        var nativeRequest = Assert.Single(context.Engine.Opened);
        Assert.Equal("170000000", new RecordedRequest(HttpMethod.Get, nativeRequest.MediaUri, null).Query["StartTimeTicks"]);
        Assert.Equal("8", new RecordedRequest(HttpMethod.Get, nativeRequest.MediaUri, null).Query["MediaSourceId"]);
        Assert.Equal(2, nativeRequest.AudioStreamIndex);
        Assert.Equal(PlaybackTimelineKind.FullSource, context.Coordinator.ActiveContext?.TimelineKind);
        context.Engine.SetPosition(observedTicks);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        await context.Coordinator.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(requestedTicks, Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(observedTicks, Assert.Single(context.Handler.At("Sessions/Playing/Progress")).JsonBody.GetProperty("PositionTicks").GetInt64());
        Assert.Equal(observedTicks, Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Theory(Timeout = 15000)]
    [InlineData("master.m3u8", null)]
    [InlineData("master.m3u8", "0")]
    [InlineData("main.m3u8", "170000000")]
    public async Task An_emby_hls_start_hint_is_preserved_without_becoming_a_trim_offset(string playlistName, string? serverHint)
    {
        await using var context = new PlaybackTestContext();
        const long target = 175_666_667;
        var returnedUrl = $"/emby/Videos/movie-a/{playlistName}?MediaSourceId=8&AudioStreamIndex=2"
            + (serverHint is null ? string.Empty : "&StartTimeTicks=" + serverHint);
        context.Sources = (_, _) => [ObservedVodSource() with
        {
            TranscodingUrl = returnedUrl,
            ContainerStartTimeTicks = 100_667_000
        }];

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(target, transcode: true), TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(returnedUrl, request.MediaUri.PathAndQuery);
        Assert.Equal(target, request.InitialPositionTicks);
        Assert.Equal(0, request.TimelineOffsetTicks);
        Assert.Equal(target, context.Coordinator.ActiveContext?.PositionTicks);
    }

    [Fact(Timeout = 15000)]
    public async Task A_seekable_paused_hls_vod_seeks_the_full_timeline_without_replacing_its_server_session()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [ObservedVodSource()];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(170_000_000, transcode: true), TestContext.Current.CancellationToken);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.SeekAsync(450_000_009, TestContext.Current.CancellationToken);

        Assert.Single(context.Engine.Opened);
        Assert.Single(context.Engine.Paused);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Equal(450_000_009, Assert.Single(context.Engine.Sought).PositionTicks);
        Assert.Equal(PlaybackStatus.Paused, context.Coordinator.Status);
        Assert.Equal(450_000_009, context.Coordinator.ActiveContext?.PositionTicks);
        var seekReport = context.Handler.At("Sessions/Playing/Progress").Last().JsonBody;
        Assert.Equal("TimeUpdate", seekReport.GetProperty("EventName").GetString());
        Assert.Equal(450_000_009, seekReport.GetProperty("PositionTicks").GetInt64());
        Assert.True(seekReport.GetProperty("IsPaused").GetBoolean());
    }

    [Fact(Timeout = 15000)]
    public async Task Restarting_an_hls_vod_still_requires_an_actual_full_source_initial_seek_and_restores_pause()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) => [ObservedVodSource("master.m3u8", request.JsonBody.GetProperty("StartTimeTicks").GetInt64())];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(170_000_000, transcode: true), TestContext.Current.CancellationToken);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        context.Engine.Emit(PlaybackEngineEventKind.StateChanged, context.Engine.Snapshot! with { CanSeek = false });

        await context.Coordinator.SeekAsync(450_000_009, TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        var replacement = context.Engine.Opened[1];
        Assert.Equal(PlaybackTimelineKind.FullSource, replacement.TimelineKind);
        Assert.Equal(0, replacement.TimelineOffsetTicks);
        Assert.Equal(450_000_009, replacement.InitialPositionTicks);
        Assert.Empty(context.Engine.Sought);
        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", 450_000_009);
    }

    [Theory(Timeout = 15000)]
    [InlineData("master.m3u8")]
    [InlineData("main.m3u8")]
    public async Task An_alternate_version_can_use_its_parent_video_route_without_changing_selected_item_or_timeline(string playlist)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [ObservedVodSource() with
        {
            TranscodingUrl = $"/emby/videos/7/{playlist}?MediaSourceId=8&AudioStreamIndex=2&StartTimeTicks=175666667"
        }];

        await context.Coordinator.PlayAsync(new PlaybackSelection
        {
            ItemId = "8", MediaSourceId = "8", AudioStreamIndex = 2,
            StartPositionTicks = 175_666_667, ForceTranscoding = true
        }, TestContext.Current.CancellationToken);

        Assert.Single(context.Handler.At("Items/8/PlaybackInfo"));
        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal($"/emby/videos/7/{playlist}", request.MediaUri.AbsolutePath);
        Assert.Equal("8", request.Source.Id);
        Assert.Equal(PlaybackTimelineKind.FullSource, request.TimelineKind);
        Assert.Equal(0, request.TimelineOffsetTicks);
        Assert.Equal(175_666_667, request.InitialPositionTicks);
        var started = Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody;
        Assert.Equal("8", started.GetProperty("ItemId").GetString());
        Assert.Equal("8", started.GetProperty("MediaSourceId").GetString());
        Assert.Equal(175_666_667, started.GetProperty("PositionTicks").GetInt64());
    }

    [Theory(Timeout = 15000)]
    [InlineData("https://cdn.example/Videos/movie-a/master.m3u8", "hls", false, 600340000L)]
    [InlineData("https://cdn.example/Videos/movie-a/stream.mp4", "http", false, 600340000L)]
    [InlineData("/emby/Other/movie-a/master.m3u8", "hls", false, 600340000L)]
    [InlineData("/emby/Videos/7/master.m3u8/extra", "hls", false, 600340000L)]
    [InlineData("/emby/Videos/a%2Fb/master.m3u8", "hls", false, 600340000L)]
    [InlineData("/outside/emby/Videos/7/master.m3u8", "hls", false, 600340000L)]
    [InlineData("/emby/Videos/movie-a/master.m3u8", "hls", true, 600340000L)]
    [InlineData("/emby/Videos/movie-a/master.m3u8", "hls", false, null)]
    [InlineData("/emby/Videos/movie-a/master.m3u8", "dash", false, 600340000L)]
    [InlineData("/emby/Videos/movie-a/stream.mp4/nested", "http", false, 600340000L)]
    [InlineData("/emby/Videos/movie-a/stream.mp4?Static=true", "http", false, 600340000L)]
    public async Task Unestablished_transcode_timeline_semantics_are_rejected_before_opening_the_engine(
        string url, string protocol, bool infinite, long? durationTicks)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with
        {
            TranscodingUrl = url,
            TranscodingSubProtocol = protocol,
            IsInfiniteStream = infinite,
            RunTimeTicks = durationTicks
        }];

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(170_000_000, transcode: true), TestContext.Current.CancellationToken));

        Assert.Equal("UnknownTranscodeTimeline", error.ErrorCode);
        Assert.Empty(context.Engine.Opened);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
    }

    [Theory(Timeout = 15000)]
    [InlineData("StartTimeTicks=-1")]
    [InlineData("StartTimeTicks=invalid")]
    [InlineData("StartTimeTicks=1&startTimeTicks=2")]
    [InlineData("CopyTimestamps=unknown")]
    public async Task Malformed_or_ambiguous_timeline_parameters_are_rejected(string query)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [ObservedVodSource() with { TranscodingUrl = "/emby/Videos/movie-a/master.m3u8?" + query }];
        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(170_000_000, transcode: true), TestContext.Current.CancellationToken));
        Assert.Equal("UnexpectedTranscodeTimeline", error.ErrorCode);
        Assert.Empty(context.Engine.Opened);
    }

    private static MediaSourceInfo ObservedVodSource(string playlist = "master.m3u8", long startTicks = 170_000_000) => PlaybackTestContext.Source("8") with
    {
        Protocol = "File",
        TranscodingUrl = $"/emby/Videos/movie-a/{playlist}?MediaSourceId=8&AudioStreamIndex=2&StartTimeTicks={startTicks.ToString(CultureInfo.InvariantCulture)}",
        TranscodingSubProtocol = "hls",
        TranscodingContainer = "ts",
        RunTimeTicks = 600_340_000,
        IsInfiniteStream = false,
        RequiresOpening = false,
        RequiresClosing = false,
        DefaultAudioStreamIndex = 2
    };

    private static long[] CompleteVodSegmentDurations() => CompleteVodPlaylist.Split('\n')
        .Select(line => line.Trim()).Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))
        .Select(line => decimal.ToInt64(decimal.Parse(line[8..].TrimEnd(','), CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond)).ToArray();

    private static int SegmentAt(long[] durations, long positionTicks)
    {
        long start = 0;
        for (var index = 0; index < durations.Length; index++)
        {
            if (positionTicks < start + durations[index]) return index;
            start += durations[index];
        }
        throw new ArgumentOutOfRangeException(nameof(positionTicks));
    }
}
