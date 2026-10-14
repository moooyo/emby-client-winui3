using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class PersonDetailsStateTests
{
    [Fact]
    public async Task Revisiting_a_person_keeps_the_loaded_window_and_only_explicit_pagination_advances_it()
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(49);
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/person-a")
            ? Item(PersonItem()) : PersonResponse(context, request));
        using var person = CreatePerson(context, TestContext.Current.CancellationToken);

        var initial = person.EnsureLoadedAsync();
        Assert.Same(initial, person.EnsureLoadedAsync());
        await initial;
        var cards = person.Works.ToArray();
        var requestCount = context.Handler.Requests.Length;
        person.ScrollOffset = 640;
        person.FocusedWorkId = cards[8].Id;
        person.IsBiographyExpanded = true;

        await person.LoadAsync();
        Assert.Equal(requestCount, context.Handler.Requests.Length);
        Assert.Equal(24, person.WorkCount);
        Assert.Equal(49, person.TotalWorkCount);
        Assert.Equal(49, person.MovieWorkCount);
        Assert.Equal(0, person.SeriesWorkCount);
        Assert.Equal(49, person.FilteredWorkCount);
        AssertSameCards(cards, person.Works);
        Assert.Equal(640d, person.ScrollOffset);
        Assert.Equal(cards[8].Id, person.FocusedWorkId);
        Assert.True(person.IsBiographyExpanded);

        await person.LoadMoreAsync();
        await person.LoadMoreAsync();
        Assert.Equal(49, person.WorkCount);
        Assert.False(person.HasMore);
        AssertSameCards(cards, person.Works.Take(24).ToArray());
        var pages = context.Handler.Requests.Where(IsPersonWorksRequest).ToArray();
        Assert.Equal(new[] { "0", "24", "48" }, pages.Select(request => request.Query["StartIndex"]));
        Assert.All(pages, request =>
        {
            Assert.Equal("24", request.Query["Limit"]);
            Assert.Equal("person-a", request.Query["PersonIds"]);
            Assert.Equal("Movie,Series", request.Query["IncludeItemTypes"]);
        });
        var counts = context.Handler.Requests.Where(IsPersonCountRequest).ToArray();
        Assert.NotEmpty(counts);
        Assert.All(counts, request =>
        {
            Assert.Equal("0", request.Query["StartIndex"]);
            Assert.Equal("false", request.Query["EnableUserData"]);
        });
    }

    [Fact]
    public async Task A_failed_initial_page_can_be_retried_without_advancing_the_server_offset()
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(30);
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/person-a")
            ? Item(PersonItem()) : IsPersonWorksRequest(request) ? Failure() : PersonResponse(context, request));
        using var person = CreatePerson(context, TestContext.Current.CancellationToken);
        await person.EnsureLoadedAsync();
        Assert.NotEmpty(person.WorksError);
        Assert.Empty(person.Works);

        context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));
        await person.LoadMoreAsync();

        Assert.Empty(person.WorksError);
        Assert.Equal(24, person.WorkCount);
        Assert.True(person.HasMore);
        var pages = context.Handler.Requests.Where(IsPersonWorksRequest).ToArray();
        Assert.Equal(2, pages.Length);
        Assert.All(pages, request => Assert.Equal("0", request.Query["StartIndex"]));
    }

    [Fact]
    public async Task A_cancelled_person_does_not_publish_late_biography_or_library_results()
    {
        using var context = await CreateAsync();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var biography = new RequestGate();
        using var works = new RequestGate();
        using var totalCount = new RequestGate();
        using var movieCount = new RequestGate();
        using var seriesCount = new RequestGate();
        var failures = new List<Exception>();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/person-a")
            ? biography.RespondAsync(request) : IsPersonCountRequest(request)
            ? CountGate(request, totalCount, movieCount, seriesCount).RespondAsync(request)
            : IsPersonWorksRequest(request) ? works.RespondAsync(request)
            : Task.FromResult(context.DefaultResponse(request));
        using var person = CreatePerson(context, session.Token, failures.Add);
        var loading = person.EnsureLoadedAsync();
        await biography.WaitForRequestAsync();
        await works.WaitForRequestAsync();
        await Task.WhenAll(totalCount.WaitForRequestAsync(), movieCount.WaitForRequestAsync(), seriesCount.WaitForRequestAsync());

        session.Cancel();
        biography.Return(Item(PersonItem()));
        works.Return(Page(Movies(24), 48));
        totalCount.Return(Page(Movies(1), 48));
        movieCount.Return(Page(Movies(1), 40));
        seriesCount.Return(Page([], 8));
        await loading;

        Assert.Empty(person.Biography);
        Assert.Empty(person.Works);
        Assert.Empty(person.BiographyError);
        Assert.Empty(person.WorksError);
        Assert.Null(person.TotalWorkCount);
        Assert.Null(person.MovieWorkCount);
        Assert.Null(person.SeriesWorkCount);
        Assert.Null(person.FilteredWorkCount);
        Assert.Empty(failures);
        Assert.True(person.LifetimeToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Changing_work_type_keeps_the_new_selection_when_old_pages_and_counts_finish_late()
    {
        using var context = await CreateAsync();
        using var oldWorks = new RequestGate();
        using var totalCount = new RequestGate();
        using var movieCount = new RequestGate();
        using var seriesCount = new RequestGate();
        var current = new BaseItemDto { Id = "series-current", Name = "Current series", Type = "Series", IsFolder = true };
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/person-a")
            ? Task.FromResult(Item(PersonItem())) : IsPersonCountRequest(request)
            ? CountGate(request, totalCount, movieCount, seriesCount).RespondAsync(request)
            : IsPersonWorksRequest(request) && request.Query["IncludeItemTypes"] == "Movie,Series"
            ? oldWorks.RespondAsync(request) : IsPersonWorksRequest(request)
            ? Task.FromResult(Page([current], 2)) : Task.FromResult(context.DefaultResponse(request));
        using var person = CreatePerson(context, TestContext.Current.CancellationToken);

        var initial = person.EnsureLoadedAsync();
        var oldRequest = await oldWorks.WaitForRequestAsync();
        await Task.WhenAll(totalCount.WaitForRequestAsync(), movieCount.WaitForRequestAsync(), seriesCount.WaitForRequestAsync());
        var changing = person.SetWorksOptionsAsync("Series", "SortName");
        totalCount.Return(Page(Movies(1), 99));
        movieCount.Return(Page(Movies(1), 97));
        seriesCount.Return(Page([current], 42));
        await changing;
        var card = Assert.Single(person.Works);
        person.ScrollOffset = 320;
        person.FocusedWorkId = card.Id;

        oldWorks.Return(Page(Movies(24), 120));
        await initial;

        Assert.True(oldRequest.CancellationToken.IsCancellationRequested);
        Assert.Equal("Series", person.SelectedWorkType);
        Assert.Equal("SortName", person.SelectedWorkSort);
        Assert.Same(card, Assert.Single(person.Works));
        Assert.Equal(current.Id, card.Id);
        Assert.Equal(1, person.WorkCount);
        Assert.Equal(2, person.FilteredWorkCount);
        Assert.Equal(2, person.SeriesWorkCount);
        Assert.Equal(99, person.TotalWorkCount);
        Assert.Equal(97, person.MovieWorkCount);
        Assert.Equal(320d, person.ScrollOffset);
        Assert.Equal(card.Id, person.FocusedWorkId);
        Assert.Empty(person.WorksError);
        var seriesRequest = Assert.Single(context.Handler.Requests.Where(request => IsPersonWorksRequest(request)
            && request.Query["IncludeItemTypes"] == "Series"));
        Assert.Equal("0", seriesRequest.Query["StartIndex"]);
        Assert.Equal("SortName", seriesRequest.Query["SortBy"]);
        Assert.Equal("Ascending", seriesRequest.Query["SortOrder"]);
    }

    private static bool IsPersonItemsRequest(ObservedRequest request) => request.IsItems
        && request.Query.GetValueOrDefault("PersonIds") == "person-a";

    private static bool IsPersonCountRequest(ObservedRequest request) => IsPersonItemsRequest(request)
        && request.Query.GetValueOrDefault("Limit") == "1"
        && request.Query.GetValueOrDefault("EnableImages") == "false";

    private static bool IsPersonWorksRequest(ObservedRequest request) => IsPersonItemsRequest(request) && !IsPersonCountRequest(request);

    private static HttpResponseMessage PersonResponse(LibraryTestContext context, ObservedRequest request)
    {
        if (!IsPersonCountRequest(request)) return context.DefaultResponse(request);
        var types = request.Query["IncludeItemTypes"].Split(',');
        var items = context.Catalog.Where(item => item.Type is { } type && types.Contains(type, StringComparer.Ordinal)).ToArray();
        return Page(items.Take(1).ToArray(), items.Length);
    }

    private static RequestGate CountGate(ObservedRequest request, RequestGate total, RequestGate movies, RequestGate series) =>
        request.Query["IncludeItemTypes"] switch { "Movie" => movies, "Series" => series, _ => total };

    private static PersonDetailsViewModel CreatePerson(LibraryTestContext context, CancellationToken token, Action<Exception>? onFailure = null)
    {
        var card = Assert.Single(PersonCardViewModel.FromItem(new BaseItemDto
        {
            People = [new PersonInfo { Id = "person-a", Name = "Person A", Type = "Actor", Role = "Lead" }]
        }));
        return new(card, "Source movie", context.Api, new(), "server-a", "user-a", token, onFailure ?? (_ => { }));
    }

    private static BaseItemDto PersonItem() => new()
    {
        Id = "person-a", Name = "Person A", Type = "Person", Overview = "A biography supplied by the server."
    };
}
