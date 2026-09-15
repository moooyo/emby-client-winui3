using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryHistoryStateTests
{
    [Fact]
    public async Task Back_restores_the_loaded_window_filter_and_viewport_without_refetching()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(120));
        var model = context.Model;
        await model.SetBrowseOptionsAsync("ProductionYear", true, WatchStatusFilter.Unplayed,
            TestContext.Current.CancellationToken);
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        var title = model.Title;
        var subtitle = model.Subtitle;
        var viewport = new BrowseViewportState(1735, 0, 0, cards[62].Id, 62, cards[67].Id);
        model.ViewportState = viewport;
        var captures = 0;
        var restores = 0;
        model.BrowseStateCapturing += (_, _) => captures++;
        model.BrowseStateRestored += (_, _) => restores++;
        context.Handler.RespondAsync = request => request.Is($"/Users/user-a/Items/{cards[67].Id}")
            ? Task.FromResult(Item(cards[67].Item)) : Task.FromResult(context.DefaultResponse(request));

        await model.ShowItemAsync(cards[67], TestContext.Current.CancellationToken);
        Assert.Equal(1, captures);
        Assert.Equal(new BrowseViewportState(), model.ViewportState);
        model.ViewportState = new(0, 840, 120, OverviewExpanded: true);
        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.Equal(1, restores);
        AssertSameCards(cards, model.Items);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal(title, model.Title);
        Assert.Equal(subtitle, model.Subtitle);
        Assert.Equal("library-a", model.SelectedLibraryId);
        Assert.Equal("ProductionYear", model.BrowseSortKey);
        Assert.True(model.BrowseSortDescending);
        Assert.Equal(WatchStatusFilter.Unplayed, model.BrowseWatchFilter);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.True(model.CanLoadMore);
        Assert.False(model.IsBusy);

        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var request = Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.IsItems);
        Assert.Equal("96", request.Query["StartIndex"]);
        Assert.Equal("ProductionYear,SortName", request.Query["SortBy"]);
        Assert.Equal("Descending", request.Query["SortOrder"]);
        Assert.Equal("false", request.Query["IsPlayed"]);
        Assert.Equal(120, model.Items.Count);
        Assert.False(model.HasMore);
    }

    [Fact]
    public async Task Back_from_a_search_result_restores_search_then_its_original_browse_state()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(72));
        var model = context.Model;
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var origin = model.Items.ToArray();
        var originViewport = new BrowseViewportState(1310, AnchorId: origin[50].Id, AnchorIndex: 50,
            FocusedItemId: origin[53].Id);
        model.ViewportState = originViewport;
        var result = Movie("search-result", "Arrival");
        context.Handler.RespondAsync = request => request.IsItems && request.Query.ContainsKey("SearchTerm")
            ? Task.FromResult(Page([result])) : request.Is("/Users/user-a/Items/search-result")
                ? Task.FromResult(Item(result)) : Task.FromResult(context.DefaultResponse(request));

        await model.SearchAsync(" Arrival ", debounce: false, TestContext.Current.CancellationToken);
        var searchCards = model.Items.ToArray();
        var searchViewport = new BrowseViewportState(260, AnchorId: result.Id, FocusedItemId: result.Id);
        model.ViewportState = searchViewport;
        await model.ShowItemAsync(Assert.Single(searchCards), TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsSearch);
        Assert.Equal("Arrival", model.SearchText);
        AssertSameCards(searchCards, model.Items);
        Assert.Equal(searchViewport, model.ViewportState);
        Assert.True(model.CanGoBack);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.False(model.IsSearch);
        Assert.Equal("library-a", model.SelectedLibraryId);
        AssertSameCards(origin, model.Items);
        Assert.Equal(originViewport, model.ViewportState);
        Assert.Equal(requestStart, context.Handler.Requests.Length);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.False(model.CanGoBack);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task Back_from_a_person_work_restores_the_same_profile_and_loaded_works()
    {
        using var context = await CreateAsync();
        var source = Movie("source", "Source movie") with
        {
            People = [new PersonInfo { Id = "person-a", Name = "Test Actor", Type = "Actor" }]
        };
        context.Catalog = Movies(40, "Work");
        context.Handler.RespondAsync = request =>
        {
            if (request.Is("/Users/user-a/Items/source")) return Task.FromResult(Item(source));
            if (request.Is("/Users/user-a/Items/person-a"))
                return Task.FromResult(Item(new BaseItemDto
                {
                    Id = "person-a", Name = "Test Actor", Type = "Person", Overview = "A retained biography."
                }));
            var work = context.Catalog.FirstOrDefault(item => request.Is($"/Users/user-a/Items/{item.Id}"));
            return Task.FromResult(work is null ? context.DefaultResponse(request) : Item(work));
        };
        var model = context.Model;
        await model.ShowItemAsync(new(source), TestContext.Current.CancellationToken);
        var sourceDetail = model.Detail;
        await model.ShowPersonAsync(Assert.Single(PersonCardViewModel.FromItem(source)),
            TestContext.Current.CancellationToken);
        var person = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        Assert.Equal(24, person.Works.Count);
        await person.LoadMoreAsync();
        var works = person.Works.ToArray();
        Assert.Equal(40, works.Length);
        Assert.False(person.HasMore);
        var viewport = new BrowseViewportState(1120, AnchorId: works[30].Id, AnchorIndex: 30,
            FocusedItemId: works[34].Id, OverviewExpanded: true);
        model.ViewportState = viewport;
        person.ScrollOffset = 1120;
        person.FocusedWorkId = works[34].Id;
        person.IsBiographyExpanded = true;

        await model.ShowItemAsync(works[34], TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsPerson);
        Assert.Equal(Visibility.Visible, model.PersonVisibility);
        Assert.Same(person, model.ActivePerson);
        AssertSameCards(works, person.Works);
        Assert.Equal("A retained biography.", person.Biography);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal(1120, person.ScrollOffset);
        Assert.Equal(works[34].Id, person.FocusedWorkId);
        Assert.True(person.IsBiographyExpanded);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(person.IsLoadingBiography);
        Assert.False(person.IsLoadingWorks);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.False(model.IsPerson);
        Assert.True(model.HasDetails);
        Assert.Same(sourceDetail, model.Detail);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task A_departed_page_response_cannot_append_to_a_restored_snapshot()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(120));
        var model = context.Model;
        var original = model.Items.ToArray();
        using var more = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? more.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var pending = model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var pendingRequest = await more.WaitForRequestAsync();
        context.Handler.RespondAsync = request => request.Is($"/Users/user-a/Items/{original[10].Id}")
            ? Task.FromResult(Item(original[10].Item)) : Task.FromResult(context.DefaultResponse(request));

        await model.ShowItemAsync(original[10], TestContext.Current.CancellationToken);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(pendingRequest.CancellationToken.IsCancellationRequested);
        AssertSameCards(original, model.Items);
        Assert.False(model.IsBusy);
        more.Return(Page(context.Catalog.Skip(48).Take(48).ToArray(), 120));
        Assert.Equal(PageLoadMoreOutcome.Canceled, await pending);
        AssertSameCards(original, model.Items);

        var requestStart = context.Handler.Requests.Length;
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal("48", Assert.Single(context.Handler.Requests.Skip(requestStart),
            request => request.IsItems).Query["StartIndex"]);
        Assert.Equal(96, model.Items.Count);
    }
}
