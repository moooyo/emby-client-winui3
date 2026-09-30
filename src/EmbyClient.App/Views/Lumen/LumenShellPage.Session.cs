using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private bool _startingPlayback;
    private TaskCompletionSource? _sessionExitCompletion;
    private ContentDialog? _resumeDialog;

    private async void LibraryPlayRequested(object? sender, LumenPlayRequestEventArgs args)
    {
        var session = _session;
        if (session is null || _transitioning || _returning || _closing || _startingPlayback) return;
        if (session.User.Policy?.EnableMediaPlayback == false)
        {
            ShowNotice(LumenText.Get("Playback is disabled for this account."), InfoBarSeverity.Warning);
            return;
        }
        if (args.AddToQueue)
        {
            var result = _player.Enqueue(args.Item);
            if (result != QueueAddResult.Added)
                ShowNotice(LumenText.Get(result == QueueAddResult.Full ? "The play queue is full." : "This item cannot be queued."), InfoBarSeverity.Warning);
            UpdateChrome();
            return;
        }
        _startingPlayback = true;
        try
        {
            var position = args.StartPositionTicks;
            if (position > 0 && _preferences.ResumeMode == "Restart") position = 0;
            else if (position > 0 && _preferences.ResumeMode == "Ask")
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, RequestedTheme = RequestedTheme,
                    Title = LumenText.Get("Resume playback"), Content = args.Item.Name ?? string.Empty,
                    PrimaryButtonText = LumenText.Get("Continue from last position"),
                    SecondaryButtonText = LumenText.Get("Play from beginning"), CloseButtonText = LumenText.Get("Cancel"),
                    DefaultButton = ContentDialogButton.Primary
                };
                LumenDialogTheme.Apply(dialog);
                _resumeDialog = dialog;
                ContentDialogResult choice;
                try { choice = await dialog.ShowAsync(); }
                finally { if (ReferenceEquals(_resumeDialog, dialog)) _resumeDialog = null; }
                if (choice == ContentDialogResult.None) return;
                if (choice == ContentDialogResult.Secondary) position = 0;
            }
            if (!ReferenceEquals(session, _session) || _closing || _transitioning) return;
            _notice.IsOpen = false;
            _settingsVisible = false;
            _player.Visibility = Visibility.Visible;
            _library.Visibility = Visibility.Collapsed;
            _settings.Visibility = Visibility.Collapsed;
            UpdateChrome();
            await _player.PlayItemAsync(args.Item, position, args.MediaSourceId, args.AudioStreamIndex, args.SubtitleStreamIndex);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(session, _session) && !_closing) ShowError(exception);
        }
        finally { _startingPlayback = false; }
    }

    private async Task ReturnToLibraryAsync()
    {
        var session = _session;
        if (_returning || _transitioning || _closing || session is null) return;
        _returning = true;
        UpdateChrome();
        try
        {
            ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
            await _player.StopAsync();
            if (!ReferenceEquals(session, _session) || _transitioning || _closing) return;
            _player.Visibility = Visibility.Collapsed;
            _library.Visibility = Visibility.Visible;
            _settingsVisible = false;
            UpdateChrome();
            await _library.RefreshAsync(_sessionLifetime?.Token ?? CancellationToken.None);
            if (ReferenceEquals(session, _session) && !_closing) _library.FocusCurrentDetails();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(session, _session) && !_closing) ShowError(exception);
        }
        finally { _returning = false; UpdateChrome(); }
    }

    private async Task LeaveSessionAsync(bool signOut, bool expired = false)
    {
        var session = _session;
        if (session is null || _transitioning || _closing) return;
        _transitioning = true;
        var exitCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionExitCompletion = exitCompletion;
        _sessionEpoch++;
        _session = null;
        _resumeDialog?.Hide();
        _sessionLifetime?.Cancel();
        _connectionRequest?.Cancel();
        _notice.IsOpen = false;
        ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
        _library.ClearSession();
        _player.Visibility = Visibility.Collapsed;
        _library.Visibility = Visibility.Visible;
        _settings.Visibility = Visibility.Collapsed;
        _settingsVisible = false;
        _connected.Visibility = Visibility.Collapsed;
        _connectionPane.Visibility = Visibility.Visible;
        _password.Password = string.Empty;
        UpdateChrome();
        SetConnecting(true, LumenText.Get("Closing the current session..."));
        try
        {
            Exception? localFailure = null;
            if (signOut || expired)
            {
                try { await _connections.ForgetTokenAsync(session.AccountKey); }
                catch (Exception exception) { localFailure = exception; }
            }
            await _player.DisconnectAsync();
            if (signOut)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var warning = await ConnectionService.RevokeSessionAsync(session, deadline.Token);
                if (warning is not null && !_closing) ShowNotice(LumenText.Get(warning), InfoBarSeverity.Warning);
            }
            if (localFailure is not null && !_closing) ShowError(localFailure);
            else if (expired && !_closing)
                ShowNotice(LumenText.Get("Your session has expired. Sign in again."), InfoBarSeverity.Warning);
        }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
        finally
        {
            try
            {
                _transitioning = false;
                _sessionLifetime?.Dispose();
                _sessionLifetime = null;
                if (!_closing)
                {
                    PopulateSavedAccounts();
                    SetConnecting(false);
                    _password.Focus(FocusState.Programmatic);
                    UpdateChrome();
                }
            }
            finally
            {
                if (ReferenceEquals(_sessionExitCompletion, exitCompletion)) _sessionExitCompletion = null;
                exitCompletion.TrySetResult();
            }
        }
    }

    public async Task ShutdownAsync()
    {
        if (_closing) return;
        _closing = true;
        _resumeDialog?.Hide();
        _sessionEpoch++;
        _connectionRequest?.Cancel();
        _sessionLifetime?.Cancel();
        _library.ClearSession();
        var sessionExit = _sessionExitCompletion?.Task;
        try
        {
            if (sessionExit is not null) await sessionExit;
            else await _player.DisconnectAsync();
            await _pendingPreferenceSave.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            _sessionLifetime?.Dispose();
            _sessionLifetime = null;
            _connections.Dispose();
        }
    }
}
