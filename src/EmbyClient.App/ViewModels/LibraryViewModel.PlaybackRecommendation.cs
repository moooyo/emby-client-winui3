namespace EmbyClient.App.ViewModels;

public sealed partial class LibraryViewModel
{
    private SeriesRecommendation? _seriesRecommendation;
    private sealed record SeriesRecommendation(int SessionVersion, int PageVersion, int SeasonVersion,
        string ItemId, Task Completion);

    private void StartSeriesRecommendation(Task<MediaCardViewModel?> resumeTask,
        Task<MediaCardViewModel?> nextTask, int pageVersion, int seasonVersion, long userDataRevision,
        CancellationToken cancellationToken)
    {
        var completion = UpdateSeriesRecommendationAsync(resumeTask, nextTask, pageVersion, seasonVersion,
            userDataRevision, cancellationToken);
        _seriesRecommendation = new(_sessionVersion, pageVersion, seasonVersion, Detail.Id, completion);
    }

    public async Task<MediaCardViewModel?> WaitForPlaybackRecommendationAsync(string itemId,
        int navigationRevision, CancellationToken cancellationToken = default)
    {
        var pageVersion = _pageVersion;
        var sessionVersion = _sessionVersion;
        var seasonVersion = _seasonRequestVersion;
        if (navigationRevision != pageVersion || _pageCancellation is null || !HasDetails
            || Detail.Id != itemId || Detail.Item.Type is not ("Series" or "Season")
            || !IsPlaybackAllowed || HasPendingSearch) return null;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_pageCancellation.Token, cancellationToken);
        var recommendation = _seriesRecommendation;
        if (Detail.Item.Type == "Series" && recommendation?.PageVersion == pageVersion)
        {
            if (recommendation.SessionVersion != sessionVersion || recommendation.SeasonVersion != seasonVersion
                || recommendation.ItemId != itemId) return null;
            // Detail loading remains nonblocking; a quick-play intent needs the settled candidate.
            try { await recommendation.Completion.WaitAsync(lifetime.Token); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
        }
        if (!IsCurrentSession(sessionVersion) || !CanCommitPage(pageVersion, lifetime.Token)
            || seasonVersion != _seasonRequestVersion || !HasDetails || Detail.Id != itemId
            || HasPendingSearch || !IsPlaybackAllowed || !PlayableDetail.CanPlay) return null;
        var seasonId = Detail.Item.Type == "Season" ? Detail.Id
            : _location.PreferSelectedSeason ? SelectedSeason?.Id : null;
        if (seasonId is not null && (!Items.Any(item => item.Id == PlayableDetail.Id && item.CanPlay)
            || !string.IsNullOrWhiteSpace(PlayableDetail.Item.SeasonId) && PlayableDetail.Item.SeasonId != seasonId)) return null;
        return PlayableDetail;
    }
}
