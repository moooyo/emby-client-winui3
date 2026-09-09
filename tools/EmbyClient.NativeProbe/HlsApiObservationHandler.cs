using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbyClient.NativeProbe;

/// <summary>Records only bounded API operation names, hashed session IDs, positions, and response codes.</summary>
internal sealed partial class HlsApiObservationHandler : DelegatingHandler
{
    private readonly object _gate = new();
    private readonly List<HlsApiEvent> _events = [];
    private readonly List<HlsNegotiationDiagnostic> _negotiations = [];
    private int _completedRequests;

    internal HlsApiObservationHandler() : base(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    }) { }

    internal int CompletedRequests => Volatile.Read(ref _completedRequests);
    internal HlsApiEvent[] Events { get { lock (_gate) return [.. _events]; } }
    internal HlsNegotiationDiagnostic[] Negotiations { get { lock (_gate) return [.. _negotiations]; } }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        HlsApiEvent? observed = null;
        var path = request.RequestUri!.AbsolutePath;
        var operation = path.EndsWith("/Sessions/Playing/Stopped", StringComparison.OrdinalIgnoreCase) ? "Stop"
            : path.EndsWith("/Sessions/Playing/Progress", StringComparison.OrdinalIgnoreCase) ? "Progress"
            : path.EndsWith("/Sessions/Playing", StringComparison.OrdinalIgnoreCase) ? "Start"
            : path.EndsWith("/Videos/ActiveEncodings", StringComparison.OrdinalIgnoreCase) ? "StopEncoding" : null;
        if (operation is "Start" or "Progress" or "Stop")
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
            var root = document.RootElement;
            var session = root.TryGetProperty("PlaySessionId", out var value) ? value.GetString() : null;
            observed = new HlsApiEvent
            {
                Operation = operation, SessionHash = HashSession(session),
                PositionTicks = root.TryGetProperty("PositionTicks", out var position) && position.TryGetInt64(out var ticks) ? ticks : null,
                EventName = root.TryGetProperty("EventName", out var name) ? SafeName(name.GetString()) : null
            };
        }
        else if (operation == "StopEncoding")
            observed = new HlsApiEvent { Operation = operation, SessionHash = HashSession(QueryValue(request.RequestUri, "PlaySessionId")) };
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _completedRequests);
        if (response.IsSuccessStatusCode && path.EndsWith("/PlaybackInfo", StringComparison.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
            if (document.RootElement.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            {
                foreach (var source in sources.EnumerateArray().Take(8))
                {
                    var requestedId = path.Split('/')[^2];
                    var raw = Text(source, "TranscodingUrl");
                    var valid = Uri.TryCreate(raw, UriKind.RelativeOrAbsolute, out var candidate);
                    var resolved = valid ? candidate!.IsAbsoluteUri ? candidate : new Uri(new Uri("http://placeholder.invalid/"), candidate) : null;
                    var copy = resolved is null ? null : QueryValue(resolved, "CopyTimestamps");
                    var start = resolved is null ? null : QueryValue(resolved, "StartTimeTicks");
                    var diagnostic = new HlsNegotiationDiagnostic
                    {
                        Container = SafeProtocol(Text(source, "Container")), Protocol = SafeProtocol(Text(source, "Protocol")),
                        SubProtocol = SafeProtocol(Text(source, "TranscodingSubProtocol")),
                        SourceIdMatchesRequestedItem = Text(source, "Id") == requestedId,
                        SourceItemIdMatchesRequestedItem = Text(source, "ItemId") == requestedId,
                        HasTranscodingUrl = !string.IsNullOrEmpty(raw), UrlWasAbsolute = valid && candidate!.IsAbsoluteUri,
                        SameOrigin = valid && candidate!.IsAbsoluteUri ? candidate.Scheme == request.RequestUri.Scheme
                            && candidate.Host == request.RequestUri.Host && candidate.Port == request.RequestUri.Port : null,
                        PathShape = resolved is null ? "Invalid" : string.Join('/', resolved.AbsolutePath.Split('/').Select(PathPart)),
                        QueryKeys = resolved is null ? [] : resolved.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                            .Select(value => Uri.UnescapeDataString(value.Split('=', 2)[0]))
                            .Select(value => value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? value : "NonStandardKey").ToArray(),
                        CopyTimestamps = copy?.ToLowerInvariant() is "true" or "false" or "0" or "1" ? copy!.ToLowerInvariant() : copy is null ? null : "Other",
                        StartTimeTicks = long.TryParse(start, out var ticks) ? ticks : null
                    };
                    lock (_gate) _negotiations.Add(diagnostic);
                }
            }
        }
        if (observed is not null)
        {
            observed.StatusCode = (int)response.StatusCode;
            lock (_gate)
            {
                if (_events.Count >= 5000) throw new InvalidOperationException("API observation exceeded its bounded event budget.");
                _events.Add(observed);
            }
        }
        return response;
    }

    private static string? HashSession(string? value) => value is null ? null
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? SafeName(string? value) => value is { Length: > 0 and <= 64 }
        && value.All(char.IsAsciiLetterOrDigit) ? value : null;
    private static string? Text(JsonElement source, string name) => source.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string SafeProtocol(string? value) => value?.ToLowerInvariant() is "mp4" or "mkv" or "ts" or "m3u8" or "hls" or "http" or "https" or "file"
        ? value.ToLowerInvariant() : value is null ? "Missing" : "Other";
    private static string PathPart(string value) => value.ToLowerInvariant() is "" or "emby" or "videos" or "master.m3u8" or "main.m3u8" or "stream.m3u8" or "stream" or "hls" or "hls1"
        ? value.ToLowerInvariant() : value.All(char.IsAsciiDigit) ? "{number}"
        : value.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "{playlist}.m3u8"
        : value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ? "{segment}.ts" : "{segment}";
    private static string? QueryValue(Uri uri, string name)
    {
        foreach (var entry in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = entry.Split('=', 2);
            if (pair.Length == 2 && Uri.UnescapeDataString(pair[0]).Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(pair[1]);
        }
        return null;
    }
}
