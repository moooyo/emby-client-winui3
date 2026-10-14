using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class MediaLibrarySelectionStoreTests
{
    private static readonly string AccountA = AccountStore.CreateAccountKey("server-a", "user-a");
    private static readonly string AccountB = AccountStore.CreateAccountKey("server-a", "user-b");
    private static readonly string AccountC = AccountStore.CreateAccountKey("server-b", "user-a");

    [Fact]
    public async Task Missing_selections_return_All_without_creating_a_directory_or_file()
    {
        using var fixture = new TemporaryAccountStore();
        var store = CreateStore(fixture);

        var selections = await store.LoadAsync(AccountA, TestContext.Current.CancellationToken);

        Assert.Equal(new MediaLibrarySelections(), selections);
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, store.LastIssue);
        Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task Opaque_ids_round_trip_without_reflection_or_account_and_global_preference_data()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        const string accountData = """{"Accounts":[{"ProtectedToken":"synthetic-secret"}]}""";
        const string preferenceData = """{"Theme":"Light","Accent":"Blue"}""";
        var preferencePath = Path.Combine(fixture.DirectoryPath, "lumen-preferences.json");
        var expected = new MediaLibrarySelections("opaque/\u0394:movie-\uD83C\uDFAC", "opaque:\u65e5\u672c-series");
        await File.WriteAllTextAsync(fixture.SettingsPath, accountData, token);
        await File.WriteAllTextAsync(preferencePath, preferenceData, token);

        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        await store.SaveAsync(AccountA, expected.MovieLibraryId, expected.SeriesLibraryId, token);
        var restored = await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token);

        Assert.Equal(expected, restored);
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, store.LastIssue);
        var path = Assert.Single(Directory.GetFiles(store.DirectoryPath));
        Assert.Equal(AccountFilePath(store, AccountA), path);
        var content = await File.ReadAllTextAsync(path, token);
        using var document = JsonDocument.Parse(content);
        Assert.Equal(new[] { "MovieLibraryId", "SeriesLibraryId" },
            document.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(expected.MovieLibraryId, document.RootElement.GetProperty("MovieLibraryId").GetString());
        Assert.Equal(expected.SeriesLibraryId, document.RootElement.GetProperty("SeriesLibraryId").GetString());
        Assert.DoesNotContain(AccountA, content);
        Assert.DoesNotContain("synthetic-secret", content);
        Assert.Equal(accountData, await File.ReadAllTextAsync(fixture.SettingsPath, token));
        Assert.Equal(preferenceData, await File.ReadAllTextAsync(preferencePath, token));
    }

    [Fact]
    public async Task Updating_one_account_keeps_other_users_and_servers_unchanged_across_restarts()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var second = new MediaLibrarySelections("movies-b", "series-b");
        var third = new MediaLibrarySelections("movies-c", "series-c");
        await store.SaveAsync(AccountA, "movies-a", "series-a", token);
        await store.SaveAsync(AccountB, second.MovieLibraryId, second.SeriesLibraryId, token);
        await store.SaveAsync(AccountC, third.MovieLibraryId, third.SeriesLibraryId, token);
        var secondBefore = await File.ReadAllBytesAsync(AccountFilePath(store, AccountB), token);
        var thirdBefore = await File.ReadAllBytesAsync(AccountFilePath(store, AccountC), token);

        await store.SaveAsync(AccountA, null, "replacement-series-a", token);
        var restarted = new MediaLibrarySelectionStore(store.DirectoryPath);

        Assert.Equal(new MediaLibrarySelections(null, "replacement-series-a"), await restarted.LoadAsync(AccountA, token));
        Assert.Equal(second, await restarted.LoadAsync(AccountB, token));
        Assert.Equal(third, await restarted.LoadAsync(AccountC, token));
        Assert.Equal(secondBefore, await File.ReadAllBytesAsync(AccountFilePath(store, AccountB), token));
        Assert.Equal(thirdBefore, await File.ReadAllBytesAsync(AccountFilePath(store, AccountC), token));
        var missingAccount = AccountStore.CreateAccountKey("server-b", "user-b");
        Assert.Equal(new MediaLibrarySelections(), await restarted.LoadAsync(missingAccount, token));
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, restarted.LastIssue);
        Assert.Equal(3, Directory.GetFiles(store.DirectoryPath).Length);
    }

    [Fact]
    public async Task Account_key_casing_aliases_share_the_same_canonical_hashed_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var alias = AccountA.ToLowerInvariant();
        var replacement = new MediaLibrarySelections("alias-movies", "alias-series");
        await store.SaveAsync(AccountA, "original-movies", "original-series", token);

        await store.SaveAsync(alias, replacement.MovieLibraryId, replacement.SeriesLibraryId, token);
        var restarted = new MediaLibrarySelectionStore(store.DirectoryPath);

        Assert.Equal(AccountFilePath(store, AccountA), Assert.Single(Directory.GetFiles(store.DirectoryPath)));
        Assert.Equal(replacement, await restarted.LoadAsync(AccountA, token));
        Assert.Equal(replacement, await restarted.LoadAsync(alias, token));
    }

    [Fact]
    public async Task Choosing_All_persists_two_explicit_null_properties_instead_of_the_previous_ids()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        await store.SaveAsync(AccountA, "movies", "series", token);

        await store.SaveAsync(AccountA, null, null, token);

        Assert.Equal(new MediaLibrarySelections(), await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(AccountFilePath(store, AccountA), token));
        Assert.Equal(2, document.RootElement.EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("MovieLibraryId").ValueKind);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("SeriesLibraryId").ValueKind);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"MovieLibraryId":"movies"}""")]
    [InlineData("""{"MovieLibraryId":"movies","SeriesLibraryId":"series","AccessToken":"synthetic-secret"}""")]
    [InlineData("""{"MovieLibraryId":"movies","MovieLibraryId":"replacement","SeriesLibraryId":"series"}""")]
    [InlineData("""{"MovieLibraryId":"movies","movielibraryid":"replacement","SeriesLibraryId":"series"}""")]
    [InlineData("""{"MovieLibraryId":42,"SeriesLibraryId":"series"}""")]
    [InlineData("""{"MovieLibraryId":"movies","SeriesLibraryId":{"Id":"series"}}""")]
    [InlineData("""{"MovieLibraryId":" ","SeriesLibraryId":"series"}""")]
    [InlineData("""{"MovieLibraryId":"movies","SeriesLibraryId":".."}""")]
    [InlineData("""{"MovieLibraryId":"movies","SeriesLibraryId":"series\ninvalid"}""")]
    [InlineData("{\"MovieLibraryId\":\"\\ud800\",\"SeriesLibraryId\":null}")]
    [InlineData("{\"MovieLibraryId\":\"\\udc00\",\"SeriesLibraryId\":null}")]
    [InlineData("{\"MovieLibraryId\\ud800\":null,\"SeriesLibraryId\":null}")]
    [InlineData("{\"MovieLibraryId\\udc00\":null,\"SeriesLibraryId\":null}")]
    public async Task Corrupt_account_files_return_All_without_repairing_them_or_changing_another_account(string content)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var retained = new MediaLibrarySelections("retained-movies", "retained-series");
        await store.SaveAsync(AccountB, retained.MovieLibraryId, retained.SeriesLibraryId, token);
        var path = AccountFilePath(store, AccountA);
        var before = Encoding.UTF8.GetBytes(content);
        await File.WriteAllBytesAsync(path, before, token);

        Assert.Equal(new MediaLibrarySelections(), await store.LoadAsync(AccountA, token));

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(retained, await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountB, token));
        Assert.Equal(2, Directory.GetFiles(store.DirectoryPath).Length);
    }

    [Fact]
    public async Task An_explicit_valid_save_can_replace_a_corrupt_record_without_changing_another_account()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var retained = new MediaLibrarySelections("retained-movies", "retained-series");
        await store.SaveAsync(AccountB, retained.MovieLibraryId, retained.SeriesLibraryId, token);
        var otherBefore = await File.ReadAllBytesAsync(AccountFilePath(store, AccountB), token);
        await File.WriteAllTextAsync(AccountFilePath(store, AccountA), "{corrupt", token);
        Assert.Equal(new MediaLibrarySelections(), await store.LoadAsync(AccountA, token));
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, store.LastIssue);
        var replacement = new MediaLibrarySelections("selected-movies", "selected-series");

        await store.SaveAsync(AccountA, replacement.MovieLibraryId, replacement.SeriesLibraryId, token);

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, store.LastIssue);
        var restarted = new MediaLibrarySelectionStore(store.DirectoryPath);
        Assert.Equal(replacement, await restarted.LoadAsync(AccountA, token));
        Assert.Equal(retained, await restarted.LoadAsync(AccountB, token));
        Assert.Equal(otherBefore, await File.ReadAllBytesAsync(AccountFilePath(store, AccountB), token));
    }

    [Fact]
    public async Task Invalid_UTF8_property_names_return_All_without_changing_the_original_bytes()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        Directory.CreateDirectory(store.DirectoryPath);
        byte[] before =
        [
            .. Encoding.UTF8.GetBytes("{\"MovieLibraryId"),
            0xff,
            .. Encoding.UTF8.GetBytes("\":null,\"SeriesLibraryId\":null}")
        ];
        var path = AccountFilePath(store, AccountA);
        await File.WriteAllBytesAsync(path, before, token);

        Assert.Equal(new MediaLibrarySelections(), await store.LoadAsync(AccountA, token));

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(store.DirectoryPath)));
    }

    [Fact]
    public async Task Oversized_otherwise_valid_JSON_returns_All_without_truncating_the_file()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        Directory.CreateDirectory(store.DirectoryPath);
        var before = Encoding.UTF8.GetBytes("""{"MovieLibraryId":"movies","SeriesLibraryId":"series"}""" + new string(' ', 4 * 1024));
        var path = AccountFilePath(store, AccountA);
        await File.WriteAllBytesAsync(path, before, token);

        Assert.Equal(new MediaLibrarySelections(), await store.LoadAsync(AccountA, token));

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
    }

    [Fact]
    public async Task Case_insensitive_JSON_property_names_load_without_rewriting_the_source()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        Directory.CreateDirectory(store.DirectoryPath);
        const string content = """{"movieLibraryId":"movies","serieslibraryid":"series"}""";
        var path = AccountFilePath(store, AccountA);
        await File.WriteAllTextAsync(path, content, token);

        Assert.Equal(new MediaLibrarySelections("movies", "series"), await store.LoadAsync(AccountA, token));

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, store.LastIssue);
        Assert.Equal(content, await File.ReadAllTextAsync(path, token));
    }

    [Fact]
    public async Task Maximum_length_library_ids_round_trip_without_silent_truncation()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var expected = new MediaLibrarySelections(new string('m', 256), new string('s', 256));

        await store.SaveAsync(AccountA, expected.MovieLibraryId, expected.SeriesLibraryId, token);

        Assert.Equal(expected, await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token));
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, store.LastIssue);
    }

    [Theory]
    [MemberData(nameof(InvalidLibraryIds))]
    public async Task Invalid_library_ids_report_InvalidData_without_replacing_existing_selections(string invalidId, bool movie)
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var original = new MediaLibrarySelections("original-movies", "original-series");
        await store.SaveAsync(AccountA, original.MovieLibraryId, original.SeriesLibraryId, token);
        var path = AccountFilePath(store, AccountA);
        var before = await File.ReadAllBytesAsync(path, token);

        var error = await Assert.ThrowsAsync<MediaLibrarySelectionStoreException>(() =>
            store.SaveAsync(AccountA, movie ? invalidId : "replacement-movies", movie ? "replacement-series" : invalidId, token));

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, error.Issue);
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, store.LastIssue);
        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(original, await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token));
    }

    [Theory]
    [MemberData(nameof(InvalidAccountKeys))]
    public async Task Invalid_account_keys_cannot_create_paths_or_persist_selections(string? accountKey)
    {
        using var fixture = new TemporaryAccountStore();
        var store = CreateStore(fixture);

        var error = await Assert.ThrowsAsync<MediaLibrarySelectionStoreException>(() =>
            store.SaveAsync(accountKey!, "movies", "series", TestContext.Current.CancellationToken));

        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, error.Issue);
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.InvalidData, store.LastIssue);
        Assert.False(Directory.Exists(store.DirectoryPath));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task Precanceled_saves_keep_the_previous_file_and_loaded_selection()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var original = new MediaLibrarySelections("original-movies", "original-series");
        await store.SaveAsync(AccountA, original.MovieLibraryId, original.SeriesLibraryId, token);
        var path = AccountFilePath(store, AccountA);
        var before = await File.ReadAllBytesAsync(path, token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(AccountA, "replacement-movies", "replacement-series", canceled.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(original, await store.LoadAsync(AccountA, token));
        Assert.Equal(original, await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(store.DirectoryPath)));
    }

    [Fact]
    public async Task Precanceled_reads_and_saves_do_not_create_storage_for_a_missing_account()
    {
        using var fixture = new TemporaryAccountStore();
        var store = CreateStore(fixture);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(AccountA, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(AccountA, "movies", "series", canceled.Token));

        Assert.False(Directory.Exists(store.DirectoryPath));
    }

    [Fact]
    public async Task A_locked_account_file_reports_a_typed_read_failure_and_remains_readable_after_release()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var expected = new MediaLibrarySelections("movies", "series");
        await store.SaveAsync(AccountA, expected.MovieLibraryId, expected.SeriesLibraryId, token);
        var path = AccountFilePath(store, AccountA);
        var before = await File.ReadAllBytesAsync(path, token);
        var restarted = new MediaLibrarySelectionStore(store.DirectoryPath);

        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var error = await Assert.ThrowsAsync<MediaLibrarySelectionStoreException>(() => restarted.LoadAsync(AccountA, token));
            Assert.Equal(MediaLibrarySelectionPersistenceIssue.ReadFailed, error.Issue);
            Assert.Equal(MediaLibrarySelectionPersistenceIssue.ReadFailed, restarted.LastIssue);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(expected, await restarted.LoadAsync(AccountA, token));
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, restarted.LastIssue);
    }

    [Fact]
    public async Task A_failed_replacement_preserves_the_previous_file_and_allows_a_successful_retry()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var store = CreateStore(fixture);
        var original = new MediaLibrarySelections("original-movies", "original-series");
        var replacement = new MediaLibrarySelections("replacement-movies", "replacement-series");
        await store.SaveAsync(AccountA, original.MovieLibraryId, original.SeriesLibraryId, token);
        var path = AccountFilePath(store, AccountA);
        var before = await File.ReadAllBytesAsync(path, token);

        // Windows read sharing allows reads while preventing replacement of the committed file.
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = await Assert.ThrowsAsync<MediaLibrarySelectionStoreException>(() =>
                store.SaveAsync(AccountA, replacement.MovieLibraryId, replacement.SeriesLibraryId, token));
            Assert.Equal(MediaLibrarySelectionPersistenceIssue.WriteFailed, error.Issue);
            Assert.Equal(MediaLibrarySelectionPersistenceIssue.WriteFailed, store.LastIssue);
        }

        Assert.Equal(before, await File.ReadAllBytesAsync(path, token));
        Assert.Equal(original, await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token));
        Assert.Equal(path, Assert.Single(Directory.GetFiles(store.DirectoryPath)));
        await store.SaveAsync(AccountA, replacement.MovieLibraryId, replacement.SeriesLibraryId, token);
        Assert.Equal(replacement, await new MediaLibrarySelectionStore(store.DirectoryPath).LoadAsync(AccountA, token));
        Assert.Equal(MediaLibrarySelectionPersistenceIssue.None, store.LastIssue);
    }

    public static IEnumerable<object[]> InvalidLibraryIds()
    {
        foreach (var value in new[] { "", " ", "\t", ".", "..", "library\0id", "library\r\nid", "\uD800", "\uDC00", new string('x', 257) })
        {
            yield return [value, true];
            yield return [value, false];
        }
    }

    public static IEnumerable<object?[]> InvalidAccountKeys()
    {
        foreach (var value in new string?[]
        {
            null, "", " ", new string('a', 63), new string('a', 65), new string('g', 64),
            new string('\uFF21', 64), new string('a', 63) + "\n", "../" + new string('a', 61)
        })
            yield return [value];
    }

    private static MediaLibrarySelectionStore CreateStore(TemporaryAccountStore fixture) =>
        new(Path.Combine(fixture.DirectoryPath, "media-library-selections"));

    private static string AccountFilePath(MediaLibrarySelectionStore store, string accountKey) =>
        Path.Combine(store.DirectoryPath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountKey.ToUpperInvariant())))
            .ToLowerInvariant() + ".json");
}
