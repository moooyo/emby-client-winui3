using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
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
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenMediaCard : UserControl
{
    private readonly bool _landscape;
    private readonly LibraryViewModel _library;
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
    private readonly Grid? _landscapeLabels;
    private readonly StackPanel? _landscapeTitles;
    private readonly Border? _landscapeFade;
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
    private bool _homeStripStyle;
    private bool _recommendationStyle;
    private bool _searchPosterStyle;
    private bool _searchDiscoveryStyle;
    private Grid? _searchSubtitleRow;
    private FrameworkElement? _subtitleRatingIcon;
    private bool _selected;
    private bool _onImage;
    private bool _primaryActionToPlay;
    private bool _continueWatchingContext;
    private string? _titleQuery;
    private string? _subtitleOverride;
    private double _dimensionWidth = double.NaN;

    public MediaCardViewModel Item { get; }
    public string? SearchContext => _titleQuery;
    public event EventHandler<MediaCardViewModel>? OpenRequested;
    public event EventHandler<MediaCardViewModel>? FavoriteRequested;
    public event EventHandler<MediaCardViewModel>? WatchedRequested;
    public event EventHandler<MediaCardViewModel>? HoverRequested;
    public event EventHandler<LumenPlayRequestEventArgs>? PlayRequested;
    public event EventHandler<MediaCardViewModel>? MediaInformationRequested;
    public event EventHandler<MediaCardViewModel>? CollectionRequested;
    public event EventHandler<MediaCardViewModel>? RemoveFromContinueWatchingRequested;

    static LumenMediaCard() => LumenText.Register(new Dictionary<string, string>
    {
        ["Mark played"] = "\u6807\u8bb0\u5df2\u770b", ["Mark unplayed"] = "\u6807\u8bb0\u672a\u770b",
        ["More"] = "\u66f4\u591a", ["Remaining {0} min"] = "\u5269\u4f59 {0} \u5206\u949f",
        ["seasons"] = "\u5b63", ["Play from beginning"] = "\u4ece\u5934\u64ad\u653e",
        ["Add to queue"] = "\u6dfb\u52a0\u5230\u64ad\u653e\u961f\u5217",
        ["Remove from continue watching"] = "\u4ece\u7ee7\u7eed\u89c2\u770b\u4e2d\u79fb\u9664",
        ["Tag"] = "\u6807\u7b7e", ["Overview"] = "\u7b80\u4ecb"
    });

    public LumenMediaCard(MediaCardViewModel item, LibraryViewModel library, bool landscape = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(library);
        Item = item;
        _library = library;
        _landscape = landscape;
        IsTabStop = false;
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
        _open.Tag = item;
        _open.Click += (_, _) => InvokePrimaryAction();
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
            _landscapeFade = new Border { Background = BottomGradient(), IsHitTestVisible = false, CornerRadius = new CornerRadius(12) };
            _surface.Children.Add(_landscapeFade);
            var labels = new Grid { Margin = new Thickness(14, 0, 14, 20), VerticalAlignment = VerticalAlignment.Bottom, ColumnSpacing = 10, IsHitTestVisible = false };
            _landscapeLabels = labels;
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titles = new StackPanel { Spacing = 3, MinWidth = 0 };
            _landscapeTitles = titles;
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

        _play = LumenUi.Button("Play", primary: true, onImage: true, height: landscape ? 50 : 52);
        _play.Width = landscape ? 50 : 52;
        _play.Padding = new Thickness(0);
        _play.Content = new Image
        {
            Width = 22, Height = 22, IsHitTestVisible = false,
            Source = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri("ms-appx:///Assets/Lumen/Icons/d/play_24_filled.svg"))
        };
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
        GotFocus += (_, _) => { _focused = true; ShowOverlay(); HoverRequested?.Invoke(this, Item); };
        LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            _focused = XamlRoot is not null && Contains(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);
            ShowOverlay();
        });
        RightTapped += (_, e) => { ShowMore(); e.Handled = true; };
        Holding += (_, e) => { if (e.HoldingState == Microsoft.UI.Input.HoldingState.Started) ShowMore(); e.Handled = true; };
        Tapped += CardTapped;
        PreviewKeyDown += CardKeyDown;
        PreviewKeyUp += (_, args) =>
        {
            if (args.Key == VirtualKey.Space && !_homeStripStyle) args.Handled = true;
        };
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
    public void SetLibraryWallStyle(bool enabled = true)
    {
        if (!_landscape) _title.Margin = new Thickness(0, enabled ? 9 : 10, 0, 0);
    }

    public void SetRecommendationStyle(bool enabled = true)
    {
        var requested = enabled && !_landscape;
        if (_recommendationStyle == requested) return;
        _recommendationStyle = requested;
        _title.FontSize = requested ? 14 : 13;
        _title.FontWeight = requested ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;
        _title.Margin = new Thickness(0, requested ? 9 : 10, 0, 0);
        _subtitle.FontSize = requested ? 12.5 : 12;
        _subtitle.Opacity = requested ? .5 : _onImage ? .55 : 1;
        var radius = requested ? 14 : 12;
        _artwork.CornerRadius = _open.CornerRadius = _hoverShade.CornerRadius = _ring.CornerRadius = new CornerRadius(radius);
        if (_shadowGeometry is not null) _shadowGeometry.CornerRadius = new Vector2(radius);
        if (requested)
        {
            _surface.ClearValue(RenderTransformProperty);
            _layout.RenderTransform = _hoverTransform;
        }
        else
        {
            _layout.ClearValue(RenderTransformProperty);
            _surface.RenderTransform = _hoverTransform;
        }
        Refresh();
        ShowOverlay(false);
    }

    public void SetSearchPosterStyle(bool discovery = false)
    {
        if (_landscape) return;
        _searchPosterStyle = true;
        _searchDiscoveryStyle = discovery;
        _title.FontSize = 13;
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.Margin = new Thickness(0, discovery ? 9 : 10, 0, 0);
        _subtitle.FontSize = 12;
        _subtitle.Opacity = 1;
        _surface.ClearValue(RenderTransformProperty);
        _layout.RenderTransform = _hoverTransform;
        if (discovery && _searchSubtitleRow is null)
        {
            _layout.Children.Remove(_subtitle);
            _searchSubtitleRow = new Grid { ColumnSpacing = 4, Margin = new Thickness(0, 2, 0, 0) };
            _searchSubtitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _searchSubtitleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _subtitleRatingIcon = LumenUi.Icon("star", 11);
            _subtitleRatingIcon.VerticalAlignment = VerticalAlignment.Center;
            _searchSubtitleRow.Children.Add(_subtitleRatingIcon);
            _subtitle.Margin = new Thickness(0);
            Grid.SetRow(_subtitle, 0);
            Grid.SetColumn(_subtitle, 1);
            _searchSubtitleRow.Children.Add(_subtitle);
            Grid.SetRow(_searchSubtitleRow, 2);
            _layout.Children.Add(_searchSubtitleRow);
        }
        Refresh();
        ShowOverlay(false);
    }

    public void SetHomeStripStyle(bool enabled = true)
    {
        var requested = enabled && _landscape;
        if (_homeStripStyle == requested) return;
        _homeStripStyle = requested;
        _title.FontSize = _homeStripStyle ? 14 : _landscape ? 15 : 13;
        _subtitle.FontSize = _homeStripStyle ? 12.5 : 12;
        if (_landscapeLabels is not null && _landscapeTitles is not null && _landscapeFade is not null)
        {
            _landscapeLabels.Visibility = _landscapeFade.Visibility = requested ? Visibility.Collapsed : Visibility.Visible;
            if (requested)
            {
                _landscapeTitles.Children.Clear();
                while (_layout.RowDefinitions.Count < 3) _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _title.Margin = new Thickness(0, 10, 0, 0);
                _subtitle.Margin = new Thickness(0, 3, 0, 0);
                _subtitle.Opacity = .5;
                Grid.SetRow(_title, 1);
                Grid.SetRow(_subtitle, 2);
                _layout.Children.Add(_title);
                _layout.Children.Add(_subtitle);
                _surface.ClearValue(RenderTransformProperty);
                _layout.RenderTransform = _hoverTransform;
                _track.Margin = new Thickness(0);
            }
            else
            {
                _layout.Children.Remove(_title);
                _layout.Children.Remove(_subtitle);
                _landscapeTitles.Children.Add(_title);
                _landscapeTitles.Children.Add(_subtitle);
                _title.Margin = _subtitle.Margin = new Thickness(0);
                _subtitle.Opacity = .8;
                _layout.ClearValue(RenderTransformProperty);
                _surface.RenderTransform = _hoverTransform;
                _track.Margin = new Thickness(14, 0, 14, 10);
            }
        }
        ShowOverlay(false);
    }

    public void SetSelected(bool selected)
    {
        if (_selected == selected) return;
        _selected = selected;
        ShowOverlay();
    }

    public void SetOnImage(bool enabled = true)
    {
        _onImage = enabled;
        _title.Foreground = enabled || _landscape ? LumenTheme.Brush("ImageInk") : LumenTheme.Brush("Ink");
        _subtitle.Foreground = enabled || _landscape ? LumenTheme.Brush("ImageInk") : LumenTheme.Brush("Muted");
        _subtitle.Opacity = enabled ? .55 : _landscape ? .8 : 1;
        RefreshTitle();
    }

    public void SetSubtitleOverride(string? text)
    {
        _subtitleOverride = text;
        _subtitle.Text = text ?? (_landscape ? LandscapeSubtitle() : PosterSubtitle());
    }

    public bool FocusCard(FocusState state = FocusState.Keyboard) => _open.Focus(state);
    public void SetTabStop(bool enabled) => _open.IsTabStop = enabled;
    public void SetPrimaryActionToPlay(bool enabled = true) { _primaryActionToPlay = enabled; Refresh(); }
    public void SetContinueWatchingContext(bool enabled = true) => _continueWatchingContext = enabled;
    public void SetTitleHighlight(string? query)
    {
        if (_titleQuery == query) return;
        _titleQuery = query;
        RefreshTitle();
        _subtitle.Text = _subtitleOverride ?? (_landscape ? LandscapeSubtitle() : PosterSubtitle());
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

    private void ThemeChanged(object? sender, EventArgs args) => UpdateShadow(!(_recommendationStyle || _searchPosterStyle) && (_hovered || _focused || _selected), false);

    private void CreateShadow()
    {
        if (_shadowVisual is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(_shadowHost).Compositor;
        // An off-tree rounded caster provides an alpha mask without painting over the artwork placeholder.
        _shadowGeometry = compositor.CreateRoundedRectangleGeometry();
        _shadowGeometry.CornerRadius = new Vector2(_recommendationStyle ? 14 : 12);
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
        var blur = _recommendationStyle ? 30f : on ? 48f : LumenTheme.IsDark ? 22f : 16f;
        var opacity = _recommendationStyle ? .4f : on ? .5f : LumenTheme.IsDark ? .35f : .14f;
        var offset = new Vector3(0, _recommendationStyle ? 12 : on ? 24 : LumenTheme.IsDark ? 8 : 6, 0);
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
        _subtitle.Text = _subtitleOverride ?? (_landscape ? LandscapeSubtitle() : PosterSubtitle());
        if (_subtitleRatingIcon is not null)
            _subtitleRatingIcon.Visibility = _searchDiscoveryStyle && Item.Item.CommunityRating is not null ? Visibility.Visible : Visibility.Collapsed;
        _remaining.Text = Item.Item.RunTimeTicks is > 0 && Item.ResumeTicks > 0
            ? LumenText.Get("Remaining {0} min", Math.Max(0, (int)Math.Ceiling(TimeSpan.FromTicks(Item.Item.RunTimeTicks.Value - Math.Min(Item.ResumeTicks, Item.Item.RunTimeTicks.Value)).TotalMinutes))) : string.Empty;
        _unplayedLabel.Text = Item.UnplayedCountLabel;
        _unplayed.Visibility = !_searchPosterStyle && !_landscape && Item.UnplayedCountLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _watched.Visibility = !_searchPosterStyle && _showWatched && Item.IsPlayed && Item.UnplayedCountLabel.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _track.Visibility = _recommendationStyle || _searchPosterStyle ? Visibility.Collapsed : Item.ProgressVisibility;
        _rating.Text = Item.Item.CommunityRating?.ToString("0.0", CultureInfo.InvariantCulture) ?? "-";
        _quality.Text = QualityLabel();
        _play.Visibility = Item.CanPlay || Item.Item.Type == "Series" ? Visibility.Visible : Visibility.Collapsed;
        _play.IsEnabled = _library.IsPlaybackAllowed;
        _favorite.Content = ActionVisual(Item.IsFavorite ? "heart-filled" : "heart");
        _played.Content = ActionVisual(Item.IsPlayed ? "check-filled" : "check");
        UpdateActionLabel(_favorite, LumenText.Get(Item.IsFavorite ? "Remove favorite" : "Add favorite"));
        UpdateActionLabel(_played, LumenText.Get(Item.IsPlayed ? "Mark unplayed" : "Mark played"));
        var identity = Item.SceneIdentity;
        AutomationProperties.SetName(this, identity);
        AutomationProperties.SetName(_open, $"{identity}, {LumenText.Get(_primaryActionToPlay ? Item.CanResume ? "Resume" : "Play" : "Details")}");
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
            _title.Inlines.Add(new Run { Text = text.Substring(hit, _titleQuery.Length), Foreground = LumenTheme.Brush(_onImage || _landscape ? "ImageAccent" : "Accent") });
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

    private void RequestPlay(long position, bool queue = false)
    {
        if (_library.IsPlaybackAllowed && (Item.CanPlay || Item.Item.Type is "Series" or "Season"))
            PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(Item.Item, position, queue));
    }

    private void InvokePrimaryAction()
    {
        if (_primaryActionToPlay) RequestPlay(Item.CanResume ? Item.ResumeTicks : 0);
        else OpenRequested?.Invoke(this, Item);
    }

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
        InvokePrimaryAction(); e.Handled = true;
    }

    private void CardKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)) return;
        var target = args.OriginalSource as DependencyObject;
        while (target is not null && !ReferenceEquals(target, this))
        {
            if (target is Button button && !ReferenceEquals(button, _open)) return;
            target = VisualTreeHelper.GetParent(target);
        }
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            InvokePrimaryAction();
        }
        else if (args.Key == VirtualKey.Space && !_homeStripStyle)
        {
            args.Handled = true;
            RequestPlay(Item.CanResume ? Item.ResumeTicks : 0);
        }
        else if (args.Key == VirtualKey.Application || args.Key == VirtualKey.F10 && IsKeyDown(VirtualKey.Shift))
        {
            args.Handled = true;
            ShowMore();
        }
        else if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            if (_openFlyout is not null) _openFlyout.Hide();
            else
            {
                var ancestor = VisualTreeHelper.GetParent(this);
                while (ancestor is not null && ancestor is not ScrollViewer) ancestor = VisualTreeHelper.GetParent(ancestor);
                if (ancestor is ScrollViewer scroll) scroll.Focus(FocusState.Programmatic);
                _focused = false;
                ShowOverlay();
            }
        }
    }

    private static bool IsKeyDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static readonly ConditionalWeakTable<FrameworkElement, CardNavigationGroup> NavigationGroups = new();
    private const string NavigationHostMarker = "Lumen.LibraryItemHost";

    internal static void MarkNavigationHost(Border host) => host.Tag = NavigationHostMarker;

    internal static FrameworkElement? UnwrapNavigationElement(DependencyObject? element)
    {
        if (element is Border { Tag: string marker, Child: FrameworkElement child }
            && string.Equals(marker, NavigationHostMarker, StringComparison.Ordinal))
            return child;
        return element as FrameworkElement;
    }

    public static void ConfigureNavigationGroup(FrameworkElement group, bool horizontalOnly = false, bool initialNavigation = true)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (NavigationGroups.TryGetValue(group, out var existing))
        {
            existing.HorizontalOnly = horizontalOnly;
            existing.InitialNavigation = initialNavigation;
            return;
        }
        var navigation = new CardNavigationGroup(group, horizontalOnly, initialNavigation);
        NavigationGroups.Add(group, navigation);
        group.TabFocusNavigation = KeyboardNavigationMode.Once;
        group.XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;
        group.PreviewKeyDown += navigation.OnKeyDown;
        group.GotFocus += navigation.OnGotFocus;
        group.Loaded += (_, _) => navigation.UpdateTabStops();
        if (group is ItemsRepeater repeater)
        {
            repeater.ElementPrepared += (_, _) => navigation.UpdateTabStops();
            repeater.ElementClearing += (_, _) => group.DispatcherQueue.TryEnqueue(navigation.UpdateTabStops);
        }
    }

    public static bool FocusNavigationItem(FrameworkElement group, int index, bool bringIntoView = true, Func<bool>? canFocus = null)
    {
        if (!NavigationGroups.TryGetValue(group, out var navigation))
        {
            ConfigureNavigationGroup(group);
            NavigationGroups.TryGetValue(group, out navigation);
        }
        return navigation?.FocusItem(index, bringIntoView, canFocus) == true;
    }

    private sealed partial class CardNavigationGroup(FrameworkElement group, bool horizontalOnly, bool initialNavigation)
    {
        private int _focusedIndex;
        public bool HorizontalOnly { get; set; } = horizontalOnly;
        public bool InitialNavigation { get; set; } = initialNavigation;

        public void OnGotFocus(object sender, RoutedEventArgs args)
        {
            var source = args.OriginalSource as DependencyObject;
            var members = RealizedMembers();
            foreach (var member in members)
            {
                if (!IsWithin(source, member.Element)) continue;
                _focusedIndex = member.Index;
                UpdateTabStops();
                break;
            }
        }

        public void UpdateTabStops()
        {
            var members = RealizedMembers();
            if (members.Count == 0) return;
            if (!members.Any(member => member.Index == _focusedIndex))
                _focusedIndex = members.Min(member => member.Index);
            foreach (var member in members)
                if (NavigationButton(member.Element) is { } button) button.IsTabStop = member.Index == _focusedIndex;
        }

        public void OnKeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Handled || IsKeyDown(VirtualKey.Control) || IsKeyDown(VirtualKey.Menu)) return;
            var count = ItemCount();
            if (count == 0) return;
            var target = _focusedIndex;
            var columns = ColumnCount();
            switch (args.Key)
            {
                case VirtualKey.Left: target--; break;
                case VirtualKey.Right: target++; break;
                case VirtualKey.Up when !HorizontalOnly: target -= columns; break;
                case VirtualKey.Down when !HorizontalOnly: target += columns; break;
                case VirtualKey.Home when !HorizontalOnly: target = 0; break;
                case VirtualKey.End when !HorizontalOnly: target = count - 1; break;
                default:
                    if (!InitialNavigation || HorizontalOnly || args.Key < VirtualKey.A || args.Key > VirtualKey.Z) return;
                    var initial = ((char)('A' + (int)args.Key - (int)VirtualKey.A)).ToString();
                    target = Enumerable.Range(0, count).FirstOrDefault(index =>
                    {
                        var item = ItemAt(index);
                        return (item?.Item.SortName ?? item?.Title)?.TrimStart().StartsWith(initial, StringComparison.OrdinalIgnoreCase) == true;
                    }, -1);
                    if (target < 0) return;
                    break;
            }
            args.Handled = true;
            FocusItem(Math.Clamp(target, 0, count - 1));
        }

        public bool FocusItem(int index, bool bringIntoView = true, Func<bool>? canFocus = null)
        {
            if (index < 0 || index >= ItemCount() || canFocus?.Invoke() == false) return false;
            var item = ItemAt(index);
            FrameworkElement? element;
            if (group is ItemsRepeater repeater)
            {
                element = repeater.TryGetElement(index) as FrameworkElement;
                if (element is null && bringIntoView)
                {
                    element = repeater.GetOrCreateElement(index) as FrameworkElement;
                    repeater.UpdateLayout();
                }
            }
            else element = RealizedMembers().FirstOrDefault(member => member.Index == index).Element;
            if (element is null) return false;
            if (bringIntoView)
            {
                var bring = new BringIntoViewOptions
                {
                    AnimationDesired = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled,
                    HorizontalAlignmentRatio = .5
                };
                if (!HorizontalOnly) { bring.VerticalAlignmentRatio = .15; bring.VerticalOffset = -100; }
                // Complete GetOrCreateElement's anchor lifecycle even when the later focus attempt cannot succeed.
                element.StartBringIntoView(bring);
            }
            if (group is ItemsRepeater currentRepeater && currentRepeater.GetElementIndex(element) != index) return false;
            element = UnwrapNavigationElement(element);
            if (element is null || NavigationButton(element) is not { } button) return false;
            if (item is not null && !ReferenceEquals(item, element is LumenMediaCard card ? card.Item : button.Tag)) return false;
            if (canFocus?.Invoke() == false) return false;
            _focusedIndex = index;
            UpdateTabStops();
            if (canFocus?.Invoke() == false) return false;
            // Programmatic focus does not enqueue keyboard focus's extra bring-into-view request.
            return button.Focus(bringIntoView ? FocusState.Keyboard : FocusState.Programmatic);
        }

        private int ItemCount() => group is ItemsRepeater repeater ? repeater.ItemsSourceView?.Count ?? 0 : RealizedMembers().Count;

        private MediaCardViewModel? ItemAt(int index)
        {
            if (group is ItemsRepeater repeater) return repeater.ItemsSourceView?.GetAt(index) as MediaCardViewModel;
            var element = UnwrapNavigationElement(RealizedMembers().FirstOrDefault(member => member.Index == index).Element);
            return element is LumenMediaCard card ? card.Item : NavigationButton(element)?.Tag as MediaCardViewModel;
        }

        private int ColumnCount()
        {
            if (HorizontalOnly) return 1;
            if (group is ItemsRepeater { Layout: UniformGridLayout layout })
            {
                var minimum = Math.Max(1, layout.MinItemWidth + layout.MinColumnSpacing);
                return Math.Max(1, (int)Math.Floor((group.ActualWidth + layout.MinColumnSpacing) / minimum));
            }
            var members = RealizedMembers();
            if (members.Count < 2) return 1;
            var first = members[0].Element.TransformToVisual(group).TransformPoint(new Point());
            return Math.Max(1, members.Count(member => Math.Abs(member.Element.TransformToVisual(group).TransformPoint(new Point()).Y - first.Y) < 8));
        }

        private List<(int Index, FrameworkElement Element)> RealizedMembers()
        {
            var members = new List<(int, FrameworkElement)>();
            if (group is ItemsRepeater repeater)
            {
                for (var childIndex = 0; childIndex < VisualTreeHelper.GetChildrenCount(repeater); childIndex++)
                {
                    if (VisualTreeHelper.GetChild(repeater, childIndex) is not FrameworkElement element) continue;
                    var index = repeater.GetElementIndex(element);
                    if (index >= 0 && NavigationButton(element) is not null) members.Add((index, element));
                }
                members.Sort((left, right) => left.Item1.CompareTo(right.Item1));
            }
            else AddMembers(group, members);
            return members;
        }

        private static void AddMembers(DependencyObject parent, List<(int, FrameworkElement)> members)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is LumenMediaCard card) members.Add((members.Count, card));
                else if (child is Button { Tag: MediaCardViewModel } button) members.Add((members.Count, button));
                else AddMembers(child, members);
            }
        }

        private static Button? NavigationButton(DependencyObject? element)
        {
            if (element is null) return null;
            element = UnwrapNavigationElement(element) ?? element;
            if (element is LumenMediaCard card) return card._open;
            if (element is Button { Tag: MediaCardViewModel } button) return button;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
                if (NavigationButton(VisualTreeHelper.GetChild(element, index)) is { } candidate) return candidate;
            return null;
        }

        private static bool IsWithin(DependencyObject? child, DependencyObject parent)
        {
            while (child is not null)
            {
                if (ReferenceEquals(child, parent)) return true;
                child = VisualTreeHelper.GetParent(child);
            }
            return false;
        }
    }

    private void ShowOverlay(bool animateTransition = true)
    {
        var on = _hovered || _focused || _selected;
        var simplePoster = _recommendationStyle || _searchPosterStyle;
        var showActions = on && !_homeStripStyle && !simplePoster;
        var raisedY = on ? simplePoster ? -5 : -6 : 0;
        var scale = on && !_homeStripStyle && !simplePoster ? 1.035 : 1;
        Opacity = _homeStripStyle && !on ? .6 : 1;
        var oldOverlay = _overlay.Opacity;
        var oldShade = _hoverShade.Opacity;
        var oldY = _hoverTransform.TranslateY;
        var oldScaleX = _hoverTransform.ScaleX;
        var oldScaleY = _hoverTransform.ScaleY;
        _hoverAnimation?.Stop();
        _hoverShade.Visibility = _overlay.Visibility = Visibility.Visible;
        _overlay.IsHitTestVisible = showActions;
        _play.IsTabStop = _favorite.IsTabStop = _played.IsTabStop = _more.IsTabStop = false;
        _hoverShade.Opacity = _overlay.Opacity = showActions ? 1 : 0;
        _ring.BorderBrush = _recommendationStyle ? RecommendationStroke
            : on && !simplePoster ? _landscape ? LumenUi.WhiteBrush : LumenTheme.Brush("ImageAccent") : LumenTheme.Brush("Line");
        _ring.BorderThickness = new Thickness(on && !simplePoster ? 2 : 1);
        var animate = animateTransition && IsLoaded && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        UpdateShadow(on && !simplePoster, animate);
        if (!_landscape || _homeStripStyle)
        {
            _hoverTransform.TranslateY = raisedY;
            _hoverTransform.ScaleX = _hoverTransform.ScaleY = scale;
        }
        if (!animate)
        {
            _hoverShade.Visibility = _overlay.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var storyboard = new Storyboard();
        _hoverAnimation = storyboard;
        AddOpacityAnimation(storyboard, _hoverShade, oldShade, showActions ? 1 : 0);
        AddOpacityAnimation(storyboard, _overlay, oldOverlay, showActions ? 1 : 0);
        if (!_landscape || _homeStripStyle)
        {
            AddHoverAnimation(storyboard, nameof(CompositeTransform.TranslateY), oldY, raisedY);
            AddHoverAnimation(storyboard, nameof(CompositeTransform.ScaleX), oldScaleX, scale);
            AddHoverAnimation(storyboard, nameof(CompositeTransform.ScaleY), oldScaleY, scale);
        }
        storyboard.Completed += (_, _) =>
        {
            if (!showActions && ReferenceEquals(_hoverAnimation, storyboard))
                _hoverShade.Visibility = _overlay.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private void AddHoverAnimation(Storyboard storyboard, string property, double from, double value)
    {
        var animation = new DoubleAnimation { From = from, To = value, Duration = TimeSpan.FromMilliseconds(_recommendationStyle || _searchPosterStyle ? 200 : 250), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
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
        var rows = new StackPanel { Width = 244, Spacing = 2, TabFocusNavigation = KeyboardNavigationMode.Cycle };
        void Add(string label, string icon, Action action, string? shortcut = null, bool enabled = true)
        {
            var button = LumenUi.Button(LumenText.Get(label), icon, height: 38);
            button.IsEnabled = enabled;
            button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.BorderThickness = new Thickness(0); button.CornerRadius = new CornerRadius(10);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            if (shortcut is not null)
            {
                var content = new Grid { ColumnSpacing = 18 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var title = (UIElement)button.Content;
                button.Content = null;
                content.Children.Add(title);
                var hint = LumenUi.Text(shortcut, 11);
                hint.Foreground = LumenTheme.Brush("Muted");
                hint.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(hint, 1);
                content.Children.Add(hint);
                button.Content = content;
                button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            }
            button.Click += (_, _) => { flyout.Hide(); action(); };
            rows.Children.Add(button);
        }
        Add("Details", "info", () => OpenRequested?.Invoke(this, Item), "Enter");
        if (Item.CanPlay || Item.Item.Type is "Series" or "Season")
        {
            Add(Item.CanResume ? "Resume" : "Play", "play", () => RequestPlay(Item.CanResume ? Item.ResumeTicks : 0), "Space", _library.IsPlaybackAllowed);
            if (Item.CanResume) Add("Play from beginning", "rotate-ccw", () => RequestPlay(0), enabled: _library.IsPlaybackAllowed);
            Add("Add to queue", "list-video", () => RequestPlay(0, true), enabled: _library.IsPlaybackAllowed);
        }
        rows.Children.Add(LumenUi.Divider());
        Add(Item.IsPlayed ? "Mark unplayed" : "Mark played", "check", () => WatchedRequested?.Invoke(this, Item));
        Add(Item.IsFavorite ? "Remove favorite" : "Add favorite", "heart", () => FavoriteRequested?.Invoke(this, Item));
        if (_continueWatchingContext && RemoveFromContinueWatchingRequested is not null)
            Add("Remove from continue watching", "x", () => RemoveFromContinueWatchingRequested?.Invoke(this, Item));
        if (CollectionRequested is not null && Item.Item.Type != "BoxSet")
            Add("Add to collection", "plus", () => CollectionRequested?.Invoke(this, Item));
        if (MediaInformationRequested is not null)
        {
            rows.Children.Add(LumenUi.Divider());
            Add("Media information", "info", () => MediaInformationRequested?.Invoke(this, Item));
        }
        rows.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape) { args.Handled = true; flyout.Hide(); return; }
            if (args.Key is not (VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End)) return;
            var buttons = rows.Children.OfType<Button>().Where(button => button.IsEnabled).ToArray();
            var current = Array.IndexOf(buttons, FocusManager.GetFocusedElement(XamlRoot) as Button);
            var next = args.Key switch
            {
                VirtualKey.Home => 0, VirtualKey.End => buttons.Length - 1,
                VirtualKey.Up => (current + buttons.Length - 1) % buttons.Length,
                _ => (current + 1) % buttons.Length
            };
            args.Handled = true;
            if (buttons.Length > 0) buttons[next].Focus(FocusState.Keyboard);
        };
        flyout.Content = rows;
        flyout.Opened += (_, _) => rows.Children.OfType<Button>().FirstOrDefault()?.Focus(FocusState.Keyboard);
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

    private static readonly SolidColorBrush RecommendationStroke = new(Color.FromArgb(20, 255, 255, 255));

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
