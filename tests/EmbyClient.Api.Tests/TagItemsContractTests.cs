using System.Net;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class TagItemsContractTests
{
    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"Large tag\",\"Id\":9007199254740993},{\"Name\":\"Maximum tag\",\"Id\":9223372036854775807}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"Large tag\",\"Id\":\"9007199254740993\"},{\"Name\":\"Maximum tag\",\"Id\":\"9223372036854775807\"}]}")]
    public async Task Tag_items_supply_names_and_lossless_int64_identifiers_through_the_AOT_contract(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        var item = await context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken);

        var pairs = Assert.IsType<NameLongIdPair[]>(item.TagItems);
        Assert.Equal(2, pairs.Length);
        Assert.Equal(9007199254740993L, pairs[0].Id);
        Assert.Equal(long.MaxValue, pairs[1].Id);
        Assert.Equal(new[] { "Large tag", "Maximum tag" }, Assert.IsType<string[]>(item.Tags));
        Assert.Null(item.LegacyTags);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"Tags\":[\"LegacyCase\"],\"TagItems\":[{\"Name\":\"ModernCase\",\"Id\":42}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"ModernCase\",\"Id\":42}],\"Tags\":[\"LegacyCase\"]}")]
    public async Task Modern_names_take_display_priority_without_replacing_legacy_wire_tags(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        var item = await context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken);

        Assert.Equal("ModernCase", Assert.Single(Assert.IsType<string[]>(item.Tags)));
        Assert.Equal("LegacyCase", Assert.Single(Assert.IsType<string[]>(item.LegacyTags)));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(item, EmbyJsonContext.Default.BaseItemDto));
        var body = document.RootElement;
        Assert.Equal("LegacyCase", Assert.Single(body.GetProperty("Tags").EnumerateArray()).GetString());
        Assert.Equal("ModernCase", Assert.Single(body.GetProperty("TagItems").EnumerateArray()).GetProperty("Name").GetString());
        Assert.Single(body.EnumerateObject(), property => property.Name == "Tags");
        Assert.False(body.TryGetProperty("LegacyTags", out _));
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"Tags\":[\"LegacyCase\",\"Comma,Semicolon;Tag\"]}")]
    [InlineData("{\"Id\":\"movie-a\",\"Tags\":[\"LegacyCase\",\"Comma,Semicolon;Tag\"],\"TagItems\":null}")]
    public async Task Missing_or_null_tag_items_fall_back_to_the_original_legacy_tags(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        var item = await context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken);

        Assert.Null(item.TagItems);
        Assert.Equal(new[] { "LegacyCase", "Comma,Semicolon;Tag" }, Assert.IsType<string[]>(item.Tags));
        Assert.Equal(new[] { "LegacyCase", "Comma,Semicolon;Tag" }, Assert.IsType<string[]>(item.LegacyTags));
    }

    [Fact]
    public async Task An_empty_modern_tag_array_overrides_a_nonempty_legacy_array()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""{"Id":"movie-a","TagItems":[],"Tags":["stale legacy"]}""");

        var item = await context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken);

        Assert.Empty(Assert.IsType<NameLongIdPair[]>(item.TagItems));
        Assert.Empty(Assert.IsType<string[]>(item.Tags));
        Assert.Equal("stale legacy", Assert.Single(Assert.IsType<string[]>(item.LegacyTags)));
    }

    [Fact]
    public async Task Display_tags_skip_null_and_blank_names_without_normalizing_valid_names()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {
              "Id":"movie-a","Tags":["stale legacy"],
              "TagItems":[
                {"Name":null,"Id":"42"},{"Id":43},{"Name":" ","Id":44},
                {"Name":" Padded;Tag ","Id":45},{"Name":"MixedCase"},{"Name":"MixedCase","Id":46}
              ]
            }
            """);

        var item = await context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken);

        var pairs = Assert.IsType<NameLongIdPair[]>(item.TagItems);
        Assert.Equal(6, pairs.Length);
        Assert.Null(pairs[0].Name);
        Assert.Equal(42, pairs[0].Id);
        Assert.Null(pairs[1].Name);
        Assert.Null(pairs[4].Id);
        Assert.Equal(new[] { " Padded;Tag ", "MixedCase", "MixedCase" }, Assert.IsType<string[]>(item.Tags));
    }

    [Fact]
    public void Legacy_tag_initializers_and_record_copies_keep_the_existing_public_contract()
    {
        var original = new BaseItemDto { Id = "movie-a", Tags = ["OriginalLegacy"] };
        var copy = original with
        {
            Tags = ["UpdatedLegacy"],
            TagItems = [new NameLongIdPair { Name = "ModernCase", Id = 42 }]
        };

        Assert.Equal("OriginalLegacy", Assert.Single(Assert.IsType<string[]>(original.Tags)));
        Assert.Equal("OriginalLegacy", Assert.Single(Assert.IsType<string[]>(original.LegacyTags)));
        Assert.Equal("ModernCase", Assert.Single(Assert.IsType<string[]>(copy.Tags)));
        Assert.Equal("UpdatedLegacy", Assert.Single(Assert.IsType<string[]>(copy.LegacyTags)));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(copy, EmbyJsonContext.Default.BaseItemDto));
        Assert.Equal("UpdatedLegacy", Assert.Single(document.RootElement.GetProperty("Tags").EnumerateArray()).GetString());
        Assert.False(document.RootElement.TryGetProperty("LegacyTags", out _));
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":{}}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":\"invalid\"}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[42]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":42,\"Id\":1}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"Valid\",\"Id\":\"not-a-number\"}]}")]
    public async Task Malformed_typed_tag_responses_are_protocol_failures(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken));

        Assert.Equal(HttpMethod.Get, Assert.Single(context.Handler.Requests).Method);
    }

    [Fact]
    public async Task Explicit_tag_edits_keep_exact_names_and_reuse_only_exact_matching_raw_pairs()
    {
        using var context = new ApiTestContext();
        const string currentJson = """
            {
              "Id":"movie/a?other=1#%","Name":"Keep title","Tags":["stale legacy"],
              "TagItems":[
                {"Name":"Keep,Comma;Tag+#%","Id":"9007199254740993","FuturePair":{"Values":[null,true,2],"Case":"Original"}},
                {"Name":"Remove me","Id":9223372036854775807,"FutureRemovedField":true}
              ],
              "FutureItemSetting":{"Enabled":true},"UserData":{"Played":false,"FuturePreference":[null,2]}
            }
            """;
        ReturnMetadata(context, currentJson);
        var client = context.Client.WithAuthentication("token-b", "user/a?scope=1");
        string[] requested = ["Keep,Comma;Tag+#%", "New;Comma,Tag+#%", "keep,comma;tag+#%", "Keep,Comma;Tag+#%", " padded;tag "];

        await client.UpdateItemMetadataAsync("movie/a?other=1#%", new ItemMetadataUpdate { Tags = requested },
            TestContext.Current.CancellationToken);

        var requests = context.Handler.Requests;
        Assert.Equal(2, requests.Length);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("/proxy/emby/Users/user%2Fa%3Fscope%3D1/Items/movie%2Fa%3Fother%3D1%23%25", requests[0].Uri.AbsolutePath);
        Assert.Null(requests[0].Body);
        Assert.Equal(HttpMethod.Post, requests[1].Method);
        Assert.Equal("/proxy/emby/Items/movie%2Fa%3Fother%3D1%23%25", requests[1].Uri.AbsolutePath);
        Assert.Equal("application/json", requests[1].ContentType);
        Assert.All(requests, request =>
        {
            Assert.Equal("token-b", request.Header("X-Emby-Token"));
            Assert.Contains("UserId=\"user/a?scope=1\"", request.Header("X-Emby-Authorization"));
            Assert.Equal(string.Empty, request.Uri.Query);
            Assert.Equal(string.Empty, request.Uri.Fragment);
            Assert.DoesNotContain("token-b", request.Uri.AbsoluteUri);
        });
        using var document = JsonDocument.Parse(Assert.IsType<string>(requests[1].Body));
        var body = document.RootElement;
        Assert.Equal(requested, body.GetProperty("Tags").EnumerateArray().Select(value => value.GetString()).ToArray());
        var pairs = body.GetProperty("TagItems").EnumerateArray().ToArray();
        Assert.Equal(requested, pairs.Select(pair => pair.GetProperty("Name").GetString()).ToArray());
        Assert.Equal(JsonValueKind.String, pairs[0].GetProperty("Id").ValueKind);
        Assert.Equal("9007199254740993", pairs[0].GetProperty("Id").GetString());
        Assert.Equal("Original", pairs[0].GetProperty("FuturePair").GetProperty("Case").GetString());
        Assert.Equal(JsonValueKind.Null, pairs[0].GetProperty("FuturePair").GetProperty("Values")[0].ValueKind);
        Assert.True(pairs[0].GetProperty("FuturePair").GetProperty("Values")[1].GetBoolean());
        Assert.Equal(2, pairs[0].GetProperty("FuturePair").GetProperty("Values")[2].GetInt32());
        Assert.True(JsonElement.DeepEquals(pairs[0], pairs[3]));
        foreach (var index in new[] { 1, 2, 4 })
        {
            Assert.Equal("Name", Assert.Single(pairs[index].EnumerateObject()).Name);
            Assert.False(pairs[index].TryGetProperty("Id", out _));
        }
        Assert.Equal("Keep title", body.GetProperty("Name").GetString());
        Assert.True(body.GetProperty("FutureItemSetting").GetProperty("Enabled").GetBoolean());
        Assert.Equal(2, body.GetProperty("UserData").GetProperty("FuturePreference")[1].GetInt32());
        Assert.Single(body.EnumerateObject(), property => property.Name == "Tags");
        Assert.Single(body.EnumerateObject(), property => property.Name == "TagItems");
        Assert.Equal(5, requested.Length);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"Name\":\"Keep\",\"TagItems\":[{\"Name\":\"old\",\"Id\":42}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"Name\":\"Keep\",\"TagItems\":[{\"Name\":\"old\",\"Id\":42}],\"Tags\":[\"stale\"]}")]
    [InlineData("{\"Id\":\"movie-a\",\"Name\":\"Keep\"}")]
    [InlineData("{\"Id\":\"movie-a\",\"Name\":\"Keep\",\"TagItems\":null}")]
    [InlineData("{\"Id\":\"movie-a\",\"Name\":\"Keep\",\"TagItems\":null,\"Tags\":[\"stale legacy\"]}")]
    [InlineData("{\"Id\":\"movie-a\",\"Name\":\"Keep\",\"TagItems\":[{\"Name\":null,\"Id\":42},{\"Id\":43}]}")]
    public async Task Modern_missing_or_null_tag_items_receive_both_wire_fields_when_tags_are_edited(string json)
    {
        using var context = new ApiTestContext();
        ReturnMetadata(context, json);

        await context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = ["FreshCase"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Handler.Requests.Length);
        using var document = JsonDocument.Parse(Assert.IsType<string>(context.Handler.Requests[1].Body));
        var body = document.RootElement;
        Assert.Equal("FreshCase", Assert.Single(body.GetProperty("Tags").EnumerateArray()).GetString());
        var pair = Assert.Single(body.GetProperty("TagItems").EnumerateArray());
        Assert.Equal("FreshCase", pair.GetProperty("Name").GetString());
        Assert.Equal("Name", Assert.Single(pair.EnumerateObject()).Name);
        Assert.False(pair.TryGetProperty("Id", out _));
        Assert.Equal("Keep", body.GetProperty("Name").GetString());
    }

    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"old\",\"Id\":42}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"old\",\"Id\":42}],\"Tags\":[\"old legacy\"]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":null}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":null,\"Tags\":[\"stale legacy\"]}")]
    public async Task Explicitly_empty_modern_tags_clear_both_wire_arrays(string json)
    {
        using var context = new ApiTestContext();
        ReturnMetadata(context, json);

        await context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = [] },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Handler.Requests.Length);
        using var document = JsonDocument.Parse(Assert.IsType<string>(context.Handler.Requests[1].Body));
        Assert.Empty(document.RootElement.GetProperty("Tags").EnumerateArray());
        Assert.Empty(document.RootElement.GetProperty("TagItems").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_legacy_tags_only_item_keeps_the_legacy_write_path(bool clear)
    {
        using var context = new ApiTestContext();
        ReturnMetadata(context, """{"Id":"movie-a","Name":"Keep title","Tags":["OldLegacy"],"FutureData":{"Mode":"keep"}}""");
        string[] requested = clear ? [] : ["New,Legacy;Case", "new,legacy;case", "New,Legacy;Case"];

        await context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = requested },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Handler.Requests.Length);
        using var document = JsonDocument.Parse(Assert.IsType<string>(context.Handler.Requests[1].Body));
        var body = document.RootElement;
        Assert.Equal(requested, body.GetProperty("Tags").EnumerateArray().Select(value => value.GetString()).ToArray());
        Assert.False(body.TryGetProperty("TagItems", out _));
        Assert.Equal("Keep title", body.GetProperty("Name").GetString());
        Assert.Equal("keep", body.GetProperty("FutureData").GetProperty("Mode").GetString());
    }

    [Fact]
    public async Task Editing_only_the_name_preserves_raw_tag_pairs_ids_and_unknown_nested_fields()
    {
        using var context = new ApiTestContext();
        const string currentJson = """
            {
              "Id":"movie-a","Name":"Old title","Tags":["OriginalLegacyCase"],
              "TagItems":[
                {"Name":"ModernCase","Id":9007199254740993,"FuturePair":{"Values":[null,false,2]}},
                {"Name":null,"Id":"9223372036854775807","Nested":{"Unknown":[false,null]}},
                {"Id":45,"MissingNameFuture":true}
              ],
              "FutureItemField":{"Mode":"preserve"}
            }
            """;
        ReturnMetadata(context, currentJson);

        await context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Name = "Updated title", Tags = null },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, context.Handler.Requests.Length);
        using var original = JsonDocument.Parse(currentJson);
        using var document = JsonDocument.Parse(Assert.IsType<string>(context.Handler.Requests[1].Body));
        var body = document.RootElement;
        Assert.Equal("Updated title", body.GetProperty("Name").GetString());
        Assert.True(JsonElement.DeepEquals(original.RootElement.GetProperty("TagItems"), body.GetProperty("TagItems")));
        Assert.True(JsonElement.DeepEquals(original.RootElement.GetProperty("Tags"), body.GetProperty("Tags")));
        Assert.Equal("9007199254740993", body.GetProperty("TagItems")[0].GetProperty("Id").GetRawText());
        Assert.Equal(JsonValueKind.String, body.GetProperty("TagItems")[1].GetProperty("Id").ValueKind);
        Assert.Equal("9223372036854775807", body.GetProperty("TagItems")[1].GetProperty("Id").GetString());
        Assert.Equal("preserve", body.GetProperty("FutureItemField").GetProperty("Mode").GetString());
    }

    [Fact]
    public async Task A_null_tag_patch_without_other_edits_does_not_send_an_HTTP_request()
    {
        using var context = new ApiTestContext();

        await context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = null },
            TestContext.Current.CancellationToken);

        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":{}}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":\"invalid\"}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":42}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[null]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[42]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[\"invalid\"]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":42,\"Id\":1}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":true,\"Id\":1}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":\"Existing\",\"name\":\"Conflicting\",\"Id\":1}]}")]
    [InlineData("{\"Id\":\"movie-a\",\"TagItems\":[{\"Name\":null,\"NAME\":\"Conflicting\",\"Id\":1}]}")]
    public async Task Tag_edits_refuse_malformed_current_pairs_before_posting(string json)
    {
        using var context = new ApiTestContext();
        ReturnMetadata(context, json);

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = ["Updated"] },
                TestContext.Current.CancellationToken));

        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Users/user-a/Items/movie-a", request.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Clearing_tags_does_not_bypass_validation_of_the_existing_modern_shape()
    {
        using var context = new ApiTestContext();
        ReturnMetadata(context, """{"Id":"movie-a","TagItems":[{"Name":42,"Id":1}]}""");

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.UpdateItemMetadataAsync("movie-a", new ItemMetadataUpdate { Tags = [] },
                TestContext.Current.CancellationToken));

        Assert.Equal(HttpMethod.Get, Assert.Single(context.Handler.Requests).Method);
    }

    private static void ReturnMetadata(ApiTestContext context, string json) =>
        context.Handler.RespondAsync = (request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? RecordingHandler.Json(json)
            : new HttpResponseMessage(HttpStatusCode.NoContent));
}
