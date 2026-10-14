using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly Grid _homeStage = new() { IsHitTestVisible = false };
    private readonly Grid[] _homeArtworkLayers = [new(), new(), new()];
    private readonly LumenArtwork[] _heroArtwork =
    [
        new() { CornerRadius = new CornerRadius(0) },
        new() { CornerRadius = new CornerRadius(0) },
        new() { CornerRadius = new CornerRadius(0) }
    ];
    private readonly Border[] _homeArtworkBlur = [new(), new(), new()];
    private readonly Grid _homeHeroShade = new() { IsHitTestVisible = false };
    private readonly Border _homeShelfShade = new() { IsHitTestVisible = false };
    private readonly Grid _homeShelfRadial = new() { IsHitTestVisible = false };
    private readonly Grid _homeAccentGlow = new()
    {
        IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom
    };
    private readonly Border _homeShelfVeil = new() { IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Border _homeShelfDim = new() { Background = new SolidColorBrush(Color.FromArgb(255, 8, 8, 10)), IsHitTestVisible = false };
    private readonly Border _homeFocusDim = new() { Background = new SolidColorBrush(Color.FromArgb(255, 8, 8, 10)), Opacity = 0, IsHitTestVisible = false };
    private readonly Dictionary<LumenArtwork, long> _heroSourceSubscriptions = [];
    private readonly DispatcherTimer _homeBackgroundFallbackTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly ConditionalWeakTable<BrowseViewportState, HomePresentationState> _homeViewports = new();
    private int _heroBuffer = -1;
    private int _heroArtworkVersion;
    private int _pendingHomeBuffer = -1;
    private string? _homeBackgroundKey;
    private long _homeFocusResetAt;
    private CompositionColorGradientStop? _homeAccentGradientStop;
    private int _homeDisplayedPageIndex;
    private int _homeBackgroundDirection;
    private int _pendingHomeDirection;

    private sealed record HomePresentationState(string? HeroId, Dictionary<string, HomeShelfPresentation> Shelves, bool Continuous);
    private sealed record HomeShelfPresentation(string? ItemId, double HorizontalOffset);

    private void BuildHomeStage()
    {
        _home.Background = new SolidColorBrush(Color.FromArgb(255, 10, 8, 7));
        for (var index = 0; index < _heroArtwork.Length; index++)
        {
            var artwork = _heroArtwork[index];
            artwork.SetCoverFocalPoint(.5, .35);
            artwork.Background = new SolidColorBrush(Color.FromArgb(255, 10, 8, 7));
            _homeArtworkLayers[index].Opacity = 0;
            _homeArtworkLayers[index].Children.Add(artwork);
            _homeArtworkBlur[index].Background = new AcrylicBrush
            {
                TintColor = Microsoft.UI.Colors.Transparent, TintOpacity = 0, TintLuminosityOpacity = 0,
                FallbackColor = Color.FromArgb(255, 18, 16, 14)
            };
            _homeArtworkBlur[index].Visibility = Visibility.Collapsed;
            _homeArtworkLayers[index].Children.Add(_homeArtworkBlur[index]);
            _homeStage.Children.Add(_homeArtworkLayers[index]);
        }
        _homeHeroShade.Children.Add(new Border
        {
            Background = HomeGradient(true, (0, 242, 10, 8, 6), (.32, 209, 10, 8, 6), (.62, 117, 10, 8, 6),
                (.84, 36, 10, 8, 6), (1, 0, 10, 8, 6))
        });
        _homeHeroShade.Children.Add(new Border
        {
            Background = HomeGradient(false, (0, 0, 10, 8, 6), (.42, 0, 10, 8, 6), (.74, 148, 10, 8, 6), (1, 240, 10, 8, 6))
        });
        _homeShelfShade.Background = HomeGradient(false, (0, 0, 8, 8, 9), (.34, 0, 8, 8, 9), (.46, 36, 8, 8, 9),
            (.7, 143, 8, 8, 9), (1, 219, 8, 8, 9));
        _homeShelfVeil.Background = HomeGradient(false, (0, 0, 8, 8, 9), (.66, 128, 8, 8, 9), (1, 230, 8, 8, 9));
        foreach (var shade in new FrameworkElement[] { _homeHeroShade, _homeShelfShade, _homeShelfRadial,
            _homeShelfVeil, _homeShelfDim, _homeFocusDim, _homeAccentGlow })
        {
            Canvas.SetZIndex(shade, 4);
            _homeStage.Children.Add(shade);
        }
        var topShade = new Border
        {
            Height = 140, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
            Background = HomeGradient(false, (0, 128, 0, 0, 0), (1, 0, 0, 0, 0))
        };
        Canvas.SetZIndex(topShade, 5);
        _homeStage.Children.Add(topShade);
        _home.Children.Add(_homeStage);
        _homeStage.Loaded += (_, _) =>
        {
            BuildHomeRadialShades();
            if (_pendingHomeBuffer < 0) return;
            if (_heroArtwork[_pendingHomeBuffer].Image.Source is not null) RevealHomeArtwork(_pendingHomeBuffer);
            else
            {
                _homeBackgroundFallbackTimer.Stop();
                _homeBackgroundFallbackTimer.Start();
            }
        };
        _home.SizeChanged += (_, _) =>
            _homeStage.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Math.Max(0, _home.ActualWidth), Math.Max(0, _home.ActualHeight)) };
        _homeBackgroundFallbackTimer.Tick += (_, _) =>
        {
            _homeBackgroundFallbackTimer.Stop();
            if (_pendingHomeBuffer >= 0 && _renderedPage == "home") RevealHomeArtwork(_pendingHomeBuffer);
        };
        UpdateHomeStageShades();
        ResetHomePointerFocus();
    }

    private void UpdateHomeStage()
    {
        var direction = _homeBackgroundDirection;
        _homeBackgroundDirection = 0;
        var featured = _homeDeck.SelectedIndex == 0;
        var shelf = _homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1);
        var item = featured ? _heroItems.ElementAtOrDefault(_heroIndex) : shelf?.Items.ElementAtOrDefault(shelf.SelectedIndex);
        var fallback = item is null;
        item ??= _heroItems.FirstOrDefault();
        UpdateHomeStageShades();
        if (item is null)
        {
            HideHomeArtwork();
            return;
        }
        var kind = featured ? ArtworkKind.Backdrop : ArtworkKind.Landscape;
        var reference = ImageCache.SelectImage(item.Item, kind);
        if (reference is { Type: "Primary" } && item.Item.Type != "Episode" && item.Item.PrimaryImageAspectRatio is not > 1)
            fallback = true;
        if (reference is null)
        {
            kind = ArtworkKind.Poster;
            reference = ImageCache.SelectImage(item.Item, kind);
            fallback = true;
        }
        if (reference is null)
        {
            HideHomeArtwork();
            return;
        }
        var key = item.Id + "|" + kind + "|" + reference + "|" + fallback;
        if (_homeBackgroundKey == key) return;
        _homeBackgroundKey = key;
        var nextIndex = (_heroBuffer + 1) % _heroArtwork.Length;
        var next = _heroArtwork[nextIndex];
        var version = ++_heroArtworkVersion;
        if (_heroSourceSubscriptions.Remove(next, out var oldSubscription))
            next.Image.UnregisterPropertyChangedCallback(Image.SourceProperty, oldSubscription);
        _homeArtworkLayers[nextIndex].Opacity = 0;
        _homeArtworkLayers[nextIndex].Margin = fallback ? new Thickness(-90) : new Thickness(0);
        _homeArtworkBlur[nextIndex].Visibility = fallback ? Visibility.Visible : Visibility.Collapsed;
        next.SetCoverFocalPoint(.5, .35);
        _pendingHomeBuffer = nextIndex;
        _pendingHomeDirection = direction;
        _homeBackgroundFallbackTimer.Stop();
        _homeBackgroundFallbackTimer.Start();
        var subscription = next.Image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
        {
            if (next.Image.Source is null || version != _heroArtworkVersion || _homeBackgroundKey != key) return;
            if (_heroSourceSubscriptions.Remove(next, out var completed))
                next.Image.UnregisterPropertyChangedCallback(Image.SourceProperty, completed);
            _homeBackgroundFallbackTimer.Stop();
            RevealHomeArtwork(nextIndex);
        });
        _heroSourceSubscriptions[next] = subscription;
        next.Set(item, ViewModel, kind, fallback ? 128 : 1920);
    }

    private void HideHomeArtwork()
    {
        _homeBackgroundKey = null;
        _homeBackgroundFallbackTimer.Stop();
        _heroArtworkVersion++;
        _pendingHomeBuffer = -1;
        _pendingHomeDirection = 0;
        _homeBackgroundDirection = 0;
        _heroBuffer = -1;
        foreach (var pair in _heroSourceSubscriptions)
            pair.Key.Image.UnregisterPropertyChangedCallback(Image.SourceProperty, pair.Value);
        _heroSourceSubscriptions.Clear();
        foreach (var artwork in _heroArtwork) artwork.Clear();
        foreach (var layer in _homeArtworkLayers) layer.Opacity = 0;
    }

    private void RevealHomeArtwork(int index)
    {
        if (_heroBuffer == index && _pendingHomeBuffer < 0) return;
        var previous = _heroBuffer;
        var direction = _pendingHomeDirection;
        _heroBuffer = index;
        _pendingHomeBuffer = -1;
        _pendingHomeDirection = 0;
        var incoming = _homeArtworkLayers[index];
        incoming.Opacity = 1;
        Canvas.SetZIndex(incoming, 2);
        var visual = ElementCompositionPreview.GetElementVisual(incoming);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Opacity = 1;
        visual.Scale = Vector3.One;
        var artworkVisual = ElementCompositionPreview.GetElementVisual(_heroArtwork[index]);
        artworkVisual.StopAnimation("Scale");
        artworkVisual.Scale = Vector3.One;
        foreach (var other in Enumerable.Range(0, _homeArtworkLayers.Length).Where(other => other != index && other != previous))
        {
            _homeArtworkLayers[other].Opacity = 0;
            Canvas.SetZIndex(_homeArtworkLayers[other], 0);
        }
        if (previous >= 0 && previous != index) Canvas.SetZIndex(_homeArtworkLayers[previous], 1);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            if (previous >= 0 && previous != index) _homeArtworkLayers[previous].Opacity = 0;
            return;
        }
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, previous >= 0 ? 0 : 1);
        var opacityEase = compositor.CreateCubicBezierEasingFunction(new Vector2(.25f, .1f), new Vector2(.25f, 1));
        fade.InsertKeyFrame(1, 1, opacityEase);
        fade.Duration = TimeSpan.FromMilliseconds(700);
        visual.StartAnimation("Opacity", fade);
        artworkVisual.CenterPoint = new Vector3((float)_heroArtwork[index].ActualWidth / 2, (float)_heroArtwork[index].ActualHeight / 2, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, Vector3.One);
        var zoom = _homeArtworkBlur[index].Visibility == Visibility.Visible ? 1.03f : 1.07f;
        scale.InsertKeyFrame(1, new Vector3(zoom, zoom, 1),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0, 0), new Vector2(.58f, 1)));
        scale.Duration = TimeSpan.FromSeconds(12);
        artworkVisual.StartAnimation("Scale", scale);
        if (direction != 0 && previous >= 0)
        {
            visual.CenterPoint = new Vector3((float)incoming.ActualWidth / 2, (float)incoming.ActualHeight / 2, 0);
            var shuttle = compositor.CreateVector3KeyFrameAnimation();
            var from = direction > 0 ? 1.08f : 1.18f;
            shuttle.InsertKeyFrame(0, new Vector3(from, from, 1));
            shuttle.InsertKeyFrame(1, Vector3.One,
                compositor.CreateCubicBezierEasingFunction(new Vector2(.16f, 1), new Vector2(.3f, 1)));
            shuttle.Duration = TimeSpan.FromMilliseconds(1300);
            visual.StartAnimation("Scale", shuttle);
        }
        if (previous >= 0 && previous != index)
        {
            var old = ElementCompositionPreview.GetElementVisual(_homeArtworkLayers[previous]);
            old.StopAnimation("Opacity");
            old.Opacity = 1;
            var version = _heroArtworkVersion;
            var cleanup = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            cleanup.Tick += (_, _) =>
            {
                cleanup.Stop();
                if (version != _heroArtworkVersion || !_renderLoaded || !_renderEnabled) return;
                for (var layer = 0; layer < _homeArtworkLayers.Length; layer++)
                    if (layer != _heroBuffer) _homeArtworkLayers[layer].Opacity = 0;
            };
            cleanup.Start();
            if (direction != 0)
            {
                old.StopAnimation("Scale");
                old.CenterPoint = new Vector3((float)_homeArtworkLayers[previous].ActualWidth / 2,
                    (float)_homeArtworkLayers[previous].ActualHeight / 2, 0);
                var shuttle = compositor.CreateVector3KeyFrameAnimation();
                var to = direction > 0 ? 1.16f : 1.05f;
                shuttle.InsertKeyFrame(0, Vector3.One);
                shuttle.InsertKeyFrame(1, new Vector3(to, to, 1),
                    compositor.CreateCubicBezierEasingFunction(new Vector2(.45f, 0), new Vector2(.25f, 1)));
                shuttle.Duration = TimeSpan.FromMilliseconds(1000);
                old.StartAnimation("Scale", shuttle);
            }
        }
    }

    private void UpdateHomeStageShades()
    {
        var featured = _homeDeck.SelectedIndex == 0;
        var expanded = _homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1)?.Expanded == true;
        SetHomeShadeOpacity(_homeHeroShade, featured ? 1 : 0, 1000);
        SetHomeShadeOpacity(_homeShelfShade, featured ? 0 : 1, 1000);
        SetHomeShadeOpacity(_homeShelfRadial, featured ? 0 : 1, 1000);
        SetHomeShadeOpacity(_homeShelfVeil, featured ? 0 : expanded ? 1 : .3, 600);
        SetHomeShadeOpacity(_homeShelfDim, !featured && expanded ? .42 : 0, 500);
        SetHomeShadeOpacity(_homeAccentGlow, featured ? .4 : 0, 1000);
    }

    private void BuildHomeRadialShades()
    {
        if (_homeAccentGradientStop is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_homeStage).Compositor;
        var shelfBrush = compositor.CreateRadialGradientBrush();
        shelfBrush.MappingMode = CompositionMappingMode.Relative;
        shelfBrush.EllipseCenter = new Vector2(0, 1);
        shelfBrush.EllipseRadius = new Vector2(.9f, .7f);
        shelfBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(153, 8, 8, 9)));
        shelfBrush.ColorStops.Add(compositor.CreateColorGradientStop(.66f, Color.FromArgb(0, 8, 8, 9)));
        var shelfVisual = compositor.CreateSpriteVisual();
        shelfVisual.RelativeSizeAdjustment = Vector2.One;
        shelfVisual.Brush = shelfBrush;
        ElementCompositionPreview.SetElementChildVisual(_homeShelfRadial, shelfVisual);
        var accentBrush = compositor.CreateRadialGradientBrush();
        accentBrush.MappingMode = CompositionMappingMode.Relative;
        accentBrush.EllipseCenter = new Vector2(.5f, .5f);
        accentBrush.EllipseRadius = new Vector2(.5f, .5f);
        _homeAccentGradientStop = compositor.CreateColorGradientStop();
        accentBrush.ColorStops.Add(_homeAccentGradientStop);
        accentBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0)));
        var accentVisual = compositor.CreateSpriteVisual();
        accentVisual.RelativeSizeAdjustment = Vector2.One;
        accentVisual.Brush = accentBrush;
        ElementCompositionPreview.SetElementChildVisual(_homeAccentGlow, accentVisual);
        UpdateHomeAccentGlow();
    }

    private void UpdateHomeAccentGlow()
    {
        if (_homeAccentGradientStop is null) return;
        var accent = LumenTheme.Brush("ImageAccent").Color;
        _homeAccentGradientStop.Color = Color.FromArgb(107, accent.R, accent.G, accent.B);
    }

    private static void SetHomeShadeOpacity(FrameworkElement shade, double opacity, double duration)
    {
        var previous = shade.Opacity;
        if (Math.Abs(previous - opacity) < .001) return;
        shade.Opacity = opacity;
        var visual = ElementCompositionPreview.GetElementVisual(shade);
        visual.StopAnimation("Opacity");
        visual.Opacity = (float)opacity;
        if (!shade.IsLoaded || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, (float)previous);
        animation.InsertKeyFrame(1, (float)opacity,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.25f, .1f), new Vector2(.25f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        visual.StartAnimation("Opacity", animation);
    }

    private void HomePageChanged()
    {
        _homeBackgroundDirection = _homeDeck.LastPageChangeAnimated ? Math.Sign(_homeDeck.SelectedIndex - _homeDisplayedPageIndex) : 0;
        _homeDisplayedPageIndex = _homeDeck.SelectedIndex;
        _homeFocusResetAt = Environment.TickCount64;
        ResetHomePointerFocus();
        foreach (var shelf in _homeShelves)
        {
            shelf.PointerWithin = false;
            shelf.HoverIndex = -1;
            shelf.HoverTimer.Stop();
            shelf.ExpandTimer.Stop();
            shelf.TextTimer.Stop();
            shelf.CollapseTimer.Stop();
            shelf.DisplayIndex = shelf.SelectedIndex;
            RenderHomeShelfText(shelf);
            SetHomeShelfExpanded(shelf, false);
            ResetHomeShelfInformationAnimation(shelf);
        }
        UpdateHomeStage();
        UpdateHeroTimer();
        NavigationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateHomePointerFocus(Point position)
    {
        _homePointerPosition = position;
        UpdateHomeInteraction();
    }

    private void ResetHomePointerFocus()
    {
        _heroInfo.Opacity = .66;
        foreach (var shelf in _homeShelves) shelf.InfoHost.Opacity = .66;
        _homeFocusDim.Opacity = 0;
        _homePointerPosition = null;
        UpdateHomeInteraction();
    }

    private void UpdateHomeLayout(double width)
    {
        if (!double.IsFinite(width) || width <= 0) return;
        var height = ActualHeight > 0 ? ActualHeight : 800;
        var pad = Math.Clamp(width * .04, 24, 56);
        var wide = width >= 1280;
        var thumbnailWidth = Math.Clamp(width * .072, 84, 148);
        var thumbnailStripWidth = _heroItems.Length * thumbnailWidth + Math.Max(0, _heroItems.Length - 1) * 10;
        _heroLayout.MinHeight = height;
        _heroLayout.Padding = new Thickness(pad, Math.Clamp(height * .13, 96, 128), pad, Math.Clamp(height * .06, 48, 64));
        _heroInfo.Width = Math.Max(240, Math.Min(680, width - pad * 2 - (wide ? thumbnailStripWidth + 40 : 0)));
        _heroContent.ColumnSpacing = wide ? 40 : 0;
        _heroThumbnailStrip.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        _heroThumbnailStrip.Width = thumbnailStripWidth;
        _heroSegments.Visibility = wide ? Visibility.Collapsed : Visibility.Visible;
        _heroTitle.FontSize = Math.Clamp(Math.Min(width * .064, height * .10), 40, 112);
        _heroTitle.LineHeight = _heroTitle.FontSize * 1.04;
        _heroMetadata.FontSize = Math.Clamp(width * .011, 14, 17);
        _heroMetadata.LineHeight = _heroMetadata.FontSize * 1.6;
        _heroOverview.FontSize = Math.Clamp(width * .0115, 15, 18);
        _heroOverview.LineHeight = _heroOverview.FontSize * 1.75;
        _heroPlayback.Margin = new Thickness(0, height * .4, 0, 0);
        _homeShelfVeil.Height = height * .64;
        _homeAccentGlow.Width = width * .74;
        _homeAccentGlow.Height = height * .84;
        _homeAccentGlow.Margin = new Thickness(-width * .14, 0, 0, -height * .3);
        foreach (var thumb in _heroThumbStates)
        {
            thumb.Surface.Width = thumbnailWidth;
            thumb.Surface.Height = thumbnailWidth * 9 / 16;
        }
        foreach (var shelf in _homeShelves)
        {
            shelf.Layout.MinHeight = height;
            shelf.Layout.Padding = new Thickness(0, Math.Clamp(height * .13, 96, 128), 0, Math.Clamp(height * .026, 14, 28));
            shelf.InfoHost.Margin = new Thickness(pad, 0, pad, 0);
            shelf.Heading.FontSize = Math.Clamp(Math.Min(width * .028, height * .05), 26, 44);
            shelf.Heading.LineHeight = shelf.Heading.FontSize * 1.12;
            shelf.Overview.Visibility = height >= 620 && !string.IsNullOrWhiteSpace(shelf.Overview.Text) ? Visibility.Visible : Visibility.Collapsed;
            shelf.RowHost.Margin = new Thickness(0, Math.Clamp(height * .034, 18, 36), 0, 0);
            shelf.Header.Margin = new Thickness(pad, 0, pad, 0);
            shelf.CardWidth = Math.Clamp(width * .14, 168, 250);
            shelf.Cards.Spacing = Math.Clamp(width * .012, 12, 18);
            shelf.Scroller.Padding = new Thickness(pad, 14, pad, 6);
            var fullHeight = shelf.CardWidth * 9 / 16 + 109;
            shelf.Row.Height = fullHeight;
            shelf.RowAnimation?.Stop();
            shelf.RowAnimation = null;
            shelf.RowHost.Height = shelf.Expanded ? fullHeight : 80;
            var arrowTop = 14 + shelf.CardWidth * 9 / 32 - 23;
            shelf.Previous.Margin = new Thickness((pad - 46) / 2, arrowTop, 0, 0);
            shelf.Next.Margin = new Thickness(0, arrowTop, (pad - 46) / 2, 0);
            foreach (var card in shelf.Cards.Children.OfType<LumenMediaCard>()) card.SetCardWidth(shelf.CardWidth);
        }
        _homeDeck.RefreshLayout();
    }

    private void ApplyHomeTheme()
    {
        foreach (var text in new[] { _heroTitle, _heroOverview, _heroType }) text.Foreground = LumenTheme.Brush("ImageInk");
        _heroMetadata.Foreground = LumenTheme.Brush("ImageInk");
        _homeDeck.ContinuousScroll = string.Equals(_preferences.HomeScrollMode, "Continuous", StringComparison.Ordinal);
        UpdateHomeAccentGlow();
        foreach (var shelf in _homeShelves)
            foreach (var card in shelf.Cards.Children.OfType<LumenMediaCard>()) card.SetHomeStripStyle();
    }

    private bool HandleHomeNavigationKey(KeyRoutedEventArgs args)
    {
        if (_renderedPage != "home" || args.Handled
            || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) != 0
            || (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & CoreVirtualKeyStates.Down) != 0) return false;
        if (args.Key is VirtualKey.Left or VirtualKey.Right)
        {
            var direction = args.Key == VirtualKey.Right ? 1 : -1;
            if (_homeDeck.SelectedIndex == 0)
            {
                if (_heroItems.Length > 0) SelectHero((_heroIndex + direction + _heroItems.Length) % _heroItems.Length);
            }
            else if (_homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1) is { } shelf)
            {
                SetHomeShelfExpanded(shelf, true);
                SelectHomeShelf(shelf, shelf.SelectedIndex + direction);
                if (shelf.Items.Length > 0) LumenMediaCard.FocusNavigationItem(shelf.Cards, shelf.SelectedIndex);
                shelf.CollapseTimer.Interval = TimeSpan.FromMilliseconds(2600);
                shelf.CollapseTimer.Stop();
                shelf.CollapseTimer.Start();
            }
            args.Handled = true;
            return true;
        }
        if (args.Key == VirtualKey.Enter)
        {
            for (var source = args.OriginalSource as DependencyObject; source is not null; source = VisualTreeHelper.GetParent(source))
                if (source is ButtonBase or TextBox or PasswordBox or RichEditBox or ComboBox or Slider) return false;
            var shelf = _homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1);
            var item = _homeDeck.SelectedIndex == 0 ? _heroItems.ElementAtOrDefault(_heroIndex)
                : shelf?.Items.ElementAtOrDefault(shelf.SelectedIndex);
            if (item is null) return false;
            args.Handled = true;
            if (shelf?.Key == "continue") ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0));
            else _ = OpenItemAsync(item);
            return true;
        }
        return _homeDeck.HandleNavigationKey(args);
    }

    private double HomeVerticalOffset => _homeDeck.ContinuousScroll ? _homeDeck.ContinuousOffset
        : _homeDeck.SelectedIndex == 0 ? _heroViewport.VerticalOffset
        : _homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1)?.Viewport.VerticalOffset ?? 0;

    private void CaptureHomeViewport(BrowseViewportState viewport)
    {
        _homeViewports.Remove(viewport);
        _homeViewports.Add(viewport, new(_heroItems.ElementAtOrDefault(_heroIndex)?.Id,
            _homeShelves.ToDictionary(shelf => shelf.Key,
                shelf => new HomeShelfPresentation(shelf.Items.ElementAtOrDefault(shelf.SelectedIndex)?.Id, shelf.Scroller.HorizontalOffset),
                StringComparer.Ordinal), _homeDeck.ContinuousScroll));
    }

    private void RestoreHomeViewport()
    {
        _homeDeck.GoToPage(ViewModel.ViewportState.HomePageIndex, false);
        if (_homeViewports.TryGetValue(ViewModel.ViewportState, out var presentation))
        {
            var hero = Array.FindIndex(_heroItems, item => item.Id == presentation.HeroId);
            if (hero >= 0) SelectHero(hero);
            foreach (var shelf in _homeShelves)
                if (presentation.Shelves.TryGetValue(shelf.Key, out var saved))
                {
                    shelf.SelectedIndex = Math.Max(0, Array.FindIndex(shelf.Items, item => item.Id == saved.ItemId));
                    shelf.DisplayIndex = shelf.SelectedIndex;
                    RefreshHomeShelfSelection(shelf);
                    RenderHomeShelfText(shelf);
                    shelf.Scroller.ChangeView(saved.HorizontalOffset, null, null, true);
                }
        }
        if (_homeDeck.ContinuousScroll)
        {
            if (presentation?.Continuous == true) _homeDeck.ContinuousOffset = ViewModel.ViewportState.VerticalOffset;
        }
        else
        {
            var vertical = _homeDeck.SelectedIndex == 0 ? _heroViewport : _homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1)?.Viewport;
            vertical?.ChangeView(null, presentation?.Continuous == true ? 0 : ViewModel.ViewportState.VerticalOffset, null, true);
        }
        UpdateHomeStage();
    }

    private void ClearHome()
    {
        _heroTimer.Stop();
        _homeBackgroundFallbackTimer.Stop();
        _heroArtworkVersion++;
        foreach (var pair in _heroSourceSubscriptions)
            pair.Key.Image.UnregisterPropertyChangedCallback(Image.SourceProperty, pair.Value);
        _heroSourceSubscriptions.Clear();
        foreach (var artwork in _heroArtwork) artwork.Clear();
        foreach (var layer in _homeArtworkLayers) layer.Opacity = 0;
        foreach (var shelf in _homeShelves) shelf.Stop();
        _heroItems = [];
        _heroIndex = 0;
        _heroBuffer = -1;
        _pendingHomeBuffer = -1;
        _pendingHomeDirection = 0;
        _homeBackgroundDirection = 0;
        _homeDisplayedPageIndex = 0;
        _heroId = null;
        _homeBackgroundKey = null;
        _homeSignature = string.Empty;
        _heroHasFocus = false;
        _heroPlayback.Visibility = Visibility.Collapsed;
        _homePointerPosition = null;
        StopHeroThumbnailAnimations();
        _heroThumbs.Children.Clear();
        _heroSegments.Children.Clear();
        _heroThumbStates.Clear();
        _homeViewports.Clear();
        RebuildHomePages([], [], []);
        _homeDeck.GoToPage(0, false);
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static LinearGradientBrush HomeGradient(bool horizontal, params (double Offset, byte Alpha, byte Red, byte Green, byte Blue)[] stops)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1)
        };
        foreach (var stop in stops) brush.GradientStops.Add(new GradientStop
        {
            Offset = stop.Offset, Color = Color.FromArgb(stop.Alpha, stop.Red, stop.Green, stop.Blue)
        });
        return brush;
    }
}
