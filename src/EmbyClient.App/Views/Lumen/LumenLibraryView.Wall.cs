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
    private readonly StackPanel _wall = new() { Spacing = 20, Margin = new Thickness(56, 104, 88, 64) };
    private readonly Grid _wallHeader = new() { ColumnSpacing = 24, RowSpacing = 16 };
    private readonly TextBlock _wallTitle = Label("Movies", 40, true);
    private readonly TextBlock _wallCount = LumenUi.Text(string.Empty, 13);
    private readonly StackPanel _wallTools = new() { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _sortButton = LumenUi.Button("Name A-Z", "arrow-up-down", height: 36);
    private readonly Button _filterButton = LumenUi.Button("Filter", "sliders-horizontal", height: 36);
    private readonly Button _gridButton = LumenUi.IconButton("layout-grid", "Grid view", 34);
    private readonly Button _listButton = LumenUi.IconButton("list", "List view", 34);
    private readonly Slider _posterSize = new() { Minimum = 6, Maximum = 10, StepFrequency = 1, SmallChange = 1, Value = 8, Width = 82, Height = 34 };
    private readonly StackPanel _genrePills = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly ItemsRepeater _wallRepeater = new() { VerticalCacheLength = 1 };
    // UniformGridLayout uses item-flow orientation, the inverse of its scrolling axis.
    private readonly UniformGridLayout _wallGridLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 30, ItemsStretch = UniformGridLayoutItemsStretch.None, ItemsJustification = UniformGridLayoutItemsJustification.Start };
    private readonly Button _wallMore = LumenUi.Button("Load more", "chevron-down");
    private readonly StackPanel _alphabet = new() { Spacing = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 178, 20, 0) };
    private readonly Flyout _sortFlyout = new();
    private readonly Flyout _filterFlyout = new();
    private readonly List<(Button Button, string? Genre)> _genreButtons = [];
    private readonly List<(Button Button, WatchStatusFilter Filter)> _watchButtons = [];
    private readonly List<(Button Button, string Sort, bool Descending)> _sortButtons = [];
    private int _columns = 8;
    private double _wallCardWidth = 145;
    private bool _listView;
    private bool _updatingWallControls;
    private string _genreSignature = string.Empty;
    private int _genreNavigationRevision = -1;

    private void BuildWall()
    {
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
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        title.Children.Add(_wallTitle);
        _wallCount.VerticalAlignment = VerticalAlignment.Bottom;
        _wallCount.Margin = new Thickness(0, 0, 0, 5);
        title.Children.Add(_wallCount);
        _wallHeader.Children.Add(title);
        Grid.SetColumn(_wallTools, 1);
        _wallHeader.Children.Add(_wallTools);
        _wallTools.Children.Add(_sortButton);
        _wallTools.Children.Add(_filterButton);
        var viewModes = new StackPanel { Orientation = Orientation.Horizontal };
        viewModes.Children.Add(_gridButton);
        viewModes.Children.Add(_listButton);
        _wallTools.Children.Add(new Border { Child = viewModes, Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(19), Padding = new Thickness(2) });
        var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var small = LumenUi.IconButton("minus", "Smaller posters", 28);
        var large = LumenUi.IconButton("plus", "Larger posters", 28);
        sizes.Children.Add(small);
        sizes.Children.Add(_posterSize);
        sizes.Children.Add(large);
        _wallTools.Children.Add(new Border { Child = sizes, Background = LumenTheme.Brush("Control"), BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(17), Padding = new Thickness(8, 0, 8, 0) });
        AutomationProperties.SetName(_posterSize, LumenText.Get("Poster size"));
        small.Click += (_, _) => _posterSize.Value = Math.Max(6, _posterSize.Value - 1);
        large.Click += (_, _) => _posterSize.Value = Math.Min(10, _posterSize.Value + 1);
        _posterSize.ValueChanged += (_, _) =>
        {
            if (_updatingWallControls) return;
            _columns = 16 - (int)Math.Round(_posterSize.Value);
            _preferences = _preferences with { PosterColumns = _columns };
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
        _wall.Children.Add(new ScrollViewer { Content = _genrePills, HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        _wallRepeater.ItemsSource = ViewModel.Items;
        _wallRepeater.Layout = _wallGridLayout;
        _wallRepeater.ItemTemplate = new LibraryCardFactory(this, () => _wallCardWidth);
        _wall.Children.Add(_wallRepeater);
        _wallMore.HorizontalAlignment = HorizontalAlignment.Center;
        _wallMore.Margin = new Thickness(0, 10, 0, 8);
        _wallMore.Click += async (_, _) => { _autoPageBlocked = false; await ViewModel.LoadMoreAsync(); };
        _wall.Children.Add(_wallMore);
        foreach (var letter in "#ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var label = letter.ToString();
            var button = new Button
            {
                Content = LumenUi.Text(label, 10), Width = 22, Height = 19, Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent)
            };
            AutomationProperties.SetName(button, LumenText.Get("Titles beginning with {0}", label));
            ToolTipService.SetToolTip(button, LumenText.Get("Titles beginning with {0}", label));
            button.Tag = label;
            AutomationProperties.SetAutomationId(button, "LumenAlphabet" + label);
            button.Click += async (_, _) =>
            {
                var prefix = label == "#" || ViewModel.BrowseNameStartsWith == label ? null : label;
                await ViewModel.SetLumenBrowseFiltersAsync(ViewModel.BrowseGenre, ViewModel.BrowseWatchFilter, prefix);
            };
            _alphabet.Children.Add(button);
        }
        _root.Children.Add(_alphabet);
    }

    private void RenderWall()
    {
        _wallTitle.Text = ViewModel.IsFavorites ? LumenText.Get("Favorites") : ViewModel.BrowseMediaType switch
        {
            "Movie" => LumenText.Get("Movies"), "Series" => LumenText.Get("Series"), _ => LumenText.Get(ViewModel.Title)
        };
        _wallCount.Text = LumenText.Get("{0} items", (ViewModel.TotalItemsCount ?? ViewModel.Items.Count).ToString("N0", CultureInfo.CurrentCulture));
        _wallMore.Visibility = ViewModel.HasMore ? Visibility.Visible : Visibility.Collapsed;
        _wallMore.IsEnabled = ViewModel.CanLoadMore;
        _sortButton.Content = ActionContent(SortLabel(), "arrow-up-down");
        AutomationProperties.SetName(_sortButton, LumenText.Get(SortLabel()));
        _filterButton.Background = ViewModel.BrowseWatchFilter != WatchStatusFilter.All || ViewModel.BrowseGenre is not null || ViewModel.BrowseNameStartsWith is not null ? LumenTheme.Brush("ControlStrong") : LumenTheme.Brush("Control");
        _gridButton.Background = !_listView ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
        _listButton.Background = _listView ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
        foreach (var entry in _sortButtons)
        {
            var selected = entry.Sort == ViewModel.BrowseSortKey && entry.Descending == ViewModel.BrowseSortDescending;
            entry.Button.Background = selected ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
            if (entry.Button.Content is StackPanel content && content.Children.Count > 0) content.Children[0].Opacity = selected ? 1 : 0;
        }
        foreach (var entry in _watchButtons)
        {
            var selected = entry.Filter == ViewModel.BrowseWatchFilter;
            entry.Button.Background = selected ? LumenTheme.Brush("ControlStrong") : new SolidColorBrush(Colors.Transparent);
            if (entry.Button.Content is StackPanel content && content.Children.Count > 0) content.Children[0].Opacity = selected ? 1 : 0;
        }
        RenderGenres();
        UpdateAlphabet(ViewModel.Items.FirstOrDefault(item => item.Id == _ambienceId));
        if (_ambienceId is null || !ViewModel.Items.Any(item => item.Id == _ambienceId)) SetAmbience(ViewModel.Items.FirstOrDefault());
    }

    private string SortLabel() => ViewModel.BrowseSortKey switch
    {
        "ProductionYear" => "Release year", "DateCreated" => "Recently added", "CommunityRating" => "Rating",
        _ => ViewModel.BrowseSortDescending ? "Name Z-A" : "Name A-Z"
    };

    private void RenderGenres()
    {
        var genres = ViewModel.AvailableGenres.Concat(ViewModel.Items.SelectMany(item => item.Item.Genres ?? []))
            .Where(genre => !string.IsNullOrWhiteSpace(genre)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        var signature = string.Join("|", genres) + ";" + ViewModel.BrowseGenre;
        if (_genreSignature == signature) return;
        _genreSignature = signature;
        _genrePills.Children.Clear();
        _genreButtons.Clear();
        foreach (var genre in new string?[] { null }.Concat(genres))
        {
            var selected = genre == ViewModel.BrowseGenre;
            var label = genre ?? "All";
            var button = LumenUi.Button(label, height: 32);
            button.MinWidth = 56;
            button.MaxWidth = 200;
            button.Padding = new Thickness(16, 0, 16, 0);
            button.BorderThickness = new Thickness(0);
            button.Background = selected ? LumenTheme.Brush("Ink") : LumenTheme.Brush("Control");
            var text = LumenUi.Text(label, 12);
            text.Text = genre ?? LumenText.Get("All");
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Foreground = selected ? LumenTheme.Brush("Background") : LumenTheme.Brush("Sub");
            text.FontWeight = selected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            button.Content = text;
            ToolTipService.SetToolTip(button, genre ?? LumenText.Get("All"));
            button.Click += async (_, _) => await ViewModel.SetLumenBrowseFiltersAsync(genre, ViewModel.BrowseWatchFilter, ViewModel.BrowseNameStartsWith);
            _genrePills.Children.Add(button);
            _genreButtons.Add((button, genre));
        }
    }

    private void SetWallMode(bool list)
    {
        if (_listView == list) return;
        _listView = list;
        _wallRepeater.ItemTemplate = new LibraryCardFactory(this, () => _wallCardWidth, list);
        _wallRepeater.Layout = list ? new StackLayout { Spacing = 12 } : _wallGridLayout;
        RenderWall();
        UpdateWallLayout(ActualWidth > 0 ? ActualWidth : 1440);
    }
}
