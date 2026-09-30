using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private (TextBox, TextBox, PasswordBox, CheckBox, Button, Button, ComboBox, StackPanel, StackPanel,
        ProgressRing, TextBlock, Button, TextBlock, TextBlock) BuildConnectionPane()
    {
        _connectionPane.Background = LumenTheme.Brush("Background");
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel
        {
            Width = 440, MaxWidth = 440, Spacing = 18, Margin = new(24, 104, 24, 48),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        brand.Children.Add(LumenUi.Icon("video_20_regular", 30));
        var title = LumenUi.Text("Lumen", 44, serif: true);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        brand.Children.Add(title);
        content.Children.Add(brand);
        var heading = LumenUi.Text(LumenText.Get("Sign in to your Emby server"), 22);
        heading.Margin = new(0, 12, 0, 6);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        content.Children.Add(heading);
        var saved = new ComboBox
        {
            Header = LumenText.Get("Saved accounts"), HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new(20), Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink")
        };
        var restore = LumenUi.Button(LumenText.Get("Continue with this account"), "person_20_regular", primary: true);
        restore.HorizontalAlignment = HorizontalAlignment.Stretch;
        var savedPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        savedPanel.Children.Add(saved);
        savedPanel.Children.Add(restore);
        savedPanel.Children.Add(LumenUi.Divider());
        content.Children.Add(savedPanel);

        var address = new TextBox
        {
            Header = LumenText.Get("Server address"), PlaceholderText = "https://media.example.com", CornerRadius = new(20),
            Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"), Padding = new(16, 10, 16, 10),
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Url) } }
        };
        address.Resources["TextControlBorderBrushFocused"] = LumenTheme.Brush("Accent");
        AutomationProperties.SetName(address, LumenText.Get("Server address"));
        AutomationProperties.SetAutomationId(address, "LumenServerAddress");
        content.Children.Add(address);
        var serverError = LumenUi.Text(string.Empty, 12);
        serverError.Foreground = LumenTheme.Brush("Danger");
        serverError.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(serverError, AutomationLiveSetting.Polite);
        content.Children.Add(serverError);
        var userName = new TextBox
        {
            Header = LumenText.Get("Username"), CornerRadius = new(20), Background = LumenTheme.Brush("Control"),
            Foreground = LumenTheme.Brush("Ink"), Padding = new(16, 10, 16, 10)
        };
        userName.Resources["TextControlBorderBrushFocused"] = LumenTheme.Brush("Accent");
        AutomationProperties.SetName(userName, LumenText.Get("Username"));
        AutomationProperties.SetAutomationId(userName, "LumenUsername");
        content.Children.Add(userName);
        var userError = LumenUi.Text(string.Empty, 12);
        userError.Foreground = LumenTheme.Brush("Danger");
        userError.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(userError, AutomationLiveSetting.Polite);
        content.Children.Add(userError);
        var password = new PasswordBox
        {
            Header = LumenText.Get("Password"), CornerRadius = new(20), Background = LumenTheme.Brush("Control"),
            Foreground = LumenTheme.Brush("Ink"), Padding = new(16, 10, 16, 10)
        };
        password.Resources["TextControlBorderBrushFocused"] = LumenTheme.Brush("Accent");
        AutomationProperties.SetName(password, LumenText.Get("Password"));
        AutomationProperties.SetAutomationId(password, "LumenPassword");
        content.Children.Add(password);
        var remember = new CheckBox { Content = LumenText.Get("Remember this account"), IsChecked = true };
        content.Children.Add(remember);
        var signIn = LumenUi.Button(LumenText.Get("Sign in"), "user", primary: true);
        signIn.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.Children.Add(signIn);
        var connectionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var connecting = new ProgressRing { Width = 20, Height = 20, IsActive = false, Visibility = Visibility.Collapsed };
        var status = LumenUi.Text(string.Empty, 13);
        status.VerticalAlignment = VerticalAlignment.Center;
        var cancel = LumenUi.Button(LumenText.Get("Cancel"), height: 32);
        cancel.Visibility = Visibility.Collapsed;
        connectionRow.Children.Add(connecting);
        connectionRow.Children.Add(status);
        connectionRow.Children.Add(cancel);
        content.Children.Add(connectionRow);
        var recovery = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var retry = LumenUi.Button(LumenText.Get("Retry settings"));
        retry.Click += async (_, _) =>
        {
            if (_connectionRequest is not null || _closing) return;
            _initialized = false;
            ShellLoaded(this, new RoutedEventArgs());
            await Task.CompletedTask;
        };
        var temporary = LumenUi.Button(LumenText.Get("Use a temporary session"));
        temporary.Click += async (_, _) =>
        {
            if (_closing || _connectionRequest is not null) return;
            try
            {
                await _connections.UseTemporarySessionAsync();
                _settingsReady = true;
                _remember.IsChecked = false;
                _recoveryPanel.Visibility = Visibility.Collapsed;
                SetConnecting(false);
                _address.Focus(FocusState.Programmatic);
            }
            catch (Exception exception) { ShowError(exception); }
        };
        recovery.Children.Add(retry);
        recovery.Children.Add(temporary);
        content.Children.Add(recovery);
        scroller.Content = content;
        _connectionPane.Children.Add(scroller);
        _connectionPane.SizeChanged += (_, args) => content.Width = Math.Min(440, Math.Max(260, args.NewSize.Width - 64));
        signIn.Click += async (_, _) => await ConnectAsync();
        restore.Click += async (_, _) =>
        {
            if (_savedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount account }) await ConnectAsync(account);
        };
        cancel.Click += (_, _) =>
        {
            _connectionRequest?.Cancel();
            _cancelConnection.IsEnabled = false;
        };
        saved.SelectionChanged += (_, _) =>
        {
            if (saved.SelectedItem is not ComboBoxItem { Tag: SavedAccount account }) return;
            address.Text = account.ApiRoot;
            userName.Text = account.UserName;
            password.Password = string.Empty;
            _accountRestriction = _connections.GetAccountRestriction(account.Key);
            restore.IsEnabled = _settingsReady && account.ProtectedToken.Length > 0 && _accountRestriction is null;
        };
        address.TextChanged += (_, _) =>
        {
            serverError.Visibility = Visibility.Collapsed;
            _accountRestriction = null;
        };
        userName.TextChanged += (_, _) =>
        {
            userError.Visibility = Visibility.Collapsed;
            _accountRestriction = null;
        };
        password.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter && _signIn.IsEnabled)
            {
                args.Handled = true;
                await ConnectAsync();
            }
        };
        return (address, userName, password, remember, signIn, restore, saved, savedPanel, recovery,
            connecting, status, cancel, serverError, userError);
    }

    private void PopulateSavedAccounts()
    {
        _savedAccounts.Items.Clear();
        foreach (var account in _connections.Settings.Accounts)
        {
            var host = Uri.TryCreate(account.ApiRoot, UriKind.Absolute, out var uri) ? uri.Host : account.ServerName;
            var option = new ComboBoxItem { Content = account.UserName + " \u00b7 " + host, Tag = account };
            _savedAccounts.Items.Add(option);
            if (account.Key == _connections.Settings.LastAccountKey) _savedAccounts.SelectedItem = option;
        }
        if (_savedAccounts.SelectedIndex < 0 && _savedAccounts.Items.Count > 0) _savedAccounts.SelectedIndex = 0;
        _savedPanel.Visibility = _savedAccounts.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetConnecting(bool connecting, string? message = null)
    {
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
        var request = new CancellationTokenSource();
        _connectionRequest = request;
        SetConnecting(true);
        _notice.IsOpen = false;
        try
        {
            var session = account is null
                ? await _connections.SignInAsync(_address.Text, _userName.Text, _password.Password, _remember.IsChecked == true, request.Token)
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
                if (UiErrors.RequiresPassword(exception)) _password.Password = string.Empty;
                if (UiErrors.IsAccountRestriction(exception)) _accountRestriction = UiErrors.Describe(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_connectionRequest, request)) _connectionRequest = null;
            request.Dispose();
            SetConnecting(false);
            if (!_closing && _session is null) _password.Focus(FocusState.Programmatic);
        }
    }

    private async Task ActivateSessionAsync(ConnectedSession session)
    {
        _sessionEpoch++;
        _sessionLifetime?.Cancel();
        _sessionLifetime?.Dispose();
        _sessionLifetime = new CancellationTokenSource();
        _session = session;
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
        _password.Password = string.Empty;
        _settingsVisible = false;
        _settings.SetSession(session);
        _settings.SetPreferences(_preferences);
        _settings.SetSubtitleStyleAvailable(Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.Media.Core.TimedTextSource"));
        _settings.Visibility = Visibility.Collapsed;
        _library.Visibility = Visibility.Visible;
        _connected.Visibility = Visibility.Visible;
        _connectionPane.Visibility = Visibility.Collapsed;
        await _player.SetSessionAsync(session);
        _player.ApplyPreferences(_preferences);
        _library.ApplyPreferences(_preferences);
        UpdateChrome();
        await _library.SetSessionAsync(session, _sessionLifetime.Token);
        if (ReferenceEquals(session, _session) && !_closing && session.PersistenceWarning is not null)
            ShowNotice(LumenText.Get(session.PersistenceWarning), InfoBarSeverity.Warning);
        UpdateChrome();
    }
}
