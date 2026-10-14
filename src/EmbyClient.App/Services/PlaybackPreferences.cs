using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace EmbyClient.App.Services;

/// <summary>Identifies one item's local playback preferences without persisting account identifiers.</summary>
public sealed record PlaybackPreferenceScope
{
    private PlaybackPreferenceScope(string key) => Key = key;

    public string Key { get; }

    public static bool TryCreate(string? serverId, string? userId, string? itemId,
        out PlaybackPreferenceScope? scope)
    {
        scope = null;
        if (!ValidId(serverId) || !ValidId(userId) || !ValidId(itemId)) return false;
        // Length prefixes prevent identifiers containing separators from sharing a scope.
        var identity = FormattableString.Invariant($"{serverId!.Length}:{serverId}{userId!.Length}:{userId}{itemId!.Length}:{itemId}");
        scope = new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
        return true;
    }

    private static bool ValidId(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256 && !value.Any(char.IsControl);
}

/// <summary>Server defers track selection to Emby; Off and Local represent explicit client choices.</summary>
public enum PlaybackSubtitleSelection { Server, Off, Local }

/// <summary>Contains local controls only. Playback position and server track indexes remain owned by Emby.</summary>
public sealed record PlaybackPreferences
{
    public int VolumeLevel { get; init; } = 100;
    public bool IsMuted { get; init; }
    public double PlaybackRate { get; init; } = 1;
    public double AudioDelaySeconds { get; init; }
    public double SubtitleDelaySeconds { get; init; }
    public PlaybackSubtitleSelection SubtitleSelection { get; init; }
    public LocalSubtitlePreference[] LocalSubtitles { get; init; } = [];
    public string? SelectedLocalSubtitlePath { get; init; }

    public bool ContentEquals(PlaybackPreferences? other) => other is not null
        && VolumeLevel == other.VolumeLevel && IsMuted == other.IsMuted && PlaybackRate == other.PlaybackRate
        && AudioDelaySeconds == other.AudioDelaySeconds && SubtitleDelaySeconds == other.SubtitleDelaySeconds
        && SubtitleSelection == other.SubtitleSelection
        && string.Equals(SelectedLocalSubtitlePath, other.SelectedLocalSubtitlePath, StringComparison.OrdinalIgnoreCase)
        && LocalSubtitles.SequenceEqual(other.LocalSubtitles);
}

/// <summary>Retains a path and content identity, never subtitle text or a brokered access token.</summary>
public sealed record LocalSubtitlePreference
{
    public required string Path { get; init; }
    public required string ContentSha256 { get; init; }
    public required int Length { get; init; }
}

/// <summary>Retains live controls for a same-account, same-item replay without requiring persistence.</summary>
public sealed record PlaybackPreferenceReplay(PlaybackPreferenceScope Scope, PlaybackPreferences Preferences);

public static class PlaybackPreferenceRestorePolicy
{
    // A global default starts a new selection; replay preserves the item's choice unless explicitly replaced.
    public static int? ResolveSubtitleRequestIndex(int? explicitIndex, string defaultMode, bool isReplay) =>
        explicitIndex ?? (!isReplay && defaultMode == "None" ? -1 : (int?)null);

    public static PlaybackPreferences? ResolveReplayPreferences(PlaybackPreferenceReplay? replay,
        PlaybackPreferenceScope? scope) => replay is not null && replay.Scope == scope ? replay.Preferences : null;

    public static int? ResolveInitialSubtitleIndex(PlaybackPreferences preferences, int? explicitIndex,
        int? preferredIndex) => explicitIndex ?? (preferences.SubtitleSelection == PlaybackSubtitleSelection.Off ? -1 : preferredIndex);

    public static bool ShouldActivateLocalSubtitle(PlaybackPreferences preferences, int? explicitIndex) =>
        explicitIndex is null && preferences.SubtitleSelection == PlaybackSubtitleSelection.Local
        && preferences.SelectedLocalSubtitlePath is not null;

    public static PlaybackSubtitleSelection ResolveSubtitleSelection(PlaybackPreferences preferences, int? explicitIndex) =>
        explicitIndex is { } index ? index == -1 ? PlaybackSubtitleSelection.Off : PlaybackSubtitleSelection.Server
            : preferences.SubtitleSelection;
}

public enum LocalSubtitleRestoreIssue { Unavailable, Changed, InvalidData }

public sealed class LocalSubtitleRestoreException(LocalSubtitleRestoreIssue issue)
    : Exception("The saved local subtitle could not be restored.")
{
    public LocalSubtitleRestoreIssue Issue { get; } = issue;
}

/// <summary>Restores only the exact bounded file that was previously selected on this computer.</summary>
public static class PlaybackSubtitleFile
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public const int MaximumPathCharacters = 2048;
    private static readonly string[] SupportedExtensions = [".srt", ".ass", ".ssa", ".vtt", ".sub"];

    public static LocalSubtitlePreference? Capture(string? path, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var normalized = NormalizePath(path);
        if (normalized is null || data.Length is 0 or > MaximumBytes) return null;
        return new() { Path = normalized, Length = data.Length,
            ContentSha256 = Convert.ToHexStringLower(SHA256.HashData(data)) };
    }

    public static async Task<byte[]> ReadVerifiedAsync(LocalSubtitlePreference subtitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subtitle);
        if (!IsValid(subtitle)) throw new LocalSubtitleRestoreException(LocalSubtitleRestoreIssue.InvalidData);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(subtitle.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != subtitle.Length || stream.Length > MaximumBytes)
                throw new LocalSubtitleRestoreException(LocalSubtitleRestoreIssue.Changed);
            // Detect growth while reading as well as a replacement with the same name and length.
            var bytes = new byte[subtitle.Length + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count != subtitle.Length || !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, count))),
                subtitle.ContentSha256, StringComparison.Ordinal))
                throw new LocalSubtitleRestoreException(LocalSubtitleRestoreIssue.Changed);
            return bytes[..count];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            throw new LocalSubtitleRestoreException(LocalSubtitleRestoreIssue.Unavailable);
        }
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathCharacters || path.Any(char.IsControl)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal) || path.StartsWith("\\\\?\\", StringComparison.Ordinal)) return null;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(path)) return null;
            var normalized = System.IO.Path.GetFullPath(path);
            return normalized.Length <= MaximumPathCharacters
                && SupportedExtensions.Contains(System.IO.Path.GetExtension(normalized), StringComparer.OrdinalIgnoreCase)
                ? normalized : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    internal static bool IsValid(LocalSubtitlePreference? subtitle) => subtitle is not null
        && NormalizePath(subtitle.Path) is { } normalized
        && string.Equals(normalized, subtitle.Path, StringComparison.Ordinal)
        && subtitle.Length is > 0 and <= MaximumBytes
        && subtitle.ContentSha256 is { Length: 64 }
        && subtitle.ContentSha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
