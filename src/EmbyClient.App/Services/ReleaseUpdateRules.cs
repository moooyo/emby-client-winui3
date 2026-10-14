using System.IO.Compression;
using System.Buffers.Binary;
using System.Text.Json;

namespace EmbyClient.App.Services;

/// <summary>Portable release, archive, and inventory validation shared by download and installation.</summary>
public static class ReleaseUpdateRules
{
    public const string Repository = "moooyo/emby-client-winui3";
    public const string ManifestFileName = "release-update-manifest.json";
    public const string ApplicationFileName = "EmbyClient.App.exe";
    public const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    public const long MaximumExpandedBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumEntryBytes = 512L * 1024 * 1024;
    public const int MaximumEntries = 8192;
    public const int MaximumReleaseBytes = 512 * 1024;
    public const int MaximumChecksumBytes = 64 * 1024;
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const string AssetPrefix = "emby-client-windows-x64-aot";

    internal static readonly string[] RequiredPayloadFiles =
    [
        ApplicationFileName, "EmbyClient.App.pri", "Microsoft.ui.xaml.dll", "Microsoft.WindowsAppRuntime.dll",
        "FFmpegInteropX.dll", "emby-client-win-x64.cdx.json", "licenses/third-party/manifest.json",
        "licenses/third-party/SOURCES.md", "Assets/AppIcon.ico",
        "Assets/Lumen/Fonts/NotoSerifSC-Variable.ttf", "Assets/Lumen/Fonts/Manrope-Variable.ttf",
        "Assets/Lumen/Fonts/LeagueGothic-Variable.ttf", "Assets/Lumen/Fonts/IBMPlexMono-Regular.ttf",
        "Assets/Lumen/Fonts/IBMPlexMono-Medium.ttf"
    ];

    internal static readonly string[] FfmpegFamilies =
        ["avcodec", "avformat", "avutil", "avfilter", "avdevice", "swresample", "swscale"];

    internal static readonly string[] ForbiddenPayloadFiles =
    [
        "EmbyClient.App.dll", "EmbyClient.App.runtimeconfig.json", "AppxManifest.xml", "AppxSignature.p7x",
        "AppxBlockMap.xml", "[Content_Types].xml", "resources.pri"
    ];

    public static ReleaseUpdateInfo ParseLatestRelease(ReadOnlyMemory<byte> bytes, Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (bytes.Length > MaximumReleaseBytes) throw InvalidRelease("The release response exceeds the size limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadBoolean(root, "draft") != false
                || ReadBoolean(root, "prerelease") != false)
                throw InvalidRelease("Only published stable releases can be installed.");
            var tag = ReadString(root, "tag_name", 128);
            if (!TryParseVersion(tag, out var version))
                throw InvalidRelease("The release tag must contain a stable three- or four-part numeric version.");
            var releasePage = ParseRepositoryUri(ReadString(root, "html_url", 2048),
                $"/{Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            var isNewer = NormalizeVersion(version).CompareTo(NormalizeVersion(currentVersion)) > 0;
            var candidates = new List<JsonElement>();
            var assets = root.TryGetProperty("assets", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().ToArray() : [];
            if (assets.Length > 128) throw InvalidRelease("The release contains too many assets.");
            foreach (var item in assets)
            {
                if (item.ValueKind != JsonValueKind.Object) throw InvalidRelease("A release asset is invalid.");
                var name = ReadString(item, "name", 180);
                if (IsCompatibleAssetName(name, tag, version)) candidates.Add(item);
            }

            if (candidates.Count != 1)
                return new ReleaseUpdateInfo(tag, version, releasePage, isNewer, null,
                    candidates.Count == 0
                        ? "This release has no complete Windows x64 Native AOT ZIP."
                        : "This release has more than one compatible Windows x64 ZIP.");

            var candidate = candidates[0];
            var assetName = ReadString(candidate, "name", 180);
            var size = ReadAssetSize(candidate, MaximumArchiveBytes);
            var assetUri = ParseAssetUri(candidate, tag, assetName);
            var digest = ReadDigest(candidate);
            var checksumCandidates = assets.Where(item =>
            {
                var name = ReadString(item, "name", 180);
                return string.Equals(name, assetName + ".sha256", StringComparison.Ordinal)
                    || name is "SHA256SUMS" or "SHA256SUMS.txt" or "checksums.sha256";
            }).ToArray();
            var exactChecksum = checksumCandidates.Where(item =>
                ReadString(item, "name", 180) == assetName + ".sha256").ToArray();
            if (exactChecksum.Length > 1 || (exactChecksum.Length == 0 && checksumCandidates.Length > 1))
                throw InvalidChecksum("The release checksum asset is ambiguous.");
            ReleaseChecksumAsset? checksum = null;
            var selectedChecksum = exactChecksum.Length == 1 ? exactChecksum[0]
                : checksumCandidates.Length == 1 ? checksumCandidates[0] : (JsonElement?)null;
            if (selectedChecksum is { } checksumItem)
            {
                var name = ReadString(checksumItem, "name", 180);
                checksum = new ReleaseChecksumAsset(name, ParseAssetUri(checksumItem, tag, name),
                    ReadAssetSize(checksumItem, MaximumChecksumBytes), ReadDigest(checksumItem));
            }

            return new ReleaseUpdateInfo(tag, version, releasePage, isNewer,
                new ReleaseUpdateAsset(assetName, assetUri, size, digest, checksum), null);
        }
        catch (JsonException exception)
        {
            throw InvalidRelease("The release response is invalid JSON.", exception);
        }
    }

    public static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        var text = value[0] is 'v' or 'V' ? value[1..] : value;
        var parts = text.Split('.');
        if (parts.Length is < 3 or > 4
            || parts.Any(part => part.Length == 0 || part.Any(character => character is < '0' or > '9'))
            || !Version.TryParse(text, out var parsed)) return false;
        version = parsed;
        return true;
    }

    public static Version NormalizeVersion(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public static bool IsCompatibleAssetName(string name, string tag, Version version) =>
        string.Equals(name, AssetPrefix + ".zip", StringComparison.Ordinal)
        || string.Equals(name, $"{AssetPrefix}-{tag}.zip", StringComparison.Ordinal)
        || string.Equals(name, $"{AssetPrefix}-{version}.zip", StringComparison.Ordinal);

    public static string ParseChecksum(string text, string assetName, bool allowBareHash)
    {
        if (text.Length > MaximumChecksumBytes) throw InvalidChecksum("The checksum text exceeds the size limit.");
        string? result = null;
        var lines = text.TrimStart('\uFEFF').Split('\n');
        foreach (var line in lines)
        {
            var value = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (allowBareHash && lines.Count(item => !string.IsNullOrWhiteSpace(item)) == 1
                && IsSha256(value.Trim())) return value.Trim().ToLowerInvariant();
            if (value.Length < 66 || !IsSha256(value[..64]) || !char.IsWhiteSpace(value[64]))
                throw InvalidChecksum("A checksum line does not use the SHA256SUMS format.");
            var name = value[64..].TrimStart(' ', '\t');
            if (name.StartsWith('*')) name = name[1..];
            if (!string.Equals(name, assetName, StringComparison.Ordinal)) continue;
            if (result is not null) throw InvalidChecksum("The asset has duplicate checksum entries.");
            result = value[..64].ToLowerInvariant();
        }
        return result ?? throw InvalidChecksum("No SHA256 checksum exists for the selected asset.");
    }

    public static bool IsSha256(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>Uses Windows filename rules even when exercised by portable tests on another OS.</summary>
    public static string ValidateRelativePath(string path, bool directory = false)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/'))
            throw InvalidArchive("An archive entry has an unsafe path.");
        if (directory && path.EndsWith('/')) path = path[..^1];
        foreach (var part in path.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')
                || part.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)))
                throw InvalidArchive("An archive entry has an unsafe Windows filename.");
            var stem = part.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789\u00B9\u00B2\u00B3".Contains(stem[3])))
                throw InvalidArchive("An archive entry names a Windows device.");
        }
        return path;
    }

    internal static ReleaseArchivePlan InspectArchive(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > MaximumEntries)
            throw InvalidArchive("The archive has an invalid number of entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<ReleaseArchiveFile>();
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var directory = entry.FullName.EndsWith('/');
            var path = ValidateRelativePath(entry.FullName, directory);
            var kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((kind != 0 && kind != (directory ? 0x4000 : 0x8000))
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw InvalidArchive("The archive contains a link or a special filesystem entry.");
            if (!names.Add(path) || (!directory && directories.Contains(path))
                || (directory && fileNames.Contains(path)))
                throw InvalidArchive("Archive paths collide under Windows filename rules.");
            var slash = path.LastIndexOf('/');
            while (slash >= 0)
            {
                var parent = path[..slash];
                if (fileNames.Contains(parent)) throw InvalidArchive("An archive file is also used as a directory.");
                directories.Add(parent);
                slash = parent.LastIndexOf('/');
            }
            if (directory)
            {
                if (entry.Length != 0) throw InvalidArchive("A directory entry contains data.");
                directories.Add(path);
                continue;
            }
            if (entry.Length < 0 || entry.Length > MaximumEntryBytes || entry.CompressedLength < 0
                || entry.CompressedLength > MaximumArchiveBytes || entry.Length > MaximumExpandedBytes - totalBytes)
                throw InvalidArchive("The archive exceeds the payload size limit.");
            totalBytes += entry.Length;
            fileNames.Add(path);
            files.Add(new ReleaseArchiveFile(entry, path));
        }

        var executables = files.Where(file => string.Equals(file.Path, ApplicationFileName, StringComparison.OrdinalIgnoreCase)
            || file.Path.EndsWith('/' + ApplicationFileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (executables.Length != 1) throw InvalidArchive("The archive must contain one complete application folder.");
        var executable = executables[0].Path;
        var prefix = executable == ApplicationFileName ? string.Empty : executable[..^ApplicationFileName.Length];
        if (prefix.Count(character => character == '/') > 1
            || files.Any(file => !file.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw InvalidArchive("Only an application folder or a single enclosing directory is supported.");
        var normalized = files.Select(file => new ReleaseArchiveFile(file.Entry, file.Path[prefix.Length..])).ToArray();
        if (!normalized.Any(file => file.Path == ManifestFileName))
            throw InvalidArchive("The archive is missing its complete release payload manifest.");
        return new ReleaseArchivePlan(normalized, totalBytes);
    }

    /// <summary>Rejects oversized central directories before ZipArchive allocates its entry collection.</summary>
    internal static ReleaseZipEnvelope InspectZipEnvelope(ReadOnlySpan<byte> tail, long archiveLength)
    {
        if (archiveLength < 22 || archiveLength > MaximumArchiveBytes || tail.Length < 22)
            throw InvalidArchive("The ZIP envelope size is invalid.");
        for (var offset = tail.Length - 22; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail[offset..]) != 0x06054b50) continue;
            var end = tail[offset..];
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(end[20..]);
            if (offset + 22 + commentLength != tail.Length) continue;
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(end[4..]);
            var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(end[6..]);
            var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(end[8..]);
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(end[10..]);
            var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..]);
            var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..]);
            var endOffset = archiveLength - tail.Length + offset;
            if (disk != 0 || directoryDisk != 0 || diskEntries != entries || entries is 0 or > MaximumEntries
                || directorySize > 16 * 1024 * 1024 || directoryOffset + (long)directorySize > endOffset
                || (offset >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail[(offset - 20)..]) == 0x07064b50))
                throw InvalidArchive("Multi-volume, ZIP64, or oversized ZIP entry directories are unsupported.");
            return new ReleaseZipEnvelope(entries, (int)directorySize, directoryOffset);
        }
        throw InvalidArchive("The ZIP end-of-directory record is missing or has trailing data.");
    }

    internal static void InspectCentralDirectory(ReadOnlySpan<byte> bytes, int expectedEntries)
    {
        var offset = 0;
        var entries = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 46 || ++entries > expectedEntries || entries > MaximumEntries
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]) != 0x02014b50)
                throw InvalidArchive("The ZIP central directory is invalid or has too many entries.");
            var entry = bytes[offset..];
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[28..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[30..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(entry[32..]);
            var disk = BinaryPrimitives.ReadUInt16LittleEndian(entry[34..]);
            var recordLength = 46 + nameLength + extraLength + commentLength;
            if ((flags & 1) != 0 || nameLength is 0 or > 960 || disk != 0 || recordLength > bytes.Length - offset
                || BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]) > MaximumArchiveBytes
                || BinaryPrimitives.ReadUInt32LittleEndian(entry[24..]) > MaximumEntryBytes)
                throw InvalidArchive("Encrypted, ZIP64, or oversized ZIP entries are unsupported.");
            offset += recordLength;
        }
        if (entries != expectedEntries) throw InvalidArchive("The ZIP entry count does not match its envelope.");
    }

    internal static IReadOnlyList<ReleaseUpdateFile> ParsePayloadManifest(ReadOnlyMemory<byte> bytes, Version version)
    {
        if (bytes.Length > MaximumManifestBytes) throw InvalidPayload("The payload manifest exceeds the size limit.");
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1 || ReadString(root, "architecture", 16) != "x64"
                || ReadString(root, "executable", 100) != ApplicationFileName
                || !TryParseVersion(ReadString(root, "version", 128), out var manifestVersion)
                || NormalizeVersion(manifestVersion) != NormalizeVersion(version)
                || !root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array
                || files.GetArrayLength() is 0 or >= MaximumEntries)
                throw InvalidPayload("The payload manifest does not match the selected release.");
            var result = new List<ReleaseUpdateFile>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in files.EnumerateArray())
            {
                var path = ValidateRelativePath(ReadString(file, "path", 240));
                var hash = ReadString(file, "sha256", 64);
                if (path == ManifestFileName || !paths.Add(path) || !IsSha256(hash)
                    || !file.TryGetProperty("length", out var size) || size.ValueKind != JsonValueKind.Number
                    || !size.TryGetInt64(out var length)
                    || length < 0 || length > MaximumEntryBytes || length > MaximumExpandedBytes - total)
                    throw InvalidPayload("The payload manifest contains an invalid file entry.");
                total += length;
                result.Add(new ReleaseUpdateFile(path, length, hash.ToLowerInvariant()));
            }
            ValidateRequiredPayload(result);
            return result.AsReadOnly();
        }
        catch (Exception exception) when (exception is JsonException
            || exception is ReleaseUpdateException { Issue: ReleaseUpdateIssue.InvalidRelease or ReleaseUpdateIssue.InvalidArchive })
        {
            throw InvalidPayload("The payload manifest is invalid.", exception);
        }
    }

    internal static void ValidateRequiredPayload(IReadOnlyList<ReleaseUpdateFile> files)
    {
        var names = files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var required in RequiredPayloadFiles)
            if (!names.TryGetValue(required, out var file) || file.Length <= 0)
                throw InvalidPayload($"The full application payload is missing {required}.");
        if (ForbiddenPayloadFiles.Any(names.ContainsKey))
            throw InvalidPayload("Only a complete unpackaged Native AOT payload can be installed.");
        foreach (var family in FfmpegFamilies)
        {
            var matches = files.Where(file => IsFfmpegLibrary(file.Path, family)).ToArray();
            if (matches.Length != 1 || matches[0].Length <= 0)
                throw InvalidPayload($"The payload must contain one {family} shared library.");
        }
        if (!files.Any(file => file.Path.StartsWith("Assets/Lumen/Icons/", StringComparison.OrdinalIgnoreCase)
            && file.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && file.Length > 0))
            throw InvalidPayload("The payload is missing the application icon library.");
    }

    internal static bool IsFfmpegLibrary(string path, string family)
    {
        if (path.Contains('/') || !path.StartsWith(family + '-', StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        var major = path[(family.Length + 1)..^4];
        return major.Length > 0 && major.All(character => character is >= '0' and <= '9');
    }

    internal static ReleaseInstallLaunchFiles CreateInstallLaunchFiles(string directory)
    {
        var launchId = Guid.NewGuid().ToString("N");
        return new ReleaseInstallLaunchFiles(launchId,
            GetContainedPath(directory, $"Install-ReleaseUpdate-{launchId}.ps1"),
            GetContainedPath(directory, $"install-plan-{launchId}.json"),
            GetContainedPath(directory, $"install-ready-{launchId}"),
            GetContainedPath(directory, $"install-cancel-{launchId}"));
    }

    internal static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static string GetContainedPath(string directory, string relativePath)
    {
        ValidateRelativePath(relativePath);
        var path = Path.GetFullPath(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithinDirectory(path, directory)) throw InvalidArchive("A payload path escapes its directory.");
        return path;
    }

    internal static void AssertOrdinaryPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw InvalidPayload("Update paths cannot traverse links or reparse points.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    internal static bool IsAllowedDownloadUri(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 4096
        && uri.Host is "github.com" or "release-assets.githubusercontent.com"
            or "objects.githubusercontent.com" or "github-releases.githubusercontent.com";

    private static Uri ParseAssetUri(JsonElement item, string tag, string name)
    {
        if (ReadString(item, "state", 32) != "uploaded") throw InvalidRelease("The release asset is not fully uploaded.");
        return ParseRepositoryUri(ReadString(item, "browser_download_url", 4096),
            $"/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}");
    }

    private static Uri ParseRepositoryUri(string value, string expectedPath)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.Query.Length != 0 || !string.Equals(uri.AbsolutePath, expectedPath, StringComparison.Ordinal))
            throw InvalidRelease("A release URL is outside the configured repository.");
        return uri;
    }

    private static long ReadAssetSize(JsonElement item, long maximum)
    {
        if (!item.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var length) || length <= 0 || length > maximum)
            throw InvalidRelease("A release asset has an invalid size.");
        return length;
    }

    private static string? ReadDigest(JsonElement item)
    {
        if (!item.TryGetProperty("digest", out var digest) || digest.ValueKind == JsonValueKind.Null) return null;
        if (digest.ValueKind != JsonValueKind.String || digest.GetString() is not { } text
            || !text.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || !IsSha256(text[7..]))
            throw InvalidChecksum("The release asset digest is not a valid SHA256 checksum.");
        return text[7..].ToLowerInvariant();
    }

    private static bool? ReadBoolean(JsonElement root, string name) => root.TryGetProperty(name, out var property)
        ? property.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    private static string ReadString(JsonElement root, string name, int maximum)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String || property.GetString() is not { Length: > 0 } value
            || value.Length > maximum || value.Any(char.IsControl))
            throw InvalidRelease($"The release field {name} is invalid.");
        return value;
    }

    internal static ReleaseUpdateException InvalidRelease(string message, Exception? inner = null) =>
        new(ReleaseUpdateIssue.InvalidRelease, message, inner);
    internal static ReleaseUpdateException InvalidChecksum(string message, Exception? inner = null) =>
        new(ReleaseUpdateIssue.InvalidChecksum, message, inner);
    internal static ReleaseUpdateException InvalidArchive(string message, Exception? inner = null) =>
        new(ReleaseUpdateIssue.InvalidArchive, message, inner);
    internal static ReleaseUpdateException InvalidPayload(string message, Exception? inner = null) =>
        new(ReleaseUpdateIssue.InvalidPayload, message, inner);
}
