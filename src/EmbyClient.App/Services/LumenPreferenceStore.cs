using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyClient.App.Services;

public enum LumenPreferencePersistenceIssue { None, InvalidData, ReadFailed, WriteFailed }

public sealed class LumenPreferenceStoreException(
    LumenPreferencePersistenceIssue issue,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public LumenPreferencePersistenceIssue Issue { get; } = issue;
}

/// <summary>Persists bounded client preferences without reading or writing account settings.</summary>
public sealed class LumenPreferenceStore
{
    private const int MaximumFileBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile LumenPreferencePersistenceIssue _lastIssue;

    public LumenPreferenceStore(string? filePath = null)
    {
        FilePath = Path.GetFullPath(filePath ?? Path.Combine(AppDataPaths.RootDirectory, "lumen-preferences.json"));
    }

    public string FilePath { get; }
    public LumenPreferencePersistenceIssue LastIssue => _lastIssue;

    public async Task<LumenPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumFileBytes) return UseDefaults();

                // The extra byte detects oversized files even if another process grows the file.
                var bytes = new byte[MaximumFileBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (count > MaximumFileBytes) return UseDefaults();
                var loaded = JsonSerializer.Deserialize(bytes.AsSpan(0, count),
                    LumenPreferencesJsonContext.Default.LumenPreferences);
                if (loaded is null) return UseDefaults();

                var validated = Normalize(loaded);
                var canonicalInput = IsValidSubtitleLanguage(loaded.SubtitleLanguage)
                    ? loaded with { SubtitleLanguage = loaded.SubtitleLanguage.ToLowerInvariant() }
                    : loaded;
                _lastIssue = validated == canonicalInput
                    ? LumenPreferencePersistenceIssue.None
                    : LumenPreferencePersistenceIssue.InvalidData;
                return validated;
            }
            catch (FileNotFoundException)
            {
                _lastIssue = LumenPreferencePersistenceIssue.None;
                return new LumenPreferences();
            }
            catch (DirectoryNotFoundException)
            {
                _lastIssue = LumenPreferencePersistenceIssue.None;
                return new LumenPreferences();
            }
            catch (JsonException)
            {
                return UseDefaults();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                _lastIssue = LumenPreferencePersistenceIssue.ReadFailed;
                throw new LumenPreferenceStoreException(_lastIssue,
                    "Client preferences could not be read. Check access to the application data folder.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LumenPreferences preferences, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var validated = Normalize(preferences);
            if (!IsValidSubtitleLanguage(preferences.SubtitleLanguage)
                || validated != (preferences with { SubtitleLanguage = validated.SubtitleLanguage }))
            {
                _lastIssue = LumenPreferencePersistenceIssue.InvalidData;
                throw new LumenPreferenceStoreException(_lastIssue,
                    "Client preferences contain invalid or unsupported values.");
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(validated,
                LumenPreferencesJsonContext.Default.LumenPreferences);
            if (bytes.Length > MaximumFileBytes)
            {
                _lastIssue = LumenPreferencePersistenceIssue.InvalidData;
                throw new LumenPreferenceStoreException(_lastIssue,
                    "Client preferences exceed the application size limit.");
            }

            var directory = Path.GetDirectoryName(FilePath)!;
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");
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

                // A canceled save must not replace the last complete preferences file.
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, FilePath, overwrite: true);
                _lastIssue = LumenPreferencePersistenceIssue.None;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                _lastIssue = LumenPreferencePersistenceIssue.WriteFailed;
                throw new LumenPreferenceStoreException(_lastIssue,
                    "Client preferences could not be saved. The previous preferences file has been retained.", exception);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (IsStorageFailure(exception))
                {
                    // Cleanup does not change the outcome of the completed or failed save.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private LumenPreferences UseDefaults()
    {
        _lastIssue = LumenPreferencePersistenceIssue.InvalidData;
        return new LumenPreferences();
    }

    private static LumenPreferences Normalize(LumenPreferences value)
    {
        var defaults = new LumenPreferences();
        return value with
        {
            Theme = KnownValue(value.Theme, defaults.Theme, "Dark", "Light"),
            Accent = KnownValue(value.Accent, defaults.Accent, "Gold", "Coral", "Sage", "Blue"),
            PosterColumns = value.PosterColumns is >= 6 and <= 10 ? value.PosterColumns : defaults.PosterColumns,
            NextEpisodeCountdownSeconds = value.NextEpisodeCountdownSeconds is 0 or 5 or 10 or 15
                ? value.NextEpisodeCountdownSeconds : defaults.NextEpisodeCountdownSeconds,
            IntroSkipMode = KnownValue(value.IntroSkipMode, defaults.IntroSkipMode, "Off", "ShowButton", "Automatic"),
            ResumeMode = KnownValue(value.ResumeMode, defaults.ResumeMode, "Continue", "Ask", "Restart"),
            LocalMaxBitrate = IsValidBitrate(value.LocalMaxBitrate) ? value.LocalMaxBitrate : defaults.LocalMaxBitrate,
            InternetMaxBitrate = IsValidBitrate(value.InternetMaxBitrate) ? value.InternetMaxBitrate : defaults.InternetMaxBitrate,
            HdrMode = KnownValue(value.HdrMode, defaults.HdrMode, "Auto", "Always", "Off"),
            SubtitleLanguage = IsValidSubtitleLanguage(value.SubtitleLanguage)
                ? value.SubtitleLanguage.ToLowerInvariant() : defaults.SubtitleLanguage,
            SubtitleMode = KnownValue(value.SubtitleMode, defaults.SubtitleMode, "Default", "Smart", "Always", "OnlyForced", "None", "HearingImpaired"),
            SubtitleSize = KnownValue(value.SubtitleSize, defaults.SubtitleSize, "Small", "Medium", "Large"),
            SubtitlePosition = KnownValue(value.SubtitlePosition, defaults.SubtitlePosition, "Bottom", "BlackBars")
        };
    }

    private static string KnownValue(string? value, string fallback, params string[] supported)
    {
        foreach (var candidate in supported)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        return fallback;
    }

    private static bool IsValidBitrate(long value) => value == 0 || value is >= 1_000_000 and <= 1_000_000_000;

    private static bool IsValidSubtitleLanguage(string? value)
    {
        if (value is null || value.Length > 16) return false;
        if (value.Length == 0) return true;
        if (value[0] == '-' || value[^1] == '-') return false;
        var hasLetter = false;
        foreach (var character in value)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z') hasLetter = true;
            else if (character is not (>= '0' and <= '9') and not '-') return false;
        }
        return hasLetter;
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    MaxDepth = 8,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(LumenPreferences))]
internal partial class LumenPreferencesJsonContext : JsonSerializerContext;
