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
            ? Item(PersonItem()) : context.DefaultResponse(request));
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
        AssertSameCards(cards, person.Works);
        Assert.Equal(640d, person.ScrollOffset);
        Assert.Equal(cards[8].Id, person.FocusedWorkId);
        Assert.True(person.IsBiographyExpanded);

        await person.LoadMoreAsync();
        await person.LoadMoreAsync();
        Assert.Equal(49, person.WorkCount);
        Assert.False(person.HasMore);
        AssertSameCards(cards, person.Works.Take(24).ToArray());
        var pages = context.Handler.Requests.Where(request => request.IsItems && request.Query.ContainsKey("PersonIds")).ToArray();
        Assert.Equal(new[] { "0", "24", "48" }, pages.Select(request => request.Query["StartIndex"]));
        Assert.All(pages, request =>
        {
            Assert.Equal("24", request.Query["Limit"]);
            Assert.Equal("person-a", request.Query["PersonIds"]);
            Assert.Equal("Movie,Series,Video,MusicVideo", request.Query["IncludeItemTypes"]);
        });
    }

    [Fact]
    public async Task A_failed_initial_page_can_be_retried_without_advancing_the_server_offset()
    {
        using var context = await CreateAsync();
        context.Catalog = Movies(30);
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/person-a")
            ? Item(PersonItem()) : request.IsItems ? Failure() : context.DefaultResponse(request));
        using var person = CreatePerson(context, TestContext.Current.CancellationToken);
        await person.EnsureLoadedAsync();
        Assert.NotEmpty(person.WorksError);
        Assert.Empty(person.Works);

        context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));
        await person.LoadMoreAsync();

        Assert.Empty(person.WorksError);
        Assert.Equal(24, person.WorkCount);
        Assert.True(person.HasMore);
        var pages = context.Handler.Requests.Where(request => request.IsItems && request.Query.ContainsKey("PersonIds"));
        Assert.All(pages, request => Assert.Equal("0", request.Query["StartIndex"]));
    }

    [Fact]
    public async Task A_cancelled_person_does_not_publish_late_biography_or_library_results()
    {
        using var context = await CreateAsync();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var biography = new RequestGate();
        using var works = new RequestGate();
        var failures = new List<Exception>();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/person-a")
            ? biography.RespondAsync(request) : request.IsItems ? works.RespondAsync(request)
            : Task.FromResult(context.DefaultResponse(request));
        using var person = CreatePerson(context, session.Token, failures.Add);
        var loading = person.EnsureLoadedAsync();
        await biography.WaitForRequestAsync();
        await works.WaitForRequestAsync();

        session.Cancel();
        biography.Return(Item(PersonItem()));
        works.Return(Page(Movies(24), 48));
        await loading;

        Assert.Empty(person.Biography);
        Assert.Empty(person.Works);
        Assert.Empty(person.BiographyError);
        Assert.Empty(person.WorksError);
        Assert.Empty(failures);
        Assert.True(person.LifetimeToken.IsCancellationRequested);
    }

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
