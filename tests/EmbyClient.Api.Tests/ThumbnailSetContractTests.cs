using System.Net;
using Xunit;

namespace EmbyClient.Api.Tests;

public sealed class ThumbnailSetContractTests
{
    [Fact]
    public async Task Thumbnail_metadata_preserves_server_positions_and_uses_the_required_width()
    {
        using var context = new ApiTestContext();
        context.ReturnJson("""
            {"AspectRatio":1.7777777777777777,"Thumbnails":[
                {"PositionTicks":0,"ImageTag":"start-tag"},
                {"PositionTicks":170000000,"ImageTag":"sample-tag"},
                {"PositionTicks":435000000},
                {"ImageTag":"missing-position"}
            ]}
            """);

        var metadata = Assert.IsType<ThumbnailSetInfo>(await context.Client.GetThumbnailSetAsync(
            "item/a?Width=1&#%", 320, TestContext.Current.CancellationToken));

        Assert.Equal(1.7777777777777777, metadata.AspectRatio);
        var samples = Assert.IsType<ThumbnailInfo[]>(metadata.Thumbnails);
        Assert.Equal(4, samples.Length);
        Assert.Equal(0L, samples[0].PositionTicks);
        Assert.Equal(170000000L, samples[1].PositionTicks);
        Assert.Equal(435000000L, samples[2].PositionTicks);
        Assert.Null(samples[3].PositionTicks);
        Assert.Equal("sample-tag", samples[1].ImageTag);
        Assert.Null(samples[2].ImageTag);
        var request = Assert.Single(context.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/proxy/emby/Items/item%2Fa%3FWidth%3D1%26%23%25/ThumbnailSet", request.Uri.AbsolutePath);
        Assert.Equal("320", request.Query()["Width"]);
        Assert.Single(request.Query());
        Assert.Equal("token-a", request.Header("X-Emby-Token"));
        Assert.Contains("UserId=\"user-a\"", request.Header("X-Emby-Authorization"));
        Assert.Equal(string.Empty, request.Uri.Fragment);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"AspectRatio\":null,\"Thumbnails\":null}")]
    public async Task Omitted_thumbnail_data_does_not_create_a_ratio_or_a_sample(string json)
    {
        using var context = new ApiTestContext();
        context.ReturnJson(json);

        var metadata = Assert.IsType<ThumbnailSetInfo>(await context.Client.GetThumbnailSetAsync(
            "item-a", 320, TestContext.Current.CancellationToken));

        Assert.Null(metadata.AspectRatio);
        Assert.Null(metadata.Thumbnails);
        Assert.Single(context.Handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Missing_thumbnail_endpoint_does_not_trigger_guessed_image_or_binary_requests(HttpStatusCode statusCode)
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(RecordingHandler.Json("{}", statusCode));

        Assert.Null(await context.Client.GetThumbnailSetAsync("item-a", 320, TestContext.Current.CancellationToken));
        Assert.Equal("/proxy/emby/Items/item-a/ThumbnailSet", Assert.Single(context.Handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task Authentication_failures_are_not_hidden_as_missing_thumbnails()
    {
        using var context = new ApiTestContext();
        context.Handler.RespondAsync = (_, _) => Task.FromResult(RecordingHandler.Json("{}", HttpStatusCode.Unauthorized));

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() => context.Client.GetThumbnailSetAsync(
            "item-a", 320, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
    }
}
