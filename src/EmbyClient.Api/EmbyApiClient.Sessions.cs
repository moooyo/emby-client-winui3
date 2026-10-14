using System.Net;
using System.Text.Json;

namespace EmbyClient.Api;

public sealed partial class EmbyApiClient
{
    /// <summary>
    /// Returns the unique server session for this device, user, and playing item, or null when unavailable.
    /// SDK 4.10.1.0 does not guarantee a PlaySessionId in session responses. Without that response field,
    /// identity is limited to the unique device/user/item match and an optional server session ID.
    /// A PlaySessionId returned by the server is checked when the caller supplies its playback ID.
    /// </summary>
    public async Task<PlaybackSessionInfo?> GetCurrentPlaybackSessionAsync(string itemId,
        string? playSessionId = null, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        if (playSessionId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(playSessionId);
        if (sessionId is not null) ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var userId = RequireUserId();

        PlaybackSessionInfo[] sessions;
        try
        {
            sessions = await GetAsync("Sessions", new Query().Add("DeviceId", Identity.DeviceId).Add("Id", sessionId),
                EmbyJsonContext.Default.PlaybackSessionInfoArray, cancellationToken).ConfigureAwait(false);
        }
        catch (EmbyApiException exception) when (exception.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
        {
            return null;
        }

        PlaybackSessionInfo? match = null;
        foreach (var session in sessions)
        {
            if (session is null || string.IsNullOrWhiteSpace(session.Id)
                || !string.Equals(session.DeviceId, Identity.DeviceId, StringComparison.Ordinal)
                || !string.Equals(session.UserId, userId, StringComparison.Ordinal)
                || !string.Equals(session.NowPlayingItem?.Id, itemId, StringComparison.Ordinal)
                || sessionId is not null && !string.Equals(session.Id, sessionId, StringComparison.Ordinal)
                || !MatchesReportedPlaySession(session.ExtensionData, playSessionId)
                || !MatchesReportedPlaySession(session.PlayState?.ExtensionData, playSessionId)) continue;

            // An ambiguous result cannot establish which server transcode belongs to this playback.
            if (match is not null) return null;
            match = session;
        }
        return match;
    }

    private static bool MatchesReportedPlaySession(Dictionary<string, JsonElement>? extensionData,
        string? playSessionId)
    {
        if (playSessionId is null || extensionData is null) return true;
        foreach (var (name, value) in extensionData)
        {
            if (!string.Equals(name, "PlaySessionId", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.String) return false;
            var reported = value.GetString();
            if (string.IsNullOrWhiteSpace(reported)) continue;
            if (!string.Equals(reported, playSessionId, StringComparison.Ordinal)) return false;
        }
        return true;
    }
}
