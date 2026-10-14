using System.Reflection;
using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackPreferenceStoreTests
{
    [Fact]
    public async Task Missing_preferences_do_not_create_a_file_or_parent_directory()
    {
        using var fixture = new TemporaryAccountStore();
        var directory = Path.Combine(fixture.DirectoryPath, "missing");
        var store = new PlaybackPreferenceStore(Path.Combine(directory, "playback-preferences.json"));

        Assert.Null(await store.LoadAsync(CreateScope(), TestContext.Current.CancellationToken));

        Assert.Equal(PlaybackPreferencePersistenceIssue.None, store.LastIssue);
        Assert.False(Directory.Exists(directory));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task Every_control_and_local_selection_round_trips_without_reflection_or_resume_data()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        const string serverId = "synthetic-private-server";
        const string userId = "synthetic-private-user";
        const string itemId = "synthetic-private-item";
        const string accountData = "synthetic account settings that playback preferences must not change";
        var scope = CreateScope(serverId, userId, itemId);
        var preferences = CreatePreferences(fixture);
        await File.WriteAllTextAsync(fixture.SettingsPath, accountData, token);

        await store.SaveAsync(scope, preferences, token);
        var restored = Assert.IsType<PlaybackPreferences>(
            await new PlaybackPreferenceStore(store.FilePath).LoadAsync(scope, token));

        Assert.True(preferences.ContentEquals(restored));
        Assert.NotSame(preferences.LocalSubtitles, restored.LocalSubtitles);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        Assert.Equal(PlaybackPreferencePersistenceIssue.None, store.LastIssue);
        var content = await File.ReadAllTextAsync(store.FilePath, token);
        Assert.DoesNotContain(serverId, content);
        Assert.DoesNotContain(userId, content);
        Assert.DoesNotContain(itemId, content);
        Assert.DoesNotContain("synthetic subtitle content", content);
        using var document = JsonDocument.Parse(content);
        Assert.Equal(1, document.RootElement.GetProperty("Version").GetInt32());
        var entry = Assert.Single(document.RootElement.GetProperty("Entries").EnumerateArray());
        Assert.Equal(scope.Key, entry.GetProperty("Key").GetString());
        var persisted = entry.GetProperty("Preferences");
        Assert.Equal(8, persisted.EnumerateObject().Count());
        Assert.False(persisted.TryGetProperty("PositionTicks", out _));
        Assert.False(persisted.TryGetProperty("StartPositionTicks", out _));
        Assert.False(persisted.TryGetProperty("PlaybackPositionTicks", out _));
        Assert.False(persisted.TryGetProperty("SubtitleStreamIndex", out _));
        Assert.False(persisted.TryGetProperty("AudioStreamIndex", out _));
        Assert.False(persisted.TryGetProperty("AccessToken", out _));
        Assert.Equal(accountData, await File.ReadAllTextAsync(fixture.SettingsPath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Fact]
    public void Scopes_are_deterministic_and_separate_all_dimensions_and_ambiguous_separators()
    {
        var scopes = new[]
        {
            CreateScope("server-a", "user-a", "item-a"),
            CreateScope("server-b", "user-a", "item-a"),
            CreateScope("server-a", "user-b", "item-a"),
            CreateScope("server-a", "user-a", "item-b"),
            CreateScope("server:user", "identity", "item"),
            CreateScope("server", "user:identity", "item"),
            CreateScope("server", "user:item", "identity"),
            CreateScope("server", "user", "item:identity"),
            CreateScope("ab", "c", "d"),
            CreateScope("a", "bc", "d"),
            CreateScope("a", "b", "cd")
        };

        Assert.Equal(scopes[0], CreateScope("server-a", "user-a", "item-a"));
        Assert.Equal(scopes.Length, scopes.Select(scope => scope.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(scopes, scope =>
        {
            Assert.Equal(64, scope.Key.Length);
            Assert.All(scope.Key, character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid\nidentity")]
    [InlineData("invalid\0identity")]
    public void Invalid_identity_in_any_dimension_cannot_create_a_scope(string? identity)
    {
        Assert.False(PlaybackPreferenceScope.TryCreate(identity, "user", "item", out var serverScope));
        Assert.Null(serverScope);
        Assert.False(PlaybackPreferenceScope.TryCreate("server", identity, "item", out var userScope));
        Assert.Null(userScope);
        Assert.False(PlaybackPreferenceScope.TryCreate("server", "user", identity, out var itemScope));
        Assert.Null(itemScope);
    }

    [Fact]
    public void Identity_length_boundary_is_applied_to_each_dimension()
    {
        var accepted = new string('a', 256);
        var rejected = accepted + "a";
        Assert.True(PlaybackPreferenceScope.TryCreate(accepted, accepted, accepted, out _));
        Assert.False(PlaybackPreferenceScope.TryCreate(rejected, "user", "item", out _));
        Assert.False(PlaybackPreferenceScope.TryCreate("server", rejected, "item", out _));
        Assert.False(PlaybackPreferenceScope.TryCreate("server", "user", rejected, out _));
    }

    [Fact]
    public async Task Saving_one_account_or_item_never_changes_another_scope_across_restarts()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scopes = new[]
        {
            CreateScope("server-a", "user-a", "item-a"),
            CreateScope("server-b", "user-a", "item-a"),
            CreateScope("server-a", "user-b", "item-a"),
            CreateScope("server-a", "user-a", "item-b"),
            CreateScope("server:user", "identity", "item"),
            CreateScope("server", "user:identity", "item")
        };
        var variants = scopes.Select((_, index) => new PlaybackPreferences
        {
            VolumeLevel = 10 + index,
            IsMuted = index % 2 == 0,
            PlaybackRate = .5 + index * .25,
            AudioDelaySeconds = index - 2,
            SubtitleDelaySeconds = 2 - index
        }).ToArray();
        for (var index = 0; index < scopes.Length; index++)
            await store.SaveAsync(scopes[index], variants[index], token);

        var replacement = variants[0] with { VolumeLevel = 89, IsMuted = false };
        await store.SaveAsync(scopes[0], replacement, token);
        var restarted = new PlaybackPreferenceStore(store.FilePath);

        Assert.True(replacement.ContentEquals(await restarted.LoadAsync(scopes[0], token)));
        for (var index = 1; index < scopes.Length; index++)
            Assert.True(variants[index].ContentEquals(await restarted.LoadAsync(scopes[index], token)));
        Assert.Null(await restarted.LoadAsync(CreateScope("unseen-server", "user-a", "item-a"), token));
    }

    [Fact]
    public async Task Saved_and_loaded_subtitle_arrays_cannot_mutate_the_cached_record()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var preferences = CreatePreferences(fixture);
        var expected = preferences with { LocalSubtitles = preferences.LocalSubtitles.ToArray() };
        await store.SaveAsync(scope, preferences, token);
        preferences.LocalSubtitles[0] = preferences.LocalSubtitles[1];
        var first = Assert.IsType<PlaybackPreferences>(await store.LoadAsync(scope, token));
        Assert.True(expected.ContentEquals(first));
        first.LocalSubtitles[0] = first.LocalSubtitles[1];

        Assert.True(expected.ContentEquals(await store.LoadAsync(scope, token)));
        Assert.True(expected.ContentEquals(await new PlaybackPreferenceStore(store.FilePath).LoadAsync(scope, token)));
    }

    [Theory]
    [InlineData(-1, PlaybackSubtitleSelection.Off)]
    [InlineData(0, PlaybackSubtitleSelection.Server)]
    [InlineData(7, PlaybackSubtitleSelection.Server)]
    public void Explicit_server_subtitles_or_Off_take_priority_over_a_saved_local_selection(
        int explicitIndex, PlaybackSubtitleSelection expectedSelection)
    {
        using var fixture = new TemporaryAccountStore();
        var preferences = CreatePreferences(fixture);

        Assert.False(PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(preferences, explicitIndex));
        Assert.Equal(explicitIndex,
            PlaybackPreferenceRestorePolicy.ResolveInitialSubtitleIndex(preferences, explicitIndex, 3));
        Assert.Equal(expectedSelection,
            PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(preferences, explicitIndex));
    }

    [Theory]
    [InlineData(PlaybackSubtitleSelection.Server, 3, false)]
    [InlineData(PlaybackSubtitleSelection.Off, -1, false)]
    [InlineData(PlaybackSubtitleSelection.Local, 3, true)]
    public void Unspecified_subtitles_restore_the_saved_mode_without_replacing_server_preference(
        PlaybackSubtitleSelection selection, int expectedIndex, bool activatesLocal)
    {
        using var fixture = new TemporaryAccountStore();
        var local = CreatePreferences(fixture);
        var preferences = local with
        {
            SubtitleSelection = selection,
            SelectedLocalSubtitlePath = selection == PlaybackSubtitleSelection.Local
                ? local.SelectedLocalSubtitlePath : null
        };

        Assert.Equal(activatesLocal, PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(preferences, null));
        Assert.Equal(expectedIndex, PlaybackPreferenceRestorePolicy.ResolveInitialSubtitleIndex(preferences, null, 3));
        Assert.Equal(selection, PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(preferences, null));
        Assert.False(PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(
            local with { SelectedLocalSubtitlePath = null }, null));
    }

    [Theory]
    [InlineData(PlaybackSubtitleSelection.Local, true)]
    [InlineData(PlaybackSubtitleSelection.Off, false)]
    [InlineData(PlaybackSubtitleSelection.Server, false)]
    public async Task Replay_preserves_the_item_selection_when_global_subtitles_default_to_None(
        PlaybackSubtitleSelection selection, bool activatesLocal)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var local = CreatePreferences(fixture);
        var saved = local with
        {
            SubtitleSelection = selection,
            SelectedLocalSubtitlePath = selection == PlaybackSubtitleSelection.Local ? local.SelectedLocalSubtitlePath : null
        };
        await store.SaveAsync(scope, saved, token);
        var restarted = Assert.IsType<PlaybackPreferences>(await new PlaybackPreferenceStore(store.FilePath).LoadAsync(scope, token));
        var replay = new PlaybackPreferenceReplay(scope, restarted);
        var restored = Assert.IsType<PlaybackPreferences>(PlaybackPreferenceRestorePolicy.ResolveReplayPreferences(replay, CreateScope()));
        var global = new LumenPreferences { SubtitleMode = "None" };
        var requestedIndex = PlaybackPreferenceRestorePolicy.ResolveSubtitleRequestIndex(null, global.SubtitleMode, isReplay: true);

        Assert.Equal(selection, PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(restored, requestedIndex));
        Assert.Equal(activatesLocal, PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(restored, requestedIndex));
        Assert.Equal(selection == PlaybackSubtitleSelection.Off ? -1 : 3,
            PlaybackPreferenceRestorePolicy.ResolveInitialSubtitleIndex(restored, requestedIndex, 3));
        Assert.Equal(-1, PlaybackPreferenceRestorePolicy.ResolveSubtitleRequestIndex(null, global.SubtitleMode, isReplay: false));
        Assert.True(saved.ContentEquals(await store.LoadAsync(scope, token)));
    }

    [Theory]
    [InlineData(-1, PlaybackSubtitleSelection.Off)]
    [InlineData(7, PlaybackSubtitleSelection.Server)]
    public void A_real_explicit_subtitle_request_still_overrides_Local_during_replay(
        int explicitIndex, PlaybackSubtitleSelection expectedSelection)
    {
        using var fixture = new TemporaryAccountStore();
        var preferences = CreatePreferences(fixture);
        var requestedIndex = PlaybackPreferenceRestorePolicy.ResolveSubtitleRequestIndex(explicitIndex, "None", isReplay: true);

        Assert.Equal(expectedSelection, PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(preferences, requestedIndex));
        Assert.Equal(explicitIndex, PlaybackPreferenceRestorePolicy.ResolveInitialSubtitleIndex(preferences, requestedIndex, 3));
        Assert.False(PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(preferences, requestedIndex));
    }

    [Theory]
    [InlineData("server-b", "user-a", "item-a")]
    [InlineData("server-a", "user-b", "item-a")]
    [InlineData("server-a", "user-a", "item-b")]
    public async Task Replay_cannot_transfer_a_live_local_selection_to_another_server_account_or_item(
        string serverId, string userId, string itemId)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var originalScope = CreateScope();
        var otherScope = CreateScope(serverId, userId, itemId);
        var local = CreatePreferences(fixture);
        var off = local with { SubtitleSelection = PlaybackSubtitleSelection.Off, SelectedLocalSubtitlePath = null };
        await store.SaveAsync(originalScope, local, token);
        await store.SaveAsync(otherScope, off, token);
        var replay = new PlaybackPreferenceReplay(originalScope, local);

        Assert.Null(PlaybackPreferenceRestorePolicy.ResolveReplayPreferences(replay, otherScope));
        Assert.Null(PlaybackPreferenceRestorePolicy.ResolveReplayPreferences(replay, null));
        var restored = Assert.IsType<PlaybackPreferences>(await store.LoadAsync(otherScope, token));
        Assert.Equal(PlaybackSubtitleSelection.Off, PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(restored, null));
        Assert.False(PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(restored, null));
        Assert.True(local.ContentEquals(await store.LoadAsync(originalScope, token)));
    }

    [Fact]
    public async Task In_memory_replay_uses_live_controls_instead_of_an_older_persisted_account_record()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var live = CreatePreferences(fixture);
        var persisted = live with { VolumeLevel = 100, SubtitleSelection = PlaybackSubtitleSelection.Off, SelectedLocalSubtitlePath = null };
        await store.SaveAsync(scope, persisted, token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        var replay = new PlaybackPreferenceReplay(scope, live);

        var restored = Assert.IsType<PlaybackPreferences>(PlaybackPreferenceRestorePolicy.ResolveReplayPreferences(replay, CreateScope()));
        Assert.Equal(PlaybackSubtitleSelection.Local, PlaybackPreferenceRestorePolicy.ResolveSubtitleSelection(restored, null));
        Assert.True(PlaybackPreferenceRestorePolicy.ShouldActivateLocalSubtitle(restored, null));
        Assert.Equal(live.VolumeLevel, restored.VolumeLevel);
        Assert.Equal(live.SubtitleDelaySeconds, restored.SubtitleDelaySeconds);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.True(persisted.ContentEquals(await new PlaybackPreferenceStore(store.FilePath).LoadAsync(scope, token)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"Version\":2,\"Entries\":[]}")]
    [InlineData("{\"Version\":1,\"Entries\":null}")]
    [InlineData("{\"Version\":1,\"Entries\":[null]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"invalid-key\",\"LastUpdatedUtcTicks\":1,\"Preferences\":{}}]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"$KEY$\",\"LastUpdatedUtcTicks\":0,\"Preferences\":{}}]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"$KEY$\",\"LastUpdatedUtcTicks\":9223372036854775807,\"Preferences\":{}}]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"$KEY$\",\"LastUpdatedUtcTicks\":1,\"Preferences\":null}]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"$KEY$\",\"LastUpdatedUtcTicks\":1,\"Preferences\":{\"VolumeLevel\":101}}]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"$KEY$\",\"LastUpdatedUtcTicks\":1,\"Preferences\":{\"SubtitleSelection\":99}}]}")]
    [InlineData("{\"Entries\":[{\"Key\":\"$KEY$\",\"LastUpdatedUtcTicks\":1,\"Preferences\":{\"LocalSubtitles\":null}}]}")]
    public async Task Corrupt_documents_return_no_record_and_preserve_the_original_file(string content)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var before = Encoding.UTF8.GetBytes(content.Replace("$KEY$", scope.Key, StringComparison.Ordinal));
        await File.WriteAllBytesAsync(store.FilePath, before, token);

        Assert.Null(await store.LoadAsync(scope, token));
        Assert.Null(await store.LoadAsync(scope, token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Fact]
    public async Task Duplicate_scopes_reject_the_entire_document_without_exposing_its_valid_prefix()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var before = EncodeDocument(
        [
            new(scope, 1, new PlaybackPreferences { VolumeLevel = 12 }),
            new(scope, 2, new PlaybackPreferences { VolumeLevel = 87 })
        ]);
        await File.WriteAllBytesAsync(store.FilePath, before, token);

        Assert.Null(await store.LoadAsync(scope, token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
    }

    [Theory]
    [InlineData(0, PlaybackPreferencePersistenceIssue.None)]
    [InlineData(1, PlaybackPreferencePersistenceIssue.InvalidData)]
    public async Task File_size_boundary_preserves_input_and_rejects_only_oversized_documents(
        int excessBytes, PlaybackPreferencePersistenceIssue expectedIssue)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var before = Enumerable.Repeat((byte)' ', PlaybackPreferenceStore.MaximumFileBytes + excessBytes).ToArray();
        Encoding.UTF8.GetBytes("{\"Version\":1,\"Entries\":[]}").CopyTo(before, 0);
        await File.WriteAllBytesAsync(store.FilePath, before, token);

        Assert.Null(await store.LoadAsync(CreateScope(), token));

        Assert.Equal(expectedIssue, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
    }

    [Fact]
    public async Task An_input_document_with_too_many_entries_is_rejected_without_rewriting_it()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var entries = Enumerable.Range(0, PlaybackPreferenceStore.MaximumEntries + 1)
            .Select(index => new SeedEntry(CreateScope(itemId: $"item-{index}"), index + 1, new PlaybackPreferences()))
            .ToArray();
        var before = EncodeDocument(entries);
        await File.WriteAllBytesAsync(store.FilePath, before, token);

        Assert.Null(await store.LoadAsync(entries[0].Scope, token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
    }

    [Theory]
    [InlineData("VolumeLevel", -1)]
    [InlineData("VolumeLevel", 101)]
    [InlineData("PlaybackRate", .49)]
    [InlineData("PlaybackRate", 2.01)]
    [InlineData("PlaybackRate", double.NaN)]
    [InlineData("PlaybackRate", double.PositiveInfinity)]
    [InlineData("PlaybackRate", double.NegativeInfinity)]
    [InlineData("AudioDelaySeconds", -10.1)]
    [InlineData("AudioDelaySeconds", 10.1)]
    [InlineData("AudioDelaySeconds", double.NaN)]
    [InlineData("AudioDelaySeconds", double.PositiveInfinity)]
    [InlineData("SubtitleDelaySeconds", -10.1)]
    [InlineData("SubtitleDelaySeconds", 10.1)]
    [InlineData("SubtitleDelaySeconds", double.NaN)]
    [InlineData("SubtitleDelaySeconds", double.NegativeInfinity)]
    public async Task Invalid_control_values_never_replace_the_last_successful_record(string property, double value)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var original = CreatePreferences(fixture);
        await store.SaveAsync(scope, original, token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        var invalid = property switch
        {
            "VolumeLevel" => original with { VolumeLevel = (int)value },
            "PlaybackRate" => original with { PlaybackRate = value },
            "AudioDelaySeconds" => original with { AudioDelaySeconds = value },
            "SubtitleDelaySeconds" => original with { SubtitleDelaySeconds = value },
            _ => throw new InvalidOperationException("Unexpected control property.")
        };

        var error = await Assert.ThrowsAsync<PlaybackPreferenceStoreException>(() => store.SaveAsync(scope, invalid, token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.InvalidData, error.Issue);
        Assert.Equal(PlaybackPreferencePersistenceIssue.InvalidData, store.LastIssue);
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        Assert.True(original.ContentEquals(await new PlaybackPreferenceStore(store.FilePath).LoadAsync(scope, token)));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Theory]
    [InlineData("unknown-selection")]
    [InlineData("null-choices")]
    [InlineData("null-choice")]
    [InlineData("too-many-choices")]
    [InlineData("duplicate-path")]
    [InlineData("duplicate-path-case")]
    [InlineData("relative-path")]
    [InlineData("noncanonical-path")]
    [InlineData("unsupported-extension")]
    [InlineData("empty-content")]
    [InlineData("oversized-content")]
    [InlineData("invalid-fingerprint")]
    [InlineData("uppercase-fingerprint")]
    [InlineData("missing-selected-path")]
    [InlineData("unknown-selected-path")]
    [InlineData("server-with-selected-path")]
    [InlineData("off-with-selected-path")]
    public async Task Invalid_subtitle_metadata_or_selection_never_changes_the_previous_file(string invalidKind)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var valid = CreatePreferences(fixture);
        var original = valid with { SelectedLocalSubtitlePath = valid.LocalSubtitles[0].Path };
        await store.SaveAsync(scope, original, token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        var first = original.LocalSubtitles[0];
        var invalid = invalidKind switch
        {
            "unknown-selection" => original with
            {
                SubtitleSelection = (PlaybackSubtitleSelection)99, SelectedLocalSubtitlePath = null
            },
            "null-choices" => original with { LocalSubtitles = null! },
            "null-choice" => original with { LocalSubtitles = [null!] },
            "too-many-choices" => original with
            {
                LocalSubtitles = Enumerable.Range(0, PlaybackPreferenceStore.MaximumLocalSubtitles + 1)
                    .Select(index => first with { Path = Path.Combine(fixture.DirectoryPath, $"choice-{index}.srt") }).ToArray(),
                SelectedLocalSubtitlePath = Path.Combine(fixture.DirectoryPath, "choice-0.srt")
            },
            "duplicate-path" => original with { LocalSubtitles = [first, first] },
            "duplicate-path-case" => original with
            {
                LocalSubtitles = [first, first with { Path = Path.ChangeExtension(first.Path, ".SRT") }]
            },
            "relative-path" => original with
            {
                LocalSubtitles = [first with { Path = "captions.srt" }], SelectedLocalSubtitlePath = "captions.srt"
            },
            "noncanonical-path" => original with
            {
                LocalSubtitles = [first with { Path = Path.Combine(fixture.DirectoryPath, "unused", "..", "first.srt") }],
                SelectedLocalSubtitlePath = Path.Combine(fixture.DirectoryPath, "unused", "..", "first.srt")
            },
            "unsupported-extension" => original with
            {
                LocalSubtitles = [first with { Path = Path.ChangeExtension(first.Path, ".idx") }],
                SelectedLocalSubtitlePath = Path.ChangeExtension(first.Path, ".idx")
            },
            "empty-content" => original with { LocalSubtitles = [first with { Length = 0 }] },
            "oversized-content" => original with { LocalSubtitles = [first with { Length = PlaybackSubtitleFile.MaximumBytes + 1 }] },
            "invalid-fingerprint" => original with { LocalSubtitles = [first with { ContentSha256 = new string('z', 64) }] },
            "uppercase-fingerprint" => original with { LocalSubtitles = [first with { ContentSha256 = new string('A', 64) }] },
            "missing-selected-path" => original with { SelectedLocalSubtitlePath = null },
            "unknown-selected-path" => original with { SelectedLocalSubtitlePath = Path.Combine(fixture.DirectoryPath, "unknown.srt") },
            "server-with-selected-path" => original with { SubtitleSelection = PlaybackSubtitleSelection.Server },
            "off-with-selected-path" => original with { SubtitleSelection = PlaybackSubtitleSelection.Off },
            _ => throw new InvalidOperationException("Unexpected subtitle metadata variant.")
        };

        var error = await Assert.ThrowsAsync<PlaybackPreferenceStoreException>(() => store.SaveAsync(scope, invalid, token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.InvalidData, error.Issue);
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Fact]
    public async Task Valid_control_boundaries_and_the_maximum_subtitle_count_round_trip()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var first = CreatePreferences(fixture).LocalSubtitles[0];
        var choices = Enumerable.Range(0, PlaybackPreferenceStore.MaximumLocalSubtitles)
            .Select(index => first with { Path = Path.Combine(fixture.DirectoryPath, $"choice-{index}.srt") }).ToArray();
        var minimum = new PlaybackPreferences
        {
            VolumeLevel = 0, IsMuted = true, PlaybackRate = .5,
            AudioDelaySeconds = -10, SubtitleDelaySeconds = -10,
            SubtitleSelection = PlaybackSubtitleSelection.Local,
            LocalSubtitles = choices, SelectedLocalSubtitlePath = choices[0].Path
        };
        var maximum = minimum with
        {
            VolumeLevel = 100, IsMuted = false, PlaybackRate = 2,
            AudioDelaySeconds = 10, SubtitleDelaySeconds = 10,
            SelectedLocalSubtitlePath = choices[^1].Path
        };
        var lowScope = CreateScope(itemId: "minimum");
        var highScope = CreateScope(itemId: "maximum");

        await store.SaveAsync(lowScope, minimum, token);
        await store.SaveAsync(highScope, maximum, token);
        var restored = new PlaybackPreferenceStore(store.FilePath);

        Assert.True(minimum.ContentEquals(await restored.LoadAsync(lowScope, token)));
        Assert.True(maximum.ContentEquals(await restored.LoadAsync(highScope, token)));
    }

    [Fact]
    public async Task Exceeding_the_entry_bound_evicts_the_oldest_saved_record_and_keeps_a_refreshed_item()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var entries = Enumerable.Range(0, PlaybackPreferenceStore.MaximumEntries)
            .Select(index => new SeedEntry(CreateScope(itemId: $"item-{index}"), index + 1,
                new PlaybackPreferences { VolumeLevel = index % 101 })).ToArray();
        await File.WriteAllBytesAsync(store.FilePath, EncodeDocument(entries), token);
        var refreshed = new PlaybackPreferences { VolumeLevel = 83 };
        var added = new PlaybackPreferences { VolumeLevel = 47 };
        var newScope = CreateScope(itemId: "new-item");

        await store.SaveAsync(entries[0].Scope, refreshed, token);
        await store.SaveAsync(newScope, added, token);
        var restarted = new PlaybackPreferenceStore(store.FilePath);

        Assert.True(refreshed.ContentEquals(await restarted.LoadAsync(entries[0].Scope, token)));
        Assert.Null(await restarted.LoadAsync(entries[1].Scope, token));
        Assert.True(entries[2].Preferences.ContentEquals(await restarted.LoadAsync(entries[2].Scope, token)));
        Assert.True(added.ContentEquals(await restarted.LoadAsync(newScope, token)));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Equal(PlaybackPreferenceStore.MaximumEntries, document.RootElement.GetProperty("Entries").GetArrayLength());
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Fact]
    public async Task Exceeding_the_document_byte_limit_evicts_old_entries_and_preserves_the_latest_scope()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var first = CreatePreferences(fixture).LocalSubtitles[0];
        var choices = Enumerable.Range(0, PlaybackPreferenceStore.MaximumLocalSubtitles).Select(index => first with
        {
            Path = Path.Combine(fixture.DirectoryPath, new string('a', 1800) + $"-{index}.srt")
        }).ToArray();
        var large = new PlaybackPreferences
        {
            SubtitleSelection = PlaybackSubtitleSelection.Local,
            LocalSubtitles = choices, SelectedLocalSubtitlePath = choices[0].Path
        };
        var entries = new List<SeedEntry>();
        byte[] before = [];
        for (var index = 0; index < PlaybackPreferenceStore.MaximumEntries; index++)
        {
            var entry = new SeedEntry(CreateScope(itemId: $"large-item-{index}"), index + 1, large);
            var candidate = EncodeDocument(entries.Append(entry));
            if (candidate.Length > PlaybackPreferenceStore.MaximumFileBytes) break;
            entries.Add(entry);
            before = candidate;
        }
        Assert.NotEmpty(entries);
        Assert.True(entries.Count < PlaybackPreferenceStore.MaximumEntries);
        await File.WriteAllBytesAsync(store.FilePath, before, token);
        Assert.True(large.ContentEquals(await store.LoadAsync(entries[0].Scope, token)));
        var newScope = CreateScope(itemId: "large-overflow");

        await store.SaveAsync(newScope, large, token);

        Assert.Equal(PlaybackPreferencePersistenceIssue.None, store.LastIssue);
        Assert.True(large.ContentEquals(await store.LoadAsync(newScope, token)));
        Assert.Null(await store.LoadAsync(entries[0].Scope, token));
        Assert.True(large.ContentEquals(await store.LoadAsync(entries[^1].Scope, token)));
        var after = await File.ReadAllBytesAsync(store.FilePath, token);
        Assert.True(after.Length <= PlaybackPreferenceStore.MaximumFileBytes);
        using var document = JsonDocument.Parse(after);
        Assert.True(document.RootElement.GetProperty("Entries").GetArrayLength() < entries.Count + 1);
        var restarted = new PlaybackPreferenceStore(store.FilePath);
        Assert.True(large.ContentEquals(await restarted.LoadAsync(newScope, token)));
        Assert.Null(await restarted.LoadAsync(entries[0].Scope, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Fact]
    public async Task Precanceled_operations_never_create_a_missing_directory()
    {
        using var fixture = new TemporaryAccountStore();
        var directory = Path.Combine(fixture.DirectoryPath, "missing");
        var store = new PlaybackPreferenceStore(Path.Combine(directory, "playback-preferences.json"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(CreateScope(), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(CreateScope(), new PlaybackPreferences(), canceled.Token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.None, store.LastIssue);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task Precanceled_updates_preserve_the_last_file_and_cached_preferences()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var original = CreatePreferences(fixture);
        await store.SaveAsync(scope, original, token);
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(scope, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(scope, original with { VolumeLevel = 2 }, canceled.Token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.None, store.LastIssue);
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
    }

    [Theory(Timeout = 15000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_while_waiting_for_the_gate_does_not_publish_a_record_or_release_an_unowned_slot(bool save)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var newScope = CreateScope(itemId: "canceled-item");
        var original = CreatePreferences(fixture);
        await File.WriteAllBytesAsync(store.FilePath, EncodeDocument(CreateFullCache(original)), token);
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var gate = Assert.IsType<SemaphoreSlim>(typeof(PlaybackPreferenceStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
        await gate.WaitAsync(token);
        try
        {
            Task pending = save ? store.SaveAsync(newScope, original, canceled.Token) : store.LoadAsync(scope, canceled.Token);
            Assert.False(pending.IsCompleted);
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(0, gate.CurrentCount);
        }
        finally { gate.Release(); }

        Assert.Equal(PlaybackPreferencePersistenceIssue.None, store.LastIssue);
        Assert.Null(await store.LoadAsync(newScope, token));
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.FilePath, token));
        using (var document = JsonDocument.Parse(before))
            Assert.Equal(PlaybackPreferenceStore.MaximumEntries, document.RootElement.GetProperty("Entries").GetArrayLength());
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
        await store.SaveAsync(newScope, new PlaybackPreferences { VolumeLevel = 71 }, token);
        Assert.Equal(71, (await store.LoadAsync(newScope, token))!.VolumeLevel);
    }

    [Fact]
    public async Task A_failed_atomic_commit_preserves_the_cached_record_and_removes_the_temporary_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var scope = CreateScope();
        var newScope = CreateScope(itemId: "failed-item");
        var original = CreatePreferences(fixture);
        await File.WriteAllBytesAsync(store.FilePath, EncodeDocument(CreateFullCache(original)), token);
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        var before = await File.ReadAllBytesAsync(store.FilePath, token);
        var preservedPath = Path.Combine(fixture.DirectoryPath, "preserved.json");
        File.Move(store.FilePath, preservedPath);
        Directory.CreateDirectory(store.FilePath);

        var error = await Assert.ThrowsAsync<PlaybackPreferenceStoreException>(() =>
            store.SaveAsync(newScope, original with { VolumeLevel = 1 }, token));

        Assert.Equal(PlaybackPreferencePersistenceIssue.WriteFailed, error.Issue);
        Assert.Equal(PlaybackPreferencePersistenceIssue.WriteFailed, store.LastIssue);
        Assert.Null(await store.LoadAsync(newScope, token));
        Assert.True(original.ContentEquals(await store.LoadAsync(scope, token)));
        Assert.Equal(before, await File.ReadAllBytesAsync(preservedPath, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, ".playback-preferences-*.tmp"));
        Directory.Delete(store.FilePath);
        File.Move(preservedPath, store.FilePath);
        Assert.True(original.ContentEquals(await new PlaybackPreferenceStore(store.FilePath).LoadAsync(scope, token)));
    }

    private static PlaybackPreferenceStore CreateStore(TemporaryAccountStore fixture) =>
        new(Path.Combine(fixture.DirectoryPath, "playback-preferences.json"));

    private static PlaybackPreferenceScope CreateScope(string serverId = "server-a", string userId = "user-a",
        string itemId = "item-a")
    {
        Assert.True(PlaybackPreferenceScope.TryCreate(serverId, userId, itemId, out var scope));
        return Assert.IsType<PlaybackPreferenceScope>(scope);
    }

    private static PlaybackPreferences CreatePreferences(TemporaryAccountStore fixture)
    {
        var data = Encoding.UTF8.GetBytes("synthetic subtitle content");
        var first = Assert.IsType<LocalSubtitlePreference>(
            PlaybackSubtitleFile.Capture(Path.Combine(fixture.DirectoryPath, "first.srt"), data));
        var second = Assert.IsType<LocalSubtitlePreference>(
            PlaybackSubtitleFile.Capture(Path.Combine(fixture.DirectoryPath, "second.vtt"), data));
        return new()
        {
            VolumeLevel = 37, IsMuted = true, PlaybackRate = 1.25,
            AudioDelaySeconds = -.7, SubtitleDelaySeconds = 2.1,
            SubtitleSelection = PlaybackSubtitleSelection.Local,
            LocalSubtitles = [first, second], SelectedLocalSubtitlePath = second.Path
        };
    }

    private static SeedEntry[] CreateFullCache(PlaybackPreferences original) =>
        Enumerable.Range(0, PlaybackPreferenceStore.MaximumEntries).Select(index => new SeedEntry(
            index == 0 ? CreateScope() : CreateScope(itemId: $"seed-{index}"),
            index + 1, index == 0 ? original : new PlaybackPreferences())).ToArray();

    private static byte[] EncodeDocument(IEnumerable<SeedEntry> entries)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Version", 1);
            writer.WriteStartArray("Entries");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("Key", entry.Scope.Key);
                writer.WriteNumber("LastUpdatedUtcTicks", entry.UpdatedTicks);
                writer.WriteStartObject("Preferences");
                var preferences = entry.Preferences;
                writer.WriteNumber("VolumeLevel", preferences.VolumeLevel);
                writer.WriteBoolean("IsMuted", preferences.IsMuted);
                writer.WriteNumber("PlaybackRate", preferences.PlaybackRate);
                writer.WriteNumber("AudioDelaySeconds", preferences.AudioDelaySeconds);
                writer.WriteNumber("SubtitleDelaySeconds", preferences.SubtitleDelaySeconds);
                writer.WriteNumber("SubtitleSelection", (int)preferences.SubtitleSelection);
                writer.WriteStartArray("LocalSubtitles");
                foreach (var subtitle in preferences.LocalSubtitles)
                {
                    writer.WriteStartObject();
                    writer.WriteString("Path", subtitle.Path);
                    writer.WriteString("ContentSha256", subtitle.ContentSha256);
                    writer.WriteNumber("Length", subtitle.Length);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("SelectedLocalSubtitlePath", preferences.SelectedLocalSubtitlePath);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private sealed record SeedEntry(PlaybackPreferenceScope Scope, long UpdatedTicks, PlaybackPreferences Preferences);
}
