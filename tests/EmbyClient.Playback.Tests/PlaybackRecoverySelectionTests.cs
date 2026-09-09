using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackRecoverySelectionTests
{
    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retry_keeps_implicit_tracks_implicit_so_a_direct_only_source_remains_playable(bool failedAfterStarting)
    {
        await using var context = new PlaybackTestContext();
        context.Sources = (_, _) => [PlaybackTestContext.Source() with { SupportsTranscoding = false, TranscodingUrl = null }];
        context.Handler.RespondAsync = (request, _) => Task.FromResult(
            request.Uri.AbsolutePath.EndsWith("/PlaybackInfo", StringComparison.Ordinal)
                && !request.JsonBody.GetProperty("EnableDirectStream").GetBoolean()
                ? RecordingHandler.Json("""{"PlaySessionId":"rejected","MediaSources":[],"ErrorCode":"NoCompatibleStream"}""")
                : context.Respond(request));
        if (failedAfterStarting)
        {
            await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken);
            var failed = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Failed);
            context.Engine.Emit(PlaybackEngineEventKind.Failed, context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "NetworkFailure");
            await failed.WaitAsync(TestContext.Current.CancellationToken);
        }
        else
        {
            context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
            await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(
                PlaybackTestContext.Selection(600_000_001), TestContext.Current.CancellationToken));
        }
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        Assert.Null(recovery.Selection.AudioStreamIndex);
        Assert.Null(recovery.Selection.SubtitleStreamIndex);
        var retry = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.True(retry.GetProperty("EnableDirectStream").GetBoolean());
        Assert.False(retry.TryGetProperty("AudioStreamIndex", out _));
        Assert.False(retry.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, context.Engine.Opened[1].DeliveryMethod);
        Assert.Equal(1, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal(-1, context.Engine.Opened[1].SubtitleStreamIndex);
        Assert.Equal(600_000_001, context.Coordinator.ActiveContext?.PositionTicks);
        Assert.Equal(PlaybackStatus.Playing, context.Coordinator.Status);
    }

    [Theory(Timeout = 15000)]
    [InlineData(1, -1)]
    [InlineData(2, 3)]
    [InlineData(null, -1)]
    [InlineData(null, 3)]
    public async Task Retry_preserves_explicit_default_alternate_and_disabled_tracks_without_inventing_other_choices(
        int? audioStreamIndex, int subtitleStreamIndex)
    {
        await using var context = new PlaybackTestContext();
        context.Engine.OnOpenAsync = (_, _) => throw new PlaybackException("NetworkFailure");
        await Assert.ThrowsAsync<PlaybackException>(() => context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001) with
        {
            AudioStreamIndex = audioStreamIndex,
            SubtitleStreamIndex = subtitleStreamIndex
        }, TestContext.Current.CancellationToken));
        var recovery = Assert.IsType<PlaybackRecovery>(context.Coordinator.Recovery);
        context.Engine.OnOpenAsync = null;

        await context.Coordinator.RetryAsync(recovery.RecoveryId, TestContext.Current.CancellationToken);

        Assert.Equal(audioStreamIndex, recovery.Selection.AudioStreamIndex);
        Assert.Equal(subtitleStreamIndex, recovery.Selection.SubtitleStreamIndex);
        var retry = context.Handler.At("Items/movie-a/PlaybackInfo")[1].JsonBody;
        Assert.Equal(!audioStreamIndex.HasValue, retry.GetProperty("EnableDirectStream").GetBoolean());
        if (audioStreamIndex.HasValue) Assert.Equal(audioStreamIndex, retry.GetProperty("AudioStreamIndex").GetInt32());
        else Assert.False(retry.TryGetProperty("AudioStreamIndex", out _));
        Assert.Equal(subtitleStreamIndex, retry.GetProperty("SubtitleStreamIndex").GetInt32());
        Assert.Equal(audioStreamIndex ?? 1, context.Engine.Opened[1].AudioStreamIndex);
        Assert.Equal(subtitleStreamIndex, context.Engine.Opened[1].SubtitleStreamIndex);
        Assert.Equal(audioStreamIndex.HasValue || subtitleStreamIndex == 3 ? PlaybackDeliveryMethod.Transcode : PlaybackDeliveryMethod.DirectStream,
            context.Engine.Opened[1].DeliveryMethod);
        Assert.Equal(600_000_001, context.Coordinator.ActiveContext?.PositionTicks);
    }
}
