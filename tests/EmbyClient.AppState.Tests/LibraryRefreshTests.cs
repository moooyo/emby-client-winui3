using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.Collections.Specialized;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryRefreshTests
{
    [Fact]
    public async Task Refresh_keeps_visible_cards_until_the_whole_result_is_ready_then_reuses_retained_cards()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync([Movie("a", "Old A"), Movie("b", "Old B"), Movie("c", "Old C")]);
        var model = context.Model;
        var collection = model.Items;
        var cards = model.Items.ToArray();
        var library = Assert.Single(model.Libraries);
        var changes = new List<NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, change) => changes.Add(change.Action);
        context.Views = [context.Views[0] with { Name = "Renamed library" }];
        using var gate = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? gate.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        await gate.WaitForRequestAsync();

        AssertSameCards(cards, model.Items);
        Assert.Equal("Old A", cards[0].Title);
        Assert.Equal("Movies", library.Title);
        Assert.True(model.IsBackgroundLoading);
        Assert.False(model.IsInitialLoading);
        Assert.False(model.IsLoadingMore);
        Assert.Empty(changes);

        gate.Return(Page([Movie("b", "New B"), Movie("a", "New A"), Movie("d", "New D")]));
        await refresh;

        Assert.Same(collection, model.Items);
        Assert.Equal(new[] { "b", "a", "d" }, model.Items.Select(card => card.Id));
        Assert.Same(cards[1], model.Items[0]);
        Assert.Same(cards[0], model.Items[1]);
        Assert.Equal("New A", cards[0].Title);
        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Renamed library", library.Title);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Refresh_reloads_the_entire_loaded_window_before_committing_and_keeps_the_next_offset()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(120));
        await context.Model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var model = context.Model;
        var original = model.Items.ToArray();
        Assert.Equal(96, original.Length);
        var requestStart = context.Handler.Requests.Length;
        context.Catalog = Movies(120, "Updated");
        using var secondPage = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems && request.Query["StartIndex"] == "48"
            ? secondPage.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        await secondPage.WaitForRequestAsync();

        AssertSameCards(original, model.Items);
        Assert.All(model.Items, card => Assert.StartsWith("Original", card.Title));
        secondPage.Return(Page(context.Catalog.Skip(48).Take(48).ToArray(), 120));
        await refresh;

        AssertSameCards(original, model.Items);
        Assert.All(model.Items, card => Assert.StartsWith("Updated", card.Title));
        Assert.True(model.HasMore);
        Assert.Equal(new[] { "0", "48" }, context.Handler.Requests.Skip(requestStart)
            .Where(request => request.IsItems).Select(request => request.Query["StartIndex"]));

        using var more = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? more.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var loadMore = model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var moreRequest = await more.WaitForRequestAsync();
        Assert.Equal("96", moreRequest.Query["StartIndex"]);
        Assert.True(model.IsLoadingMore);
        Assert.False(model.IsBackgroundLoading);
        Assert.Equal(96, model.Items.Count);
        more.Return(Page(context.Catalog.Skip(96).ToArray(), 120));
        await loadMore;
        Assert.Equal(120, model.Items.Count);
        Assert.False(model.HasMore);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task A_failed_later_refresh_page_preserves_all_content_metadata_and_paging()
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(120));
        var model = context.Model;
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var original = model.Items.ToArray();
        var subtitle = model.Subtitle;
        var library = Assert.Single(model.Libraries);
        context.Views = [context.Views[0] with { Name = "Uncommitted library" }];
        context.Catalog = Movies(120, "Uncommitted");
        using var secondPage = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems && request.Query["StartIndex"] == "48"
            ? secondPage.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        await secondPage.WaitForRequestAsync();
        secondPage.Return(Failure());
        await refresh;

        AssertSameCards(original, model.Items);
        Assert.All(model.Items, card => Assert.StartsWith("Original", card.Title));
        Assert.Equal(subtitle, model.Subtitle);
        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Movies", library.Title);
        Assert.True(model.HasMore);
        Assert.True(model.HasError);
        Assert.False(model.IsBusy);

        var requestStart = context.Handler.Requests.Length;
        await model.LoadMoreAsync(TestContext.Current.CancellationToken);
        Assert.Equal("96", Assert.Single(context.Handler.Requests.Skip(requestStart),
            request => request.IsItems).Query["StartIndex"]);
        Assert.Equal(120, model.Items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_cancelled_or_departed_refresh_cannot_commit_a_late_response(bool navigateAway)
    {
        using var context = await CreateAsync();
        await context.OpenLibraryAsync(Movies(72));
        var model = context.Model;
        var original = model.Items.ToArray();
        var library = Assert.Single(model.Libraries);
        context.Views = [context.Views[0] with { Name = "Stale library" }];
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var gate = new RequestGate();
        context.Handler.RespondAsync = request => request.IsItems
            ? gate.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refresh = model.RefreshAsync(cancellation.Token);
        var pendingRequest = await gate.WaitForRequestAsync();
        if (navigateAway)
        {
            context.Catalog = [Movie("favorite-a")];
            context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));
            await model.ShowFavoritesAsync(TestContext.Current.CancellationToken);
        }
        else cancellation.Cancel();
        Assert.True(pendingRequest.CancellationToken.IsCancellationRequested);
        gate.Return(Page(Movies(48, "Stale"), 72));
        await refresh;

        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Movies", library.Title);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
        if (navigateAway)
        {
            Assert.True(model.IsFavorites);
            Assert.Equal("favorite-a", Assert.Single(model.Items).Id);
        }
        else
        {
            AssertSameCards(original, model.Items);
            Assert.All(model.Items, card => Assert.StartsWith("Original", card.Title));
            context.Catalog = Movies(72);
            context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));
            await model.LoadMoreAsync(TestContext.Current.CancellationToken);
            Assert.Equal(72, model.Items.Count);
        }
    }

    [Fact]
    public async Task A_failed_home_shelf_preserves_its_cards_while_successful_shelves_and_libraries_refresh()
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var resumeRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var resumeCard = Assert.Single(resumeRow.Items);
        var nextUpRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp);
        var nextUpCard = Assert.Single(nextUpRow.Items);
        var library = Assert.Single(model.Libraries);
        context.Views = [context.Views[0] with { Name = "Updated library" }];
        context.Resume = [Movie("resume-a", "Updated resume")];
        context.Latest = [Movie("latest-a", "Updated latest")];
        using var nextUp = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Shows/NextUp")
            ? nextUp.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        await nextUp.WaitForRequestAsync();
        Assert.True(model.IsBusy);
        Assert.False(model.IsBackgroundLoading);
        nextUp.Return(Failure());
        await refresh;

        Assert.Equal(3, model.HomeRows.Count);
        Assert.Same(nextUpRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp));
        Assert.Same(nextUpCard, Assert.Single(nextUpRow.Items));
        Assert.Equal("Watch this next", nextUpCard.Title);
        Assert.Same(resumeRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching));
        Assert.Same(resumeCard, Assert.Single(resumeRow.Items));
        Assert.Equal("Updated resume", resumeCard.Title);
        var recentlyAddedRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        Assert.Equal(library.Id, recentlyAddedRow.LibraryId);
        Assert.Equal("Recently added in Updated library", recentlyAddedRow.Title);
        var recentlyAddedCard = Assert.Single(recentlyAddedRow.Items);
        Assert.Equal("latest-a", recentlyAddedCard.Id);
        Assert.Equal("Updated latest", recentlyAddedCard.Title);
        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Updated library", library.Title);
        Assert.True(model.IsHome);
        Assert.True(model.HasItems);
        Assert.True(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task A_refresh_snapshot_cannot_overwrite_a_favorite_change_that_completed_while_refresh_waited()
    {
        using var context = await CreateAsync();
        var original = Movie("movie-a", "Favorite candidate") with
        {
            UserData = new UserItemDataDto { IsFavorite = false, Played = false }
        };
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/movie-a")
            ? Task.FromResult(Item(original)) : Task.FromResult(context.DefaultResponse(request));
        await context.Model.ShowItemAsync(new MediaCardViewModel(original), TestContext.Current.CancellationToken);
        var model = context.Model;
        var detail = model.Detail;
        using var views = new RequestGate();
        var detailRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        context.Handler.RespondAsync = request =>
        {
            if (request.Is("/Users/user-a/Views")) return views.RespondAsync(request);
            if (request.Is("/Users/user-a/Items/movie-a"))
            {
                detailRead.SetResult();
                return Task.FromResult(Item(original with { Name = "Updated detail" }));
            }
            if (request.Is("/Users/user-a/FavoriteItems/movie-a"))
                return Task.FromResult(UserData(new UserItemDataDto { ItemId = "movie-a", IsFavorite = true, Played = false }));
            return Task.FromResult(context.DefaultResponse(request));
        };

        var refresh = model.RefreshAsync(TestContext.Current.CancellationToken);
        await views.WaitForRequestAsync();
        await detailRead.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await model.ToggleFavoriteAsync(TestContext.Current.CancellationToken);
        Assert.True(detail.IsFavorite);
        views.Return(Page(context.Views));
        await refresh;

        Assert.Same(detail, model.Detail);
        Assert.Equal("Updated detail", model.Detail.Title);
        Assert.True(model.Detail.IsFavorite);
        Assert.True(model.PlayableDetail.IsFavorite);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }
}
