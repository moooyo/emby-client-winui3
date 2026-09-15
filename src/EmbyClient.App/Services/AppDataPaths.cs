namespace EmbyClient.App.Services;

/// <summary>Resolves the optional process-scoped directory for application data.</summary>
public static class AppDataPaths
{
    public const string RootEnvironmentVariable = "EMBY_CLIENT_DATA_ROOT";

    public static string RootDirectory => Resolve(
        Environment.GetEnvironmentVariable(RootEnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Normalizes a supplied root without creating directories or reading application data.</summary>
    public static string Resolve(string? configuredRoot, string localApplicationData) => Path.GetFullPath(
        string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(localApplicationData, "EmbyClient.Windows")
            : configuredRoot);
}
