using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class WindowsActivationRouteTests
{
    private static readonly string AccountA = new('A', 64);
    private static readonly string AccountB = new('B', 64);

    [Theory]
    [InlineData(WindowsActivationDestination.Home, "home")]
    [InlineData(WindowsActivationDestination.Library, "library")]
    [InlineData(WindowsActivationDestination.Search, "search")]
    public void Known_shell_tasks_round_trip_without_account_data(WindowsActivationDestination destination, string name)
    {
        var route = new WindowsActivationRoute(destination);

        Assert.Equal($"--lumen-route {name}", route.ToArguments());
        Assert.True(WindowsActivationRoute.TryParseArguments(route.ToArguments().Split(' '), out var parsed));
        Assert.Equal(route, parsed);
        Assert.True(parsed!.MatchesAccount(null));
    }

    [Fact]
    public void Continuation_normalizes_the_account_scope_and_rejects_another_signed_in_account()
    {
        Assert.True(WindowsActivationRoute.TryParseArguments(
            ["--lumen-route", "continue", "--lumen-account", AccountA.ToLowerInvariant(), "--lumen-item", "123_456-7"], out var route));

        Assert.Equal(AccountA, route!.AccountKey);
        Assert.True(route.MatchesAccount(AccountA));
        Assert.False(route.MatchesAccount(AccountB));
        Assert.False(route.MatchesAccount(null));
        Assert.Equal($"--lumen-route continue --lumen-account {AccountA} --lumen-item 123_456-7", route.ToArguments());
    }

    [Theory]
    [InlineData("https://server/Items/123?api_key=secret")]
    [InlineData("123&api_key=secret")]
    [InlineData("123 --token secret")]
    [InlineData("../123")]
    [InlineData("123\r\n456")]
    [InlineData("")]
    public void Continuation_rejects_urls_secrets_and_command_syntax_in_media_identity(string itemId)
    {
        Assert.False(WindowsActivationRoute.TryParseArguments(
            ["--lumen-route", "continue", "--lumen-account", AccountA, "--lumen-item", itemId], out var route));
        Assert.Null(route);
    }

    [Fact]
    public void Unknown_duplicate_or_unscoped_arguments_do_not_become_a_home_fallback()
    {
        IReadOnlyList<string>[] invalid =
        [
            [],
            ["--lumen-route", "unknown"],
            ["--lumen-route", "search", "--lumen-route", "home"],
            ["--lumen-route", "home", "--lumen-account", AccountA],
            ["--lumen-route", "continue", "--lumen-item", "123"],
            ["--lumen-route", "continue", "--lumen-account", "123", "--lumen-item", "456"],
            ["--lumen-route", "search", "--api-key", "secret"],
            ["--lumen-route", "home", "unexpected"]
        ];

        foreach (var arguments in invalid)
        {
            Assert.False(WindowsActivationRoute.TryParseArguments(arguments, out var route));
            Assert.Null(route);
        }
    }

    [Fact]
    public void Protocol_activation_uses_the_same_account_scope_validation()
    {
        var uri = new Uri($"lumen-emby://open/continue?account={AccountA.ToLowerInvariant()}&item=123");

        Assert.True(WindowsActivationRoute.TryParseUri(uri, out var route));
        Assert.Equal(new WindowsActivationRoute(WindowsActivationDestination.ContinueWatching, AccountA, "123"), route);
    }

    [Theory]
    [InlineData("https://open/home")]
    [InlineData("lumen-emby://server/home")]
    [InlineData("lumen-emby://user@open/home")]
    [InlineData("lumen-emby://open/home?token=secret")]
    [InlineData("lumen-emby://open/search#secret")]
    [InlineData("lumen-emby://open:123/home")]
    [InlineData("lumen-emby://open/home?item=123")]
    [InlineData("lumen-emby://open/search?item=123&item=456")]
    public void Protocol_activation_rejects_extra_authority_and_unrecognized_query_data(string uri)
    {
        Assert.False(WindowsActivationRoute.TryParseUri(new Uri(uri), out var route));
        Assert.Null(route);
    }

    [Fact]
    public void Invalid_programmatic_routes_cannot_publish_activation_arguments()
    {
        var route = new WindowsActivationRoute(WindowsActivationDestination.ContinueWatching, AccountA, "https://server");

        Assert.False(route.IsValid);
        Assert.Throws<InvalidOperationException>(route.ToArguments);
    }
}
