using System.IO.Compression;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ReleaseUpdateTests
{
    private const string AssetName = "emby-client-windows-x64-aot-1.2.3.zip";
    private const string Tag = "v1.2.3";
    private static readonly Version ReleaseVersion = new(1, 2, 3);

    [Fact]
    public void Latest_release_selects_only_the_complete_x64_zip_and_normalizes_current_version()
    {
        var bytes = ReleaseJson(
            ("emby-client-windows-x86-aot-1.2.3.zip", 100, null),
            ("EmbyClient_1.2.3_x64.msix", 100, null),
            (AssetName, 100, "sha256:" + new string('a', 64)));

        var release = ReleaseUpdateRules.ParseLatestRelease(bytes, new Version(1, 2, 3, 0));

        Assert.Equal(ReleaseVersion, release.Version);
        Assert.False(release.IsNewer);
        Assert.NotNull(release.Asset);
        Assert.Equal(AssetName, release.Asset.Name);
        Assert.Equal(new string('a', 64), release.Asset.Sha256);
        Assert.Equal($"https://github.com/{ReleaseUpdateRules.Repository}/releases/tag/{Tag}", release.ReleasePage.AbsoluteUri);
    }

    [Fact]
    public void Ambiguous_or_missing_complete_assets_do_not_offer_installation()
    {
        var missing = ReleaseUpdateRules.ParseLatestRelease(
            ReleaseJson(("EmbyClient_1.2.3_x64.msix", 100, null)), new Version(1, 0, 0));
        var ambiguous = ReleaseUpdateRules.ParseLatestRelease(
            ReleaseJson((AssetName, 100, null), ("emby-client-windows-x64-aot.zip", 100, null)), new Version(1, 0, 0));

        Assert.True(missing.IsNewer);
        Assert.Null(missing.Asset);
        Assert.NotNull(missing.IncompatibilityReason);
        Assert.Null(ambiguous.Asset);
        Assert.NotNull(ambiguous.IncompatibilityReason);
    }

    [Theory]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2")]
    [InlineData("1.2.3+build")]
    [InlineData("1.2.3\n")]
    [InlineData("1.2.3/other")]
    public void Only_stable_numeric_versions_are_installable(string tag) =>
        Assert.False(ReleaseUpdateRules.TryParseVersion(tag, out _));

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("folder/../escape.dll")]
    [InlineData("/escape.dll")]
    [InlineData("C:/escape.dll")]
    [InlineData("folder\\escape.dll")]
    [InlineData("folder/file:stream")]
    [InlineData("folder/trailing.")]
    [InlineData("folder/trailing ")]
    [InlineData("folder//file.dll")]
    [InlineData("folder/NUL.txt")]
    [InlineData("Assets/NUL .txt")]
    [InlineData("Assets/COM1 .log")]
    [InlineData("folder/COM\u00B9.txt")]
    [InlineData("folder/LPT9")]
    public void Archive_paths_use_Windows_safety_rules_on_every_test_host(string path)
    {
        var exception = Assert.Throws<ReleaseUpdateException>(() => ReleaseUpdateRules.ValidateRelativePath(path));
        Assert.Equal(ReleaseUpdateIssue.InvalidArchive, exception.Issue);
    }

    [Fact]
    public void A_space_before_an_extension_is_allowed_for_an_ordinary_filename() =>
        Assert.Equal("Assets/normal .txt", ReleaseUpdateRules.ValidateRelativePath("Assets/normal .txt"));

    [Theory]
    [InlineData("Assets/Icon.svg", "assets/icon.svg")]
    [InlineData("Assets", "Assets/Icon.svg")]
    [InlineData("Assets/Icon.svg", "Assets")]
    public void Archive_case_and_file_directory_collisions_are_rejected(string first, string second)
    {
        var bytes = Archive(new Dictionary<string, byte[]>
        {
            [ReleaseUpdateRules.ApplicationFileName] = [1],
            [ReleaseUpdateRules.ManifestFileName] = [1], [first] = [1], [second] = [1]
        });
        using var input = new MemoryStream(bytes);
        using var archive = new ZipArchive(input);

        var exception = Assert.Throws<ReleaseUpdateException>(() => ReleaseUpdateRules.InspectArchive(archive));

        Assert.Equal(ReleaseUpdateIssue.InvalidArchive, exception.Issue);
    }

    [Fact]
    public void Symlinks_are_rejected_before_any_entry_is_extracted()
    {
        using var data = new MemoryStream();
        using (var archive = new ZipArchive(data, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, ReleaseUpdateRules.ApplicationFileName, [1]);
            WriteEntry(archive, ReleaseUpdateRules.ManifestFileName, [1]);
            var link = WriteEntry(archive, "Assets/link", Encoding.UTF8.GetBytes("../../elsewhere"));
            link.ExternalAttributes = unchecked((int)0xa1ff0000);
        }
        data.Position = 0;
        using var reopened = new ZipArchive(data);

        Assert.Equal(ReleaseUpdateIssue.InvalidArchive,
            Assert.Throws<ReleaseUpdateException>(() => ReleaseUpdateRules.InspectArchive(reopened)).Issue);
    }

    [Fact]
    public void One_enclosing_application_directory_is_supported_without_flattening_payload_subdirectories()
    {
        var bytes = Archive(new Dictionary<string, byte[]>
        {
            ["Release/EmbyClient.App.exe"] = [1], ["Release/release-update-manifest.json"] = [1],
            ["Release/Assets/Icons/icon.svg"] = [1]
        });
        using var input = new MemoryStream(bytes);
        using var archive = new ZipArchive(input);

        var plan = ReleaseUpdateRules.InspectArchive(archive);

        Assert.Contains(plan.Files, file => file.Path == "EmbyClient.App.exe");
        Assert.Contains(plan.Files, file => file.Path == "Assets/Icons/icon.svg");
    }

    [Fact]
    public void Zip_entry_count_cannot_hide_a_larger_central_directory()
    {
        var bytes = Archive(new Dictionary<string, byte[]> { ["first.txt"] = [1], ["second.txt"] = [2] });
        var end = bytes.AsSpan(bytes.Length - 22);
        BinaryPrimitives.WriteUInt16LittleEndian(end[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(end[10..], 1);
        var envelope = ReleaseUpdateRules.InspectZipEnvelope(bytes, bytes.Length);
        var directory = bytes.AsMemory((int)envelope.DirectoryOffset, envelope.DirectoryLength);

        Assert.Equal(ReleaseUpdateIssue.InvalidArchive,
            Assert.Throws<ReleaseUpdateException>(() => ReleaseUpdateRules.InspectCentralDirectory(directory.Span, envelope.EntryCount)).Issue);
    }

    [Fact]
    public void Checksum_entries_must_identify_the_selected_asset_once()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, ReleaseUpdateRules.ParseChecksum($"{new string('b', 64)}  other.zip\n{hash} *{AssetName}\n", AssetName, false));
        Assert.Equal(hash, ReleaseUpdateRules.ParseChecksum(hash.ToUpperInvariant(), AssetName, true));
        Assert.Equal(ReleaseUpdateIssue.InvalidChecksum,
            Assert.Throws<ReleaseUpdateException>(() => ReleaseUpdateRules.ParseChecksum(hash, AssetName, false)).Issue);
        Assert.Equal(ReleaseUpdateIssue.InvalidChecksum,
            Assert.Throws<ReleaseUpdateException>(() => ReleaseUpdateRules.ParseChecksum(
                $"{hash}  {AssetName}\n{hash}  {AssetName}", AssetName, false)).Issue);
    }

    [Fact]
    public void Installation_retries_cannot_reuse_an_earlier_helpers_plan_or_cancellation_marker()
    {
        var stage = Path.Combine(Path.GetTempPath(), "emby-update-launch-identity");
        var first = ReleaseUpdateRules.CreateInstallLaunchFiles(stage);
        var retry = ReleaseUpdateRules.CreateInstallLaunchFiles(stage);
        var firstPaths = new[] { first.HelperPath, first.PlanPath, first.ReadyPath, first.CancelPath };
        var retryPaths = new[] { retry.HelperPath, retry.PlanPath, retry.ReadyPath, retry.CancelPath };

        Assert.NotEqual(first.LaunchId, retry.LaunchId);
        Assert.Empty(firstPaths.Intersect(retryPaths, StringComparer.OrdinalIgnoreCase));
        Assert.Equal($"Install-ReleaseUpdate-{first.LaunchId}.ps1", Path.GetFileName(first.HelperPath));
        Assert.Equal($"install-plan-{first.LaunchId}.json", Path.GetFileName(first.PlanPath));
        Assert.All(firstPaths, path => Assert.Equal(Path.GetFullPath(stage), Path.GetDirectoryName(path)));
        Assert.All(retryPaths, path => Assert.Contains(retry.LaunchId, Path.GetFileName(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_complete_release_is_verified_and_staged_without_creating_an_installation(bool publishDigest)
    {
        using var temporary = new UpdateTestDirectory();
        var payload = CompletePayload();
        var archive = Archive(payload);
        var digest = publishDigest ? "sha256:" + Hash(archive) : null;
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson((AssetName, archive.Length, digest)), new Version(1, 0, 0));
        var phases = new List<ReleaseUpdatePhase>();
        using var handler = new UpdateHandler((_, _) => Task.FromResult(Response(archive)));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var staged = await service.DownloadAndStageAsync(release, new InlineProgress(update => phases.Add(update.Phase)));

        Assert.Equal(publishDigest, staged.ChecksumVerified);
        Assert.True(File.Exists(Path.Combine(staged.PayloadDirectory, ReleaseUpdateRules.ManifestFileName)));
        Assert.True(File.Exists(staged.RecoveryInstructionsPath));
        Assert.False(Directory.Exists(staged.InstallDirectory));
        Assert.Empty(Directory.EnumerateFiles(staged.DirectoryPath, "*.ps1"));
        Assert.Equal("Previous application remains intact.", File.ReadAllText(temporary.PreviousMarker));
        Assert.Contains(ReleaseUpdatePhase.Downloading, phases);
        Assert.Contains(ReleaseUpdatePhase.Verifying, phases);
        Assert.Contains(ReleaseUpdatePhase.Extracting, phases);
        Assert.Equal(ReleaseUpdatePhase.Ready, phases[^1]);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Published_sidecar_is_downloaded_and_checked_in_addition_to_the_archive_digest()
    {
        using var temporary = new UpdateTestDirectory();
        var archive = Archive(CompletePayload());
        var sidecar = Encoding.UTF8.GetBytes($"{Hash(archive)}  {AssetName}\n");
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson(
            (AssetName, archive.Length, "sha256:" + Hash(archive)),
            (AssetName + ".sha256", sidecar.Length, "sha256:" + Hash(sidecar))), new Version(1, 0, 0));
        using var handler = new UpdateHandler((request, _) => Task.FromResult(Response(
            request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? sidecar : archive)));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var staged = await service.DownloadAndStageAsync(release);

        Assert.True(staged.ChecksumVerified);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task A_mismatched_release_digest_discards_the_partial_stage_and_keeps_the_current_directory()
    {
        using var temporary = new UpdateTestDirectory();
        var archive = Archive(CompletePayload());
        var release = ReleaseUpdateRules.ParseLatestRelease(
            ReleaseJson((AssetName, archive.Length, "sha256:" + new string('0', 64))), new Version(1, 0, 0));
        using var handler = new UpdateHandler((_, _) => Task.FromResult(Response(archive)));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.DownloadAndStageAsync(release));

        Assert.Equal(ReleaseUpdateIssue.InvalidChecksum, exception.Issue);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.UpdateRoot, "downloads")));
        Assert.Equal("Previous application remains intact.", File.ReadAllText(temporary.PreviousMarker));
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("managed")]
    public async Task Matching_manifest_hashes_do_not_make_wrong_architecture_or_managed_app_images_installable(string kind)
    {
        using var temporary = new UpdateTestDirectory();
        var executable = NativePe(executable: true);
        if (kind == "x86") BinaryPrimitives.WriteUInt16LittleEndian(executable.AsSpan(132), 0x014c);
        else BinaryPrimitives.WriteUInt32LittleEndian(executable.AsSpan(128 + 24 + 112 + 14 * 8), 1);
        var archive = Archive(CompletePayload(executable));
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson((AssetName, archive.Length, null)), new Version(1, 0, 0));
        using var handler = new UpdateHandler((_, _) => Task.FromResult(Response(archive)));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.DownloadAndStageAsync(release));

        Assert.Equal(ReleaseUpdateIssue.InvalidPayload, exception.Issue);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.UpdateRoot, "downloads")));
    }

    [Fact]
    public async Task A_valid_GitHub_archive_digest_does_not_override_a_conflicting_published_sidecar()
    {
        using var temporary = new UpdateTestDirectory();
        var archive = Archive(CompletePayload());
        var sidecar = Encoding.UTF8.GetBytes($"{new string('0', 64)}  {AssetName}\n");
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson(
            (AssetName, archive.Length, "sha256:" + Hash(archive)),
            (AssetName + ".sha256", sidecar.Length, null)), new Version(1, 0, 0));
        using var handler = new UpdateHandler((request, _) => Task.FromResult(Response(
            request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal) ? sidecar : archive)));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.DownloadAndStageAsync(release));

        Assert.Equal(ReleaseUpdateIssue.InvalidChecksum, exception.Issue);
        Assert.Equal(2, handler.RequestCount);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.UpdateRoot, "downloads")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("modified")]
    public async Task Complete_manifest_requires_every_listed_file_and_rejects_unlisted_or_changed_files(string change)
    {
        using var temporary = new UpdateTestDirectory();
        var payload = CompletePayload();
        if (change == "missing") payload.Remove("EmbyClient.App.pri");
        else if (change == "extra") payload.Add("unlisted.txt", [1]);
        else payload["EmbyClient.App.pri"] = [9, 9];
        var archive = Archive(payload);
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson((AssetName, archive.Length, null)), new Version(1, 0, 0));
        using var handler = new UpdateHandler((_, _) => Task.FromResult(Response(archive)));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.DownloadAndStageAsync(release));

        Assert.Equal(ReleaseUpdateIssue.InvalidPayload, exception.Issue);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.UpdateRoot, "downloads")));
    }

    [Fact]
    public async Task Download_limit_is_enforced_when_content_length_is_absent()
    {
        using var temporary = new UpdateTestDirectory();
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson((AssetName, 1, null)), new Version(1, 0, 0));
        using var handler = new UpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NonSeekableStream([1, 2]))
        }));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.DownloadAndStageAsync(release));

        Assert.Equal(ReleaseUpdateIssue.InvalidArchive, exception.Issue);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.UpdateRoot, "downloads")));
    }

    [Fact]
    public async Task Cancellation_interrupts_body_reads_and_discards_the_partial_stage()
    {
        using var temporary = new UpdateTestDirectory();
        using var cancellation = new CancellationTokenSource();
        using var input = new WaitingStream();
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson((AssetName, 100, null)), new Version(1, 0, 0));
        using var handler = new UpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(input)
        }));
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var download = service.DownloadAndStageAsync(release, cancellationToken: cancellation.Token);
        await input.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);

        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(temporary.UpdateRoot, "downloads")));
        Assert.Equal("Previous application remains intact.", File.ReadAllText(temporary.PreviousMarker));
    }

    [Fact]
    public async Task Redirects_must_stay_on_https_GitHub_release_hosts()
    {
        using var temporary = new UpdateTestDirectory();
        var release = ReleaseUpdateRules.ParseLatestRelease(ReleaseJson((AssetName, 100, null)), new Version(1, 0, 0));
        using var handler = new UpdateHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://example.invalid/other.zip");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var service = new ReleaseUpdateService(http, temporary.UpdateRoot, temporary.ApplicationRoot);

        var exception = await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.DownloadAndStageAsync(release));

        Assert.Equal(ReleaseUpdateIssue.InvalidRelease, exception.Issue);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Account_authorization_is_rejected_before_any_update_request_is_sent()
    {
        using var handler = new UpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "do-not-send");
        using var service = new ReleaseUpdateService(http);

        await Assert.ThrowsAsync<ReleaseUpdateException>(() => service.CheckLatestAsync(new Version(1, 0, 0)));

        Assert.Equal(0, handler.RequestCount);
    }

    private static byte[] ReleaseJson(params (string Name, long Size, string? Digest)[] assets) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("tag_name", Tag);
        writer.WriteString("html_url", $"https://github.com/{ReleaseUpdateRules.Repository}/releases/tag/{Tag}");
        writer.WriteBoolean("draft", false);
        writer.WriteBoolean("prerelease", false);
        writer.WriteStartArray("assets");
        foreach (var asset in assets)
        {
            writer.WriteStartObject();
            writer.WriteString("name", asset.Name);
            writer.WriteString("state", "uploaded");
            writer.WriteNumber("size", asset.Size);
            writer.WriteString("browser_download_url",
                $"https://github.com/{ReleaseUpdateRules.Repository}/releases/download/{Tag}/{Uri.EscapeDataString(asset.Name)}");
            if (asset.Digest is not null) writer.WriteString("digest", asset.Digest);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    private static Dictionary<string, byte[]> CompletePayload(byte[]? executable = null)
    {
        var files = ReleaseUpdateRules.RequiredPayloadFiles.ToDictionary(name => name, _ => new byte[] { 1, 2 }, StringComparer.Ordinal);
        files[ReleaseUpdateRules.ApplicationFileName] = executable ?? NativePe(executable: true);
        foreach (var name in new[] { "Microsoft.ui.xaml.dll", "Microsoft.WindowsAppRuntime.dll", "FFmpegInteropX.dll" })
            files[name] = NativePe(executable: false);
        foreach (var family in ReleaseUpdateRules.FfmpegFamilies) files[family + "-1.dll"] = NativePe(executable: false);
        files["Assets/Lumen/Icons/w/play.svg"] = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" />");
        files["emby-client-win-x64.cdx.json"] = Encoding.UTF8.GetBytes(
            "{\"bomFormat\":\"CycloneDX\",\"metadata\":{\"component\":{\"version\":\"1.2.3\"}}}");
        files[ReleaseUpdateRules.ManifestFileName] = Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("architecture", "x64");
            writer.WriteString("version", "1.2.3");
            writer.WriteString("executable", ReleaseUpdateRules.ApplicationFileName);
            writer.WriteStartArray("files");
            foreach (var file in files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Key);
                writer.WriteNumber("length", file.Value.Length);
                writer.WriteString("sha256", Hash(file.Value));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        });
        return files;
    }

    private static byte[] NativePe(bool executable)
    {
        var bytes = new byte[1024];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x5a4d);
        stream.Position = 0x3c;
        writer.Write(128);
        stream.Position = 128;
        writer.Write(0x00004550);
        writer.Write((ushort)0x8664);
        stream.Position = 148;
        writer.Write((ushort)240);
        writer.Write((ushort)(executable ? 0x0002 : 0x2002));
        writer.Write((ushort)0x20b);
        stream.Position = 128 + 24 + 108;
        writer.Write(16);
        return bytes;
    }

    private static byte[] Archive(IReadOnlyDictionary<string, byte[]> files)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files) WriteEntry(archive, file.Key, file.Value);
        return stream.ToArray();
    }

    private static ZipArchiveEntry WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name);
        using var content = entry.Open();
        content.Write(bytes);
        return entry;
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) write(writer);
        return stream.ToArray();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static HttpResponseMessage Response(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private sealed class InlineProgress(Action<ReleaseUpdateProgress> report) : IProgress<ReleaseUpdateProgress>
    {
        public void Report(ReleaseUpdateProgress value) => report(value);
    }

    private sealed class UpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestCount++;
            return send(request, token);
        }
    }

    private sealed class UpdateTestDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "emby-release-tests-" + Guid.NewGuid().ToString("N"));
        public UpdateTestDirectory()
        {
            Directory.CreateDirectory(ApplicationRoot);
            File.WriteAllText(PreviousMarker, "Previous application remains intact.");
        }
        public string UpdateRoot => Path.Combine(_root, "updates");
        public string ApplicationRoot => Path.Combine(_root, "previous-app");
        public string PreviousMarker => Path.Combine(ApplicationRoot, "previous.txt");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _input = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _input.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
    }

    private sealed class WaitingStream() : NonSeekableStream([])
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));
        private async Task<int> WaitForCancellationAsync(CancellationToken token)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
    }
}
