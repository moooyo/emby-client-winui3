using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.Playback;
using LastWatchedIdentity = EmbyClient.App.Services.LastWatchedItem;

namespace EmbyClient.App.Views;

public sealed partial class PlayerView
{
    private readonly LastWatchedStore _lastWatchedStore = new();
    private LastWatchedItem? _lastWatchedItem;
    private LastWatchedItem? _preparedActivationItem;
    private NativePlaybackEngine? _activationEngine;
    private PlaybackCoordinator? _activationCoordinator;
    private CancellationTokenSource? _activationLifetime;
    private Task _pendingLastWatchedSave = Task.CompletedTask;
    private Guid? _recordedActivationPlaybackId;
    private long _activationEpoch;

    public event EventHandler? LastWatchedChanged;
    public event EventHandler<WindowsActivationRoute>? IdleResumeRequested;

    public LastWatchedItem? LastWatchedItem => _session is { } session
        && _lastWatchedItem is { } last && last.Route.MatchesAccount(session.AccountKey) ? last : null;

    private async Task ConfigureIdlePlaybackPresentationAsync(ConnectedSession session)
    {
        ClearIdlePlaybackPresentation();
        if (!ReferenceEquals(session, _session) || _engine is not { } engine || _coordinator is not { } coordinator) return;
        var epoch = ++_activationEpoch;
        var lifetime = new CancellationTokenSource();
        _activationLifetime = lifetime;
        _activationEngine = engine;
        _activationCoordinator = coordinator;
        engine.IdleResumeRequested += IdleMediaResumeRequested;
        coordinator.StatusChanged += ActivationPlaybackStatusChanged;
        try
        {
            var last = session.AllowLocalPersistence
                ? await _lastWatchedStore.LoadAsync(session.AccountKey, lifetime.Token)
                : null;
            if (epoch != _activationEpoch || !ReferenceEquals(session, _session) || !ReferenceEquals(engine, _engine)) return;
            _lastWatchedItem = last;
            UpdateIdlePlaybackTarget();
            LastWatchedChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Last-watched history load failed (0x{0:X8}).", exception.HResult);
        }
    }

    private void PlaybackItemPreparedForActivation(ConnectedSession session, BaseItemDto detail)
    {
        if (!ReferenceEquals(session, _session) || !WindowsActivationRoute.IsItemId(detail.Id)
            || !WindowsActivationRoute.IsAccountKey(session.AccountKey)) return;
        _preparedActivationItem = new LastWatchedItem(session.AccountKey, detail.Id!,
            LastWatchedIdentity.DisplayText(PlaybackTitle(detail, detail.Name), "Lumen"),
            LastWatchedIdentity.DisplayText(PlaybackSubtitle(detail)), DateTimeOffset.UtcNow);
    }

    private void ActivationPlaybackStatusChanged(object? sender, PlaybackStatusChangedEventArgs args)
    {
        var epoch = Volatile.Read(ref _activationEpoch);
        // The coordinator can publish from a worker. Keep this callback behind the existing presentation
        // callback so terminal controls are retired before installing an idle card on the same native player.
        DispatcherQueue.TryEnqueue(() => ApplyActivationPlaybackStatus(sender, args, epoch));
    }

    private void ApplyActivationPlaybackStatus(object? sender, PlaybackStatusChangedEventArgs args, long epoch)
    {
        if (epoch != _activationEpoch || !ReferenceEquals(sender, _activationCoordinator)
            || !ReferenceEquals(sender, _coordinator)) return;
        if (args.Status is PlaybackStatus.Negotiating or PlaybackStatus.Opening)
        {
            if (_coordinator?.Status != args.Status) return;
            _activationEngine?.SetIdleMediaControlsTarget(null);
            return;
        }
        if (args.Status is PlaybackStatus.Idle or PlaybackStatus.Ended or PlaybackStatus.Failed)
        {
            if (_coordinator?.Status != args.Status) return;
            UpdateIdlePlaybackTarget();
            return;
        }
        if (args.Status != PlaybackStatus.Playing || args.Context is not { } context
            || _coordinator?.ActiveContext?.PlaybackId != context.PlaybackId
            || context.PlaybackId == _recordedActivationPlaybackId || _session is not { } session
            || _preparedActivationItem is not { } prepared || prepared.ItemId != context.Selection.ItemId
            || !prepared.Route.MatchesAccount(session.AccountKey) || _activationLifetime is not { } lifetime) return;
        _recordedActivationPlaybackId = context.PlaybackId;
        var last = prepared with { WatchedAt = DateTimeOffset.UtcNow };
        _lastWatchedItem = last;
        UpdateIdlePlaybackTarget();
        LastWatchedChanged?.Invoke(this, EventArgs.Empty);
        if (session.AllowLocalPersistence)
            _pendingLastWatchedSave = SaveLastWatchedAsync(session, last, _activationEpoch, lifetime.Token);
    }

    private async Task SaveLastWatchedAsync(ConnectedSession session, LastWatchedItem last, long epoch,
        CancellationToken cancellationToken)
    {
        try
        {
            if (epoch != _activationEpoch || !ReferenceEquals(session, _session) || !session.AllowLocalPersistence) return;
            await _lastWatchedStore.SaveAsync(last, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceWarning("Last-watched history save failed (0x{0:X8}).", exception.HResult);
        }
    }

    private void UpdateIdlePlaybackTarget()
    {
        var allowed = _session is not null && !_expiredReported && _session.User.Policy?.EnableMediaPlayback != false;
        var last = allowed ? LastWatchedItem : null;
        _activationEngine?.SetIdleMediaControlsTarget(last is null ? null
            : new NativeIdleMediaTarget(last.AccountKey, last.ItemId, last.Title, last.Subtitle));
    }

    private void IdleMediaResumeRequested(object? sender, NativeIdleMediaTarget target)
    {
        if (!ReferenceEquals(sender, _engine) || !ReferenceEquals(sender, _activationEngine)
            || !_preferences.SystemMediaControls || _expiredReported || _session is not { } session
            || !string.Equals(target.AccountKey, session.AccountKey, StringComparison.OrdinalIgnoreCase)
            || LastWatchedItem?.ItemId != target.ItemId
            || _preparationIntent == _playIntent || _coordinator?.Status is PlaybackStatus.Negotiating
                or PlaybackStatus.Opening or PlaybackStatus.Playing or PlaybackStatus.Paused
                or PlaybackStatus.Buffering or PlaybackStatus.Seeking or PlaybackStatus.Stopping) return;
        IdleResumeRequested?.Invoke(this, new WindowsActivationRoute(WindowsActivationDestination.ContinueWatching,
            target.AccountKey, target.ItemId));
    }

    private void ClearIdlePlaybackPresentation()
    {
        _activationEpoch++;
        _activationLifetime?.Cancel();
        _activationLifetime?.Dispose();
        _activationLifetime = null;
        if (_activationEngine is { } engine)
        {
            engine.SetIdleMediaControlsTarget(null);
            engine.IdleResumeRequested -= IdleMediaResumeRequested;
        }
        if (_activationCoordinator is { } coordinator) coordinator.StatusChanged -= ActivationPlaybackStatusChanged;
        _activationEngine = null;
        _activationCoordinator = null;
        _preparedActivationItem = null;
        _recordedActivationPlaybackId = null;
        _lastWatchedItem = null;
        LastWatchedChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ForgetLastWatchedAccountAsync(string accountKey)
    {
        // Disconnect cancels all pending writes before account cleanup. The store gate orders removal after them.
        await _pendingLastWatchedSave;
        await _lastWatchedStore.RemoveAccountAsync(accountKey);
    }

    public void RetireWindowsMediaIntegration()
    {
        _engine?.SetSystemMediaControlsEnabled(false);
        ClearIdlePlaybackPresentation();
    }

    public Task DrainLastWatchedPersistenceAsync() => _pendingLastWatchedSave;
}
