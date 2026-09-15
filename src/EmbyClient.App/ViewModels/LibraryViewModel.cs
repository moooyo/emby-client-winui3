using CommunityToolkit.Mvvm.ComponentModel;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public enum HomeSection { ContinueWatching, NextUp, RecentlyAdded }
public enum WatchStatusFilter { All, Unplayed, Played }

public sealed partial class LibraryViewModel : ObservableObject
{
    private const int PageSize = 48;
    private const int HomeShelfSize = 16;
    private static readonly string[] ListFields = ["PrimaryImageAspectRatio", "Overview"];
    private static readonly string[] SearchTypes = ["Movie", "Series", "Episode", "Video", "MusicVideo", "Audio", "MusicAlbum", "BoxSet"];
    private readonly ImageCache _images = new();
    private readonly Stack<BrowseSnapshot> _history = new();
    private EmbyApiClient? _api;
    private UserDto? _user;
    private string _serverId = string.Empty;
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _pageCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _pendingSearchCancellation;
    private CancellationTokenSource? _seasonCancellation;
    private Task<bool>? _librariesReady;
    private BrowseLocation _location = Home();
    private BrowseLocation? _pendingBrowseLocation;
    private string? _pendingSeasonId;
    private int _sessionVersion;
    private int _pageVersion;
    private int _libraryRequestVersion;
    private int _seasonRequestVersion;
    private int _nextIndex;
    private int _collectionRevision;
    private bool _sessionExpiredNotified;
    private bool _isInitializingSeries;

    public ObservableCollection<MediaCardViewModel> Items { get; } = [];
    public ObservableCollection<MediaCardViewModel> Libraries { get; } = [];
    public ObservableCollection<MediaShelfViewModel> HomeRows { get; } = [];
    public ObservableCollection<MediaCardViewModel> Seasons { get; } = [];
    public ObservableCollection<MediaCardViewModel> Episodes => Items;
    public event EventHandler? SessionExpired;

    [ObservableProperty]
    public partial string Title { get; set; } = "Your library";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility), nameof(DetailEmptyVisibility))]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility), nameof(EmptyVisibility), nameof(CanLoadMore), nameof(DetailEmptyVisibility))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDetails))]
    public partial bool IsMutating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadMoreVisibility), nameof(CanLoadMore))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HomeVisibility), nameof(BrowseVisibility), nameof(CollectionVisibility), nameof(EmptyVisibility), nameof(HomeLibrariesVisibility))]
    public partial bool IsHome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailVisibility), nameof(CollectionVisibility), nameof(EmptyVisibility), nameof(CanEditDetails),
        nameof(BrowseVisibility), nameof(HeaderVisibility), nameof(DetailChildrenVisibility), nameof(SeasonSelectorVisibility), nameof(PrimaryPlayVisibility),
        nameof(DetailItemsVisibility), nameof(DetailEmptyVisibility), nameof(CanSelectSeason))]
    public partial bool HasDetails { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility), nameof(DetailChildrenVisibility), nameof(DetailItemsVisibility), nameof(DetailEmptyVisibility))]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = "No items are available.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectionVisibility), nameof(EmptyVisibility), nameof(DetailChildrenTitle), nameof(SeasonSelectorVisibility), nameof(PrimaryPlayLabel),
        nameof(DetailChildrenVisibility), nameof(DetailEmptyVisibility), nameof(CanSelectSeason))]
    public partial MediaCardViewModel Detail { get; set; } = new(new BaseItemDto());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryPlayLabel), nameof(PrimaryPlayVisibility), nameof(PrimaryPlayHint))]
    public partial MediaCardViewModel PlayableDetail { get; set; } = new(new BaseItemDto());

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailChildrenTitle))]
    public partial MediaCardViewModel? SelectedSeason { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailChildrenTitle))]
    public partial bool DetailItemsAreEpisodes { get; set; }

    [ObservableProperty]
    public partial HomeSection SelectedHomeSection { get; set; }

    public Visibility LoadingVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeVisibility => IsHome ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeLibrariesVisibility => IsHome && Libraries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => HasDetails ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BrowseVisibility => !IsHome && !HasDetails && !IsPerson ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HeaderVisibility => !HasDetails && !IsPerson ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CollectionVisibility => IsHome || IsPerson || HasDetails && !Detail.IsFolder ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => _loadOutcome == PageLoadOutcome.Succeeded && !IsBusy && !HasError && !HasItems && (!IsSearch || HasSearchQuery)
        && (IsHome || CollectionVisibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailChildrenVisibility => HasDetails && Detail.IsFolder ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailItemsVisibility => HasDetails && HasItems ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailEmptyVisibility => _loadOutcome == PageLoadOutcome.Succeeded && HasDetails && Detail.IsFolder && !HasItems && !IsBusy && !HasError ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SeasonSelectorVisibility => HasDetails && Detail.Item.Type == "Series" && Seasons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool CanSelectSeason => HasDetails && Detail.Item.Type == "Series" && !_isInitializingSeries;
    public string DetailChildrenTitle => DetailItemsAreEpisodes
        ? (_pendingSeasonId is null ? SelectedSeason : Seasons.FirstOrDefault(season => season.Id == _location.SeasonId))?.Title ?? "Episodes"
        : "Contents";
    public string PrimaryPlayLabel => PlayableDetail.CanResume
        ? (Detail.Item.Type is "Series" or "Season")
            ? $"Resume {PlayableDetail.EpisodeNumber} · {MediaCardViewModel.FormatTime(PlayableDetail.ResumeTicks)}"
            : $"Resume · {MediaCardViewModel.FormatTime(PlayableDetail.ResumeTicks)}"
        : (Detail.Item.Type is "Series" or "Season") && PlayableDetail.Item.Type == "Episode" ? $"Play {PlayableDetail.EpisodeNumber}" : "Play";
    public string PrimaryPlayHint => PlayableDetail.Item.Type == "Episode" ? $"{PlayableDetail.EpisodeNumber} · {PlayableDetail.Title}" : PlayableDetail.Title;
    public Visibility PrimaryPlayVisibility => HasDetails && IsPlaybackAllowed && PlayableDetail.CanPlay ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadMoreVisibility => HasMore ? Visibility.Visible : Visibility.Collapsed;
    public bool CanLoadMore => HasMore && !IsBusy;
    public bool CanEditDetails => HasDetails && !IsMutating;
    public bool IsPlaybackAllowed => _user?.Policy?.EnableMediaPlayback != false;
    public bool HasPendingSearch => _pendingSearchCancellation?.IsCancellationRequested == false;
    public string SearchText => _location.Kind == LocationKind.Search ? _location.SearchTerm ?? string.Empty : string.Empty;
    public string? SelectedLibraryId => _location.Kind is LocationKind.Library or LocationKind.Latest ? _location.ItemId : null;
    public bool IsDetailLocation => _location.Kind is LocationKind.Item or LocationKind.Person;
    public bool IsHomeSection => _location.Kind is LocationKind.Resume or LocationKind.NextUp
        || _location.Kind == LocationKind.Latest && _location.ItemId is null;
    public bool IsFavorites => _location.Kind == LocationKind.Favorites;
    public bool IsSearch => _location.Kind == LocationKind.Search;
    public bool HasSearchQuery => IsSearch && !string.IsNullOrWhiteSpace(_location.SearchTerm);
    public Visibility SearchEmptyVisibility => IsSearch && !HasSearchQuery ? Visibility.Visible : Visibility.Collapsed;
    public string BrowseSortKey => (_pendingBrowseLocation ?? _location).SortKey;
    public bool BrowseSortDescending => (_pendingBrowseLocation ?? _location).SortDescending;
    public WatchStatusFilter BrowseWatchFilter => (_pendingBrowseLocation ?? _location).WatchFilter;
    public bool BrowseUnplayedOnly => BrowseWatchFilter == WatchStatusFilter.Unplayed;
    public int CollectionRevision => _collectionRevision;
    public Visibility BrowseOptionsVisibility => SupportsBrowseOptions && (HasItems || IsBusy || BrowseWatchFilter != WatchStatusFilter.All)
        ? Visibility.Visible : Visibility.Collapsed;

    public async Task SetSessionAsync(EmbyApiClient api, string serverId, UserDto user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (string.IsNullOrWhiteSpace(user.Id) || !api.IsAuthenticated || api.UserId != user.Id)
            throw new ArgumentException("The library requires a matching authenticated user context.", nameof(user));

        ClearSession();
        _api = api;
        _serverId = serverId;
        _user = user;
        OnPropertyChanged(nameof(IsPlaybackAllowed));
        OnPropertyChanged(nameof(PrimaryPlayVisibility));
        _sessionCancellation = new CancellationTokenSource();
        var version = _sessionVersion;
        var pageVersion = _pageVersion;
        var libraryRequestVersion = ++_libraryRequestVersion;
        string? navigationError = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        IsBusy = true;
        try
        {
            var libraries = LoadLibrariesAsync(api, version, libraryRequestVersion, linked.Token);
            _librariesReady = libraries;
            if (!await libraries) return;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentLibraryRequest(version, libraryRequestVersion) && !linked.IsCancellationRequested
                && ((pageVersion == _pageVersion && !HasPendingSearch) || IsSessionExpired(exception)))
            {
                ShowError(exception);
                navigationError = ErrorMessage;
            }
        }
        finally
        {
            if (IsCurrentSession(version) && pageVersion == _pageVersion) IsBusy = false;
        }

        if (IsCurrentSession(version) && pageVersion == _pageVersion && !HasPendingSearch && !linked.IsCancellationRequested)
        {
            var page = NavigateAsync(Home(), false, cancellationToken);
            var homePageVersion = _pageVersion;
            await page;
            if (IsCurrentLibraryRequest(version, libraryRequestVersion) && IsCurrentPage(homePageVersion)
                && !HasPendingSearch && !HasError && navigationError is not null)
            {
                ErrorMessage = navigationError;
                HasError = true;
            }
        }
    }

    public void ClearSession()
    {
        _sessionVersion++;
        _pageVersion++;
        _libraryRequestVersion++;
        _seasonRequestVersion++;
        CancelSearch();
        CancelAndDispose(ref _seasonCancellation);
        CancelAndDispose(ref _pageCancellation);
        CancelAndDispose(ref _sessionCancellation);
        _images.Clear();
        _api = null;
        _librariesReady = null;
        _user = null;
        _serverId = string.Empty;
        _location = Home();
        _pendingBrowseLocation = null;
        _pendingSeasonId = null;
        SetSeriesInitializing(false);
        _sessionExpiredNotified = false;
        _userDataRevision = 0;
        _userDataChanges.Clear();
        foreach (var person in _people) person.Dispose();
        _people.Clear();
        ActivePerson = null;
        _searchOriginSnapshot = null;
        ViewportState = new();
        SetLoadOutcome(PageLoadOutcome.NotRequested);
        NextEpisode = null;
        _history.Clear();
        Items.Clear();
        Libraries.Clear();
        HomeRows.Clear();
        Seasons.Clear();
        SelectedSeason = null;
        DetailItemsAreEpisodes = false;
        Detail = new(new BaseItemDto());
        PlayableDetail = new(new BaseItemDto());
        Title = "Your library";
        Subtitle = string.Empty;
        HasDetails = HasItems = HasMore = IsHome = IsBusy = IsMutating = CanGoBack = HasError = false;
        ErrorMessage = string.Empty;
        EmptyMessage = "Connect to a server to browse your media.";
        NotifyBrowseOptions();
        OnPropertyChanged(nameof(SeasonSelectorVisibility));
        OnPropertyChanged(nameof(IsPlaybackAllowed));
        OnPropertyChanged(nameof(HomeLibrariesVisibility));
        NotifyPersonState();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (IsPerson && ActivePerson is { } person)
        {
            await person.LoadBiographyAsync();
            return;
        }
        if (IsSearch && !HasSearchQuery) return;
        CancelSearch();
        if (_api is null || _sessionCancellation is null) return;
        var sessionVersion = _sessionVersion;
        var libraryRequestVersion = ++_libraryRequestVersion;
        var pageVersion = ++_pageVersion;
        var seasonRequestVersion = ++_seasonRequestVersion;
        var location = _location;
        var loadedCount = _nextIndex;
        var userDataRevision = _userDataRevision;
        CancelAndDispose(ref _seasonCancellation);
        CancelAndDispose(ref _pageCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _pageCancellation = source;
        var token = source.Token;
        var api = _api;
        _pendingBrowseLocation = null;
        _pendingSeasonId = null;
        SelectedSeason = Seasons.FirstOrDefault(season => season.Id == location.SeasonId);
        SetSeriesInitializing(HasDetails && Detail.Item.Type == "Series");
        _isLoadingMore = false;
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        NotifyBrowseOptions();
        NotifyPresentation();
        try
        {
            var librariesTask = api.GetViewsAsync(cancellationToken: token);
            var pageTask = PreparePageRefreshAsync(api, location, loadedCount, userDataRevision, librariesTask, token);
            await Task.WhenAll(librariesTask, pageTask);
            if (!CanCommitPage(pageVersion, token) || seasonRequestVersion != _seasonRequestVersion
                || !IsCurrentLibraryRequest(sessionVersion, libraryRequestVersion) || HasPendingSearch) return;
            ReconcileCards(Libraries, PreserveNewerUserData((await librariesTask).Items, userDataRevision));
            _librariesReady = Task.FromResult(true);
            (await pageTask)();
            if (!HasError || HasItems) SetLoadOutcome(PageLoadOutcome.Succeeded);
            OnPropertyChanged(nameof(HomeLibrariesVisibility));
            NotifyBrowseOptions();
            NotifyPresentation();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(pageVersion, token) && seasonRequestVersion == _seasonRequestVersion
                && !HasPendingSearch)
            {
                ShowError(exception);
                if (CanCommitPage(pageVersion, token) && !IsSessionExpired(exception) && (HasItems || HasDetails))
                    ErrorMessage += " Previously loaded content is still shown.";
            }
        }
        finally
        {
            if (pageVersion == _pageVersion && seasonRequestVersion == _seasonRequestVersion)
            {
                RestorePageLifetime(pageVersion, source);
                SetSeriesInitializing(false);
                IsBusy = false;
            }
        }
    }

    private async Task<bool> LoadLibrariesAsync(EmbyApiClient api, int sessionVersion, int requestVersion,
        CancellationToken cancellationToken)
    {
        var result = await api.GetViewsAsync(cancellationToken: cancellationToken);
        if (!IsCurrentLibraryRequest(sessionVersion, requestVersion) || cancellationToken.IsCancellationRequested) return false;
        ReconcileCards(Libraries, result.Items);
        OnPropertyChanged(nameof(HomeLibrariesVisibility));
        NotifyPresentation();
        return true;
    }

    public Task ShowHomeAsync(CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(Home(), !_location.IsHome, cancellationToken);
    }

    public Task ShowHomeSectionAsync(HomeSection section, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(HomeSectionLocation(section), true, cancellationToken);
    }

    public Task ShowShelfAsync(MediaShelfViewModel shelf, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        var location = shelf.LibraryId is { } libraryId
            ? new BrowseLocation(LocationKind.Latest, libraryId, shelf.Title, SortKey: "DateCreated", SortDescending: true)
            : HomeSectionLocation(shelf.Section ?? HomeSection.RecentlyAdded);
        return NavigateAsync(location, true, cancellationToken);
    }

    public Task SetBrowseOptionsAsync(string sortKey, bool descending, bool unplayedOnly, CancellationToken cancellationToken = default) =>
        SetBrowseOptionsAsync(sortKey, descending, unplayedOnly ? WatchStatusFilter.Unplayed : WatchStatusFilter.All, cancellationToken);

    public async Task SetBrowseOptionsAsync(string sortKey, bool descending, WatchStatusFilter watchFilter, CancellationToken cancellationToken = default)
    {
        if (_api is null || _sessionCancellation is null
            || _location.Kind is not (LocationKind.Library or LocationKind.Favorites or LocationKind.Search or LocationKind.Latest)) return;
        var normalized = sortKey switch
        {
            "ProductionYear" => "ProductionYear",
            "DateCreated" => "DateCreated",
            _ => "SortName"
        };
        var normalizedFilter = watchFilter is WatchStatusFilter.Unplayed or WatchStatusFilter.Played ? watchFilter : WatchStatusFilter.All;
        var location = (_pendingBrowseLocation ?? _location) with
            { SortKey = normalized, SortDescending = descending, WatchFilter = normalizedFilter };
        if (location == (_pendingBrowseLocation ?? _location)) return;
        if (location.Kind == LocationKind.Search && string.IsNullOrWhiteSpace(location.SearchTerm))
        {
            _location = location;
            NotifyBrowseOptions();
            return;
        }
        var version = ++_pageVersion;
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

            // A filter is committed only after its first page is available. Existing cards
            // and paging still describe the previous query while the request is pending.
            _location = location;
            _pendingBrowseLocation = null;
            ReconcileItems(result.Items);
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
    }

    public Task ShowLibraryAsync(MediaCardViewModel library, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(new(LocationKind.Library, library.Id, library.Title), true, cancellationToken);
    }

    public Task ShowFavoritesAsync(CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(new(LocationKind.Favorites, null, "Favorites"), true, cancellationToken);
    }

    public Task ShowSearchAsync(CancellationToken cancellationToken = default)
    {
        if (IsSearch) return Task.CompletedTask;
        CancelSearch();
        _searchOriginSnapshot = CaptureBrowseState();
        return NavigateAsync(new(LocationKind.Search, null, "Search", SearchOrigin: _location), false, cancellationToken);
    }

    public Task ShowItemAsync(MediaCardViewModel item, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(new(LocationKind.Item, item.Id, item.Title, item.Item.SeriesId,
            SeasonId: item.Item.SeasonId, SeriesName: item.Item.SeriesName), true, cancellationToken);
    }

    public Task GoBackAsync(CancellationToken cancellationToken = default)
    {
        var searchDestination = IsSearch ? _searchOriginSnapshot : null;
        CancelSearch();
        if (searchDestination is not null) return RestoreBrowseStateAsync(searchDestination);
        return _history.TryPop(out var state) ? RestoreBrowseStateAsync(state) : Task.CompletedTask;
    }

    public async Task SearchAsync(string text, bool debounce = true, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        if (_sessionCancellation is null) return;
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
            var destination = IsSearch
                ? _location with { Title = "Search", SearchTerm = term }
                : new BrowseLocation(LocationKind.Search, null, "Search", SearchTerm: term, SearchOrigin: _location);
            await NavigateAsync(destination, false, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pendingSearchCancellation, source)) _pendingSearchCancellation = null;
        }
    }

    public async Task<PageLoadMoreOutcome> LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadMore || _api is null || _pageCancellation is null) return PageLoadMoreOutcome.NotStarted;
        var version = _pageVersion;
        var api = _api;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token, cancellationToken);
        _isLoadingMore = true;
        IsBusy = true;
        HasError = false;
        var count = Items.Count;
        try
        {
            var result = await QueryItemsAsync(api, _location, _nextIndex, linked.Token);
            if (!CanCommitPage(version, linked.Token) || HasPendingSearch) return PageLoadMoreOutcome.Canceled;
            AppendItems(result.Items);
            _nextIndex += result.Items.Length;
            HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? _nextIndex < total : result.Items.Length == PageSize);
            UpdateCount(result.TotalRecordCount);
            SetLoadOutcome(PageLoadOutcome.Succeeded);
            return Items.Count > count ? PageLoadMoreOutcome.Appended
                : HasMore ? PageLoadMoreOutcome.Repeated : PageLoadMoreOutcome.Exhausted;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { return PageLoadMoreOutcome.Canceled; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(version, linked.Token) && !HasPendingSearch) ShowError(exception);
            return PageLoadMoreOutcome.Failed;
        }
        finally
        {
            if (version == _pageVersion) IsBusy = false;
        }
    }

    public async Task ToggleFavoriteAsync(CancellationToken cancellationToken = default) =>
        await MutateUserDataAsync(true, cancellationToken);

    public async Task TogglePlayedAsync(CancellationToken cancellationToken = default) =>
        await MutateUserDataAsync(false, cancellationToken);

    public async Task<byte[]?> LoadPosterAsync(MediaCardViewModel item, int width, int height, CancellationToken cancellationToken)
        => await LoadPosterAsync(item, width, height, ArtworkKind.Poster, cancellationToken);

    public async Task<byte[]?> LoadPosterAsync(MediaCardViewModel item, int width, int height, ArtworkKind artworkKind,
        CancellationToken cancellationToken)
    {
        if (_api is null || _user?.Id is null || _sessionCancellation is null) return null;
        var version = _sessionVersion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        try
        {
            var bytes = await _images.GetAsync(_api, _serverId, _user.Id, item.Item, width, height, artworkKind, linked.Token);
            return IsCurrentSession(version) && !linked.IsCancellationRequested ? bytes : null;
        }
        catch (EmbyApiException exception) when (IsSessionExpired(exception))
        {
            if (IsCurrentSession(version) && !linked.IsCancellationRequested) ShowError(exception);
            return null;
        }
    }

    private async Task NavigateAsync(BrowseLocation location, bool remember, CancellationToken cancellationToken,
        PersonDetailsViewModel? person = null)
    {
        if (_api is null || _sessionCancellation is null) return;
        if (remember && location != _location) _history.Push(CaptureBrowseState());
        var searchOwner = location.Kind == LocationKind.Search ? _pendingSearchCancellation : null;
        var seasonRequestVersion = ++_seasonRequestVersion;
        var version = ++_pageVersion;
        CancelAndDispose(ref _seasonCancellation);
        CancelAndDispose(ref _pageCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _pageCancellation = source;
        var token = source.Token;
        var api = _api;
        _pendingBrowseLocation = null;
        _pendingSeasonId = null;
        SetSeriesInitializing(false);
        _location = location;
        ActivePerson = person;
        if (location.Kind != LocationKind.Search) _searchOriginSnapshot = null;
        ViewportState = new();
        NextEpisode = null;
        CanGoBack = _history.Count > 0 || location.SearchOrigin is not null;
        Title = location.Title;
        Subtitle = location.Kind == LocationKind.Search
            ? string.IsNullOrWhiteSpace(location.SearchTerm) ? "Find movies, shows, episodes, and more." : $"Results for \u201c{location.SearchTerm}\u201d"
            : string.Empty;
        Items.Clear();
        HomeRows.Clear();
        Seasons.Clear();
        SelectedSeason = null;
        DetailItemsAreEpisodes = false;
        PlayableDetail = new(new BaseItemDto());
        HasItems = HasMore = HasDetails = false;
        IsHome = location.IsHome;
        HasError = false;
        ErrorMessage = string.Empty;
        _nextIndex = 0;
        _isLoadingMore = false;
        IsBusy = location.Kind != LocationKind.Search || !string.IsNullOrWhiteSpace(location.SearchTerm);
        SetLoadOutcome(IsBusy ? PageLoadOutcome.Loading : PageLoadOutcome.NotRequested);
        NotifyBrowseOptions();
        NotifyPersonState();
        NotifyDetailContext();
        OnPropertyChanged(nameof(SeasonSelectorVisibility));
        UpdateEmptyMessage();
        SelectedHomeSection = location.Kind switch
        {
            LocationKind.NextUp => HomeSection.NextUp,
            LocationKind.Latest => HomeSection.RecentlyAdded,
            _ => HomeSection.ContinueWatching
        };

        try
        {
            if (person is not null)
            {
                IsBusy = false;
                await person.EnsureLoadedAsync();
                return;
            }
            if (location.Kind == LocationKind.Search && string.IsNullOrWhiteSpace(location.SearchTerm)) return;
            if (location.IsHome)
            {
                if (_librariesReady is { } librariesReady)
                {
                    try { await librariesReady.WaitAsync(token); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) when (IsExpectedFailure(exception))
                    {
                        if (CanCommitNavigation(version, token, searchOwner)) ShowError(exception);
                    }
                }
                if (!CanCommitNavigation(version, token, searchOwner)) return;
                await LoadHomeAsync(api, version, token);
                return;
            }
            QueryResult<BaseItemDto> result;
            if (location.Kind == LocationKind.Item)
            {
                var item = await api.GetItemAsync(location.ItemId!, token);
                if (!CanCommitNavigation(version, token, searchOwner)) return;
                if (item.Type is "Episode" or "Season") item = item with
                {
                    SeriesId = item.SeriesId ?? location.SeriesId,
                    SeriesName = item.SeriesName ?? location.SeriesName,
                    SeasonId = item.SeasonId ?? (item.Type == "Episode" ? location.SeasonId : null)
                };
                Detail = new(item);
                Title = Detail.Title;
                HasDetails = true;
                if (item.Type is "Series" or "Season") EmptyMessage = "No episodes are available in this season.";
                if (!Detail.IsFolder)
                {
                    Subtitle = item.SeriesName ?? "Item details";
                    PlayableDetail = Detail;
                    SetLoadOutcome(PageLoadOutcome.Succeeded);
                    if (item.Type == "Episode") _ = LoadEpisodeContextAsync(api, Detail, version, token);
                    return;
                }
                if (item.Type == "Series")
                {
                    await LoadSeriesAsync(api, location, version, token);
                    return;
                }
                else if (item.Type == "Season" && (item.SeriesId ?? location.SeriesId) is { } seriesId)
                {
                    DetailItemsAreEpisodes = true;
                    result = await api.GetEpisodesAsync(seriesId, Detail.Id, token);
                }
                else
                {
                    result = await QueryItemsAsync(api, location, 0, token);
                    if (!CanCommitNavigation(version, token, searchOwner)) return;
                    HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? result.Items.Length < total : result.Items.Length == PageSize);
                }
            }
            else
            {
                result = await QueryItemsAsync(api, location, 0, token);
                if (!CanCommitNavigation(version, token, searchOwner)) return;
                HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? result.Items.Length < total : result.Items.Length == PageSize);
            }

            if (!CanCommitNavigation(version, token, searchOwner)) return;
            AppendItems(result.Items);
            if (HasDetails && DetailItemsAreEpisodes) PlayableDetail = FindPlayable(Items);
            _nextIndex = result.Items.Length;
            UpdateCount(result.TotalRecordCount);
            SetLoadOutcome(PageLoadOutcome.Succeeded);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitNavigation(version, token, searchOwner) && seasonRequestVersion == _seasonRequestVersion) ShowError(exception);
        }
        finally
        {
            if (version == _pageVersion && seasonRequestVersion == _seasonRequestVersion)
            {
                RestorePageLifetime(version, source);
                IsBusy = false;
            }
        }
    }

    private async Task LoadHomeAsync(EmbyApiClient api, int pageVersion, CancellationToken cancellationToken)
    {
        var results = await QueryHomeShelvesAsync(api, Libraries.Select(library => library.Item).ToArray(), cancellationToken,
            completed =>
            {
                if (CanCommitPage(pageVersion, cancellationToken) && !HasPendingSearch) ApplyHomeShelves(completed);
            });
        if (!CanCommitPage(pageVersion, cancellationToken) || HasPendingSearch) return;
        ApplyHomeShelves(results);
        SetLoadOutcome(results.Any(result => result.Failure is null) ? PageLoadOutcome.Succeeded : PageLoadOutcome.Failed);
        var failures = results.Select(result => result.Failure).OfType<Exception>().ToArray();
        if (failures.Length > 0)
        {
            var failure = failures.FirstOrDefault(IsSessionExpired) ?? failures[0];
            ShowError(failure);
            if (CanCommitPage(pageVersion, cancellationToken) && !IsSessionExpired(failure) && HasItems)
                ErrorMessage = "Some home sections could not be loaded. Refresh to try again.";
        }
    }

    private async Task LoadSeriesAsync(EmbyApiClient api, BrowseLocation location, int pageVersion, CancellationToken cancellationToken)
    {
        var seriesId = Detail.Id;
        var seasonRequestVersion = _seasonRequestVersion;
        SetSeriesInitializing(true);
        try
        {
            var seasons = await api.GetSeasonsAsync(seriesId, cancellationToken);
            if (!CanCommitPage(pageVersion, cancellationToken) || HasPendingSearch) return;
            foreach (var season in seasons.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id)).OrderBy(item => item.IndexNumber ?? int.MaxValue))
                Seasons.Add(new(season));
            var selectedSeason = Seasons.FirstOrDefault(season => season.Id == location.SeasonId)
                ?? Seasons.FirstOrDefault(season => season.Item.IndexNumber is > 0) ?? Seasons.FirstOrDefault();
            _location = _location with { SeasonId = selectedSeason?.Id };
            SelectedSeason = selectedSeason;
            DetailItemsAreEpisodes = true;
            EmptyMessage = selectedSeason is null ? "No episodes are available for this series." : "No episodes are available in this season.";
            OnPropertyChanged(nameof(SeasonSelectorVisibility));

            var episodesTask = api.GetEpisodesAsync(seriesId, SelectedSeason?.Id, cancellationToken);
            var resumeTask = location.PreferSelectedSeason ? Task.FromResult<MediaCardViewModel?>(null)
                : FindSeriesPlaybackAsync(api, seriesId, true, pageVersion, cancellationToken);
            var nextTask = location.PreferSelectedSeason ? Task.FromResult<MediaCardViewModel?>(null)
                : FindSeriesPlaybackAsync(api, seriesId, false, pageVersion, cancellationToken);
            var episodes = await episodesTask;
            if (!CanCommitPage(pageVersion, cancellationToken) || seasonRequestVersion != _seasonRequestVersion || HasPendingSearch) return;
            AppendItems(episodes.Items);
            PlayableDetail = FindPlayable(Items);
            _nextIndex = episodes.Items.Length;
            UpdateCount(episodes.TotalRecordCount);
            SetLoadOutcome(PageLoadOutcome.Succeeded);
            SetSeriesInitializing(false);
            IsBusy = false;
            _ = UpdateSeriesRecommendationAsync(resumeTask, nextTask, pageVersion, seasonRequestVersion, cancellationToken);
        }
        finally
        {
            if (pageVersion == _pageVersion) SetSeriesInitializing(false);
        }
    }

    private void SetSeriesInitializing(bool initializing)
    {
        if (_isInitializingSeries == initializing) return;
        _isInitializingSeries = initializing;
        OnPropertyChanged(nameof(CanSelectSeason));
        NotifyDetailContext();
    }

    private async Task<MediaCardViewModel?> FindSeriesPlaybackAsync(EmbyApiClient api, string seriesId, bool resume,
        int pageVersion, CancellationToken cancellationToken)
    {
        try
        {
            var result = resume
                ? await api.GetResumeItemsAsync(new ItemQuery
                {
                    ParentId = seriesId,
                    Recursive = true,
                    IncludeItemTypes = ["Episode"],
                    Limit = HomeShelfSize,
                    Fields = ListFields
                }, cancellationToken)
                : await api.GetNextUpAsync(new NextUpQuery { SeriesId = seriesId, Limit = 1, Fields = ListFields }, cancellationToken);
            if (!CanCommitPage(pageVersion, cancellationToken) || HasPendingSearch) return null;
            return result.Items.Select(item => new MediaCardViewModel(item))
                .FirstOrDefault(card => card.Item.Type == "Episode" && card.CanPlay && (!resume || card.CanResume)
                    && (string.IsNullOrWhiteSpace(card.Item.SeriesId) || card.Item.SeriesId == seriesId));
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(pageVersion, cancellationToken) && !HasPendingSearch && IsSessionExpired(exception)) ShowError(exception);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
    }

    public async Task SelectSeasonAsync(MediaCardViewModel season, CancellationToken cancellationToken = default)
    {
        if (!CanSelectSeason || _api is null || _pageCancellation is null
            || !Seasons.Any(item => item.Id == season.Id)) return;
        if (IsBusy && !DetailItemsAreEpisodes) return;
        if (_pendingSeasonId == season.Id
            || _pendingSeasonId is null && _location.SeasonId == season.Id && (Items.Count > 0 || IsBusy)) return;
        var requestVersion = ++_seasonRequestVersion;
        CancelAndDispose(ref _seasonCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token, cancellationToken);
        _seasonCancellation = source;
        var pageVersion = _pageVersion;
        var api = _api;
        var seriesId = Detail.Id;
        var committedSeason = Seasons.FirstOrDefault(item => item.Id == _location.SeasonId);
        _pendingSeasonId = season.Id;
        IsBusy = true;
        SelectedSeason = season;
        HasError = false;
        NotifyDetailContext();
        var committed = false;
        try
        {
            var result = await api.GetEpisodesAsync(seriesId, season.Id, source.Token);
            if (!IsCurrentPage(pageVersion) || requestVersion != _seasonRequestVersion || source.IsCancellationRequested
                || HasPendingSearch) return;
            _location = _location with { SeasonId = season.Id, PreferSelectedSeason = true };
            _pendingSeasonId = null;
            ReconcileItems(result.Items);
            HasMore = false;
            PlayableDetail = FindPlayable(Items);
            EmptyMessage = "No episodes are available in this season.";
            UpdateCount(result.TotalRecordCount);
            SetLoadOutcome(PageLoadOutcome.Succeeded);
            committed = true;
            OnPropertyChanged(nameof(DetailChildrenTitle));
            NotifyCollectionCommitted();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentPage(pageVersion) && requestVersion == _seasonRequestVersion && !source.IsCancellationRequested
                && !HasPendingSearch) ShowError(exception);
        }
        finally
        {
            if (IsCurrentPage(pageVersion) && requestVersion == _seasonRequestVersion)
            {
                _pendingSeasonId = null;
                if (!committed) SelectedSeason = committedSeason;
                OnPropertyChanged(nameof(DetailChildrenTitle));
                IsBusy = false;
                NotifyDetailContext();
            }
        }
    }

    private static MediaCardViewModel FindPlayable(IEnumerable<MediaCardViewModel> items) =>
        items.FirstOrDefault(item => item.CanResume)
        ?? items.FirstOrDefault(item => item.CanPlay && !item.IsPlayed)
        ?? items.FirstOrDefault(item => item.CanPlay)
        ?? new(new BaseItemDto());

    private static Task<QueryResult<BaseItemDto>> QueryItemsAsync(EmbyApiClient api, BrowseLocation location, int offset, CancellationToken cancellationToken)
    {
        if (location.Kind == LocationKind.Search && string.IsNullOrWhiteSpace(location.SearchTerm))
            return Task.FromResult(new QueryResult<BaseItemDto> { Items = [], TotalRecordCount = 0 });
        if (location.Kind == LocationKind.NextUp)
            return api.GetNextUpAsync(new NextUpQuery { StartIndex = offset, Limit = PageSize, Fields = ListFields }, cancellationToken);
        if (location.Kind == LocationKind.Resume)
            return api.GetResumeItemsAsync(new ItemQuery { StartIndex = offset, Limit = PageSize, MediaTypes = ["Video"], Fields = ListFields }, cancellationToken);
        return api.GetItemsAsync(new ItemQuery
        {
            ParentId = location.Kind is LocationKind.Library or LocationKind.Item or LocationKind.Latest ? location.ItemId : null,
            StartIndex = offset,
            Limit = PageSize,
            Recursive = location.Kind is LocationKind.Search or LocationKind.Favorites or LocationKind.Latest,
            SearchTerm = location.SearchTerm,
            IsFavorite = location.Kind == LocationKind.Favorites ? true : null,
            IsPlayed = location.WatchFilter switch
            {
                WatchStatusFilter.Unplayed => false,
                WatchStatusFilter.Played => true,
                _ => null
            },
            IncludeItemTypes = location.Kind is LocationKind.Search or LocationKind.Latest ? SearchTypes : null,
            SortBy = location.SortKey == "SortName" ? ["SortName"] : [location.SortKey, "SortName"],
            SortOrder = [location.SortDescending ? "Descending" : "Ascending"],
            Fields = ListFields,
            EnableImageTypes = ["Primary", "Thumb", "Backdrop"],
            ImageTypeLimit = 1
        }, cancellationToken);
    }

    private async Task MutateUserDataAsync(bool favorite, CancellationToken cancellationToken)
    {
        if (!CanEditDetails || _api is null || _sessionCancellation is null || string.IsNullOrWhiteSpace(Detail.Id)) return;
        var version = _sessionVersion;
        var pageVersion = _pageVersion;
        var id = Detail.Id;
        var api = _api;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        IsMutating = true;
        HasError = false;
        try
        {
            var result = favorite
                ? await api.SetFavoriteAsync(id, !Detail.IsFavorite, linked.Token)
                : await api.SetPlayedAsync(id, !Detail.IsPlayed, linked.Token);
            if (!IsCurrentSession(version) || linked.IsCancellationRequested) return;
            _userDataChanges[id] = (++_userDataRevision, result);
            foreach (var card in Items.Concat(Libraries).Concat(Seasons).Concat(HomeRows.SelectMany(row => row.Items))
                .Concat(HistoryCards())
                .Where(item => item.Id == id)) card.ApplyUserData(result);
            if (Detail.Id == id) Detail.ApplyUserData(result);
            if (PlayableDetail.Id == id && !ReferenceEquals(PlayableDetail, Detail)) PlayableDetail.ApplyUserData(result);
            OnPropertyChanged(nameof(PrimaryPlayLabel));
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

    private void AppendItems(BaseItemDto[] items)
    {
        var ids = Items.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) && ids.Add(item.Id)) Items.Add(new(item));
        }
        HasItems = Items.Count > 0;
    }

    private void ReconcileItems(BaseItemDto[] items)
    {
        ReconcileCards(Items, items);
        HasItems = Items.Count > 0;
    }

    private void NotifyCollectionCommitted()
    {
        _collectionRevision++;
        OnPropertyChanged(nameof(CollectionRevision));
    }

    private void UpdateCount(int? total)
    {
        var count = total is { } value && value >= Items.Count ? value : Items.Count;
        var label = count == 1 ? "1 item" : $"{count:N0} items";
        Subtitle = HasDetails ? DetailItemsAreEpisodes ? $"{count:N0} {(count == 1 ? "episode" : "episodes")}" : $"Contents · {label}"
            : HasSearchQuery ? $"{label} for \u201c{_location.SearchTerm}\u201d" : label;
    }

    private void ShowError(Exception exception)
    {
        if (_loadOutcome is PageLoadOutcome.NotRequested or PageLoadOutcome.Loading)
            SetLoadOutcome(PageLoadOutcome.Failed);
        ErrorMessage = exception switch
        {
            EmbyApiException { ApplicationErrorCode: "ParentalControl" } => "This content is restricted by the account's parental controls.",
            EmbyApiException { IsAuthenticationFailure: true } => "Your session has expired. Sign out and connect again.",
            EmbyApiException { IsPermissionDenied: true } => "This account is not allowed to access this content.",
            EmbyApiException { StatusCode: System.Net.HttpStatusCode.NotFound } => "This content is no longer available on the server. Refresh the library.",
            EmbyApiException => "The server could not complete the request. Try refreshing this view.",
            TimeoutException => "The server took too long to respond. Check the connection and try again.",
            EmbyTransportException => "The server could not be reached. Check the connection and try again.",
            _ => "The server returned an unsupported response. Try refreshing this view."
        };
        HasError = true;
        if (!_sessionExpiredNotified && IsSessionExpired(exception))
        {
            _sessionExpiredNotified = true;
            SessionExpired?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool IsSessionExpired(Exception exception) => exception is EmbyApiException { IsAuthenticationFailure: true } apiException
        && !string.Equals(apiException.ApplicationErrorCode, "ParentalControl", StringComparison.Ordinal);

    private bool IsCurrentSession(int version) => version == _sessionVersion && _api is not null;
    private bool IsCurrentLibraryRequest(int sessionVersion, int requestVersion) =>
        IsCurrentSession(sessionVersion) && requestVersion == _libraryRequestVersion;
    private bool IsCurrentPage(int version) => version == _pageVersion && _api is not null && _pageCancellation?.IsCancellationRequested == false;
    private static bool IsExpectedFailure(Exception exception) => exception is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException;
    private void CancelSearch()
    {
        _pendingSearchCancellation = null;
        CancelAndDispose(ref _searchCancellation);
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var previous = source;
        source = null;
        previous?.Cancel();
        previous?.Dispose();
    }

    private void NotifyBrowseOptions()
    {
        if (!HasDetails) UpdateEmptyMessage();
        OnPropertyChanged(nameof(BrowseSortKey));
        OnPropertyChanged(nameof(BrowseSortDescending));
        OnPropertyChanged(nameof(BrowseWatchFilter));
        OnPropertyChanged(nameof(BrowseUnplayedOnly));
        OnPropertyChanged(nameof(BrowseOptionsVisibility));
        OnPropertyChanged(nameof(SelectedLibraryId));
        OnPropertyChanged(nameof(IsFavorites));
        OnPropertyChanged(nameof(IsSearch));
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(SearchEmptyVisibility));
        OnPropertyChanged(nameof(SearchText));
        OnPropertyChanged(nameof(EmptyVisibility));
        NotifyPresentation();
    }

    private static BrowseLocation Home() => new(LocationKind.Home, null, "Home");

    private static BrowseLocation HomeSectionLocation(HomeSection section) => section switch
    {
        HomeSection.NextUp => new(LocationKind.NextUp, null, "Next up"),
        HomeSection.RecentlyAdded => new(LocationKind.Latest, null, "Recently added", SortKey: "DateCreated", SortDescending: true),
        _ => new(LocationKind.Resume, null, "Continue watching")
    };

    private enum LocationKind { Home, Resume, NextUp, Latest, Library, Favorites, Search, Item, Person }
    private sealed record BrowseLocation(LocationKind Kind, string? ItemId, string Title, string? SeriesId = null,
        string? SearchTerm = null, BrowseLocation? SearchOrigin = null, string SortKey = "SortName",
        bool SortDescending = false, WatchStatusFilter WatchFilter = WatchStatusFilter.All, string? SeasonId = null, bool PreferSelectedSeason = false,
        string? SeriesName = null)
    {
        public bool IsHome => Kind == LocationKind.Home;
    }
}
