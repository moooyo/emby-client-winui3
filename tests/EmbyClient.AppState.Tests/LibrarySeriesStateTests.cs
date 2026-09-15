using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibrarySeriesStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Loaded_episodes_enable_season_selection_while_optional_recommendations_are_pending(bool refresh)
    {
        using var context = await CreateAsync();
        var model = context.Model;
        if (refresh)
        {
            context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request));
            await model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "Episode 1", "Episode 2" }, model.Items.Select(item => item.Title));
        }
        using var episodes = new RequestGate();
        using var resume = new RequestGate();
        using var nextUp = new RequestGate();
        context.Handler.RespondAsync = request =>
        {
            if (request.Is("/Shows/series-a/Episodes")) return episodes.RespondAsync(request);
            if (request.Is("/Users/user-a/Items/Resume")) return resume.RespondAsync(request);
            if (request.Is("/Shows/NextUp")) return nextUp.RespondAsync(request);
            return Task.FromResult(SeriesResponse(context, request));
        };
        var loading = refresh ? model.RefreshAsync(TestContext.Current.CancellationToken)
            : model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        var episodeRequest = await episodes.WaitForRequestAsync();
        await resume.WaitForRequestAsync();
        await nextUp.WaitForRequestAsync();
        Assert.Equal("season-1", episodeRequest.Query["SeasonId"]);
        Assert.False(model.CanSelectSeason);
        Assert.False(model.CanUseSeasonActions);

        episodes.Return(Page([
            Episode(1, 1) with { Name = "Updated episode 1" },
            Episode(1, 2) with { Name = "Updated episode 2" }
        ]));
        await loading.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "episode-1-1", "episode-1-2" }, model.Items.Select(item => item.Id));
        Assert.Equal(new[] { "Updated episode 1", "Updated episode 2" }, model.Items.Select(item => item.Title));
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.Equal("episode-1-1", model.PlayableDetail.Id);
        Assert.True(model.CanSelectSeason);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);

        var recommendationApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(model.PlayableDetail) && model.PlayableDetail.Id == "episode-1-2")
                recommendationApplied.TrySetResult();
        };
        resume.Return(Page([Episode(1, 2) with
        {
            UserData = new UserItemDataDto { PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks }
        }]));
        nextUp.Return(Page([]));
        await recommendationApplied.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(model.PlayableDetail.CanResume);
        Assert.True(model.CanSelectSeason);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_failed_or_canceled_season_switch_keeps_the_previous_episodes_and_can_retry(bool cancel)
    {
        using var context = await CreateAsync();
        context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request));
        var model = context.Model;
        await model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        var original = model.Items.ToArray();
        var selected = model.SelectedSeason;
        var playable = model.PlayableDetail;
        var nextSeason = Assert.Single(model.Seasons, season => season.Id == "season-2");
        using var episodes = new RequestGate();
        context.Handler.RespondAsync = request => request.Is("/Shows/series-a/Episodes")
            && request.Query.GetValueOrDefault("SeasonId") == "season-2"
                ? episodes.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));

        var switching = model.SelectSeasonAsync(nextSeason, TestContext.Current.CancellationToken);
        var request = await episodes.WaitForRequestAsync();
        Assert.Same(nextSeason, model.SelectedSeason);
        AssertSameCards(original, model.Items);
        Assert.Same(playable, model.PlayableDetail);
        Assert.Equal("Season 1", model.DetailChildrenTitle);
        Assert.True(model.IsSeasonSwitchPending);
        Assert.Equal(Visibility.Visible, model.SeasonSwitchVisibility);
        Assert.False(model.CanUseSeasonActions);
        Assert.True(model.CanSelectSeason);

        if (cancel)
        {
            model.CancelSeasonSwitch();
            Assert.True(request.CancellationToken.IsCancellationRequested);
            episodes.Return(Page([Episode(2, 1)]));
        }
        else episodes.Return(Failure());
        await switching;

        Assert.Same(selected, model.SelectedSeason);
        AssertSameCards(original, model.Items);
        Assert.Same(playable, model.PlayableDetail);
        Assert.Equal("Season 1", model.DetailChildrenTitle);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.IsSeasonSwitchPending);
        Assert.Equal(Visibility.Collapsed, model.SeasonSwitchVisibility);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
        Assert.Equal(!cancel, model.HasError);

        context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request));
        await model.SelectSeasonAsync(nextSeason, TestContext.Current.CancellationToken);
        Assert.Same(nextSeason, model.SelectedSeason);
        Assert.Equal("episode-2-1", Assert.Single(model.Items).Id);
        Assert.Equal("episode-2-1", model.PlayableDetail.Id);
        Assert.Equal("Season 2", model.DetailChildrenTitle);
        Assert.False(model.IsSeasonSwitchPending);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.HasError);
    }

    [Fact]
    public async Task Back_from_an_episode_restores_the_selected_season_and_detail_viewport()
    {
        using var context = await CreateAsync();
        context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request));
        var model = context.Model;
        await model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        await model.SelectSeasonAsync(Assert.Single(model.Seasons, season => season.Id == "season-2"),
            TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var selected = model.SelectedSeason;
        var seasons = model.Seasons.ToArray();
        var episodes = model.Items.ToArray();
        var playable = model.PlayableDetail;
        var viewport = new BrowseViewportState(0, 720, 280, episodes[0].Id, 0, episodes[0].Id,
            OverviewExpanded: true, EpisodeList: true);
        model.ViewportState = viewport;

        await model.ShowItemAsync(episodes[0], TestContext.Current.CancellationToken);
        var requestStart = context.Handler.Requests.Length;
        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.Same(detail, model.Detail);
        Assert.Same(selected, model.SelectedSeason);
        Assert.Same(playable, model.PlayableDetail);
        AssertSameCards(seasons, model.Seasons);
        AssertSameCards(episodes, model.Items);
        Assert.Equal(viewport, model.ViewportState);
        Assert.Equal("Season 2", model.DetailChildrenTitle);
        Assert.True(model.DetailItemsAreEpisodes);
        Assert.True(model.CanSelectSeason);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
    }

    private static HttpResponseMessage SeriesResponse(LibraryTestContext context, ObservedRequest request)
    {
        if (request.Is("/Users/user-a/Items/series-a")) return Item(Series());
        if (request.Is("/Shows/series-a/Seasons")) return Page([Season(1), Season(2)]);
        if (request.Is("/Shows/series-a/Episodes"))
            return request.Query.GetValueOrDefault("SeasonId") == "season-2"
                ? Page([Episode(2, 1)]) : Page([Episode(1, 1), Episode(1, 2)]);
        if (request.Is("/Users/user-a/Items/episode-2-1")) return Item(Episode(2, 1));
        return context.DefaultResponse(request);
    }

    private static BaseItemDto Series() =>
        new() { Id = "series-a", Name = "Test Series", Type = "Series", IsFolder = true };

    private static BaseItemDto Season(int number) => new()
    {
        Id = $"season-{number}", Name = $"Season {number}", Type = "Season", IsFolder = true,
        SeriesId = "series-a", SeriesName = "Test Series", IndexNumber = number
    };

    private static BaseItemDto Episode(int season, int number) => new()
    {
        Id = $"episode-{season}-{number}", Name = $"Episode {number}", Type = "Episode", MediaType = "Video",
        SeriesId = "series-a", SeriesName = "Test Series", SeasonId = $"season-{season}",
        ParentIndexNumber = season, IndexNumber = number, RunTimeTicks = TimeSpan.FromMinutes(30).Ticks
    };
}
