using System.Globalization;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private readonly List<SignInServer> _signInServers = [];
    private readonly StackPanel _serverRows = new() { Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Grid _userTileGrid = new() { ColumnSpacing = 22, RowSpacing = 22, Margin = new Thickness(0, 20, 0, 0) };
    private readonly SemaphoreSlim _signInAvatarGate = new(4, 4);
    private readonly Dictionary<string, byte[]> _signInAvatarBytes = new(StringComparer.Ordinal);
    private readonly HashSet<(string ServerId, string UserId)> _signInPasswordPrompts = [];
    private static readonly SolidColorBrush SignInErrorBrush = new(Color.FromArgb(255, 224, 121, 111));
    private SignInServer? _selectedSignInServer;
    private UserDto? _selectedSignInUser;
    private CancellationTokenSource? _signInImages;
    private StackPanel _signInContent = null!;
    private StackPanel _manualUserField = null!;
    private Grid _signInCards = null!;
    private Grid _signInCredentialRow = null!;
    private Grid _signInRememberRow = null!;
    private Border _serverCard = null!;
    private Border _viewerCard = null!;
    private Border _passwordFocusRing = null!;
    private Button _probeServer = null!;
    private ToggleButton _rememberToggle = null!;
    private TextBlock _serverCount = null!;
    private TextBlock _viewerServer = null!;
    private TextBlock _publicUserHint = null!;
    private TextBlock _passwordCaption = null!;
    private TextBlock _credentialError = null!;
    private TextBlock _passwordlessHint = null!;
    private TextBlock _signInHeading = null!;
    private bool _manualSignInUser = true;
    private bool _updatingSignIn;

    private (TextBox, TextBox, PasswordBox, CheckBox, Button, Button, ComboBox, StackPanel, StackPanel,
        ProgressRing, TextBlock, Button, TextBlock, TextBlock) CreateConnectionPane()
    {
        RegisterSignInText();
        _connectionPane.Background = LumenTheme.Brush("Background");
        _connectionPane.Children.Add(_signInBackdropHost);
        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _signInContent = new StackPanel
        {
            Width = 1068, MaxWidth = 1068, Margin = new Thickness(24, 104, 24, 64),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var welcome = LumenUi.Text(LumenText.Get("Welcome to LUMEN"), 12);
        welcome.Foreground = LumenTheme.Brush("Accent");
        welcome.CharacterSpacing = 220;
        welcome.FontWeight = FontWeights.Bold;
        _signInContent.Children.Add(welcome);
        _signInHeading = LumenUi.Text(LumenText.Get("Connect to your Emby server"), 44, serif: true);
        _signInHeading.FontWeight = FontWeights.Black;
        _signInHeading.LineHeight = 44 * 1.15;
        _signInHeading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _signInHeading.Margin = new Thickness(0, 12, 0, 0);
        _signInHeading.CharacterSpacing = 30;
        AutomationProperties.SetHeadingLevel(_signInHeading, AutomationHeadingLevel.Level1);
        _signInContent.Children.Add(_signInHeading);
        var introduction = LumenUi.Text(LumenText.Get("Save your server and account on this PC for a quicker return."), 15);
        introduction.Foreground = LumenTheme.Brush("Sub");
        introduction.Margin = new Thickness(0, 12, 0, 0);
        _signInContent.Children.Add(introduction);

        _signInCards = new Grid { ColumnSpacing = 24, RowSpacing = 24, Margin = new Thickness(0, 40, 0, 0) };
        var servers = new StackPanel();
        _serverCount = LumenUi.Text(string.Empty, 12);
        _serverCount.Foreground = LumenTheme.Brush("Muted");
        var serverCount = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var countMarker = new Grid { Width = 6, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        countMarker.Children.Add(new Ellipse { Width = 12, Height = 12, Margin = new Thickness(-3), Fill = LumenTheme.Brush("Accent"), Opacity = .2 });
        countMarker.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = LumenTheme.Brush("Accent") });
        AutomationProperties.SetAccessibilityView(countMarker, AccessibilityView.Raw);
        serverCount.Children.Add(countMarker);
        serverCount.Children.Add(_serverCount);
        servers.Children.Add(SignInSectionHeader("Server", serverCount));
        servers.Children.Add(_serverRows);
        var manualLabel = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 18, 0, 14) };
        manualLabel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        manualLabel.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        manualLabel.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var firstRule = LumenUi.Divider();
        firstRule.VerticalAlignment = VerticalAlignment.Center;
        manualLabel.Children.Add(firstRule);
        var manual = LumenUi.Text(LumenText.Get("Or enter a server address"), 12);
        manual.Foreground = LumenTheme.Brush("Muted");
        Grid.SetColumn(manual, 1);
        manualLabel.Children.Add(manual);
        var secondRule = LumenUi.Divider();
        secondRule.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(secondRule, 2);
        manualLabel.Children.Add(secondRule);
        servers.Children.Add(manualLabel);

        var serverEntry = new Grid { ColumnSpacing = 8 };
        serverEntry.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        serverEntry.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var address = new TextBox
        {
            PlaceholderText = "https://emby.example.com:8920", Height = 44, MinWidth = 0,
            CornerRadius = new(22), Background = LumenTheme.Brush("Control"),
            Foreground = LumenTheme.Brush("Ink"), BorderBrush = LumenTheme.Brush("LineStrong"),
            Padding = new(40, 10, 14, 10), FontFamily = LumenTheme.SansFont, FontSize = 14,
            InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Url) } },
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false
        };
        address.Resources["TextControlBorderBrushFocused"] = LumenTheme.Brush("Accent");
        AutomationProperties.SetName(address, LumenText.Get("Server address"));
        AutomationProperties.SetAutomationId(address, "LumenServerAddress");
        var addressField = new Grid();
        addressField.Children.Add(address);
        var globe = LumenUi.Icon("globe_20_regular", 16);
        globe.HorizontalAlignment = HorizontalAlignment.Left;
        globe.Margin = new Thickness(16, 0, 0, 0);
        globe.Opacity = .7;
        addressField.Children.Add(globe);
        serverEntry.Children.Add(addressField);
        _probeServer = LumenUi.Button(LumenText.Get("Connect"), height: 44);
        _probeServer.Padding = new Thickness(20, 0, 20, 0);
        Grid.SetColumn(_probeServer, 1);
        serverEntry.Children.Add(_probeServer);
        servers.Children.Add(serverEntry);
        var addressHint = LumenUi.Text(LumenText.Get("Use HTTP or HTTPS. Without a port, Lumen also tries Emby ports 8096 and 8920."), 12);
        addressHint.Foreground = LumenTheme.Brush("Muted");
        addressHint.LineHeight = 19.2;
        addressHint.Margin = new Thickness(0, 10, 0, 0);
        servers.Children.Add(addressHint);
        var serverError = SignInError();
        serverError.LineHeight = 19.2;
        serverError.Margin = new Thickness(0, 10, 0, 0);
        BindSignInError(address, serverError, addressHint);
        servers.Children.Add(serverError);
        servers.Children.Add(CreateServerDiscoveryControls());
        _serverCard = SignInCard(servers, new Thickness(22));
        _serverCard.MaxWidth = 520;
        _signInCards.Children.Add(_serverCard);

        var viewers = new StackPanel();
        _viewerServer = LumenUi.Text(string.Empty, 12);
        _viewerServer.Foreground = LumenTheme.Brush("Muted");
        viewers.Children.Add(SignInSectionHeader("Who is watching?", _viewerServer));
        viewers.Children.Add(_userTileGrid);
        _publicUserHint = LumenUi.Text(LumenText.Get("Connect to a server to see its public accounts."), 12);
        _publicUserHint.Foreground = LumenTheme.Brush("Muted");
        _publicUserHint.Margin = new Thickness(0, 8, 0, 0);
        viewers.Children.Add(_publicUserHint);
        var divider = LumenUi.Divider();
        divider.Margin = new Thickness(0, 24, 0, 20);
        viewers.Children.Add(divider);
        _manualUserField = new StackPanel();
        var userNameCaption = LumenUi.Text(LumenText.Get("Username"), 13);
        userNameCaption.Foreground = LumenTheme.Brush("Sub");
        _manualUserField.Children.Add(userNameCaption);
        var userName = new TextBox
        {
            PlaceholderText = LumenText.Get("Enter your username"), Height = 48, MinWidth = 0,
            CornerRadius = new(24), Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"),
            BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1.5), Padding = new(20, 0, 20, 0),
            FontFamily = LumenTheme.SansFont, FontSize = 15, Margin = new Thickness(0, 10, 0, 16),
            VerticalContentAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = false, IsTextPredictionEnabled = false
        };
        userName.Resources["TextControlBorderBrushFocused"] = LumenTheme.Brush("Accent");
        AutomationProperties.SetName(userName, LumenText.Get("Username"));
        AutomationProperties.SetAutomationId(userName, "LumenUsername");
        _manualUserField.Children.Add(userName);
        var userError = SignInError();
        BindSignInError(userName, userError);
        _manualUserField.Children.Add(userError);
        viewers.Children.Add(_manualUserField);
        _passwordCaption = LumenUi.Text(LumenText.Get("Password"), 13);
        _passwordCaption.Foreground = LumenTheme.Brush("Sub");
        viewers.Children.Add(_passwordCaption);
        var passwordLine = _signInCredentialRow = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        passwordLine.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        passwordLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var password = new PasswordBox
        {
            PlaceholderText = LumenText.Get("Enter password"), Height = 48, MinWidth = 0,
            CornerRadius = new(24), Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"),
            BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1.5),
            Padding = new(20, 0, 20, 0), FontSize = 16, FontFamily = LumenTheme.SansFont,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        password.Resources["TextControlBorderBrushFocused"] = LumenTheme.Brush("Accent");
        AutomationProperties.SetName(password, LumenText.Get("Password"));
        AutomationProperties.SetAutomationId(password, "LumenPassword");
        _passwordFocusRing = new Border
        {
            Child = password, BorderThickness = new Thickness(4), CornerRadius = new CornerRadius(28),
            Margin = new Thickness(-4), BorderBrush = new SolidColorBrush(Colors.Transparent)
        };
        passwordLine.Children.Add(_passwordFocusRing);
        _passwordlessHint = LumenUi.Text(LumenText.Get("This account does not require a password."), 15);
        _passwordlessHint.FontWeight = FontWeights.SemiBold;
        _passwordlessHint.VerticalAlignment = VerticalAlignment.Center;
        _passwordlessHint.Visibility = Visibility.Collapsed;
        passwordLine.Children.Add(_passwordlessHint);
        var signIn = LumenUi.Button(LumenText.Get("Sign in"), primary: true, height: 48);
        signIn.Padding = new Thickness(30, 0, 30, 0);
        Grid.SetColumn(signIn, 1);
        passwordLine.Children.Add(signIn);
        viewers.Children.Add(passwordLine);
        _credentialError = SignInError();
        _credentialError.MinHeight = 16;
        _credentialError.Margin = new Thickness(0, 8, 0, 0);
        _credentialError.Visibility = Visibility.Visible;
        BindSignInError(password, _credentialError);
        void RefreshPasswordFocus()
        {
            var focused = password.FocusState != FocusState.Unfocused && string.IsNullOrWhiteSpace(_credentialError.Text);
            _passwordFocusRing.BorderBrush = new SolidColorBrush(focused ? Color.FromArgb(31, 226, 184, 107) : Colors.Transparent);
        }
        password.GotFocus += (_, _) => RefreshPasswordFocus();
        password.LostFocus += (_, _) => RefreshPasswordFocus();
        _credentialError.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => RefreshPasswordFocus());
        _credentialError.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateSignInCredentialErrorPresentation());
        viewers.Children.Add(_credentialError);
        var remember = new CheckBox { IsChecked = true, Visibility = Visibility.Collapsed };
        viewers.Children.Add(remember);
        _signInRememberRow = CreateRememberChoice(remember);
        viewers.Children.Add(_signInRememberRow);

        var saved = new ComboBox { Visibility = Visibility.Collapsed };
        var restore = LumenUi.Button(LumenText.Get("Continue with saved session"), "person_20_regular", height: 38);
        restore.HorizontalAlignment = HorizontalAlignment.Left;
        var savedPanel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 18, 0, 0), Visibility = Visibility.Collapsed };
        savedPanel.Children.Add(saved);
        savedPanel.Children.Add(restore);
        viewers.Children.Add(savedPanel);
        var connectionRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        var connecting = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed };
        var status = LumenUi.Text(string.Empty, 13);
        status.VerticalAlignment = VerticalAlignment.Center;
        var cancel = LumenUi.Button(LumenText.Get("Cancel"), height: 32);
        cancel.Visibility = Visibility.Collapsed;
        connectionRow.Children.Add(connecting);
        connectionRow.Children.Add(status);
        connectionRow.Children.Add(cancel);
        void RefreshConnectionStatus() => connectionRow.Visibility = connecting.Visibility == Visibility.Visible || !string.IsNullOrWhiteSpace(status.Text)
            ? Visibility.Visible : Visibility.Collapsed;
        connecting.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => RefreshConnectionStatus());
        status.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => RefreshConnectionStatus());
        viewers.Children.Add(connectionRow);
        _viewerCard = SignInCard(viewers, new Thickness(26, 22, 26, 26));
        _signInCards.Children.Add(_viewerCard);
        _signInContent.Children.Add(_signInCards);

        var recovery = new StackPanel { Spacing = 8, Margin = new Thickness(0, 20, 0, 0), Visibility = Visibility.Collapsed };
        var retry = LumenUi.Button(LumenText.Get("Retry settings"));
        retry.Click += (_, _) =>
        {
            if (_connectionRequest is not null || _closing) return;
            _initialized = false;
            ShellLoaded(this, new RoutedEventArgs());
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
                await LoadSavedSignInServersAsync();
                RefreshKnownSignInServers();
                _recoveryPanel.Visibility = Visibility.Collapsed;
                SetConnecting(false);
                _address.Focus(FocusState.Programmatic);
            }
            catch (Exception exception) { ShowError(exception); }
        };
        recovery.Children.Add(retry);
        recovery.Children.Add(temporary);
        _signInContent.Children.Add(recovery);
        scroller.Content = _signInContent;
        _connectionPane.Children.Add(scroller);
        _connectionPane.SizeChanged += (_, _) => { UpdateSignInLayout(); UpdateSignInBackdrop(); };
        _userTileGrid.SizeChanged += (_, _) => PositionSignInUsers();
        _connectionPane.Loaded += (_, _) => { BuildSignInBackdrop(); UpdateSignInLayout(); RefreshKnownSignInServers(); };
        _connectionPane.Unloaded += (_, _) => { CancelSignInArtwork(); CancelServerDiscovery(); ReleaseSignInBackdrop(); };
        signIn.Click += async (_, _) => await ConnectAsync();
        _probeServer.Click += async (_, _) => await ProbeSignInServerAsync(address.Text);
        restore.Click += async (_, _) =>
        {
            if (_savedAccounts.SelectedItem is ComboBoxItem { Tag: SavedAccount account }) await ConnectAsync(account);
        };
        cancel.Click += (_, _) => { _connectionRequest?.Cancel(); _cancelConnection.IsEnabled = false; };
        saved.SelectionChanged += (_, _) =>
        {
            if (saved.SelectedItem is not ComboBoxItem { Tag: SavedAccount account }) return;
            _accountRestriction = _connections.GetAccountRestriction(account.Key);
            UpdateSavedSignInAction();
        };
        address.TextChanged += (_, _) =>
        {
            serverError.Visibility = Visibility.Collapsed;
            _accountRestriction = null;
            if (!_updatingSignIn && (_selectedSignInServer is null || !SameSignInAddress(_selectedSignInServer.ApiRoot, address.Text)))
            {
                _selectedSignInServer = null;
                _selectedSignInUser = null;
                _manualSignInUser = true;
                userName.Text = string.Empty;
                password.Password = string.Empty;
                _credentialError.Text = string.Empty;
                RenderSignInServers();
                RenderSignInUsers();
            }
            SelectMatchingSavedAccount();
            SetSignInControlsEnabled(_settingsReady && _connectionRequest is null && !_transitioning && !_closing);
        };
        userName.TextChanged += (_, _) =>
        {
            userError.Visibility = Visibility.Collapsed;
            _credentialError.Text = string.Empty;
            _accountRestriction = null;
            SelectMatchingSavedAccount();
        };
        password.PasswordChanged += (_, _) => _credentialError.Text = string.Empty;
        address.KeyDown += async (_, args) =>
        {
            if (args.Key != VirtualKey.Enter || !_probeServer.IsEnabled) return;
            args.Handled = true;
            await ProbeSignInServerAsync(address.Text);
        };
        async void SignInOnEnter(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key != VirtualKey.Enter || !_signIn.IsEnabled) return;
            args.Handled = true;
            await ConnectAsync();
        }
        password.KeyDown += SignInOnEnter;
        userName.KeyDown += SignInOnEnter;
        return (address, userName, password, remember, signIn, restore, saved, savedPanel, recovery,
            connecting, status, cancel, serverError, userError);
    }

    private static Grid SignInSectionHeader(string title, FrameworkElement trailing)
    {
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var label = LumenUi.Text(LumenText.Get(title), 16);
        label.FontWeight = FontWeights.SemiBold;
        header.Children.Add(label);
        trailing.HorizontalAlignment = HorizontalAlignment.Right;
        trailing.VerticalAlignment = VerticalAlignment.Center;
        if (trailing is TextBlock text)
        {
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.TextWrapping = TextWrapping.NoWrap;
        }
        Grid.SetColumn(trailing, 1);
        header.Children.Add(trailing);
        return header;
    }

    private static Border SignInCard(UIElement child, Thickness padding) => new()
    {
        Child = child, Padding = padding, CornerRadius = new CornerRadius(18),
        Background = LumenTheme.Brush("Card"), BorderBrush = LumenTheme.Brush("Line"),
        BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Top
    };

    private static TextBlock SignInError()
    {
        var error = LumenUi.Text(string.Empty, 12);
        error.Foreground = SignInErrorBrush;
        error.Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
        return error;
    }

    private static void BindSignInError(Control field, TextBlock error, FrameworkElement? helper = null)
    {
        var border = new SolidColorBrush();
        var focusedBorder = new SolidColorBrush();
        field.BorderBrush = border;
        LumenUi.SetResource(field.Resources, "TextControlBorderBrush", border);
        LumenUi.SetResource(field.Resources, "TextControlBorderBrushPointerOver", border);
        LumenUi.SetResource(field.Resources, "TextControlBorderBrushFocused", focusedBorder);
        void Refresh()
        {
            var invalid = error.Visibility == Visibility.Visible && !string.IsNullOrWhiteSpace(error.Text);
            border.Color = LumenTheme.Color(invalid ? "Danger" : "LineStrong");
            focusedBorder.Color = LumenTheme.Color(invalid ? "Danger" : "Accent");
            AutomationProperties.SetHelpText(field, invalid ? error.Text : string.Empty);
            if (helper is not null) helper.Visibility = invalid ? Visibility.Collapsed : Visibility.Visible;
        }
        error.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => Refresh());
        error.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => Refresh());
        EventHandler themeChanged = (_, _) => Refresh();
        var watchingTheme = false;
        field.Loaded += (_, _) =>
        {
            if (!watchingTheme) LumenTheme.Changed += themeChanged;
            watchingTheme = true;
            Refresh();
        };
        field.Unloaded += (_, _) =>
        {
            if (watchingTheme) LumenTheme.Changed -= themeChanged;
            watchingTheme = false;
        };
        Refresh();
    }

    private Grid CreateRememberChoice(CheckBox remember)
    {
        var row = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(44) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var offset = new TranslateTransform();
        var thumb = new Ellipse { Width = 18, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = offset };
        var track = new Border { Width = 44, Height = 24, Padding = new Thickness(3), CornerRadius = new CornerRadius(12), Child = thumb };
        _rememberToggle = new ToggleButton
        {
            Width = 44, Height = 44, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), Content = track, CornerRadius = new CornerRadius(22)
        };
        foreach (var suffix in new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed", "Disabled", "CheckedDisabled" })
        {
            _rememberToggle.Resources["ToggleButtonBackground" + suffix] = new SolidColorBrush(Colors.Transparent);
            _rememberToggle.Resources["ToggleButtonBorderBrush" + suffix] = new SolidColorBrush(Colors.Transparent);
        }
        AutomationProperties.SetName(_rememberToggle, LumenText.Get("Remember me on this PC"));
        void RefreshRemember()
        {
            var selected = remember.IsChecked == true;
            _rememberToggle.IsChecked = selected;
            track.Background = LumenTheme.Brush(selected ? "Accent" : "ControlStrong");
            thumb.Fill = LumenTheme.Brush(selected ? "ImageInk" : "Knob");
            offset.X = selected ? 20 : 0;
        }
        _rememberToggle.Checked += (_, _) => remember.IsChecked = true;
        _rememberToggle.Unchecked += (_, _) => remember.IsChecked = false;
        remember.Checked += (_, _) => RefreshRemember();
        remember.Unchecked += (_, _) => RefreshRemember();
        RefreshRemember();
        row.Children.Add(_rememberToggle);
        var explanation = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = LumenUi.Text(LumenText.Get("Remember me on this PC"), 14);
        name.FontWeight = FontWeights.SemiBold;
        explanation.Children.Add(name);
        var help = LumenUi.Text(LumenText.Get("Credentials stay on this PC, encrypted with Windows DPAPI."), 12);
        help.Foreground = LumenTheme.Brush("Muted");
        explanation.Children.Add(help);
        Grid.SetColumn(explanation, 1);
        row.Children.Add(explanation);
        var enterHint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var enterKey = LumenUi.Text("Enter", 11);
        enterKey.FontWeight = FontWeights.SemiBold;
        enterKey.Foreground = LumenTheme.Brush("Sub");
        enterHint.Children.Add(new Border
        {
            Child = enterKey, Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1), BorderBrush = LumenTheme.Brush("LineStrong")
        });
        var enterAction = LumenUi.Text(LumenText.Get("Sign in"), 12);
        enterAction.Foreground = LumenTheme.Brush("Muted");
        enterHint.Children.Add(enterAction);
        Grid.SetColumn(enterHint, 2);
        row.Children.Add(enterHint);
        return row;
    }

    private void UpdateSignInLayout()
    {
        if (_signInContent is null || _connectionPane.ActualWidth <= 0) return;
        var padding = Math.Clamp(_connectionPane.ActualWidth * .04, 24, 56);
        _signInContent.Width = Math.Max(0, Math.Min(1180, _connectionPane.ActualWidth) - 2 * padding);
        _signInContent.Margin = new Thickness(padding, Math.Clamp(_connectionPane.ActualHeight * .13, 80, 150), padding, 64);
        _signInHeading.FontSize = Math.Clamp(_connectionPane.ActualWidth * .032, 32, 44);
        _signInHeading.LineHeight = _signInHeading.FontSize * 1.15;
        var stacked = _signInContent.Width < 864;
        _serverCard.Width = stacked ? Math.Min(520, _signInContent.Width) : double.NaN;
        _serverCard.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        _signInCards.ColumnDefinitions.Clear();
        _signInCards.RowDefinitions.Clear();
        if (stacked)
        {
            _signInCards.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            _signInCards.RowDefinitions.Add(new() { Height = GridLength.Auto });
            _signInCards.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        else
        {
            var extraWidth = Math.Max(0, _signInContent.Width - 24 - 400 - 440);
            var serverWidth = Math.Min(520, 400 + extraWidth / 2.4);
            _signInCards.ColumnDefinitions.Add(new() { Width = new GridLength(serverWidth) });
            _signInCards.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        }
        Grid.SetColumn(_viewerCard, stacked ? 0 : 1);
        Grid.SetRow(_viewerCard, stacked ? 1 : 0);
        PositionSignInUsers();
    }

    private void RefreshKnownSignInServers()
    {
        if (_address is null) return;
        RefreshSignInServerSources();
        var current = _signInServers.FirstOrDefault(server => SameSignInAddress(server.ApiRoot, _address.Text));
        if (current is null && string.IsNullOrWhiteSpace(_address.Text))
            current = _signInServers.FirstOrDefault(server => server.IsSaved || server.HasSavedAccount);
        if (current is not null)
        {
            _selectedSignInServer = current;
            _address.Text = current.ApiRoot;
            var saved = _savedAccounts.SelectedItem as ComboBoxItem;
            if (string.IsNullOrWhiteSpace(_userName.Text) && saved?.Tag is SavedAccount account)
                _userName.Text = account.UserName;
        }
        else if (_selectedSignInServer is not null && !_signInServers.Contains(_selectedSignInServer))
        {
            _selectedSignInServer = null;
            _selectedSignInUser = null;
            _manualSignInUser = true;
        }
        RenderSignInServers();
        RenderSignInUsers();
        SetSignInControlsEnabled(_settingsReady && _connectionRequest is null && !_transitioning && !_closing);
    }

    private void RenderSignInServers()
    {
        _serverRows.Children.Clear();
        _serverRowControls.Clear();
        _serverCount.Text = LumenText.Get("{0} servers", _signInServers.Count);
        if (_signInServers.Count == 0)
        {
            var empty = LumenUi.Text(LumenText.Get("Add your first Emby server below."), 13);
            empty.Foreground = LumenTheme.Brush("Muted");
            empty.Margin = new Thickness(0, 6, 0, 4);
            _serverRows.Children.Add(empty);
            return;
        }
        foreach (var server in _signInServers.OrderByDescending(server => server.IsSaved || server.HasSavedAccount).ThenBy(server => server.Name, StringComparer.OrdinalIgnoreCase))
        {
            var selected = ReferenceEquals(server, _selectedSignInServer);
            var content = new Grid { ColumnSpacing = 14 };
            content.ColumnDefinitions.Add(new() { Width = new GridLength(40) });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
            content.Children.Add(new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(12),
                Background = LumenTheme.Brush("Chip"), Child = LumenUi.Icon("server_20_regular", 19)
            });
            var labels = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            var name = LumenUi.Text(server.Name, 15);
            name.FontWeight = FontWeights.SemiBold;
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(name);
            var address = Uri.TryCreate(server.ApiRoot, UriKind.Absolute, out var uri)
                ? uri.Authority + (uri.AbsolutePath.Equals("/emby/", StringComparison.OrdinalIgnoreCase) ? string.Empty : uri.AbsolutePath.TrimEnd('/'))
                : server.ApiRoot;
            var detail = LumenUi.Text(server.Connection?.Server.Version is { } version ? $"{address} · {version}" : address, 12);
            detail.Foreground = LumenTheme.Brush("Muted");
            detail.TextWrapping = TextWrapping.NoWrap;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            labels.Children.Add(detail);
            Grid.SetColumn(labels, 1);
            content.Children.Add(labels);
            var latency = LumenUi.Text(server.Connection is { } connection ? $"{connection.ResponseMilliseconds} ms" : string.Empty, 12);
            latency.Foreground = LumenTheme.Brush("Muted");
            latency.TextWrapping = TextWrapping.NoWrap;
            latency.VerticalAlignment = VerticalAlignment.Center;
            latency.Visibility = server.Connection is null ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(latency, 2);
            content.Children.Add(latency);
            var check = LumenUi.Icon("check", 16);
            check.Opacity = selected ? 1 : 0;
            Grid.SetColumn(check, 3);
            content.Children.Add(check);
            var button = LumenUi.Button(server.Name, height: 68);
            button.Content = content;
            button.CornerRadius = new CornerRadius(14);
            button.Padding = new Thickness(14, 0, 16, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.Background = selected ? LumenTheme.Brush("Selected") : new SolidColorBrush(Colors.Transparent);
            button.BorderBrush = LumenTheme.Brush(selected ? "Accent" : "Line");
            button.BorderThickness = new Thickness(selected ? 1.5 : 1);
            foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
                LumenUi.SetResource(button.Resources, "ButtonBorderBrush" + state, button.BorderBrush);
            LumenUi.SetResource(button.Resources, "ButtonBackground", button.Background);
            LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", LumenTheme.Brush("Hover"));
            LumenUi.SetResource(button.Resources, "ButtonBackgroundPressed", LumenTheme.Brush("Hover"));
            LumenUi.SetResource(button.Resources, "ButtonBackgroundDisabled", button.Background);
            button.IsEnabled = _settingsReady && _connectionRequest is null && !_transitioning;
            AutomationProperties.SetName(button, server.Name + ", " + address + ", " + SignInServerOrigin(server));
            AutomationProperties.SetItemStatus(button, LumenText.Get(selected ? "Selected" : "Not selected"));
            ToolTipService.SetToolTip(button, server.ApiRoot + Environment.NewLine + SignInServerOrigin(server));
            button.Click += async (_, _) => await ProbeSignInServerAsync(server.ApiRoot);
            _serverRowControls.Add((button, false));
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(button);
            var savedAction = CreateSavedServerAction(server);
            Grid.SetColumn(savedAction, 1);
            row.Children.Add(savedAction);
            _serverRows.Children.Add(row);
        }
    }

    private async Task ProbeSignInServerAsync(string address)
    {
        if (!_settingsReady || _connectionRequest is not null || _transitioning || _closing) return;
        using var request = new CancellationTokenSource();
        _connectionRequest = request;
        var requestedServer = _signInServers.FirstOrDefault(server => SameSignInAddress(server.ApiRoot, address));
        if (requestedServer is not null && !ReferenceEquals(requestedServer, _selectedSignInServer))
        {
            _selectedSignInServer = requestedServer;
            _selectedSignInUser = null;
            _manualSignInUser = true;
            _address.Text = requestedServer.ApiRoot;
            _userName.Text = _connections.Settings.Accounts.FirstOrDefault(account => SameSignInAddress(account.ApiRoot, requestedServer.ApiRoot))?.UserName ?? string.Empty;
            RenderSignInServers();
            RenderSignInUsers();
        }
        _serverError.Visibility = Visibility.Collapsed;
        _credentialError.Text = string.Empty;
        _password.Password = string.Empty;
        SetConnecting(true, LumenText.Get("Connecting to server..."));
        var succeeded = false;
        var failed = false;
        try
        {
            var result = await _connections.ProbeServerAsync(address, request.Token);
            if (_closing || _transitioning || request.IsCancellationRequested) return;
            var known = _signInServers.FirstOrDefault(server => SameSignInAddress(server.ApiRoot, result.Api.ApiRoot.AbsoluteUri));
            ConnectionService.RequireExpectedServerIdentity(result.Server, requestedServer?.ServerId);
            if (!ReferenceEquals(known, requestedServer)) ConnectionService.RequireExpectedServerIdentity(result.Server, known?.ServerId);
            var displayName = string.IsNullOrWhiteSpace(result.Server.ServerName) ? result.Api.ApiRoot.Host : result.Server.ServerName.Trim();
            if (known is null)
            {
                known = new SignInServer(result.Api.ApiRoot.AbsoluteUri, result.Server.Id!, displayName);
                _signInServers.Add(known);
            }
            known.Connection = result;
            known.SeenDuringSession = true;
            known.Name = displayName;
            _selectedSignInServer = known;
            _address.Text = result.Api.ApiRoot.AbsoluteUri;
            _signInAvatarBytes.Clear();
            _selectedSignInUser = result.Users.FirstOrDefault(user => user.Name == _userName.Text) ?? result.Users.FirstOrDefault();
            _manualSignInUser = _selectedSignInUser is null;
            _userName.Text = _selectedSignInUser?.Name ?? string.Empty;
            RenderSignInServers();
            RenderSignInUsers();
            succeeded = true;
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (_closing) return;
            _serverError.Text = exception is ArgumentException ? LumenText.Get("Enter a complete HTTP or HTTPS server address.") : LumenText.Get(UiErrors.Describe(exception));
            _serverError.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(_address, _serverError.Text);
            failed = true;
        }
        finally
        {
            if (ReferenceEquals(_connectionRequest, request)) _connectionRequest = null;
            SetConnecting(false);
            if (!_closing && !_transitioning)
            {
                if (failed) _address.Focus(FocusState.Programmatic);
                else if (succeeded && _manualSignInUser) _userName.Focus(FocusState.Programmatic);
                else if (succeeded && !RequiresSignInPassword(_selectedSignInUser)) _signIn.Focus(FocusState.Programmatic);
                else if (succeeded) _password.Focus(FocusState.Programmatic);
            }
        }
    }

    private void RenderSignInUsers()
    {
        if (_userName is null || _updatingSignIn) return;
        _updatingSignIn = true;
        try
        {
            CancelSignInArtwork();
            _signInImages = new CancellationTokenSource();
            _userTileGrid.Children.Clear();
            var server = _selectedSignInServer;
            var users = server?.Connection?.Users ?? _connections.Settings.Accounts
                .Where(account => server is not null && SameSignInAddress(account.ApiRoot, server.ApiRoot))
                .Select(account => new UserDto { Id = account.UserId, Name = account.UserName }).DistinctBy(user => user.Id).ToArray();
            if (_selectedSignInUser is null && users.Count > 0 && users.FirstOrDefault(user => user.Name == _userName.Text) is { } savedUser)
            {
                _selectedSignInUser = savedUser;
                _manualSignInUser = false;
            }
            _viewerServer.Text = server is null ? string.Empty : LumenText.Get("{0} · {1} users", server.Name, users.Count);
            _publicUserHint.Text = server?.Connection is { UserListUnavailable: true }
                ? LumenText.Get("Public accounts could not be loaded. You can still enter a username.")
                : server?.Connection is not null && users.Count == 0
                    ? LumenText.Get("This server hides its accounts. Enter your username below.")
                    : server?.Connection is null ? LumenText.Get("Select a server to load its public accounts.") : string.Empty;
            _publicUserHint.Visibility = _publicUserHint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var user in users) AddSignInUserTile(user, server?.Connection?.Api, _signInImages.Token);
            AddSignInUserTile(null, null, _signInImages.Token);
            _manualUserField.Visibility = _manualSignInUser ? Visibility.Visible : Visibility.Collapsed;
            _userName.Visibility = _manualSignInUser ? Visibility.Visible : Visibility.Collapsed;
            var passwordless = !_manualSignInUser && !RequiresSignInPassword(_selectedSignInUser);
            UpdateSignInCredentialPresentation(passwordless);
            _passwordCaption.Text = _manualSignInUser || _selectedSignInUser?.Name is null
                ? LumenText.Get("Password") : LumenText.Get("Password for {0}", _selectedSignInUser.Name);
            SelectMatchingSavedAccount();
            PositionSignInUsers();
        }
        finally { _updatingSignIn = false; }
    }

    private void UpdateSignInCredentialPresentation(bool passwordless)
    {
        _password.Visibility = passwordless ? Visibility.Collapsed : Visibility.Visible;
        _passwordFocusRing.Visibility = passwordless ? Visibility.Collapsed : Visibility.Visible;
        _passwordCaption.Visibility = passwordless ? Visibility.Collapsed : Visibility.Visible;
        _passwordlessHint.Visibility = passwordless ? Visibility.Visible : Visibility.Collapsed;
        _signInCredentialRow.MinHeight = passwordless ? 96 : 48;
        _signInCredentialRow.Margin = new Thickness(0, passwordless ? 0 : 10, 0, 0);
        _signInRememberRow.Visibility = passwordless ? Visibility.Collapsed : Visibility.Visible;
        UpdateSignInCredentialErrorPresentation();
        var action = LumenText.Get(passwordless ? "Enter" : "Sign in");
        _signIn.Content = new TextBlock
        {
            Text = action, FontFamily = LumenTheme.SansFont, FontSize = 15,
            Foreground = LumenTheme.Brush("Background"), FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap
        };
        AutomationProperties.SetName(_signIn, action);
    }

    private void UpdateSignInCredentialErrorPresentation()
    {
        var passwordless = _passwordlessHint.Visibility == Visibility.Visible;
        var showError = !passwordless || !string.IsNullOrWhiteSpace(_credentialError.Text);
        _credentialError.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        _credentialError.MinHeight = passwordless ? 0 : 16;
        _credentialError.Margin = new Thickness(0, showError ? 8 : 0, 0, 0);
    }

    private bool RequiresSignInPassword(UserDto? user) => user?.HasPassword != false
        || SignInUserKey(user) is { } key && _signInPasswordPrompts.Contains(key);

    private (string ServerId, string UserId)? SignInUserKey(UserDto? user) =>
        _selectedSignInServer?.ServerId is { Length: > 0 } serverId && user?.Id is { Length: > 0 } userId
            ? (serverId, userId) : null;

    private void RequireSelectedSignInUserPassword()
    {
        if (SignInUserKey(_selectedSignInUser) is { } key)
        {
            _signInPasswordPrompts.Add(key);
            RenderSignInUsers();
        }
        else UpdateSignInCredentialPresentation(passwordless: false);
    }

    private void ClearSignInPasswordPrompts() => _signInPasswordPrompts.Clear();

    private void AddSignInUserTile(UserDto? user, EmbyApiClient? api, CancellationToken token)
    {
        var selected = user is null ? _manualSignInUser : !_manualSignInUser && user.Id == _selectedSignInUser?.Id;
        var avatarBody = new Grid { Width = 76, Height = 76 };
        var avatar = new Ellipse
        {
            Width = 76, Height = 76, Fill = user is null ? new SolidColorBrush(Colors.Transparent) : SignInAvatarColor(user.Name),
            Stroke = user is null ? selected ? new SolidColorBrush(Colors.Transparent) : LumenTheme.Brush("LineStrong")
                : new SolidColorBrush(Color.FromArgb(31, 255, 255, 255)), StrokeThickness = user is null ? 1.5 : 1
        };
        if (user is null) avatar.StrokeDashArray = new DoubleCollection { 3, 3 };
        avatarBody.Children.Add(avatar);
        var initial = user is null ? LumenUi.Icon("person_20_regular", 24) : LumenUi.Text(SignInInitial(user.Name), 26);
        if (initial is TextBlock text) { text.FontWeight = FontWeights.SemiBold; text.Foreground = LumenTheme.Brush("ImageInk"); }
        initial.HorizontalAlignment = HorizontalAlignment.Center;
        initial.VerticalAlignment = VerticalAlignment.Center;
        if (user is null) initial.Opacity = .7;
        avatarBody.Children.Add(initial);
        var ring = new Border
        {
            Width = 86, Height = 86, Padding = new Thickness(3), BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(43), Child = avatarBody,
            Margin = new Thickness(-5), Background = selected ? LumenTheme.Brush("Background") : new SolidColorBrush(Colors.Transparent),
            BorderBrush = selected ? LumenTheme.Brush("Accent") : new SolidColorBrush(Colors.Transparent)
        };
        var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var avatarSlot = new Grid { Width = 76, Height = 76 };
        avatarSlot.Children.Add(ring);
        column.Children.Add(avatarSlot);
        var name = LumenUi.Text(user?.Name ?? LumenText.Get("Other user"), 13);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.MaxWidth = 88;
        name.FontWeight = selected && user is not null ? FontWeights.SemiBold : FontWeights.Normal;
        name.Foreground = LumenTheme.Brush(selected && user is not null ? "Ink" : "Sub");
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.Margin = new Thickness(0, 10, 0, 0);
        column.Children.Add(name);
        var hint = LumenUi.Text(user is not null && !RequiresSignInPassword(user) ? LumenText.Get("No password") : string.Empty, 11);
        hint.Foreground = LumenTheme.Brush("Muted");
        hint.MinHeight = 14;
        hint.Margin = new Thickness(0, 2, 0, 0);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        column.Children.Add(hint);
        var button = new Button
        {
            Width = 88, MinWidth = 88, MinHeight = 120, Padding = new Thickness(0),
            Content = column, BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(12), Foreground = LumenTheme.Brush("Ink"),
            FontFamily = LumenTheme.SansFont, HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top, UseSystemFocusVisuals = true,
            FocusVisualPrimaryBrush = LumenTheme.Brush("FocusOuter"), FocusVisualSecondaryBrush = LumenTheme.Brush("FocusInner")
        };
        AutomationProperties.SetName(button, user?.Name ?? LumenText.Get("Other user"));
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            button.Resources["ButtonBackground" + state] = new SolidColorBrush(Colors.Transparent);
            button.Resources["ButtonBorderBrush" + state] = new SolidColorBrush(Colors.Transparent);
        }
        button.IsEnabled = _settingsReady && _connectionRequest is null && !_transitioning;
        AutomationProperties.SetItemStatus(button, LumenText.Get(selected ? "Selected" : "Not selected"));
        button.Click += (_, _) =>
        {
            _selectedSignInUser = user;
            _manualSignInUser = user is null;
            _userName.Text = user?.Name ?? string.Empty;
            _password.Password = string.Empty;
            RenderSignInUsers();
            if (_manualSignInUser) _userName.Focus(FocusState.Programmatic);
            else if (!RequiresSignInPassword(user)) _signIn.Focus(FocusState.Programmatic);
            else _password.Focus(FocusState.Programmatic);
        };
        _userTileGrid.Children.Add(button);
        if (user?.PrimaryImageTag is not null && user.Id is not null && api is not null)
            _ = LoadSignInAvatarAsync(api, user, avatar, initial, token);
    }

    private async Task LoadSignInAvatarAsync(EmbyApiClient api, UserDto user, Ellipse avatar, FrameworkElement initial, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _signInAvatarGate.WaitAsync(token);
            entered = true;
            var key = user.Id + ":" + user.PrimaryImageTag;
            if (!_signInAvatarBytes.TryGetValue(key, out var bytes))
            {
                bytes = await api.GetUserImageAsync(user.Id!, options: new ImageOptions { MaxWidth = 152, MaxHeight = 152, Tag = user.PrimaryImageTag }, cancellationToken: token);
                token.ThrowIfCancellationRequested();
                _signInAvatarBytes[key] = bytes;
            }
            await NativePosterDecoder.DecodeAndApplyAsync(bytes, 152, 152, token, bitmap =>
            {
                if (token.IsCancellationRequested || _closing) return;
                avatar.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
                initial.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is EmbyApiException or EmbyTransportException or EmbyProtocolException or TimeoutException
            or System.Runtime.InteropServices.COMException or ArgumentException or HttpRequestException)
        {
            // Missing or unsupported public avatars keep the account's initial.
        }
        finally { if (entered) _signInAvatarGate.Release(); }
    }

    private void CancelSignInArtwork()
    {
        _signInImages?.Cancel();
        _signInImages?.Dispose();
        _signInImages = null;
    }

    private void PositionSignInUsers()
    {
        if (_userTileGrid.ActualWidth <= 0) return;
        var columns = Math.Max(1, (int)((_userTileGrid.ActualWidth + 22) / 110));
        if (_userTileGrid.ColumnDefinitions.Count != columns)
        {
            _userTileGrid.ColumnDefinitions.Clear();
            for (var index = 0; index < columns; index++) _userTileGrid.ColumnDefinitions.Add(new() { Width = new GridLength(88) });
        }
        var rows = (_userTileGrid.Children.Count + columns - 1) / columns;
        if (_userTileGrid.RowDefinitions.Count != rows)
        {
            _userTileGrid.RowDefinitions.Clear();
            for (var index = 0; index < rows; index++) _userTileGrid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        for (var index = 0; index < _userTileGrid.Children.Count; index++)
        {
            Grid.SetColumn((FrameworkElement)_userTileGrid.Children[index], index % columns);
            Grid.SetRow((FrameworkElement)_userTileGrid.Children[index], index / columns);
        }
    }

    private void SelectMatchingSavedAccount()
    {
        var match = _savedAccounts.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is SavedAccount account
            && SameSignInAddress(account.ApiRoot, _address.Text) && account.UserName == _userName.Text);
        _savedAccounts.SelectedItem = match;
        UpdateSavedSignInAction();
    }

    private void UpdateSavedSignInAction()
    {
        if (_restore is null || _savedPanel is null) return;
        var account = (_savedAccounts.SelectedItem as ComboBoxItem)?.Tag as SavedAccount;
        var available = account is { ProtectedToken.Length: > 0 } && _connections.GetAccountRestriction(account.Key) is null
            && SameSignInAddress(account.ApiRoot, _address.Text) && account.UserName == _userName.Text;
        _savedPanel.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _restore.IsEnabled = available && _settingsReady && _connectionRequest is null && !_transitioning && !_closing;
    }

    private void SetSignInControlsEnabled(bool enabled)
    {
        _probeServer.IsEnabled = enabled;
        var serverSelected = _selectedSignInServer is { } server && (server.Connection is not null || server.HasSavedAccount)
            && SameSignInAddress(server.ApiRoot, _address.Text);
        _signIn.IsEnabled = enabled && serverSelected;
        _userName.IsEnabled = enabled && serverSelected;
        _password.IsEnabled = enabled && serverSelected;
        _rememberToggle.IsEnabled = enabled && !_connections.IsTemporarySession;
        SetServerDiscoveryControlsEnabled(enabled);
        foreach (var button in _userTileGrid.Children.OfType<Button>()) button.IsEnabled = enabled && serverSelected;
        UpdateSavedSignInAction();
    }

    private static bool SameSignInAddress(string first, string second)
    {
        if (!Uri.TryCreate(first, UriKind.Absolute, out var a) || !Uri.TryCreate(second, UriKind.Absolute, out var b)) return false;
        return a.Scheme == b.Scheme && a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port
            && a.AbsolutePath.TrimEnd('/').Equals(b.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
    }

    private static string SignInInitial(string? name) => string.IsNullOrWhiteSpace(name) ? "?" : StringInfo.GetNextTextElement(name.Trim());

    private static SolidColorBrush SignInAvatarColor(string? name)
    {
        var index = (name ?? string.Empty).Aggregate(0, (value, character) => (value + character) % 4);
        return new SolidColorBrush(index switch
        {
            0 => Color.FromArgb(255, 114, 97, 76), 1 => Color.FromArgb(255, 99, 112, 107),
            2 => Color.FromArgb(255, 106, 110, 137), _ => Color.FromArgb(255, 140, 105, 91)
        });
    }

    private sealed class SignInServer(string apiRoot, string serverId, string name)
    {
        public string ApiRoot { get; } = apiRoot;
        public string ServerId { get; } = serverId;
        public string Name { get; set; } = name;
        public PublicServerConnection? Connection { get; set; }
        public bool IsSaved { get; set; }
        public bool IsDiscovered { get; set; }
        public bool HasSavedAccount { get; set; }
        public bool SeenDuringSession { get; set; }
    }

    private static void RegisterSignInText() => LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Welcome to LUMEN"] = "\u6b22\u8fce\u4f7f\u7528 LUMEN",
        ["Connect to your Emby server"] = "\u8fde\u63a5\u5230\u4f60\u7684 Emby \u670d\u52a1\u5668",
        ["Save your server and account on this PC for a quicker return."] = "\u670d\u52a1\u5668\u548c\u8d26\u6237\u4f1a\u4fdd\u5b58\u5728\u8fd9\u53f0\u7535\u8111\u4e0a\uff0c\u4e0b\u6b21\u6253\u5f00\u76f4\u63a5\u8fdb\u5165\u3002",
        ["Or enter a server address"] = "\u6216\u624b\u52a8\u8f93\u5165\u5730\u5740",
        ["Connect"] = "\u8fde\u63a5",
        ["Use HTTP or HTTPS. Without a port, Lumen also tries Emby ports 8096 and 8920."] = "\u652f\u6301 http / https\uff1b\u672a\u586b\u5199\u7aef\u53e3\u65f6\u4e5f\u4f1a\u5c1d\u8bd5 Emby \u9ed8\u8ba4\u7aef\u53e3 8096 \u4e0e 8920\u3002",
        ["Who is watching?"] = "\u8c01\u5728\u89c2\u770b\uff1f",
        ["Connect to a server to see its public accounts."] = "\u8fde\u63a5\u670d\u52a1\u5668\u540e\u9009\u62e9\u516c\u5f00\u8d26\u6237\uff0c\u4e5f\u53ef\u4ee5\u624b\u52a8\u8f93\u5165\u7528\u6237\u540d\u3002",
        ["Enter your username"] = "\u8f93\u5165\u7528\u6237\u540d",
        ["Enter password"] = "\u8f93\u5165\u5bc6\u7801",
        ["This account does not require a password."] = "\u6b64\u8d26\u6237\u65e0\u9700\u5bc6\u7801",
        ["Continue with saved session"] = "\u4f7f\u7528\u5df2\u4fdd\u5b58\u7684\u51ed\u636e\u7ee7\u7eed",
        ["Remember me on this PC"] = "\u5728\u8fd9\u53f0\u7535\u8111\u4e0a\u8bb0\u4f4f\u6211",
        ["Credentials stay on this PC, encrypted with Windows DPAPI."] = "\u51ed\u636e\u7ecf Windows DPAPI \u52a0\u5bc6\uff0c\u53ea\u4fdd\u5b58\u5728\u672c\u673a",
        ["{0} servers"] = "{0} \u53f0\u670d\u52a1\u5668",
        ["Add your first Emby server below."] = "\u5728\u4e0b\u65b9\u6dfb\u52a0\u4f60\u7684\u7b2c\u4e00\u53f0 Emby \u670d\u52a1\u5668\u3002",
        ["Connecting to server..."] = "\u6b63\u5728\u8fde\u63a5\u670d\u52a1\u5668\u2026",
        ["{0} \u00b7 {1} users"] = "{0} \u00b7 {1} \u4f4d\u7528\u6237",
        ["Public accounts could not be loaded. You can still enter a username."] = "\u65e0\u6cd5\u52a0\u8f7d\u516c\u5f00\u8d26\u6237\uff0c\u8bf7\u5728\u4e0b\u65b9\u8f93\u5165\u7528\u6237\u540d\u3002",
        ["This server hides its accounts. Enter your username below."] = "\u6b64\u670d\u52a1\u5668\u9690\u85cf\u4e86\u8d26\u6237\uff0c\u8bf7\u5728\u4e0b\u65b9\u8f93\u5165\u7528\u6237\u540d\u3002",
        ["Select a server to load its public accounts."] = "\u9009\u62e9\u670d\u52a1\u5668\u4ee5\u52a0\u8f7d\u516c\u5f00\u8d26\u6237\u3002",
        ["Password for {0}"] = "{0} \u7684\u5bc6\u7801",
        ["Enter"] = "\u8fdb\u5165",
        ["Other user"] = "\u5176\u4ed6\u7528\u6237",
        ["No password"] = "\u65e0\u9700\u5bc6\u7801",
    });
}
