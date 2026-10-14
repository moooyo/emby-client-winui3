using System.Text;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class LanDiscoveryTests
{
    [Fact]
    public void Discovery_uses_the_official_Emby_request_and_port()
    {
        Assert.Equal(7359, ConnectionService.LanDiscoveryPort);
        Assert.Equal("who is EmbyServer?", ConnectionService.LanDiscoveryRequest);
    }

    [Theory]
    [InlineData("{\"Address\":\"http://192.0.2.42:8096\",\"Id\":\"server-a\",\"Name\":\"Living Room\"}",
        "http://192.0.2.42:8096/emby/", "Living Room")]
    [InlineData("{\"address\":\"https://media.example.test/proxy/emby/\",\"id\":\"server-a\",\"name\":\"  Media Server  \",\"EndpointAddress\":\"192.0.2.42\"}",
        "https://media.example.test/proxy/emby/", "Media Server")]
    [InlineData("{\"Address\":\"https://media.example.test/proxy\",\"Id\":\"server-a\"}",
        "https://media.example.test/proxy/emby/", "media.example.test")]
    public void Official_response_fields_produce_a_normalized_public_server_candidate(
        string json, string expectedRoot, string expectedName)
    {
        var accepted = ConnectionService.TryParseLanDiscoveryResponse(Encoding.UTF8.GetBytes(json), out var server);

        Assert.True(accepted);
        Assert.Equal(new LanDiscoveredServer(expectedRoot, "server-a", expectedName), server);
    }

    [Theory]
    [InlineData("https://demo:synthetic-password@media.example.test/emby/")]
    [InlineData("https://media.example.test/emby/?api_key=synthetic-token")]
    [InlineData("https://media.example.test/emby/#synthetic-token")]
    [InlineData("ftp://media.example.test/emby/")]
    [InlineData("file:///C:/synthetic-server")]
    [InlineData("media.example.test:8096")]
    public void Credential_bearing_or_non_http_addresses_are_not_discovery_candidates(string address)
    {
        var json = $$"""{"Address":"{{address}}","Id":"server-a","Name":"Synthetic Server"}""";

        Assert.False(ConnectionService.TryParseLanDiscoveryResponse(Encoding.UTF8.GetBytes(json), out var server));
        Assert.Null(server);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Address\":\"https://media.example.test\"}")]
    [InlineData("{\"Id\":\"server-a\"}")]
    [InlineData("{\"Address\":null,\"Id\":\"server-a\"}")]
    [InlineData("{\"Address\":\"https://media.example.test\",\"Id\":null}")]
    [InlineData("{\"Address\":\"https://media.example.test\",\"Id\":\" \"}")]
    [InlineData("{\"Address\":\"https://media.example.test\",\"Id\":\" server-a\"}")]
    [InlineData("{\"Address\":\"https://media.example.test\",\"Id\":42}")]
    [InlineData("{\"Address\":{},\"Id\":\"server-a\"}")]
    [InlineData("{\"Address\":\"https://media.example.test\",\"Id\":\"server-a\",\"Name\":\"Synthetic\\nServer\"}")]
    public void Malformed_responses_or_missing_public_identity_are_rejected_without_a_candidate(string json)
    {
        Assert.False(ConnectionService.TryParseLanDiscoveryResponse(Encoding.UTF8.GetBytes(json), out var server));
        Assert.Null(server);
    }

    [Fact]
    public void The_response_size_limit_is_enforced_even_for_otherwise_valid_json()
    {
        const string json = "{\"Address\":\"https://media.example.test\",\"Id\":\"server-a\"}";
        var atLimit = Encoding.UTF8.GetBytes(json + new string(' ', 16 * 1024 - json.Length));
        var overLimit = Encoding.UTF8.GetBytes(json + new string(' ', 16 * 1024 - json.Length + 1));

        Assert.True(ConnectionService.TryParseLanDiscoveryResponse(atLimit, out var accepted));
        Assert.NotNull(accepted);
        Assert.False(ConnectionService.TryParseLanDiscoveryResponse(overLimit, out var rejected));
        Assert.Null(rejected);
    }

    [Fact]
    public void Excessively_nested_unknown_response_fields_are_rejected()
    {
        var json = "{\"Address\":\"https://media.example.test\",\"Id\":\"server-a\",\"Extra\":"
            + new string('[', 9) + "null" + new string(']', 9) + "}";

        Assert.False(ConnectionService.TryParseLanDiscoveryResponse(Encoding.UTF8.GetBytes(json), out var server));
        Assert.Null(server);
    }

    [Fact]
    public void Repeated_responses_collapse_by_public_identity_or_address_and_keep_the_first_candidate()
    {
        var first = new LanDiscoveredServer("https://zulu.example.test/emby/", "server-a", "Zulu");
        var sameIdentity = new LanDiscoveredServer("https://moved.example.test/emby/", "server-a", "Changed Name");
        var sameAddress = new LanDiscoveredServer(first.ApiRoot, "server-changed", "Changed Identity");
        var alphaSecond = new LanDiscoveredServer("https://b.example.test/emby/", "server-b", "alpha");
        var alphaFirst = new LanDiscoveredServer("https://a.example.test/emby/", "server-c", "ALPHA");

        var servers = ConnectionService.DeduplicateLanServers(
            [first, first, sameIdentity, sameAddress, alphaSecond, alphaFirst, alphaSecond]);

        Assert.Equal(new[] { alphaFirst, alphaSecond, first }, servers);
    }
}
