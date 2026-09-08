using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace EmbyClient.Api.Tests;

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<RecordedRequest> requests = new();

    public Func<RecordedRequest, CancellationToken, Task<HttpResponseMessage>> RespondAsync { get; set; } =
        (_, _) => Task.FromResult(Json("{}"));

    public RecordedRequest[] Requests => requests.ToArray();

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? throw new InvalidOperationException("The request URI is missing."),
            request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            request.Content?.Headers.ContentType?.MediaType,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
        requests.Enqueue(recorded);
        return await RespondAsync(recorded, cancellationToken);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    Dictionary<string, string[]> Headers,
    string? ContentType,
    string? Body)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var values) ? string.Join(",", values) : null;

    public Dictionary<string, string> Query() => Uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(
            part => System.Uri.UnescapeDataString(part[0]),
            part => part.Length == 2 ? System.Uri.UnescapeDataString(part[1]) : string.Empty,
            StringComparer.OrdinalIgnoreCase);
}
