using System.Net;
using System.Text;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ConnectionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_delayed_old_logout_preserves_new_credentials_for_the_same_account(bool logoutFails)
    {
        using var fixture = new TemporaryAccountStore();
        using var handler = new ConnectionHandler { DelayLogout = true, FailLogout = logoutFails };
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var cancellationToken = TestContext.Current.CancellationToken;
        await service.InitializeAsync(cancellationToken);
        var oldSession = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);

        var leaving = service.SignOutAsync(oldSession, cancellationToken);
        await handler.LogoutStarted.Task.WaitAsync(cancellationToken);
        var pending = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Null(pending.LastAccountKey);
        Assert.Empty(Assert.Single(pending.Accounts).ProtectedToken);
        var newSession = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);
        var newAccount = Assert.Single(service.Settings.Accounts);
        var newProtectedToken = newAccount.ProtectedToken;
        handler.CompleteLogout.TrySetResult();
        var warning = await leaving;

        Assert.Equal("synthetic-token-1", handler.LogoutToken);
        Assert.Equal(newSession.AccountKey, service.Settings.LastAccountKey);
        Assert.Same(newAccount, Assert.Single(service.Settings.Accounts));
        Assert.Equal(newProtectedToken, newAccount.ProtectedToken);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Equal(newSession.AccountKey, persisted.LastAccountKey);
        Assert.Equal("synthetic-token-2", await fixture.Store.UnprotectTokenAsync(
            Assert.Single(persisted.Accounts).ProtectedToken, cancellationToken));
        Assert.Equal(logoutFails, warning is not null);
    }

    [Fact]
    public async Task Logout_removes_its_own_saved_token_even_when_server_revocation_fails()
    {
        using var fixture = new TemporaryAccountStore();
        using var handler = new ConnectionHandler { FailLogout = true };
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var cancellationToken = TestContext.Current.CancellationToken;
        await service.InitializeAsync(cancellationToken);
        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);

        var warning = await service.SignOutAsync(session, cancellationToken);

        Assert.NotNull(warning);
        Assert.Equal("synthetic-token-1", handler.LogoutToken);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Null(persisted.LastAccountKey);
        Assert.Empty(Assert.Single(persisted.Accounts).ProtectedToken);
    }

    [Fact]
    public async Task A_disposed_transport_does_not_prevent_local_sign_out()
    {
        using var fixture = new TemporaryAccountStore();
        using var handler = new ConnectionHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var cancellationToken = TestContext.Current.CancellationToken;
        await service.InitializeAsync(cancellationToken);
        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);
        http.Dispose();

        var warning = await service.SignOutAsync(session, cancellationToken);

        Assert.NotNull(warning);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Null(persisted.LastAccountKey);
        Assert.Empty(Assert.Single(persisted.Accounts).ProtectedToken);
    }

    [Fact]
    public async Task A_cancelled_logout_still_persists_local_sign_out()
    {
        using var fixture = new TemporaryAccountStore();
        using var handler = new ConnectionHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var cancellationToken = TestContext.Current.CancellationToken;
        await service.InitializeAsync(cancellationToken);
        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var warning = await service.SignOutAsync(session, cancelled.Token);

        Assert.NotNull(warning);
        Assert.False(handler.LogoutStarted.Task.IsCompleted);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Null(persisted.LastAccountKey);
        Assert.Empty(Assert.Single(persisted.Accounts).ProtectedToken);
    }

    [Fact]
    public async Task Local_sign_out_keeps_the_api_context_available_until_playback_cleanup_finishes()
    {
        using var fixture = new TemporaryAccountStore();
        using var handler = new ConnectionHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var cancellationToken = TestContext.Current.CancellationToken;
        await service.InitializeAsync(cancellationToken);
        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);

        await service.ForgetTokenAsync(session.AccountKey);

        Assert.False(handler.LogoutStarted.Task.IsCompleted);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Null(persisted.LastAccountKey);
        Assert.Empty(Assert.Single(persisted.Accounts).ProtectedToken);
        await session.Api.GetCurrentUserAsync(cancellationToken);
        Assert.Equal("synthetic-token-1", handler.CurrentUserToken);
        Assert.Null(await ConnectionService.RevokeSessionAsync(session, cancellationToken));
        Assert.Equal("synthetic-token-1", handler.LogoutToken);
    }

    [Fact]
    public async Task A_local_persistence_failure_still_attempts_remote_revocation_and_is_reported()
    {
        using var fixture = new TemporaryAccountStore();
        using var handler = new ConnectionHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var cancellationToken = TestContext.Current.CancellationToken;
        await service.InitializeAsync(cancellationToken);
        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", true, cancellationToken);
        using var blocker = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() => service.SignOutAsync(session, cancellationToken));

        Assert.Equal(AccountStoreError.SettingsWriteFailed, error.Error);
        Assert.Equal("synthetic-token-1", handler.LogoutToken);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Equal(session.AccountKey, persisted.LastAccountKey);
        Assert.NotEmpty(Assert.Single(persisted.Accounts).ProtectedToken);
    }

    private sealed class ConnectionHandler : HttpMessageHandler
    {
        private int _signIns;
        public bool DelayLogout { get; init; }
        public bool FailLogout { get; init; }
        public string? LogoutToken { get; private set; }
        public string? CurrentUserToken { get; private set; }
        public TaskCompletionSource LogoutStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteLogout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/emby/System/Info/Public":
                    return Json("{\"Id\":\"server-a\",\"ServerName\":\"Synthetic Server\"}");
                case "/emby/Users/AuthenticateByName":
                    var token = "synthetic-token-" + Interlocked.Increment(ref _signIns);
                    return Json("{\"AccessToken\":\"" + token + "\",\"ServerId\":\"server-a\",\"User\":{\"Id\":\"user-a\"}}");
                case "/emby/Users/user-a":
                    CurrentUserToken = Assert.Single(request.Headers.GetValues("X-Emby-Token"));
                    return Json("{\"Id\":\"user-a\",\"Name\":\"Synthetic User\"}");
                case "/emby/Sessions/Logout":
                    LogoutToken = Assert.Single(request.Headers.GetValues("X-Emby-Token"));
                    LogoutStarted.TrySetResult();
                    if (DelayLogout) await CompleteLogout.Task.WaitAsync(cancellationToken);
                    if (FailLogout) throw new HttpRequestException("Synthetic network failure.");
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                default:
                    throw new InvalidOperationException("Unexpected synthetic connection request.");
            }
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
