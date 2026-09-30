using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenMediaCard : UserControl
{
    private readonly bool _landscape;
    private readonly Grid _layout = new();
    private readonly Grid _surface = new();
    private readonly Border _shadowHost = new() { IsHitTestVisible = false };
    private readonly Grid _overlay = new();
    private readonly Border _hoverShade = new();
    private readonly LumenArtwork _artwork = new();
    private readonly Border _ring;
    private readonly Border _unplayed;
    private readonly Border _watched;
    private readonly Border _track;
    private readonly Border _fill;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _remaining;
    private readonly TextBlock _unplayedLabel;
    private readonly TextBlock _rating;
    private readonly TextBlock _quality;
    private readonly Button _open;
    private readonly Button _play;
    private readonly Button _favorite;
    private readonly Button _played;
    private readonly Button _more;
    private readonly CompositeTransform _hoverTransform = new();
    private Storyboard? _hoverAnimation;
    private SpriteVisual? _shadowVisual;
    private DropShadow? _shadow;
    private CompositionRoundedRectangleGeometry? _shadowGeometry;
    private CompositionSpriteShape? _shadowShape;
    private ShapeVisual? _shadowCaster;
    private CompositionVisualSurface? _shadowSurface;
    private CompositionSurfaceBrush? _shadowMask;
    private CompositionColorBrush? _shadowFill;
    private Flyout? _openFlyout;
    private bool _hovered;
    private bool _focused;
    private bool _showWatched = true;
    private bool _subscribed;
    private string? _titleQuery;
    private double _dimensionWidth = double.NaN;

    public MediaCardViewModel Item { get; }
    public event EventHandler<MediaCardViewModel>? OpenRequested;
    public event EventHandler<MediaCardViewModel>? FavoriteRequested;
    public event EventHandler<MediaCardViewModel>? WatchedRequested;
    public event EventHandler<MediaCardViewModel>? HoverRequested;
    public event EventHandler<LumenPlayRequestEventArgs>? PlayRequested;

    static LumenMediaCard() => LumenText.Register(new Dictionary<string, string>
    {
        ["Mark played"] = "\u6807\u8bb0\u5df2\u770b", ["Mark unplayed"] = "\u6807\u8bb0\u672a\u770b",
        ["More"] = "\u66f4\u591a", ["Remaining {0} min"] = "\u5269\u4f59 {0} \u5206\u949f",
        ["seasons"] = "\u5b63", ["Play from beginning"] = "\u4ece\u5934\u64ad\u653e",
        ["Add to queue"] = "\u6dfb\u52a0\u5230\u64ad\u653e\u961f\u5217",
        ["Tag"] = "\u6807\u7b7e", ["Overview"] = "\u7b80\u4ecb"
    });

    public LumenMediaCard(MediaCardViewModel item, LibraryViewModel library, bool landscape = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(library);
        Item = item;
        _landscape = landscape;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        MinWidth = landscape ? 160 : 88;
        if (landscape) Width = 320;
        _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (!landscape)
        {
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        _surface.Height = landscape ? 180 : 240;
        _surface.RenderTransform = _hoverTransform;
        _surface.RenderTransformOrigin = new Point(.5, .5);
        _surface.Children.Add(_shadowHost);
        _surface.SizeChanged += (_, _) => UpdateShadowSize();
        _artwork.Set(item, library, landscape ? ArtworkKind.Landscape : ArtworkKind.Poster, landscape ? 640 : 480);
        _surface.Children.Add(_artwork);
        _open = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(12), UseSystemFocusVisuals = true
        };
        _open.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _open.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(35, 0, 0, 0));
        _open.Click += (_, _) => OpenRequested?.Invoke(this, Item);
        _surface.Children.Add(_open);

        _title = LumenUi.Text(item.DisplayTitle, landscape ? 15 : 13);
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.TextWrapping = TextWrapping.NoWrap;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle = LumenUi.Text(string.Empty, 12);
        _subtitle.TextWrapping = TextWrapping.NoWrap;
        _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _subtitle.Foreground = LumenTheme.Brush("Muted");
        _remaining = LumenUi.Text(string.Empty, 12);
        _remaining.Foreground = LumenUi.WhiteBrush;
        _remaining.Opacity = .8;
        _remaining.TextWrapping = TextWrapping.NoWrap;

        if (landscape)
        {
            _surface.Children.Add(new Border { Background = BottomGradient(), IsHitTestVisible = false, CornerRadius = new CornerRadius(12) });
            var labels = new Grid { Margin = new Thickness(14, 0, 14, 20), VerticalAlignment = VerticalAlignment.Bottom, ColumnSpacing = 10, IsHitTestVisible = false };
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel { Spacing = 3, MinWidth = 0 };
            _title.Foreground = LumenUi.WhiteBrush;
            _subtitle.Foreground = LumenUi.WhiteBrush;
            _subtitle.Opacity = .8;
            titles.Children.Add(_title); titles.Children.Add(_subtitle);
            labels.Children.Add(titles);
            _remaining.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(_remaining, 1);
            labels.Children.Add(_remaining);
            _surface.Children.Add(labels);
        }
        else
        {
            _title.Margin = new Thickness(0, 10, 0, 0);
            _subtitle.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetRow(_title, 1); Grid.SetRow(_subtitle, 2);
            _layout.Children.Add(_title); _layout.Children.Add(_subtitle);
        }

        _unplayedLabel = LumenUi.Text(string.Empty, 12);
        _unplayedLabel.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _unplayedLabel.Foreground = LumenTheme.Brush("ImageAccentInk");
        _unplayedLabel.VerticalAlignment = VerticalAlignment.Center;
        _unplayed = new Border
        {
            MinWidth = 22, Height = 22, Padding = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(11),
            Background = LumenTheme.Brush("ImageAccent"), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Child = _unplayedLabel, IsHitTestVisible = false
        };
        _watched = new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8), Child = LumenUi.Icon("check-filled", 12, true), IsHitTestVisible = false
        };
        _surface.Children.Add(_unplayed); _surface.Children.Add(_watched);
        _fill = new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), Background = LumenTheme.Brush("ImageAccent") };
        _track = new Border
        {
            Height = 3, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Color.FromArgb(71, 255, 255, 255)),
            Margin = landscape ? new Thickness(14, 0, 14, 10) : new Thickness(8, 0, 8, 8),
            VerticalAlignment = VerticalAlignment.Bottom, Child = _fill, IsHitTestVisible = false
        };
        _track.SizeChanged += (_, _) => UpdateProgress();
        _surface.Children.Add(_track);

        _hoverShade.Visibility = _overlay.Visibility = Visibility.Collapsed;
        _hoverShade.Opacity = _overlay.Opacity = 0;
        _hoverShade.CornerRadius = new CornerRadius(12);
        _hoverShade.Background = landscape ? new SolidColorBrush(Color.FromArgb(25, 0, 0, 0)) : HoverGradient();
        _hoverShade.IsHitTestVisible = false;
        _surface.Children.Add(_hoverShade);
        _overlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _overlay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _overlay.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _overlay.Margin = new Thickness(10);
        var metrics = new Grid { IsHitTestVisible = false };
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var score = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        score.Children.Add(LumenUi.Icon("star", 12, true));
        _rating = LumenUi.Text(string.Empty, 12);
        _rating.Foreground = LumenUi.WhiteBrush;
        _rating.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        score.Children.Add(_rating); metrics.Children.Add(score);
        _quality = LumenUi.Text(string.Empty, 10);
        _quality.Foreground = LumenUi.WhiteBrush;
        _quality.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        _quality.TextWrapping = TextWrapping.NoWrap;
        var qualityBorder = new Border { BorderThickness = new Thickness(1), BorderBrush = LumenUi.ImageStrokeBrush, CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1), Child = _quality };
        Grid.SetColumn(qualityBorder, 1); metrics.Children.Add(qualityBorder);
        if (!landscape) _overlay.Children.Add(metrics);

        _play = LumenUi.IconButton("play", LumenText.Get("Play"), landscape ? 50 : 52, true);
        _play.Background = LumenUi.WhiteBrush;
        _play.BorderThickness = new Thickness(0);
        _play.Content = new Image
        {
            Width = 22, Height = 22, IsHitTestVisible = false,
            Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/Lumen/Icons/d/play_24_filled.svg"))
        };
        _play.Resources["ButtonBackgroundPointerOver"] = LumenUi.WhiteBrush;
        _play.Resources["ButtonBackgroundPressed"] = LumenUi.WhiteBrush;
        _play.HorizontalAlignment = HorizontalAlignment.Center; _play.VerticalAlignment = VerticalAlignment.Center;
        _play.Click += (_, _) => RequestPlay(Item.CanResume ? Item.ResumeTicks : 0);
        Grid.SetRow(_play, 1); _overlay.Children.Add(_play);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, HorizontalAlignment = HorizontalAlignment.Center };
        _played = PosterAction("check", LumenText.Get("Mark played"));
        _played.Click += (_, _) => WatchedRequested?.Invoke(this, Item);
        _favorite = PosterAction("heart", LumenText.Get("Add favorite"));
        _favorite.Click += (_, _) => FavoriteRequested?.Invoke(this, Item);
        _more = PosterAction("ellipsis", LumenText.Get("More"));
        _more.Click += (_, _) => ShowMore();
        tools.Children.Add(_played); tools.Children.Add(_favorite); tools.Children.Add(_more);
        Grid.SetRow(tools, 2);
        if (!landscape) _overlay.Children.Add(tools);
        _surface.Children.Add(_overlay);
        _ring = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), BorderBrush = LumenTheme.Brush("Line"), IsHitTestVisible = false };
        _surface.Children.Add(_ring); _layout.Children.Add(_surface);
        Content = _layout;

        PointerEntered += (_, _) => { _hovered = true; ShowOverlay(); HoverRequested?.Invoke(this, Item); };
        PointerExited += (_, _) => { _hovered = false; ShowOverlay(); };
        GotFocus += (_, _) => { _focused = true; ShowOverlay(); };
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _focused = XamlRoot is not null && Contains(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);
            ShowOverlay();
        });
        RightTapped += (_, e) => { ShowMore(); e.Handled = true; };
        Holding += (_, e) => { if (e.HoldingState == Microsoft.UI.Input.HoldingState.Started) ShowMore(); e.Handled = true; };
        Tapped += CardTapped;
        SizeChanged += (_, args) =>
        {
            if (double.IsNaN(Width) && args.NewSize.Width > 0 && args.NewSize.Width != args.PreviousSize.Width)
                UpdateDimensions(args.NewSize.Width);
        };
        Loaded += (_, _) => { CreateShadow(); Subscribe(); Refresh(); ShowOverlay(false); };
        Unloaded += (_, _) =>
        {
            Unsubscribe();
            _openFlyout?.Hide();
            _hovered = _focused = false;
            _hoverAnimation?.Stop();
            _hoverAnimation = null;
            _hoverShade.Visibility = _overlay.Visibility = Visibility.Collapsed;
            _hoverShade.Opacity = _overlay.Opacity = 0;
            _overlay.IsHitTestVisible = false;
            _hoverTransform.TranslateY = 0;
            _hoverTransform.ScaleX = _hoverTransform.ScaleY = 1;
            ReleaseShadow();
        };
        Refresh();
    }

    public void SetCardWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (Width != width) Width = width;
        UpdateDimensions(width);
    }

    public void SetShowWatched(bool show) { _showWatched = show; Refresh(); }
    public void SetTitleHighlight(string? query)
    {
        _titleQuery = query;
        RefreshTitle();
        _subtitle.Text = _landscape ? LandscapeSubtitle() : PosterSubtitle();
    }
    public void SetSearchContext(string? query) => SetTitleHighlight(query);

    private void UpdateDimensions(double width)
    {
        if (_dimensionWidth == width) return;
        // Explicit-width cards use the requested width, not a second pixel-rounded size source.
        _dimensionWidth = width;
        _surface.Height = width * (_landscape ? 9d / 16 : 3d / 2);
        if (_landscape) { _remaining.MaxWidth = width * .45; return; }
        var inner = Math.Max(30, width - 20);
        var count = inner >= 90 ? 3 : 2;
        _played.Visibility = count == 3 ? Visibility.Visible : Visibility.Collapsed;
        var actionWidth = Math.Min(40, inner / count);
        foreach (var button in new[] { _played, _favorite, _more }) button.Width = button.MinWidth = actionWidth;
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        Item.PropertyChanged += ItemChanged;
        LumenTheme.Changed += ThemeChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            Item.PropertyChanged -= ItemChanged;
            LumenTheme.Changed -= ThemeChanged;
        }
        _subscribed = false;
    }

    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MediaCardViewModel.Item)) Refresh();
    }

    private void ThemeChanged(object? sender, EventArgs args) => UpdateShadow(_hovered || _focused, false);

    private void CreateShadow()
    {
        if (_shadowVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_shadowHost).Compositor;
        // An off-tree rounded caster provides an alpha mask without painting over the artwork placeholder.
        _shadowGeometry = compositor.CreateRoundedRectangleGeometry();
        _shadowGeometry.CornerRadius = new Vector2(12);
        _shadowFill = compositor.CreateColorBrush(Microsoft.UI.Colors.Black);
        _shadowShape = compositor.CreateSpriteShape(_shadowGeometry);
        _shadowShape.FillBrush = _shadowFill;
        _shadowCaster = compositor.CreateShapeVisual();
        _shadowCaster.Shapes.Add(_shadowShape);
        _shadowSurface = compositor.CreateVisualSurface();
        _shadowSurface.SourceVisual = _shadowCaster;
        _shadowMask = compositor.CreateSurfaceBrush(_shadowSurface);
        _shadow = compositor.CreateDropShadow();
        _shadow.Color = Microsoft.UI.Colors.Black;
        _shadow.Mask = _shadowMask;
        _shadowVisual = compositor.CreateSpriteVisual();
        _shadowVisual.RelativeSizeAdjustment = Vector2.One;
        _shadowVisual.Shadow = _shadow;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, _shadowVisual);
        UpdateShadowSize();
        UpdateShadow(false, false);
    }

    private void UpdateShadowSize()
    {
        if (_shadowGeometry is null || _shadowCaster is null || _shadowSurface is null
            || _surface.ActualWidth <= 0 || _surface.ActualHeight <= 0) return;
        var size = new Vector2((float)_surface.ActualWidth, (float)_surface.ActualHeight);
        _shadowGeometry.Size = size;
        _shadowCaster.Size = size;
        _shadowSurface.SourceSize = size;
    }

    private void ReleaseShadow()
    {
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, null);
        if (_shadowVisual is not null) _shadowVisual.Shadow = null;
        if (_shadow is not null)
        {
            _shadow.StopAnimation("BlurRadius");
            _shadow.StopAnimation("Opacity");
            _shadow.StopAnimation("Offset");
            _shadow.Mask = null;
        }
        if (_shadowMask is not null) _shadowMask.Surface = null;
        if (_shadowSurface is not null) _shadowSurface.SourceVisual = null;
        _shadowCaster?.Shapes.Clear();
        _shadowVisual?.Dispose();
        _shadow?.Dispose();
        _shadowMask?.Dispose();
        _shadowSurface?.Dispose();
        _shadowCaster?.Dispose();
        _shadowShape?.Dispose();
        _shadowGeometry?.Dispose();
        _shadowFill?.Dispose();
        _shadowVisual = null;
        _shadow = null;
        _shadowMask = null;
        _shadowSurface = null;
        _shadowCaster = null;
        _shadowShape = null;
        _shadowGeometry = null;
        _shadowFill = null;
    }

    private void UpdateShadow(bool on, bool animate)
    {
        if (_shadow is null) return;
        var blur = on ? 48f : LumenTheme.IsDark ? 22f : 16f;
        var opacity = on ? .5f : LumenTheme.IsDark ? .35f : .14f;
        var offset = new Vector3(0, on ? 24 : LumenTheme.IsDark ? 8 : 6, 0);
        if (!animate)
        {
            _shadow.StopAnimation("BlurRadius");
            _shadow.StopAnimation("Opacity");
            _shadow.StopAnimation("Offset");
            _shadow.BlurRadius = blur;
            _shadow.Opacity = opacity;
            _shadow.Offset = offset;
            return;
        }
        var compositor = _shadow.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(.2f, .8f), new Vector2(.2f, 1));
        void AnimateScalar(string property, float value)
        {
            var animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(1, value, easing);
            animation.Duration = TimeSpan.FromMilliseconds(250);
            _shadow.StartAnimation(property, animation);
        }
        AnimateScalar("BlurRadius", blur);
        AnimateScalar("Opacity", opacity);
        var movement = compositor.CreateVector3KeyFrameAnimation();
        movement.InsertKeyFrame(1, offset, easing);
        movement.Duration = TimeSpan.FromMilliseconds(250);
        _shadow.StartAnimation("Offset", movement);
    }

    private void Refresh()
    {
        RefreshTitle();
        _subtitle.Text = _landscape ? LandscapeSubtitle() : PosterSubtitle();
        _remaining.Text = Item.Item.RunTimeTicks is > 0 && Item.ResumeTicks > 0
            ? LumenText.Get("Remaining {0} min", Math.Max(0, (int)Math.Ceiling(TimeSpan.FromTicks(Item.Item.RunTimeTicks.Value - Math.Min(Item.ResumeTicks, Item.Item.RunTimeTicks.Value)).TotalMinutes))) : string.Empty;
        _unplayedLabel.Text = Item.UnplayedCountLabel;
        _unplayed.Visibility = !_landscape && Item.UnplayedCountLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _watched.Visibility = _showWatched && Item.IsPlayed && Item.UnplayedCountLabel.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _track.Visibility = Item.ProgressVisibility;
        _rating.Text = Item.Item.CommunityRating?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-";
        _quality.Text = QualityLabel();
        _play.Visibility = Item.CanPlay || Item.Item.Type == "Series" ? Visibility.Visible : Visibility.Collapsed;
        _favorite.Content = ActionVisual(Item.IsFavorite ? "heart-filled" : "heart");
        _played.Content = ActionVisual(Item.IsPlayed ? "check-filled" : "check");
        UpdateActionLabel(_favorite, LumenText.Get(Item.IsFavorite ? "Remove favorite" : "Add favorite"));
        UpdateActionLabel(_played, LumenText.Get(Item.IsPlayed ? "Mark unplayed" : "Mark played"));
        var identity = Item.SceneIdentity;
        AutomationProperties.SetName(this, identity);
        AutomationProperties.SetName(_open, $"{identity}, {LumenText.Get("Details")}");
        ToolTipService.SetToolTip(_open, identity);
        UpdateProgress();
    }

    private void RefreshTitle()
    {
        var text = _landscape ? Item.DisplayTitle : Item.Title;
        _title.Inlines.Clear();
        if (string.IsNullOrWhiteSpace(_titleQuery)) { _title.Text = text; return; }
        _title.Text = string.Empty;
        var position = 0;
        while (position < text.Length)
        {
            var hit = text.IndexOf(_titleQuery, position, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { _title.Inlines.Add(new Run { Text = text[position..] }); break; }
            if (hit > position) _title.Inlines.Add(new Run { Text = text[position..hit] });
            _title.Inlines.Add(new Run { Text = text.Substring(hit, _titleQuery.Length), Foreground = LumenTheme.Brush("Accent") });
            position = hit + _titleQuery.Length;
        }
    }

    private string PosterSubtitle()
    {
        var year = Item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(_titleQuery) && !Item.Title.Contains(_titleQuery, StringComparison.OrdinalIgnoreCase))
        {
            var tag = Item.Item.Tags?.FirstOrDefault(value => value?.Contains(_titleQuery, StringComparison.OrdinalIgnoreCase) == true);
            if (tag is not null) return string.Join(" \u00b7 ", new[] { year, LumenText.Get("Tag"), tag }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var overview = Item.Item.Overview;
            var hit = overview?.IndexOf(_titleQuery, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (hit >= 0 && overview is not null)
            {
                var start = Math.Max(0, hit - 8);
                var excerpt = overview.Substring(start, Math.Min(28, overview.Length - start)).Replace('\r', ' ').Replace('\n', ' ');
                return string.Join(" \u00b7 ", new[] { year, LumenText.Get("Overview"), (start > 0 ? "\u2026" : string.Empty) + excerpt }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }
        }
        return string.Join(" \u00b7 ", new[]
        {
            year,
            Item.Item.Type == "Series" && (Item.Item.SeasonCount ?? Item.Item.ChildCount) is { } count ? $"{count} {LumenText.Get("seasons")}" : Item.Item.Genres?.FirstOrDefault()
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private string LandscapeSubtitle() => Item.Item.Type == "Episode" ? $"{Item.EpisodeNumber} \u00b7 {Item.Title}"
        : string.Join(" \u00b7 ", new[] { LumenText.Get(Item.Item.Type ?? "Movie"), Item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture) }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private string QualityLabel()
    {
        if (Item.Item.Type == "Series") return (Item.Item.SeasonCount ?? Item.Item.ChildCount) is { } seasons ? $"{seasons} {LumenText.Get("seasons")}" : LumenText.Get("Series");
        var streams = Item.Item.MediaStreams ?? Item.Item.MediaSources?.FirstOrDefault()?.MediaStreams;
        var height = streams?.FirstOrDefault(stream => stream.Type == "Video")?.Height;
        return height is >= 1800 ? "4K" : height is > 0 ? $"{height}p" : string.Empty;
    }

    private void UpdateProgress() => _fill.Width = Math.Max(0, _track.ActualWidth * Math.Clamp(Item.Progress, 0, 100) / 100);

    private void RequestPlay(long position, bool queue = false) => PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(Item.Item, position, queue));

    private bool Contains(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, this)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void CardTapped(object sender, TappedRoutedEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node is not null && !ReferenceEquals(node, this))
        {
            if (node is Button) return;
            node = VisualTreeHelper.GetParent(node);
        }
        OpenRequested?.Invoke(this, Item); e.Handled = true;
    }

    private void ShowOverlay(bool animateTransition = true)
    {
        var on = _hovered || _focused;
        var oldOverlay = _overlay.Opacity;
        var oldShade = _hoverShade.Opacity;
        var oldY = _hoverTransform.TranslateY;
        var oldScaleX = _hoverTransform.ScaleX;
        var oldScaleY = _hoverTransform.ScaleY;
        _hoverAnimation?.Stop();
        _hoverShade.Visibility = _overlay.Visibility = Visibility.Visible;
        _overlay.IsHitTestVisible = on;
        _play.IsTabStop = _favorite.IsTabStop = _played.IsTabStop = _more.IsTabStop = on;
        _hoverShade.Opacity = _overlay.Opacity = on ? 1 : 0;
        _ring.BorderBrush = on ? _landscape ? LumenUi.WhiteBrush : LumenTheme.Brush("ImageAccent") : LumenTheme.Brush("Line");
        _ring.BorderThickness = new Thickness(on ? 2 : 1);
        var animate = animateTransition && IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        UpdateShadow(on, animate);
        if (!_landscape)
        {
            _hoverTransform.TranslateY = on ? -6 : 0;
            _hoverTransform.ScaleX = _hoverTransform.ScaleY = on ? 1.035 : 1;
        }
        if (!animate)
        {
            _hoverShade.Visibility = _overlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var storyboard = new Storyboard();
        _hoverAnimation = storyboard;
        AddOpacityAnimation(storyboard, _hoverShade, oldShade, on ? 1 : 0);
        AddOpacityAnimation(storyboard, _overlay, oldOverlay, on ? 1 : 0);
        if (!_landscape)
        {
            AddHoverAnimation(storyboard, nameof(CompositeTransform.TranslateY), oldY, on ? -6 : 0);
            AddHoverAnimation(storyboard, nameof(CompositeTransform.ScaleX), oldScaleX, on ? 1.035 : 1);
            AddHoverAnimation(storyboard, nameof(CompositeTransform.ScaleY), oldScaleY, on ? 1.035 : 1);
        }
        storyboard.Completed += (_, _) =>
        {
            if (!on && ReferenceEquals(_hoverAnimation, storyboard))
                _hoverShade.Visibility = _overlay.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private void AddHoverAnimation(Storyboard storyboard, string property, double from, double value)
    {
        var animation = new DoubleAnimation { From = from, To = value, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, _hoverTransform); Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private static void AddOpacityAnimation(Storyboard storyboard, UIElement target, double from, double value)
    {
        var animation = new DoubleAnimation { From = from, To = value, Duration = TimeSpan.FromMilliseconds(250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private void ShowMore()
    {
        if (_openFlyout is not null) { _openFlyout.Hide(); return; }
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        _openFlyout = flyout;
        flyout.Closed += (_, _) => { if (ReferenceEquals(_openFlyout, flyout)) _openFlyout = null; };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.BackgroundProperty, LumenTheme.Brush("Pop")));
        presenter.Setters.Add(new Setter(Control.ForegroundProperty, LumenTheme.Brush("Ink")));
        presenter.Setters.Add(new Setter(Control.BorderBrushProperty, LumenTheme.Brush("LineStrong")));
        presenter.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        presenter.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        flyout.FlyoutPresenterStyle = presenter;
        var rows = new StackPanel { MinWidth = 218, Spacing = 2 };
        void Add(string label, string icon, Action action)
        {
            var button = LumenUi.Button(LumenText.Get(label), icon, height: 38);
            button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(10);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Click += (_, _) => { flyout.Hide(); action(); };
            rows.Children.Add(button);
        }
        Add("Details", "info", () => OpenRequested?.Invoke(this, Item));
        if (Item.CanPlay)
        {
            if (Item.CanResume) Add("Resume", "play", () => RequestPlay(Item.ResumeTicks));
            Add("Play from beginning", "rotate-ccw", () => RequestPlay(0));
            Add("Add to queue", "list-video", () => RequestPlay(0, true));
        }
        Add(Item.IsPlayed ? "Mark unplayed" : "Mark played", "check", () => WatchedRequested?.Invoke(this, Item));
        Add(Item.IsFavorite ? "Remove favorite" : "Add favorite", "heart", () => FavoriteRequested?.Invoke(this, Item));
        flyout.Content = rows;
        flyout.ShowAt(_more.IsLoaded && _overlay.IsHitTestVisible ? _more : _open);
    }

    private static Button PosterAction(string icon, string label)
    {
        var button = LumenUi.IconButton(icon, label, 40, true);
        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderThickness = new Thickness(0); button.Content = ActionVisual(icon);
        return button;
    }

    private static Border ActionVisual(string icon) => new()
    {
        Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Background = LumenUi.ImageControlBrush,
        BorderBrush = LumenUi.ImageStrokeBrush, BorderThickness = new Thickness(1),
        Child = LumenUi.Icon(icon, 15, true), IsHitTestVisible = false
    };

    private static void UpdateActionLabel(Button button, string label)
    {
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label);
    }

    private static LinearGradientBrush BottomGradient() => new()
    {
        StartPoint = new Point(0, 1), EndPoint = new Point(0, 0),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(219, 0, 0, 0), Offset = 0 },
            new GradientStop { Color = Color.FromArgb(89, 0, 0, 0), Offset = .42 },
            new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = .7 }
        }
    };

    private static LinearGradientBrush HoverGradient() => new()
    {
        StartPoint = new Point(0, 1), EndPoint = new Point(0, 0),
        GradientStops =
        {
            new GradientStop { Color = Color.FromArgb(230, 0, 0, 0), Offset = 0 },
            new GradientStop { Color = Color.FromArgb(64, 0, 0, 0), Offset = .55 },
            new GradientStop { Color = Color.FromArgb(102, 0, 0, 0), Offset = 1 }
        }
    };
}
