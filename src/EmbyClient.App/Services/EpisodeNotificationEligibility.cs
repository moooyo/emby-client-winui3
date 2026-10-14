using EmbyClient.Api;

namespace EmbyClient.App.Services;

/// <summary>Uses server favorites and actual watching history to identify followed series.</summary>
public static class EpisodeNotificationEligibility
{
    public static async Task<bool> IsFollowedAsync(EmbyApiClient api, BaseItemDto episode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(episode);
        if (episode.Type != "Episode" || string.IsNullOrWhiteSpace(episode.SeriesId)) return false;
        var seriesId = episode.SeriesId;
        var series = await api.GetItemAsync(seriesId, cancellationToken).ConfigureAwait(false);
        if (series.Id != seriesId || series.Type != "Series") return false;
        if (series.UserData?.IsFavorite == true) return true;

        // NextUp alone is not proof that a series has been started. Require actual
        // resume progress or a watched episode under the authenticated user's series.
        var resume = api.GetResumeItemsAsync(new ItemQuery
        {
            ParentId = seriesId, Recursive = true, IncludeItemTypes = ["Episode"], Limit = 1,
            EnableImages = false, EnableUserData = true
        }, cancellationToken);
        var played = api.GetItemsAsync(new ItemQuery
        {
            ParentId = seriesId, Recursive = true, IncludeItemTypes = ["Episode"], IsPlayed = true,
            Limit = 1, EnableImages = false, EnableUserData = true
        }, cancellationToken);
        await Task.WhenAll(resume, played).ConfigureAwait(false);
        return (await resume.ConfigureAwait(false)).Items.Any(item => BelongsToSeries(item, seriesId)
                && item.UserData?.PlaybackPositionTicks is > 0)
            || (await played.ConfigureAwait(false)).Items.Any(item => BelongsToSeries(item, seriesId)
                && item.UserData?.Played == true);
    }

    private static bool BelongsToSeries(BaseItemDto item, string seriesId) => item.Type == "Episode"
        && !string.IsNullOrWhiteSpace(item.Id)
        && (string.IsNullOrWhiteSpace(item.SeriesId) || item.SeriesId == seriesId);
}
