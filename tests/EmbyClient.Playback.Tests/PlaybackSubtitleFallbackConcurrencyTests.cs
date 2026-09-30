using EmbyClient.Api;
using Xunit;

namespace EmbyClient.Playback.Tests;

public sealed class PlaybackSubtitleFallbackConcurrencyTests
{
    [Fact(Timeout = 15000)]
    public async Task Runtime_subtitle_failure_of_forced_video_restores_pause_and_position_and_promotes_burn_in_only_once()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) => [SourceWithSubtitles(request)];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001, transcode: true) with
        {
            MediaSourceId = "source-a",
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);
        const long position = 740_000_013;
        context.Engine.SetPosition(position);
        await context.Coordinator.PauseAsync(TestContext.Current.CancellationToken);
        var original = context.Engine.Opened[0];
        var restored = PlaybackLifecycleTests.Signal();
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Paused && args.Context?.PlaySessionId == "session-2")
                restored.TrySetResult();
        };

        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "UnsupportedSubtitle");
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        var replacement = context.Engine.Opened[1];
        Assert.NotNull(original.ExternalSubtitleUri);
        Assert.Null(replacement.ExternalSubtitleUri);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, replacement.DeliveryMethod);
        Assert.Equal("source-a", replacement.Source.Id);
        Assert.Equal(3, replacement.SubtitleStreamIndex);
        Assert.Equal(position, replacement.InitialPositionTicks);
        Assert.NotEqual(original.PlaybackId, replacement.PlaybackId);
        var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.True(active.Selection.ForceTranscoding);
        Assert.True(active.Selection.ForceSubtitleBurnIn);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(2, negotiations.Length);
        Assert.True(HasExternalProfile(negotiations[0]));
        Assert.False(HasExternalProfile(negotiations[1]));
        PlaybackPausedRestartTests.AssertPausedReplacement(context, "session-2", position);

        var failed = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Failed);
        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "UnsupportedSubtitle");
        await failed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Engine.Opened.Length);
        Assert.Equal(2, context.Handler.At("Items/movie-a/PlaybackInfo").Length);
        Assert.Equal(new[] { original.PlaybackId, replacement.PlaybackId }, context.Engine.Stopped);
        Assert.Null(context.Coordinator.ActiveContext);
        Assert.Null(context.Coordinator.Recovery);
    }

    [Fact(Timeout = 15000)]
    public async Task Runtime_video_failure_can_then_promote_an_external_subtitle_open_failure_to_a_third_burn_in_session()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) => [SourceWithSubtitles(request)];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection(600_000_001) with
        {
            MediaSourceId = "source-a",
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);
        const long position = 755_000_017;
        context.Engine.SetPosition(position);
        context.Engine.OnOpenAsync = (request, _) => request.DeliveryMethod == PlaybackDeliveryMethod.Transcode
            && request.ExternalSubtitleUri is not null
                ? Task.FromException(new PlaybackException("UnsupportedSubtitle")) : Task.CompletedTask;
        var replacementStarted = PlaybackLifecycleTests.Signal();
        context.Coordinator.StatusChanged += (_, args) =>
        {
            if (args.Status == PlaybackStatus.Playing && args.Context?.PlaySessionId == "session-3")
                replacementStarted.TrySetResult();
        };

        context.Engine.Emit(PlaybackEngineEventKind.Failed,
            context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "UnsupportedFormat");
        await replacementStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var opened = context.Engine.Opened;
        Assert.Equal(3, opened.Length);
        Assert.Equal(PlaybackDeliveryMethod.DirectStream, opened[0].DeliveryMethod);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened[1].DeliveryMethod);
        Assert.Equal(PlaybackDeliveryMethod.Transcode, opened[2].DeliveryMethod);
        Assert.NotNull(opened[0].ExternalSubtitleUri);
        Assert.NotNull(opened[1].ExternalSubtitleUri);
        Assert.Null(opened[2].ExternalSubtitleUri);
        Assert.Equal(3, opened.Select(request => request.PlaybackId).Distinct().Count());
        Assert.All(opened, request => Assert.Equal("source-a", request.Source.Id));
        Assert.All(opened.Skip(1), request => Assert.Equal(position, request.InitialPositionTicks));
        Assert.Equal(new[] { opened[0].PlaybackId, opened[1].PlaybackId }, context.Engine.Stopped);
        var negotiations = context.Handler.At("Items/movie-a/PlaybackInfo");
        Assert.Equal(3, negotiations.Length);
        Assert.True(HasExternalProfile(negotiations[0]));
        Assert.True(HasExternalProfile(negotiations[1]));
        Assert.False(HasExternalProfile(negotiations[2]));
        Assert.All(negotiations, request => Assert.Equal("source-a", request.JsonBody.GetProperty("MediaSourceId").GetString()));
        Assert.Equal(new[] { "session-1", "session-3" }, context.Handler.At("Sessions/Playing")
            .Select(request => request.JsonBody.GetProperty("PlaySessionId").GetString()));
        var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
        Assert.True(active.Selection.ForceTranscoding);
        Assert.True(active.Selection.ForceSubtitleBurnIn);
        Assert.Equal(position, active.PositionTicks);
        Assert.Equal("session-3", active.PlaySessionId);
        Assert.Null(context.Coordinator.Recovery);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_the_owner_during_runtime_subtitle_cleanup_prevents_a_new_burn_in_negotiation()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Sources = (request, _) => [SourceWithSubtitles(request)];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            MediaSourceId = "source-a",
            SubtitleStreamIndex = 3
        }, owner.Token);
        var original = Assert.Single(context.Engine.Opened);
        var stopEntered = PlaybackLifecycleTests.Signal();
        var releaseStop = PlaybackLifecycleTests.Signal();
        var cancellationProcessed = PlaybackLifecycleTests.WaitForStatus(context.Coordinator, PlaybackStatus.Idle);
        CancellationToken cleanupToken = default;
        context.Engine.OnStopAsync = async (id, token) =>
        {
            Assert.Equal(original.PlaybackId, id);
            cleanupToken = token;
            stopEntered.TrySetResult();
            await releaseStop.Task.WaitAsync(token);
        };
        try
        {
            context.Engine.Emit(PlaybackEngineEventKind.Failed,
                context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "UnsupportedSubtitle");
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            owner.Cancel();
            Assert.False(cleanupToken.IsCancellationRequested);
            releaseStop.TrySetResult();
            await cancellationProcessed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
            Assert.Single(context.Engine.Opened);
            Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
            Assert.Single(context.Handler.At("Sessions/Playing/Stopped"));
            Assert.Equal(PlaybackStatus.Idle, context.Coordinator.Status);
            Assert.Null(context.Coordinator.ActiveContext);
            Assert.Null(context.Coordinator.Recovery);
        }
        finally { releaseStop.TrySetResult(); }
    }

    [Fact(Timeout = 15000)]
    public async Task A_superseding_play_during_runtime_subtitle_cleanup_does_not_reopen_the_retired_item_with_the_new_intent()
    {
        await using var context = new PlaybackTestContext(enableExternalWebVtt: true);
        context.Sources = (request, _) => request.Uri.AbsolutePath == "/emby/Items/movie-b/PlaybackInfo"
            ? [SourceWithSubtitles(request, "movie-b", "source-b")] : [SourceWithSubtitles(request)];
        await context.Coordinator.PlayAsync(PlaybackTestContext.Selection() with
        {
            MediaSourceId = "source-a",
            SubtitleStreamIndex = 3
        }, TestContext.Current.CancellationToken);
        var original = Assert.Single(context.Engine.Opened);
        var stopEntered = PlaybackLifecycleTests.Signal();
        var releaseStop = PlaybackLifecycleTests.Signal();
        var fallbacks = 0;
        context.Engine.OnStopAsync = async (id, token) =>
        {
            if (id != original.PlaybackId) return;
            stopEntered.TrySetResult();
            await releaseStop.Task.WaitAsync(token);
        };
        context.Coordinator.Diagnostic += (_, args) =>
        {
            if (args.PlaybackId == original.PlaybackId && args.Operation == "Fallback")
                Interlocked.Increment(ref fallbacks);
        };

        try
        {
            context.Engine.Emit(PlaybackEngineEventKind.Failed,
                context.Engine.Snapshot! with { State = PlaybackEngineState.Failed }, "UnsupportedSubtitle");
            await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            const long position = 111_000_003;
            var replacement = context.Coordinator.PlayAsync(PlaybackTestContext.Selection(position) with
            {
                ItemId = "movie-b",
                MediaSourceId = "source-b",
                SubtitleStreamIndex = 3
            }, TestContext.Current.CancellationToken);
            releaseStop.TrySetResult();
            await replacement.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(2, context.Engine.Opened.Length);
            Assert.Equal(original.PlaybackId, Assert.Single(context.Engine.Stopped));
            var oldNegotiation = Assert.Single(context.Handler.At("Items/movie-a/PlaybackInfo"));
            var newNegotiation = Assert.Single(context.Handler.At("Items/movie-b/PlaybackInfo"));
            Assert.Equal("source-a", oldNegotiation.JsonBody.GetProperty("MediaSourceId").GetString());
            Assert.Equal("source-b", newNegotiation.JsonBody.GetProperty("MediaSourceId").GetString());
            Assert.True(HasExternalProfile(newNegotiation));
            Assert.Equal(0, Volatile.Read(ref fallbacks));
            var active = Assert.IsType<PlaybackContext>(context.Coordinator.ActiveContext);
            Assert.Equal("movie-b", active.Selection.ItemId);
            Assert.Equal("source-b", active.Source.Id);
            Assert.Equal("session-2", active.PlaySessionId);
            Assert.Equal(position, active.PositionTicks);
            Assert.NotEqual(original.PlaybackId, active.PlaybackId);
            Assert.NotNull(context.Engine.Opened[1].ExternalSubtitleUri);
            Assert.False(active.Selection.ForceTranscoding);
            Assert.False(active.Selection.ForceSubtitleBurnIn);
            Assert.Null(context.Coordinator.Recovery);
        }
        finally { releaseStop.TrySetResult(); }
    }

    private static MediaSourceInfo SourceWithSubtitles(RecordedRequest request, string itemId = "movie-a", string sourceId = "source-a")
    {
        var source = PlaybackTestContext.Source(sourceId);
        var method = HasExternalProfile(request) ? "External" : "Encode";
        return source with
        {
            DefaultSubtitleStreamIndex = 3,
            DirectStreamUrl = $"/emby/Videos/{itemId}/stream?Static=true&MediaSourceId={sourceId}",
            TranscodingUrl = $"/emby/Videos/{itemId}/master.m3u8?MediaSourceId={sourceId}",
            MediaStreams = source.MediaStreams.Select(stream => stream.Index is 3 or 4 ? stream with
            {
                DeliveryMethod = method,
                DeliveryUrl = null,
                IsTextSubtitleStream = true
            } : stream).ToArray()
        };
    }

    private static bool HasExternalProfile(RecordedRequest request) => request.JsonBody.GetProperty("DeviceProfile")
        .GetProperty("SubtitleProfiles").EnumerateArray().Any(profile => profile.GetProperty("Method").GetString() == "External");
}
