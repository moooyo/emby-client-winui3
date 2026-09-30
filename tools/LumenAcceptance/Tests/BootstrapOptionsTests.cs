using Xunit;

namespace EmbyClient.LumenAcceptance.Tests;

public sealed class BootstrapOptionsTests
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "lumen-options-workspace");
    private static readonly string Run = Path.Combine(Workspace, "artifacts", "lumen-acceptance", "fresh-run");

    [Theory]
    [InlineData("http://127.0.0.1:18984")]
    [InlineData("http://127.0.0.1:18984/")]
    [InlineData("http://127.0.0.1:18984/emby")]
    [InlineData("http://127.0.0.1:18984/emby/")]
    public void Accepted_fixture_URLs_normalize_to_loopback_origin(string address)
    {
        var options = BootstrapOptions.Parse(Arguments(address));
        Assert.Equal("http://127.0.0.1:18984/", options.ServerUrl.AbsoluteUri);
        Assert.Equal("Dark", options.Theme);
        Assert.Equal(Path.Combine(Run, "profile"), options.ProfileDirectory);
    }

    [Theory]
    [InlineData("https://127.0.0.1:18984")]
    [InlineData("http://localhost:18984")]
    [InlineData("http://0.0.0.0:18984")]
    [InlineData("http://[::1]:18984")]
    [InlineData("http://192.0.2.1:18984")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://demo:demo@127.0.0.1:18984")]
    [InlineData("http://127.0.0.1:18984?api_key=test-only")]
    [InlineData("http://127.0.0.1:18984/#fragment")]
    [InlineData("http://127.0.0.1:18984/another-root")]
    public void Unsafe_or_ambiguous_fixture_URLs_are_rejected(string address) =>
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(Arguments(address)));

    [Theory]
    [InlineData("--unknown", "value")]
    [InlineData("--theme", "System")]
    [InlineData("--server-url", "http://127.0.0.1:18984")]
    public void Unknown_invalid_and_duplicate_options_are_rejected(string name, string value) =>
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([.. Arguments(), name, value]));

    [Fact]
    public void Missing_option_values_are_rejected() =>
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([.. Arguments(), "--theme"]));

    [Fact]
    public void Relative_run_paths_are_rejected() => Assert.Throws<ArgumentException>(() =>
        BootstrapOptions.Parse(["--workspace-root", Workspace, "--run-directory", "relative-run", "--server-url", "http://127.0.0.1:18984"]));

    [Fact]
    public void The_acceptance_root_itself_is_not_a_run_directory() => Assert.Throws<ArgumentException>(() =>
        BootstrapOptions.Parse(["--workspace-root", Workspace, "--run-directory", Path.GetDirectoryName(Run)!, "--server-url", "http://127.0.0.1:18984"]));

    [Fact]
    public void A_sibling_with_the_same_prefix_is_not_inside_the_acceptance_root()
    {
        var outside = Path.Combine(Workspace, "artifacts", "lumen-acceptance-sibling", "run");
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            ["--workspace-root", Workspace, "--run-directory", outside, "--server-url", "http://127.0.0.1:18984"]));
    }

    [Fact]
    public void Parent_traversal_cannot_escape_the_acceptance_root()
    {
        var outside = Path.Combine(Run, "..", "..", "outside-run");
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            ["--workspace-root", Workspace, "--run-directory", outside, "--server-url", "http://127.0.0.1:18984"]));
    }

    [Fact]
    public void Light_theme_is_an_explicit_valid_override() =>
        Assert.Equal("Light", BootstrapOptions.Parse([.. Arguments(), "--theme", "Light"]).Theme);

    [Fact]
    public void Design_mode_requires_explicit_allowlisted_identity()
    {
        var ordinary = BootstrapOptions.Parse(Arguments());
        Assert.False(ordinary.DesignCatalog);
        Assert.Equal(BootstrapOptions.FixtureServerId, ordinary.ExpectedFixtureServerId);
        var design = BootstrapOptions.Parse([.. Arguments(), "--design-catalog", "true"]);
        Assert.True(design.DesignCatalog);
        Assert.Equal(BootstrapOptions.DesignFixtureServerId, design.ExpectedFixtureServerId);
        Assert.Equal("SYNTHETIC Lumen Design Catalog", design.ExpectedFixtureServerName);
        Assert.False(BootstrapOptions.Parse([.. Arguments(), "--design-catalog", "false"]).DesignCatalog);
    }

    [Fact]
    public void Ambiguous_design_mode_and_official_mode_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([.. Arguments(), "--design-catalog", "yes"]));
        var credentials = Path.Combine(Workspace, "artifacts", "lumen-official-validation", "credentials.json");
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            [.. Arguments(), "--design-catalog", "true", "--official-credentials-file", credentials]));
    }

    [Fact]
    public void Official_mode_requires_a_dedicated_backend_credentials_path()
    {
        var approved = Path.Combine(Workspace, "artifacts", "lumen-official-validation", "credentials.json");
        Assert.Equal(approved, BootstrapOptions.Parse([.. Arguments(), "--official-credentials-file", approved]).OfficialCredentialsFile);
        var outside = Path.Combine(Workspace, "artifacts", "unrelated", "credentials.json");
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([.. Arguments(), "--official-credentials-file", outside]));
    }

    [Fact]
    public void Existing_profiles_and_receipts_are_never_overwritten()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "lumen-safety-" + Guid.NewGuid().ToString("N"));
        var run = Path.Combine(workspace, "artifacts", "lumen-acceptance", "test-run");
        Directory.CreateDirectory(run);
        try
        {
            File.WriteAllText(Path.Combine(workspace, "EmbyClient.slnx"), "synthetic test marker");
            var options = BootstrapOptions.Parse(
                ["--workspace-root", workspace, "--run-directory", run, "--server-url", "http://127.0.0.1:18984"]);
            options.CheckOwnedOutput();
            Directory.CreateDirectory(options.ProfileDirectory);
            var profileError = Assert.Throws<InvalidOperationException>(options.CheckOwnedOutput);
            Assert.Contains("fresh", profileError.Message);
            Directory.Delete(options.ProfileDirectory);
            File.WriteAllText(options.ReceiptPath, "synthetic receipt marker");
            Assert.Throws<InvalidOperationException>(options.CheckOwnedOutput);
            Assert.Equal("synthetic receipt marker", File.ReadAllText(options.ReceiptPath));
        }
        finally
        {
            Assert.True(BootstrapOptions.IsStrictChild(workspace, Path.GetTempPath()));
            Assert.StartsWith("lumen-safety-", Path.GetFileName(workspace));
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static string[] Arguments(string address = "http://127.0.0.1:18984") =>
        ["--workspace-root", Workspace, "--run-directory", Run, "--server-url", address];
}
