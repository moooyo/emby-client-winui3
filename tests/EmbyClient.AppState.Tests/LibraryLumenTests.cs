using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryLumenTests
{
    private const string LumenSearchTypes = "Movie,Series,BoxSet";
    private const string GenericSearchTypes = "Movie,Series,Episode,Video,MusicVideo,Audio,MusicAlbum,BoxSet";

    [Theory]
    [InlineData("Movie")]
    [InlineData("Series")]
    public async Task Typed_entries_query_all_libraries_and_preserve_the_server_count_while_paging(string type)
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(65).Select(item => item with { Type = type, IsFolder = type == "Series" }).ToArray();
        var model = context.Model;
        var requestStart = context.Handler.Requests.Length;

        await model.ShowMediaTypeAsync(type, TestContext.Current.CancellationToken);

        var first = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal(type, first.Query["IncludeItemTypes"]);
        Assert.Equal("true", first.Query["Recursive"]);
        Assert.Equal("0", first.Query["StartIndex"]);
        Assert.Equal("48", first.Query["Limit"]);
        Assert.False(first.Query.ContainsKey("ParentId"));
        Assert.False(first.Query.ContainsKey("SearchTerm"));
        Assert.Contains("OriginalTitle", first.Query["Fields"].Split(','));
        Assert.Contains("Genres", first.Query["Fields"].Split(','));
        Assert.Equal(type, model.BrowseMediaType);
        Assert.Null(model.SelectedLibraryId);
        Assert.Equal(48, model.Items.Count);
        Assert.Equal(65, model.TotalItemsCount);
        Assert.Equal(65, model.QueryCount);
        Assert.True(model.CanLoadMore);

        var cards = model.Items.ToArray();
        requestStart = context.Handler.Requests.Length;
        await model.ShowMediaTypeAsync(type, TestContext.Current.CancellationToken);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        AssertSameCards(cards, model.Items);

        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        var more = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("48", more.Query["StartIndex"]);
        Assert.Equal(type, more.Query["IncludeItemTypes"]);
        Assert.False(more.Query.ContainsKey("ParentId"));
        Assert.Equal(65, model.Items.Count);
        Assert.Equal(65, model.TotalItemsCount);
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Series")]
    public async Task Genres_page_by_raw_offset_and_cache_the_full_typed_catalog_not_the_active_filter(string type)
    {
        using var context = await CreateAsync();
        context.Catalog = [Movie("available")];
        var model = context.Model;
        await model.ShowMediaTypeAsync(type, TestContext.Current.CancellationToken);
        var genres = Enumerable.Range(0, 501).Select(index => new BaseItemDto { Name = $"Genre {index:D3}" })
            .Concat([new() { Name = " Genre 000 " }, new() { Name = "genre 001" }, new() { Name = " " }]).ToArray();
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Genres")
            ? Page(genres.Skip(int.Parse(request.Query["StartIndex"])).Take(500).ToArray(), genres.Length)
            : context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;

        await model.LoadLumenGenresAsync(TestContext.Current.CancellationToken);

        var genreRequests = context.Handler.Requests.Skip(requestStart).Where(request => request.Is("/Genres")).ToArray();
        Assert.Equal(new[] { "0", "500" }, genreRequests.Select(request => request.Query["StartIndex"]));
        Assert.All(genreRequests, request =>
        {
            Assert.Equal(type, request.Query["IncludeItemTypes"]);
            Assert.Equal("500", request.Query["Limit"]);
            Assert.Equal("true", request.Query["Recursive"]);
            Assert.Equal("false", request.Query["EnableImages"]);
            Assert.Equal("false", request.Query["EnableUserData"]);
            Assert.False(request.Query.ContainsKey("ParentId"));
            Assert.False(request.Query.ContainsKey("Genres"));
            Assert.False(request.Query.ContainsKey("NameStartsWith"));
        });
        Assert.Equal(genres.Take(501).Select(item => item.Name!), model.AvailableGenres);

        await model.SetLumenBrowseFiltersAsync("Genre 010", WatchStatusFilter.Unplayed, "G",
            TestContext.Current.CancellationToken);
        requestStart = context.Handler.Requests.Length;
        await model.LoadLumenGenresAsync(TestContext.Current.CancellationToken);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.Equal(501, model.AvailableGenres.Count);

        await model.ShowMediaTypeAsync(type == "Movie" ? "Series" : "Movie", TestContext.Current.CancellationToken);
        Assert.Empty(model.AvailableGenres);
    }

    [Fact]
    public async Task Failed_genre_prefix_and_watch_filters_restore_the_previous_query_count_window_and_offset()
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(120);
        var model = context.Model;
        await model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken);
        await model.SetBrowseOptionsAsync("CommunityRating", true, WatchStatusFilter.Played,
            TestContext.Current.CancellationToken);
        await model.SetLumenBrowseFiltersAsync(" Drama ", WatchStatusFilter.Played, " A ",
            TestContext.Current.CancellationToken);
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        using var replacement = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? replacement.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var changing = model.SetLumenBrowseFiltersAsync(" Action ", WatchStatusFilter.Unplayed, " B ",
            TestContext.Current.CancellationToken);
        var pending = await replacement.WaitForRequestAsync();
        Assert.Equal("Action", pending.Query["Genres"]);
        Assert.Equal("B", pending.Query["NameStartsWith"]);
        Assert.Equal("false", pending.Query["IsPlayed"]);
        Assert.Equal("Movie", pending.Query["IncludeItemTypes"]);
        Assert.Equal("CommunityRating,SortName", pending.Query["SortBy"]);
        Assert.Equal("Descending", pending.Query["SortOrder"]);
        Assert.False(pending.Query.ContainsKey("ParentId"));
        Assert.Equal("Action", model.BrowseGenre);
        Assert.Equal("B", model.BrowseNameStartsWith);
        Assert.Equal(WatchStatusFilter.Unplayed, model.BrowseWatchFilter);
        AssertSameCards(cards, model.Items);
        Assert.Equal(120, model.TotalItemsCount);
        replacement.Return(Failure());
        await changing;

        Assert.True(model.HasError);
        Assert.Equal("Drama", model.BrowseGenre);
        Assert.Equal("A", model.BrowseNameStartsWith);
        Assert.Equal(WatchStatusFilter.Played, model.BrowseWatchFilter);
        Assert.Equal(120, model.QueryCount);
        AssertSameCards(cards, model.Items);
        Assert.True(model.CanLoadMore);

        context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        var next = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("96", next.Query["StartIndex"]);
        Assert.Equal("Drama", next.Query["Genres"]);
        Assert.Equal("A", next.Query["NameStartsWith"]);
        Assert.Equal("true", next.Query["IsPlayed"]);
        Assert.Equal("CommunityRating,SortName", next.Query["SortBy"]);
        Assert.Equal(120, model.Items.Count);
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(72)]
    public async Task Query_count_uses_only_a_valid_server_total_and_is_reset_then_restored_by_navigation(int? total)
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(48);
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems
            ? ReportedPage(context.Catalog, total) : context.DefaultResponse(request));
        var model = context.Model;
        await model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken);
        int? expected = total is >= 0 ? total : null;
        Assert.Equal(expected, model.QueryCount);
        Assert.Equal(48, model.Items.Count);
        var cards = model.Items.ToArray();
        var requestStart = context.Handler.Requests.Length;

        await model.ShowSearchAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.TotalItemsCount);
        Assert.Empty(model.Items);
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, model.TotalItemsCount);
        Assert.Equal("Movie", model.BrowseMediaType);
        AssertSameCards(cards, model.Items);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Movie")]
    [InlineData("Series")]
    [InlineData("BoxSet")]
    public async Task Search_type_tabs_change_the_media_query_without_discarding_or_refetching_people(string? type)
    {
        using var context = await CreateAsync();
        context.Catalog = [Movie("result")];
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Persons")
            ? Page([Person("person-a")]) : context.DefaultResponse(request));
        var model = context.Model;
        var firstRequestStart = context.Handler.Requests.Length;
        await model.SearchLumenAsync("  Arrival  ", debounce: false, TestContext.Current.CancellationToken);
        Assert.Equal(LumenSearchTypes, Assert.Single(context.Handler.Requests.Skip(firstRequestStart),
            request => request.IsItems).Query["IncludeItemTypes"]);
        var card = Assert.Single(model.Items);
        var person = Assert.Single(model.SearchPeople);
        if (string.IsNullOrEmpty(type)) await model.SetLumenSearchTypeAsync("Movie", TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;

        await model.SetLumenSearchTypeAsync(type, TestContext.Current.CancellationToken);

        var media = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal(string.IsNullOrEmpty(type) ? LumenSearchTypes : type, media.Query["IncludeItemTypes"]);
        Assert.Equal("Arrival", media.Query["SearchTerm"]);
        Assert.Equal("0", media.Query["StartIndex"]);
        Assert.Equal("true", media.Query["Recursive"]);
        Assert.Equal(string.IsNullOrEmpty(type) ? null : type, model.BrowseSearchType);
        Assert.Null(model.BrowseMediaType);
        Assert.Same(card, Assert.Single(model.Items));
        Assert.Same(person, Assert.Single(model.SearchPeople));
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));
        Assert.Single(context.Handler.Requests, request => request.Is("/Persons"));
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Lumen_search_limits_the_server_scope_and_preserves_an_escaped_UTF8_term()
    {
        using var context = await CreateAsync();
        context.Catalog = SearchScopeCatalog();
        context.Handler.RespondAsync = request => Task.FromResult(SearchScopeResponse(context, request));
        var model = context.Model;
        const string term = "\u591c & genre=drama + 100% #?";
        var requestStart = context.Handler.Requests.Length;

        await model.SearchLumenAsync("  " + term + "  ", debounce: false, TestContext.Current.CancellationToken);

        var media = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal(LumenSearchTypes, media.Query["IncludeItemTypes"]);
        Assert.Equal(term, media.Query["SearchTerm"]);
        Assert.Contains("%E5%A4%9C", media.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.False(media.Query.ContainsKey("genre"));
        Assert.False(media.Query.ContainsKey("ParentId"));
        Assert.Equal(string.Empty, media.Uri.Fragment);
        Assert.Equal("true", media.Query["Recursive"]);
        Assert.Equal("0", media.Query["StartIndex"]);
        Assert.Equal(term, model.SearchText);
        Assert.Null(model.BrowseSearchType);
        Assert.Equal(new[] { "Movie", "Series", "BoxSet" }, model.Items.Select(card => card.Item.Type));
        Assert.DoesNotContain(model.Items, card => card.Item.Type == "Episode");
        Assert.Equal(3, model.QueryCount);
        var people = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));
        Assert.Equal(term, people.Query["SearchTerm"]);
        Assert.False(people.Query.ContainsKey("IncludeItemTypes"));
        Assert.Equal("person-a", Assert.Single(model.SearchPeople).Id);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Movie")]
    [InlineData("Series")]
    [InlineData("BoxSet")]
    public async Task Generic_search_does_not_inherit_Lumen_scopes_and_Lumen_restores_its_own_scope(string? type)
    {
        using var context = await CreateAsync();
        context.Catalog = SearchScopeCatalog();
        context.Handler.RespondAsync = request => Task.FromResult(SearchScopeResponse(context, request));
        var model = context.Model;
        await model.SearchLumenAsync("shared term", debounce: false, TestContext.Current.CancellationToken);
        await model.SetLumenSearchTypeAsync(type ?? "Movie", TestContext.Current.CancellationToken);
        if (type is null) await model.SetLumenSearchTypeAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(type, model.BrowseSearchType);
        var requestStart = context.Handler.Requests.Length;

        await model.SearchAsync("shared term", debounce: false, TestContext.Current.CancellationToken);

        var generic = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal(GenericSearchTypes, generic.Query["IncludeItemTypes"]);
        Assert.Equal("shared term", generic.Query["SearchTerm"]);
        Assert.Contains("Episode", generic.Query["IncludeItemTypes"].Split(','));
        Assert.Null(model.BrowseSearchType);
        Assert.Equal(8, model.QueryCount);
        Assert.Equal(8, model.Items.Count);
        Assert.Contains(model.Items, card => card.Id == "match-Episode" && card.Item.Type == "Episode");
        Assert.Empty(model.SearchPeople);
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));
        requestStart = context.Handler.Requests.Length;

        await model.SearchLumenAsync("shared term", debounce: false, TestContext.Current.CancellationToken);

        var lumen = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal(LumenSearchTypes, lumen.Query["IncludeItemTypes"]);
        Assert.Equal("shared term", lumen.Query["SearchTerm"]);
        Assert.Null(model.BrowseSearchType);
        Assert.Equal(3, model.QueryCount);
        Assert.Equal(new[] { "Movie", "Series", "BoxSet" }, model.Items.Select(card => card.Item.Type));
        Assert.DoesNotContain(model.Items, card => card.Item.Type == "Episode");
        Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));
        Assert.Equal("person-a", Assert.Single(model.SearchPeople).Id);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_new_Lumen_term_preserves_the_typed_tab_but_generic_search_resets_it()
    {
        using var context = await CreateAsync();
        context.Catalog = SearchScopeCatalog();
        context.Handler.RespondAsync = request => Task.FromResult(SearchScopeResponse(context, request));
        var model = context.Model;
        await model.SearchLumenAsync("first term", debounce: false, TestContext.Current.CancellationToken);
        await model.SetLumenSearchTypeAsync("Movie", TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;

        await model.SearchLumenAsync("second term", debounce: false, TestContext.Current.CancellationToken);

        var typed = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("Movie", typed.Query["IncludeItemTypes"]);
        Assert.Equal("second term", typed.Query["SearchTerm"]);
        Assert.Equal("Movie", model.BrowseSearchType);
        Assert.Equal("Movie", Assert.Single(model.Items).Item.Type);
        Assert.Equal(1, model.QueryCount);
        var people = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));
        Assert.Equal("second term", people.Query["SearchTerm"]);
        Assert.False(people.Query.ContainsKey("IncludeItemTypes"));
        requestStart = context.Handler.Requests.Length;

        await model.SearchAsync("second term", debounce: false, TestContext.Current.CancellationToken);

        var generic = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal(GenericSearchTypes, generic.Query["IncludeItemTypes"]);
        Assert.Null(model.BrowseSearchType);
        Assert.Equal(8, model.QueryCount);
        Assert.Contains(model.Items, card => card.Item.Type == "Episode");
        Assert.Empty(model.SearchPeople);
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replaced_search_media_and_people_responses_cannot_commit_results_or_errors(bool failObsolete)
    {
        using var context = await CreateAsync();
        var model = context.Model;
        using var obsoleteMedia = new RequestGate();
        using var obsoletePeople = new RequestGate();
        context.Handler.RespondAsync = request =>
        {
            var term = request.Query.GetValueOrDefault("SearchTerm");
            if (request.IsItems && term == "old") return obsoleteMedia.RespondAsync(request);
            if (request.Is("/Persons") && term == "old") return obsoletePeople.RespondAsync(request);
            if (request.IsItems && term == "new") return Task.FromResult(Page([Movie("new-media")]));
            if (request.Is("/Persons") && term == "new") return Task.FromResult(Page([Person("new-person")]));
            return Task.FromResult(context.DefaultResponse(request));
        };

        var obsolete = model.SearchLumenAsync("old", debounce: false, TestContext.Current.CancellationToken);
        var oldMediaRequest = await obsoleteMedia.WaitForRequestAsync();
        var oldPeopleRequest = await obsoletePeople.WaitForRequestAsync();
        await model.SearchLumenAsync("new", debounce: false, TestContext.Current.CancellationToken);
        var media = Assert.Single(model.Items);
        var person = Assert.Single(model.SearchPeople);
        var revision = model.NavigationRevision;
        Assert.True(oldMediaRequest.CancellationToken.IsCancellationRequested);
        Assert.True(oldPeopleRequest.CancellationToken.IsCancellationRequested);

        obsoleteMedia.Return(failObsolete ? Failure() : Page([Movie("old-media")], 500));
        obsoletePeople.Return(failObsolete ? Failure() : Page([Person("old-person")], 600));
        await obsolete;

        Assert.Equal("new", model.SearchText);
        Assert.Equal("new-media", media.Id);
        Assert.Equal("new-person", person.Id);
        Assert.Same(media, Assert.Single(model.Items));
        Assert.Same(person, Assert.Single(model.SearchPeople));
        Assert.Equal(1, model.QueryCount);
        Assert.Equal(1, model.SearchPeopleTotal);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Empty(model.SearchPeopleError);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
        Assert.False(model.SearchPeopleIsBusy);
        Assert.False(model.HasPendingSearch);
    }

    [Fact]
    public async Task A_slow_people_query_does_not_block_completed_media_results_or_media_paging()
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(72);
        var model = context.Model;
        using var people = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Persons")
            ? people.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var searching = model.SearchLumenAsync("arrival", debounce: false, TestContext.Current.CancellationToken);
        await people.WaitForRequestAsync();
        Assert.Equal(48, model.Items.Count);
        Assert.Equal(72, model.TotalItemsCount);
        Assert.True(model.SearchPeopleIsBusy);
        Assert.False(searching.IsCompleted);
        Assert.False(model.HasPendingSearch);
        Assert.True(model.CanLoadMore);
        var requestStart = context.Handler.Requests.Length;

        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        var more = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("48", more.Query["StartIndex"]);
        Assert.Equal(LumenSearchTypes, more.Query["IncludeItemTypes"]);
        Assert.Equal(72, model.Items.Count);
        Assert.Empty(model.SearchPeople);

        people.Return(Page([Person("person-a")], 1));
        await searching;
        Assert.Equal("person-a", Assert.Single(model.SearchPeople).Id);
        Assert.Equal(72, model.QueryCount);
        Assert.Equal(1, model.SearchPeopleTotal);
        Assert.False(model.SearchPeopleIsBusy);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_failed_first_people_page_can_retry_from_zero_without_reloading_successful_media()
    {
        using var context = await CreateAsync();
        context.Catalog = [Movie("media-a")];
        var peopleRequests = 0;
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Persons")
            ? ++peopleRequests == 1 ? Failure() : Page([Person("person-a")])
            : context.DefaultResponse(request));
        var model = context.Model;

        await model.SearchLumenAsync("arrival", debounce: false, TestContext.Current.CancellationToken);
        var media = Assert.Single(model.Items);
        Assert.Empty(model.SearchPeople);
        Assert.NotEmpty(model.SearchPeopleError);
        Assert.False(model.HasError);
        Assert.False(model.SearchPeopleIsBusy);
        var requestStart = context.Handler.Requests.Length;

        await model.LoadMoreSearchPeopleAsync(TestContext.Current.CancellationToken);

        var retry = Assert.Single(context.Handler.Requests.Skip(requestStart));
        Assert.True(retry.Is("/Persons"));
        Assert.Equal("0", retry.Query["StartIndex"]);
        Assert.Equal("arrival", retry.Query["SearchTerm"]);
        Assert.Same(media, Assert.Single(model.Items));
        Assert.Equal("person-a", Assert.Single(model.SearchPeople).Id);
        Assert.Equal(1, model.SearchPeopleTotal);
        Assert.Empty(model.SearchPeopleError);
        Assert.False(model.SearchPeopleHasMore);
        Assert.False(model.SearchPeopleIsBusy);
    }

    [Fact]
    public async Task Back_restores_independent_media_and_people_counts_windows_and_raw_paging_offsets()
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(120);
        var people = Enumerable.Range(0, 73).Select(index => Person($"person-{index:D3}")).ToArray();
        context.Handler.RespondAsync = request =>
        {
            if (request.Is("/Persons")) return Task.FromResult(Page(people
                .Skip(int.Parse(request.Query["StartIndex"])).Take(48).ToArray(), people.Length));
            var detail = context.Catalog.FirstOrDefault(item => request.Is($"/Users/user-a/Items/{item.Id}"));
            return Task.FromResult(detail is null ? context.DefaultResponse(request) : Item(detail));
        };
        var model = context.Model;
        await model.SearchLumenAsync("arrival", debounce: false, TestContext.Current.CancellationToken);
        await model.SetLumenSearchTypeAsync("Movie", TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        await model.LoadMoreSearchPeopleAsync(TestContext.Current.CancellationToken);
        var peoplePage = Assert.Single(context.Handler.Requests.Skip(requestStart));
        Assert.True(peoplePage.Is("/Persons"));
        Assert.Equal("48", peoplePage.Query["StartIndex"]);
        var mediaCards = model.Items.ToArray();
        var peopleCards = model.SearchPeople.ToArray();
        Assert.Equal(48, mediaCards.Length);
        Assert.Equal(73, peopleCards.Length);
        var viewport = new BrowseViewportState(610, AnchorId: mediaCards[10].Id,
            AnchorIndex: 10, FocusedItemId: peopleCards[60].Id);
        model.ViewportState = viewport;

        await model.ShowItemAsync(mediaCards[10], TestContext.Current.CancellationToken);
        Assert.Null(model.TotalItemsCount);
        Assert.Empty(model.SearchPeople);
        requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsSearch);
        Assert.Equal("Movie", model.BrowseSearchType);
        Assert.Equal(120, model.QueryCount);
        Assert.Equal(73, model.SearchPeopleTotal);
        AssertSameCards(mediaCards, model.Items);
        AssertSameCards(peopleCards, model.SearchPeople);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.SearchPeopleHasMore);

        await model.LoadMoreSearchPeopleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        var mediaPage = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("48", mediaPage.Query["StartIndex"]);
        Assert.Equal("Movie", mediaPage.Query["IncludeItemTypes"]);
        Assert.Equal(96, model.Items.Count);
        Assert.Equal(120, model.TotalItemsCount);
        Assert.Equal(73, model.SearchPeopleTotal);
    }

    [Fact]
    public async Task Back_from_a_person_restarts_only_unfinished_media_and_keeps_completed_people_and_the_viewport()
    {
        using var context = await CreateAsync();
        var model = context.Model;
        using var originalMedia = new RequestGate();
        using var restoredMedia = new RequestGate();
        context.Handler.RespondAsync = request =>
        {
            if (request.IsItems && request.Query.ContainsKey("SearchTerm")) return originalMedia.RespondAsync(request);
            if (request.Is("/Persons")) return Task.FromResult(Page([Person("person-a")]));
            if (request.Is("/Users/user-a/Items/person-a")) return Task.FromResult(Item(Person("person-a")));
            return Task.FromResult(context.DefaultResponse(request));
        };

        var searching = model.SearchLumenAsync("arrival", debounce: false, TestContext.Current.CancellationToken);
        var originalRequest = await originalMedia.WaitForRequestAsync();
        var person = Assert.Single(model.SearchPeople);
        Assert.Equal(1, model.SearchPeopleTotal);
        Assert.Empty(model.Items);
        Assert.Equal(PageLoadOutcome.Loading, model.LoadOutcome);
        var viewport = new BrowseViewportState(345, AnchorId: person.Id, FocusedItemId: person.Id);
        model.ViewportState = viewport;
        await model.ShowPersonAsync(person, TestContext.Current.CancellationToken);
        Assert.True(model.IsPerson);
        Assert.True(originalRequest.CancellationToken.IsCancellationRequested);

        context.Handler.RespondAsync = request => request.IsItems && request.Query.ContainsKey("SearchTerm")
            ? restoredMedia.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;
        var returning = model.GoBackAsync(TestContext.Current.CancellationToken);
        var restarted = await restoredMedia.WaitForRequestAsync();
        var restoredRevision = model.NavigationRevision;
        Assert.Equal("0", restarted.Query["StartIndex"]);
        Assert.Equal("arrival", restarted.Query["SearchTerm"]);
        Assert.True(model.IsSearch);
        Assert.Same(person, Assert.Single(model.SearchPeople));
        Assert.Equal(1, model.SearchPeopleTotal);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Null(model.TotalItemsCount);
        Assert.True(model.IsInitialLoading);
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), request => request.Is("/Persons"));

        originalMedia.Return(Page([Movie("obsolete-media")], 500));
        await searching;
        Assert.Empty(model.Items);
        Assert.True(model.IsBusy);
        restoredMedia.Return(Page([Movie("restored-media")], 42));
        await returning;

        Assert.Equal("restored-media", Assert.Single(model.Items).Id);
        Assert.Same(person, Assert.Single(model.SearchPeople));
        Assert.Equal(42, model.QueryCount);
        Assert.Equal(1, model.SearchPeopleTotal);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal(restoredRevision, model.NavigationRevision);
        Assert.True(model.CanLoadMore);
        Assert.False(model.HasError);
        Assert.False(model.HasPendingSearch);
    }

    [Fact]
    public async Task Clearing_the_session_prevents_late_search_media_and_people_from_repopulating_the_view()
    {
        using var context = await CreateAsync();
        var model = context.Model;
        using var media = new RequestGate();
        using var people = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems ? media.RespondAsync(request)
            : request.Is("/Persons") ? people.RespondAsync(request)
            : Task.FromResult(context.DefaultResponse(request));
        var searching = model.SearchLumenAsync("arrival", debounce: false, TestContext.Current.CancellationToken);
        var mediaRequest = await media.WaitForRequestAsync();
        var peopleRequest = await people.WaitForRequestAsync();

        model.ClearSession();
        media.Return(Page([Movie("departed-media")], 100));
        people.Return(Page([Person("departed-person")], 200));
        await searching;

        Assert.True(mediaRequest.CancellationToken.IsCancellationRequested);
        Assert.True(peopleRequest.CancellationToken.IsCancellationRequested);
        Assert.Empty(model.Items);
        Assert.Empty(model.SearchPeople);
        Assert.Null(model.TotalItemsCount);
        Assert.Null(model.SearchPeopleTotal);
        Assert.Empty(model.SearchPeopleError);
        Assert.False(model.IsBusy);
        Assert.False(model.SearchPeopleIsBusy);
        Assert.False(model.HasPendingSearch);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Per_card_mutations_update_current_details_and_every_retained_browse_snapshot(bool favorite)
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var homeCard = Assert.Single(model.HomeRows.SelectMany(row => row.Items), card => card.Id == "resume-a");
        context.Catalog = [homeCard.Item];
        await model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken);
        var wallCard = Assert.Single(model.Items);
        var endpoint = favorite ? "FavoriteItems" : "PlayedItems";
        var responses = new Queue<UserItemDataDto>([MutationData(favorite, true), MutationData(favorite, false)]);
        context.Handler.RespondAsync = request => Task.FromResult(request.Is($"/Users/user-a/{endpoint}/resume-a")
            ? UserData(responses.Dequeue()) : request.Is("/Users/user-a/Items/resume-a")
                ? Item(homeCard.Item) : context.DefaultResponse(request));
        await model.ShowItemAsync(wallCard, TestContext.Current.CancellationToken);
        var detailCard = model.Detail;
        Assert.NotSame(wallCard, detailCard);
        var requestStart = context.Handler.Requests.Length;

        await (favorite ? model.ToggleItemFavoriteAsync(wallCard, TestContext.Current.CancellationToken)
            : model.ToggleItemPlayedAsync(wallCard, TestContext.Current.CancellationToken));

        Assert.True(Assert.Single(context.Handler.Requests.Skip(requestStart)).Is($"/Users/user-a/{endpoint}/resume-a"));
        Assert.All(new[] { homeCard, wallCard, detailCard, model.PlayableDetail }, card =>
            Assert.True(favorite ? card.IsFavorite : card.IsPlayed));
        Assert.False(model.IsMutating);
        Assert.False(model.HasError);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(wallCard, Assert.Single(model.Items));
        await (favorite ? model.ToggleItemFavoriteAsync(wallCard, TestContext.Current.CancellationToken)
            : model.ToggleItemPlayedAsync(wallCard, TestContext.Current.CancellationToken));
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsHome);
        Assert.Same(homeCard, Assert.Single(model.HomeRows.SelectMany(row => row.Items), card => card.Id == "resume-a"));
        Assert.False(favorite ? homeCard.IsFavorite : homeCard.IsPlayed);
        Assert.False(favorite ? wallCard.IsFavorite : wallCard.IsPlayed);
        Assert.Empty(responses);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_late_filter_read_preserves_user_data_written_by_a_per_card_mutation(bool favorite)
    {
        using var context = await CreateAsync();
        var staleItem = Movie("shared") with { UserData = new UserItemDataDto { IsFavorite = false, Played = false } };
        context.Catalog = [staleItem];
        var model = context.Model;
        await model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken);
        var card = Assert.Single(model.Items);
        var endpoint = favorite ? "FavoriteItems" : "PlayedItems";
        using var filter = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems ? filter.RespondAsync(request)
            : request.Is($"/Users/user-a/{endpoint}/shared") ? Task.FromResult(UserData(MutationData(favorite, true)))
            : Task.FromResult(context.DefaultResponse(request));

        var replacing = model.SetLumenBrowseFiltersAsync("Drama", WatchStatusFilter.All,
            cancellationToken: TestContext.Current.CancellationToken);
        await filter.WaitForRequestAsync();
        await (favorite ? model.ToggleItemFavoriteAsync(card, TestContext.Current.CancellationToken)
            : model.ToggleItemPlayedAsync(card, TestContext.Current.CancellationToken));
        Assert.True(favorite ? card.IsFavorite : card.IsPlayed);
        filter.Return(Page([staleItem with { Name = "Refreshed title" }]));
        await replacing;

        Assert.Same(card, Assert.Single(model.Items));
        Assert.Equal("Refreshed title", card.Title);
        Assert.True(favorite ? card.IsFavorite : card.IsPlayed);
        Assert.Equal("Drama", model.BrowseGenre);
        Assert.False(model.IsMutating);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_per_card_mutations_do_not_change_user_data_and_release_the_mutation_state(bool favorite)
    {
        using var context = await CreateAsync();
        context.Catalog = [Movie("unchanged") with { UserData = new UserItemDataDto { IsFavorite = false, Played = false } }];
        var model = context.Model;
        await model.ShowMediaTypeAsync("Movie", TestContext.Current.CancellationToken);
        var card = Assert.Single(model.Items);
        var original = card.Item.UserData;
        var endpoint = favorite ? "FavoriteItems" : "PlayedItems";
        context.Handler.RespondAsync = request => Task.FromResult(request.Is($"/Users/user-a/{endpoint}/unchanged")
            ? Failure() : context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;

        await (favorite ? model.ToggleItemFavoriteAsync(card, TestContext.Current.CancellationToken)
            : model.ToggleItemPlayedAsync(card, TestContext.Current.CancellationToken));

        Assert.True(Assert.Single(context.Handler.Requests.Skip(requestStart)).Is($"/Users/user-a/{endpoint}/unchanged"));
        Assert.Same(original, card.Item.UserData);
        Assert.Same(card, Assert.Single(model.Items));
        Assert.False(card.IsFavorite);
        Assert.False(card.IsPlayed);
        Assert.False(model.IsMutating);
        Assert.True(model.HasError);
        Assert.Equal(1, model.QueryCount);
    }

    private static BaseItemDto[] SearchScopeCatalog() => GenericSearchTypes.Split(',')
        .Select(type => new BaseItemDto { Id = "match-" + type, Name = "Search result " + type, Type = type }).ToArray();

    private static HttpResponseMessage SearchScopeResponse(LibraryTestContext context, ObservedRequest request)
    {
        if (request.Is("/Persons")) return Page([Person("person-a")]);
        if (!request.IsItems) return context.DefaultResponse(request);
        var types = request.Query["IncludeItemTypes"].Split(',');
        var matches = context.Catalog.Where(item => types.Contains(item.Type!, StringComparer.Ordinal)).ToArray();
        var offset = int.Parse(request.Query.GetValueOrDefault("StartIndex", "0"));
        var limit = int.Parse(request.Query.GetValueOrDefault("Limit", "48"));
        return Page(matches.Skip(offset).Take(limit).ToArray(), matches.Length);
    }

    private static UserItemDataDto MutationData(bool favorite, bool value) =>
        new() { IsFavorite = favorite && value, Played = !favorite && value };

    private static BaseItemDto Person(string id) => new() { Id = id, Name = id, Type = "Person" };

    private static HttpResponseMessage ReportedPage(BaseItemDto[] items, int? total) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new QueryResult<BaseItemDto>
        {
            Items = items, TotalRecordCount = total
        }, EmbyJsonContext.Default.QueryResultBaseItemDto), Encoding.UTF8, "application/json")
    };
}
