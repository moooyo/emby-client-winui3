using EmbyClient.Api;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private static readonly string[] LumenMovieTypes = ["Movie"];
    private static readonly string[] LumenSeriesTypes = ["Series"];
    private static readonly string[] LumenBoxSetTypes = ["BoxSet"];
    private static readonly string[] LumenAllSearchTypes = ["Movie", "Series", "BoxSet"];
    private const int GenrePageSize = 500;
    private int? _totalItemsCount;
    private string[] _availableGenres = [];
    private string? _lumenGenresContext;
    private bool _lumenGenresLoaded;
    private CancellationTokenSource? _lumenGenresCancellation;
    private CancellationTokenSource? _searchPeopleCancellation;
    private int _searchPeopleRequestVersion;
    private int _searchPeopleNextIndex;
    private bool _searchPeopleRequested;
    private bool _searchPeopleNeedsFirstPage;
    private bool _searchPeopleHasMore;
    private bool _searchPeopleIsBusy;
    private int? _searchPeopleTotal;
    private string _searchPeopleError = string.Empty;

    public string? BrowseGenre => (_pendingBrowseLocation ?? _location).Genre;
    public string? BrowseNameStartsWith => (_pendingBrowseLocation ?? _location).NameStartsWith;
    public string? BrowseMediaType => !IsSearch && (_pendingBrowseLocation ?? _location).ItemTypes is [var type] ? type : null;
    public string? BrowseSearchType => IsSearch && (_pendingBrowseLocation ?? _location).ItemTypes is [var type] ? type : null;
    public IReadOnlyList<string> AvailableGenres => _availableGenres;
    public ObservableCollection<MediaCardViewModel> SearchPeople { get; } = [];

    public int? TotalItemsCount
    {
        get => _totalItemsCount;
        private set
        {
            if (SetProperty(ref _totalItemsCount, value)) OnPropertyChanged(nameof(QueryCount));
        }
    }

    public int? QueryCount => TotalItemsCount;
    public bool SearchPeopleHasMore { get => _searchPeopleHasMore; private set => SetProperty(ref _searchPeopleHasMore, value); }
    public bool SearchPeopleIsBusy { get => _searchPeopleIsBusy; private set => SetProperty(ref _searchPeopleIsBusy, value); }
    public int? SearchPeopleTotal { get => _searchPeopleTotal; private set => SetProperty(ref _searchPeopleTotal, value); }
    public string SearchPeopleError { get => _searchPeopleError; private set => SetProperty(ref _searchPeopleError, value); }

    public Task ShowMediaTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        var itemTypes = type switch
        {
            "Movie" => LumenMovieTypes,
            "Series" => LumenSeriesTypes,
            _ => throw new ArgumentException("The media type must be Movie or Series.", nameof(type))
        };
        CancelSearch();
        if (_location.Kind == LocationKind.Library && _location.ItemId is null
            && _location.ItemTypes is [var currentType] && currentType == type
            && _loadOutcome == PageLoadOutcome.Succeeded) return Task.CompletedTask;
        return NavigateAsync(new(LocationKind.Library, null, type == "Movie" ? "Movies" : "TV shows", ItemTypes: itemTypes),
            true, cancellationToken);
    }

    public Task SetLumenBrowseFiltersAsync(string? genre, WatchStatusFilter filter, string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        if (_location.Kind is not (LocationKind.Library or LocationKind.Favorites or LocationKind.Search or LocationKind.Latest))
            return Task.CompletedTask;
        var location = (_pendingBrowseLocation ?? _location) with
        {
            Genre = string.IsNullOrWhiteSpace(genre) ? null : genre.Trim(),
            NameStartsWith = string.IsNullOrWhiteSpace(prefix) ? null : prefix.Trim(),
            WatchFilter = filter is WatchStatusFilter.Unplayed or WatchStatusFilter.Played ? filter : WatchStatusFilter.All
        };
        return SetLumenBrowseLocationAsync(location, cancellationToken);
    }

    public Task SetLumenSearchTypeAsync(string? type, CancellationToken cancellationToken = default)
    {
        if (!IsSearch) return Task.CompletedTask;
        var itemTypes = type switch
        {
            null or "" => LumenAllSearchTypes,
            "Movie" => LumenMovieTypes,
            "Series" => LumenSeriesTypes,
            "BoxSet" => LumenBoxSetTypes,
            _ => throw new ArgumentException("The search type must be Movie, Series, BoxSet, or null.", nameof(type))
        };
        return SetLumenBrowseLocationAsync((_pendingBrowseLocation ?? _location) with { ItemTypes = itemTypes }, cancellationToken);
    }

    public Task SearchLumenAsync(string text, bool debounce = true, CancellationToken cancellationToken = default) =>
        SearchLumenCoreAsync(text, debounce, true, cancellationToken);

    public Task ToggleItemFavoriteAsync(MediaCardViewModel item, CancellationToken cancellationToken = default) =>
        MutateLumenUserDataAsync(item, true, cancellationToken);

    public Task ToggleItemPlayedAsync(MediaCardViewModel item, CancellationToken cancellationToken = default) =>
        MutateLumenUserDataAsync(item, false, cancellationToken);

    public Task ShowPersonAsync(MediaCardViewModel person, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(person);
        if (string.IsNullOrWhiteSpace(person.Id) || person.Item.Type != "Person") return Task.CompletedTask;
        string? primaryImageTag = null;
        person.Item.ImageTags?.TryGetValue("Primary", out primaryImageTag);
        var card = PersonCardViewModel.FromItem(new BaseItemDto
        {
            People = [new PersonInfo { Id = person.Id, Name = person.Title, PrimaryImageTag = primaryImageTag }]
        }).FirstOrDefault();
        return card is null ? Task.CompletedTask : ShowPersonAsync(card, cancellationToken);
    }

    private async Task SetLumenBrowseLocationAsync(BrowseLocation location, CancellationToken cancellationToken)
    {
        if (_api is null || _sessionCancellation is null || location == (_pendingBrowseLocation ?? _location)) return;
        if (location.Kind == LocationKind.Search && string.IsNullOrWhiteSpace(location.SearchTerm))
        {
            _location = location;
            SetLumenGenreContext(location);
            NotifyBrowseOptions();
            return;
        }
        var restartPeople = IsSearch && _searchPeopleRequested && _searchPeopleNeedsFirstPage;
        var sessionVersion = _sessionVersion;
        var version = ++_pageVersion;
        var userDataRevision = _userDataRevision;
        CancelSearch();
        CancelAndDispose(ref _pageCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _pageCancellation = source;
        var token = source.Token;
        var api = _api;
        _pendingBrowseLocation = location;
        _isLoadingMore = false;
        IsBusy = true;
        HasError = false;
        NotifyBrowseOptions();
        try
        {
            var result = await QueryItemsAsync(api, location, 0, token);
            if (!CanCommitPage(version, token) || HasPendingSearch) return;

            // Preserve the committed query and loaded window until the replacement page is available.
            _location = location;
            _pendingBrowseLocation = null;
            SetLumenGenreContext(location);
            ReconcileItems(PreserveNewerUserData(result.Items, userDataRevision));
            _nextIndex = result.Items.Length;
            HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total
                ? _nextIndex < total : result.Items.Length == PageSize);
            UpdateCount(result.TotalRecordCount);
            SetLoadOutcome(PageLoadOutcome.Succeeded);
            NotifyBrowseOptions();
            NotifyCollectionCommitted();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(version, token) && !HasPendingSearch) ShowError(exception);
        }
        finally
        {
            if (version == _pageVersion)
            {
                _pendingBrowseLocation = null;
                RestorePageLifetime(version, source);
                NotifyBrowseOptions();
                IsBusy = false;
            }
        }
        if (restartPeople && CanCommitPage(version, token) && !HasPendingSearch && IsSearch)
            await LoadLumenSearchPeoplePageAsync(api, _location.SearchTerm!, 0, sessionVersion, version, null, cancellationToken);
    }

    private async Task SearchLumenCoreAsync(string text, bool debounce, bool includePeople, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        CancelSearch();
        ResetLumenSearchPeople();
        if (_sessionCancellation is null || _api is null) return;
        var sessionVersion = _sessionVersion;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        var token = source.Token;
        _searchCancellation = source;
        _pendingSearchCancellation = source;
        try
        {
            if (debounce) await Task.Delay(350, token);
            token.ThrowIfCancellationRequested();
            var term = text.Trim();
            if (!IsSearch) _searchOriginSnapshot = CaptureBrowseState();
            var itemTypes = includePeople
                ? IsSearch && _location.ItemTypes is [var selected] && selected is "Movie" or "Series" or "BoxSet"
                    ? _location.ItemTypes : LumenAllSearchTypes
                : null;
            var destination = IsSearch
                ? _location with { Title = "Search", SearchTerm = term, ItemTypes = itemTypes }
                : new BrowseLocation(LocationKind.Search, null, "Search", SearchTerm: term, SearchOrigin: _location, ItemTypes: itemTypes);
            var mediaTask = NavigateAsync(destination, false, token);
            var pageVersion = _pageVersion;
            var peopleTask = includePeople && term.Length > 0 && IsCurrentSession(sessionVersion)
                && IsSearch && _location.SearchTerm == term
                ? LoadLumenSearchPeoplePageAsync(_api!, term, 0, sessionVersion, pageVersion, source, token)
                : Task.CompletedTask;
            try { await mediaTask; }
            finally
            {
                // A slow people query must not keep completed media paging in a pending-search state.
                if (ReferenceEquals(_pendingSearchCancellation, source)) _pendingSearchCancellation = null;
            }
            await peopleTask;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pendingSearchCancellation, source)) _pendingSearchCancellation = null;
        }
    }

    public Task LoadMoreSearchPeopleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSearch || !HasSearchQuery || !_searchPeopleRequested || SearchPeopleIsBusy || IsBusy || HasPendingSearch
            || !SearchPeopleHasMore && !_searchPeopleNeedsFirstPage || _api is null || _pageCancellation is null)
            return Task.CompletedTask;
        return LoadLumenSearchPeoplePageAsync(_api, _location.SearchTerm!, _searchPeopleNextIndex,
            _sessionVersion, _pageVersion, null, cancellationToken);
    }

    private async Task LoadLumenSearchPeoplePageAsync(EmbyApiClient api, string term, int offset,
        int sessionVersion, int pageVersion, CancellationTokenSource? searchOwner, CancellationToken cancellationToken)
    {
        if (!IsCurrentSession(sessionVersion) || !IsCurrentPage(pageVersion) || _pageCancellation is null
            || !IsSearch || _location.SearchTerm != term || cancellationToken.IsCancellationRequested) return;
        CancelAndDispose(ref _searchPeopleCancellation);
        var requestVersion = ++_searchPeopleRequestVersion;
        var source = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token, cancellationToken);
        _searchPeopleCancellation = source;
        var token = source.Token;
        var userDataRevision = _userDataRevision;
        _searchPeopleRequested = true;
        if (offset == 0) _searchPeopleNeedsFirstPage = true;
        SearchPeopleIsBusy = true;
        SearchPeopleError = string.Empty;
        try
        {
            var result = await QueryLumenPeopleAsync(api, term, offset, token);
            if (!CanCommitLumenPeople(sessionVersion, pageVersion, requestVersion, term, token, source, searchOwner)) return;
            var items = PreserveNewerUserData(result.Items.Select(item => item with { Type = item.Type ?? "Person" }), userDataRevision);
            _searchPeopleNextIndex = offset + result.Items.Length;
            _searchPeopleNeedsFirstPage = false;
            if (offset == 0) ReconcileCards(SearchPeople, items);
            else
            {
                var ids = SearchPeople.Select(card => card.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var item in items)
                    if (!string.IsNullOrWhiteSpace(item.Id) && ids.Add(item.Id)) SearchPeople.Add(new(item));
            }
            SearchPeopleTotal = result.TotalRecordCount is >= 0 ? result.TotalRecordCount : null;
            SearchPeopleHasMore = result.Items.Length > 0 && (SearchPeopleTotal is { } total
                ? _searchPeopleNextIndex < total : result.Items.Length == PageSize);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (!CanCommitLumenPeople(sessionVersion, pageVersion, requestVersion, term, token, source, searchOwner)) return;
            SearchPeopleError = "People could not be loaded. Try searching again.";
            if (IsSessionExpired(exception)) ShowError(exception);
        }
        finally
        {
            if (IsCurrentSession(sessionVersion) && pageVersion == _pageVersion
                && requestVersion == _searchPeopleRequestVersion && ReferenceEquals(_searchPeopleCancellation, source))
                SearchPeopleIsBusy = false;
        }
    }

    private bool CanCommitLumenPeople(int sessionVersion, int pageVersion, int requestVersion, string term,
        CancellationToken cancellationToken, CancellationTokenSource source, CancellationTokenSource? searchOwner) =>
        IsCurrentSession(sessionVersion) && CanCommitNavigation(pageVersion, cancellationToken, searchOwner)
        && requestVersion == _searchPeopleRequestVersion && ReferenceEquals(_searchPeopleCancellation, source)
        && IsSearch && _location.SearchTerm == term;

    private static Task<QueryResult<BaseItemDto>> QueryLumenPeopleAsync(EmbyApiClient api, string term, int offset,
        CancellationToken cancellationToken) => api.GetPersonsAsync(new ItemQuery
        {
            SearchTerm = term, StartIndex = offset, Limit = PageSize, Fields = ListFields,
            SortBy = ["SortName"], SortOrder = ["Ascending"], EnableImageTypes = ["Primary"], ImageTypeLimit = 1
        }, cancellationToken);

    private async Task<Action?> PrepareLumenSearchPeopleRefreshAsync(EmbyApiClient api, BrowseLocation location,
        long userDataRevision, CancellationToken cancellationToken)
    {
        if (location.Kind != LocationKind.Search || string.IsNullOrWhiteSpace(location.SearchTerm) || !_searchPeopleRequested)
            return null;
        var loadedCount = _searchPeopleNextIndex;
        var items = new List<BaseItemDto>();
        var offset = 0;
        int? totalCount;
        bool hasMore;
        SearchPeopleIsBusy = true;
        try
        {
            do
            {
                var result = await QueryLumenPeopleAsync(api, location.SearchTerm, offset, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                items.AddRange(result.Items.Select(item => item with { Type = item.Type ?? "Person" }));
                offset += result.Items.Length;
                totalCount = result.TotalRecordCount is >= 0 ? result.TotalRecordCount : null;
                hasMore = result.Items.Length > 0 && (totalCount is { } total ? offset < total : result.Items.Length == PageSize);
            } while (hasMore && offset < Math.Max(PageSize, loadedCount));
            return () =>
            {
                ReconcileCards(SearchPeople, PreserveNewerUserData(items, userDataRevision));
                _searchPeopleNextIndex = offset;
                _searchPeopleNeedsFirstPage = false;
                SearchPeopleTotal = totalCount;
                SearchPeopleHasMore = hasMore;
                SearchPeopleError = string.Empty;
            };
        }
        catch (Exception exception) when (IsExpectedFailure(exception) && !IsSessionExpired(exception))
        {
            return () => SearchPeopleError = "People could not be refreshed. Previously loaded results are still shown.";
        }
    }

    public async Task LoadLumenGenresAsync(CancellationToken cancellationToken = default)
    {
        if (_api is null || _pageCancellation is null || !IsCurrentPage(_pageVersion)) return;
        var location = _location;
        SetLumenGenreContext(location);
        if (_lumenGenresLoaded) return;
        CancelAndDispose(ref _lumenGenresCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token, cancellationToken);
        _lumenGenresCancellation = source;
        var token = source.Token;
        var sessionVersion = _sessionVersion;
        var pageVersion = _pageVersion;
        var context = _lumenGenresContext;
        var api = _api;
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            bool hasMore;
            do
            {
                var previousCount = names.Count;
                var result = await api.GetGenresAsync(new ItemQuery
                {
                    ParentId = location.Kind is LocationKind.Library or LocationKind.Item or LocationKind.Latest ? location.ItemId : null,
                    IncludeItemTypes = location.ItemTypes ?? SearchTypes, Recursive = true,
                    StartIndex = offset, Limit = GenrePageSize, EnableImages = false, EnableUserData = false
                }, token);
                token.ThrowIfCancellationRequested();
                foreach (var genre in result.Items)
                    if (!string.IsNullOrWhiteSpace(genre.Name)) names.Add(genre.Name.Trim());
                offset += result.Items.Length;
                hasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total
                    ? offset < total : result.Items.Length == GenrePageSize);
                if (names.Count == previousCount) hasMore = false;
            } while (hasMore);
            if (!IsCurrentSession(sessionVersion) || !CanCommitPage(pageVersion, token)
                || !ReferenceEquals(_lumenGenresCancellation, source) || context != _lumenGenresContext || HasPendingSearch) return;
            _availableGenres = names.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
            _lumenGenresLoaded = true;
            OnPropertyChanged(nameof(AvailableGenres));
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentSession(sessionVersion) && CanCommitPage(pageVersion, token)
                && ReferenceEquals(_lumenGenresCancellation, source) && context == _lumenGenresContext && !HasPendingSearch)
                ShowError(exception);
        }
    }

    private void SetLumenGenreContext(BrowseLocation location)
    {
        var context = $"{location.Kind}:{location.ItemId}:{string.Join(',', location.ItemTypes ?? SearchTypes)}";
        if (_lumenGenresContext == context) return;
        CancelAndDispose(ref _lumenGenresCancellation);
        _lumenGenresContext = context;
        _lumenGenresLoaded = false;
        _availableGenres = [];
        OnPropertyChanged(nameof(AvailableGenres));
    }

    private async Task MutateLumenUserDataAsync(MediaCardViewModel item, bool favorite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsMutating || _api is null || _sessionCancellation is null || string.IsNullOrWhiteSpace(item.Id)) return;
        var version = _sessionVersion;
        var pageVersion = _pageVersion;
        var id = item.Id;
        var api = _api;
        var desired = favorite ? !item.IsFavorite : !item.IsPlayed;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        IsMutating = true;
        HasError = false;
        try
        {
            var result = favorite ? await api.SetFavoriteAsync(id, desired, linked.Token)
                : await api.SetPlayedAsync(id, desired, linked.Token);
            if (!IsCurrentSession(version) || linked.IsCancellationRequested) return;
            _userDataChanges[id] = (++_userDataRevision, result);
            var cards = Items.Concat(Libraries).Concat(Seasons).Concat(HomeRows.SelectMany(row => row.Items))
                .Concat(SearchPeople).Concat(HistoryCards()).Append(item).Append(Detail).Append(PlayableDetail);
            if (NextEpisode is { } nextEpisode) cards = cards.Append(nextEpisode);
            foreach (var card in cards.Where(card => card.Id == id).Distinct()) card.ApplyUserData(result);
            OnPropertyChanged(nameof(PrimaryPlayLabel));
            OnPropertyChanged(nameof(PrimaryPlayHint));
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentSession(version) && pageVersion == _pageVersion && !linked.IsCancellationRequested) ShowError(exception);
        }
        finally
        {
            if (IsCurrentSession(version)) IsMutating = false;
        }
    }

    private void CancelLumenSearchPeople()
    {
        _searchPeopleRequestVersion++;
        CancelAndDispose(ref _searchPeopleCancellation);
        SearchPeopleIsBusy = false;
    }

    private void ResetLumenSearchPeople()
    {
        CancelLumenSearchPeople();
        _searchPeopleNextIndex = 0;
        _searchPeopleRequested = false;
        _searchPeopleNeedsFirstPage = false;
        SearchPeople.Clear();
        SearchPeopleHasMore = false;
        SearchPeopleTotal = null;
        SearchPeopleError = string.Empty;
    }

    private void ResetLumenState()
    {
        ResetLumenSearchPeople();
        CancelAndDispose(ref _lumenGenresCancellation);
        _lumenGenresContext = null;
        _lumenGenresLoaded = false;
        _availableGenres = [];
        OnPropertyChanged(nameof(AvailableGenres));
    }

    private LumenSearchPeopleSnapshot CaptureLumenSearchPeopleState() =>
        new(SearchPeople.ToArray(), _searchPeopleNextIndex, SearchPeopleHasMore, SearchPeopleTotal,
            SearchPeopleError, _searchPeopleRequested, _searchPeopleNeedsFirstPage);

    private void RestoreLumenSearchPeopleState(LumenSearchPeopleSnapshot state)
    {
        CancelLumenSearchPeople();
        SearchPeople.Clear();
        foreach (var item in state.Items) SearchPeople.Add(item);
        _searchPeopleNextIndex = state.NextIndex;
        _searchPeopleRequested = state.Requested;
        _searchPeopleNeedsFirstPage = state.NeedsFirstPage;
        SearchPeopleHasMore = state.HasMore;
        SearchPeopleTotal = state.TotalCount;
        SearchPeopleError = state.Error;
        SetLumenGenreContext(_location);
    }

    private Task ResumeLumenRestoredSearchAsync(PageLoadOutcome outcome)
    {
        if (!IsSearch || !HasSearchQuery || _api is null || _pageCancellation is null) return Task.CompletedTask;
        var api = _api;
        var sessionVersion = _sessionVersion;
        var token = _pageCancellation.Token;
        var pageVersion = _pageVersion;
        var mediaTask = outcome == PageLoadOutcome.Loading && _nextIndex == 0
            ? ResumeLumenMediaFirstPageAsync(api, _location, pageVersion, token) : Task.CompletedTask;
        var peopleTask = _searchPeopleRequested && _searchPeopleNeedsFirstPage && SearchPeopleError.Length == 0
            ? LoadLumenSearchPeoplePageAsync(api, _location.SearchTerm!, 0, sessionVersion, pageVersion, null, token)
            : Task.CompletedTask;
        return Task.WhenAll(mediaTask, peopleTask);
    }

    private async Task ResumeLumenMediaFirstPageAsync(EmbyApiClient api, BrowseLocation location,
        int pageVersion, CancellationToken cancellationToken)
    {
        var userDataRevision = _userDataRevision;
        _isLoadingMore = false;
        IsBusy = true;
        HasError = false;
        try
        {
            var result = await QueryItemsAsync(api, location, 0, cancellationToken);
            if (!CanCommitPage(pageVersion, cancellationToken) || HasPendingSearch) return;
            ReconcileItems(PreserveNewerUserData(result.Items, userDataRevision));
            _nextIndex = result.Items.Length;
            HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total
                ? _nextIndex < total : result.Items.Length == PageSize);
            UpdateCount(result.TotalRecordCount);
            SetLoadOutcome(PageLoadOutcome.Succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(pageVersion, cancellationToken) && !HasPendingSearch) ShowError(exception);
        }
        finally
        {
            if (pageVersion == _pageVersion) IsBusy = false;
        }
    }

    private sealed record LumenSearchPeopleSnapshot(MediaCardViewModel[] Items, int NextIndex, bool HasMore,
        int? TotalCount, string Error, bool Requested, bool NeedsFirstPage);

    private void NotifyLumenBrowseOptions()
    {
        OnPropertyChanged(nameof(BrowseGenre));
        OnPropertyChanged(nameof(BrowseNameStartsWith));
        OnPropertyChanged(nameof(BrowseMediaType));
        OnPropertyChanged(nameof(BrowseSearchType));
    }
}
