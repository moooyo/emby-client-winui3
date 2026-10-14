using System.Globalization;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class LumenSystemPreferencesTests
{
    [Fact]
    public async Task Omitted_system_preferences_preserve_existing_behavior_and_leave_notifications_off()
    {
        using var fixture = new PreferenceHarness();
        await File.WriteAllTextAsync(fixture.FilePath, "{}", TestContext.Current.CancellationToken);

        var restored = await fixture.Store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.True(restored.PreventSleepDuringPlayback);
        Assert.True(restored.RememberWindowPlacement);
        Assert.True(restored.AutoReconnect);
        Assert.False(restored.NewEpisodeNotifications);
        Assert.Equal(32, restored.ImageCacheMegabytes);
        Assert.Equal(LumenPreferencePersistenceIssue.None, fixture.Store.LastIssue);
    }

    [Fact]
    public async Task System_switches_and_cache_limit_round_trip_without_touching_credentials()
    {
        using var fixture = new PreferenceHarness();
        var token = TestContext.Current.CancellationToken;
        var accountPath = Path.Combine(fixture.DirectoryPath, "settings.json");
        const string credentials = "Synthetic account data that preferences must not parse.";
        await File.WriteAllTextAsync(accountPath, credentials, token);
        var expected = new LumenPreferences
        {
            PreventSleepDuringPlayback = false,
            RememberWindowPlacement = false,
            AutoReconnect = false,
            NewEpisodeNotifications = true,
            ImageCacheMegabytes = 128
        };

        await fixture.Store.SaveAsync(expected, token);
        var restored = await new LumenPreferenceStore(fixture.FilePath).LoadAsync(token);

        Assert.Equal(expected, restored);
        Assert.Equal(credentials, await File.ReadAllTextAsync(accountPath, token));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(256)]
    [InlineData(512)]
    public async Task Every_cache_limit_offered_by_settings_is_persisted(int megabytes)
    {
        using var fixture = new PreferenceHarness();
        var token = TestContext.Current.CancellationToken;

        await fixture.Store.SaveAsync(new LumenPreferences { ImageCacheMegabytes = megabytes }, token);

        Assert.Equal(megabytes, (await fixture.Store.LoadAsync(token)).ImageCacheMegabytes);
        Assert.Equal(LumenPreferencePersistenceIssue.None, fixture.Store.LastIssue);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(513)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_cache_limits_cannot_replace_the_last_valid_preferences(int megabytes)
    {
        using var fixture = new PreferenceHarness();
        var token = TestContext.Current.CancellationToken;
        await fixture.Store.SaveAsync(new LumenPreferences { ImageCacheMegabytes = 16 }, token);
        var original = await File.ReadAllBytesAsync(fixture.FilePath, token);

        var error = await Assert.ThrowsAsync<LumenPreferenceStoreException>(() =>
            fixture.Store.SaveAsync(new LumenPreferences { ImageCacheMegabytes = megabytes }, token));

        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, error.Issue);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.FilePath, token));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(513)]
    public async Task Invalid_stored_cache_limits_fall_back_without_losing_valid_system_switches(int megabytes)
    {
        using var fixture = new PreferenceHarness();
        var token = TestContext.Current.CancellationToken;
        var json = $$"""
            {"ImageCacheMegabytes": {{megabytes.ToString(CultureInfo.InvariantCulture)}}, "PreventSleepDuringPlayback": false, "RememberWindowPlacement": false, "AutoReconnect": false, "NewEpisodeNotifications": true}
            """;
        await File.WriteAllTextAsync(fixture.FilePath, json, token);

        var restored = await fixture.Store.LoadAsync(token);

        Assert.Equal(32, restored.ImageCacheMegabytes);
        Assert.False(restored.PreventSleepDuringPlayback);
        Assert.False(restored.RememberWindowPlacement);
        Assert.False(restored.AutoReconnect);
        Assert.True(restored.NewEpisodeNotifications);
        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, fixture.Store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.FilePath, token));
    }

    private sealed class PreferenceHarness : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "LumenSystemPreferencesTests", Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(DirectoryPath, "lumen-preferences.json");
        public LumenPreferenceStore Store { get; }

        public PreferenceHarness()
        {
            Directory.CreateDirectory(DirectoryPath);
            Store = new LumenPreferenceStore(FilePath);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
