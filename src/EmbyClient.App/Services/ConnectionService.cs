using System.Collections.Concurrent;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

public sealed record ConnectedSession(
    EmbyApiClient Api, PublicSystemInfo Server, UserDto User, string AccountKey, string? SessionId,
    string? PersistenceWarning = null, bool CapabilitiesRegistered = false);

public sealed class AccountPolicyException() : Exception("This Emby account is disabled.") { }

public sealed partial class ConnectionService : IDisposable
{
    private readonly HttpClient _http;
    private readonly AccountStore _store;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly HashSet<string> _acceptedTokenFingerprints = new(StringComparer.Ordinal);
    private bool _settingsLoaded;
    private readonly ConcurrentDictionary<string, string> _accountRestrictions = new(StringComparer.Ordinal);

    public ConnectionService(HttpClient? httpClient = null, AccountStore? accountStore = null)
    {
        _http = httpClient ?? EmbyApiClient.CreateHttpClient();
        _store = accountStore ?? new AccountStore();
        _ownsHttp = httpClient is null;
    }

    public AppSettings Settings { get; private set; } = new();
    public bool IsTemporarySession { get; private set; }

    public string? GetAccountRestriction(string accountKey) => _accountRestrictions.GetValueOrDefault(accountKey);

    public async Task UseTemporarySessionAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_settingsLoaded) return;
            // A temporary session never reads, repairs, or overwrites the unavailable settings.
            Settings = new AppSettings();
            IsTemporarySession = true;
            Volatile.Write(ref _settingsLoaded, true);
        }
        finally { _initializationGate.Release(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _settingsLoaded)) return;
        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_settingsLoaded) return;
            var settings = await _store.LoadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Publish one successfully loaded instance. Later initialization must never replace
            // settings that an authenticated session or a local preference change now owns.
            Settings = settings;
            Volatile.Write(ref _settingsLoaded, true);
        }
        finally { _initializationGate.Release(); }
    }

    public async Task<ConnectedSession> SignInAsync(string address, string username, string password,
        bool remember, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            return await SignInCoreAsync(address, username, password, remember, cancellationToken);
        }
        finally { _connectionGate.Release(); }
    }

    public async Task<ConnectedSession> RestoreAsync(SavedAccount account, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            var protectedToken = account.ProtectedToken;
            try
            {
                return await RestoreCoreAsync(account, cancellationToken);
            }
            catch (Exception error) when (UiErrors.RequiresPassword(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Invalidate only the credential actually used by this attempt. A concurrent
                // local settings operation may already have replaced or removed the record.
                await _settingsGate.WaitAsync(cancellationToken);
                try
                {
                    if (account.ProtectedToken == protectedToken) account.ProtectedToken = string.Empty;
                    var saved = Settings.Accounts.Find(value => value.Key == account.Key);
                    if (saved?.ProtectedToken == protectedToken) saved.ProtectedToken = string.Empty;
                }
                finally { _settingsGate.Release(); }
                throw;
            }
            catch (Exception error) when (UiErrors.IsAccountRestriction(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _accountRestrictions[account.Key] = UiErrors.Describe(error);
                throw;
            }
        }
        finally { _connectionGate.Release(); }
    }

    private async Task<ConnectedSession> RestoreCoreAsync(SavedAccount account, CancellationToken cancellationToken)
    {
        var publicApi = CreatePublicClient(account.ApiRoot);
        var server = await publicApi.GetPublicSystemInfoAsync(cancellationToken);
        RequireServerIdentity(server);
        if (server.Id != account.ServerId)
            throw new EmbyProtocolException("This address belongs to a different server. Sign in again to confirm it.");
        var token = await _store.UnprotectTokenAsync(account.ProtectedToken, cancellationToken);
        var api = publicApi.WithAuthentication(token, account.UserId);
        var user = await api.GetCurrentUserAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RequireUser(user);
        if (user.Id != account.UserId)
            throw new EmbyProtocolException("The restored user does not match the saved account.");
        string? warning = null;
        try
        {
            await CommitAccountAsync(account.Key, null, cancellationToken);
        }
        catch (AccountStoreException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            warning = "Signed in, but the last used account could not be saved on this device.";
        }
        _acceptedTokenFingerprints.Add(TokenFingerprint(token));
        _accountRestrictions.TryRemove(account.Key, out _);
        return new(api, server, user, account.Key, null, warning);
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
        catch (Exception ex) when (ex is EmbyApiException or EmbyProtocolException or EmbyTransportException or TimeoutException
            or OperationCanceledException or ObjectDisposedException)
        {
            return "Signed out on this device. The server could not confirm that the previous token was revoked.";
        }
    }

    public async Task SetThemeAsync(string theme)
    {
        await InitializeAsync();
        await _settingsGate.WaitAsync();
        try
        {
            Settings.Theme = theme;
            if (!IsTemporarySession) await _store.SaveAsync(Settings);
        }
        finally { _settingsGate.Release(); }
    }

    public async Task ForgetTokenAsync(string accountKey)
    {
        await InitializeAsync();
        await _settingsGate.WaitAsync();
        try
        {
            var account = Settings.Accounts.Find(x => x.Key == accountKey);
            if (account is null) return;
            account.ProtectedToken = "";
            if (Settings.LastAccountKey == accountKey) Settings.LastAccountKey = null;
            if (!IsTemporarySession) await _store.SaveAsync(Settings);
        }
        finally { _settingsGate.Release(); }
    }

    public async Task RemoveSavedAccountAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await InitializeAsync(cancellationToken);
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            if (!Settings.Accounts.Exists(account => account.Key == key)) return;
            var updated = CopySettings();
            updated.Accounts.RemoveAll(account => account.Key == key);
            if (updated.LastAccountKey == key) updated.LastAccountKey = null;
            // Publish only after the atomic save succeeds, including late cancellation.
            if (!IsTemporarySession) await _store.SaveAsync(updated, cancellationToken);
            else cancellationToken.ThrowIfCancellationRequested();
            Settings = updated;
            _accountRestrictions.TryRemove(key, out _);
        }
        finally { _settingsGate.Release(); }
    }

    private AppSettings CopySettings() => new()
    {
        DeviceId = Settings.DeviceId,
        Theme = Settings.Theme,
        LastAccountKey = Settings.LastAccountKey,
        Accounts = [.. Settings.Accounts]
    };

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
            throw new AccountPolicyException();
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        // Local sign-out remains available after transport disposal. Do not dispose its
        // initialization gate while another settings operation may still be awaiting it.
    }
}

public static class UiErrors
{
    public static bool IsAccountRestriction(Exception exception) => exception is AccountPolicyException
        or EmbyApiException { ApplicationErrorCode: "ParentalControl" };

    public static bool RequiresPassword(Exception exception) => !IsAccountRestriction(exception)
        && (exception is EmbyApiException { IsAuthenticationFailure: true }
            or AccountStoreException { Error: AccountStoreError.TokenUnprotectionFailed });

    public static bool IsConnectionFailure(Exception exception) => exception is EmbyTransportException or TimeoutException;

    public static string Describe(Exception exception) => exception switch
    {
        AccountPolicyException =>
            "This Emby account is disabled. Choose another account or contact your server administrator.",
        EmbyApiException { ApplicationErrorCode: "ParentalControl" } =>
            "This account is restricted by the server's access rules. Choose another account or check the access schedule with your server administrator.",
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
