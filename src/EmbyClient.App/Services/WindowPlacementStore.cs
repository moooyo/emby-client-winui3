using System.Collections;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyClient.App.Services;

public enum WindowPlacementPersistenceIssue { None, InvalidData, ReadFailed, WriteFailed }

/// <summary>Provides bounded, best-effort persistence for WinUIEx window-placement strings.</summary>
/// <remarks>
/// Only WindowPersistance_ keys and nonempty Base64 strings are accepted. No account data belongs here.
/// Persistence errors and rejected values are recorded in LastIssue without escaping window-close callbacks.
/// The custom dictionary is used for both packaged and unpackaged application launches.
/// </remarks>
public sealed class WindowPlacementStore : IDictionary<string, object>
{
    private const string KeyPrefix = "WindowPersistance_";
    private const int MaximumEntries = 16;
    private const int MaximumKeyCharacters = 128;
    private const int MaximumValueCharacters = 16 * 1024;
    private const int MaximumFileBytes = 128 * 1024;
    private readonly object _gate = new();
    private Dictionary<string, string> _entries = new(StringComparer.Ordinal);
    private WindowPlacementPersistenceIssue _lastIssue;

    public WindowPlacementStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EmbyClient.Windows", "window-placement.json"));
        Load();
    }

    public string FilePath { get; }
    public WindowPlacementPersistenceIssue LastIssue { get { lock (_gate) return _lastIssue; } }
    public int Count { get { lock (_gate) return _entries.Count; } }
    public bool IsReadOnly => false;
    public ICollection<string> Keys { get { lock (_gate) return Array.AsReadOnly(_entries.Keys.ToArray()); } }
    public ICollection<object> Values { get { lock (_gate) return Array.AsReadOnly(_entries.Values.Cast<object>().ToArray()); } }

    public object this[string key]
    {
        get { lock (_gate) return _entries[key]; }
        set
        {
            lock (_gate)
            {
                if (value is not string placement || !IsValidEntry(key, placement))
                {
                    _lastIssue = WindowPlacementPersistenceIssue.InvalidData;
                    return;
                }
                var updated = new Dictionary<string, string>(_entries, StringComparer.Ordinal) { [key] = placement };
                Commit(updated);
            }
        }
    }

    public void Add(string key, object value)
    {
        lock (_gate)
        {
            if (_entries.ContainsKey(key)) throw new ArgumentException("The window-placement key already exists.", nameof(key));
            this[key] = value;
        }
    }

    public bool ContainsKey(string key) { lock (_gate) return _entries.ContainsKey(key); }

    public bool Remove(string key)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(key)) return false;
            var updated = new Dictionary<string, string>(_entries, StringComparer.Ordinal);
            updated.Remove(key);
            Commit(updated);
            return true;
        }
    }

    public bool TryGetValue(string key, out object value)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var placement))
            {
                value = placement;
                return true;
            }
            value = null!;
            return false;
        }
    }

    public void Clear() { lock (_gate) Commit(new Dictionary<string, string>(StringComparer.Ordinal)); }
    public void Add(KeyValuePair<string, object> item) => Add(item.Key, item.Value);

    public bool Contains(KeyValuePair<string, object> item)
    {
        lock (_gate) return item.Value is string value && _entries.TryGetValue(item.Key, out var existing) && value == existing;
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        lock (_gate) return Contains(item) && Remove(item.Key);
    }

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => Snapshot().CopyTo(array, arrayIndex);
    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object>>)Snapshot()).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private KeyValuePair<string, object>[] Snapshot()
    {
        lock (_gate) return _entries.Select(entry => new KeyValuePair<string, object>(entry.Key, entry.Value)).ToArray();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes)
            {
                _lastIssue = WindowPlacementPersistenceIssue.InvalidData;
                return;
            }
            var entries = JsonSerializer.Deserialize(stream, WindowPlacementJsonContext.Default.DictionaryStringString);
            if (entries is null || entries.Count > MaximumEntries || entries.Any(entry => !IsValidEntry(entry.Key, entry.Value)))
            {
                _lastIssue = WindowPlacementPersistenceIssue.InvalidData;
                return;
            }
            _entries = new Dictionary<string, string>(entries, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            _lastIssue = WindowPlacementPersistenceIssue.InvalidData;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            _lastIssue = WindowPlacementPersistenceIssue.ReadFailed;
        }
    }

    private void Commit(Dictionary<string, string> updated)
    {
        if (updated.Count > MaximumEntries)
        {
            _lastIssue = WindowPlacementPersistenceIssue.InvalidData;
            return;
        }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(updated, WindowPlacementJsonContext.Default.DictionaryStringString);
        if (bytes.Length > MaximumFileBytes)
        {
            _lastIssue = WindowPlacementPersistenceIssue.InvalidData;
            return;
        }
        _entries = updated;
        var directory = Path.GetDirectoryName(FilePath)!;
        var temporaryPath = Path.Combine(directory, $".window-placement-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, FilePath, overwrite: true);
            _lastIssue = WindowPlacementPersistenceIssue.None;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            // Keep the latest placement in memory and retain the previous complete file on disk.
            _lastIssue = WindowPlacementPersistenceIssue.WriteFailed;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                // Failed cleanup must not interrupt WinUIEx's window-close event.
            }
        }
    }

    private static bool IsValidEntry(string? key, string? value)
    {
        if (key is null || key.Length <= KeyPrefix.Length || key.Length > MaximumKeyCharacters
            || !key.StartsWith(KeyPrefix, StringComparison.Ordinal) || key.Any(char.IsControl)
            || value is null || value.Length == 0 || value.Length > MaximumValueCharacters) return false;
        Span<byte> decoded = stackalloc byte[value.Length / 4 * 3 + 3];
        return Convert.TryFromBase64String(value, decoded, out var written) && written > 0;
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
}

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class WindowPlacementJsonContext : JsonSerializerContext;
