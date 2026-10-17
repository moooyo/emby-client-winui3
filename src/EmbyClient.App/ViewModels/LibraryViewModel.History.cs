using EmbyClient.Api;
using Microsoft.UI.Xaml;

namespace EmbyClient.App.ViewModels;

public sealed record BrowseViewportState(double VerticalOffset = 0, double DetailOffset = 0,
    double ChildrenOffset = 0, string? AnchorId = null, int AnchorIndex = 0, string? FocusedItemId = null,
    bool OverviewExpanded = false, bool EpisodeList = false, string? NavigationLibraryId = null,
    double CollectionWidth = 0, int HomePageIndex = 0, int DetailPageIndex = 0);

public sealed partial class LibraryViewModel
{
    private const int MaximumHistoryEntries = 24;
    private readonly Stack<BrowseSnapshot> _forwardHistory = new();
    private string? _navigationLibraryId;
    private readonly HashSet<PersonDetailsViewModel> _people = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PersonDetailsViewModel> _pendingPeople = new(ReferenceEqualityComparer.Instance);
    public BrowseViewportState ViewportState { get; set; } = new();
    public event EventHandler? BrowseStateCapturing;
    public event EventHandler? BrowseStateRestored;
    public int NavigationRevision => _pageVersion;
    public bool CanGoForward => _forwardHistory.Count > 0;
    public string? NavigationLibraryId => _navigationLibraryId;
    public PersonDetailsViewModel? ActivePerson { get; private set; }
    public bool IsPerson => _location.Kind == LocationKind.Person;
    public Visibility PersonVisibility => IsPerson ? Visibility.Visible : Visibility.Collapsed;

    public Task GoForwardAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (_api is null || _sessionCancellation is null || _forwardHistory.Count == 0) return Task.CompletedTask;
        var current = CaptureBrowseState();
        var destination = _forwardHistory.Pop();
        CancelSearch();
        PushBoundedHistory(_history, current);
        return RestoreBrowseStateAsync(destination);
    }

    private static void PushBoundedHistory(Stack<BrowseSnapshot> history, BrowseSnapshot snapshot)
    {
        history.Push(snapshot);
        if (history.Count <= MaximumHistoryEntries) return;
        var retained = history.Take(MaximumHistoryEntries).Reverse().ToArray();
        history.Clear();
        foreach (var entry in retained) history.Push(entry);
    }

    private void UpdateNavigationAvailability()
    {
        RetireUnreachablePeople();
        CanGoBack = _history.Count > 0;
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void RetireUnreachablePeople()
    {
        if (_people.Count == 0) return;
        var retained = new HashSet<PersonDetailsViewModel>(_pendingPeople, ReferenceEqualityComparer.Instance);
        if (ActivePerson is { } active) retained.Add(active);
        foreach (var snapshot in _history.Concat(_forwardHistory))
            if (snapshot.Person is { } person) retained.Add(person);
        foreach (var person in _people.Where(person => !retained.Contains(person)).ToArray())
        {
            _people.Remove(person);
            person.Dispose();
        }
    }

    private void SetNavigationLibraryId(string? libraryId) =>
        SetProperty(ref _navigationLibraryId, libraryId, nameof(NavigationLibraryId));

    private void InvalidateQualityCatalogs()
    {
        foreach (var catalog in _history.Concat(_forwardHistory).Select(state => state.Location.QualityCatalog)
            .Append(_location.QualityCatalog).Append(_pendingBrowseLocation?.QualityCatalog).OfType<QualityCatalog>().Distinct())
            catalog.Invalidate();
    }

    private BrowseSnapshot CaptureBrowseState()
    {
        BrowseStateCapturing?.Invoke(this, EventArgs.Empty);
        return new(_location, Items.ToArray(), HomeRows.ToArray(), Seasons.ToArray(), Detail, PlayableDetail,
            NextEpisode, SelectedSeason, Title, Subtitle, EmptyMessage, ErrorMessage, HasError, HasItems,
            HasMore, HasDetails, DetailItemsAreEpisodes, _nextIndex, _browseWindowStartIndex, _loadOutcome, ViewportState, ActivePerson,
            TotalItemsCount, CaptureLumenSearchPeopleState(), NavigationLibraryId)
        {
            HomeRowsStale = _location.IsHome && _homeRowsStale,
            SeriesEpisodeCount = SeriesEpisodeCount
        };
    }

    private Task RestoreBrowseStateAsync(BrowseSnapshot state)
    {
        if (_api is null || _sessionCancellation is null) return Task.CompletedTask;
        _pageVersion++;
        _seasonRequestVersion++;
        CancelAndDispose(ref _seasonCancellation);
        CancelAndDispose(ref _pageCancellation);
        _pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_sessionCancellation.Token);
        _pendingBrowseLocation = null;
        ResetBrowseNavigationPending();
        _pendingSeasonId = null;
        SetSeriesInitializing(state.HasDetails && state.Detail.Item.Type == "Series");
        _location = state.Location;
        _homeRowsStale = state.Location.IsHome && state.HomeRowsStale;
        SetNavigationLibraryId(state.NavigationLibraryId);
        ActivePerson = state.Person;
        Title = state.Title;
        Subtitle = state.Subtitle;
        TotalItemsCount = state.TotalCount;
        EmptyMessage = state.EmptyMessage;
        ErrorMessage = state.ErrorMessage;
        HasError = state.HasError;
        IsHome = state.Location.IsHome;
        HasDetails = state.HasDetails;
        Detail = state.Detail;
        ResetSeriesEpisodeCount(state.SeriesEpisodeCount);
        PlayableDetail = state.Playable;
        NextEpisode = state.NextEpisode;
        DetailItemsAreEpisodes = state.Episodes;
        Items.Clear();
        foreach (var item in state.Items) Items.Add(item);
        HomeRows.Clear();
        foreach (var row in state.Rows) HomeRows.Add(row);
        Seasons.Clear();
        foreach (var season in state.Seasons) Seasons.Add(season);
        SelectedSeason = state.Season;
        SetSeriesInitializing(false);
        HasItems = state.HasItems;
        HasMore = state.HasMore;
        _nextIndex = state.NextIndex;
        _browseWindowStartIndex = state.WindowStartIndex;
        RestoreLumenSearchPeopleState(state.SearchPeople);
        SetLoadOutcome(state.Outcome);
        ViewportState = state.Viewport;
        IsBusy = false;
        SelectedHomeSection = state.Location.Kind switch
        {
            LocationKind.NextUp => HomeSection.NextUp,
            LocationKind.Latest => HomeSection.RecentlyAdded,
            _ => HomeSection.ContinueWatching
        };
        UpdateNavigationAvailability();
        NotifyBrowseOptions();
        NotifyDetailContext();
        NotifyPersonState();
        BrowseStateRestored?.Invoke(this, EventArgs.Empty);
        if (state.Location.IsHome && state.HomeRowsStale
            || state.Outcome == PageLoadOutcome.Loading && state.Location.Kind is not (LocationKind.Search or LocationKind.Person))
            return ResumeRestoredPageAsync(state.Viewport);
        if (SeriesEpisodeCount is null && HasDetails && Detail.Item.Type == "Series") _ = RefreshSeriesEpisodeCountAsync();
        return ResumeLumenRestoredSearchAsync(state.Outcome);
    }

    private async Task ResumeRestoredPageAsync(BrowseViewportState viewport)
    {
        // A page left before its first response needs a new read instead of a permanently empty snapshot.
        var refresh = RefreshAsync();
        var revision = _pageVersion;
        await refresh;
        if (_api is null || revision != _pageVersion) return;
        ViewportState = viewport;
        BrowseStateRestored?.Invoke(this, EventArgs.Empty);
    }

    public Task ShowPersonAsync(PersonCardViewModel person, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        var viewModel = CreatePersonDetails(person);
        if (viewModel is null) return Task.CompletedTask;
        _people.Add(viewModel);
        _pendingPeople.Add(viewModel);
        try
        {
            CancelSearch();
            return NavigateAsync(new(LocationKind.Person, person.Id, person.Name), true, cancellationToken, viewModel);
        }
        finally
        {
            // Navigation transfers ownership before its first await, including during capture callbacks.
            _pendingPeople.Remove(viewModel);
            RetireUnreachablePeople();
        }
    }

    private void NotifyPersonState()
    {
        OnPropertyChanged(nameof(ActivePerson));
        OnPropertyChanged(nameof(IsPerson));
        OnPropertyChanged(nameof(PersonVisibility));
        OnPropertyChanged(nameof(BrowseVisibility));
        OnPropertyChanged(nameof(HeaderVisibility));
        OnPropertyChanged(nameof(CollectionVisibility));
    }

    private IEnumerable<MediaCardViewModel> HistoryCards()
    {
        foreach (var current in _history.Concat(_forwardHistory))
        {
            yield return current.Detail;
            yield return current.Playable;
            if (current.NextEpisode is { } nextEpisode) yield return nextEpisode;
            foreach (var item in current.Items.Concat(current.Seasons).Concat(current.Rows.SelectMany(row => row.Items)))
                yield return item;
            foreach (var person in current.SearchPeople.Items) yield return person;
        }
        foreach (var item in _people.SelectMany(person => person.Works)) yield return item;
    }

    private sealed record BrowseSnapshot(BrowseLocation Location, MediaCardViewModel[] Items,
        MediaShelfViewModel[] Rows, MediaCardViewModel[] Seasons, MediaCardViewModel Detail,
        MediaCardViewModel Playable, MediaCardViewModel? NextEpisode, MediaCardViewModel? Season,
        string Title, string Subtitle, string EmptyMessage, string ErrorMessage, bool HasError,
        bool HasItems, bool HasMore, bool HasDetails, bool Episodes, int NextIndex, int WindowStartIndex, PageLoadOutcome Outcome,
        BrowseViewportState Viewport, PersonDetailsViewModel? Person,
        int? TotalCount, LumenSearchPeopleSnapshot SearchPeople, string? NavigationLibraryId)
    {
        public bool HomeRowsStale { get; init; }
        public int? SeriesEpisodeCount { get; init; }
    }
}
