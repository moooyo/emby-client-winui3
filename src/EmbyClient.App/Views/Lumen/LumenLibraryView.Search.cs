using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
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
    private readonly StackPanel _search = new() { Spacing = 16, Margin = new Thickness(56, 116, 56, 60) };
    private readonly Border _searchBar = new() { Height = 60, CornerRadius = new CornerRadius(30), BorderThickness = new Thickness(1.5), Padding = new Thickness(26, 0, 10, 0) };
    private readonly Button _searchClear = LumenUi.IconButton("x", "Clear search", 40);
    private readonly TextBox _searchInput = new() { FontSize = 20, MinWidth = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Center, SelectionHighlightColor = LumenTheme.Brush("Accent"), TextWrapping = TextWrapping.NoWrap, IsSpellCheckEnabled = false };
    private readonly TextBlock _searchHint = LumenUi.Text(string.Empty, 12);
    private readonly StackPanel _searchTabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(-14, 0, 0, 0) };
    private readonly Dictionary<string, (Button Button, TextBlock Label, TextBlock Count, Border Dot)> _searchTabControls = new(StringComparer.Ordinal);
    private readonly StackPanel _searchResults = new() { Spacing = 30 };
    private readonly Grid _searchFeatured = new() { ColumnSpacing = 28, RowSpacing = 28 };
    private readonly Grid _searchSecondary = new() { ColumnSpacing = 20, RowSpacing = 20, VerticalAlignment = VerticalAlignment.Top };
    private StackPanel? _searchPeopleSection;
    private int _searchPeoplePreviewCount;
    private int _searchLayoutRevision;
    private readonly ObservableCollection<MediaCardViewModel> _searchMedia = [];
    private readonly ItemsRepeater _searchRepeater = new() { VerticalCacheLength = 1 };
    private readonly ItemsRepeater _searchPeopleRepeater = new() { VerticalCacheLength = 1 };
    private readonly UniformGridLayout _searchPeopleGridLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 10, MinRowSpacing = 10, MinItemHeight = 72, ItemsStretch = UniformGridLayoutItemsStretch.Fill };
    private readonly ItemsRepeater _searchSkeletons = new();
    private readonly UniformGridLayout _searchSkeletonLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 26, ItemsStretch = UniformGridLayoutItemsStretch.None };
    private readonly StackPanel _searchMediaSection = new() { Spacing = 14 };
    private readonly TextBlock _searchMediaHeading = LumenUi.Text(string.Empty, 16);
    private readonly UniformGridLayout _searchGridLayout = new() { Orientation = Orientation.Horizontal, MinColumnSpacing = 20, MinRowSpacing = 26, ItemsStretch = UniformGridLayoutItemsStretch.None };
    private readonly Button _searchMore = LumenUi.Button("Load more", "chevron-down");
    private readonly Button _peopleMore = LumenUi.Button("Load more", "chevron-down");
    private string _searchTab = "all";
    private string _searchSummarySignature = string.Empty;
    private string _searchPeopleRenderQuery = string.Empty;
    private double _searchCardWidth = 145;
    private bool _updatingSearchInput;
    private bool _searchDraft;
    private int _searchInputVersion;
    private int _searchTabVersion;
    private Task _searchTypeOperation = Task.CompletedTask;
    private readonly HashSet<MediaCardViewModel> _searchObservedCards = [];
    private bool _searchFocusPending;
    private bool _searchFocusQueued;
    private int _searchFocusRequestVersion;
    private int _searchFocusSessionGeneration;
    private int _searchFocusNavigationRevision;
    private long _searchFocusRenderEpoch;

    private void BuildSearch()
    {
        var input = new Grid { ColumnSpacing = 14 };
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        input.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var searchIcon = LumenUi.Icon("search", 20);
        searchIcon.VerticalAlignment = VerticalAlignment.Center;
        searchIcon.Opacity = .85;
        input.Children.Add(searchIcon);
        _searchInput.FontFamily = LumenTheme.SansFont;
        _searchInput.PlaceholderText = LumenText.Get("Search movies, series, people, or genres");
        _searchInput.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        _searchInput.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Colors.Transparent);
        _searchInput.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Colors.Transparent);
        _searchInput.Loaded += (_, _) =>
        {
            _searchInput.ApplyTemplate();
            HideNativeSearchClearButton(_searchInput);
            QueueSearchInputFocus();
        };
        _searchInput.SizeChanged += (_, _) => QueueSearchInputFocus();
        _searchInput.Unloaded += (_, _) => CancelSearchInputFocus();
        Unloaded += (_, _) => CancelSearchInputFocus();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility != Visibility.Visible) CancelSearchInputFocus();
        });
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
        var escapeText = LumenUi.Text("Esc", 11);
        escapeText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        escapeText.Foreground = LumenTheme.Brush("Sub");
        var escape = new Border { Child = escapeText, CornerRadius = new CornerRadius(5), BorderBrush = LumenTheme.Brush("LineStrong"), BorderThickness = new Thickness(1), Padding = new Thickness(6, 1, 6, 1), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(escape, 3);
        input.Children.Add(escape);
        _searchClear.Background = new SolidColorBrush(Colors.Transparent);
        _searchClear.BorderThickness = new Thickness(0);
        _searchClear.Content = LumenUi.Icon("x", 16);
        _searchClear.Click += (_, _) => { _searchInput.Text = string.Empty; RequestSearchInputFocus(); };
        Grid.SetColumn(_searchClear, 4);
        input.Children.Add(_searchClear);
        _searchBar.Child = input;
        var searchBarFrame = new Grid();
        AttachHomeShadow(searchBarFrame, 30, 32, 12, .18f);
        searchBarFrame.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(34), BorderThickness = new Thickness(4), Margin = new Thickness(-4),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(26, 226, 184, 107)), IsHitTestVisible = false
        });
        searchBarFrame.Children.Add(_searchBar);
        _search.Children.Add(searchBarFrame);
        _searchInput.TextChanged += async (_, _) =>
        {
            if (_updatingSearchInput || !ViewModel.IsSearch) return;
            _searchDraft = true;
            await RunSearchAsync(true);
        };
        _searchInput.KeyDown += async (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Enter)
            {
                args.Handled = true;
                await RunSearchAsync(false);
            }
            else if (args.Key == Windows.System.VirtualKey.Escape && _searchInput.Text.Length > 0)
            {
                args.Handled = true;
                _searchInput.Text = string.Empty;
            }
            else if (args.Key == Windows.System.VirtualKey.Down)
            {
                if (_searchTab == "people" && ViewModel.SearchPeople.Count > 0)
                    args.Handled = LumenMediaCard.FocusNavigationItem(_searchPeopleRepeater, 0);
                else if (FirstButton(_searchFeatured) is { } best) args.Handled = best.Focus(FocusState.Keyboard);
                else if (_searchMedia.Count > 0) args.Handled = LumenMediaCard.FocusNavigationItem(_searchRepeater, 0);
            }
        };
        foreach (var tab in new[] { (Key: "all", Label: "All"), (Key: "movies", Label: "Movies"), (Key: "series", Label: "Series"), (Key: "people", Label: "People") })
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
        LumenMediaCard.ConfigureNavigationGroup(_searchRepeater, initialNavigation: false);
        _searchPeopleRepeater.ItemsSource = ViewModel.SearchPeople;
        _searchPeopleRepeater.Layout = _searchPeopleGridLayout;
        _searchPeopleRepeater.ItemTemplate = new SearchPersonFactory(this);
        LumenMediaCard.ConfigureNavigationGroup(_searchPeopleRepeater, initialNavigation: false);
        _searchSkeletons.ItemsSource = Enumerable.Range(0, 16).ToArray();
        _searchSkeletons.Layout = _searchSkeletonLayout;
        _searchSkeletons.ItemTemplate = new CatalogSkeletonFactory(() => _searchCardWidth);
        _searchMore.HorizontalAlignment = HorizontalAlignment.Center;
        _searchMore.Click += async (_, _) => { _autoPageBlocked = false; await ViewModel.LoadMoreAsync(); };
        _searchMediaHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _searchMediaSection.Children.Add(_searchMediaHeading);
        _searchMediaSection.Children.Add(_searchRepeater);
        _searchMediaSection.Children.Add(_searchMore);
        _peopleMore.HorizontalAlignment = HorizontalAlignment.Center;
        _peopleMore.Click += async (_, _) => await ViewModel.LoadMoreSearchPeopleAsync();
        _search.Children.Add(_searchResults);
        _search.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Windows.System.VirtualKey.Tab) CancelSearchInputFocus();
        };
        _search.PointerPressed += (_, _) => CancelSearchInputFocus();
    }

    private void RequestSearchInputFocus()
    {
        if (_session is null || !_renderEnabled || !_renderLoaded || !ViewModel.IsSearch || Visibility != Visibility.Visible) return;
        _searchFocusRequestVersion++;
        _searchFocusSessionGeneration = _sessionGeneration;
        _searchFocusNavigationRevision = ViewModel.NavigationRevision;
        _searchFocusRenderEpoch = _renderEpoch;
        _searchFocusPending = true;
        _searchFocusQueued = false;
        QueueSearchInputFocus();
    }

    private void QueueSearchInputFocus()
    {
        if (!_searchFocusPending || _searchFocusQueued) return;
        if (!IsSearchFocusRequestCurrent()) { CancelSearchInputFocus(); return; }
        if (!IsSearchInputMounted()) return;
        var version = _searchFocusRequestVersion;
        _searchFocusQueued = true;
        // Rendering is queued at Low priority. Focus must follow attachment, template creation, and layout.
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (version != _searchFocusRequestVersion) return;
            _searchFocusQueued = false;
            if (!IsSearchFocusRequestCurrent()) { CancelSearchInputFocus(); return; }
            if (!IsSearchInputMounted()) return;
            _searchInput.ApplyTemplate();
            _searchInput.UpdateLayout();
            if (!IsSearchFocusRequestCurrent() || !IsSearchInputMounted()) { CancelSearchInputFocus(); return; }
            if (_searchInput.ActualWidth <= 0 || _searchInput.ActualHeight <= 0) return;
            var focused = _searchInput.Focus(FocusState.Keyboard);
            if (focused) _searchFocusPending = false;
            AcceptanceDiagnostics.Trace($"search-input-focus loaded={_searchInput.IsLoaded} mounted={IsSearchInputMounted()} acquired={focused} state={_searchInput.FocusState}");
        })) CancelSearchInputFocus();
    }

    private bool IsSearchFocusRequestCurrent() => _searchFocusPending && _session is not null && _renderEnabled && _renderLoaded
        && _searchFocusRenderEpoch == _renderEpoch && _searchFocusSessionGeneration == _sessionGeneration
        && _searchFocusNavigationRevision == ViewModel.NavigationRevision && ViewModel.IsSearch && Visibility == Visibility.Visible;

    private bool IsSearchInputMounted()
    {
        if (_renderedPage != "search" || !_page.Children.Contains(_search) || !_search.IsLoaded || !_searchInput.IsLoaded
            || _searchInput.XamlRoot is not { IsHostVisible: true } || !_searchInput.IsEnabled || _searchInput.IsReadOnly) return false;
        for (DependencyObject? current = _searchInput; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }

    private void CancelSearchInputFocus()
    {
        _searchFocusRequestVersion++;
        _searchFocusPending = false;
        _searchFocusQueued = false;
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
        if (!debounce && text.Trim().Length > 0) RememberSearchQuery(text);
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
            _searchTypeOperation = ViewModel.SetLumenSearchTypeAsync(tab switch { "movies" => "Movie", "series" => "Series", _ => null });
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
        if (_searchTab != "people") _searchTab = ViewModel.BrowseSearchType switch { "Movie" => "movies", "Series" => "series", _ => "all" };
        _searchClear.Visibility = _searchInput.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchTabs.Visibility = ViewModel.HasSearchQuery ? Visibility.Visible : Visibility.Collapsed;
        RenderSearchTabs();
        var mediaCount = SearchMediaCount(ViewModel.BrowseSearchType ?? "all", ViewModel.Items.Count);
        var peopleCount = SearchPeopleCount();
        var count = _searchTab == "people" ? peopleCount : _searchTab == "all" ? LumenSearchCount.Sum(mediaCount, peopleCount) : mediaCount;
        var server = _session?.Server.ServerName ?? "Emby";
        _searchHint.Text = LumenText.Get("Search in {0}", server) + (ViewModel.HasSearchQuery ? " \u00B7 " + LumenText.Get("{0} results", count.Format(CultureInfo.CurrentCulture)) : string.Empty);
        var candidates = _searchTab == "all" && ViewModel.BrowseSearchType != "BoxSet"
            ? ViewModel.Items.Where(item => item.Item.Type is "Movie" or "Series").ToArray() : ViewModel.Items.ToArray();
        var best = _searchTab == "all" ? candidates.OrderBy(SearchMatchRank).FirstOrDefault() : null;
        var media = _searchTab == "people" ? Array.Empty<MediaCardViewModel>() : candidates.Where(item => best is null || item.Id != best.Id).ToArray();
        ReconcileMedia(_searchMedia, media);
        for (var index = 0; index < _searchMedia.Count; index++)
            if (LumenMediaCard.UnwrapNavigationElement(_searchRepeater.TryGetElement(index)) is LumenMediaCard card) card.SetSearchContext(ViewModel.SearchText);
        if (_searchPeopleRenderQuery != ViewModel.SearchText)
        {
            _searchPeopleRenderQuery = ViewModel.SearchText;
            _searchPeopleRepeater.ItemTemplate = new SearchPersonFactory(this);
        }
        var signature = ViewModel.SearchText + ";" + _searchTab + ";" + string.Join("|", ViewModel.Items.Select(item => item.Id + ":" + item.Title))
            + ";" + string.Join("|", ViewModel.SearchPeople.Select(item => item.Id + ":" + item.Title)) + ";" + ViewModel.SearchPeopleError
            + ";" + ViewModel.IsBusy + ";" + ViewModel.HasPendingSearch + ";" + ViewModel.SearchPeopleIsBusy + ";" + ViewModel.ErrorMessage;
        if (_searchSummarySignature != signature)
        {
            _searchLayoutRevision++;
            _searchSummarySignature = signature;
            var keepMediaSection = ViewModel.HasSearchQuery && _searchTab != "people" && media.Length > 0;
            foreach (var child in _searchResults.Children.Where(child => !keepMediaSection || !ReferenceEquals(child, _searchMediaSection)).ToArray())
                _searchResults.Children.Remove(child);
            _searchFeatured.Children.Clear();
            _searchSecondary.Children.Clear();
            _searchPeopleSection = null;
            _searchPeoplePreviewCount = 0;
            if (!ViewModel.HasSearchQuery)
            {
                _searchResults.Children.Add(_searchDiscovery);
            }
            else if (_searchTab == "people")
            {
                if (ViewModel.SearchPeople.Count > 0) _searchResults.Children.Add(_searchPeopleRepeater);
                _searchResults.Children.Add(_peopleMore);
            }
            else
            {
                if (best is not null)
                {
                    _searchFeatured.Children.Add(BestMatch(best));
                    SetAmbience(best);
                }
                if (_searchTab == "all" && ViewModel.SearchPeople.Count > 0)
                {
                    _searchPeopleSection = SearchResultSection("People");
                    var people = new StackPanel { Spacing = 8 };
                    foreach (var person in ViewModel.SearchPeople.Take(4)) people.Children.Add(PersonResult(person));
                    _searchPeoplePreviewCount = people.Children.Count;
                    _searchPeopleSection.Children.Add(people);
                    LumenMediaCard.ConfigureNavigationGroup(people, initialNavigation: false);
                    _searchSecondary.Children.Add(_searchPeopleSection);
                    _searchFeatured.Children.Add(_searchSecondary);
                }
                if (_searchFeatured.Children.Count > 0) _searchResults.Children.Insert(0, _searchFeatured);
                if (media.Length > 0)
                {
                    _searchMediaHeading.Text = LumenText.Get(ViewModel.BrowseSearchType == "BoxSet" ? "Collections"
                        : _searchTab switch { "movies" => "Movies", "series" => "Series", _ => "Movies and series" });
                    if (!_searchResults.Children.Contains(_searchMediaSection)) _searchResults.Children.Add(_searchMediaSection);
                }
            }
            if (ViewModel.HasSearchQuery)
            {
                var hasResults = _searchTab == "people" ? ViewModel.SearchPeople.Count > 0
                    : candidates.Length > 0 || _searchTab == "all" && ViewModel.SearchPeople.Count > 0;
                var waiting = ViewModel.IsBusy || ViewModel.HasPendingSearch || (_searchTab is "all" or "people") && ViewModel.SearchPeopleIsBusy;
                if (!hasResults && waiting) _searchResults.Children.Add(_searchSkeletons);
                else if (!hasResults && ViewModel.SearchPeopleError.Length == 0) _searchResults.Children.Add(SearchEmptyState());
            }
            if (ViewModel.SearchPeopleError.Length > 0 && _searchTab is "all" or "people")
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
        if (!ViewModel.HasSearchQuery) RenderSearchDiscovery();
        _searchMore.Visibility = ViewModel.HasMore ? Visibility.Visible : Visibility.Collapsed;
        _searchMore.IsEnabled = ViewModel.CanLoadMore;
        _peopleMore.Visibility = ViewModel.SearchPeopleHasMore ? Visibility.Visible : Visibility.Collapsed;
        _peopleMore.IsEnabled = !ViewModel.SearchPeopleIsBusy;
        QueueSearchInputFocus();
    }

    private int SearchMatchRank(MediaCardViewModel item)
    {
        var query = ViewModel.SearchText;
        if (string.Equals(item.Title, query, StringComparison.CurrentCultureIgnoreCase)) return 0;
        if (item.Title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) return 1;
        if (item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return 2;
        if (item.Item.OriginalTitle?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true) return 3;
        if (item.Item.Genres?.Any(genre => genre.Contains(query, StringComparison.CurrentCultureIgnoreCase)) == true) return 4;
        return 5;
    }

    private FrameworkElement SearchEmptyState()
    {
        var content = new StackPanel { Margin = new Thickness(0, 90, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 620 };
        var icon = EmptyStateIcon("search", 80);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        if (icon.Child is FrameworkElement symbol) symbol.Opacity = .8;
        content.Children.Add(icon);
        var title = Label(ViewModel.HasError ? "No results" : LumenText.Get("No results for \u201c{0}\u201d", ViewModel.SearchText), 24, true);
        title.TextAlignment = TextAlignment.Center;
        title.Margin = new Thickness(0, 20, 0, 0);
        content.Children.Add(title);
        var message = Label(ViewModel.HasError ? ViewModel.ErrorMessage : "Try a person, an original title, or a genre.", 14);
        message.Foreground = LumenTheme.Brush("Sub");
        message.TextAlignment = TextAlignment.Center;
        message.Margin = new Thickness(0, 8, 0, 0);
        content.Children.Add(message);
        if (!ViewModel.HasError)
        {
            var genres = _searchDiscoveryGenres.Length > 0 ? _searchDiscoveryGenres : GetCachedDiscoveryGenres();
            var chips = new CatalogWrapPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 20, 0, 0) };
            foreach (var genre in genres.Take(5))
            {
                var chip = LumenUi.Button(genre.Name, height: 34);
                chip.Background = LumenTheme.Brush("Chip");
                chip.BorderBrush = LumenTheme.Brush("Line");
                chip.Padding = new Thickness(16, 0, 16, 0);
                var label = LumenUi.Text(genre.Name, 13);
                label.TextWrapping = TextWrapping.NoWrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.MaxWidth = 240;
                chip.Content = label;
                chip.Click += async (_, _) => await OpenDiscoveryGenreAsync(genre);
                ToolTipService.SetToolTip(chip, genre.Name);
                chips.Children.Add(chip);
            }
            if (chips.Children.Count > 0) content.Children.Add(chips);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        if (_searchTab != "all")
        {
            var all = LumenUi.Button("Search all types", height: 34);
            all.Click += async (_, _) => await SelectSearchTabAsync("all");
            actions.Children.Add(all);
        }
        var clear = LumenUi.Button(ViewModel.HasError ? "Retry" : "Clear search", height: 34);
        clear.Click += async (_, _) =>
        {
            if (ViewModel.HasError) await RunSearchAsync(false);
            else { _searchInput.Text = string.Empty; RequestSearchInputFocus(); }
        };
        actions.Children.Add(clear);
        content.Children.Add(actions);
        return content;
    }

    private sealed partial class SearchPersonFactory(LumenLibraryView owner) : IElementFactory
    {
        public UIElement GetElement(ElementFactoryGetArgs args) => args.Data is MediaCardViewModel person ? owner.PersonResult(person, true) : new Border();
        public void RecycleElement(ElementFactoryRecycleArgs args) { }
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
