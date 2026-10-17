using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbyClient.FixtureServer;

internal static class FixtureEmbeddedPgs
{
    public const int MaximumProbeBytes = 64 * 1024;
    public const int MaximumMediaBytes = 64 * 1024 * 1024;
    public const long MinimumLongDurationTicks = 990_000_000;
    public const long MaximumLongDurationTicks = 1_010_000_000;

    public static void Validate(string mediaDirectory, MediaFixtureMetadata metadata, Stream media, bool allowLongEmbeddedPgs = false)
    {
        var durationMatches = allowLongEmbeddedPgs
            ? metadata.DurationTicks is >= MinimumLongDurationTicks and <= MaximumLongDurationTicks
            : metadata.DurationTicks is >= 590_000_000 and <= 610_000_000;
        if (metadata.Container != "mkv" || !durationMatches
            || media.Length > MaximumMediaBytes || !IsSha256(metadata.FileSha256) || !IsSha256(metadata.ProbeSha256))
            throw InvalidInput();
        media.Position = 0;
        if (!Convert.ToHexString(SHA256.HashData(media)).Equals(metadata.FileSha256, StringComparison.OrdinalIgnoreCase))
            throw InvalidInput();
        if (allowLongEmbeddedPgs && (metadata.Width > 1920 || metadata.Height > 1080
            || FixtureMatroskaInfo.ReadDurationTicks(media) != metadata.DurationTicks))
            throw InvalidInput();
        using var probe = File.OpenRead(Path.Combine(mediaDirectory, "fixture-h264-aac.ffprobe.json"));
        if (probe.Length is <= 0 or > MaximumProbeBytes) throw InvalidInput();
        var bytes = new byte[checked((int)probe.Length)];
        probe.ReadExactly(bytes);
        if (probe.ReadByte() != -1
            || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(metadata.ProbeSha256, StringComparison.OrdinalIgnoreCase))
            throw InvalidInput();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streamList) || streamList.ValueKind != JsonValueKind.Array
                || streamList.GetArrayLength() != 3 || !root.TryGetProperty("format", out var format)
                || String(format, "format_name") != "matroska,webm"
                || !decimal.TryParse(String(format, "duration"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var duration)
                || (long)decimal.Round(duration * TimeSpan.TicksPerSecond, 0, MidpointRounding.AwayFromZero) != metadata.DurationTicks
                || !long.TryParse(String(format, "size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                || size != metadata.FileLength)
                throw InvalidInput();
            var streams = streamList.EnumerateArray().ToArray();
            var videos = streams.Where(stream => String(stream, "codec_type") == "video").ToArray();
            var audios = streams.Where(stream => String(stream, "codec_type") == "audio").ToArray();
            var subtitles = streams.Where(stream => String(stream, "codec_type") == "subtitle").ToArray();
            if (videos.Length != 1 || audios.Length != 1 || subtitles.Length != 1) throw InvalidInput();
            var video = videos[0];
            var audio = audios[0];
            var subtitle = subtitles[0];
            var videoIndex = Integer(video, "index");
            var audioIndex = Integer(audio, "index");
            var subtitleIndex = Integer(subtitle, "index");
            if (videoIndex < 0 || audioIndex < 0 || subtitleIndex < 0 || videoIndex == audioIndex
                || videoIndex == subtitleIndex || audioIndex == subtitleIndex
                || videoIndex != metadata.VideoStreamIndex || audioIndex != metadata.AudioStreamIndex
                || String(video, "codec_name") != "h264" || String(audio, "codec_name") != "aac"
                || String(subtitle, "codec_name") != "hdmv_pgs_subtitle"
                || Integer(video, "width") != metadata.Width || Integer(video, "height") != metadata.Height
                || Integer(audio, "channels") != metadata.AudioChannels
                || !int.TryParse(String(audio, "sample_rate"), NumberStyles.None, CultureInfo.InvariantCulture, out var sampleRate)
                || sampleRate != metadata.AudioSampleRate || !MatchesFrameRate(String(video, "avg_frame_rate"), metadata)
                || Integer(video.GetProperty("disposition"), "default") != 1 || Integer(audio.GetProperty("disposition"), "default") != 1)
                throw InvalidInput();
            var configured = metadata.EmbeddedSubtitle;
            var tags = subtitle.GetProperty("tags");
            var disposition = subtitle.GetProperty("disposition");
            if (configured is null || configured.Index != subtitleIndex || configured.Type != "Subtitle" || configured.Codec != "pgssub"
                || configured.IsExternal != false || configured.IsTextSubtitleStream != false || configured.DeliveryMethod != "Embed"
                || configured.DeliveryUrl is not null || configured.Language != String(tags, "language")
                || configured.Title != String(tags, "title") || configured.DisplayTitle != String(tags, "title")
                || configured.IsDefault != (Integer(disposition, "default") == 1)
                || configured.IsForced != (Integer(disposition, "forced") == 1))
                throw InvalidInput();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new ArgumentException("The explicit synthetic PGS probe evidence is invalid.", exception);
        }
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static int Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var result) ? result : throw InvalidInput();

    private static string? String(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool MatchesFrameRate(string? value, MediaFixtureMetadata metadata)
    {
        var parts = value?.Split('/');
        return parts is { Length: 2 }
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0
            && numerator == metadata.FrameRateNumerator && denominator == metadata.FrameRateDenominator;
    }

    private static ArgumentException InvalidInput() => new("The explicit measured synthetic PGS input does not match its bounded FFprobe evidence.");
}
