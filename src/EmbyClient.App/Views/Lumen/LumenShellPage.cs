using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace EmbyClient.App.Views.Lumen;

/// <summary>Owns connection transitions and composes the native Lumen experience.</summary>
public sealed partial class LumenShellPage : Page
{
    private readonly ConnectionService _connections = new();
    private readonly LumenPreferenceStore _preferenceStore = new();
    private readonly SemaphoreSlim _preferenceGate = new(1, 1);
    private readonly Grid _root;
    private readonly Grid _connected = new();
    private readonly Grid _header;
    private readonly Border _headerGradient;
    private readonly LumenNavigationBar _navigation;
    private readonly LumenLibraryView _library;
    private readonly LumenSettingsView _settings;
    private readonly PlayerView _player;
    private readonly Button _back;
    private readonly Button _queue;
    private readonly InfoBar _notice;
    private readonly Grid _connectionPane = new();
    private readonly TextBox _address;
    private readonly TextBox _userName;
    private readonly PasswordBox _password;
    private readonly CheckBox _remember;
    private readonly Button _signIn;
    private readonly Button _restore;
    private readonly Button _cancelConnection;
    private readonly ComboBox _savedAccounts;
    private readonly StackPanel _savedPanel;
    private readonly StackPanel _recoveryPanel;
    private readonly ProgressRing _connecting;
    private readonly TextBlock _connectionStatus;
    private readonly TextBlock _serverError;
    private readonly TextBlock _userError;
    private ConnectedSession? _session;
    private LumenPreferences _preferences = new();
    private CancellationTokenSource? _connectionRequest;
    private CancellationTokenSource? _sessionLifetime;
    private Task _pendingPreferenceSave = Task.CompletedTask;
    private bool _initialized;
    private bool _settingsReady;
    private bool _transitioning;
    private bool _closing;
    private bool _returning;
    private bool _settingsVisible;
    private int _sessionEpoch;
    private string? _accountRestriction;

    public event EventHandler? FullscreenRequested;
    public event EventHandler? ExitFullscreenRequested;
    public event EventHandler? CompactOverlayRequested;
    public event EventHandler? ChromeChanged;
    public bool IsPlayerVisible => _session is not null && _player.Visibility == Visibility.Visible;
    public bool ArePlayerControlsVisible => !IsPlayerVisible || _player.AreControlsVisible;
    public bool IsImagePage => IsPlayerVisible || !_settingsVisible && (_library.IsDetail || _library.ActiveNavigation == "home");
    public ElementTheme PreferredTheme => _preferences.Theme == "Light" ? ElementTheme.Light : ElementTheme.Dark;

    public LumenShellPage()
    {
        LumenTheme.Apply("Dark", "Gold");
        FontFamily = LumenTheme.SansFont;
        RequestedTheme = ElementTheme.Dark;
        _root = new Grid { Background = LumenTheme.Brush("Background") };
        _library = new LumenLibraryView();
        _settings = new LumenSettingsView { Visibility = Visibility.Collapsed };
        _player = new PlayerView { Visibility = Visibility.Collapsed };
        _player.SetWindowTitleBarIntegrated(false);
        _connected.Children.Add(_library);
        _connected.Children.Add(_settings);
        _connected.Visibility = Visibility.Collapsed;
        _root.Children.Add(_connected);

        _header = new Grid { Height = 64, VerticalAlignment = VerticalAlignment.Top };
        _headerGradient = new Border { IsHitTestVisible = false };
        _header.Children.Add(_headerGradient);
        _navigation = new LumenNavigationBar { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _header.Children.Add(_navigation);
        _back = LumenUi.Button(LumenText.Get("Back"), "arrow_left_20_regular", onImage: true, height: 38);
        _back.Margin = new(28, 0, 0, 0);
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.VerticalAlignment = VerticalAlignment.Center;
        _back.Visibility = Visibility.Collapsed;
        _back.Click += async (_, _) => await NavigateBackAsync();
        _header.Children.Add(_back);
        _queue = LumenUi.IconButton("apps_list_20_regular", LumenText.Get("Open play queue"), 38);
        _queue.Margin = new(140, 0, 0, 0);
        _queue.HorizontalAlignment = HorizontalAlignment.Left;
        _queue.VerticalAlignment = VerticalAlignment.Center;
        _queue.Visibility = Visibility.Collapsed;
        _queue.Click += async (_, _) =>
        {
            if (_session is null || _transitioning) return;
            try { await _player.ShowQueueAsync(XamlRoot, RequestedTheme, _queue); }
            catch (Exception exception) { ShowError(exception); }
        };
        _header.Children.Add(_queue);
        _root.Children.Add(_header);
        _root.Children.Add(_player);

        (_address, _userName, _password, _remember, _signIn, _restore, _savedAccounts, _savedPanel,
            _recoveryPanel, _connecting, _connectionStatus, _cancelConnection, _serverError, _userError) = BuildConnectionPane();
        _root.Children.Add(_connectionPane);
        _notice = new InfoBar
        {
            IsOpen = false, IsClosable = true, VerticalAlignment = VerticalAlignment.Top,
            Margin = new(24, 72, 24, 0), MaxWidth = 920, HorizontalAlignment = HorizontalAlignment.Center
        };
        _root.Children.Add(_notice);
        Content = _root;
        _navigation.NavigationRequested += async (_, key) => await NavigateAsync(key);
        _library.PlayRequested += LibraryPlayRequested;
        _library.SessionExpired += async (_, _) => await LeaveSessionAsync(false, expired: true);
        _library.NavigationStateChanged += (_, _) => UpdateChrome();
        _library.PreferencesChanged += (_, preferences) =>
        {
            var previous = _preferences;
            ApplyPreferences(preferences);
            _pendingPreferenceSave = SavePreferencesAsync(previous, preferences);
        };
        _player.BackRequested += async (_, _) => await ReturnToLibraryAsync();
        _player.ItemPlaybackRequested += LibraryPlayRequested;
        _player.FullscreenRequested += (_, _) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
        _player.CompactOverlayRequested += (_, _) => CompactOverlayRequested?.Invoke(this, EventArgs.Empty);
        _player.SessionExpired += async (_, _) => await LeaveSessionAsync(false, expired: true);
        _player.PresentationChanged += (_, _) => ChromeChanged?.Invoke(this, EventArgs.Empty);
        _player.QueueChanged += (_, _) => UpdateChrome();
        _player.PreferencesChanged += (_, preferences) =>
        {
            var previous = _preferences;
            ApplyPreferences(preferences);
            _pendingPreferenceSave = SavePreferencesAsync(previous, preferences);
        };
        _settings.PreferencesChanged += (_, preferences) =>
        {
            var previous = _preferences;
            ApplyPreferences(preferences);
            _pendingPreferenceSave = SavePreferencesAsync(previous, preferences);
        };
        _settings.SignOutRequested += async (_, _) => await LeaveSessionAsync(true);
        _settings.SwitchAccountRequested += async (_, _) => await LeaveSessionAsync(false);
        _settings.DiagnosticsRequested += async (_, _) =>
        {
            try { await _player.ShowDiagnosticsAsync(XamlRoot, RequestedTheme); }
            catch (Exception exception) { ShowError(exception); }
        };
        Loaded += ShellLoaded;
        KeyDown += ShellKeyDown;
        SizeChanged += (_, _) => UpdateChrome();
        UpdateChrome();
    }

    private async void ShellLoaded(object sender, RoutedEventArgs args)
    {
        if (_initialized) return;
        _initialized = true;
        SetConnecting(true, LumenText.Get("Loading saved accounts..."));
        try
        {
            try
            {
                ApplyPreferences(await _preferenceStore.LoadAsync());
                if (_preferenceStore.LastIssue == LumenPreferencePersistenceIssue.InvalidData)
                    ShowNotice(LumenText.Get("Some preferences could not be read. Defaults are used without changing the saved file."), InfoBarSeverity.Warning);
            }
            catch (Exception exception) { ShowError(exception); }
            await _connections.InitializeAsync();
            if (_closing) return;
            _settingsReady = true;
            PopulateSavedAccounts();
            SetConnecting(false);
            var account = _connections.Settings.Accounts.Find(value => value.Key == _connections.Settings.LastAccountKey);
            if (account is { ProtectedToken.Length: > 0 }) await ConnectAsync(account);
            else _address.Focus(FocusState.Programmatic);
        }
        catch (Exception exception)
        {
            if (_closing) return;
            ShowError(exception);
            _recoveryPanel.Visibility = Visibility.Visible;
            SetConnecting(false);
        }
    }

    private void ApplyPreferences(LumenPreferences preferences)
    {
        _preferences = preferences;
        LumenTheme.Apply(preferences.Theme, preferences.Accent);
        RequestedTheme = PreferredTheme;
        _settings.SetPreferences(preferences);
        _library.ApplyPreferences(preferences);
        _player.ApplyPreferences(preferences);
        UpdateChrome();
    }

    private async Task SavePreferencesAsync(LumenPreferences previous, LumenPreferences preferences)
    {
        var session = _session;
        var epoch = _sessionEpoch;
        await _preferenceGate.WaitAsync();
        try
        {
            await _preferenceStore.SaveAsync(preferences);
            if (session is null || epoch != _sessionEpoch || !ReferenceEquals(session, _session) || _closing) return;
            var nextChanged = previous.AutoPlayNext != preferences.AutoPlayNext;
            var languageChanged = previous.SubtitleLanguage != preferences.SubtitleLanguage;
            var modeChanged = previous.SubtitleMode != preferences.SubtitleMode;
            if (!nextChanged && !languageChanged && !modeChanged) return;
            if (session.User.Policy?.EnableUserPreferenceAccess == false) return;
            await session.Api.UpdateUserConfigurationAsync(new UserConfiguration
            {
                EnableNextEpisodeAutoPlay = nextChanged ? preferences.AutoPlayNext : null,
                SubtitleLanguagePreference = languageChanged ? preferences.SubtitleLanguage : null,
                SubtitleMode = modeChanged ? preferences.SubtitleMode switch
                {
                    "Always" => "Always", "OnlyForced" => "OnlyForced", "None" => "None", "Default" => "Default",
                    "HearingImpaired" => "HearingImpaired", _ => "Smart"
                } : null
            }, _sessionLifetime?.Token ?? CancellationToken.None);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!_closing && epoch == _sessionEpoch) ShowError(exception);
        }
        finally { _preferenceGate.Release(); }
    }

    private async Task NavigateAsync(string key)
    {
        if (_session is null || _transitioning || _returning || _closing || IsPlayerVisible) return;
        _notice.IsOpen = false;
        if (key == "settings")
        {
            _settingsVisible = true;
            _settings.Visibility = Visibility.Visible;
            _library.Visibility = Visibility.Collapsed;
            LumenUi.AnimateEntrance(_settings);
        }
        else
        {
            _settingsVisible = false;
            _settings.Visibility = Visibility.Collapsed;
            _library.Visibility = Visibility.Visible;
            await _library.NavigateAsync(key);
        }
        UpdateChrome();
    }

    public async Task NavigateBackAsync()
    {
        if (_transitioning || _returning || _closing) return;
        if (IsPlayerVisible) await ReturnToLibraryAsync();
        else if (_settingsVisible)
        {
            _settingsVisible = false;
            _settings.Visibility = Visibility.Collapsed;
            _library.Visibility = Visibility.Visible;
            UpdateChrome();
        }
        else if (_library.CanGoBack) await _library.NavigateBackAsync();
    }

    private void UpdateChrome()
    {
        if (_navigation is null) return;
        _header.Visibility = _session is null || IsPlayerVisible ? Visibility.Collapsed : Visibility.Visible;
        _back.Visibility = _session is not null && !_settingsVisible && _library.IsDetail
            ? Visibility.Visible : Visibility.Collapsed;
        _back.IsEnabled = !_transitioning && !_returning;
        var narrow = ActualWidth is > 0 and < 900;
        _navigation.Margin = narrow ? new(48, 0, 140, 0) : new(0);
        _back.Margin = new(narrow ? 16 : 28, 0, 0, 0);
        var backContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        backContent.Children.Add(LumenUi.Icon("arrow_left_20_regular", 18, IsImagePage));
        if (!narrow)
        {
            var label = LumenUi.Text(LumenText.Get("Back"), 14);
            label.TextWrapping = TextWrapping.NoWrap;
            label.Foreground = LumenTheme.Brush(IsImagePage ? "ImageInk" : "Ink");
            backContent.Children.Add(label);
        }
        _back.Content = backContent;
        _back.Width = narrow ? 38 : double.NaN;
        _back.Padding = narrow ? new(0) : new(12, 0, 16, 0);
        _back.Foreground = LumenTheme.Brush(IsImagePage ? "ImageInk" : "Ink");
        _back.Background = LumenTheme.Brush(IsImagePage ? "ImageGlass" : "Glass");
        _navigation.Select(_settingsVisible ? "settings" : _library.ActiveNavigation, IsImagePage);
        _navigation.SetEnabled(!_transitioning && !_returning && !_closing);
        _queue.Visibility = _session is not null && _player.QueueCount > 0 && !narrow ? Visibility.Visible : Visibility.Collapsed;
        _queue.IsEnabled = !_transitioning && !_returning;
        ToolTipService.SetToolTip(_queue, LumenText.Get("Play queue") + " (" + _player.QueueCount + ")");
        _headerGradient.Background = IsImagePage ? LumenTheme.ImageFade() : new SolidColorBrush(Colors.Transparent);
        ChromeChanged?.Invoke(this, EventArgs.Empty);
    }

    private async void ShellKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || _transitioning || _closing || _player.IsModalOpen) return;
        if (args.Key == VirtualKey.F11 && IsPlayerVisible)
        {
            args.Handled = true;
            FullscreenRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Key == VirtualKey.Escape)
        {
            if (IsPlayerVisible && _player.IsSettingsOpen) return;
            args.Handled = true;
            if (IsPlayerVisible) ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
            else await NavigateBackAsync();
        }
        else if (IsPlayerVisible && !_player.IsSettingsOpen
            && FocusManager.GetFocusedElement(XamlRoot) is not (TextBox or PasswordBox or ComboBox or Slider or Button or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton))
        {
            if (args.Key == VirtualKey.Space) { args.Handled = true; await _player.TogglePauseAsync(); }
            else if (args.Key is VirtualKey.Left or VirtualKey.Right)
            {
                args.Handled = true;
                await _player.SeekRelativeAsync(args.Key == VirtualKey.Left ? -10 : 30);
            }
        }
    }

    public void SetFullscreenState(bool fullscreen) => _player.SetFullscreenState(fullscreen);
    public void SetCompactOverlayState(bool compactOverlay) => _player.SetCompactOverlayState(compactOverlay);

    private void ShowError(Exception exception) => ShowNotice(LumenText.Get(UiErrors.Describe(exception)), InfoBarSeverity.Error);
    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _notice.Message = message;
        _notice.Severity = severity;
        _notice.IsOpen = true;
    }
}
