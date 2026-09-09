using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class WindowPlacementStoreTests
{
    private const string MainWindowKey = "WindowPersistance_MainWindow";

    [Fact]
    public void WinUIEx_dictionary_writes_round_trip_without_reflection_or_account_fields()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        var value = Convert.ToBase64String([1, 2, 3, 4, 5, 6]);
        IDictionary<string, object> store = new WindowPlacementStore(path);

        store[MainWindowKey] = value;
        IDictionary<string, object> restored = new WindowPlacementStore(path);

        Assert.Equal(value, restored[MainWindowKey]);
        Assert.Single(restored);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(value, json.RootElement.GetProperty(MainWindowKey).GetString());
        Assert.False(json.RootElement.TryGetProperty("Accounts", out _));
        Assert.False(json.RootElement.TryGetProperty("AccessToken", out _));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"WindowPersistance_MainWindow\":42}")]
    [InlineData("{\"WindowPersistance_MainWindow\":\"invalid-base64\"}")]
    [InlineData("{\"AccessToken\":\"YWJj\"}")]
    public void Corrupt_window_data_falls_back_to_defaults_without_overwriting_the_input(string content)
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        File.WriteAllText(path, content);

        var store = new WindowPlacementStore(path);

        Assert.Empty(store);
        Assert.Equal(WindowPlacementPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void Invalid_and_oversized_values_do_not_replace_a_valid_placement()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        var store = new WindowPlacementStore(path);
        var original = Convert.ToBase64String([1, 2, 3]);
        store[MainWindowKey] = original;
        var before = File.ReadAllBytes(path);

        store[MainWindowKey] = new object();
        store["AccessToken"] = "synthetic-secret";
        store[MainWindowKey] = Convert.ToBase64String(new byte[32 * 1024]);

        Assert.Equal(WindowPlacementPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(original, store[MainWindowKey]);
        Assert.False(store.ContainsKey("AccessToken"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Excessive_file_size_is_rejected_before_loading_entries()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        File.WriteAllText(path, new string(' ', 129 * 1024));

        var store = new WindowPlacementStore(path);

        Assert.Empty(store);
        Assert.Equal(WindowPlacementPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(129 * 1024, new FileInfo(path).Length);
    }

    [Fact]
    public void A_locked_destination_cannot_crash_close_callbacks_or_replace_the_previous_file()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        var store = new WindowPlacementStore(path);
        var original = Convert.ToBase64String([1, 2, 3]);
        var updated = Convert.ToBase64String([4, 5, 6]);
        store[MainWindowKey] = original;
        var before = File.ReadAllBytes(path);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => store[MainWindowKey] = updated);
            Assert.Null(failure);
            Assert.Equal(WindowPlacementPersistenceIssue.WriteFailed, store.LastIssue);
        }

        Assert.Equal(updated, store[MainWindowKey]);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(original, new WindowPlacementStore(path)[MainWindowKey]);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".window-placement-*.tmp"));
        store[MainWindowKey] = updated;
        Assert.Equal(WindowPlacementPersistenceIssue.None, store.LastIssue);
        Assert.Equal(updated, new WindowPlacementStore(path)[MainWindowKey]);
    }

    [Fact]
    public void An_unwritable_parent_keeps_in_memory_placement_and_reports_failure_without_throwing()
    {
        using var fixture = new TemporaryAccountStore();
        var blockedParent = Path.Combine(fixture.DirectoryPath, "blocked-parent");
        File.WriteAllText(blockedParent, "synthetic file blocks directory creation");
        var store = new WindowPlacementStore(Path.Combine(blockedParent, "window-placement.json"));
        var value = Convert.ToBase64String([1, 2, 3]);

        var failure = Record.Exception(() => store[MainWindowKey] = value);

        Assert.Null(failure);
        Assert.Equal(value, store[MainWindowKey]);
        Assert.Equal(WindowPlacementPersistenceIssue.WriteFailed, store.LastIssue);
    }

    [Fact]
    public void Removing_and_clearing_window_records_persists_the_dictionary_changes()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        var store = new WindowPlacementStore(path);
        store[MainWindowKey] = Convert.ToBase64String([1, 2, 3]);
        store["WindowPersistance_Secondary"] = Convert.ToBase64String([4, 5, 6]);

        Assert.True(store.Remove(MainWindowKey));
        Assert.False(new WindowPlacementStore(path).ContainsKey(MainWindowKey));
        store.Clear();

        Assert.Empty(new WindowPlacementStore(path));
        Assert.Equal(WindowPlacementPersistenceIssue.None, store.LastIssue);
    }
}
