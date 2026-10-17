using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views.Lumen;

/// <summary>Owns connection transitions and composes the native Lumen experience.</summary>
public sealed partial class LumenShellPage : Page
{
    private readonly ConnectionService _connections = new();
    private readonly LumenPreferenceStore _preferenceStore = new();
    private readonly SemaphoreSlim _preferenceGate = new(1, 1);
    private readonly UISettings _systemUiSettings = new();
    private readonly Dictionary<VirtualKey, Func<Task>> _controlShortcuts = [];
    private readonly Dictionary<VirtualKey, string> _controlShortcutNames = [];
    private readonly Dictionary<(VirtualKey Key, VirtualKeyModifiers Modifiers), Func<Task>> _previewShortcuts = [];
    private readonly Dictionary<(VirtualKey Key, VirtualKeyModifiers Modifiers), string> _previewShortcutNames = [];
    private readonly HashSet<(VirtualKey Key, VirtualKeyModifiers Modifiers)> _claimedPreviewShortcuts = [];
    private readonly HashSet<VirtualKey> _claimedControlKeys = [];
    private readonly HashSet<VirtualKey> _completedControlKeyUps = [];
    private readonly bool _acceptanceTraceEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable));
    private readonly KeyEventHandler _globalPreviewKeyDown;
    private readonly KeyEventHandler _globalPreviewKeyUp;
    private readonly KeyEventHandler _globalKeyDown;
    private readonly Grid _root;
    private readonly Grid _connected = new();
    private readonly Grid _header;
    private readonly Border _headerGradient;
    private readonly Border _headerBlur;
    private readonly LumenNavigationBar _navigation;
    private readonly LumenLibraryView _library;
    private readonly LumenSettingsView _settings;
    private readonly PlayerView _player;
    private readonly Button _back;
    private readonly SolidColorBrush _backForeground = new();
    private readonly SolidColorBrush _backBackground = new();
    private readonly SolidColorBrush _backBorder = new();
    private readonly SolidColorBrush _backHover = new();
    private readonly SolidColorBrush _backPressed = new();
    private readonly LumenToastNotice _notice;
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
    private bool _settingsForward;
    private bool _fullscreen;
    private bool _compactOverlay;
    private bool _themeSubscribed;
    private bool _applyingPreferences;
    private bool _systemColorsSubscribed;
    private bool _observedControlPress;
    private ContentDialog? _shellDialog;
    private UIElement? _globalKeyboardRoot;
    private InputFocusController? _contentInputFocus;
    private double _lastTracedWidth = double.NaN;
    private double _lastTracedHeight = double.NaN;
    private int _sessionEpoch;
    private string? _accountRestriction;
    private Storyboard? _headerTintTransition;
    private bool? _headerTintVisible;

    public event EventHandler? FullscreenRequested;
    public event EventHandler? ExitFullscreenRequested;
    public event EventHandler? CompactOverlayRequested;
    public event EventHandler? ChromeChanged;
    public event EventHandler? TaskbarStateChanged;
    public event EventHandler? ShutdownRequested;
    public bool IsPlayerVisible => _session is not null && _player.Visibility == Visibility.Visible;
    public bool ArePlayerControlsVisible => !IsPlayerVisible || _player.AreControlsVisible;
    public bool IsImagePage => IsPlayerVisible || _session is not null && !_settingsVisible && (_library.ViewModel.HasDetails || _library.ActiveNavigation == "home");
    public ElementTheme PreferredTheme => LumenTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light;
    public string WindowTitle => _session is null ? "Lumen"
        : _settingsVisible ? $"Lumen \u00b7 {LumenText.Get("Settings")}"
        : IsPlayerVisible && WindowsLastWatched is { Title: { Length: > 0 } title } ? $"Lumen \u00b7 {title}"
        : _library.WindowTitle;
    public TaskbarPlaybackState TaskbarState => _player.TaskbarState;
    public ContentRefreshRatePlayback RefreshRatePlayback => _player.RefreshRatePlayback;
    public event EventHandler? RefreshRatePlaybackChanged;
    public void SetRefreshRateStatus(ContentRefreshRateStatus status) => _settings.SetRefreshRateStatus(status);
    public Task RefreshHdrDisplayEligibilityAsync() => _player.RefreshHdrDisplayEligibilityAsync();
    public IReadOnlyList<FrameworkElement> CaptionInteractiveElements => IsPlayerVisible
        ? _player.CaptionInteractiveElements
        : _session is not null ? [_navigation, _back] : [];

    public LumenShellPage()
    {
        _globalPreviewKeyDown = ShellPreviewKeyDown;
        _globalPreviewKeyUp = ShellPreviewKeyUp;
        _globalKeyDown = ShellKeyDown;
        LumenTheme.Apply("Dark", "Gold");
        FontFamily = LumenTheme.SansFont;
        RequestedTheme = ElementTheme.Dark;
        _root = new Grid { Background = LumenTheme.Brush("Background") };
        _library = new LumenLibraryView();
        _library.HomeInteractionBlocked = () => !_settingsReady || _navigation?.IsMenuOpen == true
            || _shellDialog is not null || _resumeDialog is not null || _settingsVisible
            || _transitioning || _returning || _closing;
        _library.MediaLibrarySelectionChanged += MediaLibrarySelectionChanged;
        _settings = new LumenSettingsView { Visibility = Visibility.Collapsed };
        _player = new PlayerView { Visibility = Visibility.Collapsed };
        _player.SetWindowTitleBarIntegrated(true);
        _connected.Children.Add(_library);
        _connected.Children.Add(_settings);
        _connected.Visibility = Visibility.Collapsed;
        _root.Children.Add(_connected);

        _header = new Grid { Height = 84, VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetAutomationId(_header, "LumenHeader");
        _headerBlur = new Border
        {
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Background = new AcrylicBrush
            {
                TintColor = Colors.Transparent, TintOpacity = 0, TintLuminosityOpacity = 0,
                TintTransitionDuration = TimeSpan.Zero, FallbackColor = LumenTheme.Color("BarScrolled")
            }
        };
        _header.Children.Add(_headerBlur);
        _headerGradient = new Border { IsHitTestVisible = false };
        _header.Children.Add(_headerGradient);
        _navigation = new LumenNavigationBar
        {
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 30, 0, 0)
        };
        _header.Children.Add(_navigation);
        _back = new Button
        {
            Width = 32, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new(0), CornerRadius = new(16),
            BorderThickness = new(1), Background = _backBackground, Foreground = _backForeground, BorderBrush = _backBorder,
            UseSystemFocusVisuals = true
        };
        _back.Resources["ButtonBackgroundPointerOver"] = _backHover;
        _back.Resources["ButtonBackgroundPressed"] = _backPressed;
        _back.Resources["ButtonForegroundPointerOver"] = _backForeground;
        _back.Resources["ButtonForegroundPressed"] = _backForeground;
        _back.Resources["ButtonBorderBrushPointerOver"] = _backBorder;
        _back.Resources["ButtonBorderBrushPressed"] = _backBorder;
        AutomationProperties.SetName(_back, LumenText.Get("Back") + " (Alt+\u2190)");
        AutomationProperties.SetAutomationId(_back, "LumenBack");
        ToolTipService.SetToolTip(_back, LumenText.Get("Back") + " (Alt+\u2190)");
        _back.Margin = new(12, 34, 0, 0);
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.VerticalAlignment = VerticalAlignment.Top;
        _back.Visibility = Visibility.Collapsed;
        _back.Content = LumenUi.Icon("arrow_left_20_regular", 16, true);
        _back.RenderTransform = new TranslateTransform();
        _back.PointerEntered += (_, _) => AnimateBack(-2);
        _back.PointerExited += (_, _) => AnimateBack(0);
        _back.Click += async (_, _) => await NavigateBackAsync();
        _header.Children.Add(_back);
        _root.Children.Add(_header);
        _root.Children.Add(_player);

        (_address, _userName, _password, _remember, _signIn, _restore, _savedAccounts, _savedPanel,
            _recoveryPanel, _connecting, _connectionStatus, _cancelConnection, _serverError, _userError) = BuildConnectionPane();
        _root.Children.Add(_connectionPane);
        _notice = new LumenToastNotice();
        _root.Children.Add(_notice);
        InitializeServerIntegration();
        InitializeWindowsActivation();
        Content = _root;
        _navigation.NavigationRequested += async (_, key) => await NavigateAsync(key);
        _navigation.AccountActionRequested += AccountActionRequested;
        _library.ViewModel.Libraries.CollectionChanged += (_, _) => UpdateChrome();
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
        _player.ItemDetailsRequested += PlayerItemDetailsRequested;
        _player.FullscreenRequested += (_, _) => FullscreenRequested?.Invoke(this, EventArgs.Empty);
        _player.CompactOverlayRequested += (_, _) => CompactOverlayRequested?.Invoke(this, EventArgs.Empty);
        _player.SessionExpired += async (_, _) => await LeaveSessionAsync(false, expired: true);
        _player.PresentationChanged += (_, _) => ChromeChanged?.Invoke(this, EventArgs.Empty);
        _player.QueueChanged += (_, _) => UpdateChrome();
        _player.TaskbarStateChanged += (_, _) => TaskbarStateChanged?.Invoke(this, EventArgs.Empty);
        _player.RefreshRatePlaybackChanged += (_, _) => RefreshRatePlaybackChanged?.Invoke(this, EventArgs.Empty);
        _player.HdrDisplayEligibilityChanged += (_, _) => _settings.SetHdrDisplayEligibility(_player.HdrDisplayEligibility);
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
        _settings.SignOutRequestedAsync = () => LeaveSessionAsync(true);
        _settings.SwitchAccountRequested += async (_, _) => await LeaveSessionAsync(false);
        _settings.KeyboardShortcutsRequested += async (_, _) => await ShowKeyboardShortcutsAsync();
        _settings.HeaderScrollChanged += (_, _) => UpdateChrome();
        _settings.ShutdownRequested += (_, _) => ShutdownRequested?.Invoke(this, EventArgs.Empty);
        _settings.ClearImageCacheRequested += (_, _) =>
        {
            _library.ViewModel.ClearImageCache();
            ShowNotice(ShellText("The image cache has been cleared."));
        };
        _settings.DiagnosticsRequested += async (_, _) =>
        {
            try { await _player.OpenDiagnosticLogsFolderAsync(); }
            catch (Exception exception) { ShowError(exception); }
        };
        Loaded += ShellLoaded;
        Loaded += (_, _) =>
        {
            AttachGlobalKeyboardInput();
            TraceViewportSize();
            if (!_themeSubscribed) LumenTheme.Changed += ThemeChanged;
            _themeSubscribed = true;
            if (!_systemColorsSubscribed)
            {
                try { _systemUiSettings.ColorValuesChanged += SystemColorsChanged; _systemColorsSubscribed = true; }
                catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
            }
        };
        Unloaded += (_, _) =>
        {
            CancelServerDiscovery();
            _libraryEventTimer.Stop();
            DetachGlobalKeyboardInput();
            if (_themeSubscribed) LumenTheme.Changed -= ThemeChanged;
            _themeSubscribed = false;
            UnsubscribeSystemColors();
            _navigation.DismissMenus();
        };
        AddHandler(PointerPressedEvent, new PointerEventHandler(ShellPointerPressed), true);
        RegisterGlobalAccelerators();
        SizeChanged += (_, _) => { TraceViewportSize(); UpdateChrome(); };
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
            await LoadSavedSignInServersAsync();
            if (_closing) return;
            _settingsReady = true;
            PopulateSavedAccounts();
            _settings.SetSavedAccounts(_connections.Settings.Accounts);
            SetConnecting(false);
            var account = _connections.Settings.Accounts.Find(value => value.Key == _connections.Settings.LastAccountKey);
            RecordStartupPhase($"restore-candidate saved={account is not null} token={account is { ProtectedToken.Length: > 0 }} ready={_settingsReady} transitioning={_transitioning} request={_connectionRequest is not null}");
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
        _applyingPreferences = true;
        try
        {
            LumenTheme.Apply(preferences.Theme, preferences.Accent);
            RequestedTheme = PreferredTheme;
            _settings.SetPreferences(preferences);
            _library.ApplyPreferences(preferences);
            _player.ApplyPreferences(preferences);
            ApplyServerIntegrationPreferences();
            NotifyWindowsActivationPreferences();
        }
        finally { _applyingPreferences = false; }
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
        _navigation.DismissMenus();
        EnsureContentKeyboardFocus();
        _notice.IsOpen = false;
        _settingsForward = false;
        try
        {
            if (key == "settings")
            {
                _settingsVisible = true;
                _settings.SetSavedAccounts(_connections.Settings.Accounts);
                var artwork = _library.ViewModel.HomeRows.SelectMany(row => row.Items).Concat(_library.ViewModel.Items)
                    .FirstOrDefault(item => ImageCache.SelectImage(item.Item, ArtworkKind.Poster) is not null);
                _settings.SetAmbientArtwork(artwork, artwork is null ? null : _library.ViewModel);
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
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
        UpdateChrome();
    }

    public async Task NavigateBackAsync()
    {
        if (_transitioning || _returning || _closing || _shellDialog is not null) return;
        if (_navigation.DismissMenus()) { EnsureContentKeyboardFocus(); return; }
        EnsureContentKeyboardFocus();
        try
        {
            if (IsPlayerVisible) await ReturnToLibraryAsync();
            else if (_settingsVisible)
            {
                _settingsVisible = false;
                _settingsForward = true;
                _settings.Visibility = Visibility.Collapsed;
                _library.Visibility = Visibility.Visible;
                _library.FocusCurrentDetails();
            }
            else if (_library.CanGoBack) await _library.NavigateBackAsync();
            else if (_session is not null && _library.ActiveNavigation == "search") await NavigateAsync("home");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
        UpdateChrome();
    }

    public async Task NavigateForwardAsync()
    {
        if (_transitioning || _returning || _closing || IsPlayerVisible || _settingsVisible || _shellDialog is not null) return;
        _navigation.DismissMenus();
        EnsureContentKeyboardFocus();
        if (_settingsForward) { await NavigateAsync("settings"); return; }
        try { if (_library.CanGoForward) await _library.NavigateForwardAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
        UpdateChrome();
    }

    private async void AccountActionRequested(object? sender, string key)
    {
        if (_session is null || _transitioning || _returning || _closing) return;
        if (key == "settings") await NavigateAsync("settings");
        else if (key == "favs") await NavigateAsync("favs");
        else if (key == "switch") await LeaveSessionAsync(false);
        else if (key == "shortcuts") await ShowKeyboardShortcutsAsync();
        else if (key == "signout") await ConfirmSignOutAsync();
    }

    private async Task ConfirmSignOutAsync()
    {
        if (_shellDialog is not null || XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = PreferredTheme, Title = ShellText("Sign out of Lumen?"),
            Content = ShellText("This account will require a password the next time you sign in."),
            PrimaryButtonText = LumenText.Get("Sign out"), CloseButtonText = LumenText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        LumenDialogTheme.Apply(dialog);
        Task? signOutTask = null;
        dialog.PrimaryButtonClick += (_, _) =>
        {
            // Register session cleanup before dialog dismissal can race window shutdown.
            if (ReferenceEquals(_shellDialog, dialog)) _shellDialog = null;
            signOutTask ??= LeaveSessionAsync(true);
        };
        _shellDialog = dialog;
        try
        {
            try { await dialog.ShowAsync(); }
            finally { if (signOutTask is not null) await signOutTask; }
        }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
        finally { if (ReferenceEquals(_shellDialog, dialog)) _shellDialog = null; ResumePendingWindowsActivation(); _ = OpenPendingEpisodeNotificationAsync(); }
    }

    private void UpdateChrome()
    {
        try { UpdateChromeCore(); }
        catch (Exception exception)
        {
            AcceptanceDiagnostics.Record(exception, "LumenShellPage.UpdateChrome");
            throw;
        }
    }

    private void UpdateChromeCore()
    {
        if (_navigation is null) return;
        var connected = _session is not null;
        var canBack = connected && (_settingsVisible || _library.CanGoBack || _library.IsDetail || _library.ActiveNavigation == "search");
        _header.Visibility = connected && !IsPlayerVisible ? Visibility.Visible : Visibility.Collapsed;
        _back.Visibility = canBack ? Visibility.Visible : Visibility.Collapsed;
        _back.IsEnabled = !_transitioning && !_returning && !_closing;
        _back.Content = LumenUi.Icon("arrow_left_20_regular", 16, IsImagePage);
        _backForeground.Color = LumenTheme.Color(IsImagePage ? "ImageInk" : "Ink");
        _backBackground.Color = LumenTheme.Color(IsImagePage ? "ImageGlass" : "Glass");
        _backBorder.Color = LumenTheme.Color(IsImagePage ? "ImageLine" : "LineStrong");
        _backHover.Color = LumenTheme.Color(IsImagePage ? "ImageControl" : "Hover");
        _backPressed.Color = LumenTheme.Color(IsImagePage ? "ImageControl" : "ControlStrong");
        var narrow = ActualWidth is > 0 and < 900;
        _navigation.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _navigation.Margin = new(narrow ? canBack ? 56 : 12 : 0, 30, 0, 0);
        _navigation.RefreshLabels();
        _navigation.SetAccount(_session?.User.Name, _session?.Server.ServerName, _session?.Api.ApiRoot.Authority);
        _navigation.Select(_settingsVisible ? "settings" : _library.ActiveNavigation, IsImagePage);
        _navigation.SetEnabled(!_transitioning && !_returning && !_closing);
        var headerScrolled = _settingsVisible ? _settings.IsHeaderScrolled : _library.IsHeaderScrolled;
        _headerBlur.Visibility = !IsImagePage && headerScrolled ? Visibility.Visible : Visibility.Collapsed;
        if (_headerBlur.Background is AcrylicBrush headerAcrylic) headerAcrylic.FallbackColor = LumenTheme.Color("BarScrolled");
        _headerGradient.Background = IsImagePage ? LumenTheme.ImageFade() : LumenTheme.Brush("BarScrolled");
        SetHeaderTintVisible(IsImagePage || headerScrolled);
        _headerGradient.BorderBrush = LumenTheme.Brush("Line");
        _headerGradient.BorderThickness = new(0, 0, 0, !IsImagePage && headerScrolled ? 1 : 0);
        _notice.Margin = new(24, 0, 24, IsPlayerVisible ? 132 : 32);
        UpdateConnectionBanner();
        ScheduleServerRefresh();
        ChromeChanged?.Invoke(this, EventArgs.Empty);
        ResumePendingWindowsActivation();
        _ = OpenPendingEpisodeNotificationAsync();
    }

    private void SetHeaderTintVisible(bool visible)
    {
        if (_headerTintVisible == visible) return;
        var from = _headerGradient.Opacity;
        _headerTintTransition?.Stop();
        _headerTintVisible = visible;
        var opacity = visible ? 1d : 0d;
        if (!IsLoaded || !_systemUiSettings.AnimationsEnabled)
        {
            _headerGradient.Opacity = opacity;
            _headerTintTransition = null;
            return;
        }
        _headerGradient.Opacity = from;
        var animation = new DoubleAnimation
        {
            From = from, To = opacity, Duration = new(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        var transition = new Storyboard();
        Storyboard.SetTarget(animation, _headerGradient);
        Storyboard.SetTargetProperty(animation, "Opacity");
        transition.Children.Add(animation);
        transition.Completed += (_, _) =>
        {
            if (!ReferenceEquals(transition, _headerTintTransition)) return;
            _headerGradient.Opacity = opacity;
            transition.Stop();
            _headerTintTransition = null;
        };
        _headerTintTransition = transition;
        transition.Begin();
    }

    private void AnimateBack(double x)
    {
        if (_back.RenderTransform is not TranslateTransform transform) return;
        var animation = new DoubleAnimation
        {
            To = x, Duration = new(TimeSpan.FromMilliseconds(200)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, transform);
        Storyboard.SetTargetProperty(animation, "X");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void RegisterGlobalAccelerators()
    {
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, NavigateBackAsync, "back");
        AddAccelerator(VirtualKey.Right, VirtualKeyModifiers.Menu, NavigateForwardAsync, "forward");
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => InvokeBrowseShortcutAsync("search"), "search");
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, () => InvokeBrowseShortcutAsync("settings"), "settings");
        AddAccelerator(VirtualKey.F5, VirtualKeyModifiers.None, RefreshCurrentPageAsync, "refresh");
        for (var index = 0; index < 3; index++)
        {
            var order = index;
            AddAccelerator((VirtualKey)((int)VirtualKey.Number1 + index), VirtualKeyModifiers.Control, async () =>
            {
                if (order < _navigation.KeyboardNavigationOrder.Count)
                    await InvokeBrowseShortcutAsync(_navigation.KeyboardNavigationOrder[order]);
            }, $"primary-page-{order + 1}");
        }
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Func<Task> action, string actionName)
    {
        _previewShortcuts[(key, modifiers)] = action;
        _previewShortcutNames[(key, modifiers)] = actionName;
        if (modifiers == VirtualKeyModifiers.Control)
        {
            _controlShortcuts[key] = action;
            _controlShortcutNames[key] = actionName;
        }
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += async (_, args) =>
        {
            if (_acceptanceTraceEnabled) AcceptanceDiagnostics.Trace($"shell-accelerator action={actionName} handled={args.Handled} enabled={CanInvokeGlobalShortcut} islandFocused={ContentIslandHasFocus()}");
            if (!CanInvokeGlobalShortcut) return;
            args.Handled = true;
            if (modifiers == VirtualKeyModifiers.Control)
            {
                if (_completedControlKeyUps.Contains(key) && !IsKeyDown(key)) return;
                _completedControlKeyUps.Remove(key);
                if (!_claimedControlKeys.Add(key)) return;
            }
            else if (!_claimedPreviewShortcuts.Add((key, modifiers))) return;
            await InvokeGlobalShortcutAsync(action);
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private bool CanInvokeGlobalShortcut => _session is not null && !_transitioning && !_returning && !_closing
        && _shellDialog is null && !_player.IsModalOpen;

    private void AttachGlobalKeyboardInput()
    {
        var root = XamlRoot?.Content as UIElement ?? this;
        if (ReferenceEquals(_globalKeyboardRoot, root)) return;
        DetachGlobalKeyboardInput();
        _globalKeyboardRoot = root;
        root.AddHandler(PreviewKeyDownEvent, _globalPreviewKeyDown, true);
        root.AddHandler(PreviewKeyUpEvent, _globalPreviewKeyUp, true);
        root.AddHandler(KeyDownEvent, _globalKeyDown, true);
        _contentInputFocus = XamlRoot?.ContentIsland is { } island ? InputFocusController.GetForIsland(island) : null;
    }

    private void DetachGlobalKeyboardInput()
    {
        if (_globalKeyboardRoot is { } root)
        {
            root.RemoveHandler(PreviewKeyDownEvent, _globalPreviewKeyDown);
            root.RemoveHandler(PreviewKeyUpEvent, _globalPreviewKeyUp);
            root.RemoveHandler(KeyDownEvent, _globalKeyDown);
            _globalKeyboardRoot = null;
        }
        ResetGlobalShortcutPressState();
        _contentInputFocus = null;
    }

    private async void ShellPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        TraceShortcutInput("down", args);
        if (args.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl && !args.KeyStatus.WasKeyDown)
        {
            // A release in another window has no routed KeyUp in this root.
            ResetControlShortcutPressState();
            _observedControlPress = true;
            return;
        }
        if (args.Key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu && !args.KeyStatus.WasKeyDown)
        {
            _claimedPreviewShortcuts.RemoveWhere(shortcut => shortcut.Modifiers == VirtualKeyModifiers.Menu);
            return;
        }
        if (args.Handled || !CanInvokeGlobalShortcut) return;
        var modifiers = CurrentShortcutModifiers();
        if (!_previewShortcuts.TryGetValue((args.Key, modifiers), out var action)) return;
        // Claim the press before awaiting so routed keys and native accelerators share one dispatch.
        args.Handled = true;
        if (modifiers == VirtualKeyModifiers.Control)
        {
            if (!args.KeyStatus.WasKeyDown) _completedControlKeyUps.Remove(args.Key);
            if (!_claimedControlKeys.Add(args.Key)) return;
        }
        else if (!_claimedPreviewShortcuts.Add((args.Key, modifiers))) return;
        await InvokeGlobalShortcutAsync(action);
    }

    private async void ShellPreviewKeyUp(object sender, KeyRoutedEventArgs args)
    {
        TraceShortcutInput("up", args);
        if (args.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl)
        {
            ResetControlShortcutPressState();
            return;
        }
        if (args.Key is VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu)
        {
            _claimedPreviewShortcuts.RemoveWhere(shortcut => shortcut.Modifiers == VirtualKeyModifiers.Menu);
            return;
        }
        if (_claimedPreviewShortcuts.RemoveWhere(shortcut => shortcut.Key == args.Key) > 0)
        {
            args.Handled = true;
            return;
        }
        var claimed = _claimedControlKeys.Remove(args.Key);
        if (!_controlShortcuts.TryGetValue(args.Key, out var action)) return;
        if (claimed)
        {
            _completedControlKeyUps.Add(args.Key);
            args.Handled = true;
            return;
        }
        if (args.Handled || !_observedControlPress || !CanInvokeGlobalShortcut || !args.KeyStatus.WasKeyDown) return;
        var control = IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.LeftControl) || IsKeyDown(VirtualKey.RightControl);
        var alt = IsKeyDown(VirtualKey.Menu) || IsKeyDown(VirtualKey.LeftMenu) || IsKeyDown(VirtualKey.RightMenu);
        var shift = IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftShift) || IsKeyDown(VirtualKey.RightShift);
        if (!control || alt || shift || !_completedControlKeyUps.Add(args.Key)) return;
        // Some input paths deliver a registered action's release without its routed press.
        args.Handled = true;
        if (_acceptanceTraceEnabled)
            AcceptanceDiagnostics.Trace($"shell-keyup-fallback action={_controlShortcutNames[args.Key]} islandFocused={ContentIslandHasFocus()}");
        await InvokeGlobalShortcutAsync(action);
    }

    public void ResetGlobalShortcutPressState()
    {
        ResetControlShortcutPressState();
        _claimedPreviewShortcuts.Clear();
    }

    private void ResetControlShortcutPressState()
    {
        _observedControlPress = false;
        _claimedControlKeys.Clear();
        _completedControlKeyUps.Clear();
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static VirtualKeyModifiers CurrentShortcutModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.LeftControl) || IsKeyDown(VirtualKey.RightControl))
            modifiers |= VirtualKeyModifiers.Control;
        if (IsKeyDown(VirtualKey.Menu) || IsKeyDown(VirtualKey.LeftMenu) || IsKeyDown(VirtualKey.RightMenu))
            modifiers |= VirtualKeyModifiers.Menu;
        if (IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.LeftShift) || IsKeyDown(VirtualKey.RightShift))
            modifiers |= VirtualKeyModifiers.Shift;
        if (IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
        return modifiers;
    }

    public bool EnsureContentKeyboardFocus(bool reassertNativeFocus = false)
    {
        if (_closing || XamlRoot?.ContentIsland is not { } island) return false;
        try
        {
            var focus = _contentInputFocus ??= InputFocusController.GetForIsland(island);
            var hadFocus = focus.HasFocus;
            var acquired = (!reassertNativeFocus && hadFocus) || focus.TrySetFocus();
            if (_acceptanceTraceEnabled) AcceptanceDiagnostics.Trace($"shell-content-focus hadFocus={hadFocus} reassert={reassertNativeFocus} acquired={acquired}");
            return acquired;
        }
        catch (Exception exception)
        {
            AcceptanceDiagnostics.Record(exception, "LumenShellPage.EnsureContentKeyboardFocus");
            return false;
        }
    }

    private bool ContentIslandHasFocus()
    {
        try { return _contentInputFocus?.HasFocus == true; }
        catch (Exception) { return false; }
    }

    private void TraceShortcutInput(string stage, KeyRoutedEventArgs args)
    {
        if (!_acceptanceTraceEnabled) return;
        var modifiers = CurrentShortcutModifiers();
        var control = (modifiers & VirtualKeyModifiers.Control) != 0;
        var alt = (modifiers & VirtualKeyModifiers.Menu) != 0;
        var shift = (modifiers & VirtualKeyModifiers.Shift) != 0;
        var action = _previewShortcutNames.GetValueOrDefault((args.Key, modifiers), "none");
        var family = args.Key is VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl
            ? "control" : action != "none" ? "registered-action" : "other";
        AcceptanceDiagnostics.Trace($"shell-key stage={stage} family={family} action={action} handled={args.Handled} wasKeyDown={args.KeyStatus.WasKeyDown} control={control} alt={alt} shift={shift} enabled={CanInvokeGlobalShortcut} islandFocused={ContentIslandHasFocus()}");
    }

    private void TraceViewportSize()
    {
        if (!_acceptanceTraceEnabled || ActualWidth <= 0 || ActualHeight <= 0
            || Math.Abs(ActualWidth - _lastTracedWidth) < .1 && Math.Abs(ActualHeight - _lastTracedHeight) < .1) return;
        _lastTracedWidth = ActualWidth;
        _lastTracedHeight = ActualHeight;
        AcceptanceDiagnostics.Trace(string.Format(CultureInfo.InvariantCulture, "shell-viewport width={0:0.##} height={1:0.##} scale={2:0.###}",
            ActualWidth, ActualHeight, XamlRoot?.RasterizationScale ?? 1));
    }

    private async Task InvokeGlobalShortcutAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
    }

    private async Task InvokeBrowseShortcutAsync(string key)
    {
        if (IsPlayerVisible) await ReturnToLibraryAsync();
        await NavigateAsync(key);
        if (key != "search" && _session is not null && !_closing && !IsPlayerVisible) _navigation.FocusSelected();
    }

    private async Task RefreshCurrentPageAsync()
    {
        if (_session is null || IsPlayerVisible || _settingsVisible) return;
        await _library.RefreshAsync(_sessionLifetime?.Token ?? CancellationToken.None);
        UpdateChrome();
    }

    private async void ShellPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (_shellDialog is not null || _player.IsModalOpen) return;
        var update = args.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (update == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed) { args.Handled = true; await NavigateBackAsync(); }
        else if (update == Microsoft.UI.Input.PointerUpdateKind.XButton2Pressed) { args.Handled = true; await NavigateForwardAsync(); }
    }

    private async void ShellKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || _transitioning || _closing || _player.IsModalOpen || _shellDialog is not null) return;
        var control = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        var alt = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot);
        if (args.Key == VirtualKey.Escape)
        {
            if (IsPlayerVisible && _player.IsSettingsOpen) return;
            args.Handled = true;
            if (_navigation.DismissMenus()) return;
            if (IsPlayerVisible && (_fullscreen || _compactOverlay)) ExitFullscreenRequested?.Invoke(this, EventArgs.Empty);
            else await NavigateBackAsync();
            return;
        }
        if (focused is TextBox or PasswordBox or RichEditBox) return;
        if (args.Key == (VirtualKey)191 && shift && !control)
        {
            args.Handled = true;
            await ShowKeyboardShortcutsAsync();
            return;
        }
        if (!IsPlayerVisible)
        {
            if (!_settingsVisible && _session is not null && !control && !alt && _library.HandleNavigationKey(args)) args.Handled = true;
            return;
        }
        if (_player.IsSettingsOpen || focused is ComboBox or Slider) return;
        if (alt || control && args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
        if (args.Key == VirtualKey.Space && focused is ButtonBase) return;
        if (args.Key is VirtualKey.Space or VirtualKey.K or VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.PageUp or VirtualKey.PageDown or VirtualKey.F or VirtualKey.F11 or VirtualKey.M
            or VirtualKey.C or VirtualKey.A or VirtualKey.Z or VirtualKey.X or VirtualKey.P or VirtualKey.I
            || args.Key is (VirtualKey)219 or (VirtualKey)221)
        {
            args.Handled = true;
            try { await _player.HandlePlaybackShortcutAsync(args.Key, control, shift); }
            catch (Exception exception) { if (!_closing) ShowError(exception); }
        }
    }

    private async Task ShowKeyboardShortcutsAsync()
    {
        if (_shellDialog is not null || _player.IsModalOpen || XamlRoot is null) return;
        _navigation.DismissMenus();
        var layout = new StackPanel { Spacing = 24, MinWidth = 360, MaxWidth = 760 };
        foreach (var (heading, rows) in new (string Heading, (string Label, string Keys)[] Rows)[]
        {
            ("Global", [("Search", "Ctrl+F"), ("Back / Forward", "Alt+\u2190 / \u2192"), ("Mouse navigation", "X1 / X2"),
                ("Home / Movies / TV shows", "Ctrl+1 / 2 / 3"), ("Refresh current page", "F5"), ("Settings", "Ctrl+,"),
                ("Move focus", "Tab / Shift+Tab"), ("Activate focused control", "Enter / Space"), ("Keyboard shortcuts", "?")]),
            ("Browse", [("Move focus", "\u2191 \u2193 \u2190 \u2192"), ("Open details", "Enter"), ("Play", "Space"),
                ("Context menu", "Shift+F10"), ("Jump to letter", "A\u2013Z"), ("First / Last item", "Home / End")]),
            ("Playback", [("Play / Pause", "Space"), ("Seek backward / forward", "\u2190 / \u2192"), ("Previous / Next chapter", "Ctrl+\u2190 / \u2192"),
                ("Previous / Next episode", "PgUp / PgDn"), ("Volume", "\u2191 / \u2193"), ("Mute", "M"),
                ("Cycle subtitle / audio", "C / A"), ("Subtitle delay", "Z / X"), ("Audio delay", "Shift+Z / X"),
                ("Playback speed", "[ / ]"), ("Fullscreen", "F / F11"), ("Exit fullscreen / Back", "Esc"),
                ("Picture in picture", "P"), ("Playback information", "I")])
        })
        {
            var group = new StackPanel { Spacing = 8 };
            var label = LumenUi.Text(ShellText(heading), 16);
            label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            label.Margin = new(0, 0, 0, 4);
            group.Children.Add(label);
            foreach (var (rowLabel, keys) in rows)
            {
                var row = new Grid { ColumnSpacing = 24 };
                row.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                row.Children.Add(LumenUi.Text(ShellText(rowLabel), 13));
                var key = LumenUi.Text(keys, 12);
                key.Foreground = LumenTheme.Brush("Sub");
                Grid.SetColumn(key, 1);
                row.Children.Add(key);
                group.Children.Add(row);
            }
            layout.Children.Add(group);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = PreferredTheme, Title = ShellText("Keyboard shortcuts"),
            Content = new ScrollViewer { Content = layout, MaxHeight = Math.Max(220, XamlRoot.Size.Height - 250), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
            CloseButtonText = LumenText.Get("Close"), DefaultButton = ContentDialogButton.Close
        };
        LumenDialogTheme.Apply(dialog);
        _shellDialog = dialog;
        try { await dialog.ShowAsync(); }
        catch (Exception exception) { if (!_closing) ShowError(exception); }
        finally { if (ReferenceEquals(_shellDialog, dialog)) _shellDialog = null; ResumePendingWindowsActivation(); _ = OpenPendingEpisodeNotificationAsync(); }
    }

    private void ThemeChanged(object? sender, EventArgs args)
    {
        if (_applyingPreferences || _closing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closing) return;
            RequestedTheme = PreferredTheme;
            _settings.SetPreferences(_preferences);
            _library.ApplyPreferences(_preferences);
            _player.ApplyPreferences(_preferences);
            UpdateChrome();
        });
    }

    private void SystemColorsChanged(UISettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closing && _preferences.Theme == "System") ApplyPreferences(_preferences);
    });

    private void UnsubscribeSystemColors()
    {
        if (!_systemColorsSubscribed) return;
        try { _systemUiSettings.ColorValuesChanged -= SystemColorsChanged; }
        catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
        _systemColorsSubscribed = false;
    }

    public void SetFullscreenState(bool fullscreen)
    {
        _fullscreen = fullscreen;
        UpdateEpisodeNotificationContext();
        _player.SetFullscreenState(fullscreen);
    }
    public void SetCompactOverlayState(bool compactOverlay) { _compactOverlay = compactOverlay; _player.SetCompactOverlayState(compactOverlay); }
    public void SetCaptionPointerActive(bool active) => _player.SetCaptionPointerActive(active);
    public void SetWindowTitleBarIntegrated(bool integrated) => _player.SetWindowTitleBarIntegrated(integrated);
    public void RequestTaskbarCommand(NativeMediaCommandEventArgs args) => _player.RequestTaskbarCommand(args);

    private static string ShellText(string key) => key switch
    {
        "Sign out of Lumen?" => "\u9000\u51fa Lumen\uff1f",
        "The image cache has been cleared." => "\u56fe\u7247\u7f13\u5b58\u5df2\u6e05\u9664\u3002",
        "This account will require a password the next time you sign in." => "\u4e0b\u6b21\u4f7f\u7528\u6b64\u8d26\u6237\u767b\u5f55\u65f6\u9700\u8981\u91cd\u65b0\u8f93\u5165\u5bc6\u7801\u3002",
        "Keyboard shortcuts" => "\u952e\u76d8\u5feb\u6377\u952e",
        "Global" => "\u5168\u5c40", "Browse" => "\u6d4f\u89c8", "Playback" => "\u64ad\u653e",
        "Back / Forward" => "\u540e\u9000 / \u524d\u8fdb", "Mouse navigation" => "\u9f20\u6807\u4fa7\u952e\u540e\u9000 / \u524d\u8fdb",
        "Home / Movies / TV shows" => "\u9996\u9875 / \u7535\u5f71 / \u5267\u96c6", "Refresh current page" => "\u5237\u65b0\u5f53\u524d\u9875",
        "Move focus" => "\u79fb\u52a8\u7126\u70b9", "Activate focused control" => "\u6309\u4e0b\u5f53\u524d\u6309\u94ae",
        "Context menu" => "\u53f3\u952e\u83dc\u5355", "Jump to letter" => "\u8df3\u5230\u9996\u5b57\u6bcd",
        "First / Last item" => "\u7b2c\u4e00\u4e2a / \u6700\u540e\u4e00\u4e2a",
        "Play / Pause" => "\u64ad\u653e / \u6682\u505c", "Seek backward / forward" => "\u540e\u9000 10 \u79d2 / \u524d\u8fdb 30 \u79d2",
        "Previous / Next chapter" => "\u4e0a\u4e00\u7ae0 / \u4e0b\u4e00\u7ae0", "Previous / Next episode" => "\u4e0a\u4e00\u96c6 / \u4e0b\u4e00\u96c6",
        "Cycle subtitle / audio" => "\u5207\u6362\u5b57\u5e55 / \u97f3\u8f68", "Subtitle delay" => "\u5b57\u5e55\u63d0\u524d / \u5ef6\u540e 0.1 \u79d2",
        "Audio delay" => "\u97f3\u9891\u63d0\u524d / \u5ef6\u540e 0.1 \u79d2", "Playback speed" => "\u500d\u901f",
        "Exit fullscreen / Back" => "\u9000\u51fa\u5168\u5c4f / \u8fd4\u56de", "Picture in picture" => "\u753b\u4e2d\u753b",
        "Playback information" => "\u64ad\u653e\u4fe1\u606f", _ => LumenText.Get(key)
    };

    private void ShowError(Exception exception)
    {
        var diagnosticRoot = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(diagnosticRoot) && Directory.Exists(diagnosticRoot))
        {
            try
            {
                // Opt-in acceptance diagnostics contain code locations, never messages, credentials, or server payloads.
                File.AppendAllText(Path.Combine(diagnosticRoot, "handled-ui-errors.log"),
                    $"{DateTimeOffset.UtcNow:O} {exception.GetType().Name} 0x{exception.HResult:X8}\n{exception.StackTrace}\n");
            }
            catch (Exception) { }
        }
        ShowNotice(LumenText.Get(UiErrors.Describe(exception)), InfoBarSeverity.Error);
    }

    private static void RecordStartupPhase(string phase)
    {
        var diagnosticRoot = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(diagnosticRoot) || !Directory.Exists(diagnosticRoot)) return;
        try { File.AppendAllText(Path.Combine(diagnosticRoot, "startup-phases.log"), $"{DateTimeOffset.UtcNow:O} {phase}\n"); }
        catch (Exception) { }
    }
    private void ShowNotice(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        _notice.Message = message;
        _notice.Severity = severity;
        _notice.IsOpen = true;
    }

    private sealed partial class LumenToastNotice : Grid
    {
        private readonly TextBlock _message = LumenUi.Text(string.Empty, 13);
        private readonly Border _dot = new() { Width = 6, Height = 6, CornerRadius = new(3), VerticalAlignment = VerticalAlignment.Center };
        private readonly DispatcherTimer _lifetime = new() { Interval = TimeSpan.FromMilliseconds(3400) };
        private Storyboard? _animation;
        private bool _isOpen;
        private InfoBarSeverity _severity;

        public LumenToastNotice()
        {
            Visibility = Visibility.Collapsed;
            VerticalAlignment = VerticalAlignment.Bottom;
            HorizontalAlignment = HorizontalAlignment.Center;
            Margin = new(24, 0, 24, 32);
            Padding = new(18, 0, 18, 0);
            MinHeight = 44;
            MaxWidth = 920;
            CornerRadius = new(24);
            BorderThickness = new(1);
            BorderBrush = LumenTheme.Brush("LineStrong");
            Background = LumenTheme.Brush("Pop");
            ColumnSpacing = 12;
            ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            RenderTransform = new TranslateTransform();
            _message.TextWrapping = TextWrapping.NoWrap;
            _message.TextTrimming = TextTrimming.CharacterEllipsis;
            _message.Margin = new(0, 12, 0, 12);
            Grid.SetColumn(_message, 1);
            Children.Add(_dot);
            Children.Add(_message);
            AutomationProperties.SetLiveSetting(this, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
            AutomationProperties.SetAutomationId(this, "LumenToast");
            _lifetime.Tick += (_, _) => IsOpen = false;
            Unloaded += (_, _) => _lifetime.Stop();
        }

        public string Message
        {
            get => _message.Text;
            set { _message.Text = value; AutomationProperties.SetName(this, value); }
        }

        public InfoBarSeverity Severity
        {
            get => _severity;
            set { _severity = value; _dot.Background = LumenTheme.Brush(value == InfoBarSeverity.Error ? "Danger" : "Accent"); }
        }

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                _lifetime.Stop();
                _isOpen = value;
                _animation?.Stop();
                if (!value && Visibility == Visibility.Collapsed) return;
                if (value) { Visibility = Visibility.Visible; _lifetime.Start(); }
                var story = new Storyboard();
                var opacity = new DoubleAnimation
                {
                    From = value ? 0 : 1, To = value ? 1 : 0, Duration = new(TimeSpan.FromMilliseconds(250))
                };
                Storyboard.SetTarget(opacity, this);
                Storyboard.SetTargetProperty(opacity, "Opacity");
                story.Children.Add(opacity);
                var movement = new DoubleAnimation
                {
                    From = value ? 12 : 0, To = value ? 0 : 12, Duration = new(TimeSpan.FromMilliseconds(300)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(movement, this);
                Storyboard.SetTargetProperty(movement, "(UIElement.RenderTransform).(TranslateTransform.Y)");
                story.Children.Add(movement);
                story.Completed += (_, _) => { if (!_isOpen) Visibility = Visibility.Collapsed; };
                _animation = story;
                story.Begin();
            }
        }
    }
}
