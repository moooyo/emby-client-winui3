using System.Diagnostics;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

public sealed record PublicServerConnection(
    EmbyApiClient Api, PublicSystemInfo Server, IReadOnlyList<UserDto> Users,
    long ResponseMilliseconds, bool UserListUnavailable = false);

public sealed partial class ConnectionService
{
    /// <summary>Resolves a server without sending credentials, then reads its public account list.</summary>
    public async Task<PublicServerConnection> ProbeServerAsync(string address, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var candidates = PublicServerAddresses(address);
        Exception? lastFailure = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var api = CreatePublicClient(candidate.AbsoluteUri);
            PublicSystemInfo server;
            var started = Stopwatch.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                server = await api.GetPublicSystemInfoAsync(deadline.Token);
                RequireServerIdentity(server);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastFailure = new TimeoutException("The server did not respond in time.");
                continue;
            }
            catch (Exception error) when (error is EmbyApiException or EmbyTransportException or EmbyProtocolException or HttpRequestException or TimeoutException)
            {
                lastFailure = error;
                continue;
            }

            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            try
            {
                var users = await api.GetPublicUsersAsync(deadline.Token);
                return new(api, server, users.Where(user => !string.IsNullOrWhiteSpace(user.Id)
                    && !string.IsNullOrWhiteSpace(user.Name) && user.Policy?.IsDisabled != true).ToArray(), elapsed);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new(api, server, [], elapsed, UserListUnavailable: true);
            }
            catch (Exception error) when (error is EmbyApiException or EmbyTransportException or EmbyProtocolException or HttpRequestException or TimeoutException)
            {
                return new(api, server, [], elapsed, UserListUnavailable: true);
            }
        }

        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastFailure
            ?? new EmbyProtocolException("This address did not return an Emby server identity.")).Throw();
        throw new InvalidOperationException("No server connection was returned.");
    }

    private static IReadOnlyList<Uri> PublicServerAddresses(string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var value = address.Trim();
        var hasScheme = value.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(hasScheme ? value : "http://" + value, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("Enter a complete HTTP or HTTPS server address.", nameof(address));

        var authority = value[(hasScheme ? value.IndexOf("://", StringComparison.Ordinal) + 3 : 0)..].Split('/')[0];
        var hasPort = authority.StartsWith('[')
            ? authority.Contains("]:", StringComparison.Ordinal)
            : authority.Count(character => character == ':') == 1;
        if (hasPort) return [uri];

        var result = new List<Uri>();
        if (hasScheme) result.Add(uri);
        if (uri.Scheme == "http") result.Add(new UriBuilder(uri) { Port = 8096 }.Uri);
        result.Add(new UriBuilder(uri) { Scheme = "https", Port = 8920 }.Uri);
        return result.Distinct().ToArray();
    }
}
