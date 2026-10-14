using EmbyClient.Api;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private int _browseWindowStartIndex;
    private bool _isBrowseNavigationPending;
    private string? _pendingBrowseInitial;
    private BrowseLocation? _browseAlphabetScope;
    private CancellationTokenSource? _browseAlphabetCancellation;
    private int _browseAlphabetRequestVersion;
    private bool _browseAlphabetLoaded;
    private bool _browseAlphabetLoading;
    private string[]? _availableBrowseInitials;

    public int BrowseWindowStartIndex => _browseWindowStartIndex;
    public bool IsBrowseNavigationPending => _isBrowseNavigationPending;
    public string? PendingBrowseInitial => _pendingBrowseInitial;
    public IReadOnlyList<string>? AvailableBrowseInitials => _availableBrowseInitials;
    public bool SupportsBrowseBoundaryNavigation => _api is not null
        && (_pendingBrowseLocation ?? _location).Kind is LocationKind.Library or LocationKind.Favorites or LocationKind.Latest;
    public bool SupportsAlphabetNavigation => SupportsBrowseBoundaryNavigation
        && BrowseSortKey == "SortName" && !BrowseSortDescending;

    public Task<MediaCardViewModel?> JumpToBrowseInitialAsync(string initial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initial);
        var normalized = initial.Trim().ToUpperInvariant();
        if (normalized.Length != 1 || normalized[0] is not ('#' or >= 'A' and <= 'Z'))
            throw new ArgumentException("The library initial must be A through Z or #.", nameof(initial));
        return SupportsAlphabetNavigation
            ? NavigateBrowseWindowAsync(normalized, false, cancellationToken)
            : Task.FromResult<MediaCardViewModel?>(null);
    }

    public Task<MediaCardViewModel?> JumpToBrowseBoundaryAsync(bool end, CancellationToken cancellationToken = default)
    {
        if (!SupportsBrowseBoundaryNavigation) return Task.FromResult<MediaCardViewModel?>(null);
        if (!IsBusy && _pendingBrowseLocation is null && Items.Count > 0
            && (end ? !HasMore : _browseWindowStartIndex == 0))
            return Task.FromResult<MediaCardViewModel?>(end ? Items[^1] : Items[0]);
        return NavigateBrowseWindowAsync(null, end, cancellationToken);
    }

    public async Task LoadBrowseAlphabetAsync(CancellationToken cancellationToken = default)
    {
        if (!SupportsAlphabetNavigation || _api is null || _sessionCancellation is null
            || _pendingBrowseLocation is not null) return;
        SetBrowseAlphabetContext();
        if (_browseAlphabetLoaded || _browseAlphabetLoading) return;
        var scope = _browseAlphabetScope;
        if (scope is null) return;
        var api = _api;
        var sessionVersion = _sessionVersion;
        var requestVersion = ++_browseAlphabetRequestVersion;
        CancelAndDispose(ref _browseAlphabetCancellation);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _browseAlphabetCancellation = source;
        var token = source.Token;
        _browseAlphabetLoading = true;
        try
        {
            string[] initials;
            if (scope.QualityFilter is not null)
            {
                var quality = scope.QualityFilter == "4K" ? VideoQuality.FourK : VideoQuality.FullHd;
                var entries = await (scope.QualityCatalog ?? new QualityCatalog()).ReadAsync(api, scope, token);
                initials = entries.Where(entry => (entry.Quality & quality) != 0)
                    .Select(entry => BrowseAlphabetKey(entry.Item)).Distinct(StringComparer.Ordinal).ToArray();
            }
            else
            {
                var query = CreateNavigationCountQuery(scope) with { StartIndex = null, Limit = null };
                var prefixes = await api.GetItemPrefixesAsync(query, token);
                initials = prefixes.Where(prefix => !string.IsNullOrWhiteSpace(prefix.Name))
                    .Select(prefix => prefix.Name!.Trim().ToUpperInvariant())
                    .Select(prefix => prefix.Length == 1 && prefix[0] is >= 'A' and <= 'Z' ? prefix : "#")
                    .Distinct(StringComparer.Ordinal).ToArray();
            }
            if (CanCommitAlphabet()) _availableBrowseInitials = initials;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            // An older server can omit prefix discovery while all direct letter jumps remain available.
            if (CanCommitAlphabet() && IsSessionExpired(exception)) ShowError(exception);
        }
        finally
        {
            if (requestVersion == _browseAlphabetRequestVersion && scope == _browseAlphabetScope)
            {
                _browseAlphabetLoaded = !token.IsCancellationRequested;
                _browseAlphabetLoading = false;
                _browseAlphabetCancellation = null;
                OnPropertyChanged(nameof(AvailableBrowseInitials));
            }
        }

        bool CanCommitAlphabet() => IsCurrentSession(sessionVersion) && !token.IsCancellationRequested
            && requestVersion == _browseAlphabetRequestVersion && scope == _browseAlphabetScope;
    }

    private async Task<MediaCardViewModel?> NavigateBrowseWindowAsync(string? initial, bool end,
        CancellationToken cancellationToken)
    {
        if (_api is null || _sessionCancellation is null || cancellationToken.IsCancellationRequested) return null;
        var location = _pendingBrowseLocation ?? _location;
        var api = _api;
        var version = ++_pageVersion;
        var userDataRevision = _userDataRevision;
        CancelSearch();
        CancelAndDispose(ref _pageCancellation);
        var source = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token, cancellationToken);
        _pageCancellation = source;
        var token = source.Token;
        _pendingBrowseLocation = location;
        _isBrowseNavigationPending = true;
        _pendingBrowseInitial = initial;
        _isLoadingMore = false;
        IsBusy = true;
        HasError = false;
        ErrorMessage = string.Empty;
        NotifyBrowseOptions();
        try
        {
            var read = location.QualityFilter is not null
                ? await QueryQualityNavigationAsync(api, location, initial, end, token)
                : initial is not null
                    ? await QueryAlphabetNavigationAsync(api, location, initial, token)
                    : await QueryBoundaryNavigationAsync(api, location, end, token);
            if (!CanCommitPage(version, token) || HasPendingSearch) return null;
            // Keep the committed query, cards, and absolute paging offset until the new window is ready.
            _location = location;
            _pendingBrowseLocation = null;
            SetLumenGenreContext(location);
            ApplyPageWindow(read.Page with { Items = PreserveNewerUserData(read.Page.Items, userDataRevision) });
            SetLoadOutcome(PageLoadOutcome.Succeeded);
            var target = Items.ElementAtOrDefault(Math.Clamp(read.FocusIndex, 0, Math.Max(0, Items.Count - 1)));
            ViewportState = new(FocusedItemId: target?.Id, NavigationLibraryId: NavigationLibraryId);
            NotifyBrowseOptions();
            NotifyCollectionCommitted();
            return target;
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { return null; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(version, token) && !HasPendingSearch) ShowError(exception);
            return null;
        }
        finally
        {
            if (version == _pageVersion)
            {
                _pendingBrowseLocation = null;
                ResetBrowseNavigationPending();
                RestorePageLifetime(version, source);
                NotifyBrowseOptions();
                IsBusy = false;
            }
        }
    }

    private sealed record BrowseNavigationRead(PageWindow Page, int FocusIndex = 0);

    private static async Task<BrowseNavigationRead> QueryAlphabetNavigationAsync(EmbyApiClient api,
        BrowseLocation location, string initial, CancellationToken token)
    {
        if (initial == "#") return new(await QueryNavigationPageAsync(api, location, 0, token));
        var totalTask = QueryNavigationCountAsync(api, CreateNavigationCountQuery(location), token);
        var pageTask = api.GetItemsAsync(CreateItemsQuery(location, 0, PageSize, ListFields) with
        {
            NameStartsWithOrGreater = initial
        }, token);
        await Task.WhenAll(totalTask, pageTask);
        token.ThrowIfCancellationRequested();
        var total = await totalTask;
        var result = await pageTask;
        var remaining = RequireNavigationCount(result);
        if (remaining > total)
        {
            // Counts are separate server snapshots; retry once when a live update makes their complement invalid.
            total = await QueryNavigationCountAsync(api, CreateNavigationCountQuery(location), token);
            if (remaining > total)
                throw new EmbyProtocolException("The server returned inconsistent library navigation counts.");
        }
        // The official web client uses the range count as the complement of the full scoped count.
        var startIndex = total - remaining;
        if (result.Items.Length == 0)
        {
            var last = await QueryNavigationPageAsync(api, location, Math.Max(0, total - PageSize), token);
            return new(last, last.Items.Length - 1);
        }
        var nextIndex = AddNavigationCount(startIndex, result.Items.Length);
        return new(new(result.Items, nextIndex, total, nextIndex < total, startIndex));
    }

    private static async Task<BrowseNavigationRead> QueryBoundaryNavigationAsync(EmbyApiClient api,
        BrowseLocation location, bool end, CancellationToken token)
    {
        var startIndex = end
            ? Math.Max(0, await QueryNavigationCountAsync(api, CreateNavigationCountQuery(location), token) - PageSize)
            : 0;
        var page = await QueryNavigationPageAsync(api, location, startIndex, token);
        return new(page, end ? page.Items.Length - 1 : 0);
    }

    private static ItemQuery CreateNavigationCountQuery(BrowseLocation location) =>
        CreateItemsQuery(location, 0, 1, ["SortName"]) with
        {
            EnableImages = false,
            EnableImageTypes = null,
            ImageTypeLimit = null,
            EnableUserData = false
        };

    private static async Task<int> QueryNavigationCountAsync(EmbyApiClient api, ItemQuery query, CancellationToken token)
    {
        var result = await api.GetItemsAsync(query, token);
        token.ThrowIfCancellationRequested();
        return RequireNavigationCount(result);
    }

    private static int RequireNavigationCount(QueryResult<BaseItemDto> result)
    {
        if (result.TotalRecordCount is >= 0)
        {
            if (result.Items.Length > result.TotalRecordCount.Value || result.TotalRecordCount.Value > 0 && result.Items.Length == 0)
                throw new EmbyProtocolException("The server returned an invalid library navigation count.");
            return result.TotalRecordCount.Value;
        }
        if (result.TotalRecordCount is < 0)
            throw new EmbyProtocolException("The server returned a negative library navigation count.");
        if (result.Items.Length == 0) return 0;
        throw new EmbyProtocolException("The server did not provide the item count required for library navigation.");
    }

    private static async Task<PageWindow> QueryNavigationPageAsync(EmbyApiClient api, BrowseLocation location,
        int startIndex, CancellationToken token)
    {
        var result = await QueryItemsAsync(api, location, startIndex, token);
        token.ThrowIfCancellationRequested();
        if (startIndex > 0 && result.Items.Length == 0 && result.TotalRecordCount is >= 0)
        {
            // A live library may shrink between its count response and the requested page.
            startIndex = Math.Max(0, result.TotalRecordCount.Value - PageSize);
            result = await QueryItemsAsync(api, location, startIndex, token);
            token.ThrowIfCancellationRequested();
        }
        var nextIndex = AddNavigationCount(startIndex, result.Items.Length);
        var hasMore = result.Items.Length > 0 && (result.TotalRecordCount is { } total
            ? nextIndex < total : result.Items.Length == PageSize);
        return new(result.Items, nextIndex, result.TotalRecordCount, hasMore, startIndex);
    }

    private static async Task<BrowseNavigationRead> QueryQualityNavigationAsync(EmbyApiClient api,
        BrowseLocation location, string? initial, bool end, CancellationToken token)
    {
        var quality = location.QualityFilter == "4K" ? VideoQuality.FourK : VideoQuality.FullHd;
        var entries = await (location.QualityCatalog ?? new QualityCatalog()).ReadAsync(api, location, token);
        token.ThrowIfCancellationRequested();
        // Resolution filtering already owns a catalog in the server's order; reuse it without rescanning or sorting.
        var matches = entries.Where(entry => (entry.Quality & quality) != 0).Select(entry => entry.Item).ToArray();
        var startIndex = end ? Math.Max(0, matches.Length - PageSize) : 0;
        var focusLast = end;
        if (initial is not null)
        {
            startIndex = initial == "#" ? 0 : Array.FindIndex(matches, item => BrowseAlphabetKey(item) == initial);
            if (startIndex < 0)
                startIndex = Array.FindIndex(matches, item => BrowseAlphabetKey(item) is { Length: 1 } key
                    && key[0] is >= 'A' and <= 'Z' && key[0] > initial[0]);
            if (startIndex < 0)
            {
                startIndex = Math.Max(0, matches.Length - PageSize);
                focusLast = true;
            }
        }
        var items = matches.Skip(startIndex).Take(PageSize).ToArray();
        var nextIndex = startIndex + items.Length;
        return new(new(items, nextIndex, matches.Length, nextIndex < matches.Length, startIndex), focusLast ? items.Length - 1 : 0);
    }

    private static string BrowseAlphabetKey(BaseItemDto item)
    {
        var first = (item.SortName ?? item.Name ?? string.Empty).TrimStart().FirstOrDefault();
        return first is >= 'A' and <= 'Z' or >= 'a' and <= 'z' ? char.ToUpperInvariant(first).ToString() : "#";
    }

    private static int AddNavigationCount(int startIndex, int count)
    {
        if (startIndex < 0 || count < 0 || startIndex > int.MaxValue - count)
            throw new EmbyProtocolException("The server returned an invalid library navigation count.");
        return startIndex + count;
    }

    private void ResetBrowseNavigationPending()
    {
        _isBrowseNavigationPending = false;
        _pendingBrowseInitial = null;
    }

    private void ResetBrowseWindow()
    {
        _browseWindowStartIndex = 0;
        ResetBrowseNavigationPending();
    }

    private void ResetBrowseAlphabet()
    {
        _browseAlphabetRequestVersion++;
        CancelAndDispose(ref _browseAlphabetCancellation);
        _browseAlphabetScope = null;
        _browseAlphabetLoaded = _browseAlphabetLoading = false;
        _availableBrowseInitials = null;
    }

    private void SetBrowseAlphabetContext()
    {
        var scope = SupportsAlphabetNavigation
            ? (_pendingBrowseLocation ?? _location) with { SortKey = "SortName", SortDescending = false }
            : null;
        if (scope == _browseAlphabetScope) return;
        ResetBrowseAlphabet();
        _browseAlphabetScope = scope;
    }

    private void NotifyAlphabetNavigation()
    {
        SetBrowseAlphabetContext();
        OnPropertyChanged(nameof(BrowseWindowStartIndex));
        OnPropertyChanged(nameof(SupportsBrowseBoundaryNavigation));
        OnPropertyChanged(nameof(SupportsAlphabetNavigation));
        OnPropertyChanged(nameof(IsBrowseNavigationPending));
        OnPropertyChanged(nameof(PendingBrowseInitial));
        OnPropertyChanged(nameof(AvailableBrowseInitials));
    }
}
