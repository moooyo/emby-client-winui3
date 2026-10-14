using System.Collections.Concurrent;
using System.Net;
using System.Text;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class EpisodeNotificationEligibilityTests
{
    private const string EmptyItems = """{"Items":[],"TotalRecordCount":0}""";
    private const string Series = """{"Id":"series-a","Type":"Series","UserData":{"IsFavorite":false}}""";
    private const string FavoriteSeries = """{"Id":"series-a","Type":"Series","UserData":{"IsFavorite":true}}""";
    private const string ResumingEpisode =
        """{"Id":"history-a","Type":"Episode","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":1,"Played":false}}""";
    private const string PlayedEpisode =
        """{"Id":"history-a","Type":"Episode","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":0,"Played":true}}""";

    [Theory]
    [InlineData("user-a", "synthetic-token-a")]
    [InlineData("user-b", "synthetic-token-b")]
    public async Task Watching_evidence_is_queried_only_for_the_current_authenticated_user(string userId, string accessToken)
    {
        using var context = new EligibilityContext(userId, accessToken);
        context.Handler.ResumeJson = Items(ResumingEpisode);

        Assert.True(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        var requests = context.Handler.Requests;
        Assert.Equal(3, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.StartsWith($"/emby/Users/{userId}/Items", request.Uri.AbsolutePath);
            Assert.Equal(accessToken, request.AccessToken);
            Assert.Contains($"UserId=\"{userId}\"", request.Authorization);
            Assert.DoesNotContain(accessToken, request.Uri.AbsoluteUri);
            Assert.DoesNotContain("api_key", request.Uri.Query, StringComparison.OrdinalIgnoreCase);
        });
        AssertHistoryScope(requests, userId);
        Assert.Null(context.Http.DefaultRequestHeaders.Authorization);
        Assert.False(context.Http.DefaultRequestHeaders.Contains("X-Emby-Token"));
    }

    [Fact]
    public async Task A_public_client_cannot_query_favorites_or_history_for_an_implicit_user()
    {
        using var context = new EligibilityContext();
        var publicClient = new EmbyApiClient(context.Http, context.Client.ApiRoot, context.Client.Identity);

        await Assert.ThrowsAsync<InvalidOperationException>(() => EpisodeNotificationEligibility.IsFollowedAsync(
            publicClient, NewEpisode(), TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Fact]
    public async Task A_favorite_series_qualifies_without_loading_watching_history()
    {
        using var context = new EligibilityContext();
        context.Handler.SeriesJson = FavoriteSeries;

        Assert.True(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        Assert.Equal("/emby/Users/user-a/Items/series-a", Assert.Single(context.Handler.Requests).Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_shared_transport_does_not_reuse_another_users_favorite_state_for_the_same_series()
    {
        using var context = new EligibilityContext();
        context.Handler.SeriesJson = FavoriteSeries;
        Assert.True(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));
        context.Handler.SeriesJson = Series;
        var secondUser = context.Client.WithAuthentication("synthetic-token-b", "user-b");

        Assert.False(await EpisodeNotificationEligibility.IsFollowedAsync(
            secondUser, NewEpisode(), TestContext.Current.CancellationToken));

        var firstRequest = Assert.Single(context.Handler.Requests.Where(
            request => request.Uri.AbsolutePath.StartsWith("/emby/Users/user-a/", StringComparison.Ordinal)));
        Assert.Equal("synthetic-token-a", firstRequest.AccessToken);
        var secondRequests = context.Handler.Requests.Where(
            request => request.Uri.AbsolutePath.StartsWith("/emby/Users/user-b/", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, secondRequests.Length);
        Assert.All(secondRequests, request =>
        {
            Assert.Equal("synthetic-token-b", request.AccessToken);
            Assert.Contains("UserId=\"user-b\"", request.Authorization);
        });
        AssertHistoryScope(secondRequests, "user-b");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Actual_resume_progress_or_a_played_episode_independently_qualifies_a_series(bool useResume)
    {
        using var context = new EligibilityContext();
        if (useResume) context.Handler.ResumeJson = Items(ResumingEpisode);
        else context.Handler.PlayedJson = Items(PlayedEpisode);

        Assert.True(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        AssertHistoryScope(context.Handler.Requests, "user-a");
    }

    [Theory]
    [InlineData("""{"Items":[],"TotalRecordCount":100}""")]
    [InlineData("""{"Items":[{"Id":"history-a","Type":"Episode","SeriesId":"series-a"}],"TotalRecordCount":1}""")]
    [InlineData("""{"Items":[{"Id":"history-a","Type":"Episode","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":0,"Played":false}}],"TotalRecordCount":1}""")]
    [InlineData("""{"Items":[{"Id":"history-a","Type":"Episode","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":-1,"Played":null}}],"TotalRecordCount":1}""")]
    public async Task Counts_and_items_without_actual_watching_evidence_do_not_qualify_a_series(string historyJson)
    {
        using var context = new EligibilityContext();
        context.Handler.ResumeJson = historyJson;
        context.Handler.PlayedJson = historyJson;
        context.Handler.NextUpJson = Items(ResumingEpisode);

        Assert.False(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        AssertHistoryScope(context.Handler.Requests, "user-a");
        Assert.DoesNotContain(context.Handler.Requests,
            request => request.Uri.AbsolutePath == "/emby/Shows/NextUp");
    }

    [Theory]
    [InlineData("""{"Id":"series-b","Type":"Series","UserData":{"IsFavorite":true}}""")]
    [InlineData("""{"Id":"series-a","Type":"Movie","UserData":{"IsFavorite":true}}""")]
    [InlineData("""{"Id":"series-a","UserData":{"IsFavorite":true}}""")]
    [InlineData("""{"Type":"Series","UserData":{"IsFavorite":true}}""")]
    public async Task A_foreign_or_unknown_series_response_cannot_use_the_favorite_fast_path(string seriesJson)
    {
        using var context = new EligibilityContext();
        context.Handler.SeriesJson = seriesJson;
        context.Handler.ResumeJson = Items(ResumingEpisode);
        context.Handler.PlayedJson = Items(PlayedEpisode);

        Assert.False(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        Assert.Single(context.Handler.Requests);
    }

    [Fact]
    public async Task Watching_evidence_explicitly_returned_for_another_series_cannot_qualify_this_episode()
    {
        using var context = new EligibilityContext();
        var foreignEpisode =
            """{"Id":"history-b","Type":"Episode","SeriesId":"series-b","UserData":{"PlaybackPositionTicks":2000,"Played":true}}""";
        context.Handler.ResumeJson = Items(foreignEpisode);
        context.Handler.PlayedJson = Items(foreignEpisode);

        Assert.False(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        AssertHistoryScope(context.Handler.Requests, "user-a");
    }

    [Theory]
    [InlineData("""{"Type":"Episode","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":1,"Played":true}}""")]
    [InlineData("""{"Id":"","Type":"Episode","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":1,"Played":true}}""")]
    [InlineData("""{"Id":"history-a","Type":"Movie","SeriesId":"series-a","UserData":{"PlaybackPositionTicks":1,"Played":true}}""")]
    public async Task Progress_flags_require_a_real_episode_identity(string itemJson)
    {
        using var context = new EligibilityContext();
        context.Handler.ResumeJson = Items(itemJson);
        context.Handler.PlayedJson = Items(itemJson);

        Assert.False(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Movie", "series-a")]
    [InlineData("Season", "series-a")]
    [InlineData("Episode", null)]
    [InlineData("Episode", " ")]
    public async Task An_item_without_an_episode_series_identity_does_not_make_account_queries(string type, string? seriesId)
    {
        using var context = new EligibilityContext();
        var item = NewEpisode() with { Type = type, SeriesId = seriesId };

        Assert.False(await EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, item, TestContext.Current.CancellationToken));

        Assert.Empty(context.Handler.Requests);
    }

    [Theory]
    [InlineData("series", HttpStatusCode.Forbidden)]
    [InlineData("series", HttpStatusCode.NotFound)]
    [InlineData("resume", HttpStatusCode.Forbidden)]
    [InlineData("played", HttpStatusCode.Forbidden)]
    public async Task Access_failures_remain_visible_to_the_shell_instead_of_becoming_notification_permission(
        string source, HttpStatusCode status)
    {
        using var context = new EligibilityContext();
        context.Handler.FailingSource = source;
        context.Handler.FailureStatus = status;
        context.Handler.ResumeJson = Items(ResumingEpisode);
        context.Handler.PlayedJson = Items(PlayedEpisode);

        var exception = await Assert.ThrowsAsync<EmbyApiException>(() => EpisodeNotificationEligibility.IsFollowedAsync(
            context.Client, NewEpisode(), TestContext.Current.CancellationToken));

        Assert.Equal(status, exception.StatusCode);
        if (source == "series") Assert.Single(context.Handler.Requests);
        Assert.DoesNotContain(context.Handler.Requests,
            request => request.Uri.AbsolutePath == "/emby/Shows/NextUp");
    }

    private static BaseItemDto NewEpisode() => new() { Id = "new-episode", Type = "Episode", SeriesId = "series-a" };
    private static string Items(string itemJson) => $$"""{"Items":[{{itemJson}}],"TotalRecordCount":1}""";

    private static void AssertHistoryScope(IReadOnlyList<RequestSnapshot> requests, string userId)
    {
        var resume = Assert.Single(requests.Where(request => request.Uri.AbsolutePath == $"/emby/Users/{userId}/Items/Resume"));
        var played = Assert.Single(requests.Where(request => request.Uri.AbsolutePath == $"/emby/Users/{userId}/Items"));
        Assert.All(new[] { resume, played }, request =>
        {
            var query = request.Query();
            Assert.Equal("series-a", query["ParentId"]);
            Assert.Equal("true", query["Recursive"]);
            Assert.Equal("Episode", query["IncludeItemTypes"]);
            Assert.Equal("true", query["EnableUserData"]);
            Assert.Equal("false", query["EnableImages"]);
        });
        Assert.Equal("Video", resume.Query()["MediaTypes"]);
        Assert.Equal("true", played.Query()["IsPlayed"]);
    }

    private sealed class EligibilityContext : IDisposable
    {
        public EligibilityContext(string userId = "user-a", string accessToken = "synthetic-token-a")
        {
            Http = new HttpClient(Handler);
            Client = new EmbyApiClient(Http, new Uri("https://synthetic.example/emby/"),
                new ClientIdentity("Synthetic eligibility tests", "Windows", "synthetic-device", "0.1"),
                accessToken, userId);
        }

        public EligibilityHandler Handler { get; } = new();
        public HttpClient Http { get; }
        public EmbyApiClient Client { get; }
        public void Dispose() => Http.Dispose();
    }

    private sealed class EligibilityHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<RequestSnapshot> _requests = new();
        public string SeriesJson { get; set; } = Series;
        public string ResumeJson { get; set; } = EmptyItems;
        public string PlayedJson { get; set; } = EmptyItems;
        public string NextUpJson { get; set; } = EmptyItems;
        public string? FailingSource { get; set; }
        public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.Forbidden;
        public RequestSnapshot[] Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            _requests.Enqueue(new(request.Method, uri,
                request.Headers.TryGetValues("X-Emby-Token", out var tokens) ? Assert.Single(tokens) : string.Empty,
                request.Headers.TryGetValues("X-Emby-Authorization", out var identities) ? Assert.Single(identities) : string.Empty));
            var source = uri.AbsolutePath switch
            {
                var path when path.EndsWith("/Items/series-a", StringComparison.Ordinal) => "series",
                var path when path.EndsWith("/Items/Resume", StringComparison.Ordinal) => "resume",
                var path when path.EndsWith("/Items", StringComparison.Ordinal) => "played",
                "/emby/Shows/NextUp" => "nextup",
                _ => throw new InvalidOperationException("Unexpected synthetic notification eligibility request.")
            };
            var body = source switch
            {
                "series" => SeriesJson,
                "resume" => ResumeJson,
                "played" => PlayedJson,
                _ => NextUpJson
            };
            return Task.FromResult(new HttpResponseMessage(source == FailingSource ? FailureStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record RequestSnapshot(HttpMethod Method, Uri Uri, string AccessToken, string Authorization)
    {
        public IReadOnlyDictionary<string, string> Query() => Uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split('=', 2))
            .ToDictionary(parts => System.Uri.UnescapeDataString(parts[0]),
                parts => parts.Length > 1 ? System.Uri.UnescapeDataString(parts[1]) : string.Empty, StringComparer.Ordinal);
    }
}
