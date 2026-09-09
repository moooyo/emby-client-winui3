using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;

namespace EmbyClient.ServerValidation.ApiProbe;

internal sealed class MediaTransfer(HttpClient transport, EmbyApiClient api)
{
    public async Task<Dictionary<string, string>> ReadRangeAsync(Uri uri, MediaSourceInfo source, CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        using var request = CreateRequest(uri, source.RequiredHttpHeaders);
        request.Headers.Range = new RangeHeaderValue(0, 4095);
        using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        Require(response.StatusCode == HttpStatusCode.PartialContent, "ExpectedHttp206");
        var range = response.Content.Headers.ContentRange;
        Require(range?.Unit == "bytes" && range.From == 0 && range.To is > 0 && range.Length > range.To, "InvalidContentRange");
        var bytes = await ReadBoundedAsync(response.Content, 4096, timeout.Token);
        Require(bytes.Length > 0 && range!.To + 1 == bytes.Length, "RangeLengthMismatch");
        return new()
        {
            ["HttpStatus"] = "206",
            ["BytesRead"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ContentRangeValidated"] = "true",
            ["Authentication"] = "Scoped Emby request headers",
            ["DecodedPlayback"] = "NotRun"
        };
    }

    public async Task<PlaylistResult> ReadHlsAsync(Uri uri, MediaSourceInfo source, CancellationToken cancellationToken)
    {
        for (var depth = 0; depth < 5; depth++)
        {
            using var timeout = RequestTimeout(cancellationToken);
            using var request = CreateRequest(uri, source.RequiredHttpHeaders);
            using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            EnsureSuccess(response);
            var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token);
            var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            Require(text.StartsWith("#EXTM3U", StringComparison.Ordinal), "MissingHlsHeader");
            var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var mediaLine = lines.FirstOrDefault(line => !line.StartsWith('#'));
            Require(mediaLine is not null, "NoHlsMediaReference");
            var child = new Uri(uri, mediaLine!);
            ValidateOrigin(child);
            if (lines.Any(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)))
            {
                uri = child;
                continue;
            }
            Require(lines.Any(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)), "MissingHlsSegmentDuration");
            Require(!lines.Any(line => line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)
                && !line.Contains("METHOD=NONE", StringComparison.Ordinal)), "EncryptedHlsNotSupportedByProbe");
            return new(child, new()
            {
                ["PlaylistsRead"] = (depth + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["MediaPlaylistBytes"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ExtM3UValidated"] = "true",
                ["SegmentReferences"] = lines.Count(line => !line.StartsWith('#')).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["Authentication"] = "Scoped Emby request headers"
            });
        }
        throw new ProbeAssertionException("HlsPlaylistNestingLimit");
    }

    public async Task<Dictionary<string, string>> ReadSegmentAsync(Uri uri, MediaSourceInfo source, CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        using var request = CreateRequest(uri, source.RequiredHttpHeaders);
        using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response);
        var bytes = await ReadBoundedAsync(response.Content, 16 * 1024 * 1024, timeout.Token);
        Require(bytes.Length >= 188, "EmptyOrTruncatedHlsSegment");
        var looksLikeTransportStream = bytes[0] == 0x47 && bytes.Length > 376 && bytes[188] == 0x47 && bytes[376] == 0x47;
        Require(looksLikeTransportStream, "ExpectedMpegTsSegment");
        return new()
        {
            ["HttpStatus"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["BytesRead"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["MpegTsSyncBytesValidated"] = "true",
            ["Authentication"] = "Scoped Emby request headers",
            ["DecodedPlayback"] = "NotRun"
        };
    }

    public async Task<Dictionary<string, string>> ReadSubtitleAsync(Uri uri, MediaSourceInfo source, int streamIndex, CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        using var request = CreateRequest(uri, source.RequiredHttpHeaders);
        using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        Require(string.Equals(contentType, "text/vtt", StringComparison.OrdinalIgnoreCase), "ExpectedWebVttContentType");
        var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token);
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        Require(text.StartsWith("WEBVTT", StringComparison.Ordinal), "MissingWebVttHeader");
        Require(text.Split('\n').Any(line => line.Contains("-->", StringComparison.Ordinal)), "MissingWebVttCue");
        return new()
        {
            ["HttpStatus"] = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["SubtitleStreamIndex"] = streamIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["ContentType"] = "text/vtt",
            ["BytesRead"] = bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["WebVttHeaderAndCueValidated"] = "true",
            ["Authentication"] = "Scoped Emby request headers",
            ["NativeSubtitleRendering"] = "NotRun"
        };
    }

    public async Task<SessionObservation> ReadSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var timeout = RequestTimeout(cancellationToken);
        var uri = new Uri(api.ApiRoot, "Sessions?DeviceId=" + Uri.EscapeDataString(api.Identity.DeviceId));
        using var request = CreateRequest(uri, null);
        using var response = await transport.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        EnsureSuccess(response);
        var bytes = await ReadBoundedAsync(response.Content, 1024 * 1024, timeout.Token);
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        Require(json.RootElement.ValueKind == JsonValueKind.Array, "InvalidSessionsContract");
        foreach (var entry in json.RootElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("Id", out var id) || id.GetString() != sessionId) continue;
            string? itemId = null;
            long? position = null;
            if (entry.TryGetProperty("NowPlayingItem", out var item) && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("Id", out var itemValue)) itemId = itemValue.GetString();
            if (entry.TryGetProperty("PlayState", out var state) && state.ValueKind == JsonValueKind.Object
                && state.TryGetProperty("PositionTicks", out var ticks) && ticks.TryGetInt64(out var value)) position = value;
            return new(true, itemId, position);
        }
        return new(false, null, null);
    }

    private HttpRequestMessage CreateRequest(Uri uri, Dictionary<string, string>? requiredHeaders)
    {
        ValidateOrigin(uri);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var header in requiredHeaders ?? [])
        {
            Require(!string.IsNullOrWhiteSpace(header.Key) && header.Value is not null
                && !header.Key.Any(char.IsControl) && !header.Value.Any(char.IsControl), "InvalidRequiredMediaHeader");
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
                || header.Key.StartsWith("X-Emby-", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        var scopedHeaders = api.GetMediaRequestHeaders(uri);
        Require(scopedHeaders.ContainsKey("X-Emby-Token"), "MissingScopedAuthentication");
        foreach (var header in scopedHeaders) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return request;
    }

    private void ValidateOrigin(Uri uri) => Require(uri.IsAbsoluteUri && uri.Scheme == api.ApiRoot.Scheme
        && uri.Host == "127.0.0.1" && uri.Port == 19096 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0,
        "MediaOriginRejected");

    private static CancellationTokenSource RequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        return timeout;
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        Require(content.Headers.ContentLength is null || content.Headers.ContentLength <= maximum, "ResponseSizeLimit");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            Require(output.Length + read <= maximum, "ResponseSizeLimit");
            output.Write(buffer, 0, read);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw new ProbeHttpException((int)response.StatusCode);
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new ProbeAssertionException(code);
    }
}

internal sealed record PlaylistResult(Uri SegmentUri, Dictionary<string, string> Evidence);
internal sealed record SessionObservation(bool Found, string? ItemId, long? PositionTicks);
