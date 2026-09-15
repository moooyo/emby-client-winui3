using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackRecoverySettingsTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Edited_retry_settings_preserve_the_failed_absolute_position_and_pause_intent(bool isPaused)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.ProgressiveSource(), NewSource()];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true) with
        {
            MediaSourceId = "source-a",
            AudioStreamIndex = 2,
            SubtitleStreamIndex = 3,
            MaxStreamingBitrate = 8_000_000
        }, TestContext.Current.CancellationToken);
        context.Engine.SetPosition(120_000_007);
        if (isPaused) await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var failed = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Failed);
        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "NetworkFailure");
        await failed.WaitAsync(TestContext.Current.CancellationToken);
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);

        await context.Coordinator.RetryAsync(recovery.RecoveryId, new PlaybackSelectionChange
        {
            MediaSourceId = "source-b",
            AudioStreamIndex = 107,
            SubtitleStreamIndex = -1,
            MaxStreamingBitrate = 4_000_000,
            ForceTranscoding = false
        }, TestContext.Current.CancellationToken);

        var negotiation = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal("source-b", negotiation.GetProperty("MediaSourceId").GetString());
        Assert.Equal(107, negotiation.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(-1, negotiation.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(4_000_000, negotiation.GetProperty("MaxStreamingBitrate").GetInt64());
        Assert.Equal(720_000_008, negotiation.GetProperty("StartTimeTicks").GetInt64());
        Assert.True(negotiation.GetProperty("AllowVideoStreamCopy").GetBoolean());
        var replacement = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.Equal("source-b", replacement.Source.Id);
        Assert.Equal(107, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal(-1, context.Engine.Opened[1].SubtitleStreamIndex);
        Assert.False(replacement.Selection.ForceTranscoding);
        Assert.Equal(720_000_008, replacement.PositionTicks);
        Assert.Equal(isPaused, recovery.IsPaused);
        Assert.Null(context.Coordinator.Recovery);
        if (isPaused) PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", 720_000_008);
        else Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
    }

    [Theory(Timeout = 15000)]
    [InlineData("source-a", 2, 3)]
    [InlineData("source-b", 101, 203)]
    public async Task Omitted_retry_tracks_preserve_the_same_source_selection_or_request_new_source_defaults(
        string mediaSourceId, int expectedAudioStreamIndex, int expectedSubtitleStreamIndex)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source(), NewSource()];
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            AudioStreamIndex = 2,
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, new PlaybackSelectionChange
        {
            MediaSourceId = mediaSourceId,
            MaxStreamingBitrate = 4_000_000
        }, TestContext.Current.CancellationToken);

        var negotiation = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal(mediaSourceId, negotiation.GetProperty("MediaSourceId").GetString());
        Assert.Equal(mediaSourceId == "source-a", negotiation.TryGetProperty("AudioStreamIndex", out _));
        Assert.Equal(mediaSourceId == "source-a", negotiation.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.Equal(expectedAudioStreamIndex, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal(expectedSubtitleStreamIndex, context.Engine.Opened[1].SubtitleStreamIndex);
    }

    [Fact(Timeout = 15000)]
    public async Task An_invalid_settings_retry_leaves_the_same_recovery_available_for_correction()
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.Coordinator.RetryAsync(recovery.RecoveryId,
            new PlaybackSelectionChange { MaxStreamingBitrate = 0 }, TestContext.Current.CancellationToken));

        Assert.Equal(recovery, context.Coordinator.Recovery);
        Assert.Single(context.Engine.Opened);
        context.Engine.OnOpenAsync = null;
        await context.Coordinator.RetryAsync(recovery.RecoveryId,
            new PlaybackSelectionChange { MaxStreamingBitrate = 4_000_000 }, TestContext.Current.CancellationToken);
        Assert.Equal(4_000_000, context.Coordinator.ActiveContext?.Selection.MaxStreamingBitrate);
        Assert.Null(context.Coordinator.Recovery);
    }

    [Fact(Timeout = 15000)]
    public async Task A_stale_settings_retry_cannot_consume_a_new_recovery_or_replace_active_playback()
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
            PlaybackTestContext.Selection(), TestContext.Current.CancellationToken));
        var first = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.RetryAsync(first.RecoveryId,
            new PlaybackSelectionChange { MaxStreamingBitrate = 4_000_000 }, TestContext.Current.CancellationToken));
        var second = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        var staleChange = new PlaybackSelectionChange { MaxStreamingBitrate = 1_000_000 };

        await context.Coordinator.RetryAsync(first.RecoveryId, staleChange, TestContext.Current.CancellationToken);

        Assert.Equal(second, context.Coordinator.Recovery);
        Assert.Equal(4_000_000, second.Selection.MaxStreamingBitrate);
        Assert.Equal(2, context.Engine.Opened.Length);
        context.Engine.OnOpenAsync = null;
        await context.Coordinator.RetryAsync(second.RecoveryId, TestContext.Current.CancellationToken);
        var active = context.Coordinator.ActiveContext;

        await context.Coordinator.RetryAsync(second.RecoveryId, staleChange, TestContext.Current.CancellationToken);

        Assert.Equal(active, context.Coordinator.ActiveContext);
        Assert.Equal(3, context.Engine.Opened.Length);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
    }

    private static MediaSourceInfo NewSource() => PlaybackTestContext.ProgressiveSource("source-b") with
    {
        DefaultAudioStreamIndex = 101,
        DefaultSubtitleStreamIndex = 203,
        MediaStreams =
        [
            new() { Index = 0, Type = "Video", Codec = "h264" },
            new() { Index = 101, Type = "Audio", Codec = "aac" },
            new() { Index = 107, Type = "Audio", Codec = "aac" },
            new() { Index = 203, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode" }
        ]
    };
}
