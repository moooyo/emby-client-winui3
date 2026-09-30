using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackSubtitleFallbackTests
{
    [Fact(Timeout = 15000)]
    public async Task Explicit_video_conversion_keeps_authenticated_external_webvtt_capabilities()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.NotNull(request.ExternalSubtitleUri);
        Assert.Equal("test-token", request.ExternalSubtitleHeaders["X-Emby-Token"]);
        Assert.True(HasExternalProfile(Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"))));
        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceTranscoding);
        Assert.False(context.Coordinator.ActiveContext.Selection.ForceSubtitleBurnIn);
    }

    [Theory(Timeout = 15000)]
    [InlineData("UnsupportedFormat")]
    [InlineData("UnsupportedTrack")]
    public async Task A_video_fallback_preserves_external_text_instead_of_requesting_subtitle_burn_in(string errorCode)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);
        context.Engine.OnOpenAsync = (request, _) => request.DeliveryMethod == PlaybackDeliveryMethod.DirectStream
            ? Task.FromException(new PlaybackException(errorCode)) : Task.CompletedTask;

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_003) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.All(context.Engine.Opened, request => Assert.NotNull(request.ExternalSubtitleUri));
        Assert.All(context.Handler.At("Items/movie-a/PlaybackInfo"), request => Assert.True(HasExternalProfile(request)));
        Assert.Equal(PlaybackDeliveryMethod.Transcode, context.Engine.Opened[1].DeliveryMethod);
        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceTranscoding);
        Assert.False(context.Coordinator.ActiveContext.Selection.ForceSubtitleBurnIn);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_external_subtitle_failure_has_one_independent_burn_in_fallback_even_when_video_is_already_forced(bool forceVideo)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);
        FailExternalSubtitles(context);

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(700_000_003, forceVideo) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.NotNull(context.Engine.Opened[0].ExternalSubtitleUri);
        Assert.Null(context.Engine.Opened[1].ExternalSubtitleUri);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, context.Engine.Opened[1].DeliveryMethod);
        Assert.Equal(700_000_003, context.Engine.Opened[1].InitialPositionTicks);
        Assert.Equal(forceVideo, context.Coordinator.ActiveContext!.Selection.ForceTranscoding);
        Assert.True(context.Coordinator.ActiveContext.Selection.ForceSubtitleBurnIn);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.True(HasExternalProfile(negotiations[0]));
        Assert.False(HasExternalProfile(negotiations[1]));
        Assert.False(negotiations[1].JsonBody.GetProperty("EnableDirectStream").GetBoolean());
        Assert.False(negotiations[1].JsonBody.GetProperty("AllowVideoStreamCopy").GetBoolean());
        Assert.Equal(!forceVideo, negotiations[1].JsonBody.GetProperty("AllowAudioStreamCopy").GetBoolean());
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_subtitle_failure_ends_after_the_burn_in_attempt_without_creating_network_recovery(bool forceVideo)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);
        context.Engine.OnOpenAsync = (_, _) => Task.FromException(new PlaybackException("UnsupportedSubtitle"));

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(transcode: forceVideo) with { SubtitleStreamIndex = 3 }, TestContext.Current.CancellationToken));

        Assert.Equal("UnsupportedSubtitle", error.ErrorCode);
        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(2, context.Engine.Stopped.Length);
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.NotNull(context.Engine.Opened[0].ExternalSubtitleUri);
        Assert.Null(context.Engine.Opened[1].ExternalSubtitleUri);
        Assert.Equal(PlaybackStatus.Failed, context.Coordinator.Status);
        Assert.Null(context.Coordinator.Recovery);
    }

    [Theory(Timeout = 15000)]
    [InlineData("UnsupportedFormat", "UnsupportedSubtitle")]
    [InlineData("UnsupportedSubtitle", "UnsupportedFormat")]
    [InlineData("UnsupportedTrack", "UnsupportedSubtitle")]
    [InlineData("UnsupportedSubtitle", "UnsupportedTrack")]
    public async Task Each_delivery_axis_can_fall_back_once_in_either_order(string firstError, string secondError)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);
        var attempts = 0;
        context.Engine.OnOpenAsync = (_, _) => ++attempts switch
        {
            1 => Task.FromException(new PlaybackException(firstError)),
            2 => Task.FromException(new PlaybackException(secondError)),
            _ => Task.CompletedTask
        };
        const long position = 720_000_007;

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(position) with
        {
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, context.Engine.Opened.Length);
        Assert.Equal(2, context.Engine.Stopped.Length);
        Assert.Equal(3, context.Engine.Opened.Select(request => request.PlaybackId).Distinct().Count());
        Assert.All(context.Engine.Opened, request => Assert.Equal(position, request.InitialPositionTicks));
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(3, negotiations.Length);
        Assert.All(negotiations.Skip(1), request => Assert.Equal("source-a", request.JsonBody.GetProperty("MediaSourceId").GetString()));
        Assert.True(HasExternalProfile(negotiations[0]));
        Assert.Equal(firstError != "UnsupportedSubtitle", HasExternalProfile(negotiations[1]));
        Assert.False(HasExternalProfile(negotiations[2]));
        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceTranscoding);
        Assert.True(context.Coordinator.ActiveContext.Selection.ForceSubtitleBurnIn);
        Assert.Equal("session-3", Assert.Single(context.Handler.At("Sessions/Playing")).JsonBody.GetProperty("PlaySessionId").GetString());
    }

    [Theory(Timeout = 15000)]
    [InlineData("UnsupportedFormat", "UnsupportedSubtitle", "UnsupportedFormat")]
    [InlineData("UnsupportedSubtitle", "UnsupportedFormat", "UnsupportedSubtitle")]
    public async Task Failure_after_both_fallbacks_does_not_start_a_fourth_session(string firstError, string secondError, string finalError)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);
        var attempts = 0;
        context.Engine.OnOpenAsync = (_, _) => Task.FromException(new PlaybackException(++attempts switch
        {
            1 => firstError,
            2 => secondError,
            _ => finalError
        }));

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection() with { SubtitleStreamIndex = 3 }, TestContext.Current.CancellationToken));

        Assert.Equal(finalError, error.ErrorCode);
        Assert.Equal(3, context.Engine.Opened.Length);
        Assert.Equal(3, context.Engine.Stopped.Length);
        Assert.Equal(3, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Null(context.Coordinator.Recovery);
    }

    [Fact(Timeout = 15000)]
    public async Task A_server_that_ignores_burn_in_capabilities_cannot_reattach_the_failed_external_track()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { DefaultSubtitleStreamIndex = 4 }];
        FailExternalSubtitles(context);

        var error = await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));

        Assert.Equal("UnsupportedSubtitle", error.ErrorCode);
        Assert.NotNull(Assert.Single(context.Engine.Opened).ExternalSubtitleUri);
        Assert.Single(context.Engine.Stopped);
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Empty(context.Handler.At("Sessions/Playing"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(null)]
    [InlineData("Embed")]
    [InlineData("Hls")]
    public async Task Requested_burn_in_never_accepts_a_direct_source_with_missing_or_inconsistent_subtitle_delivery(string? delivery)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (_, _) =>
        {
            var source = PlaybackTestContext.Source();
            return [source with
            {
                DefaultSubtitleStreamIndex = 3,
                TranscodingUrl = "/emby/Videos/movie-a/master.m3u8?SubtitleMethod=Encode",
                MediaStreams = source.MediaStreams.Select(stream => stream.Index == 3
                    ? stream with { DeliveryMethod = delivery } : stream).ToArray()
            }];
        };

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            SubtitleStreamIndex = 3,
            ForceSubtitleBurnIn = true
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.Contains("SubtitleMethod=Encode", request.MediaUri.Query, StringComparison.Ordinal);
        Assert.Null(request.ExternalSubtitleUri);
        Assert.False(HasExternalProfile(Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"))));
    }

    [Theory(Timeout = 15000)]
    [InlineData("HDR10")]
    [InlineData("HLG")]
    [InlineData("FutureVideoRange")]
    public async Task Subtitle_burn_in_does_not_bypass_the_conservative_non_sdr_video_guard(string range)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) =>
        {
            var source = NegotiatedSource(request);
            return [source with { MediaStreams = source.MediaStreams.Select(stream => stream.Type == "Video"
                ? stream with { VideoRange = range } : stream).ToArray() }];
        };

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            SubtitleStreamIndex = 3,
            ForceSubtitleBurnIn = true
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Null(request.ExternalSubtitleUri);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, request.DeliveryMethod);
        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceTranscoding);
        Assert.True(context.Coordinator.ActiveContext.Selection.ForceSubtitleBurnIn);
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
    }

    [Theory(Timeout = 15000)]
    [InlineData("Audio")]
    [InlineData("Quality")]
    [InlineData("SameSubtitle")]
    [InlineData("SameSource")]
    [InlineData("VideoConversion")]
    public async Task Changes_that_keep_the_actual_subtitle_track_preserve_its_burn_in_fallback(string kind)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context);
        FailExternalSubtitles(context);
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        Assert.Null(context.Coordinator.ActiveContext!.Selection.SubtitleStreamIndex);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.ChangeSelectionAsync(Change(kind), TestContext.Current.CancellationToken);

        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceSubtitleBurnIn);
        Assert.Null(context.Engine.Opened[^1].ExternalSubtitleUri);
        Assert.False(HasExternalProfile(context.Handler.At("Items/movie-a/PlaybackInfo")[^1]));
        Assert.Equal(3, context.Engine.Opened.Length);
    }

    [Theory(Timeout = 15000)]
    [InlineData("DifferentSubtitle")]
    [InlineData("DifferentSource")]
    [InlineData("Off")]
    public async Task A_different_subtitle_or_source_clears_the_old_tracks_burn_in_fallback(string kind)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context, twoSources: true);
        FailExternalSubtitles(context);
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.ChangeSelectionAsync(Change(kind), TestContext.Current.CancellationToken);

        Assert.False(context.Coordinator.ActiveContext!.Selection.ForceSubtitleBurnIn);
        Assert.True(HasExternalProfile(context.Handler.At("Items/movie-a/PlaybackInfo")[^1]));
        if (kind == "Off") Assert.Null(context.Engine.Opened[^1].ExternalSubtitleUri);
        else Assert.NotNull(context.Engine.Opened[^1].ExternalSubtitleUri);
        if (kind == "DifferentSource") Assert.Equal("source-b", context.Coordinator.ActiveContext.Source.Id);
    }

    [Fact(Timeout = 15000)]
    public async Task Restart_seeking_and_replay_preserve_subtitle_burn_in_without_retesting_the_failed_track()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context, progressive: true);
        FailExternalSubtitles(context);
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.SeekAsync(900_000_009, TestContext.Current.CancellationToken);

        Assert.Equal(PlaybackStatus.Paused, context.Coordinator.Status);
        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceSubtitleBurnIn);
        Assert.Equal(900_000_009, context.Engine.Opened[^1].TimelineOffsetTicks);
        Assert.Null(context.Engine.Opened[^1].ExternalSubtitleUri);
        Assert.Empty(context.Engine.Sought);

        await context.Coordinator.ReplayAsync(TestContext.Current.CancellationToken);

        Assert.True(context.Coordinator.ActiveContext!.Selection.ForceSubtitleBurnIn);
        Assert.Equal(0, context.Coordinator.ActiveContext.PositionTicks);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
        Assert.All(context.Handler.At("Items/movie-a/PlaybackInfo").Skip(1), request => Assert.False(HasExternalProfile(request)));
    }

    [Theory(Timeout = 15000)]
    [InlineData("SameSubtitle", true)]
    [InlineData("Quality", true)]
    [InlineData("DifferentSubtitle", false)]
    [InlineData("DifferentSource", false)]
    public async Task Edited_network_recovery_preserves_or_resets_the_actual_subtitle_scope_without_resolving_implicit_tracks(
        string kind, bool expectBurnIn)
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        ConfigureSources(context, twoSources: true);
        FailExternalSubtitles(context);
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);
        context.Engine.OnOpenAsync = null;
        var failed = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Failed);
        context.Engine.Emit(PlaybackEngineEventKind.Failed, context.Engine.Snapshot! with
        {
            State = PlaybackEngineState.Failed,
            PositionTicks = 555_000_007
        }, "NetworkFailure");
        await failed.WaitAsync(TestContext.Current.CancellationToken);
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        Assert.True(recovery.Selection.ForceSubtitleBurnIn);
        Assert.Null(recovery.Selection.AudioStreamIndex);
        Assert.Null(recovery.Selection.SubtitleStreamIndex);

        await context.Coordinator.RetryAsync(recovery.RecoveryId, Change(kind), TestContext.Current.CancellationToken);

        Assert.Equal(expectBurnIn, context.Coordinator.ActiveContext!.Selection.ForceSubtitleBurnIn);
        Assert.Equal(555_000_007, context.Coordinator.ActiveContext.PositionTicks);
        Assert.Equal(!expectBurnIn, context.Engine.Opened[^1].ExternalSubtitleUri is not null);
        Assert.Null(context.Coordinator.Recovery);
        var retry = context.Handler.At("Items/movie-a/PlaybackInfo")[^1].JsonBody;
        Assert.False(retry.TryGetProperty("AudioStreamIndex", out _));
        if (kind is "Quality" or "DifferentSource") Assert.False(retry.TryGetProperty("SubtitleStreamIndex", out _));
    }

    [Fact(Timeout = 15000)]
    public async Task A_fallback_before_engine_open_keeps_the_already_selected_media_source()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) =>
        {
            var selected = NegotiatedSource(request, "source-b");
            return
            [
                NegotiatedSource(request) with { SupportsDirectStream = false },
                selected with { MediaStreams = selected.MediaStreams.Select(stream => stream.Type == "Video"
                    ? stream with { VideoRange = "HDR10" } : stream).ToArray() }
            ];
        };

        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(), TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Engine.Opened);
        Assert.Equal("source-b", request.Source.Id);
        Assert.NotNull(request.ExternalSubtitleUri);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.False(negotiations[0].JsonBody.TryGetProperty("MediaSourceId", out _));
        Assert.Equal("source-b", negotiations[1].JsonBody.GetProperty("MediaSourceId").GetString());
    }

    private static void ConfigureSources(PlaybackTestContext context, bool progressive = false, bool twoSources = false) =>
        context.Sources = (request, _) => twoSources
            ? [NegotiatedSource(request, progressive: progressive), NegotiatedSource(request, "source-b", progressive)]
            : [NegotiatedSource(request, progressive: progressive)];

    private static MediaSourceInfo NegotiatedSource(RecordedRequest request, string id = "source-a", bool progressive = false)
    {
        var source = progressive ? PlaybackTestContext.ProgressiveSource(id) : PlaybackTestContext.Source(id);
        var delivery = HasExternalProfile(request) ? "External" : "Encode";
        return source with
        {
            DefaultSubtitleStreamIndex = 3,
            MediaStreams = source.MediaStreams.Select(stream => stream.Type == "Subtitle" ? stream with
            {
                DeliveryMethod = delivery,
                IsTextSubtitleStream = true
            } : stream).ToArray()
        };
    }

    private static void FailExternalSubtitles(PlaybackTestContext context) => context.Engine.OnOpenAsync = (request, _) =>
        request.ExternalSubtitleUri is null ? Task.CompletedTask : Task.FromException(new PlaybackException("UnsupportedSubtitle"));

    private static PlaybackSelectionChange Change(string kind) => kind switch
    {
        "Audio" => new() { AudioStreamIndex = 2 },
        "Quality" => new() { MaxStreamingBitrate = 4_000_000 },
        "SameSubtitle" => new() { SubtitleStreamIndex = 3 },
        "SameSource" => new() { MediaSourceId = "source-a" },
        "VideoConversion" => new() { ForceTranscoding = true },
        "DifferentSubtitle" => new() { SubtitleStreamIndex = 4 },
        "DifferentSource" => new() { MediaSourceId = "source-b" },
        "Off" => new() { SubtitleStreamIndex = -1 },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static bool HasExternalProfile(RecordedRequest request) => request.JsonBody.GetProperty("DeviceProfile")
        .GetProperty("SubtitleProfiles").EnumerateArray().Any(value => value.GetProperty("Method").GetString() == "External");
}
