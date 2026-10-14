using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyClient.App.Services;

public enum PlaybackPreferencePersistenceIssue { None, InvalidData, ReadFailed, WriteFailed }

public sealed class PlaybackPreferenceStoreException(PlaybackPreferencePersistenceIssue issue, Exception? innerException = null)
    : Exception("Local playback preferences could not be persisted.", innerException)
{
    public PlaybackPreferencePersistenceIssue Issue { get; } = issue;
}

/// <summary>Persists a bounded account-and-item-scoped cache using reflection-free, atomic JSON writes.</summary>
public sealed class PlaybackPreferenceStore
{
    public const int MaximumEntries = 256;
    public const int MaximumLocalSubtitles = 6;
    public const int MaximumFileBytes = 1024 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, PlaybackPreferenceEntry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;
    private volatile PlaybackPreferencePersistenceIssue _lastIssue;

    public PlaybackPreferenceStore(string? filePath = null) => FilePath = Path.GetFullPath(
        filePath ?? Path.Combine(AppDataPaths.RootDirectory, "playback-preferences.json"));

    public string FilePath { get; }
    public PlaybackPreferencePersistenceIssue LastIssue => _lastIssue;

    public async Task<PlaybackPreferences?> LoadAsync(PlaybackPreferenceScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return _entries.TryGetValue(scope.Key, out var entry) ? Clone(entry.Preferences) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(PlaybackPreferenceScope scope, PlaybackPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(preferences);
        if (!TryNormalize(preferences, out var validated))
        {
            _lastIssue = PlaybackPreferencePersistenceIssue.InvalidData;
            throw new PlaybackPreferenceStoreException(_lastIssue);
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var updated = new Dictionary<string, PlaybackPreferenceEntry>(_entries, StringComparer.Ordinal)
            {
                [scope.Key] = new() { Key = scope.Key, LastUpdatedUtcTicks = DateTime.UtcNow.Ticks, Preferences = validated! }
            };
            while (updated.Count > MaximumEntries)
            {
                var oldest = updated.Values.Where(entry => entry.Key != scope.Key)
                    .OrderBy(entry => entry.LastUpdatedUtcTicks).ThenBy(entry => entry.Key, StringComparer.Ordinal).First();
                updated.Remove(oldest.Key);
            }
            byte[] bytes;
            while (true)
            {
                var document = new PlaybackPreferenceDocument
                { Entries = updated.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToArray() };
                bytes = JsonSerializer.SerializeToUtf8Bytes(document, PlaybackPreferencesJsonContext.Default.PlaybackPreferenceDocument);
                if (bytes.Length <= MaximumFileBytes) break;
                if (updated.Count == 1)
                {
                    _lastIssue = PlaybackPreferencePersistenceIssue.InvalidData;
                    throw new PlaybackPreferenceStoreException(_lastIssue);
                }
                var oldest = updated.Values.Where(entry => entry.Key != scope.Key)
                    .OrderBy(entry => entry.LastUpdatedUtcTicks).ThenBy(entry => entry.Key, StringComparer.Ordinal).First();
                updated.Remove(oldest.Key);
            }
            await CommitAsync(bytes, cancellationToken).ConfigureAwait(false);
            _entries.Clear();
            foreach (var entry in updated) _entries.Add(entry.Key, entry.Value);
            _lastIssue = PlaybackPreferencePersistenceIssue.None;
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes) { RejectInput(); return; }
            var bytes = new byte[MaximumFileBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count > MaximumFileBytes) { RejectInput(); return; }
            var document = JsonSerializer.Deserialize(bytes.AsSpan(0, count),
                PlaybackPreferencesJsonContext.Default.PlaybackPreferenceDocument);
            if (document is null || document.Version != 1 || document.Entries is null
                || document.Entries.Length > MaximumEntries) { RejectInput(); return; }
            var loaded = new Dictionary<string, PlaybackPreferenceEntry>(StringComparer.Ordinal);
            foreach (var entry in document.Entries)
            {
                if (entry is null || !ValidKey(entry.Key) || entry.LastUpdatedUtcTicks <= 0
                    || entry.LastUpdatedUtcTicks > DateTime.MaxValue.Ticks
                    || !TryNormalize(entry.Preferences, out var normalized) || loaded.ContainsKey(entry.Key))
                { RejectInput(); return; }
                loaded.Add(entry.Key, entry with { Preferences = normalized! });
            }
            foreach (var entry in loaded) _entries.Add(entry.Key, entry.Value);
            _loaded = true;
            _lastIssue = PlaybackPreferencePersistenceIssue.None;
        }
        catch (FileNotFoundException) { _loaded = true; _lastIssue = PlaybackPreferencePersistenceIssue.None; }
        catch (DirectoryNotFoundException) { _loaded = true; _lastIssue = PlaybackPreferencePersistenceIssue.None; }
        catch (JsonException) { RejectInput(); }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _lastIssue = PlaybackPreferencePersistenceIssue.ReadFailed;
            throw new PlaybackPreferenceStoreException(_lastIssue, exception);
        }
    }

    private void RejectInput()
    {
        _entries.Clear();
        _loaded = true;
        _lastIssue = PlaybackPreferencePersistenceIssue.InvalidData;
    }

    private async Task CommitAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        var temporaryPath = Path.Combine(directory, $".playback-preferences-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _lastIssue = PlaybackPreferencePersistenceIssue.WriteFailed;
            throw new PlaybackPreferenceStoreException(_lastIssue, exception);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (IsStorageFailure(exception)) { }
        }
    }

    private static bool TryNormalize(PlaybackPreferences? value, out PlaybackPreferences? normalized)
    {
        normalized = null;
        if (value is null || value.VolumeLevel is < 0 or > 100 || !double.IsFinite(value.PlaybackRate)
            || value.PlaybackRate is < .5 or > 2 || !ValidDelay(value.AudioDelaySeconds)
            || !ValidDelay(value.SubtitleDelaySeconds) || !Enum.IsDefined(value.SubtitleSelection)
            || value.LocalSubtitles is null || value.LocalSubtitles.Length > MaximumLocalSubtitles) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtitle in value.LocalSubtitles)
            if (!PlaybackSubtitleFile.IsValid(subtitle) || !paths.Add(subtitle.Path)) return false;
        if (value.SubtitleSelection == PlaybackSubtitleSelection.Local)
        {
            if (value.SelectedLocalSubtitlePath is null || !paths.Contains(value.SelectedLocalSubtitlePath)) return false;
        }
        else if (value.SelectedLocalSubtitlePath is not null) return false;
        var selected = value.SelectedLocalSubtitlePath is null ? null : value.LocalSubtitles
            .First(subtitle => string.Equals(subtitle.Path, value.SelectedLocalSubtitlePath, StringComparison.OrdinalIgnoreCase)).Path;
        normalized = value with { LocalSubtitles = value.LocalSubtitles.ToArray(), SelectedLocalSubtitlePath = selected };
        return true;
    }

    private static PlaybackPreferences Clone(PlaybackPreferences preferences) => preferences with { LocalSubtitles = preferences.LocalSubtitles.ToArray() };
    private static bool ValidDelay(double delay) => double.IsFinite(delay) && delay is >= -10 and <= 10;
    private static bool ValidKey(string? key) => key is { Length: 64 } && key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
}

internal sealed record PlaybackPreferenceDocument
{
    public int Version { get; init; } = 1;
    public PlaybackPreferenceEntry[] Entries { get; init; } = [];
}

internal sealed record PlaybackPreferenceEntry
{
    public required string Key { get; init; }
    public required long LastUpdatedUtcTicks { get; init; }
    public required PlaybackPreferences Preferences { get; init; }
}

[JsonSourceGenerationOptions(MaxDepth = 8, GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PlaybackPreferenceDocument))]
internal partial class PlaybackPreferencesJsonContext : JsonSerializerContext;
