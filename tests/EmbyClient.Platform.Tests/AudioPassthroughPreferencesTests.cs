using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AudioPassthroughPreferencesTests
{
    [Fact]
    public async Task Legacy_preferences_leave_passthrough_off()
    {
        using var fixture = new PreferencesFixture();
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(fixture.FilePath, """{"Theme":"Light"}""", token);

        var restored = await fixture.Store.LoadAsync(token);

        Assert.Equal("Off", restored.AudioPassthrough);
        Assert.Equal("Light", restored.Theme);
        Assert.Equal(LumenPreferencePersistenceIssue.None, fixture.Store.LastIssue);
    }

    [Theory]
    [InlineData("Off")]
    [InlineData("DolbyDts")]
    [InlineData("All")]
    public async Task Every_offered_audio_policy_survives_a_new_store_instance(string policy)
    {
        using var fixture = new PreferencesFixture();
        var token = TestContext.Current.CancellationToken;
        await fixture.Store.SaveAsync(new LumenPreferences { AudioPassthrough = policy }, token);

        var restored = await new LumenPreferenceStore(fixture.FilePath).LoadAsync(token);

        Assert.Equal(policy, restored.AudioPassthrough);
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("\"\"")]
    [InlineData("null")]
    public async Task Invalid_stored_audio_policy_falls_back_without_enabling_passthrough(string jsonValue)
    {
        using var fixture = new PreferencesFixture();
        var token = TestContext.Current.CancellationToken;
        var json = $$"""{"AudioPassthrough":{{jsonValue}},"Theme":"Light"}""";
        await File.WriteAllTextAsync(fixture.FilePath, json, token);

        var restored = await fixture.Store.LoadAsync(token);

        Assert.Equal("Off", restored.AudioPassthrough);
        Assert.Equal("Light", restored.Theme);
        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, fixture.Store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(fixture.FilePath, token));
    }

    [Fact]
    public async Task Unsupported_audio_policy_cannot_replace_a_valid_saved_choice()
    {
        using var fixture = new PreferencesFixture();
        var token = TestContext.Current.CancellationToken;
        await fixture.Store.SaveAsync(new LumenPreferences { AudioPassthrough = "DolbyDts" }, token);
        var original = await File.ReadAllBytesAsync(fixture.FilePath, token);

        var error = await Assert.ThrowsAsync<LumenPreferenceStoreException>(() =>
            fixture.Store.SaveAsync(new LumenPreferences { AudioPassthrough = "Unknown" }, token));

        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, error.Issue);
        Assert.Equal(original, await File.ReadAllBytesAsync(fixture.FilePath, token));
    }

    private sealed class PreferencesFixture : IDisposable
    {
        private string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(),
            nameof(AudioPassthroughPreferencesTests), Guid.NewGuid().ToString("N"));
        public string FilePath => Path.Combine(DirectoryPath, "lumen-preferences.json");
        public LumenPreferenceStore Store { get; }

        public PreferencesFixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            Store = new LumenPreferenceStore(FilePath);
        }

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
