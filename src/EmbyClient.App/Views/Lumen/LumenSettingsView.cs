using System.Security;
using EmbyClient.Api;
using EmbyClient.App.Playback;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using Path = System.IO.Path;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenSettingsView : UserControl
{
    private static readonly string[] TabKeys = ["Playback", "Subtitles", "Appearance", "System", "Account", "About"];
    private readonly StackPanel _column = new() { Width = 880, Margin = new Thickness(0, 120, 0, 80) };
    private readonly StackPanel _groups = new() { Spacing = 40, Margin = new Thickness(0, 40, 0, 0) };
    private readonly List<Action> _refreshControls = [];
    private readonly List<SettingRowLayout> _rows = [];
    private readonly Button[] _tabButtons = new Button[6];
    private readonly Grid _tabTrack;
    private readonly TranslateTransform _tabOffset = new();
    private readonly LinearGradientBrush _ambientBrush = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    private readonly LinearGradientBrush _ambientArtworkFade = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    private readonly LumenAmbientArtwork _ambientArtwork = new();
    private readonly Grid _ambientArtworkLayer = new() { Opacity = 0, IsHitTestVisible = false };
    private readonly Border _ambientFallback = new() { IsHitTestVisible = false };
    private readonly Button _signOutButton;
    private readonly Border _tabIndicator;
    private readonly Grid _header;
    private readonly StackPanel _headerActions;
    private readonly TextBlock _accountInitial;
    private readonly TextBlock _accountName;
    private readonly TextBlock _accountServer;
    private readonly Border _accountBadge;
    private readonly ScrollViewer _tabScroller;
    private int _savedAccountCount;
    private ConnectedSession? _session;
    private LumenPreferences _preferences = new();
    private Flyout? _openFlyout;
    private ContentDialog? _signOutDialog;
    private ContentDialog? _installUpdateDialog;
    private CancellationTokenSource? _updateCheck;
    private readonly ReleaseUpdateService _releaseUpdates = new();
    private ReleaseUpdateInfo? _availableUpdate;
    private StagedReleaseUpdate? _stagedUpdate;
    private ReleaseUpdateProgress? _updateProgress;
    private string _updateOperation = "";
    private StartupRegistrationService _startupRegistration = new();
    private StartupRegistrationStatus _startupStatus = new(false, false);
    private bool _startupChanging;
    private HdrDisplayEligibilitySnapshot? _hdrDisplayEligibility;
    private ContentRefreshRateStatus? _refreshRateStatus;
    private bool _refreshRateAvailable;
    private ImageCacheUsage _imageCacheUsage = new(0, 0, 32L * 1024 * 1024);
    private int _tab;
    private double _tabWidth = 72;
    private bool _synchronizing;
    private bool _subtitleStyleAvailable;
    private bool _licensesOpen;
    private bool _isHeaderScrolled;
    private MediaCardViewModel? _ambientItem;
    private LibraryViewModel? _ambientLibrary;
    private bool _ambientArtworkActive;
    private string _updateStatus = "Development build";
    private object[] _updateArguments = [];

    public LumenSettingsView()
    {
        RegisterSettingsText();
        Background = LumenTheme.Brush("Background");
        var root = new Grid();
        var ambience = new Grid { Height = 520, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        _ambientFallback.Background = _ambientBrush;
        ambience.Children.Add(_ambientFallback);
        _ambientArtworkLayer.Children.Add(_ambientArtwork);
        _ambientArtworkLayer.Children.Add(new Border { Background = _ambientArtworkFade, IsHitTestVisible = false });
        ambience.Children.Add(_ambientArtworkLayer);
        _ambientArtwork.ArtworkChanged += (_, _) => UpdateAmbientArtworkVisibility();
        root.Children.Add(ambience);
        var scroll = new ScrollViewer
        {
            Content = _column, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        scroll.ViewChanged += (_, _) =>
        {
            var scrolled = scroll.VerticalOffset > 8;
            if (_isHeaderScrolled == scrolled) return;
            _isHeaderScrolled = scrolled;
            HeaderScrollChanged?.Invoke(this, EventArgs.Empty);
        };
        root.Children.Add(scroll);
        Content = root;

        var header = _header = new Grid { MinHeight = 44, ColumnSpacing = 16, RowSpacing = 16 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = LumenUi.Text(T("Settings"), 44, serif: true);
        title.FontWeight = FontWeights.Black;
        title.LineHeight = 44;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        header.Children.Add(title);
        _headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Bottom
        };
        _accountInitial = LumenUi.Text(string.Empty, 12);
        _accountInitial.FontWeight = FontWeights.Bold;
        _accountInitial.Foreground = LumenTheme.Brush("ImageInk");
        _accountInitial.HorizontalAlignment = HorizontalAlignment.Center;
        _accountInitial.VerticalAlignment = VerticalAlignment.Center;
        _accountName = LumenUi.Text(string.Empty, 13);
        _accountName.FontWeight = FontWeights.SemiBold;
        _accountName.VerticalAlignment = VerticalAlignment.Center;
        _accountName.TextWrapping = TextWrapping.NoWrap;
        _accountName.TextTrimming = TextTrimming.CharacterEllipsis;
        _accountName.MaxWidth = 140;
        _accountServer = LumenUi.Text(string.Empty, 13);
        _accountServer.Foreground = LumenTheme.Brush("Muted");
        _accountServer.VerticalAlignment = VerticalAlignment.Center;
        _accountServer.TextWrapping = TextWrapping.NoWrap;
        _accountServer.TextTrimming = TextTrimming.CharacterEllipsis;
        _accountServer.MaxWidth = 140;
        var accountLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        accountLine.Children.Add(new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(255, 99, 105, 84)), Child = _accountInitial
        });
        accountLine.Children.Add(_accountName);
        accountLine.Children.Add(_accountServer);
        _accountBadge = new Border
        {
            Height = 38, Padding = new Thickness(6, 5, 14, 5), CornerRadius = new CornerRadius(19),
            Background = LumenTheme.Brush("Glass"), BorderBrush = LumenTheme.Brush("LineStrong"),
            BorderThickness = new Thickness(1), Child = accountLine, Visibility = Visibility.Collapsed
        };
        _headerActions.Children.Add(_accountBadge);
        _signOutButton = LumenUi.Button(T("Sign out"), "log-out", height: 38);
        _signOutButton.MinWidth = 112;
        _signOutButton.Padding = new Thickness(14, 0, 18, 0);
        if (_signOutButton.Content is StackPanel signOutContent)
        {
            if (signOutContent.Children[0] is FrameworkElement icon) { icon.Width = 15; icon.Height = 15; }
            if (signOutContent.Children[1] is TextBlock label) label.FontSize = 13;
        }
        _signOutButton.VerticalAlignment = VerticalAlignment.Center;
        _signOutButton.Background = LumenTheme.Brush("Glass");
        // Override the helper resource while this new control is still detached.
        LumenUi.SetResource(_signOutButton.Resources, "ButtonBorderBrushPointerOver", LumenTheme.Brush("Danger"));
        _signOutButton.Click += async (_, _) => await ShowSignOutAsync();
        _headerActions.Children.Add(_signOutButton);
        Grid.SetColumn(_headerActions, 1);
        header.Children.Add(_headerActions);
        _column.Children.Add(header);

        _tabTrack = new Grid { Width = 442, Height = 34 };
        _tabIndicator = new Border
        {
            Width = 72, Height = 34, Background = LumenTheme.Brush("Ink"),
            CornerRadius = new CornerRadius(17), HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _tabOffset, IsHitTestVisible = false
        };
        _tabTrack.Children.Add(_tabIndicator);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var index = 0; index < TabKeys.Length; index++)
        {
            var current = index;
            var button = FlatButton(T(TabKeys[index]), 34);
            button.Width = 72;
            if (button.Content is TextBlock tabLabel) tabLabel.FontSize = 14;
            button.Click += (_, _) => SelectTab(current);
            _tabButtons[index] = button;
            tabs.Children.Add(button);
        }
        _tabTrack.Children.Add(tabs);
        _tabScroller = new ScrollViewer
        {
            Margin = new Thickness(0, 28, 0, 0), HorizontalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Left,
            Content = new Border
            {
                Child = _tabTrack, Padding = new Thickness(4), Height = 44,
                CornerRadius = new CornerRadius(22), Background = LumenTheme.Brush("Glass"),
                BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1)
            }
        };
        _column.Children.Add(_tabScroller);
        _column.Children.Add(_groups);
        UpdateTabButtons();
        BuildGroups();
        UpdateAmbient();
        SizeChanged += (_, _) => UpdateLayoutWidth();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible)
            {
                _openFlyout?.Hide();
                _signOutDialog?.Hide();
                _installUpdateDialog?.Hide();
            }
            UpdateAmbientArtwork();
        });
        Loaded += (_, _) =>
        {
            LumenTheme.Changed -= OnThemeChanged;
            LumenTheme.Changed += OnThemeChanged;
            UpdateLayoutWidth();
            UpdateAmbient();
            UpdateAmbientArtwork();
        };
        Unloaded += (_, _) =>
        {
            LumenTheme.Changed -= OnThemeChanged;
            _openFlyout?.Hide();
            _signOutDialog?.Hide();
            _installUpdateDialog?.Hide();
            _updateCheck?.Cancel();
            _ambientArtworkActive = false;
            _ambientArtwork.Clear();
            UpdateAmbientArtworkVisibility();
        };
    }

    public event EventHandler<LumenPreferences>? PreferencesChanged;
    public event EventHandler? SignOutRequested;
    public event EventHandler? SwitchAccountRequested;
    public event EventHandler? DiagnosticsRequested;
    public event EventHandler? KeyboardShortcutsRequested;
    public event EventHandler? ClearImageCacheRequested;
    public event EventHandler? ShutdownRequested;
    public event EventHandler? HeaderScrollChanged;
    public bool IsHeaderScrolled => _isHeaderScrolled;

    public void SetSession(ConnectedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_session?.AccountKey != session.AccountKey) SetAmbientArtwork(null, null);
        _session = session;
        _accountName.Text = session.User.Name ?? T("Current user");
        _accountServer.Text = session.Server.ServerName ?? session.Api.ApiRoot.Host;
        ToolTipService.SetToolTip(_accountName, _accountName.Text);
        ToolTipService.SetToolTip(_accountServer, _accountServer.Text);
        _accountInitial.Text = string.IsNullOrWhiteSpace(session.User.Name) ? "L" : System.Globalization.StringInfo.GetNextTextElement(session.User.Name);
        _accountBadge.Visibility = Visibility.Visible;
        if (_tab is 4 or 5) BuildGroups();
        UpdateLayoutWidth();
    }

    public void SetSavedAccounts(IEnumerable<SavedAccount> accounts)
    {
        var count = accounts.Count(account => account.ProtectedToken.Length > 0);
        if (_savedAccountCount == count) return;
        _savedAccountCount = count;
        if (_tab == 4) BuildGroups();
    }

    public void SetAmbientArtwork(MediaCardViewModel? item, LibraryViewModel? library)
    {
        if (!ReferenceEquals(item, _ambientItem) || !ReferenceEquals(library, _ambientLibrary))
        {
            _ambientArtworkActive = false;
            _ambientArtwork.Clear();
            _ambientItem = item;
            _ambientLibrary = library;
        }
        UpdateAmbientArtwork();
    }

    private void UpdateAmbientArtwork()
    {
        var available = IsLoaded && Visibility == Visibility.Visible && _ambientItem is not null && _ambientLibrary is not null
            && ImageCache.SelectImage(_ambientItem.Item, ArtworkKind.Poster) is not null;
        if (!available)
        {
            _ambientArtworkActive = false;
            _ambientArtwork.Clear();
        }
        else if (!_ambientArtworkActive)
        {
            _ambientArtworkActive = true;
            _ambientArtwork.Set(_ambientItem!, _ambientLibrary!);
        }
        UpdateAmbientArtworkVisibility();
    }

    private void UpdateAmbientArtworkVisibility()
    {
        var ready = _ambientArtworkActive && _ambientArtwork.HasArtwork;
        _ambientArtworkLayer.Opacity = ready ? 1 : 0;
        _ambientFallback.Opacity = ready ? 0 : 1;
    }

    public void SetPreferences(LumenPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences with { };
        RefreshControls();
        UpdateAmbient();
    }

    public void SetImageCacheUsage(ImageCacheUsage usage)
    {
        if (_imageCacheUsage == usage) return;
        _imageCacheUsage = usage;
        if (_tab == 3) RefreshControls();
    }

    public void SetRefreshRateStatus(ContentRefreshRateStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (_refreshRateStatus == status) return;
        _refreshRateStatus = status;
        _refreshRateAvailable = status.Reason != ContentRefreshRateReason.PlatformUnavailable
            && status.State != ContentRefreshRateState.Closed;
        if (_tab == 3) RefreshControls();
    }

    public void SetHdrDisplayEligibility(HdrDisplayEligibilitySnapshot? eligibility)
    {
        if (_hdrDisplayEligibility == eligibility) return;
        _hdrDisplayEligibility = eligibility;
        if (_tab == 3) RefreshControls();
    }

    public void SetStartupRegistrationService(StartupRegistrationService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (_startupChanging) throw new InvalidOperationException("Startup registration is being changed.");
        _startupRegistration = service;
        if (_tab == 3) BuildGroups();
    }

    public void SetSubtitleStyleAvailable(bool available)
    {
        if (_subtitleStyleAvailable == available) return;
        _subtitleStyleAvailable = available;
        if (_tab == 1) BuildGroups();
    }

    private static string T(string key, params object[] arguments) => LumenText.Get(key, arguments);

    private void OnThemeChanged(object? sender, EventArgs args)
    {
        UpdateAmbient();
        UpdateTabButtons();
        RefreshControls();
    }

    private void SelectTab(int tab)
    {
        if (_tab == tab) return;
        _openFlyout?.Hide();
        _tab = tab;
        Animate(_tabOffset, "X", tab * (_tabWidth + 2), 550, spring: true);
        UpdateTabButtons();
        BuildGroups();
        _groups.Opacity = 0;
        Animate(_groups, "Opacity", 1, 220);
    }

    private void UpdateTabButtons()
    {
        for (var index = 0; index < _tabButtons.Length; index++)
        {
            var selected = index == _tab;
            if (_tabButtons[index].Content is TextBlock label)
            {
                label.Foreground = LumenTheme.Brush(selected ? "Background" : "Sub");
                label.FontWeight = selected ? FontWeights.Bold : FontWeights.Medium;
            }
            AutomationProperties.SetItemStatus(_tabButtons[index], T(selected ? "Selected" : "Not selected"));
        }
    }

    private void BuildGroups()
    {
        _openFlyout?.Hide();
        _groups.Children.Clear();
        _rows.Clear();
        _refreshControls.Clear();
        switch (_tab)
        {
            case 0: BuildPlayback(); break;
            case 1: BuildSubtitles(); break;
            case 2: BuildAppearance(); break;
            case 3: BuildSystem(); break;
            case 4: BuildAccount(); break;
            case 5: BuildAbout(); break;
        }
        RefreshControls();
        UpdateRowLayout();
    }

    private void BuildPlayback()
    {
        var behavior = Group("Playback behavior");
        AddRow(behavior, "Auto-play next episode", "Start the next episode when the current one ends",
            Switch("Auto-play next episode", () => _preferences.AutoPlayNext, value => Change(p => p with { AutoPlayNext = value })));
        AddRow(behavior, "Next episode countdown", "Start the countdown when the next episode card appears",
            Segments([new("0", "Countdown off"), new("5", "5 seconds"), new("10", "10 seconds"), new("15", "15 seconds")],
                () => _preferences.NextEpisodeCountdownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { NextEpisodeCountdownSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }), minimumWidth: 212));
        AddRow(behavior, "Intro", "Use chapter markers supplied by the server",
            Dropdown("Intro", [new("ShowButton", "Show skip intro button"), new("Automatic", "Skip automatically"), new("Off", "Do not skip")],
                () => _preferences.IntroSkipMode, value => Change(p => p with { IntroSkipMode = value })));
        AddRow(behavior, "Resume playback", "When opening a partially watched video",
            Dropdown("Resume playback", [new("Continue", "Continue from last position"), new("Ask", "Ask every time"), new("Restart", "Play from beginning")],
                () => _preferences.ResumeMode, value => Change(p => p with { ResumeMode = value })));

        var quality = Group("Quality and bitrate");
        AddRow(quality, "Prefer direct play", "Prefer direct play when the format and bitrate limits allow it",
            Switch("Prefer direct play", () => _preferences.PreferDirectPlay, value => Change(p => p with { PreferDirectPlay = value })));
        AddRow(quality, "Local network maximum bitrate", "When connecting to a server on your local network",
            Dropdown("Local network maximum bitrate", [new("0", "Unlimited"), new("120000000", "120 Mbps"), new("80000000", "80 Mbps"), new("40000000", "40 Mbps")],
                () => _preferences.LocalMaxBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { LocalMaxBitrate = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) })));
        AddRow(quality, "Internet maximum bitrate", "When connecting outside your local network",
            Dropdown("Internet maximum bitrate", [new("20000000", "20 Mbps"), new("10000000", "10 Mbps"), new("4000000", "4 Mbps"), new("0", "Unlimited")],
                () => _preferences.InternetMaxBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { InternetMaxBitrate = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) })));
    }

    private void BuildSubtitles()
    {
        var tracks = Group("Default tracks");
        AddRow(tracks, "Preferred subtitle language", "Prefer this language when multiple subtitle tracks are available",
            Dropdown("Preferred subtitle language", [new("chi", "Simplified Chinese"), new("zht", "Traditional Chinese"), new("eng", "English"), new("", "Follow audio language")],
                () => _preferences.SubtitleLanguage, value => Change(p => p with { SubtitleLanguage = value }),
                formatUnknown: value => T("Language ({0})", value)));
        var mode = AddRow(tracks, "Subtitle mode", "When subtitles are shown automatically",
            Segments([new("Smart", "Smart"), new("Always", "Always show"), new("OnlyForced", "Forced only"), new("None", "Off")],
                () => _preferences.SubtitleMode, value => Change(p => p with { SubtitleMode = value })));
        _refreshControls.Add(() =>
        {
            mode.Description.Text = _preferences.SubtitleMode switch
            {
                "Default" => T("Current server setting: default subtitles"),
                "HearingImpaired" => T("Current server setting: hearing-impaired subtitles"),
                _ => T("When subtitles are shown automatically")
            };
            AutomationProperties.SetHelpText(mode.Control, mode.Description.Text);
        });
        var style = Group("Subtitle style");
        const string unavailableReason = "Client text subtitle styling is unavailable on this Windows installation.";
        var sizeDescription = _subtitleStyleAvailable
            ? "Text subtitle size; picture and server-burned subtitles are unchanged."
            : unavailableReason;
        var outlineDescription = _subtitleStyleAvailable
            ? "Keep text subtitles legible against bright scenes"
            : unavailableReason;
        var positionDescription = _subtitleStyleAvailable ? "Where text subtitles appear" : unavailableReason;
        AddRow(style, "Font size", sizeDescription,
            Segments([new("Small", "Small"), new("Medium", "Medium"), new("Large", "Large")],
                () => _preferences.SubtitleSize, value => Change(p => p with { SubtitleSize = value }), enabled: _subtitleStyleAvailable, reason: sizeDescription));
        AddRow(style, "Text outline", outlineDescription,
            Switch("Text outline", () => _preferences.SubtitleOutline, value => Change(p => p with { SubtitleOutline = value }), _subtitleStyleAvailable, outlineDescription));
        AddRow(style, "Subtitle position", positionDescription,
            Segments([new("Bottom", "Bottom of picture"), new("BlackBars", "In black bars")],
                () => _preferences.SubtitlePosition, value => Change(p => p with { SubtitlePosition = value }),
                enabled: _subtitleStyleAvailable, reason: positionDescription));
    }

    private void BuildAppearance()
    {
        var appearance = Group("Interface");
        AddRow(appearance, "Theme", "Follow the Windows light or dark appearance", Segments([new("System", "Follow system"), new("Dark", "Dark"), new("Light", "Light")],
            () => _preferences.Theme, value => Change(p => p with { Theme = value })));
        AddRow(appearance, "Accent color", "Color for progress, selection, and focused content", AccentSwatches());
        AddRow(appearance, "Home carousel", "Rotate featured artwork every 8 seconds", Switch("Home carousel", () => _preferences.HeroRotation,
            value => Change(p => p with { HeroRotation = value })));
        AddRow(appearance, "Home scroll mode", "Move by full pages or scroll freely through the home screen",
            Segments([new("Paged", "Full-page scrolling"), new("Continuous", "Continuous scrolling")],
                () => _preferences.HomeScrollMode, value => Change(p => p with { HomeScrollMode = value })));
        var posters = Group("Poster wall");
        AddRow(posters, "Poster size", "Also available from the slider above each library", PosterSizeControl());
        AddRow(posters, "Show watched marks", "Mark completed titles at the top right of each poster", Switch("Show watched marks", () => _preferences.ShowWatchedMarks,
            value => Change(p => p with { ShowWatchedMarks = value })));
    }

    private void BuildSystem()
    {
        var decoding = Group("Decoding and output");
        AddRow(decoding, "Video decoding", "Applies to the next video. Unavailable decoder APIs or unsupported streams fall back to software.", DecoderApiDropdown());
        var hdrControl = Segments([new("Auto", "Auto"), new("Always", "Always"), new("Off", "Off")],
            () => _preferences.HdrMode, value => Change(p => p with { HdrMode = value }),
            optionEnabledWhen: CanSelectHdrMode, optionHelpText: HdrModeDescription);
        var hdrRow = AddRow(decoding, "HDR output", "", hdrControl);
        hdrRow.Description.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(hdrRow.Description, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _refreshControls.Add(() =>
        {
            hdrRow.Description.Text = _preferences.HdrMode == "Off" && !CanSelectHdrMode("Auto")
                ? T("{0} {1}", HdrModeDescription("Off"), HdrAvailabilityDescription())
                : HdrModeDescription(_preferences.HdrMode);
            AutomationProperties.SetHelpText(hdrControl, hdrRow.Description.Text);
        });
        var passthroughControl = Segments(
            [new("Off", "Off"), new("DolbyDts", "Dolby Digital / DTS"), new("All", "All supported formats")],
            () => _preferences.AudioPassthrough, value => Change(p => p with { AudioPassthrough = value }),
            optionEnabledWhen: CanSelectAudioPassthrough, optionHelpText: AudioPassthroughDescription);
        var passthroughRow = AddRow(decoding, "Audio passthrough", "", passthroughControl);
        passthroughRow.Description.Visibility = Visibility.Visible;
        AutomationProperties.SetLiveSetting(passthroughRow.Description,
            Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _refreshControls.Add(() =>
        {
            passthroughRow.Description.Text = AudioPassthroughDescription(_preferences.AudioPassthrough);
            AutomationProperties.SetHelpText(passthroughControl, passthroughRow.Description.Text);
        });
        const string refreshDescription = "Match the video frame rate during full-screen playback when the display supports it";
        var refreshToggle = Switch("Match content refresh rate", () => _preferences.MatchRefreshRate,
            value => Change(p => p with { MatchRefreshRate = value }), reason: refreshDescription,
            enabledWhen: () => _refreshRateAvailable);
        var refreshRow = AddRow(decoding, "Match content refresh rate", refreshDescription, refreshToggle);
        AutomationProperties.SetLiveSetting(refreshRow.Description, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _refreshControls.Add(() =>
        {
            refreshRow.Description.Text = RefreshRateDescription(refreshDescription);
            AutomationProperties.SetHelpText(refreshToggle, refreshRow.Description.Text);
            ToolTipService.SetToolTip(refreshToggle, refreshRow.Description.Text);
        });
        const string sleepReason = "Keep the screen awake while a video is playing";
        AddRow(decoding, "Prevent sleep during playback", sleepReason,
            Switch("Prevent sleep during playback", () => _preferences.PreventSleepDuringPlayback,
                value => Change(p => p with { PreventSleepDuringPlayback = value })));

        var integration = Group("Window and system integration");
        AddStartupRegistrationRow(integration);
        const string placementReason = "Restore the saved window size and position when Lumen opens";
        AddRow(integration, "Remember window size and position", placementReason,
            Switch("Remember window size and position", () => _preferences.RememberWindowPlacement,
                value => Change(p => p with { RememberWindowPlacement = value })));
        const string mediaReason = "Media keys, lock-screen controls, and taskbar playback buttons";
        AddRow(integration, "System media controls", mediaReason,
            Switch("System media controls", () => _preferences.SystemMediaControls,
                value => Change(p => p with { SystemMediaControls = value })));
        const string notificationDescription = "Notify when a followed or favorite series has a new episode (only while Lumen is running and connected to the server)";
        AddRow(integration, "New episode notifications", notificationDescription,
            Switch("New episode notifications", () => _preferences.NewEpisodeNotifications,
                value => Change(p => p with { NewEpisodeNotifications = value }), reason: notificationDescription));

        var connection = Group("Connection");
        AddRow(connection, "Automatically reconnect", "Reconnect to this server after a temporary connection failure",
            Switch("Automatically reconnect", () => _preferences.AutoReconnect,
                value => Change(p => p with { AutoReconnect = value })));

        var storage = Group("Storage");
        AddRow(storage, "Image cache limit", "Reducing the limit immediately removes older cached artwork",
            Dropdown("Image cache limit", [new("0", "Do not retain images"), new("16", "16 MB"), new("32", "32 MB"),
                new("64", "64 MB"), new("128", "128 MB"), new("256", "256 MB"), new("512", "512 MB")],
                () => _preferences.ImageCacheMegabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { ImageCacheMegabytes = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }),
                formatUnknown: value => T("{0} MB", value)));
        var clearCache = ActionButton("Clear cache", "Clear image cache");
        clearCache.Click += (_, _) => ClearImageCacheRequested?.Invoke(this, EventArgs.Empty);
        var cacheRow = AddRow(storage, "Image cache", "Artwork is cached in memory for the current session", clearCache);
        _refreshControls.Add(() =>
        {
            cacheRow.Description.Text = _imageCacheUsage.MaximumBytes == 0
                ? T("Image retention is off. Artwork still loads normally.")
                : T("Posters and stills \u00b7 {0:0.0} / {1:0} MB retained for this session \u00b7 {2} images",
                    _imageCacheUsage.Bytes / (1024d * 1024), _imageCacheUsage.MaximumBytes / (1024d * 1024),
                    _imageCacheUsage.EntryCount);
            clearCache.IsEnabled = _imageCacheUsage.EntryCount > 0;
            AutomationProperties.SetHelpText(clearCache, cacheRow.Description.Text);
        });
        var diagnostics = ActionButton("Open folder", "Open diagnostic logs folder");
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        AddRow(storage, "Diagnostic logs", "Include these logs when reporting playback problems", diagnostics);
    }

    private bool CanSelectHdrMode(string mode) => mode == "Off"
        || _hdrDisplayEligibility is { NativeHdrApiAvailable: true, DisplayHdrActive: true, DisplaySupportsHdr10: true };

    private static bool CanSelectAudioPassthrough(string mode) => mode == "Off"
        || NativePlaybackEngine.AudioPassthroughAvailable;

    private static string AudioPassthroughDescription(string mode)
    {
        if (!NativePlaybackEngine.AudioPassthroughAvailable)
            return T("The native audio passthrough interface is unavailable. Audio uses normal PCM output.");
        if (mode == "Off")
            return T("Decode audio to PCM with normal volume, mute, and playback speed controls.");
        var formats = T(mode == "DolbyDts" ? "AC3, EAC3, DTS core" : "AC3, EAC3, DTS core, TrueHD, DTS-HD");
        return T("Applies to the next video. Request {0} through exclusive WASAPI when the output device supports the exact format; otherwise use PCM. Changing volume, mute, or playback speed switches to PCM.", formats);
    }

    private string HdrModeDescription(string mode)
    {
        if (mode == "Off")
            return T("Turning HDR off requests server conversion to SDR for an active HDR10 video; Windows HDR is not changed.");
        if (CanSelectHdrMode(mode))
            return T(mode == "Always"
                ? "Applies the next time a video opens. Request HDR output for compatible HDR10 sources while Windows HDR is on; other HDR formats use server conversion."
                : "Applies the next time a video opens. Follow Windows HDR for compatible HDR10 sources; other HDR formats use server conversion.");
        return T("Applies the next time a video opens. {0} HDR sources require server conversion to SDR.", HdrAvailabilityDescription());
    }

    private string HdrAvailabilityDescription()
    {
        if (_hdrDisplayEligibility is not { } eligibility || eligibility.State == HdrMetadataConfigurationState.Pending)
            return T("Reading HDR support for the current display.");
        if (eligibility.UnavailableReason is "HdrWindowUnavailable" or "HdrDispatcherUnavailable" or "DisplayHdrInformationUnavailable")
            return T("HDR support for the current display could not be read.");
        if (!eligibility.NativeHdrApiAvailable || eligibility.UnavailableReason == "NativeHdrApiUnavailable")
            return T("HDR output is unavailable with the current playback setup.");
        if (eligibility.DisplayHdrActive == false || eligibility.UnavailableReason == "DisplayHdrDisabled")
            return T("Windows HDR is off. Enable HDR in Windows settings to use Auto or Always.");
        if (eligibility.DisplaySupportsHdr10 == false || eligibility.UnavailableReason == "Hdr10MetadataUnsupported")
            return T("The current display mode does not support HDR10 output.");
        return T("HDR support for the current display could not be read.");
    }

    private string RefreshRateDescription(string defaultDescription)
    {
        if (_refreshRateStatus is not { } status)
            return T("Display refresh-rate matching is unavailable in this app window.");
        if (!_refreshRateAvailable)
            return T(status.Reason == ContentRefreshRateReason.PlatformUnavailable
                ? "Display refresh-rate matching is unavailable on this system."
                : "Display refresh-rate matching is unavailable in this app window.");
        if (!_preferences.MatchRefreshRate && status.State != ContentRefreshRateState.RestorePending)
            return T(defaultDescription);
        return status.State switch
        {
            ContentRefreshRateState.Matched when status.RefreshRate is { } refreshRate && refreshRate > 0
                && status.FramesPerSecond is { } frameRate && frameRate > 0
                => T("Matched: {0} Hz \u00b7 nominal video {1:0.###} fps", refreshRate, frameRate),
            ContentRefreshRateState.Matched when status.RefreshRate is { } refreshRate && refreshRate > 0
                => T("Matched display refresh rate: {0} Hz", refreshRate),
            ContentRefreshRateState.WaitingForFrameRate
                => T("Waiting for the video frame rate before matching the display."),
            ContentRefreshRateState.Unsupported => T(status.Reason switch
            {
                ContentRefreshRateReason.PlatformUnavailable => "Display refresh-rate matching is unavailable on this system.",
                ContentRefreshRateReason.MonitorUnavailable or ContentRefreshRateReason.CurrentModeUnavailable
                    => "The current display mode could not be read.",
                ContentRefreshRateReason.NoCompatibleMode => "This display has no compatible mode for the video frame rate.",
                ContentRefreshRateReason.ModeRejected => "The display rejected the requested refresh rate.",
                ContentRefreshRateReason.ReadbackFailed => "The changed display mode could not be confirmed.",
                _ => "Windows could not change the display refresh rate."
            }),
            ContentRefreshRateState.ExternalChange
                => T("The display mode was changed outside Lumen; that mode will be kept."),
            ContentRefreshRateState.RestorePending
                => T("Windows could not restore the previous display mode yet."),
            ContentRefreshRateState.Closed
                => T("Display refresh-rate matching is unavailable in this app window."),
            _ => T(defaultDescription)
        };
    }

    private void AddStartupRegistrationRow(StackPanel group)
    {
        if (!_startupChanging) _startupStatus = _startupRegistration.GetStatus();
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var busy = new ProgressRing { Width = 18, Height = 18, IsActive = false, Foreground = LumenTheme.Brush("Accent") };
        AutomationProperties.SetName(busy, T("Changing startup registration"));
        controls.Children.Add(busy);
        var retry = ActionButton("Retry", "Retry startup registration");
        retry.Click += (_, _) =>
        {
            if (_startupChanging) return;
            _startupStatus = _startupRegistration.GetStatus();
            RefreshControls();
        };
        controls.Children.Add(retry);
        var toggle = Switch("Launch at startup", () => _startupStatus.IsEnabled,
            value => { _ = SetStartupRegistrationAsync(value); },
            enabledWhen: () => _startupStatus is { IsSupported: true, Error: null } && !_startupChanging);
        controls.Children.Add(toggle);
        var row = AddRow(group, "Launch at startup", "Start Lumen when you sign in to Windows", controls);
        AutomationProperties.SetLiveSetting(row.Description, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _refreshControls.Add(() =>
        {
            var description = _startupChanging ? "Changing startup registration"
                : _startupStatus.Error ?? "Start Lumen when you sign in to Windows";
            row.Description.Text = T(description);
            row.Description.Foreground = LumenTheme.Brush(_startupStatus.Error is null ? "Muted" : "Danger");
            busy.IsActive = _startupChanging;
            busy.Visibility = _startupChanging ? Visibility.Visible : Visibility.Collapsed;
            retry.Visibility = _startupStatus.Error is not null && _startupStatus.IsSupported ? Visibility.Visible : Visibility.Collapsed;
            retry.IsEnabled = !_startupChanging;
            AutomationProperties.SetHelpText(toggle, row.Description.Text);
        });
    }

    private async Task SetStartupRegistrationAsync(bool enabled)
    {
        if (_synchronizing || _startupChanging || !_startupStatus.IsSupported || _startupStatus.Error is not null) return;
        _startupChanging = true;
        RefreshControls();
        try
        {
            _startupStatus = await _startupRegistration.SetEnabledAsync(enabled);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            _startupStatus = _startupStatus with { Error = "Could not change startup registration. Try again." };
        }
        finally
        {
            _startupChanging = false;
            RefreshControls();
        }
    }

    private void BuildAccount()
    {
        var server = Group("Server");
        var root = _session?.Api.ApiRoot;
        var serverValue = _session?.Server.ServerName is { Length: > 0 } name
            ? root is null ? name : $"{name} \u00b7 {root.AbsoluteUri.TrimEnd('/')}"
            : root?.AbsoluteUri.TrimEnd('/') ?? T("Not available");
        AddRow(server, "Server", "", ValueText(serverValue));
        AddRow(server, "Server version", "", ValueText(_session?.Server.Version is { } version ? $"Emby Server {version}" : T("Not available")));
        AddRow(server, "Connection", "", ValueText(root is null ? T("Not available") : T(root.Scheme == "https" ? "Secure HTTPS" : "HTTP")));
        var changeServer = ActionButton("Switch", "Switch server");
        changeServer.Click += (_, _) => SwitchAccountRequested?.Invoke(this, EventArgs.Empty);
        AddRow(server, "Switch server", "Return to the connection page to choose or add a server", changeServer);
        var account = Group("Account");
        AddRow(account, "Current user", "", ValueText(_session?.User.Name ?? T("Not available")));
        var change = ActionButton("Switch", "Switch user");
        change.Click += (_, _) => SwitchAccountRequested?.Invoke(this, EventArgs.Empty);
        AddRow(account, "Switch user", "Sign in to a different account on this server", change);
        AddRow(account, "Saved accounts", "", ValueText(_savedAccountCount == 0 ? T("No saved credentials") : T("{0} accounts · encrypted with DPAPI", _savedAccountCount)));
    }

    private void BuildAbout()
    {
        var about = Group("Application");
        var version = _session?.Api.Identity.Version ?? "0.1.0";
        AddRow(about, "Application version", "", ValueText($"{version} \u00b7 {T("Development build")}"));
        var check = ActionButton("Check", "Check updates");
        var checking = new ProgressRing { Width = 18, Height = 18, Foreground = LumenTheme.Brush("Accent") };
        AutomationProperties.SetName(checking, T("Checking"));
        var checkControls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        checkControls.Children.Add(checking);
        checkControls.Children.Add(check);
        var status = AddRow(about, "Check updates", _updateStatus, checkControls);
        AutomationProperties.SetLiveSetting(status.Description, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        status.Description.Text = T(_updateStatus, _updateArguments);
        _refreshControls.Add(() =>
        {
            check.IsEnabled = _updateCheck is null;
            var label = _updateOperation == "Checking" ? "Checking" : "Check";
            check.Content = ActionLabel(label);
            AutomationProperties.SetName(check, T(label == "Checking" ? "Checking for updates" : "Check updates"));
            checking.IsActive = _updateOperation == "Checking";
            checking.Visibility = checking.IsActive ? Visibility.Visible : Visibility.Collapsed;
            status.Description.Text = T(_updateStatus, _updateArguments);
        });
        check.Click += async (_, _) => await CheckUpdatesAsync();

        var update = ActionButton("Download update");
        update.MinWidth = 124;
        update.Click += async (_, _) =>
        {
            if (_stagedUpdate is not null) await InstallUpdateAsync();
            else await DownloadUpdateAsync();
        };
        var cancel = ActionButton("Cancel", "Cancel update download");
        cancel.Click += (_, _) => _updateCheck?.Cancel();
        var releaseLink = new HyperlinkButton { Content = T("View release"), Foreground = LumenTheme.Brush("Accent"), Padding = new Thickness(0) };
        AutomationProperties.SetName(releaseLink, T("View release"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        actions.Children.Add(update);
        actions.Children.Add(cancel);
        var downloadProgress = new ProgressBar { Width = 232, Minimum = 0, Maximum = 100, Foreground = LumenTheme.Brush("Accent") };
        AutomationProperties.SetName(downloadProgress, T("Update download progress"));
        var updateControls = new StackPanel { Spacing = 10 };
        updateControls.Children.Add(actions);
        updateControls.Children.Add(downloadProgress);
        updateControls.Children.Add(releaseLink);
        var package = AddRow(about, "Update package", "Check for a published release to see available downloads", updateControls);
        _refreshControls.Add(() =>
        {
            var label = _stagedUpdate is null ? "Download update" : "Install and restart";
            update.Content = ActionLabel(label);
            AutomationProperties.SetName(update, T(label));
            update.IsEnabled = _updateCheck is null && (_stagedUpdate is not null
                || _availableUpdate is { IsNewer: true, Asset: not null });
            cancel.Visibility = _updateOperation == "Downloading" ? Visibility.Visible : Visibility.Collapsed;
            cancel.IsEnabled = _updateCheck is { IsCancellationRequested: false };
            downloadProgress.Visibility = _updateOperation == "Downloading" ? Visibility.Visible : Visibility.Collapsed;
            downloadProgress.IsIndeterminate = _updateProgress?.Percent is null;
            downloadProgress.Value = Math.Clamp(_updateProgress?.Percent ?? 0, 0, 100);
            releaseLink.Visibility = _availableUpdate is null ? Visibility.Collapsed : Visibility.Visible;
            releaseLink.NavigateUri = _availableUpdate?.ReleasePage;
            package.Description.Text = _stagedUpdate is { } staged
                ? T("Update {0} is downloaded and ready to install", staged.Release.Tag)
                : _availableUpdate is not { } release
                    ? T("Check for a published release to see available downloads")
                    : !release.IsNewer
                        ? T("You are using the latest published version")
                        : release.Asset is not { } asset
                            ? T("This release has no compatible x64 update package.")
                            : T("Download {0} ({1:0.0} MB), then choose Install and restart", asset.Name, asset.Size / (1024d * 1024));
            AutomationProperties.SetHelpText(update, package.Description.Text);
        });
        var shortcuts = ActionButton("View", "Keyboard shortcuts");
        shortcuts.Click += (_, _) => KeyboardShortcutsRequested?.Invoke(this, EventArgs.Empty);
        AddRow(about, "Keyboard shortcuts", "Press ? on any page to open the shortcut guide", shortcuts);
        var licenses = ActionButton("View", "Open source licenses");
        licenses.Click += async (_, _) => await ShowLicensesAsync();
        AddRow(about, "Open source licenses", "Third-party licenses shipped with this application", licenses);
    }

    private void Change(Func<LumenPreferences, LumenPreferences> update)
    {
        if (_synchronizing) return;
        var changed = update(_preferences);
        if (changed == _preferences) return;
        _preferences = changed;
        PreferencesChanged?.Invoke(this, changed);
        RefreshControls();
    }

    private void RefreshControls()
    {
        _synchronizing = true;
        try
        {
            foreach (var refresh in _refreshControls) refresh();
        }
        finally { _synchronizing = false; }
    }

    private StackPanel Group(string key)
    {
        var group = new StackPanel();
        var title = LumenUi.Text(T(key), 22, serif: true);
        title.FontWeight = FontWeights.Bold;
        title.LineHeight = 26;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        group.Children.Add(title);
        group.Children.Add(new Border
        {
            Height = 1, Background = LumenTheme.Brush("LineStrong"), Margin = new Thickness(0, 10, 0, 0)
        });
        _groups.Children.Add(group);
        return group;
    }

    private SettingRowLayout AddRow(StackPanel group, string title, string description, FrameworkElement control)
    {
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var name = LumenUi.Text(T(title), 15);
        name.FontWeight = FontWeights.SemiBold;
        name.TextWrapping = TextWrapping.Wrap;
        name.LineHeight = 22;
        name.LineStackingStrategy = LineStackingStrategy.MaxHeight;
        labels.Children.Add(name);
        var help = LumenUi.Text(string.IsNullOrEmpty(description) ? "" : T(description), 13);
        help.Foreground = LumenTheme.Brush("Muted");
        help.TextWrapping = TextWrapping.Wrap;
        help.LineHeight = 19;
        help.Visibility = string.IsNullOrEmpty(description) ? Visibility.Collapsed : Visibility.Visible;
        labels.Children.Add(help);
        AutomationProperties.SetLabeledBy(control, name);
        if (!string.IsNullOrEmpty(description)) AutomationProperties.SetHelpText(control, help.Text);
        var row = new Grid { MinHeight = 77, ColumnSpacing = 24, Margin = new Thickness(4, 0, 4, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(labels);
        Grid.SetColumn(control, 1);
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(control);
        group.Children.Add(new Border
        {
            Child = row, BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1)
        });
        var layout = new SettingRowLayout(row, labels, control, help);
        _rows.Add(layout);
        return layout;
    }

    private static TextBlock ValueText(string value)
    {
        var text = LumenUi.Text(value, 14);
        text.Foreground = LumenTheme.Brush("Sub");
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxWidth = 420;
        text.TextAlignment = TextAlignment.Right;
        return text;
    }

    private void UpdateLayoutWidth()
    {
        if (ActualWidth <= 0) return;
        var padding = Math.Clamp(ActualWidth * .04, 24, 56);
        _column.Width = Math.Max(0, Math.Min(880, ActualWidth - 2 * padding));
        _tabScroller.MaxWidth = _column.Width;
        _accountServer.Visibility = _column.Width < 420 ? Visibility.Collapsed : Visibility.Visible;
        var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
        var headerTitle = (FrameworkElement)_header.Children[0];
        headerTitle.Measure(unconstrained);
        _headerActions.Measure(unconstrained);
        var compactHeader = headerTitle.DesiredSize.Width + _header.ColumnSpacing + _headerActions.DesiredSize.Width > _column.Width;
        _header.RowDefinitions.Clear();
        if (compactHeader)
        {
            _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _header.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        Grid.SetColumn(_headerActions, compactHeader ? 0 : 1);
        Grid.SetRow(_headerActions, compactHeader ? 1 : 0);
        Grid.SetColumnSpan(_headerActions, compactHeader ? 2 : 1);
        _headerActions.HorizontalAlignment = compactHeader ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        UpdateRowLayout();
    }

    private void UpdateRowLayout()
    {
        foreach (var layout in _rows)
        {
            layout.Control.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var availableWidth = Math.Max(0, _column.Width - layout.Row.Margin.Left - layout.Row.Margin.Right);
            // Match the prototype's 240-pixel text basis and wrap only when the control does not fit beside it.
            var compact = 240 + layout.Row.ColumnSpacing + layout.Control.DesiredSize.Width > availableWidth;
            layout.Row.RowDefinitions.Clear();
            layout.Row.ColumnDefinitions.Clear();
            layout.Row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (compact)
            {
                layout.Row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                layout.Row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(layout.Control, 0);
                Grid.SetRow(layout.Control, 1);
                layout.Labels.Margin = new Thickness(0, 16, 0, 8);
                layout.Control.Margin = new Thickness(0, 0, 0, 16);
                layout.Control.HorizontalAlignment = HorizontalAlignment.Left;
                if (layout.Control is TextBlock value) value.TextAlignment = TextAlignment.Left;
            }
            else
            {
                layout.Row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(layout.Control, 1);
                Grid.SetRow(layout.Control, 0);
                layout.Labels.Margin = new Thickness(0, 14, 0, 14);
                layout.Control.Margin = new Thickness(0);
                layout.Control.HorizontalAlignment = HorizontalAlignment.Right;
                if (layout.Control is TextBlock value) value.TextAlignment = TextAlignment.Right;
            }
        }
    }

    private static Button FlatButton(string label, double height, bool highlightOnHover = false)
    {
        var text = LumenUi.Text(label, 13);
        text.TextWrapping = TextWrapping.NoWrap;
        text.MaxLines = 1;
        text.FontWeight = FontWeights.Medium;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        var button = new Button
        {
            Content = text, Height = height, MinHeight = 0, MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(height / 2), Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent), Foreground = LumenTheme.Brush("Ink"),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = LumenTheme.SansFont, FontSize = 13
        };
        AutomationProperties.SetName(button, label);
        NeutralChrome(button, "Button", highlightOnHover);
        return button;
    }

    private static TextBlock ActionLabel(string label)
    {
        var text = LumenUi.Text(T(label), 13);
        text.FontWeight = FontWeights.SemiBold;
        text.TextWrapping = TextWrapping.NoWrap;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    private static Button ActionButton(string label, string? accessibleName = null)
    {
        var button = LumenUi.Button(T(label), height: 34);
        var clear = new SolidColorBrush(Colors.Transparent);
        button.Content = ActionLabel(label);
        button.Padding = new Thickness(18, 0, 18, 0);
        button.Background = clear;
        button.FontSize = 13;
        AutomationProperties.SetName(button, T(accessibleName ?? label));
        LumenUi.SetResource(button.Resources, "ButtonBackground", clear);
        LumenUi.SetResource(button.Resources, "ButtonBackgroundPointerOver", LumenTheme.Brush("Hover"));
        return button;
    }

    private static void NeutralChrome(Control control, string prefix, bool highlightOnHover = false)
    {
        // Initialize a new control once; theme refreshes update the shared brush objects.
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled" })
        {
            control.Resources[$"{prefix}Background{state}"] = highlightOnHover && (state is "PointerOver" or "Pressed")
                ? LumenTheme.Brush("ControlStrong") : clear;
            control.Resources[$"{prefix}BorderBrush{state}"] = clear;
            control.Resources[$"{prefix}Foreground{state}"] = LumenTheme.Brush("Ink");
        }
        control.UseSystemFocusVisuals = true;
        control.FocusVisualPrimaryBrush = LumenTheme.Brush("FocusOuter");
        control.FocusVisualSecondaryBrush = LumenTheme.Brush("FocusInner");
    }

    private ToggleButton Switch(string name, Func<bool> getter, Action<bool> setter, bool enabled = true,
        string? reason = null, Func<bool>? enabledWhen = null)
    {
        var offset = new TranslateTransform();
        var thumb = new Ellipse
        {
            Width = 18, Height = 18, Fill = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = offset
        };
        var inner = new Grid { Width = 38, Height = 18 };
        inner.Children.Add(thumb);
        var track = new Border
        {
            Width = 44, Height = 24, Padding = new Thickness(3), CornerRadius = new CornerRadius(12), Child = inner
        };
        var toggle = new ToggleButton
        {
            Width = 44, Height = 44, MinWidth = 44, Padding = new Thickness(0), BorderThickness = new Thickness(0),
            Content = track, IsEnabled = enabled, CornerRadius = new CornerRadius(22)
        };
        NeutralChrome(toggle, "ToggleButton");
        AutomationProperties.SetName(toggle, T(name));
        if (reason is not null)
        {
            AutomationProperties.SetHelpText(toggle, T(reason));
            ToolTipService.SetToolTip(toggle, T(reason));
        }
        var initial = true;
        _refreshControls.Add(() =>
        {
            var value = getter();
            toggle.IsEnabled = enabled && (enabledWhen?.Invoke() ?? true);
            toggle.IsChecked = value;
            track.Background = LumenTheme.Brush(value ? "Accent" : "ControlStrong");
            thumb.Fill = LumenTheme.Brush(value ? "ImageInk" : "Knob");
            if (initial) offset.X = value ? 20 : 0;
            else Animate(offset, "X", value ? 20 : 0, 250, spring: true);
            initial = false;
            track.Opacity = toggle.IsEnabled ? 1 : .45;
        });
        toggle.Checked += (_, _) => { if (!_synchronizing) setter(true); };
        toggle.Unchecked += (_, _) => { if (!_synchronizing) setter(false); };
        toggle.PointerEntered += (_, _) => { if (toggle.IsEnabled) track.Opacity = .86; };
        toggle.PointerExited += (_, _) => { if (toggle.IsEnabled) track.Opacity = 1; };
        return toggle;
    }

    private FrameworkElement Segments(Option[] options, Func<string> getter, Action<string> setter,
        bool enabled = true, string? reason = null, double minimumWidth = 0,
        Func<string, bool>? optionEnabledWhen = null, Func<string, string>? optionHelpText = null)
    {
        const double horizontalPadding = 14;
        const double gap = 2;
        const double outerInset = 8;
        var widths = new double[options.Length];
        var offsets = new double[options.Length];
        for (var index = 0; index < options.Length; index++)
        {
            var label = LumenUi.Text(T(options[index].Label), 13);
            label.TextWrapping = TextWrapping.NoWrap;
            label.FontWeight = FontWeights.Bold;
            label.MaxLines = 1;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[index] = Math.Max(40, Math.Ceiling(label.DesiredSize.Width) + horizontalPadding * 2);
        }
        var extraWidth = Math.Max(0, minimumWidth - outerInset - (options.Length - 1) * gap - widths.Sum());
        for (var index = 0; index < options.Length; index++)
        {
            widths[index] += extraWidth / options.Length;
            if (index > 0) offsets[index] = offsets[index - 1] + widths[index - 1] + gap;
        }
        var offset = new TranslateTransform();
        var track = new Grid { Height = 30, Width = widths.Sum() + (options.Length - 1) * gap };
        var indicator = new Border
        {
            Width = widths[0], Height = 30, CornerRadius = new CornerRadius(15),
            Background = LumenTheme.Brush("Ink"), HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = offset, IsHitTestVisible = false
        };
        track.Children.Add(indicator);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap };
        var choices = new List<Button>();
        for (var index = 0; index < options.Length; index++)
        {
            var option = options[index];
            var button = FlatButton(T(option.Label), 30);
            button.Width = widths[index];
            button.MinWidth = widths[index];
            button.Padding = new Thickness(horizontalPadding, 0, horizontalPadding, 0);
            button.IsEnabled = enabled && (optionEnabledWhen?.Invoke(option.Value) ?? true);
            var helpText = optionHelpText?.Invoke(option.Value) ?? (reason is null ? null : T(reason));
            if (helpText is not null)
            {
                ToolTipService.SetToolTip(button, helpText);
                AutomationProperties.SetHelpText(button, helpText);
            }
            button.Click += (_, _) => setter(option.Value);
            buttons.Children.Add(button);
            choices.Add(button);
        }
        track.Children.Add(buttons);
        var outer = new Border
        {
            Height = 38, Child = track, Padding = new Thickness(3), CornerRadius = new CornerRadius(19),
            Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("Line"),
            BorderThickness = new Thickness(1), Opacity = enabled ? 1 : .5
        };
        var initial = true;
        _refreshControls.Add(() =>
        {
            var selected = Array.FindIndex(options, option => option.Value == getter());
            indicator.Visibility = selected < 0 ? Visibility.Collapsed : Visibility.Visible;
            if (selected >= 0)
            {
                indicator.Width = widths[selected];
                if (initial) offset.X = offsets[selected];
                else Animate(offset, "X", offsets[selected], 250, spring: true);
            }
            initial = false;
            for (var index = 0; index < choices.Count; index++)
            {
                var available = enabled && (optionEnabledWhen?.Invoke(options[index].Value) ?? true);
                choices[index].IsEnabled = available;
                choices[index].Opacity = enabled && !available ? .45 : 1;
                if (index == selected) indicator.Opacity = enabled && !available ? .45 : 1;
                var helpText = optionHelpText?.Invoke(options[index].Value) ?? (reason is null ? null : T(reason));
                if (helpText is not null)
                {
                    ToolTipService.SetToolTip(choices[index], helpText);
                    AutomationProperties.SetHelpText(choices[index], helpText);
                }
                if (choices[index].Content is TextBlock label)
                {
                    label.Foreground = LumenTheme.Brush(index == selected ? "Background" : "Sub");
                    label.FontWeight = index == selected ? FontWeights.Bold : FontWeights.Medium;
                }
                AutomationProperties.SetItemStatus(choices[index], T(index == selected ? "Selected" : "Not selected"));
            }
        });
        return outer;
    }

    private FrameworkElement DecoderApiDropdown()
    {
        return Dropdown("Video decoding",
            [new("Auto", "Automatic (recommended)"), new("D3D11", "D3D11VA"), new("NvidiaNvdec", "NVIDIA NVDEC"),
             new("IntelQsv", "Intel Quick Sync"), new("AmdAmf", "AMD AMF"), new("Software", "Software decoding")],
            () => _preferences.VideoDecoderApi,
            value => Change(p => p with { VideoDecoderApi = value, HardwareDecoding = value != "Software" }));
    }

    private FrameworkElement PosterSizeControl() => Segments(
        [new("120", "Small"), new("150", "Medium"), new("190", "Large")],
        () => _preferences.PosterWidth <= 130 ? "120" : _preferences.PosterWidth <= 170 ? "150" : "190",
        value => Change(p => p with
        {
            PosterWidth = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            PosterColumns = value == "120" ? 10 : value == "190" ? 6 : 8
        }));

    private FrameworkElement Dropdown(string name, Option[] options, Func<string> getter, Action<string> setter,
        Func<string, string>? formatUnknown = null)
    {
        var button = LumenUi.Button("", height: 36);
        button.Width = 200;
        button.Padding = new Thickness(18, 0, 14, 0);
        button.Background = LumenTheme.Brush("Control");
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var value = LumenUi.Text("", 13);
        value.TextWrapping = TextWrapping.NoWrap;
        value.MaxLines = 1;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(value);
        var arrowOffset = new RotateTransform();
        var arrow = LumenUi.Icon("chevron-down", 12);
        arrow.Opacity = .7;
        arrow.RenderTransformOrigin = new Point(.5, .5);
        arrow.RenderTransform = arrowOffset;
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);
        button.Content = content;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, T(name));

        var items = new StackPanel();
        var checks = new List<(string Value, FrameworkElement Icon, Button Button)>();
        var clear = new SolidColorBrush(Colors.Transparent);
        var flyout = new Flyout { Content = items, FlyoutPresenterStyle = FlyoutStyle(200, 6) };
        void PopulateMenu()
        {
            items.Children.Clear();
            checks.Clear();
            var current = getter();
            var menuOptions = options.ToList();
            // Preserve a server value that the curated language or bitrate choices do not include.
            if (menuOptions.All(option => option.Value != current))
                menuOptions.Insert(0, new Option(current, formatUnknown?.Invoke(current) ?? current));
            foreach (var option in menuOptions)
            {
                var item = FlatButton(T(option.Label), 38, highlightOnHover: true);
                item.HorizontalAlignment = HorizontalAlignment.Stretch;
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                item.Padding = new Thickness(10, 0, 18, 0);
                item.CornerRadius = new CornerRadius(10);
                item.Background = option.Value == current ? LumenTheme.Brush("Selection") : clear;
                AutomationProperties.SetItemStatus(item, T(option.Value == current ? "Selected" : "Not selected"));
                var line = new Grid { ColumnSpacing = 10 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var check = LumenUi.Icon("check", 15);
                check.Opacity = option.Value == current ? 1 : 0;
                line.Children.Add(check);
                var label = LumenUi.Text(T(option.Label), 13);
                label.TextWrapping = TextWrapping.NoWrap;
                label.MaxLines = 1;
                label.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(label, 1);
                line.Children.Add(label);
                item.Content = line;
                item.Click += (_, _) => { setter(option.Value); flyout.Hide(); };
                items.Children.Add(item);
                checks.Add((option.Value, check, item));
            }
        }
        _refreshControls.Add(() =>
        {
            var selected = Array.FindIndex(options, option => option.Value == getter());
            value.Text = selected >= 0 ? T(options[selected].Label) : formatUnknown?.Invoke(getter()) ?? getter();
            AutomationProperties.SetHelpText(button, $"{T(name)}: {value.Text}");
            ToolTipService.SetToolTip(button, value.Text);
            foreach (var check in checks)
            {
                var current = check.Value == getter();
                check.Icon.Opacity = current ? 1 : 0;
                check.Button.Background = current ? LumenTheme.Brush("Selection") : clear;
                AutomationProperties.SetItemStatus(check.Button, T(current ? "Selected" : "Not selected"));
            }
        });
        flyout.Opened += (_, _) =>
        {
            button.Background = LumenTheme.Brush("ControlStrong");
            AutomationProperties.SetItemStatus(button, T("Expanded"));
            Animate(arrowOffset, "Angle", 180, 250);
        };
        flyout.Closed += (_, _) =>
        {
            button.Background = LumenTheme.Brush("Control");
            AutomationProperties.SetItemStatus(button, T("Collapsed"));
            Animate(arrowOffset, "Angle", 0, 180);
            if (_openFlyout == flyout) _openFlyout = null;
        };
        button.Click += (_, _) =>
        {
            if (_openFlyout != flyout) PopulateMenu();
            ShowFlyout(flyout, button);
        };
        return button;
    }

    private FrameworkElement AccentSwatches()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        var choices = new (string Value, string Label, Color Dark, Color Light)[]
        {
            ("Gold", "Amber", Color.FromArgb(255, 226, 184, 107), Color.FromArgb(255, 143, 98, 18)),
            ("Coral", "Coral", Color.FromArgb(255, 232, 134, 106), Color.FromArgb(255, 168, 69, 42)),
            ("Sage", "Sage", Color.FromArgb(255, 159, 194, 164), Color.FromArgb(255, 61, 112, 80)),
            ("Blue", "Mist blue", Color.FromArgb(255, 169, 184, 232), Color.FromArgb(255, 63, 85, 168))
        };
        foreach (var choice in choices)
        {
            var color = new SolidColorBrush(choice.Dark);
            var clear = new SolidColorBrush(Colors.Transparent);
            var swatch = new Ellipse { Width = 24, Height = 24, Fill = color };
            var ring = new Border
            {
                Width = 32, Height = 32, Padding = new Thickness(2), BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(16), Background = LumenTheme.Brush("Background"), Child = swatch
            };
            var button = FlatButton("", 44);
            button.Width = 36;
            button.Padding = new Thickness(0);
            button.Content = ring;
            AutomationProperties.SetName(button, T(choice.Label));
            ToolTipService.SetToolTip(button, T(choice.Label));
            button.Click += (_, _) => Change(p => p with { Accent = choice.Value });
            row.Children.Add(button);
            _refreshControls.Add(() =>
            {
                var selected = _preferences.Accent == choice.Value;
                ring.BorderBrush = selected ? color : clear;
                AutomationProperties.SetItemStatus(button, T(selected ? "Selected" : "Not selected"));
            });
        }
        return row;
    }

    private void ShowFlyout(Flyout flyout, FrameworkElement target)
    {
        if (_openFlyout == flyout) { flyout.Hide(); return; }
        _openFlyout?.Hide();
        _openFlyout = flyout;
        flyout.ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private static Style FlyoutStyle(double width, double padding)
    {
        var style = new Style
        {
            TargetType = typeof(FlyoutPresenter),
            BasedOn = (Style)Application.Current.Resources["DefaultFlyoutPresenterStyle"]
        };
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, width));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, width));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, width));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(padding)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, LumenTheme.Brush("Pop")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, LumenTheme.Brush("Ink")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, LumenTheme.Brush("LineStrong")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        return style;
    }

    private void UpdateAmbient()
    {
        var dark = ((SolidColorBrush)LumenTheme.Brush("Background")).Color.R < 128;
        var top = ((SolidColorBrush)LumenTheme.Brush("Muted")).Color;
        top.A = dark ? (byte)18 : (byte)90;
        var bottom = top;
        bottom.A = 0;
        if (_ambientBrush.GradientStops.Count == 0)
        {
            _ambientBrush.GradientStops.Add(new GradientStop { Offset = 0 });
            _ambientBrush.GradientStops.Add(new GradientStop { Offset = 1 });
        }
        _ambientBrush.GradientStops[0].Color = top;
        _ambientBrush.GradientStops[1].Color = bottom;
        _ambientArtwork.ApplyTheme();
        if (_ambientArtworkFade.GradientStops.Count == 0)
        {
            _ambientArtworkFade.GradientStops.Add(new GradientStop { Offset = 0 });
            _ambientArtworkFade.GradientStops.Add(new GradientStop { Offset = .55 });
            _ambientArtworkFade.GradientStops.Add(new GradientStop { Offset = 1 });
        }
        var background = LumenTheme.Color("Background");
        _ambientArtworkFade.GradientStops[0].Color = Color.FromArgb(dark ? (byte)51 : (byte)64, background.R, background.G, background.B);
        _ambientArtworkFade.GradientStops[1].Color = Color.FromArgb(dark ? (byte)184 : (byte)191, background.R, background.G, background.B);
        _ambientArtworkFade.GradientStops[2].Color = background;
    }

    private async Task ShowSignOutAsync()
    {
        if (_signOutDialog is not null || XamlRoot is null) return;
        _openFlyout?.Hide();
        var title = LumenUi.Text(T("Sign out?"), 22, serif: true);
        title.FontWeight = FontWeights.Bold;
        var server = _session?.Server.ServerName ?? _session?.Api.ApiRoot.Authority ?? T("Server");
        var description = LumenUi.Text(T("Disconnect from {0}? Your server-saved playback progress will remain.", server), 14);
        description.Foreground = LumenTheme.Brush("Sub");
        description.LineHeight = 22;
        description.MaxWidth = Math.Max(220, Math.Min(360, ActualWidth - 112));
        var dangerStyle = new Style { TargetType = typeof(Button) };
        dangerStyle.Setters.Add(new Setter(Control.BackgroundProperty, LumenTheme.Brush("Danger")));
        dangerStyle.Setters.Add(new Setter(Control.ForegroundProperty, LumenTheme.Brush("ImageInk")));
        dangerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, LumenTheme.Brush("Danger")));
        dangerStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(19)));
        dangerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(20, 0, 20, 0)));
        dangerStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, 38d));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = LumenTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Title = title, Content = description, PrimaryButtonText = T("Sign out"),
            CloseButtonText = T("Cancel"), DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = dangerStyle
        };
        LumenDialogTheme.Apply(dialog);
        dialog.Resources["ContentDialogMaxWidth"] = 440d;
        dialog.Resources["ContentDialogMinWidth"] = Math.Min(440d, Math.Max(280d, ActualWidth - 64));
        _signOutDialog = dialog;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                SignOutRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            if (ReferenceEquals(_signOutDialog, dialog)) _signOutDialog = null;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        if (_updateCheck is not null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _updateCheck = request;
        _updateOperation = "Checking";
        SetUpdateStatus("Checking for updates");
        try
        {
            var current = Version.TryParse(_session?.Api.Identity.Version, out var version)
                ? version : typeof(LumenSettingsView).Assembly.GetName().Version ?? new Version(0, 1, 0);
            _availableUpdate = await _releaseUpdates.CheckLatestAsync(current, request.Token);
            if (_stagedUpdate is not null && _stagedUpdate.Release.Tag != _availableUpdate?.Tag)
                _stagedUpdate = null;
            if (_availableUpdate is null)
            {
                SetUpdateStatus("Development build. No public release was found.");
            }
            else
            {
                SetUpdateStatus(_availableUpdate.IsNewer ? "Update available: {0}" : "Latest published release: {0}", _availableUpdate.Tag);
            }
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("Update check timed out. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            SetUpdateStatus("Update check failed. Try again later.");
        }
        finally
        {
            if (_updateCheck == request) _updateCheck = null;
            _updateOperation = "";
            RefreshControls();
        }
    }

    private async Task DownloadUpdateAsync()
    {
        if (_updateCheck is not null || _availableUpdate is not { IsNewer: true, Asset: not null } release) return;
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        _updateCheck = request;
        _updateOperation = "Downloading";
        _updateProgress = null;
        SetUpdateStatus("Downloading update");
        var progress = new Progress<ReleaseUpdateProgress>(value =>
        {
            // A queued progress report cannot overwrite a finished or canceled operation.
            if (!ReferenceEquals(_updateCheck, request) || request.IsCancellationRequested) return;
            _updateProgress = value;
            switch (value.Phase)
            {
                case ReleaseUpdatePhase.Downloading:
                    if (value.Percent is { } percent) SetUpdateStatus("Downloading update: {0:0}%", percent);
                    else SetUpdateStatus("Downloading update: {0:0.0} MB", value.BytesReceived / (1024d * 1024));
                    break;
                case ReleaseUpdatePhase.Verifying: SetUpdateStatus("Verifying update package"); break;
                case ReleaseUpdatePhase.Extracting: SetUpdateStatus("Preparing update package"); break;
                case ReleaseUpdatePhase.Ready: SetUpdateStatus("Update package is ready"); break;
            }
        });
        try
        {
            _stagedUpdate = await _releaseUpdates.DownloadAndStageAsync(release, progress, request.Token);
            SetUpdateStatus("Update {0} is ready. Choose Install and restart to apply it.", release.Tag);
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("Update download canceled. You can try again.");
        }
        catch (ReleaseUpdateException exception) when (exception.Issue == ReleaseUpdateIssue.InvalidChecksum)
        {
            SetUpdateStatus("The update package failed its integrity check. Download it again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException or SecurityException)
        {
            SetUpdateStatus("The update could not be downloaded or prepared. Try again.");
        }
        finally
        {
            if (_updateCheck == request) _updateCheck = null;
            _updateOperation = "";
            _updateProgress = null;
            RefreshControls();
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updateCheck is not null || _stagedUpdate is not { } staged) return;
        var shutdown = ShutdownRequested;
        if (shutdown is null)
        {
            SetUpdateStatus("Restart is unavailable in this app window. Reopen Lumen and try again.");
            return;
        }
        if (!await ConfirmUpdateInstallationAsync(staged)) return;
        if (!IsLoaded || Visibility != Visibility.Visible || !ReferenceEquals(staged, _stagedUpdate)) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _updateCheck = request;
        _updateOperation = "Installing";
        SetUpdateStatus("Starting the update installer");
        try
        {
            await _releaseUpdates.LaunchInstallAndRestartAsync(staged, request.Token);
            SetUpdateStatus("Restarting to install the update");
            shutdown.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("The update installer timed out. Try again.");
        }
        catch (ReleaseUpdateException exception) when (exception.Issue == ReleaseUpdateIssue.UnsupportedInstallation)
        {
            SetUpdateStatus("This installation cannot be updated automatically. Open the release page to install it.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            SetUpdateStatus("Could not start the update installer. Try again.");
        }
        finally
        {
            if (_updateCheck == request) _updateCheck = null;
            _updateOperation = "";
            RefreshControls();
        }
    }

    private async Task<bool> ConfirmUpdateInstallationAsync(StagedReleaseUpdate staged)
    {
        if (_installUpdateDialog is not null || XamlRoot is null) return false;
        _openFlyout?.Hide();
        var content = new StackPanel { Spacing = 12, MaxWidth = Math.Max(220, Math.Min(400, ActualWidth - 112)) };
        var description = LumenUi.Text(T("Lumen will close and open the updated version. The Start menu entry {0} will launch the new version. Your previous application folder remains available.",
            StagedReleaseUpdate.StartMenuEntryName), 14);
        description.TextWrapping = TextWrapping.Wrap;
        description.Foreground = LumenTheme.Brush("Sub");
        description.LineHeight = 22;
        content.Children.Add(description);
        var location = LumenUi.Text(T("Installation folder: {0}", staged.InstallDirectory), 13);
        location.TextWrapping = TextWrapping.Wrap;
        location.Foreground = LumenTheme.Brush("Muted");
        content.Children.Add(location);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = LumenTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Title = T("Install update {0}?", staged.Release.Tag), Content = content,
            PrimaryButtonText = T("Install and restart"), CloseButtonText = T("Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        LumenDialogTheme.Apply(dialog);
        dialog.Resources["ContentDialogMaxWidth"] = 480d;
        _installUpdateDialog = dialog;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally
        {
            if (ReferenceEquals(_installUpdateDialog, dialog)) _installUpdateDialog = null;
        }
    }

    private void SetUpdateStatus(string key, params object[] arguments)
    {
        _updateStatus = key;
        _updateArguments = arguments;
        RefreshControls();
    }

    private async Task ShowLicensesAsync()
    {
        if (_licensesOpen) return;
        _licensesOpen = true;
        try { await ShowLicensesCoreAsync(); }
        finally { _licensesOpen = false; }
    }

    private async Task ShowLicensesCoreAsync()
    {
        _openFlyout?.Hide();
        var directory = Path.Combine(AppContext.BaseDirectory, "licenses", "third-party");
        var panel = new Grid { Width = Math.Min(760, Math.Max(360, ActualWidth - 160)), Height = 450, ColumnSpacing = 16 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new ListBox
        {
            Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"),
            BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1)
        };
        var text = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"),
            BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1),
            IsSpellCheckEnabled = false
        };
        LumenDialogTheme.ApplyField(text);
        AutomationProperties.SetName(list, T("License files"));
        AutomationProperties.SetName(text, T("License text"));
        ScrollViewer.SetVerticalScrollBarVisibility(text, ScrollBarVisibility.Auto);
        panel.Children.Add(list);
        Grid.SetColumn(text, 1);
        panel.Children.Add(text);
        try
        {
            if (!Directory.Exists(directory)) text.Text = T("No license files were included in this build.");
            else
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetFileName(path).Contains("license", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).Contains("notice", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(path).EndsWith("-OFL.txt", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(path), "OFL.txt", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(path), "ISC.txt", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(path), "README.md", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Path.GetFileName(path), "COPYING", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var label = Path.GetRelativePath(directory, path);
                    var item = new ListBoxItem { Content = LumenUi.Text(label, 12), Tag = path };
                    ToolTipService.SetToolTip(item, label);
                    list.Items.Add(item);
                }
                if (list.Items.Count == 0) text.Text = T("No license files were included in this build.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            text.Text = T("Could not read license files.");
        }
        CancellationTokenSource? pending = null;
        list.SelectionChanged += async (_, _) =>
        {
            pending?.Cancel();
            if (list.SelectedItem is not ListBoxItem { Tag: string path }) return;
            var selected = list.SelectedItem;
            var request = new CancellationTokenSource();
            pending = request;
            try
            {
                var contents = new FileInfo(path).Length > 4 * 1024 * 1024
                    ? T("License file is too large to display.")
                    : await File.ReadAllTextAsync(path, request.Token);
                if (!request.IsCancellationRequested && list.SelectedItem == selected)
                    text.Text = LicenseDisplayText(path, contents);
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                if (!request.IsCancellationRequested && list.SelectedItem == selected) text.Text = T("Could not read this license file.");
            }
            finally
            {
                if (pending == request) pending = null;
                request.Dispose();
            }
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = T("Open source licenses"), CloseButtonText = T("Close"),
            DefaultButton = ContentDialogButton.Close, Content = panel,
            RequestedTheme = LumenTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            Background = LumenTheme.Brush("Pop"), Foreground = LumenTheme.Brush("Ink")
        };
        LumenDialogTheme.Apply(dialog);
        dialog.Resources["ContentDialogMaxWidth"] = panel.Width + 48;
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        try { await dialog.ShowAsync(); }
        finally { pending?.Cancel(); }
    }

    private static string LicenseDisplayText(string path, string contents)
    {
        if (!string.Equals(Path.GetExtension(path), ".rtf", StringComparison.OrdinalIgnoreCase)) return contents;
        try
        {
            var parser = new RichEditBox();
            parser.Document.SetText(Microsoft.UI.Text.TextSetOptions.FormatRtf, contents);
            parser.Document.GetText(Microsoft.UI.Text.TextGetOptions.None, out var plainText);
            return plainText;
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            return contents;
        }
    }

    private static void Animate(DependencyObject target, string property, double value, int milliseconds, bool spring = false)
    {
        if (!new UISettings().AnimationsEnabled)
        {
            if (target is TranslateTransform translate) translate.X = value;
            else if (target is RotateTransform rotate) rotate.Angle = value;
            else if (target is UIElement element && property == "Opacity") element.Opacity = value;
            return;
        }
        var animation = new DoubleAnimation
        {
            To = value, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)), EnableDependentAnimation = true,
            EasingFunction = spring ? new BackEase { Amplitude = .35, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private static void RegisterSettingsText() => LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Turning HDR off requests server conversion to SDR for an active HDR10 video; Windows HDR is not changed."] = "\u5173\u95ed HDR \u4f1a\u8bf7\u6c42\u670d\u52a1\u5668\u5c06\u6b63\u5728\u64ad\u653e\u7684 HDR10 \u89c6\u9891\u8f6c\u4e3a SDR\uff1bWindows HDR \u8bbe\u7f6e\u4fdd\u6301\u4e0d\u53d8\u3002",
        ["Applies the next time a video opens. Request HDR output for compatible HDR10 sources while Windows HDR is on; other HDR formats use server conversion."] = "\u4e0b\u6b21\u6253\u5f00\u89c6\u9891\u65f6\u751f\u6548\uff1aWindows HDR \u5df2\u5f00\u542f\u65f6\uff0c\u4e3a\u7b26\u5408\u6761\u4ef6\u7684 HDR10 \u7247\u6e90\u8bf7\u6c42 HDR \u8f93\u51fa\uff1b\u5176\u4ed6 HDR \u683c\u5f0f\u4ecd\u7531\u670d\u52a1\u5668\u8f6c\u7801\u3002",
        ["Applies the next time a video opens. Follow Windows HDR for compatible HDR10 sources; other HDR formats use server conversion."] = "\u4e0b\u6b21\u6253\u5f00\u89c6\u9891\u65f6\u751f\u6548\uff1a\u7b26\u5408\u6761\u4ef6\u7684 HDR10 \u7247\u6e90\u8ddf\u968f Windows HDR \u8bbe\u7f6e\uff1b\u5176\u4ed6 HDR \u683c\u5f0f\u4ecd\u7531\u670d\u52a1\u5668\u8f6c\u7801\u3002",
        ["Applies the next time a video opens. {0} HDR sources require server conversion to SDR."] = "\u4e0b\u6b21\u6253\u5f00\u89c6\u9891\u65f6\u751f\u6548\uff1a{0} HDR \u7247\u6e90\u9700\u7531\u670d\u52a1\u5668\u8f6c\u4e3a SDR\u3002",
        ["Reading HDR support for the current display."] = "\u6b63\u5728\u8bfb\u53d6\u5f53\u524d\u663e\u793a\u5668\u7684 HDR \u652f\u6301\u3002",
        ["HDR support for the current display could not be read."] = "\u65e0\u6cd5\u8bfb\u53d6\u5f53\u524d\u663e\u793a\u5668\u7684 HDR \u652f\u6301\u3002",
        ["HDR output is unavailable with the current playback setup."] = "\u5f53\u524d\u64ad\u653e\u914d\u7f6e\u65e0\u6cd5\u4f7f\u7528 HDR \u8f93\u51fa\u3002",
        ["Windows HDR is off. Enable HDR in Windows settings to use Auto or Always."] = "Windows HDR \u5df2\u5173\u95ed\u3002\u8bf7\u5148\u5728 Windows \u8bbe\u7f6e\u4e2d\u542f\u7528 HDR\uff0c\u518d\u4f7f\u7528\u81ea\u52a8\u6216\u59cb\u7ec8\u3002",
        ["The current display mode does not support HDR10 output."] = "\u5f53\u524d\u663e\u793a\u5668\u7684\u663e\u793a\u6a21\u5f0f\u4e0d\u652f\u6301 HDR10 \u8f93\u51fa\u3002",
        ["Match the video frame rate during full-screen playback when the display supports it"] = "\u5168\u5c4f\u64ad\u653e\u65f6\u5339\u914d\u5f71\u7247\u5e27\u7387\uff0c\u9700\u8981\u517c\u5bb9\u7684\u663e\u793a\u6a21\u5f0f",
        ["Display refresh-rate matching is unavailable in this app window."] = "\u5f53\u524d\u5e94\u7528\u7a97\u53e3\u6682\u65e0\u6cd5\u5339\u914d\u663e\u793a\u5668\u5237\u65b0\u7387\u3002",
        ["Display refresh-rate matching is unavailable on this system."] = "\u5f53\u524d\u7cfb\u7edf\u4e0d\u652f\u6301\u5339\u914d\u663e\u793a\u5668\u5237\u65b0\u7387\u3002",
        ["Matched: {0} Hz \u00b7 nominal video {1:0.###} fps"] = "\u5df2\u5339\u914d\uff1a{0} Hz \u00b7 \u5f71\u7247\u6807\u79f0\u5e27\u7387 {1:0.###} fps",
        ["Matched display refresh rate: {0} Hz"] = "\u663e\u793a\u5668\u5237\u65b0\u7387\u5df2\u5339\u914d\u4e3a {0} Hz",
        ["Waiting for the video frame rate before matching the display."] = "\u7b49\u5f85\u5f71\u7247\u5e27\u7387\u540e\u5339\u914d\u663e\u793a\u5668\u5237\u65b0\u7387\u3002",
        ["The current display mode could not be read."] = "\u65e0\u6cd5\u8bfb\u53d6\u5f53\u524d\u663e\u793a\u5668\u7684\u663e\u793a\u6a21\u5f0f\u3002",
        ["This display has no compatible mode for the video frame rate."] = "\u6b64\u663e\u793a\u5668\u6ca1\u6709\u4e0e\u5f71\u7247\u5e27\u7387\u517c\u5bb9\u7684\u663e\u793a\u6a21\u5f0f\u3002",
        ["The display rejected the requested refresh rate."] = "\u663e\u793a\u5668\u62d2\u7edd\u4e86\u8bf7\u6c42\u7684\u5237\u65b0\u7387\u3002",
        ["The changed display mode could not be confirmed."] = "\u65e0\u6cd5\u786e\u8ba4\u5207\u6362\u540e\u7684\u663e\u793a\u6a21\u5f0f\u3002",
        ["Windows could not change the display refresh rate."] = "Windows \u65e0\u6cd5\u5207\u6362\u663e\u793a\u5668\u5237\u65b0\u7387\u3002",
        ["The display mode was changed outside Lumen; that mode will be kept."] = "\u663e\u793a\u6a21\u5f0f\u5df2\u5728 Lumen \u4e4b\u5916\u66f4\u6539\uff0c\u5c06\u4fdd\u7559\u8be5\u6a21\u5f0f\u3002",
        ["Windows could not restore the previous display mode yet."] = "Windows \u5c1a\u672a\u6062\u590d\u4e4b\u524d\u7684\u663e\u793a\u6a21\u5f0f\u3002",
        ["Open folder"] = "\u6253\u5f00\u6587\u4ef6\u5939",
        ["Open diagnostic logs folder"] = "\u6253\u5f00\u8bca\u65ad\u65e5\u5fd7\u6587\u4ef6\u5939",
        ["Include these logs when reporting playback problems"] = "\u53cd\u9988\u64ad\u653e\u95ee\u9898\u65f6\u9644\u4e0a",
        ["Clear image cache"] = "\u6e05\u9664\u56fe\u7247\u7f13\u5b58",
        ["Retry startup registration"] = "\u91cd\u8bd5\u5f00\u673a\u542f\u52a8\u8bbe\u7f6e",
        ["Cancel update download"] = "\u53d6\u6d88\u66f4\u65b0\u4e0b\u8f7d",
        ["Notify when a followed or favorite series has a new episode (only while Lumen is running and connected to the server)"] = "\u5728\u8ffd\u6216\u6536\u85cf\u7684\u5267\u96c6\u66f4\u65b0\u65f6\u53d1\u9001 Windows \u901a\u77e5\uff08\u4ec5\u5728 Lumen \u8fd0\u884c\u5e76\u8fde\u63a5\u670d\u52a1\u5668\u65f6\uff09",
        ["Amber"] = "\u7425\u73c0",
        ["Posters and stills \u00b7 {0:0.0} / {1:0} MB retained for this session \u00b7 {2} images"] = "\u6d77\u62a5\u4e0e\u5267\u7167 \u00b7 \u5f53\u524d\u4f1a\u8bdd\u5df2\u7528 {0:0.0} / {1:0} MB \u00b7 {2} \u5f20\u56fe\u7247",
        ["Start the countdown when the next episode card appears"] = "\u7247\u5c3e\u51fa\u73b0\u201c\u4e0b\u4e00\u96c6\u201d\u5361\u7247\u65f6\u5f00\u59cb\u5012\u8ba1\u65f6",
        ["Prefer direct play when the format and bitrate limits allow it"] = "\u683c\u5f0f\u548c\u7801\u7387\u4e0a\u9650\u5141\u8bb8\u65f6\u4f18\u5148\u76f4\u63a5\u64ad\u653e",
        ["Prefer this language when multiple subtitle tracks are available"] = "\u6709\u591a\u6761\u5b57\u5e55\u65f6\u4f18\u5148\u9009\u62e9",
        ["When subtitles are shown automatically"] = "\u4f55\u65f6\u81ea\u52a8\u663e\u793a\u5b57\u5e55",
        ["Current server setting: default subtitles"] = "\u5f53\u524d\u670d\u52a1\u5668\u8bbe\u7f6e\uff1a\u9ed8\u8ba4\u5b57\u5e55",
        ["Current server setting: hearing-impaired subtitles"] = "\u5f53\u524d\u670d\u52a1\u5668\u8bbe\u7f6e\uff1a\u542c\u969c\u5b57\u5e55",
        ["Client text subtitle styling is unavailable on this Windows installation."] = "\u5f53\u524d Windows \u73af\u5883\u4e0d\u652f\u6301\u5ba2\u6237\u7aef\u6587\u672c\u5b57\u5e55\u6837\u5f0f\u8c03\u6574\u3002",
        ["Text subtitle size; picture and server-burned subtitles are unchanged."] = "\u6587\u672c\u5b57\u5e55\u7684\u5927\u5c0f\uff1b\u56fe\u5f62\u5b57\u5e55\u548c\u670d\u52a1\u5668\u70e7\u5f55\u7684\u5b57\u5e55\u4fdd\u6301\u539f\u6837",
        ["Keep text subtitles legible against bright scenes"] = "\u8ba9\u6587\u672c\u5b57\u5e55\u5728\u660e\u4eae\u753b\u9762\u4e0a\u4fdd\u6301\u6e05\u6670",
        ["Where text subtitles appear"] = "\u5b57\u5e55\u663e\u793a\u7684\u4f4d\u7f6e",
        ["Rotate featured artwork every 8 seconds"] = "\u9996\u9875\u5927\u56fe\u6bcf 8 \u79d2\u81ea\u52a8\u5207\u6362",
        ["Decoding and output"] = "\u89e3\u7801\u4e0e\u8f93\u51fa",
        ["Video decoding"] = "\u89c6\u9891\u89e3\u7801",
        ["Applies to the next video. Unavailable decoder APIs or unsupported streams fall back to software."] = "\u4e0b\u4e00\u4e2a\u89c6\u9891\u751f\u6548\u3002\u89e3\u7801\u63a5\u53e3\u4e0d\u53ef\u7528\u6216\u89c6\u9891\u6d41\u4e0d\u652f\u6301\u65f6\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["Audio passthrough"] = "\u97f3\u9891\u76f4\u901a",
        ["All supported formats"] = "\u6240\u6709\u652f\u6301\u683c\u5f0f",
        ["AC3, EAC3, DTS core"] = "AC3\u3001EAC3\u3001DTS \u6838\u5fc3",
        ["AC3, EAC3, DTS core, TrueHD, DTS-HD"] = "AC3\u3001EAC3\u3001DTS \u6838\u5fc3\u3001TrueHD\u3001DTS-HD",
        ["The native audio passthrough interface is unavailable. Audio uses normal PCM output."] = "\u539f\u751f\u97f3\u9891\u76f4\u901a\u63a5\u53e3\u4e0d\u53ef\u7528\uff0c\u97f3\u9891\u4f7f\u7528\u666e\u901a PCM \u8f93\u51fa\u3002",
        ["Decode audio to PCM with normal volume, mute, and playback speed controls."] = "\u5c06\u97f3\u9891\u89e3\u7801\u4e3a PCM\uff0c\u652f\u6301\u666e\u901a\u97f3\u91cf\u3001\u9759\u97f3\u4e0e\u64ad\u653e\u500d\u901f\u63a7\u5236\u3002",
        ["Applies to the next video. Request {0} through exclusive WASAPI when the output device supports the exact format; otherwise use PCM. Changing volume, mute, or playback speed switches to PCM."] = "\u4e0b\u6b21\u6253\u5f00\u89c6\u9891\u65f6\u751f\u6548\uff1a\u8f93\u51fa\u8bbe\u5907\u652f\u6301\u5b8c\u6574\u683c\u5f0f\u65f6\uff0c\u901a\u8fc7 WASAPI \u72ec\u5360\u6a21\u5f0f\u8bf7\u6c42 {0} \u76f4\u901a\uff1b\u5426\u5219\u4f7f\u7528 PCM\u3002\u8c03\u6574\u97f3\u91cf\u3001\u9759\u97f3\u6216\u64ad\u653e\u500d\u901f\u4f1a\u5207\u6362\u4e3a PCM\u3002",
        ["Dolby Digital / DTS"] = "\u675c\u6bd4\u6570\u5b57 / DTS",
        ["All formats"] = "\u5168\u90e8\u683c\u5f0f",
        ["Display refresh-rate switching is not implemented by the playback engine yet."] = "\u64ad\u653e\u5185\u6838\u5c1a\u672a\u5b9e\u73b0\u663e\u793a\u5668\u5237\u65b0\u7387\u5207\u6362\uff0c\u6b64\u8bbe\u7f6e\u6682\u4e0d\u53ef\u7528\u3002",
        ["Notify when this server adds a new episode (only while Lumen is running and connected to the server)"] = "\u5f53\u524d\u670d\u52a1\u5668\u65b0\u589e\u5267\u96c6\u65f6\u901a\u77e5\uff08\u4ec5\u5728 Lumen \u8fd0\u884c\u5e76\u8fde\u63a5\u670d\u52a1\u5668\u65f6\uff09",
        ["Server version"] = "\u670d\u52a1\u5668\u7248\u672c",
        ["Application"] = "\u5e94\u7528",
        ["Intel Quick Sync"] = "Intel Quick Sync",
        ["Expanded"] = "\u5df2\u5c55\u5f00",
        ["Collapsed"] = "\u5df2\u6536\u8d77",
        ["Intro"] = "\u7247\u5934",
        ["Show skip intro button"] = "\u663e\u793a\u201c\u8df3\u8fc7\u7247\u5934\u201d\u6309\u94ae",
        ["Clear cache"] = "\u6e05\u9664\u7f13\u5b58",
        ["Media keys, lock-screen controls, and taskbar playback buttons"] = "\u5a92\u4f53\u952e\u3001\u9501\u5c4f\u4e0e\u4efb\u52a1\u680f\u7f29\u7565\u56fe\u6309\u94ae",
        ["System"] = "\u7cfb\u7edf",
        ["Quality and bitrate"] = "\u753b\u8d28\u4e0e\u7801\u7387",
        ["Follow system"] = "\u8ddf\u968f\u7cfb\u7edf",
        ["Follow the Windows light or dark appearance"] = "\u8ddf\u968f Windows \u7684\u6df1\u8272 / \u6d45\u8272\u8bbe\u7f6e",
        ["Color for progress, selection, and focused content"] = "\u8fdb\u5ea6\u6761\u3001\u9009\u4e2d\u72b6\u6001\u4e0e\u7126\u70b9\u73af\u7684\u989c\u8272",
        ["Rotate featured artwork every 7 seconds"] = "\u9996\u9875\u5927\u56fe\u6bcf 7 \u79d2\u81ea\u52a8\u5207\u6362",
        ["Home scroll mode"] = "\u9996\u9875\u6eda\u52a8\u65b9\u5f0f",
        ["Move by full pages or scroll freely through the home screen"] = "\u6574\u5c4f\u7ffb\u9875\uff1a\u6eda\u8f6e\u4e00\u6b21\u7ffb\u8fc7\u4e00\u5c4f\uff1b\u8fde\u7eed\u6eda\u52a8\uff1a\u50cf\u7f51\u9875\u4e00\u6837\u81ea\u7531\u6eda\u52a8",
        ["Full-page scrolling"] = "\u6574\u5c4f\u7ffb\u9875",
        ["Continuous scrolling"] = "\u8fde\u7eed\u6eda\u52a8",
        ["Also available from the slider above each library"] = "\u4e5f\u53ef\u4ee5\u7528\u6d77\u62a5\u5899\u53f3\u4e0a\u89d2\u7684\u6ed1\u5757\u8c03\u6574",
        ["Mark completed titles at the top right of each poster"] = "\u5728\u6d77\u62a5\u53f3\u4e0a\u89d2\u6807\u8bb0\u5df2\u770b\u5b8c\u7684\u5f71\u7247",
        ["Decoding and display"] = "\u89e3\u7801\u4e0e\u663e\u793a",
        ["Prevent sleep during playback"] = "\u64ad\u653e\u65f6\u963b\u6b62\u4f11\u7720",
        ["Keep the screen awake while a video is playing"] = "\u89c6\u9891\u64ad\u653e\u65f6\u4fdd\u6301\u5c4f\u5e55\u5e38\u4eae",
        ["Enabled automatically while a video is playing"] = "\u5df2\u81ea\u52a8\u542f\u7528\uff0c\u89c6\u9891\u64ad\u653e\u671f\u95f4\u4fdd\u6301\u5c4f\u5e55\u5e38\u4eae",
        ["Window and system integration"] = "\u7a97\u53e3\u4e0e\u7cfb\u7edf\u96c6\u6210",
        ["Launch at startup"] = "\u5f00\u673a\u65f6\u542f\u52a8",
        ["Start Lumen when you sign in to Windows"] = "\u767b\u5f55 Windows \u65f6\u81ea\u52a8\u542f\u52a8 Lumen",
        ["Changing startup registration"] = "\u6b63\u5728\u66f4\u6539\u5f00\u673a\u542f\u52a8\u8bbe\u7f6e",
        ["Startup registration is unavailable for this launch."] = "\u5f53\u524d\u542f\u52a8\u65b9\u5f0f\u4e0d\u652f\u6301\u5f00\u673a\u542f\u52a8\u3002\u8bf7\u4ece\u5b89\u88c5\u76ee\u5f55\u7684\u5e94\u7528\u7a0b\u5e8f\u542f\u52a8\u3002",
        ["Could not read startup registration. Try again."] = "\u65e0\u6cd5\u8bfb\u53d6\u5f00\u673a\u542f\u52a8\u8bbe\u7f6e\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Could not change startup registration. Try again."] = "\u65e0\u6cd5\u66f4\u6539\u5f00\u673a\u542f\u52a8\u8bbe\u7f6e\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Startup registration belongs to a different installation."] = "\u5f00\u673a\u542f\u52a8\u9879\u6307\u5411\u53e6\u4e00\u4e2a\u5b89\u88c5\u76ee\u5f55\uff0c\u8bf7\u5148\u5728\u65e7\u7248\u5e94\u7528\u4e2d\u5173\u95ed\u5f00\u673a\u542f\u52a8\u3002",
        ["Retry"] = "\u91cd\u8bd5",
        ["Startup registration is not available in this build."] = "\u6b64\u7248\u672c\u6682\u4e0d\u652f\u6301\u5f00\u673a\u542f\u52a8",
        ["Remember window size and position"] = "\u8bb0\u4f4f\u7a97\u53e3\u5927\u5c0f\u548c\u4f4d\u7f6e",
        ["Restore the saved window size and position when Lumen opens"] = "\u4e0b\u6b21\u6253\u5f00 Lumen \u65f6\u6062\u590d\u5df2\u4fdd\u5b58\u7684\u7a97\u53e3\u5927\u5c0f\u4e0e\u4f4d\u7f6e",
        ["Window size and position are saved automatically"] = "\u5df2\u81ea\u52a8\u4fdd\u5b58\u7a97\u53e3\u5927\u5c0f\u548c\u4f4d\u7f6e",
        ["System media controls"] = "\u7cfb\u7edf\u5a92\u4f53\u63a7\u5236",
        ["Media keys and Windows playback controls are connected automatically"] = "\u5df2\u81ea\u52a8\u8fde\u63a5\u5a92\u4f53\u952e\u4e0e Windows \u64ad\u653e\u63a7\u4ef6",
        ["New episode notifications"] = "\u65b0\u5267\u96c6\u901a\u77e5",
        ["Show a notification when this server adds a new episode"] = "\u670d\u52a1\u5668\u6dfb\u52a0\u65b0\u5267\u96c6\u65f6\u663e\u793a\u901a\u77e5",
        ["Automatically reconnect"] = "\u81ea\u52a8\u91cd\u8fde",
        ["Reconnect to this server after a temporary connection failure"] = "\u8fde\u63a5\u6682\u65f6\u4e2d\u65ad\u540e\u81ea\u52a8\u91cd\u8fde\u670d\u52a1\u5668",
        ["Server update notifications are not connected yet."] = "\u5c1a\u672a\u63a5\u5165\u670d\u52a1\u5668\u7684\u5267\u96c6\u66f4\u65b0\u901a\u77e5",
        ["Storage"] = "\u5b58\u50a8",
        ["Image cache"] = "\u56fe\u7247\u7f13\u5b58",
        ["Image cache limit"] = "\u56fe\u7247\u7f13\u5b58\u4e0a\u9650",
        ["Reducing the limit immediately removes older cached artwork"] = "\u51cf\u5c0f\u4e0a\u9650\u4f1a\u7acb\u5373\u79fb\u9664\u6700\u8fd1\u672a\u4f7f\u7528\u7684\u7f13\u5b58\u56fe\u7247",
        ["Do not retain images"] = "\u4e0d\u4fdd\u7559\u56fe\u7247\u7f13\u5b58",
        ["Image retention is off. Artwork still loads normally."] = "\u5df2\u5173\u95ed\u56fe\u7247\u7f13\u5b58\uff0c\u6d77\u62a5\u4ecd\u4f1a\u6b63\u5e38\u52a0\u8f7d\u3002",
        ["Artwork retained: {0:0.0} / {1:0} MB \u00b7 {2} images"] = "\u5df2\u7f13\u5b58\uff1a{0:0.0} / {1:0} MB \u00b7 {2} \u5f20\u56fe\u7247",
        ["Artwork is cached in memory for the current session"] = "\u6d77\u62a5\u4e0e\u5267\u7167\u7f13\u5b58\u5728\u5f53\u524d\u4f1a\u8bdd\u7684\u5185\u5b58\u4e2d",
        ["Up to 32 MB per image cache"] = "\u6bcf\u4e2a\u56fe\u7247\u7f13\u5b58\u4e0a\u9650 32 MB",
        ["Diagnostic logs"] = "\u8bca\u65ad\u65e5\u5fd7",
        ["View and export details for the current playback session"] = "\u67e5\u770b\u5e76\u5bfc\u51fa\u5f53\u524d\u64ad\u653e\u4f1a\u8bdd\u7684\u8bca\u65ad\u4fe1\u606f",
        ["Switch server"] = "\u5207\u6362\u670d\u52a1\u5668",
        ["Return to the connection page to choose or add a server"] = "\u56de\u5230\u8fde\u63a5\u9875\uff0c\u9009\u62e9\u6216\u6dfb\u52a0\u5176\u4ed6\u670d\u52a1\u5668",
        ["Sign in to a different account on this server"] = "\u5728\u540c\u4e00\u670d\u52a1\u5668\u4e0a\u767b\u5f55\u5176\u4ed6\u8d26\u6237",
        ["Saved accounts"] = "\u5df2\u4fdd\u5b58\u7684\u8d26\u6237",
        ["No saved credentials"] = "\u672a\u4fdd\u5b58\u51ed\u636e",
        ["{0} accounts \u00b7 encrypted with DPAPI"] = "{0} \u4e2a \u00b7 \u51ed\u636e\u7ecf DPAPI \u52a0\u5bc6",
        ["Keyboard shortcuts"] = "\u952e\u76d8\u5feb\u6377\u952e",
        ["Press ? on any page to open the shortcut guide"] = "\u5728\u4efb\u4f55\u9875\u9762\u6309 ? \u4e5f\u80fd\u6253\u5f00",
        ["Automatic (recommended)"] = "\u81ea\u52a8\uff08\u63a8\u8350\uff09",
        ["Settings"] = "\u8bbe\u7f6e",
        ["Playback"] = "\u64ad\u653e",
        ["Subtitles"] = "\u5b57\u5e55",
        ["Appearance"] = "\u5916\u89c2",
        ["Account"] = "\u8d26\u6237",
        ["About"] = "\u5173\u4e8e",
        ["Sign out"] = "\u9000\u51fa\u767b\u5f55",
        ["Sign out action"] = "\u9000\u51fa",
        ["Sign out?"] = "\u9000\u51fa\u767b\u5f55\uff1f",
        ["Cancel"] = "\u53d6\u6d88",
        ["Close"] = "\u5173\u95ed",
        ["Disconnect from {0}? Your server-saved playback progress will remain."] = "\u5c06\u65ad\u5f00\u4e0e\u201c{0}\u201d\u7684\u8fde\u63a5\uff0c\u670d\u52a1\u5668\u4fdd\u5b58\u7684\u64ad\u653e\u8fdb\u5ea6\u4e0d\u4f1a\u4e22\u5931\u3002",
        ["Selected"] = "\u5df2\u9009\u4e2d",
        ["Not selected"] = "\u672a\u9009\u4e2d",
        ["Playback behavior"] = "\u64ad\u653e\u884c\u4e3a",
        ["Auto-play next episode"] = "\u81ea\u52a8\u64ad\u653e\u4e0b\u4e00\u96c6",
        ["Start the next episode when the current one ends"] = "\u5f53\u524d\u4e00\u96c6\u7ed3\u675f\u540e\u81ea\u52a8\u5f00\u59cb\u4e0b\u4e00\u96c6",
        ["Next episode countdown"] = "\u4e0b\u4e00\u96c6\u5012\u8ba1\u65f6",
        ["Show a prompt when end credits begin"] = "\u7247\u5c3e\u5b57\u5e55\u51fa\u73b0\u65f6\u663e\u793a\u63d0\u793a",
        ["Countdown off"] = "\u5173",
        ["5 seconds"] = "5 \u79d2",
        ["10 seconds"] = "10 \u79d2",
        ["15 seconds"] = "15 \u79d2",
        ["Intro and credits"] = "\u7247\u5934\u4e0e\u7247\u5c3e",
        ["Use chapter markers supplied by the server"] = "\u4f7f\u7528\u670d\u52a1\u5668\u7684\u7ae0\u8282\u6807\u8bb0\u8bc6\u522b",
        ["Show skip button"] = "\u663e\u793a\u201c\u8df3\u8fc7\u201d\u6309\u94ae",
        ["Skip automatically"] = "\u81ea\u52a8\u8df3\u8fc7",
        ["Do not skip"] = "\u4e0d\u5904\u7406",
        ["Resume playback"] = "\u7ee7\u7eed\u64ad\u653e",
        ["When opening a partially watched video"] = "\u6253\u5f00\u770b\u8fc7\u4e00\u534a\u7684\u5f71\u7247\u65f6",
        ["Continue from last position"] = "\u4ece\u4e0a\u6b21\u4f4d\u7f6e\u7ee7\u7eed",
        ["Ask every time"] = "\u6bcf\u6b21\u8be2\u95ee",
        ["Play from beginning"] = "\u4ece\u5934\u64ad\u653e",
        ["Quality and decoding"] = "\u753b\u8d28\u4e0e\u89e3\u7801",
        ["Prefer direct play"] = "\u4f18\u5148\u76f4\u63a5\u64ad\u653e",
        ["Transcode only when the format is unsupported"] = "\u4ec5\u5728\u683c\u5f0f\u4e0d\u53d7\u652f\u6301\u65f6\u7531\u670d\u52a1\u5668\u8f6c\u7801",
        ["Local network maximum bitrate"] = "\u5c40\u57df\u7f51\u6700\u5927\u7801\u7387",
        ["When connecting to a server on your local network"] = "\u8fde\u63a5\u5bb6\u5ead\u7f51\u7edc\u4e2d\u7684\u670d\u52a1\u5668\u65f6",
        ["Internet maximum bitrate"] = "\u4e92\u8054\u7f51\u6700\u5927\u7801\u7387",
        ["When connecting outside your local network"] = "\u901a\u8fc7\u4e92\u8054\u7f51\u8fde\u63a5\u670d\u52a1\u5668\u65f6",
        ["Unlimited"] = "\u4e0d\u9650\u5236",
        ["Video decoder API"] = "\u89c6\u9891\u89e3\u7801 API",
        ["Applies to the next video. Unavailable APIs or unsupported streams fall back to software."] = "\u4e0b\u4e00\u4e2a\u89c6\u9891\u751f\u6548\u3002API \u4e0d\u53ef\u7528\u6216\u89c6\u9891\u6d41\u4e0d\u652f\u6301\u65f6\u56de\u9000\u8f6f\u4ef6\u89e3\u7801\u3002",
        ["Automatic (D3D11VA)"] = "\u81ea\u52a8\uff08D3D11VA\uff09",
        ["D3D11VA"] = "D3D11VA",
        ["Intel VPL / QSV"] = "Intel VPL / QSV",
        ["AMD AMF"] = "AMD AMF",
        ["NVIDIA NVDEC"] = "NVIDIA NVDEC",
        ["Software decoding"] = "\u8f6f\u4ef6\u89e3\u7801",
        ["HDR output"] = "HDR \u8f93\u51fa",
        ["Auto"] = "\u81ea\u52a8",
        ["Always"] = "\u59cb\u7ec8",
        ["Match content refresh rate"] = "\u5339\u914d\u5185\u5bb9\u5237\u65b0\u7387",
        ["This player does not change the display refresh rate."] = "\u5f53\u524d\u64ad\u653e\u5668\u4e0d\u652f\u6301\u5207\u6362\u663e\u793a\u5668\u5237\u65b0\u7387\u3002",
        ["Default tracks"] = "\u9ed8\u8ba4\u8f68\u9053",
        ["Preferred subtitle language"] = "\u9996\u9009\u5b57\u5e55\u8bed\u8a00",
        ["Select a default language for subtitles"] = "\u9009\u62e9\u9ed8\u8ba4\u5b57\u5e55\u8bed\u8a00",
        ["Simplified Chinese"] = "\u7b80\u4f53\u4e2d\u6587",
        ["Traditional Chinese"] = "\u7e41\u9ad4\u4e2d\u6587",
        ["English"] = "English",
        ["Follow audio language"] = "\u8ddf\u968f\u97f3\u8f68\u8bed\u8a00",
        ["Subtitle mode"] = "\u5b57\u5e55\u6a21\u5f0f",
        ["Server default"] = "\u670d\u52a1\u5668\u9ed8\u8ba4",
        ["Language ({0})"] = "\u8bed\u8a00\uff08{0}\uff09",
        ["Use the preferred language and server playback rules"] = "\u4f7f\u7528\u9996\u9009\u8bed\u8a00\u4e0e\u670d\u52a1\u5668\u64ad\u653e\u89c4\u5219",
        ["Smart"] = "\u667a\u80fd",
        ["Always show"] = "\u603b\u662f",
        ["Forced only"] = "\u4ec5\u5f3a\u5236",
        ["Hearing impaired"] = "\u542c\u529b\u8f85\u52a9",
        ["Subtitle style"] = "\u6837\u5f0f",
        ["Font size"] = "\u5b57\u53f7",
        ["Small"] = "\u5c0f",
        ["Medium"] = "\u4e2d",
        ["Large"] = "\u5927",
        ["Text outline"] = "\u6587\u5b57\u63cf\u8fb9",
        ["Subtitle position"] = "\u4f4d\u7f6e",
        ["Bottom of picture"] = "\u753b\u9762\u5e95\u90e8",
        ["In black bars"] = "\u9ed1\u8fb9\u5185",
        ["Applies to external text subtitles only; server-rendered subtitles cannot be styled."] = "\u4ec5\u9002\u7528\u4e8e\u5916\u6302\u6587\u672c\u5b57\u5e55\uff0c\u670d\u52a1\u5668\u70e7\u5f55\u5b57\u5e55\u65e0\u6cd5\u8c03\u6574\u6837\u5f0f\u3002",
        ["Subtitle appearance is rendered by the server and cannot be changed by this player."] = "\u5b57\u5e55\u6837\u5f0f\u7531\u670d\u52a1\u5668\u70e7\u5f55\u751f\u6210\uff0c\u5f53\u524d\u64ad\u653e\u5668\u65e0\u6cd5\u8c03\u6574\u3002",
        ["Interface"] = "\u754c\u9762",
        ["Theme"] = "\u5916\u89c2\u6a21\u5f0f",
        ["Dark"] = "\u6df1\u8272",
        ["Light"] = "\u6d45\u8272",
        ["Accent color"] = "\u5f3a\u8c03\u8272",
        ["Gold"] = "\u9999\u69df\u91d1",
        ["Coral"] = "\u73ca\u745a",
        ["Sage"] = "\u9f20\u5c3e\u8349",
        ["Mist blue"] = "\u96fe\u84dd",
        ["Home carousel"] = "\u9996\u9875\u8f6e\u64ad",
        ["Poster wall"] = "\u6d77\u62a5\u5899",
        ["Poster size"] = "\u6d77\u62a5\u5c3a\u5bf8",
        ["Show watched marks"] = "\u663e\u793a\u5df2\u770b\u6807\u8bb0",
        ["Server"] = "\u670d\u52a1\u5668",
        ["Connection"] = "\u8fde\u63a5\u65b9\u5f0f",
        ["Current user"] = "\u5f53\u524d\u7528\u6237",
        ["Switch user"] = "\u5207\u6362\u7528\u6237",
        ["Switch"] = "\u5207\u6362",
        ["Secure HTTPS"] = "HTTPS \u52a0\u5bc6\u8fde\u63a5",
        ["HTTP"] = "HTTP",
        ["Application version"] = "\u7248\u672c",
        ["Development build"] = "\u5f00\u53d1\u6784\u5efa",
        ["Check updates"] = "\u68c0\u67e5\u66f4\u65b0",
        ["Check"] = "\u68c0\u67e5",
        ["Checking"] = "\u6b63\u5728\u68c0\u67e5",
        ["Checking for updates"] = "\u6b63\u5728\u68c0\u67e5\u66f4\u65b0",
        ["Update available: {0}"] = "\u53ef\u66f4\u65b0\u81f3\uff1a{0}",
        ["Update package"] = "\u66f4\u65b0\u5305",
        ["Download update"] = "\u4e0b\u8f7d\u66f4\u65b0",
        ["Install and restart"] = "\u5b89\u88c5\u5e76\u91cd\u542f",
        ["Install update {0}?"] = "\u5b89\u88c5\u66f4\u65b0 {0}\uff1f",
        ["Lumen will close and open the updated version. The Start menu entry {0} will launch the new version. Your previous application folder remains available."] = "Lumen \u5c06\u5173\u95ed\u5e76\u6253\u5f00\u65b0\u7248\u5e94\u7528\u3002\u5f00\u59cb\u83dc\u5355\u4e2d\u7684 {0} \u4f1a\u542f\u52a8\u65b0\u7248\u672c\uff0c\u65e7\u7248\u5e94\u7528\u76ee\u5f55\u4ecd\u4f1a\u4fdd\u7559\u3002",
        ["Installation folder: {0}"] = "\u5b89\u88c5\u76ee\u5f55\uff1a{0}",
        ["View release"] = "\u67e5\u770b\u53d1\u5e03\u9875",
        ["Update download progress"] = "\u66f4\u65b0\u4e0b\u8f7d\u8fdb\u5ea6",
        ["Check for a published release to see available downloads"] = "\u68c0\u67e5\u516c\u5f00\u53d1\u5e03\u7684\u7248\u672c\u540e\u53ef\u67e5\u770b\u4e0b\u8f7d\u9009\u9879",
        ["You are using the latest published version"] = "\u5f53\u524d\u5df2\u662f\u6700\u65b0\u516c\u5f00\u53d1\u5e03\u7248\u672c",
        ["This release has no compatible x64 update package."] = "\u8be5\u7248\u672c\u5c1a\u672a\u63d0\u4f9b\u517c\u5bb9\u7684 x64 \u66f4\u65b0\u5305\u3002",
        ["Download {0} ({1:0.0} MB), then choose Install and restart"] = "\u4e0b\u8f7d {0}\uff08{1:0.0} MB\uff09\u540e\u70b9\u51fb\u201c\u5b89\u88c5\u5e76\u91cd\u542f\u201d",
        ["Update {0} is downloaded and ready to install"] = "\u66f4\u65b0 {0} \u5df2\u4e0b\u8f7d\uff0c\u53ef\u5f00\u59cb\u5b89\u88c5",
        ["Downloading update"] = "\u6b63\u5728\u4e0b\u8f7d\u66f4\u65b0",
        ["Downloading update: {0:0}%"] = "\u6b63\u5728\u4e0b\u8f7d\u66f4\u65b0\uff1a{0:0}%",
        ["Downloading update: {0:0.0} MB"] = "\u6b63\u5728\u4e0b\u8f7d\u66f4\u65b0\uff1a{0:0.0} MB",
        ["Verifying update package"] = "\u6b63\u5728\u6821\u9a8c\u66f4\u65b0\u5305",
        ["Preparing update package"] = "\u6b63\u5728\u51c6\u5907\u66f4\u65b0\u5305",
        ["Update package is ready"] = "\u66f4\u65b0\u5305\u5df2\u5c31\u7eea",
        ["Update {0} is ready. Choose Install and restart to apply it."] = "\u66f4\u65b0 {0} \u5df2\u5c31\u7eea\u3002\u70b9\u51fb\u201c\u5b89\u88c5\u5e76\u91cd\u542f\u201d\u5373\u53ef\u5e94\u7528\u3002",
        ["Update download canceled. You can try again."] = "\u66f4\u65b0\u4e0b\u8f7d\u5df2\u53d6\u6d88\uff0c\u53ef\u4ee5\u518d\u6b21\u4e0b\u8f7d\u3002",
        ["The update package failed its integrity check. Download it again."] = "\u66f4\u65b0\u5305\u672a\u901a\u8fc7\u5b8c\u6574\u6027\u6821\u9a8c\uff0c\u8bf7\u91cd\u65b0\u4e0b\u8f7d\u3002",
        ["The update could not be downloaded or prepared. Try again."] = "\u65e0\u6cd5\u4e0b\u8f7d\u6216\u51c6\u5907\u66f4\u65b0\u5305\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Restart is unavailable in this app window. Reopen Lumen and try again."] = "\u5f53\u524d\u7a97\u53e3\u65e0\u6cd5\u91cd\u542f\u5e94\u7528\uff0c\u8bf7\u91cd\u65b0\u6253\u5f00 Lumen \u540e\u91cd\u8bd5\u3002",
        ["Starting the update installer"] = "\u6b63\u5728\u542f\u52a8\u66f4\u65b0\u5b89\u88c5\u7a0b\u5e8f",
        ["Restarting to install the update"] = "\u6b63\u5728\u91cd\u542f\u4ee5\u5b89\u88c5\u66f4\u65b0",
        ["The update installer timed out. Try again."] = "\u542f\u52a8\u66f4\u65b0\u5b89\u88c5\u7a0b\u5e8f\u8d85\u65f6\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["This installation cannot be updated automatically. Open the release page to install it."] = "\u5f53\u524d\u5b89\u88c5\u65b9\u5f0f\u4e0d\u652f\u6301\u81ea\u52a8\u66f4\u65b0\uff0c\u8bf7\u6253\u5f00\u53d1\u5e03\u9875\u624b\u52a8\u5b89\u88c5\u3002",
        ["Could not start the update installer. Try again."] = "\u65e0\u6cd5\u542f\u52a8\u66f4\u65b0\u5b89\u88c5\u7a0b\u5e8f\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Development build. No public release was found."] = "\u5f00\u53d1\u6784\u5efa\uff0c\u672a\u627e\u5230\u516c\u5f00\u53d1\u5e03\u7248\u672c\u3002",
        ["Latest published release: {0}"] = "\u6700\u65b0\u516c\u5f00\u53d1\u5e03\u7248\u672c\uff1a{0}",
        ["Update check timed out. Try again."] = "\u68c0\u67e5\u66f4\u65b0\u8d85\u65f6\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Update check failed. Try again later."] = "\u65e0\u6cd5\u68c0\u67e5\u66f4\u65b0\uff0c\u8bf7\u7a0d\u540e\u91cd\u8bd5\u3002",
        ["Open source licenses"] = "\u5f00\u6e90\u8bb8\u53ef",
        ["Third-party licenses shipped with this application"] = "\u672c\u5e94\u7528\u968f\u5305\u63d0\u4f9b\u7684\u7b2c\u4e09\u65b9\u7ec4\u4ef6\u8bb8\u53ef",
        ["View"] = "\u67e5\u770b",
        ["Playback diagnostics"] = "\u64ad\u653e\u8bca\u65ad",
        ["License files"] = "\u8bb8\u53ef\u6587\u4ef6",
        ["License text"] = "\u8bb8\u53ef\u5185\u5bb9",
        ["No license files were included in this build."] = "\u5f53\u524d\u6784\u5efa\u672a\u5305\u542b\u8bb8\u53ef\u6587\u4ef6\u3002",
        ["Could not read license files."] = "\u65e0\u6cd5\u8bfb\u53d6\u8bb8\u53ef\u6587\u4ef6\u3002",
        ["Could not read this license file."] = "\u65e0\u6cd5\u8bfb\u53d6\u6b64\u8bb8\u53ef\u6587\u4ef6\u3002",
        ["License file is too large to display."] = "\u6b64\u8bb8\u53ef\u6587\u4ef6\u8fc7\u5927\uff0c\u65e0\u6cd5\u663e\u793a\u3002"
    });

    private sealed record Option(string Value, string Label);
    private sealed record SettingRowLayout(Grid Row, StackPanel Labels, FrameworkElement Control, TextBlock Description);
}
