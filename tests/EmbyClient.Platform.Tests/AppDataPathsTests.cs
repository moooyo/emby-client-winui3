using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AppDataPathsTests
{
    private static string TestRoot => Path.Combine(Path.GetTempPath(), "EmbyClient.AppDataPaths.Tests");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void An_unset_override_preserves_the_default_product_directory(string? configuredRoot)
    {
        var localApplicationData = Path.Combine(TestRoot, "local");

        Assert.Equal(Path.GetFullPath(Path.Combine(localApplicationData, "EmbyClient.Windows")),
            AppDataPaths.Resolve(configuredRoot, localApplicationData));
    }

    [Fact]
    public void A_configured_root_is_used_directly_without_appending_the_product_directory()
    {
        var configuredRoot = Path.Combine(TestRoot, "isolated", "run-01");

        Assert.Equal(Path.GetFullPath(configuredRoot), AppDataPaths.Resolve(configuredRoot, "\0"));
    }

    [Fact]
    public void A_configured_root_is_normalized_before_storage_paths_are_combined()
    {
        var configuredRoot = Path.Combine(TestRoot, "isolated", "unused", "..", "run-02");

        Assert.Equal(Path.GetFullPath(Path.Combine(TestRoot, "isolated", "run-02")),
            AppDataPaths.Resolve(configuredRoot, Path.Combine(TestRoot, "local")));
    }

    [Fact]
    public void An_invalid_override_does_not_silently_fall_back_to_existing_user_data()
    {
        Assert.Throws<ArgumentException>(() => AppDataPaths.Resolve("\0", Path.Combine(TestRoot, "local")));
    }
}
