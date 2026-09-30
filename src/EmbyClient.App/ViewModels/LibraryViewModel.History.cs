using EmbyClient.Api;
using Microsoft.UI.Xaml;

namespace EmbyClient.App.ViewModels;

public sealed record BrowseViewportState(double VerticalOffset = 0, double DetailOffset = 0,
    double ChildrenOffset = 0, string? AnchorId = null, int AnchorIndex = 0, string? FocusedItemId = null,
    bool OverviewExpanded = false, bool EpisodeList = false, string? NavigationLibraryId = null,
    double CollectionWidth = 0);

public sealed partial class LibraryViewModel
{
    private BrowseSnapshot? _searchOriginSnapshot;
    private readonly HashSet<PersonDetailsViewModel> _people = [];
    public BrowseViewportState ViewportState { get; set; } = new();
    public event EventHandler? BrowseStateCapturing;
    public event EventHandler? BrowseStateRestored;
    public int NavigationRevision => _pageVersion;
    public PersonDetailsViewModel? ActivePerson { get; private set; }
    public bool IsPerson => _location.Kind == LocationKind.Person;
    public Visibility PersonVisibility => IsPerson ? Visibility.Visible : Visibility.Collapsed;

    private BrowseSnapshot CaptureBrowseState()
    {
        BrowseStateCapturing?.Invoke(this, EventArgs.Empty);
        return new(_location, Items.ToArray(), HomeRows.ToArray(), Seasons.ToArray(), Detail, PlayableDetail,
            NextEpisode, SelectedSeason, Title, Subtitle, EmptyMessage, ErrorMessage, HasError, HasItems,
            HasMore, HasDetails, DetailItemsAreEpisodes, _nextIndex, _loadOutcome, ViewportState, ActivePerson,
            _searchOriginSnapshot, TotalItemsCount, CaptureLumenSearchPeopleState());
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
        _pendingSeasonId = null;
        SetSeriesInitializing(state.HasDetails && state.Detail.Item.Type == "Series");
        _location = state.Location;
        _searchOriginSnapshot = state.SearchOrigin;
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
        RestoreLumenSearchPeopleState(state.SearchPeople);
        SetLoadOutcome(state.Outcome);
        ViewportState = state.Viewport;
        IsBusy = false;
        CanGoBack = _history.Count > 0 || _searchOriginSnapshot is not null;
        NotifyBrowseOptions();
        NotifyDetailContext();
        NotifyPersonState();
        BrowseStateRestored?.Invoke(this, EventArgs.Empty);
        return ResumeLumenRestoredSearchAsync(state.Outcome);
    }

    public Task ShowPersonAsync(PersonCardViewModel person, CancellationToken cancellationToken = default)
    {
        var viewModel = CreatePersonDetails(person);
        if (viewModel is null) return Task.CompletedTask;
        _people.Add(viewModel);
        CancelSearch();
        return NavigateAsync(new(LocationKind.Person, person.Id, person.Name), true, cancellationToken, viewModel);
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
        foreach (var state in _history.Concat(_searchOriginSnapshot is { } searchOrigin ? [searchOrigin] : []))
        {
            for (BrowseSnapshot? current = state; current is not null; current = current.SearchOrigin)
            {
                yield return current.Detail;
                yield return current.Playable;
                if (current.NextEpisode is { } nextEpisode) yield return nextEpisode;
                foreach (var item in current.Items.Concat(current.Seasons).Concat(current.Rows.SelectMany(row => row.Items)))
                    yield return item;
                foreach (var person in current.SearchPeople.Items) yield return person;
            }
        }
        foreach (var item in _people.SelectMany(person => person.Works)) yield return item;
    }

    private sealed record BrowseSnapshot(BrowseLocation Location, MediaCardViewModel[] Items,
        MediaShelfViewModel[] Rows, MediaCardViewModel[] Seasons, MediaCardViewModel Detail,
        MediaCardViewModel Playable, MediaCardViewModel? NextEpisode, MediaCardViewModel? Season,
        string Title, string Subtitle, string EmptyMessage, string ErrorMessage, bool HasError,
        bool HasItems, bool HasMore, bool HasDetails, bool Episodes, int NextIndex, PageLoadOutcome Outcome,
        BrowseViewportState Viewport, PersonDetailsViewModel? Person, BrowseSnapshot? SearchOrigin,
        int? TotalCount, LumenSearchPeopleSnapshot SearchPeople);
}
