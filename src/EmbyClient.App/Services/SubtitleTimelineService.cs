using EmbyClient.Api;
using System.Globalization;
using System.Net;
using System.Text;

namespace EmbyClient.App.Services;

internal sealed record SubtitleTimelineCue(int Number, long StartTicks, long EndTicks, string Preview);
internal sealed record SubtitleTimelineInterval(SubtitleTimelineCue Cue, long StartTicks, long EndTicks,
    double StartFraction, double EndFraction);
internal sealed record SubtitleTimelineDocument(int CueCount, IReadOnlyList<SubtitleTimelineCue> Cues)
{
    public bool IsSampled => CueCount > Cues.Count;
}
internal sealed record SubtitleTimelineResult(SubtitleTimelineDocument? Document, string? UnavailableReason)
{
    internal static SubtitleTimelineResult Unavailable(string reason) => new(null, reason);
}

/// <summary>Reads original-media text cue intervals for a bounded metadata visualization.</summary>
internal static class SubtitleTimelineService
{
    internal const int MaximumCues = 50_000;
    internal const int MaximumSampleCues = 256;
    internal const int MaximumPreviewCharacters = 160;

    internal static bool IsGraphicalTrack(MediaStream stream) => stream.Type == "Subtitle" && stream.Index >= 0
        && (stream.IsTextSubtitleStream == false || stream.Codec?.Trim().ToLowerInvariant() is
            "pgssub" or "pgs" or "hdmv_pgs_subtitle" or "dvdsub" or "dvd_subtitle" or "dvbsub" or "dvb_subtitle" or "vobsub" or "xsub");

    internal static bool IsTextTrack(MediaStream stream) => stream.Type == "Subtitle" && stream.Index >= 0
        && !IsGraphicalTrack(stream) && (stream.IsTextSubtitleStream == true || stream.Codec?.Trim().ToLowerInvariant() is
            "srt" or "subrip" or "vtt" or "webvtt" or "ass" or "ssa" or "ttml" or "mov_text" or "text");

    internal static async Task<SubtitleTimelineResult> LoadAsync(EmbyApiClient api, string itemId,
        MediaSourceInfo source, MediaStream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(stream);
        if (!IsTextTrack(stream)) return SubtitleTimelineResult.Unavailable("Text cue timing is unavailable for this subtitle format.");
        if (string.IsNullOrWhiteSpace(source.Id)) return SubtitleTimelineResult.Unavailable("The subtitle media source is unavailable.");
        if (source.IsInfiniteStream == true || source.RequiresOpening == true)
            return SubtitleTimelineResult.Unavailable("Subtitle timelines are unavailable for live media.");

        var response = await api.GetPlaybackInfoAsync(itemId, new PlaybackInfoRequest
        {
            MediaSourceId = source.Id,
            SubtitleStreamIndex = stream.Index,
            StartTimeTicks = 0,
            IsPlayback = false,
            AutoOpenLiveStream = false,
            EnableTranscoding = false,
            DeviceProfile = new DeviceProfile
            {
                Name = "Text subtitle timeline inspection",
                SubtitleProfiles =
                [
                    new SubtitleProfile { Format = "vtt", Method = "External", AllowChunkedResponse = false },
                    new SubtitleProfile { Format = "srt", Method = "External", AllowChunkedResponse = false }
                ]
            }
        }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(response.ErrorCode))
            throw new EmbyProtocolException("The server rejected subtitle timeline negotiation.");
        var negotiatedSource = response.MediaSources.FirstOrDefault(candidate => candidate.Id == source.Id);
        if (negotiatedSource is null || negotiatedSource.IsInfiniteStream == true || negotiatedSource.RequiresOpening == true)
            return SubtitleTimelineResult.Unavailable("The subtitle media source is unavailable.");
        var negotiated = negotiatedSource.MediaStreams.FirstOrDefault(candidate => candidate.Type == "Subtitle" && candidate.Index == stream.Index);
        // The request advertises reader capabilities; only the actual server response establishes delivery.
        if (negotiated is null || !IsTextTrack(negotiated)
            || !string.Equals(negotiated.DeliveryMethod, "External", StringComparison.OrdinalIgnoreCase))
            return SubtitleTimelineResult.Unavailable("The server did not provide downloadable text subtitles.");
        var deliveryUrl = !string.IsNullOrWhiteSpace(negotiated.DeliveryUrl) ? negotiated.DeliveryUrl
            : new Uri(api.ApiRoot, $"Videos/{Uri.EscapeDataString(itemId)}/{Uri.EscapeDataString(source.Id)}/Subtitles/{stream.Index.ToString(CultureInfo.InvariantCulture)}/Stream.vtt").AbsoluteUri;
        var target = api.ResolveTextSubtitleUri(deliveryUrl);
        if (!SameOrigin(target, api.ApiRoot))
            return SubtitleTimelineResult.Unavailable("Subtitle timelines are unavailable for external download hosts.");
        if (!HasOriginalTimeline(target, api.ApiRoot, source.Id, stream.Index))
            return SubtitleTimelineResult.Unavailable("The subtitle time reference could not be confirmed.");
        var format = Path.GetExtension(target.AbsolutePath).ToLowerInvariant();
        if (format is not (".srt" or ".vtt"))
            return SubtitleTimelineResult.Unavailable("The server did not provide SRT or WebVTT subtitles.");
        var bytes = await api.GetTextSubtitleAsync(target.AbsoluteUri, cancellationToken).ConfigureAwait(false);
        var document = await Task.Run(() => Parse(format, bytes, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new SubtitleTimelineResult(document, null);
    }

    internal static IReadOnlyList<SubtitleTimelineInterval> Project(SubtitleTimelineDocument document, long durationTicks)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (durationTicks <= 0) return [];
        return document.Cues.Where(cue => cue.EndTicks > 0 && cue.StartTicks < durationTicks)
            .Select(cue => new SubtitleTimelineInterval(cue, Math.Clamp(cue.StartTicks, 0, durationTicks),
                Math.Clamp(cue.EndTicks, 0, durationTicks), Math.Clamp(cue.StartTicks / (double)durationTicks, 0, 1),
                Math.Clamp(cue.EndTicks / (double)durationTicks, 0, 1)))
            .Where(interval => interval.EndTicks > interval.StartTicks).ToArray();
    }

    internal static SubtitleTimelineDocument Parse(string format, byte[] data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        var webVtt = format.TrimStart('.').ToLowerInvariant() switch
        {
            "vtt" => true,
            "srt" => false,
            _ => throw new FormatException("The subtitle timeline reader supports SRT and WebVTT only.")
        };
        var text = Decode(data);
        using var reader = new StringReader(text);
        if (webVtt)
        {
            var signature = reader.ReadLine();
            if (signature is null || !(signature == "WEBVTT" || signature.StartsWith("WEBVTT ", StringComparison.Ordinal)
                || signature.StartsWith("WEBVTT\t", StringComparison.Ordinal)) || signature.Contains("-->", StringComparison.Ordinal))
                throw new FormatException("The WebVTT signature is invalid.");
            string? header;
            while ((header = reader.ReadLine()) is not null && !string.IsNullOrWhiteSpace(header))
            {
                if (header.Contains("-->", StringComparison.Ordinal) || header.Contains("X-TIMESTAMP-MAP", StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("The WebVTT file does not establish an original-media time reference.");
            }
        }
        var samples = new List<SubtitleTimelineCue>(MaximumSampleCues);
        SubtitleTimelineCue? lastCue = null;
        var count = 0;
        var stride = 1;
        string? line;
        while ((line = ReadNextContent(reader, cancellationToken)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (webVtt && (line is "STYLE" or "REGION" or "NOTE" || line.StartsWith("NOTE ", StringComparison.Ordinal)
                || line.StartsWith("NOTE\t", StringComparison.Ordinal)))
            {
                if (line is "STYLE" or "REGION" && count > 0)
                    throw new FormatException("WebVTT styles and regions must precede cues.");
                while (reader.ReadLine() is { } metadata && !string.IsNullOrWhiteSpace(metadata))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (line is "STYLE" or "REGION" && metadata.Contains("-->", StringComparison.Ordinal))
                        throw new FormatException("WebVTT style and region blocks contain an invalid timestamp delimiter.");
                    if (metadata.Contains("X-TIMESTAMP-MAP", StringComparison.OrdinalIgnoreCase))
                        throw new FormatException("The WebVTT file has an unsupported time reference.");
                }
                continue;
            }
            if (!line.Contains("-->", StringComparison.Ordinal) && (webVtt || line.All(char.IsAsciiDigit)))
                line = reader.ReadLine() ?? throw new FormatException("The subtitle cue is missing its timestamp.");
            var (start, end) = ParseTiming(line.Trim(), webVtt);
            var preview = new StringBuilder(MaximumPreviewCharacters);
            var hasText = false;
            var shortened = false;
            while (reader.ReadLine() is { } payload && !string.IsNullOrWhiteSpace(payload))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (payload.Contains("-->", StringComparison.Ordinal))
                    throw new FormatException("Subtitle cues must be separated by a blank line.");
                AppendPreview(payload, preview, ref hasText, ref shortened);
            }
            if (!hasText) throw new FormatException("The subtitle cue has no text.");
            if (++count > MaximumCues) throw new FormatException("The subtitle cue count exceeds the client limit.");
            if (shortened && preview.Length == MaximumPreviewCharacters) preview.Length--;
            if (preview.Length > 0 && char.IsHighSurrogate(preview[^1])) preview.Length--;
            if (shortened) preview.Append('\u2026');
            lastCue = new SubtitleTimelineCue(count, start, end, preview.ToString().Trim());
            // Reduce the sample by powers of two as it grows. Every retained interval is a real cue,
            // and the final cue is retained separately so a long file is represented across its duration.
            if ((count - 1) % stride == 0) samples.Add(lastCue);
            if (samples.Count > MaximumSampleCues)
            {
                samples = samples.Where((_, index) => index % 2 == 0).ToList();
                stride *= 2;
            }
        }
        if (lastCue is null) throw new FormatException("The subtitle file contains no valid text cues.");
        if (samples[^1].Number != lastCue.Number)
        {
            if (samples.Count == MaximumSampleCues) samples[^1] = lastCue;
            else samples.Add(lastCue);
        }
        return new SubtitleTimelineDocument(count, Array.AsReadOnly(samples.OrderBy(cue => cue.StartTicks).ThenBy(cue => cue.Number).ToArray()));
    }

    private static bool SameOrigin(Uri target, Uri origin) => target.Scheme.Equals(origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && target.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase) && target.Port == origin.Port;

    private static bool HasOriginalTimeline(Uri target, Uri apiRoot, string sourceId, int streamIndex)
    {
        if (!target.AbsolutePath.StartsWith(apiRoot.AbsolutePath, StringComparison.OrdinalIgnoreCase)) return false;
        var path = target.AbsolutePath[apiRoot.AbsolutePath.Length..].Split('/');
        if (path.Length is not (6 or 7) || !(path[0].Equals("Videos", StringComparison.OrdinalIgnoreCase)
                || path[0].Equals("Items", StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrEmpty(path[1]) || Uri.UnescapeDataString(path[2]) != sourceId
            || !path[3].Equals("Subtitles", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(path[4], NumberStyles.None, CultureInfo.InvariantCulture, out var deliveredIndex) || deliveredIndex != streamIndex
            || !path[^1].StartsWith("Stream.", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == 7 && (!long.TryParse(path[5], NumberStyles.None, CultureInfo.InvariantCulture, out var start) || start != 0))
            return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in target.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = parameter.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
            if (key is not ("startpositionticks" or "starttimeticks" or "endpositionticks" or "copytimestamps")) continue;
            if (!seen.Add(key) || pair.Length != 2) return false;
            var value = Uri.UnescapeDataString(pair[1]);
            if (key == "copytimestamps")
            {
                if (!value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0") return false;
            }
            else if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) || ticks != 0) return false;
        }
        return true;
    }

    private static string Decode(byte[] data)
    {
        if (data.Length is 0 or > EmbyApiClient.MaximumTextSubtitleBytes)
            throw new FormatException("The subtitle file size is outside the supported range.");
        Encoding encoding = new UTF8Encoding(false, true);
        var offset = 0;
        if (data.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, false, true); offset = 4; }
        else if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = new UTF32Encoding(false, false, true); offset = 4; }
        else if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = new UnicodeEncoding(false, false, true); offset = 2; }
        else if (data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = new UnicodeEncoding(true, false, true); offset = 2; }
        else if (data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) offset = 3;
        string text;
        try { text = encoding.GetString(data, offset, data.Length - offset); }
        catch (DecoderFallbackException exception) { throw new FormatException("The subtitle file is not valid Unicode text.", exception); }
        if (Encoding.UTF8.GetByteCount(text) > EmbyApiClient.MaximumTextSubtitleBytes
            || text.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
            throw new FormatException("The subtitle file contains unsupported text.");
        return text;
    }

    private static string? ReadNextContent(StringReader reader, CancellationToken cancellationToken)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(line)) return line;
        }
        return null;
    }

    private static (long Start, long End) ParseTiming(string line, bool webVtt)
    {
        var delimiter = line.IndexOf("-->", StringComparison.Ordinal);
        if (delimiter < 0 || delimiter != line.LastIndexOf("-->", StringComparison.Ordinal))
            throw new FormatException("The subtitle timestamp delimiter is invalid.");
        var start = ParseTimestamp(line[..delimiter].Trim(), webVtt);
        var ending = line[(delimiter + 3)..].Trim();
        var settings = ending.IndexOfAny([' ', '\t']);
        if (settings >= 0)
        {
            if (!webVtt) throw new FormatException("The SRT timestamp has unsupported settings.");
            ending = ending[..settings];
        }
        var end = ParseTimestamp(ending, webVtt);
        if (end <= start) throw new FormatException("The subtitle cue has an invalid time range.");
        return (start, end);
    }

    private static long ParseTimestamp(string text, bool webVtt)
    {
        var parts = text.Split(':');
        if (parts.Length != 3 && !(webVtt && parts.Length == 2)) throw new FormatException("The subtitle timestamp is invalid.");
        var secondsAndFraction = parts[^1].Split(webVtt ? new[] { '.' } : new[] { '.', ',' });
        if (secondsAndFraction.Length != 2 || secondsAndFraction[0].Length != 2
            || secondsAndFraction[1].Length is < 1 or > 3 || webVtt && secondsAndFraction[1].Length != 3
            || parts[^2].Length != 2 || parts.Length == 3 && parts[0].Length < 2)
            throw new FormatException("The subtitle timestamp is invalid.");
        static long Number(string value) => value.Length > 0 && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number
                : throw new FormatException("The subtitle timestamp is invalid.");
        var hours = parts.Length == 3 ? Number(parts[0]) : 0;
        var minutes = Number(parts[^2]);
        var seconds = Number(secondsAndFraction[0]);
        if (minutes >= 60 || seconds >= 60) throw new FormatException("The subtitle timestamp is invalid.");
        var milliseconds = Number(secondsAndFraction[1]) * (secondsAndFraction[1].Length == 1 ? 100 : secondsAndFraction[1].Length == 2 ? 10 : 1);
        try { return checked(hours * TimeSpan.TicksPerHour + minutes * TimeSpan.TicksPerMinute + seconds * TimeSpan.TicksPerSecond + milliseconds * TimeSpan.TicksPerMillisecond); }
        catch (OverflowException exception) { throw new FormatException("The subtitle timestamp exceeds the supported range.", exception); }
    }

    private static void AppendPreview(string payload, StringBuilder preview, ref bool hasText, ref bool shortened)
    {
        var plain = new StringBuilder(payload.Length);
        var hasClosingMarkup = true;
        for (var index = 0; index < payload.Length; index++)
        {
            if (payload[index] == '<' && hasClosingMarkup)
            {
                var end = payload.IndexOf('>', index + 1);
                if (end < 0) hasClosingMarkup = false;
                else if (IsMarkup(payload.AsSpan(index + 1, end - index - 1)))
                {
                    index = end;
                    continue;
                }
                else
                {
                    plain.Append(payload.AsSpan(index, end - index + 1));
                    index = end;
                    continue;
                }
            }
            plain.Append(payload[index]);
        }
        var text = WebUtility.HtmlDecode(plain.ToString());
        if (text.Any(character => char.IsControl(character) && character != '\t'))
            throw new FormatException("The subtitle cue contains unsupported control characters.");
        foreach (var character in text)
        {
            if (!char.IsWhiteSpace(character)) hasText = true;
            if (preview.Length >= MaximumPreviewCharacters)
            {
                if (!char.IsWhiteSpace(character)) shortened = true;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (preview.Length > 0 && preview[^1] != ' ') preview.Append(' ');
            }
            else preview.Append(character);
        }
        if (preview.Length > 0 && preview.Length < MaximumPreviewCharacters && preview[^1] != ' ') preview.Append(' ');
    }

    private static bool IsMarkup(ReadOnlySpan<char> tag)
    {
        if (tag.IsEmpty || char.IsWhiteSpace(tag[0])) return false;
        if (tag[0] == '/') tag = tag[1..];
        var separator = tag.IndexOfAny(' ', '.', '\t');
        if (separator >= 0) tag = tag[..separator];
        return tag.Equals("i", StringComparison.OrdinalIgnoreCase) || tag.Equals("b", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("u", StringComparison.OrdinalIgnoreCase) || tag.Equals("font", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("c", StringComparison.OrdinalIgnoreCase) || tag.Equals("v", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("lang", StringComparison.OrdinalIgnoreCase) || tag.Equals("ruby", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("rt", StringComparison.OrdinalIgnoreCase) || tag.Equals("br", StringComparison.OrdinalIgnoreCase)
            || tag.Length > 0 && char.IsAsciiDigit(tag[0]);
    }
}
