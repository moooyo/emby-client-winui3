using CommunityToolkit.Mvvm.ComponentModel;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public enum HomeSection { ContinueWatching, NextUp, RecentlyAdded }

public sealed partial class LibraryViewModel : ObservableObject
{
    private const int PageSize = 48;
    private static readonly string[] ListFields = ["PrimaryImageAspectRatio", "Overview"];
    private static readonly string[] SearchTypes = ["Movie", "Series", "Episode", "Video", "MusicVideo", "Audio", "MusicAlbum", "BoxSet"];
    private readonly ImageCache _images = new();
    private readonly Stack<BrowseLocation> _history = new();
    private EmbyApiClient? _api;
    private UserDto? _user;
    private string _serverId = string.Empty;
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _pageCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _pendingSearchCancellation;
    private BrowseLocation _location = Home(HomeSection.ContinueWatching);
    private int _sessionVersion;
    private int _pageVersion;
    private int _libraryRequestVersion;
    private int _nextIndex;
    private bool _sessionExpiredNotified;

    public ObservableCollection<MediaCardViewModel> Items { get; } = [];
    public ObservableCollection<MediaCardViewModel> Libraries { get; } = [];
    public event EventHandler? SessionExpired;

    [ObservableProperty]
    public partial string Title { get; set; } = "Your library";

    [ObservableProperty]
    public partial string Subtitle { get; set; } = "Connect to a server to browse your media.";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadingVisibility), nameof(EmptyVisibility), nameof(CanLoadMore))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditDetails))]
    public partial bool IsMutating { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadMoreVisibility), nameof(CanLoadMore))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HomeVisibility))]
    public partial bool IsHome { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailVisibility), nameof(CollectionVisibility), nameof(EmptyVisibility), nameof(CanEditDetails))]
    public partial bool HasDetails { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyVisibility))]
    public partial bool HasItems { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = "No items are available.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CollectionVisibility), nameof(EmptyVisibility))]
    public partial MediaCardViewModel Detail { get; set; } = new(new BaseItemDto());

    [ObservableProperty]
    public partial HomeSection SelectedHomeSection { get; set; }

    public Visibility LoadingVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HomeVisibility => IsHome ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => HasDetails ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CollectionVisibility => HasDetails && !Detail.IsFolder ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EmptyVisibility => !IsBusy && !HasError && !HasItems && CollectionVisibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility LoadMoreVisibility => HasMore ? Visibility.Visible : Visibility.Collapsed;
    public bool CanLoadMore => HasMore && !IsBusy;
    public bool CanEditDetails => HasDetails && !IsMutating;
    public bool IsPlaybackAllowed => _user?.Policy?.EnableMediaPlayback != false;
    public bool HasPendingSearch => _pendingSearchCancellation?.IsCancellationRequested == false;
    public string SearchText => _location.Kind == LocationKind.Search ? _location.SearchTerm ?? string.Empty : string.Empty;
    public string? SelectedLibraryId => _location.Kind == LocationKind.Library ? _location.ItemId : null;
    public bool IsFavorites => _location.Kind == LocationKind.Favorites;

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
        _sessionCancellation = new CancellationTokenSource();
        var version = _sessionVersion;
        var pageVersion = _pageVersion;
        var libraryRequestVersion = ++_libraryRequestVersion;
        string? navigationError = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        IsBusy = true;
        try
        {
            if (!await LoadLibrariesAsync(api, version, libraryRequestVersion, linked.Token)) return;
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
            var page = NavigateAsync(Home(HomeSection.ContinueWatching), false, cancellationToken);
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
        CancelSearch();
        CancelAndDispose(ref _pageCancellation);
        CancelAndDispose(ref _sessionCancellation);
        _images.Clear();
        _api = null;
        _user = null;
        _serverId = string.Empty;
        _location = Home(HomeSection.ContinueWatching);
        _sessionExpiredNotified = false;
        _history.Clear();
        Items.Clear();
        Libraries.Clear();
        Detail = new(new BaseItemDto());
        Title = "Your library";
        Subtitle = "Connect to a server to browse your media.";
        HasDetails = HasItems = HasMore = IsHome = IsBusy = IsMutating = CanGoBack = HasError = false;
        ErrorMessage = string.Empty;
        EmptyMessage = "Connect to a server to browse your media.";
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        CancelSearch();
        if (_api is null || _sessionCancellation is null) return;
        var sessionVersion = _sessionVersion;
        var libraryRequestVersion = ++_libraryRequestVersion;
        var api = _api;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        var page = NavigateAsync(_location, false, cancellationToken);
        var pageVersion = _pageVersion;
        Exception? failure = null;
        try
        {
            await LoadLibrariesAsync(api, sessionVersion, libraryRequestVersion, linked.Token);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception)) { failure = exception; }
        await page;
        if (failure is not null && IsCurrentLibraryRequest(sessionVersion, libraryRequestVersion) && IsCurrentPage(pageVersion)
            && !linked.IsCancellationRequested && (!HasError || IsSessionExpired(failure))) ShowError(failure);
    }

    private async Task<bool> LoadLibrariesAsync(EmbyApiClient api, int sessionVersion, int requestVersion,
        CancellationToken cancellationToken)
    {
        var result = await api.GetViewsAsync(cancellationToken: cancellationToken);
        if (!IsCurrentLibraryRequest(sessionVersion, requestVersion) || cancellationToken.IsCancellationRequested) return false;
        Libraries.Clear();
        foreach (var item in result.Items.Where(item => !string.IsNullOrWhiteSpace(item.Id))) Libraries.Add(new(item));
        return true;
    }

    public Task ShowHomeAsync(HomeSection section = HomeSection.ContinueWatching, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(Home(section), !_location.IsHome, cancellationToken);
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

    public Task ShowItemAsync(MediaCardViewModel item, CancellationToken cancellationToken = default)
    {
        CancelSearch();
        return NavigateAsync(new(LocationKind.Item, item.Id, item.Title, item.Item.SeriesId), true, cancellationToken);
    }

    public Task GoBackAsync(CancellationToken cancellationToken = default)
    {
        var searchDestination = _location.Kind == LocationKind.Search ? _location.SearchOrigin : null;
        CancelSearch();
        if (searchDestination is not null) return NavigateAsync(searchDestination, false, cancellationToken);
        return _history.TryPop(out var location) ? NavigateAsync(location, false, cancellationToken) : Task.CompletedTask;
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
            if (term.Length == 0)
            {
                var destination = _location.Kind == LocationKind.Search ? _location.SearchOrigin ?? Home(HomeSection.ContinueWatching) : _location;
                await NavigateAsync(destination, false, token);
            }
            else
            {
                var origin = _location.Kind == LocationKind.Search ? _location.SearchOrigin : _location;
                await NavigateAsync(new(LocationKind.Search, null, $"Search: {term}", SearchTerm: term, SearchOrigin: origin), false, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_pendingSearchCancellation, source)) _pendingSearchCancellation = null;
        }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadMore || _api is null || _pageCancellation is null) return;
        var version = _pageVersion;
        var api = _api;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token, cancellationToken);
        IsBusy = true;
        HasError = false;
        try
        {
            var result = await QueryItemsAsync(api, _location, _nextIndex, linked.Token);
            if (!IsCurrentPage(version)) return;
            AppendItems(result.Items);
            _nextIndex += result.Items.Length;
            HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? _nextIndex < total : result.Items.Length == PageSize);
            UpdateCount(result.TotalRecordCount);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentPage(version) && !linked.IsCancellationRequested) ShowError(exception);
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
    {
        if (_api is null || _user?.Id is null || _sessionCancellation is null) return null;
        var version = _sessionVersion;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        try
        {
            var bytes = await _images.GetAsync(_api, _serverId, _user.Id, item.Item, width, height, linked.Token);
            return IsCurrentSession(version) && !linked.IsCancellationRequested ? bytes : null;
        }
        catch (EmbyApiException exception) when (IsSessionExpired(exception))
        {
            if (IsCurrentSession(version) && !linked.IsCancellationRequested) ShowError(exception);
            return null;
        }
    }

    private async Task NavigateAsync(BrowseLocation location, bool remember, CancellationToken cancellationToken)
    {
        if (_api is null || _sessionCancellation is null) return;
        CancelAndDispose(ref _pageCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _pageCancellation = source;
        var version = ++_pageVersion;
        var api = _api;
        if (remember && location != _location) _history.Push(_location);
        _location = location;
        CanGoBack = _history.Count > 0 || location.SearchOrigin is not null;
        Title = location.Title;
        Subtitle = string.Empty;
        Items.Clear();
        HasItems = HasMore = HasDetails = false;
        IsHome = location.IsHome;
        HasError = false;
        ErrorMessage = string.Empty;
        _nextIndex = 0;
        IsBusy = true;
        EmptyMessage = location.Kind switch
        {
            LocationKind.Resume => "Nothing to continue yet. Start watching a movie or episode from a library.",
            LocationKind.NextUp => "No next episodes are available. Your shows will appear here as you watch them.",
            LocationKind.Favorites => "No favorites yet. Open an item and choose Add favorite.",
            LocationKind.Search => "No matching items. Try a different title or a shorter search.",
            _ => "No items are available in this collection."
        };
        if (location.IsHome) SelectedHomeSection = location.Kind switch
        {
            LocationKind.NextUp => HomeSection.NextUp,
            LocationKind.Latest => HomeSection.RecentlyAdded,
            _ => HomeSection.ContinueWatching
        };

        try
        {
            QueryResult<BaseItemDto> result;
            if (location.Kind == LocationKind.Item)
            {
                var item = await api.GetItemAsync(location.ItemId!, source.Token);
                if (!IsCurrentPage(version)) return;
                Detail = new(item);
                Title = Detail.Title;
                HasDetails = true;
                if (!Detail.IsFolder)
                {
                    Subtitle = item.SeriesName ?? "Item details";
                    return;
                }
                if (item.Type == "Series")
                {
                    result = await api.GetSeasonsAsync(Detail.Id, source.Token);
                }
                else if (item.Type == "Season" && (item.SeriesId ?? location.SeriesId) is { } seriesId)
                {
                    result = await api.GetEpisodesAsync(seriesId, Detail.Id, source.Token);
                }
                else
                {
                    result = await QueryItemsAsync(api, location, 0, source.Token);
                    if (!IsCurrentPage(version)) return;
                    HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? result.Items.Length < total : result.Items.Length == PageSize);
                }
            }
            else if (location.Kind == LocationKind.Latest)
            {
                var latest = await api.GetLatestItemsAsync(new LatestItemsQuery { Limit = PageSize, Fields = ListFields, GroupItems = true }, source.Token);
                result = new QueryResult<BaseItemDto> { Items = latest, TotalRecordCount = latest.Length };
            }
            else
            {
                result = await QueryItemsAsync(api, location, 0, source.Token);
                if (!IsCurrentPage(version)) return;
                HasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total ? result.Items.Length < total : result.Items.Length == PageSize);
            }

            if (!IsCurrentPage(version)) return;
            AppendItems(result.Items);
            _nextIndex = result.Items.Length;
            UpdateCount(result.TotalRecordCount);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentPage(version)) ShowError(exception);
        }
        finally
        {
            if (version == _pageVersion) IsBusy = false;
        }
    }

    private static Task<QueryResult<BaseItemDto>> QueryItemsAsync(EmbyApiClient api, BrowseLocation location, int offset, CancellationToken cancellationToken)
    {
        if (location.Kind == LocationKind.NextUp)
            return api.GetNextUpAsync(new NextUpQuery { StartIndex = offset, Limit = PageSize, Fields = ListFields }, cancellationToken);
        if (location.Kind == LocationKind.Resume)
            return api.GetResumeItemsAsync(new ItemQuery { StartIndex = offset, Limit = PageSize, MediaTypes = ["Video"], Fields = ListFields }, cancellationToken);
        return api.GetItemsAsync(new ItemQuery
        {
            ParentId = location.Kind is LocationKind.Library or LocationKind.Item ? location.ItemId : null,
            StartIndex = offset,
            Limit = PageSize,
            Recursive = location.Kind is LocationKind.Search or LocationKind.Favorites,
            SearchTerm = location.SearchTerm,
            IsFavorite = location.Kind == LocationKind.Favorites ? true : null,
            IncludeItemTypes = location.Kind == LocationKind.Search ? SearchTypes : null,
            SortBy = ["SortName"],
            SortOrder = ["Ascending"],
            Fields = ListFields,
            EnableImageTypes = ["Primary", "Thumb"],
            ImageTypeLimit = 1
        }, cancellationToken);
    }

    private async Task MutateUserDataAsync(bool favorite, CancellationToken cancellationToken)
    {
        if (!CanEditDetails || _api is null || _sessionCancellation is null || string.IsNullOrWhiteSpace(Detail.Id)) return;
        var version = _sessionVersion;
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
            if (!IsCurrentSession(version)) return;
            foreach (var card in Items.Where(item => item.Id == id)) card.ApplyUserData(result);
            if (Detail.Id == id) Detail.ApplyUserData(result);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (IsCurrentSession(version) && !linked.IsCancellationRequested) ShowError(exception);
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

    private void UpdateCount(int? total)
    {
        var count = total is { } value && value >= Items.Count ? value : Items.Count;
        var label = count == 1 ? "1 item" : $"{count:N0} items";
        Subtitle = HasDetails ? $"{(Detail.Item.Type == "Series" ? "Seasons" : Detail.Item.Type == "Season" ? "Episodes" : "Contents")} · {label}" : label;
    }

    private void ShowError(Exception exception)
    {
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

    private static BrowseLocation Home(HomeSection section) => section switch
    {
        HomeSection.NextUp => new(LocationKind.NextUp, null, "Next up"),
        HomeSection.RecentlyAdded => new(LocationKind.Latest, null, "Recently added"),
        _ => new(LocationKind.Resume, null, "Continue watching")
    };

    private enum LocationKind { Resume, NextUp, Latest, Library, Favorites, Search, Item }
    private sealed record BrowseLocation(LocationKind Kind, string? ItemId, string Title, string? SeriesId = null,
        string? SearchTerm = null, BrowseLocation? SearchOrigin = null)
    {
        public bool IsHome => Kind is LocationKind.Resume or LocationKind.NextUp or LocationKind.Latest;
    }
}
