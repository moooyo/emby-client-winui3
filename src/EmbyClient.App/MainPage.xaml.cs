using EmbyClient.App.Services;
using EmbyClient.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App;

public sealed partial class MainPage : Page
{
    public event EventHandler? FullscreenRequested;
    public event EventHandler? ExitFullscreenRequested;
    public event EventHandler? ThemePreferenceChanged;
    private readonly ConnectionService _connections = new();
    private ConnectedSession? _session;
    private CancellationTokenSource? _connectionRequest;
    private bool _initialized;
    private bool _settingsReady;
    private bool _shuttingDown;
    private bool _sessionTransition;
    private bool _themeSavePending;
    private TaskCompletionSource? _sessionExitCompletion;

    public MainPage()
    {
        InitializeComponent();
        Player.QueueChanged += (_, _) => UpdateQueueButton();
        SetConnecting(false);
        Loaded += OnLoaded;
        KeyDown += OnPageKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            await _connections.InitializeAsync();
            if (_shuttingDown) return;
            ApplyTheme();
            LoadSavedAccounts();
            _settingsReady = true;
        }
        catch (Exception ex)
        {
            if (!_shuttingDown) ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
            _settingsReady = false;
        }
        finally { if (!_shuttingDown) SetConnecting(_connectionRequest is not null); }
    }

    private void LoadSavedAccounts()
    {
        SavedAccounts.Items.Clear();
        foreach (var account in _connections.Settings.Accounts)
        {
            var option = new ComboBoxItem { Content = $"{account.UserName} · {account.ServerName}", Tag = account };
            SavedAccounts.Items.Add(option);
            if (account.Key == _connections.Settings.LastAccountKey) SavedAccounts.SelectedItem = option;
        }
        SavedAccountSection.Visibility = SavedAccounts.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SavedAccountChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SavedAccounts.SelectedItem is not ComboBoxItem { Tag: SavedAccount account }) return;
        ServerAddress.Text = account.ApiRoot;
        UserName.Text = account.UserName;
        Password.Password = "";
        RestoreButton.IsEnabled = CanRestoreSelectedAccount();
    }

    private void CredentialsChanged(object sender, TextChangedEventArgs args)
    {
        if (RestoreButton is not null) RestoreButton.IsEnabled = CanRestoreSelectedAccount();
        if (ReferenceEquals(sender, ServerAddress) && ServerInputError is not null)
        {
            ServerInputError.Visibility = Visibility.Collapsed;
            AutomationProperties.SetHelpText(ServerAddress, string.Empty);
        }
        if (ReferenceEquals(sender, UserName) && UserInputError is not null)
        {
            UserInputError.Visibility = Visibility.Collapsed;
            AutomationProperties.SetHelpText(UserName, string.Empty);
        }
    }

    private bool CanRestoreSelectedAccount() => _settingsReady && !_sessionTransition && !_shuttingDown && _connectionRequest is null
        && SavedAccounts?.SelectedItem is ComboBoxItem { Tag: SavedAccount { ProtectedToken.Length: > 0 } account }
        && ServerAddress?.Text.Trim() == account.ApiRoot && UserName?.Text.Trim() == account.UserName;

    private async void SignInClicked(object sender, RoutedEventArgs args) => await ConnectAsync(false);
    private async void RestoreClicked(object sender, RoutedEventArgs args) => await ConnectAsync(true);
    private async void PasswordKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter && SignInButton.IsEnabled)
        {
            args.Handled = true;
            await ConnectAsync(false);
        }
    }

    private async Task ConnectAsync(bool restore)
    {
        if (!_settingsReady || _connectionRequest is not null || _sessionTransition || _shuttingDown) return;
        if (!restore && !ValidateCredentials())
        {
            return;
        }
        var request = new CancellationTokenSource();
        _connectionRequest = request;
        var cancellationToken = request.Token;
        SetConnecting(true);
        Notice.IsOpen = false;
        try
        {
            var session = restore && SavedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount account }
                ? await _connections.RestoreAsync(account, cancellationToken)
                : await _connections.SignInAsync(ServerAddress.Text, UserName.Text, Password.Password,
                    RememberAccount.IsChecked == true, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _session = session;
            Password.Password = "";
            AccountLabel.Text = session.User.Name ?? "Your account";
            ServerLabel.Text = session.Server.ServerName ?? "Emby Server";
            AccountAvatar.DisplayName = AccountLabel.Text;
            AutomationProperties.SetName(AccountButton, $"Account and settings for {AccountLabel.Text} on {ServerLabel.Text}");
            ToolTipService.SetToolTip(AccountButton, $"{AccountLabel.Text} · {ServerLabel.Text}");
            ConnectedPane.Visibility = Visibility.Visible;
            ConnectionPane.Visibility = Visibility.Collapsed;
            await Player.SetSessionAsync(session);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session) || _sessionTransition || _shuttingDown) return;
            await Library.SetSessionAsync(session.Api, session.Server.Id!, session.User, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session) || _sessionTransition || _shuttingDown) return;
            Library.FocusNavigation();
            if (session.PersistenceWarning is not null) ShowNotice(session.PersistenceWarning, InfoBarSeverity.Warning);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_shuttingDown && !_sessionTransition) ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
        }
        finally
        {
            Password.Password = "";
            request.Dispose();
            if (ReferenceEquals(_connectionRequest, request)) _connectionRequest = null;
            SetConnecting(_connectionRequest is not null);
        }
    }

    private void SetConnecting(bool connecting)
    {
        var available = _settingsReady && !connecting && !_sessionTransition && !_shuttingDown;
        SignInButton.IsEnabled = available;
        SavedAccounts.IsEnabled = available;
        ServerAddress.IsEnabled = available;
        UserName.IsEnabled = available;
        Password.IsEnabled = available;
        RememberAccount.IsEnabled = available;
        RestoreButton.IsEnabled = available && CanRestoreSelectedAccount();
        foreach (var button in AccountToolbar.Children.OfType<Button>())
            button.IsEnabled = !_sessionTransition && !_shuttingDown;
        UpdateQueueButton();
        ConnectingProgress.Visibility = connecting || _sessionTransition ? Visibility.Visible : Visibility.Collapsed;
        ConnectingProgress.IsIndeterminate = connecting || _sessionTransition;
        CancelConnection.Visibility = connecting && !_sessionTransition && !_shuttingDown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelConnectionClicked(object sender, RoutedEventArgs args) => _connectionRequest?.Cancel();

    private bool ValidateCredentials()
    {
        var validServer = Uri.TryCreate(ServerAddress.Text.Trim(), UriKind.Absolute, out var server)
            && (server.Scheme == Uri.UriSchemeHttp || server.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(server.Host);
        var validUser = !string.IsNullOrWhiteSpace(UserName.Text);
        ServerInputError.Text = validServer ? string.Empty : "Enter a complete server address beginning with http:// or https://.";
        UserInputError.Text = validUser ? string.Empty : "Enter your Emby username.";
        ServerInputError.Visibility = validServer ? Visibility.Collapsed : Visibility.Visible;
        UserInputError.Visibility = validUser ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetHelpText(ServerAddress, ServerInputError.Text);
        AutomationProperties.SetHelpText(UserName, UserInputError.Text);
        if (!validServer) ServerAddress.Focus(FocusState.Programmatic);
        else if (!validUser) UserName.Focus(FocusState.Programmatic);
        return validServer && validUser;
    }

    public void SetFullscreenState(bool fullscreen) => Player.SetFullscreenState(fullscreen);

    private async void LibraryPlayRequested(object? sender, PlayItemRequestedEventArgs args)
    {
        if (_session is null) return;
        if (_session.User.Policy?.EnableMediaPlayback == false)
        {
            ShowNotice("Media playback is disabled for this account.", InfoBarSeverity.Warning);
            return;
        }
        if (args.AddToQueue)
        {
            var result = Player.Enqueue(args.Item);
            if (result == QueueAddResult.Added)
            {
                Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(LibraryQueueButton)?
                    .RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
                return;
            }
            ShowNotice(result switch
            {
                QueueAddResult.Full => $"The queue is full ({TransientPlaybackQueue.MaximumItems} items). Remove an item or clear the queue before adding more.",
                _ => "This item cannot be added to the play queue."
            }, InfoBarSeverity.Warning);
            return;
        }
        Notice.IsOpen = false;
        Player.Visibility = Visibility.Visible;
        Library.Visibility = Visibility.Collapsed;
        AccountToolbar.Visibility = Visibility.Collapsed;
        Player.Focus(FocusState.Programmatic);
        await Player.PlayItemAsync(args.Item, args.StartPositionTicks);
    }

    private async void PlayerBackRequested(object? sender, EventArgs args)
    {
        ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
        await Player.StopAsync();
        Player.Visibility = Visibility.Collapsed;
        Library.Visibility = Visibility.Visible;
        AccountToolbar.Visibility = Visibility.Visible;
        try
        {
            await Library.RefreshAsync();
            Library.FocusCurrentDetails();
        }
        catch (Exception ex) { ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error); }
    }

    private void PlayerFullscreenRequested(object? sender, EventArgs args) => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateQueueButton()
    {
        QueueCountLabel.Text = Player.QueueCount.ToString();
        AutomationProperties.SetName(LibraryQueueButton, $"Open play queue, {Player.QueueCount} items");
        ToolTipService.SetToolTip(LibraryQueueButton, $"Play queue ({Player.QueueCount})");
        LibraryQueueButton.IsEnabled = _session is not null && !_sessionTransition && !_shuttingDown && !Player.IsModalOpen;
        AccountButton.IsEnabled = _session is not null && !_sessionTransition && !_shuttingDown && !Player.IsModalOpen;
        LibraryDiagnosticsButton.IsEnabled = !_sessionTransition && !_shuttingDown && !Player.IsModalOpen;
    }

    private void AccountToolbar_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        var labelVisibility = args.NewSize.Width >= 120 ? Visibility.Visible : Visibility.Collapsed;
        QueueLabel.Visibility = labelVisibility;
        QueueCountBadge.Visibility = labelVisibility;
        AccountLabels.Visibility = labelVisibility;
        AccountChevron.Visibility = labelVisibility;
    }

    private async void QueueClicked(object sender, RoutedEventArgs args)
    {
        if (_session is null || _sessionTransition || _shuttingDown || Player.IsModalOpen) return;
        try { await Player.ShowQueueAsync(XamlRoot, RequestedTheme, sender as Control); }
        catch (Exception ex) { ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error); }
    }

    private async void DiagnosticsClicked(object sender, RoutedEventArgs args)
    {
        if (_sessionTransition || _shuttingDown || Player.IsModalOpen) return;
        try { await Player.ShowDiagnosticsAsync(XamlRoot, RequestedTheme, AccountButton); }
        catch (Exception) { ShowNotice("Playback diagnostics could not be opened. Try again.", InfoBarSeverity.Warning); }
    }

    private async void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || Player.IsModalOpen || Player.IsSettingsOpen) return;
        if (Player.Visibility != Visibility.Visible) return;
        if (args.Key == VirtualKey.F11)
        {
            args.Handled = true;
            FullscreenRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or PasswordBox or ComboBox or Slider or Button or ToggleButton or ToggleSwitch))
        {
            if (args.Key == VirtualKey.Space) { args.Handled = true; await Player.TogglePauseAsync(); }
            else if (args.Key is VirtualKey.Left or VirtualKey.Right)
            { args.Handled = true; await Player.SeekRelativeAsync(args.Key == VirtualKey.Left ? -10 : 10); }
        }
    }

    private async void SwitchAccountClicked(object sender, RoutedEventArgs args) => await LeaveSessionAsync(false);
    private async void SignOutClicked(object sender, RoutedEventArgs args) => await LeaveSessionAsync(true);

    private async Task LeaveSessionAsync(bool signOut, bool sessionExpired = false)
    {
        if (_sessionTransition || _shuttingDown) return;
        var session = _session;
        if (session is null) return;
        _sessionTransition = true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sessionExitCompletion = completion;
        try
        {
            _session = null;
            AccountMenu.Hide();
            _connectionRequest?.Cancel();
            SetConnecting(_connectionRequest is not null);
            ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
            Library.ClearSession();
            Player.Visibility = Visibility.Collapsed;
            Library.Visibility = Visibility.Visible;
            AccountToolbar.Visibility = Visibility.Visible;
            ConnectedPane.Visibility = Visibility.Collapsed;
            ConnectionPane.Visibility = Visibility.Visible;
            Notice.IsOpen = false;

            Exception? localSignOutError = null;
            if (signOut || sessionExpired)
            {
                // Remove the remembered sign-in before potentially slow playback cleanup,
                // while keeping the in-memory API token available for Stop/cleanup reports.
                try { await _connections.ForgetTokenAsync(session.AccountKey); }
                catch (Exception ex) { localSignOutError = ex; }
            }

            Exception? disconnectError = null;
            try { await Player.DisconnectAsync(); }
            catch (Exception ex) { disconnectError = ex; }

            if (signOut)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var warning = await ConnectionService.RevokeSessionAsync(session, deadline.Token);
                if (localSignOutError is null && warning is not null) ShowNotice(warning, InfoBarSeverity.Warning);
            }
            else if (sessionExpired && localSignOutError is null)
            {
                ShowNotice("Your sign-in has expired. Sign in again to continue.", InfoBarSeverity.Warning);
            }
            if (localSignOutError is not null)
                ShowNotice(UiErrors.Describe(localSignOutError), InfoBarSeverity.Warning);
            else if (disconnectError is not null && !Notice.IsOpen)
                ShowNotice(UiErrors.Describe(disconnectError), InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            if (!_shuttingDown) ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
        }
        finally
        {
            try
            {
                _sessionTransition = false;
                if (!_shuttingDown)
                {
                    LoadSavedAccounts();
                    SetConnecting(_connectionRequest is not null);
                }
            }
            finally
            {
                if (ReferenceEquals(_sessionExitCompletion, completion)) _sessionExitCompletion = null;
                completion.TrySetResult();
            }
        }
    }

    private async void SessionExpired(object? sender, EventArgs args)
    {
        if (_sessionTransition || _session is null) return;
        await LeaveSessionAsync(false, sessionExpired: true);
    }

    private async void ThemeClicked(object sender, RoutedEventArgs args)
    {
        if (_themeSavePending || sender is not MenuFlyoutItem { Tag: string theme }) return;
        var previousSetting = _connections.Settings.Theme;
        var previousAppliedTheme = RequestedTheme;
        _themeSavePending = true;
        SetThemeOptionsEnabled(false);
        UpdateThemeSelection();
        try
        {
            await _connections.SetThemeAsync(theme);
            ApplyTheme();
        }
        catch (Exception ex)
        {
            _connections.Settings.Theme = previousSetting;
            RequestedTheme = previousAppliedTheme;
            UpdateThemeSelection();
            ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Warning);
        }
        finally
        {
            _themeSavePending = false;
            SetThemeOptionsEnabled(true);
        }
    }

    private void ApplyTheme()
    {
        var theme = _connections.Settings.Theme;
        RequestedTheme = theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
        UpdateThemeSelection();
        ThemePreferenceChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateThemeSelection()
    {
        SystemThemeItem.IsChecked = RequestedTheme == ElementTheme.Default;
        LightThemeItem.IsChecked = RequestedTheme == ElementTheme.Light;
        DarkThemeItem.IsChecked = RequestedTheme == ElementTheme.Dark;
    }

    private void SetThemeOptionsEnabled(bool enabled)
    {
        SystemThemeItem.IsEnabled = enabled;
        LightThemeItem.IsEnabled = enabled;
        DarkThemeItem.IsEnabled = enabled;
    }

    private void ShowNotice(string message, InfoBarSeverity severity)
    {
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsOpen = true;
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _connectionRequest?.Cancel();
        Library.ClearSession();
        var sessionExit = _sessionExitCompletion?.Task;
        try
        {
            // An existing exit owns local persistence, playback Stop/cleanup, and finally
            // remote revocation. Do not steal its disconnect while it is still saving.
            if (sessionExit is not null) await sessionExit;
            else await Player.DisconnectAsync();
        }
        finally { _connections.Dispose(); }
    }
}
