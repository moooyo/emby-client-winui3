using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class ResumeDefaultsContractTests
{
    [Theory]
    [InlineData("missing", "Video")]
    [InlineData("empty", "Video")]
    [InlineData("audio", "Audio")]
    [InlineData("mixed", "Video,Audio")]
    public async Task Explicit_resume_queries_default_video_without_overwriting_supplied_media_types(string scenario, string expected)
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"Items\":[],\"TotalRecordCount\":0}");
        string[]? types = scenario switch
        {
            "missing" => null,
            "empty" => [],
            "audio" => ["Audio"],
            "mixed" => ["Video", "Audio"],
            _ => throw new InvalidOperationException("The test scenario is not implemented.")
        };
        var query = new ItemQuery
        {
            ParentId = "series/a?scope=all",
            Recursive = true,
            IncludeItemTypes = ["Episode"],
            MediaTypes = types,
            Limit = 16,
            Fields = ["Overview", "PrimaryImageAspectRatio"]
        };

        await context.Client.GetResumeItemsAsync(query, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Users/user-a/Items/Resume", request.Uri.AbsolutePath);
        var actual = request.Query();
        Assert.Equal(expected, actual["MediaTypes"]);
        Assert.Equal("series/a?scope=all", actual["ParentId"]);
        Assert.Equal("true", actual["Recursive"]);
        Assert.Equal("Episode", actual["IncludeItemTypes"]);
        Assert.Equal("16", actual["Limit"]);
        Assert.Equal("Overview,PrimaryImageAspectRatio", actual["Fields"]);
        Assert.Same(types, query.MediaTypes);
        Assert.False(actual.ContainsKey("scope"));
    }

    [Fact]
    public async Task A_default_resume_request_keeps_its_bounded_video_page()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"Items\":[],\"TotalRecordCount\":0}");

        await context.Client.GetResumeItemsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var query = Assert.Single(context.Handler.Requests).Query();
        Assert.Equal("Video", query["MediaTypes"]);
        Assert.Equal("20", query["Limit"]);
    }
}
