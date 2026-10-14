using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class AlphabetContractTests
{
    [Fact]
    public async Task Prefixes_use_a_user_scoped_bare_array_and_preserve_active_filters()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            [{"Name":"A","Value":"opaque/first?index=42+#%","FuturePrefixField":true},{"name":"#"}]
            """);
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=all");
        var query = new ItemQuery
        {
            ParentId = "library/a?UserId=another-user",
            Recursive = true,
            IncludeItemTypes = ["Movie", "Series"],
            MediaTypes = ["Video"],
            PersonIds = ["person/a?scope=1"],
            Genres = ["Drama & mystery", "Science fiction"],
            Tags = ["featured+#%", "watch later"],
            Filters = ["IsFavorite", "IsUnplayed"],
            IsPlayed = false,
            IsFavorite = true,
            SearchTerm = "A & B+#%",
            NameStartsWith = "A",
            NameStartsWithOrGreater = "A?Limit=1",
            NameLessThan = "Z&UserId=another-user",
            StartIndex = 25,
            Limit = 7,
            SortBy = ["SortName"],
            SortOrder = ["Ascending"]
        };

        var prefixes = await client.GetItemPrefixesAsync(query, TestContext.Current.CancellationToken);

        Assert.Equal(2, prefixes.Length);
        Assert.Equal("A", prefixes[0].Name);
        Assert.Equal("opaque/first?index=42+#%", prefixes[0].Value);
        Assert.Equal("#", prefixes[1].Name);
        Assert.Null(prefixes[1].Value);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Items/Prefixes", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        var actual = request.Query();
        Assert.Equal("user/a?scope=all", actual["UserId"]);
        Assert.Equal(query.ParentId, actual["ParentId"]);
        Assert.Equal("true", actual["Recursive"]);
        Assert.Equal("Movie,Series", actual["IncludeItemTypes"]);
        Assert.Equal("Video", actual["MediaTypes"]);
        Assert.Equal("person/a?scope=1", actual["PersonIds"]);
        Assert.Equal("Drama & mystery|Science fiction", actual["Genres"]);
        Assert.Equal("featured+#%|watch later", actual["Tags"]);
        Assert.Equal("IsFavorite,IsUnplayed", actual["Filters"]);
        Assert.Equal("false", actual["IsPlayed"]);
        Assert.Equal("true", actual["IsFavorite"]);
        Assert.Equal(query.SearchTerm, actual["SearchTerm"]);
        Assert.Equal(query.NameStartsWith, actual["NameStartsWith"]);
        Assert.Equal(query.NameStartsWithOrGreater, actual["NameStartsWithOrGreater"]);
        Assert.Equal(query.NameLessThan, actual["NameLessThan"]);
        Assert.Equal("25", actual["StartIndex"]);
        Assert.Equal("7", actual["Limit"]);
        Assert.Equal("SortName", actual["SortBy"]);
        Assert.Equal("Ascending", actual["SortOrder"]);
        Assert.False(actual.ContainsKey("scope"));
        Assert.Equal("token-b", request.Header("X-Emby-Token"));
        Assert.DoesNotContain("token-b", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Prefixes_allow_an_unpaged_filtered_request_without_mutating_the_query()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("[]");
        var query = new ItemQuery
        {
            ParentId = "library-a",
            StartIndex = null,
            Limit = null,
            Filters = ["IsPlayed"],
            IsPlayed = true
        };

        var prefixes = await context.Client.GetItemPrefixesAsync(query, TestContext.Current.CancellationToken);

        Assert.Empty(prefixes);
        var actual = Assert.Single(context.Handler.Requests).Query();
        Assert.Equal("user-a", actual["UserId"]);
        Assert.Equal("library-a", actual["ParentId"]);
        Assert.Equal("IsPlayed", actual["Filters"]);
        Assert.Equal("true", actual["IsPlayed"]);
        Assert.False(actual.ContainsKey("StartIndex"));
        Assert.False(actual.ContainsKey("Limit"));
        Assert.Null(query.StartIndex);
        Assert.Null(query.Limit);
        Assert.Equal("IsPlayed", Assert.Single(query.Filters!));
    }

    [Theory]
    [InlineData("{\"Items\":[],\"TotalRecordCount\":0}")]
    [InlineData("null")]
    [InlineData("[{\"Name\":\"A\",\"Value\":42}]")]
    public async Task Prefixes_reject_an_unexpected_response_shape_instead_of_an_empty_index(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.GetItemPrefixesAsync(new ItemQuery { Limit = null },
                TestContext.Current.CancellationToken));
    }
}
