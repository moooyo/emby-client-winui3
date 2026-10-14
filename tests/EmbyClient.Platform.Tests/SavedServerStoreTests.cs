using System.Text;
using System.Text.Json;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class SavedServerStoreTests
{
    [Theory]
    [InlineData("  https://MEDIA.example.test:443  ", "https://media.example.test/emby/")]
    [InlineData("http://192.0.2.42:8096", "http://192.0.2.42:8096/emby/")]
    [InlineData("https://media.example.test/proxy/", "https://media.example.test/proxy/emby/")]
    [InlineData("https://media.example.test/proxy/emby", "https://media.example.test/proxy/emby/")]
    public void Server_creation_normalizes_the_api_root_and_preserves_the_public_identity(string address, string expectedRoot)
    {
        var server = SavedServer.Create(address, "server-a", "  Media Server  ");

        Assert.Equal(new SavedServer(expectedRoot, "server-a", "Media Server"), server);
    }

    [Fact]
    public void A_missing_display_name_uses_the_host_without_inventing_an_identity()
    {
        Assert.Equal("media.example.test", SavedServer.Create("https://media.example.test/proxy", "server-a").Name);
        Assert.Throws<ArgumentException>(() => SavedServer.Create("https://media.example.test", " "));
        Assert.Throws<ArgumentException>(() => SavedServer.Create("https://media.example.test", " server-a"));
        Assert.Throws<ArgumentException>(() => SavedServer.Create("https://media.example.test", "server-a", "Synthetic\nServer"));
    }

    [Fact]
    public async Task Independent_server_roundtrip_creates_only_a_public_server_document()
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var server = SavedServer.Create("https://media.example.test/proxy", "server-a", "Media Server");

        var saved = await store.UpsertAsync(server, token);
        var restored = await new SavedServerStore(store.Path).LoadAsync(token);

        Assert.Equal(server, Assert.Single(saved));
        Assert.Equal(server, Assert.Single(restored));
        Assert.False(File.Exists(fixture.SettingsPath));
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(store.Path, token));
        Assert.Equal(new[] { "Servers", "Version" }, document.RootElement.EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, document.RootElement.GetProperty("Version").GetInt32());
        var persistedServer = Assert.Single(document.RootElement.GetProperty("Servers").EnumerateArray());
        Assert.Equal(new[] { "ApiRoot", "Name", "ServerId" }, persistedServer.EnumerateObject()
            .Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(server.ApiRoot, persistedServer.GetProperty("ApiRoot").GetString());
        Assert.Equal(server.ServerId, persistedServer.GetProperty("ServerId").GetString());
        Assert.Equal(server.Name, persistedServer.GetProperty("Name").GetString());
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task Missing_store_load_and_removal_do_not_create_directories_or_account_settings()
    {
        using var fixture = new TemporaryAccountStore();
        var folder = Path.Combine(fixture.DirectoryPath, "not-created");
        var store = new SavedServerStore(Path.Combine(folder, "saved-servers.json"));
        var token = TestContext.Current.CancellationToken;

        Assert.Empty(await store.LoadAsync(token));
        Assert.Empty(await new SavedServerStore(store.Path).LoadAsync(token));
        Assert.Empty(await store.RemoveAsync("server-a", token));

        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(store.Path));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task Server_operations_do_not_read_or_modify_unavailable_account_settings()
    {
        using var fixture = new TemporaryAccountStore();
        var token = TestContext.Current.CancellationToken;
        var accountBytes = Encoding.UTF8.GetBytes("{\"AccessToken\":\"synthetic-account-token\",\"Password\":\"synthetic-password\"}");
        await File.WriteAllBytesAsync(fixture.SettingsPath, accountBytes, token);
        var store = Store(fixture);

        await store.UpsertAsync(SavedServer.Create("https://media.example.test", "server-a"), token);
        Assert.Single(await new SavedServerStore(store.Path).LoadAsync(token));
        Assert.Empty(await store.RemoveAsync("server-a", token));

        Assert.Equal(accountBytes, await File.ReadAllBytesAsync(fixture.SettingsPath, token));
        var json = await File.ReadAllTextAsync(store.Path, token);
        Assert.DoesNotContain("synthetic-account-token", json);
        Assert.DoesNotContain("synthetic-password", json);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Servers\":[]}")]
    [InlineData("{\"Version\":1}")]
    [InlineData("{\"Version\":1,\"Servers\":null}")]
    [InlineData("{\"Version\":1,\"Servers\":[null]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/\",\"ServerId\":\"server-a\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/\",\"Name\":\"Media Server\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ServerId\":\"server-a\",\"Name\":\"Media Server\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[],\"AccessToken\":\"synthetic-token\"}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/\",\"ServerId\":\"server-a\",\"Name\":\"Media Server\",\"Password\":\"synthetic-password\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/\",\"ServerId\":\"server-a\",\"Name\":\"Media Server\",\"ProtectedToken\":\"synthetic-token\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/\",\"ServerId\":\"server-a\",\"Name\":\"Media Server\",\"UserId\":\"user-a\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://demo:synthetic-password@media.example.test/emby/\",\"ServerId\":\"server-a\",\"Name\":\"Media Server\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/?api_key=synthetic-token\",\"ServerId\":\"server-a\",\"Name\":\"Media Server\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test/emby/\",\"ServerId\":\" \",\"Name\":\"Media Server\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://a.example.test/emby/\",\"ServerId\":\"server-a\",\"Name\":\"A\"},{\"ApiRoot\":\"https://b.example.test/emby/\",\"ServerId\":\"server-a\",\"Name\":\"B\"}]}")]
    [InlineData("{\"Version\":1,\"Servers\":[{\"ApiRoot\":\"https://media.example.test\",\"ServerId\":\"server-a\",\"Name\":\"A\"},{\"ApiRoot\":\"https://media.example.test/emby/\",\"ServerId\":\"server-b\",\"Name\":\"B\"}]}")]
    public async Task Corrupt_or_credential_bearing_documents_are_preserved_and_block_all_updates(string json)
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var before = Encoding.UTF8.GetBytes(json);
        await File.WriteAllBytesAsync(store.Path, before, token);

        var loadError = await Assert.ThrowsAsync<SavedServerStoreException>(() => store.LoadAsync(token));
        var upsertError = await Assert.ThrowsAsync<SavedServerStoreException>(() =>
            store.UpsertAsync(SavedServer.Create("https://new.example.test", "server-new"), token));
        var removeError = await Assert.ThrowsAsync<SavedServerStoreException>(() => store.RemoveAsync("server-a", token));

        Assert.Equal(SavedServerStoreError.InvalidData, loadError.Error);
        Assert.Equal(SavedServerStoreError.InvalidData, upsertError.Error);
        Assert.Equal(SavedServerStoreError.InvalidData, removeError.Error);
        Assert.Null(loadError.InnerException);
        Assert.DoesNotContain("synthetic-password", loadError.ToString());
        Assert.DoesNotContain("synthetic-token", loadError.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(store.Path, token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Theory]
    [InlineData("Bytes")]
    [InlineData("Servers")]
    public async Task Oversized_existing_documents_are_rejected_without_replacing_the_evidence(string limit)
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var bytes = limit == "Bytes"
            ? Encoding.UTF8.GetBytes("{\"Version\":1,\"Servers\":[]}" + new string(' ', 512 * 1024))
            : JsonSerializer.SerializeToUtf8Bytes(new SavedServerDocument
            {
                Servers = Enumerable.Range(0, 129).Select(index =>
                    SavedServer.Create($"https://server-{index}.example.test", $"server-{index}")).ToList()
            }, SavedServerJsonContext.Default.SavedServerDocument);
        await File.WriteAllBytesAsync(store.Path, bytes, token);

        var error = await Assert.ThrowsAsync<SavedServerStoreException>(() => store.LoadAsync(token));

        Assert.Equal(SavedServerStoreError.InvalidData, error.Error);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.Path, token));
    }

    [Fact]
    public async Task Updating_an_identity_or_address_and_removing_one_server_retains_the_other_servers()
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var first = SavedServer.Create("https://first.example.test", "server-a", "First");
        var other = SavedServer.Create("https://other.example.test", "server-b", "Other");
        var third = SavedServer.Create("https://third.example.test", "server-c", "Third");
        await store.UpsertAsync(first, token);
        await store.UpsertAsync(other, token);
        await store.UpsertAsync(third, token);

        var moved = SavedServer.Create("https://moved.example.test/proxy", first.ServerId, "Renamed");
        var movedServers = await store.UpsertAsync(moved, token);
        Assert.Equal(new[] { other, third, moved }, movedServers);
        var replacement = SavedServer.Create(third.ApiRoot, "server-d", "Replacement");
        var replacedServers = await store.UpsertAsync(replacement, token);
        Assert.Equal(new[] { other, moved, replacement }, replacedServers);

        var remaining = await store.RemoveAsync(moved.ServerId, token);

        Assert.Equal(new[] { other, replacement }, remaining);
        Assert.Equal(remaining, await new SavedServerStore(store.Path).LoadAsync(token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task A_full_server_list_allows_replacement_but_rejects_new_servers_without_writing()
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var servers = Enumerable.Range(0, 128).Select(index =>
            SavedServer.Create($"https://server-{index}.example.test", $"server-{index}")).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SavedServerDocument { Servers = servers },
            SavedServerJsonContext.Default.SavedServerDocument);
        await File.WriteAllBytesAsync(store.Path, bytes, token);
        var replacement = SavedServer.Create("https://moved.example.test", "server-0", "Moved Server");

        var replaced = await store.UpsertAsync(replacement, token);
        Assert.Equal(128, replaced.Count);
        Assert.Equal(replacement, Assert.Single(replaced, server => server.ServerId == "server-0"));
        Assert.Contains(servers[127], replaced);
        var before = await File.ReadAllBytesAsync(store.Path, token);

        var error = await Assert.ThrowsAsync<SavedServerStoreException>(() =>
            store.UpsertAsync(SavedServer.Create("https://new.example.test", "server-new"), token));

        Assert.Equal(SavedServerStoreError.WriteFailed, error.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(store.Path, token));
        Assert.Equal(replaced, await new SavedServerStore(store.Path).LoadAsync(token));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Theory]
    [InlineData("https://demo:synthetic-password@media.example.test/emby/")]
    [InlineData("https://media.example.test/emby/?api_key=synthetic-token")]
    [InlineData("https://media.example.test/emby/#synthetic-token")]
    [InlineData("file:///C:/synthetic-server")]
    public async Task Directly_constructed_unsafe_records_cannot_replace_valid_saved_servers(string apiRoot)
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var original = SavedServer.Create("https://media.example.test", "server-a");
        await store.UpsertAsync(original, token);
        var before = await File.ReadAllBytesAsync(store.Path, token);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            store.UpsertAsync(new SavedServer(apiRoot, "server-a", "Media Server"), token));

        Assert.DoesNotContain("synthetic-password", error.ToString());
        Assert.DoesNotContain("synthetic-token", error.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(store.Path, token));
        Assert.Equal(original, Assert.Single(await store.LoadAsync(token)));
    }

    [Fact]
    public async Task Precanceled_operations_preserve_the_previous_file_and_leave_no_partial_files()
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        var token = TestContext.Current.CancellationToken;
        var original = SavedServer.Create("https://media.example.test", "server-a");
        await store.UpsertAsync(original, token);
        var before = await File.ReadAllBytesAsync(store.Path, token);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpsertAsync(
            SavedServer.Create("https://new.example.test", "server-b"), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveAsync(original.ServerId, canceled.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(store.Path, token));
        Assert.Equal(original, Assert.Single(await store.LoadAsync(token)));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
    }

    [Fact]
    public async Task A_precanceled_first_write_does_not_create_the_store_directory()
    {
        using var fixture = new TemporaryAccountStore();
        var folder = Path.Combine(fixture.DirectoryPath, "not-created");
        var store = new SavedServerStore(Path.Combine(folder, "saved-servers.json"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.UpsertAsync(SavedServer.Create("https://media.example.test", "server-a"), canceled.Token));

        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    [Fact]
    public async Task A_directory_at_the_store_path_blocks_writing_and_partial_files_are_removed()
    {
        using var fixture = new TemporaryAccountStore();
        var store = Store(fixture);
        Directory.CreateDirectory(store.Path);

        var error = await Assert.ThrowsAsync<SavedServerStoreException>(() => store.UpsertAsync(
            SavedServer.Create("https://media.example.test", "server-a"), TestContext.Current.CancellationToken));

        Assert.Equal(SavedServerStoreError.WriteFailed, error.Error);
        Assert.True(Directory.Exists(store.Path));
        Assert.Empty(Directory.GetFiles(fixture.DirectoryPath, "*.tmp"));
        Assert.False(File.Exists(fixture.SettingsPath));
    }

    private static SavedServerStore Store(TemporaryAccountStore fixture) =>
        new(Path.Combine(fixture.DirectoryPath, "saved-servers.json"));
}
