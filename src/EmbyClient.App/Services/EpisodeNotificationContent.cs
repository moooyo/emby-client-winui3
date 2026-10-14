using EmbyClient.Api;

namespace EmbyClient.App.Services;

/// <summary>Formats notification text from the authenticated episode response without placeholder media data.</summary>
public sealed record EpisodeNotificationContent(string Title, string Description, string PlayNowLabel, string RemindLaterLabel)
{
    static EpisodeNotificationContent()
    {
        LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["\"{0}\" has a new episode"] = "\u300a{0}\u300b\u66f4\u65b0\u4e86",
            ["New episode available"] = "\u6709\u65b0\u5267\u96c6\u53ef\u89c2\u770b",
            ["Season {0}, Episode {1}"] = "\u7b2c {0} \u5b63\u7b2c {1} \u96c6",
            ["Episode {0}"] = "\u7b2c {0} \u96c6",
            ["{0} \"{1}\" is now available on \"{2}\""] = "{0}\u300c{1}\u300d\u5df2\u5728\u300c{2}\u300d\u4e0a\u7ebf",
            ["{0} \"{1}\" is now available"] = "{0}\u300c{1}\u300d\u5df2\u4e0a\u7ebf",
            ["Play now"] = "\u7acb\u5373\u64ad\u653e",
            ["Remind later"] = "\u7a0d\u540e\u63d0\u9192"
        });
    }

    public static EpisodeNotificationContent Create(BaseItemDto item, string? serverName)
    {
        ArgumentNullException.ThrowIfNull(item);
        var series = LastWatchedItem.DisplayText(item.SeriesName);
        var name = LastWatchedItem.DisplayText(item.Name, LumenText.Get("Episode"));
        var server = LastWatchedItem.DisplayText(serverName);
        var identity = item.ParentIndexNumber is { } season && item.IndexNumber is { } episode
            ? LumenText.Get("Season {0}, Episode {1}", season, episode)
            : item.IndexNumber is { } index ? LumenText.Get("Episode {0}", index) : LumenText.Get("Episode");
        var description = server.Length > 0
            ? LumenText.Get("{0} \"{1}\" is now available on \"{2}\"", identity, name, server)
            : LumenText.Get("{0} \"{1}\" is now available", identity, name);
        if (item.RunTimeTicks is > 0)
            description += $" \u00b7 {LumenText.Get("{0} min", Math.Max(1, Math.Round(TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes)))}";
        return new(series.Length > 0 ? LumenText.Get("\"{0}\" has a new episode", series)
            : LumenText.Get("New episode available"), description, LumenText.Get("Play now"), LumenText.Get("Remind later"));
    }
}
