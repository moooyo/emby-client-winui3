using EmbyClient.Api;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;

namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private bool _isLoadingMore;
    private long _userDataRevision;
    private readonly Dictionary<string, (long Revision, UserItemDataDto Data)> _userDataChanges = new(StringComparer.Ordinal);

    public bool IsLoadingMore => IsBusy && _isLoadingMore;
    public bool IsInitialLoading => IsBusy && !_isLoadingMore && !HasDetails && !HasItems && !IsPerson
        && !(IsHome && Libraries.Count > 0);
    public bool IsDetailChildrenLoading => IsBusy && !_isLoadingMore && HasDetails && Detail.IsFolder && !HasItems;
    public bool IsBackgroundLoading => IsBusy && !IsLoadingMore && !IsInitialLoading && !IsDetailChildrenLoading
        && !IsSeasonSwitchPending && !IsHome && !IsPerson;
    public Visibility InitialLoadingVisibility => IsInitialLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BackgroundLoadingVisibility => IsBackgroundLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailChildrenLoadingVisibility => IsDetailChildrenLoading ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MoreLoadingVisibility => IsLoadingMore ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilterEmptyVisibility => EmptyVisibility == Visibility.Visible && SupportsBrowseOptions
        && BrowseWatchFilter != WatchStatusFilter.All ? Visibility.Visible : Visibility.Collapsed;

    private bool SupportsBrowseOptions => _location.Kind is LocationKind.Library or LocationKind.Favorites or LocationKind.Latest
        || HasSearchQuery;

    public Task ClearBrowseFilterAsync(CancellationToken cancellationToken = default) =>
        SetBrowseOptionsAsync(BrowseSortKey, BrowseSortDescending, WatchStatusFilter.All, cancellationToken);

    partial void OnIsBusyChanged(bool value)
    {
        if (!value) _isLoadingMore = false;
        NotifyPresentation();
    }

    partial void OnHasItemsChanged(bool value) => NotifyPresentation();
    partial void OnHasDetailsChanged(bool value) => NotifyPresentation();
    partial void OnHasErrorChanged(bool value) => NotifyPresentation();
    partial void OnIsHomeChanged(bool value) => NotifyPresentation();
    partial void OnDetailChanged(MediaCardViewModel value) => NotifyPresentation();
    partial void OnPlayableDetailChanged(MediaCardViewModel value) => NotifyDetailContext();

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(IsInitialLoading));
        OnPropertyChanged(nameof(InitialLoadingVisibility));
        OnPropertyChanged(nameof(IsBackgroundLoading));
        OnPropertyChanged(nameof(BackgroundLoadingVisibility));
        OnPropertyChanged(nameof(IsDetailChildrenLoading));
        OnPropertyChanged(nameof(DetailChildrenLoadingVisibility));
        OnPropertyChanged(nameof(IsLoadingMore));
        OnPropertyChanged(nameof(MoreLoadingVisibility));
        OnPropertyChanged(nameof(BrowseOptionsVisibility));
        OnPropertyChanged(nameof(FilterEmptyVisibility));
        OnPropertyChanged(nameof(InitialFailureVisibility));
        NotifyDetailContext();
    }

    private bool CanCommitPage(int version, CancellationToken cancellationToken) =>
        IsCurrentPage(version) && !cancellationToken.IsCancellationRequested;

    private bool CanCommitNavigation(int version, CancellationToken cancellationToken, CancellationTokenSource? searchOwner) =>
        CanCommitPage(version, cancellationToken) && (!HasPendingSearch || ReferenceEquals(searchOwner, _pendingSearchCancellation));

    private void UpdateEmptyMessage()
    {
        EmptyMessage = _api is null ? "Connect to a server to browse your media."
            : SupportsBrowseOptions && BrowseWatchFilter != WatchStatusFilter.All
            ? "No items match this watch status. Clear the filter to see all available items."
            : _location.Kind switch
            {
                LocationKind.Home => "Your home is ready for a first watch. Browse a media library to get started.",
                LocationKind.Resume => "Nothing to continue yet. Start watching a movie or episode from a library.",
                LocationKind.NextUp => "No next episodes are available. Your shows will appear here as you watch them.",
                LocationKind.Favorites => "No favorites yet. Open an item and choose Add favorite.",
                LocationKind.Search when !HasSearchQuery => "Search your library using the search box in the sidebar.",
                LocationKind.Search => "No matching items. Try a different title or a shorter search.",
                _ => "No items are available in this collection."
            };
    }

    private void RestorePageLifetime(int version, CancellationTokenSource source)
    {
        if (version != _pageVersion || !ReferenceEquals(_pageCancellation, source)
            || !source.IsCancellationRequested || _sessionCancellation?.IsCancellationRequested != false) return;
        CancelAndDispose(ref _pageCancellation);
        _pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token);
    }

    private static void ReconcileCards(ObservableCollection<MediaCardViewModel> cards, IEnumerable<BaseItemDto> items)
    {
        var incoming = items.Where(item => !string.IsNullOrWhiteSpace(item.Id)).DistinctBy(item => item.Id).ToArray();
        var incomingIds = incoming.Select(item => item.Id!).ToHashSet(StringComparer.Ordinal);
        var retained = cards.ToDictionary(item => item.Id, StringComparer.Ordinal);
        for (var index = cards.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(cards[index].Id)) cards.RemoveAt(index);
        }
        for (var index = 0; index < incoming.Length; index++)
        {
            var item = incoming[index];
            if (retained.TryGetValue(item.Id!, out var card))
            {
                var previousIndex = cards.IndexOf(card);
                if (previousIndex != index) cards.Move(previousIndex, index);
                card.ApplyItem(item);
            }
            else cards.Insert(index, new(item));
        }
    }

    private sealed record PageWindow(BaseItemDto[] Items, int NextIndex, int? TotalCount, bool HasMore);
    private sealed record HomeShelfResult(MediaShelfViewModel Shelf, BaseItemDto[] Items, Exception? Failure);

    private static async Task<PageWindow> QueryLoadedWindowAsync(EmbyApiClient api, BrowseLocation location,
        int loadedCount, CancellationToken cancellationToken)
    {
        var items = new List<BaseItemDto>();
        var offset = 0;
        int? totalCount;
        bool hasMore;
        do
        {
            var page = await QueryItemsAsync(api, location, offset, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(page.Items);
            offset += page.Items.Length;
            totalCount = page.TotalRecordCount;
            hasMore = page.Items.Length > 0 && (totalCount is { } total ? offset < total : page.Items.Length == PageSize);
        } while (hasMore && offset < Math.Max(PageSize, loadedCount));
        return new(items.ToArray(), offset, totalCount, hasMore);
    }

    private static async Task<HomeShelfResult[]> QueryHomeShelvesAsync(EmbyApiClient api,
        IEnumerable<BaseItemDto> libraries, CancellationToken cancellationToken,
        Action<HomeShelfResult[]>? progress = null)
    {
        var requests = new List<(MediaShelfViewModel Shelf, Func<Task<BaseItemDto[]>> Load)>
        {
            (new("Continue watching", true, HomeSection.ContinueWatching), async () =>
                (await api.GetResumeItemsAsync(new ItemQuery
                {
                    Limit = HomeShelfSize, MediaTypes = ["Video"], Fields = ListFields
                }, cancellationToken)).Items),
            (new("Next up", true, HomeSection.NextUp), async () =>
                (await api.GetNextUpAsync(new NextUpQuery { Limit = HomeShelfSize, Fields = ListFields }, cancellationToken)).Items)
        };
        foreach (var library in libraries.Where(item => !string.IsNullOrWhiteSpace(item.Id)))
        {
            var libraryId = library.Id!;
            var libraryTitle = string.IsNullOrWhiteSpace(library.Name) ? "Untitled item" : library.Name;
            requests.Add((new($"Recently added in {libraryTitle}", false, HomeSection.RecentlyAdded, libraryId),
                () => api.GetLatestItemsAsync(new LatestItemsQuery
                {
                    ParentId = libraryId, Limit = HomeShelfSize, Fields = ListFields, GroupItems = true
                }, cancellationToken)));
        }
        if (requests.Count == 2)
            requests.Add((new("Recently added", false, HomeSection.RecentlyAdded),
                () => api.GetLatestItemsAsync(new LatestItemsQuery { Limit = HomeShelfSize, Fields = ListFields }, cancellationToken)));

        using var slots = new SemaphoreSlim(3, 3);
        var completed = new HomeShelfResult?[requests.Count];
        async Task<HomeShelfResult> LoadShelfAsync((MediaShelfViewModel Shelf, Func<Task<BaseItemDto[]>> Load) request, int index)
        {
            await slots.WaitAsync(cancellationToken);
            HomeShelfResult result;
            try { result = new(request.Shelf, await request.Load(), null); }
            catch (Exception exception) when (IsExpectedFailure(exception)) { result = new(request.Shelf, [], exception); }
            finally { slots.Release(); }
            lock (completed)
            {
                completed[index] = result;
                progress?.Invoke(completed.OfType<HomeShelfResult>().ToArray());
            }
            return result;
        }
        return await Task.WhenAll(requests.Select((request, index) => LoadShelfAsync(request, index)));
    }

    private void ApplyHomeShelves(IEnumerable<HomeShelfResult> results, long userDataRevision)
    {
        var available = results.Select(result => result.Shelf.Section == HomeSection.ContinueWatching
            ? result with { Items = PreserveHiddenResumeItems(result.Items, userDataRevision) } : result).ToArray();
        var resumeIds = available.Where(result => result.Shelf.Section == HomeSection.ContinueWatching)
            .SelectMany(result => result.Failure is null ? result.Items : HomeRows
                .Where(row => row.Section == HomeSection.ContinueWatching).SelectMany(row => row.Items).Select(card => card.Item))
            .Where(item => new MediaCardViewModel(item).CanResume).Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var desired = new List<MediaShelfViewModel>();
        foreach (var result in available)
        {
            var shelf = HomeRows.FirstOrDefault(row => row.Section == result.Shelf.Section
                && row.LibraryId == result.Shelf.LibraryId && row.Title == result.Shelf.Title) ?? result.Shelf;
            if (result.Failure is null)
                ReconcileCards(shelf.Items, result.Shelf.Section == HomeSection.NextUp
                    ? result.Items.Where(item => !resumeIds.Contains(item.Id)) : result.Items);
            if (shelf.Items.Count > 0) desired.Add(shelf);
        }
        for (var index = HomeRows.Count - 1; index >= 0; index--)
        {
            if (!desired.Contains(HomeRows[index])) HomeRows.RemoveAt(index);
        }
        for (var index = 0; index < desired.Count; index++)
        {
            var previousIndex = HomeRows.IndexOf(desired[index]);
            if (previousIndex < 0) HomeRows.Insert(index, desired[index]);
            else if (previousIndex != index) HomeRows.Move(previousIndex, index);
        }
        HasItems = Libraries.Count > 0 || HomeRows.Count > 0;
        Subtitle = "Pick up where you left off, or find something new.";
        NotifyPresentation();
    }

    private async Task<Action> PreparePageRefreshAsync(EmbyApiClient api, BrowseLocation location, int loadedCount, long userDataRevision,
        Task<QueryResult<BaseItemDto>> librariesTask, CancellationToken cancellationToken)
    {
        // All requests stage data first. The caller alone commits the prepared operation
        // after the page, season, and library generations still match their owners.
        if (location.IsHome)
        {
            var libraries = await librariesTask;
            var shelves = await QueryHomeShelvesAsync(api, libraries.Items, cancellationToken);
            var failures = shelves.Select(shelf => shelf.Failure).OfType<Exception>().ToArray();
            if (failures.FirstOrDefault(IsSessionExpired) is { } expired) throw expired;
            return () =>
            {
                ApplyHomeShelves(shelves.Select(shelf => shelf with
                    { Items = PreserveNewerUserData(shelf.Items, userDataRevision) }), userDataRevision);
                if (failures.Length == 0) return;
                ShowError(failures[0]);
                ErrorMessage = "Some home sections could not be refreshed. Previously loaded content is still shown.";
            };
        }
        if (location.Kind != LocationKind.Item)
        {
            var page = await QueryLoadedWindowAsync(api, location, loadedCount, cancellationToken);
            return () =>
            {
                var preserved = PreserveHiddenResumeWindow(location, page, userDataRevision);
                ApplyPageWindow(preserved with { Items = PreserveNewerUserData(preserved.Items, userDataRevision) });
            };
        }

        var item = await api.GetItemAsync(location.ItemId!, cancellationToken);
        if (item.Type is "Episode" or "Season") item = item with
        {
            SeriesId = item.SeriesId ?? location.SeriesId,
            SeriesName = item.SeriesName ?? location.SeriesName,
            SeasonId = item.SeasonId ?? (item.Type == "Episode" ? location.SeasonId : null)
        };
        var detail = new MediaCardViewModel(item);
        if (!detail.IsFolder)
            return () =>
            {
                ApplyRefreshedDetail(PreserveNewerUserData(item, userDataRevision));
                PlayableDetail = Detail;
                Subtitle = item.SeriesName ?? "Item details";
                if (item.Type == "Episode") _ = LoadEpisodeContextAsync(api, Detail, _pageVersion, cancellationToken);
            };

        if (item.Type == "Series")
        {
            var seasons = await api.GetSeasonsAsync(detail.Id, cancellationToken);
            var orderedSeasons = seasons.Items.Where(season => !string.IsNullOrWhiteSpace(season.Id))
                .OrderBy(season => season.IndexNumber ?? int.MaxValue).ToArray();
            var selected = orderedSeasons.FirstOrDefault(season => season.Id == location.SeasonId)
                ?? orderedSeasons.FirstOrDefault(season => season.IndexNumber is > 0) ?? orderedSeasons.FirstOrDefault();
            var episodesTask = api.GetEpisodesAsync(detail.Id, selected?.Id, cancellationToken);
            var pageVersion = _pageVersion;
            var seasonVersion = _seasonRequestVersion;
            var resumeTask = location.PreferSelectedSeason ? Task.FromResult<MediaCardViewModel?>(null)
                : FindSeriesPlaybackAsync(api, detail.Id, true, pageVersion, cancellationToken);
            var nextTask = location.PreferSelectedSeason ? Task.FromResult<MediaCardViewModel?>(null)
                : FindSeriesPlaybackAsync(api, detail.Id, false, pageVersion, cancellationToken);
            var episodes = await episodesTask;
            return () =>
            {
                ApplyRefreshedDetail(PreserveNewerUserData(item, userDataRevision));
                ReconcileCards(Seasons, PreserveNewerUserData(orderedSeasons, userDataRevision));
                _location = _location with { SeasonId = selected?.Id };
                SelectedSeason = Seasons.FirstOrDefault(season => season.Id == selected?.Id);
                DetailItemsAreEpisodes = true;
                ReconcileItems(PreserveNewerUserData(episodes.Items, userDataRevision));
                _nextIndex = episodes.Items.Length;
                HasMore = false;
                ApplyRefreshedPlayable(null);
                EmptyMessage = selected is null ? "No episodes are available for this series." : "No episodes are available in this season.";
                UpdateCount(episodes.TotalRecordCount);
                OnPropertyChanged(nameof(SeasonSelectorVisibility));
                StartSeriesRecommendation(resumeTask, nextTask, pageVersion, seasonVersion, userDataRevision, cancellationToken);
            };
        }
        if (item.Type == "Season" && (item.SeriesId ?? location.SeriesId) is { } seriesId)
        {
            var episodes = await api.GetEpisodesAsync(seriesId, detail.Id, cancellationToken);
            return () =>
            {
                ApplyRefreshedDetail(PreserveNewerUserData(item, userDataRevision));
                DetailItemsAreEpisodes = true;
                ReconcileItems(PreserveNewerUserData(episodes.Items, userDataRevision));
                _nextIndex = episodes.Items.Length;
                HasMore = false;
                PlayableDetail = FindPlayable(Items);
                UpdateCount(episodes.TotalRecordCount);
            };
        }
        var children = await QueryLoadedWindowAsync(api, location, loadedCount, cancellationToken);
        return () =>
        {
            ApplyRefreshedDetail(PreserveNewerUserData(item, userDataRevision));
            ApplyPageWindow(children with { Items = PreserveNewerUserData(children.Items, userDataRevision) });
        };
    }

    private static async Task<BaseItemDto?> QuerySeriesPlaybackAsync(EmbyApiClient api, string seriesId,
        bool preferSelectedSeason, CancellationToken cancellationToken)
    {
        if (preferSelectedSeason) return null;
        var resumeTask = api.GetResumeItemsAsync(new ItemQuery
        {
            ParentId = seriesId, Recursive = true, IncludeItemTypes = ["Episode"], Limit = HomeShelfSize, Fields = ListFields
        }, cancellationToken);
        var nextTask = api.GetNextUpAsync(new NextUpQuery { SeriesId = seriesId, Limit = 1, Fields = ListFields }, cancellationToken);
        await Task.WhenAll(resumeTask, nextTask);
        return (await resumeTask).Items.Select(candidate => new MediaCardViewModel(candidate))
            .FirstOrDefault(card => IsSeriesEpisode(card, seriesId) && card.CanResume)?.Item
            ?? (await nextTask).Items.Select(candidate => new MediaCardViewModel(candidate))
            .FirstOrDefault(card => IsSeriesEpisode(card, seriesId))?.Item;
    }

    private static bool IsSeriesEpisode(MediaCardViewModel card, string seriesId) =>
        card.Item.Type == "Episode" && card.CanPlay && (string.IsNullOrWhiteSpace(card.Item.SeriesId) || card.Item.SeriesId == seriesId);

    private BaseItemDto PreserveNewerUserData(BaseItemDto item, long readRevision) =>
        item.Id is { } id && _userDataChanges.TryGetValue(id, out var change) && change.Revision > readRevision
            ? item with { UserData = change.Data } : item;

    private BaseItemDto[] PreserveNewerUserData(IEnumerable<BaseItemDto> items, long readRevision) =>
        items.Select(item => PreserveNewerUserData(item, readRevision)).ToArray();

    private void ApplyRefreshedDetail(BaseItemDto item)
    {
        foreach (var card in HistoryCards().Where(card => card.Id == item.Id).Distinct()) card.ApplyItem(item);
        if (Detail.Id == item.Id) Detail.ApplyItem(item);
        else Detail = new(item);
        Title = Detail.Title;
        HasDetails = true;
        OnPropertyChanged(nameof(CollectionVisibility));
        OnPropertyChanged(nameof(DetailChildrenVisibility));
        OnPropertyChanged(nameof(DetailEmptyVisibility));
        OnPropertyChanged(nameof(SeasonSelectorVisibility));
        OnPropertyChanged(nameof(CanSelectSeason));
        OnPropertyChanged(nameof(DetailChildrenTitle));
        OnPropertyChanged(nameof(PrimaryPlayLabel));
        OnPropertyChanged(nameof(PrimaryPlayVisibility));
        OnPropertyChanged(nameof(PrimaryPlayHint));
        NotifyPresentation();
    }

    private void ApplyRefreshedPlayable(BaseItemDto? item)
    {
        if (item is null) PlayableDetail = FindPlayable(Items);
        else if (Items.FirstOrDefault(card => card.Id == item.Id) is { } card)
        {
            card.ApplyItem(item);
            PlayableDetail = card;
        }
        else if (PlayableDetail.Id == item.Id) PlayableDetail.ApplyItem(item);
        else PlayableDetail = new(item);
        OnPropertyChanged(nameof(PrimaryPlayLabel));
        OnPropertyChanged(nameof(PrimaryPlayHint));
        OnPropertyChanged(nameof(PrimaryPlayVisibility));
    }

    private void ApplyPageWindow(PageWindow page)
    {
        ReconcileItems(page.Items);
        _nextIndex = page.NextIndex;
        HasMore = page.HasMore;
        UpdateCount(page.TotalCount);
    }
}
