using EmbyClient.App.Services;

namespace EmbyClient.Platform.Tests;

internal sealed class TemporaryAccountStore : IDisposable
{
    private readonly string _testRoot;

    public TemporaryAccountStore()
    {
        _testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EmbyClient.Platform.Tests"));
        DirectoryPath = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        SettingsPath = Path.Combine(DirectoryPath, "settings.json");
        Store = new AccountStore(SettingsPath);
    }

    public string DirectoryPath { get; }
    public string SettingsPath { get; }
    public AccountStore Store { get; }

    public static SavedAccount Account(string serverId = "server-a", string userId = "user-a",
        string protectedToken = "") => new()
    {
        Key = AccountStore.CreateAccountKey(serverId, userId),
        ServerId = serverId,
        ServerName = "Test Server",
        ApiRoot = "https://example.test/proxy/emby/",
        UserId = userId,
        UserName = "Test User",
        ProtectedToken = protectedToken
    };

    public void Dispose()
    {
        var path = Path.GetFullPath(DirectoryPath);
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!path.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetDirectoryName(path), _testRoot, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(path), "N", out _))
        {
            throw new InvalidOperationException("Refusing to clean a directory outside this test's temporary boundary.");
        }

        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(_testRoot) & FileAttributes.ReparsePoint) != 0) return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A locked test artifact can remain for diagnosis; never mask the original assertion.
        }
        catch (UnauthorizedAccessException)
        {
            // The directory contains synthetic test data only and can be removed manually.
        }
    }
}
