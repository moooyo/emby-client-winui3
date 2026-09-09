using System.Collections.ObjectModel;
using System.Globalization;
using EmbyClient.Api;

namespace EmbyClient.Playback;

internal static class PlaybackRequestFactory
{
    internal static MediaSourceInfo SelectSource(PlaybackInfoResponse response, PlaybackSelection selection)
    {
        if (!string.IsNullOrWhiteSpace(response.ErrorCode)) throw new PlaybackException(response.ErrorCode
            is "NotAllowed" or "NoCompatibleStream" or "RateLimitExceeded" ? response.ErrorCode : "NegotiationRejected");
        if (string.IsNullOrWhiteSpace(response.PlaySessionId)) throw new PlaybackException("MissingPlaySessionId");
        var candidates = (response.MediaSources ?? []).Where(source => !string.IsNullOrWhiteSpace(source.Id));
        if (selection.MediaSourceId is not null)
        {
            candidates = candidates.Where(source => string.Equals(source.Id, selection.MediaSourceId, StringComparison.Ordinal));
        }

        return candidates.OrderBy(source => CanUseDirectStream(source, selection) ? 0 : 1)
            .FirstOrDefault(source => source.RequiresOpening == true
                || CanUseDirectStream(source, selection)
                || (source.SupportsTranscoding == true && !string.IsNullOrWhiteSpace(source.TranscodingUrl)))
            ?? throw new PlaybackException("NoCompatibleSource");
    }

    internal static PlaybackEngineRequest Create(EmbyApiClient api, Guid id, string playSessionId,
        MediaSourceInfo source, PlaybackSelection selection)
    {
        if (string.IsNullOrWhiteSpace(source.Id)) throw new PlaybackException("MissingMediaSourceId");
        if (source.RequiresOpening == true) throw new PlaybackException("SourceNotOpened");
        ValidateTrack(source, selection.AudioStreamIndex, "Audio");
        ValidateTrack(source, selection.SubtitleStreamIndex, "Subtitle");
        // The SDK defines VideoRange as an open string without defining profile-condition values.
        // Force a fresh conversion with stream copy disabled for explicitly non-SDR source metadata.
        // Missing metadata is not evidence of HDR support.
        if (!selection.ForceTranscoding && (source.MediaStreams ?? []).Any(stream => IsType(stream, "Video")
            && !string.IsNullOrWhiteSpace(stream.VideoRange) && !string.Equals(stream.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase)))
            throw new PlaybackException("UnsupportedFormat");
        var audio = selection.AudioStreamIndex ?? source.DefaultAudioStreamIndex;
        var subtitle = selection.SubtitleStreamIndex ?? source.DefaultSubtitleStreamIndex;
        var subtitleStream = subtitle is >= 0
            ? (source.MediaStreams ?? []).FirstOrDefault(stream => stream.Index == subtitle && IsType(stream, "Subtitle"))
            : null;
        var useDirect = CanUseDirectStream(source, selection);
        PlaybackDeliveryMethod method;
        var timelineKind = PlaybackTimelineKind.FullSource;
        Uri uri;
        long offset;
        var initialPosition = selection.StartPositionTicks;
        if (useDirect)
        {
            method = PlaybackDeliveryMethod.DirectStream;
            uri = !string.IsNullOrWhiteSpace(source.DirectStreamUrl)
                ? api.ResolveMediaUri(source.DirectStreamUrl)
                : api.BuildVideoStreamUri(selection.ItemId, source.Id, playSessionId);
            offset = 0;
            if (source.AddApiKeyToDirectStreamUrl == true && SameOrigin(uri, api.ApiRoot)
                && !HasQueryParameter(uri, "api_key") && !HasQueryParameter(uri, "X-Emby-Token"))
            {
                var auth = api.GetMediaRequestHeaders(uri);
                if (auth.TryGetValue("X-Emby-Token", out var token)) uri = AddQuery(uri, "api_key", token);
            }
        }
        else
        {
            if (source.SupportsTranscoding != true || string.IsNullOrWhiteSpace(source.TranscodingUrl))
                throw new PlaybackException("NoCompatibleTranscode");
            method = PlaybackDeliveryMethod.Transcode;
            uri = api.ResolveMediaUri(source.TranscodingUrl);
            if (!SameOrigin(uri, api.ApiRoot) || source.IsInfiniteStream == true || source.RunTimeTicks is not > 0)
                throw new PlaybackException("UnknownTranscodeTimeline");
            var copyTimestamps = GetQueryParameter(uri, "CopyTimestamps", rejectDuplicates: true);
            if (string.Equals(copyTimestamps, "true", StringComparison.OrdinalIgnoreCase) || copyTimestamps == "1")
                throw new PlaybackException("UnsupportedFormat");
            if (copyTimestamps is not null && !string.Equals(copyTimestamps, "false", StringComparison.OrdinalIgnoreCase) && copyTimestamps != "0")
                throw new PlaybackException("UnexpectedTranscodeTimeline");
            var returnedStart = GetQueryParameter(uri, "StartTimeTicks", rejectDuplicates: true);
            long? returnedStartTicks = null;
            if (returnedStart is not null)
            {
                if (!long.TryParse(returnedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                    throw new PlaybackException("UnexpectedTranscodeTimeline");
                returnedStartTicks = ticks;
            }

            // Alternate versions can share their parent's video route while MediaSourceId selects the actual file.
            // Route identity must stay inside the configured server mount, but need not equal the selected library item ID.
            if (!TryGetVideoResource(api.ApiRoot, uri, out var videoResource))
                throw new PlaybackException("UnknownTranscodeTimeline");
            var staticBytes = GetQueryParameter(uri, "Static", rejectDuplicates: true);
            if (staticBytes is not null && !string.Equals(staticBytes, "false", StringComparison.OrdinalIgnoreCase) && staticBytes != "0")
                throw new PlaybackException("UnknownTranscodeTimeline");
            var isHls = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source.TranscodingSubProtocol, "hls", StringComparison.OrdinalIgnoreCase);
            if (isHls)
            {
                var canonicalPlaylist = videoResource.Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
                    || videoResource.Equals("main.m3u8", StringComparison.OrdinalIgnoreCase);
                if (!canonicalPlaylist || !IsProtocol(source.TranscodingSubProtocol, "hls"))
                    throw new PlaybackException("UnknownTranscodeTimeline");
                // Emby 4.9.5 VOD playlists retain sequence zero and the complete source duration even
                // when StartTimeTicks is nonzero. Keep the negotiated URL intact and seek the engine.
                offset = 0;
            }
            else
            {
                var canonicalProgressive = videoResource.Equals("stream", StringComparison.OrdinalIgnoreCase)
                    || videoResource.StartsWith("stream.", StringComparison.OrdinalIgnoreCase)
                        && videoResource[7..] is { Length: > 0 and <= 16 } extension
                        && extension.All(char.IsAsciiLetterOrDigit);
                if (!canonicalProgressive || !IsProtocol(source.TranscodingSubProtocol, "http"))
                    throw new PlaybackException("UnknownTranscodeTimeline");
                timelineKind = PlaybackTimelineKind.ProgressiveSegment;
                offset = selection.StartPositionTicks;
                initialPosition = 0;
                if (returnedStartTicks.HasValue && returnedStartTicks != offset)
                    throw new PlaybackException("UnexpectedTranscodeTimeline");
                if (!returnedStartTicks.HasValue && offset > 0)
                    uri = AddQuery(uri, "StartTimeTicks", offset.ToString(CultureInfo.InvariantCulture));
            }
        }

        Uri? subtitleUri = null;
        IReadOnlyDictionary<string, string> subtitleHeaders = ReadOnlyDictionary<string, string>.Empty;
        if (subtitleStream is not null && string.Equals(subtitleStream.DeliveryMethod, "External", StringComparison.OrdinalIgnoreCase))
        {
            // Arbitrary third-party subtitle URLs have no established offset contract.
            // A trimmed progressive conversion uses burn-in fallback rather than silently misaligning captions.
            if (method == PlaybackDeliveryMethod.Transcode && offset > 0)
                throw new PlaybackException("UnsupportedSubtitle");
            if (!string.IsNullOrWhiteSpace(subtitleStream.DeliveryUrl))
            {
                subtitleUri = api.ResolveMediaUri(subtitleStream.DeliveryUrl);
            }
            else if (subtitleStream.IsTextSubtitleStream == true)
            {
                subtitleUri = new Uri(api.ApiRoot, $"Videos/{Uri.EscapeDataString(selection.ItemId)}/{Uri.EscapeDataString(source.Id)}/Subtitles/{subtitleStream.Index.ToString(CultureInfo.InvariantCulture)}/Stream.vtt");
            }
            else throw new PlaybackException("UnsupportedSubtitle");
            subtitleHeaders = BuildHeaders(api, subtitleUri, SameOrigin(uri, subtitleUri) ? source.RequiredHttpHeaders : null);
        }

        return new PlaybackEngineRequest
        {
            PlaybackId = id,
            Source = source,
            MediaUri = uri,
            Headers = BuildHeaders(api, uri, source.RequiredHttpHeaders),
            DeliveryMethod = method,
            TimelineKind = timelineKind,
            InitialPositionTicks = initialPosition,
            TimelineOffsetTicks = offset,
            ItemRunTimeTicks = source.IsInfiniteStream == true ? null : source.RunTimeTicks,
            AudioStreamIndex = audio,
            SubtitleStreamIndex = subtitle,
            ExternalSubtitleUri = subtitleUri,
            ExternalSubtitleHeaders = subtitleHeaders
        };
    }

    private static void ValidateTrack(MediaSourceInfo source, int? index, string type)
    {
        if (index is null || type == "Subtitle" && index == -1) return;
        if (index < 0 || !(source.MediaStreams ?? []).Any(stream => stream.Index == index && IsType(stream, type)))
            throw new PlaybackException("InvalidTrackSelection");
    }

    private static bool CanUseDirectStream(MediaSourceInfo source, PlaybackSelection selection)
    {
        if (selection.ForceTranscoding || selection.AudioStreamIndex.HasValue || source.SupportsDirectStream != true) return false;
        var index = selection.SubtitleStreamIndex ?? source.DefaultSubtitleStreamIndex;
        var subtitle = (source.MediaStreams ?? []).FirstOrDefault(stream => index >= 0 && stream.Index == index && IsType(stream, "Subtitle"));
        return !string.Equals(subtitle?.DeliveryMethod, "Encode", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsType(MediaStream stream, string type) => string.Equals(stream.Type, type, StringComparison.OrdinalIgnoreCase);

    private static bool IsProtocol(string? actual, string expected) => string.IsNullOrWhiteSpace(actual)
        || string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryGetVideoResource(Uri apiRoot, Uri uri, out string resource)
    {
        resource = string.Empty;
        var apiPath = apiRoot.AbsolutePath;
        if (!apiPath.EndsWith("/emby/", StringComparison.OrdinalIgnoreCase)) return false;
        // Emby accepts an optional terminal /emby API alias. Removing it must never remove a configured proxy mount.
        // Preserve the mount's path casing; only Emby's route components are case-insensitive.
        var serverMount = apiPath[..^5];
        if (!uri.AbsolutePath.StartsWith(serverMount, StringComparison.Ordinal)) return false;
        var mountedPath = uri.AbsolutePath[serverMount.Length..];
        const string canonicalPrefix = "emby/Videos/";
        const string aliasPrefix = "Videos/";
        var relativeVideoPath = mountedPath.StartsWith(canonicalPrefix, StringComparison.OrdinalIgnoreCase)
            ? mountedPath[canonicalPrefix.Length..]
            : mountedPath.StartsWith(aliasPrefix, StringComparison.OrdinalIgnoreCase) ? mountedPath[aliasPrefix.Length..] : null;
        if (relativeVideoPath is null) return false;
        var segments = relativeVideoPath.Split('/');
        if (segments.Length != 2 || segments[0].Length == 0 || segments[1].Length == 0) return false;
        var routeId = Uri.UnescapeDataString(segments[0]);
        if (routeId is "." or ".." || routeId.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)
            || character is '/' or '\\' or '?' or '#' or '%')) return false;
        resource = segments[1];
        return true;
    }

    private static IReadOnlyDictionary<string, string> BuildHeaders(EmbyApiClient api, Uri uri, Dictionary<string, string>? required)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in required ?? [])
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null
                || pair.Key.Any(char.IsControl) || pair.Value.Any(char.IsControl)) throw new PlaybackException("InvalidMediaHeader");
            if (pair.Key.Equals("X-Emby-Token", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("X-Emby-Authorization", StringComparison.OrdinalIgnoreCase)
                || pair.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            result[pair.Key] = pair.Value;
        }
        foreach (var pair in api.GetMediaRequestHeaders(uri)) result[pair.Key] = pair.Value;
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static bool SameOrigin(Uri first, Uri second) => first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase)
        && first.IdnHost.Equals(second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;

    private static bool HasQueryParameter(Uri uri, string key) => GetQueryParameter(uri, key) is not null;

    private static string? GetQueryParameter(Uri uri, string key, bool rejectDuplicates = false)
    {
        string? value = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.IndexOf('=');
            var name = Uri.UnescapeDataString(split < 0 ? part : part[..split]);
            if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                if (rejectDuplicates && value is not null) throw new PlaybackException("UnexpectedTranscodeTimeline");
                value = split < 0 ? string.Empty : Uri.UnescapeDataString(part[(split + 1)..]);
                if (!rejectDuplicates) return value;
            }
        }
        return value;
    }

    private static Uri AddQuery(Uri uri, string key, string value) => new(uri.AbsoluteUri
        + (uri.Query.Length == 0 ? "?" : "&") + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value));
}
