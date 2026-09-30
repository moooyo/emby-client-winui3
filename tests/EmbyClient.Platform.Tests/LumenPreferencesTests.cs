using System.Reflection;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

[CollectionDefinition("Lumen preference application data root", DisableParallelization = true)]
public sealed class LumenPreferenceEnvironmentCollection
{
}

[Collection("Lumen preference application data root")]
public sealed class LumenPreferencesTests
{
    private const int MaximumPreferencesFileBytes = 64 * 1024;

    [Fact]
    public async Task Missing_preferences_return_all_defaults_without_creating_a_file_or_directory()
    {
        using var fixture = new TemporaryAccountStore();
        var directory = Path.Combine(fixture.DirectoryPath, "missing");
        var store = new LumenPreferenceStore(Path.Combine(directory, "lumen-preferences.json"));

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        AssertDefaults(loaded);
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.False(File.Exists(store.FilePath));
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task Every_property_round_trips_without_reflection_or_account_fields()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        const string accountData = "synthetic account data that preferences must not parse";
        await File.WriteAllTextAsync(fixture.SettingsPath, accountData, token);
        var preferences = new LumenPreferences
        {
            Theme = "Light",
            Accent = "Coral",
            HeroRotation = false,
            PosterColumns = 10,
            ShowWatchedMarks = false,
            AutoPlayNext = false,
            NextEpisodeCountdownSeconds = 0,
            IntroSkipMode = "Automatic",
            ResumeMode = "Ask",
            PreferDirectPlay = false,
            LocalMaxBitrate = 0,
            InternetMaxBitrate = 4_000_000,
            HardwareDecoding = false,
            HdrMode = "Always",
            MatchRefreshRate = true,
            SubtitleLanguage = "",
            SubtitleMode = "OnlyForced",
            SubtitleSize = "Small",
            SubtitleOutline = false,
            SubtitlePosition = "BlackBars"
        };

        await store.SaveAsync(preferences, token);
        var restored = await new LumenPreferenceStore(store.FilePath).LoadAsync(token);

        Assert.Equal(preferences, restored);
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Equal(20, json.RootElement.EnumerateObject().Count());
        Assert.False(json.RootElement.TryGetProperty("Accounts", out _));
        Assert.False(json.RootElement.TryGetProperty("AccessToken", out _));
        Assert.False(json.RootElement.TryGetProperty("DeviceId", out _));
        Assert.Equal(accountData, await File.ReadAllTextAsync(fixture.SettingsPath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(6, 0, 0L)]
    [InlineData(7, 5, 1_000_000L)]
    [InlineData(8, 10, 40_000_000L)]
    [InlineData(9, 15, 120_000_000L)]
    [InlineData(10, 15, 1_000_000_000L)]
    public async Task Supported_numeric_boundaries_and_unlimited_bitrate_are_preserved(
        int columns, int countdown, long bitrate)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var preferences = new LumenPreferences
        {
            PosterColumns = columns,
            NextEpisodeCountdownSeconds = countdown,
            LocalMaxBitrate = bitrate,
            InternetMaxBitrate = bitrate
        };

        await store.SaveAsync(preferences, token);

        Assert.Equal(preferences, await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
    }

    [Theory]
    [InlineData("", "", "Default")]
    [InlineData("chi", "chi", "Default")]
    [InlineData("zht", "zht", "Default")]
    [InlineData("eng", "eng", "Default")]
    [InlineData("fra", "fra", "Default")]
    [InlineData("FRA", "fra", "Default")]
    [InlineData("und", "und", "Default")]
    [InlineData("zh-Hant", "zh-hant", "Default")]
    [InlineData("a1-b2", "a1-b2", "Default")]
    [InlineData("ABCDEFGHIJKLMNOP", "abcdefghijklmnop", "Default")]
    [InlineData("fra", "fra", "HearingImpaired")]
    [InlineData("FRA", "fra", "HearingImpaired")]
    public async Task Server_languages_and_modes_are_retained_with_lowercase_language_codes(
        string language, string canonicalLanguage, string mode)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var preferences = new LumenPreferences { SubtitleLanguage = language, SubtitleMode = mode };
        var expected = preferences with { SubtitleLanguage = canonicalLanguage };

        await store.SaveAsync(preferences, token);
        Assert.Equal(expected, await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);

        var original = JsonSerializer.SerializeToUtf8Bytes(preferences,
            LumenPreferencesJsonContext.Default.LumenPreferences);
        await File.WriteAllBytesAsync(store.FilePath, original, token);

        Assert.Equal(expected, await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal(original, await File.ReadAllBytesAsync(store.FilePath, token));
    }

    [Theory]
    [InlineData("{\"Theme\":\"Light\",\"Accent\":\"Blue\",\"IntroSkipMode\":\"Off\",\"ResumeMode\":\"Restart\",\"HdrMode\":\"Off\",\"SubtitleMode\":\"None\",\"SubtitleSize\":\"Large\"}")]
    [InlineData("{\"SubtitleMode\":\"Always\"}")]
    public async Task Other_supported_enum_choices_remain_unchanged_on_load_and_save(string json)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var expected = JsonSerializer.Deserialize(json, LumenPreferencesJsonContext.Default.LumenPreferences)!;
        await File.WriteAllTextAsync(store.FilePath, json, token);

        Assert.Equal(expected, await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        await store.SaveAsync(expected, token);
        Assert.Equal(expected, await new LumenPreferenceStore(store.FilePath).LoadAsync(token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"FuturePreferences\":{\"nested\":[true,42]},\"FutureMode\":\"Experimental\"}")]
    public async Task Missing_known_fields_and_unknown_keys_preserve_defaults_and_forward_compatibility(string json)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await File.WriteAllTextAsync(store.FilePath, json, token);

        AssertDefaults(await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath, token));
    }

    [Fact]
    public async Task Future_fields_do_not_hide_valid_known_preferences_or_trigger_a_write_back()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        const string json = """{"Theme":"Light","Accent":"Sage","FuturePreferences":{"nested":[true,42]}}""";
        await File.WriteAllTextAsync(store.FilePath, json, token);

        var loaded = await store.LoadAsync(token);

        Assert.Equal(new LumenPreferences { Theme = "Light", Accent = "Sage" }, loaded);
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath, token));
    }

    [Theory]
    [InlineData("{\"Theme\":\"System\"}")]
    [InlineData("{\"Theme\":null}")]
    [InlineData("{\"Accent\":\"Unknown\"}")]
    [InlineData("{\"Accent\":null}")]
    [InlineData("{\"PosterColumns\":5}")]
    [InlineData("{\"PosterColumns\":11}")]
    [InlineData("{\"NextEpisodeCountdownSeconds\":-1}")]
    [InlineData("{\"NextEpisodeCountdownSeconds\":1}")]
    [InlineData("{\"NextEpisodeCountdownSeconds\":20}")]
    [InlineData("{\"IntroSkipMode\":\"Always\"}")]
    [InlineData("{\"IntroSkipMode\":null}")]
    [InlineData("{\"ResumeMode\":\"Resume\"}")]
    [InlineData("{\"ResumeMode\":null}")]
    [InlineData("{\"LocalMaxBitrate\":-1}")]
    [InlineData("{\"LocalMaxBitrate\":999999}")]
    [InlineData("{\"LocalMaxBitrate\":1000000001}")]
    [InlineData("{\"InternetMaxBitrate\":-1}")]
    [InlineData("{\"InternetMaxBitrate\":1}")]
    [InlineData("{\"InternetMaxBitrate\":9223372036854775807}")]
    [InlineData("{\"HdrMode\":\"On\"}")]
    [InlineData("{\"HdrMode\":null}")]
    [InlineData("{\"SubtitleLanguage\":\"../private\"}")]
    [InlineData("{\"SubtitleLanguage\":\"chi\\neng\"}")]
    [InlineData("{\"SubtitleLanguage\":\"chi\\u0000\"}")]
    [InlineData("{\"SubtitleLanguage\":\"\\u00e9ng\"}")]
    [InlineData("{\"SubtitleLanguage\":\"-chi\"}")]
    [InlineData("{\"SubtitleLanguage\":\"chi-\"}")]
    [InlineData("{\"SubtitleLanguage\":\"123-4\"}")]
    [InlineData("{\"SubtitleLanguage\":\"abcdefghijklmnopq\"}")]
    [InlineData("{\"SubtitleLanguage\":null}")]
    [InlineData("{\"SubtitleMode\":\"Off\"}")]
    [InlineData("{\"SubtitleMode\":null}")]
    [InlineData("{\"SubtitleSize\":\"Huge\"}")]
    [InlineData("{\"SubtitleSize\":null}")]
    [InlineData("{\"SubtitlePosition\":\"Top\"}")]
    [InlineData("{\"SubtitlePosition\":null}")]
    public async Task Invalid_known_values_fall_back_to_defaults_without_modifying_the_source(string json)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await File.WriteAllTextAsync(store.FilePath, json, token);

        AssertDefaults(await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath, token));
    }

    [Fact]
    public async Task Invalid_known_fields_are_repaired_individually_while_valid_fields_survive()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        const string json = """{"Theme":"Light","Accent":"Unknown","HeroRotation":false,"InternetMaxBitrate":-1}""";
        await File.WriteAllTextAsync(store.FilePath, json, token);

        var loaded = await store.LoadAsync(token);

        Assert.Equal(new LumenPreferences { Theme = "Light", HeroRotation = false }, loaded);
        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath, token));
    }

    [Fact]
    public async Task Case_insensitive_input_is_canonicalized_without_modifying_the_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        const string json = """{"theme":"light","Accent":"sage","subtitlelanguage":"ENG"}""";
        await File.WriteAllTextAsync(store.FilePath, json, token);

        Assert.Equal(new LumenPreferences { Theme = "Light", Accent = "Sage", SubtitleLanguage = "eng" },
            await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath, token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"invalid\"")]
    [InlineData("{\"HeroRotation\":\"true\"}")]
    [InlineData("{\"PosterColumns\":1.5}")]
    public async Task Corrupt_json_returns_defaults_and_an_issue_without_destroying_the_input(string json)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await File.WriteAllTextAsync(store.FilePath, json, token);

        AssertDefaults(await store.LoadAsync(token));
        Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(json, await File.ReadAllTextAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData(MaximumPreferencesFileBytes, false)]
    [InlineData(MaximumPreferencesFileBytes + 1, true)]
    public async Task The_file_size_limit_is_inclusive_and_never_rewrites_oversized_input(int length, bool invalid)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var bytes = new byte[length];
        Array.Fill(bytes, (byte)' ');
        bytes[^2] = (byte)'{';
        bytes[^1] = (byte)'}';
        await File.WriteAllBytesAsync(store.FilePath, bytes, token);

        AssertDefaults(await store.LoadAsync(token));
        Assert.Equal(invalid ? LumenPreferencePersistenceIssue.InvalidData : LumenPreferencePersistenceIssue.None,
            store.LastIssue);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.FilePath, token));
    }

    [Fact]
    public async Task Invalid_saves_are_rejected_before_replacing_valid_preferences()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await store.SaveAsync(new LumenPreferences(), token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);

        foreach (var preferences in InvalidPreferences())
        {
            var error = await Assert.ThrowsAsync<LumenPreferenceStoreException>(() => store.SaveAsync(preferences, token));

            Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, error.Issue);
            Assert.Equal(LumenPreferencePersistenceIssue.InvalidData, store.LastIssue);
            Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
            Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        }

        await store.SaveAsync(new LumenPreferences { Theme = "Light" }, token);
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal("Light", (await store.LoadAsync(token)).Theme);
    }

    [Fact]
    public async Task A_locked_destination_reports_write_failure_and_retains_the_previous_complete_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var original = new LumenPreferences();
        var updated = original with { Theme = "Light", Accent = "Blue" };
        await store.SaveAsync(original, token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);

        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<LumenPreferenceStoreException>(() => store.SaveAsync(updated, token));
            Assert.Equal(LumenPreferencePersistenceIssue.WriteFailed, error.Issue);
            Assert.NotNull(error.InnerException);
            Assert.Equal(LumenPreferencePersistenceIssue.WriteFailed, store.LastIssue);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        Assert.Equal(original, await new LumenPreferenceStore(store.FilePath).LoadAsync(token));
        await store.SaveAsync(updated, token);
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal(updated, await store.LoadAsync(token));
    }

    [Fact]
    public async Task An_unreadable_file_reports_failure_instead_of_silently_returning_defaults()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await store.SaveAsync(new LumenPreferences { Theme = "Light" }, token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);

        using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<LumenPreferenceStoreException>(() => store.LoadAsync(token));
            Assert.Equal(LumenPreferencePersistenceIssue.ReadFailed, error.Issue);
            Assert.NotNull(error.InnerException);
            Assert.Equal(LumenPreferencePersistenceIssue.ReadFailed, store.LastIssue);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Equal("Light", (await store.LoadAsync(token)).Theme);
        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
    }

    [Fact]
    public async Task A_blocked_parent_reports_save_failure_without_modifying_the_blocking_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var parent = Path.Combine(fixture.DirectoryPath, "blocked-parent");
        const string original = "synthetic file blocks directory creation";
        await File.WriteAllTextAsync(parent, original, token);
        var store = new LumenPreferenceStore(Path.Combine(parent, "lumen-preferences.json"));

        var error = await Assert.ThrowsAsync<LumenPreferenceStoreException>(() => store.SaveAsync(new LumenPreferences(), token));

        Assert.Equal(LumenPreferencePersistenceIssue.WriteFailed, error.Issue);
        Assert.Equal(LumenPreferencePersistenceIssue.WriteFailed, store.LastIssue);
        Assert.Equal(original, await File.ReadAllTextAsync(parent, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Precanceled_operations_do_not_create_a_missing_directory_or_preferences_file()
    {
        using var fixture = new TemporaryAccountStore();
        var directory = Path.Combine(fixture.DirectoryPath, "missing");
        var store = new LumenPreferenceStore(Path.Combine(directory, "lumen-preferences.json"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(new LumenPreferences(), canceled.Token));

        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Precanceled_operations_keep_the_original_file_and_persistence_status()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await store.SaveAsync(new LumenPreferences(), token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(new LumenPreferences { Theme = "Light" }, canceled.Token));

        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_while_waiting_for_the_gate_never_releases_an_unowned_slot_or_changes_the_file(bool save)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var updated = new LumenPreferences { Theme = "Light" };
        await store.SaveAsync(new LumenPreferences(), token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var gate = Assert.IsType<SemaphoreSlim>(typeof(LumenPreferenceStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
        await gate.WaitAsync(token);
        try
        {
            Task pending = save ? store.SaveAsync(updated, canceled.Token) : store.LoadAsync(canceled.Token);
            Assert.False(pending.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, gate.CurrentCount);
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        await store.SaveAsync(updated, token);
        Assert.Equal(updated, await store.LoadAsync(token));
    }

    [Fact(Timeout = 15000)]
    public async Task Concurrent_reads_and_writes_share_the_gate_and_only_observe_complete_valid_records()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var variants = Enumerable.Range(0, 16).Select(index => new LumenPreferences
        {
            Theme = index % 2 == 0 ? "Dark" : "Light",
            PosterColumns = 6 + index % 5,
            InternetMaxBitrate = 1_000_000L * (index + 1)
        }).ToArray();

        await Task.WhenAll(variants.Select(async preferences =>
        {
            await store.SaveAsync(preferences, token);
            Assert.Contains(await store.LoadAsync(token), variants);
        }));

        Assert.Equal(LumenPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        var final = new LumenPreferences { Accent = "Sage" };
        await store.SaveAsync(final, token);
        Assert.Equal(final, await new LumenPreferenceStore(store.FilePath).LoadAsync(token));
    }

    [Fact]
    public async Task The_process_data_root_is_used_without_reading_or_writing_neighboring_account_and_window_data()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        const string accountData = "synthetic account file must remain untouched";
        const string placementData = "synthetic placement file must remain untouched";
        var placementPath = Path.Combine(fixture.DirectoryPath, "window-placement.json");
        await File.WriteAllTextAsync(fixture.SettingsPath, accountData, token);
        await File.WriteAllTextAsync(placementPath, placementData, token);
        var priorRoot = Environment.GetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable, fixture.DirectoryPath);
            var store = new LumenPreferenceStore();
            Assert.Equal(Path.Combine(fixture.DirectoryPath, "lumen-preferences.json"), store.FilePath);
            AssertDefaults(await store.LoadAsync(token));
            Assert.False(File.Exists(store.FilePath));
            await store.SaveAsync(new LumenPreferences { Theme = "Light" }, token);
            Assert.Equal("Light", (await new LumenPreferenceStore().LoadAsync(token)).Theme);
            Assert.Equal(accountData, await File.ReadAllTextAsync(fixture.SettingsPath, token));
            Assert.Equal(placementData, await File.ReadAllTextAsync(placementPath, token));
            Assert.Equal(3, Directory.GetFiles(fixture.DirectoryPath).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AppDataPaths.RootEnvironmentVariable, priorRoot);
        }
    }

    private static LumenPreferenceStore CreateStore(TemporaryAccountStore fixture) =>
        new(Path.Combine(fixture.DirectoryPath, "lumen-preferences.json"));

    private static void AssertDefaults(LumenPreferences preferences)
    {
        Assert.Equal("Dark", preferences.Theme);
        Assert.Equal("Gold", preferences.Accent);
        Assert.True(preferences.HeroRotation);
        Assert.Equal(8, preferences.PosterColumns);
        Assert.True(preferences.ShowWatchedMarks);
        Assert.True(preferences.AutoPlayNext);
        Assert.Equal(15, preferences.NextEpisodeCountdownSeconds);
        Assert.Equal("ShowButton", preferences.IntroSkipMode);
        Assert.Equal("Continue", preferences.ResumeMode);
        Assert.True(preferences.PreferDirectPlay);
        Assert.Equal(0L, preferences.LocalMaxBitrate);
        Assert.Equal(20_000_000L, preferences.InternetMaxBitrate);
        Assert.True(preferences.HardwareDecoding);
        Assert.Equal("Auto", preferences.HdrMode);
        Assert.False(preferences.MatchRefreshRate);
        Assert.Equal("chi", preferences.SubtitleLanguage);
        Assert.Equal("Smart", preferences.SubtitleMode);
        Assert.Equal("Medium", preferences.SubtitleSize);
        Assert.True(preferences.SubtitleOutline);
        Assert.Equal("Bottom", preferences.SubtitlePosition);
    }

    private static IEnumerable<LumenPreferences> InvalidPreferences()
    {
        yield return new() { Theme = "System" };
        yield return new() { Theme = "dark" };
        yield return new() { Theme = null! };
        yield return new() { Accent = "Unknown" };
        yield return new() { Accent = null! };
        yield return new() { PosterColumns = 5 };
        yield return new() { PosterColumns = 11 };
        yield return new() { NextEpisodeCountdownSeconds = -1 };
        yield return new() { NextEpisodeCountdownSeconds = 1 };
        yield return new() { NextEpisodeCountdownSeconds = 20 };
        yield return new() { IntroSkipMode = "Always" };
        yield return new() { IntroSkipMode = null! };
        yield return new() { ResumeMode = "Resume" };
        yield return new() { ResumeMode = null! };
        yield return new() { HdrMode = "On" };
        yield return new() { HdrMode = null! };
        yield return new() { SubtitleLanguage = "../private" };
        yield return new() { SubtitleLanguage = "chi\neng" };
        yield return new() { SubtitleLanguage = "chi\0" };
        yield return new() { SubtitleLanguage = "\u00e9ng" };
        yield return new() { SubtitleLanguage = "-chi" };
        yield return new() { SubtitleLanguage = "chi-" };
        yield return new() { SubtitleLanguage = "123-4" };
        yield return new() { SubtitleLanguage = "abcdefghijklmnopq" };
        yield return new() { SubtitleLanguage = null! };
        yield return new() { SubtitleMode = "Off" };
        yield return new() { SubtitleMode = null! };
        yield return new() { SubtitleSize = "Huge" };
        yield return new() { SubtitleSize = null! };
        yield return new() { SubtitlePosition = "Top" };
        yield return new() { SubtitlePosition = null! };
        foreach (var bitrate in new[] { -1L, 999_999L, 1_000_000_001L, long.MaxValue })
        {
            yield return new() { LocalMaxBitrate = bitrate };
            yield return new() { InternetMaxBitrate = bitrate };
        }
    }
}
