using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibraryPlaybackRecommendationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Quick_play_waits_for_cross_season_resume_or_next_up_without_duplicate_requests(bool resumePreferred)
    {
        using var context = await CreateAsync();
        using var resume = new RequestGate();
        using var next = new RequestGate();
        var requestStart = context.Handler.Requests.Length;
        await OpenPendingSeriesAsync(context, resume, next);
        var model = context.Model;
        Assert.Equal("episode-1-1", model.PlayableDetail.Id);
        var waiting = model.WaitForPlaybackRecommendationAsync("series-a", model.NavigationRevision,
            TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        var resumeRequest = await resume.WaitForRequestAsync();
        var nextRequest = await next.WaitForRequestAsync();
        Assert.Equal("series-a", resumeRequest.Query["ParentId"]);
        Assert.Equal("Episode", resumeRequest.Query["IncludeItemTypes"]);
        Assert.Equal("series-a", nextRequest.Query["SeriesId"]);
        var candidate = Episode(2, 5) with
        {
            UserData = new UserItemDataDto { PlaybackPositionTicks = resumePreferred ? TimeSpan.FromMinutes(5).Ticks : 0 }
        };
        resume.Return(Page(resumePreferred ? [candidate] : []));
        next.Return(Page([Episode(2, resumePreferred ? 6 : 5)]));
        var chosen = await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.NotNull(chosen);
        Assert.Equal("episode-2-5", chosen.Id);
        Assert.Equal(resumePreferred ? TimeSpan.FromMinutes(5).Ticks : 0, chosen.ResumeTicks);
        Assert.Equal(1, context.Handler.Requests.Skip(requestStart).Count(request => request.Is("/Users/user-a/Items/Resume")));
        Assert.Equal(1, context.Handler.Requests.Skip(requestStart).Count(request => request.Is("/Shows/NextUp")));
    }

    [Fact]
    public async Task Failed_optional_recommendations_use_the_loaded_season_fallback()
    {
        using var context = await CreateAsync();
        using var resume = new RequestGate();
        using var next = new RequestGate();
        await OpenPendingSeriesAsync(context, resume, next);
        var waiting = context.Model.WaitForPlaybackRecommendationAsync("series-a", context.Model.NavigationRevision,
            TestContext.Current.CancellationToken);
        resume.Return(Failure());
        next.Return(Failure());
        Assert.Equal("episode-1-1", (await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))?.Id);
        Assert.False(context.Model.HasError);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_late_recommendation_cannot_escape_its_page_session_or_season_owner(int transition)
    {
        using var context = await CreateAsync();
        using var resume = new RequestGate();
        using var next = new RequestGate();
        await OpenPendingSeriesAsync(context, resume, next);
        var model = context.Model;
        var waiting = model.WaitForPlaybackRecommendationAsync("series-a", model.NavigationRevision,
            TestContext.Current.CancellationToken);
        context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request));
        switch (transition)
        {
            case 0: await model.ShowLibraryAsync(Assert.Single(model.Libraries), TestContext.Current.CancellationToken); break;
            case 1: model.ClearSession(); break;
            case 2: await model.SelectSeasonAsync(Assert.Single(model.Seasons, season => season.Id == "season-2"), TestContext.Current.CancellationToken); break;
            case 3: await model.RefreshAsync(TestContext.Current.CancellationToken); break;
        }
        resume.Return(Page([Episode(2, 5) with
        {
            UserData = new UserItemDataDto { PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks }
        }]));
        next.Return(Page([]));
        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.NotEqual("episode-2-5", model.PlayableDetail.Id);
    }

    [Fact]
    public async Task Canceling_only_the_wait_does_not_cancel_the_details_recommendation()
    {
        using var context = await CreateAsync();
        using var resume = new RequestGate();
        using var next = new RequestGate();
        await OpenPendingSeriesAsync(context, resume, next);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var model = context.Model;
        var waiting = model.WaitForPlaybackRecommendationAsync("series-a", model.NavigationRevision, cancellation.Token);
        cancellation.Cancel();
        Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.False((await resume.WaitForRequestAsync()).CancellationToken.IsCancellationRequested);
        var continuing = model.WaitForPlaybackRecommendationAsync("series-a", model.NavigationRevision,
            TestContext.Current.CancellationToken);
        resume.Return(Page([Episode(2, 5) with
        {
            UserData = new UserItemDataDto { PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks }
        }]));
        next.Return(Page([]));
        Assert.Equal("episode-2-5", (await continuing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))?.Id);
    }

    [Fact]
    public async Task The_callers_original_navigation_revision_is_required()
    {
        using var context = await CreateAsync();
        context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request));
        var oldRevision = context.Model.NavigationRevision;
        await context.Model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        Assert.Null(await context.Model.WaitForPlaybackRecommendationAsync("series-a", oldRevision,
            TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Season_quick_play_never_uses_a_different_seasons_candidate(bool incorrectSeasonResponse)
    {
        using var context = await CreateAsync();
        context.Resume = [Episode(2, 5) with { UserData = new UserItemDataDto { PlaybackPositionTicks = 1000 } }];
        context.NextUp = [Episode(2, 6)];
        context.Handler.RespondAsync = request => Task.FromResult(incorrectSeasonResponse && request.Is("/Shows/series-a/Episodes")
            ? Page([Episode(2, 5)]) : SeriesResponse(context, request));
        var requestStart = context.Handler.Requests.Length;
        var model = context.Model;
        await model.ShowItemAsync(new(Season(1)), TestContext.Current.CancellationToken);
        var chosen = await model.WaitForPlaybackRecommendationAsync("season-1", model.NavigationRevision,
            TestContext.Current.CancellationToken);
        if (incorrectSeasonResponse) Assert.Null(chosen);
        else Assert.Equal("episode-1-1", chosen?.Id);
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), request => request.Is("/Users/user-a/Items/Resume")
            || request.Is("/Shows/NextUp"));
        Assert.Equal("season-1", Assert.Single(context.Handler.Requests.Skip(requestStart),
            request => request.Is("/Shows/series-a/Episodes")).Query["SeasonId"]);
    }

    [Theory]
    [InlineData("Movie")]
    [InlineData("Episode")]
    public async Task Explicit_playable_items_are_not_reinterpreted_as_recommendations(string type)
    {
        using var context = await CreateAsync();
        var explicitItem = type == "Episode" ? Episode(1, 1) : Movie("movie-a");
        explicitItem = explicitItem with { UserData = new UserItemDataDto { Played = true, PlaybackPositionTicks = 1000 } };
        context.Handler.RespondAsync = request => Task.FromResult(request.Is("/Users/user-a/Items/" + explicitItem.Id)
            ? Item(explicitItem) : SeriesResponse(context, request));
        var model = context.Model;
        await model.ShowItemAsync(new(explicitItem), TestContext.Current.CancellationToken);
        var requestCount = context.Handler.Requests.Length;
        Assert.Null(await model.WaitForPlaybackRecommendationAsync(explicitItem.Id!, model.NavigationRevision,
            TestContext.Current.CancellationToken));
        Assert.Equal(explicitItem.Id, model.PlayableDetail.Id);
        Assert.Equal(1000, model.PlayableDetail.ResumeTicks);
        Assert.Equal(requestCount, context.Handler.Requests.Length);
    }

    private static async Task OpenPendingSeriesAsync(LibraryTestContext context, RequestGate resume, RequestGate next)
    {
        context.Handler.RespondAsync = request => request.Is("/Users/user-a/Items/Resume") ? resume.RespondAsync(request)
            : request.Is("/Shows/NextUp") ? next.RespondAsync(request)
            : Task.FromResult(SeriesResponse(context, request));
        await context.Model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        await resume.WaitForRequestAsync();
        await next.WaitForRequestAsync();
        Assert.False(context.Model.IsBusy);
    }

    private static HttpResponseMessage SeriesResponse(LibraryTestContext context, ObservedRequest request)
    {
        if (request.Is("/Users/user-a/Items/series-a")) return Item(Series());
        if (request.Is("/Users/user-a/Items/season-1")) return Item(Season(1));
        if (request.Is("/Shows/series-a/Seasons")) return Page([Season(1), Season(2)]);
        if (request.Is("/Shows/series-a/Episodes"))
            return request.Query.GetValueOrDefault("SeasonId") == "season-2"
                ? Page([Episode(2, 1)]) : Page([Episode(1, 1), Episode(1, 2)]);
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
        ParentIndexNumber = season, IndexNumber = number, RunTimeTicks = TimeSpan.FromMinutes(30).Ticks,
        UserData = new UserItemDataDto { Played = season == 1 }
    };
}
