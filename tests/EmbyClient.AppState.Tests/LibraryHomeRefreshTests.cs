using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.Net;
using System.Text;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryHomeRefreshTests
{
    [Fact]
    public async Task A_home_background_refresh_updates_cached_home_without_changing_details_or_the_viewport()
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var homeCard = Assert.Single(model.HomeRows.SelectMany(row => row.Items), card => card.Id == "resume-a");
        var homeViewport = Viewport(homeCard.Id);
        model.ViewportState = homeViewport;
        context.Handler.RespondAsync = request => Task.FromResult(request.Is($"/Users/user-a/Items/{homeCard.Id}")
            ? Item(homeCard.Item) : context.DefaultResponse(request));
        await model.ShowItemAsync(homeCard, TestContext.Current.CancellationToken);
        model.ViewportState = Viewport(homeCard.Id) with { DetailOffset = 1230, DetailPageIndex = 2 };
        var visible = CaptureVisibleState(model);
        var library = Assert.Single(model.Libraries);
        context.Views = [context.Views[0] with { Name = "Updated library" }];
        context.Resume = [homeCard.Item with { Name = "Updated continue watching" }];
        context.Latest = [Movie("latest-b", "New latest movie")];
        using var latest = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Latest")
            ? latest.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refreshing = model.RefreshHomeShelvesAsync(TestContext.Current.CancellationToken);
        await latest.WaitForRequestAsync();

        Assert.False(refreshing.IsCompleted);
        AssertVisibleState(visible, model);
        Assert.Empty(model.HomeRows);
        Assert.Equal("Movies", library.Title);
        latest.Return(LibraryTestContext.Array(context.Latest));
        Assert.True(await refreshing);

        AssertVisibleState(visible, model);
        Assert.Empty(model.HomeRows);
        Assert.Equal("Updated library", library.Title);
        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.True(model.IsHome);
        Assert.True(model.HasItems);
        Assert.False(model.HasError);
        Assert.Equal(homeViewport, model.ViewportState);
        Assert.Equal(new[] { HomeSection.ContinueWatching, HomeSection.NextUp, HomeSection.RecentlyAdded },
            model.HomeRows.Select(row => row.Section!.Value));
        Assert.Equal("Updated continue watching", Assert.Single(Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items).Title);
        var latestRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        Assert.Equal("Recently added in Updated library", latestRow.Title);
        Assert.Equal("latest-b", Assert.Single(latestRow.Items).Id);
        Assert.Equal(requestStart, context.Handler.Requests.Length);

        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(visible.Detail, model.Detail);
        Assert.Same(visible.DetailItem, model.Detail.Item);
        Assert.Same(visible.Playable, model.PlayableDetail);
        Assert.Equal(visible.Viewport, model.ViewportState);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task A_partial_home_background_refresh_keeps_failed_cards_removes_empty_rows_and_reloads_stale_home()
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var nextUpRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp);
        var nextUpCard = Assert.Single(nextUpRow.Items);
        var detailCard = Assert.Single(model.HomeRows.SelectMany(row => row.Items), card => card.Id == "latest-a");
        var homeViewport = Viewport(nextUpCard.Id) with { HomePageIndex = 3 };
        model.ViewportState = homeViewport;
        context.Handler.RespondAsync = request => Task.FromResult(request.Is($"/Users/user-a/Items/{detailCard.Id}")
            ? Item(detailCard.Item) : context.DefaultResponse(request));
        await model.ShowItemAsync(detailCard, TestContext.Current.CancellationToken);
        model.ViewportState = Viewport(detailCard.Id) with { DetailPageIndex = 2 };
        var visible = CaptureVisibleState(model);
        context.Resume = [Movie("resume-a", "Updated continue watching")];
        context.Latest = [];
        using var nextUp = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Shows/NextUp")
            ? nextUp.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var refreshing = model.RefreshHomeShelvesAsync(TestContext.Current.CancellationToken);
        await nextUp.WaitForRequestAsync();
        nextUp.Return(Failure());
        Assert.False(await refreshing);
        AssertVisibleState(visible, model);
        Assert.Empty(model.HomeRows);

        context.NextUp = [Movie("next-a", "Recovered next up")];
        using var retryLatest = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Latest")
            ? retryLatest.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;
        var returning = model.GoBackAsync(TestContext.Current.CancellationToken);
        await retryLatest.WaitForRequestAsync();

        Assert.True(model.IsHome);
        Assert.False(returning.IsCompleted);
        Assert.True(model.IsBusy);
        Assert.Equal(homeViewport, model.ViewportState);
        Assert.Same(nextUpRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp));
        Assert.Same(nextUpCard, Assert.Single(nextUpRow.Items));
        Assert.Equal("Watch this next", nextUpCard.Title);
        Assert.Equal("Updated continue watching", Assert.Single(Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items).Title);
        Assert.DoesNotContain(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        var retryRequests = context.Handler.Requests.Skip(requestStart).ToArray();
        Assert.Equal(4, retryRequests.Length);
        Assert.Single(retryRequests, request => request.Is("/Users/user-a/Views"));
        Assert.Single(retryRequests, request => request.Is("/Users/user-a/Items/Resume"));
        Assert.Single(retryRequests, request => request.Is("/Shows/NextUp"));
        Assert.Single(retryRequests, request => request.Is("/Users/user-a/Items/Latest"));

        retryLatest.Return(LibraryTestContext.Array([]));
        await returning;
        Assert.Same(nextUpRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp));
        Assert.Same(nextUpCard, Assert.Single(nextUpRow.Items));
        Assert.Equal("Recovered next up", nextUpCard.Title);
        Assert.Equal(homeViewport, model.ViewportState);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
        Assert.DoesNotContain(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);

        requestStart = context.Handler.Requests.Length;
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Same(visible.Detail, model.Detail);
        Assert.Same(visible.DetailItem, model.Detail.Item);
        Assert.Equal(visible.Viewport, model.ViewportState);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(homeViewport, model.ViewportState);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task A_background_home_refresh_reloads_resume_after_a_concurrent_removal()
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var removed = Assert.Single(Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items);
        var viewport = Viewport(removed.Id);
        model.ViewportState = viewport;
        var revision = model.NavigationRevision;
        using var obsoleteResume = new RequestGate();
        using var freshResume = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume")
            ? obsoleteResume.RespondAsync(request) : request.Is("/HideFromResume")
            ? Task.FromResult(UserData(new())) : Task.FromResult(context.DefaultResponse(request));
        var requestStart = context.Handler.Requests.Length;

        var refreshing = model.RefreshHomeShelvesAsync(TestContext.Current.CancellationToken);
        await obsoleteResume.WaitForRequestAsync();
        await model.RemoveFromContinueWatchingAsync(removed, TestContext.Current.CancellationToken);
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume")
            ? freshResume.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        obsoleteResume.Return(Page([removed.Item, Movie("resume-survivor", "Obsolete survivor")]));
        await freshResume.WaitForRequestAsync();

        Assert.False(refreshing.IsCompleted);
        Assert.Equal(2, context.Handler.Requests.Skip(requestStart)
            .Count(request => request.Is("/Users/user-a/Items/Resume")));
        Assert.DoesNotContain(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(viewport, model.ViewportState);
        freshResume.Return(Page([Movie("resume-survivor", "Updated survivor")]));
        Assert.True(await refreshing);

        var row = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var survivor = Assert.Single(row.Items);
        Assert.Equal("resume-survivor", survivor.Id);
        Assert.Equal("Updated survivor", survivor.Title);
        Assert.DoesNotContain(row.Items, card => card.Id == removed.Id);
        Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.Is("/HideFromResume"));
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(viewport, model.ViewportState);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_older_home_load_or_refresh_cannot_replace_fresh_background_rows_or_library_metadata(
        bool foregroundRefresh, bool failObsolete)
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        context.Views = [context.Views[0] with { Name = "Obsolete library" }];
        context.Resume = [Movie("obsolete-resume")];
        context.NextUp = [Movie("obsolete-next")];
        BaseItemDto[] obsoleteLatest = [Movie("obsolete-latest")];
        using var latest = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Latest")
            ? latest.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;

        var foreground = foregroundRefresh ? model.RefreshAsync(TestContext.Current.CancellationToken)
            : model.ShowHomeAsync(TestContext.Current.CancellationToken);
        var obsoleteRequest = await latest.WaitForRequestAsync();
        var revision = model.NavigationRevision;
        context.Views = [context.Views[0] with { Name = "Fresh library" }];
        context.Resume = [Movie("fresh-resume")];
        context.NextUp = [Movie("fresh-next")];
        context.Latest = [Movie("fresh-latest")];
        context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));

        Assert.True(await model.RefreshHomeShelvesAsync(TestContext.Current.CancellationToken));
        var freshRows = CaptureRows(model.HomeRows);
        var library = Assert.Single(model.Libraries);
        Assert.Equal("Fresh library", library.Title);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.False(obsoleteRequest.CancellationToken.IsCancellationRequested);
        latest.Return(failObsolete ? Failure() : LibraryTestContext.Array(obsoleteLatest));
        await foreground;

        AssertRowsUnchanged(freshRows, model.HomeRows);
        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Fresh library", library.Title);
        Assert.Equal(new[] { "fresh-resume", "fresh-next", "fresh-latest" },
            model.HomeRows.SelectMany(row => row.Items).Select(card => card.Id));
        Assert.Equal("Recently added in Fresh library", Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.RecentlyAdded).Title);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(0, expired);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task An_obsolete_foreground_home_views_failure_cannot_replace_fresh_rows_or_expire_the_session(
        HttpStatusCode status)
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var viewport = Viewport("resume-a");
        model.ViewportState = viewport;
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;
        using var views = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Views")
            ? views.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));

        var foreground = model.RefreshAsync(TestContext.Current.CancellationToken);
        var obsoleteRequest = await views.WaitForRequestAsync();
        var revision = model.NavigationRevision;
        context.Views = [context.Views[0] with { Name = "Fresh library" }];
        context.Resume = [Movie("fresh-resume")];
        context.NextUp = [Movie("fresh-next")];
        context.Latest = [Movie("fresh-latest")];
        context.Handler.RespondAsync = request => Task.FromResult(context.DefaultResponse(request));

        Assert.True(await model.RefreshHomeShelvesAsync(TestContext.Current.CancellationToken));
        var freshRows = CaptureRows(model.HomeRows);
        var library = Assert.Single(model.Libraries);
        Assert.False(obsoleteRequest.CancellationToken.IsCancellationRequested);
        views.Return(StatusResponse(status));
        await foreground;

        AssertRowsUnchanged(freshRows, model.HomeRows);
        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Fresh library", library.Title);
        Assert.Equal(new[] { "fresh-resume", "fresh-next", "fresh-latest" },
            model.HomeRows.SelectMany(row => row.Items).Select(card => card.Id));
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal(revision, model.NavigationRevision);
        Assert.Equal(0, expired);
        Assert.False(model.HasError);
        Assert.Equal(string.Empty, model.ErrorMessage);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Replacing_the_user_session_ignores_old_home_refresh_results_and_authentication_failures(
        bool gateViews, bool failObsolete)
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        BaseItemDto[] obsoleteViews = [context.Views[0] with { Name = "Obsolete library" }];
        BaseItemDto[] obsoleteLatest = [Movie("obsolete-latest")];
        context.Views = obsoleteViews;
        using var obsolete = new RequestGate();
        context.Handler.RespondAsync = request => request.Is(gateViews
                ? "/Users/user-a/Views" : "/Users/user-a/Items/Latest")
            ? obsolete.RespondAsync(request) : Task.FromResult(context.DefaultResponse(request));
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;

        var refreshing = model.RefreshHomeShelvesAsync(TestContext.Current.CancellationToken);
        var obsoleteRequest = await obsolete.WaitForRequestAsync();
        BaseItemDto[] newViews = [context.Views[0] with { Id = "library-b", Name = "Movies B" }];
        BaseItemDto[] newResume = [Movie("resume-b")];
        BaseItemDto[] newNextUp = [Movie("next-b")];
        BaseItemDto[] newLatest = [Movie("latest-b")];
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-b/Views")
            ? Page(newViews) : request.Is("/Users/user-b/Items/Resume") ? Page(newResume)
            : request.Is("/Shows/NextUp") && request.Query.GetValueOrDefault("UserId") == "user-b" ? Page(newNextUp)
            : request.Is("/Users/user-b/Items/Latest") ? LibraryTestContext.Array(newLatest)
            : context.DefaultResponse(request));
        var newApi = context.Api.WithAuthentication("token-b", "user-b");
        var requestStart = context.Handler.Requests.Length;
        await model.SetSessionAsync(newApi, "server-b", new UserDto { Id = "user-b" },
            TestContext.Current.CancellationToken);
        model.ViewportState = Viewport("resume-b") with { NavigationLibraryId = "library-b", HomePageIndex = 3 };
        var visible = CaptureVisibleState(model);
        var newRows = CaptureRows(model.HomeRows);
        var library = Assert.Single(model.Libraries);

        Assert.Equal("library-b", library.Id);
        Assert.Equal("Movies B", library.Title);
        Assert.True(obsoleteRequest.CancellationToken.IsCancellationRequested);
        Assert.Contains(context.Handler.Requests.Skip(requestStart), request => request.Is("/Shows/NextUp")
            && request.Query.GetValueOrDefault("UserId") == "user-b");
        obsolete.Return(failObsolete ? StatusResponse(HttpStatusCode.Unauthorized)
            : gateViews ? Page(obsoleteViews) : LibraryTestContext.Array(obsoleteLatest));
        Assert.False(await refreshing);

        AssertVisibleState(visible, model);
        AssertRowsUnchanged(newRows, model.HomeRows);
        Assert.Same(library, Assert.Single(model.Libraries));
        Assert.Equal("Movies B", library.Title);
        Assert.Equal(new[] { "resume-b", "next-b", "latest-b" },
            model.HomeRows.SelectMany(row => row.Items).Select(card => card.Id));
        Assert.False(model.CanGoBack);
        Assert.False(model.CanGoForward);
        Assert.Equal(0, expired);
        Assert.False(model.HasError);
    }

    private static BrowseViewportState Viewport(string itemId) => new(
        VerticalOffset: 540, DetailOffset: 810, ChildrenOffset: 240,
        AnchorId: itemId, AnchorIndex: 0, FocusedItemId: itemId,
        OverviewExpanded: true, EpisodeList: true, NavigationLibraryId: "library-a",
        CollectionWidth: 1180, HomePageIndex: 2, DetailPageIndex: 1);

    private static HttpResponseMessage StatusResponse(HttpStatusCode status) => new(status)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    };

    private static RowState[] CaptureRows(IEnumerable<MediaShelfViewModel> rows) => rows
        .Select(row => new RowState(row, row.Items.ToArray(), row.Items.Select(card => card.Item).ToArray())).ToArray();

    private static void AssertRowsUnchanged(IReadOnlyList<RowState> expected, IReadOnlyList<MediaShelfViewModel> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index].Row, actual[index]);
            AssertSameCards(expected[index].Cards, actual[index].Items);
            for (var cardIndex = 0; cardIndex < expected[index].Items.Length; cardIndex++)
                Assert.Same(expected[index].Items[cardIndex], actual[index].Items[cardIndex].Item);
        }
    }

    private static VisibleState CaptureVisibleState(LibraryViewModel model) => new(
        model.NavigationRevision, model.Title, model.Subtitle, model.EmptyMessage, model.ErrorMessage,
        model.HasError, model.HasItems, model.HasMore, model.HasDetails, model.IsHome, model.IsBusy, model.IsMutating,
        model.LoadOutcome, model.TotalItemsCount, model.NavigationLibraryId,
        model.Detail, model.Detail.Item, model.PlayableDetail, model.PlayableDetail.Item, model.NextEpisode,
        model.ViewportState);

    private static void AssertVisibleState(VisibleState expected, LibraryViewModel model)
    {
        Assert.Equal(expected, CaptureVisibleState(model));
        Assert.Same(expected.DetailItem, model.Detail.Item);
        Assert.Same(expected.PlayableItem, model.PlayableDetail.Item);
    }

    private sealed record RowState(MediaShelfViewModel Row, MediaCardViewModel[] Cards, BaseItemDto[] Items);

    private sealed record VisibleState(int NavigationRevision, string Title, string Subtitle, string EmptyMessage,
        string ErrorMessage, bool HasError, bool HasItems, bool HasMore, bool HasDetails, bool IsHome, bool IsBusy,
        bool IsMutating, PageLoadOutcome Outcome, int? TotalItemsCount, string? NavigationLibraryId,
        MediaCardViewModel Detail, BaseItemDto DetailItem, MediaCardViewModel Playable, BaseItemDto PlayableItem,
        MediaCardViewModel? NextEpisode, BrowseViewportState Viewport);
}
