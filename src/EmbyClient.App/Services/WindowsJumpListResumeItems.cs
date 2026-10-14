using EmbyClient.Api;

namespace EmbyClient.App.Services;

/// <summary>Builds shell identities only from the current authenticated account's resume response.</summary>
public static class WindowsJumpListResumeItems
{
    static WindowsJumpListResumeItems() => LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Untitled item"] = "\u672a\u547d\u540d\u5185\u5bb9"
    });

    public static IReadOnlyList<LastWatchedItem> Create(string accountKey, IEnumerable<BaseItemDto> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (!WindowsActivationRoute.IsAccountKey(accountKey)) return [];
        var result = new List<LastWatchedItem>(WindowsJumpListPresentation.MaximumContinueItems);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is null) continue;
            var userData = item.UserData;
            var position = userData?.PlaybackPositionTicks ?? 0;
            if (!WindowsActivationRoute.IsItemId(item.Id) || item.IsFolder == true || userData is null
                || item.Type is not ("Movie" or "Episode" or "Video") || position <= 0
                || item.RunTimeTicks is > 0 && position >= item.RunTimeTicks.Value
                || !ids.Add(item.Id!)) continue;
            var title = LastWatchedItem.DisplayText(item.Type == "Episode" ? item.SeriesName : item.Name,
                LastWatchedItem.DisplayText(item.Name, LumenText.Get("Untitled item")));
            var subtitle = item.Type == "Episode"
                ? string.Join(" \u00b7 ", new[]
                {
                    item.ParentIndexNumber is { } season ? LumenText.Get("Season {0}", season) : null,
                    item.IndexNumber is { } episode ? LumenText.Get("Episode {0}", episode) : null
                }.Where(value => !string.IsNullOrWhiteSpace(value)))
                : item.RunTimeTicks is > 0
                    ? LumenText.Get("Remaining {0}", LumenText.Get("{0} min", Math.Max(1,
                        Math.Ceiling(TimeSpan.FromTicks(item.RunTimeTicks.Value - position).TotalMinutes))))
                    : string.Empty;
            result.Add(new(accountKey.ToUpperInvariant(), item.Id!, title, LastWatchedItem.DisplayText(subtitle),
                userData.LastPlayedDate ?? DateTimeOffset.MinValue));
            if (result.Count == WindowsJumpListPresentation.MaximumContinueItems) break;
        }
        return result.ToArray();
    }
}
