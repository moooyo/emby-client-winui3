using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT;

namespace EmbyClient.App.Services;

/// <summary>Updates window-owned resource brushes without invalidating existing theme-resource references.</summary>
internal sealed class DetailAppearanceResources
{
    private readonly ResourceDictionary _resources = Application.Current.Resources;

    internal void Apply(ElementTheme theme, bool hasArtwork, bool advancedEffects, bool highContrast, UISettings settings)
    {
        var dark = theme == ElementTheme.Dark;
        if (highContrast)
        {
            ApplyHighContrast(settings);
            return;
        }

        var surface = dark ? Rgb(32, 32, 32) : Rgb(250, 250, 250);
        var baseColor = dark ? Rgb(32, 32, 32) : Rgb(243, 243, 243);
        var translucent = hasArtwork && advancedEffects;
        Set("DetailCardBackgroundBrush", translucent
            ? dark ? Rgb(17, 23, 27, .18) : Rgb(255, 255, 255, .28)
            : advancedEffects ? dark ? Rgb(255, 255, 255, .05) : Rgb(255, 255, 255, .70)
            : dark ? Rgb(43, 43, 43) : Rgb(255, 255, 255));
        Set("DetailCardStrokeBrush", translucent
            ? dark ? Rgb(255, 255, 255, .06) : Rgb(14, 32, 44, .065)
            : dark ? Rgb(0, 0, 0, .10) : Rgb(0, 0, 0, .06));
        Set("DetailDividerBrush", translucent
            ? dark ? Rgb(255, 255, 255, .075) : Rgb(14, 32, 44, .075)
            : dark ? Rgb(255, 255, 255, .07) : Rgb(0, 0, 0, .06));
        Set("DetailChipBackgroundBrush", translucent
            ? dark ? Rgb(255, 255, 255, .045) : Rgb(255, 255, 255, .26)
            : advancedEffects ? dark ? Rgb(255, 255, 255, .084) : Rgb(249, 249, 249, .50)
            : dark ? Rgb(48, 48, 48) : Rgb(255, 255, 255));
        Set("DetailChipStrokeBrush", translucent
            ? dark ? Rgb(255, 255, 255, .07) : Rgb(14, 32, 44, .075)
            : dark ? Rgb(255, 255, 255, .07) : Rgb(0, 0, 0, .06));
        Set("DetailControlBackgroundBrush", translucent
            ? dark ? Rgb(22, 28, 32, .30) : Rgb(255, 255, 255, .44)
            : advancedEffects ? dark ? Rgb(255, 255, 255, .06) : Rgb(255, 255, 255, .70)
            : dark ? Rgb(48, 48, 48) : Rgb(255, 255, 255));
        Set("DetailControlHoverBrush", translucent
            ? dark ? Rgb(255, 255, 255, .07) : Rgb(255, 255, 255, .62)
            : advancedEffects ? dark ? Rgb(255, 255, 255, .084) : Rgb(249, 249, 249, .50)
            : dark ? Rgb(58, 58, 58) : Rgb(240, 240, 240));
        Set("DetailSelectedBackgroundBrush", translucent
            ? dark ? Rgb(255, 255, 255, .12) : Rgb(255, 255, 255, .66)
            : dark ? Rgb(58, 58, 58) : Rgb(255, 255, 255));
        Set("DetailSecondaryTextBrush", hasArtwork && dark ? Rgb(196, 203, 208)
            : dark ? Rgb(255, 255, 255, .7725) : Rgb(0, 0, 0, .6196));
        Set("DetailContentBackgroundBrush", hasArtwork ? Transparent : surface);
        Set("NavigationViewContentBackground", hasArtwork ? Transparent : surface);
        Set("NavigationViewContentGridBorderBrush", hasArtwork ? Transparent
            : dark ? Rgb(0, 0, 0, .10) : Rgb(0, 0, 0, .06));

        var mask = dark ? Rgb(25, 29, 32) : Rgb(250, 250, 250);
        Set("DetailBackdropBaseBrush", mask);
        SetGradient("DetailBackdropHorizontalMaskBrush", mask,
            dark ? [.79, .77, .56] : [.84, .87, .74], dark ? .34 : .35);
        SetGradient("DetailBackdropVerticalMaskBrush", mask,
            dark ? [.68, .12, .12] : [.82, .20, .20], dark ? .74 : .76);

        var chrome = dark ? Rgb(18, 23, 27) : Rgb(250, 250, 250);
        SetGradient("NavigationViewExpandedPaneBackground", advancedEffects ? chrome : baseColor,
            !advancedEffects ? [1, 1, 1] : hasArtwork ? [.25, .12, 0] : [0, 0, 0], .68);
        Set("DetailTitleBarBackgroundBrush", !advancedEffects ? baseColor : hasArtwork ? WithAlpha(chrome, .20) : Transparent);
        var overlay = _resources["NavigationViewDefaultPaneBackground"].As<AcrylicBrush>();
        overlay.TintColor = hasArtwork ? mask : baseColor;
        overlay.TintOpacity = dark ? .86 : .90;
        overlay.FallbackColor = baseColor;
        overlay.AlwaysUseFallback = !advancedEffects;
    }

    private void ApplyHighContrast(UISettings settings)
    {
        var window = Opaque(settings.UIElementColor(UIElementType.Window));
        var text = Opaque(settings.UIElementColor(UIElementType.WindowText));
        var button = Opaque(settings.UIElementColor(UIElementType.ButtonFace));
        var buttonText = Opaque(settings.UIElementColor(UIElementType.ButtonText));
        foreach (var key in new[] { "DetailCardBackgroundBrush", "DetailContentBackgroundBrush", "DetailBackdropBaseBrush",
                     "DetailTitleBarBackgroundBrush", "NavigationViewContentBackground" }) Set(key, window);
        foreach (var key in new[] { "DetailCardStrokeBrush", "DetailDividerBrush", "DetailSecondaryTextBrush" }) Set(key, text);
        foreach (var key in new[] { "DetailChipBackgroundBrush", "DetailControlBackgroundBrush", "DetailControlHoverBrush",
                     "DetailSelectedBackgroundBrush" }) Set(key, button);
        Set("DetailChipStrokeBrush", buttonText);
        Set("NavigationViewContentGridBorderBrush", Transparent);
        SetGradient("NavigationViewExpandedPaneBackground", window, [1, 1, 1], .68);
        var overlay = _resources["NavigationViewDefaultPaneBackground"].As<AcrylicBrush>();
        overlay.FallbackColor = window;
        overlay.AlwaysUseFallback = true;
    }

    private void Set(string key, Color value) => _resources[key].As<SolidColorBrush>().Color = value;

    private void SetGradient(string key, Color color, double[] opacity, double middleOffset)
    {
        // A native resource can arrive as the base Brush projection in Native AOT.
        // Query its WinRT interface instead of relying on a managed derived-type cast.
        var stops = _resources[key].As<LinearGradientBrush>().GradientStops;
        for (var index = 0; index < stops.Count; index++) stops[index].Color = WithAlpha(color, opacity[index]);
        stops[1].Offset = middleOffset;
    }

    private static Color Transparent => Color.FromArgb(0, 0, 0, 0);
    private static Color Rgb(byte red, byte green, byte blue, double alpha = 1) =>
        Color.FromArgb((byte)Math.Round(alpha * 255), red, green, blue);
    private static Color WithAlpha(Color color, double alpha) => Rgb(color.R, color.G, color.B, alpha);
    private static Color Opaque(Color color) => Rgb(color.R, color.G, color.B);
}
