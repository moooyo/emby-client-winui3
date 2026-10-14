namespace EmbyClient.App.Services;

public enum EpisodeNotificationIntent { Details, PlayNow, RemindLater }

/// <summary>Contains account-scoped identifiers only; activation never carries credentials or server addresses.</summary>
public sealed record EpisodeNotificationTarget(string ServerId, string UserId, string ItemId,
    EpisodeNotificationIntent Intent = EpisodeNotificationIntent.Details)
{
    public const string Action = "episode-details";
    public const string PlayNowAction = "episode-play";
    public const string RemindLaterAction = "episode-later";

    public bool IsValid => IsIdentifier(ServerId) && IsIdentifier(UserId) && IsIdentifier(ItemId)
        && Intent is EpisodeNotificationIntent.Details or EpisodeNotificationIntent.PlayNow or EpisodeNotificationIntent.RemindLater;

    public string ActivationAction => Intent switch
    {
        EpisodeNotificationIntent.Details => Action,
        EpisodeNotificationIntent.PlayNow => PlayNowAction,
        EpisodeNotificationIntent.RemindLater => RemindLaterAction,
        _ => throw new InvalidOperationException("The notification intent is invalid.")
    };

    public bool Matches(string? serverId, string? userId) =>
        string.Equals(ServerId, serverId, StringComparison.Ordinal)
        && string.Equals(UserId, userId, StringComparison.Ordinal);

    public static bool TryParse(IReadOnlyDictionary<string, string> arguments, out EpisodeNotificationTarget? target)
    {
        target = null;
        if (!arguments.TryGetValue("action", out var action)
            || !arguments.TryGetValue("serverId", out var serverId) || !IsIdentifier(serverId)
            || !arguments.TryGetValue("userId", out var userId) || !IsIdentifier(userId)
            || !arguments.TryGetValue("itemId", out var itemId) || !IsIdentifier(itemId)) return false;
        var intent = action switch
        {
            Action => EpisodeNotificationIntent.Details,
            PlayNowAction => EpisodeNotificationIntent.PlayNow,
            RemindLaterAction => EpisodeNotificationIntent.RemindLater,
            _ => (EpisodeNotificationIntent?)null
        };
        if (!intent.HasValue) return false;
        target = new(serverId, userId, itemId, intent.Value);
        return true;
    }

    private static bool IsIdentifier(string? value) => value is { Length: > 0 and <= 256 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
