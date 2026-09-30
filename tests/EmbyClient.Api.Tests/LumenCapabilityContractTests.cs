using System.Net;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class LumenCapabilityContractTests
{
    [Fact]
    public async Task Item_capability_fields_and_future_chapter_markers_use_the_AOT_contract()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {
              "Id":"series-a","OriginalTitle":"Original series","SortName":"Series, The",
              "Tags":["featured"],"SeasonCount":4,"RecursiveItemCount":53,"MovieCount":2,"SeriesCount":1,
              "CanEditItems":true,"LocalTrailerCount":1,
              "RemoteTrailers":[{"Url":"https://video.example/watch?v=trailer-a","Name":"Official trailer"}],
              "Chapters":[{"Name":"Intro","StartPositionTicks":9007199254740993,"MarkerType":"FutureMarker","ChapterIndex":3}],
              "FutureItemCapability":{"Enabled":true,"Values":[null,2]}
            }
            """);

        var item = await context.Client.GetItemAsync("series-a", TestContext.Current.CancellationToken);

        Assert.Equal("Original series", item.OriginalTitle);
        Assert.Equal("Series, The", item.SortName);
        Assert.Equal("featured", Assert.Single(Assert.IsType<string[]>(item.Tags)));
        Assert.Equal(4, item.SeasonCount);
        Assert.Equal(53, item.RecursiveItemCount);
        Assert.Equal(2, item.MovieCount);
        Assert.Equal(1, item.SeriesCount);
        Assert.True(item.CanEditItems);
        Assert.Equal(1, item.LocalTrailerCount);
        var trailer = Assert.Single(Assert.IsType<RemoteTrailer[]>(item.RemoteTrailers));
        Assert.Equal("https://video.example/watch?v=trailer-a", trailer.Url);
        Assert.Equal("Official trailer", trailer.Name);
        var chapter = Assert.Single(Assert.IsType<ChapterInfo[]>(item.Chapters));
        Assert.Equal(9007199254740993L, chapter.StartPositionTicks);
        Assert.Equal("FutureMarker", chapter.MarkerType);
        Assert.Equal(3, chapter.ChapterIndex);
        var extension = Assert.IsType<Dictionary<string, JsonElement>>(item.ExtensionData);
        Assert.True(extension["FutureItemCapability"].GetProperty("Enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, extension["FutureItemCapability"].GetProperty("Values")[0].ValueKind);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public async Task User_edit_permissions_and_optional_playback_preferences_deserialize_without_reflection()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {
              "Id":"user-a",
              "Policy":{"IsAdministrator":true,"EnableUserPreferenceAccess":false,"EnableSubtitleManagement":true},
              "Configuration":{"ResumeRewindSeconds":15,"IntroSkipMode":"FutureMode","FuturePreference":{"Enabled":true}}
            }
            """);

        var user = await context.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        var policy = Assert.IsType<UserPolicy>(user.Policy);
        Assert.True(policy.IsAdministrator);
        Assert.False(policy.EnableUserPreferenceAccess);
        Assert.True(policy.EnableSubtitleManagement);
        var configuration = Assert.IsType<UserConfiguration>(user.Configuration);
        Assert.Equal(15, configuration.ResumeRewindSeconds);
        Assert.Equal("FutureMode", configuration.IntroSkipMode);
        Assert.Null(configuration.EnableNextEpisodeAutoPlay);
        var extension = Assert.IsType<Dictionary<string, JsonElement>>(configuration.ExtensionData);
        Assert.True(extension["FuturePreference"].GetProperty("Enabled").GetBoolean());
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public async Task Similar_items_escape_the_item_path_and_bind_the_current_user()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""{"Items":[{"Id":"similar-a"}],"TotalRecordCount":42}""");
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=1");

        var result = await client.GetSimilarItemsAsync("movie/a?other=1#%", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("similar-a", Assert.Single(result.Items).Id);
        Assert.Equal(42, result.TotalRecordCount);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Items/movie%2Fa%3Fother%3D1%23%25/Similar", request.Uri.AbsolutePath);
        Assert.Equal("user/a?scope=1", request.Query()["UserId"]);
        Assert.Equal("16", request.Query()["Limit"]);
        Assert.False(request.Query().ContainsKey("other"));
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.Equal("token-b", request.Header("X-Emby-Token"));
        Assert.DoesNotContain("token-b", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Local_trailers_use_a_user_scoped_bare_array_contract()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""[{"Id":"trailer-a","Type":"Video","FutureTrailerField":true}]""");
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=1");

        var trailers = await client.GetLocalTrailersAsync("movie/a?other=1#%", TestContext.Current.CancellationToken);

        Assert.Equal("trailer-a", Assert.Single(trailers).Id);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fscope%3D1/Items/movie%2Fa%3Fother%3D1%23%25/LocalTrailers", request.Uri.AbsolutePath);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.Equal("token-b", request.Header("X-Emby-Token"));
        Assert.DoesNotContain("token-b", request.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("items", "/proxy/emby/Users/user-a/Items")]
    [InlineData("genres", "/proxy/emby/Genres")]
    [InlineData("persons", "/proxy/emby/Persons")]
    public async Task Alphabetical_discovery_filters_preserve_paging_and_escaped_query_values(string operation, string path)
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""{"Items":[{"Id":"result-a"}],"TotalRecordCount":700}""");
        var query = new ItemQuery
        {
            ParentId = "library/a?scope=1",
            StartIndex = 20,
            Limit = 500,
            NameStartsWith = "A & B+#%",
            NameStartsWithOrGreater = "A?UserId=another-user",
            NameLessThan = "Z&Limit=1",
            IncludeItemTypes = ["Movie", "Series"],
            Fields = ["Overview", "Tags"],
            SortBy = ["SortName"],
            SortOrder = ["Ascending"]
        };

        var result = operation switch
        {
            "items" => await context.Client.GetItemsAsync(query, TestContext.Current.CancellationToken),
            "genres" => await context.Client.GetGenresAsync(query, TestContext.Current.CancellationToken),
            "persons" => await context.Client.GetPersonsAsync(query, TestContext.Current.CancellationToken),
            _ => throw new InvalidOperationException("The test operation is not implemented.")
        };

        Assert.Equal(700, result.TotalRecordCount);
        Assert.Equal("result-a", Assert.Single(result.Items).Id);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(path, request.Uri.AbsolutePath);
        var actual = request.Query();
        Assert.Equal("library/a?scope=1", actual["ParentId"]);
        Assert.Equal("20", actual["StartIndex"]);
        Assert.Equal("500", actual["Limit"]);
        Assert.Equal("A & B+#%", actual["NameStartsWith"]);
        Assert.Equal("A?UserId=another-user", actual["NameStartsWithOrGreater"]);
        Assert.Equal("Z&Limit=1", actual["NameLessThan"]);
        Assert.Equal("Movie,Series", actual["IncludeItemTypes"]);
        Assert.Equal("Overview,Tags", actual["Fields"]);
        Assert.Equal("SortName", actual["SortBy"]);
        Assert.Equal("Ascending", actual["SortOrder"]);
        if (operation != "items") Assert.Equal("user-a", actual["UserId"]);
        Assert.Equal(string.Empty, request.Uri.Fragment);
    }

    [Theory]
    [InlineData("similar")]
    [InlineData("trailers")]
    [InlineData("genres")]
    [InlineData("persons")]
    public async Task Concurrent_discovery_requests_remain_isolated_between_accounts(string operation)
    {
        using var context = new ApiTestContext();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = async (_, cancellationToken) =>
        {
            arrived.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return RecordingHandler.Json(operation == "trailers" ? "[]" : "{\"Items\":[],\"TotalRecordCount\":0}");
        };
        var second = context.Client.WithAuthentication("token-b", "user/b?scope=all");

        var firstRequest = InvokeDiscoveryAsync(context.Client, operation, TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondRequest = InvokeDiscoveryAsync(second, operation, TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(firstRequest, secondRequest);

        var requests = context.Handler.Requests;
        Assert.Equal(2, requests.Length);
        Assert.Equal("token-a", requests[0].Header("X-Emby-Token"));
        Assert.Equal("token-b", requests[1].Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", requests[0].Header("X-Emby-Authorization"));
        Assert.Contains("UserId=\"user/b?scope=all\"", requests[1].Header("X-Emby-Authorization"));
        if (operation == "trailers")
        {
            Assert.Equal("/proxy/emby/Users/user-a/Items/movie-a/LocalTrailers", requests[0].Uri.AbsolutePath);
            Assert.Equal("/proxy/emby/Users/user%2Fb%3Fscope%3Dall/Items/movie-a/LocalTrailers", requests[1].Uri.AbsolutePath);
        }
        else
        {
            Assert.Equal("user-a", requests[0].Query()["UserId"]);
            Assert.Equal("user/b?scope=all", requests[1].Query()["UserId"]);
        }

        Assert.Equal("user-a", context.Client.UserId);
        Assert.False(context.Http.DefaultRequestHeaders.Contains("X-Emby-Token"));
        Assert.All(requests, request => Assert.DoesNotContain("token-", request.Uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("similar", -1)]
    [InlineData("similar", 0)]
    [InlineData("similar", 501)]
    [InlineData("genres", -1)]
    [InlineData("genres", 0)]
    [InlineData("genres", 501)]
    [InlineData("persons", -1)]
    [InlineData("persons", 0)]
    [InlineData("persons", 501)]
    public async Task Discovery_rejects_unbounded_or_nonpositive_page_sizes_before_transport(string operation, int limit)
    {
        using var context = new ApiTestContext();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => operation switch
        {
            "similar" => context.Client.GetSimilarItemsAsync("movie-a", limit, TestContext.Current.CancellationToken),
            "genres" => context.Client.GetGenresAsync(new ItemQuery { Limit = limit }, TestContext.Current.CancellationToken),
            "persons" => context.Client.GetPersonsAsync(new ItemQuery { Limit = limit }, TestContext.Current.CancellationToken),
            _ => throw new InvalidOperationException("The test operation is not implemented.")
        });

        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData("similar", "[]")]
    [InlineData("similar", "{\"Items\":null}")]
    [InlineData("genres", "{\"Unexpected\":true}")]
    [InlineData("genres", "null")]
    [InlineData("persons", "[]")]
    [InlineData("persons", "{\"Items\":null}")]
    [InlineData("trailers", "{\"Items\":[],\"TotalRecordCount\":0}")]
    [InlineData("trailers", "null")]
    public async Task Discovery_rejects_response_shapes_from_a_different_endpoint(string operation, string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            InvokeDiscoveryAsync(context.Client, operation, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Removing_resume_progress_posts_the_documented_hide_flag_without_a_body()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""{"ItemId":"movie-a","PlaybackPositionTicks":9007199254740993,"Played":false}""");
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=1");

        var result = await client.RemoveFromResumeAsync("movie/a?other=1#%", TestContext.Current.CancellationToken);

        Assert.Equal("movie-a", result.ItemId);
        Assert.Equal(9007199254740993L, result.PlaybackPositionTicks);
        Assert.False(result.Played);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fscope%3D1/Items/movie%2Fa%3Fother%3D1%23%25/HideFromResume", request.Uri.AbsolutePath);
        Assert.Equal("true", request.Query()["Hide"]);
        Assert.Null(request.Body);
        Assert.Null(request.ContentType);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.Equal("token-b", request.Header("X-Emby-Token"));
    }

    [Fact]
    public async Task Creating_a_collection_escapes_query_values_and_deduplicates_member_ids()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""{"Id":"collection-a","Name":"New collection","FutureCollectionField":true}""");
        string[] ids = ["movie/a?scope=1", "movie & second+#%", "movie/a?scope=1"];

        var result = await context.Client.CreateCollectionAsync("Collection & Name+#%", ids, true,
            TestContext.Current.CancellationToken);

        Assert.Equal("collection-a", result.Id);
        Assert.Equal("New collection", result.Name);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/proxy/emby/Collections", request.Uri.AbsolutePath);
        Assert.Equal("Collection & Name+#%", request.Query()["Name"]);
        Assert.Equal("movie/a?scope=1,movie & second+#%", request.Query()["Ids"]);
        Assert.Equal("true", request.Query()["IsLocked"]);
        Assert.False(request.Query().ContainsKey("scope"));
        Assert.Null(request.Body);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.Equal(3, ids.Length);
        Assert.Equal("token-a", request.Header("X-Emby-Token"));
        Assert.DoesNotContain("token-a", request.Uri.AbsoluteUri);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public async Task Adding_collection_members_escapes_the_collection_id_and_uses_a_query_only_post()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        await context.Client.AddToCollectionAsync("collection/a?other=1#%", ["item/a", "item & b+#%", "item/a"],
            TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/proxy/emby/Collections/collection%2Fa%3Fother%3D1%23%25/Items", request.Uri.AbsolutePath);
        Assert.Equal("item/a,item & b+#%", request.Query()["Ids"]);
        Assert.Null(request.Body);
        Assert.Equal(string.Empty, request.Uri.Fragment);
        Assert.Equal("token-a", request.Header("X-Emby-Token"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("blank")]
    [InlineData("comma")]
    [InlineData("control")]
    [InlineData("too-many")]
    public async Task Collection_mutations_reject_invalid_or_unbounded_member_lists_before_transport(string invalid)
    {
        using var context = new ApiTestContext();
        string[] ids = invalid switch
        {
            "empty" => [],
            "blank" => [" "],
            "comma" => ["movie-a,movie-b"],
            "control" => ["movie-a\nInjected"],
            "too-many" => Enumerable.Range(0, 101).Select(index => $"movie-{index}").ToArray(),
            _ => throw new InvalidOperationException("The test input is not implemented.")
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.CreateCollectionAsync("Collection", ids, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.AddToCollectionAsync("collection-a", ids, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task Refreshing_metadata_uses_the_documented_defaults_and_an_empty_JSON_object()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        await context.Client.RefreshItemMetadataAsync("movie/a?other=1#%", cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/proxy/emby/Items/movie%2Fa%3Fother%3D1%23%25/Refresh", request.Uri.AbsolutePath);
        var query = request.Query();
        Assert.Equal("false", query["Recursive"]);
        Assert.Equal("Default", query["MetadataRefreshMode"]);
        Assert.Equal("Default", query["ImageRefreshMode"]);
        Assert.Equal("false", query["ReplaceAllMetadata"]);
        Assert.Equal("false", query["ReplaceAllImages"]);
        Assert.False(query.ContainsKey("ReplaceThumbnailImages"));
        Assert.Equal("application/json", request.ContentType);
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
        Assert.Empty(body.RootElement.EnumerateObject());
    }

    [Fact]
    public async Task Refresh_options_keep_thumbnail_replacement_in_the_JSON_body()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));

        await context.Client.RefreshItemMetadataAsync("movie-a", new MetadataRefreshOptions
        {
            Recursive = true,
            MetadataRefreshMode = "FullRefresh",
            ImageRefreshMode = "ValidationOnly",
            ReplaceAllMetadata = true,
            ReplaceAllImages = true,
            ReplaceThumbnailImages = false
        }, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests);
        var query = request.Query();
        Assert.Equal("true", query["Recursive"]);
        Assert.Equal("FullRefresh", query["MetadataRefreshMode"]);
        Assert.Equal("ValidationOnly", query["ImageRefreshMode"]);
        Assert.Equal("true", query["ReplaceAllMetadata"]);
        Assert.Equal("true", query["ReplaceAllImages"]);
        Assert.False(query.ContainsKey("ReplaceThumbnailImages"));
        using var body = JsonDocument.Parse(Assert.IsType<string>(request.Body));
        Assert.Equal("ReplaceThumbnailImages", Assert.Single(body.RootElement.EnumerateObject()).Name);
        Assert.False(body.RootElement.GetProperty("ReplaceThumbnailImages").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unknown_refresh_modes_are_rejected_before_transport(bool metadata)
    {
        using var context = new ApiTestContext();
        var options = metadata
            ? new MetadataRefreshOptions { MetadataRefreshMode = "UnknownMode" }
            : new MetadataRefreshOptions { ImageRefreshMode = "UnknownMode" };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.RefreshItemMetadataAsync("movie-a", options, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task Metadata_updates_merge_only_editable_fields_and_preserve_unknown_nested_server_data()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RecordingHandler.Json("""
                {
                  "Id":"movie/a?other=1","Name":"Existing title","OriginalTitle":"Existing original",
                  "Type":"Movie","SortName":"Existing sort","Overview":"Existing overview",
                  "ProductionYear":1997,"OfficialRating":"PG","Genres":["Drama"],"Tags":["original"],
                  "LockData":false,"LockedFields":["SortName"],"ProviderIds":{"Imdb":"tt123"},
                  "FutureItemSetting":{"Mode":"server","Flags":[1,2],"NullValue":null},
                  "UserData":{"Played":true,"FutureUserSetting":{"Values":[null,false]}},
                  "MediaSources":[{"Id":"source-a","FutureMediaSetting":{"Number":9007199254740993}}],
                  "People":[{"Id":"person-a","Name":"Known person","FutureRole":["Director","Writer"]}],
                  "Chapters":[{"Name":"Intro","StartPositionTicks":10000,"FutureChapter":{"Kind":"unknown"}}]
                }
                """)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=1");

        await client.UpdateItemMetadataAsync("movie/a?other=1", new ItemMetadataUpdate
        {
            Name = "Updated title",
            OriginalTitle = "Updated original",
            Overview = "",
            ProductionYear = 2026,
            OfficialRating = "",
            Genres = [],
            Tags = ["new"],
            LockData = true,
            LockedFields = ["Name"]
        }, TestContext.Current.CancellationToken);

        var requests = context.Handler.Requests;
        Assert.Equal(2, requests.Length);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fscope%3D1/Items/movie%2Fa%3Fother%3D1", requests[0].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("/proxy/emby/Items/movie%2Fa%3Fother%3D1", requests[1].Uri.AbsolutePath);
        Assert.Equal("application/json", requests[1].ContentType);
        Assert.All(requests, request => Assert.Equal("token-b", request.Header("X-Emby-Token")));
        using var document = JsonDocument.Parse(Assert.IsType<string>(requests[1].Body));
        var body = document.RootElement;
        Assert.Equal("movie/a?other=1", body.GetProperty("Id").GetString());
        Assert.Equal("Updated title", body.GetProperty("Name").GetString());
        Assert.Equal("Updated original", body.GetProperty("OriginalTitle").GetString());
        Assert.Equal("", body.GetProperty("Overview").GetString());
        Assert.Equal(2026, body.GetProperty("ProductionYear").GetInt32());
        Assert.Equal("", body.GetProperty("OfficialRating").GetString());
        Assert.Empty(body.GetProperty("Genres").EnumerateArray());
        Assert.Equal("new", Assert.Single(body.GetProperty("Tags").EnumerateArray()).GetString());
        Assert.True(body.GetProperty("LockData").GetBoolean());
        Assert.Equal("Name", Assert.Single(body.GetProperty("LockedFields").EnumerateArray()).GetString());
        Assert.Equal("Existing sort", body.GetProperty("SortName").GetString());
        Assert.Equal("Movie", body.GetProperty("Type").GetString());
        Assert.Equal("tt123", body.GetProperty("ProviderIds").GetProperty("Imdb").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("FutureItemSetting").GetProperty("NullValue").ValueKind);
        Assert.Equal(2, body.GetProperty("FutureItemSetting").GetProperty("Flags")[1].GetInt32());
        Assert.True(body.GetProperty("UserData").GetProperty("Played").GetBoolean());
        Assert.False(body.GetProperty("UserData").GetProperty("FutureUserSetting").GetProperty("Values")[1].GetBoolean());
        Assert.Equal(9007199254740993L, body.GetProperty("MediaSources")[0].GetProperty("FutureMediaSetting").GetProperty("Number").GetInt64());
        Assert.Equal("Writer", body.GetProperty("People")[0].GetProperty("FutureRole")[1].GetString());
        Assert.Equal("unknown", body.GetProperty("Chapters")[0].GetProperty("FutureChapter").GetProperty("Kind").GetString());
        Assert.Single(body.EnumerateObject(), property => property.Name == "Name");
        Assert.False(body.TryGetProperty("ExtensionData", out _));
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Fact]
    public async Task Sparse_metadata_updates_preserve_unspecified_known_fields()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RecordingHandler.Json("""{"Id":"movie-a","Name":"Keep title","Overview":"Keep overview","ProductionYear":1997,"OfficialRating":"PG","Tags":["old"]}""")
            : new HttpResponseMessage(HttpStatusCode.NoContent));

        await context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = [] }, TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.IsType<string>(context.Handler.Requests[1].Body));
        var body = document.RootElement;
        Assert.Equal("Keep title", body.GetProperty("Name").GetString());
        Assert.Equal("Keep overview", body.GetProperty("Overview").GetString());
        Assert.Equal(1997, body.GetProperty("ProductionYear").GetInt32());
        Assert.Equal("PG", body.GetProperty("OfficialRating").GetString());
        Assert.Empty(body.GetProperty("Tags").EnumerateArray());
    }

    [Theory]
    [InlineData("blank-name")]
    [InlineData("long-name")]
    [InlineData("long-overview")]
    [InlineData("early-year")]
    [InlineData("late-year")]
    [InlineData("many-genres")]
    [InlineData("long-genre")]
    [InlineData("many-tags")]
    [InlineData("long-tag")]
    [InlineData("many-locked-fields")]
    [InlineData("long-locked-field")]
    public async Task Invalid_metadata_edits_are_rejected_before_loading_or_mutating_the_item(string invalid)
    {
        using var context = new ApiTestContext();
        var update = invalid switch
        {
            "blank-name" => new ItemMetadataUpdate { Name = " " },
            "long-name" => new ItemMetadataUpdate { Name = new string('x', 1001) },
            "long-overview" => new ItemMetadataUpdate { Overview = new string('x', 20001) },
            "early-year" => new ItemMetadataUpdate { ProductionYear = 0 },
            "late-year" => new ItemMetadataUpdate { ProductionYear = 10000 },
            "many-genres" => new ItemMetadataUpdate { Genres = Enumerable.Range(0, 101).Select(index => $"Genre {index}").ToArray() },
            "long-genre" => new ItemMetadataUpdate { Genres = [new string('x', 501)] },
            "many-tags" => new ItemMetadataUpdate { Tags = Enumerable.Range(0, 101).Select(index => $"Tag {index}").ToArray() },
            "long-tag" => new ItemMetadataUpdate { Tags = [new string('x', 501)] },
            "many-locked-fields" => new ItemMetadataUpdate { LockedFields = Enumerable.Range(0, 101).Select(index => $"Field{index}").ToArray() },
            "long-locked-field" => new ItemMetadataUpdate { LockedFields = [new string('x', 501)] },
            _ => throw new InvalidOperationException("The test input is not implemented.")
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.UpdateItemMetadataAsync("movie-a", update, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task Configuration_updates_merge_false_and_zero_values_without_losing_unknown_preferences()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RecordingHandler.Json("""
                {
                  "Id":"user/a?scope=1",
                  "Configuration":{
                    "AudioLanguagePreference":"en","SubtitleLanguagePreference":"fr","SubtitleMode":"Default",
                    "PlayDefaultAudioTrack":true,"EnableNextEpisodeAutoPlay":true,
                    "ResumeRewindSeconds":15,"IntroSkipMode":"ShowButton",
                    "FuturePreference":{"Rows":[null,{"Enabled":false}],"Ratio":1.25,"NullValue":null}
                  }
                }
                """)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        using var extensionValue = JsonDocument.Parse("""{"Nested":[null,{"Enabled":true}]}""");
        var configuration = new UserConfiguration
        {
            SubtitleLanguagePreference = "ja",
            SubtitleMode = "Always",
            EnableNextEpisodeAutoPlay = false,
            ResumeRewindSeconds = 0,
            IntroSkipMode = "AutoSkip",
            ExtensionData = new Dictionary<string, JsonElement>
            {
                ["ClientFuturePreference"] = extensionValue.RootElement.Clone()
            }
        };
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=1");

        await client.UpdateUserConfigurationAsync(configuration, TestContext.Current.CancellationToken);

        var requests = context.Handler.Requests;
        Assert.Equal(2, requests.Length);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fscope%3D1", requests[0].Uri.AbsolutePath);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fscope%3D1/Configuration", requests[1].Uri.AbsolutePath);
        Assert.Equal("application/json", requests[1].ContentType);
        Assert.All(requests, request => Assert.Equal("token-b", request.Header("X-Emby-Token")));
        using var document = JsonDocument.Parse(Assert.IsType<string>(requests[1].Body));
        var body = document.RootElement;
        Assert.Equal("en", body.GetProperty("AudioLanguagePreference").GetString());
        Assert.Equal("ja", body.GetProperty("SubtitleLanguagePreference").GetString());
        Assert.Equal("Always", body.GetProperty("SubtitleMode").GetString());
        Assert.True(body.GetProperty("PlayDefaultAudioTrack").GetBoolean());
        Assert.False(body.GetProperty("EnableNextEpisodeAutoPlay").GetBoolean());
        Assert.Equal(0, body.GetProperty("ResumeRewindSeconds").GetInt32());
        Assert.Equal("AutoSkip", body.GetProperty("IntroSkipMode").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("FuturePreference").GetProperty("Rows")[0].ValueKind);
        Assert.False(body.GetProperty("FuturePreference").GetProperty("Rows")[1].GetProperty("Enabled").GetBoolean());
        Assert.Equal(1.25, body.GetProperty("FuturePreference").GetProperty("Ratio").GetDouble());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("FuturePreference").GetProperty("NullValue").ValueKind);
        Assert.True(body.GetProperty("ClientFuturePreference").GetProperty("Nested")[1].GetProperty("Enabled").GetBoolean());
        Assert.False(body.TryGetProperty("ExtensionData", out _));
        Assert.False(body.TryGetProperty("Id", out _));
        Assert.False(body.TryGetProperty("Configuration", out _));
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("{\"Id\":\"user-a\"}")]
    [InlineData("{\"Id\":\"user-a\",\"Configuration\":null}")]
    [InlineData("{\"Id\":\"user-a\",\"Configuration\":[]}")]
    public async Task Configuration_updates_reject_missing_or_non_object_server_preferences_before_mutation(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.UpdateUserConfigurationAsync(new UserConfiguration { EnableNextEpisodeAutoPlay = false },
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpMethod.Get, Assert.Single(context.Handler.Requests).Method);
    }

    [Theory]
    [InlineData("negative-rewind")]
    [InlineData("long-rewind")]
    [InlineData("intro-mode")]
    [InlineData("subtitle-mode")]
    [InlineData("long-audio-language")]
    [InlineData("long-subtitle-language")]
    public async Task Invalid_user_preferences_are_rejected_before_loading_or_mutating_configuration(string invalid)
    {
        using var context = new ApiTestContext();
        var configuration = invalid switch
        {
            "negative-rewind" => new UserConfiguration { ResumeRewindSeconds = -1 },
            "long-rewind" => new UserConfiguration { ResumeRewindSeconds = 301 },
            "intro-mode" => new UserConfiguration { IntroSkipMode = "UnknownMode" },
            "subtitle-mode" => new UserConfiguration { SubtitleMode = "UnknownMode" },
            "long-audio-language" => new UserConfiguration { AudioLanguagePreference = new string('x', 101) },
            "long-subtitle-language" => new UserConfiguration { SubtitleLanguagePreference = new string('x', 101) },
            _ => throw new InvalidOperationException("The test input is not implemented.")
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.UpdateUserConfigurationAsync(configuration, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData("resumeRewindSeconds", "301")]
    [InlineData("resumerewindseconds", "301")]
    [InlineData("SubtitleMode", "\"UnknownMode\"")]
    [InlineData("IntroSkipMode", "\"UnknownMode\"")]
    public async Task Extension_preferences_cannot_bypass_known_field_validation(string name, string json)
    {
        using var context = new ApiTestContext();
        using var value = JsonDocument.Parse(json);
        var configuration = new UserConfiguration
        {
            ExtensionData = new Dictionary<string, JsonElement> { [name] = value.RootElement.Clone() }
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.UpdateUserConfigurationAsync(configuration, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task Undefined_extension_preferences_are_rejected_before_transport()
    {
        using var context = new ApiTestContext();
        var configuration = new UserConfiguration
        {
            ExtensionData = new Dictionary<string, JsonElement> { ["FuturePreference"] = default }
        };

        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            context.Client.UpdateUserConfigurationAsync(configuration, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("configuration")]
    public async Task Edit_operations_reject_a_read_response_for_a_different_target_before_mutation(string operation)
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""{"Id":"other-target","Name":"Other item","Configuration":{}}""");

        await Assert.ThrowsAsync<EmbyProtocolException>(() => operation == "metadata"
            ? context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Name = "Updated" }, TestContext.Current.CancellationToken)
            : context.Client.UpdateUserConfigurationAsync(new UserConfiguration { SubtitleMode = "Always" }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpMethod.Get, Assert.Single(context.Handler.Requests).Method);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("configuration")]
    public async Task A_redirect_during_the_read_phase_does_not_send_a_mutation_or_disclose_the_location(string operation)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://other.example/?api_key=private-redirect-value");
            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<EmbyApiException>(() => operation == "metadata"
            ? context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Name = "Updated" }, TestContext.Current.CancellationToken)
            : context.Client.UpdateUserConfigurationAsync(new UserConfiguration { SubtitleMode = "Always" }, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, error.StatusCode);
        Assert.Equal(HttpMethod.Get, Assert.Single(context.Handler.Requests).Method);
        Assert.DoesNotContain("private-redirect-value", error.ToString());
        Assert.DoesNotContain("other.example", error.ToString());
    }

    [Fact]
    public async Task Every_new_capability_requires_an_authenticated_context_before_transport()
    {
        using var context = new ApiTestContext();
        var anonymous = new EmbyApiClient(context.Http, context.Client.ApiRoot, ApiTestContext.Identity);
        Func<Task>[] operations =
        [
            () => anonymous.GetSimilarItemsAsync("movie-a", cancellationToken: TestContext.Current.CancellationToken),
            () => anonymous.GetLocalTrailersAsync("movie-a", TestContext.Current.CancellationToken),
            () => anonymous.GetGenresAsync(cancellationToken: TestContext.Current.CancellationToken),
            () => anonymous.GetPersonsAsync(cancellationToken: TestContext.Current.CancellationToken),
            () => anonymous.RemoveFromResumeAsync("movie-a", TestContext.Current.CancellationToken),
            () => anonymous.CreateCollectionAsync("Collection", ["movie-a"], cancellationToken: TestContext.Current.CancellationToken),
            () => anonymous.AddToCollectionAsync("collection-a", ["movie-a"], TestContext.Current.CancellationToken),
            () => anonymous.RefreshItemMetadataAsync("movie-a", cancellationToken: TestContext.Current.CancellationToken),
            () => anonymous.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Name = "Updated" }, TestContext.Current.CancellationToken),
            () => anonymous.UpdateUserConfigurationAsync(new UserConfiguration(), TestContext.Current.CancellationToken)
        ];

        foreach (var operation in operations)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(operation);
        }

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task Concurrent_sparse_configuration_updates_preserve_both_changes()
    {
        using var context = new ApiTestContext();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var configuration = new UserConfiguration
        {
            EnableNextEpisodeAutoPlay = true,
            SubtitleMode = "Default",
            AudioLanguagePreference = "en"
        };
        context.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    arrived.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
                return RecordingHandler.Json(JsonSerializer.Serialize(
                    new UserDto { Id = "user-a", Configuration = configuration }, EmbyJsonContext.Default.UserDto));
            }
            configuration = Assert.IsType<UserConfiguration>(JsonSerializer.Deserialize(
                Assert.IsType<string>(request.Body), EmbyJsonContext.Default.UserConfiguration));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        var first = context.Client.UpdateUserConfigurationAsync(new UserConfiguration { EnableNextEpisodeAutoPlay = false },
            TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = context.Client.UpdateUserConfigurationAsync(new UserConfiguration { SubtitleMode = "Always" },
            TestContext.Current.CancellationToken);
        var requestsWhileHeld = context.Handler.Requests.Length;
        var secondCompletedWhileHeld = second.IsCompleted;
        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, requestsWhileHeld);
        Assert.False(secondCompletedWhileHeld);
        Assert.False(configuration.EnableNextEpisodeAutoPlay);
        Assert.Equal("Always", configuration.SubtitleMode);
        Assert.Equal("en", configuration.AudioLanguagePreference);
        Assert.Collection(context.Handler.Requests,
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Post, request.Method),
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Post, request.Method));
    }

    [Fact]
    public async Task Concurrent_sparse_metadata_updates_to_one_item_preserve_both_changes()
    {
        using var context = new ApiTestContext();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var item = new BaseItemDto
        {
            Id = "movie-a",
            Name = "Original title",
            Tags = ["original"],
            Overview = "Keep overview"
        };
        context.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    arrived.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
                return RecordingHandler.Json(JsonSerializer.Serialize(item, EmbyJsonContext.Default.BaseItemDto));
            }
            item = Assert.IsType<BaseItemDto>(JsonSerializer.Deserialize(
                Assert.IsType<string>(request.Body), EmbyJsonContext.Default.BaseItemDto));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        var first = context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Name = "Updated title" },
            TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = ["new"] },
            TestContext.Current.CancellationToken);
        var requestsWhileHeld = context.Handler.Requests.Length;
        var secondCompletedWhileHeld = second.IsCompleted;
        release.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, requestsWhileHeld);
        Assert.False(secondCompletedWhileHeld);
        Assert.Equal("Updated title", item.Name);
        Assert.Equal("new", Assert.Single(Assert.IsType<string[]>(item.Tags)));
        Assert.Equal("Keep overview", item.Overview);
        Assert.Collection(context.Handler.Requests,
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Post, request.Method),
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Post, request.Method));
    }

    [Fact]
    public async Task Canceling_a_queued_configuration_update_sends_no_HTTP_request_and_keeps_the_gate_usable()
    {
        using var context = new ApiTestContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var configuration = new UserConfiguration { EnableNextEpisodeAutoPlay = true, SubtitleMode = "Default" };
        context.Handler.RespondAsync = async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    arrived.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
                return RecordingHandler.Json(JsonSerializer.Serialize(
                    new UserDto { Id = "user-a", Configuration = configuration }, EmbyJsonContext.Default.UserDto));
            }
            configuration = Assert.IsType<UserConfiguration>(JsonSerializer.Deserialize(
                Assert.IsType<string>(request.Body), EmbyJsonContext.Default.UserConfiguration));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        };

        var first = context.Client.UpdateUserConfigurationAsync(new UserConfiguration { EnableNextEpisodeAutoPlay = false },
            TestContext.Current.CancellationToken);
        await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        var queued = context.Client.UpdateUserConfigurationAsync(new UserConfiguration { SubtitleMode = "Always" }, cancellation.Token);
        var requestsWhileQueued = context.Handler.Requests.Length;
        var requestsAfterCancellation = -1;
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            requestsAfterCancellation = context.Handler.Requests.Length;
        }
        finally
        {
            release.TrySetResult();
            await first;
        }

        await context.Client.UpdateUserConfigurationAsync(new UserConfiguration { SubtitleMode = "Smart" },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, requestsWhileQueued);
        Assert.Equal(1, requestsAfterCancellation);
        Assert.False(configuration.EnableNextEpisodeAutoPlay);
        Assert.Equal("Smart", configuration.SubtitleMode);
        Assert.Collection(context.Handler.Requests,
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Post, request.Method),
            request => Assert.Equal(HttpMethod.Get, request.Method),
            request => Assert.Equal(HttpMethod.Post, request.Method));
    }

    private static Task InvokeDiscoveryAsync(EmbyApiClient client, string operation, CancellationToken cancellationToken) =>
        operation switch
        {
            "similar" => client.GetSimilarItemsAsync("movie-a", cancellationToken: cancellationToken),
            "trailers" => client.GetLocalTrailersAsync("movie-a", cancellationToken),
            "genres" => client.GetGenresAsync(cancellationToken: cancellationToken),
            "persons" => client.GetPersonsAsync(cancellationToken: cancellationToken),
            _ => throw new InvalidOperationException("The test operation is not implemented.")
        };
}
