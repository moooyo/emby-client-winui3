using EmbyClient.App.Services;
using EmbyClient.Playback;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    public ContentRefreshRatePlayback RefreshRatePlayback { get; private set; } = new(null, false, null);

    public event EventHandler? RefreshRatePlaybackChanged;

    private void UpdateRefreshRatePlayback()
    {
        var context = _coordinator?.ActiveContext;
        var engine = _engine;
        var native = engine?.Snapshot;
        var statistics = engine?.PlaybackStatistics;
        var current = context is not null && native?.PlaybackId == context.PlaybackId
            && statistics?.PlaybackId == context.PlaybackId;
        // Native metadata belongs to the negotiated stream; original-source frame rates cannot describe a transcode.
        var state = new ContentRefreshRatePlayback(context?.PlaybackId,
            current && _coordinator?.Status == PlaybackStatus.Playing && native?.State == PlaybackEngineState.Playing
                && engine?.VideoHeight is > 0,
            current ? statistics?.NativeVideoFramesPerSecond : null);
        if (state == RefreshRatePlayback) return;
        RefreshRatePlayback = state;
        RefreshRatePlaybackChanged?.Invoke(this, EventArgs.Empty);
    }
}
