using EmbyClient.App.Services;
using EmbyClient.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace EmbyClient.App;

public sealed partial class MainPage : Page
{
    public event EventHandler? FullscreenRequested;
    public event EventHandler? ExitFullscreenRequested;
    private readonly ConnectionService _connections = new();
    private ConnectedSession? _session;
    private CancellationTokenSource? _connectionRequest;
    private bool _initialized;
    private bool _shuttingDown;
    private bool _sessionTransition;
    private TaskCompletionSource? _sessionExitCompletion;

    public MainPage()
    {
        InitializeComponent();
        Player.QueueChanged += (_, _) => UpdateQueueButton();
        UpdateQueueButton();
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
            ApplyTheme();
            LoadSavedAccounts();
        }
        catch (Exception ex)
        {
            ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error);
            SignInButton.IsEnabled = false;
        }
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
    }

    private bool CanRestoreSelectedAccount() => !_sessionTransition && !_shuttingDown && _connectionRequest is null
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
        if (_connectionRequest is not null || _sessionTransition || _shuttingDown) return;
        if (!restore && (string.IsNullOrWhiteSpace(ServerAddress.Text) || string.IsNullOrWhiteSpace(UserName.Text)))
        {
            ShowNotice("Enter a server address and username.", InfoBarSeverity.Warning);
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
            AccountLabel.Text = $"{session.Server.ServerName}  /  {session.User.Name}";
            ConnectedPane.Visibility = Visibility.Visible;
            ConnectionPane.Visibility = Visibility.Collapsed;
            await Player.SetSessionAsync(session);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session) || _sessionTransition || _shuttingDown) return;
            await Library.SetSessionAsync(session.Api, session.Server.Id!, session.User, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_session, session) || _sessionTransition || _shuttingDown) return;
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
        var available = !connecting && !_sessionTransition && !_shuttingDown;
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
        CancelConnection.Visibility = connecting && !_sessionTransition && !_shuttingDown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelConnectionClicked(object sender, RoutedEventArgs args) => _connectionRequest?.Cancel();

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
            ShowNotice(result switch
            {
                QueueAddResult.Added => "Added to the play queue.",
                QueueAddResult.Full => $"The queue is full ({TransientPlaybackQueue.MaximumItems} items). Remove an item or clear the queue before adding more.",
                _ => "This item cannot be added to the play queue."
            }, result == QueueAddResult.Added ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
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
        try { await Library.RefreshAsync(); }
        catch (Exception ex) { ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error); }
    }

    private void PlayerFullscreenRequested(object? sender, EventArgs args) => FullscreenRequested?.Invoke(this, EventArgs.Empty);

    private void UpdateQueueButton()
    {
        LibraryQueueButton.Content = $"Queue ({Player.QueueCount})";
        LibraryQueueButton.IsEnabled = _session is not null && !_sessionTransition && !_shuttingDown && !Player.IsModalOpen;
        LibraryDiagnosticsButton.IsEnabled = !_sessionTransition && !_shuttingDown && !Player.IsModalOpen;
    }

    private async void QueueClicked(object sender, RoutedEventArgs args)
    {
        if (_session is null || _sessionTransition || _shuttingDown) return;
        try { await Player.ShowQueueAsync(XamlRoot, RequestedTheme, sender as Control); }
        catch (Exception ex) { ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Error); }
    }

    private async void DiagnosticsClicked(object sender, RoutedEventArgs args)
    {
        if (_sessionTransition || _shuttingDown) return;
        try { await Player.ShowDiagnosticsAsync(XamlRoot, RequestedTheme, sender as Control); }
        catch (Exception) { ShowNotice("Playback diagnostics could not be opened. Try again.", InfoBarSeverity.Warning); }
    }

    private async void OnPageKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (Player.IsModalOpen) return;
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
        else if (FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or PasswordBox or ComboBox or Slider or Button or ToggleButton))
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
        if (sender is not MenuFlyoutItem { Tag: string theme }) return;
        try { await _connections.SetThemeAsync(theme); ApplyTheme(); }
        catch (Exception ex) { ShowNotice(UiErrors.Describe(ex), InfoBarSeverity.Warning); }
    }

    private void ApplyTheme() => RequestedTheme = _connections.Settings.Theme switch
    {
        "Dark" => ElementTheme.Dark,
        "Light" => ElementTheme.Light,
        _ => ElementTheme.Default
    };

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
