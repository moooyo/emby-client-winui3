using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private WindowsActivationRoute? _pendingWindowsActivation;
    private CancellationTokenSource? _windowsActivationRequest;
    private string? _windowsActivationAccountKey;
    private long _windowsActivationIntent;
    private bool _processingWindowsActivation;
    private bool _windowsActivationStopped;
    private IReadOnlyList<LastWatchedItem> _windowsContinueWatching = [];
    private long _windowsResumeVersion;

    public event EventHandler? WindowsIntegrationStateChanged;
    public event EventHandler? WindowsIntegrationPreferencesChanged;
    public event EventHandler? ForegroundRequested;
    public LumenPreferences WindowsIntegrationPreferences => _preferences;
    public string? WindowsIntegrationAccountKey => _session is { } session
        && session.AllowLocalPersistence && _windowsActivationAccountKey == session.AccountKey ? session.AccountKey : null;
    public IReadOnlyList<LastWatchedItem> WindowsContinueWatching => WindowsIntegrationAccountKey is not null
        ? _windowsContinueWatching : [];
    public LastWatchedItem? WindowsLastWatched => _session is { } session
        && _windowsActivationAccountKey == session.AccountKey ? _player.LastWatchedItem : null;

    private void InitializeWindowsActivation()
    {
        _player.LastWatchedChanged += WindowsLastWatchedChanged;
        _player.IdleResumeRequested += IdlePlaybackResumeRequested;
    }

    private void NotifyWindowsActivationPreferences() =>
        WindowsIntegrationPreferencesChanged?.Invoke(this, EventArgs.Empty);

    private void WindowsLastWatchedChanged(object? sender, EventArgs args)
    {
        if (_windowsActivationStopped || _closing) return;
        WindowsIntegrationStateChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshWindowsResumeItemsAsync();
    }

    private async Task RefreshWindowsResumeItemsAsync()
    {
        var session = _session;
        if (session is null || _closing || _windowsActivationStopped
            || _windowsActivationAccountKey != session.AccountKey) return;
        var epoch = _sessionEpoch;
        var version = ++_windowsResumeVersion;
        try
        {
            var result = await session.Api.GetResumeItemsAsync(new ItemQuery
            {
                Limit = WindowsJumpListPresentation.MaximumContinueItems,
                MediaTypes = ["Video"], EnableUserData = true
            }, _sessionLifetime?.Token ?? CancellationToken.None);
            if (version != _windowsResumeVersion || epoch != _sessionEpoch || _closing
                || !ReferenceEquals(session, _session) || _windowsActivationAccountKey != session.AccountKey) return;
            _windowsContinueWatching = WindowsJumpListResumeItems.Create(session.AccountKey, result.Items);
            WindowsIntegrationStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyTransportException or EmbyProtocolException or TimeoutException)
        {
            if (version == _windowsResumeVersion && epoch == _sessionEpoch && !_closing)
                System.Diagnostics.Trace.TraceWarning("Resume tasks could not be refreshed (0x{0:X8}).", exception.HResult);
        }
    }

    private void IdlePlaybackResumeRequested(object? sender, WindowsActivationRoute route)
    {
        if (!_preferences.SystemMediaControls || _closing || _windowsActivationStopped) return;
        ForegroundRequested?.Invoke(this, EventArgs.Empty);
        ActivateWindowsRoute(route);
    }

    public void ActivateWindowsRoute(WindowsActivationRoute route)
    {
        if (_closing || _windowsActivationStopped || !route.IsValid) return;
        CancelEpisodeNotificationActivation(clearPending: true);
        _pendingWindowsActivation = route;
        _windowsActivationIntent++;
        _windowsActivationRequest?.Cancel();
        if (_processingWindowsActivation) _resumeDialog?.Hide();
        ResumePendingWindowsActivation();
    }

    private Task ActivateWindowsSessionAsync(ConnectedSession session)
    {
        if (!ReferenceEquals(session, _session) || _closing || _transitioning || _windowsActivationStopped) return Task.CompletedTask;
        _windowsActivationAccountKey = session.AccountKey;
        WindowsIntegrationStateChanged?.Invoke(this, EventArgs.Empty);
        _ = RefreshWindowsResumeItemsAsync();
        return ProcessPendingWindowsActivationAsync();
    }

    private async void ResumePendingWindowsActivation()
    {
        try { await ProcessPendingWindowsActivationAsync(); }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
    }

    private async Task ProcessPendingWindowsActivationAsync()
    {
        if (_processingWindowsActivation || _session is null || _windowsActivationAccountKey != _session.AccountKey
            || _startingPlayback || _transitioning || _returning || _closing || _windowsActivationStopped
            || _shellDialog is not null) return;
        _processingWindowsActivation = true;
        try
        {
            while (_pendingWindowsActivation is { } route && _session is { } session
                && _windowsActivationAccountKey == session.AccountKey && !_closing && !_transitioning && !_windowsActivationStopped)
            {
                _pendingWindowsActivation = null;
                var intent = _windowsActivationIntent;
                var epoch = _sessionEpoch;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_sessionLifetime?.Token ?? CancellationToken.None);
                _windowsActivationRequest = request;
                bool OwnsActivation() => !request.IsCancellationRequested && intent == _windowsActivationIntent
                    && epoch == _sessionEpoch && ReferenceEquals(session, _session) && !_closing && !_transitioning;
                try
                {
                    if (!route.MatchesAccount(session.AccountKey))
                    {
                        ShowNotice(LumenText.Get("This activation belongs to another account."), InfoBarSeverity.Warning);
                        continue;
                    }
                    if (IsPlayerVisible) await ReturnToLibraryAsync();
                    if (!OwnsActivation() || IsPlayerVisible) continue;
                    if (route.Destination == WindowsActivationDestination.ContinueWatching)
                    {
                        if (session.User.Policy?.EnableMediaPlayback == false)
                        {
                            ShowNotice(LumenText.Get("Playback is disabled for this account."), InfoBarSeverity.Warning);
                            continue;
                        }
                        // Fetch under the current authenticated user. No persisted position, source, or URL
                        // can bypass the usual preparation, negotiation, or the configured resume policy.
                        var item = await session.Api.GetItemAsync(route.ItemId!, request.Token);
                        if (!OwnsActivation()) continue;
                        if (item is null || item.Id != route.ItemId || item.IsFolder == true)
                        {
                            ShowNotice(LumenText.Get("The last-watched item is no longer available."), InfoBarSeverity.Warning);
                            continue;
                        }
                        await StartLibraryPlaybackAsync(new LumenPlayRequestEventArgs(item,
                            Math.Max(0, item.UserData?.PlaybackPositionTicks ?? 0)));
                    }
                    else
                    {
                        if (route.Destination == WindowsActivationDestination.SwitchAccount)
                        {
                            if (OwnsActivation()) await LeaveSessionAsync(false);
                            continue;
                        }
                        var destination = route.Destination switch
                        {
                            WindowsActivationDestination.Search => "search",
                            WindowsActivationDestination.Settings => "settings",
                            WindowsActivationDestination.Library when _library.ViewModel.Libraries.FirstOrDefault() is { Id: { } id }
                                => $"library:{id}",
                            _ => "home"
                        };
                        if (OwnsActivation()) await NavigateAsync(destination);
                    }
                }
                catch (OperationCanceledException) when (request.IsCancellationRequested) { }
                catch (Exception exception) { if (OwnsActivation()) ShowError(exception); }
                finally
                {
                    if (ReferenceEquals(_windowsActivationRequest, request)) _windowsActivationRequest = null;
                }
            }
        }
        finally { _processingWindowsActivation = false; ScheduleServerRefresh(); }
    }

    private async Task ClearWindowsSessionActivationAsync(ConnectedSession session, bool forgetAccount)
    {
        _windowsActivationAccountKey = null;
        _windowsContinueWatching = [];
        _windowsResumeVersion++;
        _pendingWindowsActivation = null;
        _windowsActivationIntent++;
        _windowsActivationRequest?.Cancel();
        _player.RetireWindowsMediaIntegration();
        WindowsIntegrationStateChanged?.Invoke(this, EventArgs.Empty);
        if (forgetAccount)
        {
            try { await _player.ForgetLastWatchedAccountAsync(session.AccountKey); }
            catch (Exception exception)
            {
                if (!_closing) ShowError(exception);
                System.Diagnostics.Trace.TraceWarning("Last-watched account cleanup failed (0x{0:X8}).", exception.HResult);
            }
        }
    }

    private async Task ShutdownWindowsActivationAsync()
    {
        _windowsActivationStopped = true;
        _windowsResumeVersion++;
        _pendingWindowsActivation = null;
        _windowsActivationIntent++;
        _windowsActivationRequest?.Cancel();
        _player.LastWatchedChanged -= WindowsLastWatchedChanged;
        _player.IdleResumeRequested -= IdlePlaybackResumeRequested;
        // Retain the committed continuation task on normal exit, after its corresponding history write drains.
        await _player.DrainLastWatchedPersistenceAsync();
        _player.RetireWindowsMediaIntegration();
    }
}
