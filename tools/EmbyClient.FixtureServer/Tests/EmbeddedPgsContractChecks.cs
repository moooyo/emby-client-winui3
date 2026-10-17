using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class EmbeddedPgsContractChecks
{
    public static async Task RunAsync(string serverPath, string testRoot, string inputDirectory, Action<bool, string> check,
        bool allowLongEmbeddedPgs = false)
    {
        var pgsRoot = Path.Combine(testRoot, "embedded-pgs");
        Directory.CreateDirectory(pgsRoot);
        foreach (var file in new[] { "fixture-h264-aac-pgs.mkv", "fixture-h264-aac.json", "fixture-h264-aac.ffprobe.json" })
            File.Copy(Path.Combine(inputDirectory, file), Path.Combine(pgsRoot, file));
        var mediaPath = Path.Combine(pgsRoot, "fixture-h264-aac-pgs.mkv");
        var metadataPath = Path.Combine(pgsRoot, "fixture-h264-aac.json");
        var probePath = Path.Combine(pgsRoot, "fixture-h264-aac.ffprobe.json");
        var bytes = await File.ReadAllBytesAsync(mediaPath);
        var metadataText = await File.ReadAllTextAsync(metadataPath);
        var probeBytes = await File.ReadAllBytesAsync(probePath);
        using var metadataDocument = JsonDocument.Parse(metadataText);
        using var probeDocument = JsonDocument.Parse(probeBytes);
        var metadata = metadataDocument.RootElement;
        var observedStreams = probeDocument.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = observedStreams.Single(stream => stream.GetProperty("codec_type").GetString() == "video");
        var audio = observedStreams.Single(stream => stream.GetProperty("codec_type").GetString() == "audio");
        var subtitle = observedStreams.Single(stream => stream.GetProperty("codec_type").GetString() == "subtitle");
        var subtitleIndex = subtitle.GetProperty("index").GetInt32();
        var durationTicks = metadata.GetProperty("DurationTicks").GetInt64();
        var mediaHash = Convert.ToHexString(SHA256.HashData(bytes));
        var probeHash = Convert.ToHexString(SHA256.HashData(probeBytes));
        var pgsArguments = allowLongEmbeddedPgs
            ? new[] { "--embedded-pgs", "--allow-long-embedded-pgs" } : ["--embedded-pgs"];

        await using (var disabled = await TestServer.StartAsync(serverPath, testRoot))
        {
            var item = await JsonAsync(disabled, "/emby/Users/synthetic-user-demo/Items/1001");
            var source = item.GetProperty("MediaSources")[0];
            var stats = await disabled.StatsAsync();
            check(source.GetProperty("Container").GetString() == "mp4" && source.GetProperty("MediaStreams").GetArrayLength() == 2
                && source.GetProperty("DefaultSubtitleStreamIndex").GetInt32() == -1
                && !stats.GetProperty("EmbeddedPgsConfigured").GetBoolean()
                && !stats.GetProperty("LongEmbeddedPgsConfigured").GetBoolean(),
                "Embedded PGS is disabled by default and preserves the original MP4 source and two streams.");
        }
        await using (var server = await TestServer.StartAsync(serverPath, pgsRoot, ["--lumen-design-catalog", .. pgsArguments]))
        {
            var item = await JsonAsync(server, "/emby/Users/synthetic-user-demo/Items/1001");
            var sources = item.GetProperty("MediaSources").EnumerateArray().ToArray();
            check(sources.Length == 2 && sources.All(source => source.GetProperty("Container").GetString() == "mkv"
                && source.GetProperty("Size").GetInt64() == bytes.Length && source.GetProperty("RunTimeTicks").GetInt64() == durationTicks
                && source.GetProperty("MediaStreams").GetArrayLength() == 3),
                "Both existing opaque aliases advertise the actual measured MKV size, duration, and three streams.");
            check(item.GetProperty("RunTimeTicks").GetInt64() == durationTicks,
                "The catalog item runtime preserves the measured media timeline without a sixty-second clamp.");
            var catalog = await JsonAsync(server, "/emby/Users/synthetic-user-demo/Items?Recursive=true&IncludeItemTypes=Movie,Episode&Limit=200");
            var playable = catalog.GetProperty("Items").EnumerateArray().ToArray();
            check(playable.Length > 1 && playable.All(entry => entry.GetProperty("RunTimeTicks").GetInt64() == durationTicks),
                "All returned playable movie and episode catalog entries expose the same actual runtime.");
            var source = sources[0];
            var streams = source.GetProperty("MediaStreams").EnumerateArray().ToArray();
            check(streams[0].GetProperty("Index").GetInt32() == video.GetProperty("index").GetInt32()
                && streams[0].GetProperty("Codec").GetString() == video.GetProperty("codec_name").GetString()
                && streams[0].GetProperty("Width").GetInt32() == video.GetProperty("width").GetInt32()
                && streams[0].GetProperty("Height").GetInt32() == video.GetProperty("height").GetInt32()
                && streams[1].GetProperty("Index").GetInt32() == audio.GetProperty("index").GetInt32()
                && streams[1].GetProperty("Codec").GetString() == audio.GetProperty("codec_name").GetString()
                && streams[1].GetProperty("Channels").GetInt32() == audio.GetProperty("channels").GetInt32()
                && streams[1].GetProperty("SampleRate").GetInt32() == int.Parse(audio.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture),
                "Video and audio indices and properties match independent observed FFprobe stream metadata.");
            var pgs = streams[2];
            var observedDefault = subtitle.GetProperty("disposition").GetProperty("default").GetInt32() == 1;
            check(pgs.GetProperty("Index").GetInt32() == subtitleIndex && pgs.GetProperty("Type").GetString() == "Subtitle"
                && subtitle.GetProperty("codec_name").GetString() == "hdmv_pgs_subtitle" && pgs.GetProperty("Codec").GetString() == "pgssub"
                && !pgs.GetProperty("IsExternal").GetBoolean() && !pgs.GetProperty("IsTextSubtitleStream").GetBoolean()
                && pgs.GetProperty("DeliveryMethod").GetString() == "Embed" && !pgs.TryGetProperty("DeliveryUrl", out _),
                "The actual global PGS index is exposed as an original embedded bitmap track without a delivery URL.");
            check(pgs.GetProperty("Language").GetString() == subtitle.GetProperty("tags").GetProperty("language").GetString()
                && pgs.GetProperty("Title").GetString() == subtitle.GetProperty("tags").GetProperty("title").GetString()
                && pgs.GetProperty("IsDefault").GetBoolean() == observedDefault
                && pgs.GetProperty("IsForced").GetBoolean() == (subtitle.GetProperty("disposition").GetProperty("forced").GetInt32() == 1)
                && source.GetProperty("DefaultSubtitleStreamIndex").GetInt32() == (observedDefault ? subtitleIndex : -1)
                && source.GetProperty("DefaultAudioStreamIndex").GetInt32() == audio.GetProperty("index").GetInt32(),
                "Observed language and disposition are preserved; a non-default PGS requires explicit UI selection.");
            using var negotiation = await server.PostAsync("/emby/Items/1001/PlaybackInfo", $$"""
                {"UserId":"synthetic-user-demo","IsPlayback":true,"SubtitleStreamIndex":{{subtitleIndex}}}
                """);
            negotiation.EnsureSuccessStatusCode();
            using var responseDocument = JsonDocument.Parse(await negotiation.Content.ReadAsStringAsync());
            var negotiated = responseDocument.RootElement.GetProperty("MediaSources")[0];
            var session = responseDocument.RootElement.GetProperty("PlaySessionId").GetString()!;
            var url = negotiated.GetProperty("DirectStreamUrl").GetString()!;
            check(negotiated.GetProperty("Container").GetString() == "mkv" && negotiated.GetProperty("SupportsDirectStream").GetBoolean()
                && !negotiated.GetProperty("SupportsTranscoding").GetBoolean() && !negotiated.GetProperty("RequiresOpening").GetBoolean()
                && !negotiated.GetProperty("IsInfiniteStream").GetBoolean() && url.Contains("Static=true", StringComparison.Ordinal)
                && url.Contains("PlaySessionId=" + session, StringComparison.Ordinal)
                && negotiated.GetProperty("MediaStreams")[2].GetProperty("Index").GetInt32() == subtitleIndex,
                "PGS PlaybackInfo supplies the original static complete-source route without claiming remux or transcoding.");
            check(responseDocument.RootElement.GetProperty("MediaSources").EnumerateArray()
                .All(entry => entry.GetProperty("RunTimeTicks").GetInt64() == durationTicks),
                "Every PlaybackInfo alias retains the actual measured duration, including the long-PGS timeline.");
            using var full = await server.GetAsync(url);
            check(full.StatusCode == HttpStatusCode.OK && full.Content.Headers.ContentType?.MediaType == "video/x-matroska"
                && full.Content.Headers.ContentLength == bytes.Length
                && (await full.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "The negotiated media route serves the actual MKV bytes and truthful Matroska content type.");
            using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, url);
            rangeRequest.Headers.Range = new RangeHeaderValue(0, 63);
            using var range = await server.Client.SendAsync(rangeRequest);
            check(range.StatusCode == HttpStatusCode.PartialContent && range.Content.Headers.ContentRange?.From == 0
                && range.Content.Headers.ContentRange.To == 63 && range.Content.Headers.ContentRange.Length == bytes.Length
                && (await range.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes[..64]),
                "Native opening Range requests preserve the original MKV prefix, boundaries, and total length.");
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var head = await server.Client.SendAsync(headRequest);
            check(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == bytes.Length
                && (await head.Content.ReadAsByteArrayAsync()).Length == 0,
                "MKV HEAD returns original media length without decoding or delivering a body.");
            using var anonymous = new HttpClient { BaseAddress = server.Client.BaseAddress };
            using var unauthorized = await anonymous.GetAsync(url);
            using var forbidden = await server.GetAsync(url + "&UserId=another-user");
            using var unknownSource = await server.GetAsync($"/emby/Videos/1001/stream?Static=true&PlaySessionId={session}&MediaSourceId=unknown");
            using var unknownSession = await server.GetAsync("/emby/Videos/1001/stream?Static=true&PlaySessionId=unknown");
            check(unauthorized.StatusCode == HttpStatusCode.Unauthorized && forbidden.StatusCode == HttpStatusCode.Forbidden
                && unknownSource.StatusCode == HttpStatusCode.NotFound && unknownSession.StatusCode == HttpStatusCode.NotFound,
                "PGS media retains the existing token, requested-user, source, and playback-session ownership gates.");
            using var disabledSubtitle = await server.PostAsync("/emby/Items/1001/PlaybackInfo", """
                {"UserId":"synthetic-user-demo","IsPlayback":true,"SubtitleStreamIndex":-1}
                """);
            disabledSubtitle.EnsureSuccessStatusCode();
            using var offDocument = JsonDocument.Parse(await disabledSubtitle.Content.ReadAsStringAsync());
            check(offDocument.RootElement.GetProperty("MediaSources")[0].GetProperty("MediaStreams")[2].GetProperty("Index").GetInt32() == subtitleIndex,
                "Subtitle-off negotiation retains honest metadata for the same original embedded MKV.");
            using var missingVtt = await server.GetAsync("/emby/Videos/1001/synthetic-mp4/Subtitles/2/Stream.vtt");
            check(missingVtt.StatusCode == HttpStatusCode.NotFound, "Embedded PGS does not fabricate an external WebVTT endpoint.");
            var stats = await server.StatsAsync();
            check(stats.GetProperty("EmbeddedPgsConfigured").GetBoolean() && stats.GetProperty("MediaContainer").GetString() == "mkv"
                && stats.GetProperty("EmbeddedPgsMediaSha256").GetString()!.Equals(mediaHash, StringComparison.OrdinalIgnoreCase)
                && stats.GetProperty("EmbeddedPgsProbeSha256").GetString()!.Equals(probeHash, StringComparison.OrdinalIgnoreCase)
                && stats.GetProperty("EmbeddedPgsNativeSubtitleIndex").GetInt32() == subtitleIndex
                && stats.GetProperty("LongEmbeddedPgsConfigured").GetBoolean() == allowLongEmbeddedPgs
                && stats.GetProperty("MediaDurationTicks").GetInt64() == durationTicks
                && stats.GetProperty("MediaLength").GetInt64() == bytes.Length,
                "Credential-free fixture observations identify the actual MKV/probe hashes, size, and native subtitle index.");
            check(!stats.GetRawText().Contains(server.Token, StringComparison.Ordinal)
                && !stats.GetRawText().Contains(pgsRoot, StringComparison.Ordinal)
                && !stats.GetRawText().Contains(inputDirectory, StringComparison.Ordinal),
                "PGS observations contain no access token or private source path.");
        }
        if (allowLongEmbeddedPgs)
            await LongEmbeddedPgsContractChecks.RunAsync(serverPath, testRoot, pgsRoot, check);
        await InvalidConfiguration(serverPath, testRoot, pgsRoot, metadataPath, probePath, metadataText, probeBytes, pgsArguments, check);
    }

    private static async Task InvalidConfiguration(string serverPath, string testRoot, string pgsRoot, string metadataPath,
        string probePath, string metadataText, byte[] probeBytes, string[] pgsArguments, Action<bool, string> check)
    {
        async Task Reject(string directory, string description, params string[] controls)
        {
            using var process = TestServer.CreateProcess(serverPath, directory, TestServer.FindPort(), controls);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            await Task.WhenAll(output, error);
            check(process.ExitCode != 0 && !output.Result.Contains("listening at", StringComparison.Ordinal), description);
        }
        await Reject(testRoot, "PGS opt-in rejects the ordinary MP4-only directory before binding.", "--embedded-pgs");
        await Reject(pgsRoot, "Duplicate PGS flags are rejected before binding.", "--embedded-pgs", "--embedded-pgs");
        await Reject(pgsRoot, "The long-PGS flag alone cannot enable embedded media or widen the MP4 mode.", "--allow-long-embedded-pgs");
        await Reject(pgsRoot, "Duplicate long-PGS flags are rejected before binding.",
            "--embedded-pgs", "--allow-long-embedded-pgs", "--allow-long-embedded-pgs");
        if (pgsArguments.Length == 1)
            await Reject(pgsRoot, "The explicit long-PGS window rejects the ordinary measured sixty-second MKV.",
                "--embedded-pgs", "--allow-long-embedded-pgs");
        var vttPath = Path.Combine(testRoot, "mutually-exclusive.vtt");
        await File.WriteAllTextAsync(vttPath, "WEBVTT\n\n00:00:02.000 --> 00:00:08.000\nTest only.\n");
        await Reject(pgsRoot, "PGS and fixed-index external WebVTT inputs are rejected together.", "--embedded-pgs", "--subtitle-file", vttPath);
        var changes = new (string Description, Action<JsonNode> Change)[]
        {
            ("incorrect native subtitle index", node => node["EmbeddedSubtitle"]!["Index"] = 99),
            ("incorrect native video index", node => node["VideoStreamIndex"] = 99),
            ("incorrect native audio index", node => node["AudioStreamIndex"] = 99),
            ("incorrect container", node => node["Container"] = "mp4"),
            ("external bitmap claim", node => node["EmbeddedSubtitle"]!["IsExternal"] = true),
            ("text subtitle claim", node => node["EmbeddedSubtitle"]!["IsTextSubtitleStream"] = true),
            ("external delivery claim", node => node["EmbeddedSubtitle"]!["DeliveryMethod"] = "External"),
            ("incorrect media hash", node => node["FileSha256"] = new string('0', 64)),
            ("incorrect probe hash", node => node["ProbeSha256"] = new string('0', 64)),
            ("incorrect subtitle disposition", node => node["EmbeddedSubtitle"]!["IsDefault"] = !node["EmbeddedSubtitle"]!["IsDefault"]!.GetValue<bool>()),
            ("incorrect observed language", node => node["EmbeddedSubtitle"]!["Language"] = "zho")
        };
        foreach (var (description, change) in changes)
        {
            var altered = JsonNode.Parse(metadataText)!;
            change(altered);
            await File.WriteAllTextAsync(metadataPath, altered.ToJsonString());
            await Reject(pgsRoot, "PGS input rejects " + description + " before binding.", pgsArguments);
        }
        await File.WriteAllTextAsync(metadataPath, metadataText);
        await File.WriteAllBytesAsync(probePath, [.. probeBytes, (byte)' ']);
        await Reject(pgsRoot, "Changed FFprobe bytes are rejected by their recorded SHA-256 before binding.", pgsArguments);
        await using (var oversized = File.Create(probePath)) oversized.SetLength(64 * 1024 + 1);
        await Reject(pgsRoot, "Oversized FFprobe evidence is rejected before binding.", pgsArguments);
        await File.WriteAllBytesAsync(probePath, probeBytes);
        var mediaPath = Path.Combine(pgsRoot, "fixture-h264-aac-pgs.mkv");
        await using (var media = new FileStream(mediaPath, FileMode.Open, FileAccess.ReadWrite))
        {
            media.Position = 128;
            var original = media.ReadByte();
            media.Position = 128;
            media.WriteByte((byte)(original ^ 1));
        }
        await Reject(pgsRoot, "Changed MKV bytes are rejected by their recorded SHA-256 before binding.", pgsArguments);
    }

    private static async Task<JsonElement> JsonAsync(TestServer server, string path)
    {
        using var response = await server.GetAsync(path);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}
