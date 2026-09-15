using EmbyClient.Api;

namespace EmbyClient.App.Services;

/// <summary>Resolves immediate episode neighbors from an authoritative whole-series server order.</summary>
internal sealed record PlaybackEpisodeNeighbors(BaseItemDto? Previous, BaseItemDto? Next)
{
    public static PlaybackEpisodeNeighbors Resolve(BaseItemDto current, IReadOnlyList<BaseItemDto> serverOrder)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(serverOrder);
        if (string.IsNullOrWhiteSpace(current.SeriesId) || !IsEpisodeInSeries(current, current.SeriesId))
            return new(null, null);

        var currentIndex = -1;
        for (var index = 0; index < serverOrder.Count; index++)
        {
            if (!string.Equals(serverOrder[index]?.Id, current.Id, StringComparison.Ordinal)) continue;
            // Duplicate IDs make the current position ambiguous, even when one duplicate is otherwise invalid.
            if (currentIndex >= 0) return new(null, null);
            currentIndex = index;
        }

        if (currentIndex < 0 || !IsEpisodeInSeries(serverOrder[currentIndex], current.SeriesId))
            return new(null, null);

        var previous = currentIndex > 0 ? serverOrder[currentIndex - 1] : null;
        var next = currentIndex + 1 < serverOrder.Count ? serverOrder[currentIndex + 1] : null;
        return new(IsEpisodeInSeries(previous, current.SeriesId) ? previous : null,
            IsEpisodeInSeries(next, current.SeriesId) ? next : null);
    }

    private static bool IsEpisodeInSeries(BaseItemDto? item, string seriesId) => item is not null
        && !string.IsNullOrWhiteSpace(item.Id)
        && item.IsFolder != true
        && string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase)
        && (string.IsNullOrWhiteSpace(item.SeriesId) || string.Equals(item.SeriesId, seriesId, StringComparison.Ordinal));
}
