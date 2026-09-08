using System.Net;
using System.Text.Json;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class CatalogAndFailureTests
{
    [Fact]
    public async Task Latest_items_use_the_documented_bare_array_and_tolerate_new_server_fields()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            [{"Id":"movie-1","Type":"FutureVideoType","UserData":{"PlaybackPositionTicks":9007199254740993},"FutureField":{"Enabled":true}}]
            """);

        var items = await context.Client.GetLatestItemsAsync(new LatestItemsQuery { ParentId = "library-a", GroupItems = false },
            TestContext.Current.CancellationToken);

        var item = Assert.Single(items);
        Assert.Equal("movie-1", item.Id);
        Assert.Equal("FutureVideoType", item.Type);
        Assert.Equal(9007199254740993L, item.UserData?.PlaybackPositionTicks);
        var request = Assert.Single(context.Handler.Requests);
        Assert.EndsWith("/Users/user-a/Items/Latest", request.Uri.AbsolutePath);
        Assert.Equal("false", request.Query()["GroupItems"]);
        Assert.Equal("library-a", request.Query()["ParentId"]);
    }

    [Fact]
    public async Task Episodes_accept_the_known_wrapper_and_keep_unknown_types_as_data()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {"Items":[{"Id":"episode-1","Type":"FutureEpisode","MediaSources":[{"Id":"source-a","MediaStreams":[{"Index":7,"Type":"FutureStream"}]}]}],"TotalRecordCount":12,"FuturePageData":true}
            """);

        var result = await context.Client.GetEpisodesAsync("series-a", "season-a", TestContext.Current.CancellationToken);

        Assert.Equal(12, result.TotalRecordCount);
        var item = Assert.Single(result.Items);
        Assert.Equal("FutureEpisode", item.Type);
        var source = Assert.Single(Assert.IsType<MediaSourceInfo[]>(item.MediaSources));
        Assert.Equal("FutureStream", Assert.Single(source.MediaStreams).Type);
        var query = Assert.Single(context.Handler.Requests).Query();
        Assert.Equal("user-a", query["UserId"]);
        Assert.Equal("season-a", query["SeasonId"]);
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"Unexpected\":\"server changed its response\"}")]
    [InlineData("{\"Items\":null}")]
    [InlineData("null")]
    public async Task An_unknown_episodes_shape_is_a_protocol_failure_instead_of_an_empty_library(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.GetEpisodesAsync("series-a", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Latest_does_not_silently_accept_the_paginated_endpoint_shape()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("{\"Items\":[],\"TotalRecordCount\":0}");

        await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.GetLatestItemsAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Item_details_accept_the_sdk_numeric_genre_and_studio_identifiers()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {"Id":"movie-a","GenreItems":[{"Id":9007199254740993,"Name":"Drama"}],"Studios":[{"Id":"42","Name":"Example Studio"}]}
            """);

        var item = await context.Client.GetItemAsync("movie-a", TestContext.Current.CancellationToken);

        Assert.Equal(9007199254740993L, Assert.Single(Assert.IsType<NameLongIdPair[]>(item.GenreItems)).Id);
        Assert.Equal(42, Assert.Single(Assert.IsType<NameLongIdPair[]>(item.Studios)).Id);
    }

    [Theory]
    [InlineData(401, true, false)]
    [InlineData(403, false, true)]
    [InlineData(429, false, false)]
    [InlineData(500, false, false)]
    public async Task Http_failures_preserve_status_without_disclosing_server_content(int status, bool authentication, bool permission)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) =>
        {
            var response = RecordingHandler.Json("{\"Error\":\"private-path token-secret\"}", (HttpStatusCode)status);
            response.Headers.Add("X-Application-Error-Code", "ParentalControl");
            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<EmbyApiException>(() =>
            context.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken));

        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(authentication, error.IsAuthenticationFailure);
        Assert.Equal(permission, error.IsPermissionDenied);
        Assert.Equal("ParentalControl", error.ApplicationErrorCode);
        Assert.DoesNotContain("token-secret", error.ToString());
        Assert.DoesNotContain("private-path", error.ToString());
        Assert.DoesNotContain("token-a", error.ToString());
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_handler_and_remains_cancellation()
    {
        using var context = new ApiTestContext();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var arrived = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = async (_, token) =>
        {
            arrived.SetResult(token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecordingHandler.Json("{}");
        };

        var pending = context.Client.GetCurrentUserAsync(cancellation.Token);
        var receivedToken = await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(receivedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Request_deadline_is_distinct_from_caller_cancellation()
    {
        using var context = new ApiTestContext(requestTimeout: TimeSpan.FromMilliseconds(30));
        context.Handler.RespondAsync = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return RecordingHandler.Json("{}");
        };

        await Assert.ThrowsAsync<TimeoutException>(() =>
            context.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Transport_and_malformed_json_errors_are_sanitized_and_distinct()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => throw new HttpRequestException("https://server.example/?api_key=secret-value");

        var transport = await Assert.ThrowsAsync<EmbyTransportException>(() =>
            context.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret-value", transport.ToString());

        context.ReturnJson("{\"private-library-title\": secret-value}");
        var protocol = await Assert.ThrowsAsync<EmbyProtocolException>(() =>
            context.Client.GetCurrentUserAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-library-title", protocol.ToString());
        Assert.DoesNotContain("secret-value", protocol.ToString());
    }
}
