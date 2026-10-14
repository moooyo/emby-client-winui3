using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyClient.App.Services;

/// <summary>Stores presentation identity only; the authenticated server supplies the current resume position.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LastWatchedItem(string AccountKey, string ItemId, string Title, string Subtitle,
    DateTimeOffset WatchedAt)
{
    [JsonIgnore]
    public WindowsActivationRoute Route => new(WindowsActivationDestination.ContinueWatching, AccountKey, ItemId);

    [JsonIgnore]
    public bool IsValid => WindowsActivationRoute.IsAccountKey(AccountKey) && WindowsActivationRoute.IsItemId(ItemId)
        && IsDisplayText(Title, allowEmpty: false) && IsDisplayText(Subtitle, allowEmpty: true);

    public static string DisplayText(string? value, string fallback = "")
    {
        var text = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var filtered = new string(text.Where(character => !char.IsControl(character)).Take(256).ToArray());
        return string.IsNullOrWhiteSpace(filtered) && !string.IsNullOrEmpty(fallback)
            ? new string(fallback.Where(character => !char.IsControl(character)).Take(256).ToArray()) : filtered;
    }

    private static bool IsDisplayText(string? text, bool allowEmpty) => text is not null && text.Length <= 256
        && (allowEmpty || !string.IsNullOrWhiteSpace(text)) && !text.Any(char.IsControl);
}

public enum LastWatchedPersistenceIssue { None, InvalidData, ReadFailed, WriteFailed }

/// <summary>Persists bounded, account-scoped history independently of account tokens and playback preferences.</summary>
public sealed class LastWatchedStore
{
    private const int MaximumAccounts = 32;
    private const int MaximumFileBytes = 128 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, LastWatchedItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _forgottenAccounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public LastWatchedStore(string? filePath = null) => FilePath = Path.GetFullPath(filePath
        ?? Path.Combine(AppDataPaths.RootDirectory, "last-watched.json"));

    public string FilePath { get; }
    public LastWatchedPersistenceIssue LastIssue { get; private set; }

    public async Task<LastWatchedItem?> LoadAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        if (!WindowsActivationRoute.IsAccountKey(accountKey)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_forgottenAccounts.Contains(accountKey)) return null;
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return _items.GetValueOrDefault(accountKey);
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(LastWatchedItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsValid) throw new ArgumentException("The last-watched identity is invalid.", nameof(item));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var updated = new Dictionary<string, LastWatchedItem>(_items, StringComparer.OrdinalIgnoreCase);
            foreach (var forgotten in _forgottenAccounts) updated.Remove(forgotten);
            updated[item.AccountKey.ToUpperInvariant()] = item with { AccountKey = item.AccountKey.ToUpperInvariant() };
            foreach (var expired in updated.Values.OrderByDescending(value => value.WatchedAt)
                .Skip(MaximumAccounts).ToArray()) updated.Remove(expired.AccountKey);
            await CommitAsync(updated, cancellationToken).ConfigureAwait(false);
            _forgottenAccounts.Remove(item.AccountKey);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveAccountAsync(string accountKey, CancellationToken cancellationToken = default)
    {
        if (!WindowsActivationRoute.IsAccountKey(accountKey)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A disk failure must not restore this account's old continuation in the current process.
            _forgottenAccounts.Add(accountKey);
            await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!_items.ContainsKey(accountKey)) return;
            var updated = new Dictionary<string, LastWatchedItem>(_items, StringComparer.OrdinalIgnoreCase);
            updated.Remove(accountKey);
            try { await CommitAsync(updated, cancellationToken).ConfigureAwait(false); }
            catch { _items = updated; throw; }
        }
        finally { _gate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded) return;
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes) { LastIssue = LastWatchedPersistenceIssue.InvalidData; _loaded = true; return; }
            var bytes = new byte[MaximumFileBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (count > MaximumFileBytes) { LastIssue = LastWatchedPersistenceIssue.InvalidData; _loaded = true; return; }
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 8 });
            var loaded = document.RootElement.Deserialize(LastWatchedJsonContext.Default.DictionaryStringLastWatchedItem);
            if (loaded is null || loaded.Count > MaximumAccounts
                || loaded.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != loaded.Count
                || loaded.Any(entry => entry.Value is null
                || !entry.Value.IsValid || !string.Equals(entry.Key, entry.Value.AccountKey, StringComparison.OrdinalIgnoreCase)))
            {
                LastIssue = LastWatchedPersistenceIssue.InvalidData;
                _loaded = true;
                return;
            }
            _items = new Dictionary<string, LastWatchedItem>(loaded, StringComparer.OrdinalIgnoreCase);
            LastIssue = LastWatchedPersistenceIssue.None;
            _loaded = true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            LastIssue = LastWatchedPersistenceIssue.None;
            _loaded = true;
        }
        catch (JsonException)
        {
            LastIssue = LastWatchedPersistenceIssue.InvalidData;
            _loaded = true;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            LastIssue = LastWatchedPersistenceIssue.ReadFailed;
            throw;
        }
    }

    private async Task CommitAsync(Dictionary<string, LastWatchedItem> updated, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, LastWatchedJsonContext.Default.DictionaryStringLastWatchedItem);
        if (bytes.Length > MaximumFileBytes) throw new InvalidOperationException("Last-watched history exceeds its storage limit.");
        var folder = Path.GetDirectoryName(FilePath)!;
        var temporaryPath = Path.Combine(folder, $".last-watched-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
            _items = updated;
            LastIssue = LastWatchedPersistenceIssue.None;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            LastIssue = LastWatchedPersistenceIssue.WriteFailed;
            throw;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (IsStorageFailure(exception)) { }
        }
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Dictionary<string, LastWatchedItem>))]
internal partial class LastWatchedJsonContext : JsonSerializerContext;
