using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class WindowPlacementPreferenceTests
{
    private const string MainWindowKey = "WindowPersistance_MainWindow";

    [Fact]
    public void Disabled_startup_skips_placement_reads_and_close_writes()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        File.WriteAllText(path, "corrupt placement is ignored while disabled");
        var store = new WindowPlacementStore(path, enabled: false);

        store[MainWindowKey] = Convert.ToBase64String([1, 2, 3]);
        store.Clear();

        Assert.Empty(store);
        Assert.True(store.IsReadOnly);
        Assert.False(store.TryGetValue(MainWindowKey, out _));
        Assert.False(store.Remove(MainWindowKey));
        Assert.Equal(WindowPlacementPersistenceIssue.None, store.LastIssue);
        Assert.Equal("corrupt placement is ignored while disabled", File.ReadAllText(path));
    }

    [Fact]
    public void Runtime_preference_changes_control_saving_without_overwriting_the_last_committed_placement()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        var store = new WindowPlacementStore(path);
        var original = Convert.ToBase64String([1, 2, 3]);
        var replacement = Convert.ToBase64String([4, 5, 6]);
        store[MainWindowKey] = original;
        var before = File.ReadAllBytes(path);

        store.SetEnabled(false);
        store[MainWindowKey] = replacement;

        Assert.False(store.ContainsKey(MainWindowKey));
        Assert.Empty(store.Keys);
        Assert.Empty(store.Values);
        Assert.Equal(before, File.ReadAllBytes(path));
        store.SetEnabled(true);
        Assert.Equal(original, store[MainWindowKey]);
        store[MainWindowKey] = replacement;
        Assert.Equal(replacement, new WindowPlacementStore(path)[MainWindowKey]);
    }

    [Fact]
    public void Enabling_a_store_that_started_disabled_loads_once_and_accepts_current_placement()
    {
        using var fixture = new TemporaryAccountStore();
        var path = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        var original = Convert.ToBase64String([1, 2, 3]);
        new WindowPlacementStore(path)[MainWindowKey] = original;
        var store = new WindowPlacementStore(path, enabled: false);

        store.SetEnabled(true);

        Assert.Equal(original, store[MainWindowKey]);
        Assert.False(store.IsReadOnly);
        store.SetEnabled(true);
        Assert.Equal(original, store[MainWindowKey]);
    }
}
