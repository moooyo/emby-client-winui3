using System.Reflection;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class SavedAccountRemovalTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Removal_preserves_other_accounts_and_preferences_without_a_server_request(bool removeLastAccount)
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        var seed = await SeedAsync(fixture.Store, cancellationToken);
        using var handler = new RejectRequestsHandler();
        using var http = new HttpClient(handler);
        using var service = new ConnectionService(http, fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var original = service.Settings;
        var removed = original.Accounts[removeLastAccount ? 0 : 1];
        var retained = original.Accounts.Single(account => account.Key != removed.Key);

        await service.RemoveSavedAccountAsync(removed.Key, cancellationToken);

        Assert.NotSame(original, service.Settings);
        Assert.Equal(2, original.Accounts.Count);
        Assert.Same(retained, Assert.Single(service.Settings.Accounts));
        Assert.Equal(seed.DeviceId, service.Settings.DeviceId);
        Assert.Equal("Dark", service.Settings.Theme);
        Assert.Equal(removeLastAccount ? null : seed.LastAccountKey, service.Settings.LastAccountKey);
        var persisted = await fixture.Store.LoadAsync(cancellationToken);
        var saved = Assert.Single(persisted.Accounts);
        Assert.Equal(retained.Key, saved.Key);
        Assert.Equal(retained.ProtectedToken, saved.ProtectedToken);
        Assert.Equal(retained.ServerName, saved.ServerName);
        Assert.Equal(retained.ApiRoot, saved.ApiRoot);
        Assert.Equal(retained.UserName, saved.UserName);
        Assert.Equal(seed.DeviceId, persisted.DeviceId);
        Assert.Equal("Dark", persisted.Theme);
        Assert.Equal(service.Settings.LastAccountKey, persisted.LastAccountKey);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task A_failed_save_keeps_the_original_settings_and_persisted_accounts()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        using var service = new ConnectionService(accountStore: fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var original = service.Settings;
        var removed = original.Accounts[0];
        var retained = original.Accounts[1];
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);
        // The real storage size limit gives a deterministic write failure on every platform.
        retained.ServerName = new string('x', 4 * 1024 * 1024);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            service.RemoveSavedAccountAsync(removed.Key, cancellationToken));

        Assert.Equal(AccountStoreError.SettingsWriteFailed, error.Error);
        Assert.Same(original, service.Settings);
        Assert.Equal(2, original.Accounts.Count);
        Assert.Same(removed, original.Accounts[0]);
        Assert.Same(retained, original.Accounts[1]);
        Assert.Equal(removed.Key, original.LastAccountKey);
        Assert.Equal("dpapi-user-v1:AQID", removed.ProtectedToken);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact(Timeout = 15000)]
    public async Task Cancellation_while_waiting_to_save_keeps_the_original_settings_and_file()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        using var service = new ConnectionService(accountStore: fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var original = service.Settings;
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gate = Assert.IsType<SemaphoreSlim>(typeof(AccountStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Store));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var removal = service.RemoveSavedAccountAsync(original.Accounts[0].Key, cancelled.Token);
            Assert.False(removal.IsCompleted);
            Assert.Same(original, service.Settings);
            Assert.Equal(2, original.Accounts.Count);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removal);
        }
        finally
        {
            gate.Release();
        }

        Assert.Same(original, service.Settings);
        Assert.Equal(2, original.Accounts.Count);
        Assert.Equal(original.Accounts[0].Key, original.LastAccountKey);
        Assert.Equal("dpapi-user-v1:AQID", original.Accounts[0].ProtectedToken);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
    }

    [Fact]
    public async Task Removing_an_unknown_account_is_an_idempotent_local_operation()
    {
        using var fixture = new TemporaryAccountStore();
        var cancellationToken = TestContext.Current.CancellationToken;
        await SeedAsync(fixture.Store, cancellationToken);
        using var service = new ConnectionService(accountStore: fixture.Store);
        await service.InitializeAsync(cancellationToken);
        var original = service.Settings;
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken);

        await service.RemoveSavedAccountAsync("unknown-account", cancellationToken);

        Assert.Same(original, service.Settings);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, cancellationToken));
    }

    private static async Task<AppSettings> SeedAsync(AccountStore store, CancellationToken cancellationToken)
    {
        // These syntactically protected values are never passed to Windows token protection.
        var first = TemporaryAccountStore.Account("server-a", "user-a", "dpapi-user-v1:AQID");
        var second = TemporaryAccountStore.Account("server-b", "user-b", "dpapi-user-v1:BAUG");
        second.ServerName = "Second Test Server";
        second.UserName = "Second Test User";
        var settings = new AppSettings { Theme = "Dark", Accounts = [first, second], LastAccountKey = first.Key };
        await store.SaveAsync(settings, cancellationToken);
        return settings;
    }

    private sealed class RejectRequestsHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("Saved account removal must not contact the server.");
        }
    }
}
