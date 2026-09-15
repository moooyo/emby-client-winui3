using EmbyClient.Api;
using Microsoft.UI.Xaml;

namespace EmbyClient.App.ViewModels;

public enum PageLoadOutcome { NotRequested, Loading, Succeeded, Failed }
public enum PageLoadMoreOutcome { NotStarted, Canceled, Failed, Appended, Exhausted, Repeated }

public sealed partial class LibraryViewModel
{
    private PageLoadOutcome _loadOutcome;
    private MediaCardViewModel? _nextEpisode;
    public PageLoadOutcome LoadOutcome => _loadOutcome;
    public Visibility InitialFailureVisibility => _loadOutcome == PageLoadOutcome.Failed && !HasError
        && !IsBusy && !HasItems && !IsPerson ? Visibility.Visible : Visibility.Collapsed;
    public MediaCardViewModel? NextEpisode
    {
        get => _nextEpisode;
        private set
        {
            if (ReferenceEquals(_nextEpisode, value)) return;
            _nextEpisode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextEpisodeVisibility));
        }
    }
    public Visibility NextEpisodeVisibility => HasDetails && Detail.Item.Type == "Episode" && NextEpisode is not null
        ? Visibility.Visible : Visibility.Collapsed;
    public bool IsSeasonSwitchPending => _pendingSeasonId is not null;
    public bool CanUseSeasonActions => !IsSeasonSwitchPending && !_isInitializingSeries;
    public Visibility SeasonSwitchVisibility => IsSeasonSwitchPending ? Visibility.Visible : Visibility.Collapsed;
    public string SeasonSwitchMessage => IsSeasonSwitchPending ? $"Switching to {SelectedSeason?.Title ?? "season"}…" : string.Empty;
    public string PlaybackUnavailableMessage => !IsPlaybackAllowed
        ? "Playback is disabled for this account. Contact your server administrator."
        : Detail.Item.Type is "Series" or "Season" && !PlayableDetail.CanPlay
            ? "No playable episodes are available in this season."
            : Detail.PlaybackUnavailableReason;
    public Visibility PlaybackUnavailableVisibility => (HasDetails && !IsBusy && !PlayableDetail.CanPlay
        && PlaybackUnavailableMessage.Length > 0) || (HasDetails && !IsPlaybackAllowed)
            ? Visibility.Visible : Visibility.Collapsed;

    private void SetLoadOutcome(PageLoadOutcome outcome)
    {
        _loadOutcome = outcome;
        OnPropertyChanged(nameof(LoadOutcome));
        OnPropertyChanged(nameof(EmptyVisibility));
        OnPropertyChanged(nameof(DetailEmptyVisibility));
        OnPropertyChanged(nameof(InitialFailureVisibility));
    }

    private void NotifyDetailContext()
    {
        OnPropertyChanged(nameof(IsSeasonSwitchPending));
        OnPropertyChanged(nameof(CanUseSeasonActions));
        OnPropertyChanged(nameof(SeasonSwitchVisibility));
        OnPropertyChanged(nameof(SeasonSwitchMessage));
        OnPropertyChanged(nameof(NextEpisodeVisibility));
        OnPropertyChanged(nameof(PlaybackUnavailableMessage));
        OnPropertyChanged(nameof(PlaybackUnavailableVisibility));
        OnPropertyChanged(nameof(SeasonSelectorVisibility));
    }

    public void CancelSeasonSwitch()
    {
        if (!IsSeasonSwitchPending) return;
        _seasonRequestVersion++;
        CancelAndDispose(ref _seasonCancellation);
        _pendingSeasonId = null;
        SelectedSeason = Seasons.FirstOrDefault(season => season.Id == _location.SeasonId);
        IsBusy = false;
        OnPropertyChanged(nameof(DetailChildrenTitle));
        NotifyDetailContext();
    }

    public Task OpenParentSeriesAsync(CancellationToken cancellationToken = default)
    {
        if (Detail.Item.SeriesId is not { Length: > 0 } seriesId) return Task.CompletedTask;
        CancelSearch();
        var seasonId = Detail.Item.Type == "Season" ? Detail.Id : Detail.Item.SeasonId;
        return NavigateAsync(new(LocationKind.Item, seriesId, Detail.Item.SeriesName ?? "Series",
            SeasonId: seasonId, PreferSelectedSeason: true), true, cancellationToken);
    }

    public Task OpenNextEpisodeAsync(CancellationToken cancellationToken = default) => NextEpisode is { } next
        ? ShowItemAsync(next, cancellationToken) : Task.CompletedTask;

    private async Task LoadEpisodeContextAsync(EmbyApiClient api, MediaCardViewModel episode, int version,
        CancellationToken cancellationToken)
    {
        if (episode.Item.SeriesId is not { Length: > 0 } seriesId) return;
        try
        {
            var result = await api.GetEpisodesAsync(seriesId, episode.Item.SeasonId, cancellationToken);
            if (!CanCommitPage(version, cancellationToken) || Detail.Id != episode.Id || HasPendingSearch) return;
            var ordered = result.Items.OrderBy(item => item.ParentIndexNumber ?? int.MaxValue)
                .ThenBy(item => item.IndexNumber ?? int.MaxValue).ToArray();
            var index = Array.FindIndex(ordered, item => item.Id == episode.Id);
            var next = index >= 0 ? ordered.Skip(index + 1).Select(item => new MediaCardViewModel(item))
                .FirstOrDefault(item => item.CanPlay) : null;
            if (next is null && index >= 0 && episode.Item.SeasonId is { Length: > 0 } seasonId)
            {
                var seasons = await api.GetSeasonsAsync(seriesId, cancellationToken);
                var seasonList = seasons.Items.OrderBy(item => item.IndexNumber ?? int.MaxValue).ToArray();
                var seasonIndex = Array.FindIndex(seasonList, item => item.Id == seasonId);
                if (seasonIndex >= 0 && seasonList.ElementAtOrDefault(seasonIndex + 1)?.Id is { } nextSeasonId)
                {
                    var nextSeason = await api.GetEpisodesAsync(seriesId, nextSeasonId, cancellationToken);
                    next = nextSeason.Items.OrderBy(item => item.IndexNumber ?? int.MaxValue)
                        .Select(item => new MediaCardViewModel(item)).FirstOrDefault(item => item.CanPlay);
                }
            }
            if (CanCommitPage(version, cancellationToken) && Detail.Id == episode.Id && !HasPendingSearch) NextEpisode = next;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            if (CanCommitPage(version, cancellationToken) && IsSessionExpired(exception)) ShowError(exception);
        }
    }

    private async Task UpdateSeriesRecommendationAsync(Task<MediaCardViewModel?> resumeTask,
        Task<MediaCardViewModel?> nextTask, int pageVersion, int seasonVersion, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(resumeTask, nextTask);
            if (!CanCommitPage(pageVersion, cancellationToken) || seasonVersion != _seasonRequestVersion || HasPendingSearch) return;
            PlayableDetail = await resumeTask ?? await nextTask ?? FindPlayable(Items);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}
