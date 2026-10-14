using EmbyClient.App.Services;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    static partial void RecordAcceptanceSubtitleFailure(Exception exception, string operation) =>
        AcceptanceDiagnostics.Record(exception, operation);

    partial void RecordAcceptanceAssObservation(Session session)
    {
        try
        {
            // Only an explicitly isolated profile opts in. These counters never assert visible pixels.
            var root = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)
                || !IsActive(session) || session.DecoderSource is not { } source)
                return;
            var usesStyles = UsesLibassSubtitleStyles(session);
            if (!usesStyles && session.LastAssObservationLogTick is null) return;
            var now = Environment.TickCount64;
            if (session.LastAssObservationLogTick is { } previous && now - previous < 1000
                && usesStyles == session.LastAssObservationUsesStyles) return;
            session.LastAssObservationLogTick = now;
            session.LastAssObservationUsesStyles = usesStyles;
            var value = NativeAssInterop.ReadObservation(source);
            if (value is null)
            {
                RecordSubtitleDiagnostic("AssObservation available=0");
                return;
            }
            RecordSubtitleDiagnostic(FormattableString.Invariant(
                $"AssObservation failure=0x{value.Failure:X8} tracks={value.OwnedTrackCount} selected={value.PlatformPresentedTrackCount} enabled={value.EnabledTrackCount} images={value.CurrentImageCount} active={value.PlatformActiveImageCount} clocksHidden={value.HiddenReferenceTrackCount} samples={value.VideoSampleCount} scheduled={value.ScheduledFrameCount} callbacks={value.ReferenceCueEnteredCount} renders={value.RenderCallCount} imageFrames={value.ImageFrameCount} added={value.ImageCueAddedCount} entered={value.ImageCueEnteredCount} exited={value.ImageCueExitedCount} clears={value.ClearCount} stale={value.StaleFrameCount} pending={value.PendingScheduledFrameCount} refreshes={value.RefreshCount} sampleTicks={value.LastSamplePositionTicks} renderTicks={value.LastRenderPositionTicks} scriptTicks={value.LastScriptPositionTicks} alphaPixels={value.LastBitmapNonzeroAlphaPixels} rect={value.LastImageX},{value.LastImageY},{value.LastImageWidth},{value.LastImageHeight} video={value.VideoWidth},{value.VideoHeight}"));
        }
        catch (Exception) { /* Acceptance observations cannot change playback or ownership. */ }
    }

    private sealed partial class Session
    {
        public long? LastAssObservationLogTick { get; set; }
        public bool LastAssObservationUsesStyles { get; set; }
    }
}
