using System.Net;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyClient.Api;
using EmbyClient.App.Services;
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
    private static readonly string[] TabKeys = ["Playback", "Subtitles", "Appearance", "Account", "About"];
    private readonly StackPanel _column = new() { Width = 880, Margin = new Thickness(0, 104, 0, 64) };
    private readonly StackPanel _groups = new() { Spacing = 36, Margin = new Thickness(0, 42, 0, 0) };
    private readonly List<Action> _refreshControls = [];
    private readonly List<SettingRowLayout> _rows = [];
    private readonly Button[] _tabButtons = new Button[5];
    private readonly Grid _tabTrack;
    private readonly TranslateTransform _tabOffset = new();
    private readonly LinearGradientBrush _ambientBrush = new() { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
    private readonly Button _signOutButton;
    private readonly Border _tabIndicator;
    private ConnectedSession? _session;
    private LumenPreferences _preferences = new();
    private Flyout? _openFlyout;
    private Flyout? _signOutFlyout;
    private CancellationTokenSource? _updateCheck;
    private int _tab;
    private double _tabWidth = 72;
    private bool _synchronizing;
    private bool _subtitleStyleAvailable;
    private bool _licensesOpen;
    private string _updateStatus = "Development build";
    private object[] _updateArguments = [];

    public LumenSettingsView()
    {
        RegisterSettingsText();
        Background = LumenTheme.Brush("Background");
        var root = new Grid();
        root.Children.Add(new Border
        {
            Height = 640, VerticalAlignment = VerticalAlignment.Top,
            Background = _ambientBrush, IsHitTestVisible = false
        });
        var scroll = new ScrollViewer
        {
            Content = _column, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        root.Children.Add(scroll);
        Content = root;

        var header = new Grid { MinHeight = 50 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = LumenUi.Text(T("Settings"), 44, serif: true);
        title.FontWeight = FontWeights.Black;
        title.LineHeight = 48;
        title.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        header.Children.Add(title);
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
        _signOutButton.Resources["ButtonBorderBrushPointerOver"] = LumenTheme.Brush("Danger");
        _signOutButton.Click += (_, _) => ShowSignOut();
        Grid.SetColumn(_signOutButton, 1);
        header.Children.Add(_signOutButton);
        _column.Children.Add(header);

        _tabTrack = new Grid { Width = 368, Height = 34 };
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
            button.Click += (_, _) => SelectTab(current);
            _tabButtons[index] = button;
            tabs.Children.Add(button);
        }
        _tabTrack.Children.Add(tabs);
        _column.Children.Add(new Border
        {
            Child = _tabTrack, Padding = new Thickness(4), Margin = new Thickness(0, 22, 0, 0),
            Height = 44, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(22),
            Background = LumenTheme.Brush("Glass"), BorderBrush = LumenTheme.Brush("LineStrong"),
            BorderThickness = new Thickness(1)
        });
        _column.Children.Add(_groups);
        UpdateTabButtons();
        BuildGroups();
        UpdateAmbient();
        SizeChanged += (_, _) => UpdateLayoutWidth();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible) _openFlyout?.Hide();
        });
        Loaded += (_, _) =>
        {
            LumenTheme.Changed -= OnThemeChanged;
            LumenTheme.Changed += OnThemeChanged;
            UpdateLayoutWidth();
            UpdateAmbient();
        };
        Unloaded += (_, _) =>
        {
            LumenTheme.Changed -= OnThemeChanged;
            _openFlyout?.Hide();
            _updateCheck?.Cancel();
        };
    }

    public event EventHandler<LumenPreferences>? PreferencesChanged;
    public event EventHandler? SignOutRequested;
    public event EventHandler? SwitchAccountRequested;
    public event EventHandler? DiagnosticsRequested;

    public void SetSession(ConnectedSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        if (_tab is 3 or 4) BuildGroups();
    }

    public void SetPreferences(LumenPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _preferences = preferences with { };
        RefreshControls();
        UpdateAmbient();
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
            case 3: BuildAccount(); break;
            case 4: BuildAbout(); break;
        }
        RefreshControls();
        UpdateRowLayout();
    }

    private void BuildPlayback()
    {
        var behavior = Group("Playback behavior");
        AddRow(behavior, "Auto-play next episode", "Start the next episode when the current one ends",
            Switch("Auto-play next episode", () => _preferences.AutoPlayNext, value => Change(p => p with { AutoPlayNext = value })));
        AddRow(behavior, "Next episode countdown", "Show a prompt when end credits begin",
            Segments([new("0", "Countdown off"), new("5", "5 seconds"), new("10", "10 seconds"), new("15", "15 seconds")],
                () => _preferences.NextEpisodeCountdownSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { NextEpisodeCountdownSeconds = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) }), minimumWidth: 212));
        AddRow(behavior, "Intro and credits", "Use chapter markers supplied by the server",
            Dropdown("Intro and credits", [new("ShowButton", "Show skip button"), new("Automatic", "Skip automatically"), new("Off", "Do not skip")],
                () => _preferences.IntroSkipMode, value => Change(p => p with { IntroSkipMode = value })));
        AddRow(behavior, "Resume playback", "When opening a partially watched video",
            Dropdown("Resume playback", [new("Continue", "Continue from last position"), new("Ask", "Ask every time"), new("Restart", "Play from beginning")],
                () => _preferences.ResumeMode, value => Change(p => p with { ResumeMode = value })));

        var quality = Group("Quality and decoding");
        AddRow(quality, "Prefer direct play", "Transcode only when the format is unsupported",
            Switch("Prefer direct play", () => _preferences.PreferDirectPlay, value => Change(p => p with { PreferDirectPlay = value })));
        AddRow(quality, "Local network maximum bitrate", "When connecting to a server on your local network",
            Dropdown("Local network maximum bitrate", [new("0", "Unlimited"), new("120000000", "120 Mbps"), new("80000000", "80 Mbps"), new("40000000", "40 Mbps")],
                () => _preferences.LocalMaxBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { LocalMaxBitrate = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) })));
        AddRow(quality, "Internet maximum bitrate", "When connecting outside your local network",
            Dropdown("Internet maximum bitrate", [new("20000000", "20 Mbps"), new("10000000", "10 Mbps"), new("4000000", "4 Mbps"), new("0", "Unlimited")],
                () => _preferences.InternetMaxBitrate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                value => Change(p => p with { InternetMaxBitrate = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture) })));
        const string decoderDescription = "Applies to the next video. Unavailable APIs or unsupported streams fall back to software.";
        AddRow(quality, "Video decoder API", decoderDescription,
            DecoderApiDropdown());
        const string hdrReason = "This playback profile outputs SDR. HDR has not been verified.";
        AddRow(quality, "HDR output", hdrReason,
            Segments([new("Auto", "Auto"), new("Always", "Always"), new("Off", "Off")], () => _preferences.HdrMode, _ => { }, enabled: false, reason: hdrReason));
        const string refreshReason = "This player does not change the display refresh rate.";
        AddRow(quality, "Match content refresh rate", refreshReason,
            Switch("Match content refresh rate", () => _preferences.MatchRefreshRate, _ => { }, enabled: false, refreshReason));
    }

    private void BuildSubtitles()
    {
        var tracks = Group("Default tracks");
        AddRow(tracks, "Preferred subtitle language", "Select a default language for subtitles",
            Dropdown("Preferred subtitle language", [new("chi", "Simplified Chinese"), new("zht", "Traditional Chinese"), new("eng", "English"), new("", "Follow audio language")],
                () => _preferences.SubtitleLanguage, value => Change(p => p with { SubtitleLanguage = value }),
                formatUnknown: value => T("Language ({0})", value)));
        AddRow(tracks, "Subtitle mode", "Use the preferred language and server playback rules",
            Segments([new("Smart", "Smart"), new("Always", "Always show"), new("OnlyForced", "Forced only"), new("None", "Off"), new("Default", "Server default"), new("HearingImpaired", "Hearing impaired")],
                () => _preferences.SubtitleMode, value => Change(p => p with { SubtitleMode = value })));
        var style = Group("Subtitle style");
        var reason = _subtitleStyleAvailable
            ? "Applies to external text subtitles only; server-rendered subtitles cannot be styled."
            : "Subtitle appearance is rendered by the server and cannot be changed by this player.";
        AddRow(style, "Font size", reason,
            Segments([new("Small", "Small"), new("Medium", "Medium"), new("Large", "Large")],
                () => _preferences.SubtitleSize, value => Change(p => p with { SubtitleSize = value }), enabled: _subtitleStyleAvailable, reason: reason));
        AddRow(style, "Text outline", reason,
            Switch("Text outline", () => _preferences.SubtitleOutline, value => Change(p => p with { SubtitleOutline = value }), _subtitleStyleAvailable, reason));
        AddRow(style, "Subtitle position", reason,
            Segments([new("Bottom", "Bottom of picture"), new("BlackBars", "In black bars")],
                () => _preferences.SubtitlePosition, value => Change(p => p with { SubtitlePosition = value }),
                enabled: _subtitleStyleAvailable, reason: reason));
    }

    private void BuildAppearance()
    {
        var appearance = Group("Interface");
        AddRow(appearance, "Theme", "", Segments([new("Dark", "Dark"), new("Light", "Light")],
            () => _preferences.Theme, value => Change(p => p with { Theme = value })));
        AddRow(appearance, "Accent color", "", AccentSwatches());
        AddRow(appearance, "Home carousel", "", Switch("Home carousel", () => _preferences.HeroRotation,
            value => Change(p => p with { HeroRotation = value })));
        var posters = Group("Poster wall");
        AddRow(posters, "Poster size", "", Segments([new("10", "Small"), new("8", "Medium"), new("6", "Large")],
            () => (_preferences.PosterColumns >= 9 ? 10 : _preferences.PosterColumns <= 7 ? 6 : 8).ToString(System.Globalization.CultureInfo.InvariantCulture),
            value => Change(p => p with { PosterColumns = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) })));
        AddRow(posters, "Show watched marks", "", Switch("Show watched marks", () => _preferences.ShowWatchedMarks,
            value => Change(p => p with { ShowWatchedMarks = value })));
    }

    private void BuildAccount()
    {
        var server = Group("Server");
        var root = _session?.Api.ApiRoot;
        AddRow(server, "Server", root?.AbsoluteUri ?? "", ValueText(_session?.Server.ServerName ?? T("Not available")));
        AddRow(server, "Version", "", ValueText(_session?.Server.Version is { } version ? $"Emby Server {version}" : T("Not available")));
        AddRow(server, "Connection", "", ValueText(root is null ? T("Not available") : T(root.Scheme == "https" ? "Secure HTTPS" : "HTTP")));
        var account = Group("Current user");
        AddRow(account, "Current user", "", ValueText(_session?.User.Name ?? T("Not available")));
        var change = LumenUi.Button(T("Switch"), height: 36);
        change.Click += (_, _) => SwitchAccountRequested?.Invoke(this, EventArgs.Empty);
        AddRow(account, "Switch user", "", change);
    }

    private void BuildAbout()
    {
        var about = Group("Lumen");
        var version = _session?.Api.Identity.Version ?? "0.1.0";
        AddRow(about, "Application version", "", ValueText($"{version} \u00b7 {T("Development build")}"));
        var check = LumenUi.Button(T("Check"), height: 36);
        check.MinWidth = 86;
        check.IsEnabled = _updateCheck is null;
        var status = AddRow(about, "Check updates", _updateStatus, check);
        status.Description.Text = T(_updateStatus, _updateArguments);
        _refreshControls.Add(() =>
        {
            check.IsEnabled = _updateCheck is null;
            status.Description.Text = T(_updateStatus, _updateArguments);
        });
        check.Click += async (_, _) => await CheckUpdatesAsync(check, status.Description);
        var licenses = LumenUi.Button(T("View"), height: 36);
        licenses.Click += async (_, _) => await ShowLicensesAsync();
        AddRow(about, "Open source licenses", "Third-party licenses shipped with this application", licenses);
        var diagnostics = LumenUi.Button(T("View"), "info", height: 36);
        diagnostics.Click += (_, _) => DiagnosticsRequested?.Invoke(this, EventArgs.Empty);
        AddRow(about, "Playback diagnostics", "", diagnostics);
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
        name.FontWeight = FontWeights.Bold;
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
        var row = new Grid { MinHeight = 78, ColumnSpacing = 24, Margin = new Thickness(4, 0, 4, 0) };
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
        var text = LumenUi.Text(value, 13);
        text.Foreground = LumenTheme.Brush("Sub");
        text.TextWrapping = TextWrapping.Wrap;
        text.MaxWidth = 420;
        text.TextAlignment = TextAlignment.Right;
        return text;
    }

    private void UpdateLayoutWidth()
    {
        if (ActualWidth <= 0) return;
        _column.Width = Math.Max(0, Math.Min(880, ActualWidth - (ActualWidth < 650 ? 32 : 56)));
        var width = Math.Min(72, Math.Max(36, (_column.Width - 18) / 5));
        if (Math.Abs(_tabWidth - width) > .01)
        {
            _tabWidth = width;
            foreach (var tab in _tabButtons) tab.Width = width;
            _tabTrack.Width = width * 5 + 8;
            _tabIndicator.Width = width;
            Animate(_tabOffset, "X", _tab * (width + 2), 120);
        }
        UpdateRowLayout();
    }

    private void UpdateRowLayout()
    {
        var compact = _column.Width < 620;
        foreach (var layout in _rows)
        {
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
                layout.Labels.Margin = new Thickness(0);
                layout.Control.Margin = new Thickness(0);
                layout.Control.HorizontalAlignment = HorizontalAlignment.Right;
                if (layout.Control is TextBlock value) value.TextAlignment = TextAlignment.Right;
            }
        }
    }

    private static Button FlatButton(string label, double height)
    {
        var button = LumenUi.Button(label, height: height);
        var text = LumenUi.Text(label, 13);
        text.TextWrapping = TextWrapping.NoWrap;
        text.MaxLines = 1;
        text.FontWeight = FontWeights.Medium;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextAlignment = TextAlignment.Center;
        button.Content = text;
        button.Padding = new Thickness(8, 0, 8, 0);
        button.MinWidth = 0;
        button.MinHeight = 0;
        button.BorderThickness = new Thickness(0);
        button.Background = new SolidColorBrush(Colors.Transparent);
        button.BorderBrush = new SolidColorBrush(Colors.Transparent);
        NeutralChrome(button, "Button");
        return button;
    }

    private static void NeutralChrome(Control control, string prefix)
    {
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "Checked", "CheckedPointerOver", "CheckedPressed", "CheckedDisabled" })
        {
            control.Resources[$"{prefix}Background{state}"] = clear;
            control.Resources[$"{prefix}BorderBrush{state}"] = clear;
            control.Resources[$"{prefix}Foreground{state}"] = LumenTheme.Brush("Ink");
        }
        control.UseSystemFocusVisuals = true;
        control.FocusVisualPrimaryBrush = LumenTheme.Brush("Accent");
        control.FocusVisualSecondaryBrush = LumenTheme.Brush("Background");
    }

    private ToggleButton Switch(string name, Func<bool> getter, Action<bool> setter, bool enabled = true, string? reason = null)
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
            toggle.IsChecked = value;
            track.Background = LumenTheme.Brush(value ? "Accent" : "ControlStrong");
            thumb.Fill = value ? new SolidColorBrush(Colors.White) : LumenTheme.Brush("Muted");
            if (initial) offset.X = value ? 20 : 0;
            else Animate(offset, "X", value ? 20 : 0, 250, spring: true);
            initial = false;
            track.Opacity = enabled ? 1 : .45;
        });
        toggle.Checked += (_, _) => setter(true);
        toggle.Unchecked += (_, _) => setter(false);
        toggle.PointerEntered += (_, _) => { if (enabled) track.Opacity = .86; };
        toggle.PointerExited += (_, _) => { if (enabled) track.Opacity = 1; };
        return toggle;
    }

    private FrameworkElement Segments(Option[] options, Func<string> getter, Action<string> setter,
        bool enabled = true, string? reason = null, double minimumWidth = 0)
    {
        const double horizontalPadding = 12;
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
            button.IsEnabled = enabled;
            if (reason is not null)
            {
                ToolTipService.SetToolTip(button, T(reason));
                AutomationProperties.SetHelpText(button, T(reason));
            }
            button.Click += (_, _) => setter(option.Value);
            buttons.Children.Add(button);
            choices.Add(button);
        }
        track.Children.Add(buttons);
        var outer = new Border
        {
            Height = 38, Child = track, Padding = new Thickness(3), CornerRadius = new CornerRadius(19),
            Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("LineStrong"),
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
        Option[] options =
        [
            new("Auto", "Automatic (D3D11VA)"), new("D3D11", "D3D11VA"), new("IntelQsv", "Intel VPL / QSV"),
            new("AmdAmf", "AMD AMF"), new("NvidiaNvdec", "NVIDIA NVDEC"), new("Software", "Software decoding")
        ];
        var selector = new ComboBox
        {
            Width = 220, MinHeight = 36, FontFamily = LumenTheme.SansFont, FontSize = 13,
            Background = LumenTheme.Brush("Control"), Foreground = LumenTheme.Brush("Ink"),
            BorderBrush = LumenTheme.Brush("LineStrong"), CornerRadius = new CornerRadius(12),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, UseSystemFocusVisuals = true
        };
        foreach (var option in options)
            selector.Items.Add(new ComboBoxItem { Content = T(option.Label), Tag = option.Value, MinHeight = 36 });
        AutomationProperties.SetName(selector, T("Video decoder API"));
        selector.SelectionChanged += (_, _) =>
        {
            if (_synchronizing || selector.SelectedItem is not ComboBoxItem { Tag: string value }
                || value == _preferences.VideoDecoderApi) return;
            Change(p => p with { VideoDecoderApi = value, HardwareDecoding = value != "Software" });
        };
        _refreshControls.Add(() =>
        {
            ComboBoxItem? selected = null;
            foreach (var item in selector.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string value && value == _preferences.VideoDecoderApi)
                {
                    selected = item;
                    break;
                }
            }
            if (!ReferenceEquals(selector.SelectedItem, selected)) selector.SelectedItem = selected;
            AutomationProperties.SetHelpText(selector, $"{T("Video decoder API")}: {selected?.Content}");
        });
        return selector;
    }

    private FrameworkElement Dropdown(string name, Option[] options, Func<string> getter, Action<string> setter,
        Func<string, string>? formatUnknown = null)
    {
        var button = LumenUi.Button("", height: 36);
        button.Width = 200;
        button.Padding = new Thickness(16, 0, 14, 0);
        button.Background = LumenTheme.Brush("Control");
        var content = new Grid { ColumnSpacing = 10 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var value = LumenUi.Text("", 13);
        value.TextWrapping = TextWrapping.NoWrap;
        value.MaxLines = 1;
        value.TextTrimming = TextTrimming.CharacterEllipsis;
        value.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(value);
        var arrowOffset = new RotateTransform();
        var arrow = LumenUi.Icon("chevron-down", 14);
        arrow.RenderTransformOrigin = new Point(.5, .5);
        arrow.RenderTransform = arrowOffset;
        Grid.SetColumn(arrow, 1);
        content.Children.Add(arrow);
        button.Content = content;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetName(button, T(name));

        var items = new StackPanel { Spacing = 2 };
        var checks = new List<(string Value, FrameworkElement Icon)>();
        var flyout = new Flyout { Content = items, FlyoutPresenterStyle = FlyoutStyle(200, 8) };
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
                var item = FlatButton(T(option.Label), 38);
                item.HorizontalAlignment = HorizontalAlignment.Stretch;
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                item.CornerRadius = new CornerRadius(10);
                item.Resources["ButtonBackgroundPointerOver"] = LumenTheme.Brush("ControlStrong");
                item.Resources["ButtonBackgroundPressed"] = LumenTheme.Brush("ControlStrong");
                var line = new Grid { ColumnSpacing = 10 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var check = LumenUi.Icon("check", 16);
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
                checks.Add((option.Value, check));
            }
        }
        _refreshControls.Add(() =>
        {
            var selected = Array.FindIndex(options, option => option.Value == getter());
            value.Text = selected >= 0 ? T(options[selected].Label) : formatUnknown?.Invoke(getter()) ?? getter();
            AutomationProperties.SetHelpText(button, $"{T(name)}: {value.Text}");
            foreach (var check in checks) check.Icon.Opacity = check.Value == getter() ? 1 : 0;
        });
        flyout.Opened += (_, _) => Animate(arrowOffset, "Angle", 180, 250);
        flyout.Closed += (_, _) =>
        {
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
            ("Gold", "Gold", Color.FromArgb(255, 226, 184, 107), Color.FromArgb(255, 143, 98, 18)),
            ("Coral", "Coral", Color.FromArgb(255, 232, 134, 106), Color.FromArgb(255, 168, 69, 42)),
            ("Sage", "Sage", Color.FromArgb(255, 159, 194, 164), Color.FromArgb(255, 61, 112, 80)),
            ("Blue", "Mist blue", Color.FromArgb(255, 169, 184, 232), Color.FromArgb(255, 63, 85, 168))
        };
        foreach (var choice in choices)
        {
            var swatch = new Ellipse { Width = 24, Height = 24 };
            var ring = new Border
            {
                Width = 32, Height = 32, Padding = new Thickness(2), BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(16), Background = LumenTheme.Brush("Background"), Child = swatch
            };
            var button = FlatButton("", 44);
            button.Width = 44;
            button.Padding = new Thickness(0);
            button.Content = ring;
            AutomationProperties.SetName(button, T(choice.Label));
            ToolTipService.SetToolTip(button, T(choice.Label));
            button.Click += (_, _) => Change(p => p with { Accent = choice.Value });
            row.Children.Add(button);
            _refreshControls.Add(() =>
            {
                var color = new SolidColorBrush(_preferences.Theme == "Light" ? choice.Light : choice.Dark);
                var selected = _preferences.Accent == choice.Value;
                swatch.Fill = color;
                ring.BorderBrush = selected ? color : new SolidColorBrush(Colors.Transparent);
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
    }

    private void ShowSignOut()
    {
        if (_openFlyout is not null && _openFlyout == _signOutFlyout) { _openFlyout.Hide(); return; }
        const double padding = 18;
        var viewportWidth = XamlRoot?.Size.Width ?? (ActualWidth > 0 ? ActualWidth : 322);
        var flyoutWidth = Math.Min(290, Math.Max(96, viewportWidth - 32));
        var contentWidth = flyoutWidth - 2 * (padding + 1);
        var compactActions = contentWidth < 160;
        var content = new StackPanel { Spacing = 8, Width = contentWidth, MaxWidth = contentWidth };
        var title = LumenUi.Text(T("Sign out?"), 15);
        title.FontWeight = FontWeights.Bold;
        content.Children.Add(title);
        var server = _session?.Server.ServerName ?? _session?.Api.ApiRoot.Authority ?? T("Server");
        var description = LumenUi.Text(T("Disconnect from {0}? Your server-saved playback progress will remain.", server), 13);
        description.Foreground = LumenTheme.Brush("Muted");
        description.TextWrapping = TextWrapping.Wrap;
        description.Width = description.MaxWidth = contentWidth;
        description.LineHeight = 21;
        content.Children.Add(description);
        var actions = new StackPanel
        {
            Orientation = compactActions ? Orientation.Vertical : Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = compactActions ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            MaxWidth = contentWidth, Margin = new Thickness(0, 8, 0, 0)
        };
        var cancel = LumenUi.Button(T("Cancel"), height: 34);
        cancel.Padding = new Thickness(16, 0, 16, 0);
        if (cancel.Content is StackPanel cancelContent && cancelContent.Children[0] is TextBlock cancelLabel)
        {
            cancelLabel.FontSize = 13;
            if (compactActions)
            {
                cancelLabel.TextWrapping = TextWrapping.Wrap;
                cancelLabel.MaxWidth = Math.Max(13, contentWidth - 34);
            }
        }
        var exit = LumenUi.Button(T("Sign out action"), primary: true, height: 34);
        exit.Padding = new Thickness(18, 0, 18, 0);
        exit.Background = LumenTheme.Brush("Danger");
        exit.BorderBrush = LumenTheme.Brush("Danger");
        var exitLabel = LumenUi.Text(T("Sign out action"), 13);
        exitLabel.Foreground = new SolidColorBrush(Colors.White);
        exitLabel.FontWeight = FontWeights.Bold;
        exit.Content = exitLabel;
        if (compactActions)
        {
            cancel.HorizontalAlignment = exit.HorizontalAlignment = HorizontalAlignment.Stretch;
            cancel.MaxWidth = exit.MaxWidth = contentWidth;
        }
        foreach (var state in new[] { "", "PointerOver", "Pressed" })
        {
            exit.Resources[$"ButtonBackground{state}"] = LumenTheme.Brush("Danger");
            exit.Resources[$"ButtonBorderBrush{state}"] = LumenTheme.Brush("Danger");
            exit.Resources[$"ButtonForeground{state}"] = new SolidColorBrush(Colors.White);
        }
        actions.Children.Add(cancel);
        actions.Children.Add(exit);
        content.Children.Add(actions);
        var flyout = new Flyout { Content = content, FlyoutPresenterStyle = FlyoutStyle(flyoutWidth, padding) };
        _signOutFlyout = flyout;
        cancel.Click += (_, _) => flyout.Hide();
        exit.Click += (_, _) =>
        {
            flyout.Hide();
            SignOutRequested?.Invoke(this, EventArgs.Empty);
        };
        flyout.Closed += (_, _) => { if (_openFlyout == flyout) _openFlyout = null; };
        ShowFlyout(flyout, _signOutButton);
    }

    private async Task CheckUpdatesAsync(Button button, TextBlock status)
    {
        if (_updateCheck is not null) return;
        using var request = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _updateCheck = request;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = LumenUi.Text(T("Checking"), 13);
        AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        try
        {
            using var http = EmbyApiClient.CreateHttpClient();
            using var message = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/moooyo/emby-client-winui3/releases/latest");
            message.Headers.UserAgent.ParseAdd("Lumen-EmbyClient/0.1.0");
            message.Headers.Accept.ParseAdd("application/vnd.github+json");
            message.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                SetUpdateStatus("Development build. No public release was found.");
            }
            else
            {
                response.EnsureSuccessStatusCode();
                const int limit = 256 * 1024;
                if (response.Content.Headers.ContentLength is > limit)
                    throw new JsonException("The release response exceeds the allowed size.");
                await using var input = await response.Content.ReadAsStreamAsync(request.Token);
                using var bounded = new MemoryStream();
                var bytes = new byte[8192];
                while (true)
                {
                    var count = await input.ReadAsync(bytes, request.Token);
                    if (count == 0) break;
                    if (bounded.Length + count > limit)
                        throw new JsonException("The release response exceeds the allowed size.");
                    bounded.Write(bytes, 0, count);
                }
                bounded.Position = 0;
                var release = await JsonSerializer.DeserializeAsync(bounded, LumenReleaseJsonContext.Default.LumenReleaseInfo, request.Token);
                if (release?.Tag is not { Length: > 0 and <= 128 } tag || tag.Any(char.IsControl))
                    throw new JsonException("The release response has no valid version tag.");
                SetUpdateStatus("Latest published release: {0}", tag);
            }
        }
        catch (OperationCanceledException)
        {
            if (IsLoaded) SetUpdateStatus("Update check timed out. Try again.");
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            SetUpdateStatus("Update check failed. Try again later.");
        }
        finally
        {
            if (_updateCheck == request) _updateCheck = null;
            button.Content = originalContent;
            button.IsEnabled = true;
            status.Text = T(_updateStatus, _updateArguments);
            RefreshControls();
        }
    }

    private void SetUpdateStatus(string key, params object[] arguments)
    {
        _updateStatus = key;
        _updateArguments = arguments;
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
            RequestedTheme = _preferences.Theme == "Light" ? ElementTheme.Light : ElementTheme.Dark,
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
        ["This playback profile outputs SDR. HDR has not been verified."] = "\u5f53\u524d\u64ad\u653e\u914d\u7f6e\u8f93\u51fa SDR\uff0cHDR \u5c1a\u672a\u901a\u8fc7\u9a8c\u8bc1\u3002",
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

[JsonSerializable(typeof(LumenReleaseInfo))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = false)]
internal partial class LumenReleaseJsonContext : JsonSerializerContext;

internal sealed record LumenReleaseInfo
{
    [JsonPropertyName("tag_name")]
    public string? Tag { get; init; }
}
