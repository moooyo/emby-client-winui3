using EmbyClient.Api;
using EmbyClient.App.Services;
using EmbyClient.App.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;
using System.Globalization;
using Windows.System;
using Windows.UI.Core;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenLibraryView
{
    private readonly StackPanel _searchDiscovery = new() { Spacing = 30 };
    private readonly StackPanel _searchDiscoveryRecent = new() { Spacing = 12 };
    private readonly CatalogWrapPanel _searchDiscoveryRecentChips = new();
    private readonly StackPanel _searchDiscoveryGenreSection = new() { Spacing = 12 };
    private readonly CatalogWrapPanel _searchDiscoveryGenreChips = new();
    private readonly TextBlock _searchDiscoveryGenreStatus = LumenUi.Text(string.Empty, 13);
    private readonly Button _searchDiscoveryGenreRetry = LumenUi.Button("Retry", height: 32);
    private readonly StackPanel _searchDiscoveryRecommendationSection = new();
    private readonly Grid _searchDiscoveryRecommendationHeader = new();
    private readonly StackPanel _searchDiscoveryRecommendationRow = new() { Orientation = Orientation.Horizontal, Spacing = 16 };
    private readonly ScrollViewer _searchDiscoveryRecommendationScroller = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
        VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        IsTabStop = false
    };
    private readonly Button _searchDiscoveryPrevious = LumenUi.IconButton("chevron-left", "Previous recommendations", 32);
    private readonly Button _searchDiscoveryNext = LumenUi.IconButton("chevron-right", "Next recommendations", 32);
    private readonly StackPanel _searchDiscoveryRecommendationNotice = new() { Spacing = 10, MinHeight = 140 };
    private readonly TextBlock _searchDiscoveryRecommendationStatus = LumenUi.Text(string.Empty, 13);
    private readonly Button _searchDiscoveryRecommendationRetry = LumenUi.Button("Retry", height: 32);
    private readonly HashSet<LumenMediaCard> _searchDiscoveryCards = [];
    private SearchDiscoveryGenre[] _searchDiscoveryGenres = [];
    private MediaCardViewModel[] _searchDiscoveryRecommendations = [];
    private CancellationTokenSource? _searchDiscoveryCancellation;
    private int _searchDiscoveryGeneration = -1;
    private int _searchDiscoveryEpoch;
    private bool _searchDiscoveryBuilt;
    private bool _searchDiscoveryAttempted;
    private bool _searchDiscoveryLoading;
    private bool _searchDiscoveryGenresLoaded;
    private bool _searchDiscoveryRecommendationsLoaded;
    private string _searchDiscoveryGenreError = string.Empty;
    private string _searchDiscoveryRecommendationError = string.Empty;
    private string? _searchDiscoveryRecentSignature;
    private string? _searchDiscoveryGenreSignature;
    private string? _searchDiscoveryRecommendationSignature;

    private void RenderSearchDiscovery()
    {
        if (_searchDiscoveryGeneration != _sessionGeneration)
        {
            ResetSearchDiscovery();
            _searchDiscoveryGeneration = _sessionGeneration;
        }
        BuildSearchDiscovery();
        if (_searchDiscovery.Children.Count == 0)
        {
            _searchDiscovery.Children.Add(_searchDiscoveryRecent);
            _searchDiscovery.Children.Add(_searchDiscoveryGenreSection);
            _searchDiscovery.Children.Add(_searchDiscoveryRecommendationSection);
        }
        if (!_searchDiscoveryAttempted && _session is { } session)
        {
            if (_searchDiscoveryGenres.Length == 0) _searchDiscoveryGenres = GetCachedDiscoveryGenres();
            _searchDiscoveryAttempted = true;
            _searchDiscoveryLoading = true;
            var source = new CancellationTokenSource();
            _searchDiscoveryCancellation = source;
            _ = LoadSearchDiscoveryAsync(session, _sessionGeneration, _searchDiscoveryEpoch, source);
        }
        RenderDiscoveryRecentSearches();
        RenderDiscoveryGenres();
        RenderDiscoveryRecommendations();
        UpdateSearchDiscoveryLayout();
    }

    private void BuildSearchDiscovery()
    {
        if (_searchDiscoveryBuilt) return;
        _searchDiscoveryBuilt = true;
        RegisterSearchDiscoveryStrings();
        AutomationProperties.SetAutomationId(_searchDiscovery, "LumenSearchDiscovery");
        var recentHeader = new Grid();
        recentHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        recentHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        recentHeader.Children.Add(DiscoveryHeading("Recent searches"));
        var clear = LumenUi.Button("Clear recent searches", height: 30);
        var clearLabel = Label("Clear", 13);
        clearLabel.Foreground = LumenTheme.Brush("Sub");
        clear.Content = clearLabel;
        clear.Background = new SolidColorBrush(Colors.Transparent);
        clear.BorderThickness = new Thickness(0);
        clear.Padding = new Thickness(10, 0, 10, 0);
        clear.CornerRadius = new CornerRadius(8);
        clear.Foreground = LumenTheme.Brush("Sub");
        clear.Click += (_, _) =>
        {
            SetRecentSearchQueries([]);
            RequestSearchInputFocus();
        };
        Grid.SetColumn(clear, 1);
        recentHeader.Children.Add(clear);
        _searchDiscoveryRecent.Children.Add(recentHeader);
        _searchDiscoveryRecent.Children.Add(_searchDiscoveryRecentChips);

        _searchDiscoveryGenreSection.Children.Add(DiscoveryHeading("Browse by genre"));
        _searchDiscoveryGenreSection.Children.Add(_searchDiscoveryGenreChips);
        _searchDiscoveryGenreSection.Children.Add(_searchDiscoveryGenreStatus);
        _searchDiscoveryGenreRetry.HorizontalAlignment = HorizontalAlignment.Left;
        _searchDiscoveryGenreRetry.Click += (_, _) => RetrySearchDiscovery();
        _searchDiscoveryGenreSection.Children.Add(_searchDiscoveryGenreRetry);
        AutomationProperties.SetLiveSetting(_searchDiscoveryGenreStatus, AutomationLiveSetting.Polite);

        _searchDiscoveryRecommendationHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _searchDiscoveryRecommendationHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _searchDiscoveryRecommendationHeader.Children.Add(DiscoveryHeading("Highly rated unwatched titles"));
        var arrows = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { _searchDiscoveryPrevious, _searchDiscoveryNext })
        {
            button.BorderBrush = LumenTheme.Brush("Line");
            arrows.Children.Add(button);
        }
        _searchDiscoveryPrevious.Content = LumenUi.Icon("chevron-left", 14);
        _searchDiscoveryNext.Content = LumenUi.Icon("chevron-right", 14);
        AutomationProperties.SetName(_searchDiscoveryPrevious, LumenText.Get("Previous recommendations"));
        AutomationProperties.SetName(_searchDiscoveryNext, LumenText.Get("Next recommendations"));
        ToolTipService.SetToolTip(_searchDiscoveryPrevious, LumenText.Get("Previous recommendations"));
        ToolTipService.SetToolTip(_searchDiscoveryNext, LumenText.Get("Next recommendations"));
        Grid.SetColumn(arrows, 1);
        _searchDiscoveryRecommendationHeader.Children.Add(arrows);
        _searchDiscoveryRecommendationSection.Children.Add(_searchDiscoveryRecommendationHeader);
        _searchDiscoveryRecommendationScroller.Content = _searchDiscoveryRecommendationRow;
        _searchDiscoveryRecommendationSection.Children.Add(_searchDiscoveryRecommendationScroller);
        LumenMediaCard.ConfigureNavigationGroup(_searchDiscoveryRecommendationRow, true, initialNavigation: false);
        _searchDiscoveryPrevious.Click += (_, _) => ScrollDiscoveryRecommendations(-1);
        _searchDiscoveryNext.Click += (_, _) => ScrollDiscoveryRecommendations(1);
        _searchDiscoveryRecommendationScroller.ViewChanged += (_, _) => UpdateDiscoveryRecommendationArrows();
        _searchDiscoveryRecommendationScroller.SizeChanged += (_, _) => UpdateDiscoveryRecommendationArrows();
        _searchDiscoveryRecommendationRow.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(DiscoveryRecommendationWheel), true);
        _searchDiscoveryRecommendationScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(DiscoveryRecommendationWheel), true);
        AutomationProperties.SetAutomationId(_searchDiscoveryRecommendationScroller, "LumenSearchRecommendationScroller");
        _searchDiscoveryRecommendationNotice.Children.Add(_searchDiscoveryRecommendationStatus);
        _searchDiscoveryRecommendationRetry.HorizontalAlignment = HorizontalAlignment.Left;
        _searchDiscoveryRecommendationRetry.Click += (_, _) => RetrySearchDiscovery();
        _searchDiscoveryRecommendationNotice.Children.Add(_searchDiscoveryRecommendationRetry);
        _searchDiscoveryRecommendationSection.Children.Add(_searchDiscoveryRecommendationNotice);
        AutomationProperties.SetLiveSetting(_searchDiscoveryRecommendationStatus, AutomationLiveSetting.Polite);
    }

    private void RememberSearchQuery(string query)
    {
        query = query.Trim();
        if (query.Length is 0 or > 100) return;
        var recent = new[] { query }.Concat(_preferences.RecentSearches ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Where(value => value.Length <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        SetRecentSearchQueries(recent);
    }

    private void SetRecentSearchQueries(string[] queries)
    {
        queries = queries.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim())
            .Where(value => value.Length <= 100).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if ((_preferences.RecentSearches ?? []).SequenceEqual(queries, StringComparer.Ordinal)) return;
        _preferences = _preferences with { RecentSearches = queries };
        PreferencesChanged?.Invoke(this, _preferences);
        _searchDiscoveryRecentSignature = null;
        if (_searchDiscoveryBuilt) RenderDiscoveryRecentSearches();
    }

    private void RenderDiscoveryRecentSearches()
    {
        var queries = (_preferences.RecentSearches ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Where(value => value.Length <= 100)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        _searchDiscoveryRecent.Visibility = queries.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var signature = string.Join('\u001f', queries);
        if (_searchDiscoveryRecentSignature == signature) return;
        _searchDiscoveryRecentSignature = signature;
        _searchDiscoveryRecentChips.Children.Clear();
        foreach (var query in queries) _searchDiscoveryRecentChips.Children.Add(CreateRecentSearchChip(query));
    }

    private Border CreateRecentSearchChip(string query)
    {
        var chip = new Border
        {
            Height = 34, CornerRadius = new CornerRadius(17), BorderBrush = LumenTheme.Brush("Line"),
            BorderThickness = new Thickness(1), Background = LumenTheme.Brush("Chip")
        };
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var main = new Button
        {
            Height = 32, MinHeight = 32, MinWidth = 0, MaxWidth = 310,
            Padding = new Thickness(14, 0, 8, 0), BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent), CornerRadius = new CornerRadius(16, 0, 0, 16),
            UseSystemFocusVisuals = true
        };
        var label = LumenUi.Text(query, 13);
        label.MaxWidth = 248;
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var icon = LumenUi.Icon("history", 14);
        icon.Opacity = .7;
        row.Children.Add(icon);
        row.Children.Add(label);
        main.Content = row;
        main.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Colors.Transparent);
        main.Resources["ButtonBackgroundPressed"] = LumenTheme.Brush("ControlStrong");
        AutomationProperties.SetName(main, LumenText.Get("Search for {0}", query));
        ToolTipService.SetToolTip(main, query);
        main.Click += (_, _) =>
        {
            RememberSearchQuery(query);
            _searchInput.Text = query;
            RequestSearchInputFocus();
            _searchInput.SelectionStart = query.Length;
        };
        content.Children.Add(main);
        var remove = LumenUi.IconButton("x", LumenText.Get("Remove recent search {0}", query), 20);
        remove.Content = LumenUi.Icon("x", 10);
        remove.Margin = new Thickness(0, 0, 8, 0);
        remove.Background = new SolidColorBrush(Colors.Transparent);
        remove.BorderThickness = new Thickness(0);
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.Click += (_, _) =>
        {
            var keepFocus = remove.FocusState != FocusState.Unfocused;
            var index = _searchDiscoveryRecentChips.Children.IndexOf(chip);
            SetRecentSearchQueries((_preferences.RecentSearches ?? [])
                .Where(value => !string.Equals(value?.Trim(), query, StringComparison.OrdinalIgnoreCase)).Take(8).ToArray());
            if (!keepFocus) return;
            if (_searchDiscoveryRecentChips.Children.Count == 0) RequestSearchInputFocus();
            else FirstButton(_searchDiscoveryRecentChips.Children[Math.Clamp(index, 0, _searchDiscoveryRecentChips.Children.Count - 1)])?.Focus(FocusState.Programmatic);
        };
        Grid.SetColumn(remove, 1);
        content.Children.Add(remove);
        chip.Child = content;
        chip.PointerEntered += (_, _) => chip.Background = LumenTheme.Brush("ControlStrong");
        chip.PointerExited += (_, _) => chip.Background = LumenTheme.Brush("Chip");
        return chip;
    }

    private void RenderDiscoveryGenres()
    {
        var signature = string.Join('\u001f', _searchDiscoveryGenres.Select(genre => genre.Name + ":" + genre.MediaType));
        if (_searchDiscoveryGenreSignature != signature)
        {
            _searchDiscoveryGenreSignature = signature;
            _searchDiscoveryGenreChips.Children.Clear();
            foreach (var genre in _searchDiscoveryGenres)
            {
                var button = LumenUi.Button(genre.Name, height: 34);
                button.Background = LumenTheme.Brush("Chip");
                button.BorderBrush = LumenTheme.Brush("Line");
                button.Padding = new Thickness(16, 0, 16, 0);
                var label = LumenUi.Text(genre.Name, 13);
                label.TextWrapping = TextWrapping.NoWrap;
                label.TextTrimming = TextTrimming.CharacterEllipsis;
                label.MaxWidth = 240;
                button.Content = label;
                ToolTipService.SetToolTip(button, genre.Name);
                button.Click += async (_, _) => await OpenDiscoveryGenreAsync(genre);
                _searchDiscoveryGenreChips.Children.Add(button);
            }
        }
        var hasError = _searchDiscoveryGenreError.Length > 0;
        _searchDiscoveryGenreStatus.Text = LumenText.Get(hasError ? _searchDiscoveryGenreError
            : _searchDiscoveryGenresLoaded ? "This server has not provided any genres." : "Loading genres...");
        _searchDiscoveryGenreStatus.Foreground = LumenTheme.Brush(hasError ? "Danger" : "Muted");
        _searchDiscoveryGenreStatus.Visibility = hasError || _searchDiscoveryGenres.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDiscoveryGenreRetry.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        _searchDiscoveryGenreRetry.IsEnabled = !_searchDiscoveryLoading;
    }

    private async Task OpenDiscoveryGenreAsync(SearchDiscoveryGenre genre)
    {
        var session = _session;
        var generation = _sessionGeneration;
        if (session is null) return;
        var destination = genre.MediaType == "Series" ? "series" : "movies";
        await NavigateAsync(destination);
        if (generation != _sessionGeneration || !ReferenceEquals(session, _session) || ActiveNavigation != destination) return;
        await ViewModel.SetLumenBrowseFiltersAsync(genre.Name, WatchStatusFilter.All);
    }

    private void RenderDiscoveryRecommendations()
    {
        var items = _searchDiscoveryRecommendations.Where(item => !item.IsPlayed).ToArray();
        var signature = string.Join('\u001f', items.Select(item => item.Id + ":" + item.Title + ":" + item.Item.CommunityRating + ":" + item.Item.ProductionYear));
        if (_searchDiscoveryRecommendationSignature != signature)
        {
            _searchDiscoveryRecommendationSignature = signature;
            var offset = _searchDiscoveryRecommendationScroller.HorizontalOffset;
            _searchDiscoveryRecommendationRow.Children.Clear();
            _searchDiscoveryCards.Clear();
            foreach (var item in items)
            {
                var card = MakeCard(item, width: 150);
                card.SetSearchPosterStyle(discovery: true);
                var rating = item.Item.CommunityRating is { } communityRating ? communityRating.ToString("0.0", CultureInfo.CurrentCulture) : null;
                card.SetSubtitleOverride(string.Join(" \u00b7 ", new[] { rating, item.Item.ProductionYear?.ToString(CultureInfo.InvariantCulture) }
                    .Where(value => !string.IsNullOrWhiteSpace(value))));
                _searchDiscoveryCards.Add(card);
                _searchDiscoveryRecommendationRow.Children.Add(card);
            }
            if (offset > 0) _searchDiscoveryRecommendationScroller.ChangeView(offset, null, null, true);
            if (items.FirstOrDefault() is { } first && _renderedPage == "search") SetAmbience(first);
        }
        var hasError = _searchDiscoveryRecommendationError.Length > 0;
        _searchDiscoveryRecommendationScroller.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDiscoveryRecommendationNotice.Visibility = hasError || items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDiscoveryRecommendationStatus.Text = LumenText.Get(hasError ? _searchDiscoveryRecommendationError
            : _searchDiscoveryRecommendationsLoaded ? "No unwatched recommendations are available." : "Loading recommendations...");
        _searchDiscoveryRecommendationStatus.Foreground = LumenTheme.Brush(hasError ? "Danger" : "Muted");
        _searchDiscoveryRecommendationRetry.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        _searchDiscoveryRecommendationRetry.IsEnabled = !_searchDiscoveryLoading;
        UpdateDiscoveryRecommendationArrows();
    }

    private async Task LoadSearchDiscoveryAsync(ConnectedSession session, int generation, int epoch, CancellationTokenSource source)
    {
        var token = source.Token;
        try
        {
            await Task.WhenAll(
                _searchDiscoveryGenresLoaded ? Task.CompletedTask : LoadDiscoveryGenresAsync(session, generation, epoch, token),
                _searchDiscoveryRecommendationsLoaded ? Task.CompletedTask : LoadDiscoveryRecommendationsAsync(session, generation, epoch, token));
        }
        finally
        {
            if (IsCurrentDiscoveryRequest(session, generation, epoch, token))
            {
                _searchDiscoveryLoading = false;
                _searchDiscoveryCancellation = null;
                RefreshSearchDiscoveryIfVisible();
            }
            source.Dispose();
        }
    }

    private async Task LoadDiscoveryGenresAsync(ConnectedSession session, int generation, int epoch, CancellationToken token)
    {
        _searchDiscoveryGenreError = string.Empty;
        try
        {
            var genres = new Dictionary<string, SearchDiscoveryGenre>(StringComparer.OrdinalIgnoreCase);
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (true)
            {
                var page = await session.Api.GetGenresAsync(new ItemQuery
                {
                    IncludeItemTypes = ["Movie", "Series"], Recursive = true, Fields = ["ItemCounts"],
                    StartIndex = offset, Limit = 200, EnableImages = false, EnableUserData = false
                }, token);
                if (!IsCurrentDiscoveryRequest(session, generation, epoch, token)) return;
                var previousCount = seenNames.Count;
                foreach (var item in page.Items)
                {
                    if (string.IsNullOrWhiteSpace(item.Name)) continue;
                    seenNames.Add(item.Name.Trim());
                    var count = DiscoveryGenreCount(item);
                    if (count == 0) continue;
                    var mediaType = item.MovieCount is > 0 ? "Movie"
                        : item.SeriesCount is > 0 ? "Series" : PreferredDiscoveryMediaType();
                    var genre = new SearchDiscoveryGenre(item.Name.Trim(), mediaType, count);
                    if (!genres.TryGetValue(genre.Name, out var previous) || genre.Count.GetValueOrDefault() > previous.Count.GetValueOrDefault())
                        genres[genre.Name] = genre;
                }
                offset += page.Items.Length;
                if (page.Items.Length == 0 || seenNames.Count == previousCount || page.TotalRecordCount is { } total && offset >= total) break;
            }
            if (!IsCurrentDiscoveryRequest(session, generation, epoch, token)) return;
            _searchDiscoveryGenres = genres.Values.OrderByDescending(genre => genre.Count ?? -1)
                .ThenBy(genre => genre.Name, StringComparer.CurrentCultureIgnoreCase).Take(8).ToArray();
            _searchDiscoveryGenresLoaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException or ObjectDisposedException)
        {
            if (!IsCurrentDiscoveryRequest(session, generation, epoch, token)) return;
            _searchDiscoveryGenreError = "Genres could not be loaded. Try again.";
            if (exception is EmbyApiException { IsAuthenticationFailure: true }) SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        if (IsCurrentDiscoveryRequest(session, generation, epoch, token)) RefreshSearchDiscoveryIfVisible();
    }

    private async Task LoadDiscoveryRecommendationsAsync(ConnectedSession session, int generation, int epoch, CancellationToken token)
    {
        _searchDiscoveryRecommendationError = string.Empty;
        try
        {
            var result = await session.Api.GetItemsAsync(new ItemQuery
            {
                IncludeItemTypes = ["Movie", "Series"], Recursive = true, IsPlayed = false,
                SortBy = ["CommunityRating", "SortName"], SortOrder = ["Descending", "Ascending"],
                StartIndex = 0, Limit = 12, Fields = ["PrimaryImageAspectRatio", "Genres"],
                EnableImageTypes = ["Primary"], ImageTypeLimit = 1
            }, token);
            if (!IsCurrentDiscoveryRequest(session, generation, epoch, token)) return;
            foreach (var item in _searchDiscoveryRecommendations) item.PropertyChanged -= DiscoveryRecommendedItemChanged;
            _searchDiscoveryRecommendations = result.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id)
                    && (item.Type is "Movie" or "Series") && item.UserData?.Played != true)
                .GroupBy(item => item.Id, StringComparer.Ordinal).Select(group => new MediaCardViewModel(group.First())).Take(12).ToArray();
            foreach (var item in _searchDiscoveryRecommendations) item.PropertyChanged += DiscoveryRecommendedItemChanged;
            _searchDiscoveryRecommendationsLoaded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException or ObjectDisposedException)
        {
            if (!IsCurrentDiscoveryRequest(session, generation, epoch, token)) return;
            _searchDiscoveryRecommendationError = "Recommendations could not be loaded. Try again.";
            if (exception is EmbyApiException { IsAuthenticationFailure: true }) SessionExpired?.Invoke(this, EventArgs.Empty);
        }
        if (IsCurrentDiscoveryRequest(session, generation, epoch, token)) RefreshSearchDiscoveryIfVisible();
    }

    private bool IsCurrentDiscoveryRequest(ConnectedSession session, int generation, int epoch, CancellationToken token) =>
        !token.IsCancellationRequested && generation == _sessionGeneration && epoch == _searchDiscoveryEpoch && ReferenceEquals(session, _session);

    private void RefreshSearchDiscoveryIfVisible()
    {
        if (_renderEnabled && _renderLoaded && ViewModel.IsSearch && string.IsNullOrWhiteSpace(_searchInput.Text)) RenderSearchDiscovery();
    }

    private void RetrySearchDiscovery()
    {
        if (_searchDiscoveryLoading) return;
        _searchDiscoveryAttempted = false;
        RenderSearchDiscovery();
    }

    private void ResetSearchDiscovery()
    {
        _searchDiscoveryEpoch++;
        var source = _searchDiscoveryCancellation;
        _searchDiscoveryCancellation = null;
        source?.Cancel();
        _searchDiscoveryGeneration = -1;
        _searchDiscoveryAttempted = false;
        _searchDiscoveryLoading = false;
        _searchDiscoveryGenresLoaded = false;
        _searchDiscoveryRecommendationsLoaded = false;
        _searchDiscoveryGenreError = _searchDiscoveryRecommendationError = string.Empty;
        _searchDiscoveryRecentSignature = _searchDiscoveryGenreSignature = _searchDiscoveryRecommendationSignature = null;
        foreach (var item in _searchDiscoveryRecommendations) item.PropertyChanged -= DiscoveryRecommendedItemChanged;
        _searchDiscoveryGenres = [];
        _searchDiscoveryRecommendations = [];
        _searchDiscoveryRecentChips.Children.Clear();
        _searchDiscoveryGenreChips.Children.Clear();
        _searchDiscoveryRecommendationRow.Children.Clear();
        _searchDiscoveryCards.Clear();
        _searchDiscovery.Children.Clear();
    }

    private void DiscoveryRecommendedItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MediaCardViewModel.Item) or nameof(MediaCardViewModel.IsPlayed))) return;
        RefreshSearchDiscoveryIfVisible();
    }

    private SearchDiscoveryGenre[] GetCachedDiscoveryGenres() => ViewModel.HomeRows.SelectMany(row => row.Items)
        .Where(item => item.Item.Type is "Movie" or "Series")
        .SelectMany(item => (item.Item.Genres ?? []).Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => new SearchDiscoveryGenre(name.Trim(), item.Item.Type!, null)))
        .GroupBy(genre => genre.Name, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.FirstOrDefault(genre => genre.MediaType == "Movie") ?? group.First())
        .OrderBy(genre => genre.Name, StringComparer.CurrentCultureIgnoreCase).Take(8).ToArray();

    private string PreferredDiscoveryMediaType() => ViewModel.Libraries.Any(library => string.Equals(library.Item.CollectionType, "tvshows", StringComparison.OrdinalIgnoreCase))
        && !ViewModel.Libraries.Any(library => string.Equals(library.Item.CollectionType, "movies", StringComparison.OrdinalIgnoreCase)) ? "Series" : "Movie";

    private static int? DiscoveryGenreCount(BaseItemDto genre)
    {
        if (genre.MovieCount is >= 0 && genre.SeriesCount is >= 0)
            return (int)Math.Min(int.MaxValue, (long)genre.MovieCount.Value + genre.SeriesCount.Value);
        if (genre.RecursiveItemCount is >= 0) return genre.RecursiveItemCount;
        if (genre.ChildCount is >= 0) return genre.ChildCount;
        return genre.MovieCount is > 0 ? genre.MovieCount : genre.SeriesCount is > 0 ? genre.SeriesCount : null;
    }

    private void UpdateSearchDiscoveryLayout()
    {
        _searchDiscovery.Margin = new Thickness(0, Math.Max(0, 30 - _search.Spacing), 0, 0);
        _searchDiscoveryRecommendationSection.Margin = new Thickness(-_pageMargin, 0, -_pageMargin, 0);
        _searchDiscoveryRecommendationHeader.Margin = new Thickness(_pageMargin, 0, _pageMargin, 0);
        _searchDiscoveryRecommendationScroller.Padding = new Thickness(_pageMargin, 12, _pageMargin, 10);
        _searchDiscoveryRecommendationNotice.Margin = new Thickness(_pageMargin, 24, _pageMargin, 0);
        foreach (var card in _searchDiscoveryCards) if (card.Width != 150) card.SetCardWidth(150);
        UpdateDiscoveryRecommendationArrows();
    }

    private void ScrollDiscoveryRecommendations(int direction)
    {
        var scroller = _searchDiscoveryRecommendationScroller;
        var offset = Math.Clamp(scroller.HorizontalOffset + direction * Math.Max(166, scroller.ViewportWidth * .8), 0, scroller.ScrollableWidth);
        scroller.ChangeView(offset, null, null, !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled);
    }

    private void UpdateDiscoveryRecommendationArrows()
    {
        var scroller = _searchDiscoveryRecommendationScroller;
        var available = _searchDiscoveryRecommendationRow.Children.Count > 0 && scroller.Visibility == Visibility.Visible;
        _searchDiscoveryPrevious.IsEnabled = available && scroller.HorizontalOffset > 1;
        _searchDiscoveryNext.IsEnabled = available && scroller.ScrollableWidth - scroller.HorizontalOffset > 1;
        _searchDiscoveryPrevious.Opacity = _searchDiscoveryPrevious.IsEnabled ? 1 : .25;
        _searchDiscoveryNext.Opacity = _searchDiscoveryNext.IsEnabled ? 1 : .25;
    }

    private void DiscoveryRecommendationWheel(object sender, PointerRoutedEventArgs args)
    {
        if (args.Handled) return;
        var properties = args.GetCurrentPoint(_searchDiscoveryRecommendationScroller).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0) return;
        var shift = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0;
        if (shift)
        {
            var scroller = _searchDiscoveryRecommendationScroller;
            scroller.ChangeView(Math.Clamp(scroller.HorizontalOffset - properties.MouseWheelDelta, 0, scroller.ScrollableWidth), null, null, true);
        }
        else
        {
            // Route ordinary vertical wheel gestures to the page before a horizontal viewport can consume them.
            _scroll.ChangeView(null, Math.Clamp(_scroll.VerticalOffset - properties.MouseWheelDelta, 0, _scroll.ScrollableHeight), null, true);
        }
        args.Handled = true;
    }

    private static TextBlock DiscoveryHeading(string key)
    {
        var heading = Label(key, 16);
        heading.FontWeight = FontWeights.SemiBold;
        heading.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        return heading;
    }

    private static void RegisterSearchDiscoveryStrings() => LumenText.Register(new Dictionary<string, string>
    {
        ["Recent searches"] = "\u6700\u8fd1\u641c\u7d22", ["Clear"] = "\u6e05\u9664", ["Clear recent searches"] = "\u6e05\u9664\u6700\u8fd1\u641c\u7d22",
        ["Browse by genre"] = "\u6309\u7c7b\u578b\u6d4f\u89c8", ["Highly rated unwatched titles"] = "\u8fd8\u6ca1\u770b\u8fc7\u7684\u9ad8\u5206\u4f5c\u54c1",
        ["Previous recommendations"] = "\u4e0a\u4e00\u7ec4\u63a8\u8350", ["Next recommendations"] = "\u4e0b\u4e00\u7ec4\u63a8\u8350",
        ["Search for {0}"] = "\u641c\u7d22 {0}", ["Remove recent search {0}"] = "\u79fb\u9664\u6700\u8fd1\u641c\u7d22 {0}",
        ["Loading genres..."] = "\u6b63\u5728\u52a0\u8f7d\u7c7b\u578b...", ["Loading recommendations..."] = "\u6b63\u5728\u52a0\u8f7d\u63a8\u8350...",
        ["This server has not provided any genres."] = "\u670d\u52a1\u5668\u6682\u672a\u63d0\u4f9b\u7c7b\u578b\u3002",
        ["No unwatched recommendations are available."] = "\u6682\u65e0\u672a\u770b\u63a8\u8350\u4f5c\u54c1\u3002",
        ["Genres could not be loaded. Try again."] = "\u7c7b\u578b\u672a\u80fd\u52a0\u8f7d\uff0c\u8bf7\u91cd\u8bd5\u3002",
        ["Recommendations could not be loaded. Try again."] = "\u63a8\u8350\u672a\u80fd\u52a0\u8f7d\uff0c\u8bf7\u91cd\u8bd5\u3002"
    });

    private sealed record SearchDiscoveryGenre(string Name, string MediaType, int? Count);
}
