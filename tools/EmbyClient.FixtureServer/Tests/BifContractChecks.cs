using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using EmbyClient.Api;

internal static class BifContractChecks
{
    private const long DurationTicks = 60 * TimeSpan.TicksPerSecond;
    private const string BifRoute = "/emby/Videos/1001/index.bif?Width=240";
    private const string MetadataRoute = "/emby/Items/1001/ThumbnailSet?Width=240";

    public static async Task RunAsync(string serverPath, string testRoot, string inputPath, Action<bool, string> check)
    {
        using var input = File.OpenRead(inputPath);
        if (!Path.GetExtension(inputPath).Equals(".bif", StringComparison.OrdinalIgnoreCase)
            || input.Length is < 72 or > BifThumbnailReader.MaximumPackageBytes)
            throw new ArgumentException("The positive boundary scope requires an existing bounded real .bif input.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new ArgumentException("The positive BIF input changed during its bounded read.");
        var parsed = BifThumbnailReader.Read(bytes, DurationTicks);
        if (parsed.Thumbnails.Length == 0)
            throw new ArgumentException("The positive BIF input must contain a real JPEG within the sixty-second boundary fixture.");
        var expectedHash = Convert.ToHexString(SHA256.HashData(bytes));
        var bifPath = Path.Combine(testRoot, "external-real.bif");
        await File.WriteAllBytesAsync(bifPath, bytes);

        await using (var disabled = await TestServer.StartAsync(serverPath, testRoot))
        {
            using var bif = await disabled.GetAsync(BifRoute);
            using var metadata = await disabled.GetAsync(MetadataRoute);
            var stats = await disabled.StatsAsync();
            check(bif.StatusCode == HttpStatusCode.NotFound && metadata.StatusCode == HttpStatusCode.NotFound
                && !stats.GetProperty("ExternalBifConfigured").GetBoolean()
                && stats.GetProperty("BifArchivesServed").GetInt32() == 0
                && stats.GetProperty("ThumbnailSetsServed").GetInt32() == 0,
                "BIF and thumbnail metadata remain unavailable with zero serving counters by default.");
        }
        await using (var server = await TestServer.StartAsync(serverPath, testRoot, "--lumen-design-catalog", "--bif-file", bifPath))
        {
            var metadata = await JsonAsync(server, MetadataRoute);
            var samples = metadata.GetProperty("Thumbnails").EnumerateArray().ToArray();
            check(samples.Select(sample => sample.GetProperty("PositionTicks").GetInt64())
                    .SequenceEqual(parsed.Thumbnails.Select(sample => sample.PositionTicks))
                && samples.Length == parsed.Thumbnails.Length,
                "ThumbnailSet exposes only the production reader's actual selected BIF timestamps.");
            var first = parsed.Thumbnails[0];
            var consistentRatio = parsed.Thumbnails.All(sample =>
                (long)sample.PixelWidth * first.PixelHeight == (long)first.PixelWidth * sample.PixelHeight);
            check(consistentRatio
                    ? metadata.GetProperty("AspectRatio").GetDouble() == (double)first.PixelWidth / first.PixelHeight
                    : !metadata.TryGetProperty("AspectRatio", out _),
                "ThumbnailSet derives a consistent aspect ratio from parsed JPEG dimensions without inventing one.");
            check(metadata.EnumerateObject().All(property => property.Name is "AspectRatio" or "Thumbnails")
                && samples.All(sample => sample.EnumerateObject().All(property => property.Name is "PositionTicks" or "ImageTag"))
                && samples.Select(sample => sample.GetProperty("ImageTag").GetString())
                    .SequenceEqual(parsed.Thumbnails.Select(sample => Convert.ToHexString(SHA256.HashData(sample.JpegBytes)))),
                "Metadata uses the exact ThumbnailSet models and source JPEG tags, with no guessed image URL or index.");
            var before = await server.StatsAsync();
            using var response = await server.GetAsync(BifRoute);
            check(response.StatusCode == HttpStatusCode.OK
                && response.Content.Headers.ContentType?.MediaType == "application/octet-stream"
                && response.Content.Headers.ContentLength == bytes.Length
                && response.Headers.CacheControl?.Private == true && response.Headers.CacheControl.NoStore
                && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "The authenticated BIF endpoint returns the original archive snapshot and real byte length.");
            var after = await server.StatsAsync();
            check(after.GetProperty("BifArchivesServed").GetInt32() == before.GetProperty("BifArchivesServed").GetInt32() + 1
                && after.GetProperty("BifBytesServed").GetInt64() == before.GetProperty("BifBytesServed").GetInt64() + bytes.Length
                && after.GetProperty("ThumbnailSetsServed").GetInt32() == 1,
                "Only completed metadata and full-archive GET responses advance their separate counters.");
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, BifRoute);
            using var head = await server.Client.SendAsync(headRequest);
            check(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == bytes.Length
                && (await head.Content.ReadAsByteArrayAsync()).Length == 0
                && (await server.StatsAsync()).GetProperty("BifArchivesServed").GetInt32() == after.GetProperty("BifArchivesServed").GetInt32(),
                "HEAD preserves the BIF headers without serving or counting an archive body.");
            using var anonymous = new HttpClient { BaseAddress = server.Client.BaseAddress };
            using var unauthorizedBif = await anonymous.GetAsync(BifRoute);
            using var unauthorizedMetadata = await anonymous.GetAsync(MetadataRoute);
            using var forbiddenBif = await server.GetAsync(BifRoute + "&UserId=another-user");
            using var forbiddenMetadata = await server.GetAsync(MetadataRoute + "&UserId=another-user");
            check(unauthorizedBif.StatusCode == HttpStatusCode.Unauthorized && unauthorizedMetadata.StatusCode == HttpStatusCode.Unauthorized
                && forbiddenBif.StatusCode == HttpStatusCode.Forbidden && forbiddenMetadata.StatusCode == HttpStatusCode.Forbidden,
                "Both endpoints preserve the normal token and requested-user authorization gate.");
            foreach (var route in new[]
            {
                "/emby/Videos/missing/index.bif?Width=240", "/emby/Items/missing/ThumbnailSet?Width=240",
                "/emby/Videos/movies/index.bif?Width=240", "/emby/Items/movies/ThumbnailSet?Width=240",
                "/emby/Videos/2000/index.bif?Width=240", "/emby/Videos/1001/synthetic-mp4/index.bif?Width=240",
                "/emby/Videos/1001/index.bif/extra?Width=240", "/emby/Items/1001/ThumbnailSet/extra?Width=240"
            })
            {
                using var rejected = await server.GetAsync(route);
                check(rejected.StatusCode == HttpStatusCode.NotFound,
                    "Thumbnail routes reject unknown or nonplayable items and extra segments: " + route);
            }
            foreach (var query in new[] { "", "?Width=0", "?Width=-1", "?Width=x", "?Width=240&Width=320" })
            {
                using var rejectedBif = await server.GetAsync("/emby/Videos/1001/index.bif" + query);
                using var rejectedMetadata = await server.GetAsync("/emby/Items/1001/ThumbnailSet" + query);
                check(rejectedBif.StatusCode == HttpStatusCode.BadRequest && rejectedMetadata.StatusCode == HttpStatusCode.BadRequest,
                    "Both endpoints require one positive numeric Width: " + (query.Length == 0 ? "omitted" : query));
            }
            using var excessiveBifWidth = await server.GetAsync("/emby/Videos/1001/index.bif?Width=4097");
            using var positiveMetadataWidth = await server.GetAsync("/emby/Items/1001/ThumbnailSet?Width=4097");
            check(excessiveBifWidth.StatusCode == HttpStatusCode.BadRequest && positiveMetadataWidth.StatusCode == HttpStatusCode.OK,
                "BIF applies the client dimension bound while metadata preserves its positive-Width contract.");
            using var episode = await server.GetAsync("/emby/Videos/2101/index.bif?Width=320");
            check(episode.StatusCode == HttpStatusCode.OK && (await episode.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "Another real playable synthetic item returns the same explicit snapshot without resizing its JPEGs.");
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, BifRoute);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 99);
            using var range = await server.Client.SendAsync(rangeRequest);
            check(range.StatusCode == HttpStatusCode.OK && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "BIF GET returns one validated complete archive rather than manufacturing a partial package.");
            await File.WriteAllBytesAsync(bifPath, [1, 2, 3]);
            using var snapshot = await server.GetAsync(BifRoute);
            var snapshotMetadata = await JsonAsync(server, MetadataRoute);
            check((await snapshot.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes)
                && snapshotMetadata.GetRawText() == metadata.GetRawText(),
                "Later source mutations cannot change the bounded startup archive or its parsed metadata.");
            var stats = await server.StatsAsync();
            check(stats.GetProperty("ExternalBifConfigured").GetBoolean()
                && stats.GetProperty("ExternalBifSha256").GetString() == expectedHash
                && stats.GetProperty("ExternalBifBytes").GetInt32() == bytes.Length
                && stats.GetProperty("ExternalBifTotalImageCount").GetInt32() == parsed.TotalImageCount
                && stats.GetProperty("ExternalBifAvailableImageCount").GetInt32() == parsed.AvailableImageCount
                && stats.GetProperty("ExternalBifSelectedImageCount").GetInt32() == parsed.Thumbnails.Length,
                "Statistics separately preserve archive, duration-available, and selected image counts plus input SHA-256.");
            check(!stats.GetRawText().Contains(server.Token, StringComparison.Ordinal)
                && !stats.GetRawText().Contains(bifPath, StringComparison.Ordinal)
                && !stats.GetRawText().Contains(inputPath, StringComparison.Ordinal),
                "Statistics contain no access token or BIF source filesystem path.");
        }
        await File.WriteAllBytesAsync(bifPath, bytes);
        await CheckDurationBoundary(serverPath, testRoot, bifPath, bytes, parsed, check);
        await InvalidConfiguration(serverPath, testRoot, bifPath, bytes, parsed.TotalImageCount, check);
    }

    private static async Task CheckDurationBoundary(string serverPath, string testRoot, string bifPath, byte[] bytes,
        BifThumbnailSet parsed, Action<bool, string> check)
    {
        var lastPosition = parsed.Thumbnails[^1].PositionTicks;
        if (lastPosition <= parsed.Thumbnails[0].PositionTicks || lastPosition <= 1) return;
        var boundedDuration = lastPosition - 1;
        var expected = BifThumbnailReader.Read(bytes, boundedDuration);
        var durationRoot = Path.Combine(testRoot, "duration-boundary");
        Directory.CreateDirectory(durationRoot);
        File.Copy(Path.Combine(testRoot, "fixture-h264-aac.mp4"), Path.Combine(durationRoot, "fixture-h264-aac.mp4"));
        await File.WriteAllTextAsync(Path.Combine(durationRoot, "fixture-h264-aac.json"), $$"""
            {"Synthetic":true,"FileName":"fixture-h264-aac.mp4","FileLength":4096,
             "DurationTicks":{{boundedDuration}},"Width":1280,"Height":720,"VideoCodec":"H264",
             "AudioCodec":"AAC","AudioChannels":2,"AudioSampleRate":48000}
            """);
        await using var server = await TestServer.StartAsync(serverPath, durationRoot, "--bif-file", bifPath);
        var metadata = await JsonAsync(server, MetadataRoute);
        var stats = await server.StatsAsync();
        check(stats.GetProperty("ExternalBifTotalImageCount").GetInt32() == expected.TotalImageCount
            && stats.GetProperty("ExternalBifAvailableImageCount").GetInt32() == expected.AvailableImageCount
            && expected.AvailableImageCount < expected.TotalImageCount
            && metadata.GetProperty("Thumbnails").EnumerateArray().Select(sample => sample.GetProperty("PositionTicks").GetInt64())
                .SequenceEqual(expected.Thumbnails.Select(sample => sample.PositionTicks)),
            "A shorter item duration filters actual timestamps and available count while retaining the complete archive count.");
        using var response = await server.GetAsync(BifRoute);
        check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
            "Duration filtering changes metadata selection, never the original server-delivered archive bytes.");
    }

    private static async Task InvalidConfiguration(string serverPath, string testRoot, string bifPath, byte[] bytes,
        int imageCount, Action<bool, string> check)
    {
        var malformedPath = Path.Combine(testRoot, "malformed.bif");
        var truncatedPath = Path.Combine(testRoot, "truncated.bif");
        var brokenJpegPath = Path.Combine(testRoot, "broken-jpeg.bif");
        var emptyPath = Path.Combine(testRoot, "empty.bif");
        var afterDurationPath = Path.Combine(testRoot, "after-duration.bif");
        var wrongExtensionPath = Path.Combine(testRoot, "archive.txt");
        var oversizedPath = Path.Combine(testRoot, "oversized.bif");
        var malformed = (byte[])bytes.Clone();
        malformed[0] ^= 1;
        await File.WriteAllBytesAsync(malformedPath, malformed);
        await File.WriteAllBytesAsync(truncatedPath, bytes[..^1]);
        var brokenJpeg = (byte[])bytes.Clone();
        brokenJpeg[^1] ^= 1;
        await File.WriteAllBytesAsync(brokenJpegPath, brokenJpeg);
        var empty = new byte[72];
        bytes.AsSpan(0, 8).CopyTo(empty);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(64, 4), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(empty.AsSpan(68, 4), (uint)empty.Length);
        await File.WriteAllBytesAsync(emptyPath, empty);
        var afterDuration = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(afterDuration.AsSpan(16, 4), 1000);
        for (var index = 0; index < imageCount; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(afterDuration.AsSpan(64 + index * 8, 4), (uint)(61 + index));
        await File.WriteAllBytesAsync(afterDurationPath, afterDuration);
        await File.WriteAllBytesAsync(wrongExtensionPath, bytes);
        await using (var file = File.Create(oversizedPath)) file.SetLength(BifThumbnailReader.MaximumPackageBytes + 1L);
        string[][] invalidArguments =
        [
            ["--bif-file", "relative.bif"], ["--bif-file", "D:relative.bif"],
            ["--bif-file", Path.Combine(testRoot, "missing.bif")], ["--bif-file", malformedPath],
            ["--bif-file", truncatedPath], ["--bif-file", brokenJpegPath], ["--bif-file", emptyPath],
            ["--bif-file", afterDurationPath], ["--bif-file", wrongExtensionPath], ["--bif-file", oversizedPath],
            ["--bif-file"], ["--bif-file", bifPath, "--bif-file", bifPath]
        ];
        foreach (var arguments in invalidArguments)
        {
            using var process = TestServer.CreateProcess(serverPath, testRoot, TestServer.FindPort(), arguments);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            await Task.WhenAll(output, error);
            check(process.ExitCode != 0 && !output.Result.Contains("listening at", StringComparison.Ordinal),
                "Invalid BIF input is rejected before binding: " + string.Join(' ', arguments));
        }
    }

    private static async Task<JsonElement> JsonAsync(TestServer server, string path)
    {
        using var response = await server.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.Clone();
    }
}
