using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private (TextBox, TextBox, PasswordBox, CheckBox, Button, Button, ComboBox, StackPanel, StackPanel,
        ProgressRing, TextBlock, Button, TextBlock, TextBlock) BuildConnectionPane() => CreateConnectionPane();

    private void PopulateSavedAccounts()
    {
        var previousKey = (_savedAccounts.SelectedItem as ComboBoxItem)?.Tag is SavedAccount previous ? previous.Key : null;
        _savedAccounts.Items.Clear();
        foreach (var account in _connections.Settings.Accounts)
        {
            var host = Uri.TryCreate(account.ApiRoot, UriKind.Absolute, out var uri) ? uri.Host : account.ServerName;
            var option = new ComboBoxItem { Content = account.UserName + " \u00b7 " + host, Tag = account };
            _savedAccounts.Items.Add(option);
            if (account.Key == (_connections.Settings.LastAccountKey ?? previousKey)) _savedAccounts.SelectedItem = option;
        }
        if (_savedAccounts.SelectedIndex < 0 && _savedAccounts.Items.Count > 0) _savedAccounts.SelectedIndex = 0;
        if (_savedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount selected })
        {
            _address.Text = selected.ApiRoot;
            _userName.Text = selected.UserName;
            _selectedSignInUser = null;
        }
        RefreshKnownSignInServers();
        UpdateSavedSignInAction();
    }

    private void SetConnecting(bool connecting, string? message = null)
    {
        if (connecting) CancelServerDiscovery();
        var enabled = _settingsReady && !connecting && !_transitioning && !_closing;
        _address.IsEnabled = enabled;
        _userName.IsEnabled = enabled;
        _password.IsEnabled = enabled;
        _remember.IsEnabled = enabled && !_connections.IsTemporarySession;
        _signIn.IsEnabled = enabled;
        _savedAccounts.IsEnabled = enabled;
        _restore.IsEnabled = enabled && _savedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount { ProtectedToken.Length: > 0 } account }
            && _connections.GetAccountRestriction(account.Key) is null;
        _connecting.IsActive = connecting;
        _connecting.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        _connectionStatus.Text = connecting ? message ?? LumenText.Get("Connecting...") : string.Empty;
        _cancelConnection.Visibility = connecting && _connectionRequest is not null ? Visibility.Visible : Visibility.Collapsed;
        _cancelConnection.IsEnabled = _connectionRequest?.IsCancellationRequested != true;
        SetSignInControlsEnabled(enabled);
    }

    private bool ValidateCredentials()
    {
        var validServer = Uri.TryCreate(_address.Text.Trim(), UriKind.Absolute, out var address)
            && address.Scheme is "http" or "https" && address.UserInfo.Length == 0 && address.Query.Length == 0 && address.Fragment.Length == 0;
        var validUser = !string.IsNullOrWhiteSpace(_userName.Text);
        _serverError.Text = validServer ? string.Empty : LumenText.Get("Enter a complete HTTP or HTTPS server address.");
        _userError.Text = validUser ? string.Empty : LumenText.Get("Enter your username.");
        _serverError.Visibility = validServer ? Visibility.Collapsed : Visibility.Visible;
        _userError.Visibility = validUser ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(_address, _serverError.Text);
        AutomationProperties.SetHelpText(_userName, _userError.Text);
        if (!validServer) _address.Focus(FocusState.Programmatic);
        else if (!validUser) _userName.Focus(FocusState.Programmatic);
        return validServer && validUser;
    }

    private async Task ConnectAsync(SavedAccount? account = null)
    {
        if (!_settingsReady || _transitioning || _closing || _connectionRequest is not null) return;
        if (account is null && !ValidateCredentials()) return;
        _credentialError.Text = string.Empty;
        if (account is not null)
        {
            _address.Text = account.ApiRoot;
            _userName.Text = account.UserName;
        }
        var request = new CancellationTokenSource();
        _connectionRequest = request;
        SetConnecting(true);
        _notice.IsOpen = false;
        try
        {
            var session = account is null
                ? await _connections.SignInAsync(_address.Text, _userName.Text, _password.Password, _remember.IsChecked == true,
                    request.Token, expectedServerId: _selectedSignInServer?.ServerId)
                : await _connections.RestoreAsync(account, request.Token);
            if (_closing || _transitioning) return;
            await ActivateSessionAsync(session);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closing)
            {
                ShowError(exception);
                if (UiErrors.RequiresPassword(exception) || exception is ServerIdentityMismatchException) _password.Password = string.Empty;
                if (UiErrors.IsAccountRestriction(exception)) _accountRestriction = UiErrors.Describe(exception);
                _credentialError.Text = LumenText.Get(UiErrors.Describe(exception));
                if (UiErrors.RequiresPassword(exception) && _selectedSignInUser?.HasPassword == false)
                {
                    RequireSelectedSignInUserPassword();
                }
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionRequest, request)) _connectionRequest = null;
            request.Dispose();
            SetConnecting(false);
            if (!_closing && _session is null)
            {
                if (_password.Visibility == Visibility.Visible) _password.Focus(FocusState.Programmatic);
                else _signIn.Focus(FocusState.Programmatic);
            }
        }
    }

    private async Task ActivateSessionAsync(ConnectedSession session)
    {
        RecordStartupPhase("activate-session");
        _sessionEpoch++;
        _sessionLifetime?.Cancel();
        _sessionLifetime?.Dispose();
        _sessionLifetime = new CancellationTokenSource();
        _session = session;
        ClearSignInPasswordPrompts();
        CancelServerDiscovery();
        CancelSignInArtwork();
        var configuration = session.User.Configuration;
        var language = configuration?.SubtitleLanguagePreference;
        var validLanguage = language is not null && language.Length <= 16
            && language.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
        var mode = configuration?.SubtitleMode;
        ApplyPreferences(_preferences with
        {
            AutoPlayNext = configuration?.EnableNextEpisodeAutoPlay ?? true,
            SubtitleLanguage = validLanguage ? language! : string.Empty,
            SubtitleMode = mode is "Default" or "Smart" or "Always" or "OnlyForced" or "None" or "HearingImpaired" ? mode : "Default"
        });
        RecordStartupPhase("session-preferences-applied");
        _password.Password = string.Empty;
        _settingsVisible = false;
        _settings.SetSession(session);
        _settings.SetPreferences(_preferences);
        _settings.SetSubtitleStyleAvailable(Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Core.TimedTextSource"));
        _settings.Visibility = Visibility.Collapsed;
        _library.Visibility = Visibility.Visible;
        _connected.Visibility = Visibility.Visible;
        _connectionPane.Visibility = Visibility.Collapsed;
        RecordStartupPhase("session-content-visible");
        await _player.SetSessionAsync(session);
        RecordStartupPhase("player-session-ready");
        _player.ApplyPreferences(_preferences);
        _library.ApplyPreferences(_preferences);
        UpdateChrome();
        await _library.SetSessionAsync(session, _sessionLifetime.Token);
        RecordStartupPhase("library-session-ready");
        if (!ReferenceEquals(session, _session) || _closing || _transitioning) return;
        await RestoreMediaLibrarySelectionsAsync(session);
        if (!ReferenceEquals(session, _session) || _closing || _transitioning) return;
        await ActivateServerIntegrationAsync(session);
        await ActivateWindowsSessionAsync(session);
        if (ReferenceEquals(session, _session) && !_closing && session.PersistenceWarning is not null)
            ShowNotice(LumenText.Get(session.PersistenceWarning), InfoBarSeverity.Warning);
        UpdateChrome();
    }
}
