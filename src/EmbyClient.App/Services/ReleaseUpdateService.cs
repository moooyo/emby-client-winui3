using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyClient.App.Services;

/// <summary>Downloads complete releases on request; installation requires a separate explicit call.</summary>
public sealed partial class ReleaseUpdateService : IDisposable
{
    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{ReleaseUpdateRules.Repository}/releases/latest");
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _stagingRoot;
    private readonly string _applicationDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StagedReleaseUpdate> _stages = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="httpClient">An optional isolated transport for tests, with no credentials or automatic redirects.</param>
    public ReleaseUpdateService(HttpClient? httpClient = null, string? stagingDirectory = null,
        string? applicationDirectory = null)
    {
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            AutomaticDecompression = DecompressionMethods.None
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsHttp = httpClient is null;
        _stagingRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            stagingDirectory ?? Path.Combine(AppDataPaths.RootDirectory, "updates")));
        _applicationDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            applicationDirectory ?? AppContext.BaseDirectory));
    }

    public async Task<ReleaseUpdateInfo?> CheckLatestAsync(Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = CreateRequest(LatestReleaseUri, json: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is { } actual && actual != LatestReleaseUri)
            throw ReleaseUpdateRules.InvalidRelease("The update transport must not follow redirects automatically.");
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedResponseAsync(response, ReleaseUpdateRules.MaximumReleaseBytes,
            expectedLength: null, timeout.Token).ConfigureAwait(false);
        return ReleaseUpdateRules.ParseLatestRelease(bytes, currentVersion);
    }

    public async Task<StagedReleaseUpdate> DownloadAndStageAsync(ReleaseUpdateInfo release,
        IProgress<ReleaseUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var asset = ValidateSelectedRelease(release);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stageDirectory = null;
        var completed = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            var token = timeout.Token;
            var identifier = $"{release.Version}-{Guid.NewGuid():N}";
            var downloadRoot = Path.Combine(_stagingRoot, "downloads");
            var installRoot = Path.Combine(_stagingRoot, "installed");
            if (downloadRoot.Equals(_applicationDirectory, StringComparison.OrdinalIgnoreCase)
                || ReleaseUpdateRules.IsWithinDirectory(downloadRoot, _applicationDirectory)
                || installRoot.Equals(_applicationDirectory, StringComparison.OrdinalIgnoreCase)
                || ReleaseUpdateRules.IsWithinDirectory(installRoot, _applicationDirectory))
                throw ReleaseUpdateRules.InvalidPayload("Update storage must be outside the running application folder.");
            stageDirectory = Path.Combine(downloadRoot, identifier);
            var payloadDirectory = Path.Combine(stageDirectory, "payload");
            var installDirectory = Path.Combine(installRoot, identifier);
            ReleaseUpdateRules.AssertOrdinaryPath(stageDirectory);
            ReleaseUpdateRules.AssertOrdinaryPath(installDirectory);
            Directory.CreateDirectory(payloadDirectory);
            var archivePath = Path.Combine(stageDirectory, "release.zip");
            progress?.Report(new(ReleaseUpdatePhase.Downloading, 0, asset.Size));
            var archiveHash = await DownloadArchiveAsync(asset, archivePath, progress, token).ConfigureAwait(false);
            progress?.Report(new(ReleaseUpdatePhase.Verifying, asset.Size, asset.Size));
            var checksumVerified = await VerifyArchiveChecksumAsync(asset, archiveHash, token).ConfigureAwait(false);
            var files = await ExtractPayloadAsync(archivePath, payloadDirectory, release.Version, progress, token)
                .ConfigureAwait(false);
            await ValidateNativePayloadAsync(payloadDirectory, release.Version, token).ConfigureAwait(false);
            var receipt = CreateReceipt(release, archiveHash, checksumVerified, files);
            var receiptPath = Path.Combine(stageDirectory, "stage-receipt.json");
            await WriteNewFileAsync(receiptPath, receipt, token).ConfigureAwait(false);
            var staged = new StagedReleaseUpdate(release, stageDirectory, payloadDirectory, installDirectory,
                checksumVerified, archiveHash, files, HashBytes(receipt));
            await WriteNewFileAsync(staged.RecoveryInstructionsPath,
                Encoding.UTF8.GetBytes(CreateRecoveryInstructions(staged)), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _stages.Add(stageDirectory, staged);
            completed = true;
            progress?.Report(new(ReleaseUpdatePhase.Ready, asset.Size, asset.Size));
            return staged;
        }
        catch (InvalidDataException exception)
        {
            throw ReleaseUpdateRules.InvalidArchive("The downloaded archive could not be read.", exception);
        }
        finally
        {
            if (!completed && stageDirectory is not null) TryDeleteOwnedStage(stageDirectory);
            _gate.Release();
        }
    }

    /// <summary>Starts the trusted helper. The caller must exit the app only after this call succeeds.</summary>
    public async Task LaunchInstallAndRestartAsync(StagedReleaseUpdate staged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staged);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_stages.TryGetValue(staged.DirectoryPath, out var owned) || !ReferenceEquals(owned, staged))
                throw ReleaseUpdateRules.InvalidPayload("Only a complete download from this update session can be installed.");
            var previousExecutable = ValidateCurrentInstallation();
            await ValidateStagedPayloadAsync(staged, cancellationToken).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var launchFiles = ReleaseUpdateRules.CreateInstallLaunchFiles(staged.DirectoryPath);
            var plan = CreateInstallPlan(staged, previousExecutable, process.Id,
                process.StartTime.ToUniversalTime().ToString("O"), launchFiles.LaunchId);
            await WriteNewFileAsync(launchFiles.HelperPath, Encoding.UTF8.GetBytes(ReleaseUpdateInstallerScript.Content),
                cancellationToken).ConfigureAwait(false);
            await WriteNewFileAsync(launchFiles.PlanPath, plan, cancellationToken).ConfigureAwait(false);
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            if (!File.Exists(powershell))
                throw new ReleaseUpdateException(ReleaseUpdateIssue.UnsupportedInstallation,
                    "Windows PowerShell is unavailable. The validated payload remains available for a manual folder install.");
            var start = new ProcessStartInfo
            {
                FileName = powershell, UseShellExecute = false, CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = staged.DirectoryPath
            };
            foreach (var argument in new[]
            {
                "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", launchFiles.HelperPath, "-PlanPath", launchFiles.PlanPath
            }) start.ArgumentList.Add(argument);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var helper = Process.Start(start);
                if (helper is null) throw new InvalidOperationException("Windows did not create the update helper process.");
                await WaitForHelperReadyAsync(helper, launchFiles, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await SignalHelperCancellationAsync(launchFiles).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
                or InvalidOperationException or IOException)
            {
                await SignalHelperCancellationAsync(launchFiles).ConfigureAwait(false);
                throw new ReleaseUpdateException(ReleaseUpdateIssue.InstallLaunchFailed,
                    "The update helper could not start. The running application and validated download were retained.", exception);
            }
            _stages.Remove(staged.DirectoryPath);
        }
        finally { _gate.Release(); }
    }

    private async Task<string> DownloadArchiveAsync(ReleaseUpdateAsset asset, string destination,
        IProgress<ReleaseUpdateProgress>? progress, CancellationToken token)
    {
        using var response = await GetAssetResponseAsync(asset.DownloadUri, token).ConfigureAwait(false);
        ValidateResponseLength(response, asset.Size, ReleaseUpdateRules.MaximumArchiveBytes);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        var nextReport = Stopwatch.GetTimestamp();
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > asset.Size - received || count > ReleaseUpdateRules.MaximumArchiveBytes - received)
                throw ReleaseUpdateRules.InvalidArchive("The download exceeds its advertised or permitted size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer.AsSpan(0, count));
            received += count;
            var now = Stopwatch.GetTimestamp();
            if (now >= nextReport)
            {
                progress?.Report(new(ReleaseUpdatePhase.Downloading, received, asset.Size));
                nextReport = now + Stopwatch.Frequency / 10;
            }
        }
        if (received != asset.Size) throw ReleaseUpdateRules.InvalidArchive("The release download is incomplete.");
        await output.FlushAsync(token).ConfigureAwait(false);
        output.Flush(flushToDisk: true);
        token.ThrowIfCancellationRequested();
        progress?.Report(new(ReleaseUpdatePhase.Downloading, received, asset.Size));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private async Task<bool> VerifyArchiveChecksumAsync(ReleaseUpdateAsset asset, string actualHash, CancellationToken token)
    {
        if (asset.Sha256 is not null && !HashesMatch(actualHash, asset.Sha256))
            throw ReleaseUpdateRules.InvalidChecksum("The release archive does not match the GitHub SHA256 digest.");
        if (asset.Checksum is { } checksum)
        {
            using var response = await GetAssetResponseAsync(checksum.DownloadUri, token).ConfigureAwait(false);
            var bytes = await ReadBoundedResponseAsync(response, ReleaseUpdateRules.MaximumChecksumBytes,
                checksum.Size, token).ConfigureAwait(false);
            if (checksum.Sha256 is not null && !HashesMatch(HashBytes(bytes), checksum.Sha256))
                throw ReleaseUpdateRules.InvalidChecksum("The checksum asset does not match its GitHub SHA256 digest.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException exception)
            {
                throw ReleaseUpdateRules.InvalidChecksum("The published checksum is not valid UTF-8 text.", exception);
            }
            var expected = ReleaseUpdateRules.ParseChecksum(text, asset.Name, checksum.Name == asset.Name + ".sha256");
            if (!HashesMatch(actualHash, expected))
                throw ReleaseUpdateRules.InvalidChecksum("The release archive does not match its published SHA256 checksum.");
        }
        return asset.Sha256 is not null || asset.Checksum is not null;
    }

    private static async Task<IReadOnlyList<ReleaseUpdateFile>> ExtractPayloadAsync(string archivePath,
        string payloadDirectory, Version version, IProgress<ReleaseUpdateProgress>? progress, CancellationToken token)
    {
        await using (var envelope = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous))
        {
            var tail = new byte[(int)Math.Min(envelope.Length, 65557)];
            envelope.Position = envelope.Length - tail.Length;
            await envelope.ReadExactlyAsync(tail, token).ConfigureAwait(false);
            var info = ReleaseUpdateRules.InspectZipEnvelope(tail, envelope.Length);
            var centralDirectory = new byte[info.DirectoryLength];
            envelope.Position = info.DirectoryOffset;
            await envelope.ReadExactlyAsync(centralDirectory, token).ConfigureAwait(false);
            ReleaseUpdateRules.InspectCentralDirectory(centralDirectory, info.EntryCount);
        }
        using var archive = ZipFile.OpenRead(archivePath);
        var plan = ReleaseUpdateRules.InspectArchive(archive);
        var manifestEntry = plan.Files.Single(file => file.Path == ReleaseUpdateRules.ManifestFileName).Entry;
        if (manifestEntry.Length > ReleaseUpdateRules.MaximumManifestBytes)
            throw ReleaseUpdateRules.InvalidPayload("The payload manifest exceeds its size limit.");
        await using var manifestInput = manifestEntry.Open();
        var manifestBytes = await ReadBoundedStreamAsync(manifestInput, ReleaseUpdateRules.MaximumManifestBytes, token)
            .ConfigureAwait(false);
        var expected = ReleaseUpdateRules.ParsePayloadManifest(manifestBytes, version)
            .ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var payloadFiles = plan.Files.Where(file => file.Path != ReleaseUpdateRules.ManifestFileName).ToArray();
        if (payloadFiles.Length != expected.Count || payloadFiles.Any(file => !expected.ContainsKey(file.Path)))
            throw ReleaseUpdateRules.InvalidPayload("The archive does not contain exactly the complete manifest payload.");
        var result = new List<ReleaseUpdateFile>();
        long expanded = 0;
        var nextReport = Stopwatch.GetTimestamp();
        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            var destination = ReleaseUpdateRules.GetContainedPath(payloadDirectory, file.Path);
            ReleaseUpdateRules.AssertOrdinaryPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = file.Entry.Open();
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long length = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count > file.Entry.Length - length || count > ReleaseUpdateRules.MaximumExpandedBytes - expanded)
                    throw ReleaseUpdateRules.InvalidArchive("The expanded archive exceeds its advertised size.");
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                hash.AppendData(buffer.AsSpan(0, count));
                length += count;
                expanded += count;
                var now = Stopwatch.GetTimestamp();
                if (now >= nextReport)
                {
                    progress?.Report(new(ReleaseUpdatePhase.Extracting, expanded, plan.TotalBytes));
                    nextReport = now + Stopwatch.Frequency / 10;
                }
            }
            if (length != file.Entry.Length) throw ReleaseUpdateRules.InvalidArchive("An archive entry is incomplete.");
            await output.FlushAsync(token).ConfigureAwait(false);
            var actual = new ReleaseUpdateFile(file.Path, length,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
            if (file.Path != ReleaseUpdateRules.ManifestFileName
                && (expected[file.Path].Length != length || !HashesMatch(actual.Sha256, expected[file.Path].Sha256)))
                throw ReleaseUpdateRules.InvalidPayload($"The release file does not match its manifest: {file.Path}");
            result.Add(actual);
        }
        return result.AsReadOnly();
    }

    private async Task ValidateStagedPayloadAsync(StagedReleaseUpdate staged, CancellationToken token)
    {
        if (!ReleaseUpdateRules.IsWithinDirectory(staged.DirectoryPath, Path.Combine(_stagingRoot, "downloads"))
            || !staged.PayloadDirectory.Equals(Path.Combine(staged.DirectoryPath, "payload"), StringComparison.OrdinalIgnoreCase)
            || !ReleaseUpdateRules.IsWithinDirectory(staged.InstallDirectory, Path.Combine(_stagingRoot, "installed")))
            throw ReleaseUpdateRules.InvalidPayload("The staged update paths are invalid.");
        ReleaseUpdateRules.AssertOrdinaryPath(staged.DirectoryPath);
        ReleaseUpdateRules.AssertOrdinaryPath(staged.InstallDirectory);
        var receipt = await ReadBoundedFileAsync(Path.Combine(staged.DirectoryPath, "stage-receipt.json"),
            ReleaseUpdateRules.MaximumManifestBytes, token).ConfigureAwait(false);
        if (!HashesMatch(HashBytes(receipt), staged.ReceiptSha256))
            throw ReleaseUpdateRules.InvalidPayload("The staged receipt changed after verification.");
        var actualPaths = EnumerateOrdinaryFiles(staged.PayloadDirectory)
            .Select(path => Path.GetRelativePath(staged.PayloadDirectory, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actualPaths.SetEquals(staged.Files.Select(file => file.Path)))
            throw ReleaseUpdateRules.InvalidPayload("The staged payload file set changed after verification.");
        foreach (var file in staged.Files)
        {
            token.ThrowIfCancellationRequested();
            var path = ReleaseUpdateRules.GetContainedPath(staged.PayloadDirectory, file.Path);
            if (new FileInfo(path).Length != file.Length
                || !HashesMatch(await HashFileAsync(path, token).ConfigureAwait(false), file.Sha256))
                throw ReleaseUpdateRules.InvalidPayload("A staged release file changed after verification.");
        }
        await ValidateNativePayloadAsync(staged.PayloadDirectory, staged.Release.Version, token).ConfigureAwait(false);
    }

    private static async Task ValidateNativePayloadAsync(string directory, Version version, CancellationToken token)
    {
        ValidateNativeX64Image(Path.Combine(directory, ReleaseUpdateRules.ApplicationFileName), requireAotExecutable: true);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            ValidateNativeX64Image(file, requireAotExecutable: false);
        }
        var sbom = await ReadBoundedFileAsync(Path.Combine(directory, "emby-client-win-x64.cdx.json"),
            16 * 1024 * 1024, token).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(sbom, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (!root.TryGetProperty("bomFormat", out var format) || format.GetString() != "CycloneDX"
                || !root.TryGetProperty("metadata", out var metadata) || metadata.ValueKind != JsonValueKind.Object
                || !metadata.TryGetProperty("component", out var component) || component.ValueKind != JsonValueKind.Object
                || !component.TryGetProperty("version", out var componentVersion) || componentVersion.ValueKind != JsonValueKind.String
                || !ReleaseUpdateRules.TryParseVersion(componentVersion.GetString(), out var sbomVersion)
                || ReleaseUpdateRules.NormalizeVersion(sbomVersion) != ReleaseUpdateRules.NormalizeVersion(version))
                throw ReleaseUpdateRules.InvalidPayload("The application SBOM does not match the selected release version.");
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw ReleaseUpdateRules.InvalidPayload("The application SBOM is invalid.", exception);
        }
    }

    internal static void ValidateNativeX64Image(string path, bool requireAotExecutable)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 256 || reader.ReadUInt16() != 0x5a4d)
            throw ReleaseUpdateRules.InvalidPayload("The release contains an invalid PE image.");
        stream.Position = 0x3c;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > 1024 * 1024 || offset + 264L > stream.Length)
            throw ReleaseUpdateRules.InvalidPayload("A PE image header is invalid.");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
            throw ReleaseUpdateRules.InvalidPayload("The full release payload must target Windows x64.");
        stream.Position = offset + 20;
        var optionalSize = reader.ReadUInt16();
        var characteristics = reader.ReadUInt16();
        if (optionalSize < 240 || offset + 24L + optionalSize > stream.Length || reader.ReadUInt16() != 0x20b)
            throw ReleaseUpdateRules.InvalidPayload("A release PE image is not PE32+.");
        if (!requireAotExecutable) return;
        if ((characteristics & 0x0002) == 0 || (characteristics & 0x2000) != 0)
            throw ReleaseUpdateRules.InvalidPayload("The application entry point is not an executable image.");
        stream.Position = offset + 24 + 108;
        var directoryCount = reader.ReadUInt32();
        if (directoryCount < 15) throw ReleaseUpdateRules.InvalidPayload("The application PE data directories are incomplete.");
        stream.Position = offset + 24 + 112 + 14 * 8;
        if (reader.ReadUInt32() != 0 || reader.ReadUInt32() != 0)
            throw ReleaseUpdateRules.InvalidPayload("The release application must be Native AOT, not a managed publish.");
    }

    private string ValidateCurrentInstallation()
    {
        var executable = Environment.ProcessPath;
        if (!OperatingSystem.IsWindows() || executable is null
            || !Path.GetFileName(executable).Equals(ReleaseUpdateRules.ApplicationFileName, StringComparison.OrdinalIgnoreCase)
            || !Path.GetDirectoryName(executable)!.Equals(_applicationDirectory, StringComparison.OrdinalIgnoreCase))
            throw new ReleaseUpdateException(ReleaseUpdateIssue.UnsupportedInstallation,
                "In-app installation is available only in the unpackaged Windows application.");
        uint packageNameLength = 0;
        if (GetCurrentPackageFullName(ref packageNameLength, IntPtr.Zero) != 15700)
            throw new ReleaseUpdateException(ReleaseUpdateIssue.UnsupportedInstallation,
                "Packaged applications must be updated through their package distribution channel.");
        ReleaseUpdateRules.AssertOrdinaryPath(_applicationDirectory);
        if (ReleaseUpdateRules.ForbiddenPayloadFiles.Any(file => File.Exists(Path.Combine(_applicationDirectory, file))))
            throw new ReleaseUpdateException(ReleaseUpdateIssue.UnsupportedInstallation,
                "Managed or packaged development outputs cannot use the folder-release installer.");
        ValidateNativeX64Image(executable, requireAotExecutable: true);
        return executable;
    }

    private async Task<HttpResponseMessage> GetAssetResponseAsync(Uri initial, CancellationToken token)
    {
        var uri = initial;
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (!ReleaseUpdateRules.IsAllowedDownloadUri(uri))
                throw ReleaseUpdateRules.InvalidRelease("The asset redirect is outside the GitHub download hosts.");
            using var request = CreateRequest(uri, json: false);
            var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.RequestMessage?.RequestUri is { } actual && actual != uri)
            {
                response.Dispose();
                throw ReleaseUpdateRules.InvalidRelease("The update transport must not follow redirects automatically.");
            }
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirects == 5)
                    throw ReleaseUpdateRules.InvalidRelease("The asset redirect limit was exceeded.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); }
            catch { response.Dispose(); throw; }
            return response;
        }
        throw ReleaseUpdateRules.InvalidRelease("The asset redirect limit was exceeded.");
    }

    private HttpRequestMessage CreateRequest(Uri uri, bool json)
    {
        if (_http.DefaultRequestHeaders.Any(header => header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || header.Key.Contains("Token", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || header.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase)))
            throw ReleaseUpdateRules.InvalidRelease("Updates require a transport isolated from account credentials.");
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Lumen-EmbyClient/0.1.0");
        request.Headers.Accept.ParseAdd(json ? "application/vnd.github+json" : "application/octet-stream");
        if (json) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static ReleaseUpdateAsset ValidateSelectedRelease(ReleaseUpdateInfo release)
    {
        if (!release.IsNewer) throw ReleaseUpdateRules.InvalidRelease("Only a newer stable release can be downloaded.");
        var asset = release.Asset ?? throw new ReleaseUpdateException(ReleaseUpdateIssue.NoCompatibleAsset,
            "No compatible complete Windows x64 ZIP is attached to this release.");
        if (!ReleaseUpdateRules.TryParseVersion(release.Tag, out var version)
            || ReleaseUpdateRules.NormalizeVersion(version) != ReleaseUpdateRules.NormalizeVersion(release.Version)
            || !ReleaseUpdateRules.IsCompatibleAssetName(asset.Name, release.Tag, release.Version)
            || asset.Size <= 0 || asset.Size > ReleaseUpdateRules.MaximumArchiveBytes
            || !IsRepositoryAssetUri(asset.DownloadUri, release.Tag, asset.Name)
            || (asset.Sha256 is not null && !ReleaseUpdateRules.IsSha256(asset.Sha256)))
            throw ReleaseUpdateRules.InvalidRelease("The selected release asset is invalid.");
        if (asset.Checksum is { } checksum
            && (checksum.Size <= 0 || checksum.Size > ReleaseUpdateRules.MaximumChecksumBytes
                || !IsRepositoryAssetUri(checksum.DownloadUri, release.Tag, checksum.Name)
                || (checksum.Sha256 is not null && !ReleaseUpdateRules.IsSha256(checksum.Sha256))))
            throw ReleaseUpdateRules.InvalidChecksum("The selected checksum asset is invalid.");
        return asset;
    }

    private static bool IsRepositoryAssetUri(Uri uri, string tag, string name) =>
        ReleaseUpdateRules.IsAllowedDownloadUri(uri) && uri.Host == "github.com" && uri.Query.Length == 0
        && uri.AbsolutePath == $"/{ReleaseUpdateRules.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    private static async Task<byte[]> ReadBoundedResponseAsync(HttpResponseMessage response, int maximum,
        long? expectedLength, CancellationToken token)
    {
        ValidateResponseLength(response, expectedLength, maximum);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = await ReadBoundedStreamAsync(input, maximum, token).ConfigureAwait(false);
        if (expectedLength.HasValue && bytes.LongLength != expectedLength.Value)
            throw ReleaseUpdateRules.InvalidRelease("The downloaded release metadata or checksum is incomplete.");
        return bytes;
    }

    private static void ValidateResponseLength(HttpResponseMessage response, long? expectedLength, long maximum)
    {
        if (response.Content.Headers.ContentLength is { } length
            && (length < 0 || length > maximum || (expectedLength.HasValue && length != expectedLength.Value)))
            throw ReleaseUpdateRules.InvalidRelease("The release response has an unexpected size.");
        if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw ReleaseUpdateRules.InvalidRelease("Encoded release downloads are not supported.");
    }

    private static async Task<byte[]> ReadBoundedStreamAsync(Stream input, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > maximum - output.Length)
                throw ReleaseUpdateRules.InvalidRelease("The response exceeds the permitted size.");
            output.Write(buffer, 0, count);
        }
        token.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, int maximum, CancellationToken token)
    {
        ReleaseUpdateRules.AssertOrdinaryPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > maximum) throw ReleaseUpdateRules.InvalidPayload("An update metadata file exceeds the size limit.");
        return await ReadBoundedStreamAsync(stream, maximum, token).ConfigureAwait(false);
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        var count = 0;
        while (pending.Count != 0)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                if (++count > ReleaseUpdateRules.MaximumEntries * 2)
                    throw ReleaseUpdateRules.InvalidPayload("The staged payload contains too many filesystem entries.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw ReleaseUpdateRules.InvalidPayload("The staged payload contains a filesystem link.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
                else yield return path;
            }
        }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool HashesMatch(string first, string second) => ReleaseUpdateRules.IsSha256(first)
        && ReleaseUpdateRules.IsSha256(second)
        && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(first), Convert.FromHexString(second));

    private static byte[] CreateReceipt(ReleaseUpdateInfo release, string hash, bool verified,
        IReadOnlyList<ReleaseUpdateFile> files) => WriteJson(writer =>
    {
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("version", release.Version.ToString());
        writer.WriteString("archiveSha256", hash);
        writer.WriteBoolean("checksumVerified", verified);
        WriteFiles(writer, files);
    });

    private byte[] CreateInstallPlan(StagedReleaseUpdate staged, string previousExecutable, int processId,
        string processStartTimeUtc, string launchId) => WriteJson(writer =>
    {
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("stageDirectory", staged.DirectoryPath);
        writer.WriteString("payloadDirectory", staged.PayloadDirectory);
        writer.WriteString("installRoot", Path.Combine(_stagingRoot, "installed"));
        writer.WriteString("installDirectory", staged.InstallDirectory);
        writer.WriteString("previousExecutable", previousExecutable);
        writer.WriteNumber("processId", processId);
        writer.WriteString("processStartTimeUtc", processStartTimeUtc);
        writer.WriteString("launchId", launchId);
        writer.WriteString("receiptSha256", staged.ReceiptSha256);
    });

    private static byte[] WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteFiles(Utf8JsonWriter writer, IReadOnlyList<ReleaseUpdateFile> files)
    {
        writer.WriteStartArray("files");
        foreach (var file in files)
        {
            writer.WriteStartObject();
            writer.WriteString("path", file.Path);
            writer.WriteNumber("length", file.Length);
            writer.WriteString("sha256", file.Sha256);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private string CreateRecoveryInstructions(StagedReleaseUpdate staged) =>
        $"Lumen release update recovery{Environment.NewLine}{Environment.NewLine}"
        + $"Validated release: {staged.Release.Tag}{Environment.NewLine}"
        + $"New complete application directory: {staged.InstallDirectory}{Environment.NewLine}"
        + $"Start Menu entry: {StagedReleaseUpdate.StartMenuEntryName}{Environment.NewLine}"
        + $"Previous complete application directory: {_applicationDirectory}{Environment.NewLine}"
        + $"Previous executable: {Path.Combine(_applicationDirectory, ReleaseUpdateRules.ApplicationFileName)}{Environment.NewLine}"
        + $"Installer log: {Path.Combine(staged.DirectoryPath, "install.log")}{Environment.NewLine}{Environment.NewLine}"
        + "The installer keeps the complete previous application directory and existing application data. "
        + "It creates or updates only its own current-user Start Menu shortcut. Existing portable shortcuts still open the old directory. "
        + "If installation fails before the new process is created, the helper attempts to restart the previous executable. "
        + "For later failures, inspect the log and launch a compatible complete version manually. "
        + "A downgrade requires compatible settings; no automatic data rollback or successful runtime activation is promised. "
        + "Do not copy individual executable or DLL files between versions, and do not copy decrypted account tokens."
        + Environment.NewLine;

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        ReleaseUpdateRules.AssertOrdinaryPath(path);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WaitForHelperReadyAsync(Process helper, ReleaseInstallLaunchFiles files, CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (helper.HasExited) throw new IOException("The installer exited before accepting the installation plan.");
            if (File.Exists(files.ReadyPath))
            {
                var acknowledgement = await ReadBoundedFileAsync(files.ReadyPath, 64, token).ConfigureAwait(false);
                if (Encoding.UTF8.GetString(acknowledgement) != files.LaunchId)
                    throw new IOException("The installer acknowledgement is invalid.");
                token.ThrowIfCancellationRequested();
                return;
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new IOException("The installer did not acknowledge the installation plan in time.");
    }

    private static async Task SignalHelperCancellationAsync(ReleaseInstallLaunchFiles files)
    {
        try
        {
            await WriteAtomicFileAsync(files.CancelPath,
                Encoding.UTF8.GetBytes(files.LaunchId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static async Task WriteAtomicFileAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewFileAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ReleaseUpdateRules.AssertOrdinaryPath(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private void TryDeleteOwnedStage(string directory)
    {
        try
        {
            if (ReleaseUpdateRules.IsWithinDirectory(directory, Path.Combine(_stagingRoot, "downloads")))
            {
                ReleaseUpdateRules.AssertOrdinaryPath(directory);
                // Enumerate first so recursive cleanup cannot traverse a replaced payload link.
                _ = EnumerateOrdinaryFiles(directory).ToArray();
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        // A downloaded stage is intentionally retained for inspection or manual recovery.
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, IntPtr packageFullName);
}
