using System.Net;
using System.Net.WebSockets;

namespace EmbyClient.Api;

public sealed partial class EmbyApiClient
{
    /// <summary>Returns the event endpoint without credentials, preserving the configured proxy mount.</summary>
    /// <remarks>
    /// The official JavaScript client replaces the terminal emby/socket route with embywebsocket.
    /// See https://github.com/MediaBrowser/Emby.ApiClient.Javascript/blob/master/apiclient.js.
    /// </remarks>
    public Uri GetServerEventWebSocketEndpoint()
    {
        RequireUserId();
        var endpoint = new Uri(ApiRoot, "../embywebsocket");
        return new UriBuilder(endpoint)
        {
            Scheme = endpoint.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Port = endpoint.IsDefaultPort ? -1 : endpoint.Port
        }.Uri;
    }

    /// <summary>Connects one fresh event socket without exposing the credential-bearing URI.</summary>
    /// <param name="socket">A socket that has not already started connecting.</param>
    /// <param name="cancellationToken">Cancels this connection attempt.</param>
    /// <param name="transport">An optional caller-owned transport with redirects and cookies disabled.</param>
    /// <remarks>
    /// Emby's documented WebSocket authentication uses api_key and deviceId query values.
    /// These values stay inside the connection operation and must never be logged.
    /// See https://dev.emby.media/doc/restapi/Web-Socket.html.
    /// </remarks>
    public async Task ConnectServerEventSocketAsync(ClientWebSocket socket,
        CancellationToken cancellationToken = default, HttpMessageInvoker? transport = null)
    {
        ArgumentNullException.ThrowIfNull(socket);
        cancellationToken.ThrowIfCancellationRequested();
        RequireUserId();
        EnsureSafeDefaultHeaders();
        if (socket.State != WebSocketState.None)
            throw new InvalidOperationException("The event socket must be fresh for each connection attempt.");

        socket.Options.CollectHttpResponseDetails = true;
        var query = new Query().Add("api_key", _accessToken).Add("deviceId", Identity.DeviceId);
        var endpoint = new UriBuilder(GetServerEventWebSocketEndpoint()) { Query = query.ToString() }.Uri;
        using var deadline = CreateTimeout(cancellationToken);
        try
        {
            await socket.ConnectAsync(endpoint, transport ?? _httpClient, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Emby event connection timed out.");
        }
        catch (OperationCanceledException)
        {
            // A custom transport's cancellation exception can also contain the authenticated URI.
            throw new OperationCanceledException("The Emby event connection was canceled.", cancellationToken);
        }
        catch (WebSocketException) { throw ServerEventConnectionFailure(socket); }
        catch (HttpRequestException) { throw ServerEventConnectionFailure(socket); }
        catch (IOException) { throw new EmbyTransportException("The Emby event connection failed."); }
    }

    private static Exception ServerEventConnectionFailure(ClientWebSocket socket)
    {
        // Do not attach the original exception: its message can contain the authenticated URI.
        var status = socket.HttpStatusCode;
        var applicationErrorCode = socket.HttpResponseHeaders is { } headers
            && headers.Any(header => header.Key.Equals("X-Application-Error-Code", StringComparison.OrdinalIgnoreCase)
                && header.Value.Any(value => value.Equals("ParentalControl", StringComparison.Ordinal)))
            ? "ParentalControl" : null;
        return (int)status >= 300
            ? new EmbyApiException(status, applicationErrorCode)
            : new EmbyTransportException("The Emby event connection failed.");
    }
}
