using System.Security.Cryptography;
using System.Text;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

public sealed partial class ConnectionService
{
    private async Task<ConnectedSession> SignInCoreAsync(string address, string username, string password,
        bool remember, CancellationToken cancellationToken)
    {
        var publicApi = CreatePublicClient(address);
        var server = await publicApi.GetPublicSystemInfoAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RequireServerIdentity(server);
        var existingTokens = await ReadExistingTokenFingerprintsAsync(cancellationToken);
        var result = await publicApi.AuthenticateByNameAsync(username.Trim(), password, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.AccessToken) || string.IsNullOrWhiteSpace(result.User?.Id))
            throw new EmbyProtocolException("The sign-in response did not contain a user and an access token.");
        var api = publicApi.WithAuthentication(result.AccessToken, result.User.Id);
        var fingerprint = TokenFingerprint(result.AccessToken);
        var mayRevoke = existingTokens is not null && !existingTokens.Contains(fingerprint)
            && !_acceptedTokenFingerprints.Contains(fingerprint);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ServerId is not null && result.ServerId != server.Id)
                throw new EmbyProtocolException("The server identity changed during sign-in. Check the server address.");
            var user = await api.GetCurrentUserAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            RequireUser(user);
            if (user.Id != result.User.Id)
                throw new EmbyProtocolException("The signed-in user does not match the authentication response.");
            var key = AccountStore.CreateAccountKey(server.Id!, user.Id!);
            var registered = await RegisterCapabilitiesAsync(api, result.SessionInfo?.Id, cancellationToken);
            string? warning = IsTemporarySession
                ? "Signed in for this session only. Account details and preferences will not be saved on this device."
                : null;
            try
            {
                if (!IsTemporarySession)
                {
                    var protectedToken = remember
                        ? await _store.ProtectTokenAsync(result.AccessToken, cancellationToken) : "";
                    await CommitAccountAsync(key, new SavedAccount
                    {
                        Key = key, ApiRoot = api.ApiRoot.AbsoluteUri, ServerId = server.Id!,
                        ServerName = server.ServerName ?? "Emby server", UserId = user.Id!,
                        UserName = username.Trim(), ProtectedToken = protectedToken
                    }, cancellationToken);
                }
                else cancellationToken.ThrowIfCancellationRequested();
            }
            catch (AccountStoreException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                warning = "Signed in, but Windows could not save this account. You may need to sign in again next time.";
            }

            // An atomic save, or the final cancellation check for an unsaved session, commits
            // the connection. Cancellation after that boundary must not discard its result.
            _acceptedTokenFingerprints.Add(fingerprint);
            _accountRestrictions.TryRemove(key, out _);
            return new(api, server, user, key, result.SessionInfo?.Id, warning, registered);
        }
        catch
        {
            if (mayRevoke)
            {
                // Keep this bounded and independent of the cancelled request. The connection
                // gate prevents a newer authentication from reusing this token during cleanup.
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await api.LogoutAsync(deadline.Token); }
                catch (Exception error) when (error is EmbyApiException or EmbyProtocolException
                    or EmbyTransportException or TimeoutException or OperationCanceledException or ObjectDisposedException)
                {
                    // Cleanup cannot replace the original connection failure or cancellation.
                }
            }
            throw;
        }
    }

    private async Task<bool> RegisterCapabilitiesAsync(EmbyApiClient api, string? sessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        try
        {
            await api.SetCapabilitiesAsync(sessionId, new ClientCapabilities
            {
                PlayableMediaTypes = ["Video"], SupportsMediaControl = false, SupportsSync = false
            }, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is EmbyApiException or EmbyProtocolException
            or EmbyTransportException or TimeoutException)
        {
            // Playback negotiation remains authoritative when this optional request fails.
            return false;
        }
    }

    private async Task CommitAccountAsync(string key, SavedAccount? account, CancellationToken cancellationToken)
    {
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTemporarySession) return;
            // Take the snapshot while owning the settings gate so another local preference
            // or account removal cannot be overwritten by a connection's stale snapshot.
            var updated = CopySettings();
            if (account is not null)
            {
                updated.Accounts.RemoveAll(saved => saved.Key == key);
                updated.Accounts.Add(account);
            }
            updated.LastAccountKey = key;
            await _store.SaveAsync(updated, cancellationToken);
            Settings = updated;
        }
        finally { _settingsGate.Release(); }
    }

    private async Task<HashSet<string>?> ReadExistingTokenFingerprintsAsync(CancellationToken cancellationToken)
    {
        string[] protectedTokens;
        await _settingsGate.WaitAsync(cancellationToken);
        try
        {
            protectedTokens = Settings.Accounts.Select(account => account.ProtectedToken)
                .Where(token => token.Length > 0).ToArray();
        }
        finally { _settingsGate.Release(); }

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var protectedToken in protectedTokens)
        {
            try
            {
                var token = await _store.UnprotectTokenAsync(protectedToken, cancellationToken);
                fingerprints.Add(TokenFingerprint(token));
            }
            catch (AccountStoreException)
            {
                // An unreadable saved credential could be the returned token. Do not revoke
                // a possibly shared session, and do not prevent password-based recovery.
                return null;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return fingerprints;
    }

    private static string TokenFingerprint(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
