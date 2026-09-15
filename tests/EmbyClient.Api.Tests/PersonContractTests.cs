using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class PersonContractTests
{
    [Fact]
    public async Task Person_works_use_a_comma_separated_filter_with_user_scope_and_pagination()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {"Items":[{"Id":"movie-a","Type":"Movie","Name":"Example film"}],"TotalRecordCount":65}
            """);

        var result = await context.Client.GetItemsAsync(new ItemQuery
        {
            PersonIds = ["person/a?scope=1", "person & second+#%"],
            StartIndex = 40,
            Limit = 20,
            Recursive = true,
            IncludeItemTypes = ["Movie", "Series", "Video", "MusicVideo"],
            SortBy = ["SortName"],
            SortOrder = ["Ascending"]
        }, TestContext.Current.CancellationToken);

        Assert.Equal(65, result.TotalRecordCount);
        Assert.Equal("movie-a", Assert.Single(result.Items).Id);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Users/user-a/Items", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        var query = request.Query();
        Assert.Equal("person/a?scope=1,person & second+#%", query["PersonIds"]);
        Assert.Equal("40", query["StartIndex"]);
        Assert.Equal("20", query["Limit"]);
        Assert.Equal("true", query["Recursive"]);
        Assert.Equal("Movie,Series,Video,MusicVideo", query["IncludeItemTypes"]);
        Assert.Equal("SortName", query["SortBy"]);
        Assert.Equal("Ascending", query["SortOrder"]);
        Assert.Equal("true", query["EnableUserData"]);
        Assert.False(query.ContainsKey("scope"));
        Assert.DoesNotContain("token-a", request.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_absent_or_empty_person_filter_does_not_change_an_ordinary_item_query(bool empty)
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"Items\":[],\"TotalRecordCount\":0}");

        await context.Client.GetItemsAsync(new ItemQuery { PersonIds = empty ? [] : null },
            TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(context.Handler.Requests).Query().ContainsKey("PersonIds"));
    }

    [Fact]
    public async Task Person_works_remain_isolated_when_accounts_request_the_same_person()
    {
        using var context = new ApiTestContext();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            arrived.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return request.Header("X-Emby-Token") == "token-a"
                ? RecordingHandler.Json("{\"Items\":[{\"Id\":\"movie-a\"}],\"TotalRecordCount\":1}")
                : RecordingHandler.Json("{\"Items\":[],\"TotalRecordCount\":0}");
        };
        var secondClient = context.Client.WithAuthentication("token-b", "user/b?scope=all");
        var query = new ItemQuery { PersonIds = ["person-1"], Recursive = true, Limit = 30 };

        var firstRequest = context.Client.GetItemsAsync(query, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondRequest = secondClient.GetItemsAsync(query, TestContext.Current.CancellationToken);
        release.SetResult();
        var results = await Task.WhenAll(firstRequest, secondRequest);

        Assert.Equal("movie-a", Assert.Single(results[0].Items).Id);
        Assert.Empty(results[1].Items);
        var requests = context.Handler.Requests;
        Assert.Equal(2, requests.Length);
        Assert.Equal("/proxy/emby/Users/user-a/Items", requests[0].Uri.AbsolutePath);
        Assert.Equal("/proxy/emby/Users/user%2Fb%3Fscope%3Dall/Items", requests[1].Uri.AbsolutePath);
        Assert.Equal("token-a", requests[0].Header("X-Emby-Token"));
        Assert.Equal("token-b", requests[1].Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", requests[0].Header("X-Emby-Authorization"));
        Assert.Contains("UserId=\"user/b?scope=all\"", requests[1].Header("X-Emby-Authorization"));
        Assert.All(requests, request => Assert.Equal("person-1", request.Query()["PersonIds"]));
        Assert.Equal("user-a", context.Client.UserId);
    }

    [Fact]
    public async Task Person_details_and_cast_metadata_use_the_existing_item_contract()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {
              "Id":"person-1",
              "Name":"Example Person",
              "Type":"Person",
              "Overview":"A performer biography supplied by the server.",
              "PremiereDate":"1984-07-09T00:00:00Z",
              "ImageTags":{"Primary":"portrait-tag"},
              "FutureBiographyField":"ignored"
            }
            """);

        var person = await context.Client.GetItemAsync("person-1", TestContext.Current.CancellationToken);
        context.ReturnJson("""
            {
              "Id":"series-a",
              "Type":"Series",
              "People":[
                {"Id":"person-1","Name":"Example Person","Type":"Actor","Role":"Example Role","PrimaryImageTag":"portrait-tag"},
                {"Id":"person-2","Name":"Example Director","Type":"Director"}
              ]
            }
            """);
        var series = await context.Client.GetItemAsync("series-a", TestContext.Current.CancellationToken);

        Assert.Equal("Person", person.Type);
        Assert.Equal("Example Person", person.Name);
        Assert.Equal("A performer biography supplied by the server.", person.Overview);
        Assert.Equal(new DateTimeOffset(1984, 7, 9, 0, 0, 0, TimeSpan.Zero), person.PremiereDate);
        Assert.Equal("portrait-tag", Assert.IsType<Dictionary<string, string>>(person.ImageTags)["Primary"]);
        Assert.Null(person.People);
        var cast = Assert.IsType<PersonInfo[]>(series.People);
        Assert.Equal(2, cast.Length);
        Assert.Equal(person.Id, cast[0].Id);
        Assert.Equal("Actor", cast[0].Type);
        Assert.Equal("Example Role", cast[0].Role);
        Assert.Equal("portrait-tag", cast[0].PrimaryImageTag);
        Assert.Equal("Director", cast[1].Type);
        Assert.Null(cast[1].Role);
        Assert.Null(cast[1].PrimaryImageTag);
        Assert.Equal("/proxy/emby/Users/user-a/Items/person-1", context.Handler.Requests[0].Uri.AbsolutePath);
        Assert.Equal(string.Empty, context.Handler.Requests[0].Uri.Query);
        Assert.Equal("token-a", context.Handler.Requests[0].Header("X-Emby-Token"));
    }
}
