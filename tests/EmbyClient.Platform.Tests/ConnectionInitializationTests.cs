using System.Net;
using System.Reflection;
using System.Text;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ConnectionInitializationTests
{
    [Fact(Timeout = 15000)]
    public async Task Early_sign_in_waits_for_saved_settings_without_sending_http_or_overwriting_existing_accounts()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(fixture.Store, cancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);
        using var handler = new InitializationHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var gate = StoreGate(fixture.Store);
        await gate.WaitAsync(cancellationToken);
        var initializing = service.InitializeAsync(cancellationToken);
        var signingIn = service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", false, cancellationToken);
        ConnectedSession session;
        try
        {
            Assert.False(initializing.IsCompleted);
            Assert.False(signingIn.IsCompleted);
            Assert.Equal(0, handler.RequestCount);
            Assert.Empty(service.Settings.Accounts);
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
        }
        finally
        {
            gate.Release();
            await initializing;
            session = await signingIn;
        }

        Assert.Equal(seed.DeviceId, session.Api.Identity.DeviceId);
        Assert.Equal(seed.DeviceId, service.Settings.DeviceId);
        Assert.Equal("Dark", service.Settings.Theme);
        Assert.Equal(2, service.Settings.Accounts.Count);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Equal(seed.DeviceId, persisted.DeviceId);
        Assert.Equal("Dark", persisted.Theme);
        Assert.Contains(persisted.Accounts, account => account.Key == seed.Accounts[0].Key
            && account.ProtectedToken == seed.Accounts[0].ProtectedToken);
        Assert.Contains(persisted.Accounts, account => account.Key == session.AccountKey);
        Assert.Equal(session.AccountKey, persisted.LastAccountKey);
    }

    [Fact(Timeout = 15000)]
    public async Task Concurrent_initializers_share_one_settings_instance_and_later_calls_do_not_reload_after_writes()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        using var handler = new InitializationHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var gate = StoreGate(fixture.Store);
        await gate.WaitAsync(cancellationToken);
        async Task<AppSettings> InitializeAndCaptureAsync()
        {
            await service.InitializeAsync(cancellationToken);
            return service.Settings;
        }
        var initializers = Enumerable.Range(0, 8).Select(_ => InitializeAndCaptureAsync()).ToArray();
        AppSettings[] results;
        try { Assert.All(initializers, initialization => Assert.False(initialization.IsCompleted)); }
        finally
        {
            gate.Release();
            results = await Task.WhenAll(initializers);
        }

        var settings = service.Settings;
        Assert.All(results, result => Assert.Same(settings, result));
        await service.SetThemeAsync("Light");
        Assert.Same(settings, service.Settings);
        // A repeated initialization must not access storage or replace an instance after a write.
        using var blocker = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.InitializeAsync(cancellationToken)));
        Assert.Same(settings, service.Settings);
        Assert.Equal("Light", service.Settings.Theme);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory(Timeout = 15000)]
    [InlineData("SignIn")]
    [InlineData("Restore")]
    [InlineData("Theme")]
    [InlineData("Forget")]
    public async Task An_initialization_failure_blocks_operations_without_writes_and_can_be_retried(string operation)
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(fixture.Store, cancellationToken);
        const string damaged = "{ damaged settings";
        await File.WriteAllTextAsync(fixture.SettingsPath, damaged, cancellationToken);
        using var handler = new InitializationHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        var placeholder = service.Settings;

        Task RunOperationAsync() => operation switch
        {
            "SignIn" => service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", false, cancellationToken),
            "Restore" => service.RestoreAsync(seed.Accounts[0], cancellationToken),
            "Theme" => service.SetThemeAsync("Light"),
            "Forget" => service.ForgetTokenAsync(seed.Accounts[0].Key),
            _ => throw new InvalidOperationException("Unknown test operation.")
        };
        var error = await Assert.ThrowsAsync<AccountStoreException>(RunOperationAsync);

        Assert.Equal(AccountStoreError.SettingsCorrupt, error.Error);
        Assert.Same(placeholder, service.Settings);
        Assert.Empty(service.Settings.Accounts);
        Assert.Equal("System", service.Settings.Theme);
        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(damaged, await File.ReadAllTextAsync(fixture.SettingsPath, cancellationToken));

        await fixture.Store.SaveAsync(seed, cancellationToken);
        await RunOperationAsync();

        Assert.NotSame(placeholder, service.Settings);
        Assert.Equal(seed.DeviceId, service.Settings.DeviceId);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Equal(seed.DeviceId, persisted.DeviceId);
        Assert.Equal(operation == "SignIn" ? 2 : 1, persisted.Accounts.Count);
        Assert.Equal(operation == "Theme" ? "Light" : "Dark", persisted.Theme);
        var original = Assert.Single(persisted.Accounts, account => account.Key == seed.Accounts[0].Key);
        if (operation == "Forget")
        {
            Assert.Empty(original.ProtectedToken);
            Assert.Null(persisted.LastAccountKey);
        }
        else Assert.Equal(seed.Accounts[0].ProtectedToken, original.ProtectedToken);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancelling_the_pending_load_prevents_http_and_writes_then_allows_a_same_service_retry()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(fixture.Store, cancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);
        using var handler = new InitializationHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var placeholder = service.Settings;
        var gate = StoreGate(fixture.Store);
        await gate.WaitAsync(cancellationToken);
        try
        {
            var signingIn = service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", false, request.Token);
            Assert.False(signingIn.IsCompleted);
            request.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => signingIn);
            Assert.Same(placeholder, service.Settings);
            Assert.Equal(0, handler.RequestCount);
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
        }
        finally { gate.Release(); }

        var session = await service.SignInAsync("https://synthetic.example", "demo", "synthetic-password", false, cancellationToken);
        Assert.Equal(seed.DeviceId, session.Api.Identity.DeviceId);
        Assert.Equal(2, (await fixture.Store.LoadAsync(cancellationToken)).Accounts.Count);
    }

    [Fact(Timeout = 15000)]
    public async Task A_cancelled_waiter_does_not_cancel_initialization_or_poison_a_loaded_service()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(fixture.Store, cancellationToken);
        using var handler = new InitializationHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        using var waiter = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gate = StoreGate(fixture.Store);
        await gate.WaitAsync(cancellationToken);
        var initializing = service.InitializeAsync(cancellationToken);
        try
        {
            var cancelled = service.InitializeAsync(waiter.Token);
            waiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.False(initializing.IsCompleted);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            gate.Release();
            await initializing;
        }

        var loaded = service.Settings;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InitializeAsync(waiter.Token));
        await service.InitializeAsync(cancellationToken);
        Assert.Same(loaded, service.Settings);
        Assert.Equal(seed.DeviceId, loaded.DeviceId);
    }

    [Fact(Timeout = 15000)]
    public async Task A_disposed_service_can_initialize_local_settings_to_complete_sign_out()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(fixture.Store, cancellationToken);
        using var handler = new InitializationHandler();
        using var http = new HttpClient(handler);
        var account = seed.Accounts[0];
        var api = new EmbyApiClient(http, new Uri(account.ApiRoot),
            new ClientIdentity("Synthetic Client", "Synthetic Device", seed.DeviceId, "1.0"), "synthetic-saved-token", account.UserId);
        var session = new ConnectedSession(api, new PublicSystemInfo { Id = account.ServerId },
            new UserDto { Id = account.UserId }, account.Key, null);
        using var service = new ConnectionService(accountStore: fixture.Store);
        service.Dispose();
        http.Dispose();

        Assert.NotNull(await service.SignOutAsync(session, cancellationToken));

        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        Assert.Equal(seed.DeviceId, persisted.DeviceId);
        Assert.Equal("Dark", persisted.Theme);
        Assert.Empty(Assert.Single(persisted.Accounts).ProtectedToken);
        Assert.Null(persisted.LastAccountKey);
        Assert.Equal(0, handler.RequestCount);
    }

    private static async Task<AppSettings> SeedAsync(AccountStore store, CancellationToken cancellationToken)
    {
        var token = await store.ProtectTokenAsync("synthetic-saved-token", cancellationToken);
        var account = TemporaryAccountStore.Account("server-saved", "user-saved", token);
        account.ApiRoot = "https://synthetic.example/emby/";
        var settings = new AppSettings { Theme = "Dark", Accounts = [account], LastAccountKey = account.Key };
        await store.SaveAsync(settings, cancellationToken);
        return settings;
    }

    private static SemaphoreSlim StoreGate(AccountStore store) =>
        // Hold the real storage lock to make initialization ordering deterministic without
        // introducing an injectable loader or production-only timing hooks.
        Assert.IsType<SemaphoreSlim>(typeof(AccountStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));

    private sealed class InitializationHandler : HttpMessageHandler
    {
        private int _requests;
        public int RequestCount => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            var body = request.RequestUri!.AbsolutePath switch
            {
                "/emby/System/Info/Public" => "{\"Id\":\"server-saved\",\"ServerName\":\"Synthetic Server\"}",
                "/emby/Users/AuthenticateByName" => "{\"AccessToken\":\"synthetic-new-token\",\"ServerId\":\"server-saved\",\"User\":{\"Id\":\"user-new\"}}",
                "/emby/Users/user-new" => "{\"Id\":\"user-new\",\"Name\":\"Synthetic New User\"}",
                "/emby/Users/user-saved" => "{\"Id\":\"user-saved\",\"Name\":\"Synthetic Saved User\"}",
                _ => throw new InvalidOperationException("Unexpected synthetic initialization request.")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
