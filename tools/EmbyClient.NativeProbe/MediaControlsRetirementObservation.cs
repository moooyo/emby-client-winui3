using EmbyClient.App.Playback;
using EmbyClient.Playback;
using Windows.Media;

namespace EmbyClient.NativeProbe;

/// <summary>Reads retired SMTC before the native player's owning COM lifetime ends.</summary>
internal sealed partial class MediaControlsRetirementObservation : IDisposable
{
    private readonly NativePlaybackEngine _engine;
    private readonly Guid _playbackId;
    private readonly LoopResult _result;
    private SystemMediaTransportControls? _controls;

    internal MediaControlsRetirementObservation(NativePlaybackEngine engine, Guid playbackId,
        SystemMediaTransportControls controls, LoopResult result)
    {
        _engine = engine;
        _playbackId = playbackId;
        _controls = controls;
        _result = result;
        engine.EventReceived += OnEngineEvent;
    }

    private void OnEngineEvent(object? sender, PlaybackEngineEventArgs args)
    {
        if (args.Snapshot.PlaybackId != _playbackId || args.Kind != PlaybackEngineEventKind.StateChanged
            || args.Snapshot.State != PlaybackEngineState.Stopped || _controls is not { } controls) return;
        _result.MediaControlsRetirementObserved = true;
        try
        {
            // NativePlaybackEngine raises Stopped after RetireMediaControls and before MediaPlayer.Dispose.
            // Read synchronously on that dispatcher callback; querying later may correctly return RO_E_CLOSED.
            _result.MediaControlsRetirementGetter = "IsEnabled";
            _result.MediaControlsEnabledAfterRetire = controls.IsEnabled;
            _result.MediaControlsRetirementGetter = "PlaybackStatus";
            var status = controls.PlaybackStatus;
            _result.MediaControlsStatusAfterRetire = status.ToString();
            _result.MediaControlsRetirementGetter = "DisplayUpdater.Type";
            var type = controls.DisplayUpdater.Type;
            _result.MediaControlsTypeAfterRetire = type.ToString();
            var metadataCleared = type == MediaPlaybackType.Unknown;
            if (type == MediaPlaybackType.Video)
            {
                _result.MediaControlsRetirementGetter = "DisplayUpdater.VideoProperties.Title";
                _result.MediaControlsVideoTitleEmptyAfterRetire = string.IsNullOrEmpty(controls.DisplayUpdater.VideoProperties.Title);
                metadataCleared = _result.MediaControlsVideoTitleEmptyAfterRetire == true;
            }
            _result.MediaControlsRetired = _result.MediaControlsEnabledAfterRetire == false
                && status == MediaPlaybackStatus.Closed && metadataCleared;
            _result.MediaControlsRetirementGetter = "Complete";
        }
        catch (Exception exception) { _result.MediaControlsRetirementHResult = exception.HResult; }
        finally { _controls = null; }
    }

    public void Dispose()
    {
        _engine.EventReceived -= OnEngineEvent;
        _controls = null;
    }
}
