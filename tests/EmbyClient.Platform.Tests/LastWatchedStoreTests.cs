using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class LastWatchedStoreTests
{
    private static readonly string AccountA = new('A', 64);
    private static readonly string AccountB = new('B', 64);

    [Fact]
    public async Task Last_watched_items_round_trip_by_account_without_persisting_transport_or_position()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var store = new LastWatchedStore(path);
        var first = Item(AccountA, "123", "First episode");
        var second = Item(AccountB, "123", "Second account episode");

        await store.SaveAsync(first);
        await store.SaveAsync(second);
        var restored = new LastWatchedStore(path);

        Assert.Equal(first, await restored.LoadAsync(AccountA));
        Assert.Equal(second, await restored.LoadAsync(AccountB));
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var properties = json.RootElement.GetProperty(AccountA).EnumerateObject().Select(value => value.Name).Order().ToArray();
        Assert.Equal(new[] { "AccountKey", "ItemId", "Subtitle", "Title", "WatchedAt" }, properties);
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task Signing_out_removes_only_the_matching_account_and_survives_a_new_store_instance()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var store = new LastWatchedStore(path);
        var retained = Item(AccountB, "456", "Retained episode");
        await store.SaveAsync(Item(AccountA, "123", "Removed episode"));
        await store.SaveAsync(retained);

        await store.RemoveAccountAsync(AccountA.ToLowerInvariant());

        var restored = new LastWatchedStore(path);
        Assert.Null(await restored.LoadAsync(AccountA));
        Assert.Equal(retained, await restored.LoadAsync(AccountB));
        Assert.DoesNotContain(AccountA, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Untrusted_ids_cannot_create_persistent_history()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var store = new LastWatchedStore(path);

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Item(AccountA, "https://server?token=secret", "Title")));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(Item("raw-user-id", "123", "Title")));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_title_containing_only_control_characters_uses_a_safe_presentation_fallback()
    {
        Assert.Equal("Lumen", LastWatchedItem.DisplayText("\0\u0001", "Lumen"));
        Assert.Equal(string.Empty, LastWatchedItem.DisplayText("\0\u0001"));
        Assert.Equal(256, LastWatchedItem.DisplayText(new string('x', 300), "Lumen").Length);
    }

    [Fact]
    public async Task Unknown_sensitive_fields_reject_the_input_without_repairing_it_during_load()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var content = $$$"""{"{{{AccountA}}}":{"AccountKey":"{{{AccountA}}}","ItemId":"123","Title":"Title","Subtitle":"","WatchedAt":"2026-10-13T00:00:00+00:00","AccessToken":"synthetic-secret"}}""";
        await File.WriteAllTextAsync(path, content);
        var store = new LastWatchedStore(path);

        Assert.Null(await store.LoadAsync(AccountA));
        Assert.Equal(LastWatchedPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Conflicting_cased_account_keys_do_not_cause_a_dictionary_exception()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var first = JsonSerializer.Serialize(Item(AccountA, "123", "First"),
            LastWatchedJsonContext.Default.LastWatchedItem);
        var second = JsonSerializer.Serialize(Item(AccountA.ToLowerInvariant(), "456", "Second"),
            LastWatchedJsonContext.Default.LastWatchedItem);
        await File.WriteAllTextAsync(path, $"{{\"{AccountA}\":{first},\"{AccountA.ToLowerInvariant()}\":{second}}}");
        var store = new LastWatchedStore(path);

        Assert.Null(await store.LoadAsync(AccountA));
        Assert.Equal(LastWatchedPersistenceIssue.InvalidData, store.LastIssue);
    }

    [Fact]
    public async Task A_failed_write_preserves_the_previous_history_until_a_successful_retry()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var store = new LastWatchedStore(path);
        var original = Item(AccountA, "123", "Original");
        var replacement = Item(AccountA, "456", "Replacement");
        await store.SaveAsync(original);
        var before = await File.ReadAllBytesAsync(path);

        // A directory at the destination blocks atomic replacement on every supported host.
        File.Delete(path);
        Directory.CreateDirectory(path);
        var failure = await Record.ExceptionAsync(() => store.SaveAsync(replacement));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(LastWatchedPersistenceIssue.WriteFailed, store.LastIssue);

        Assert.Equal(original, await store.LoadAsync(AccountA));
        Assert.True(Directory.Exists(path));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".last-watched-*.tmp"));
        Directory.Delete(path);
        await File.WriteAllBytesAsync(path, before);
        await store.SaveAsync(replacement);
        Assert.Equal(replacement, await new LastWatchedStore(path).LoadAsync(AccountA));
    }

    [Fact]
    public async Task A_failed_signout_write_cannot_restore_that_accounts_continuation_in_the_current_process()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var store = new LastWatchedStore(path);
        var retained = Item(AccountB, "456", "Retained");
        await store.SaveAsync(Item(AccountA, "123", "Removed"));
        await store.SaveAsync(retained);
        var before = await File.ReadAllBytesAsync(path);
        File.Delete(path);
        Directory.CreateDirectory(path);

        var failure = await Record.ExceptionAsync(() => store.RemoveAccountAsync(AccountA));

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Null(await store.LoadAsync(AccountA));
        Assert.Equal(retained, await store.LoadAsync(AccountB));
        Directory.Delete(path);
        await File.WriteAllBytesAsync(path, before);
        await store.SaveAsync(retained);
        Assert.Null(await new LastWatchedStore(path).LoadAsync(AccountA));
    }

    [Fact]
    public async Task Account_history_is_bounded_and_evicts_the_oldest_identity()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "last-watched.json");
        var store = new LastWatchedStore(path);
        var baseline = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        for (var index = 0; index < 33; index++)
        {
            var key = index.ToString("X64");
            await store.SaveAsync(Item(key, index.ToString(), $"Episode {index}") with { WatchedAt = baseline.AddMinutes(index) });
        }

        Assert.Null(await store.LoadAsync(0.ToString("X64")));
        Assert.NotNull(await store.LoadAsync(32.ToString("X64")));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Equal(32, json.RootElement.EnumerateObject().Count());
    }

    private static LastWatchedItem Item(string account, string itemId, string title) => new(account, itemId,
        title, "Season 1", new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
}
