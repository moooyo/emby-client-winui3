using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace EmbyClient.AppState.Tests;

internal sealed class LibraryTestContext : IDisposable
{
    private LibraryTestContext()
    {
        Http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
        Api = new EmbyApiClient(Http, new Uri("https://server.example/emby/"),
            new ClientIdentity("Library State Tests", "Test Device", "device-a", "0.1.0"),
            "token-a", "user-a") { RequestTimeout = TimeSpan.FromSeconds(20) };
        Handler.RespondAsync = request => Task.FromResult(DefaultResponse(request));
    }

    public LibraryViewModel Model { get; } = new();
    public ControlledHandler Handler { get; } = new();
    public HttpClient Http { get; }
    public EmbyApiClient Api { get; }
    public BaseItemDto[] Views { get; set; } =
        [new() { Id = "library-a", Name = "Movies", Type = "CollectionFolder", IsFolder = true }];
    public BaseItemDto[] Catalog { get; set; } = [];
    public BaseItemDto[] Resume { get; set; } = [];
    public BaseItemDto[] NextUp { get; set; } = [];
    public BaseItemDto[] Latest { get; set; } = [];

    public static async Task<LibraryTestContext> CreateAsync(bool populatedHome = false)
    {
        var context = new LibraryTestContext();
        if (populatedHome)
        {
            context.Resume = [Movie("resume-a", "Continue this movie")];
            context.NextUp = [Movie("next-a", "Watch this next")];
            context.Latest = [Movie("latest-a", "Recently added movie")];
        }
        await context.Model.SetSessionAsync(context.Api, "server-a", new UserDto { Id = "user-a" },
            TestContext.Current.CancellationToken);
        Assert.False(context.Model.HasError);
        return context;
    }

    public async Task OpenLibraryAsync(BaseItemDto[] catalog)
    {
        Catalog = catalog;
        await Model.ShowLibraryAsync(Assert.Single(Model.Libraries), TestContext.Current.CancellationToken);
        Assert.False(Model.HasError);
    }

    public HttpResponseMessage DefaultResponse(ObservedRequest request)
    {
        if (request.Is("/Users/user-a/Views")) return Page(Views);
        if (request.Is("/Users/user-a/Items/Resume")) return Page(Resume);
        if (request.Is("/Shows/NextUp")) return Page(NextUp);
        if (request.Is("/Users/user-a/Items/Latest")) return Array(Latest);
        if (request.IsItems)
        {
            var offset = int.Parse(request.Query.GetValueOrDefault("StartIndex", "0"));
            var limit = int.Parse(request.Query.GetValueOrDefault("Limit", "48"));
            return Page(Catalog.Skip(offset).Take(limit).ToArray(), Catalog.Length);
        }
        throw new InvalidOperationException($"Unexpected request: {request.Uri}");
    }

    public static BaseItemDto Movie(string id, string? title = null) =>
        new() { Id = id, Name = title ?? id, Type = "Movie", MediaType = "Video" };

    public static BaseItemDto[] Movies(int count, string titlePrefix = "Original") =>
        Enumerable.Range(0, count).Select(index => Movie($"movie-{index:D3}", $"{titlePrefix} {index:D3}")).ToArray();

    public static HttpResponseMessage Page(BaseItemDto[] items, int? total = null) =>
        Json(JsonSerializer.Serialize(new QueryResult<BaseItemDto>
        {
            Items = items,
            TotalRecordCount = total ?? items.Length
        }, EmbyJsonContext.Default.QueryResultBaseItemDto));

    public static HttpResponseMessage Array(BaseItemDto[] items) =>
        Json(JsonSerializer.Serialize(items, EmbyJsonContext.Default.BaseItemDtoArray));

    public static HttpResponseMessage Item(BaseItemDto item) =>
        Json(JsonSerializer.Serialize(item, EmbyJsonContext.Default.BaseItemDto));

    public static HttpResponseMessage UserData(UserItemDataDto data) =>
        Json(JsonSerializer.Serialize(data, EmbyJsonContext.Default.UserItemDataDto));

    public static HttpResponseMessage Failure() => Json("{}", HttpStatusCode.ServiceUnavailable);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static void AssertSameCards(IReadOnlyList<MediaCardViewModel> expected, IReadOnlyList<MediaCardViewModel> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++) Assert.Same(expected[index], actual[index]);
    }

    public void Dispose()
    {
        Model.ClearSession();
        Http.Dispose();
    }
}

internal sealed class ControlledHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<ObservedRequest> _requests = new();

    public Func<ObservedRequest, Task<HttpResponseMessage>> RespondAsync { get; set; } =
        _ => throw new InvalidOperationException("No response was configured.");
    public ObservedRequest[] Requests => _requests.ToArray();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var observed = new ObservedRequest(request.RequestUri
            ?? throw new InvalidOperationException("The request URI is missing."), cancellationToken);
        _requests.Enqueue(observed);
        return RespondAsync(observed);
    }
}

internal sealed record ObservedRequest(Uri Uri, CancellationToken CancellationToken)
{
    public Dictionary<string, string> Query { get; } = Uri.Query.TrimStart('?')
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2))
        .ToDictionary(part => System.Uri.UnescapeDataString(part[0]),
            part => part.Length == 2 ? System.Uri.UnescapeDataString(part[1]) : string.Empty,
            StringComparer.OrdinalIgnoreCase);

    public bool Is(string suffix) => Uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal);
    public bool IsItems => Is("/Users/user-a/Items");
}

internal sealed class RequestGate : IDisposable
{
    private readonly TaskCompletionSource<ObservedRequest> _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<ObservedRequest> WaitForRequestAsync() =>
        _arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    public Task<HttpResponseMessage> RespondAsync(ObservedRequest request)
    {
        if (!_arrived.TrySetResult(request)) throw new InvalidOperationException("A request gate can accept only one request.");
        // Deliberately release a response even after cancellation to exercise stale owners.
        return _response.Task;
    }

    public void Return(HttpResponseMessage response) => _response.SetResult(response);
    public void Dispose() => _response.TrySetCanceled();
}
