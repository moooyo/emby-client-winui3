using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using EmbyClient.Api;

namespace EmbyClient.Playback.Tests;

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> requests = new();

    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

    public RecordedRequest[] Requests => requests.ToArray();

    public RecordedRequest[] At(string relativePath) => Requests
        .Where(request => request.Uri.AbsolutePath.Equals("/emby/" + relativePath, StringComparison.Ordinal)).ToArray();

    public static HttpResponseMessage PlaybackInfo(PlaybackInfoResponse response) =>
        Json(JsonSerializer.Serialize(response, EmbyJsonContext.Default.PlaybackInfoResponse));

    public static HttpResponseMessage OpenedSource(MediaSourceInfo source) =>
        Json(JsonSerializer.Serialize(new LiveStreamResponse { MediaSource = source }, EmbyJsonContext.Default.LiveStreamResponse));

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("The request URI is missing."),
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        requests.Enqueue(recorded);
        return await RespondAsync(recorded, cancellationToken);
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Body)
{
    public JsonElement JsonBody
    {
        get
        {
            using var document = JsonDocument.Parse(Body ?? throw new InvalidOperationException("The request body is missing."));
            return document.RootElement.Clone();
        }
    }

    public Dictionary<string, string> Query => Uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => System.Uri.UnescapeDataString(part[0]),
            part => part.Length == 2 ? System.Uri.UnescapeDataString(part[1]) : string.Empty,
            StringComparer.OrdinalIgnoreCase);
}
