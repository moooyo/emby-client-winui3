using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private static void SetRoundedHomeClip(FrameworkElement element, float radius)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        geometry.CornerRadius = new Vector2(radius);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }

    private static void AttachHomeShadow(Grid frame, float radius, float blur, float offset, float opacity)
    {
        var host = new Border { IsHitTestVisible = false };
        frame.Children.Insert(0, host);
        HomeElementShadow? shadow = null;
        frame.Loaded += (_, _) =>
        {
            shadow ??= new HomeElementShadow(host, radius, blur, offset, opacity);
            shadow.UpdateSize(frame.ActualWidth, frame.ActualHeight);
        };
        frame.SizeChanged += (_, _) => shadow?.UpdateSize(frame.ActualWidth, frame.ActualHeight);
        frame.Unloaded += (_, _) => { shadow?.Dispose(); shadow = null; };
    }

    private sealed partial class HomeElementShadow : IDisposable
    {
        private readonly Border _host;
        private readonly CompositionRoundedRectangleGeometry _geometry;
        private readonly CompositionColorBrush _color;
        private readonly CompositionSpriteShape _shape;
        private readonly ShapeVisual _caster;
        private readonly CompositionVisualSurface _surface;
        private readonly CompositionSurfaceBrush _mask;
        private readonly DropShadow _shadow;
        private readonly SpriteVisual _visual;

        public HomeElementShadow(Border host, float radius, float blur, float offset, float opacity)
        {
            _host = host;
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            _geometry = compositor.CreateRoundedRectangleGeometry();
            _geometry.CornerRadius = new Vector2(radius);
            _color = compositor.CreateColorBrush(Colors.White);
            _shape = compositor.CreateSpriteShape(_geometry);
            _shape.FillBrush = _color;
            _caster = compositor.CreateShapeVisual();
            _caster.Shapes.Add(_shape);
            _surface = compositor.CreateVisualSurface();
            _surface.SourceVisual = _caster;
            _mask = compositor.CreateSurfaceBrush(_surface);
            _shadow = compositor.CreateDropShadow();
            _shadow.Color = Colors.Black;
            _shadow.BlurRadius = blur;
            _shadow.Opacity = opacity;
            _shadow.Offset = new Vector3(0, offset, 0);
            _shadow.Mask = _mask;
            _visual = compositor.CreateSpriteVisual();
            _visual.RelativeSizeAdjustment = Vector2.One;
            _visual.Shadow = _shadow;
            ElementCompositionPreview.SetElementChildVisual(host, _visual);
        }

        public void UpdateSize(double width, double height)
        {
            if (width <= 0 || height <= 0) return;
            var size = new Vector2((float)width, (float)height);
            _geometry.Size = size;
            _caster.Size = size;
            _surface.SourceSize = size;
        }

        public void Dispose()
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            _visual.Shadow = null;
            _shadow.Mask = null;
            _mask.Surface = null;
            _surface.SourceVisual = null;
            _caster.Shapes.Clear();
            _visual.Dispose();
            _shadow.Dispose();
            _mask.Dispose();
            _surface.Dispose();
            _caster.Dispose();
            _shape.Dispose();
            _color.Dispose();
            _geometry.Dispose();
        }
    }
}
