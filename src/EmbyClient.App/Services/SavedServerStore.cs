using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

/// <summary>Describes a validated server without storing any account or credential data.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SavedServer(
    [property: JsonRequired] string ApiRoot,
    [property: JsonRequired] string ServerId,
    [property: JsonRequired] string Name)
{
    public static SavedServer Create(string address, string serverId, string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        if (address.Length > 2048 || serverId.Length > 256 || serverId.Any(char.IsControl)
            || serverId != serverId.Trim() || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Use a valid HTTP or HTTPS server address and server identity.");

        var displayName = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim();
        if (displayName.Length > 256 || displayName.Any(char.IsControl))
            throw new ArgumentException("The server name is invalid or too long.");
        return new(EmbyApiClient.NormalizeApiRoot(uri).AbsoluteUri, serverId, displayName);
    }
}

public enum SavedServerStoreError
{
    ReadFailed,
    InvalidData,
    WriteFailed
}

public sealed class SavedServerStoreException(SavedServerStoreError error, string message) : Exception(message)
{
    public SavedServerStoreError Error { get; } = error;
}

/// <summary>Persists only server addresses and public identities in an independent atomic file.</summary>
public sealed class SavedServerStore
{
    private const int MaximumFileBytes = 512 * 1024;
    private const int MaximumServers = 128;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SavedServerStore(string? path = null)
    {
        Path = System.IO.Path.GetFullPath(path ?? System.IO.Path.Combine(AppDataPaths.RootDirectory, "saved-servers.json"));
    }

    public string Path { get; }

    public async Task<IReadOnlyList<SavedServer>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SavedServer>> UpsertAsync(SavedServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        var normalized = SavedServer.Create(server.ApiRoot, server.ServerId, server.Name);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var servers = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            servers.RemoveAll(existing => existing.ServerId == normalized.ServerId || existing.ApiRoot == normalized.ApiRoot);
            if (servers.Count >= MaximumServers)
                throw new SavedServerStoreException(SavedServerStoreError.WriteFailed,
                    "The saved server list is full. Remove a server before adding another.");
            servers.Add(normalized);
            await WriteCoreAsync(servers, cancellationToken).ConfigureAwait(false);
            return servers.ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<SavedServer>> RemoveAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var servers = (await LoadCoreAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (servers.RemoveAll(server => server.ServerId == serverId) == 0) return servers.ToArray();
            await WriteCoreAsync(servers, cancellationToken).ConfigureAwait(false);
            return servers.ToArray();
        }
        finally { _gate.Release(); }
    }

    private async Task<IReadOnlyList<SavedServer>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Reading an empty store does not create application data or account settings.
        if (!File.Exists(Path)) return [];
        try
        {
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumFileBytes) throw InvalidData();
            var document = await JsonSerializer.DeserializeAsync(stream,
                SavedServerJsonContext.Default.SavedServerDocument, cancellationToken).ConfigureAwait(false);
            if (document is null || document.Version != 1 || document.Servers is null || document.Servers.Count > MaximumServers)
                throw InvalidData();
            var servers = new List<SavedServer>(document.Servers.Count);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in document.Servers)
            {
                if (server is null) throw InvalidData();
                var normalized = SavedServer.Create(server.ApiRoot, server.ServerId, server.Name);
                if (!identities.Add(normalized.ServerId) || !addresses.Add(normalized.ApiRoot)) throw InvalidData();
                servers.Add(normalized);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return servers.ToArray();
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw InvalidData();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SavedServerStoreException(SavedServerStoreError.ReadFailed,
                "Saved servers could not be read. Check access to the application data folder.");
        }
    }

    private async Task WriteCoreAsync(List<SavedServer> servers, CancellationToken cancellationToken)
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var temporaryPath = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedServerDocument { Servers = servers },
                SavedServerJsonContext.Default.SavedServerDocument);
            if (bytes.Length > MaximumFileBytes)
                throw new SavedServerStoreException(SavedServerStoreError.WriteFailed,
                    "The saved server list exceeds the application size limit.");
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(folder);
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new SavedServerStoreException(SavedServerStoreError.WriteFailed,
                "Saved servers could not be written. The existing list has been retained.");
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static SavedServerStoreException InvalidData() => new(SavedServerStoreError.InvalidData,
        "Saved servers are damaged or incompatible. The saved file has been retained.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SavedServerDocument
{
    [JsonRequired]
    public int Version { get; set; } = 1;
    [JsonRequired]
    public List<SavedServer> Servers { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Default, MaxDepth = 8)]
[JsonSerializable(typeof(SavedServerDocument))]
internal partial class SavedServerJsonContext : JsonSerializerContext;
