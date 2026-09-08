using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackSourceSwitchTests
{
    private const long OriginalPositionTicks = 800_000_008;

    [Fact(Timeout = 15000)]
    public async Task A_paused_source_switch_uses_the_new_container_defaults_instead_of_old_sparse_track_indexes()
    {
        await using var context = new PlaybackTestContext();
        await PreparePausedSourceAsync(context, TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { MediaSourceId = "source-b" },
            TestContext.Current.CancellationToken);

        var negotiation = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal("source-b", negotiation.GetProperty("MediaSourceId").GetString());
        Assert.Equal(OriginalPositionTicks, negotiation.GetProperty("StartTimeTicks").GetInt64());
        Assert.False(negotiation.TryGetProperty("AudioStreamIndex", out _));
        Assert.False(negotiation.TryGetProperty("SubtitleStreamIndex", out _));
        var replacement = context.Engine.Opened[1];
        Assert.Equal("source-b", replacement.Source.Id);
        Assert.Equal(101, replacement.AudioStreamIndex);
        Assert.Equal(203, replacement.SubtitleStreamIndex);
        Assert.Equal(new[] { originalId, replacement.PlaybackId }, context.Engine.Paused);
        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", OriginalPositionTicks);
    }

    [Theory(Timeout = 15000)]
    [InlineData(211)]
    [InlineData(-1)]
    public async Task Explicit_new_source_tracks_override_its_defaults_while_preserving_pause(int subtitleStreamIndex)
    {
        await using var context = new PlaybackTestContext();
        await PreparePausedSourceAsync(context, TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            MediaSourceId = "source-b",
            AudioStreamIndex = 107,
            SubtitleStreamIndex = subtitleStreamIndex
        }, TestContext.Current.CancellationToken);

        var negotiation = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal("source-b", negotiation.GetProperty("MediaSourceId").GetString());
        Assert.Equal(107, negotiation.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(subtitleStreamIndex, negotiation.GetProperty("SubtitleStreamIndex").GetInt32());
        var replacement = context.Engine.Opened[1];
        Assert.Equal(107, replacement.AudioStreamIndex);
        Assert.Equal(subtitleStreamIndex, replacement.SubtitleStreamIndex);
        Assert.Equal(new[] { originalId, replacement.PlaybackId }, context.Engine.Paused);
        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", OriginalPositionTicks);
        var started = context.Handler.At("Sessions/Playing")[1].JsonBody;
        Assert.Equal(107, started.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(subtitleStreamIndex, started.GetProperty("SubtitleStreamIndex").GetInt32());
    }

    [Fact(Timeout = 15000)]
    public async Task Quality_changes_for_the_same_actual_source_preserve_its_explicit_sparse_track_selection()
    {
        await using var context = new PlaybackTestContext();
        await PreparePausedSourceAsync(context, TestContext.Current.CancellationToken);
        var originalId = context.Coordinator.ActiveContext!.PlaybackId;

        await context.Coordinator.ChangeSelectionAsync(new PlaybackSelectionChange
        {
            MediaSourceId = "source-a",
            MaxStreamingBitrate = 4_000_000
        }, TestContext.Current.CancellationToken);

        var negotiation = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal("source-a", negotiation.GetProperty("MediaSourceId").GetString());
        Assert.Equal(4_000_000, negotiation.GetProperty("MaxStreamingBitrate").GetInt64());
        Assert.Equal(17, negotiation.GetProperty("AudioStreamIndex").GetInt32());
        Assert.Equal(23, negotiation.GetProperty("SubtitleStreamIndex").GetInt32());
        var replacement = context.Engine.Opened[1];
        Assert.Equal(17, replacement.AudioStreamIndex);
        Assert.Equal(23, replacement.SubtitleStreamIndex);
        Assert.Equal(new[] { originalId, replacement.PlaybackId }, context.Engine.Paused);
        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", OriginalPositionTicks);
    }

    private static async Task PreparePausedSourceAsync(PlaybackTestContext context, CancellationToken cancellationToken)
    {
        context.Sources = (request, _) => request.JsonBody.TryGetProperty("MediaSourceId", out var sourceId)
            && sourceId.GetString() == "source-b" ? [NewSource()] : [OriginalSource()];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001) with
        {
            AudioStreamIndex = 17,
            SubtitleStreamIndex = 23
        }, cancellationToken);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, Assert.Single(context.Engine.Opened).DeliveryMethod);
        context.Engine.SetPosition(OriginalPositionTicks);
        await context.Coordinator.PauseAsync(cancellationToken);
    }

    private static MediaSourceInfo OriginalSource() => PlaybackTestContext.Source("source-a") with
    {
        DefaultAudioStreamIndex = 7,
        DefaultSubtitleStreamIndex = 11,
        MediaStreams =
        [
            new() { Index = 0, Type = "Video", Codec = "h264" },
            new() { Index = 7, Type = "Audio", Codec = "aac" },
            new() { Index = 17, Type = "Audio", Codec = "aac" },
            new() { Index = 11, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode" },
            new() { Index = 23, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode" }
        ]
    };

    private static MediaSourceInfo NewSource() => PlaybackTestContext.Source("source-b") with
    {
        DefaultAudioStreamIndex = 101,
        DefaultSubtitleStreamIndex = 203,
        MediaStreams =
        [
            new() { Index = 0, Type = "Video", Codec = "h264" },
            new() { Index = 101, Type = "Audio", Codec = "aac" },
            new() { Index = 107, Type = "Audio", Codec = "aac" },
            new() { Index = 203, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode" },
            new() { Index = 211, Type = "Subtitle", Codec = "srt", DeliveryMethod = "Encode" }
        ]
    };
}
