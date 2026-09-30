using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryResumeRemovalTests
{
    [Fact]
    public async Task Removal_from_details_prunes_resume_and_home_history_without_mutating_cards_or_refetching()
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        var homeTarget = target with { UserData = target.UserData! with { PlaybackPositionTicks = 0 } };
        context.Resume = [target, .. Movies(48)];
        context.NextUp = [target];
        context.Latest = [target];
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/Resume")
            && request.Query.GetValueOrDefault("Limit") == "16"
                ? Page([homeTarget]) : ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        var continueRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var continueCard = Assert.Single(continueRow.Items);
        var nextUpRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp);
        var nextUpCard = Assert.Single(nextUpRow.Items);
        var latestRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded);
        var latestCard = Assert.Single(latestRow.Items);

        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var resumeCards = model.Items.ToArray();
        Assert.Equal(48, resumeCards.Length);
        var resumeCard = Assert.Single(resumeCards, card => card.Id == target.Id);
        await model.ShowItemAsync(resumeCard, TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var playable = model.PlayableDetail;
        var states = CaptureUserData(continueCard, nextUpCard, latestCard, resumeCard, detail, playable);
        var requestStart = context.Handler.Requests.Length;

        await model.RemoveFromContinueWatchingAsync(detail, TestContext.Current.CancellationToken);

        var removal = Assert.Single(context.Handler.Requests.Skip(requestStart));
        AssertRemovalRequest(removal, target.Id!);
        Assert.Same(detail, model.Detail);
        Assert.Same(playable, model.PlayableDetail);
        AssertUnchangedUserData(states);
        Assert.False(model.IsMutating);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(requestStart + 1, context.Handler.Requests.Length);
        AssertSameCards(resumeCards.Where(card => card.Id != target.Id).ToArray(), model.Items);
        Assert.DoesNotContain(model.Items, card => card.Id == target.Id);
        Assert.Equal(48, model.TotalItemsCount);
        Assert.True(model.HasMore);

        context.Resume = context.Resume.Where(item => item.Id != target.Id).ToArray();
        var pagingStart = context.Handler.Requests.Length;
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        var paging = Assert.Single(context.Handler.Requests.Skip(pagingStart));
        Assert.True(paging.Is("/Users/user-a/Items/Resume"));
        Assert.Equal("47", paging.Query["StartIndex"]);
        Assert.Equal(48, model.Items.Count);
        Assert.False(model.HasMore);

        var restoreStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(restoreStart, context.Handler.Requests.Length);
        Assert.True(model.IsHome);
        Assert.DoesNotContain(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        Assert.Empty(continueRow.Items);
        Assert.Same(nextUpRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.NextUp));
        Assert.Same(nextUpCard, Assert.Single(nextUpRow.Items));
        Assert.Same(latestRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.RecentlyAdded));
        Assert.Same(latestCard, Assert.Single(latestRow.Items));
        AssertUnchangedUserData(states);
        Assert.Single(context.Handler.Requests, request => request.Is("/HideFromResume"));
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Removal_prunes_a_resume_search_origin_but_keeps_search_results_and_library_history()
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        context.Resume = [target, Movie("resume-survivor")];
        context.Catalog = [target, Movie("library-survivor")];
        context.Handler.RespondAsync = request => Task.FromResult(request.IsItems
            && request.Query.ContainsKey("SearchTerm")
                ? Page([target]) : ResumeResponse(context, request, target));
        var model = context.Model;
        await context.OpenLibraryAsync(context.Catalog);
        var libraryCards = model.Items.ToArray();
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var resumeCards = model.Items.ToArray();
        await model.SearchAsync("candidate", debounce: false, TestContext.Current.CancellationToken);
        var searchCard = Assert.Single(model.Items);
        var viewport = new BrowseViewportState(260, AnchorId: target.Id, FocusedItemId: target.Id);
        model.ViewportState = viewport;
        await model.ShowItemAsync(searchCard, TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var states = CaptureUserData(libraryCards[0], resumeCards[0], searchCard, detail);

        await model.RemoveFromContinueWatchingAsync(detail, TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsSearch);
        Assert.Same(searchCard, Assert.Single(model.Items));
        Assert.Equal(viewport, model.ViewportState);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHomeSection);
        Assert.Equal(HomeSection.ContinueWatching, model.SelectedHomeSection);
        Assert.Same(resumeCards[1], Assert.Single(model.Items));
        Assert.Equal(1, model.TotalItemsCount);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal("library-a", model.SelectedLibraryId);
        AssertSameCards(libraryCards, model.Items);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        AssertUnchangedUserData(states);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removal_keeps_current_and_cached_episode_lists_and_their_playable_card(bool cacheEpisodes)
    {
        using var context = await CreateAsync();
        var episode = ResumeMovie() with
        {
            Type = "Episode", SeriesId = "series-a", SeriesName = "Test Series", SeasonId = "season-a",
            ParentIndexNumber = 1, IndexNumber = 1
        };
        var following = episode with { Id = "episode-b", Name = "Following episode", IndexNumber = 2 };
        var season = new BaseItemDto
        {
            Id = "season-a", Name = "Season 1", Type = "Season", IsFolder = true,
            SeriesId = "series-a", SeriesName = "Test Series", IndexNumber = 1
        };
        var unrelated = Movie("unrelated-detail");
        context.Resume = [episode];
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/season-a")
            ? Item(season) : request.Is("/Shows/series-a/Episodes") ? Page([episode, following])
            : request.Is("/Users/user-a/Items/unrelated-detail") ? Item(unrelated)
            : ResumeResponse(context, request, episode));
        var model = context.Model;
        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        var continueRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var continueCard = Assert.Single(continueRow.Items);
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var resumeCard = Assert.Single(model.Items);
        await model.ShowItemAsync(new(season), TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var episodes = model.Items.ToArray();
        var playable = model.PlayableDetail;
        var viewport = new BrowseViewportState(0, 720, 280, episode.Id, 0, episode.Id, EpisodeList: true);
        model.ViewportState = viewport;
        var states = CaptureUserData(continueCard, resumeCard, episodes[0], episodes[1], playable);
        if (cacheEpisodes) await model.ShowItemAsync(new(unrelated), TestContext.Current.CancellationToken);

        await model.RemoveFromContinueWatchingAsync(episodes[0], TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        if (cacheEpisodes) await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(detail, model.Detail);
        Assert.Same(playable, model.PlayableDetail);
        AssertSameCards(episodes, model.Items);
        Assert.True(model.DetailItemsAreEpisodes);
        Assert.Equal(viewport, model.ViewportState);
        AssertUnchangedUserData(states);

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHomeSection);
        Assert.Empty(model.Items);
        Assert.False(model.HasItems);
        Assert.False(model.HasMore);
        Assert.Equal(0, model.TotalItemsCount);
        Assert.Equal(Visibility.Visible, model.EmptyVisibility);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.DoesNotContain(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        AssertUnchangedUserData(states);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_home_load_started_before_removal_does_not_restore_the_item_in_progress_or_final_results()
    {
        using var context = await CreateAsync(populatedHome: true);
        var model = context.Model;
        var card = Assert.Single(Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items);
        var target = ResumeMovie();
        var survivor = Movie("resume-survivor");
        using var resume = new RequestGate();
        using var nextUp = new RequestGate();
        using var latest = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume")
            ? resume.RespondAsync(request) : request.Is("/Shows/NextUp") ? nextUp.RespondAsync(request)
            : request.Is("/Users/user-a/Items/Latest") ? latest.RespondAsync(request)
            : Task.FromResult(ResumeResponse(context, request, target));
        var progressPresented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, _) =>
        {
            if (model.IsBusy && model.HomeRows.Any(row => row.Section == HomeSection.ContinueWatching
                && row.Items.Any(item => item.Id == survivor.Id))) progressPresented.TrySetResult();
        };

        var loading = model.ShowHomeAsync(TestContext.Current.CancellationToken);
        await resume.WaitForRequestAsync();
        await nextUp.WaitForRequestAsync();
        await latest.WaitForRequestAsync();
        await model.RemoveFromContinueWatchingAsync(card, TestContext.Current.CancellationToken);
        resume.Return(Page([target, survivor]));
        await progressPresented.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var continueRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var survivorCard = Assert.Single(continueRow.Items);
        Assert.Equal(survivor.Id, survivorCard.Id);
        Assert.False(loading.IsCompleted);
        nextUp.Return(Page([Movie("next-a")]));
        latest.Return(LibraryTestContext.Array([Movie("latest-a")]));
        await loading;

        Assert.Same(continueRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching));
        Assert.Same(survivorCard, Assert.Single(continueRow.Items));
        Assert.DoesNotContain(continueRow.Items, item => item.Id == target.Id);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_home_or_resume_read_started_before_removal_cannot_restore_it_but_a_new_read_can_include_it(
        bool home, bool refresh)
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        context.Resume = [target, Movie("resume-survivor")];
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        if (!home && refresh) await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var original = home || !refresh ? Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items.ToArray() : model.Items.ToArray();
        var card = Assert.Single(original, item => item.Id == target.Id);
        var survivor = Assert.Single(original, item => item.Id == "resume-survivor");
        var states = CaptureUserData(card);
        using var resume = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume")
            ? resume.RespondAsync(request) : Task.FromResult(ResumeResponse(context, request, target));

        var requestStart = context.Handler.Requests.Length;
        var loading = refresh ? model.RefreshAsync(TestContext.Current.CancellationToken)
            : model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        await resume.WaitForRequestAsync();
        await model.RemoveFromContinueWatchingAsync(card, TestContext.Current.CancellationToken);
        var visible = home ? Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items : model.Items;
        if (refresh) Assert.Same(survivor, Assert.Single(visible));
        else Assert.Empty(visible);
        context.Resume = [Movie("resume-survivor", "Updated survivor")];
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        resume.Return(Page([target, Movie("resume-survivor", "Updated survivor")]));
        await loading;

        visible = home ? Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items : model.Items;
        var retained = Assert.Single(visible);
        Assert.Equal("resume-survivor", retained.Id);
        if (refresh) Assert.Same(survivor, retained);
        Assert.Equal("Updated survivor", retained.Title);
        Assert.Equal(home ? 1 : 2, context.Handler.Requests.Skip(requestStart)
            .Count(request => request.Is("/Users/user-a/Items/Resume")));
        AssertUnchangedUserData(states);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);

        context.Resume = [target, Movie("resume-survivor")];
        await model.RefreshAsync(TestContext.Current.CancellationToken);
        visible = home ? Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.ContinueWatching).Items : model.Items;
        Assert.Equal(target.Id, Assert.Single(visible, item => item.Id == target.Id).Id);
        Assert.Equal(2, visible.Count);
        Assert.Single(context.Handler.Requests, request => request.Is("/HideFromResume"));
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_resume_page_started_before_removal_reloads_the_shifted_offset_instead_of_losing_the_tail()
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        context.Resume = [target, .. Movies(48)];
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var original = model.Items.ToArray();
        var card = Assert.Single(original, item => item.Id == target.Id);
        using var more = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume")
            ? more.RespondAsync(request) : Task.FromResult(ResumeResponse(context, request, target));

        var requestStart = context.Handler.Requests.Length;
        var loading = model.LoadMoreAsync(TestContext.Current.CancellationToken);
        var request = await more.WaitForRequestAsync();
        Assert.Equal("48", request.Query["StartIndex"]);
        await model.RemoveFromContinueWatchingAsync(card, TestContext.Current.CancellationToken);
        AssertSameCards(original.Where(item => item.Id != target.Id).ToArray(), model.Items);
        context.Resume = context.Resume.Where(item => item.Id != target.Id).ToArray();
        context.Handler.RespondAsync = observed => Task.FromResult(ResumeResponse(context, observed, target));
        more.Return(Page([], 48));
        Assert.Equal(PageLoadMoreOutcome.Appended, await loading);

        Assert.DoesNotContain(model.Items, item => item.Id == target.Id);
        Assert.Equal(48, model.Items.Count);
        Assert.Equal(48, model.TotalItemsCount);
        AssertSameCards(original.Where(item => item.Id != target.Id).ToArray(), model.Items.Take(47).ToArray());
        Assert.Equal(new[] { "48", "47" }, context.Handler.Requests.Skip(requestStart)
            .Where(observed => observed.Is("/Users/user-a/Items/Resume"))
            .Select(observed => observed.Query["StartIndex"]));
        Assert.Equal(context.Resume.Select(item => item.Id), model.Items.Select(item => item.Id));
        Assert.False(model.HasMore);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task A_resume_response_received_before_a_pending_removal_waits_for_its_outcome(
        bool refresh, bool removalSucceeds)
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        context.Resume = [target, .. Movies(48)];
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var original = model.Items.ToArray();
        var card = Assert.Single(original, item => item.Id == target.Id);
        var states = CaptureUserData(card);
        using var resume = new RequestGate();
        using var removal = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/HideFromResume")
            ? removal.RespondAsync(request) : request.Is("/Users/user-a/Items/Resume")
                ? resume.RespondAsync(request) : Task.FromResult(ResumeResponse(context, request, target));
        var requestStart = context.Handler.Requests.Length;
        Task<PageLoadMoreOutcome>? paging = null;
        Task reading;
        if (refresh) reading = model.RefreshAsync(TestContext.Current.CancellationToken);
        else
        {
            paging = model.LoadMoreAsync(TestContext.Current.CancellationToken);
            reading = paging;
        }
        var resumeRequest = await resume.WaitForRequestAsync();
        Assert.Equal(refresh ? "0" : "48", resumeRequest.Query["StartIndex"]);
        var hiding = model.RemoveFromContinueWatchingAsync(card, TestContext.Current.CancellationToken);
        AssertRemovalRequest(await removal.WaitForRequestAsync(), target.Id!);
        if (removalSucceeds) context.Resume = context.Resume.Where(item => item.Id != target.Id).ToArray();
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        var response = refresh ? Page(context.Resume.Take(48).ToArray(), context.Resume.Length)
            : Page(context.Resume.Skip(48).ToArray(), context.Resume.Length);

        resume.Return(response);
        await Assert.ThrowsAsync<TimeoutException>(() => reading.WaitAsync(TimeSpan.FromMilliseconds(100),
            TestContext.Current.CancellationToken));

        Assert.False(reading.IsCompleted);
        AssertSameCards(original, model.Items);
        Assert.Equal(49, model.TotalItemsCount);
        Assert.True(model.HasMore);
        Assert.True(model.IsBusy);
        Assert.True(model.IsMutating);
        AssertUnchangedUserData(states);
        Assert.Single(context.Handler.Requests.Skip(requestStart), request => request.Is("/Users/user-a/Items/Resume"));
        removal.Return(removalSucceeds ? RemovalResponse(target.Id!) : Failure());
        if (removalSucceeds) await hiding;
        else await Assert.ThrowsAsync<EmbyApiException>(() => hiding);
        await reading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        if (paging is not null) Assert.Equal(PageLoadMoreOutcome.Appended, await paging);
        Assert.Equal(context.Resume.Select(item => item.Id), model.Items.Select(item => item.Id));
        Assert.Equal(removalSucceeds ? 48 : 49, model.TotalItemsCount);
        if (removalSucceeds)
        {
            Assert.DoesNotContain(model.Items, item => item.Id == target.Id);
            AssertSameCards(original.Where(item => item.Id != target.Id).ToArray(), model.Items.Take(47).ToArray());
        }
        else
        {
            Assert.Same(card, Assert.Single(model.Items, item => item.Id == target.Id));
            AssertSameCards(original, model.Items.Take(48).ToArray());
        }
        var expectedOffsets = removalSucceeds ? new[] { refresh ? "0" : "48", refresh ? "0" : "47" }
            : new[] { "48" };
        Assert.Equal(expectedOffsets, context.Handler.Requests.Skip(requestStart)
            .Where(request => request.Is("/Users/user-a/Items/Resume"))
            .Select(request => request.Query["StartIndex"]));
        Assert.Single(context.Handler.Requests, request => request.Is("/HideFromResume"));
        AssertUnchangedUserData(states);
        Assert.False(model.HasMore);
        Assert.False(model.IsBusy);
        Assert.False(model.IsMutating);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Removing_an_unloaded_item_invalidates_the_cached_resume_total_without_shifting_its_loaded_offset()
    {
        using var context = await CreateAsync();
        context.Resume = Movies(100);
        var target = context.Resume[79] with { UserData = ResumeMovie().UserData };
        context.Resume[79] = target;
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var original = model.Items.ToArray();
        Assert.Equal(48, original.Length);
        Assert.Equal(100, model.TotalItemsCount);
        Assert.DoesNotContain(original, card => card.Id == target.Id);
        await context.OpenLibraryAsync([target]);
        var libraryCard = Assert.Single(model.Items);
        await model.ShowItemAsync(libraryCard, TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var states = CaptureUserData(libraryCard, detail);

        await model.RemoveFromContinueWatchingAsync(detail, TestContext.Current.CancellationToken);
        var restoreStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(libraryCard, Assert.Single(model.Items));
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        AssertSameCards(original, model.Items);
        Assert.Null(model.TotalItemsCount);
        Assert.True(model.HasMore);
        Assert.Equal(restoreStart, context.Handler.Requests.Length);

        context.Resume = context.Resume.Where(item => item.Id != target.Id).ToArray();
        var requestStart = context.Handler.Requests.Length;
        Assert.Equal(PageLoadMoreOutcome.Appended, await model.LoadMoreAsync(TestContext.Current.CancellationToken));
        Assert.Equal("48", Assert.Single(context.Handler.Requests.Skip(requestStart)).Query["StartIndex"]);
        Assert.Equal(96, model.Items.Count);
        Assert.Equal(99, model.TotalItemsCount);
        AssertUnchangedUserData(states);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_failed_removal_preserves_visible_and_cached_content_until_a_successful_retry()
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        context.Resume = [target, Movie("resume-survivor")];
        context.Latest = [target];
        var rejectRemoval = true;
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/HideFromResume") && rejectRemoval
            ? Failure() : ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        var homeRows = model.HomeRows.ToArray();
        var continueRow = Assert.Single(homeRows, row => row.Section == HomeSection.ContinueWatching);
        var homeCard = Assert.Single(continueRow.Items, card => card.Id == target.Id);
        var latestCard = Assert.Single(Assert.Single(homeRows,
            row => row.Section == HomeSection.RecentlyAdded).Items);
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var resumeCards = model.Items.ToArray();
        await model.ShowItemAsync(resumeCards[0], TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var states = CaptureUserData(homeCard, latestCard, resumeCards[0], detail);
        var requestStart = context.Handler.Requests.Length;

        await Assert.ThrowsAsync<EmbyApiException>(() =>
            model.RemoveFromContinueWatchingAsync(detail, TestContext.Current.CancellationToken));

        AssertRemovalRequest(Assert.Single(context.Handler.Requests.Skip(requestStart)), target.Id!);
        Assert.Same(detail, model.Detail);
        AssertUnchangedUserData(states);
        Assert.False(model.IsMutating);
        var restoreStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        AssertSameCards(resumeCards, model.Items);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(homeRows.Length, model.HomeRows.Count);
        for (var index = 0; index < homeRows.Length; index++) Assert.Same(homeRows[index], model.HomeRows[index]);
        Assert.Same(homeCard, Assert.Single(continueRow.Items, card => card.Id == target.Id));
        Assert.Equal(restoreStart, context.Handler.Requests.Length);

        rejectRemoval = false;
        await model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        var retryCard = Assert.Single(model.Items, card => card.Id == target.Id);
        await model.RemoveFromContinueWatchingAsync(retryCard, TestContext.Current.CancellationToken);
        Assert.Equal("resume-survivor", Assert.Single(model.Items).Id);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.True(model.IsHome);
        Assert.DoesNotContain(continueRow.Items, card => card.Id == target.Id);
        Assert.Same(latestCard, Assert.Single(Assert.Single(model.HomeRows,
            row => row.Section == HomeSection.RecentlyAdded).Items));
        AssertUnchangedUserData(states);
        Assert.Equal(2, context.Handler.Requests.Count(request => request.Is("/HideFromResume")));
        Assert.False(model.IsMutating);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task A_new_session_resets_removals_and_ignores_a_late_removal_owned_by_the_old_session()
    {
        using var context = await CreateAsync();
        var target = ResumeMovie();
        var lateTarget = target with { Id = "late-resume", Name = "Late removal" };
        context.Resume = [target, lateTarget];
        context.Handler.RespondAsync = request => Task.FromResult(ResumeResponse(context, request, target));
        var model = context.Model;
        await model.ShowHomeAsync(TestContext.Current.CancellationToken);
        var originalRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var originalCard = Assert.Single(originalRow.Items, card => card.Id == target.Id);
        var lateCard = Assert.Single(originalRow.Items, card => card.Id == lateTarget.Id);
        await model.RemoveFromContinueWatchingAsync(originalCard, TestContext.Current.CancellationToken);
        Assert.Same(lateCard, Assert.Single(originalRow.Items));
        using var removal = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/late-resume/HideFromResume")
            ? removal.RespondAsync(request) : Task.FromResult(ResumeResponse(context, request, target));
        var oldRemoval = model.RemoveFromContinueWatchingAsync(lateCard, TestContext.Current.CancellationToken);
        var oldRequest = await removal.WaitForRequestAsync();

        await model.SetSessionAsync(context.Api, "server-b", new UserDto { Id = "user-a" },
            TestContext.Current.CancellationToken);

        var freshRow = Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching);
        var freshCards = freshRow.Items.ToArray();
        Assert.Equal(new[] { target.Id, lateTarget.Id }, freshCards.Select(card => card.Id));
        Assert.NotSame(originalCard, freshCards[0]);
        Assert.True(oldRequest.CancellationToken.IsCancellationRequested);
        var states = CaptureUserData(freshCards);
        using var resume = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume")
            ? resume.RespondAsync(request) : Task.FromResult(ResumeResponse(context, request, target));
        var navigation = model.ShowHomeSectionAsync(HomeSection.ContinueWatching, TestContext.Current.CancellationToken);
        await resume.WaitForRequestAsync();
        removal.Return(RemovalResponse(lateTarget.Id!));
        var oldFailure = await Record.ExceptionAsync(() => oldRemoval);
        Assert.True(oldFailure is null or OperationCanceledException);
        AssertSameCards(freshCards, freshRow.Items);
        resume.Return(Page(context.Resume));
        await navigation;

        Assert.Equal(new[] { target.Id, lateTarget.Id }, model.Items.Select(card => card.Id));
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Same(freshRow, Assert.Single(model.HomeRows, row => row.Section == HomeSection.ContinueWatching));
        AssertSameCards(freshCards, freshRow.Items);
        AssertUnchangedUserData(states);
        Assert.False(model.IsMutating);
        Assert.False(model.HasError);
    }

    private static BaseItemDto ResumeMovie() => Movie("resume-a", "Resume candidate") with
    {
        RunTimeTicks = TimeSpan.FromHours(1).Ticks,
        UserData = new UserItemDataDto
        {
            PlaybackPositionTicks = TimeSpan.FromMinutes(10).Ticks,
            Played = false,
            IsFavorite = false
        }
    };

    private static HttpResponseMessage ResumeResponse(LibraryTestContext context, ObservedRequest request, BaseItemDto target)
    {
        if (request.Is("/HideFromResume")) return RemovalResponse(target.Id!);
        if (request.Is($"/Users/user-a/Items/{target.Id}")) return Item(target);
        if (!request.Is("/Users/user-a/Items/Resume")) return context.DefaultResponse(request);
        var offset = int.Parse(request.Query.GetValueOrDefault("StartIndex", "0"));
        var limit = int.Parse(request.Query.GetValueOrDefault("Limit", "48"));
        return Page(context.Resume.Skip(offset).Take(limit).ToArray(), context.Resume.Length);
    }

    private static HttpResponseMessage RemovalResponse(string id) => UserData(new UserItemDataDto
    {
        ItemId = id,
        PlaybackPositionTicks = 0,
        Played = true,
        IsFavorite = true
    });

    private static void AssertRemovalRequest(ObservedRequest request, string id)
    {
        Assert.True(request.Is($"/Users/user-a/Items/{id}/HideFromResume"));
        Assert.Equal("true", request.Query["Hide"]);
    }

    private static (MediaCardViewModel Card, UserItemDataDto Data)[] CaptureUserData(params MediaCardViewModel[] cards) =>
        cards.Select(card => (card, Assert.IsType<UserItemDataDto>(card.Item.UserData))).ToArray();

    private static void AssertUnchangedUserData(IEnumerable<(MediaCardViewModel Card, UserItemDataDto Data)> states)
    {
        foreach (var (card, data) in states)
        {
            Assert.Same(data, card.Item.UserData);
            Assert.False(card.IsPlayed);
            Assert.False(card.IsFavorite);
            Assert.Equal(data.PlaybackPositionTicks ?? 0, card.ResumeTicks);
        }
    }
}
