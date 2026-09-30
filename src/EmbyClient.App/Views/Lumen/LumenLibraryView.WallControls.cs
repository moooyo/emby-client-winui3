using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private void BuildSortFlyout()
    {
        var options = new StackPanel { Spacing = 3, Width = 216 };
        foreach (var entry in new[]
        {
            (Label: "Name A-Z", Sort: "SortName", Descending: false),
            (Label: "Name Z-A", Sort: "SortName", Descending: true),
            (Label: "Recently added", Sort: "DateCreated", Descending: true),
            (Label: "Release year", Sort: "ProductionYear", Descending: true),
            (Label: "Rating", Sort: "CommunityRating", Descending: true)
        })
        {
            var button = LumenUi.Button(entry.Label, "check", height: 38);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(10);
            button.Click += async (_, _) =>
            {
                _sortFlyout.Hide();
                await ViewModel.SetBrowseOptionsAsync(entry.Sort, entry.Descending, ViewModel.BrowseWatchFilter);
            };
            options.Children.Add(button);
            _sortButtons.Add((button, entry.Sort, entry.Descending));
        }
        _sortFlyout.Content = options;
        _sortFlyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        _sortFlyout.FlyoutPresenterStyle = PopupStyle();
    }

    private void BuildFilterFlyout()
    {
        var content = new StackPanel { Spacing = 4, Width = 220 };
        var heading = Label("Watch status", 13);
        heading.Margin = new Thickness(12, 6, 12, 10);
        content.Children.Add(heading);
        foreach (var entry in new[]
        {
            (Label: "All", Filter: WatchStatusFilter.All),
            (Label: "Unwatched", Filter: WatchStatusFilter.Unplayed),
            (Label: "Watched", Filter: WatchStatusFilter.Played)
        })
        {
            var button = LumenUi.Button(entry.Label, "check", height: 38);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.BorderThickness = new Thickness(0);
            button.CornerRadius = new CornerRadius(10);
            button.Click += async (_, _) =>
            {
                _filterFlyout.Hide();
                await ViewModel.SetLumenBrowseFiltersAsync(ViewModel.BrowseGenre, entry.Filter, ViewModel.BrowseNameStartsWith);
            };
            content.Children.Add(button);
            _watchButtons.Add((button, entry.Filter));
        }
        content.Children.Add(LumenUi.Divider());
        var clear = LumenUi.Button("Clear filters", "x", height: 38);
        clear.BorderThickness = new Thickness(0);
        clear.Click += async (_, _) => { _filterFlyout.Hide(); await ViewModel.SetLumenBrowseFiltersAsync(null, WatchStatusFilter.All); };
        content.Children.Add(clear);
        _filterFlyout.Content = content;
        _filterFlyout.Placement = FlyoutPlacementMode.BottomEdgeAlignedRight;
        _filterFlyout.FlyoutPresenterStyle = PopupStyle();
    }

    private static Style PopupStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.BackgroundProperty, LumenTheme.Brush("Pop")));
        style.Setters.Add(new Setter(Control.ForegroundProperty, LumenTheme.Brush("Ink")));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, LumenTheme.Brush("LineStrong")));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(16)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        return style;
    }

    private void CloseBrowseFlyouts()
    {
        _sortFlyout.Hide();
        _filterFlyout.Hide();
    }

    private async Task LoadWallGenresAsync(int revision, int generation)
    {
        await ViewModel.LoadLumenGenresAsync();
        if (generation == _sessionGeneration && revision == ViewModel.NavigationRevision) QueueRender();
    }

    private void UpdateAlphabet(MediaCardViewModel? hovered = null)
    {
        var letters = ViewModel.Items.Select(item => AlphabetKey(item.Item.SortName ?? item.Title)).ToHashSet(StringComparer.Ordinal);
        var selected = hovered is not null ? AlphabetKey(hovered.Item.SortName ?? hovered.Title) : ViewModel.BrowseNameStartsWith;
        foreach (var button in _alphabet.Children.OfType<Button>())
        {
            if (button.Tag is not string letter) continue;
            var active = selected == letter;
            button.Background = active ? LumenTheme.Brush("Accent") : new SolidColorBrush(Colors.Transparent);
            button.Opacity = active || letters.Contains(letter) ? 1 : 0.35;
            if (button.Content is TextBlock text) text.Foreground = active ? LumenTheme.Brush("AccentInk") : LumenTheme.Brush("Muted");
        }
    }

    private static string AlphabetKey(string title)
    {
        var first = title.TrimStart().FirstOrDefault();
        return first is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? char.ToUpperInvariant(first).ToString() : "#";
    }

    private void UpdateWallLayout(double width)
    {
        _wall.Margin = new Thickness(_pageMargin, 104, _pageMargin + 32, 64);
        var compact = width < 1120;
        _wallHeader.RowSpacing = compact ? 16 : 0;
        Grid.SetRow(_wallTools, compact ? 1 : 0);
        Grid.SetColumn(_wallTools, compact ? 0 : 1);
        Grid.SetColumnSpan(_wallTools, compact ? 2 : 1);
        _wallTools.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        var usable = Math.Max(1, width - _pageMargin * 2 - 32);
        var columns = Math.Clamp(Math.Min(_columns, (int)(usable / 115)), 2, 10);
        _wallCardWidth = Math.Max(80, Math.Floor((usable - 20 * (columns - 1)) / columns * 4) / 4);
        _wallRepeater.Width = usable;
        _wallRepeater.HorizontalAlignment = HorizontalAlignment.Left;
        _wallGridLayout.MaximumRowsOrColumns = columns;
        _wallGridLayout.MinItemWidth = _wallCardWidth;
        _wallGridLayout.MinItemHeight = _wallCardWidth * 1.5 + 48;
        if (_renderedPage == "wall") foreach (var card in _realizedCards) card.SetCardWidth(_wallCardWidth);
        ToolTipService.SetToolTip(_posterSize, LumenText.Get("{0} columns", columns));
    }

    private void ApplyWallTheme()
    {
        _wallTitle.Foreground = LumenTheme.Brush("Ink");
        _wallCount.Foreground = LumenTheme.Brush("Muted");
        _posterSize.Foreground = LumenTheme.Brush("Accent");
        _posterSize.Background = LumenTheme.Brush("LineStrong");
        // Native Slider thumb states use separate resources from the filled track's Foreground.
        foreach (var state in new[] { string.Empty, "PointerOver", "Pressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            _posterSize.Resources["SliderThumbBackground" + state] = LumenTheme.Brush(disabled ? "Muted" : "Accent");
            _posterSize.Resources["SliderTrackValueFill" + state] = LumenTheme.Brush(disabled ? "Muted" : "Accent");
            _posterSize.Resources["SliderTrackFill" + state] = LumenTheme.Brush(disabled ? "Line" : "LineStrong");
        }
        _posterSize.Resources["SliderThumbBorderBrush"] = LumenTheme.Brush("LineStrong");
        _posterSize.Resources["SliderOuterThumbBackground"] = LumenTheme.Brush(LumenTheme.IsDark ? "ControlStrong" : "Control");
        _updatingWallControls = true;
        _posterSize.Value = 16 - _columns;
        _updatingWallControls = false;
        _genreSignature = string.Empty;
        _sortFlyout.FlyoutPresenterStyle = PopupStyle();
        _filterFlyout.FlyoutPresenterStyle = PopupStyle();
    }

    private sealed partial class LibraryCardFactory(LumenLibraryView owner, Func<double> width, bool list = false, Func<string?>? query = null) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            if (args.Data is not MediaCardViewModel item) return new Border();
            return list ? owner.MakeListRow(item) : owner.MakeCard(item, width: width(), query: query?.Invoke());
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            // Artwork cancels when its element is unloaded; no off-screen control pool is retained.
        }
    }

    private FrameworkElement MakeListRow(MediaCardViewModel item)
    {
        var row = new Grid { Height = 92, ColumnSpacing = 20, BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var main = new Grid { ColumnSpacing = 18 };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var image = new LumenArtwork { Width = 112, Height = 63, CornerRadius = new CornerRadius(10) };
        image.Set(item, ViewModel, ArtworkKind.Landscape, 320);
        main.Children.Add(image);
        var info = new StackPanel { Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        var title = LumenUi.Text(item.DisplayTitle, 17);
        title.Text = item.DisplayTitle;
        title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(title);
        var metadata = LumenUi.Text(MediaMetadata(item), 12);
        metadata.Foreground = LumenTheme.Brush("Muted");
        metadata.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(metadata);
        Grid.SetColumn(info, 1);
        main.Children.Add(info);
        var open = new Button { Content = main, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
        open.Tag = item;
        AutomationProperties.SetName(open, item.AutomationLabel);
        open.Click += async (_, _) => await OpenItemAsync(item);
        open.PointerEntered += (_, _) => SetAmbience(item);
        row.Children.Add(open);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var play = LumenUi.IconButton("play", "Play");
        play.Click += (_, _) => ForwardPlay(new(item.Item, item.CanResume ? item.ResumeTicks : 0));
        play.Visibility = item.CanPlay || item.Item.Type is "Series" or "Season" ? Visibility.Visible : Visibility.Collapsed;
        actions.Children.Add(play);
        var favorite = LumenUi.IconButton(item.IsFavorite ? "heart-filled" : "heart", item.FavoriteLabel);
        favorite.Click += async (_, _) =>
        {
            await ViewModel.ToggleItemFavoriteAsync(item);
            favorite.Content = LumenUi.Icon(item.IsFavorite ? "heart-filled" : "heart");
            AutomationProperties.SetName(favorite, LumenText.Get(item.FavoriteLabel));
        };
        actions.Children.Add(favorite);
        Grid.SetColumn(actions, 1);
        row.Children.Add(actions);
        return row;
    }
}
