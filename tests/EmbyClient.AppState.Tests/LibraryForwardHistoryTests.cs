using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryForwardHistoryTests
{
    [Fact]
    public async Task Forward_reloads_an_interrupted_detail_without_accepting_its_departed_response()
    {
        using var context = await CreateAsync();
        var movie = Movie("pending-movie", "Pending movie");
        await context.OpenLibraryAsync([movie]);
        var model = context.Model;
        var cards = model.Items.ToArray();
        using var opening = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/pending-movie")
            ? opening.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var pending = model.ShowItemAsync(cards[0], TestContext.Current.CancellationToken);
        var departed = await opening.WaitForRequestAsync();
        var viewport = new BrowseViewportState(DetailPageIndex: 2, DetailOffset: 760);
        model.ViewportState = viewport;

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(departed.CancellationToken.IsCancellationRequested);
        AssertSameCards(cards, model.Items);
        Assert.True(model.CanGoForward);
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/pending-movie")
            ? Item(movie with { Name = "Loaded movie" }) : context.DefaultResponse(request));

        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.True(model.HasDetails);
        Assert.Equal("Loaded movie", model.Detail.Title);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.Equal(viewport, model.ViewportState);
        Assert.True(model.CanGoBack);
        Assert.False(model.CanGoForward);
        opening.Return(Item(movie with { Name = "Stale movie" }));
        await pending;
        Assert.Equal("Loaded movie", model.Detail.Title);

        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        AssertSameCards(cards, model.Items);
        Assert.False(model.CanGoBack);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task Home_and_detail_page_indices_survive_repeated_back_and_forward_navigation()
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var rows = model.HomeRows.ToArray();
        var movie = Assert.Single(rows.SelectMany(row => row.Items), item => item.Id == "resume-a");
        var homeViewport = new BrowseViewportState(VerticalOffset: 540, FocusedItemId: movie.Id,
            HomePageIndex: 2);
        model.ViewportState = homeViewport;
        RespondWithDetails(context, movie.Item);

        await model.ShowItemAsync(movie, TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var detailViewport = new BrowseViewportState(DetailOffset: 810, ChildrenOffset: 240,
            OverviewExpanded: true, DetailPageIndex: 1);
        model.ViewportState = detailViewport;
        var requestStart = context.Handler.Requests.Length;

        for (var repeat = 0; repeat < 2; repeat++)
        {
            await model.GoBackAsync(TestContext.Current.CancellationToken);
            Assert.True(model.IsHome);
            Assert.Equal(homeViewport, model.ViewportState);
            Assert.Null(model.NavigationLibraryId);
            Assert.Equal(rows.Length, model.HomeRows.Count);
            for (var index = 0; index < rows.Length; index++) Assert.Same(rows[index], model.HomeRows[index]);
            Assert.False(model.CanGoBack);
            Assert.True(model.CanGoForward);

            await model.GoForwardAsync(TestContext.Current.CancellationToken);
            Assert.True(model.HasDetails);
            Assert.Same(detail, model.Detail);
            Assert.Equal(detailViewport, model.ViewportState);
            Assert.True(model.CanGoBack);
            Assert.False(model.CanGoForward);
        }

        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_directions_retain_only_twenty_four_entries_including_a_search_origin(bool enterSearch)
    {
        using var context = await CreateAsync();
        var movies = Movies(30);
        await context.OpenLibraryAsync(movies);
        var model = context.Model;
        RespondWithDetails(context, movies);
        foreach (var movie in movies) await model.ShowItemAsync(new(movie), TestContext.Current.CancellationToken);
        if (enterSearch) await model.SearchAsync("last query", debounce: false, TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        var oldestRetained = enterSearch ? 6 : 5;
        var firstBack = enterSearch ? 29 : 28;

        for (var index = firstBack; index >= oldestRetained; index--)
        {
            Assert.True(model.CanGoBack);
            await model.GoBackAsync(TestContext.Current.CancellationToken);
            Assert.True(model.HasDetails);
            Assert.Equal(movies[index].Id, model.Detail.Id);
        }

        Assert.False(model.CanGoBack);
        Assert.True(model.CanGoForward);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(movies[oldestRetained].Id, model.Detail.Id);

        for (var index = oldestRetained + 1; index < movies.Length; index++)
        {
            Assert.True(model.CanGoForward);
            await model.GoForwardAsync(TestContext.Current.CancellationToken);
            Assert.True(model.HasDetails);
            Assert.Equal(movies[index].Id, model.Detail.Id);
        }

        if (enterSearch)
        {
            Assert.True(model.CanGoForward);
            await model.GoForwardAsync(TestContext.Current.CancellationToken);
            Assert.True(model.IsSearch);
            Assert.Equal("last query", model.SearchText);
        }

        Assert.False(model.CanGoForward);
        var revision = model.NavigationRevision;
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("search-page")]
    [InlineData("search-query")]
    [InlineData("replace-search")]
    public async Task A_new_branch_after_back_discards_forward_history(string destination)
    {
        using var context = await CreateAsync();
        var movies = Movies(3);
        await context.OpenLibraryAsync(movies);
        var model = context.Model;
        RespondWithDetails(context, movies);
        await model.ShowItemAsync(new(movies[0]), TestContext.Current.CancellationToken);
        var origin = model.Detail;
        if (destination == "replace-search")
            await model.SearchAsync("original", debounce: false, TestContext.Current.CancellationToken);
        await model.ShowItemAsync(new(movies[1]), TestContext.Current.CancellationToken);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanGoForward);

        switch (destination)
        {
            case "detail":
                await model.ShowItemAsync(new(movies[2]), TestContext.Current.CancellationToken);
                Assert.Equal(movies[2].Id, model.Detail.Id);
                break;
            case "search-page":
                await model.ShowSearchAsync(TestContext.Current.CancellationToken);
                Assert.True(model.IsSearch);
                Assert.Empty(model.SearchText);
                break;
            default:
                await model.SearchAsync("replacement", debounce: false, TestContext.Current.CancellationToken);
                Assert.True(model.IsSearch);
                Assert.Equal("replacement", model.SearchText);
                break;
        }

        Assert.False(model.CanGoForward);
        var requestStart = context.Handler.Requests.Length;
        var revision = model.NavigationRevision;
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Equal(revision, model.NavigationRevision);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.HasDetails);
        Assert.Same(origin, model.Detail);
        Assert.Equal("library-a", model.NavigationLibraryId);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal("library-a", model.SelectedLibraryId);
        Assert.False(model.CanGoBack);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Theory]
    [InlineData("home")]
    [InlineData("library")]
    [InlineData("favorites")]
    [InlineData("Movie")]
    [InlineData("Series")]
    public async Task Switching_primary_tabs_clears_both_directions_and_search_origins(string destination)
    {
        using var context = await CreateAsync();
        var movies = Movies(2);
        await context.OpenLibraryAsync(movies);
        var model = context.Model;
        RespondWithDetails(context, movies);
        await model.ShowSearchAsync(TestContext.Current.CancellationToken);
        await model.ShowItemAsync(new(movies[0]), TestContext.Current.CancellationToken);
        await model.ShowItemAsync(new(movies[1]), TestContext.Current.CancellationToken);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.CanGoBack);
        Assert.True(model.CanGoForward);

        switch (destination)
        {
            case "home":
                await model.ShowHomeAsync(TestContext.Current.CancellationToken);
                Assert.True(model.IsHome);
                break;
            case "library":
                await model.ShowLibraryAsync(Assert.Single(model.Libraries), TestContext.Current.CancellationToken);
                Assert.Equal("library-a", model.SelectedLibraryId);
                break;
            case "favorites":
                await model.ShowFavoritesAsync(TestContext.Current.CancellationToken);
                Assert.True(model.IsFavorites);
                break;
            default:
                await model.ShowMediaTypeAsync(destination, TestContext.Current.CancellationToken);
                Assert.Equal(destination, model.BrowseMediaType);
                break;
        }

        Assert.False(model.CanGoBack);
        Assert.False(model.CanGoForward);
        Assert.Equal(destination is "library" or "Movie" ? "library-a" : null, model.NavigationLibraryId);
        var requestStart = context.Handler.Requests.Length;
        var revision = model.NavigationRevision;
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opening_a_shelf_or_home_section_preserves_its_parent_for_both_directions(bool libraryShelf)
    {
        using var context = await CreateAsync(populatedHome: true);
        context.Catalog = Movies(2);
        var model = context.Model;
        var parentViewport = new BrowseViewportState(HomePageIndex: 2);
        model.ViewportState = parentViewport;

        if (libraryShelf)
            await model.ShowShelfAsync(new MediaShelfViewModel("Recently added in Movies", false,
                HomeSection.RecentlyAdded, "library-a"), TestContext.Current.CancellationToken);
        else
            await model.ShowHomeSectionAsync(HomeSection.RecentlyAdded, TestContext.Current.CancellationToken);
        var cards = model.Items.ToArray();
        var childViewport = new BrowseViewportState(620, AnchorId: cards[1].Id, FocusedItemId: cards[1].Id);
        model.ViewportState = childViewport;
        var requestStart = context.Handler.Requests.Length;
        Assert.True(model.CanGoBack);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.Equal(parentViewport, model.ViewportState);
        Assert.Null(model.NavigationLibraryId);
        Assert.False(model.CanGoBack);
        Assert.True(model.CanGoForward);

        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.False(model.IsHome);
        AssertSameCards(cards, model.Items);
        Assert.Equal(childViewport, model.ViewportState);
        Assert.Equal(libraryShelf ? "library-a" : null, model.NavigationLibraryId);
        Assert.True(model.CanGoBack);
        Assert.False(model.CanGoForward);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task The_source_library_survives_detail_person_and_work_navigation_until_a_primary_tab_changes()
    {
        using var context = await CreateAsync();
        context.Views = [.. context.Views, new BaseItemDto
        {
            Id = "library-b", Name = "Documentaries", Type = "CollectionFolder", IsFolder = true
        }];
        var model = context.Model;
        await model.SetSessionAsync(context.Api, "server-a", new UserDto { Id = "user-a" },
            TestContext.Current.CancellationToken);
        var source = Movie("source", "Source movie") with
        {
            People = [new PersonInfo { Id = "person-a", Name = "Test Actor", Type = "Actor" }]
        };
        var work = Movie("work", "Another movie");
        var personItem = new BaseItemDto { Id = "person-a", Name = "Test Actor", Type = "Person" };
        context.Catalog = [source, work];
        RespondWithDetails(context, source, work, personItem);
        var changes = new List<string?>();
        model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        await model.ShowLibraryAsync(Assert.Single(model.Libraries, item => item.Id == "library-a"),
            TestContext.Current.CancellationToken);
        Assert.Equal("library-a", model.NavigationLibraryId);
        Assert.Contains(nameof(LibraryViewModel.NavigationLibraryId), changes);
        var libraryCards = model.Items.ToArray();
        await model.ShowItemAsync(Assert.Single(libraryCards, item => item.Id == source.Id),
            TestContext.Current.CancellationToken);
        var detail = model.Detail;
        Assert.Equal("library-a", model.NavigationLibraryId);
        await model.ShowPersonAsync(Assert.Single(PersonCardViewModel.FromItem(source)),
            TestContext.Current.CancellationToken);
        var person = Assert.IsType<PersonDetailsViewModel>(model.ActivePerson);
        Assert.Equal("library-a", model.NavigationLibraryId);
        var workCard = Assert.Single(person.Works, item => item.Id == work.Id);
        await model.ShowItemAsync(workCard, TestContext.Current.CancellationToken);
        var workDetail = model.Detail;
        Assert.Equal("library-a", model.NavigationLibraryId);
        var requestStart = context.Handler.Requests.Length;

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(person, model.ActivePerson);
        Assert.Equal("library-a", model.NavigationLibraryId);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(detail, model.Detail);
        Assert.Equal("library-a", model.NavigationLibraryId);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal("library-a", model.SelectedLibraryId);
        AssertSameCards(libraryCards, model.Items);

        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(detail, model.Detail);
        Assert.Equal("library-a", model.NavigationLibraryId);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(person, model.ActivePerson);
        Assert.Equal("library-a", model.NavigationLibraryId);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(workDetail, model.Detail);
        Assert.Equal("library-a", model.NavigationLibraryId);
        Assert.Equal(requestStart, context.Handler.Requests.Length);

        changes.Clear();
        await model.ShowLibraryAsync(Assert.Single(model.Libraries, item => item.Id == "library-b"),
            TestContext.Current.CancellationToken);
        Assert.Equal("library-b", model.NavigationLibraryId);
        Assert.Contains(nameof(LibraryViewModel.NavigationLibraryId), changes);
        Assert.False(model.CanGoBack);
        Assert.False(model.CanGoForward);

        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.NavigationLibraryId);
        await model.ShowItemAsync(workCard, TestContext.Current.CancellationToken);
        Assert.Null(model.NavigationLibraryId);
    }

    [Fact]
    public async Task Hiding_continue_watching_from_home_prunes_the_forward_resume_page_without_removing_its_details()
    {
        using var context = await CreateAsync();
        var position = TimeSpan.FromMinutes(10).Ticks;
        var target = Movie("resume-target", "Resume candidate") with
        {
            RunTimeTicks = TimeSpan.FromHours(1).Ticks,
            UserData = new UserItemDataDto { PlaybackPositionTicks = position, Played = false, IsFavorite = false }
        };
        var survivor = Movie("resume-survivor");
        context.Resume = [target, survivor];
        context.Latest = [target];
        context.Handler.RespondAsync = request => Task.FromResult(request.Is($"/Users/user-a/Items/{target.Id}/HideFromResume")
            ? UserData(new UserItemDataDto { ItemId = target.Id, PlaybackPositionTicks = 0, Played = true, IsFavorite = true })
            : request.Is($"/Users/user-a/Items/{target.Id}") ? Item(target) : context.DefaultResponse(request));
        var model = context.Model;
        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        var homeRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var homeCard = Assert.Single(homeRow.Items, item => item.Id == target.Id);
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var resumeCard = Assert.Single(model.Items, item => item.Id == target.Id);
        var survivorCard = Assert.Single(model.Items, item => item.Id == survivor.Id);
        var resumeViewport = new BrowseViewportState(420, AnchorId: survivor.Id, FocusedItemId: survivor.Id);
        model.ViewportState = resumeViewport;
        await model.ShowItemAsync(resumeCard, TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var unchangedData = new[] { homeCard, resumeCard, detail }
            .Select(card => (Card: card, Data: Assert.IsType<UserItemDataDto>(card.Item.UserData))).ToArray();
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.True(model.CanGoForward);
        var requestStart = context.Handler.Requests.Length;

        await model.RemoveFromContinueWatchingAsync(homeCard, TestContext.Current.CancellationToken);

        var request = Assert.Single(context.Handler.Requests.Skip(requestStart));
        Assert.True(request.Is($"/Users/user-a/Items/{target.Id}/HideFromResume"));
        Assert.Equal("true", request.Query["Hide"]);
        Assert.Equal(survivor.Id, Assert.Single(homeRow.Items).Id);
        var latestRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        Assert.Equal(target.Id, Assert.Single(latestRow.Items).Id);
        requestStart = context.Handler.Requests.Length;

        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHomeSection);
        Assert.Equal(HomeSection.ContinueWatching, model.SelectedHomeSection);
        Assert.Same(survivorCard, Assert.Single(model.Items));
        Assert.Equal(1, model.TotalItemsCount);
        Assert.False(model.HasMore);
        Assert.Equal(resumeViewport, model.ViewportState);
        Assert.True(model.CanGoForward);

        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(detail, model.Detail);
        Assert.Equal(target.Id, model.Detail.Id);
        Assert.False(model.CanGoForward);
        foreach (var state in unchangedData)
        {
            Assert.Same(state.Data, state.Card.Item.UserData);
            Assert.Equal(position, state.Card.ResumeTicks);
            Assert.False(state.Card.IsPlayed);
            Assert.False(state.Card.IsFavorite);
        }

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(survivorCard, Assert.Single(model.Items));
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.HasError);
    }

    private static void RespondWithDetails(LibraryTestContext context, params BaseItemDto[] items)
    {
        context.Handler.RespondAsync = request =>
        {
            var item = items.FirstOrDefault(item => request.Is($"/Users/user-a/Items/{item.Id}"));
            return Task.FromResult(item is null ? context.DefaultResponse(request) : Item(item));
        };
    }
}
