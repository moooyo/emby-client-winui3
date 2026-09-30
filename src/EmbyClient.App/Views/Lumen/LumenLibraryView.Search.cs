using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly StackPanel _search = new() { Spacing = 16, Margin = new Thickness(56, 96, 64, 64) };
    private readonly Border _searchBar = new() { Height = 60, CornerRadius = new CornerRadius(30), BorderThickness = new Thickness(1.5), Padding = new Thickness(24, 0, 8, 0) };
    private readonly TextBox _searchInput = new() { FontSize = 20, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center, SelectionHighlightColor = LumenTheme.Brush("Accent"), TextWrapping = TextWrapping.NoWrap };
    private readonly TextBlock _searchHint = LumenUi.Text(string.Empty, 12);
    private readonly StackPanel _searchTabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(-14, 0, 0, 0) };
    private readonly Dictionary<string, (Button Button, TextBlock Label, TextBlock Count, Border Dot)> _searchTabControls = new(StringComparer.Ordinal);
    private readonly StackPanel _searchResults = new() { Spacing = 30 };
    private readonly Grid _searchFeatured = new() { ColumnSpacing = 28, RowSpacing = 28 };
    private readonly Grid _searchSecondary = new() { ColumnSpacing = 20, RowSpacing = 20, VerticalAlignment = VerticalAlignment.Top };
    private StackPanel? _searchPeopleSection;
    private StackPanel? _searchCollectionsSection;
    private int _searchPeoplePreviewCount;
    private int _searchCollectionPreviewCount;
    private int _searchLayoutRevision;
    private readonly ObservableCollection<MediaCardViewModel> _searchMedia = [];
    private readonly ItemsRepeater _searchRepeater = new() { VerticalCacheLength = 1 };
    private readonly StackPanel _searchMediaSection = new() { Spacing = 14 };
    private readonly TextBlock _searchMediaHeading = LumenUi.Text(string.Empty, 16);
    private readonly UniformGridLayout _searchGridLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 30, ItemsStretch = UniformGridLayoutItemsStretch.None };
    private readonly Button _searchMore = LumenUi.Button("Load more", "chevron-down");
    private readonly Button _peopleMore = LumenUi.Button("Load more", "chevron-down");
    private string _searchTab = "all";
    private string _searchSummarySignature = string.Empty;
    private double _searchCardWidth = 145;
    private bool _updatingSearchInput;
    private bool _searchDraft;
    private int _searchInputVersion;
    private int _searchTabVersion;
    private Task _searchTypeOperation = Task.CompletedTask;
    private readonly HashSet<MediaCardViewModel> _searchObservedCards = [];

    private void BuildSearch()
    {
        var input = new Grid { ColumnSpacing = 14 };
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchIcon = LumenUi.Icon("search", 22);
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        input.Children.Add(searchIcon);
        _searchInput.FontFamily = LumenTheme.SansFont;
        _searchInput.PlaceholderText = LumenText.Get("Search your library");
        _searchInput.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _searchInput.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        _searchInput.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        _searchInput.Loaded += (_, _) => HideNativeSearchClearButton(_searchInput);
        AutomationProperties.SetName(_searchInput, LumenText.Get("Search your library"));
        AutomationProperties.SetAutomationId(_searchInput, "LumenSearchInput");
        Grid.SetColumn(_searchInput, 1);
        input.Children.Add(_searchInput);
        _searchHint.VerticalAlignment = VerticalAlignment.Center;
        _searchHint.MaxWidth = 340;
        _searchHint.TextWrapping = TextWrapping.NoWrap;
        _searchHint.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(_searchHint, 2);
        input.Children.Add(_searchHint);
        var clear = LumenUi.IconButton("x", "Clear search", 44);
        clear.Background = new SolidColorBrush(Colors.Transparent);
        clear.BorderThickness = new Thickness(0);
        clear.Click += (_, _) => { _searchInput.Text = string.Empty; _searchInput.Focus(FocusState.Programmatic); };
        Grid.SetColumn(clear, 3);
        input.Children.Add(clear);
        _searchBar.Child = input;
        _search.Children.Add(_searchBar);
        _searchInput.TextChanged += async (_, _) =>
        {
            if (_updatingSearchInput || !ViewModel.IsSearch) return;
            _searchDraft = true;
            await RunSearchAsync(true);
        };
        _searchInput.KeyDown += async (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            await RunSearchAsync(false);
        };
        foreach (var tab in new[] { (Key: "all", Label: "All"), (Key: "movies", Label: "Movies"), (Key: "series", Label: "Series"), (Key: "people", Label: "People"), (Key: "collections", Label: "Collections") })
        {
            var content = new Grid { Height = 40 };
            var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var label = Label(tab.Label, 15);
            var count = LumenUi.Text(string.Empty, 12);
            count.Foreground = LumenTheme.Brush("Muted");
            count.VerticalAlignment = VerticalAlignment.Center;
            labels.Children.Add(label);
            labels.Children.Add(count);
            content.Children.Add(labels);
            var dot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(3), Background = LumenTheme.Brush("Accent"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            content.Children.Add(dot);
            var button = new Button { Content = content, Height = 40, MinHeight = 40, Padding = new Thickness(14, 0, 14, 0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), HorizontalContentAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(button, LumenText.Get(tab.Label));
            AutomationProperties.SetAutomationId(button, "LumenSearchTab_" + tab.Key);
            button.Click += async (_, _) => await SelectSearchTabAsync(tab.Key);
            _searchTabs.Children.Add(button);
            _searchTabControls[tab.Key] = (button, label, count, dot);
        }
        _search.Children.Add(_searchTabs);
        _searchFeatured.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(600) });
        _searchFeatured.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _searchFeatured.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _searchFeatured.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _searchSecondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _searchSecondary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _searchSecondary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _searchSecondary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _searchRepeater.ItemsSource = _searchMedia;
        _searchRepeater.Layout = _searchGridLayout;
        _searchRepeater.ItemTemplate = new LibraryCardFactory(this, () => _searchCardWidth, query: () => ViewModel.SearchText);
        _searchMore.HorizontalAlignment = HorizontalAlignment.Center;
        _searchMore.Click += async (_, _) => { _autoPageBlocked = false; await ViewModel.LoadMoreAsync(); };
        _searchMediaHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _searchMediaSection.Children.Add(_searchMediaHeading);
        _searchMediaSection.Children.Add(_searchRepeater);
        _searchMediaSection.Children.Add(_searchMore);
        _peopleMore.HorizontalAlignment = HorizontalAlignment.Center;
        _peopleMore.Click += async (_, _) => await ViewModel.LoadMoreSearchPeopleAsync();
        _search.Children.Add(_searchResults);
    }

    private void SetSearchDraft(string text)
    {
        _updatingSearchInput = true;
        _searchInput.Text = text;
        _updatingSearchInput = false;
        _searchDraft = false;
    }

    private async Task RunSearchAsync(bool debounce)
    {
        var text = _searchInput.Text;
        var generation = _sessionGeneration;
        var inputVersion = ++_searchInputVersion;
        await _searchTypeOperation;
        if (generation != _sessionGeneration || inputVersion != _searchInputVersion || !ViewModel.IsSearch || _searchInput.Text != text) return;
        await ViewModel.SearchLumenAsync(text, debounce);
        if (generation != _sessionGeneration || inputVersion != _searchInputVersion || !ViewModel.IsSearch || _searchInput.Text != text) return;
        _searchDraft = false;
        QueueRender();
    }

    private async Task SelectSearchTabAsync(string tab)
    {
        var text = _searchInput.Text;
        var pendingInput = _searchDraft || ViewModel.HasPendingSearch || text.Trim() != ViewModel.SearchText;
        var generation = _sessionGeneration;
        var tabVersion = ++_searchTabVersion;
        if (tab != "people")
        {
            _searchInputVersion++;
            if (pendingInput) _searchDraft = true;
        }
        _searchTab = tab;
        _searchSummarySignature = string.Empty;
        if (tab != "people")
        {
            _searchTypeOperation = ViewModel.SetLumenSearchTypeAsync(tab switch { "movies" => "Movie", "series" => "Series", "collections" => "BoxSet", _ => null });
            await _searchTypeOperation;
        }
        if (generation != _sessionGeneration || tabVersion != _searchTabVersion || !ViewModel.IsSearch) return;
        if (pendingInput && tab != "people" && _searchInput.Text == text) await RunSearchAsync(false);
        _scroll.ChangeView(null, 0, null, true);
        QueueRender();
    }

    private void RenderSearch()
    {
        ObserveSearchCards();
        if (!_searchDraft && !ViewModel.HasPendingSearch && _searchInput.Text != ViewModel.SearchText) SetSearchDraft(ViewModel.SearchText);
        if (_searchTab != "people") _searchTab = ViewModel.BrowseSearchType switch { "Movie" => "movies", "Series" => "series", "BoxSet" => "collections", _ => "all" };
        RenderSearchTabs();
        var mediaCount = SearchMediaCount(ViewModel.BrowseSearchType ?? "all", ViewModel.Items.Count);
        var peopleCount = SearchPeopleCount();
        var count = _searchTab == "people" ? peopleCount : _searchTab == "all" ? LumenSearchCount.Sum(mediaCount, peopleCount) : mediaCount;
        var server = _session?.Server.ServerName ?? "Emby";
        _searchHint.Text = LumenText.Get("Search in {0}", server) + (ViewModel.HasSearchQuery ? " \u00B7 " + LumenText.Get("{0} results", count.Format(CultureInfo.CurrentCulture)) : string.Empty);
        var media = _searchTab == "all" ? ViewModel.Items.Where(item => item.Item.Type is not ("Person" or "BoxSet")).ToArray() : ViewModel.Items.ToArray();
        ReconcileMedia(_searchMedia, media);
        var signature = ViewModel.SearchText + ";" + _searchTab + ";" + string.Join("|", ViewModel.Items.Select(item => item.Id + ":" + item.Title))
            + ";" + string.Join("|", ViewModel.SearchPeople.Select(item => item.Id + ":" + item.Title)) + ";" + ViewModel.SearchPeopleError;
        if (_searchSummarySignature != signature)
        {
            _searchLayoutRevision++;
            _searchSummarySignature = signature;
            var keepMediaSection = ViewModel.HasSearchQuery && _searchTab != "people" && media.Length > 0;
            // Retained cards must not unload when only their surrounding summary changes.
            foreach (var child in _searchResults.Children.Where(child => !keepMediaSection
                || !ReferenceEquals(child, _searchMediaSection)).ToArray()) _searchResults.Children.Remove(child);
            _searchFeatured.Children.Clear();
            _searchSecondary.Children.Clear();
            _searchPeopleSection = null;
            _searchCollectionsSection = null;
            _searchPeoplePreviewCount = 0;
            _searchCollectionPreviewCount = 0;
            if (!ViewModel.HasSearchQuery) return;
            if (_searchTab == "people")
            {
                var people = new StackPanel { Spacing = 10 };
                foreach (var person in ViewModel.SearchPeople) people.Children.Add(PersonResult(person));
                _searchResults.Children.Add(people);
                _searchResults.Children.Add(_peopleMore);
            }
            else
            {
                var best = media.OrderBy(item => string.Equals(item.Title, ViewModel.SearchText, StringComparison.CurrentCultureIgnoreCase) ? 0 : item.Title.Contains(ViewModel.SearchText, StringComparison.CurrentCultureIgnoreCase) ? 1 : 2).FirstOrDefault();
                if (best is not null)
                {
                    _searchFeatured.Children.Add(BestMatch(best));
                    SetAmbience(best);
                }
                if (_searchTab == "all")
                {
                    if (ViewModel.SearchPeople.Count > 0)
                    {
                        _searchPeopleSection = SearchResultSection("People");
                        var people = new StackPanel { Spacing = 8 };
                        foreach (var person in ViewModel.SearchPeople.Take(3)) people.Children.Add(PersonResult(person));
                        _searchPeoplePreviewCount = people.Children.Count;
                        _searchPeopleSection.Children.Add(people);
                        _searchSecondary.Children.Add(_searchPeopleSection);
                    }
                    var collections = ViewModel.Items.Where(item => item.Item.Type == "BoxSet").Take(3).ToArray();
                    if (collections.Length > 0)
                    {
                        _searchCollectionsSection = SearchResultSection("Collections");
                        var collectionRows = new StackPanel { Spacing = 8 };
                        foreach (var collection in collections) collectionRows.Children.Add(CollectionResult(collection));
                        _searchCollectionPreviewCount = collectionRows.Children.Count;
                        _searchCollectionsSection.Children.Add(collectionRows);
                        _searchSecondary.Children.Add(_searchCollectionsSection);
                    }
                    if (_searchSecondary.Children.Count > 0) _searchFeatured.Children.Add(_searchSecondary);
                }
                if (_searchFeatured.Children.Count > 0) _searchResults.Children.Insert(0, _searchFeatured);
                if (media.Length > 0)
                {
                    _searchMediaHeading.Text = LumenText.Get(_searchTab == "collections" ? "Collections" : "Movies and series");
                    if (!_searchResults.Children.Contains(_searchMediaSection)) _searchResults.Children.Add(_searchMediaSection);
                }
            }
            if (ViewModel.SearchPeopleError.Length > 0)
            {
                var error = new StackPanel { Spacing = 10 };
                error.Children.Add(Label(ViewModel.SearchPeopleError, 13));
                var retry = LumenUi.Button("Retry", "refresh-cw", height: 36);
                retry.HorizontalAlignment = HorizontalAlignment.Left;
                retry.Click += async (_, _) => await ViewModel.LoadMoreSearchPeopleAsync();
                error.Children.Add(retry);
                _searchResults.Children.Add(error);
            }
        }
        _searchMore.Visibility = ViewModel.HasMore ? Visibility.Visible : Visibility.Collapsed;
        _searchMore.IsEnabled = ViewModel.CanLoadMore;
        _peopleMore.Visibility = ViewModel.SearchPeopleHasMore ? Visibility.Visible : Visibility.Collapsed;
        _peopleMore.IsEnabled = !ViewModel.SearchPeopleIsBusy;
    }

    private void ObserveSearchCards()
    {
        var current = ViewModel.Items.Concat(ViewModel.SearchPeople).ToHashSet();
        foreach (var card in _searchObservedCards.Where(card => !current.Contains(card)).ToArray())
        {
            card.PropertyChanged -= SearchCardChanged;
            _searchObservedCards.Remove(card);
        }
        foreach (var card in current) if (_searchObservedCards.Add(card)) card.PropertyChanged += SearchCardChanged;
    }

    private void DetachSearchCards()
    {
        foreach (var card in _searchObservedCards) card.PropertyChanged -= SearchCardChanged;
        _searchObservedCards.Clear();
    }

    private void SearchCardChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MediaCardViewModel.Item) or null or "")) return;
        _searchSummarySignature = string.Empty;
        QueueRender();
    }
}
