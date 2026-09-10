using System.Net;
using System.Net.Http.Headers;
using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class ArtworkSelectionTests
{
    [Fact]
    public async Task The_existing_overload_keeps_the_primary_poster_when_other_artwork_is_available()
    {
        using var fixture = new ArtworkHarness();
        var item = MovieWithArtwork();

        var bytes = await fixture.Cache.GetAsync(fixture.Api, "server-a", "user-a", item, 176, 264,
            TestContext.Current.CancellationToken);

        Assert.NotNull(bytes);
        Assert.Equal("/emby/Items/movie-a/Images/Primary", Assert.Single(fixture.Handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task A_landscape_movie_card_uses_its_thumb_instead_of_cropping_the_portrait_poster()
    {
        using var fixture = new ArtworkHarness();

        Assert.NotNull(await fixture.GetAsync(MovieWithArtwork(), ArtworkKind.Landscape));

        Assert.Equal("/emby/Items/movie-a/Images/Thumb", Assert.Single(fixture.Handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task An_episode_card_prefers_its_own_primary_still_over_series_artwork()
    {
        using var fixture = new ArtworkHarness();
        var episode = MovieWithArtwork() with
        {
            Id = "episode-a",
            Type = "Episode",
            PrimaryImageAspectRatio = null
        };

        Assert.NotNull(await fixture.GetAsync(episode, ArtworkKind.Landscape));

        Assert.Equal("/emby/Items/episode-a/Images/Primary", Assert.Single(fixture.Handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task A_wide_primary_image_is_used_for_landscape_cards_without_an_episode_type()
    {
        using var fixture = new ArtworkHarness();
        var item = MovieWithArtwork() with { PrimaryImageAspectRatio = 16d / 9 };

        Assert.NotNull(await fixture.GetAsync(item, ArtworkKind.Landscape));

        Assert.Equal("/emby/Items/movie-a/Images/Primary", Assert.Single(fixture.Handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task A_parent_backdrop_preserves_the_first_usable_tag_index_and_takes_priority_over_a_thumb()
    {
        using var fixture = new ArtworkHarness();
        var item = MovieWithArtwork() with
        {
            BackdropImageTags = null,
            ParentBackdropItemId = "series-a",
            ParentBackdropImageTags = [string.Empty, "series-backdrop-v2"]
        };

        Assert.NotNull(await fixture.GetAsync(item, ArtworkKind.Backdrop));

        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("/emby/Items/series-a/Images/Backdrop/1", request.AbsolutePath);
        Assert.Contains("Tag=series-backdrop-v2", request.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backdrop_request_with_only_a_portrait_poster_returns_no_artwork_without_HTTP()
    {
        using var fixture = new ArtworkHarness();
        var item = new BaseItemDto
        {
            Id = "portrait-only",
            Type = "Movie",
            PrimaryImageAspectRatio = 2d / 3,
            ImageTags = new Dictionary<string, string> { ["Primary"] = "poster-v1" }
        };

        Assert.Null(await fixture.GetAsync(item, ArtworkKind.Backdrop));

        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task A_backdrop_request_can_use_a_thumb_when_no_backdrop_is_available()
    {
        using var fixture = new ArtworkHarness();
        var item = MovieWithArtwork() with { BackdropImageTags = null };

        Assert.NotNull(await fixture.GetAsync(item, ArtworkKind.Backdrop));

        Assert.Equal("/emby/Items/movie-a/Images/Thumb", Assert.Single(fixture.Handler.Requests).AbsolutePath);
    }

    [Fact]
    public async Task Equal_sized_poster_landscape_and_backdrop_requests_keep_their_selected_images_separate()
    {
        using var fixture = new ArtworkHarness();
        var item = MovieWithArtwork();

        var poster = await fixture.GetAsync(item, ArtworkKind.Poster);
        var landscape = await fixture.GetAsync(item, ArtworkKind.Landscape);
        var backdrop = await fixture.GetAsync(item, ArtworkKind.Backdrop);

        Assert.NotNull(poster);
        Assert.NotNull(landscape);
        Assert.NotNull(backdrop);
        Assert.NotSame(poster, landscape);
        Assert.NotSame(poster, backdrop);
        Assert.NotSame(landscape, backdrop);
        Assert.Same(poster, await fixture.GetAsync(item, ArtworkKind.Poster));
        Assert.Same(landscape, await fixture.GetAsync(item, ArtworkKind.Landscape));
        Assert.Same(backdrop, await fixture.GetAsync(item, ArtworkKind.Backdrop));
        Assert.Equal(3, fixture.Handler.Requests.Count);
        Assert.Equal(
            ["/emby/Items/movie-a/Images/Primary", "/emby/Items/movie-a/Images/Thumb", "/emby/Items/movie-a/Images/Backdrop/0"],
            fixture.Handler.Requests.Select(request => request.AbsolutePath));
    }

    private static BaseItemDto MovieWithArtwork() => new()
    {
        Id = "movie-a",
        Type = "Movie",
        PrimaryImageAspectRatio = 2d / 3,
        ImageTags = new Dictionary<string, string> { ["Primary"] = "poster-v1", ["Thumb"] = "thumb-v1" },
        BackdropImageTags = ["backdrop-v1"],
        ParentThumbItemId = "library-a",
        ParentThumbImageTag = "library-thumb-v1"
    };

    private sealed class ArtworkHarness : IDisposable
    {
        private readonly HttpClient _http;
        public ImageCache Cache { get; } = new();
        public ArtworkHandler Handler { get; } = new();
        public EmbyApiClient Api { get; }

        public ArtworkHarness()
        {
            _http = new HttpClient(Handler);
            Api = new EmbyApiClient(_http, new Uri("https://synthetic.example/emby/"),
                new ClientIdentity("Synthetic artwork tests", "Windows", "synthetic-device", "0.1"),
                "synthetic-token", "user-a") { RequestTimeout = TimeSpan.FromSeconds(15) };
        }

        public Task<byte[]?> GetAsync(BaseItemDto item, ArtworkKind kind) =>
            Cache.GetAsync(Api, "server-a", "user-a", item, 320, 180, kind, TestContext.Current.CancellationToken);

        public void Dispose() => _http.Dispose();
    }

    private sealed class ArtworkHandler : HttpMessageHandler
    {
        private static readonly byte[] Png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jfUYAAAAASUVORK5CYII=");
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(HttpMethod.Get, request.Method);
            Requests.Add(request.RequestUri!);
            var content = new ByteArrayContent(Png.ToArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
