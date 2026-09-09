using EmbyClient.Playback;
using Windows.Foundation;
using Windows.Media.Playback;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private Action? _cancelAtOpeningForProbe;
    private Action? _clearOpeningProbe;
    internal string? OpeningStateObservedForProbe { get; private set; }
    internal bool OpeningSourceBoundForProbe { get; private set; }
    internal bool HasBoundNativeSourceForProbe => _current?.Player?.Source is not null;
    internal bool ProductPlayerOwnerClearedForProbe => _playerOwner is null;
    internal bool ProductDisposalCompletedForProbe => _disposalTask?.IsCompleted == true;

    internal void ArmOpeningCancellationForProbe(Action cancel)
    {
        if (_cancelAtOpeningForProbe is not null || _clearOpeningProbe is not null)
            throw new PlaybackException("OpeningCancellationProbeAlreadyArmed");
        OpeningStateObservedForProbe = null;
        OpeningSourceBoundForProbe = false;
        _cancelAtOpeningForProbe = cancel;
    }

    internal void ClearOpeningCancellationForProbe()
    {
        _cancelAtOpeningForProbe = null;
        var clear = _clearOpeningProbe;
        _clearOpeningProbe = null;
        clear?.Invoke();
    }

    private void AttachOpeningCancellationForProbe(PlaybackEngineRequest request)
    {
        if (_cancelAtOpeningForProbe is null) return;
        var owner = _playerOwner ?? throw new PlaybackException("OpeningCancellationProbeRequiresWarmOwner");
        var native = owner.PlaybackSession;
        TypedEventHandler<MediaPlaybackSession, object>? handler = null;
        handler = (_, _) => Queue(() =>
        {
            if (_cancelAtOpeningForProbe is not { } cancel || _current?.Request.PlaybackId != request.PlaybackId
                || !ReferenceEquals(_current.Player, owner) || owner.Source is null
                || native.PlaybackState != MediaPlaybackState.Opening) return;
            OpeningStateObservedForProbe = native.PlaybackState.ToString();
            OpeningSourceBoundForProbe = true;
            native.PlaybackStateChanged -= handler;
            _clearOpeningProbe = null;
            _cancelAtOpeningForProbe = null;
            cancel();
        });
        _clearOpeningProbe = () => native.PlaybackStateChanged -= handler;
        native.PlaybackStateChanged += handler;
    }

    internal Func<Task> CaptureCurrentCallbackReplayForProbe()
    {
        var session = _current ?? throw new PlaybackException("NoSessionForCallbackReplay");
        var opened = session.Opened;
        var ended = session.Ended;
        var state = session.StateChanged;
        var position = session.PositionChanged;
        var seek = session.SeekCompleted;
        var duration = session.DurationChanged;
        if (opened is null || ended is null || state is null || position is null || seek is null || duration is null)
            throw new PlaybackException("CallbackReplayCaptureIncomplete");
        return () => OnDispatcherAsync(() =>
        {
            opened(null!, null!);
            ended(null!, null!);
            state(null!, null!);
            position(null!, null!);
            seek(null!, null!);
            duration(null!, null!);
            Fail(session, "UnsupportedFormat");
        });
    }
}
