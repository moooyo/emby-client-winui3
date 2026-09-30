using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private void RestoreScrollOffset()
    {
        if (_pendingScrollOffset is not { } offset || !IsLoaded || _refreshQueued) return;
        _surface.UpdateLayout();
        _pendingScrollOffset = null;
        _scroll.ChangeView(null, Math.Min(offset, _scroll.ScrollableHeight), null, true);
    }

    private void UpdateBackdropFade()
    {
        var color = LumenTheme.Color("Background");
        var clear = Color.FromArgb(0, color.R, color.G, color.B);
        _bottomFade.Background = !LumenTheme.IsDark
            ? Gradient(new Point(0, 0), new Point(0, 1), (0, clear), (.87, clear), (1, color))
            : Gradient(new Point(0, 0), new Point(0, 1), (0, clear), (.55, clear), (.88, Color.FromArgb(210, color.R, color.G, color.B)), (1, color));
    }

    private void UpdateResponsiveLayout()
    {
        var width = ActualWidth;
        if (width <= 0) return;
        var inset = width < 1000 ? 28 : 56;
        _content.Margin = new Thickness(inset, _library.Detail.Item.Type is "Series" or "Season" ? 106 : 118, inset, 56);
        foreach (var scroller in new[] { _episodeScroll, _castScroll, _similarScroll })
            if (scroller is not null) scroller.Margin = new Thickness(0, 0, -inset, 0);
        if (_mediaTiles is null) return;
        var columns = width < 600 ? 1 : width < 1000 ? 2 : 4;
        if (_mediaTiles.ColumnDefinitions.Count == columns) return;
        _mediaTiles.ColumnDefinitions.Clear();
        _mediaTiles.RowDefinitions.Clear();
        for (var index = 0; index < columns; index++)
            _mediaTiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < (int)Math.Ceiling(_mediaTiles.Children.Count / (double)columns); index++)
            _mediaTiles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < _mediaTiles.Children.Count; index++)
        {
            if (_mediaTiles.Children[index] is not FrameworkElement tile) continue;
            Grid.SetColumn(tile, index % columns);
            Grid.SetRow(tile, index / columns);
        }
    }

    private static string Format(string key, params object[] values) => LumenText.Get(key, values);

    private static string Runtime(long ticks)
    {
        var minutes = Math.Max(1, (int)Math.Round(TimeSpan.FromTicks(Math.Max(0, ticks)).TotalMinutes));
        return minutes >= 60 ? minutes % 60 == 0 ? Format("{0} hr", minutes / 60)
            : Format("{0} hr {1} min", minutes / 60, minutes % 60) : Format("{0} min", minutes);
    }

    private static string EpisodeCode(BaseItemDto item) => item.ParentIndexNumber is { } season && item.IndexNumber is { } episode
        ? $"S{season}:E{episode}" : item.IndexNumber is { } index ? Format("Episode {0}", index) : LumenText.Get("Episode");

    private static TextBlock ImageText(string text, double size, bool serif = false, double opacity = 1, bool weight = false)
    {
        var result = LumenUi.Text(text, size, serif);
        result.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255));
        result.Opacity = opacity;
        if (weight) result.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        return result;
    }

    private static SolidColorBrush ImageBrush(byte alpha) => new(Color.FromArgb(alpha, 255, 255, 255));

    private static Border OutlineBadge(string text) => new()
    {
        BorderBrush = ImageBrush(128), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = ImageText(text, 11, weight: true)
    };

    private static LinearGradientBrush Gradient(Point start, Point end, params (double Offset, Color Color)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop { Offset = stop.Offset, Color = stop.Color });
        return brush;
    }

    private static Style PopupStyle(double minWidth = 200)
    {
        var style = new Style { TargetType = typeof(FlyoutPresenter) };
        style.Setters.Add(new Setter(BackgroundProperty, LumenTheme.Brush("Pop")));
        style.Setters.Add(new Setter(ForegroundProperty, LumenTheme.Brush("Ink")));
        style.Setters.Add(new Setter(BorderBrushProperty, LumenTheme.Brush("LineStrong")));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(CornerRadiusProperty, new CornerRadius(16)));
        style.Setters.Add(new Setter(PaddingProperty, new Thickness(6)));
        style.Setters.Add(new Setter(MinWidthProperty, minWidth));
        style.Setters.Add(new Setter(MaxWidthProperty, 520d));
        return style;
    }

    private bool HasFocusWithin(DependencyObject owner)
    {
        if (XamlRoot is null) return false;
        var node = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (node is not null)
        {
            if (ReferenceEquals(node, owner)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static void ApplyRoundedClip(FrameworkElement element, double radius)
    {
        if (!element.IsLoaded || element.ActualWidth <= 0 || element.ActualHeight <= 0) return;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var geometry = visual.Compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        geometry.CornerRadius = new Vector2((float)radius);
        visual.Clip = visual.Compositor.CreateGeometricClip(geometry);
    }

    private sealed partial class FlowPanel : Panel
    {
        public double Spacing { get; set; } = 8;
        public double RowSpacing { get; set; } = 8;

        protected override Size MeasureOverride(Size availableSize)
        {
            var limit = double.IsInfinity(availableSize.Width) ? double.MaxValue : Math.Max(0, availableSize.Width);
            double x = 0, y = 0, rowHeight = 0, width = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(limit, double.PositiveInfinity));
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > limit)
                {
                    width = Math.Max(width, x - Spacing);
                    y += rowHeight + RowSpacing;
                    x = rowHeight = 0;
                }
                x += size.Width + Spacing;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            width = Math.Max(width, Math.Max(0, x - Spacing));
            return new Size(Math.Min(limit, width), y + rowHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0, y = 0, rowHeight = 0;
            foreach (var child in Children)
            {
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > finalSize.Width)
                {
                    y += rowHeight + RowSpacing;
                    x = rowHeight = 0;
                }
                child.Arrange(new Rect(x, y, Math.Min(size.Width, finalSize.Width), size.Height));
                x += size.Width + Spacing;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            return finalSize;
        }
    }
}
