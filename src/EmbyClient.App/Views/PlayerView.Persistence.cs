using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly PlaybackPreferenceStore _playbackPreferenceStore = new();
    private readonly Dictionary<NativePreparedSubtitle, LocalSubtitlePreference> _localSubtitleFiles = [];
    private PlaybackPreferenceScope? _playbackPreferenceScope;
    private PlaybackPreferences _playbackPreferences = new();
    private PlaybackPreferences? _lastQueuedPlaybackPreferences;
    private Task _playbackPreferenceWrite = Task.CompletedTask;
    private CancellationTokenSource? _playbackPreferenceRestoreRequest;
    private CancellationTokenSource? _subtitleSelectionRequest;
    private long _playbackPreferenceIntent = -1;
    private long _playbackPreferenceFilesRestoredIntent = -1;
    private long _subtitleSelectionRevision;
    private Guid? _playbackPreferencesRestoredId;
    private Guid? _playbackPreferenceControlsReadyId;
    private bool _restoringPlaybackPreferences;
    private bool _savedLocalSubtitleActivationPending;
    private double _appliedAudioDelaySeconds;
    private double _appliedSubtitleDelaySeconds;

    private void BeginPlaybackPreferenceRequest()
    {
        SavePlaybackPreferences();
        CancelPlaybackPreferenceRestore();
        _playbackPreferenceScope = null;
        _playbackPreferenceIntent = -1;
        _playbackPreferenceFilesRestoredIntent = -1;
        _playbackPreferencesRestoredId = null;
        _playbackPreferenceControlsReadyId = null;
        _lastQueuedPlaybackPreferences = null;
        _localSubtitleFiles.Clear();
    }

    private void CancelPlaybackPreferenceRestore()
    {
        ++_subtitleSelectionRevision;
        _savedLocalSubtitleActivationPending = false;
        _playbackPreferenceRestoreRequest?.Cancel();
        _playbackPreferenceRestoreRequest?.Dispose();
        _playbackPreferenceRestoreRequest = null;
        _subtitleSelectionRequest?.Cancel();
        _subtitleSelectionRequest?.Dispose();
        _subtitleSelectionRequest = null;
        _restoringPlaybackPreferences = false;
    }

    private void ResetPlaybackPreferenceSession()
    {
        BeginPlaybackPreferenceRequest();
        _playbackPreferences = new();
        _appliedAudioDelaySeconds = _appliedSubtitleDelaySeconds = 0;
        _subtitleDelay = 0;
        SynchronizePlaybackPreferenceControls(_playbackPreferences);
    }

    private async Task<PlaybackPreferences> PreparePlaybackPreferencesForItemAsync(ConnectedSession session,
        string itemId, long intent, int? explicitSubtitleIndex, CancellationToken cancellationToken,
        PlaybackPreferenceReplay? replay = null)
    {
        var engine = _engine;
        var coordinator = _coordinator;
        bool OwnsRequest() => ReferenceEquals(session, _session) && ReferenceEquals(engine, _engine)
            && ReferenceEquals(coordinator, _coordinator) && intent == _playIntent && _item?.Id == itemId;
        await _playbackPreferenceWrite;
        cancellationToken.ThrowIfCancellationRequested();
        if (!OwnsRequest() || engine is null) throw new OperationCanceledException(cancellationToken);
        // The scope also isolates in-memory replays; disk access still requires AllowLocalPersistence.
        PlaybackPreferenceScope.TryCreate(session.Server.Id, session.User.Id, itemId, out var scope);
        var retained = PlaybackPreferenceRestorePolicy.ResolveReplayPreferences(replay, scope);
        var saved = retained ?? new PlaybackPreferences();
        if (retained is null && session.AllowLocalPersistence && scope is not null)
        {
            try
            {
                saved = await _playbackPreferenceStore.LoadAsync(scope, cancellationToken) ?? saved;
                if (OwnsRequest() && _playbackPreferenceStore.LastIssue == PlaybackPreferencePersistenceIssue.InvalidData)
                    ShowPlaybackPreferenceNotice("Saved playback settings could not be read. Default controls are in use.");
            }
            catch (PlaybackPreferenceStoreException)
            {
                if (OwnsRequest()) ShowPlaybackPreferenceNotice("Playback settings could not be read. Check access to the application data folder.");
            }
        }
        if (!OwnsRequest()) throw new OperationCanceledException(cancellationToken);
        var selection = PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(saved, explicitSubtitleIndex);
        _playbackPreferenceScope = scope;
        _playbackPreferenceIntent = intent;
        _subtitleSelectionRequest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _playbackPreferences = saved with { SubtitleSelection = selection,
            SelectedLocalSubtitlePath = selection == PlaybackSubtitleSelection.Local ? saved.SelectedLocalSubtitlePath : null };
        _savedLocalSubtitleActivationPending = (session.AllowLocalPersistence || retained is not null)
            && PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(saved, explicitSubtitleIndex);
        _subtitleDelay = saved.SubtitleDelaySeconds;
        _appliedSubtitleDelaySeconds = saved.SubtitleDelaySeconds;
        _appliedAudioDelaySeconds = 0;
        _playbackPreferencesRestoredId = null;
        _playbackPreferenceControlsReadyId = null;
        _playbackPreferenceFilesRestoredIntent = session.AllowLocalPersistence || retained is not null ? -1 : intent;
        _lastQueuedPlaybackPreferences = null;
        SynchronizePlaybackPreferenceControls(saved);
        await engine.ConfigureNextPlaybackPreferencesAsync(saved.VolumeLevel, saved.IsMuted, saved.PlaybackRate,
            saved.AudioDelaySeconds, saved.SubtitleDelaySeconds, cancellationToken);
        if (!OwnsRequest()) throw new OperationCanceledException(cancellationToken);
        return saved;
    }

    private PlaybackPreferenceReplay? CapturePlaybackPreferencesForReplay() =>
        _playbackPreferenceIntent == _playIntent && _playbackPreferenceScope is { } scope
            ? new(scope, _playbackPreferences with { LocalSubtitles = _playbackPreferences.LocalSubtitles.ToArray() })
            : null;

    private void SynchronizePlaybackPreferenceControls(PlaybackPreferences preferences)
    {
        var wasUpdating = _updating;
        _updating = true;
        Volume.Value = preferences.VolumeLevel;
        MuteButton.IsChecked = preferences.IsMuted;
        RateText.Text = FormatPlaybackRate(preferences.PlaybackRate);
        _updating = wasUpdating;
        UpdateVolumeIcon();
    }

    private void ObservePlaybackPreferenceSnapshot(PlaybackEngineSnapshot snapshot)
    {
        if (_playbackPreferenceIntent != _playIntent
            || _coordinator?.ActiveContext?.PlaybackId != snapshot.PlaybackId
            || _playbackPreferencesRestoredId != snapshot.PlaybackId
            || _playbackPreferenceControlsReadyId != snapshot.PlaybackId
            || snapshot.State is not (PlaybackEngineState.Playing or PlaybackEngineState.Paused
                or PlaybackEngineState.Buffering or PlaybackEngineState.Seeking)) return;
        if (_engine?.SupportsAudioDelay == true) _appliedAudioDelaySeconds = _engine.AudioDelaySeconds;
        SynchronizePlaybackPreferenceControls(_playbackPreferences with
        { VolumeLevel = snapshot.VolumeLevel, IsMuted = snapshot.IsMuted, PlaybackRate = snapshot.PlaybackRate });
        SavePlaybackPreferences(snapshot);
    }

    private void SavePlaybackPreferences(PlaybackEngineSnapshot? snapshot = null)
    {
        var scope = _playbackPreferenceScope;
        var session = _session;
        var engine = _engine;
        var context = _coordinator?.ActiveContext;
        var intent = _playIntent;
        snapshot ??= engine?.Snapshot;
        if (session is null || engine is null || snapshot is null || context is null
            || _playbackPreferenceIntent != intent
            || _playbackPreferencesRestoredId != snapshot.PlaybackId || context.PlaybackId != snapshot.PlaybackId
            || _playbackPreferenceControlsReadyId != snapshot.PlaybackId
            || snapshot.State is not (PlaybackEngineState.Playing or PlaybackEngineState.Paused
                or PlaybackEngineState.Buffering or PlaybackEngineState.Seeking)) return;
        if (engine.SupportsAudioDelay) _appliedAudioDelaySeconds = engine.AudioDelaySeconds;
        var actual = _playbackPreferences with
        {
            VolumeLevel = Math.Clamp(snapshot.VolumeLevel, 0, 100), IsMuted = snapshot.IsMuted,
            PlaybackRate = double.IsFinite(snapshot.PlaybackRate) ? Math.Clamp(snapshot.PlaybackRate, .5, 2) : 1,
            AudioDelaySeconds = _appliedAudioDelaySeconds, SubtitleDelaySeconds = _appliedSubtitleDelaySeconds,
            LocalSubtitles = _playbackPreferences.LocalSubtitles.ToArray()
        };
        _playbackPreferences = actual;
        // Temporary sessions retain live controls in memory without touching the persisted account cache.
        if (!session.AllowLocalPersistence || scope is null) return;
        if (actual.ContentEquals(_lastQueuedPlaybackPreferences)) return;
        _lastQueuedPlaybackPreferences = actual;
        _playbackPreferenceWrite = WritePlaybackPreferencesAsync(_playbackPreferenceWrite, scope, actual, session, intent);
    }

    private async Task WritePlaybackPreferencesAsync(Task previous, PlaybackPreferenceScope scope,
        PlaybackPreferences preferences, ConnectedSession session, long intent)
    {
        if (!session.AllowLocalPersistence) return;
        await previous;
        try { await _playbackPreferenceStore.SaveAsync(scope, preferences); }
        catch (PlaybackPreferenceStoreException)
        {
            if (ReferenceEquals(session, _session) && intent == _playIntent && scope == _playbackPreferenceScope)
            {
                _lastQueuedPlaybackPreferences = null;
                ShowPlaybackPreferenceNotice("Playback settings could not be saved. Check access to the application data folder.");
            }
        }
    }

    private async void RestorePlaybackPreferencesForContext(PlaybackContext context)
    {
        var session = _session;
        var engine = _engine;
        var coordinator = _coordinator;
        var scope = _playbackPreferenceScope;
        var itemId = _item?.Id;
        var intent = _playIntent;
        var revision = _subtitleSelectionRevision;
        if (session is null || engine is null || coordinator is null || _playbackPreferenceIntent != intent
            || _restoringPlaybackPreferences || _playbackPreferencesRestoredId == context.PlaybackId
            || coordinator.ActiveContext?.PlaybackId != context.PlaybackId
            || coordinator.Status is not (PlaybackStatus.Playing or PlaybackStatus.Paused)) return;
        _restoringPlaybackPreferences = true;
        _playbackPreferencesRestoredId = context.PlaybackId;
        var request = CancellationTokenSource.CreateLinkedTokenSource(_playRequest?.Token ?? CancellationToken.None);
        _playbackPreferenceRestoreRequest = request;
        var token = request.Token;
        bool OwnsItem() => ReferenceEquals(session, _session) && ReferenceEquals(engine, _engine)
            && ReferenceEquals(coordinator, _coordinator) && intent == _playIntent && _item?.Id == itemId
            && scope == _playbackPreferenceScope && _playbackPreferenceIntent == intent;
        bool OwnsPlayback() => OwnsItem() && revision == _subtitleSelectionRevision
            && coordinator.ActiveContext?.PlaybackId == context.PlaybackId;
        try
        {
            var saved = _playbackPreferences;
            await engine.SetVolumeAsync(context.PlaybackId, saved.VolumeLevel, saved.IsMuted, token);
            if (!OwnsPlayback()) return;
            try { await engine.SetPlaybackRateAsync(context.PlaybackId, saved.PlaybackRate, token); }
            catch (PlaybackException exception) when (exception.ErrorCode == "PlaybackRateNotSupported")
            { if (saved.PlaybackRate != 1 && OwnsPlayback()) ShowOperationError(exception); }
            if (!OwnsPlayback()) return;
            if (engine.SupportsAudioDelay)
            {
                try { await engine.SetAudioDelayAsync(context.PlaybackId, saved.AudioDelaySeconds, token); }
                catch (PlaybackException exception) when (exception.ErrorCode == "AudioDelayNotSupported")
                { if (OwnsPlayback()) ShowOperationError(exception); }
                if (!OwnsPlayback()) return;
                _appliedAudioDelaySeconds = engine.AudioDelaySeconds;
            }
            else if (saved.AudioDelaySeconds != 0)
                ShowPlaybackPreferenceNotice("Saved audio delay is unavailable for this stream.");
            _playbackPreferenceControlsReadyId = context.PlaybackId;
            if (engine.Snapshot is { } restoredSnapshot) ObservePlaybackPreferenceSnapshot(restoredSnapshot);
            if (_playbackPreferenceFilesRestoredIntent != intent)
            {
                var filesCompleted = await RestoreSavedLocalSubtitleChoicesAsync(saved, engine, coordinator,
                    revision, OwnsItem, token);
                if (filesCompleted && OwnsItem()) _playbackPreferenceFilesRestoredIntent = intent;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (OwnsItem()) ShowOperationError(exception); }
        finally
        {
            if (ReferenceEquals(request, _playbackPreferenceRestoreRequest))
            {
                _playbackPreferenceRestoreRequest = null;
                _restoringPlaybackPreferences = false;
                if (OwnsItem())
                {
                    var activeId = coordinator.ActiveContext?.PlaybackId;
                    _playbackPreferencesRestoredId = _playbackPreferenceControlsReadyId == activeId
                        && (_playbackPreferenceFilesRestoredIntent == intent || revision != _subtitleSelectionRevision
                            || context.PlaybackId == activeId) ? activeId : null;
                    if (engine.Snapshot is { } snapshot) ObservePlaybackPreferenceSnapshot(snapshot);
                    UpdateSubtitleControls();
                    UpdateQueueControls();
                }
            }
            request.Dispose();
        }
    }

    private async Task<bool> RestoreSavedLocalSubtitleChoicesAsync(PlaybackPreferences saved,
        NativePlaybackEngine engine, PlaybackCoordinator coordinator, long revision,
        Func<bool> ownsItem, CancellationToken cancellationToken)
    {
        if (_session is null || saved.LocalSubtitles.Length == 0) return true;
        var failures = 0;
        var selectedFailed = false;
        var selectedChanged = false;
        foreach (var file in saved.LocalSubtitles.OrderByDescending(file =>
            string.Equals(file.Path, saved.SelectedLocalSubtitlePath, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ownsItem() || revision != _subtitleSelectionRevision) return false;
            var selected = _savedLocalSubtitleActivationPending
                && string.Equals(file.Path, saved.SelectedLocalSubtitlePath, StringComparison.OrdinalIgnoreCase);
            try
            {
                var data = await PlaybackSubtitleFile.ReadVerifiedAsync(file, cancellationToken);
                if (!ownsItem() || revision != _subtitleSelectionRevision || coordinator.ActiveContext is not { } active) return false;
                var prepared = await engine.PrepareLocalSubtitleAsync(active.PlaybackId, Path.GetFileName(file.Path), data, cancellationToken);
                if (!ownsItem() || revision != _subtitleSelectionRevision || coordinator.ActiveContext?.PlaybackId != active.PlaybackId) return false;
                _localSubtitleFiles[prepared] = file;
                AddLocalSubtitleChoice(prepared);
                if (selected)
                {
                    await ApplyPreparedLocalSubtitleAsync(prepared, cancellationToken, revision);
                    if (!ownsItem() || revision != _subtitleSelectionRevision) return false;
                    _savedLocalSubtitleActivationPending = false;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                if (!ownsItem() || revision != _subtitleSelectionRevision) return false;
                failures++;
                selectedFailed |= selected;
                selectedChanged |= selected && exception is LocalSubtitleRestoreException { Issue: LocalSubtitleRestoreIssue.Changed };
            }
        }
        if (!ownsItem()) return false;
        if (selectedFailed)
            ShowPlaybackPreferenceNotice(selectedChanged
                ? "The saved local subtitle file has changed. Select it again to confirm the new contents. The server subtitle selection has been kept."
                : "The selected local subtitle could not be restored. Select the file again. The server subtitle selection has been kept.");
        else if (failures > 0)
            ShowPlaybackPreferenceNotice("Some saved local subtitles are unavailable. Select the files again to restore them.");
        _trackRowsSnapshots.Remove(SubtitleTrackItems);
        RebuildTrackPanel();
        return true;
    }

    private void RegisterLocalSubtitleFile(NativePreparedSubtitle prepared, string? path, byte[] data)
    {
        if (PlaybackSubtitleFile.Capture(path, data) is { } file) _localSubtitleFiles[prepared] = file;
    }

    private void AddLocalSubtitleChoice(NativePreparedSubtitle prepared)
    {
        _localSubtitleChoices.RemoveAll(choice => ReferenceEquals(choice, prepared)
            || _localSubtitleFiles.TryGetValue(choice, out var existing) && _localSubtitleFiles.TryGetValue(prepared, out var incoming)
                && string.Equals(existing.Path, incoming.Path, StringComparison.OrdinalIgnoreCase));
        _localSubtitleChoices.Add(prepared);
        while (_localSubtitleChoices.Count > PlaybackPreferenceStore.MaximumLocalSubtitles) _localSubtitleChoices.RemoveAt(0);
        foreach (var old in _localSubtitleFiles.Keys.Where(choice => !_localSubtitleChoices.Contains(choice)).ToArray())
            _localSubtitleFiles.Remove(old);
    }

    private bool IsLocalSubtitleChoiceSelected(NativePreparedSubtitle prepared) => ReferenceEquals(prepared, _selectedLocalSubtitle)
        && _localSubtitleName is not null;

    private string? LocalSubtitleChoicePath(NativePreparedSubtitle prepared) =>
        _localSubtitleFiles.TryGetValue(prepared, out var file) ? file.Path : null;

    private void RememberLocalSubtitleSelection(NativePreparedSubtitle prepared)
    {
        if (_playbackPreferenceControlsReadyId is not null && _coordinator?.ActiveContext is { } active)
            _playbackPreferenceControlsReadyId = _playbackPreferencesRestoredId = active.PlaybackId;
        if (!_localSubtitleFiles.TryGetValue(prepared, out var file))
        {
            _playbackPreferences = _playbackPreferences with
            { SubtitleSelection = PlaybackSubtitleSelection.Off, SelectedLocalSubtitlePath = null };
            ShowPlaybackPreferenceNotice("This local subtitle has no reusable file path. Select the file again after restarting the app.");
        }
        else
        {
            var files = _playbackPreferences.LocalSubtitles.Where(existing =>
                !string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase)).Append(file)
                .TakeLast(PlaybackPreferenceStore.MaximumLocalSubtitles).ToArray();
            _playbackPreferences = _playbackPreferences with
            { SubtitleSelection = PlaybackSubtitleSelection.Local, SelectedLocalSubtitlePath = file.Path, LocalSubtitles = files };
        }
        SavePlaybackPreferences();
    }

    private long BeginExplicitSubtitleSelection()
    {
        ++_subtitleSelectionRevision;
        _savedLocalSubtitleActivationPending = false;
        _playbackPreferenceRestoreRequest?.Cancel();
        _subtitleSelectionRequest?.Cancel();
        _subtitleSelectionRequest?.Dispose();
        _subtitleSelectionRequest = CancellationTokenSource.CreateLinkedTokenSource(_playRequest?.Token ?? CancellationToken.None);
        return _subtitleSelectionRevision;
    }

    private CancellationToken SubtitleSelectionToken => _subtitleSelectionRequest?.Token
        ?? _playRequest?.Token ?? CancellationToken.None;

    private void RememberServerSubtitleSelection(int index)
    {
        _localSubtitleName = null;
        _localSubtitlePlaybackId = null;
        _selectedLocalSubtitle = null;
        _localSubtitleRestorePlaybackId = null;
        _playbackPreferences = _playbackPreferences with
        { SubtitleSelection = index == -1 ? PlaybackSubtitleSelection.Off : PlaybackSubtitleSelection.Server,
            SelectedLocalSubtitlePath = null };
        SavePlaybackPreferences();
    }

    private void RememberAppliedSubtitleDelay(NativePlaybackEngine engine, Guid playbackId, double delay)
    {
        if (!ReferenceEquals(engine, _engine) || _coordinator?.ActiveContext?.PlaybackId != playbackId) return;
        _appliedSubtitleDelaySeconds = delay;
        SavePlaybackPreferences();
    }

    private void ShowPlaybackPreferenceNotice(string message)
    {
        if (_coordinator?.Status == PlaybackStatus.Failed || PlaybackNotice.IsOpen && PlaybackNotice.Severity == InfoBarSeverity.Error) return;
        PlaybackNotice.Title = LumenText.Get("Playback settings");
        PlaybackNotice.Message = LumenText.Get(message);
        PlaybackNotice.Severity = InfoBarSeverity.Warning;
        PlaybackNotice.IsClosable = true;
        PlaybackNoticeHost.VerticalAlignment = VerticalAlignment.Top;
        PlaybackNotice.IsOpen = true;
        UpdateRecoveryControls();
    }
}
