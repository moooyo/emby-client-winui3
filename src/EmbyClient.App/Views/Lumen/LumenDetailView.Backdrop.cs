using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private readonly List<LumenArtwork> _detailBackdrops = [];
    private readonly List<Grid> _detailBackdropFrames = [];
    private readonly List<bool> _detailBackdropBlurModes = [];
    private int _visibleDetailBackdrop = -1;
    private int _pendingDetailBackdrop = -1;
    private int _detailBackdropVersion;
    private Image? _pendingDetailBackdropImage;
    private long? _pendingDetailBackdropCallback;
    private DispatcherTimer? _detailBackdropFallbackTimer;
    private CompositionRadialGradientBrush? _detailEpisodeRadialBrush;
    private CompositionRadialGradientBrush? _detailAccentRadialBrush;
    private CompositionColorGradientStop? _detailAccentGradientStop;
    private SpriteVisual? _detailEpisodeRadialVisual;
    private SpriteVisual? _detailAccentRadialVisual;

    private void CreateDetailBackdropLayers()
    {
        for (var index = 0; index < 3; index++)
        {
            var artwork = index == 0 ? _backdrop : new LumenArtwork { CornerRadius = new CornerRadius(0), IsHitTestVisible = false };
            artwork.SetCoverFocalPoint(.5, .35);
            var frame = new Grid { IsHitTestVisible = false };
            frame.Children.Add(artwork);
            _detailBackdrops.Add(artwork);
            _detailBackdropFrames.Add(frame);
            _detailBackdropBlurModes.Add(false);
            _backdropLayer.Children.Add(frame);
            ElementCompositionPreview.GetElementVisual(frame).Opacity = 0;
        }
    }

    private void BuildDetailRadialShades()
    {
        if (_detailAccentGradientStop is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_backdropLayer).Compositor;
        var shadeBrush = compositor.CreateRadialGradientBrush();
        shadeBrush.MappingMode = CompositionMappingMode.Relative;
        shadeBrush.EllipseCenter = new Vector2(0, 1);
        shadeBrush.EllipseRadius = new Vector2(.9f, .7f);
        shadeBrush.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(153, 8, 8, 9)));
        shadeBrush.ColorStops.Add(compositor.CreateColorGradientStop(.66f, Color.FromArgb(0, 8, 8, 9)));
        _detailEpisodeRadialBrush = shadeBrush;
        _detailEpisodeRadialVisual = compositor.CreateSpriteVisual();
        _detailEpisodeRadialVisual.RelativeSizeAdjustment = Vector2.One;
        _detailEpisodeRadialVisual.Brush = shadeBrush;
        ElementCompositionPreview.SetElementChildVisual(_episodeRadialShade, _detailEpisodeRadialVisual);
        var accentBrush = compositor.CreateRadialGradientBrush();
        accentBrush.MappingMode = CompositionMappingMode.Relative;
        accentBrush.EllipseCenter = new Vector2(.5f, .5f);
        accentBrush.EllipseRadius = new Vector2(.5f, .5f);
        _detailAccentGradientStop = compositor.CreateColorGradientStop();
        accentBrush.ColorStops.Add(_detailAccentGradientStop);
        accentBrush.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0)));
        _detailAccentRadialBrush = accentBrush;
        _detailAccentRadialVisual = compositor.CreateSpriteVisual();
        _detailAccentRadialVisual.RelativeSizeAdjustment = Vector2.One;
        _detailAccentRadialVisual.Brush = accentBrush;
        ElementCompositionPreview.SetElementChildVisual(_detailAccentGlow, _detailAccentRadialVisual);
        LumenTheme.Changed += DetailAccentThemeChanged;
        UpdateDetailAccentGlow();
    }

    private void DetailAccentThemeChanged(object? sender, EventArgs args) => UpdateDetailAccentGlow();

    private void UpdateDetailAccentGlow()
    {
        if (_detailAccentGradientStop is null) return;
        var accent = LumenTheme.Color("ImageAccent");
        _detailAccentGradientStop.Color = Color.FromArgb(107, accent.R, accent.G, accent.B);
    }

    private void ReleaseDetailRadialShades()
    {
        LumenTheme.Changed -= DetailAccentThemeChanged;
        ElementCompositionPreview.SetElementChildVisual(_episodeRadialShade, null);
        ElementCompositionPreview.SetElementChildVisual(_detailAccentGlow, null);
        _detailEpisodeRadialVisual?.Dispose();
        _detailAccentRadialVisual?.Dispose();
        _detailEpisodeRadialBrush?.Dispose();
        _detailAccentRadialBrush?.Dispose();
        _detailAccentGradientStop?.Dispose();
        _detailEpisodeRadialVisual = _detailAccentRadialVisual = null;
        _detailEpisodeRadialBrush = _detailAccentRadialBrush = null;
        _detailAccentGradientStop = null;
    }

    private static void SetDetailShadeOpacity(FrameworkElement shade, double opacity, double duration)
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

    private void QueueDetailBackdrop(MediaCardViewModel item, ArtworkKind kind, bool blurred)
    {
        CancelPendingDetailBackdrop();
        var version = ++_detailBackdropVersion;
        var index = (_visibleDetailBackdrop + 1) % _detailBackdrops.Count;
        _pendingDetailBackdrop = index;
        var artwork = _detailBackdrops[index];
        var frame = _detailBackdropFrames[index];
        _detailBackdropBlurModes[index] = blurred;
        frame.Margin = blurred ? new Thickness(-90) : new Thickness(0);
        var visual = ElementCompositionPreview.GetElementVisual(frame);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Scale");
        visual.Scale = Vector3.One;
        visual.Opacity = 0;
        _pendingDetailBackdropImage = artwork.Image;
        _pendingDetailBackdropCallback = artwork.Image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
        {
            if (version == _detailBackdropVersion && _pendingDetailBackdrop == index && artwork.Image.Source is not null)
                RevealDetailBackdrop(index, version);
        });
        artwork.Set(item, _library, kind, 1600);
        if (artwork.Image.Source is not null) RevealDetailBackdrop(index, version);
        if (_pendingDetailBackdrop != index) return;
        _detailBackdropFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _detailBackdropFallbackTimer.Tick += (_, _) =>
        {
            _detailBackdropFallbackTimer?.Stop();
            if (version != _detailBackdropVersion || _pendingDetailBackdrop != index || artwork.Image.Source is not null) return;
            foreach (var oldFrame in _detailBackdropFrames)
            {
                var oldVisual = ElementCompositionPreview.GetElementVisual(oldFrame);
                oldVisual.StopAnimation("Opacity");
                oldVisual.Opacity = 0;
            }
            _visibleDetailBackdrop = -1;
        };
        _detailBackdropFallbackTimer.Start();
    }

    private void RevealDetailBackdrop(int index, int version)
    {
        if (version != _detailBackdropVersion || _pendingDetailBackdrop != index) return;
        CancelPendingDetailBackdrop();
        var previous = _visibleDetailBackdrop;
        _visibleDetailBackdrop = index;
        var animate = IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        for (var layer = 0; layer < _detailBackdropFrames.Count; layer++)
        {
            var frame = _detailBackdropFrames[layer];
            var visual = ElementCompositionPreview.GetElementVisual(frame);
            visual.StopAnimation("Opacity");
            var target = layer == index ? 1f : 0f;
            if (!animate || previous < 0 || layer != previous && layer != index)
            {
                visual.Opacity = target;
                continue;
            }
            var opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
            opacity.InsertKeyFrame(0, layer == previous ? 1 : 0);
            opacity.InsertKeyFrame(1, target);
            opacity.Duration = TimeSpan.FromMilliseconds(700);
            visual.StartAnimation("Opacity", opacity);
        }
        if (animate)
        {
            var frame = _detailBackdropFrames[index];
            var visual = ElementCompositionPreview.GetElementVisual(frame);
            visual.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight * .35f, 0);
            var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0, Vector3.One);
            var targetScale = _detailBackdropBlurModes[index] ? 1.03f : 1.07f;
            scale.InsertKeyFrame(1, new Vector3(targetScale, targetScale, 1));
            scale.Duration = TimeSpan.FromSeconds(12);
            visual.StartAnimation("Scale", scale);
        }
    }

    private void CancelPendingDetailBackdrop()
    {
        _detailBackdropFallbackTimer?.Stop();
        _detailBackdropFallbackTimer = null;
        if (_pendingDetailBackdropImage is { } image && _pendingDetailBackdropCallback is { } callback)
            image.UnregisterPropertyChangedCallback(Image.SourceProperty, callback);
        _pendingDetailBackdropImage = null;
        _pendingDetailBackdropCallback = null;
        _pendingDetailBackdrop = -1;
    }

    private void ClearDetailBackdrops()
    {
        _detailBackdropVersion++;
        CancelPendingDetailBackdrop();
        _visibleDetailBackdrop = -1;
        foreach (var artwork in _detailBackdrops) artwork.Clear();
        foreach (var frame in _detailBackdropFrames)
        {
            var visual = ElementCompositionPreview.GetElementVisual(frame);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 0;
            visual.Scale = Vector3.One;
        }
    }
}
