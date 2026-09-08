using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AccountProtectionTests
{
    [Fact]
    public async Task Windows_user_protection_round_trips_a_synthetic_unicode_token_across_store_instances()
    {
        using var fixture = new TemporaryAccountStore();
        var token = $"fake-token-{Guid.NewGuid():N}-\u03B1";

        var protectedToken = await fixture.Store.ProtectTokenAsync(token, TestContext.Current.CancellationToken);
        var anotherStore = new AccountStore(fixture.SettingsPath);
        var restored = await anotherStore.UnprotectTokenAsync(protectedToken, TestContext.Current.CancellationToken);

        Assert.Equal(token, restored);
        Assert.NotEqual(token, protectedToken);
        Assert.DoesNotContain(token, protectedToken);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(token)), protectedToken);
    }

    [Fact]
    public async Task Persisted_accounts_contain_only_protected_tokens_and_restore_their_own_credentials()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var tokens = new[] { $"fake-a-{Guid.NewGuid():N}", $"fake-b-{Guid.NewGuid():N}", $"fake-c-{Guid.NewGuid():N}" };
        var accounts = new[]
        {
            TemporaryAccountStore.Account("server-a", "user-a"),
            TemporaryAccountStore.Account("server-b", "user-a"),
            TemporaryAccountStore.Account("server-a", "user-b")
        };
        for (var index = 0; index < accounts.Length; index++)
        {
            accounts[index].ProtectedToken = await fixture.Store.ProtectTokenAsync(tokens[index],
                TestContext.Current.CancellationToken);
            settings.Accounts.Add(accounts[index]);
        }

        settings.LastAccountKey = accounts[2].Key;
        await fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(fixture.SettingsPath, TestContext.Current.CancellationToken);
        foreach (var token in tokens) Assert.DoesNotContain(token, json);
        using (var document = JsonDocument.Parse(json))
        {
            foreach (var account in document.RootElement.GetProperty("Accounts").EnumerateArray())
            {
                Assert.False(account.TryGetProperty("AccessToken", out _));
                Assert.False(account.TryGetProperty("Password", out _));
                Assert.False(account.TryGetProperty("Pw", out _));
                Assert.NotEmpty(account.GetProperty("ProtectedToken").GetString()!);
            }
        }

        var reloaded = await new AccountStore(fixture.SettingsPath).LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, reloaded.Accounts.Select(account => account.Key).Distinct().Count());
        Assert.Equal(accounts[2].Key, reloaded.LastAccountKey);
        for (var index = 0; index < accounts.Length; index++)
        {
            var saved = Assert.Single(reloaded.Accounts, account => account.Key == accounts[index].Key);
            Assert.Equal(tokens[index], await fixture.Store.UnprotectTokenAsync(saved.ProtectedToken,
                TestContext.Current.CancellationToken));
        }

        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public void Account_keys_are_deterministic_and_separate_both_identity_dimensions_and_ambiguous_delimiters()
    {
        var keys = new[]
        {
            AccountStore.CreateAccountKey("server-a", "user-a"),
            AccountStore.CreateAccountKey("server-b", "user-a"),
            AccountStore.CreateAccountKey("server-a", "user-b"),
            AccountStore.CreateAccountKey("server:user", "identity"),
            AccountStore.CreateAccountKey("server", "user:identity"),
            AccountStore.CreateAccountKey("ab", "c"),
            AccountStore.CreateAccountKey("a", "bc")
        };

        Assert.Equal(keys[0], AccountStore.CreateAccountKey("server-a", "user-a"));
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, key => Assert.DoesNotContain("server", key, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Damaged_DPAPI_payload_is_rejected_without_exposing_the_original_or_ciphertext()
    {
        using var fixture = new TemporaryAccountStore();
        var token = $"fake-secret-{Guid.NewGuid():N}";
        var protectedToken = await fixture.Store.ProtectTokenAsync(token, TestContext.Current.CancellationToken);
        var separator = protectedToken.IndexOf(':');
        var payload = Convert.FromBase64String(protectedToken[(separator + 1)..]);
        payload[^1] ^= 0x7f;
        var damaged = protectedToken[..(separator + 1)] + Convert.ToBase64String(payload);

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.UnprotectTokenAsync(damaged, TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.TokenUnprotectionFailed, error.Error);
        Assert.DoesNotContain(token, error.ToString());
        Assert.DoesNotContain(damaged, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("")]
    [InlineData("cleartext-fake-secret")]
    [InlineData("dpapi-user-v1:not base64")]
    [InlineData("unknown-v2:YWJj")]
    public async Task Invalid_or_unknown_protected_token_formats_fail_closed(string value)
    {
        using var fixture = new TemporaryAccountStore();

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.UnprotectTokenAsync(value, TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.TokenUnprotectionFailed, error.Error);
        if (value.Length > 0) Assert.DoesNotContain(value, error.ToString());
    }

    [Fact]
    public async Task Saving_a_plaintext_token_is_rejected_and_preserves_existing_settings()
    {
        using var fixture = new TemporaryAccountStore();
        var settings = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken);
        var clearToken = $"fake-plaintext-{Guid.NewGuid():N}";
        settings.Accounts.Add(TemporaryAccountStore.Account(protectedToken: clearToken));

        var error = await Assert.ThrowsAsync<AccountStoreException>(() =>
            fixture.Store.SaveAsync(settings, TestContext.Current.CancellationToken));

        Assert.Equal(AccountStoreError.SettingsCorrupt, error.Error);
        Assert.DoesNotContain(clearToken, error.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.SettingsPath, TestContext.Current.CancellationToken));
    }
}
