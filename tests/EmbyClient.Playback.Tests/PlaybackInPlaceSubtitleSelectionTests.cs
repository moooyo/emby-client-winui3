using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackInPlaceSubtitleSelectionTests
{
    private const int SubtitleIndex = 3;
    private const long InitialPositionTicks = 600_000_001;
    private const long PositionTicks = 800_000_009;

    [Fact(Timeout = 15000)]
    public async Task Turning_off_and_reselecting_the_same_bitmap_track_keeps_the_paused_playback_and_server_session()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource()];
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) => Task.FromResult(true);
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        context.Engine.SetPosition(PositionTicks);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
            TestContext.Current.CancellationToken);

        AssertUnchangedPausedPlayback(context, original.PlaybackId, -1);
        var offReport = Assert.Single(SubtitleReports(context));
        Assert.Equal(-1, offReport.JsonBody.GetProperty("SubtitleStreamIndex").GetInt32());

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = SubtitleIndex },
            TestContext.Current.CancellationToken);
        await context.Coordinator.SetVolumeAsync(65, false, TestContext.Current.CancellationToken);

        AssertUnchangedPausedPlayback(context, original.PlaybackId, SubtitleIndex);
        Assert.Equal(new[] { (original.PlaybackId, -1), (original.PlaybackId, SubtitleIndex) },
            context.Engine.SelectedEmbeddedBitmapSubtitles);
        Assert.Single(context.Engine.Opened);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Paused));
        Assert.Empty(context.Engine.Stopped);
        Assert.Empty(context.Engine.Sought);
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        var start = Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody;
        Assert.Equal("session-1", start.GetProperty("PlaySessionId").GetString());
        Assert.Equal(SubtitleIndex, start.GetProperty("SubtitleStreamIndex").GetInt32());
        var subtitleReports = SubtitleReports(context);
        Assert.Equal(new[] { -1, SubtitleIndex }, subtitleReports
            .Select(report => report.JsonBody.GetProperty("SubtitleStreamIndex").GetInt32()));
        Assert.All(subtitleReports, report =>
        {
            Assert.Equal("session-1", report.JsonBody.GetProperty("PlaySessionId").GetString());
            Assert.Equal("source-a", report.JsonBody.GetProperty("MediaSourceId").GetString());
            Assert.Equal(PositionTicks, report.JsonBody.GetProperty("PositionTicks").GetInt64());
            Assert.True(report.JsonBody.GetProperty("IsPaused").GetBoolean());
        });
        var volumeReport = Assert.Single(context.Handler.At("Sessions/Playing/Progress"),
            report => report.JsonBody.GetProperty("EventName").GetString() == "VolumeChange");
        Assert.Equal(SubtitleIndex, volumeReport.JsonBody.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Empty(context.Handler.At("Sessions/Playing/Stopped"));
    }

    [Fact(Timeout = 15000)]
    public async Task An_engine_without_in_place_support_renegotiates_and_restores_the_paused_position()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource()];
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        context.Engine.SetPosition(PositionTicks);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
            TestContext.Current.CancellationToken);

        Assert.Equal((original.PlaybackId, -1), Assert.Single(context.Engine.SelectedEmbeddedBitmapSubtitles));
        Assert.Equal(2, context.Engine.Opened.Length);
        var replacement = context.Engine.Opened[1];
        Assert.NotEqual(original.PlaybackId, replacement.PlaybackId);
        Assert.Equal(PositionTicks, replacement.InitialPositionTicks);
        Assert.Equal(-1, replacement.SubtitleStreamIndex);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Equal(2, context.Handler.At("Sessions/Playing").Length);
        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", PositionTicks);
        var report = Assert.Single(SubtitleReports(context)).JsonBody;
        Assert.Equal("session-2", report.GetProperty("PlaySessionId").GetString());
        Assert.Equal(-1, report.GetProperty("SubtitleStreamIndex").GetInt32());
    }

    [Fact(Timeout = 15000)]
    public async Task A_declined_in_place_attempt_restarts_at_the_latest_confirmed_engine_position()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource()];
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) =>
        {
            context.Engine.SetPosition(PositionTicks);
            return Task.FromResult(false);
        };

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(PositionTicks, context.Engine.Opened[1].InitialPositionTicks);
        Assert.Equal(PositionTicks, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(PositionTicks, Assert.Single(context.Handler.At("Sessions/Playing/Stopped"))
            .JsonBody.GetProperty("PositionTicks").GetInt64());
    }

    [Theory(Timeout = 15000)]
    [InlineData("Quality")]
    [InlineData("Audio")]
    [InlineData("Source")]
    [InlineData("Capability")]
    public async Task A_subtitle_change_combined_with_other_selection_changes_uses_new_negotiation(string changeKind)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource(), OriginalSource(id: "source-b")];
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) => Task.FromResult(true);
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        context.Engine.SetPosition(PositionTicks);
        var change = new PlaybackSelectionChange { SubtitleStreamIndex = -1 };
        change = changeKind switch
        {
            "Quality" => change with { MaxStreamingBitrate = 4_000_000 },
            "Audio" => change with { AudioStreamIndex = 2 },
            "Source" => change with { MediaSourceId = "source-b" },
            "Capability" => change with { NativeEmbeddedSubtitlesEligible = false },
            _ => throw new ArgumentOutOfRangeException(nameof(changeKind))
        };

        await context.Coordinator.ChangeSelectionAsync(change, TestContext.Current.CancellationToken);

        Assert.Empty(context.Engine.SelectedEmbeddedBitmapSubtitles);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.NotEqual(original.PlaybackId, active.PlaybackId);
        Assert.Equal("session-2", active.PlaySessionId);
        Assert.Equal(PositionTicks, active.PositionTicks);
        Assert.Equal(-1, active.Selection.SubtitleStreamIndex);
        switch (changeKind)
        {
            case "Quality": Assert.Equal(4_000_000, active.Selection.MaxStreamingBitrate); break;
            case "Audio": Assert.Equal(2, active.Selection.AudioStreamIndex); break;
            case "Source": Assert.Equal("source-b", active.Source.Id); break;
            case "Capability": Assert.False(active.Selection.NativeEmbeddedSubtitlesEligible); break;
        }
    }

    [Theory(Timeout = 15000)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task Text_external_ass_and_unknown_bitmap_tracks_do_not_use_the_bitmap_selection_command(int subtitleIndex)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) => [OriginalSource()];
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) => Task.FromResult(true);
        await context.Coordinator.PlayAsync(NativeSelection() with { NativeAssSubtitlesEligible = true },
            TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = subtitleIndex },
            TestContext.Current.CancellationToken);

        Assert.Empty(context.Engine.SelectedEmbeddedBitmapSubtitles);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Equal(subtitleIndex, context.Engine.Opened[1].SubtitleStreamIndex);
        Assert.Equal(subtitleIndex, context.Coordinator.ActiveContext?.Selection.SubtitleStreamIndex);
        Assert.Equal("session-2", context.Coordinator.ActiveContext?.PlaySessionId);
    }

    [Fact(Timeout = 15000)]
    public async Task Turning_off_external_subtitles_reopens_instead_of_using_the_native_bitmap_command()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) => [OriginalSource()];
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) => Task.FromResult(true);
        await context.Coordinator.PlayAsync(NativeSelection() with { SubtitleStreamIndex = 5 },
            TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        Assert.NotNull(original.ExternalSubtitleUri);

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
            TestContext.Current.CancellationToken);

        Assert.Empty(context.Engine.SelectedEmbeddedBitmapSubtitles);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Null(context.Engine.Opened[1].ExternalSubtitleUri);
        Assert.Equal(-1, context.Engine.Opened[1].SubtitleStreamIndex);
    }

    [Fact(Timeout = 15000)]
    public async Task A_failed_in_place_selection_retires_the_engine_without_reopening_the_old_source()
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [OriginalSource()];
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        context.Engine.SetPosition(PositionTicks);
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) =>
            Task.FromException<bool>(new PlaybackException("NativeSubtitleSelectionFailed"));

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.ChangeSelectionAsync(
            new PlaybackSelectionChange { SubtitleStreamIndex = -1 }, TestContext.Current.CancellationToken));

        Assert.Equal("NativeSubtitleSelectionFailed", error.ErrorCode);
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Single(context.Engine.Opened);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Empty(SubtitleReports(context));
        var stopped = Assert.Single(context.Handler.At("Sessions/Playing/Stopped")).JsonBody;
        Assert.Equal("session-1", stopped.GetProperty("PlaySessionId").GetString());
        Assert.Equal(PositionTicks, stopped.GetProperty("PositionTicks").GetInt64());
        Assert.True(stopped.GetProperty("Failed").GetBoolean());
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_a_pending_in_place_selection_retires_the_current_session_without_new_negotiation()
    {
        await using var context = new PlaybackTestContext();
        using var selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Sources = (_, _) => [OriginalSource()];
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        var entered = PlaybackLifecycleTests.Signal();
        CancellationToken nativeToken = default;
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = async (_, _, token) =>
        {
            nativeToken = token;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return true;
        };

        var change = context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
            selectionCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        selectionCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);

        Assert.True(nativeToken.IsCancellationRequested);
        Assert.Equal(PlaybackStatus.Idle, context.Coordinator.Status);
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Single(context.Engine.Opened);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
        Assert.Empty(SubtitleReports(context));
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_the_playback_owner_during_a_declined_in_place_selection_does_not_reopen_with_the_selection_token()
    {
        await using var context = new PlaybackTestContext();
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var selectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Sources = (_, _) => [OriginalSource()];
        await context.Coordinator.PlayAsync(NativeSelection(), owner.Token);
        var original = Assert.Single(context.Engine.Opened);
        var entered = PlaybackLifecycleTests.Signal();
        var release = PlaybackLifecycleTests.Signal();
        CancellationToken nativeToken = default;
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = async (_, _, token) =>
        {
            nativeToken = token;
            entered.TrySetResult();
            await release.Task;
            return false;
        };

        try
        {
            var change = context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
                selectionCancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            owner.Cancel();
            Assert.True(nativeToken.IsCancellationRequested);
            Assert.False(selectionCancellation.IsCancellationRequested);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);

            Assert.Equal(PlaybackStatus.Idle, context.Coordinator.Status);
            Assert.Null(context.Coordinator.ActiveContext);
            Assert.False(selectionCancellation.IsCancellationRequested);
            Assert.Single(context.Engine.Opened);
            Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
            Assert.Equal((original.PlaybackId, -1), Assert.Single(context.Engine.SelectedEmbeddedBitmapSubtitles));
            Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
            Assert.Single(context.Handler.At("Sessions/Playing"));
            Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
            Assert.Empty(SubtitleReports(context));
        }
        finally { release.TrySetResult(); }
    }

    [Fact(Timeout = 15000)]
    public async Task A_successful_in_place_selection_keeps_the_original_playback_owner_cancellation_scope()
    {
        await using var context = new PlaybackTestContext();
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Sources = (_, _) => [OriginalSource()];
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = (_, _, _) => Task.FromResult(true);
        await context.Coordinator.PlayAsync(NativeSelection(), owner.Token);
        var original = Assert.Single(context.Engine.Opened);
        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 });
        var retired = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Idle);

        owner.Cancel();
        await retired.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Single(context.Engine.Opened);
        Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
        Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
        Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_play_wins_even_when_a_cancelled_bitmap_selection_returns_late(bool selectionSucceeded)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (request, _) => request.Uri.AbsolutePath == "/emby/Items/movie-b/PlaybackInfo"
            ? [OriginalSource("movie-b", "source-b")] : [OriginalSource()];
        await context.Coordinator.PlayAsync(NativeSelection(), TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        var entered = PlaybackLifecycleTests.Signal();
        var release = PlaybackLifecycleTests.Signal();
        CancellationToken nativeToken = default;
        context.Engine.OnTrySelectEmbeddedBitmapSubtitleAsync = async (_, _, token) =>
        {
            nativeToken = token;
            entered.TrySetResult();
            await release.Task;
            return selectionSucceeded;
        };

        try
        {
            var change = context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = -1 },
                TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            const long replacementPositionTicks = 111_000_003;
            var replacement = context.Coordinator.PlayAsync(NativeSelection() with
            {
                ItemId = "movie-b",
                MediaSourceId = "source-b",
                StartPositionTicks = replacementPositionTicks
            }, TestContext.Current.CancellationToken);
            Assert.True(nativeToken.IsCancellationRequested);
            release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
            await replacement.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(2, context.Engine.Opened.Length);
            Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
            Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
            Assert.Single(context.Handler.At("Items/movie-b/PlaybackInfo"));
            Assert.Equal((original.PlaybackId, -1), Assert.Single(context.Engine.SelectedEmbeddedBitmapSubtitles));
            Assert.Empty(SubtitleReports(context));
            var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
            Assert.Equal("movie-b", active.Selection.ItemId);
            Assert.Equal("source-b", active.Source.Id);
            Assert.Equal("session-2", active.PlaySessionId);
            Assert.Equal(replacementPositionTicks, active.PositionTicks);
            Assert.NotEqual(original.PlaybackId, active.PlaybackId);
            Assert.Equal(SubtitleIndex, active.Selection.SubtitleStreamIndex);
            Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        }
        finally { release.TrySetResult(); }
    }

    private static PlaybackSelection NativeSelection() => PlaybackTestContext.Selection(InitialPositionTicks) with
    {
        MediaSourceId = "source-a",
        SubtitleStreamIndex = SubtitleIndex,
        NativeEmbeddedSubtitlesEligible = true
    };

    private static MediaSourceInfo OriginalSource(string itemId = "movie-a", string id = "source-a") =>
        PlaybackTestContext.Source(id) with
        {
            Container = "mkv",
            DirectStreamUrl = $"/emby/Videos/{itemId}/stream.mkv?Static=true&MediaSourceId={id}",
            TranscodingUrl = $"/emby/Videos/{itemId}/master.m3u8?MediaSourceId={id}",
            DefaultSubtitleStreamIndex = SubtitleIndex,
            MediaStreams =
            [
                new() { Index = 0, Type = "Video", Codec = "h264", BitDepth = 8, Profile = "High", IsInterlaced = false, VideoRange = "SDR" },
                new() { Index = 1, Type = "Audio", Codec = "aac", Channels = 2 },
                new() { Index = 2, Type = "Audio", Codec = "aac", Channels = 2 },
                new() { Index = SubtitleIndex, Type = "Subtitle", Codec = "pgssub", DeliveryMethod = "Embed", IsExternal = false, IsTextSubtitleStream = false },
                new() { Index = 4, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode", IsExternal = false, IsTextSubtitleStream = true },
                new() { Index = 5, Type = "Subtitle", Codec = "vtt", DeliveryMethod = "External", IsExternal = true, IsTextSubtitleStream = true },
                new() { Index = 6, Type = "Subtitle", Codec = "ass", DeliveryMethod = "Embed", IsExternal = false, IsTextSubtitleStream = true },
                new() { Index = 7, Type = "Subtitle", Codec = "future-bitmap", DeliveryMethod = "Encode", IsExternal = false, IsTextSubtitleStream = false }
            ]
        };

    private static RecordedRequest[] SubtitleReports(PlaybackTestContext context) =>
        context.Handler.At("Sessions/Playing/Progress")
            .Where(report => report.JsonBody.GetProperty("EventName").GetString() == "SubtitleTrackChange").ToArray();

    private static void AssertUnchangedPausedPlayback(PlaybackTestContext context, Guid playbackId, int subtitleIndex)
    {
        var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.Equal(playbackId, active.PlaybackId);
        Assert.Equal("session-1", active.PlaySessionId);
        Assert.Equal("source-a", active.Source.Id);
        Assert.Equal(subtitleIndex, active.Selection.SubtitleStreamIndex);
        Assert.Equal(PositionTicks, active.PositionTicks);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, active.DeliveryMethod);
        Assert.Equal(PlaybackTimelineKind.FullSource, active.TimelineKind);
        Assert.Equal(0, active.TimelineOffsetTicks);
        Assert.Equal(PlaybackStatus.Paused, context.Coordinator.Status);
        Assert.Equal(playbackId, context.Engine.Snapshot?.PlaybackId);
        Assert.Equal(PositionTicks, context.Engine.Snapshot?.PositionTicks);
        Assert.Equal(PlaybackEngineState.Paused, context.Engine.Snapshot?.State);
    }
}
