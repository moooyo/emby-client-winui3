using System.Security.Cryptography;
using System.Text;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed record FixtureSubtitle(byte[] Bytes, string Sha256, string? Language)
{
    private const int MaximumBytes = 4 * 1024 * 1024;

    public static FixtureSubtitle Read(string path)
    {
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".vtt", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The explicit subtitle must be an existing .vtt file.");
        using var stream = File.OpenRead(path);
        if (stream.Length is < 8 or > MaximumBytes)
            throw new ArgumentException("The external WebVTT file must be nonempty and no larger than 4 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new ArgumentException("The subtitle grew beyond its bounded input snapshot.");
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
        var firstLine = text.Split('\n', 2)[0];
        if (!(firstLine == "WEBVTT" || firstLine.StartsWith("WEBVTT ", StringComparison.Ordinal)
                || firstLine.StartsWith("WEBVTT\t", StringComparison.Ordinal))
            || !text.Contains("\n\n", StringComparison.Ordinal) || !text.Contains(" --> ", StringComparison.Ordinal))
            throw new ArgumentException("The subtitle must have a WebVTT header, separator, and a timed cue. Native validation remains separate.");
        var name = Path.GetFileName(path);
        var language = name.EndsWith(".zh.vtt", StringComparison.OrdinalIgnoreCase) ? "zho"
            : name.EndsWith(".en.vtt", StringComparison.OrdinalIgnoreCase) ? "eng" : null;
        return new FixtureSubtitle(bytes, Convert.ToHexString(SHA256.HashData(bytes)), language);
    }
}

internal sealed partial class FixtureState
{
    private MediaStream[] WithExternalSubtitle(string itemId, string sourceId, MediaStream[] streams) => Options.Subtitle is { } subtitle
        ? [.. streams, new MediaStream
        {
            Index = 2, Type = "Subtitle", Codec = "vtt", Language = subtitle.Language,
            DisplayLanguage = subtitle.Language == "zho" ? "Chinese" : subtitle.Language == "eng" ? "English" : null,
            Title = "Synthetic external WebVTT", DisplayTitle = "Synthetic external WebVTT",
            IsDefault = true, IsForced = false, IsExternal = true, IsTextSubtitleStream = true, DeliveryMethod = "External",
            DeliveryUrl = $"/emby/Videos/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(sourceId)}/Subtitles/2/Stream.vtt"
        }]
        : streams;

    public FixtureSubtitle? Subtitle(string itemId, string sourceId)
    {
        lock (_gate)
        {
            return Options.Subtitle is { } subtitle && _items.TryGetValue(itemId, out var item) && IsPlayable(item)
                && Sources(itemId).Any(source => source.Id == sourceId) ? subtitle : null;
        }
    }
}
