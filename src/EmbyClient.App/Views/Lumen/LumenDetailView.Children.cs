using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Globalization;
using Windows.UI;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private void RenderChildren(MediaCardViewModel detail)
    {
        var previousOffset = _renderedSeasonId == _library.SelectedSeason?.Id ? _episodeScroll?.HorizontalOffset ?? 0 : 0;
        _childrenSection.Children.Clear();
        _childrenSection.Visibility = detail.IsFolder || detail.Item.Type == "Episode" && _library.NextEpisode is not null
            ? Visibility.Visible : Visibility.Collapsed;
        _episodeScroll = null;
        if (_childrenSection.Visibility == Visibility.Collapsed) return;
        if (detail.Item.Type == "Episode" && _library.NextEpisode is { } next)
        {
            _childrenSection.Children.Add(SectionHeading("Next episode"));
            _childrenSection.Children.Add(CreateEpisodeCard(next));
            return;
        }
        var episodes = _library.DetailItemsAreEpisodes;
        if (episodes)
        {
            var episodeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Padding = new Thickness(0, 0, 0, 4),
                Opacity = _library.IsSeasonSwitchPending ? .45 : 1 };
            foreach (var episode in _library.Items) episodeRow.Children.Add(CreateEpisodeCard(episode));
            _episodeScroll = HorizontalScroll(episodeRow);
            var header = new Grid { ColumnSpacing = 14 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var seasons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (_library.Seasons.Count > 0)
                foreach (var season in _library.Seasons) seasons.Children.Add(CreateSeasonTab(season));
            else seasons.Children.Add(SectionHeading("Episodes"));
            header.Children.Add(HorizontalScroll(seasons));
            var info = LumenUi.Text(_library.IsSeasonSwitchPending ? LumenText.Get("Loading episodes...")
                : Format("{0} episodes", _library.Items.Count) + " \u00b7 " + Format("{0} watched", _library.Items.Count(card => card.IsPlayed)), 13);
            info.Foreground = LumenTheme.Brush("Sub");
            info.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(info, 1);
            header.Children.Add(info);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var previous = LumenUi.IconButton("chevron-left", LumenText.Get("Previous episodes"), 32);
            var following = LumenUi.IconButton("chevron-right", LumenText.Get("Next episodes"), 32);
            var scroller = _episodeScroll;
            previous.Click += (_, _) => scroller.ChangeView(Math.Max(0, scroller.HorizontalOffset - Math.Max(316, scroller.ViewportWidth)), null, null);
            following.Click += (_, _) => scroller.ChangeView(Math.Min(scroller.ScrollableWidth, scroller.HorizontalOffset + Math.Max(316, scroller.ViewportWidth)), null, null);
            void UpdatePaging()
            {
                previous.IsEnabled = _library.CanUseSeasonActions && scroller.HorizontalOffset > 1;
                following.IsEnabled = _library.CanUseSeasonActions && scroller.HorizontalOffset + 1 < scroller.ScrollableWidth;
            }
            scroller.ViewChanged += (_, _) => UpdatePaging();
            scroller.SizeChanged += (_, _) => UpdatePaging();
            scroller.Loaded += (_, _) => { scroller.ChangeView(previousOffset, null, null, true); UpdatePaging(); };
            previous.IsEnabled = false;
            buttons.Children.Add(previous);
            buttons.Children.Add(following);
            Grid.SetColumn(buttons, 2);
            header.Children.Add(buttons);
            _childrenSection.Children.Add(header);
            _childrenSection.Children.Add(scroller);
        }
        else
        {
            _childrenSection.Children.Add(SectionHeading("Contents"));
            var grid = new FlowPanel { Spacing = 20, RowSpacing = 30 };
            foreach (var item in _library.Items) grid.Children.Add(CreateMediaCard(item));
            _childrenSection.Children.Add(grid);
            if (_library.HasMore)
            {
                var more = LumenUi.Button(LumenText.Get("Load more"), "chevron-down");
                more.IsEnabled = _library.CanLoadMore;
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Click += async (_, _) => await _library.LoadMoreAsync();
                _childrenSection.Children.Add(more);
            }
        }
        if (_library.IsBusy)
        {
            var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            loading.Children.Add(new ProgressRing { IsActive = true, Width = 24, Height = 24, Foreground = LumenTheme.Brush("Accent") });
            var text = LumenUi.Text(LumenText.Get(episodes ? "Loading episodes..." : "Loading..."), 13);
            text.Foreground = LumenTheme.Brush("Muted");
            text.VerticalAlignment = VerticalAlignment.Center;
            loading.Children.Add(text);
            _childrenSection.Children.Add(loading);
        }
        else if (_library.Items.Count == 0 && !_library.HasError && _library.LoadOutcome == PageLoadOutcome.Succeeded)
        {
            var empty = LumenUi.Text(LumenText.Get(episodes ? "No episodes are available in this season." : "No items are available in this collection."), 14);
            empty.Foreground = LumenTheme.Brush("Sub");
            _childrenSection.Children.Add(empty);
        }
    }

    private Button CreateSeasonTab(MediaCardViewModel season)
    {
        var selected = season.Id == _library.SelectedSeason?.Id;
        var label = season.Item.IndexNumber == 0 ? LumenText.Get("Specials")
            : season.Item.IndexNumber is { } number ? Format("Season {0}", number) : season.Title;
        var button = new Button
        {
            Height = 40, Padding = new Thickness(14, 0, 14, 0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsEnabled = _library.CanSelectSeason && !_actionBusy,
            HorizontalContentAlignment = HorizontalAlignment.Center, UseSystemFocusVisuals = true
        };
        button.Resources["ButtonBackgroundPointerOver"] = LumenTheme.Brush("Control");
        var body = new Grid { Height = 40 };
        var text = LumenUi.Text(label, 16);
        text.Foreground = LumenTheme.Brush(selected ? "Ink" : "Muted");
        text.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        text.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(text);
        if (selected) body.Children.Add(new Border
        {
            Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = LumenTheme.Brush("Accent"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom
        });
        button.Content = body;
        AutomationProperties.SetName(button, label + (selected ? ", " + LumenText.Get("Selected") : string.Empty));
        button.Click += async (_, _) => await _library.SelectSeasonAsync(season);
        return button;
    }

    private FrameworkElement CreateEpisodeCard(MediaCardViewModel episode)
    {
        var card = new StackPanel { Width = 300 };
        var image = new Grid { Height = 169 };
        var artwork = new LumenArtwork();
        artwork.Set(episode, _library, ArtworkKind.Landscape, 640);
        image.Children.Add(artwork);
        var open = new Button
        {
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(12), UseSystemFocusVisuals = true, IsEnabled = _library.CanUseSeasonActions && !_actionBusy
        };
        open.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        open.Click += (_, _) => RequestEpisodePlayback(episode);
        AutomationProperties.SetName(open, LumenText.Get("Play") + ": " + EpisodeCode(episode.Item) + " " + episode.Title);
        ToolTipService.SetToolTip(open, episode.SceneIdentity);
        image.Children.Add(open);
        image.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top, Child = ImageText(EpisodeCode(episode.Item), 11, weight: true), IsHitTestVisible = false
        });
        if (episode.Item.RunTimeTicks is > 0) image.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(10, 0, 10, 12), HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom, Child = ImageText(Runtime(episode.Item.RunTimeTicks.Value), 11), IsHitTestVisible = false
        });
        if (episode.CanResume)
        {
            var status = LumenUi.Text(LumenText.Get("Currently watching"), 11);
            status.Foreground = LumenTheme.Brush("ImageAccentInk");
            status.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            image.Children.Add(new Border
            {
                Background = LumenTheme.Brush("ImageAccent"), CornerRadius = new CornerRadius(11), Padding = new Thickness(8, 4, 8, 4),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
                Child = status, IsHitTestVisible = false
            });
            var progress = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Background = ImageBrush(77), IsHitTestVisible = false };
            progress.Children.Add(new Border { Width = 300 * episode.Progress / 100, HorizontalAlignment = HorizontalAlignment.Left, Background = LumenTheme.Brush("ImageAccent") });
            image.Children.Add(progress);
        }
        else if (_showWatchedMarks && episode.IsPlayed) image.Children.Add(new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
            Child = LumenUi.Icon("check-filled", 12, true), IsHitTestVisible = false
        });
        var shade = new Grid { Background = new SolidColorBrush(Color.FromArgb(66, 0, 0, 0)), CornerRadius = new CornerRadius(12), Visibility = Visibility.Collapsed };
        var play = LumenUi.IconButton("play", LumenText.Get("Play"), 48, true);
        play.Background = LumenUi.WhiteBrush;
        play.Resources["ButtonBackgroundPointerOver"] = LumenUi.WhiteBrush;
        play.Resources["ButtonBackgroundPressed"] = LumenUi.WhiteBrush;
        play.Content = new Image { Width = 20, Height = 20, Source = new SvgImageSource(new Uri("ms-appx:///Assets/Lumen/Icons/d/play_24_filled.svg")), IsHitTestVisible = false };
        play.HorizontalAlignment = HorizontalAlignment.Center;
        play.VerticalAlignment = VerticalAlignment.Center;
        play.Click += (_, _) => RequestEpisodePlayback(episode);
        shade.Children.Add(play);
        image.Children.Add(shade);
        var ring = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(episode.CanResume ? 2 : 1),
            BorderBrush = LumenTheme.Brush(episode.CanResume ? "ImageAccent" : "Line"), IsHitTestVisible = false };
        image.Children.Add(ring);
        var hovered = false;
        var focused = false;
        void UpdateOverlay() => shade.Visibility = (hovered || focused) && episode.CanPlay && _library.IsPlaybackAllowed
            ? Visibility.Visible : Visibility.Collapsed;
        image.PointerEntered += (_, _) => { hovered = true; UpdateOverlay(); };
        image.PointerExited += (_, _) => { hovered = false; UpdateOverlay(); };
        image.GotFocus += (_, _) => { focused = true; UpdateOverlay(); };
        image.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() => { focused = HasFocusWithin(image); UpdateOverlay(); });
        image.Loaded += (_, _) => ApplyRoundedClip(image, 12);
        image.SizeChanged += (_, _) => ApplyRoundedClip(image, 12);
        card.Children.Add(image);
        var title = LumenUi.Text((episode.Item.IndexNumber is { } number ? number.ToString(CultureInfo.InvariantCulture) + ". " : string.Empty) + episode.Title, 15);
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.MaxLines = 1;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var titleButton = new Button { Content = title, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0), Padding = new Thickness(0), Margin = new Thickness(0, 12, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Left, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 0, Height = 22,
            IsEnabled = _library.CanUseSeasonActions && !_actionBusy };
        titleButton.Click += (_, _) => ItemRequested?.Invoke(this, episode);
        AutomationProperties.SetName(titleButton, LumenText.Get("Details") + ": " + episode.SceneIdentity);
        card.Children.Add(titleButton);
        var date = LumenUi.Text(string.Join(" \u00b7 ", new[] { episode.Item.PremiereDate?.ToString("yyyy\u5e74M\u6708d\u65e5", CultureInfo.InvariantCulture),
            episode.Item.RunTimeTicks is > 0 ? Runtime(episode.Item.RunTimeTicks.Value) : null }.Where(value => !string.IsNullOrWhiteSpace(value))), 12);
        date.Foreground = LumenTheme.Brush("Muted");
        date.Margin = new Thickness(0, 3, 0, 0);
        card.Children.Add(date);
        var overview = LumenUi.Text(episode.Item.Overview ?? string.Empty, 13);
        overview.Foreground = LumenTheme.Brush("Sub");
        overview.TextWrapping = TextWrapping.Wrap;
        overview.TextTrimming = TextTrimming.CharacterEllipsis;
        overview.LineHeight = 20.15;
        overview.MaxLines = 2;
        overview.Margin = new Thickness(0, 6, 0, 0);
        card.Children.Add(overview);
        return card;
    }

    private void RequestEpisodePlayback(MediaCardViewModel episode)
    {
        if (episode.CanPlay && _library.IsPlaybackAllowed && !_actionBusy && _library.CanUseSeasonActions)
            PlayRequested?.Invoke(this, new LumenPlayRequestEventArgs(episode.Item, episode.CanResume ? episode.ResumeTicks : 0));
        else ItemRequested?.Invoke(this, episode);
    }

    private LumenMediaCard CreateMediaCard(MediaCardViewModel item)
    {
        var card = new LumenMediaCard(item, _library);
        card.SetCardWidth(160);
        card.SetShowWatched(_showWatchedMarks);
        card.OpenRequested += (_, selected) => ItemRequested?.Invoke(this, selected);
        card.PlayRequested += (_, request) => { if (_library.IsPlaybackAllowed) PlayRequested?.Invoke(this, request); };
        card.FavoriteRequested += async (_, selected) => await _library.ToggleItemFavoriteAsync(selected);
        card.WatchedRequested += async (_, selected) => await _library.ToggleItemPlayedAsync(selected);
        return card;
    }

    private static ScrollViewer HorizontalScroll(UIElement content) => new()
    {
        Content = content, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, IsTabStop = false
    };

    private static TextBlock SectionHeading(string key)
    {
        var text = LumenUi.Text(LumenText.Get(key), 22, true);
        text.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        AutomationProperties.SetHeadingLevel(text, AutomationHeadingLevel.Level2);
        return text;
    }
}
