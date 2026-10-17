namespace EmbyClient.App.Views;

/// <summary>Keeps pending display-release cleanup scheduled after playback ownership is detached.</summary>
internal static class PlaybackPresentationClockPolicy
{
    public static bool ShouldRun(bool hasCoordinator, bool needsPlaybackPolling, bool needsDisplayReleaseRetry) =>
        needsDisplayReleaseRetry || (hasCoordinator && needsPlaybackPolling);
}
