using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.Playback;

namespace EmbyClient.NativeProbe;

internal static partial class ComplexSubtitleProbe
{
    private static DeviceProfile CreateHttpControlProfile()
    {
        var baseline = ConservativeDeviceProfile.Create();
        return baseline with
        {
            TranscodingProfiles = baseline.TranscodingProfiles!.Select(profile => profile with
            {
                Container = "mp4", Protocol = "http"
            }).ToArray()
        };
    }

    private static void RequireProgressiveStream(ComplexSubtitleNativeObservation value) => Require(
        value.ActivePlaybackMatches && value.DeliveryMethod == nameof(PlaybackDeliveryMethod.Transcode)
        && value.TimelineKind == nameof(PlaybackTimelineKind.ProgressiveSegment) && value.InitialPositionTicks == 0
        && value.HasProgressiveRelay && !value.HasFullSourceRangeRelay && value.TranscodingContainer == "mp4"
        && value.MediaRouteKind == "ProgressiveVideo", "ActualProgressiveRelayNotConfirmed");

    private static async Task RunProgressiveTailAsync(string output, ComplexSubtitleReport report, ComplexSubtitleCase fixture,
        PlaybackContext disabledContext, PlaybackCoordinator coordinator, NativePlaybackEngine engine,
        string referenceFile, string referenceHash, Action save, Func<ComplexSubtitlePlaybackEvent[]> getEvents, CancellationToken token)
    {
        var context = disabledContext;
        var disabled = report.Phases[1];
        if (context.DeliveryMethod == PlaybackDeliveryMethod.DirectStream)
        {
            report.Status = "ReopeningEncodedTailControl";
            save();
            await coordinator.ChangeSelectionAsync(new PlaybackSelectionChange { SubtitleStreamIndex = fixture.SubtitleStreamIndex }, token);
            context = coordinator.ActiveContext ?? throw new ComplexSubtitleFailure("TailContextMissing");
            Require(context.PlaybackId != disabledContext.PlaybackId && context.PlaySessionId != disabledContext.PlaySessionId,
                "TailEncodedSessionNotReplaced");
            report.TailUsedNewEncodedSession = true;
        }
        Require(context.Source.Id == fixture.MediaSourceId && context.DeliveryMethod == PlaybackDeliveryMethod.Transcode
            && context.TimelineKind == PlaybackTimelineKind.ProgressiveSegment && !context.Selection.ForceTranscoding,
            "ProgressiveTailContextRequired");
        var tail = NewPhase("TailFrame", context);
        report.Phases.Add(tail);
        await ObservePauseAsync(engine, coordinator, context, tail, disabled.Native!.SourcePositionTicks!.Value, token, progressiveControl: true);
        report.TailInitialNative = tail.Native;
        RequireProgressiveStream(tail.Native!);
        if (report.TailUsedNewEncodedSession)
            Require(tail.Native!.SelectedSubtitleStreamIndex == fixture.SubtitleStreamIndex
                && tail.Native.SelectedSubtitleDeliveryMethod == "Encode" && tail.Native.RequestSubtitleMethod == "Encode",
                "TailEncodeRouteMissing");

        report.Status = "PlayingProgressiveTail";
        save();
        await coordinator.ResumeAsync(token);
        var tailTarget = fixture.RunTimeTicks - TimeSpan.FromMilliseconds(750).Ticks;
        var advanceDeadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Require(coordinator.ActiveContext?.PlaybackId == context.PlaybackId
                && coordinator.Status is not (PlaybackStatus.Failed or PlaybackStatus.Ended), "TailPlaybackRetiredBeforeTarget");
            var snapshot = engine.Snapshot;
            if (snapshot?.PlaybackId == context.PlaybackId && snapshot.State == PlaybackEngineState.Playing
                && snapshot.PositionTicks + context.TimelineOffsetTicks >= tailTarget) break;
            Require(DateTimeOffset.UtcNow < advanceDeadline, "TailPositionTimedOut");
            await Task.Delay(40, token);
        }
        await coordinator.PauseAsync(token);
        await ObservePauseAsync(engine, coordinator, context, tail, tailTarget, token, progressiveControl: true);
        RequireProgressiveStream(tail.Native!);
        Require(tail.Native!.SourcePositionTicks >= tailTarget && tail.Native.SourcePositionTicks < fixture.RunTimeTicks,
            "TailSourcePositionMissing");
        tail.AutomatedChecksPassed = true;
        save();
        await ConfirmVisualAsync(output, report, fixture, tail, context, coordinator, engine,
            ["video-frame-visible-near-end", "subtitles-absent-after-cue"], "ProgressiveSourceTailFrame",
            referenceFile, referenceHash, save, token);

        report.Status = "AwaitingProgressiveEnded";
        save();
        await coordinator.ResumeAsync(token);
        var endDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var events = getEvents();
            var failed = events.FirstOrDefault(value => value.PlaybackIdHash == tail.PlaybackIdHash && value.State == "Failed");
            if (failed is not null) throw new PlaybackException(failed.ErrorCode ?? "TailPlaybackFailed");
            var ended = events.FirstOrDefault(value => value.Observer == "Native" && value.PlaybackIdHash == tail.PlaybackIdHash && value.Kind == "Ended");
            if (ended is not null)
            {
                report.TailNativeEndedObserved = true;
                report.TailEndedNativePositionTicks = ended.PositionTicks;
                report.TailEndedSourcePositionTicks = ended.PositionTicks + context.TimelineOffsetTicks;
                Require(report.TailEndedSourcePositionTicks is long finalPosition
                    && Math.Abs(finalPosition - fixture.RunTimeTicks) <= TimeSpan.FromSeconds(1).Ticks,
                    "ProgressiveEndedSourcePositionMismatch");
                report.TailCoordinatorEndedObserved = events.Any(value => value.Observer == "Coordinator"
                    && value.State == "Ended" && value.PlaybackIdHash == tail.PlaybackIdHash);
                if (report.TailCoordinatorEndedObserved)
                {
                    report.TailDrainCompleted = await engine.ComplexSubtitleSessionDrainedForProbeAsync();
                    if (report.TailDrainCompleted) break;
                }
            }
            Require(DateTimeOffset.UtcNow < endDeadline, "ProgressiveEndedOrDrainTimedOut");
            await Task.Delay(50, token);
        }
        save();
    }
}
