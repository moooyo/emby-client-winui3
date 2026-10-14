using System.Net;
using System.Text;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ServerIdentitySignInTests
{
    [Theory]
    [InlineData("{\"Id\":\"server-b\",\"ServerName\":\"Changed Server\"}")]
    [InlineData("{\"Id\":\"Server-A\",\"ServerName\":\"Changed Server\"}")]
    public async Task A_selected_server_identity_mismatch_rejects_sign_in_before_authentication(string publicInfo)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var seed = await fixture.Store.LoadAsync(token);
        seed.Theme = "Dark";
        var account = TemporaryAccountStore.Account("server-existing", "user-existing");
        seed.Accounts.Add(account);
        seed.LastAccountKey = account.Key;
        await fixture.Store.SaveAsync(seed, token);
        using var handler = new IdentityHandler(publicInfo);
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(token);
        var settings = service.Settings;
        var savedAccount = Assert.Single(settings.Accounts);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, token);

        await Assert.ThrowsAsync<ServerIdentityMismatchException>(() => service.SignInAsync(
            "https://synthetic.example", "demo", "synthetic-password", false, token, expectedServerId: "server-a"));

        AssertOnlyPublicRequest(handler);
        Assert.Same(settings, service.Settings);
        Assert.Same(savedAccount, Assert.Single(service.Settings.Accounts));
        Assert.Equal(seed.DeviceId, service.Settings.DeviceId);
        Assert.Equal("Dark", service.Settings.Theme);
        Assert.Equal(account.Key, service.Settings.LastAccountKey);
        Assert.Equal(account.ServerId, savedAccount.ServerId);
        Assert.Equal(account.UserId, savedAccount.UserId);
        Assert.Empty(savedAccount.ProtectedToken);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("server-a")]
    [InlineData(null)]
    public async Task Matching_identity_and_manual_sign_in_both_allow_authentication(string? expectedServerId)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        using var handler = new IdentityHandler("{\"Id\":\"server-a\",\"ServerName\":\"Synthetic Server\"}");
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);

        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password",
            false, token, expectedServerId: expectedServerId);

        Assert.Equal(new[]
        {
            "GET /emby/System/Info/Public",
            "POST /emby/Users/AuthenticateByName",
            "GET /emby/Users/user-new"
        }, handler.Requests);
        Assert.Equal("server-a", session.Server.Id);
        Assert.Equal("user-new", session.User.Id);
        Assert.True(session.Api.IsAuthenticated);
        var persisted = await fixture.Store.LoadAsync(token);
        var account = Assert.Single(persisted.Accounts);
        Assert.Equal("server-a", account.ServerId);
        Assert.Equal("https://synthetic.example/emby/", account.ApiRoot);
        Assert.Empty(account.ProtectedToken);
        Assert.Equal(session.AccountKey, account.Key);
        Assert.Equal(session.AccountKey, persisted.LastAccountKey);
    }

    [Theory]
    [InlineData("{}", "server-a")]
    [InlineData("{\"Id\":null}", "server-a")]
    [InlineData("{\"Id\":\"\"}", null)]
    [InlineData("{\"Id\":\" \"}", "server-a")]
    public async Task Missing_public_identity_rejects_sign_in_before_authentication(string publicInfo, string? expectedServerId)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        using var handler = new IdentityHandler(publicInfo);
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(token);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, token);

        await Assert.ThrowsAsync<EmbyProtocolException>(() => service.SignInAsync("https://synthetic.example",
            "demo", "synthetic-password", false, token, expectedServerId: expectedServerId));

        AssertOnlyPublicRequest(handler);
        Assert.Empty(service.Settings.Accounts);
        Assert.Null(service.Settings.LastAccountKey);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, token));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task An_empty_selected_identity_is_rejected_before_authentication(string expectedServerId)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        using var handler = new IdentityHandler("{\"Id\":\"server-a\"}");
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(token);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, token);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SignInAsync("https://synthetic.example",
            "demo", "synthetic-password", false, token, expectedServerId: expectedServerId));

        AssertOnlyPublicRequest(handler);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, token));
    }

    private static void AssertOnlyPublicRequest(IdentityHandler handler) =>
        Assert.Equal("GET /emby/System/Info/Public", Assert.Single(handler.Requests));

    private sealed class IdentityHandler(string publicInfo) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            if (path == "/emby/System/Info/Public") Assert.False(request.Headers.Contains("X-Emby-Token"));
            var body = path switch
            {
                "/emby/System/Info/Public" => publicInfo,
                "/emby/Users/AuthenticateByName" =>
                    "{\"AccessToken\":\"synthetic-new-token\",\"ServerId\":\"server-a\",\"User\":{\"Id\":\"user-new\"}}",
                "/emby/Users/user-new" => "{\"Id\":\"user-new\",\"Name\":\"Synthetic User\"}",
                _ => throw new InvalidOperationException("Unexpected synthetic identity request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
