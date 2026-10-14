using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly StackPanel _wall = new() { Margin = new Thickness(56, 116, 78, 64) };
    private readonly Grid _wallHeader = new() { ColumnSpacing = 14, RowSpacing = 14 };
    private readonly TextBlock _wallTitle = Label("Movies", 40, true);
    private readonly TextBlock _wallCount = LumenUi.Text(string.Empty, 14);
    private readonly StackPanel _wallTools = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Button _sortButton = LumenUi.Button("Name A-Z", "arrow-up-down", height: 34);
    private readonly Button _filterButton = LumenUi.Button("Filter", "sliders-horizontal", height: 34);
    private readonly Button _gridButton = LumenUi.IconButton("layout-grid", "Grid view", 28);
    private readonly Button _listButton = LumenUi.IconButton("list", "List view", 28);
    private readonly Slider _posterSize = new() { Minimum = 110, Maximum = 220, StepFrequency = 1, SmallChange = 20, LargeChange = 20, Value = 160, Width = 84, Height = 32 };
    private readonly Border _posterSizeControl = new();
    private readonly CatalogWrapPanel _genrePills = new() { HorizontalSpacing = 8, VerticalSpacing = 8, Margin = new Thickness(0, 20, 0, 0) };
    private readonly ItemsRepeater _wallRepeater = new() { VerticalCacheLength = 1 };
    private readonly UniformGridLayout _wallGridLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 30, ItemsStretch = UniformGridLayoutItemsStretch.None, ItemsJustification = UniformGridLayoutItemsJustification.Start };
    private readonly ItemsRepeater _wallSkeletons = new();
    private readonly UniformGridLayout _wallSkeletonLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 30, ItemsStretch = UniformGridLayoutItemsStretch.None };
    private readonly Grid _wallItems = new() { Margin = new Thickness(0, 26, 0, 0) };
    private readonly StackPanel _wallEmpty = new() { Spacing = 10, Margin = new Thickness(0, 78, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 480 };
    private readonly Button _wallMore = LumenUi.Button("Load more", "chevron-down");
    private readonly StackPanel _alphabet = new() { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 170, 22, 0) };
    private readonly Flyout _sortFlyout = new();
    private readonly Flyout _filterFlyout = new();
    private readonly List<(Button Button, string? Genre)> _genreButtons = [];
    private readonly List<(Button Button, WatchStatusFilter Filter)> _watchButtons = [];
    private readonly List<(Button Button, string? Quality)> _qualityButtons = [];
    private readonly List<(Button Button, string Sort, bool Descending)> _sortButtons = [];
    private readonly Dictionary<string, bool> _wallModes = new(StringComparer.Ordinal);
    private int _columns = 8;
    private double _wallCardWidth = 160;
    private bool _listView;
    private bool _updatingWallControls;
    private string _wallContext = string.Empty;
    private string _wallEmptySignature = string.Empty;
    private string _genreSignature = string.Empty;
    private int _genreNavigationRevision = -1;

    private void BuildWall()
    {
        RegisterCatalogText();
        _wallTitle.FontWeight = Microsoft.UI.Text.FontWeights.Black;
        _wallTitle.LineHeight = 44;
        _wallTitle.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        AutomationProperties.SetAutomationId(_wallTitle, "LumenLibraryTitle");
        AutomationProperties.SetAutomationId(_sortButton, "LumenSort");
        AutomationProperties.SetAutomationId(_filterButton, "LumenFilter");
        AutomationProperties.SetAutomationId(_gridButton, "LumenGridView");
        AutomationProperties.SetAutomationId(_listButton, "LumenListView");
        AutomationProperties.SetAutomationId(_posterSize, "LumenPosterSize");
        AutomationProperties.SetAutomationId(_wallMore, "LumenLoadMore");
        _wallHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _wallHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _wallHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _wallHeader.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        title.Children.Add(_wallTitle);
        _wallCount.VerticalAlignment = VerticalAlignment.Bottom;
        _wallCount.Margin = new Thickness(0, 0, 0, 6);
        _wallCount.TextWrapping = TextWrapping.NoWrap;
        title.Children.Add(_wallCount);
        _wallHeader.Children.Add(title);
        Grid.SetColumn(_wallTools, 1);
        _wallHeader.Children.Add(_wallTools);
        _sortButton.Padding = new Thickness(12, 0, 10, 0);
        _filterButton.Padding = new Thickness(12, 0, 12, 0);
        _wallTools.Children.Add(_sortButton);
        _wallTools.Children.Add(_filterButton);
        var viewModes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var button in new[] { _gridButton, _listButton })
        {
            button.Width = button.MinWidth = 30;
            button.BorderThickness = new Thickness(0);
            button.Content = LumenUi.Icon(ReferenceEquals(button, _gridButton) ? "grid" : "list", 16);
            viewModes.Children.Add(button);
        }
        _wallTools.Children.Add(new Border { Child = viewModes, Height = 34, Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(17), Padding = new Thickness(2) });
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var small = LumenUi.IconButton("minus", "Smaller posters", 20);
        var large = LumenUi.IconButton("plus", "Larger posters", 20);
        foreach (var button in new[] { small, large })
        {
            button.Background = new SolidColorBrush(Colors.Transparent);
            button.BorderThickness = new Thickness(0);
            button.VerticalAlignment = VerticalAlignment.Center;
        }
        sizes.Children.Add(small);
        sizes.Children.Add(_posterSize);
        sizes.Children.Add(large);
        _posterSizeControl.Child = sizes;
        _posterSizeControl.Height = 34;
        _posterSizeControl.Background = LumenTheme.Brush("Control");
        _posterSizeControl.BorderBrush = LumenTheme.Brush("Line");
        _posterSizeControl.BorderThickness = new Thickness(1);
        _posterSizeControl.CornerRadius = new CornerRadius(17);
        _posterSizeControl.Padding = new Thickness(10, 0, 10, 0);
        _wallTools.Children.Add(_posterSizeControl);
        AutomationProperties.SetName(_posterSize, LumenText.Get("Poster size"));
        small.Click += (_, _) => _posterSize.Value = Math.Max(110, _posterSize.Value - 20);
        large.Click += (_, _) => _posterSize.Value = Math.Min(220, _posterSize.Value + 20);
        _posterSize.ValueChanged += (_, _) =>
        {
            if (_updatingWallControls) return;
            _preferences = _preferences with { PosterWidth = (int)Math.Round(_posterSize.Value) };
            PreferencesChanged?.Invoke(this, _preferences);
            UpdateWallLayout(ActualWidth > 0 ? ActualWidth : 1440);
        };
        _gridButton.Click += (_, _) => SetWallMode(false);
        _listButton.Click += (_, _) => SetWallMode(true);
        BuildSortFlyout();
        BuildFilterFlyout();
        _sortButton.Flyout = _sortFlyout;
        _filterButton.Flyout = _filterFlyout;
        _wall.Children.Add(_wallHeader);
        BuildWallLibrarySelector();
        _wall.Children.Add(_genrePills);
        _wall.Children.Add(_wallNavigationStatus);
        _wallRepeater.ItemsSource = ViewModel.Items;
        _wallRepeater.Layout = _wallGridLayout;
        _wallRepeater.ItemTemplate = new LibraryCardFactory(this, () => _wallCardWidth);
        LumenMediaCard.ConfigureNavigationGroup(_wallRepeater, initialNavigation: false);
        ConfigureWallNavigation();
        _wallSkeletons.ItemsSource = Enumerable.Range(0, 16).ToArray();
        _wallSkeletons.Layout = _wallSkeletonLayout;
        _wallSkeletons.ItemTemplate = new CatalogSkeletonFactory(() => _wallCardWidth);
        _wallItems.Children.Add(_wallRepeater);
        _wallItems.Children.Add(_wallSkeletons);
        _wallItems.Children.Add(_wallEmpty);
        _wall.Children.Add(_wallItems);
        _wallMore.HorizontalAlignment = HorizontalAlignment.Center;
        _wallMore.Margin = new Thickness(0, 24, 0, 8);
        _wallMore.Click += async (_, _) => { _autoPageBlocked = false; await ViewModel.LoadMoreAsync(); };
        _wall.Children.Add(_wallMore);
        foreach (var letter in "#ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var label = letter.ToString();
            var button = new Button
            {
                Content = LumenUi.Text(label, 11), Width = 22, Height = 19, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent),
                UseSystemFocusVisuals = true, Tag = label
            };
            var navigationLabel = label == "#" ? LumenText.Get("Jump to the first title") : LumenText.Get("Jump to titles beginning with {0}", label);
            AutomationProperties.SetName(button, navigationLabel);
            ToolTipService.SetToolTip(button, navigationLabel);
            AutomationProperties.SetAutomationId(button, "LumenAlphabet" + label);
            button.Click += async (_, _) => await JumpToWallInitialAsync(label);
            _alphabet.Children.Add(button);
        }
        _root.Children.Add(_alphabet);
    }

    private void RenderWall()
    {
        var context = ViewModel.IsFavorites ? "favorites" : ViewModel.BrowseMediaType ?? ViewModel.SelectedLibraryId ?? ViewModel.Title;
        if (_wallContext != context)
        {
            _wallContext = context;
            ApplyWallMode(_wallModes.GetValueOrDefault(context));
        }
        _wallTitle.Text = LumenText.Get(ViewModel.IsFavorites ? "Favorites" : ViewModel.Title);
        RenderWallLibrarySelector();
        RenderWallCount();
        _wallCount.Visibility = ViewModel.IsBusy && ViewModel.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _wallMore.Visibility = ViewModel.HasMore ? Visibility.Visible : Visibility.Collapsed;
        _wallMore.IsEnabled = ViewModel.CanLoadMore;
        _sortButton.Content = WallToolContent(SortLabel(), "arrow-up-down", true);
        AutomationProperties.SetName(_sortButton, LumenText.Get(SortLabel()));
        var filterCount = (ViewModel.BrowseWatchFilter != WatchStatusFilter.All ? 1 : 0) + (ViewModel.BrowseQualityFilter is not null ? 1 : 0);
        _filterButton.Content = WallToolContent("Filter", "filter", count: filterCount);
        _filterButton.Background = filterCount > 0 ? LumenTheme.Brush("ControlStrong") : LumenTheme.Brush("Control");
        _gridButton.Background = !_listView ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
        _listButton.Background = _listView ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
        _gridButton.Opacity = _listView ? .55 : 1;
        _listButton.Opacity = _listView ? 1 : .55;
        _posterSizeControl.Visibility = _listView ? Visibility.Collapsed : Visibility.Visible;
        foreach (var entry in _sortButtons)
        {
            var selected = entry.Sort == ViewModel.BrowseSortKey && entry.Descending == ViewModel.BrowseSortDescending;
            entry.Button.Background = selected ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
            if (entry.Button.Content is StackPanel content && content.Children.Count > 0) content.Children[0].Opacity = selected ? 1 : 0;
        }
        foreach (var entry in _watchButtons) SetSegmentAppearance(entry.Button, entry.Filter == ViewModel.BrowseWatchFilter);
        foreach (var entry in _qualityButtons) SetSegmentAppearance(entry.Button, entry.Quality == ViewModel.BrowseQualityFilter);
        RenderGenres();
        var loading = ViewModel.IsBusy && ViewModel.Items.Count == 0;
        _wallSkeletons.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        _wallRepeater.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        RenderWallEmpty();
        UpdateAlphabet(ViewModel.Items.FirstOrDefault(item => item.Id == _ambienceId));
        if (_ambienceId is null || !ViewModel.Items.Any(item => item.Id == _ambienceId)) SetAmbience(ViewModel.Items.FirstOrDefault());
    }

    private string SortLabel() => ViewModel.BrowseSortKey switch
    {
        "ProductionYear" => "Release date", "DateCreated" => "Recently added", "CommunityRating" => "Rating", "Random" => "Random",
        _ => ViewModel.BrowseSortDescending ? "Name Z-A" : "Name A-Z"
    };

    private void RenderGenres()
    {
        var loadedCounts = ViewModel.Items.SelectMany(item => item.Item.Genres ?? []).GroupBy(genre => genre, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var genres = ViewModel.AvailableGenres.Concat(loadedCounts.Keys)
            .Where(genre => !string.IsNullOrWhiteSpace(genre)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(genre => ViewModel.AvailableGenreCounts.GetValueOrDefault(genre, loadedCounts.GetValueOrDefault(genre)))
            .ThenBy(genre => genre, StringComparer.CurrentCultureIgnoreCase).Take(9).ToList();
        if (ViewModel.BrowseGenre is { } selectedGenre && !genres.Contains(selectedGenre, StringComparer.OrdinalIgnoreCase)) genres.Add(selectedGenre);
        var signature = string.Join("|", genres) + ";" + ViewModel.BrowseGenre;
        if (_genreSignature == signature) return;
        _genreSignature = signature;
        _genrePills.Children.Clear();
        _genreButtons.Clear();
        foreach (var genre in new string?[] { null }.Concat(genres))
        {
            var selected = genre == ViewModel.BrowseGenre;
            var label = genre ?? LumenText.Get("All");
            var button = LumenUi.Button(label, height: 32);
            button.MaxWidth = 200;
            button.Padding = new Thickness(15, 0, 15, 0);
            button.BorderThickness = new Thickness(0);
            button.Background = selected ? LumenTheme.Brush("Ink") : LumenTheme.Brush("Chip");
            var text = LumenUi.Text(label, 13);
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.TextWrapping = TextWrapping.NoWrap;
            text.Foreground = selected ? LumenTheme.Brush("Background") : LumenTheme.Brush("Sub");
            text.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            button.Content = text;
            ToolTipService.SetToolTip(button, label);
            button.Click += async (_, _) => await ViewModel.SetLumenBrowseFiltersAsync(genre, ViewModel.BrowseWatchFilter);
            _genrePills.Children.Add(button);
            _genreButtons.Add((button, genre));
        }
    }

    private void SetWallMode(bool list)
    {
        if (_listView == list) return;
        _wallModes[_wallContext] = list;
        ApplyWallMode(list);
        RenderWall();
        UpdateWallLayout(ActualWidth > 0 ? ActualWidth : 1440);
    }

    private void ApplyWallMode(bool list)
    {
        if (_listView == list && _wallRepeater.ItemTemplate is not null) return;
        _listView = list;
        _wallRepeater.ItemTemplate = new LibraryCardFactory(this, () => _wallCardWidth, list);
        _wallRepeater.Layout = list ? new StackLayout { Spacing = 2 } : _wallGridLayout;
        _wallItems.Margin = new Thickness(0, list ? 22 : 26, 0, 0);
    }
}
