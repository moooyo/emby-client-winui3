using EmbyClient.App.Services;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views.Lumen;

/// <summary>Builds the fixed Home, Movies, and TV shows capsule used by the V2 shell.</summary>
public sealed partial class LumenNavigationBar : UserControl
{
    private sealed record NavigationEntry(string Key, string Title);
    private sealed class ButtonAppearance
    {
        public SolidColorBrush Foreground { get; } = new();
        public SolidColorBrush Hover { get; } = new();
        public SolidColorBrush Pressed { get; } = new();
    }
    private readonly Grid _layout = new() { Height = 32, HorizontalAlignment = HorizontalAlignment.Left, Margin = new(4) };
    private readonly Border _surface = new() { CornerRadius = new(20), BorderThickness = new(1), IsHitTestVisible = false };
    private readonly Border _indicator;
    private readonly Border _divider = new() { Width = 1, Height = 18, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly AcrylicBrush _glass = new() { TintColor = Colors.Transparent, TintOpacity = 0, TintLuminosityOpacity = 0, TintTransitionDuration = TimeSpan.Zero };
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly UISettings _uiSettings = new();
    private readonly SolidColorBrush _systemWindow = new();
    private readonly SolidColorBrush _systemText = new();
    private readonly SolidColorBrush _systemHighlight = new();
    private readonly SolidColorBrush _systemHighlightText = new();
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ButtonAppearance> _buttonAppearance = new(StringComparer.Ordinal);
    private static readonly IReadOnlyList<string> PrimaryNavigationOrder = Array.AsReadOnly(new[] { "home", "movies", "series" });
    private Storyboard? _movement;
    private Popup? _popup;
    private Button? _popupTrigger;
    private string _selected = "home";
    private string _activeButton = "home";
    private string _signature = string.Empty;
    private string _userName = "Lumen";
    private string _serverName = string.Empty;
    private string _serverAddress = string.Empty;
    private bool _onImage;
    private bool _enabled = true;
    private bool _themeSubscribed;
    private bool _highContrastSubscribed;
    private bool _systemColorsSubscribed;

    public event EventHandler<string>? NavigationRequested;
    public event EventHandler<string>? AccountActionRequested;
    public IReadOnlyList<string> KeyboardNavigationOrder => PrimaryNavigationOrder;
    public bool IsMenuOpen => _popup?.IsOpen == true;

    public LumenNavigationBar()
    {
        Height = 40;
        IsTabStop = false;
        AutomationProperties.SetAutomationId(this, "LumenNavigation");
        _indicator = new Border
        {
            Width = 64, Height = 32, CornerRadius = new(16), HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new TranslateTransform(), IsHitTestVisible = false
        };
        var layers = new Grid();
        layers.Children.Add(new Border { CornerRadius = new(20), Background = _glass, IsHitTestVisible = false });
        layers.Children.Add(_surface);
        layers.Children.Add(_layout);
        Content = layers;
        Loaded += (_, _) =>
        {
            if (!_themeSubscribed) LumenTheme.Changed += ThemeChanged;
            _themeSubscribed = true;
            SubscribeSystemPreferences();
            UpdateAppearance();
        };
        Unloaded += (_, _) =>
        {
            DismissMenus();
            _movement?.Stop();
            if (_themeSubscribed) LumenTheme.Changed -= ThemeChanged;
            _themeSubscribed = false;
            UnsubscribeSystemPreferences();
        };
        RebuildTabs();
    }

    public void RefreshLabels()
    {
        var signature = string.Join('|', LumenText.Get("Home"), LumenText.Get("Movies"), Tr("TV shows"));
        if (_signature == signature) return;
        DismissMenus();
        _signature = signature;
        RebuildTabs();
    }

    public void SetAccount(string? userName, string? serverName, string? serverAddress)
    {
        userName = string.IsNullOrWhiteSpace(userName) ? "Lumen" : userName;
        serverName ??= string.Empty;
        serverAddress ??= string.Empty;
        if (_userName == userName && _serverName == serverName && _serverAddress == serverAddress) return;
        DismissMenus();
        _userName = userName;
        _serverName = serverName;
        _serverAddress = serverAddress;
        UpdateAppearance();
    }

    public void Select(string key, bool onImage)
    {
        if (_selected == key && _onImage == onImage) return;
        _selected = key;
        _onImage = onImage;
        UpdateIndicator(true);
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        foreach (var button in _buttons.Values) button.IsEnabled = enabled;
        if (!enabled) DismissMenus();
    }

    public void FocusSelected()
    {
        if (_buttons.TryGetValue(_activeButton, out var selected)) selected.Focus(FocusState.Programmatic);
        else _buttons.GetValueOrDefault("home")?.Focus(FocusState.Programmatic);
    }

    public bool DismissMenus()
    {
        if (_popup?.IsOpen != true) return false;
        _popup.IsOpen = false;
        return true;
    }

    private void RebuildTabs()
    {
        var focusedNavigation = CaptureNavigationFocus();
        var entries = new[]
        {
            new NavigationEntry("home", LumenText.Get("Home")),
            new NavigationEntry("movies", LumenText.Get("Movies")),
            new NavigationEntry("series", Tr("TV shows"))
        };
        var widths = entries.Select(entry => LabelWidth(entry.Title)).ToArray();
        var keys = entries.Select(entry => entry.Key).Concat(["search", "settings"]).ToArray();
        var replaceButtons = !_buttons.Keys.SequenceEqual(keys, StringComparer.Ordinal);
        _movement?.Stop();
        if (replaceButtons)
        {
            _layout.Children.Clear();
            _buttons.Clear();
            _buttonAppearance.Clear();
            _layout.Children.Add(_indicator);
        }
        var left = 0d;
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var button = replaceButtons ? CreateButton(entry.Key, entry.Title, left, widths[index]) : _buttons[entry.Key];
            button.Width = widths[index];
            button.Margin = new(left, 0, 0, 0);
            var label = button.Content as TextBlock ?? LumenUi.Text(entry.Title, 14);
            label.Text = entry.Title;
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.MaxWidth = Math.Max(20, widths[index] - 20);
            button.Content = label;
            AutomationProperties.SetName(button, entry.Title);
            AutomationProperties.SetAcceleratorKey(button, "Ctrl+" + (index + 1).ToString(CultureInfo.InvariantCulture));
            ToolTipService.SetToolTip(button, entry.Title);
            if (replaceButtons) _layout.Children.Add(button);
            left += widths[index] + 2;
        }
        left -= 2;
        _divider.Margin = new(left + 6, 0, 0, 0);
        if (replaceButtons)
        {
            _layout.Children.Add(_divider);
            _layout.Children.Add(CreateButton("search", LumenText.Get("Search") + " (Ctrl+F)", left + 13, 32));
            _layout.Children.Add(CreateButton("settings", Tr("Account"), left + 47, 32));
        }
        else
        {
            _buttons["search"].Margin = new(left + 13, 0, 0, 0);
            _buttons["settings"].Margin = new(left + 47, 0, 0, 0);
        }
        Width = left + 87;
        _layout.Width = Width - 8;
        UpdateIndicator(false);
        if (replaceButtons && focusedNavigation.Key is not null) RestoreNavigationFocus(focusedNavigation.Key, focusedNavigation.State);
    }

    private (string? Key, FocusState State) CaptureNavigationFocus()
    {
        if (XamlRoot is null) return (null, FocusState.Unfocused);
        for (var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
        {
            foreach (var pair in _buttons)
                if (ReferenceEquals(pair.Value, focused)) return (pair.Key, pair.Value.FocusState);
            if (ReferenceEquals(focused, this)) break;
        }
        return (null, FocusState.Unfocused);
    }

    private void RestoreNavigationFocus(string key, FocusState state)
    {
        var replacement = _buttons.GetValueOrDefault(key) ?? _buttons.GetValueOrDefault(_activeButton) ?? _buttons.GetValueOrDefault("home");
        if (replacement is null) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || XamlRoot is null || !replacement.IsLoaded || !replacement.IsEnabled || IsMenuOpen
                || !_buttons.ContainsValue(replacement)) return;
            var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (current is Control { IsLoaded: true, IsTabStop: true, Visibility: Visibility.Visible } control
                && !_buttons.Values.Any(button => ReferenceEquals(button, control))) return;
            replacement.Focus(state == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
        });
    }

    private static double LabelWidth(string title)
    {
        var width = 30d;
        foreach (var character in title.EnumerateRunes()) width += character.Value > 0x2E80 ? 14 : 8;
        return Math.Max(64, width);
    }

    private Button CreateButton(string key, string label, double left, double width)
    {
        var appearance = new ButtonAppearance();
        var button = new Button
        {
            Width = width, Height = 32, MinWidth = 0, MinHeight = 0, Padding = new(0), CornerRadius = new(16),
            BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(left, 0, 0, 0), FontSize = 14,
            FontFamily = LumenTheme.SansFont, UseSystemFocusVisuals = true, IsEnabled = _enabled,
            Foreground = appearance.Foreground
        };
        // WinUI resource keys are registered once. Updating brush colors keeps active templates intact.
        button.Resources["ButtonBackgroundPointerOver"] = appearance.Hover;
        button.Resources["ButtonBackgroundPressed"] = appearance.Pressed;
        button.Resources["ButtonForegroundPointerOver"] = appearance.Foreground;
        button.Resources["ButtonForegroundPressed"] = appearance.Foreground;
        _buttonAppearance.Add(key, appearance);
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, "LumenNav_" + key.Replace(':', '_'));
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) =>
        {
            if (key == "settings") ShowAccount(button);
            else { DismissMenus(); NavigationRequested?.Invoke(this, key); }
        };
        _buttons.Add(key, button);
        return button;
    }

    private void UpdateIndicator(bool animate)
    {
        var oldX = _indicator.Margin.Left + ((_indicator.RenderTransform as TranslateTransform)?.X ?? 0);
        var oldWidth = _indicator.ActualWidth > 0 ? _indicator.ActualWidth : _indicator.Width;
        _movement?.Stop();
        _movement = null;
        _activeButton = _selected is "settings" or "favs" ? "settings" : _buttons.ContainsKey(_selected) ? _selected : string.Empty;
        if (!_buttons.TryGetValue(_activeButton, out var button))
        {
            _indicator.Visibility = Visibility.Collapsed;
            UpdateAppearance();
            return;
        }
        _indicator.Visibility = Visibility.Visible;
        var target = button.Margin.Left;
        var translation = new TranslateTransform();
        // The resting position belongs to layout; animations never own the final navigation state.
        _indicator.Margin = new(target, 0, 0, 0);
        _indicator.RenderTransform = translation;
        _indicator.Width = button.Width;
        if (animate && IsLoaded && AnimationsEnabled())
        {
            _movement = new Storyboard();
            var movement = new DoubleAnimation
            {
                From = oldX - target, To = 0, Duration = new(TimeSpan.FromMilliseconds(550)),
                EasingFunction = new BackEase { Amplitude = .3, EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(movement, translation);
            Storyboard.SetTargetProperty(movement, nameof(TranslateTransform.X));
            _movement.Children.Add(movement);
            var sizing = new DoubleAnimation
            {
                From = oldWidth, To = button.Width, Duration = new(TimeSpan.FromMilliseconds(550)), EnableDependentAnimation = true,
                EasingFunction = new BackEase { Amplitude = .3, EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.Stop
            };
            Storyboard.SetTarget(sizing, _indicator);
            Storyboard.SetTargetProperty(sizing, "Width");
            _movement.Children.Add(sizing);
            _movement.Begin();
        }
        UpdateAppearance();
    }

    private void ShowAccount(Button trigger)
    {
        var content = new StackPanel();
        var identity = new Grid { ColumnSpacing = 12, Margin = new(10, 10, 10, 12) };
        identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        identity.Children.Add(Avatar(40, 16));
        var details = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var name = LumenUi.Text(_userName, 14);
        name.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        details.Children.Add(name);
        var server = LumenUi.Text(string.Join(" \u00b7 ", new[] { _serverName, _serverAddress }.Where(value => !string.IsNullOrEmpty(value))), 12);
        server.Foreground = LumenTheme.Brush("Muted");
        server.TextWrapping = TextWrapping.NoWrap;
        server.TextTrimming = TextTrimming.CharacterEllipsis;
        details.Children.Add(server);
        Grid.SetColumn(details, 1);
        identity.Children.Add(details);
        content.Children.Add(identity);
        content.Children.Add(MenuDivider());
        var controls = new List<Button>();
        foreach (var (key, label, icon, hint) in new[]
        {
            ("favs", Tr("My favorites"), "heart_20_regular", string.Empty),
            ("settings", LumenText.Get("Settings"), "settings_20_regular", "Ctrl+,"),
            ("switch", Tr("Switch user"), "person_20_regular", string.Empty),
            ("shortcuts", Tr("Keyboard shortcuts"), "keyboard_20_regular", "?"),
            ("signout", LumenText.Get("Sign out"), "sign_out_20_regular", string.Empty)
        })
        {
            if (key == "signout") content.Children.Add(MenuDivider());
            var row = MenuButton(label, icon, hint);
            row.Click += (_, _) => { DismissMenus(); AccountActionRequested?.Invoke(this, key); };
            content.Children.Add(row);
            controls.Add(row);
        }
        OpenPopup(trigger, content, controls);
    }

    private static Border MenuDivider() => new() { Height = 1, Margin = new(10, 5, 10, 5), Background = LumenTheme.Brush("Line") };

    private static Button MenuButton(string label, string icon, string hint)
    {
        var row = new Button
        {
            Height = 38, MinHeight = 0, MinWidth = 0, CornerRadius = new(10), Padding = new(12, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new(0), UseSystemFocusVisuals = true
        };
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        layout.Children.Add(LumenUi.Icon(icon, 16));
        var text = LumenUi.Text(label, 13);
        text.TextWrapping = TextWrapping.NoWrap;
        text.TextTrimming = TextTrimming.CharacterEllipsis;
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        var shortcut = LumenUi.Text(hint, 12);
        shortcut.Foreground = LumenTheme.Brush("Muted");
        shortcut.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(shortcut, 2);
        layout.Children.Add(shortcut);
        row.Content = layout;
        row.Resources["ButtonBackgroundPointerOver"] = LumenTheme.Brush("Hover");
        row.Resources["ButtonBackgroundPressed"] = LumenTheme.Brush("ControlStrong");
        AutomationProperties.SetName(row, label);
        return row;
    }

    private Border Avatar(double size, double fontSize)
    {
        var initial = StringInfo.GetNextTextElement(_userName.Trim());
        var text = LumenUi.Text(initial.ToUpperInvariant(), fontSize);
        text.Foreground = LumenTheme.Brush("ImageInk");
        text.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.TextWrapping = TextWrapping.NoWrap;
        AutomationProperties.SetAccessibilityView(text, AccessibilityView.Raw);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new(size / 2), Background = LumenTheme.Brush("Avatar"), Child = text,
            IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private void OpenPopup(Button trigger, StackPanel content, IReadOnlyList<Button> controls)
    {
        const double width = 268;
        if (ReferenceEquals(_popupTrigger, trigger) && IsMenuOpen) { DismissMenus(); return; }
        var openingFocus = trigger.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic;
        DismissMenus();
        if (XamlRoot is null) return;
        var frame = new Border
        {
            Width = width, Padding = new(6), CornerRadius = new(16), BorderThickness = new(1),
            Background = LumenTheme.Brush("Pop"), BorderBrush = LumenTheme.Brush("LineStrong"), Child = content,
            RequestedTheme = LumenTheme.IsDark ? ElementTheme.Dark : ElementTheme.Light,
            TabFocusNavigation = KeyboardNavigationMode.Cycle
        };
        AutomationProperties.SetName(frame, Tr("Account"));
        var bounds = trigger.TransformToVisual(null).TransformBounds(new Rect(0, 0, trigger.ActualWidth, trigger.ActualHeight));
        var left = bounds.Right - width;
        var popup = new Popup
        {
            XamlRoot = XamlRoot, Child = frame, IsLightDismissEnabled = true,
            HorizontalOffset = Math.Clamp(left, 8, Math.Max(8, XamlRoot.Size.Width - width - 8)), VerticalOffset = 76
        };
        _popup = popup;
        _popupTrigger = trigger;
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup)) { _popup = null; _popupTrigger = null; }
            if (trigger.IsLoaded && trigger.IsEnabled) trigger.Focus(FocusState.Programmatic);
            UpdateAppearance();
        };
        frame.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; popup.IsOpen = false; return; }
            if (controls.Count == 0 || args.Key is not (VirtualKey.Down or VirtualKey.Up or VirtualKey.Home or VirtualKey.End or VirtualKey.Tab)) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot);
            var current = controls.ToList().FindIndex(button => ReferenceEquals(button, focused));
            var backwards = args.Key == VirtualKey.Up || args.Key == VirtualKey.Tab
                && (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
            var next = args.Key == VirtualKey.Home ? 0 : args.Key == VirtualKey.End ? controls.Count - 1
                : (current + (backwards ? controls.Count - 1 : 1)) % controls.Count;
            args.Handled = true;
            controls[next].Focus(FocusState.Keyboard);
        };
        frame.Loaded += (_, _) =>
        {
            if (controls.Count > 0) controls[0].Focus(openingFocus);
            AnimatePopup(frame);
        };
        popup.IsOpen = true;
        UpdateAppearance();
    }

    private void AnimatePopup(FrameworkElement content)
    {
        if (!AnimationsEnabled()) return;
        content.RenderTransform = new TranslateTransform();
        var story = new Storyboard();
        var opacity = new DoubleAnimation { From = 0, To = 1, Duration = new(TimeSpan.FromMilliseconds(200)) };
        Storyboard.SetTarget(opacity, content);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        story.Children.Add(opacity);
        var translate = new DoubleAnimation
        {
            From = -6, To = 0, Duration = new(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(translate, content);
        Storyboard.SetTargetProperty(translate, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        story.Children.Add(translate);
        story.Begin();
    }

    private void ThemeChanged(object? sender, EventArgs args) { DismissMenus(); RefreshLabels(); UpdateAppearance(); }
    private void HighContrastChanged(AccessibilitySettings sender, object args) => QueueAppearanceUpdate();
    private void SystemColorsChanged(UISettings sender, object args) => QueueAppearanceUpdate();
    private void QueueAppearanceUpdate() => DispatcherQueue.TryEnqueue(() => { if (IsLoaded) UpdateAppearance(); });

    private void SubscribeSystemPreferences()
    {
        if (!_highContrastSubscribed)
        {
            try { _accessibilitySettings.HighContrastChanged += HighContrastChanged; _highContrastSubscribed = true; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
        if (!_systemColorsSubscribed)
        {
            try { _uiSettings.ColorValuesChanged += SystemColorsChanged; _systemColorsSubscribed = true; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
    }

    private void UnsubscribeSystemPreferences()
    {
        if (_highContrastSubscribed)
        {
            try { _accessibilitySettings.HighContrastChanged -= HighContrastChanged; _highContrastSubscribed = false; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
        if (_systemColorsSubscribed)
        {
            try { _uiSettings.ColorValuesChanged -= SystemColorsChanged; _systemColorsSubscribed = false; }
            catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { }
        }
    }

    private bool ReadHighContrast()
    {
        try { return _accessibilitySettings.HighContrast; }
        catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { return false; }
    }

    private bool AnimationsEnabled()
    {
        try { return _uiSettings.AnimationsEnabled; }
        catch (Exception ex) when (IsOptionalPreferenceUnavailable(ex)) { return false; }
    }

    private static bool IsOptionalPreferenceUnavailable(Exception exception) => exception is COMException or InvalidOperationException;

    private void UpdateAppearance()
    {
        try { UpdateAppearanceCore(); }
        catch (Exception exception)
        {
            AcceptanceDiagnostics.Record(exception, "LumenNavigationBar.UpdateAppearance");
            throw;
        }
    }

    private void UpdateAppearanceCore()
    {
        var highContrast = ReadHighContrast();
        var tint = LumenTheme.Color(_onImage ? "ImageGlass" : "Glass");
        _glass.AlwaysUseFallback = highContrast;
        if (highContrast)
        {
            _systemWindow.Color = _uiSettings.UIElementColor(UIElementType.Window);
            _systemText.Color = _uiSettings.UIElementColor(UIElementType.WindowText);
            _systemHighlight.Color = _uiSettings.UIElementColor(UIElementType.Highlight);
            _systemHighlightText.Color = _uiSettings.UIElementColor(UIElementType.HighlightText);
        }
        _glass.FallbackColor = highContrast ? _systemWindow.Color : ColorHelper.FromArgb(255, tint.R, tint.G, tint.B);
        _surface.Background = highContrast ? _systemWindow : LumenTheme.Brush(_onImage ? "ImageGlass" : "Glass");
        _surface.BorderBrush = highContrast ? _systemText : LumenTheme.Brush(_onImage ? "ImageLine" : "LineStrong");
        _divider.Background = _surface.BorderBrush;
        _indicator.Background = highContrast ? _systemHighlight : LumenTheme.Brush(_onImage ? "ImageNavSelected" : "Ink");
        foreach (var pair in _buttons)
        {
            var active = pair.Key == _activeButton;
            var foreground = highContrast ? active ? _systemHighlightText : _systemText
                : active ? LumenTheme.Brush(_onImage ? "ImageNavSelectedInk" : "Background")
                : LumenTheme.Brush(_onImage ? "ImageNavSub" : "Sub");
            var appearance = _buttonAppearance[pair.Key];
            appearance.Foreground.Color = foreground.Color;
            appearance.Hover.Color = highContrast ? active ? _systemHighlight.Color : _systemWindow.Color
                : active ? Colors.Transparent : LumenTheme.Color(_onImage ? "ImageControl" : "Hover");
            appearance.Pressed.Color = highContrast ? _systemHighlight.Color : LumenTheme.Color("ControlStrong");
            pair.Value.FontWeight = active ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Medium;
            AutomationProperties.SetItemStatus(pair.Value, active ? LumenText.Get("Selected") : string.Empty);
            if (pair.Value.Content is TextBlock text) { text.FontWeight = pair.Value.FontWeight; text.Foreground = appearance.Foreground; }
            if (pair.Key == "search")
                pair.Value.Content = new FontIcon
                {
                    Glyph = "\uE721", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16,
                    Foreground = appearance.Foreground, IsHitTestVisible = false
                };
            else if (pair.Key == "settings")
            {
                var avatar = Avatar(24, 11);
                avatar.BorderThickness = new(ReferenceEquals(_popupTrigger, pair.Value) && IsMenuOpen ? 2 : 0);
                avatar.BorderBrush = LumenTheme.Brush(_onImage ? "ImageInk" : "Ink");
                pair.Value.Content = avatar;
            }
        }
    }

    private static string Tr(string key, params object[] values)
    {
        var translation = key switch
        {
            "TV shows" => "\u5267\u96c6",
            "My favorites" => "\u6211\u7684\u6536\u85cf",
            "Account" => "\u8d26\u6237",
            "Switch user" => "\u5207\u6362\u7528\u6237",
            "Keyboard shortcuts" => "\u952e\u76d8\u5feb\u6377\u952e",
            _ => null
        };
        var text = translation is null ? LumenText.Get(key) : LumenText.Localize(key, translation);
        return values.Length == 0 ? text : string.Format(CultureInfo.CurrentCulture, text, values);
    }
}
