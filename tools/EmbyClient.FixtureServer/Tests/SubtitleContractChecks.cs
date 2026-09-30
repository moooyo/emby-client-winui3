using System.Net;
using System.Text;
using System.Text.Json;

internal static class SubtitleContractChecks
{
    public static async Task RunAsync(string serverPath, string testRoot, Action<bool, string> check)
    {
        var subtitlePath = Path.Combine(testRoot, "fixture-test.zh.vtt");
        var text = "WEBVTT\n\n00:00:00.000 --> 00:01:00.000\n\u4fe1\u53f7\u4ecd\u5728\u8fd9\u91cc\u3002\n";
        var bytes = new UTF8Encoding(false, true).GetBytes(text);
        await File.WriteAllBytesAsync(subtitlePath, bytes);
        const string route = "/emby/Videos/1001/synthetic-mp4/Subtitles/2/Stream.vtt";
        await using (var disabled = await TestServer.StartAsync(serverPath, testRoot))
        {
            using var missing = await disabled.GetAsync(route);
            var details = await JsonAsync(disabled, "/emby/Users/synthetic-user-demo/Items/1001");
            var defaultSource = details.GetProperty("MediaSources")[0];
            check(missing.StatusCode == HttpStatusCode.NotFound
                && defaultSource.GetProperty("DefaultSubtitleStreamIndex").GetInt32() == -1
                && defaultSource.GetProperty("MediaStreams").GetArrayLength() == 2,
                "External subtitles are disabled by default and do not change existing sources.");
        }
        await using (var server = await TestServer.StartAsync(serverPath, testRoot, "--lumen-design-catalog", "--subtitle-file", subtitlePath))
        {
            var details = await JsonAsync(server, "/emby/Users/synthetic-user-demo/Items/1001");
            var sources = details.GetProperty("MediaSources").EnumerateArray().ToArray();
            check(sources.Length == 2 && sources.All(source => source.GetProperty("DefaultSubtitleStreamIndex").GetInt32() == 2
                && source.GetProperty("MediaStreams").GetArrayLength() == 3),
                "Configured real external WebVTT is advertised consistently on both honest fixture aliases.");
            var stream = sources[0].GetProperty("MediaStreams")[2];
            check(stream.GetProperty("Index").GetInt32() == 2 && stream.GetProperty("Type").GetString() == "Subtitle"
                && stream.GetProperty("Codec").GetString() == "vtt" && stream.GetProperty("Language").GetString() == "zho"
                && stream.GetProperty("IsExternal").GetBoolean() && stream.GetProperty("IsTextSubtitleStream").GetBoolean()
                && stream.GetProperty("DeliveryMethod").GetString() == "External" && stream.GetProperty("DeliveryUrl").GetString() == route,
                "The stream uses real external-text metadata and a canonical same-origin Emby delivery URL.");
            using var response = await server.GetAsync(route);
            check(response.StatusCode == HttpStatusCode.OK && response.Content.Headers.ContentType?.MediaType == "text/vtt"
                && response.Content.Headers.ContentType.CharSet == "utf-8"
                && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "The authenticated endpoint returns the exact UTF-8 source cue bytes as WebVTT.");
            using var anonymous = new HttpClient { BaseAddress = server.Client.BaseAddress };
            using var unauthorized = await anonymous.GetAsync(route);
            using var forbidden = await server.GetAsync(route + "?UserId=another-user");
            check(unauthorized.StatusCode == HttpStatusCode.Unauthorized && forbidden.StatusCode == HttpStatusCode.Forbidden,
                "Subtitle delivery preserves the normal token and requested-user authorization gate.");
            using var alternate = await server.GetAsync("/emby/Videos/1001/synthetic-mp4-alternate/Subtitles/2/Stream.vtt");
            check(alternate.StatusCode == HttpStatusCode.OK && (await alternate.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "The real duplicate MP4 alias serves the same real external cue file.");
            foreach (var path in new[] { "/emby/Videos/missing/synthetic-mp4/Subtitles/2/Stream.vtt",
                "/emby/Videos/movies/synthetic-mp4/Subtitles/2/Stream.vtt", "/emby/Videos/1001/unknown/Subtitles/2/Stream.vtt",
                "/emby/Videos/1001/synthetic-mp4/Subtitles/3/Stream.vtt", route + "/extra" })
            {
                using var rejected = await server.GetAsync(path);
                check(rejected.StatusCode == HttpStatusCode.NotFound, "Subtitle routes reject unknown item/source/index or extra segments: " + path);
            }
            using var playback = await server.PostAsync("/emby/Items/1001/PlaybackInfo", """{"UserId":"synthetic-user-demo","IsPlayback":true,"SubtitleStreamIndex":2}""");
            playback.EnsureSuccessStatusCode();
            using var payload = JsonDocument.Parse(await playback.Content.ReadAsStringAsync());
            var negotiated = payload.RootElement.GetProperty("MediaSources")[0];
            check(negotiated.GetProperty("MediaStreams")[2].GetProperty("DeliveryUrl").GetString() == route
                && negotiated.GetProperty("MediaStreams")[0].GetProperty("Codec").GetString() == "h264"
                && negotiated.GetProperty("MediaStreams")[1].GetProperty("Codec").GetString() == "aac"
                && !negotiated.GetProperty("SupportsTranscoding").GetBoolean(),
                "Normal PlaybackInfo preserves actual H.264/AAC and external text without claiming burn-in or transcoding.");
            await File.WriteAllTextAsync(subtitlePath, "WEBVTT\n\n00:00:00.000 --> 00:01:00.000\nChanged after startup.\n");
            using var snapshot = await server.GetAsync(route);
            check((await snapshot.Content.ReadAsByteArrayAsync()).SequenceEqual(bytes),
                "Requests use the bounded startup snapshot, never request-derived file paths or later file mutations.");
            var stats = await server.StatsAsync();
            check(!stats.GetRawText().Contains(server.Token, StringComparison.Ordinal)
                && !stats.GetRawText().Contains(subtitlePath, StringComparison.Ordinal)
                && !stats.GetRawText().Contains(text, StringComparison.Ordinal),
                "Fixture observations do not expose subtitle text, filesystem paths, or authentication tokens.");
        }

        var invalidHeader = Path.Combine(testRoot, "invalid-header.vtt");
        var invalidUtf8 = Path.Combine(testRoot, "invalid-utf8.vtt");
        var noCue = Path.Combine(testRoot, "no-cue.vtt");
        var wrongExtension = Path.Combine(testRoot, "subtitle.txt");
        var oversized = Path.Combine(testRoot, "oversized.vtt");
        await File.WriteAllTextAsync(invalidHeader, "NOTVTT\n\n00:00:00.000 --> 00:00:10.000\nTest.\n");
        await File.WriteAllBytesAsync(invalidUtf8, [0xff, 0xfe, 0xff, 0xfe, 0xff, 0xfe, 0xff, 0xfe]);
        await File.WriteAllTextAsync(noCue, "WEBVTT\n\nNo timed cue.\n");
        await File.WriteAllTextAsync(wrongExtension, text);
        await using (var file = File.Create(oversized)) file.SetLength(4 * 1024 * 1024 + 1);
        string[][] invalidArguments =
        [
            ["--subtitle-file", "relative.vtt"], ["--subtitle-file", Path.Combine(testRoot, "missing.vtt")],
            ["--subtitle-file", invalidHeader], ["--subtitle-file", invalidUtf8], ["--subtitle-file", noCue],
            ["--subtitle-file", wrongExtension], ["--subtitle-file", oversized], ["--subtitle-file"],
            ["--subtitle-file", subtitlePath, "--subtitle-file", subtitlePath]
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
                "Invalid external subtitle input is rejected before binding: " + string.Join(' ', arguments));
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
