using EmbyClient.Api;
using EmbyClient.App.ViewModels;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text;
using Xunit;
using static EmbyClient.AppState.Tests.LibraryTestContext;

namespace EmbyClient.AppState.Tests;

public sealed class LibrarySeriesEpisodeCountTests
{
    [Fact]
    public async Task The_series_aggregate_covers_every_season_and_survives_season_selection()
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;

        Assert.Equal(2, model.Detail.Item.ChildCount);
        Assert.All(model.Seasons, season => Assert.Null(season.Item.ChildCount));
        Assert.Equal(2, model.Items.Count);
        Assert.Equal(2, model.TotalItemsCount);
        Assert.Equal(3, model.SeriesEpisodeCount);
        var aggregate = Assert.Single(context.Handler.Requests, IsSeriesCountRequest);
        Assert.Equal("user-a", aggregate.Query["UserId"]);
        Assert.Equal("false", aggregate.Query["EnableImages"]);
        Assert.Equal("false", aggregate.Query["EnableUserData"]);
        var requestStart = context.Handler.Requests.Length;

        await model.SelectSeasonAsync(Assert.Single(model.Seasons, season => season.Id == "season-2"),
            TestContext.Current.CancellationToken);

        Assert.Equal("episode-2-1", Assert.Single(model.Items).Id);
        Assert.Equal(1, model.TotalItemsCount);
        Assert.Equal(3, model.SeriesEpisodeCount);
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), IsSeriesCountRequest);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(0, true, 0)]
    [InlineData(null, false, null)]
    [InlineData(null, true, null)]
    [InlineData(-7, true, null)]
    public async Task A_metadata_refresh_preserves_zero_and_clears_unknown_totals_without_inventing_a_count(
        int? reportedTotal, bool includeTotal, int? expectedCount)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var original = model.Items.ToArray();
        context.Handler.RespondAsync = request => Task.FromResult(IsSeriesCountRequest(request)
            ? CountResponse(reportedTotal, includeTotal) : SeriesResponse(context, request));

        await model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expectedCount, model.SeriesEpisodeCount);
        Assert.Equal(2, model.Detail.Item.ChildCount);
        Assert.Equal(2, model.TotalItemsCount);
        AssertSameCards(original, model.Items);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_pending_aggregate_starts_after_content_and_does_not_block_playback_or_season_selection(bool refresh)
    {
        using var context = await CreateAsync();
        var model = context.Model;
        if (refresh)
        {
            ConfigureSeriesResponses(context);
            await ShowSeriesWithCountAsync(context, 3);
        }
        using var episodes = new RequestGate();
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : request.Is("/Shows/series-a/Episodes")
                && request.Query.GetValueOrDefault("SeasonId") == "season-1"
                ? episodes.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));
        var requestStart = context.Handler.Requests.Length;

        var loading = refresh ? model.RefreshAsync(TestContext.Current.CancellationToken)
            : model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        await episodes.WaitForRequestAsync();
        Assert.DoesNotContain(context.Handler.Requests.Skip(requestStart), IsSeriesCountRequest);
        episodes.Return(Page([Episode(1, 1) with { Name = "Updated episode 1" }, Episode(1, 2)]));
        await CompleteAsync(loading);
        await aggregate.WaitForRequestAsync();

        Assert.Equal("Updated episode 1", model.Items[0].Title);
        Assert.Equal(2, model.Items.Count);
        Assert.Null(model.SeriesEpisodeCount);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.True(model.CanSelectSeason);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
        var playable = await model.WaitForPlaybackRecommendationAsync(model.Detail.Id, model.NavigationRevision,
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal("episode-1-1", playable?.Id);

        await model.SelectSeasonAsync(Assert.Single(model.Seasons, season => season.Id == "season-2"),
            TestContext.Current.CancellationToken);
        Assert.Equal("episode-2-1", Assert.Single(model.Items).Id);
        Assert.Null(model.SeriesEpisodeCount);
        Assert.False(model.IsBusy);
        using var applied = new CountChange(model, 3);
        aggregate.Return(CountResponse(3));
        await applied.WaitAsync();

        Assert.Equal(3, model.SeriesEpisodeCount);
        Assert.Equal("season-2", model.SelectedSeason?.Id);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("transport")]
    [InlineData("protocol")]
    public async Task An_optional_count_failure_clears_the_previous_aggregate_and_preserves_usable_content(string failure)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var detail = model.Detail;
        var playable = model.PlayableDetail;
        var original = model.Items.ToArray();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? FailedCountResponse(failure) : Task.FromResult(SeriesResponse(context, request));

        await model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.SeriesEpisodeCount);
        Assert.Same(detail, model.Detail);
        Assert.Same(playable, model.PlayableDetail);
        AssertSameCards(original, model.Items);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.True(model.CanSelectSeason);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
        Assert.Empty(model.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1, "Your session has expired. Sign out and connect again.")]
    [InlineData(HttpStatusCode.Forbidden, 0, "This account is not allowed to access this content.")]
    public async Task A_current_count_access_failure_uses_the_existing_session_and_permission_errors(
        HttpStatusCode status, int expectedExpired, string expectedMessage)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var original = model.Items.ToArray();
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;
        context.Handler.RespondAsync = request => Task.FromResult(IsSeriesCountRequest(request)
            ? Json("{}", status) : SeriesResponse(context, request));

        await model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.SeriesEpisodeCount);
        AssertSameCards(original, model.Items);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.True(model.HasError);
        Assert.Equal(expectedMessage, model.ErrorMessage);
        Assert.Equal(expectedExpired, expired);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Caller_cancellation_discards_a_delayed_aggregate_and_keeps_the_page_available_for_retry()
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var original = model.Items.ToArray();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));

        var reading = model.RefreshSeriesEpisodeCountAsync(cancellation.Token);
        var request = await aggregate.WaitForRequestAsync();
        Assert.Null(model.SeriesEpisodeCount);
        Assert.False(model.IsBusy);
        cancellation.Cancel();
        Assert.True(request.CancellationToken.IsCancellationRequested);
        aggregate.Return(CountResponse(99));
        await CompleteAsync(reading);

        Assert.Null(model.SeriesEpisodeCount);
        AssertSameCards(original, model.Items);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.False(model.HasError);
        ConfigureSeriesResponses(context, 5);
        await model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, model.SeriesEpisodeCount);
        Assert.True(model.CanSelectSeason);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task A_precancelled_metadata_refresh_preserves_the_known_aggregate_without_a_request()
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var requestStart = context.Handler.Requests.Length;
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        canceled.Cancel();

        await model.RefreshSeriesEpisodeCountAsync(canceled.Token);

        Assert.Equal(3, model.SeriesEpisodeCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.False(model.IsBusy);
        Assert.False(model.HasError);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_departed_count_response_cannot_change_a_new_detail_or_report_an_obsolete_access_error(
        HttpStatusCode obsoleteStatus)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));
        var reading = model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        var request = await aggregate.WaitForRequestAsync();

        await model.ShowItemAsync(new(Movie("movie-a")), TestContext.Current.CancellationToken);
        var detail = model.Detail;
        Assert.Null(model.SeriesEpisodeCount);
        Assert.True(request.CancellationToken.IsCancellationRequested);
        aggregate.Return(ObsoleteCountResponse(obsoleteStatus));
        await CompleteAsync(reading);

        Assert.Same(detail, model.Detail);
        Assert.Equal("movie-a", model.Detail.Id);
        Assert.Null(model.SeriesEpisodeCount);
        Assert.Equal(0, expired);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Replacing_the_user_ignores_the_old_aggregate_even_when_the_same_series_is_open_again(
        HttpStatusCode obsoleteStatus)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));
        var reading = model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        var oldRequest = await aggregate.WaitForRequestAsync();
        ConfigureSeriesResponses(context, 5, "user-b");

        await model.SetSessionAsync(context.Api.WithAuthentication("token-b", "user-b"), "server-b",
            new UserDto { Id = "user-b" }, TestContext.Current.CancellationToken);
        Assert.Null(model.SeriesEpisodeCount);
        var requestStart = context.Handler.Requests.Length;
        await ShowSeriesWithCountAsync(context, 5);
        var current = Assert.Single(context.Handler.Requests.Skip(requestStart), IsSeriesCountRequest);
        Assert.Equal("user-b", current.Query["UserId"]);
        Assert.True(oldRequest.CancellationToken.IsCancellationRequested);
        aggregate.Return(ObsoleteCountResponse(obsoleteStatus));
        await CompleteAsync(reading);

        Assert.Equal("series-a", model.Detail.Id);
        Assert.Equal(5, model.SeriesEpisodeCount);
        Assert.Equal(0, expired);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Clearing_the_session_resets_the_aggregate_and_rejects_a_late_count()
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));
        var reading = model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        var request = await aggregate.WaitForRequestAsync();

        model.ClearSession();
        Assert.Null(model.SeriesEpisodeCount);
        Assert.True(request.CancellationToken.IsCancellationRequested);
        aggregate.Return(CountResponse(99));
        await CompleteAsync(reading);
        var requestStart = context.Handler.Requests.Length;
        await model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);

        Assert.Null(model.SeriesEpisodeCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.Empty(model.Items);
        Assert.False(model.HasDetails);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task History_restores_a_known_aggregate_with_its_selected_season_without_refetching(int count)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context, count);
        await ShowSeriesWithCountAsync(context, count);
        var model = context.Model;
        await model.SelectSeasonAsync(Assert.Single(model.Seasons, season => season.Id == "season-2"),
            TestContext.Current.CancellationToken);
        var detail = model.Detail;
        var selected = model.SelectedSeason;
        var original = model.Items.ToArray();
        await model.ShowItemAsync(new(Movie("movie-a")), TestContext.Current.CancellationToken);
        Assert.Null(model.SeriesEpisodeCount);
        var requestStart = context.Handler.Requests.Length;

        await model.GoBackAsync(TestContext.Current.CancellationToken);

        Assert.Equal(count, model.SeriesEpisodeCount);
        Assert.Same(detail, model.Detail);
        Assert.Same(selected, model.SelectedSeason);
        AssertSameCards(original, model.Items);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.True(model.CanSelectSeason);
        Assert.False(model.IsBusy);
        await model.GoForwardAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.SeriesEpisodeCount);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(count, model.SeriesEpisodeCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
    }

    [Fact]
    public async Task Restoring_an_unknown_aggregate_starts_one_nonblocking_read_after_the_snapshot_is_ready()
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        context.Handler.RespondAsync = request => Task.FromResult(IsSeriesCountRequest(request)
            ? CountResponse(null, includeTotal: false) : SeriesResponse(context, request));
        await model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.SeriesEpisodeCount);
        var detail = model.Detail;
        var original = model.Items.ToArray();
        await model.ShowItemAsync(new(Movie("movie-a")), TestContext.Current.CancellationToken);
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));
        var requestStart = context.Handler.Requests.Length;

        await CompleteAsync(model.GoBackAsync(TestContext.Current.CancellationToken));
        await aggregate.WaitForRequestAsync();

        Assert.Same(detail, model.Detail);
        AssertSameCards(original, model.Items);
        Assert.Null(model.SeriesEpisodeCount);
        Assert.Equal(PageLoadOutcome.Succeeded, model.LoadOutcome);
        Assert.True(model.CanSelectSeason);
        Assert.True(model.CanUseSeasonActions);
        Assert.False(model.IsBusy);
        Assert.Single(context.Handler.Requests.Skip(requestStart), IsSeriesCountRequest);
        using var applied = new CountChange(model, 3);
        aggregate.Return(CountResponse(3));
        await applied.WaitAsync();
        Assert.Equal(3, model.SeriesEpisodeCount);
        AssertSameCards(original, model.Items);
    }

    [Fact]
    public async Task A_late_read_for_a_reopened_series_cannot_overwrite_an_older_known_history_snapshot()
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var detail = model.Detail;
        var original = model.Items.ToArray();
        await model.ShowItemAsync(new(Movie("movie-a")), TestContext.Current.CancellationToken);
        await ShowSeriesWithCountAsync(context, 3);
        using var aggregate = new RequestGate();
        context.Handler.RespondAsync = request => IsSeriesCountRequest(request)
            ? aggregate.RespondAsync(request) : Task.FromResult(SeriesResponse(context, request));
        var reading = model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        var request = await aggregate.WaitForRequestAsync();
        var requestStart = context.Handler.Requests.Length;

        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Null(model.SeriesEpisodeCount);
        await model.GoBackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, model.SeriesEpisodeCount);
        Assert.Equal(requestStart, context.Handler.Requests.Length);
        Assert.Same(detail, model.Detail);
        Assert.True(request.CancellationToken.IsCancellationRequested);
        aggregate.Return(CountResponse(99));
        await CompleteAsync(reading);

        Assert.Equal(3, model.SeriesEpisodeCount);
        Assert.Same(detail, model.Detail);
        AssertSameCards(original, model.Items);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(7, HttpStatusCode.OK)]
    [InlineData(null, HttpStatusCode.OK)]
    [InlineData(7, HttpStatusCode.Unauthorized)]
    [InlineData(7, HttpStatusCode.Forbidden)]
    public async Task The_latest_metadata_read_owns_the_aggregate_and_ignores_older_values_or_access_errors(
        int? latestCount, HttpStatusCode obsoleteStatus)
    {
        using var context = await CreateAsync();
        ConfigureSeriesResponses(context);
        await ShowSeriesWithCountAsync(context, 3);
        var model = context.Model;
        var original = model.Items.ToArray();
        var expired = 0;
        model.SessionExpired += (_, _) => expired++;
        using var obsolete = new RequestGate();
        using var latest = new RequestGate();
        var readCount = 0;
        context.Handler.RespondAsync = request => !IsSeriesCountRequest(request)
            ? Task.FromResult(SeriesResponse(context, request)) : Interlocked.Increment(ref readCount) switch
            {
                1 => obsolete.RespondAsync(request),
                2 => latest.RespondAsync(request),
                _ => throw new InvalidOperationException("An unexpected aggregate read was started.")
            };

        var olderRead = model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        await obsolete.WaitForRequestAsync();
        var newerRead = model.RefreshSeriesEpisodeCountAsync(TestContext.Current.CancellationToken);
        await latest.WaitForRequestAsync();
        Assert.Null(model.SeriesEpisodeCount);
        Assert.False(model.IsBusy);
        latest.Return(CountResponse(latestCount));
        await CompleteAsync(newerRead);
        Assert.Equal(latestCount, model.SeriesEpisodeCount);
        obsolete.Return(ObsoleteCountResponse(obsoleteStatus));
        await CompleteAsync(olderRead);

        Assert.Equal(latestCount, model.SeriesEpisodeCount);
        AssertSameCards(original, model.Items);
        Assert.Equal(2, readCount);
        Assert.Equal(0, expired);
        Assert.False(model.HasError);
        Assert.False(model.IsBusy);
    }

    private static void ConfigureSeriesResponses(LibraryTestContext context, int count = 3, string userId = "user-a") =>
        context.Handler.RespondAsync = request => Task.FromResult(SeriesResponse(context, request, count, userId));

    private static async Task ShowSeriesWithCountAsync(LibraryTestContext context, int expectedCount)
    {
        using var applied = new CountChange(context.Model, expectedCount);
        await context.Model.ShowItemAsync(new(Series()), TestContext.Current.CancellationToken);
        await applied.WaitAsync();
        Assert.False(context.Model.HasError);
    }

    private static HttpResponseMessage SeriesResponse(LibraryTestContext context, ObservedRequest request,
        int count = 3, string userId = "user-a")
    {
        if (IsSeriesCountRequest(request)) return CountResponse(count);
        if (request.Is($"/Users/{userId}/Items/series-a")) return Item(Series());
        if (request.Is("/Shows/series-a/Seasons")) return Page([Season(1), Season(2)]);
        if (request.Is("/Shows/series-a/Episodes"))
            return request.Query.GetValueOrDefault("SeasonId") == "season-2"
                ? Page([Episode(2, 1)]) : Page([Episode(1, 1), Episode(1, 2)]);
        if (request.Is($"/Users/{userId}/Items/movie-a")) return Item(Movie("movie-a"));
        if (request.Is($"/Users/{userId}/Views")) return Page(context.Views);
        if (request.Is($"/Users/{userId}/Items/Resume")) return Page(context.Resume);
        if (request.Is("/Shows/NextUp") && request.Query.GetValueOrDefault("UserId") == userId) return Page(context.NextUp);
        if (request.Is($"/Users/{userId}/Items/Latest")) return LibraryTestContext.Array(context.Latest);
        return context.DefaultResponse(request);
    }

    private static bool IsSeriesCountRequest(ObservedRequest request) => request.Is("/Shows/series-a/Episodes")
        && !request.Query.ContainsKey("SeasonId") && request.Query.GetValueOrDefault("Limit") == "1";

    private static HttpResponseMessage CountResponse(int? total, bool includeTotal = true)
    {
        var totalMember = includeTotal
            ? $",\"TotalRecordCount\":{total?.ToString(CultureInfo.InvariantCulture) ?? "null"}" : string.Empty;
        return Json($"{{\"Items\":[{{\"Id\":\"count-only\",\"Type\":\"Episode\"}}]{totalMember}}}");
    }

    private static Task<HttpResponseMessage> FailedCountResponse(string failure) => failure switch
    {
        "unavailable" => Task.FromResult(Failure()),
        "transport" => Task.FromException<HttpResponseMessage>(new HttpRequestException("The optional count read failed.")),
        "protocol" => Task.FromResult(Json("{")),
        _ => throw new ArgumentException("Unknown count failure.", nameof(failure))
    };

    private static HttpResponseMessage ObsoleteCountResponse(HttpStatusCode status) =>
        status == HttpStatusCode.OK ? CountResponse(99) : Json("{}", status);

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Task CompleteAsync(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private static BaseItemDto Series() => new()
    {
        Id = "series-a", Name = "Test Series", Type = "Series", IsFolder = true, ChildCount = 2
    };

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

    private sealed class CountChange : IDisposable
    {
        private readonly LibraryViewModel _model;
        private readonly int _expectedCount;
        private readonly TaskCompletionSource _applied = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CountChange(LibraryViewModel model, int expectedCount)
        {
            _model = model;
            _expectedCount = expectedCount;
            _model.PropertyChanged += OnPropertyChanged;
        }

        public Task WaitAsync() => CompleteAsync(_applied.Task);

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs change)
        {
            if (change.PropertyName == nameof(_model.SeriesEpisodeCount) && _model.SeriesEpisodeCount == _expectedCount)
                _applied.TrySetResult();
        }

        public void Dispose() => _model.PropertyChanged -= OnPropertyChanged;
    }
}
