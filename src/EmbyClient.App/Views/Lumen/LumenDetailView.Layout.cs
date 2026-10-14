using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        if (_pendingPageIndex is { } page && IsLoaded && !_refreshQueued && _pageConfiguration is not null)
        {
            _pendingPageIndex = null;
            _pendingScrollOffset = null;
            _deck.GoToPage(page, false);
            return;
        }
        if (_pendingScrollOffset is not { } offset || !IsLoaded || _refreshQueued) return;
        var height = Math.Max(1, _deck.ViewportHeight);
        _pendingScrollOffset = null;
        _deck.GoToPage((int)Math.Round(offset / height), false);
    }

    private void UpdateBackdropFade()
    {
        var index = _deck?.SelectedIndex ?? 0;
        var episode = index > 0 && index == _episodePageIndex;
        var overview = index == 0;
        _backdropBlur.Opacity = _backdropIsBlurred || !overview && !episode ? 1 : 0;
        SetDetailShadeOpacity(_horizontalFade, overview ? 1 : episode ? 0 : .3, 1000);
        SetDetailShadeOpacity(_bottomFade, overview ? 1 : episode ? 0 : .3, 1000);
        SetDetailShadeOpacity(_episodeLinearShade, episode ? 1 : 0, 1000);
        SetDetailShadeOpacity(_detailPageShade, !overview && !episode ? 1 : 0, 1000);
        SetDetailShadeOpacity(_episodeRadialShade, episode ? 1 : 0, 1000);
        SetDetailShadeOpacity(_episodeStripShade, episode ? _episodeStripExpanded ? 1 : .3 : 0, 600);
        SetDetailShadeOpacity(_episodeStripDim, episode && _episodeStripExpanded ? .42 : 0, 500);
        SetDetailShadeOpacity(_detailAccentGlow, episode ? 0 : .4, 1000);
    }

    private void UpdateResponsiveLayout()
    {
        if (_updatingResponsiveLayout) return;
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        _updatingResponsiveLayout = true;
        try
        {
            var inset = DetailInset;
            _backdropLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
            _episodeStripShade.Height = height * .64;
            _detailAccentGlow.Width = width * .74;
            _detailAccentGlow.Height = height * .84;
            _detailAccentGlow.Margin = new Thickness(-width * .14, 0, 0, -height * .3);
            var top = Math.Clamp(height * .13, 96, 128);
            foreach (var page in new[] { _overviewPage, _episodePage, _castPage, _mediaPage }) page.Height = height;
            foreach (var frame in new[] { _overviewFrame, _episodeFrame, _castFrame, _mediaFrame }) frame.MinHeight = height;
            _overviewFrame.Padding = new Thickness(inset, top, inset, Math.Clamp(height * .10, 56, 110));
            _episodeFrame.Padding = new Thickness(0, top, 0, Math.Clamp(height * .026, 14, 28));
            _castFrame.Padding = new Thickness(0, top, 0, Math.Clamp(height * .08, 40, 80));
            _mediaFrame.Padding = new Thickness(inset, top, inset, Math.Clamp(height * .08, 40, 80));
            _castContent.Spacing = Math.Clamp(height * .05, 26, 52);
            if (_heroActionDivider is not null) _heroActionDivider.Visibility = width >= 720 ? Visibility.Visible : Visibility.Collapsed;
            UpdateStreamSelectorWidths();
            if (_heroTitle is not null)
            {
                _heroTitle.FontSize = Math.Clamp(Math.Min(width * .06, height * .095), 40, 104);
                _heroTitle.LineHeight = _heroTitle.FontSize * 1.06;
            }
            if (_heroDescription is not null)
            {
                _heroDescription.FontSize = Math.Clamp(width * .0115, 15, 17);
                _heroDescription.LineHeight = _heroDescription.FontSize * 1.8;
            }
            _floatingStage.Margin = new Thickness(0, Math.Max(92, height * (_deck.SelectedIndex == _episodePageIndex ? .42 : .40) - 54), 0, 0);
            var posterWidth = Math.Clamp(width * .18, 200, 260);
            _fallbackPoster.Width = posterWidth;
            _fallbackPoster.Height = posterWidth * 1.5;
            _fallbackPoster.Margin = new Thickness(0, Math.Max(96, height * .5 - posterWidth * 1.5 * .58), inset + 40, 0);
            _fallbackPoster.Visibility = width >= 1100 && _library.HasDetails
                && ImageCache.SelectImage(_library.Detail.Item, ArtworkKind.Backdrop) is null
                && ImageCache.SelectImage(_library.Detail.Item, ArtworkKind.Poster) is not null ? Visibility.Visible : Visibility.Collapsed;
            foreach (var section in new[] { _castSection, _similarSection })
            {
                foreach (var child in section.Children.OfType<FrameworkElement>())
                {
                    if (child is ScrollViewer scroll && scroll.Content is StackPanel row)
                        row.Padding = new Thickness(inset, row.Padding.Top, inset, row.Padding.Bottom);
                    else child.Margin = new Thickness(inset, child.Margin.Top, inset, child.Margin.Bottom);
                }
            }
            if (_similarScroll?.Content is StackPanel similar)
                foreach (var card in similar.Children.OfType<LumenMediaCard>())
                    card.SetCardWidth(Math.Clamp(Math.Min(width * .115, height * .19), 104, 196));
            UpdateEpisodeResponsiveLayout();
            _deck.RefreshLayout();
            UpdateStageInteraction();
        }
        finally
        {
            _updatingResponsiveLayout = false;
        }
    }

    private double DetailInset => Math.Clamp(Math.Max(640, ActualWidth) * .04, 24, 56);

    private static ScrollViewer CreatePageScroll(UIElement content) => new()
    {
        Content = content, HorizontalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Enabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, IsTabStop = false
    };

    private void ConfigureDetailPages(MediaCardViewModel detail)
    {
        var hasChildren = detail.IsFolder || detail.Item.Type == "Episode" && _library.NextEpisode is not null;
        var configuration = detail.Item.Type + ":" + hasChildren;
        if (_pageConfiguration == configuration) return;
        _pageConfiguration = configuration;
        _episodePageIndex = hasChildren ? 1 : -1;
        var pages = new List<(string Title, FrameworkElement Content)> { (LumenText.Get("Detail overview"), _overviewPage) };
        if (hasChildren) pages.Add((LumenText.Get(detail.Item.Type == "Episode" ? "Next episode"
            : detail.Item.Type is "Series" or "Season" ? "Episodes" : "Contents"), _episodePage));
        pages.Add((LumenText.Get("Cast & recommendations"), _castPage));
        pages.Add((LumenText.Get("Media information"), _mediaPage));
        _deck.SetPages(pages);
        OnDetailPageChanged(_deck.SelectedIndex);
    }

    private void OnDetailPageChanged(int index)
    {
        UpdateBackdropFade();
        if (_library.HasDetails)
        {
            var item = index == _episodePageIndex && _focusedEpisode is not null ? _focusedEpisode : _library.Detail;
            SetDetailBackdrop(item);
            RenderFloatingPlayback();
            if (index == _episodePageIndex)
            {
                CenterCurrentEpisode();
                EnsureInitialEpisodeSelection();
            }
        }
        _stagePointer = null;
        UpdateResponsiveLayout();
    }

    private string? _backdropItemId;
    private ImageCache.ImageReference? _backdropReference;
    private bool _backdropIsBlurred;

    private void SetDetailBackdrop(MediaCardViewModel item)
    {
        var imagePage = _deck.SelectedIndex == 0 || _deck.SelectedIndex == _episodePageIndex;
        var kind = imagePage ? item.Item.Type == "Episode" ? ArtworkKind.Landscape : ArtworkKind.Backdrop : ArtworkKind.Poster;
        var reference = ImageCache.SelectImage(item.Item, kind);
        var blurred = !imagePage;
        if (reference is null && imagePage)
        {
            kind = ArtworkKind.Poster;
            reference = ImageCache.SelectImage(item.Item, kind);
            blurred = reference is not null;
        }
        if (_backdropItemId == item.Id && _backdropReference == reference && _backdropIsBlurred == blurred) return;
        _backdropItemId = item.Id;
        _backdropReference = reference;
        _backdropIsBlurred = blurred;
        if (reference is null) ClearDetailBackdrops();
        else QueueDetailBackdrop(item, kind, blurred);
        UpdateBackdropFade();
    }

    private void OnFocusedEpisodeChanged(MediaCardViewModel episode)
    {
        if (_deck.SelectedIndex != _episodePageIndex) return;
        SetDetailBackdrop(episode);
        RenderFloatingPlayback();
        UpdateStageInteraction();
    }

    private void HandleArtworkTap(TappedRoutedEventArgs args, DependencyObject information)
    {
        var node = args.OriginalSource as DependencyObject;
        while (node is not null && !ReferenceEquals(node, _surface))
        {
            if (ReferenceEquals(node, information) || node is ButtonBase or TextBox or ComboBox or Slider) return;
            node = VisualTreeHelper.GetParent(node);
        }
        if (_activeDialog is not null) return;
        PlayActiveTarget(false);
        args.Handled = true;
    }

    private void UpdateStageInteraction()
    {
        if (_floatingStage is null || _deck is null || _hero is null) return;
        var active = _deck.SelectedIndex == 0 || _deck.SelectedIndex == _episodePageIndex;
        if (!active)
        {
            _floatingStage.Visibility = Visibility.Collapsed;
            _interactionDim.Opacity = 0;
            return;
        }
        var target = ActivePlaybackTarget;
        var stripExpanded = _deck.SelectedIndex == _episodePageIndex && _episodeStripExpanded;
        _floatingStage.Visibility = target.CanPlay && !stripExpanded ? Visibility.Visible : Visibility.Collapsed;
        var focused = HasFocusWithin(_floatingStage);
        var information = _deck.SelectedIndex == _episodePageIndex ? _episodeFocusInfo ?? _childrenSection : _hero;
        double proximity = focused ? 1 : 0;
        double informationProximity = HasFocusWithin(information) ? 1 : 0;
        if (_stagePointer is { } pointer && ActualWidth > 0 && ActualHeight > 0 && _activeDialog is null && !_hasOpenDetailFlyout)
        {
            var dx = (pointer.X - ActualWidth * .5) / Math.Max(220, ActualWidth * .26);
            var dy = (pointer.Y - ActualHeight * (_deck.SelectedIndex == _episodePageIndex ? .42 : .4)) / Math.Max(150, ActualHeight * .21);
            var distance = Math.Sqrt(dx * dx + dy * dy);
            proximity = Math.Max(proximity, Smooth(Math.Clamp((1 - distance) / .7, 0, 1)));
            if (information.IsLoaded)
            {
                var bounds = information.TransformToVisual(_surface).TransformBounds(new Rect(0, 0, information.ActualWidth, information.ActualHeight));
                var ix = Math.Max(bounds.Left - pointer.X, Math.Max(0, pointer.X - bounds.Right));
                var iy = Math.Max(bounds.Top - pointer.Y, Math.Max(0, pointer.Y - bounds.Bottom));
                informationProximity = Math.Max(informationProximity, Smooth(Math.Clamp(1 - Math.Sqrt(ix * ix + iy * iy) / 140, 0, 1)));
            }
        }
        if (_activeDialog is not null || _hasOpenDetailFlyout || stripExpanded) proximity = 0;
        _floatingStage.Opacity = proximity;
        _floatingStage.IsHitTestVisible = proximity > .2;
        _floatingStage.RenderTransformOrigin = new Point(.5, .25);
        _floatingScale.ScaleX = _floatingScale.ScaleY = .9 + .1 * proximity;
        if (_floatingLabels is not null) _floatingLabels.Opacity = proximity > .55 ? 1 : 0;
        information.Opacity = Math.Clamp(.66 + .34 * informationProximity - .36 * proximity, .3, 1);
        _interactionDim.Opacity = Math.Max(proximity * .3, informationProximity * .42);
    }

    private static double Smooth(double value) => value * value * (3 - 2 * value);

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
        BorderBrush = ImageBrush(128), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 1, 7, 1), VerticalAlignment = VerticalAlignment.Center,
        Child = ImageText(text, 12, weight: true)
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
        style.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(247, 28, 26, 23))));
        style.Setters.Add(new Setter(ForegroundProperty, LumenTheme.Brush("ImageInk")));
        style.Setters.Add(new Setter(BorderBrushProperty, ImageBrush(26)));
        style.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(CornerRadiusProperty, new CornerRadius(18)));
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
