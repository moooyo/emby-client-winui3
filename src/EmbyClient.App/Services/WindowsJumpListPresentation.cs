using System.Security.Cryptography;
using System.Text;

namespace EmbyClient.App.Services;

/// <summary>Supplies localized shell labels and bounded media identities for one authenticated account.</summary>
public sealed record WindowsJumpListPresentation(string SearchTitle, string SettingsTitle, string SwitchAccountTitle,
    string ContinueTitle, string? AccountKey, IReadOnlyList<LastWatchedItem> ContinueItems)
{
    public const int MaximumContinueItems = 4;

    static WindowsJumpListPresentation() => LumenText.Register(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Search media library"] = "\u641c\u7d22\u5a92\u4f53\u5e93",
        ["Switch server or user"] = "\u5207\u6362\u670d\u52a1\u5668\u6216\u7528\u6237"
    });

    // Existing callers can still publish their single authenticated continuation while adopting the new labels.
    public WindowsJumpListPresentation(string homeTitle, string libraryTitle, string searchTitle,
        string continueTitle, LastWatchedItem? lastWatched)
        : this(searchTitle, LumenText.Get("Settings"), LumenText.Get("Switch server or user"), continueTitle,
            lastWatched?.AccountKey, lastWatched is null ? [] : [lastWatched]) { }

    public IReadOnlyList<LastWatchedItem> GetContinueItems() => WindowsActivationRoute.IsAccountKey(AccountKey)
        ? (ContinueItems ?? []).Where(item => item is { IsValid: true }
            && string.Equals(item.AccountKey, AccountKey, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.ItemId, StringComparer.Ordinal)
            .Take(MaximumContinueItems).ToArray()
        : [];

    internal string Fingerprint
    {
        get
        {
            var value = new StringBuilder();
            foreach (var title in new[] { SearchTitle, SettingsTitle, SwitchAccountTitle, ContinueTitle })
                value.AppendLine(LastWatchedItem.DisplayText(title));
            value.AppendLine(AccountKey?.ToUpperInvariant());
            foreach (var item in GetContinueItems())
                value.AppendLine(item.Route.ToArguments()).AppendLine(item.Title).AppendLine(item.Subtitle);
            // Playback timestamps alone do not require a new shell transaction.
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
        }
    }

    internal static string ItemTitle(LastWatchedItem item) => LastWatchedItem.DisplayText(
        item.Subtitle.Length > 0 ? $"{item.Title} \u00b7 {item.Subtitle}" : item.Title, "Lumen");
}
