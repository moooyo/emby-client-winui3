namespace EmbyClient.Api.Tests;

internal sealed class ApiTestContext : IDisposable
{
    public ApiTestContext(string serverAddress = "https://server.example/proxy/emby/", TimeSpan? requestTimeout = null)
    {
        Http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Client = new EmbyApiClient(Http, new Uri(serverAddress), Identity, "token-a", "user-a")
        {
            RequestTimeout = requestTimeout ?? TimeSpan.FromSeconds(5)
        };
    }

    public static ClientIdentity Identity { get; } = new("Windows Native Client", "Windows PC", "device-a", "0.1.0");
    public RecordingHandler Handler { get; } = new();
    public HttpClient Http { get; }
    public EmbyApiClient Client { get; }

    public void ReturnJson(string json) => Handler.RespondAsync = (_, _) => Task.FromResult(RecordingHandler.Json(json));

    public void Dispose() => Http.Dispose();
}
