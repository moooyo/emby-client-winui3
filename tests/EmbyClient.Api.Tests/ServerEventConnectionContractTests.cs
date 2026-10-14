using System.Net;
using System.Net.WebSockets;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class ServerEventConnectionContractTests
{
    [Theory]
    [InlineData("http://server.example", "ws://server.example/embywebsocket")]
    [InlineData("https://server.example/emby/", "wss://server.example/embywebsocket")]
    [InlineData("https://server.example/team/media", "wss://server.example/team/media/embywebsocket")]
    [InlineData("https://server.example/team/media/emby/", "wss://server.example/team/media/embywebsocket")]
    [InlineData("https://server.example/Case%20Sensitive/EmBy/", "wss://server.example/Case%20Sensitive/embywebsocket")]
    [InlineData("http://[::1]:8096/proxy/emby", "ws://[::1]:8096/proxy/embywebsocket")]
    [InlineData("https://server.example:8920/proxy/emby", "wss://server.example:8920/proxy/embywebsocket")]
    public void Event_endpoint_preserves_origin_and_proxy_mount_without_exposing_credentials(string server, string expected)
    {
        using var context = new ApiTestContext(server);

        var endpoint = context.Client.GetServerEventWebSocketEndpoint();

        Assert.Equal(new Uri(expected), endpoint);
        Assert.Equal(string.Empty, endpoint.Query);
        Assert.Equal(string.Empty, endpoint.Fragment);
        Assert.DoesNotContain("token-a", endpoint.AbsoluteUri);
        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public void Event_endpoint_requires_an_authenticated_user_context()
    {
        using var context = new ApiTestContext();
        var publicClient = new EmbyApiClient(context.Http, context.Client.ApiRoot, ApiTestContext.Identity);

        Assert.Throws<InvalidOperationException>(() => publicClient.GetServerEventWebSocketEndpoint());
        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Failed_upgrade_retains_http_classification_and_does_not_expose_credentials(HttpStatusCode status)
    {
        using var context = new ApiTestContext();
        var token = "event secret &?#%";
        var identity = ApiTestContext.Identity with { DeviceId = "device &?#%" };
        var client = new EmbyApiClient(context.Http, context.Client.ApiRoot, identity, token, "user-a");
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("An untrusted upgrade error body must not become a UI message.")
        });
        using var socket = new ClientWebSocket();

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() =>
            client.ConnectServerEventSocketAsync(socket, TestContext.Current.CancellationToken));

        Assert.Equal(status, exception.StatusCode);
        Assert.DoesNotContain(token, exception.ToString());
        Assert.DoesNotContain("untrusted", exception.ToString());
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal("/proxy/embywebsocket", request.Uri.AbsolutePath);
        Assert.Equal(token, request.Query()["api_key"]);
        Assert.Equal(identity.DeviceId, request.Query()["deviceId"]);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.False(context.Http.DefaultRequestHeaders.Contains("X-Emby-Token"));
    }

    [Fact]
    public async Task Parental_control_upgrade_failure_remains_distinct_from_an_expired_credential()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
            response.Headers.Add("X-Application-Error-Code", "ParentalControl");
            return Task.FromResult(response);
        };
        using var socket = new ClientWebSocket();

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() =>
            context.Client.ConnectServerEventSocketAsync(socket, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal("ParentalControl", exception.ApplicationErrorCode);
        Assert.DoesNotContain("token-a", exception.ToString());
    }

    [Fact]
    public async Task Caller_cancellation_preserves_the_cancellation_token_without_leaking_transport_exception_details()
    {
        using var context = new ApiTestContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Handler.RespondAsync = (request, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(request.Uri.AbsoluteUri,
                new Exception("api_key=token-a"), cancellation.Token));
        };
        using var socket = new ClientWebSocket();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Client.ConnectServerEventSocketAsync(socket, cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("token-a", exception.ToString());
        Assert.DoesNotContain("api_key", exception.ToString());
    }
}
