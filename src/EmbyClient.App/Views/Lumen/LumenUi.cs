using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Numerics;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public static class LumenUi
{
    private static readonly Dictionary<string, string> IconNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = "play_24_filled", ["pause"] = "pause_24_filled",
        ["search"] = "search_20_regular", ["arrow-left"] = "arrow_left_20_regular",
        ["back"] = "arrow_left_20_regular", ["chevron-down"] = "chevron_down_16_regular",
        ["chevron-up"] = "chevron_down_16_regular",
        ["chevron-left"] = "chevron_left_20_regular", ["chevron-right"] = "chevron_right_20_regular",
        ["heart"] = "heart_20_regular", ["heart-filled"] = "heart_20_filled",
        ["check"] = "checkmark_20_regular", ["check-filled"] = "checkmark_16_filled",
        ["plus"] = "add_20_regular", ["minus"] = "subtract_16_regular",
        ["ellipsis"] = "more_horizontal_20_regular", ["more"] = "more_horizontal_20_regular",
        ["info"] = "info_20_regular", ["x"] = "dismiss_20_regular",
        ["close"] = "dismiss_20_regular", ["square"] = "square_16_regular",
        ["circle-user-round"] = "person_20_regular", ["user"] = "person_20_regular",
        ["log-out"] = "sign_out_20_regular", ["sign-out"] = "sign_out_20_regular",
        ["sliders-horizontal"] = "filter_20_regular", ["filter"] = "filter_20_regular",
        ["arrow-up-down"] = "arrow_sort_20_regular", ["sort"] = "arrow_sort_20_regular",
        ["layout-grid"] = "grid_20_regular", ["grid"] = "grid_20_regular",
        ["list"] = "text_bullet_list_ltr_20_regular", ["volume-2"] = "speaker_2_24_regular",
        ["volume"] = "speaker_2_24_regular", ["music"] = "speaker_2_20_regular",
        ["captions"] = "closed_caption_24_regular", ["subtitles"] = "closed_caption_24_regular",
        ["maximize"] = "full_screen_maximize_24_regular", ["fullscreen"] = "full_screen_maximize_24_regular",
        ["picture-in-picture-2"] = "picture_in_picture_24_regular", ["pip"] = "picture_in_picture_24_regular",
        ["rotate-ccw"] = "skip_back_10_24_regular", ["rewind"] = "skip_back_10_24_regular",
        ["rotate-cw"] = "skip_forward_30_24_regular", ["forward"] = "skip_forward_30_24_regular",
        ["refresh"] = "skip_forward_30_24_regular", ["refresh-cw"] = "skip_forward_30_24_regular",
        ["skip-forward"] = "next_24_filled", ["next"] = "next_24_filled",
        ["film"] = "video_20_regular", ["video"] = "video_20_regular",
        ["tv"] = "tv_20_regular", ["camera"] = "movies_and_tv_20_regular",
        ["palette"] = "paint_brush_20_regular", ["history"] = "history_20_regular",
        ["timer"] = "clock_20_regular", ["clock"] = "clock_20_regular",
        ["monitor"] = "desktop_20_regular", ["globe"] = "globe_20_regular",
        ["cpu"] = "options_20_regular", ["sun"] = "hd_20_regular",
        ["gauge"] = "top_speed_24_regular", ["speed"] = "top_speed_24_regular",
        ["library"] = "library_20_regular", ["server"] = "server_20_regular",
        ["keyboard"] = "keyboard_20_regular", ["headphones"] = "headphones_20_regular",
        ["list-video"] = "apps_list_20_regular", ["queue"] = "apps_list_20_regular",
        ["clapperboard"] = "video_clip_20_regular", ["trailer"] = "video_clip_20_regular",
        ["type"] = "text_font_20_regular", ["settings"] = "settings_20_regular"
    };

    public static FontFamily SansFont => LumenTheme.SansFont;
    public static FontFamily SerifFont => LumenTheme.SerifFont;

    public static TextBlock Text(string text, double size = 14, bool serif = false) => new()
    {
        Text = text, FontFamily = serif ? SerifFont : SansFont, FontSize = size,
        FontWeight = serif ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        Foreground = LumenTheme.Brush("Ink"), TextWrapping = TextWrapping.Wrap, CharacterSpacing = 0
    };

    public static FrameworkElement Icon(string name, double size = 20, bool onImage = false) => IconCore(name, size, onImage, false);

    private static FrameworkElement IconCore(string name, double size, bool onImage, bool inverse)
    {
        var image = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        if (name == "chevron-up")
        {
            image.RenderTransformOrigin = new(0.5, 0.5);
            image.RenderTransform = new RotateTransform { Angle = 180 };
        }
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        var file = IconNames.GetValueOrDefault(name, name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name);
        var ink = LumenTheme.Brush("Ink");
        long? themeToken = null;
        void Refresh()
        {
            var dark = ink.Color.R + ink.Color.G + ink.Color.B > 380;
            var white = onImage ? !inverse : dark != inverse;
            var directory = name == "star" ? "y" : white ? "w" : "d";
            var filename = name == "star" ? "star_16_filled" : file;
            var scale = image.XamlRoot?.RasterizationScale ?? 1;
            image.Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Lumen/Icons/{directory}/{filename}.svg"))
            { RasterizePixelWidth = size * scale, RasterizePixelHeight = size * scale };
        }
        Refresh();
        image.Loaded += (_, _) =>
        {
            Refresh();
            if (!onImage && name != "star" && themeToken is null)
                themeToken = ink.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => Refresh());
        };
        image.Unloaded += (_, _) =>
        {
            if (themeToken is { } token) ink.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, token);
            themeToken = null;
        };
        return image;
    }

    public static Button Button(string text, string? icon = null, bool primary = false, bool onImage = false, double height = 44)
    {
        text = LumenText.Get(text);
        var foreground = primary ? onImage ? DarkImageBrush : LumenTheme.Brush("Background")
            : onImage ? WhiteBrush : LumenTheme.Brush("Ink");
        var background = primary ? onImage ? WhiteBrush : LumenTheme.Brush("Ink")
            : onImage ? ImageControlBrush : LumenTheme.Brush("Control");
        var result = new Button
        {
            Height = height, MinHeight = height, MinWidth = height,
            Padding = new Thickness(icon is null ? 20 : 16, 0, 20, 0),
            CornerRadius = new CornerRadius(height / 2), Background = background, Foreground = foreground,
            BorderBrush = primary ? background : onImage ? ImageStrokeBrush : LumenTheme.Brush("LineStrong"),
            BorderThickness = new Thickness(primary ? 0 : 1),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            FontFamily = SansFont, FontSize = 14, UseSystemFocusVisuals = true
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            content.Children.Add(IconCore(icon, 18, onImage, primary));
        }
        var label = Text(text, 14);
        label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        label.Foreground = foreground;
        label.TextWrapping = TextWrapping.NoWrap;
        label.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(label);
        result.Content = content;
        ApplyButtonResources(result, background, foreground, primary, onImage);
        AutomationProperties.SetName(result, text);
        return result;
    }

    public static Button IconButton(string icon, string label, double size = 44, bool onImage = false)
    {
        label = LumenText.Get(label);
        var result = new Button
        {
            Width = size, Height = size, MinWidth = size, MinHeight = size,
            Padding = new Thickness(0), CornerRadius = new CornerRadius(size / 2),
            Background = onImage ? ImageControlBrush : LumenTheme.Brush("Control"),
            Foreground = onImage ? WhiteBrush : LumenTheme.Brush("Ink"),
            BorderBrush = onImage ? ImageStrokeBrush : LumenTheme.Brush("LineStrong"),
            BorderThickness = new Thickness(1), Content = Icon(icon, Math.Min(20, size * .45), onImage),
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = true
        };
        ApplyButtonResources(result, result.Background, result.Foreground, false, onImage);
        AutomationProperties.SetName(result, label);
        ToolTipService.SetToolTip(result, label);
        return result;
    }

    public static Border Divider() => new() { Height = 1, Background = LumenTheme.Brush("Line") };

    public static void AnimateEntrance(FrameworkElement element)
    {
        if (!element.IsLoaded)
        {
            RoutedEventHandler? loaded = null;
            loaded = (_, _) => { element.Loaded -= loaded; AnimateEntrance(element); };
            element.Loaded += loaded;
            return;
        }
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1));
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0, 0); opacity.InsertKeyFrame(1, 1);
        opacity.Duration = TimeSpan.FromMilliseconds(450);
        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0, new Vector3(0, 16, 0));
        translation.InsertKeyFrame(1, Vector3.Zero, ease);
        translation.Duration = TimeSpan.FromMilliseconds(600);
        visual.StartAnimation("Opacity", opacity);
        visual.Properties.StartAnimation("Translation", translation);
    }

    internal static readonly SolidColorBrush WhiteBrush = LumenTheme.Brush("ImageInk");
    internal static readonly SolidColorBrush DarkImageBrush = new(Color.FromArgb(255, 21, 18, 15));
    internal static readonly SolidColorBrush ImageControlBrush = new(Color.FromArgb(36, 255, 255, 255));
    internal static readonly SolidColorBrush ImageStrokeBrush = LumenTheme.Brush("ImageLine");

    private static void ApplyButtonResources(Button button, Brush background, Brush foreground, bool primary, bool onImage)
    {
        button.Resources["ButtonBackground"] = background;
        button.Resources["ButtonForeground"] = foreground;
        button.Resources["ButtonForegroundPointerOver"] = foreground;
        button.Resources["ButtonForegroundPressed"] = foreground;
        button.Resources["ButtonBackgroundPointerOver"] = primary ? background
            : onImage ? new SolidColorBrush(Color.FromArgb(56, 255, 255, 255)) : LumenTheme.Brush("ControlStrong");
        button.Resources["ButtonBackgroundPressed"] = primary ? background
            : onImage ? new SolidColorBrush(Color.FromArgb(76, 255, 255, 255)) : LumenTheme.Brush("ControlStrong");
        button.Resources["ButtonBorderBrushPointerOver"] = onImage ? ImageStrokeBrush : LumenTheme.Brush("LineStrong");
        button.Resources["ButtonBorderBrushPressed"] = onImage ? ImageStrokeBrush : LumenTheme.Brush("LineStrong");
    }
}
