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
    public event EventHandler? ChromeStateChanged;
    private readonly ConnectionService _connections = new();
    private ConnectedSession? _session;
    private CancellationTokenSource? _connectionRequest;
    private bool _initialized;
    private bool _settingsReady;
    private bool _settingsLoading;
    private bool _settingsLoadFailed;
    private string? _manualAccountRestriction;
    private bool _shuttingDown;
    private bool _sessionTransition;
    private bool _themeSavePending;
    private bool _returningToLibrary;
    private TaskCompletionSource? _sessionExitCompletion;

    public MainPage()
    {
        InitializeComponent();
        ManualSignIn.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, (_, _) => UpdateConnectionPresentation());
        Player.PresentationChanged += (_, _) => ChromeStateChanged?.Invoke(this, EventArgs.Empty);
        Player.SetWindowTitleBarIntegrated(true);
        Library.NavigationStateChanged += (_, _) =>
        {
            ChromeStateChanged?.Invoke(this, EventArgs.Empty);
            UpdateQueueButton();
        };
        Player.QueueChanged += (_, _) =>
        {
            UpdateQueueButton();
            ChromeStateChanged?.Invoke(this, EventArgs.Empty);
        };
        SetConnecting(false);
        Loaded += OnLoaded;
        KeyDown += OnPageKeyDown;
    }

    public bool IsLibraryVisible => _session is not null && ConnectedPane.Visibility == Visibility.Visible && Library.Visibility == Visibility.Visible;
    public bool IsPlayerVisible => _session is not null && Player.Visibility == Visibility.Visible;
    public bool IsBackNavigationVisible => IsPlayerVisible || IsLibraryVisible && Library.ViewModel.CanGoBack;
    public bool CanNavigateBack => !_sessionTransition && !_shuttingDown && !_returningToLibrary && !_accountActionPending && !Player.IsModalOpen && !Library.IsPersonDialogOpen
        && (IsPlayerVisible || IsLibraryVisible && Library.ViewModel.CanGoBack);
    public string PresentationTitle => IsPlayerVisible ? Player.PresentationTitle : "Emby for Windows";
    public string PresentationSubtitle => IsPlayerVisible ? Player.PresentationSubtitle : string.Empty;

    public void ToggleNavigation()
    {
        if (IsLibraryVisible && !_sessionTransition && !_accountActionPending && !Player.IsModalOpen && !Library.IsPersonDialogOpen) Library.ToggleNavigationPane();
    }

    public async Task NavigateBackAsync()
    {
        if (!CanNavigateBack) return;
        if (IsPlayerVisible) await ReturnToLibraryAsync();
        else await Library.NavigateBackAsync();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        if (_settingsLoading || _settingsReady || _shuttingDown) return;
        _settingsLoading = true;
        SetConnecting(false);
        try
        {
            await _connections.InitializeAsync();
            if (_shuttingDown) return;
            ApplyTheme();
            _settingsReady = true;
            _settingsLoadFailed = false;
            ConnectionNotice.IsOpen = false;
            LoadSavedAccounts();
        }
        catch (Exception ex)
        {
            if (!_shuttingDown) ShowConnectionNotice(
                $"{UiErrors.Describe(ex)} Retry to load the existing settings, or sign in for this session without changing the saved file.",
                InfoBarSeverity.Error);
            _settingsReady = false;
            _settingsLoadFailed = true;
        }
        finally
        {
            _settingsLoading = false;
            if (!_shuttingDown)
            {
                SetConnecting(_connectionRequest is not null);
                if (_settingsLoadFailed) RetrySettingsButton.Focus(FocusState.Programmatic);
                else FocusSignInAction();
            }
        }
    }

    private async void RetrySettingsClicked(object sender, RoutedEventArgs args) => await LoadSettingsAsync();

    private async void TemporarySessionClicked(object sender, RoutedEventArgs args)
    {
        if (_settingsLoading || _settingsReady || _shuttingDown) return;
        await _connections.UseTemporarySessionAsync();
        if (_shuttingDown) return;
        _settingsReady = true;
        _settingsLoadFailed = false;
        RememberAccount.IsChecked = false;
        LoadSavedAccounts();
        ShowConnectionNotice("This session will not save accounts or preferences. Your existing settings file will stay unchanged.", InfoBarSeverity.Informational);
        SetConnecting(false);
        ServerAddress.Focus(FocusState.Programmatic);
    }

    private void LoadSavedAccounts(string? preferredKey = null)
    {
        var selectedKey = preferredKey ?? _connections.Settings.LastAccountKey
            ?? ((SavedAccounts.SelectedItem as ComboBoxItem)?.Tag is SavedAccount selected ? selected.Key : null);
        SavedAccounts.Items.Clear();
        foreach (var account in _connections.Settings.Accounts)
        {
            var option = new ComboBoxItem { Content = $"{account.UserName} · {AccountConnectionDialog.AccountHost(account.ApiRoot)}", Tag = account };
            ToolTipService.SetToolTip(option, $"{account.ServerName}\n{account.ApiRoot}");
            SavedAccounts.Items.Add(option);
            if (account.Key == selectedKey) SavedAccounts.SelectedItem = option;
        }
        SavedAccountSection.Visibility = SavedAccounts.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (SavedAccounts.SelectedIndex < 0 && SavedAccounts.Items.Count > 0) SavedAccounts.SelectedIndex = 0;
        if (SavedAccounts.Items.Count == 0) ManualSignIn.IsExpanded = true;
        UpdateConnectionPresentation();
    }

    private void SavedAccountChanged(object sender, SelectionChangedEventArgs args)
    {
        if (SavedAccounts.SelectedItem is not ComboBoxItem { Tag: SavedAccount account }) return;
        ServerAddress.Text = account.ApiRoot;
        UserName.Text = account.UserName;
        Password.Password = "";
        _manualAccountRestriction = _connections.GetAccountRestriction(account.Key);
        ManualSignIn.IsExpanded = account.ProtectedToken.Length == 0 && _manualAccountRestriction is null;
        if (_manualAccountRestriction is not null) ShowConnectionNotice(_manualAccountRestriction, InfoBarSeverity.Warning);
        else if (account.ProtectedToken.Length == 0)
            ShowConnectionNotice("This saved account needs a password. Sign in below to continue.", InfoBarSeverity.Informational);
        else ConnectionNotice.IsOpen = false;
        UpdateConnectionPresentation();
        RestoreButton.IsEnabled = CanRestoreSelectedAccount();
        SignInButton.IsEnabled = CanConnect();
    }

    private void CredentialsChanged(object sender, TextChangedEventArgs args)
    {
        if (_manualAccountRestriction is not null && ConnectionNotice is not null) ConnectionNotice.IsOpen = false;
        _manualAccountRestriction = null;
        if (Password is not null) Password.Password = string.Empty;
        if (RestoreButton is not null) RestoreButton.IsEnabled = CanRestoreSelectedAccount();
        if (SignInButton is not null) SignInButton.IsEnabled = CanConnect();
        if (SavedAccounts is not null) UpdateConnectionPresentation();
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

    private bool CanRestoreSelectedAccount() => _settingsReady && !_sessionTransition && !_shuttingDown && !_accountActionPending && _connectionRequest is null
        && SavedAccounts?.SelectedItem is ComboBoxItem { Tag: SavedAccount { ProtectedToken.Length: > 0 } account }
        && _connections.GetAccountRestriction(account.Key) is null;

    private bool CanConnect() => _settingsReady && !_settingsLoading && !_sessionTransition && !_shuttingDown
        && !_accountActionPending && !_themeSavePending && _connectionRequest is null && _manualAccountRestriction is null;

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
        if (!CanConnect()) return;
        if (restore && !CanRestoreSelectedAccount()) return;
        if (!restore && !ValidateCredentials())
        {
            return;
        }
        var request = new CancellationTokenSource();
        _connectionRequest = request;
        var cancellationToken = request.Token;
        SetConnecting(true);
        if (_pendingSignOut is null) Notice.IsOpen = false;
        ConnectionNotice.IsOpen = false;
        CancelConnection.Focus(FocusState.Programmatic);
        ConnectionStatus.StartBringIntoView();
        try
        {
            var session = restore && SavedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount account }
                ? await _connections.RestoreAsync(account, cancellationToken)
                : await _connections.SignInAsync(ServerAddress.Text, UserName.Text, Password.Password,
                    RememberAccount.IsChecked == true, cancellationToken);
            // ConnectionService owns cancellation through the final account commit.
            // Once committed, browse loading belongs to the session rather than the sign-in request.
            if (_shuttingDown || _sessionTransition) return;
            await ActivateSessionAsync(session, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_shuttingDown && !_sessionTransition)
            {
                if (_session is null) ShowConnectionNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
                else ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
                if (UiErrors.IsAccountRestriction(ex)) _manualAccountRestriction = UiErrors.Describe(ex);
                if (UiErrors.RequiresPassword(ex))
                {
                    Password.Password = string.Empty;
                    ManualSignIn.IsExpanded = true;
                }
                UpdateConnectionPresentation();
            }
        }
        finally
        {
            request.Dispose();
            if (ReferenceEquals(_connectionRequest, request)) _connectionRequest = null;
            SetConnecting(_connectionRequest is not null);
            if (!_shuttingDown && _session is null && !_sessionTransition) FocusSignInAction();
        }
    }

    private async Task ActivateSessionAsync(ConnectedSession session, CancellationToken cancellationToken)
    {
        _session = session;
        _pendingSignOut = null;
        Notice.ActionButton = null;
        Notice.IsClosable = true;
        Notice.IsOpen = false;
        ConnectionNotice.ActionButton = null;
        Password.Password = string.Empty;
        _manualAccountRestriction = null;
        AccountLabel.Text = session.User.Name ?? "Your account";
        ServerLabel.Text = AccountConnectionDialog.AccountHost(session.Api.ApiRoot.AbsoluteUri);
        AccountAvatar.DisplayName = AccountLabel.Text;
        AccountIdentityItem.Text = $"{AccountLabel.Text} · {ServerLabel.Text}";
        AutomationProperties.SetName(AccountButton, $"Account and settings for {AccountLabel.Text} on {ServerLabel.Text}");
        ToolTipService.SetToolTip(AccountButton, $"{AccountLabel.Text} · {session.Server.ServerName}\n{session.Api.ApiRoot}");
        RemoveCurrentAccountItem.Visibility = _connections.Settings.Accounts.Exists(account => account.Key == session.AccountKey)
            ? Visibility.Visible : Visibility.Collapsed;
        ConnectedPane.Visibility = Visibility.Visible;
        ConnectionPane.Visibility = Visibility.Collapsed;
        ChromeStateChanged?.Invoke(this, EventArgs.Empty);
        await Player.SetSessionAsync(session);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_session, session) || _sessionTransition || _shuttingDown) return;
        await Library.SetSessionAsync(session.Api, session.Server.Id!, session.User, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_session, session) || _sessionTransition || _shuttingDown) return;
        Library.FocusNavigation();
        if (session.PersistenceWarning is not null) ShowNotice(session.PersistenceWarning, InfoBarSeverity.Warning);
    }

    private void FocusSignInAction()
    {
        if (_pendingSignOut is not null && Notice.IsOpen && Notice.ActionButton is ButtonBase retry)
            retry.Focus(FocusState.Programmatic);
        else if (_manualAccountRestriction is not null)
        {
            if (SavedAccounts.Items.Count > 0) SavedAccounts.Focus(FocusState.Programmatic);
            else UserName.Focus(FocusState.Programmatic);
        }
        else if (CanRestoreSelectedAccount() && !ManualSignIn.IsExpanded) RestoreButton.Focus(FocusState.Programmatic);
        else if (string.IsNullOrWhiteSpace(ServerAddress.Text)) ServerAddress.Focus(FocusState.Programmatic);
        else if (string.IsNullOrWhiteSpace(UserName.Text)) UserName.Focus(FocusState.Programmatic);
        else Password.Focus(FocusState.Programmatic);
    }

    private void SetConnecting(bool connecting)
    {
        var available = _settingsReady && !connecting && !_sessionTransition && !_shuttingDown && !_accountActionPending && !_themeSavePending;
        SignInButton.IsEnabled = available && _manualAccountRestriction is null;
        SavedAccounts.IsEnabled = available;
        ServerAddress.IsEnabled = available;
        UserName.IsEnabled = available;
        Password.IsEnabled = available;
        RememberAccount.IsEnabled = available && !_connections.IsTemporarySession;
        ManualSignIn.IsEnabled = available;
        RemoveSavedButton.IsEnabled = available && SavedAccounts.SelectedItem is not null;
        RestoreButton.IsEnabled = available && CanRestoreSelectedAccount();
        if (ConnectionNotice.ActionButton is ButtonBase recoveryAction) recoveryAction.IsEnabled = available;
        if (Notice.ActionButton is ButtonBase signOutRetry) signOutRetry.IsEnabled = available;
        foreach (var button in AccountToolbar.Children.OfType<Button>())
            button.IsEnabled = !_sessionTransition && !_shuttingDown;
        UpdateQueueButton();
        SettingsRecoveryPanel.Visibility = _settingsLoadFailed && !_settingsReady ? Visibility.Visible : Visibility.Collapsed;
        RetrySettingsButton.IsEnabled = !_settingsLoading && !_shuttingDown;
        TemporarySessionButton.IsEnabled = !_settingsLoading && !_shuttingDown;
        ConnectionStatus.Visibility = connecting || _sessionTransition || _settingsLoading ? Visibility.Visible : Visibility.Collapsed;
        ConnectingProgress.IsActive = connecting || _sessionTransition || _settingsLoading;
        ConnectionStatusLabel.Text = _sessionTransition ? "Closing the current session…"
            : _settingsLoading ? "Loading saved accounts…" : "Connecting to your server…";
        CancelConnection.Visibility = connecting && !_sessionTransition && !_shuttingDown ? Visibility.Visible : Visibility.Collapsed;
        CancelConnection.IsEnabled = _connectionRequest?.IsCancellationRequested != true;
        ChromeStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CancelConnectionClicked(object sender, RoutedEventArgs args)
    {
        _connectionRequest?.Cancel();
        ConnectionStatusLabel.Text = "Cancelling connection…";
        CancelConnection.IsEnabled = false;
    }

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
        ChromeStateChanged?.Invoke(this, EventArgs.Empty);
        Player.Focus(FocusState.Programmatic);
        await Player.PlayItemAsync(args.Item, args.StartPositionTicks);
    }

    private async void PlayerBackRequested(object? sender, EventArgs args) => await ReturnToLibraryAsync();

    private async Task ReturnToLibraryAsync()
    {
        if (_returningToLibrary || _sessionTransition || _shuttingDown || _session is null) return;
        _returningToLibrary = true;
        var session = _session;
        ChromeStateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
            await Player.StopAsync();
            if (!ReferenceEquals(session, _session) || _sessionTransition || _shuttingDown) return;
            Player.Visibility = Visibility.Collapsed;
            Library.Visibility = Visibility.Visible;
            AccountToolbar.Visibility = Visibility.Visible;
            ChromeStateChanged?.Invoke(this, EventArgs.Empty);
            await Library.RefreshAsync();
            if (ReferenceEquals(session, _session) && !_sessionTransition && !_shuttingDown)
                Library.FocusCurrentDetails();
        }
        catch (Exception ex)
        {
            if (!_shuttingDown && ReferenceEquals(session, _session)) ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
        }
        finally
        {
            _returningToLibrary = false;
            ChromeStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PlayerFullscreenRequested(object? sender, EventArgs args) => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateQueueButton()
    {
        QueueCountLabel.Text = Player.QueueCount.ToString();
        AutomationProperties.SetName(LibraryQueueButton, $"Open play queue, {Player.QueueCount} items");
        ToolTipService.SetToolTip(LibraryQueueButton, $"Play queue ({Player.QueueCount})");
        var available = !_sessionTransition && !_shuttingDown && !_accountActionPending && !_themeSavePending && !Player.IsModalOpen && !Library.IsPersonDialogOpen;
        LibraryQueueButton.IsEnabled = _session is not null && available;
        AccountButton.IsEnabled = _session is not null && available;
        LibraryDiagnosticsButton.IsEnabled = available;
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
        if (_session is null || _sessionTransition || _shuttingDown || _accountActionPending || Player.IsModalOpen || Library.IsPersonDialogOpen) return;
        try { await Player.ShowQueueAsync(XamlRoot, RequestedTheme, sender as Control); }
        catch (Exception) { ShowNotice("The play queue could not be opened. Try again.", InfoBarSeverity.Error); }
    }

    private async void DiagnosticsClicked(object sender, RoutedEventArgs args)
    {
        if (_sessionTransition || _shuttingDown || _accountActionPending || Player.IsModalOpen || Library.IsPersonDialogOpen) return;
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

    private async void SwitchAccountClicked(object sender, RoutedEventArgs args)
    {
        await ShowAccountConnectionAsync(addAccount: false);
    }

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
            _accountDialog?.Hide();
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
            ConnectionNotice.IsOpen = false;
            Password.Password = string.Empty;

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

            var serverSignOutFailed = false;
            if (signOut)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var warning = await ConnectionService.RevokeSessionAsync(session, deadline.Token);
                serverSignOutFailed = warning is not null;
            }
            else if (sessionExpired && localSignOutError is null)
            {
                ShowConnectionNotice("Your sign-in has expired. Sign in again with your password to continue.", InfoBarSeverity.Warning);
            }
            if (localSignOutError is not null || serverSignOutFailed)
                ShowPartialSignOut(session, localSignOutError is not null, serverSignOutFailed);
            else if (disconnectError is not null && !ConnectionNotice.IsOpen && !Notice.IsOpen)
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
                    var recoveryMessage = ConnectionNotice.Message;
                    var recoverySeverity = ConnectionNotice.Severity;
                    var recoveryAction = ConnectionNotice.ActionButton;
                    var hadRecoveryMessage = ConnectionNotice.IsOpen;
                    LoadSavedAccounts(session.AccountKey);
                    if (hadRecoveryMessage)
                    {
                        ShowConnectionNotice(recoveryMessage, recoverySeverity);
                        ConnectionNotice.ActionButton = recoveryAction;
                    }
                    SetConnecting(_connectionRequest is not null);
                    if (sessionExpired) FocusSignInAction();
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
        if (_themeSavePending || _accountActionPending || _sessionTransition || _shuttingDown || sender is not MenuFlyoutItem { Tag: string theme }) return;
        var previousSetting = _connections.Settings.Theme;
        var previousAppliedTheme = RequestedTheme;
        _themeSavePending = true;
        SetConnecting(_connectionRequest is not null);
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
            SetConnecting(_connectionRequest is not null);
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

    private void ShowConnectionNotice(string message, InfoBarSeverity severity)
    {
        ConnectionNotice.ActionButton = null;
        ConnectionNotice.Message = message;
        ConnectionNotice.Severity = severity;
        ConnectionNotice.IsOpen = true;
    }

    public async Task ShutdownAsync()
    {
        _shuttingDown = true;
        _accountDialog?.Hide();
        _connectionRequest?.Cancel();
        Library.ClearSession();
        var sessionExit = _sessionExitCompletion?.Task;
        try
        {
            // An existing exit owns local persistence, playback Stop/cleanup, and finally
            // remote revocation. Do not steal its disconnect while it is still saving.
            if (sessionExit is not null) await sessionExit;
            else await Player.DisconnectAsync();
            if (_accountActionCompletion is not null) await _accountActionCompletion.Task;
        }
        finally { _connections.Dispose(); }
    }
}
