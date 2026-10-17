using System.Text.Json;
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

    [Theory]
    [InlineData(1)]
    [InlineData(5000)]
    [InlineData(10000)]
    public void Large_library_mode_requires_an_explicit_bounded_count(int count)
    {
        var value = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var options = BootstrapOptions.Parse([.. Arguments(), "--large-library-items", value]);
        Assert.Equal(count, options.LargeLibraryItems);
        Assert.False(options.DesignCatalog);
        Assert.Null(options.OfficialCredentialsFile);
        Assert.Equal("synthetic-large-server-" + value, options.ExpectedFixtureServerId);
        Assert.Equal("SYNTHETIC Large Library (" + value + ")", options.ExpectedFixtureServerName);
        Assert.Equal(0, BootstrapOptions.Parse(Arguments()).LargeLibraryItems);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("10001")]
    [InlineData("5,000")]
    [InlineData(" 5000")]
    [InlineData("5000.0")]
    [InlineData("5000x")]
    public void Invalid_large_library_counts_are_rejected(string count) =>
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse([.. Arguments(), "--large-library-items", count]));

    [Fact]
    public void Large_library_mode_cannot_mix_with_design_or_official_modes()
    {
        var credentials = Path.Combine(Workspace, "artifacts", "lumen-official-validation", "credentials.json");
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            [.. Arguments(), "--large-library-items", "5000", "--design-catalog", "true"]));
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            [.. Arguments(), "--large-library-items", "5000", "--official-credentials-file", credentials]));
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            [.. Arguments(), "--large-library-items", "5000", "--large-library-items", "5000"]));
    }

    [Theory]
    [InlineData("http://localhost:19010")]
    [InlineData("http://127.0.0.1:19010?api_key=test-only")]
    [InlineData("http://192.0.2.1:19010")]
    public void Large_library_mode_preserves_the_loopback_URL_boundary(string address) =>
        Assert.Throws<ArgumentException>(() => BootstrapOptions.Parse(
            [.. Arguments(address), "--large-library-items", "5000"]));

    [Theory]
    [InlineData(false, 0, "synthetic-lumen-server-0001", true)]
    [InlineData(true, 0, "synthetic-lumen-design-server-0001", true)]
    [InlineData(false, 5000, "synthetic-large-server-5000", false)]
    public void Synthetic_catalog_statistics_must_match_the_selected_mode(bool design, int count, string serverId, bool lumen)
    {
        var arguments = Arguments().ToList();
        if (design) arguments.AddRange(["--design-catalog", "true"]);
        if (count > 0) arguments.AddRange(["--large-library-items", count.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var options = BootstrapOptions.Parse([.. arguments]);
        using var statistics = JsonDocument.Parse($$"""
            {"Synthetic":true,"LumenCatalog":{{lumen.ToString().ToLowerInvariant()}},
             "LumenDesignCatalog":{{design.ToString().ToLowerInvariant()}},
             "LargeLibraryItems":{{count}},"ServerId":"{{serverId}}"}
            """);
        options.VerifySyntheticCatalogStatistics(statistics.RootElement);
    }

    [Theory]
    [InlineData("""{"Synthetic":false,"LumenCatalog":false,"LumenDesignCatalog":false,"LargeLibraryItems":5000,"ServerId":"synthetic-large-server-5000"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":true,"LumenDesignCatalog":false,"LargeLibraryItems":5000,"ServerId":"synthetic-large-server-5000"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":false,"LumenDesignCatalog":true,"LargeLibraryItems":5000,"ServerId":"synthetic-large-server-5000"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":false,"LumenDesignCatalog":false,"LargeLibraryItems":4999,"ServerId":"synthetic-large-server-5000"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":false,"LumenDesignCatalog":false,"LargeLibraryItems":5000,"ServerId":"synthetic-lumen-server-0001"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":false,"LumenDesignCatalog":false,"LargeLibraryItems":"5000","ServerId":"synthetic-large-server-5000"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":"false","LumenDesignCatalog":false,"LargeLibraryItems":5000,"ServerId":"synthetic-large-server-5000"}""")]
    [InlineData("""{"Synthetic":true,"LumenCatalog":false,"LumenDesignCatalog":false,"ServerId":"synthetic-large-server-5000"}""")]
    public void Large_library_statistics_reject_changed_identity_count_and_catalog_flags(string json)
    {
        var options = BootstrapOptions.Parse([.. Arguments(), "--large-library-items", "5000"]);
        using var statistics = JsonDocument.Parse(json);
        Assert.Throws<InvalidOperationException>(() => options.VerifySyntheticCatalogStatistics(statistics.RootElement));
    }

    [Fact]
    public void A_large_library_is_not_silently_accepted_by_rich_mode()
    {
        using var statistics = JsonDocument.Parse("""
            {"Synthetic":true,"LumenCatalog":false,"LumenDesignCatalog":false,
             "LargeLibraryItems":5000,"ServerId":"synthetic-large-server-5000"}
            """);
        Assert.Throws<InvalidOperationException>(() => BootstrapOptions.Parse(Arguments())
            .VerifySyntheticCatalogStatistics(statistics.RootElement));
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
