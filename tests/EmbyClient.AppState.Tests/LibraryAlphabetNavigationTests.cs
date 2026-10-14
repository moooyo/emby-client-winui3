using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryAlphabetNavigationTests
{
    [Fact]
    public async Task A_letter_beyond_the_first_page_loads_a_bounded_window_and_continues_at_the_absolute_offset()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        Assert.DoesNotContain(model.Items, item => item.Item.SortName!.StartsWith("M", StringComparison.Ordinal));
        var requestStart = context.Handler.Requests.Length;

        var target = await model.JumpToBrowseInitialAsync(" m ", TestContext.Current.CancellationToken);

        Assert.Equal("m-000", target?.Id);
        Assert.Equal(96, model.BrowseWindowStartIndex);
        Assert.Equal(48, model.Items.Count);
        Assert.Equal(240, model.QueryCount);
        Assert.True(model.HasMore);
        Assert.False(model.IsBrowseNavigationPending);
        var jumpRequests = context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems).ToArray();
        Assert.Equal(2, jumpRequests.Length);
        var fullCount = Assert.Single(jumpRequests, request => request.Query["Limit"] == "1");
        Assert.False(fullCount.Query.ContainsKey("NameStartsWithOrGreater"));
        Assert.Equal("false", fullCount.Query["EnableImages"]);
        var letterPage = Assert.Single(jumpRequests, request => request.Query["Limit"] == "48");
        Assert.Equal("M", letterPage.Query["NameStartsWithOrGreater"]);
        Assert.All(jumpRequests, AssertTypedNameScope);
        requestStart = context.Handler.Requests.Length;

        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));

        var more = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("144", more.Query["StartIndex"]);
        Assert.False(more.Query.ContainsKey("NameStartsWithOrGreater"));
        Assert.Equal(context.Catalog.Skip(96).Take(96).Select(item => item.Id), model.Items.Select(item => item.Id));
        Assert.Equal(240, model.QueryCount);
    }

    [Fact]
    public async Task Letter_counts_and_pages_preserve_favorite_genre_watch_and_explicit_prefix_filters()
    {
        using var context = await CreateAlphabetContextAsync();
        context.Catalog = context.Catalog.Select(item => item with
        {
            Genres = ["Drama"], UserData = new() { IsFavorite = true, Played = false }
        }).Concat([Movie("unrelated", "M unrelated") with
        {
            SortName = "M unrelated", Genres = ["Comedy"], UserData = new() { IsFavorite = false, Played = true }
        }]).ToArray();
        var model = context.Model;
        await model.ShowFavoritesAsync(TestContext.Current.CancellationToken);
        await model.SetLumenBrowseFiltersAsync("Drama", WatchStatusFilter.Unplayed, "M", TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;

        var target = await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);

        Assert.Equal("m-000", target?.Id);
        Assert.Equal(0, model.BrowseWindowStartIndex);
        Assert.Equal(96, model.QueryCount);
        Assert.Equal("M", model.BrowseNameStartsWith);
        Assert.All(context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems), request =>
        {
            Assert.Equal("true", request.Query["IsFavorite"]);
            Assert.Equal("false", request.Query["IsPlayed"]);
            Assert.Equal("Drama", request.Query["Genres"]);
            Assert.Equal("M", request.Query["NameStartsWith"]);
            Assert.Equal("SortName", request.Query["SortBy"]);
            Assert.Equal("Ascending", request.Query["SortOrder"]);
        });
    }

    [Fact]
    public async Task End_and_home_use_the_full_sorted_query_without_loading_intervening_pages()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        context.Catalog = context.Catalog.Select((item, index) => item with { ProductionYear = 2000 + index }).ToArray();
        await model.SetBrowseOptionsAsync("ProductionYear", true, WatchStatusFilter.All, TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;

        var last = await model.JumpToBrowseBoundaryAsync(true, TestContext.Current.CancellationToken);

        Assert.False(model.SupportsAlphabetNavigation);
        Assert.Equal("a-000", last?.Id);
        Assert.Equal(192, model.BrowseWindowStartIndex);
        Assert.Equal(48, model.Items.Count);
        Assert.False(model.HasMore);
        var requests = context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal("192", Assert.Single(requests, request => request.Query["Limit"] == "48").Query["StartIndex"]);
        Assert.All(requests, request =>
        {
            Assert.Equal("ProductionYear,SortName", request.Query["SortBy"]);
            Assert.Equal("Descending", request.Query["SortOrder"]);
        });
        requestStart = context.Handler.Requests.Length;

        var first = await model.JumpToBrowseBoundaryAsync(false, TestContext.Current.CancellationToken);

        Assert.Equal("z-047", first?.Id);
        Assert.Equal(0, model.BrowseWindowStartIndex);
        Assert.True(model.HasMore);
        Assert.Equal("0", Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems).Query["StartIndex"]);
    }

    [Fact]
    public async Task A_missing_letter_uses_the_next_title_and_hash_returns_to_the_start()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;

        var next = await model.JumpToBrowseInitialAsync("N", TestContext.Current.CancellationToken);

        Assert.Equal("z-000", next?.Id);
        Assert.Equal(192, model.BrowseWindowStartIndex);
        Assert.False(model.HasMore);

        var first = await model.JumpToBrowseInitialAsync("#", TestContext.Current.CancellationToken);

        Assert.Equal("a-000", first?.Id);
        Assert.Equal(0, model.BrowseWindowStartIndex);
        Assert.Equal(240, model.QueryCount);
    }

    [Fact]
    public async Task History_restores_the_window_and_refresh_reads_only_that_loaded_window()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        var viewport = new BrowseViewportState(VerticalOffset: 830, FocusedItemId: cards[8].Id);
        model.ViewportState = viewport;

        await model.ShowItemAsync(cards[8], TestContext.Current.CancellationToken);
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        AssertSameCards(cards, model.Items);
        Assert.Same(viewport, model.ViewportState);
        Assert.Equal(96, model.BrowseWindowStartIndex);
        Assert.Equal(240, model.QueryCount);
        var requestStart = context.Handler.Requests.Length;

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        var refreshed = context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems).ToArray();
        Assert.Equal(new[] { "96", "144" }, refreshed.Select(request => request.Query["StartIndex"]));
        AssertSameCards(cards, model.Items);
        Assert.Equal(96, model.BrowseWindowStartIndex);
        requestStart = context.Handler.Requests.Length;

        await model.LoadMoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal("192", Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems).Query["StartIndex"]);
        Assert.False(model.HasMore);
    }

    [Fact]
    public async Task A_stale_letter_response_cannot_replace_a_newer_letter_window()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        var previous = model.Items.ToArray();
        using var pending = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems && request.Query.GetValueOrDefault("NameStartsWithOrGreater") == "M"
            ? pending.RespondAsync(request) : Task.FromResult(BrowseResponse(context, request));
        var older = model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);
        var oldRequest = await pending.WaitForRequestAsync();
        AssertSameCards(previous, model.Items);
        Assert.True(model.IsBrowseNavigationPending);
        Assert.Equal("M", model.PendingBrowseInitial);

        var latest = await model.JumpToBrowseInitialAsync("Z", TestContext.Current.CancellationToken);
        pending.Return(Page(context.Catalog.Skip(96).Take(48).ToArray(), 144));

        Assert.Null(await older);
        Assert.True(oldRequest.CancellationToken.IsCancellationRequested);
        Assert.Equal("z-000", latest?.Id);
        Assert.Equal(192, model.BrowseWindowStartIndex);
        Assert.All(model.Items, item => Assert.StartsWith("z-", item.Id));
        Assert.False(model.IsBrowseNavigationPending);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Cancelling_a_jump_preserves_the_committed_window_and_its_paging_lifetime()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        using var pending = new RequestGate();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        context.Handler.RespondAsync = request => request.IsItems && request.Query["Limit"] == "1"
            ? pending.RespondAsync(request) : Task.FromResult(BrowseResponse(context, request));
        var jumping = model.JumpToBrowseBoundaryAsync(true, cancellation.Token);
        var request = await pending.WaitForRequestAsync();

        cancellation.Cancel();
        pending.Return(Page(context.Catalog.Take(1).ToArray(), 240));

        Assert.Null(await jumping);
        Assert.True(request.CancellationToken.IsCancellationRequested);
        AssertSameCards(cards, model.Items);
        Assert.Equal(96, model.BrowseWindowStartIndex);
        Assert.Equal(240, model.QueryCount);
        Assert.False(model.IsBusy);
        Assert.True(model.CanLoadMore);
        context.Handler.RespondAsync = observed => Task.FromResult(BrowseResponse(context, observed));
        var requestStart = context.Handler.Requests.Length;

        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));

        Assert.Equal("144", Assert.Single(context.Handler.Requests.Skip(requestStart), observed => observed.IsItems).Query["StartIndex"]);
    }

    [Fact]
    public async Task A_failed_jump_does_not_destroy_existing_cards_counts_or_the_next_page_offset()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems && request.Query.ContainsKey("NameStartsWithOrGreater")
            ? Failure() : BrowseResponse(context, request));

        Assert.Null(await model.JumpToBrowseInitialAsync("Z", TestContext.Current.CancellationToken));

        AssertSameCards(cards, model.Items);
        Assert.Equal(96, model.BrowseWindowStartIndex);
        Assert.Equal(240, model.QueryCount);
        Assert.True(model.HasError);
        Assert.False(model.IsBusy);
        context.Handler.RespondAsync = request => Task.FromResult(BrowseResponse(context, request));
        var requestStart = context.Handler.Requests.Length;

        await model.LoadMoreAsync(TestContext.Current.CancellationToken);

        Assert.Equal("144", Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems).Query["StartIndex"]);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Prefix_discovery_uses_the_full_filtered_scope_and_includes_letters_from_unloaded_pages()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        context.Catalog = context.Catalog.Select(item => item with { Genres = ["Drama"], UserData = new() { Played = false } }).ToArray();
        await model.SetLumenBrowseFiltersAsync("Drama", WatchStatusFilter.Unplayed, cancellationToken: TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;

        await model.LoadBrowseAlphabetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "A", "M", "Z" }, model.AvailableBrowseInitials);
        var prefixes = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.Is("/Items/Prefixes"));
        Assert.Equal("user-a", prefixes.Query["UserId"]);
        Assert.Equal("Movie", prefixes.Query["IncludeItemTypes"]);
        Assert.Equal("Drama", prefixes.Query["Genres"]);
        Assert.Equal("false", prefixes.Query["IsPlayed"]);
        Assert.Equal("false", prefixes.Query["EnableImages"]);
        Assert.False(prefixes.Query.ContainsKey("StartIndex"));
        Assert.False(prefixes.Query.ContainsKey("Limit"));
        Assert.DoesNotContain(model.Items, item => item.Id.StartsWith("m-", StringComparison.Ordinal));
        requestStart = context.Handler.Requests.Length;
        await model.LoadBrowseAlphabetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task Unsupported_prefix_discovery_keeps_direct_letter_navigation_available()
    {
        using var context = await CreateAlphabetContextAsync();
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Items/Prefixes")
            ? Json("{}", HttpStatusCode.NotFound) : BrowseResponse(context, request));
        var model = context.Model;

        await model.LoadBrowseAlphabetAsync(TestContext.Current.CancellationToken);
        var target = await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);

        Assert.Null(model.AvailableBrowseInitials);
        Assert.Equal("m-000", target?.Id);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Missing_server_counts_preserve_the_old_window_instead_of_scanning_the_library()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        var cards = model.Items.ToArray();
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems && request.Query["Limit"] == "1"
            ? UnknownCountPage(context.Catalog.Take(1).ToArray()) : BrowseResponse(context, request));
        var requestStart = context.Handler.Requests.Length;

        Assert.Null(await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken));

        AssertSameCards(cards, model.Items);
        Assert.Equal(0, model.BrowseWindowStartIndex);
        Assert.Equal(240, model.QueryCount);
        Assert.True(model.HasError);
        Assert.Equal(2, context.Handler.Requests.Skip(requestStart).Count(request => request.IsItems));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inconsistent_range_counts_retry_once_and_never_commit_an_invalid_paging_offset(bool recovered)
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        var cards = model.Items.ToArray();
        var countReads = 0;
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems && request.Query["Limit"] == "1"
            ? Page(context.Catalog.Take(1).ToArray(), Interlocked.Increment(ref countReads) > 1 && recovered ? 240 : 40)
            : BrowseResponse(context, request));
        var requestStart = context.Handler.Requests.Length;

        var target = await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);

        Assert.Equal(2, countReads);
        Assert.Equal(3, context.Handler.Requests.Skip(requestStart).Count(request => request.IsItems));
        Assert.Equal(240, model.QueryCount);
        if (recovered)
        {
            Assert.Equal("m-000", target?.Id);
            Assert.Equal(96, model.BrowseWindowStartIndex);
            Assert.False(model.HasError);
        }
        else
        {
            Assert.Null(target);
            AssertSameCards(cards, model.Items);
            Assert.Equal(0, model.BrowseWindowStartIndex);
            Assert.True(model.HasError);
        }
    }

    [Fact]
    public async Task Resolution_filtered_letters_and_boundaries_reuse_the_existing_server_ordered_catalog()
    {
        using var context = await CreateAlphabetContextAsync();
        context.Catalog = context.Catalog.Select(item => item with
        {
            MediaStreams = [new() { Type = "Video", Width = 3840, Height = 2160 }]
        }).Concat([Movie("hd-only", "G HD title") with
        {
            SortName = "G HD title", MediaStreams = [new() { Type = "Video", Width = 1920, Height = 1080 }]
        }]).ToArray();
        var model = context.Model;
        await model.SetLumenQualityFilterAsync("4K", TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;

        await model.LoadBrowseAlphabetAsync(TestContext.Current.CancellationToken);
        var letter = await model.JumpToBrowseInitialAsync("M", TestContext.Current.CancellationToken);
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var last = await model.JumpToBrowseBoundaryAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "A", "M", "Z" }, model.AvailableBrowseInitials);
        Assert.Equal("m-000", letter?.Id);
        Assert.Equal("z-047", last?.Id);
        Assert.Equal(192, model.BrowseWindowStartIndex);
        Assert.Equal(240, model.QueryCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Refresh_clamps_a_removed_last_window_without_reloading_the_library_from_zero()
    {
        using var context = await CreateAlphabetContextAsync();
        var model = context.Model;
        await model.JumpToBrowseBoundaryAsync(true, TestContext.Current.CancellationToken);
        context.Catalog = context.Catalog.Take(100).ToArray();
        var requestStart = context.Handler.Requests.Length;

        await model.RefreshAsync(TestContext.Current.CancellationToken);

        var requests = context.Handler.Requests.Skip(requestStart).Where(request => request.IsItems).ToArray();
        Assert.Equal(new[] { "192", "52" }, requests.Select(request => request.Query["StartIndex"]));
        Assert.Equal(52, model.BrowseWindowStartIndex);
        Assert.Equal(context.Catalog.Skip(52).Select(item => item.Id), model.Items.Select(item => item.Id));
        Assert.Equal(100, model.QueryCount);
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
    }

    private static async Task<LibraryTestContext> CreateAlphabetContextAsync()
    {
        var context = await CreateAsync();
        context.Catalog = Group("a", "A", 96).Concat(Group("m", "M", 96)).Concat(Group("z", "Z", 48)).ToArray();
        context.Handler.RespondAsync = request => Task.FromResult(BrowseResponse(context, request));
        await context.Model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken);
        return context;
    }

    private static IEnumerable<BaseItemDto> Group(string idPrefix, string initial, int count) =>
        Enumerable.Range(0, count).Select(index => Movie($"{idPrefix}-{index:D3}", $"The {initial} title {index:D3}") with
        {
            SortName = $"{initial} title {index:D3}"
        });

    private static void AssertTypedNameScope(ObservedRequest request)
    {
        Assert.Equal("Movie", request.Query["IncludeItemTypes"]);
        Assert.Equal("true", request.Query["Recursive"]);
        Assert.Equal("SortName", request.Query["SortBy"]);
        Assert.Equal("Ascending", request.Query["SortOrder"]);
        Assert.False(request.Query.ContainsKey("ParentId"));
    }

    private static HttpResponseMessage BrowseResponse(LibraryTestContext context, ObservedRequest request)
    {
        if (request.IsItems)
        {
            var matches = MatchCatalog(context.Catalog, request.Query);
            var offset = int.Parse(request.Query.GetValueOrDefault("StartIndex", "0"));
            var limit = int.Parse(request.Query.GetValueOrDefault("Limit", "48"));
            return Page(matches.Skip(offset).Take(limit).ToArray(), matches.Length);
        }
        if (request.Is("/Items/Prefixes"))
        {
            var initials = MatchCatalog(context.Catalog, request.Query).Select(item => item.SortName![0].ToString())
                .Distinct(StringComparer.Ordinal).ToArray();
            return Json("[" + string.Join(",", initials.Select(initial => $"{{\"Name\":\"{initial}\",\"Value\":\"opaque\"}}")) + "]");
        }
        if (request.Uri.AbsolutePath.Contains("/Users/user-a/Items/", StringComparison.Ordinal)
            && context.Catalog.FirstOrDefault(item => request.Is("/Items/" + item.Id)) is { } detail) return Item(detail);
        return context.DefaultResponse(request);
    }

    private static BaseItemDto[] MatchCatalog(BaseItemDto[] catalog, Dictionary<string, string> query)
    {
        IEnumerable<BaseItemDto> matches = catalog;
        if (query.TryGetValue("IncludeItemTypes", out var types))
            matches = matches.Where(item => types.Split(',').Contains(item.Type, StringComparer.Ordinal));
        if (query.TryGetValue("Genres", out var genre))
            matches = matches.Where(item => (item.Genres ?? []).Contains(genre, StringComparer.OrdinalIgnoreCase));
        if (query.TryGetValue("IsPlayed", out var played))
            matches = matches.Where(item => (item.UserData?.Played == true) == bool.Parse(played));
        if (query.TryGetValue("IsFavorite", out var favorite))
            matches = matches.Where(item => (item.UserData?.IsFavorite == true) == bool.Parse(favorite));
        if (query.TryGetValue("NameStartsWith", out var prefix))
            matches = matches.Where(item => (item.SortName ?? item.Name ?? string.Empty).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (query.TryGetValue("NameStartsWithOrGreater", out var lower))
            matches = matches.Where(item => StringComparer.OrdinalIgnoreCase.Compare(item.SortName ?? item.Name, lower) >= 0);
        var descending = query.GetValueOrDefault("SortOrder") == "Descending";
        if (query.GetValueOrDefault("SortBy")?.StartsWith("ProductionYear", StringComparison.Ordinal) == true)
            return descending ? matches.OrderByDescending(item => item.ProductionYear).ThenByDescending(item => item.SortName, StringComparer.Ordinal).ToArray()
                : matches.OrderBy(item => item.ProductionYear).ThenBy(item => item.SortName, StringComparer.Ordinal).ToArray();
        return descending ? matches.OrderByDescending(item => item.SortName ?? item.Name, StringComparer.Ordinal).ToArray()
            : matches.OrderBy(item => item.SortName ?? item.Name, StringComparer.Ordinal).ToArray();
    }

    private static HttpResponseMessage UnknownCountPage(BaseItemDto[] items) => Json(JsonSerializer.Serialize(
        new QueryResult<BaseItemDto> { Items = items, TotalRecordCount = null }, EmbyJsonContext.Default.QueryResultBaseItemDto));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
