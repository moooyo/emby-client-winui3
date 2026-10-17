using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

internal static class LongEmbeddedPgsContractChecks
{
    public static async Task RunAsync(string serverPath, string testRoot, string inputDirectory, Action<bool, string> check)
    {
        var directory = Path.Combine(testRoot, "long-pgs-invalid");
        Directory.CreateDirectory(directory);
        var mediaPath = Path.Combine(directory, "fixture-h264-aac-pgs.mkv");
        var metadataPath = Path.Combine(directory, "fixture-h264-aac.json");
        var probePath = Path.Combine(directory, "fixture-h264-aac.ffprobe.json");
        var originalMedia = await File.ReadAllBytesAsync(Path.Combine(inputDirectory, "fixture-h264-aac-pgs.mkv"));
        var originalMetadata = await File.ReadAllTextAsync(Path.Combine(inputDirectory, "fixture-h264-aac.json"));
        var originalProbe = await File.ReadAllBytesAsync(Path.Combine(inputDirectory, "fixture-h264-aac.ffprobe.json"));
        var longArguments = new[] { "--embedded-pgs", "--allow-long-embedded-pgs" };

        async Task RestoreAsync()
        {
            await File.WriteAllBytesAsync(mediaPath, originalMedia);
            await File.WriteAllTextAsync(metadataPath, originalMetadata);
            await File.WriteAllBytesAsync(probePath, originalProbe);
        }

        async Task RejectAsync(string description, params string[] arguments)
        {
            using var process = TestServer.CreateProcess(serverPath, directory, TestServer.FindPort(), arguments);
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            await Task.WhenAll(output, error);
            check(process.ExitCode != 0 && !output.Result.Contains("listening at", StringComparison.Ordinal), description);
        }

        async Task WriteProbeAsync(JsonNode metadata, byte[] probe)
        {
            metadata["ProbeSha256"] = Convert.ToHexString(SHA256.HashData(probe));
            await File.WriteAllBytesAsync(probePath, probe);
            await File.WriteAllTextAsync(metadataPath, metadata.ToJsonString());
        }

        await RestoreAsync();
        await RejectAsync("A measured hundred-second MKV is rejected by the unchanged default PGS duration window.", "--embedded-pgs");
        var actualDuration = JsonNode.Parse(originalMetadata)!["DurationTicks"]!.GetValue<long>();
        check(actualDuration is >= 990_000_000 and <= 1_010_000_000,
            "The long-mode positive input is independently measured inside the explicit 99-to-101-second window.");
        foreach (var duration in new[] { 0L, 989_999_999L, 1_010_000_001L })
        {
            var metadata = JsonNode.Parse(originalMetadata)!;
            var probe = JsonNode.Parse(originalProbe)!;
            metadata["DurationTicks"] = duration;
            probe["format"]!["duration"] = ((decimal)duration / TimeSpan.TicksPerSecond).ToString("0.0000000", CultureInfo.InvariantCulture);
            await WriteProbeAsync(metadata, Encoding.UTF8.GetBytes(probe.ToJsonString()));
            await RejectAsync("Long PGS rejects a self-consistent out-of-window duration of " + duration + " ticks.", longArguments);
        }
        {
            var metadata = JsonNode.Parse(originalMetadata)!;
            var probe = JsonNode.Parse(originalProbe)!;
            var forgedDuration = actualDuration == 990_000_000 ? 1_000_000_000 : 990_000_000;
            metadata["DurationTicks"] = forgedDuration;
            probe["format"]!["duration"] = ((decimal)forgedDuration / TimeSpan.TicksPerSecond).ToString("0.0000000", CultureInfo.InvariantCulture);
            await WriteProbeAsync(metadata, Encoding.UTF8.GetBytes(probe.ToJsonString()));
            await RejectAsync("A rehashed probe and matching metadata cannot forge a different permitted duration than the real Matroska Info.", longArguments);
        }
        var changes = new (string Description, Action<JsonNode> Change)[]
        {
            ("a video codec that differs from metadata", probe => probe["streams"]![0]!["codec_name"] = "hevc"),
            ("a video width that differs from metadata", probe => probe["streams"]![0]!["width"] = probe["streams"]![0]!["width"]!.GetValue<int>() + 1),
            ("an audio codec that differs from metadata", probe => probe["streams"]![1]!["codec_name"] = "ac3"),
            ("a subtitle codec that is not PGS", probe => probe["streams"]![2]!["codec_name"] = "ass"),
            ("a subtitle index that differs from metadata", probe => probe["streams"]![2]!["index"] = 3),
            ("a size that differs from the actual file", probe => probe["format"]!["size"] = (originalMedia.LongLength + 1).ToString(CultureInfo.InvariantCulture)),
            ("a nonnumeric duration", probe => probe["format"]!["duration"] = "NaN"),
            ("a missing stream list", probe => probe.AsObject().Remove("streams"))
        };
        foreach (var (description, change) in changes)
        {
            var metadata = JsonNode.Parse(originalMetadata)!;
            var probe = JsonNode.Parse(originalProbe)!;
            change(probe);
            await WriteProbeAsync(metadata, Encoding.UTF8.GetBytes(probe.ToJsonString()));
            await RejectAsync("Long PGS rejects a rehashed probe with " + description + ".", longArguments);
        }
        await WriteProbeAsync(JsonNode.Parse(originalMetadata)!, "{not valid JSON"u8.ToArray());
        await RejectAsync("Long PGS rejects malformed JSON even when its recorded probe hash is recomputed.", longArguments);

        await RestoreAsync();
        var oversizedDimensions = JsonNode.Parse(originalMetadata)!;
        var matchingProbe = JsonNode.Parse(originalProbe)!;
        oversizedDimensions["Width"] = 3840;
        matchingProbe["streams"]![0]!["width"] = 3840;
        await WriteProbeAsync(oversizedDimensions, Encoding.UTF8.GetBytes(matchingProbe.ToJsonString()));
        await RejectAsync("The explicit long mode rejects self-consistent dimensions above its 1920-by-1080 bound.", longArguments);

        await RestoreAsync();
        var corruptedContainer = (byte[])originalMedia.Clone();
        Array.Clear(corruptedContainer, 4, Math.Min(64, corruptedContainer.Length - 4));
        var corruptedMetadata = JsonNode.Parse(originalMetadata)!;
        corruptedMetadata["FileSha256"] = Convert.ToHexString(SHA256.HashData(corruptedContainer));
        await File.WriteAllBytesAsync(mediaPath, corruptedContainer);
        await File.WriteAllTextAsync(metadataPath, corruptedMetadata.ToJsonString());
        await RejectAsync("Long PGS rejects malformed Matroska Info even with a matching replacement media hash and retained probe.", longArguments);

        await RestoreAsync();
        await using (var oversized = new FileStream(mediaPath, FileMode.Open, FileAccess.Write))
            oversized.SetLength(64 * 1024 * 1024 + 1);
        var oversizedMetadata = JsonNode.Parse(originalMetadata)!;
        oversizedMetadata["FileLength"] = 64 * 1024 * 1024 + 1;
        await File.WriteAllTextAsync(metadataPath, oversizedMetadata.ToJsonString());
        await RejectAsync("The explicit long mode retains the existing 64 MiB media bound.", longArguments);
    }
}
