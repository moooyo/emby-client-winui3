using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenShellPage
{
    private readonly Border _signInBackdropHost = new() { IsHitTestVisible = false };
    private readonly List<CompositionColorGradientStop> _signInBackdropStops = [];
    private ContainerVisual? _signInBackdropVisual;
    private SpriteVisual? _signInWarmBackdropVisual;
    private SpriteVisual? _signInCoolBackdropVisual;
    private CompositionRadialGradientBrush? _signInWarmBackdropBrush;
    private CompositionRadialGradientBrush? _signInCoolBackdropBrush;

    private void BuildSignInBackdrop()
    {
        if (_signInBackdropVisual is not null || _closing) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_signInBackdropHost).Compositor;
        _signInWarmBackdropBrush = CreateSignInRadialBrush(compositor, new Vector2(.12f, -.1f), Color.FromArgb(255, 226, 184, 107));
        _signInCoolBackdropBrush = CreateSignInRadialBrush(compositor, new Vector2(1.05f, 1.15f), Color.FromArgb(255, 169, 184, 232));
        _signInWarmBackdropVisual = compositor.CreateSpriteVisual();
        _signInWarmBackdropVisual.RelativeSizeAdjustment = Vector2.One;
        _signInWarmBackdropVisual.Brush = _signInWarmBackdropBrush;
        // Float opacity preserves the prototype's alpha without 8-bit color rounding.
        _signInWarmBackdropVisual.Opacity = .14f;
        _signInCoolBackdropVisual = compositor.CreateSpriteVisual();
        _signInCoolBackdropVisual.RelativeSizeAdjustment = Vector2.One;
        _signInCoolBackdropVisual.Brush = _signInCoolBackdropBrush;
        _signInCoolBackdropVisual.Opacity = .07f;
        _signInBackdropVisual = compositor.CreateContainerVisual();
        _signInBackdropVisual.Children.InsertAtTop(_signInCoolBackdropVisual);
        _signInBackdropVisual.Children.InsertAtTop(_signInWarmBackdropVisual);
        ElementCompositionPreview.SetElementChildVisual(_signInBackdropHost, _signInBackdropVisual);
        UpdateSignInBackdrop();
    }

    private CompositionRadialGradientBrush CreateSignInRadialBrush(Compositor compositor, Vector2 center, Color color)
    {
        var brush = compositor.CreateRadialGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.EllipseCenter = center;
        var origin = compositor.CreateColorGradientStop(0, color);
        var edge = compositor.CreateColorGradientStop(.7f, Color.FromArgb(0, color.R, color.G, color.B));
        brush.ColorStops.Add(origin);
        brush.ColorStops.Add(edge);
        _signInBackdropStops.Add(origin);
        _signInBackdropStops.Add(edge);
        return brush;
    }

    private void UpdateSignInBackdrop()
    {
        if (_signInBackdropVisual is null || _signInWarmBackdropBrush is null || _signInCoolBackdropBrush is null
            || _connectionPane.ActualWidth <= 0 || _connectionPane.ActualHeight <= 0) return;
        var size = new Vector2((float)_connectionPane.ActualWidth, (float)_connectionPane.ActualHeight);
        _signInBackdropVisual.Size = size;
        // Relative mapping keeps percentage centers while these radii remain fixed in DIPs.
        _signInWarmBackdropBrush.EllipseRadius = new Vector2(900 / size.X, 620 / size.Y);
        _signInCoolBackdropBrush.EllipseRadius = new Vector2(900 / size.X, 700 / size.Y);
    }

    private void ReleaseSignInBackdrop()
    {
        ElementCompositionPreview.SetElementChildVisual(_signInBackdropHost, null);
        _signInBackdropVisual?.Children.RemoveAll();
        if (_signInWarmBackdropVisual is not null) _signInWarmBackdropVisual.Brush = null;
        if (_signInCoolBackdropVisual is not null) _signInCoolBackdropVisual.Brush = null;
        _signInWarmBackdropVisual?.Dispose();
        _signInCoolBackdropVisual?.Dispose();
        _signInWarmBackdropBrush?.ColorStops.Clear();
        _signInCoolBackdropBrush?.ColorStops.Clear();
        foreach (var stop in _signInBackdropStops) stop.Dispose();
        _signInBackdropStops.Clear();
        _signInWarmBackdropBrush?.Dispose();
        _signInCoolBackdropBrush?.Dispose();
        _signInBackdropVisual?.Dispose();
        _signInWarmBackdropVisual = _signInCoolBackdropVisual = null;
        _signInWarmBackdropBrush = _signInCoolBackdropBrush = null;
        _signInBackdropVisual = null;
    }
}
