using System.Net;
using System.Text;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AccountRecoveryTests
{
    [Fact(Timeout = 15000)]
    public async Task An_explicit_temporary_session_can_sign_in_without_reading_or_overwriting_damaged_settings()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        const string damaged = "{ damaged saved account settings";
        await File.WriteAllTextAsync(fixture.SettingsPath, damaged, cancellationToken);
        using var handler = new RecoveryHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() => service.InitializeAsync(cancellationToken));
        Assert.Equal(AccountStoreError.SettingsCorrupt, error.Error);
        Assert.False(service.IsTemporarySession);

        // Exclusive access also proves that temporary operations do not attempt another load.
        using (var blocker = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await service.UseTemporarySessionAsync(cancellationToken);
            Assert.True(service.IsTemporarySession);
            var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password",
                true, cancellationToken);

            Assert.Equal("user-a", session.User.Id);
            await service.SetThemeAsync("Dark");
            Assert.Equal("Dark", service.Settings.Theme);
            await service.InitializeAsync(cancellationToken);
            Assert.True(service.IsTemporarySession);
            Assert.Null(await service.SignOutAsync(session, cancellationToken));
        }

        Assert.Equal(damaged, await File.ReadAllTextAsync(fixture.SettingsPath, cancellationToken));
        Assert.Equal(1, handler.LogoutRequests);
    }

    [Fact(Timeout = 15000)]
    public async Task An_unauthorized_restored_token_is_removed_from_memory_without_discarding_another_account()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        using var handler = new RecoveryHandler { CurrentUserStatus = HttpStatusCode.Unauthorized };
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var account = Assert.Single(service.Settings.Accounts, saved => saved.UserId == "user-a");
        var other = Assert.Single(service.Settings.Accounts, saved => saved.UserId == "user-other");
        var otherToken = other.ProtectedToken;

        var error = await Assert.ThrowsAsync<EmbyApiException>(() => service.RestoreAsync(account, cancellationToken));

        Assert.True(error.IsAuthenticationFailure);
        Assert.Empty(account.ProtectedToken);
        Assert.Empty(Assert.Single(service.Settings.Accounts, saved => saved.Key == account.Key).ProtectedToken);
        Assert.Equal(2, service.Settings.Accounts.Count);
        Assert.Equal(otherToken, Assert.Single(service.Settings.Accounts, saved => saved.Key == other.Key).ProtectedToken);
    }

    [Fact(Timeout = 15000)]
    public async Task An_unreadable_DPAPI_token_is_removed_from_memory_before_it_can_be_reused()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken, damageToken: true);
        using var handler = new RecoveryHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var account = Assert.Single(service.Settings.Accounts, saved => saved.UserId == "user-a");
        var other = Assert.Single(service.Settings.Accounts, saved => saved.UserId == "user-other");
        var otherToken = other.ProtectedToken;

        var error = await Assert.ThrowsAsync<AccountStoreException>(() => service.RestoreAsync(account, cancellationToken));

        Assert.Equal(AccountStoreError.TokenUnprotectionFailed, error.Error);
        Assert.Empty(account.ProtectedToken);
        Assert.Empty(Assert.Single(service.Settings.Accounts, saved => saved.Key == account.Key).ProtectedToken);
        Assert.Equal(otherToken, other.ProtectedToken);
        Assert.Equal(0, handler.CurrentUserRequests);
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_network_failure_during_restore_preserves_the_saved_token_for_retry(bool failPublicInfo)
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);
        using var handler = new RecoveryHandler
        {
            FailPublicInfo = failPublicInfo,
            FailCurrentUser = !failPublicInfo
        };
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var settings = service.Settings;
        var account = Assert.Single(settings.Accounts, saved => saved.UserId == "user-a");
        var protectedToken = account.ProtectedToken;

        await Assert.ThrowsAsync<EmbyTransportException>(() => service.RestoreAsync(account, cancellationToken));

        Assert.Same(settings, service.Settings);
        Assert.Equal(protectedToken, account.ProtectedToken);
        Assert.Equal(account.Key, service.Settings.LastAccountKey);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_disabled_account_has_an_explicit_policy_error_during_sign_in_or_restore(bool restore)
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        using var handler = new RecoveryHandler { IsDisabled = true };
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var account = Assert.Single(service.Settings.Accounts, saved => saved.UserId == "user-a");

        Task ConnectAsync() => restore
            ? service.RestoreAsync(account, cancellationToken)
            : service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", false, cancellationToken);
        var error = await Assert.ThrowsAsync<AccountPolicyException>(ConnectAsync);
        var description = UiErrors.Describe(error);

        Assert.Contains("disabled", description, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(UiErrors.Describe(new InvalidOperationException("Synthetic failure.")), description);
    }

    [Theory(Timeout = 15000)]
    [InlineData("user-a")]
    [InlineData("user-new")]
    public async Task A_failed_sign_in_save_does_not_publish_new_or_replaced_account_state(string signedInUserId)
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);
        using var handler = new RecoveryHandler { UserId = signedInUserId };
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var settings = service.Settings;
        var originalAccounts = settings.Accounts.ToArray();
        var originalTokens = originalAccounts.Select(account => account.ProtectedToken).ToArray();
        var originalLastAccountKey = settings.LastAccountKey;

        // Allow the assertion reads while preventing atomic replacement of the settings file.
        using var blocker = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var session = await service.SignInAsync("https://synthetic.example", "replacement-name", "synthetic-password",
            true, cancellationToken);

        Assert.Equal(signedInUserId, session.User.Id);
        Assert.False(string.IsNullOrWhiteSpace(session.PersistenceWarning));
        Assert.Same(settings, service.Settings);
        Assert.Equal("Dark", service.Settings.Theme);
        Assert.Equal(originalLastAccountKey, service.Settings.LastAccountKey);
        Assert.Equal(originalAccounts.Length, service.Settings.Accounts.Count);
        for (var index = 0; index < originalAccounts.Length; index++)
        {
            Assert.Same(originalAccounts[index], service.Settings.Accounts[index]);
            Assert.Equal(originalTokens[index], service.Settings.Accounts[index].ProtectedToken);
            Assert.Equal("Test User", service.Settings.Accounts[index].UserName);
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
    }

    private static async Task SeedAsync(AccountStore store, CancellationToken cancellationToken, bool damageToken = false)
    {
        var protectedToken = await store.ProtectTokenAsync("synthetic-saved-token", cancellationToken);
        if (damageToken)
        {
            var separator = protectedToken.IndexOf(':');
            var payload = Convert.FromBase64String(protectedToken[(separator + 1)..]);
            payload[^1] ^= 0x7f;
            protectedToken = protectedToken[..(separator + 1)] + Convert.ToBase64String(payload);
        }
        var account = TemporaryAccountStore.Account(protectedToken: protectedToken);
        account.ApiRoot = "https://synthetic.example/emby/";
        var otherToken = await store.ProtectTokenAsync("synthetic-other-token", cancellationToken);
        var other = TemporaryAccountStore.Account("server-other", "user-other", otherToken);
        await store.SaveAsync(new AppSettings
        {
            Theme = "Dark",
            Accounts = [account, other],
            LastAccountKey = account.Key
        }, cancellationToken);
    }

    private sealed class RecoveryHandler : HttpMessageHandler
    {
        private int _currentUserRequests;
        private int _logoutRequests;
        public string UserId { get; init; } = "user-a";
        public bool IsDisabled { get; init; }
        public bool FailPublicInfo { get; init; }
        public bool FailCurrentUser { get; init; }
        public HttpStatusCode CurrentUserStatus { get; init; } = HttpStatusCode.OK;
        public int CurrentUserRequests => Volatile.Read(ref _currentUserRequests);
        public int LogoutRequests => Volatile.Read(ref _logoutRequests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/emby/System/Info/Public":
                    if (FailPublicInfo) throw new HttpRequestException("Synthetic discovery network failure.");
                    return Task.FromResult(Json("{\"Id\":\"server-a\",\"ServerName\":\"Synthetic Server\"}"));
                case "/emby/Users/AuthenticateByName":
                    return Task.FromResult(Json("{\"AccessToken\":\"synthetic-new-token\",\"ServerId\":\"server-a\",\"User\":{\"Id\":\""
                        + UserId + "\"}}"));
                case var path when path == "/emby/Users/" + UserId:
                    Interlocked.Increment(ref _currentUserRequests);
                    if (FailCurrentUser) throw new HttpRequestException("Synthetic account network failure.");
                    if (CurrentUserStatus != HttpStatusCode.OK)
                        return Task.FromResult(new HttpResponseMessage(CurrentUserStatus));
                    return Task.FromResult(Json("{\"Id\":\"" + UserId
                        + "\",\"Name\":\"Synthetic User\",\"Policy\":{\"IsDisabled\":"
                        + (IsDisabled ? "true" : "false") + "}}"));
                case "/emby/Sessions/Logout":
                    Interlocked.Increment(ref _logoutRequests);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
                default:
                    throw new InvalidOperationException("Unexpected synthetic recovery request.");
            }
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
