namespace EmbyClient.LumenAcceptance;

public sealed record BootstrapOptions(string WorkspaceRoot, string RunDirectory, Uri ServerUrl, string Theme,
    string? OfficialCredentialsFile = null, bool DesignCatalog = false)
{
    public const string FixtureServerId = "synthetic-lumen-server-0001";
    public const string FixtureVersion = "synthetic-1.0";
    public const string FixtureHeader = "EmbyClient development data; not Emby Server";
    public const string DesignFixtureServerId = "synthetic-lumen-design-server-0001";
    public string ExpectedFixtureServerId => DesignCatalog ? DesignFixtureServerId : FixtureServerId;
    public string ExpectedFixtureServerName => DesignCatalog ? "SYNTHETIC Lumen Design Catalog" : "SYNTHETIC Lumen Library";

    public string ProfileDirectory => Path.Combine(RunDirectory, "profile");
    public string ReceiptPath => Path.Combine(RunDirectory, "bootstrap.json");

    public static BootstrapOptions Parse(string[] arguments)
    {
        string? workspaceRoot = null;
        string? runDirectory = null;
        string? serverUrl = null;
        string? officialCredentialsFile = null;
        var theme = "Dark";
        var designCatalog = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var name = arguments[index];
            if (!seen.Add(name) || index + 1 >= arguments.Length)
                throw new ArgumentException("Every bootstrap option must appear once with a value.");
            var value = arguments[++index];
            switch (name)
            {
                case "--workspace-root": workspaceRoot = NormalizeAbsolutePath(value); break;
                case "--run-directory": runDirectory = NormalizeAbsolutePath(value); break;
                case "--server-url": serverUrl = value; break;
                case "--official-credentials-file": officialCredentialsFile = NormalizeAbsolutePath(value); break;
                case "--theme" when value is "Dark" or "Light": theme = value; break;
                case "--design-catalog" when value is "true" or "false": designCatalog = value == "true"; break;
                default: throw new ArgumentException("Unknown bootstrap option or unsupported value.");
            }
        }

        if (workspaceRoot is null || runDirectory is null || serverUrl is null)
            throw new ArgumentException("Workspace, run directory, and server URL are required.");
        var artifactRoot = Path.Combine(workspaceRoot, "artifacts", "lumen-acceptance");
        if (!IsStrictChild(runDirectory, artifactRoot))
            throw new ArgumentException("The run directory must remain inside the workspace acceptance artifacts.");
        if (officialCredentialsFile is not null && !IsStrictChild(officialCredentialsFile,
            Path.Combine(workspaceRoot, "artifacts", "lumen-official-validation")))
            throw new ArgumentException("Official test credentials must stay inside the dedicated owned backend artifacts.");
        if (designCatalog && officialCredentialsFile is not null)
            throw new ArgumentException("Design fixtures cannot be combined with official server credentials.");
        return new(workspaceRoot, runDirectory, NormalizeFixtureUrl(serverUrl), theme, officialCredentialsFile, designCatalog);
    }

    public static bool IsStrictChild(string path, string parent)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static Uri NormalizeFixtureUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1"
            || uri.Port is < 1024 or > 65535 || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath is not ("/" or "/emby" or "/emby/"))
            throw new ArgumentException("The bootstrap accepts only an explicit IPv4 loopback fixture URL.");
        return new Uri(FormattableString.Invariant($"http://127.0.0.1:{uri.Port}/"), UriKind.Absolute);
    }

    public void CheckOwnedOutput()
    {
        if (!File.Exists(Path.Combine(WorkspaceRoot, "EmbyClient.slnx")))
            throw new InvalidOperationException("The supplied workspace is not the client repository.");
        if (Directory.Exists(ProfileDirectory) || File.Exists(ProfileDirectory) || File.Exists(ReceiptPath))
            throw new InvalidOperationException("Select a fresh bootstrap profile and receipt.");
        var ancestor = new DirectoryInfo(RunDirectory);
        while (ancestor is not null && IsStrictChild(ancestor.FullName, WorkspaceRoot))
        {
            if (ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Acceptance output cannot traverse a reparse point.");
            ancestor = ancestor.Parent;
        }
    }

    private static string NormalizeAbsolutePath(string value)
    {
        if (!Path.IsPathFullyQualified(value))
            throw new ArgumentException("Acceptance paths must be absolute.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }
}
