using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.ViewManagement;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly Grid _home = new();
    private readonly LumenPageDeck _homeDeck = new();
    private readonly Grid _hero = new();
    private readonly ScrollViewer _heroViewport = new()
    {
        HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false
    };
    private readonly Grid _heroLayout = new();
    private readonly Grid _heroContent = new() { ColumnSpacing = 40 };
    private readonly StackPanel _heroInfo = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
        MaxWidth = 680, Opacity = .66
    };
    private readonly TextBlock _heroTitle = LumenUi.Text(string.Empty, 66, true);
    private readonly RichTextBlock _heroMetadata = new()
    {
        FontFamily = LumenTheme.SansFont, FontSize = 14, LineHeight = 24,
        Foreground = LumenTheme.Brush("ImageInk"), IsTextSelectionEnabled = false,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 0), Opacity = .92
    };
    private readonly TextBlock _heroOverview = LumenUi.Text(string.Empty, 15);
    private readonly TextBlock _heroType = LumenUi.Text(string.Empty, 15);
    private readonly Button _heroDetails = LumenUi.Button("Details", "info", false, true, 56);
    private readonly Button _heroFavorite = LumenUi.IconButton("heart", "Add favorite", 56, true);
    private readonly StackPanel _heroButtons = new() { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 28, 0, 0) };
    private readonly Border _heroThumbnailStrip = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(0, 0, 0, 4)
    };
    private readonly StackPanel _heroThumbs = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 7, 0, 0) };
    private readonly StackPanel _heroSegments = new() { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 14, 0, -10) };
    private readonly DispatcherTimer _heroTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly UISettings _homeAnimationSettings = new();
    private readonly List<HeroThumbnailState> _heroThumbStates = [];
    private readonly List<HomeShelfState> _homeShelves = [];
    private MediaCardViewModel[] _heroItems = [];
    private int _heroIndex;
    private string? _heroId;
    private bool _heroHasFocus;
    private bool _homeAnimationSettingsSubscribed;
    private long _homeAnimationSettingsEpoch;
    private string _homeSignature = string.Empty;

    public Func<bool>? HomeInteractionBlocked { get; set; }

    private sealed record HeroThumbnailState(Button Button, LumenArtwork Artwork, Grid Surface,
        Border Progress, Border Stroke, Button SegmentButton, Border Segment)
    {
        public bool Hovered { get; set; }
        public double VerticalOffset { get; set; }
        public Storyboard? SegmentAnimation { get; set; }
    }

    private void BuildHome()
    {
        AutomationProperties.SetAutomationId(_home, "LumenHome");
        AutomationProperties.SetAutomationId(_homeDeck, "LumenHomePages");
        AutomationProperties.SetAutomationId(_hero, "LumenHomeFeaturedPage");
        AutomationProperties.SetAutomationId(_heroTitle, "LumenHeroTitle");
        AutomationProperties.SetAutomationId(_heroDetails, "LumenHeroDetails");
        AutomationProperties.SetAutomationId(_heroFavorite, "LumenHeroFavorite");
        BuildHomeStage();
        _home.Children.Add(_homeDeck);
        _root.Children.Insert(2, _home);
        _heroLayout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _heroLayout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _heroContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _heroContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetRow(_heroContent, 1);
        _heroLayout.Children.Add(_heroContent);
        _heroViewport.Content = _heroLayout;
        _hero.Children.Add(_heroViewport);
        BuildHomePlayback();

        var eyebrow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var badgeText = Label("Latest added", 12);
        badgeText.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        badgeText.Foreground = LumenTheme.Brush("ImageAccentInk");
        badgeText.CharacterSpacing = 80;
        eyebrow.Children.Add(new Border
        {
            Background = LumenTheme.Brush("ImageAccent"), CornerRadius = new CornerRadius(99),
            Padding = new Thickness(12, 5, 12, 5), Child = badgeText
        });
        _heroType.VerticalAlignment = VerticalAlignment.Center;
        _heroType.Opacity = .82;
        eyebrow.Children.Add(_heroType);
        _heroInfo.Children.Add(eyebrow);
        _heroTitle.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        _heroTitle.CharacterSpacing = 30;
        _heroTitle.Margin = new Thickness(0, 18, 0, 0);
        _heroTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _heroTitle.TextWrapping = TextWrapping.Wrap;
        _heroInfo.Children.Add(_heroTitle);
        _heroInfo.Children.Add(_heroMetadata);
        _heroOverview.Margin = new Thickness(0, 16, 0, 0);
        _heroOverview.MaxWidth = 620;
        _heroOverview.Opacity = .84;
        _heroOverview.TextWrapping = TextWrapping.Wrap;
        _heroOverview.MaxLines = 2;
        _heroOverview.TextTrimming = TextTrimming.CharacterEllipsis;
        _heroInfo.Children.Add(_heroOverview);
        _heroDetails.Padding = new Thickness(20, 0, 26, 0);
        if (_heroDetails.Content is StackPanel detailContent)
        {
            detailContent.Spacing = 10;
            foreach (var label in detailContent.Children.OfType<TextBlock>()) label.FontSize = 18;
        }
        _heroDetails.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 255, 255, 255));
        _heroButtons.Children.Add(_heroDetails);
        _heroButtons.Children.Add(_heroFavorite);
        _heroInfo.Children.Add(_heroButtons);
        _heroInfo.Children.Add(_heroSegments);
        _heroContent.Children.Add(_heroInfo);
        _heroThumbnailStrip.Child = _heroThumbs;
        _heroThumbs.AddHandler(PointerWheelChangedEvent,
            new PointerEventHandler((_, args) => _homeDeck.HandleVerticalWheel(args)), true);
        Grid.SetColumn(_heroThumbnailStrip, 1);
        _heroContent.Children.Add(_heroThumbnailStrip);

        _homeDeck.PageChanged += (_, _) => HomePageChanged();
        _home.PointerMoved += (_, args) => UpdateHomePointerFocus(args.GetCurrentPoint(_home).Position);
        _home.PointerExited += (_, _) => ResetHomePointerFocus();
        _hero.GotFocus += (_, _) => { _heroHasFocus = true; UpdateHeroTimer(); UpdateHomeInteraction(); };
        _hero.LostFocus += (_, _) =>
        {
            var epoch = _renderEpoch;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_renderLoaded || !_renderEnabled || epoch != _renderEpoch) return;
                _heroHasFocus = XamlRoot is not null && IsDescendantOf(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject, _hero);
                UpdateHeroTimer();
                UpdateHomeInteraction();
            });
        };
        _heroTimer.Tick += (_, _) =>
        {
            if (!_homeAnimationSettings.AnimationsEnabled)
            {
                UpdateHomeAnimationPreference();
                return;
            }
            if (HomeInteractionBlocked?.Invoke() == true || XamlRoot?.IsHostVisible != true) return;
            SelectHero((_heroIndex + 1) % Math.Max(1, _heroItems.Length));
        };
        _heroDetails.Click += async (_, _) =>
        {
            if (_heroItems.ElementAtOrDefault(_heroIndex) is { } item) await OpenItemAsync(item);
        };
        _heroFavorite.Click += async (_, _) =>
        {
            if (_heroItems.ElementAtOrDefault(_heroIndex) is not { } item) return;
            _heroFavorite.IsEnabled = false;
            try { await ViewModel.ToggleItemFavoriteAsync(item); }
            finally { _heroFavorite.IsEnabled = true; }
            RenderHeroText();
        };
        RebuildHomePages([], [], []);
    }

    private void RenderHome()
    {
        var latest = ViewModel.HomeRows.Where(row => row.Section == HomeSection.RecentlyAdded)
            .SelectMany(row => row.Items).DistinctBy(item => item.Id).ToArray();
        var resume = ViewModel.HomeRows.Where(row => row.Section == HomeSection.ContinueWatching)
            .SelectMany(row => row.Items).DistinctBy(item => item.Id).ToArray();
        var heroes = latest.Where(item => item.Item.Type is "Movie" or "Series" or "Episode" or "Video")
            .DistinctBy(item => item.Item.BackdropImageTags?.FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag)) ?? item.Id)
            .Take(6).ToArray();
        if (heroes.Length == 0) heroes = resume.Take(6).ToArray();
        if (!_heroItems.SequenceEqual(heroes))
        {
            _heroItems = heroes;
            _heroIndex = Math.Max(0, Array.FindIndex(heroes, item => item.Id == _heroId));
            BuildHeroThumbnails();
            SelectHero(_heroIndex);
        }
        else RenderHeroText();

        var movies = latest.Where(item => item.Item.Type == "Movie").Take(24).ToArray();
        var shows = latest.Where(item => item.Item.Type is "Series" or "Episode" or "Season")
            .DistinctBy(item => item.Item.Type == "Series" ? item.Id : item.Item.SeriesId ?? item.Id).Take(24).ToArray();
        var signature = string.Join("|", new[] { resume, movies, shows }
            .Select(items => string.Join(",", items.Select(item => item.Id))));
        var sameInstances = _homeShelves.Count == 3 && _homeShelves[0].Items.SequenceEqual(resume)
            && _homeShelves[1].Items.SequenceEqual(movies) && _homeShelves[2].Items.SequenceEqual(shows);
        if (signature != _homeSignature || !sameInstances)
        {
            _homeSignature = signature;
            RebuildHomePages(resume, movies, shows);
        }
        foreach (var shelf in _homeShelves) RenderHomeShelfText(shelf);
        UpdateHomeStage();
        UpdateHeroTimer();
    }

    private void BuildHeroThumbnails()
    {
        StopHeroThumbnailAnimations();
        _heroThumbs.Children.Clear();
        _heroSegments.Children.Clear();
        _heroThumbStates.Clear();
        for (var index = 0; index < _heroItems.Length; index++)
        {
            var selectedIndex = index;
            var item = _heroItems[index];
            var artwork = new LumenArtwork { CornerRadius = new CornerRadius(12) };
            artwork.SetCoverFocalPoint(.5, .5);
            artwork.Set(item, ViewModel, ArtworkKind.Landscape, 320);
            var frame = new Grid();
            var surface = new Grid { Width = 104, Height = 58.5, CornerRadius = new CornerRadius(12) };
            frame.Children.Add(surface);
            AttachHomeShadow(frame, 12, 26, 10, .45f);
            surface.Loaded += (_, _) => SetRoundedHomeClip(surface, 12);
            surface.SizeChanged += (_, _) => SetRoundedHomeClip(surface, 12);
            surface.Children.Add(artwork);
            var progress = new Border
            {
                Height = 3, Background = LumenTheme.Brush("ImageAccent"),
                VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false
            };
            surface.Children.Add(progress);
            var stroke = new Border
            {
                BorderThickness = new Thickness(2), BorderBrush = LumenTheme.Brush("ImageAccent"),
                CornerRadius = new CornerRadius(17), Margin = new Thickness(-5), IsHitTestVisible = false
            };
            // The outline extends beyond the thumbnail without changing its layout width.
            frame.Children.Add(stroke);
            var button = new Button
            {
                Content = frame, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(17), Background = new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = true
            };
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
            button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Colors.Transparent);
            var segment = new Border { Width = 8, Height = 4, CornerRadius = new CornerRadius(2), Background = LumenUi.WhiteBrush };
            var segmentButton = new Button
            {
                Content = segment, Height = 24, MinHeight = 24, MinWidth = 0, Padding = new Thickness(0),
                BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = true
            };
            AutomationProperties.SetName(button, item.DisplayTitle);
            AutomationProperties.SetName(segmentButton, item.DisplayTitle);
            AutomationProperties.SetAutomationId(button, "LumenHeroThumbnail" + index);
            AutomationProperties.SetAutomationId(segmentButton, "LumenHeroSegment" + index);
            ToolTipService.SetToolTip(button, item.DisplayTitle);
            ToolTipService.SetToolTip(segmentButton, item.DisplayTitle);
            button.Click += (_, _) => SelectHero(selectedIndex);
            segmentButton.Click += (_, _) => SelectHero(selectedIndex);
            var state = new HeroThumbnailState(button, artwork, surface, progress, stroke, segmentButton, segment);
            button.PointerEntered += (_, _) => { state.Hovered = true; SetHeroOpacity(button, 1, 350); };
            button.PointerExited += (_, _) => { state.Hovered = false; SetHeroOpacity(button, selectedIndex == _heroIndex ? 1 : .5, 350); };
            _heroThumbs.Children.Add(button);
            _heroSegments.Children.Add(segmentButton);
            _heroThumbStates.Add(state);
        }
        UpdateHomeLayout(Math.Max(640, ActualWidth));
    }

    private void SelectHero(int index)
    {
        if (_heroItems.ElementAtOrDefault(index) is not { } item)
        {
            _heroId = null;
            RenderHeroText();
            return;
        }
        _heroIndex = index;
        _heroId = item.Id;
        RenderHeroText();
        for (var thumb = 0; thumb < _heroThumbStates.Count; thumb++)
        {
            var state = _heroThumbStates[thumb];
            var selected = thumb == index;
            SetHeroOpacity(state.Button, selected || state.Hovered ? 1 : .5, 350);
            state.Stroke.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            SetHeroThumbnailOffset(state, selected ? -6 : 0);
            SetHeroSegmentWidth(state, selected ? 28 : 8);
            SetHeroOpacity(state.SegmentButton, selected ? 1 : .45, 400);
            state.Progress.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            if (selected) AnimateHeroProgress(state.Progress, _preferences.HeroRotation && !_heroHasFocus);
        }
        if (_homeDeck.SelectedIndex == 0) UpdateHomeStage();
        _heroTimer.Stop();
        UpdateHeroTimer();
    }

    private void RenderHeroText()
    {
        if (_heroItems.ElementAtOrDefault(_heroIndex) is not { } item)
        {
            _heroTitle.Text = LumenText.Get("Your library is empty");
            _heroType.Text = string.Empty;
            _heroOverview.Text = LumenText.Get("Browse a library to find your next watch.");
            _heroOverview.Visibility = Visibility.Visible;
            _heroMetadata.Visibility = Visibility.Collapsed;
            _heroButtons.Visibility = Visibility.Collapsed;
            _heroPlayback.Visibility = Visibility.Collapsed;
            return;
        }
        _heroTitle.Text = item.DisplayTitle;
        _heroType.Text = LumenText.Get(item.Item.Type is "Series" or "Episode" or "Season" ? "Series" : "Movie");
        RenderHeroMetadata(item);
        _heroOverview.Text = item.Item.Overview ?? string.Empty;
        _heroOverview.Visibility = string.IsNullOrWhiteSpace(_heroOverview.Text) ? Visibility.Collapsed : Visibility.Visible;
        _heroButtons.Visibility = Visibility.Visible;
        _heroDetails.Tag = item;
        _heroFavorite.Content = LumenUi.Icon(item.IsFavorite ? "heart-filled" : "heart", 20, true);
        _heroFavorite.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(item.IsFavorite ? (byte)66 : (byte)26, 255, 255, 255));
        AutomationProperties.SetName(_heroFavorite, LumenText.Get(item.FavoriteLabel));
        ToolTipService.SetToolTip(_heroFavorite, LumenText.Get(item.FavoriteLabel));
        RenderHomePlayback(item);
    }

    private static void AnimateHeroProgress(Border progress, bool rotate)
    {
        if (!progress.IsLoaded) return;
        var visual = ElementCompositionPreview.GetElementVisual(progress);
        visual.StopAnimation("Scale");
        visual.CenterPoint = Vector3.Zero;
        if (!rotate || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            visual.Scale = Vector3.One;
            return;
        }
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector3(0, 1, 1));
        animation.InsertKeyFrame(1, Vector3.One, visual.Compositor.CreateLinearEasingFunction());
        animation.Duration = TimeSpan.FromSeconds(8);
        visual.StartAnimation("Scale", animation);
    }

    private void StopHeroThumbnailAnimations()
    {
        foreach (var state in _heroThumbStates)
        {
            state.SegmentAnimation?.Stop();
            state.SegmentAnimation = null;
        }
    }

    private static void SetHeroOpacity(FrameworkElement element, double opacity, double duration)
    {
        var previous = element.Opacity;
        if (Math.Abs(previous - opacity) < .001) return;
        element.Opacity = opacity;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.Opacity = (float)opacity;
        if (!element.IsLoaded || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, (float)previous);
        animation.InsertKeyFrame(1, (float)opacity,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.25f, .1f), new Vector2(.25f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(duration);
        visual.StartAnimation("Opacity", animation);
    }

    private static void SetHeroThumbnailOffset(HeroThumbnailState state, double offset)
    {
        var previous = state.VerticalOffset;
        state.VerticalOffset = offset;
        if (!state.Button.IsLoaded)
        {
            state.Button.RenderTransform = new TranslateTransform { Y = offset };
            return;
        }
        state.Button.ClearValue(RenderTransformProperty);
        ElementCompositionPreview.SetIsTranslationEnabled(state.Button, true);
        var visual = ElementCompositionPreview.GetElementVisual(state.Button);
        var target = new Vector3(0, (float)offset, 0);
        visual.Properties.InsertVector3("Translation", target);
        visual.Properties.StopAnimation("Translation");
        if (previous == offset || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector3(0, (float)previous, 0));
        animation.InsertKeyFrame(1, target,
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(.25f, .1f), new Vector2(.25f, 1)));
        animation.Duration = TimeSpan.FromMilliseconds(350);
        visual.Properties.StartAnimation("Translation", animation);
    }

    private static void SetHeroSegmentWidth(HeroThumbnailState state, double width)
    {
        if (state.Segment.Width == width) return;
        var previous = state.Segment.ActualWidth > 0 ? state.Segment.ActualWidth : state.Segment.Width;
        state.SegmentAnimation?.Stop();
        state.SegmentAnimation = null;
        state.Segment.Width = width;
        if (!state.Segment.IsLoaded || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled) return;
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true, Duration = TimeSpan.FromMilliseconds(400) };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { Value = previous, KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero) });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            Value = width, KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)),
            KeySpline = new KeySpline { ControlPoint1 = new Point(.25, .1), ControlPoint2 = new Point(.25, 1) }
        });
        Storyboard.SetTarget(animation, state.Segment);
        Storyboard.SetTargetProperty(animation, "Width");
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        state.SegmentAnimation = storyboard;
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(state.SegmentAnimation, storyboard)) return;
            storyboard.Stop();
            state.SegmentAnimation = null;
        };
        storyboard.Begin();
    }

    private void SubscribeHomeAnimationSettings()
    {
        if (_homeAnimationSettingsSubscribed) return;
        _homeAnimationSettingsEpoch++;
        _homeAnimationSettings.AnimationsEnabledChanged += HomeAnimationsChanged;
        _homeAnimationSettingsSubscribed = true;
    }

    private void UnsubscribeHomeAnimationSettings()
    {
        _homeAnimationSettingsEpoch++;
        if (!_homeAnimationSettingsSubscribed) return;
        _homeAnimationSettingsSubscribed = false;
        _homeAnimationSettings.AnimationsEnabledChanged -= HomeAnimationsChanged;
    }

    private void HomeAnimationsChanged(UISettings sender, UISettingsAnimationsEnabledChangedEventArgs args)
    {
        var epoch = _homeAnimationSettingsEpoch;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_renderLoaded || !_homeAnimationSettingsSubscribed || epoch != _homeAnimationSettingsEpoch) return;
            UpdateHomeAnimationPreference();
        });
    }

    private void UpdateHomeAnimationPreference()
    {
        UpdateHeroTimer();
        if (_homeAnimationSettings.AnimationsEnabled) return;
        StopHeroThumbnailAnimations();
        foreach (var state in _heroThumbStates) AnimateHeroProgress(state.Progress, false);
    }

    private void UpdateHeroTimer()
    {
        var shouldRun = IsLoaded && Visibility == Visibility.Visible && _renderedPage == "home"
            && _homeDeck.SelectedIndex == 0 && _preferences.HeroRotation && !_heroHasFocus
            && _homeAnimationSettings.AnimationsEnabled && _heroItems.Length > 1;
        if (shouldRun && !_heroTimer.IsEnabled)
        {
            _heroTimer.Start();
            if (_heroThumbStates.ElementAtOrDefault(_heroIndex) is { } active) AnimateHeroProgress(active.Progress, true);
        }
        else if (!shouldRun)
        {
            _heroTimer.Stop();
            if (_heroThumbStates.ElementAtOrDefault(_heroIndex) is { } active) AnimateHeroProgress(active.Progress, false);
        }
    }
}
