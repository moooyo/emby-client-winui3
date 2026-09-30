using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly StackPanel _home = new();
    private readonly Grid _hero = new() { Height = 660 };
    private readonly LumenArtwork[] _heroArtwork = [new() { CornerRadius = new CornerRadius(0) }, new() { CornerRadius = new CornerRadius(0), Opacity = 0 }];
    private readonly Border _heroBottom = new();
    private readonly StackPanel _heroInfo = new() { Spacing = 12, Width = 580, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(56, 0, 0, 112) };
    private readonly TextBlock _heroTitle = LumenUi.Text(string.Empty, 66, true);
    private readonly TextBlock _heroOriginal = LumenUi.Text(string.Empty, 12);
    private readonly RichTextBlock _heroMetadata = new()
    {
        FontFamily = LumenTheme.SansFont, FontSize = 13, LineHeight = 22,
        Foreground = LumenTheme.Brush("ImageInk"), IsTextSelectionEnabled = false,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly TextBlock _heroOverview = LumenUi.Text(string.Empty, 15);
    private readonly TextBlock _heroType = LumenUi.Text(string.Empty, 12);
    private readonly Button _heroPlay = LumenUi.Button("Play", "play", true, true);
    private readonly Button _heroDetails = LumenUi.Button("Details", "info", false, true);
    private readonly Button _heroFavorite = LumenUi.IconButton("heart", "Add favorite", 44, true);
    private readonly StackPanel _heroButtons = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly ScrollViewer _heroThumbScroll = new() { HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 56, 112) };
    private readonly StackPanel _heroThumbs = new() { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _homeBody = new() { Spacing = 36, Margin = new Thickness(0, -44, 0, 64) };
    private readonly DispatcherTimer _heroTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly List<(Button Button, Border Progress, Border Stroke)> _heroThumbStates = [];
    private readonly List<FrameworkElement> _homeHeaders = [];
    private readonly List<ScrollViewer> _homeScrollers = [];
    private Grid? _mediaTiles;
    private MediaCardViewModel[] _heroItems = [];
    private int _heroIndex;
    private int _heroBuffer;
    private int _heroArtworkVersion;
    private readonly Dictionary<LumenArtwork, long> _heroSourceSubscriptions = [];
    private string? _heroId;
    private string _homeSignature = string.Empty;

    private void BuildHome()
    {
        _hero.SizeChanged += (_, _) => _hero.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, _hero.ActualWidth, _hero.ActualHeight) };
        _heroTitle.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        _heroTitle.LineHeight = 71.28;
        _heroTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        AutomationProperties.SetAutomationId(_heroTitle, "LumenHeroTitle");
        AutomationProperties.SetAutomationId(_heroPlay, "LumenHeroPlay");
        AutomationProperties.SetAutomationId(_heroDetails, "LumenHeroDetails");
        AutomationProperties.SetAutomationId(_heroFavorite, "LumenHeroFavorite");
        foreach (var artwork in _heroArtwork)
        {
            artwork.SetCoverFocalPoint(.5, .5);
            _hero.Children.Add(artwork);
        }
        _hero.Children.Add(new Border { Background = LumenTheme.ImageFade(horizontal: true) });
        _hero.Children.Add(new Border { Background = LumenTheme.ImageFade(), Height = 180, VerticalAlignment = VerticalAlignment.Top });
        _heroBottom.Background = LumenTheme.ImageFade(pageBottom: true);
        _hero.Children.Add(_heroBottom);
        var eyebrow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var badge = new Border { Background = LumenTheme.Brush("ImageAccent"), CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 4, 12, 4) };
        var badgeText = Label("Latest added", 11);
        badgeText.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        badgeText.Foreground = LumenTheme.Brush("ImageAccentInk");
        badge.Child = badgeText;
        eyebrow.Children.Add(badge);
        _heroType.VerticalAlignment = VerticalAlignment.Center;
        eyebrow.Children.Add(_heroType);
        _heroInfo.Children.Add(eyebrow);
        _heroTitle.TextWrapping = TextWrapping.Wrap;
        _heroTitle.MaxLines = 2;
        _heroTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _heroInfo.Children.Add(_heroTitle);
        _heroOriginal.Opacity = 0.7;
        _heroOriginal.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _heroInfo.Children.Add(_heroOriginal);
        _heroInfo.Children.Add(_heroMetadata);
        _heroOverview.TextWrapping = TextWrapping.Wrap;
        _heroOverview.MaxLines = 3;
        _heroOverview.TextTrimming = TextTrimming.CharacterEllipsis;
        _heroOverview.LineHeight = 25.5;
        _heroInfo.Children.Add(_heroOverview);
        _heroButtons.Margin = new Thickness(0, 8, 0, 0);
        _heroButtons.Children.Add(_heroPlay);
        _heroButtons.Children.Add(_heroDetails);
        _heroButtons.Children.Add(_heroFavorite);
        _heroInfo.Children.Add(_heroButtons);
        _hero.Children.Add(_heroInfo);
        _heroThumbScroll.Content = _heroThumbs;
        _hero.Children.Add(_heroThumbScroll);
        _home.Children.Add(_hero);
        _home.Children.Add(_homeBody);
        _heroTimer.Tick += (_, _) => SelectHero((_heroIndex + 1) % Math.Max(1, _heroItems.Length));
        _heroPlay.Click += (_, _) =>
        {
            if (_heroItems.ElementAtOrDefault(_heroIndex) is not { } item) return;
            ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0));
        };
        _heroDetails.Click += async (_, _) => { if (_heroItems.ElementAtOrDefault(_heroIndex) is { } item) await OpenItemAsync(item); };
        _heroFavorite.Click += async (_, _) =>
        {
            if (_heroItems.ElementAtOrDefault(_heroIndex) is not { } item) return;
            await ViewModel.ToggleItemFavoriteAsync(item);
            RenderHeroText();
        };
    }

    private void RenderHome()
    {
        var latest = ViewModel.HomeRows.Where(row => row.Section == HomeSection.RecentlyAdded)
            .SelectMany(row => row.Items).DistinctBy(item => item.Id).ToArray();
        var heroes = latest.Where(item => item.Item.Type is "Movie" or "Series" or "Episode" or "Video")
            .DistinctBy(item => item.Item.BackdropImageTags?.FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag)) ?? item.Id)
            .Take(6).ToArray();
        if (!_heroItems.Select(item => item.Id).SequenceEqual(heroes.Select(item => item.Id)))
        {
            _heroItems = heroes;
            _heroIndex = Math.Max(0, Array.FindIndex(heroes, item => item.Id == _heroId));
            BuildHeroThumbnails();
            SelectHero(_heroIndex);
        }
        else RenderHeroText();
        _hero.Visibility = heroes.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _homeBody.Margin = new Thickness(0, heroes.Length > 0 ? (LumenTheme.IsDark ? -44 : 8) : 104, 0, 64);
        var signature = string.Join("|", ViewModel.HomeRows.Select(row => row.Title + ":" + string.Join(",", row.Items.Select(item => item.Id))))
            + ";" + string.Join("|", ViewModel.Libraries.Select(item => item.Id + ":" + item.Title + ":" + item.Item.ChildCount + ":" + item.Item.RecursiveItemCount));
        if (signature == _homeSignature) return;
        _homeSignature = signature;
        _homeBody.Children.Clear();
        _homeHeaders.Clear();
        _homeScrollers.Clear();
        var resume = ViewModel.HomeRows.FirstOrDefault(row => row.Section == HomeSection.ContinueWatching);
        if (resume?.Items.Count > 0) _homeBody.Children.Add(HomeShelf("Continue watching", resume.Items, true, async () => await ViewModel.ShowShelfAsync(resume)));
        var next = ViewModel.HomeRows.FirstOrDefault(row => row.Section == HomeSection.NextUp);
        if (ViewModel.Libraries.Count > 0) _homeBody.Children.Add(BuildMediaTiles());
        var movies = latest.Where(item => item.Item.Type == "Movie").ToArray();
        var shows = latest.Where(item => item.Item.Type == "Series").ToArray();
        if (movies.Length > 0) _homeBody.Children.Add(HomeShelf("Latest movies", movies, false, async () => await NavigateAsync("movies")));
        if (shows.Length > 0) _homeBody.Children.Add(HomeShelf("Latest series", shows, false, async () => await NavigateAsync("series")));
        if (next?.Items.Count > 0) _homeBody.Children.Add(HomeShelf("Next up", next.Items, true, async () => await ViewModel.ShowShelfAsync(next)));
        if (movies.Length == 0 && shows.Length == 0 && latest.Length > 0)
            _homeBody.Children.Add(HomeShelf("Recently added", latest, false, async () => await ViewModel.ShowHomeSectionAsync(HomeSection.RecentlyAdded)));
    }

    private void BuildHeroThumbnails()
    {
        _heroThumbs.Children.Clear();
        _heroThumbStates.Clear();
        for (var index = 0; index < _heroItems.Length; index++)
        {
            var selectedIndex = index;
            var item = _heroItems[index];
            var artwork = new LumenArtwork { CornerRadius = new CornerRadius(10), Width = 116, Height = 65 };
            artwork.SetCoverFocalPoint(.5, .5);
            artwork.Set(item, ViewModel, ArtworkKind.Backdrop, 232);
            var surface = new Grid { Width = 116, Height = 65 };
            surface.Children.Add(artwork);
            var progress = new Border { Height = 3, Background = LumenTheme.Brush("ImageAccent"), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(3, 0, 3, 2), IsHitTestVisible = false };
            surface.Children.Add(progress);
            var stroke = new Border { BorderThickness = new Thickness(2), BorderBrush = LumenTheme.Brush("ImageAccent"), CornerRadius = new CornerRadius(10), IsHitTestVisible = false };
            surface.Children.Add(stroke);
            var button = new Button { Content = surface, Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(10), Width = 116, Height = 65, Background = new SolidColorBrush(Colors.Transparent) };
            AutomationProperties.SetName(button, item.Title);
            AutomationProperties.SetAutomationId(button, "LumenHeroThumbnail" + index);
            ToolTipService.SetToolTip(button, item.Title);
            button.Click += (_, _) => { SelectHero(selectedIndex); _heroTimer.Stop(); UpdateHeroTimer(); };
            _heroThumbs.Children.Add(button);
            _heroThumbStates.Add((button, progress, stroke));
        }
    }

    private void SelectHero(int index)
    {
        if (_heroItems.ElementAtOrDefault(index) is not { } item) return;
        _heroIndex = index;
        if (_heroId != item.Id)
        {
            _heroId = item.Id;
            var old = _heroArtwork[_heroBuffer];
            var incomingBuffer = 1 - _heroBuffer;
            var next = _heroArtwork[incomingBuffer];
            var version = ++_heroArtworkVersion;
            if (_heroSourceSubscriptions.Remove(next, out var oldSubscription))
                next.Image.UnregisterPropertyChangedCallback(Image.SourceProperty, oldSubscription);
            next.Opacity = 1;
            var nextVisual = ElementCompositionPreview.GetElementVisual(next);
            nextVisual.StopAnimation("Opacity");
            nextVisual.Opacity = 0;
            var subscription = next.Image.RegisterPropertyChangedCallback(Image.SourceProperty, (_, _) =>
            {
                if (next.Image.Source is null || version != _heroArtworkVersion || _heroId != item.Id) return;
                if (_heroSourceSubscriptions.Remove(next, out var completed))
                    next.Image.UnregisterPropertyChangedCallback(Image.SourceProperty, completed);
                _heroBuffer = incomingBuffer;
                CrossFadeHero(old, next);
            });
            _heroSourceSubscriptions[next] = subscription;
            next.Set(item, ViewModel, ArtworkKind.Backdrop, 1920);
        }
        RenderHeroText();
        for (var thumb = 0; thumb < _heroThumbStates.Count; thumb++)
        {
            var (button, progress, stroke) = _heroThumbStates[thumb];
            button.Opacity = thumb == index ? 1 : 0.62;
            stroke.Visibility = thumb == index ? Visibility.Visible : Visibility.Collapsed;
            button.RenderTransform = new TranslateTransform { Y = thumb == index ? -8 : 0 };
            progress.Visibility = thumb == index && _preferences.HeroRotation ? Visibility.Visible : Visibility.Collapsed;
            if (thumb == index) AnimateHeroProgress(progress);
        }
    }

    private void RenderHeroText()
    {
        if (_heroItems.ElementAtOrDefault(_heroIndex) is not { } item) return;
        _heroTitle.Text = item.DisplayTitle;
        _heroOriginal.Text = item.Item.OriginalTitle?.ToUpperInvariant() ?? string.Empty;
        _heroOriginal.Visibility = string.IsNullOrWhiteSpace(_heroOriginal.Text) ? Visibility.Collapsed : Visibility.Visible;
        _heroType.Text = LumenText.Get(item.Item.Type == "Series" ? "Series" : item.Item.Type == "Episode" ? "Episode" : "Movie");
        RenderHeroMetadata(item);
        _heroOverview.Text = string.IsNullOrWhiteSpace(item.Item.Overview) ? string.Empty : item.Item.Overview;
        _heroOverview.Visibility = _heroOverview.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var playLabel = item.CanResume ? LumenText.Get("Resume") : LumenText.Get("Play");
        if (item.CanResume && item.Item.Type == "Episode"
            && item.Item.ParentIndexNumber is { } season && item.Item.IndexNumber is { } episode)
            playLabel += $" S{season}:E{episode}";
        _heroPlay.Content = ActionContent(playLabel, "play", true);
        AutomationProperties.SetName(_heroPlay, playLabel);
        _heroPlay.IsEnabled = ViewModel.IsPlaybackAllowed;
        _heroDetails.Tag = item;
        _heroFavorite.Content = LumenUi.Icon(item.IsFavorite ? "heart-filled" : "heart", 20, true);
        AutomationProperties.SetName(_heroFavorite, LumenText.Get(item.FavoriteLabel));
        ToolTipService.SetToolTip(_heroFavorite, LumenText.Get(item.FavoriteLabel));
    }

    private static void CrossFadeHero(LumenArtwork old, LumenArtwork next)
    {
        var visual = ElementCompositionPreview.GetElementVisual(next);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Opacity = 1;
            visual.Scale = Vector3.One;
            var previousVisual = ElementCompositionPreview.GetElementVisual(old);
            previousVisual.StopAnimation("Opacity");
            previousVisual.Opacity = 0;
            return;
        }
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1);
        fade.Duration = TimeSpan.FromMilliseconds(900);
        visual.StartAnimation("Opacity", fade);
        var previous = ElementCompositionPreview.GetElementVisual(old);
        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(0, 1);
        fadeOut.InsertKeyFrame(1, 0);
        fadeOut.Duration = TimeSpan.FromMilliseconds(900);
        previous.StartAnimation("Opacity", fadeOut);
        visual.CenterPoint = new Vector3((float)next.ActualWidth / 2, (float)next.ActualHeight / 2, 0);
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0, new Vector3(1.06f, 1.06f, 1));
        scale.InsertKeyFrame(1, Vector3.One);
        scale.Duration = TimeSpan.FromSeconds(9);
        visual.StartAnimation("Scale", scale);
    }

    private static void AnimateHeroProgress(Border progress)
    {
        var visual = ElementCompositionPreview.GetElementVisual(progress);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
            return;
        }
        visual.CenterPoint = Vector3.Zero;
        var animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, new Vector3(0, 1, 1));
        animation.InsertKeyFrame(1, Vector3.One);
        animation.Duration = TimeSpan.FromSeconds(7);
        visual.StartAnimation("Scale", animation);
    }

    private void UpdateHeroTimer()
    {
        var shouldRun = IsLoaded && Visibility == Visibility.Visible && _renderedPage == "home" && _preferences.HeroRotation && _heroItems.Length > 1;
        if (shouldRun && !_heroTimer.IsEnabled) _heroTimer.Start();
        else if (!shouldRun) _heroTimer.Stop();
    }
}
