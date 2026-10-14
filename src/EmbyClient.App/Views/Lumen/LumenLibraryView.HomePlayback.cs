using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly Grid _heroPlayback = new()
    {
        Width = 360, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
        Opacity = 0, IsHitTestVisible = false, Visibility = Visibility.Collapsed
    };
    private readonly Grid _heroPlayRing = new() { Width = 108, Height = 108, IsHitTestVisible = false };
    private readonly Button _heroPlay = LumenUi.IconButton("play", "Play", 92, true);
    private readonly Image _heroPlayIcon = new() { Width = 30, Height = 30, Margin = new Thickness(4, 0, 0, 0), IsHitTestVisible = false };
    private readonly StackPanel _heroPlaybackLabels = new() { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _heroPlayTitle = LumenUi.Text(string.Empty, 16);
    private readonly TextBlock _heroPlaySummary = LumenUi.Text(string.Empty, 12.5);
    private readonly Button _heroRestart = LumenUi.Button("Play from beginning", "rotate-ccw", onImage: true, height: 28);
    private readonly ScaleTransform _heroPlaybackScale = new();
    private readonly TranslateTransform _heroPlaybackOffset = new();
    private Point? _homePointerPosition;

    private void BuildHomePlayback()
    {
        var transforms = new TransformGroup();
        transforms.Children.Add(_heroPlaybackScale);
        transforms.Children.Add(_heroPlaybackOffset);
        _heroPlayback.RenderTransform = transforms;
        _heroPlayback.RenderTransformOrigin = new Point(.5, .5);
        _heroPlayback.SizeChanged += (_, _) => _heroPlaybackOffset.Y = -_heroPlayback.ActualHeight / 2;
        var column = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center };
        var disc = new Grid { Width = 92, Height = 92 };
        _heroPlayRing.Margin = new Thickness(-8);
        _heroPlay.HorizontalAlignment = HorizontalAlignment.Center;
        _heroPlay.VerticalAlignment = VerticalAlignment.Center;
        _heroPlay.Background = new AcrylicBrush
        {
            TintColor = Microsoft.UI.Colors.White, TintOpacity = .06, TintLuminosityOpacity = .1,
            FallbackColor = Windows.UI.Color.FromArgb(56, 255, 255, 255)
        };
        _heroPlay.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(77, 255, 255, 255));
        _heroPlay.Resources["ButtonBackground"] = _heroPlay.Background;
        _heroPlay.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 243, 242, 240));
        _heroPlay.Resources["ButtonBackgroundPressed"] = LumenTheme.Brush("ImageInk");
        _heroPlay.Resources["ButtonBorderBrushPointerOver"] = _heroPlay.BorderBrush;
        _heroPlay.Resources["ButtonBorderBrushPressed"] = _heroPlay.BorderBrush;
        _heroPlay.Content = _heroPlayIcon;
        SetHomePlayIcon(false);
        _heroPlay.PointerEntered += (_, _) => SetHomePlayIcon(true);
        _heroPlay.PointerExited += (_, _) => SetHomePlayIcon(false);
        _heroPlay.Click += (_, _) => PlaySelectedHero(false);
        AutomationProperties.SetAutomationId(_heroPlay, "LumenHeroPlay");
        AutomationProperties.SetAccessibilityView(_heroPlayIcon, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        disc.Children.Add(_heroPlay);
        disc.Children.Add(_heroPlayRing);
        column.Children.Add(disc);
        _heroPlayTitle.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _heroPlayTitle.TextAlignment = _heroPlaySummary.TextAlignment = TextAlignment.Center;
        _heroPlayTitle.Foreground = _heroPlaySummary.Foreground = LumenTheme.Brush("ImageInk");
        _heroPlaySummary.Opacity = .75;
        _heroPlaySummary.TextTrimming = TextTrimming.CharacterEllipsis;
        _heroPlaySummary.MaxLines = 2;
        _heroPlaySummary.MaxWidth = 340;
        _heroPlaybackLabels.Children.Add(_heroPlayTitle);
        _heroPlaybackLabels.Children.Add(_heroPlaySummary);
        _heroRestart.HorizontalAlignment = HorizontalAlignment.Center;
        _heroRestart.Padding = new Thickness(12, 0, 12, 0);
        _heroRestart.Background = new AcrylicBrush
        {
            TintColor = Windows.UI.Color.FromArgb(255, 20, 18, 16), TintOpacity = .5, TintLuminosityOpacity = .15,
            FallbackColor = Windows.UI.Color.FromArgb(220, 20, 18, 16)
        };
        _heroRestart.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(51, 255, 255, 255));
        _heroRestart.Resources["ButtonBackground"] = _heroRestart.Background;
        _heroRestart.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255));
        _heroRestart.Resources["ButtonBorderBrushPointerOver"] = _heroRestart.BorderBrush;
        _heroRestart.Resources["ButtonBorderBrushPressed"] = _heroRestart.BorderBrush;
        if (_heroRestart.Content is StackPanel restartContent)
        {
            restartContent.Spacing = 6;
            foreach (var label in restartContent.Children.OfType<TextBlock>())
            {
                label.Text = LumenText.Get("Play from beginning");
                label.FontSize = 12;
            }
            foreach (var icon in restartContent.Children.OfType<Image>()) icon.Width = icon.Height = 13;
        }
        _heroRestart.Click += (_, _) => PlaySelectedHero(true);
        AutomationProperties.SetName(_heroRestart, LumenText.Get("Play from beginning"));
        _heroPlaybackLabels.Children.Add(_heroRestart);
        column.Children.Add(_heroPlaybackLabels);
        _heroPlayback.Children.Add(column);
        Canvas.SetZIndex(_heroPlayback, 10);
        _hero.Children.Insert(0, _heroPlayback);
        _hero.Tapped += (_, args) =>
        {
            for (var node = args.OriginalSource as DependencyObject; node is not null && !ReferenceEquals(node, _hero);
                node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, _heroInfo) || ReferenceEquals(node, _heroThumbnailStrip) || node is ButtonBase) return;
            if (_heroPlay.IsEnabled && _homeDeck.SelectedIndex == 0)
            {
                PlaySelectedHero(false);
                args.Handled = true;
            }
        };
    }

    private void SetHomePlayIcon(bool hovered) => _heroPlayIcon.Source = new SvgImageSource(
        new Uri("ms-appx:///Assets/Lumen/Icons/" + (hovered ? "d" : "w") + "/play_24_filled.svg"));

    private MediaCardViewModel HomePlaybackTarget(MediaCardViewModel item)
    {
        if (item.CanPlay || item.Item.Type is not ("Series" or "Season")) return item;
        var seriesId = item.Item.Type == "Series" ? item.Id : item.Item.SeriesId;
        return ViewModel.HomeRows.Where(row => row.Section is HomeSection.ContinueWatching or HomeSection.NextUp)
            .OrderBy(row => row.Section == HomeSection.ContinueWatching ? 0 : 1).SelectMany(row => row.Items)
            .FirstOrDefault(episode => episode.CanPlay && episode.Item.Type == "Episode" && episode.Item.SeriesId == seriesId
                && (item.Item.Type != "Season" || episode.Item.SeasonId == item.Id)) ?? item;
    }

    private void RenderHomePlayback(MediaCardViewModel item)
    {
        var target = HomePlaybackTarget(item);
        var canResolve = target.Item.Type is "Series" or "Season" && target.Item.LocationType != "Virtual";
        _heroPlayback.Visibility = target.CanPlay || canResolve ? Visibility.Visible : Visibility.Collapsed;
        _heroPlay.IsEnabled = _heroRestart.IsEnabled = ViewModel.IsPlaybackAllowed && (target.CanPlay || canResolve);
        var label = LumenText.Get(target.CanResume ? "Resume" : target.IsPlayed ? "Watch again" : "Play");
        if (target.Item.Type == "Episode" && target.Item.ParentIndexNumber is { } season && target.Item.IndexNumber is { } episode)
            label += $" S{season}:E{episode}";
        _heroPlayTitle.Text = label;
        AutomationProperties.SetName(_heroPlay, label);
        ToolTipService.SetToolTip(_heroPlay, label);
        _heroPlaySummary.Text = string.Join(" \u00b7 ", new[]
        {
            target.Item.Type == "Episode" ? target.Title : null,
            target.CanResume ? RemainingHomeTime(target) : RuntimeText(target.Item.RunTimeTicks)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var hasProgress = target.CanResume && target.ResumeTicks > TimeSpan.FromSeconds(30).Ticks;
        _heroRestart.Visibility = hasProgress ? Visibility.Visible : Visibility.Collapsed;
        RenderHomePlayRing(hasProgress ? Math.Clamp(target.Progress / 100, 0, 1) : 0);
        UpdateHomeInteraction();
    }

    private void RenderHomePlayRing(double progress)
    {
        _heroPlayRing.Children.Clear();
        if (progress <= 0) return;
        _heroPlayRing.Children.Add(new Ellipse
        {
            Width = 103, Height = 103, Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(46, 255, 255, 255)), StrokeThickness = 3
        });
        if (progress >= .999)
        {
            _heroPlayRing.Children.Add(new Ellipse { Width = 103, Height = 103, Stroke = LumenTheme.Brush("ImageAccent"), StrokeThickness = 3 });
            return;
        }
        var angle = progress * Math.PI * 2 - Math.PI / 2;
        var figure = new PathFigure { StartPoint = new Point(54, 4), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(54 + 50 * Math.Cos(angle), 54 + 50 * Math.Sin(angle)), Size = new Size(50, 50),
            IsLargeArc = progress > .5, SweepDirection = SweepDirection.Clockwise
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _heroPlayRing.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry, Stroke = LumenTheme.Brush("ImageAccent"), StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
    }

    private void PlaySelectedHero(bool fromStart)
    {
        if (_homeDeck.SelectedIndex != 0 || !ViewModel.IsPlaybackAllowed || HomeInteractionBlocked?.Invoke() == true
            || _heroItems.ElementAtOrDefault(_heroIndex) is not { } item) return;
        var target = HomePlaybackTarget(item);
        if (!target.CanPlay && target.Item.Type is not ("Series" or "Season")) return;
        ForwardPlay(new(target.Item, !fromStart && target.CanResume ? target.ResumeTicks : 0));
    }

    private void UpdateHomeInteraction()
    {
        if (_renderedPage != "home") return;
        var featured = _homeDeck.SelectedIndex == 0;
        var blocked = HomeInteractionBlocked?.Invoke() == true;
        FrameworkElement? info = featured ? _heroInfo : _homeShelves.ElementAtOrDefault(_homeDeck.SelectedIndex - 1)?.InfoHost;
        if (info is null) return;
        var playFocus = featured && !blocked && (_heroPlay.FocusState is FocusState.Keyboard or FocusState.Programmatic
            || _heroRestart.FocusState is FocusState.Keyboard or FocusState.Programmatic);
        double playProximity = playFocus ? 1 : 0;
        double informationProximity = 0;
        if (!blocked && _homePointerPosition is { } pointer && Environment.TickCount64 - _homeFocusResetAt >= 1050)
        {
            if (featured && _heroPlayback.Visibility == Visibility.Visible)
            {
                var dx = (pointer.X - ActualWidth * .5) / Math.Max(220, ActualWidth * .26);
                var dy = (pointer.Y - ActualHeight * .4) / Math.Max(150, ActualHeight * .21);
                playProximity = Math.Max(playProximity, SmoothHomeFocus(Math.Clamp((1 - Math.Sqrt(dx * dx + dy * dy)) / .7, 0, 1)));
            }
            if (info.IsLoaded)
            {
                var bounds = info.TransformToVisual(_home).TransformBounds(new Rect(0, 0, info.ActualWidth, info.ActualHeight));
                var dx = Math.Max(bounds.Left - pointer.X, Math.Max(0, pointer.X - bounds.Right));
                var dy = Math.Max(bounds.Top - pointer.Y, Math.Max(0, pointer.Y - bounds.Bottom));
                informationProximity = SmoothHomeFocus(Math.Clamp(1 - Math.Sqrt(dx * dx + dy * dy) / 140, 0, 1));
            }
        }
        _heroPlayback.Opacity = playProximity;
        _heroPlayback.IsHitTestVisible = playProximity > .2;
        _heroPlaybackScale.ScaleX = _heroPlaybackScale.ScaleY = .9 + .1 * playProximity;
        _heroPlaybackLabels.Opacity = playProximity > .55 ? 1 : 0;
        info.Opacity = Math.Clamp(.66 + .34 * informationProximity - .36 * playProximity, .3, 1);
        _homeFocusDim.Opacity = Math.Max(playProximity * .3, informationProximity * .42);
    }

    private static double SmoothHomeFocus(double value) => value * value * (3 - 2 * value);
}
