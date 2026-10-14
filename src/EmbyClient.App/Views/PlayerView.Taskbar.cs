using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private TaskbarPlaybackState? _publishedTaskbarState;

    public event EventHandler? TaskbarStateChanged;

    public TaskbarPlaybackState TaskbarState
    {
        get
        {
            var context = _coordinator?.ActiveContext;
            var valid = _session is not null && context is not null && _engine?.Snapshot?.PlaybackId == context.PlaybackId;
            var enabled = _preferences.SystemMediaControls;
            if (!valid) return new TaskbarPlaybackState(null, false, false, false, false, enabled);
            var canToggle = !_expiredReported && !_advancing && !_retryInProgress && !IsModalOpen
                && _preparationIntent != _playIntent && _coordinator?.Status is PlaybackStatus.Playing or PlaybackStatus.Paused;
            var neighbors = _episodeNeighborsItemId == _item?.Id ? _episodeNeighbors : null;
            return new TaskbarPlaybackState(context!.PlaybackId, _coordinator!.Status == PlaybackStatus.Playing,
                canToggle, CanChangeEpisode() && neighbors?.Previous is not null,
                CanChangeEpisode() && neighbors?.Next is not null, enabled);
        }
    }

    public void RequestTaskbarCommand(NativeMediaCommandEventArgs args)
    {
        var state = TaskbarState;
        if (!state.Enabled || state.PlaybackId != args.PlaybackId || _engine is null) return;
        if (args.Command is NativeMediaCommand.Play or NativeMediaCommand.Pause && !state.CanToggle
            || args.Command == NativeMediaCommand.Previous && !state.CanPrevious
            || args.Command == NativeMediaCommand.Next && !state.CanNext) return;
        if (args.Command is not (NativeMediaCommand.Play or NativeMediaCommand.Pause or NativeMediaCommand.Previous or NativeMediaCommand.Next)) return;
        SystemMediaCommandRequested(_engine, args);
    }

    private void UpdateTaskbarState()
    {
        var state = TaskbarState;
        if (state == _publishedTaskbarState) return;
        _publishedTaskbarState = state;
        TaskbarStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
