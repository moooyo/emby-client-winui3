using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class RequestIsolationTests
{
    [Theory]
    [InlineData("https://server.example", "/emby/System/Info/Public")]
    [InlineData("https://server.example/emby", "/emby/System/Info/Public")]
    [InlineData("https://server.example/emby/", "/emby/System/Info/Public")]
    [InlineData("https://server.example/proxy", "/proxy/emby/System/Info/Public")]
    [InlineData("https://server.example/proxy/emby/", "/proxy/emby/System/Info/Public")]
    [InlineData("https://server.example/proxy%20name/emby", "/proxy%20name/emby/System/Info/Public")]
    public async Task Requests_preserve_the_proxy_prefix_and_do_not_append_emby_twice(string server, string expectedPath)
    {
        using var context = new ApiTestContext(server);

        await context.Client.GetPublicSystemInfoAsync(TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(expectedPath, request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Query);
        Assert.Equal(HttpMethod.Get, request.Method);
    }

    [Fact]
    public async Task User_ids_item_ids_and_query_values_cannot_escape_their_url_components()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"Items\":[],\"TotalRecordCount\":0}");
        var client = context.Client.WithAuthentication("token-b", "user/a?other=1");

        await client.GetItemsAsync(new ItemQuery
        {
            SearchTerm = "title & genre=drama + 100%",
            ParentId = "folder/#?",
            Genres = ["Science Fiction", "Action"],
            IncludeItemTypes = ["Movie", "Episode"]
        }, TestContext.Current.CancellationToken);
        context.ReturnJson("{\"Id\":\"item\"}");
        await client.GetItemAsync("item/segment?#%", TestContext.Current.CancellationToken);

        var requests = context.Handler.Requests;
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fother%3D1/Items", requests[0].Uri.AbsolutePath);
        var query = requests[0].Query();
        Assert.Equal("title & genre=drama + 100%", query["SearchTerm"]);
        Assert.Equal("folder/#?", query["ParentId"]);
        Assert.Equal("Science Fiction|Action", query["Genres"]);
        Assert.Equal("Movie,Episode", query["IncludeItemTypes"]);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fother%3D1/Items/item%2Fsegment%3F%23%25", requests[1].Uri.AbsolutePath);
        Assert.Equal(string.Empty, requests[1].Uri.Query);
        Assert.Equal(string.Empty, requests[1].Uri.Fragment);
    }

    [Fact]
    public async Task Concurrent_account_contexts_keep_tokens_and_user_identity_on_each_request()
    {
        using var context = new ApiTestContext();
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = async (_, cancellationToken) =>
        {
            firstArrived.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return RecordingHandler.Json("{}");
        };
        var second = context.Client.WithAuthentication("token-b", "user-b");

        var firstRequest = context.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken);
        await firstArrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondRequest = second.GetCurrentUserAsync(TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(firstRequest, secondRequest);

        var requests = context.Handler.Requests;
        Assert.Equal(2, requests.Length);
        Assert.Equal("token-a", requests[0].Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", requests[0].Header("X-Emby-Authorization"));
        Assert.Equal("token-b", requests[1].Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-b\"", requests[1].Header("X-Emby-Authorization"));
        Assert.DoesNotContain("token-a", requests[1].Uri.AbsoluteUri);
        Assert.False(context.Http.DefaultRequestHeaders.Contains("X-Emby-Token"));
        Assert.False(context.Http.DefaultRequestHeaders.Contains("X-Emby-Authorization"));
        Assert.Equal("user-a", context.Client.UserId);
    }

    [Fact]
    public async Task Login_and_public_discovery_ignore_the_authenticated_contexts_previous_credentials()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"User\":{\"Id\":\"user-b\"},\"AccessToken\":\"new-token\"}");

        var result = await context.Client.AuthenticateByNameAsync("new user", "password & secret", TestContext.Current.CancellationToken);
        context.ReturnJson("[]");
        await context.Client.GetPublicUsersAsync(TestContext.Current.CancellationToken);
        context.ReturnJson("{}");
        await context.Client.GetPublicSystemInfoAsync(TestContext.Current.CancellationToken);

        Assert.Equal("new-token", result.AccessToken);
        Assert.Equal("user-a", context.Client.UserId);
        Assert.All(context.Handler.Requests, request =>
        {
            Assert.Null(request.Header("X-Emby-Token"));
            Assert.DoesNotContain("UserId=", request.Header("X-Emby-Authorization"));
            Assert.DoesNotContain("token-a", request.Uri.AbsoluteUri);
        });
        var login = context.Handler.Requests[0];
        Assert.Equal(HttpMethod.Post, login.Method);
        Assert.EndsWith("/Users/AuthenticateByName", login.Uri.AbsolutePath);
        Assert.Equal("application/json", login.ContentType);
        using var body = JsonDocument.Parse(Assert.IsType<string>(login.Body));
        Assert.Equal("new user", body.RootElement.GetProperty("Username").GetString());
        Assert.Equal("password & secret", body.RootElement.GetProperty("Pw").GetString());
        Assert.DoesNotContain("password", login.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("Videos/123/stream?Static=true", "https://server.example/proxy/emby/Videos/123/stream?Static=true")]
    [InlineData("/emby/Videos/123/master.m3u8?MediaSourceId=a", "https://server.example/proxy/emby/Videos/123/master.m3u8?MediaSourceId=a")]
    [InlineData("https://cdn.example/movie.m3u8?signed=123", "https://cdn.example/movie.m3u8?signed=123")]
    public void Negotiated_urls_preserve_proxy_routing_and_absolute_origins(string value, string expected)
    {
        using var context = new ApiTestContext();

        Assert.Equal(expected, context.Client.ResolveMediaUri(value).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://cdn.example/movie.m3u8")]
    [InlineData("http://server.example/proxy/emby/Videos/1/stream")]
    [InlineData("https://server.example:8443/proxy/emby/Videos/1/stream")]
    public void Media_authentication_does_not_cross_host_scheme_or_port_boundaries(string destination)
    {
        using var context = new ApiTestContext();

        Assert.Empty(context.Client.GetMediaRequestHeaders(new Uri(destination)));
        Assert.Equal("token-a", context.Client.GetMediaRequestHeaders(context.Client.ApiRoot)["X-Emby-Token"]);
    }

    [Fact]
    public async Task A_shared_http_clients_default_token_cannot_leak_into_an_anonymous_login()
    {
        using var context = new ApiTestContext();
        context.Http.DefaultRequestHeaders.Add("X-Emby-Token", "unsafe-shared-token");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Client.AuthenticateByNameAsync("new user", "password", TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task Identity_quotes_and_backslashes_are_escaped_inside_the_authentication_header()
    {
        using var context = new ApiTestContext();
        var identity = new ClientIdentity("Windows \"Native\"\\Client", "PC", "device-a", "0.1.0");
        var client = new EmbyApiClient(context.Http, context.Client.ApiRoot, identity);

        await client.GetPublicSystemInfoAsync(TestContext.Current.CancellationToken);

        var header = Assert.Single(context.Handler.Requests).Header("X-Emby-Authorization");
        Assert.Contains("Client=\"Windows \\\"Native\\\"\\\\Client\"", header);
    }

    [Theory]
    [InlineData("device\r\nX-Injected: value")]
    [InlineData("device\nvalue")]
    public void Header_control_characters_are_rejected_before_transport(string device)
    {
        using var context = new ApiTestContext();

        Assert.Throws<ArgumentException>(() => new EmbyApiClient(context.Http, context.Client.ApiRoot,
            ApiTestContext.Identity with { Device = device }));

        Assert.Empty(context.Handler.Requests);
    }
}
