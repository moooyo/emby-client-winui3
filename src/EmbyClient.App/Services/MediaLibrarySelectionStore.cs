using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyClient.App.Services;

public sealed record MediaLibrarySelections(string? MovieLibraryId = null, string? SeriesLibraryId = null);

public enum MediaLibrarySelectionPersistenceIssue { None, InvalidData, ReadFailed, WriteFailed }

public sealed class MediaLibrarySelectionStoreException(
    MediaLibrarySelectionPersistenceIssue issue,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public MediaLibrarySelectionPersistenceIssue Issue { get; } = issue;
}

/// <summary>Persists only movie and series library selections in bounded, account-scoped JSON files.</summary>
public sealed class MediaLibrarySelectionStore
{
    private const int MaximumLibraryIdLength = 256;
    private const int MaximumFileBytes = 4 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile MediaLibrarySelectionPersistenceIssue _lastIssue;

    public MediaLibrarySelectionStore(string? directoryPath = null)
    {
        DirectoryPath = Path.GetFullPath(directoryPath
            ?? Path.Combine(AppDataPaths.RootDirectory, "media-library-selections"));
    }

    public string DirectoryPath { get; }
    public MediaLibrarySelectionPersistenceIssue LastIssue => _lastIssue;

    public async Task<MediaLibrarySelections> LoadAsync(string accountKey,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = GetAccountFilePath(accountKey);
            try
            {
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (stream.Length > MaximumFileBytes) return UseAll(cancellationToken);

                // The extra byte detects a file that grows beyond the limit while it is being read.
                var bytes = new byte[MaximumFileBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    count += read;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (count > MaximumFileBytes) return UseAll(cancellationToken);
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count),
                    new JsonDocumentOptions { MaxDepth = 4 });
                if (!HasSelectionFields(document.RootElement)) return UseAll(cancellationToken);
                var selections = document.RootElement.Deserialize(
                    MediaLibrarySelectionsJsonContext.Default.MediaLibrarySelections);
                if (selections is null || !IsValidLibraryId(selections.MovieLibraryId)
                    || !IsValidLibraryId(selections.SeriesLibraryId)) return UseAll(cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                _lastIssue = MediaLibrarySelectionPersistenceIssue.None;
                return selections;
            }
            catch (FileNotFoundException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lastIssue = MediaLibrarySelectionPersistenceIssue.None;
                return new MediaLibrarySelections();
            }
            catch (DirectoryNotFoundException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lastIssue = MediaLibrarySelectionPersistenceIssue.None;
                return new MediaLibrarySelections();
            }
            catch (JsonException)
            {
                return UseAll(cancellationToken);
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                _lastIssue = MediaLibrarySelectionPersistenceIssue.ReadFailed;
                throw new MediaLibrarySelectionStoreException(_lastIssue,
                    "Media library selections could not be read. Check access to the application data folder.", exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string accountKey, string? movieLibraryId, string? seriesLibraryId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filePath = GetAccountFilePath(accountKey);
            if (!IsValidLibraryId(movieLibraryId) || !IsValidLibraryId(seriesLibraryId))
            {
                _lastIssue = MediaLibrarySelectionPersistenceIssue.InvalidData;
                throw new MediaLibrarySelectionStoreException(_lastIssue,
                    "Media library selections contain an invalid library identifier.");
            }

            var selections = new MediaLibrarySelections(movieLibraryId, seriesLibraryId);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(selections,
                MediaLibrarySelectionsJsonContext.Default.MediaLibrarySelections);
            if (bytes.Length > MaximumFileBytes)
            {
                _lastIssue = MediaLibrarySelectionPersistenceIssue.InvalidData;
                throw new MediaLibrarySelectionStoreException(_lastIssue,
                    "Media library selections exceed the application size limit.");
            }

            var temporaryPath = Path.Combine(DirectoryPath, $".{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(DirectoryPath);
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }

                // Replacement is the commit point. A canceled save must retain the previous complete file.
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, filePath, overwrite: true);
                _lastIssue = MediaLibrarySelectionPersistenceIssue.None;
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                _lastIssue = MediaLibrarySelectionPersistenceIssue.WriteFailed;
                throw new MediaLibrarySelectionStoreException(_lastIssue,
                    "Media library selections could not be saved. The previous selections file has been retained.", exception);
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception exception) when (IsStorageFailure(exception))
                {
                    // Temporary-file cleanup must not change the result of the save.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetAccountFilePath(string accountKey)
    {
        if (accountKey is not { Length: 64 } || !accountKey.All(char.IsAsciiHexDigit))
        {
            _lastIssue = MediaLibrarySelectionPersistenceIssue.InvalidData;
            throw new MediaLibrarySelectionStoreException(_lastIssue,
                "Media library selections require a valid account key.");
        }

        // Existing account keys are case-insensitive hexadecimal hashes of the server and user identities.
        var canonicalKey = accountKey.ToUpperInvariant();
        var fileName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalKey))).ToLowerInvariant() + ".json";
        return Path.Combine(DirectoryPath, fileName);
    }

    private MediaLibrarySelections UseAll(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lastIssue = MediaLibrarySelectionPersistenceIssue.InvalidData;
        // Invalid documents remain untouched; only an explicit save can replace their contents.
        return new MediaLibrarySelections();
    }

    private static bool HasSelectionFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        var hasMovie = false;
        var hasSeries = false;
        foreach (var property in root.EnumerateObject())
        {
            string propertyName;
            try { propertyName = property.Name; }
            catch (InvalidOperationException)
            {
                // DOM name decoding can reject invalid Unicode without the serializer's JsonException wrapper.
                return false;
            }
            if (string.Equals(propertyName, nameof(MediaLibrarySelections.MovieLibraryId), StringComparison.OrdinalIgnoreCase))
            {
                if (hasMovie) return false;
                hasMovie = true;
            }
            else if (string.Equals(propertyName, nameof(MediaLibrarySelections.SeriesLibraryId), StringComparison.OrdinalIgnoreCase))
            {
                if (hasSeries) return false;
                hasSeries = true;
            }
            else return false;
            if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) return false;
        }
        return hasMovie && hasSeries;
    }

    private static bool IsValidLibraryId(string? value)
    {
        if (value is null) return true;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLibraryIdLength || value is "." or "..") return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsControl(character)) return false;
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return false;
                index++;
            }
            else if (char.IsLowSurrogate(character)) return false;
        }
        return true;
    }

    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or SecurityException;
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    MaxDepth = 4,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(MediaLibrarySelections))]
internal partial class MediaLibrarySelectionsJsonContext : JsonSerializerContext;
