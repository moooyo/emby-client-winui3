using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EmbyClient.App.Services;

/// <summary>Shared brush identities keep the native interface in sync during live theme changes.</summary>
public static class LumenTheme
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.Ordinal);
    public static bool IsDark { get; private set; } = true;
    public static string AccentName { get; private set; } = "Gold";
    public static FontFamily SerifFont { get; } = new("ms-appx:///Assets/Lumen/Fonts/NotoSerifSC-Variable.ttf#Noto Serif SC");
    public static FontFamily SansFont { get; } = new("ms-appx:///Assets/Lumen/Fonts/Manrope-Variable.ttf#Manrope, Microsoft YaHei UI");
    public static event EventHandler? Changed;

    public static SolidColorBrush Brush(string key)
    {
        if (Brushes.Count == 0) Apply("Dark", "Gold");
        return Brushes.TryGetValue(key, out var brush) ? brush : Brushes["Ink"];
    }

    public static Color Color(string key) => Brush(key).Color;

    public static void Apply(string theme, string accent)
    {
        IsDark = theme != "Light";
        AccentName = accent;
        Set("Background", IsDark ? "#0C0B0A" : "#F4F1EB");
        Set("Ink", IsDark ? "#F6F2EA" : "#1B1815");
        Set("Sub", IsDark ? "#BFB8AC" : "#524A40");
        Set("Muted", IsDark ? "#8E877C" : "#756C60");
        Set("Line", IsDark ? "#14FFFFFF" : "#12000000");
        Set("LineStrong", IsDark ? "#24FFFFFF" : "#24000000");
        Set("Card", IsDark ? "#0BFFFFFF" : "#FFFFFFFF");
        Set("Control", IsDark ? "#0FFFFFFF" : "#FFFFFFFF");
        Set("ControlStrong", IsDark ? "#24FFFFFF" : "#12000000");
        Set("Glass", IsDark ? "#9E1E1B18" : "#B8FFFFFF");
        Set("Pop", IsDark ? "#E61C1A17" : "#F0FFFFFF");
        Set("Accent", (IsDark, accent) switch
        {
            (true, "Coral") => "#E8866A", (false, "Coral") => "#A8452A",
            (true, "Sage") => "#9FC2A4", (false, "Sage") => "#3D7050",
            (true, "Blue") or (true, "MistBlue") => "#A9B8E8",
            (false, "Blue") or (false, "MistBlue") => "#3F55A8",
            (true, _) => "#E2B86B", _ => "#8F6212"
        });
        Set("AccentInk", IsDark ? "#1F1606" : "#FFFFFF");
        Set("ImageAccent", accent switch
        {
            "Coral" => "#E8866A", "Sage" => "#9FC2A4", "Blue" or "MistBlue" => "#A9B8E8", _ => "#E2B86B"
        });
        Set("ImageAccentInk", "#1F1606");
        Set("Star", "#E9C46A");
        Set("Danger", "#D9534A");
        Set("Online", "#3FCF7A");
        Set("ImageInk", "#FFFFFF");
        Set("ImageSub", "#BFFFFFFF");
        Set("ImageGlass", "#61141210");
        Set("ImageLine", "#29FFFFFF");
        Set("ImageNavSelected", "#F0FFFFFF");
        Set("ImageNavSelectedInk", "#15120F");
        Set("ImageNavSub", "#D1FFFFFF");
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static LinearGradientBrush ImageFade(bool horizontal = false, bool pageBottom = false)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = horizontal ? new(1, 0) : new(0, 1)
        };
        if (horizontal)
        {
            brush.GradientStops.Add(new() { Color = Parse("#E60A0806"), Offset = 0 });
            brush.GradientStops.Add(new() { Color = Parse("#990A0806"), Offset = 0.30 });
            brush.GradientStops.Add(new() { Color = Parse("#1A0A0806"), Offset = 0.62 });
            brush.GradientStops.Add(new() { Color = Parse("#000A0806"), Offset = 0.85 });
        }
        else if (pageBottom)
        {
            var background = Color("Background");
            var transparentBackground = ColorHelper.FromArgb(0, background.R, background.G, background.B);
            brush.GradientStops.Add(new() { Color = transparentBackground, Offset = 0 });
            brush.GradientStops.Add(new() { Color = transparentBackground, Offset = IsDark ? 0.62 : 0.87 });
            brush.GradientStops.Add(new() { Color = background, Offset = 1 });
        }
        else
        {
            brush.GradientStops.Add(new() { Color = Parse("#80000000"), Offset = 0 });
            brush.GradientStops.Add(new() { Color = Parse("#00000000"), Offset = 1 });
        }
        return brush;
    }

    private static void Set(string key, string value)
    {
        if (!Brushes.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush();
            Brushes[key] = brush;
            Application.Current.Resources["Lumen" + key + "Brush"] = brush;
        }
        brush.Color = Parse(value);
    }

    private static Color Parse(string value)
    {
        var hex = value.AsSpan(1);
        var number = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        if (hex.Length == 6) number |= 0xFF000000;
        return ColorHelper.FromArgb((byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number);
    }
}
