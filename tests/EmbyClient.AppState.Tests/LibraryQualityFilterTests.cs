using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryQualityFilterTests
{
    [Fact]
    public async Task Quality_filters_scan_raw_pages_before_paging_matches_and_reuse_the_same_catalog()
    {
        using var context = await CreateAsync();
        var catalog = Enumerable.Range(0, 320)
            .Select(index => Video($"movie-{index:D3}", index < 200 ? 1920 : 3840, index < 200 ? 1080 : 2160))
            .ToArray();
        await context.OpenLibraryAsync(catalog);
        var model = context.Model;
        var expected = catalog.Skip(200).Select(item => item.Id).ToArray();
        var requestStart = context.Handler.Requests.Length;

        await model.SetLumenQualityFilterAsync("4K", TestContext.Current.CancellationToken);

        Assert.Equal("4K", model.BrowseQualityFilter);
        Assert.Equal(expected.Take(48), model.Items.Select(item => item.Id));
        Assert.Equal(120, model.TotalItemsCount);
        Assert.True(model.CanLoadMore);
        var rawPages = context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems).ToArray();
        Assert.Equal(new[] { "0", "200" }, rawPages.Select(request => request.Query["StartIndex"]));
        Assert.All(rawPages, request =>
        {
            Assert.Equal("200", request.Query["Limit"]);
            Assert.Equal("library-a", request.Query["ParentId"]);
            AssertQualityFields(request);
        });
        requestStart = context.Handler.Requests.Length;

        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal(96, model.Items.Count);
        Assert.True(model.CanLoadMore);
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expected, model.Items.Select(item => item.Id));
        Assert.Equal(120, model.Items.Select(item => item.Id).Distinct().Count());
        Assert.Equal(120, model.TotalItemsCount);
        Assert.False(model.HasMore);
        Assert.Equal(requestStart, context.Handler.Requests.Length);

        await model.SetLumenQualityFilterAsync("1080p", TestContext.Current.CancellationToken);
        Assert.Equal("1080p", model.BrowseQualityFilter);
        Assert.Equal(200, model.TotalItemsCount);
        Assert.Equal(catalog.Take(48).Select(item => item.Id), model.Items.Select(item => item.Id));
        Assert.Equal(requestStart, context.Handler.Requests.Length);

        await model.SetLumenQualityFilterAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(model.BrowseQualityFilter);
        Assert.Equal(320, model.TotalItemsCount);
        Assert.Equal(catalog.Take(48).Select(item => item.Id), model.Items.Select(item => item.Id));
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Resolution_filters_inspect_all_video_sources_and_ignore_unknown_or_non_video_dimensions()
    {
        using var context = await CreateAsync();
        BaseItemDto[] catalog =
        [
            Video("a-letterbox", 1920, 800),
            Video("b-ultrawide", 2560, 1080),
            Video("c-4k-width", 4096, 1080),
            Video("d-4k-height", 1920, 2160),
            Video("e-multiple-sources", 1920, 1080) with
            {
                MediaSources =
                [
                    new() { Id = "hd", MediaStreams = [VideoStream(1920, 1080)] },
                    new() { Id = "uhd", MediaStreams = [VideoStream(3840, 1600)] }
                ]
            },
            Movie("f-source-only-hd") with
            {
                MediaSources = [new() { Id = "hd-source", MediaStreams = [VideoStream(1920, 800)] }]
            },
            Movie("g-unknown", "Unknown 4K"),
            Movie("h-audio-dimensions") with
            {
                MediaStreams = [new() { Type = "Audio", Width = 3840, Height = 2160 }]
            },
            Movie("i-missing-dimensions") with { MediaStreams = [new() { Type = "Video" }] },
            Video("j-720p", 1280, 720)
        ];
        await context.OpenLibraryAsync(catalog);
        var model = context.Model;

        await model.SetLumenQualityFilterAsync("1080p", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "a-letterbox", "b-ultrawide", "e-multiple-sources", "f-source-only-hd" },
            model.Items.Select(item => item.Id));
        Assert.Equal(4, model.TotalItemsCount);
        var requestStart = context.Handler.Requests.Length;

        await model.SetLumenQualityFilterAsync("4K", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "c-4k-width", "d-4k-height", "e-multiple-sources" }, model.Items.Select(item => item.Id));
        Assert.Equal(3, model.TotalItemsCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_repeated_raw_page_restores_the_previous_filter_and_loaded_window(bool repeatPage)
    {
        using var context = await CreateAsync();
        var catalog = Enumerable.Range(0, 260).Select(index => Video($"movie-{index:D3}", 3840, 2160)).ToArray();
        await context.OpenLibraryAsync(catalog);
        var model = context.Model;
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        var viewport = new BrowseViewportState(1260, AnchorId: cards[70].Id, AnchorIndex: 70, FocusedItemId: cards[74].Id);
        model.ViewportState = viewport;
        var subtitle = model.Subtitle;
        using var laterPage = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems && request.Query["StartIndex"] == "200"
            ? laterPage.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var filtering = model.SetLumenQualityFilterAsync("4K", TestContext.Current.CancellationToken);
        var request = await laterPage.WaitForRequestAsync();
        Assert.Equal("200", request.Query["Limit"]);
        Assert.Equal("4K", model.BrowseQualityFilter);
        Assert.True(model.IsBackgroundLoading);
        AssertSameCards(cards, model.Items);
        Assert.Equal(260, model.TotalItemsCount);

        laterPage.Return(repeatPage ? Page(catalog.Take(200).ToArray(), catalog.Length) : Failure());
        await filtering;

        Assert.Null(model.BrowseQualityFilter);
        AssertSameCards(cards, model.Items);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal(subtitle, model.Subtitle);
        Assert.Equal(260, model.TotalItemsCount);
        Assert.True(model.HasError);
        Assert.True(model.CanLoadMore);
        Assert.False(model.IsBusy);

        context.Handler.RespondAsync = observed => Task.FromResult(context.DefaultResponse(observed));
        var requestStart = context.Handler.Requests.Length;
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        var next = Assert.Single(context.Handler.Requests.Skip(requestStart), observed => observed.IsItems);
        Assert.Equal("96", next.Query["StartIndex"]);
        Assert.Equal("48", next.Query["Limit"]);
        Assert.Equal(144, model.Items.Count);
        Assert.Null(model.BrowseQualityFilter);
    }

    [Theory]
    [InlineData("Series", "Episode")]
    [InlineData("Season", "Episode")]
    [InlineData("BoxSet", "Movie")]
    public async Task Containers_use_real_child_resolution_and_preserve_unknown_children_as_unknown(string type, string childType)
    {
        using var context = await CreateAsync();
        BaseItemDto[] containers =
        [
            new() { Id = "folder-4k", Name = "4K folder", Type = type, IsFolder = true },
            new() { Id = "folder-hd", Name = "HD folder", Type = type, IsFolder = true },
            new() { Id = "folder-unknown", Name = "Unknown folder", Type = type, IsFolder = true }
        ];
        var children = new Dictionary<string, BaseItemDto[]>
        {
            ["folder-4k"] = [Video("child-uhd", 3840, 2160, childType), Video("child-hd", 1920, 1080, childType)],
            ["folder-hd"] = [Video("child-letterbox", 1920, 800, childType)],
            ["folder-unknown"] = [Movie("child-unknown") with { Type = childType }]
        };
        await context.OpenLibraryAsync(containers);
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems
            && children.TryGetValue(request.Query.GetValueOrDefault("ParentId", string.Empty), out var descendants)
                ? Page(descendants) : context.DefaultResponse(request));
        var model = context.Model;
        var requestStart = context.Handler.Requests.Length;

        await model.SetLumenQualityFilterAsync("4K", TestContext.Current.CancellationToken);

        Assert.Equal("folder-4k", Assert.Single(model.Items).Id);
        Assert.Equal(1, model.TotalItemsCount);
        var childRequests = context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems
            && children.ContainsKey(request.Query.GetValueOrDefault("ParentId", string.Empty))).ToArray();
        Assert.Equal(new[] { "folder-4k", "folder-hd", "folder-unknown" },
            childRequests.Select(request => request.Query["ParentId"]).Order(StringComparer.Ordinal));
        Assert.All(childRequests, request =>
        {
            Assert.Equal("true", request.Query["Recursive"]);
            Assert.Equal("Video", request.Query["MediaTypes"]);
            AssertQualityFields(request);
        });
        requestStart = context.Handler.Requests.Length;

        await model.SetLumenQualityFilterAsync("1080p", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "folder-4k", "folder-hd" }, model.Items.Select(item => item.Id));
        Assert.Equal(2, model.TotalItemsCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Refresh_reclassifies_changed_video_metadata_instead_of_reusing_an_old_quality_catalog()
    {
        using var context = await CreateAsync();
        BaseItemDto[] catalog =
        [
            Video("a-promoted", 1920, 1080),
            Video("b-retained", 3840, 2160),
            Video("c-demoted", 3840, 2160)
        ];
        await context.OpenLibraryAsync(catalog);
        var model = context.Model;
        await model.SetLumenQualityFilterAsync("4K", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "b-retained", "c-demoted" }, model.Items.Select(item => item.Id));
        var retained = Assert.Single(model.Items, item => item.Id == "b-retained");
        context.Catalog =
        [
            Video("a-promoted", 3840, 2160),
            Video("b-retained", 3840, 2160) with { Name = "Retained refreshed" },
            Video("c-demoted", 1920, 1080)
        ];
        var requestStart = context.Handler.Requests.Length;

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal("4K", model.BrowseQualityFilter);
        Assert.Equal(new[] { "a-promoted", "b-retained" }, model.Items.Select(item => item.Id));
        Assert.Same(retained, model.Items[1]);
        Assert.Equal("Retained refreshed", retained.Title);
        Assert.Equal(2, model.TotalItemsCount);
        var rawPage = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("0", rawPage.Query["StartIndex"]);
        Assert.Equal("200", rawPage.Query["Limit"]);
        AssertQualityFields(rawPage);
        requestStart = context.Handler.Requests.Length;

        await model.SetLumenQualityFilterAsync("1080p", TestContext.Current.CancellationToken);
        Assert.Equal("c-demoted", Assert.Single(model.Items).Id);
        Assert.Equal(1, model.TotalItemsCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.HasError);
    }

    private static BaseItemDto Video(string id, int width, int height, string type = "Movie") => Movie(id) with
    {
        Type = type,
        MediaStreams = [VideoStream(width, height)]
    };

    private static MediaStream VideoStream(int width, int height) => new() { Type = "Video", Width = width, Height = height };

    private static void AssertQualityFields(ObservedRequest request)
    {
        var fields = request.Query["Fields"].Split(',');
        Assert.Contains("MediaStreams", fields);
        Assert.Contains("MediaSources", fields);
    }
}
