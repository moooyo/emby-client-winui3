using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace EmbyClient.App.Playback;

internal sealed record LocalSubtitleTextDocument(string WebVtt, int CueCount);

/// <summary>Validates local Unicode text cues without depending on a native codec or the playback clock.</summary>
internal static partial class LocalSubtitleTextParser
{
    internal const int MaximumBytes = 4 * 1024 * 1024;
    internal const int MaximumCues = 50_000;

    internal static LocalSubtitleTextDocument Parse(string fileName, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(data);
        var text = Decode(data);
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".srt" => ParseSubRip(text),
            ".vtt" => ParseWebVtt(text),
            _ => throw new FormatException("The text parser supports SubRip and WebVTT only.")
        };
    }

    internal static string Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length is 0 or > MaximumBytes) throw new FormatException("The subtitle file size is outside the supported range.");
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var offset = 0;
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 && data[2] == 0xfe && data[3] == 0xff)
        {
            encoding = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: true);
            offset = 4;
        }
        else if (data.Length >= 4 && data[0] == 0xff && data[1] == 0xfe && data[2] == 0 && data[3] == 0)
        {
            encoding = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: true);
            offset = 4;
        }
        else if (data.Length >= 2 && data[0] == 0xff && data[1] == 0xfe)
        {
            encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true);
            offset = 2;
        }
        else if (data.Length >= 2 && data[0] == 0xfe && data[1] == 0xff)
        {
            encoding = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
            offset = 2;
        }
        else if (data.Length >= 3 && data[0] == 0xef && data[1] == 0xbb && data[2] == 0xbf) offset = 3;
        string text;
        try { text = encoding.GetString(data, offset, data.Length - offset); }
        catch (DecoderFallbackException exception) { throw new FormatException("The subtitle file is not valid Unicode text.", exception); }
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(text) || text.Any(character => char.IsControl(character) && character is not ('\n' or '\t')))
            throw new FormatException("The subtitle file contains no text or contains binary control characters.");
        if (Encoding.UTF8.GetByteCount(text) > MaximumBytes)
            throw new FormatException("The decoded subtitle text exceeds the supported size.");
        return text;
    }

    private static LocalSubtitleTextDocument ParseSubRip(string text)
    {
        var lines = text.Split('\n');
        var output = new StringBuilder("WEBVTT\n\n");
        var index = 0;
        var count = 0;
        while (SkipSeparators(lines, ref index))
        {
            var timing = lines[index++].Trim();
            if (timing.Length > 0 && timing.All(char.IsAsciiDigit))
            {
                if (index >= lines.Length) throw new FormatException("The subtitle cue is missing its timestamp.");
                timing = lines[index++].Trim();
            }
            var (start, end, _) = ParseTiming(timing, webVtt: false);
            var payload = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                var line = lines[index++];
                if (line.Contains("-->", StringComparison.Ordinal))
                    throw new FormatException("Subtitle cues must be separated by a blank line.");
                // Native text presentation has no HTML styles. Strip familiar formatting and encode literal markup.
                var plainText = WebUtility.HtmlDecode(SubRipFormatting().Replace(line, string.Empty));
                if (plainText.Any(character => char.IsControl(character) && character != '\t'))
                    throw new FormatException("The subtitle cue contains an invalid encoded control character.");
                payload.Add(EscapeText(plainText));
            }
            if (payload.Count == 0 || payload.All(string.IsNullOrWhiteSpace))
                throw new FormatException("The subtitle cue has no text.");
            AppendCue(output, start, end, payload, string.Empty);
            if (++count > MaximumCues) throw new FormatException("The subtitle cue count exceeds the supported limit.");
        }
        return Complete(output, count);
    }

    private static LocalSubtitleTextDocument ParseWebVtt(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length == 0 || !(lines[0] == "WEBVTT" || lines[0].StartsWith("WEBVTT ", StringComparison.Ordinal)
            || lines[0].StartsWith("WEBVTT\t", StringComparison.Ordinal)) || lines[0].Contains("-->", StringComparison.Ordinal))
            throw new FormatException("The WebVTT signature is invalid.");
        var index = 1;
        // A blank line must end the WebVTT header before the first cue or metadata block.
        while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
        {
            if (lines[index].Contains("-->", StringComparison.Ordinal))
                throw new FormatException("The WebVTT header is missing its separator.");
            index++;
        }
        var output = new StringBuilder("WEBVTT\n\n");
        var count = 0;
        while (SkipSeparators(lines, ref index))
        {
            var line = lines[index++];
            if (line is "STYLE" or "REGION" || line == "NOTE" || line.StartsWith("NOTE ", StringComparison.Ordinal)
                || line.StartsWith("NOTE\t", StringComparison.Ordinal))
            {
                if (line is "STYLE" or "REGION" && count > 0)
                    throw new FormatException("WebVTT style and region blocks must precede cues.");
                var metadata = new List<string> { line };
                while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index])) metadata.Add(lines[index++]);
                if (line is "STYLE" or "REGION")
                {
                    if (metadata.Any(value => value.Contains("-->", StringComparison.Ordinal)))
                        throw new FormatException("WebVTT metadata contains an invalid cue delimiter.");
                    foreach (var value in metadata) output.Append(value).Append('\n');
                    output.Append('\n');
                }
                continue;
            }
            if (!line.Contains("-->", StringComparison.Ordinal))
            {
                // Cue identifiers do not affect presentation and are intentionally omitted from the normalized output.
                if (index >= lines.Length || string.IsNullOrWhiteSpace(lines[index]))
                    throw new FormatException("The WebVTT cue identifier is missing its timestamp.");
                line = lines[index++];
            }
            var (start, end, settings) = ParseTiming(line.Trim(), webVtt: true);
            var payload = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]))
            {
                var payloadLine = lines[index++];
                if (payloadLine.Contains("-->", StringComparison.Ordinal))
                    throw new FormatException("WebVTT cues must be separated by a blank line.");
                payload.Add(payloadLine);
            }
            if (payload.Count == 0 || payload.All(string.IsNullOrWhiteSpace))
                throw new FormatException("The WebVTT cue has no text.");
            AppendCue(output, start, end, payload, settings);
            if (++count > MaximumCues) throw new FormatException("The subtitle cue count exceeds the supported limit.");
        }
        return Complete(output, count);
    }

    private static (long Start, long End, string Settings) ParseTiming(string line, bool webVtt)
    {
        var arrow = line.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0 || line.IndexOf("-->", arrow + 3, StringComparison.Ordinal) >= 0)
            throw new FormatException("The subtitle cue timestamp is invalid.");
        var left = line[..arrow].Trim();
        var right = line[(arrow + 3)..].Trim();
        var separator = right.IndexOfAny([' ', '\t']);
        var endText = separator < 0 ? right : right[..separator];
        var settings = separator < 0 ? string.Empty : right[separator..].Trim();
        if (!webVtt && settings.Length > 0) throw new FormatException("The SubRip cue timestamp has unexpected text.");
        if (webVtt && settings.Length > 0) ValidateWebVttSettings(settings);
        var start = ParseTimestamp(left, webVtt);
        var end = ParseTimestamp(endText, webVtt);
        if (end <= start) throw new FormatException("A subtitle cue must end after it starts.");
        return (start, end, settings);
    }

    private static long ParseTimestamp(string value, bool webVtt)
    {
        var parts = value.Split(':');
        if (parts.Length != 3 && !(webVtt && parts.Length == 2))
            throw new FormatException("The subtitle timestamp must contain hours, minutes and seconds.");
        var hours = parts.Length == 3 ? ParseDigits(parts[0], webVtt ? 2 : 1, 6) : 0;
        var minutes = ParseDigits(parts[^2], 2, 2);
        var secondsPart = parts[^1];
        var separator = secondsPart.IndexOf('.');
        if (!webVtt && separator < 0) separator = secondsPart.IndexOf(',');
        if (separator != 2) throw new FormatException("The subtitle timestamp has invalid milliseconds.");
        var seconds = ParseDigits(secondsPart[..separator], 2, 2);
        var fraction = secondsPart[(separator + 1)..];
        var milliseconds = ParseDigits(fraction, webVtt ? 3 : 1, 3);
        if (fraction.Length == 1) milliseconds *= 100;
        else if (fraction.Length == 2) milliseconds *= 10;
        if (minutes > 59 || seconds > 59) throw new FormatException("Subtitle minutes and seconds must be below 60.");
        return checked(hours * 3_600_000L + minutes * 60_000L + seconds * 1000L + milliseconds);
    }

    private static int ParseDigits(string value, int minimumLength, int maximumLength)
    {
        if (value.Length < minimumLength || value.Length > maximumLength || !value.All(char.IsAsciiDigit)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw new FormatException("The subtitle timestamp contains an invalid number.");
        return number;
    }

    private static void ValidateWebVttSettings(string settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in settings.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1) throw new FormatException("The WebVTT cue setting is invalid.");
            var name = token[..separator];
            var value = token[(separator + 1)..];
            if (!seen.Add(name)) throw new FormatException("The WebVTT cue setting is duplicated.");
            var valid = name switch
            {
                "vertical" => value is "rl" or "lr",
                "align" => value is "start" or "center" or "end" or "left" or "right",
                "size" => IsPercentage(value),
                "position" => IsPositionSetting(value),
                "line" => IsLineSetting(value),
                "region" => !value.Any(char.IsWhiteSpace),
                _ => false
            };
            if (!valid) throw new FormatException("The WebVTT cue setting is unsupported or invalid.");
        }
    }

    private static bool IsPositionSetting(string value)
    {
        var parts = value.Split(',');
        return parts.Length is 1 or 2 && IsPercentage(parts[0])
            && (parts.Length == 1 || parts[1] is "line-left" or "center" or "line-right" or "auto");
    }

    private static bool IsLineSetting(string value)
    {
        var parts = value.Split(',');
        return parts.Length is 1 or 2 && (parts[0] == "auto" || IsPercentage(parts[0])
            || int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            && (parts.Length == 1 || parts[1] is "start" or "center" or "end");
    }

    private static bool IsPercentage(string value) => value.EndsWith('%')
        && double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percentage)
        && double.IsFinite(percentage) && percentage is >= 0 and <= 100;

    private static bool SkipSeparators(string[] lines, ref int index)
    {
        while (index < lines.Length && string.IsNullOrWhiteSpace(lines[index])) index++;
        return index < lines.Length;
    }

    private static void AppendCue(StringBuilder output, long start, long end, List<string> payload, string settings)
    {
        output.Append(FormatTimestamp(start)).Append(" --> ").Append(FormatTimestamp(end));
        if (settings.Length > 0) output.Append(' ').Append(settings);
        output.Append('\n');
        foreach (var line in payload) output.Append(line).Append('\n');
        output.Append('\n');
        if (output.Length > MaximumBytes) throw new FormatException("The normalized subtitle exceeds the supported size.");
    }

    private static LocalSubtitleTextDocument Complete(StringBuilder output, int count)
    {
        var webVtt = output.ToString();
        if (count == 0 || Encoding.UTF8.GetByteCount(webVtt) > MaximumBytes)
            throw new FormatException("The subtitle has no valid cues or exceeds the supported size.");
        return new LocalSubtitleTextDocument(webVtt, count);
    }

    private static string FormatTimestamp(long milliseconds) => string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds / 3_600_000:00}:{milliseconds / 60_000 % 60:00}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");

    private static string EscapeText(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    [GeneratedRegex(@"</?(?:b|i|u|s|font)(?:[ \t][^>\n]*)?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubRipFormatting();
}
