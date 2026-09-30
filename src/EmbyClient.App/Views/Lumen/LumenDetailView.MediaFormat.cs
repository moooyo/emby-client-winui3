using EmbyClient.Api;
using EmbyClient.App.Services;
using System.Globalization;

namespace EmbyClient.App.Views.Lumen;

public sealed partial class LumenDetailView
{
    private IEnumerable<string> TechnicalTags(BaseItemDto item)
    {
        var streams = item.Id == _selectionItemId ? SourceStreams
            : item.MediaSources?.FirstOrDefault()?.MediaStreams ?? item.MediaStreams ?? [];
        var video = streams.FirstOrDefault(stream => stream.Type == "Video");
        var audio = streams.FirstOrDefault(stream => stream.Type == "Audio" && stream.Index == _selectedAudioStreamIndex)
            ?? streams.FirstOrDefault(stream => stream.Type == "Audio" && stream.IsDefault == true)
            ?? streams.FirstOrDefault(stream => stream.Type == "Audio");
        var tags = new List<string>();
        if (video?.Height is >= 2160) tags.Add("4K");
        else if (video?.Height is > 0 and var height) tags.Add($"{height}p");
        var range = VideoRange(video);
        if (!string.IsNullOrWhiteSpace(range) && !string.Equals(range, "SDR", StringComparison.OrdinalIgnoreCase)) tags.Add(range);
        if (new[] { audio?.DisplayTitle, audio?.Title, audio?.Profile }.Any(value => value?.Contains("Atmos", StringComparison.OrdinalIgnoreCase) == true))
            tags.Add("Dolby Atmos");
        if (streams.Any(stream => stream.Type == "Subtitle" && NormalizeLanguage(stream.Language) == "zh")) tags.Add(LumenText.Get("Chinese subtitles"));
        return tags.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string VersionTitle(MediaSourceInfo? source)
    {
        if (source is null) return LumenText.Get("Not available");
        var video = (source.MediaStreams ?? []).FirstOrDefault(stream => stream.Type == "Video");
        var values = new List<string>();
        if (video?.Height is > 0) values.Add($"{video.Height}p");
        if (!string.IsNullOrWhiteSpace(video?.Codec)) values.Add(video.Codec.ToUpperInvariant());
        var range = VideoRange(video);
        if (!string.IsNullOrWhiteSpace(range) && !string.Equals(range, "SDR", StringComparison.OrdinalIgnoreCase)) values.Add(range);
        if (source.Size is > 0) values.Add(FileSize(source.Size.Value));
        return values.Count > 0 ? string.Join(" \u00b7 ", values)
            : source.Name ?? source.Container?.ToUpperInvariant() ?? LumenText.Get("Not available");
    }

    private static string SourceSummary(MediaSourceInfo source) => string.Join(" \u00b7 ", new[]
    {
        source.Bitrate is > 0 ? Bitrate(source.Bitrate.Value) : null,
        source.Size is > 0 ? FileSize(source.Size.Value) : null,
        source.Name
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string StreamTitle(MediaStream stream)
    {
        if (!string.IsNullOrWhiteSpace(stream.DisplayTitle)) return stream.DisplayTitle;
        if (!string.IsNullOrWhiteSpace(stream.Title)) return stream.Title;
        var title = string.Join(" \u00b7 ", new[]
        {
            stream.Type == "Video" ? null : LanguageName(stream), stream.Codec?.ToUpperInvariant(),
            stream.Type == "Audio" ? ChannelName(stream) : null
        }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return title.Length > 0 ? title : LumenText.Get("Not available");
    }

    private static string StreamSummary(MediaStream stream)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(stream.Codec)) flags.Add(stream.Codec.ToUpperInvariant());
        if (stream.Type == "Audio")
        {
            var channels = ChannelName(stream);
            if (!string.IsNullOrWhiteSpace(channels)) flags.Add(channels);
            if (stream.SampleRate is > 0) flags.Add($"{stream.SampleRate.Value / 1000d:0.##} kHz");
        }
        if (stream.Type == "Subtitle" && stream.IsExternal is { } external) flags.Add(LumenText.Get(external ? "External" : "Embedded"));
        if (stream.IsDefault == true) flags.Add(LumenText.Get("Default"));
        if (stream.IsForced == true) flags.Add(LumenText.Get("Forced"));
        return string.Join(" \u00b7 ", flags);
    }

    private static string VideoRange(MediaStream? stream)
    {
        var extended = CleanVideoRange(stream?.ExtendedVideoSubTypeDescription);
        return extended.Length > 0 ? extended : CleanVideoRange(stream?.VideoRange);
    }

    private static string CleanVideoRange(string? value)
    {
        var range = value?.Trim() ?? string.Empty;
        return string.Equals(range, "None", StringComparison.OrdinalIgnoreCase)
            || string.Equals(range, "Unknown", StringComparison.OrdinalIgnoreCase) ? string.Empty : range;
    }

    private static string ChannelName(MediaStream stream) => !string.IsNullOrWhiteSpace(stream.ChannelLayout)
        ? stream.ChannelLayout : stream.Channels == 2 ? LumenText.Get("Stereo")
        : stream.Channels is > 0 ? Format("{0} channels", stream.Channels.Value) : string.Empty;

    private static string LanguageName(MediaStream stream)
    {
        if (!string.IsNullOrWhiteSpace(stream.DisplayLanguage)) return LumenText.Get(stream.DisplayLanguage);
        return LumenText.Get(NormalizeLanguage(stream.Language) switch
        {
            "zh" => "Chinese", "en" => "English", "ja" => "Japanese", "ko" => "Korean", "fr" => "French", "de" => "German",
            "es" => "Spanish", "it" => "Italian", "ru" => "Russian", _ => stream.Language ?? "Unknown language"
        });
    }

    private static string NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        "chi" or "zho" or "zh-cn" or "zh-tw" or "zh-hans" or "zh-hant" => "zh",
        "eng" => "en", "jpn" => "ja", "kor" => "ko", "fre" or "fra" => "fr", "ger" or "deu" => "de",
        "spa" => "es", "ita" => "it", "rus" => "ru", var value => value ?? string.Empty
    };

    private static bool SameLanguage(string? first, string? second) => !string.IsNullOrWhiteSpace(first)
        && !string.IsNullOrWhiteSpace(second) && NormalizeLanguage(first) == NormalizeLanguage(second);
    private static string Bitrate(long bitrate) => bitrate >= 1_000_000
        ? (bitrate / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + " Mbps"
        : (bitrate / 1_000d).ToString("0.##", CultureInfo.InvariantCulture) + " kbps";
    private static string FileSize(long bytes) => bytes >= 1_000_000_000
        ? (bytes / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
        : (bytes / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + " MB";
    private static string? SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile) return null;
        return path.Replace('\\', '/').Split('/').LastOrDefault();
    }
}
