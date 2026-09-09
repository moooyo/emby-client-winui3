using EmbyClient.Api;

namespace EmbyClient.App.Services;

public sealed record ConnectedSession(
    EmbyApiClient Api, PublicSystemInfo Server, UserDto User, string AccountKey, string? SessionId,
    string? PersistenceWarning = null, bool CapabilitiesRegistered = false);

public sealed partial class ConnectionService : IDisposable
{
    private readonly HttpClient _http;
    private readonly AccountStore _store;
    private readonly bool _ownsHttp;

    public ConnectionService(HttpClient? httpClient = null, AccountStore? accountStore = null)
    {
        _http = httpClient ?? EmbyApiClient.CreateHttpClient();
        _store = accountStore ?? new AccountStore();
        _ownsHttp = httpClient is null;
    }

    public AppSettings Settings { get; private set; } = new();

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        Settings = await _store.LoadAsync(cancellationToken);

    public async Task<ConnectedSession> SignInAsync(string address, string username, string password,
        bool remember, CancellationToken cancellationToken)
    {
        var publicApi = CreatePublicClient(address);
        var server = await publicApi.GetPublicSystemInfoAsync(cancellationToken);
        RequireServerIdentity(server);
        var result = await publicApi.AuthenticateByNameAsync(username.Trim(), password, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.User?.Id))
            throw new EmbyProtocolException("The sign-in response did not contain a user and an access token.");
        if (result.ServerId is not null && result.ServerId != server.Id)
            throw new EmbyProtocolException("The server identity changed during sign-in. Check the server address.");
        var api = publicApi.WithAuthentication(result.AccessToken, result.User.Id);
        var user = await api.GetCurrentUserAsync(cancellationToken);
        RequireUser(user);
        var key = AccountStore.CreateAccountKey(server.Id!, user.Id!);
        string? warning = null;
        try
        {
            var protectedToken = remember ? await _store.ProtectTokenAsync(result.AccessToken, cancellationToken) : "";
            var account = new SavedAccount
            {
                Key = key, ApiRoot = api.ApiRoot.AbsoluteUri, ServerId = server.Id!,
                ServerName = server.ServerName ?? "Emby server", UserId = user.Id!,
                UserName = username.Trim(), ProtectedToken = protectedToken
            };
            Settings.Accounts.RemoveAll(x => x.Key == key);
            Settings.Accounts.Add(account);
            Settings.LastAccountKey = key;
            await _store.SaveAsync(Settings, cancellationToken);
        }
        catch (AccountStoreException)
        {
            warning = "Signed in, but Windows could not save this account. You may need to sign in again next time.";
        }
        var registered = false;
        if (result.SessionInfo?.Id is { Length: > 0 } sessionId)
        {
            try
            {
                await api.SetCapabilitiesAsync(sessionId, new ClientCapabilities
                {
                    PlayableMediaTypes = ["Video"], SupportsMediaControl = false, SupportsSync = false
                }, cancellationToken);
                registered = true;
            }
            catch (Exception ex) when (ex is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException)
            {
                // Playback negotiation remains authoritative if optional capability registration is unavailable.
            }
        }
        return new(api, server, user, key, result.SessionInfo?.Id, warning, registered);
    }

    public async Task<ConnectedSession> RestoreAsync(SavedAccount account, CancellationToken cancellationToken)
    {
        var publicApi = CreatePublicClient(account.ApiRoot);
        var server = await publicApi.GetPublicSystemInfoAsync(cancellationToken);
        RequireServerIdentity(server);
        if (server.Id != account.ServerId)
            throw new EmbyProtocolException("This address belongs to a different server. Sign in again to confirm it.");
        var token = await _store.UnprotectTokenAsync(account.ProtectedToken, cancellationToken);
        var api = publicApi.WithAuthentication(token, account.UserId);
        var user = await api.GetCurrentUserAsync(cancellationToken);
        RequireUser(user);
        if (user.Id != account.UserId)
            throw new EmbyProtocolException("The restored user does not match the saved account.");
        Settings.LastAccountKey = account.Key;
        await _store.SaveAsync(Settings, cancellationToken);
        return new(api, server, user, account.Key, null);
    }

    public async Task<string?> SignOutAsync(ConnectedSession session, CancellationToken cancellationToken)
    {
        // Persist the local decision before a slow, cancelled, or unavailable logout request.
        // Never clear account state again after the remote await: a new sign-in may now own it.
        AccountStoreException? localFailure = null;
        try { await ForgetTokenAsync(session.AccountKey); }
        catch (AccountStoreException error) { localFailure = error; }
        var warning = await RevokeSessionAsync(session, cancellationToken);
        if (localFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(localFailure).Throw();
        return warning;
    }

    internal static async Task<string?> RevokeSessionAsync(ConnectedSession session, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await session.Api.LogoutAsync(cancellationToken);
            return null;
        }
        catch (Exception ex) when (ex is EmbyApiException or EmbyTransportException or TimeoutException
            or OperationCanceledException or ObjectDisposedException)
        {
            return "Signed out on this device. The server could not confirm that the previous token was revoked.";
        }
    }

    public async Task SetThemeAsync(string theme)
    {
        Settings.Theme = theme;
        await _store.SaveAsync(Settings);
    }

    public async Task ForgetTokenAsync(string accountKey)
    {
        var account = Settings.Accounts.Find(x => x.Key == accountKey);
        if (account is not null) account.ProtectedToken = "";
        if (Settings.LastAccountKey == accountKey) Settings.LastAccountKey = null;
        await _store.SaveAsync(Settings);
    }

    private EmbyApiClient CreatePublicClient(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri))
            throw new ArgumentException("Enter a complete server address, including http:// or https://.");
        return new(_http, uri, new("EmbyClient.Windows", "Windows PC", Settings.DeviceId, "0.1.0"));
    }

    private static void RequireServerIdentity(PublicSystemInfo server)
    {
        if (string.IsNullOrWhiteSpace(server.Id))
            throw new EmbyProtocolException("This address did not return an Emby server identity.");
    }

    private static void RequireUser(UserDto user)
    {
        if (string.IsNullOrWhiteSpace(user.Id))
            throw new EmbyProtocolException("The server did not return a user identity.");
        if (user.Policy?.IsDisabled == true)
            throw new InvalidOperationException("This Emby account is disabled.");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

public static class UiErrors
{
    public static string Describe(Exception exception) => exception switch
    {
        EmbyApiException { ApplicationErrorCode: "ParentalControl" } =>
            "This account is currently restricted by the server's access rules.",
        EmbyApiException { IsAuthenticationFailure: true } =>
            "Your sign-in was not accepted or has expired. Check your account and sign in again.",
        EmbyApiException { IsPermissionDenied: true } =>
            "Your Emby account does not have permission to do this.",
        EmbyApiException e => $"The server returned HTTP {(int)e.StatusCode}. Try again or check the server.",
        EmbyProtocolException => "The server returned an unexpected response. Check the address and server compatibility.",
        EmbyTransportException => "Could not reach the Emby server. Check the address and your connection.",
        AccountStoreException e => e.Message,
        TimeoutException => "The request timed out. Check your connection and try again.",
        ArgumentException => "Check the server address and required account details.",
        OperationCanceledException => "The operation was cancelled.",
        _ => "The operation could not be completed. Try again."
    };
}
