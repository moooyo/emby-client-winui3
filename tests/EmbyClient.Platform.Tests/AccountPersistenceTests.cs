using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AccountPersistenceTests
{
    [Fact]
    public async Task First_load_persists_a_device_identity_that_survives_a_new_store_instance()
    {
        using var fixture = new TemporaryAccountStore();

        var initial = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(fixture.SettingsPath));
        var again = await new AccountStore(fixture.SettingsPath).LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(Guid.TryParseExact(initial.DeviceId, "N", out var deviceId));
        Assert.NotEqual(Guid.Empty, deviceId);
        Assert.Equal(initial.DeviceId, again.DeviceId);
        Assert.Empty(again.Accounts);
        Assert.Null(again.LastAccountKey);
    }

    [Fact]
    public async Task Repeated_first_loads_on_one_store_share_the_same_persisted_identity()
    {
        using var fixture = new TemporaryAccountStore();

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => fixture.Store.LoadAsync(TestContext.Current.CancellationToken)));

        Assert.Single(results.Select(result => result.DeviceId).Distinct());
        var persisted = await new AccountStore(fixture.SettingsPath).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(results[0].DeviceId, persisted.DeviceId);
    }

    [Fact]
    public async Task Concurrent_first_loads_from_different_store_instances_converge_on_one_device_identity()
    {
        using var fixture = new TemporaryAccountStore();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Enumerable.Range(0, 12).Select(async _ =>
        {
            var store = new AccountStore(fixture.SettingsPath);
            await start.Task.WaitAsync(TestContext.Current.CancellationToken);
            return await store.LoadAsync(TestContext.Current.CancellationToken);
        }).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(pending);

        Assert.Single(results.Select(result => result.DeviceId).Distinct());
        var persisted = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.All(results, result => Assert.Equal(persisted.DeviceId, result.DeviceId));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"DeviceId\":\"invalid-device\",\"Accounts\":[]}")]
    [InlineData("{\"DeviceId\":\"11111111111111111111111111111111\"}")]
    [InlineData("{\"DeviceId\":\"11111111111111111111111111111111\",\"Accounts\":null}")]
    [InlineData("{\"DeviceId\":\"11111111111111111111111111111111\",\"Accounts\":[null]}")]
    [InlineData("{\"DeviceId\":\"11111111111111111111111111111111\",\"Accounts\":[],\"LastAccountKey\":\"absent-account\"}")]
    public async Task Corrupt_settings_are_not_replaced_with_a_fresh_device_or_logged_in_state(string json)
    {
        using var fixture = new TemporaryAccountStore();
        await File.WriteAllTextAsync(fixture.SettingsPath, json, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.SettingsCorrupt, error.Error);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task Duplicate_account_identity_is_rejected_on_save_and_on_load()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken);
        settings.Accounts.Add(TemporaryAccountStore.Account());
        settings.Accounts.Add(TemporaryAccountStore.Account());

        var saveError = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken));
        Assert.Equal(AccountStoreError.SettingsCorrupt, saveError.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));

        var malformed = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);
        await File.WriteAllBytesAsync(fixture.SettingsPath, malformed, TestContext.Current.CancellationToken);
        var loadError = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(AccountStoreError.SettingsCorrupt, loadError.Error);
    }

    [Theory]
    [InlineData("https://user:fake-password@example.test/emby/")]
    [InlineData("https://example.test/emby/?api_key=fake-secret")]
    [InlineData("file:///C:/private/server")]
    public async Task Credential_bearing_or_non_http_endpoints_cannot_enter_saved_settings(string apiRoot)
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var account = TemporaryAccountStore.Account();
        account.ApiRoot = apiRoot;
        settings.Accounts.Add(account);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.SettingsCorrupt, error.Error);
        Assert.DoesNotContain(apiRoot, error.ToString());
        Assert.Empty((await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).Accounts);
    }

    [Fact]
    public async Task Oversized_existing_settings_are_rejected_without_overwriting_the_evidence()
    {
        using var fixture = new TemporaryAccountStore();
        var bytes = new byte[4 * 1024 * 1024 + 1];
        Array.Fill(bytes, (byte)' ');
        await File.WriteAllBytesAsync(fixture.SettingsPath, bytes, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.SettingsCorrupt, error.Error);
        Assert.Equal(bytes.Length, new FileInfo(fixture.SettingsPath).Length);
    }

    [Fact]
    public async Task Oversized_save_keeps_the_previous_file_intact()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken);
        var account = TemporaryAccountStore.Account();
        account.ServerName = new string('x', 4 * 1024 * 1024);
        settings.Accounts.Add(account);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.SettingsWriteFailed, error.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Replacement_failure_keeps_the_original_file_and_removes_the_partial_temporary_file()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken);
        settings.Theme = "Dark";

        using (var locked = new FileStream(fixture.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
                fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken));
            Assert.Equal(AccountStoreError.SettingsWriteFailed, error.Error);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        Assert.Equal("System", (await fixture.Store.LoadAsync(TestContext.Current.CancellationToken)).Theme);
    }

    [Fact]
    public async Task Successful_replacement_persists_the_new_preferences_and_leaves_no_partial_files()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var deviceId = settings.DeviceId;
        settings.Theme = "Light";
        var account = TemporaryAccountStore.Account();
        settings.Accounts.Add(account);
        settings.LastAccountKey = account.Key;

        await fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken);
        var restored = await new AccountStore(fixture.SettingsPath).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Light", restored.Theme);
        Assert.Equal(deviceId, restored.DeviceId);
        Assert.Equal(account.Key, restored.LastAccountKey);
        Assert.Empty(Assert.Single(restored.Accounts).ProtectedToken);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Precanceled_operations_remain_canceled_and_do_not_replace_saved_state()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        settings.Theme = "Dark";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.LoadAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.SaveAsync(settings, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.ProtectTokenAsync("fake-token", canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.UnprotectTokenAsync("fake-ciphertext", canceled.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Precanceled_first_load_does_not_create_settings()
    {
        using var fixture = new TemporaryAccountStore();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.LoadAsync(canceled.Token));

        Assert.False(File.Exists(fixture.SettingsPath));
    }
}
