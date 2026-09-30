using EmbyClient.App.Services;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenNavigationBar : UserControl
{
    private readonly Border _surface;
    private readonly Border _indicator;
    private readonly Border _divider;
    private readonly AcrylicBrush _glass = new()
    {
        TintColor = Colors.Transparent,
        TintOpacity = 0,
        TintLuminosityOpacity = 0,
        TintTransitionDuration = TimeSpan.Zero
    };
    private readonly AccessibilitySettings _accessibilitySettings = new();
    private readonly UISettings _uiSettings = new();
    private readonly SolidColorBrush _systemWindow = new();
    private readonly SolidColorBrush _systemText = new();
    private readonly SolidColorBrush _systemHighlight = new();
    private readonly SolidColorBrush _systemHighlightText = new();
    private readonly TranslateTransform _indicatorPosition = new();
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private Storyboard? _movement;
    private string _selected = "home";
    private bool _onImage;
    private bool _themeSubscribed;
    private bool _highContrastSubscribed;
    private bool _systemColorsSubscribed;
    public event EventHandler<string>? NavigationRequested;

    public LumenNavigationBar()
    {
        Width = 354;
        Height = 44;
        var layout = new Grid { Width = 346, Height = 34 };
        _surface = new Border
        {
            Padding = new(4), CornerRadius = new(24), BorderThickness = new(1),
            Background = LumenTheme.Brush("Glass"), BorderBrush = LumenTheme.Brush("LineStrong"), Child = layout
        };
        _indicator = new Border
        {
            Width = 64, Height = 34, CornerRadius = new(18), HorizontalAlignment = HorizontalAlignment.Left,
            Background = LumenTheme.Brush("Ink"), RenderTransform = _indicatorPosition, IsHitTestVisible = false
        };
        layout.Children.Add(_indicator);
        var keys = new[] { "home", "movies", "series", "favs" };
        var names = new[] { "Home", "Movies", "Series", "Favorites" };
        for (var index = 0; index < keys.Length; index++)
        {
            var button = CreateButton(keys[index], LumenText.Get(names[index]), index * 66, 64);
            button.Content = new TextBlock
            {
                Text = LumenText.Get(names[index]), FontSize = 14, FontFamily = LumenTheme.SansFont,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap
            };
            layout.Children.Add(button);
        }
        _divider = new Border
        {
            Width = 1, Height = 18, Margin = new(271, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center, Background = LumenTheme.Brush("LineStrong"), IsHitTestVisible = false
        };
        layout.Children.Add(_divider);
        var search = CreateButton("search", LumenText.Get("Search"), 275, 34);
        search.Content = LumenUi.Icon("search_20_regular", 18);
        layout.Children.Add(search);
        var account = CreateButton("settings", LumenText.Get("Settings"), 311, 34);
        account.Content = LumenUi.Icon("person_20_regular", 18);
        layout.Children.Add(account);
        var layers = new Grid();
        // The color overlay uses source-over alpha; Acrylic's color blend would alter the design tint.
        layers.Children.Add(new Border { CornerRadius = new(24), Background = _glass, IsHitTestVisible = false });
        layers.Children.Add(_surface);
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
            _movement?.Stop();
            if (_themeSubscribed) LumenTheme.Changed -= ThemeChanged;
            _themeSubscribed = false;
            UnsubscribeSystemPreferences();
        };
        UpdateAppearance();
    }

    private Button CreateButton(string key, string label, double left, double width)
    {
        var button = new Button
        {
            Width = width, Height = 34, MinWidth = 0, MinHeight = 0, Padding = new(0), CornerRadius = new(18),
            BorderThickness = new(0), Background = new SolidColorBrush(Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new(left, 0, 0, 0), FontSize = 14,
            FontFamily = LumenTheme.SansFont
        };
        button.Resources["ButtonBackgroundPointerOver"] = LumenTheme.Brush("Control");
        button.Resources["ButtonBackgroundPressed"] = LumenTheme.Brush("ControlStrong");
        AutomationProperties.SetName(button, label);
        AutomationProperties.SetAutomationId(button, "LumenNav_" + key);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => NavigationRequested?.Invoke(this, key);
        _buttons.Add(key, button);
        return button;
    }

    public void Select(string key, bool onImage)
    {
        if (!_buttons.ContainsKey(key)) key = "home";
        if (_selected == key && _onImage == onImage) return;
        _selected = key;
        _onImage = onImage;
        var target = key switch { "movies" => 66, "series" => 132, "favs" => 198, "search" => 275, "settings" => 311, _ => 0 };
        var width = key is "search" or "settings" ? 34d : 64d;
        var oldX = _indicatorPosition.X;
        var oldWidth = _indicator.Width;
        _movement?.Stop();
        _indicatorPosition.X = target;
        _indicator.Width = width;
        if (IsLoaded && _uiSettings.AnimationsEnabled)
        {
            _movement = new Storyboard();
            var movement = new DoubleAnimation
            {
                From = oldX, To = target, Duration = new(TimeSpan.FromMilliseconds(550)),
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(movement, _indicator);
            Storyboard.SetTargetProperty(movement, "(UIElement.RenderTransform).(TranslateTransform.X)");
            _movement.Children.Add(movement);
            var sizing = new DoubleAnimation
            {
                From = oldWidth, To = width, Duration = new(TimeSpan.FromMilliseconds(550)),
                EnableDependentAnimation = true, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(sizing, _indicator);
            Storyboard.SetTargetProperty(sizing, "Width");
            _movement.Children.Add(sizing);
            _movement.Begin();
        }
        UpdateAppearance();
    }

    public void SetEnabled(bool enabled)
    {
        foreach (var button in _buttons.Values) button.IsEnabled = enabled;
    }

    public void FocusSelected() => _buttons[_selected].Focus(FocusState.Programmatic);

    private void ThemeChanged(object? sender, EventArgs args) => UpdateAppearance();

    private void HighContrastChanged(AccessibilitySettings sender, object args) => QueueAppearanceUpdate();
    private void SystemColorsChanged(UISettings sender, object args) => QueueAppearanceUpdate();

    private void QueueAppearanceUpdate() => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsLoaded) UpdateAppearance();
    });

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

    private static bool IsOptionalPreferenceUnavailable(Exception exception) => exception is COMException or InvalidOperationException;

    private void UpdateAppearance()
    {
        var highContrast = ReadHighContrast();
        var tint = LumenTheme.Color(_onImage ? "ImageGlass" : "Glass");
        var opaqueTint = ColorHelper.FromArgb(255, tint.R, tint.G, tint.B);
        _glass.AlwaysUseFallback = highContrast;
        if (highContrast)
        {
            _systemWindow.Color = _uiSettings.UIElementColor(UIElementType.Window);
            _systemText.Color = _uiSettings.UIElementColor(UIElementType.WindowText);
            _systemHighlight.Color = _uiSettings.UIElementColor(UIElementType.Highlight);
            _systemHighlightText.Color = _uiSettings.UIElementColor(UIElementType.HighlightText);
        }
        // Acrylic handles transparency, battery-saver, and GPU policy fallback natively.
        _glass.FallbackColor = highContrast ? _systemWindow.Color : opaqueTint;
        _surface.Background = highContrast ? _systemWindow : LumenTheme.Brush(_onImage ? "ImageGlass" : "Glass");
        _surface.BorderBrush = highContrast ? _systemText : LumenTheme.Brush(_onImage ? "ImageLine" : "LineStrong");
        _divider.Background = _surface.BorderBrush;
        _indicator.Background = highContrast ? _systemHighlight : LumenTheme.Brush(_onImage ? "ImageNavSelected" : "Ink");
        foreach (var pair in _buttons)
        {
            var active = pair.Key == _selected;
            pair.Value.Foreground = highContrast
                ? active ? _systemHighlightText : _systemText
                : active
                    ? LumenTheme.Brush(_onImage ? "ImageNavSelectedInk" : "Background")
                    : LumenTheme.Brush(_onImage ? "ImageNavSub" : "Sub");
            pair.Value.Resources["ButtonBackgroundPointerOver"] = highContrast
                ? active ? _systemHighlight : _systemWindow
                : LumenTheme.Brush("Control");
            pair.Value.Resources["ButtonBackgroundPressed"] = highContrast
                ? active ? _systemHighlight : _systemWindow
                : LumenTheme.Brush("ControlStrong");
            pair.Value.Resources["ButtonForegroundPointerOver"] = pair.Value.Foreground;
            pair.Value.Resources["ButtonForegroundPressed"] = pair.Value.Foreground;
            pair.Value.FontWeight = active ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Medium;
            if (pair.Value.Content is TextBlock text) text.FontWeight = pair.Value.FontWeight;
            AutomationProperties.SetItemStatus(pair.Value, active ? LumenText.Get("Selected") : string.Empty);
            if (pair.Key is "search" or "settings")
            {
                if (highContrast)
                {
                    var icon = new FontIcon
                    {
                        Glyph = pair.Key == "search" ? "\uE721" : "\uE77B", FontFamily = new FontFamily("Segoe Fluent Icons"),
                        FontSize = pair.Key == "search" ? 16 : 18, Width = 18, Height = 18,
                        Foreground = pair.Value.Foreground, IsHitTestVisible = false
                    };
                    AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
                    pair.Value.Content = icon;
                    continue;
                }
                var white = active ? !_onImage && !LumenTheme.IsDark : _onImage || LumenTheme.IsDark;
                var file = pair.Key == "search" ? "search_20_regular" : "person_20_regular";
                var image = new Image
                {
                    Width = pair.Key == "search" ? 16 : 18, Height = pair.Key == "search" ? 16 : 18, IsHitTestVisible = false,
                    Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/{(white ? "w" : "d")}/{file}.svg"))
                };
                AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
                pair.Value.Content = image;
            }
        }
    }
}
