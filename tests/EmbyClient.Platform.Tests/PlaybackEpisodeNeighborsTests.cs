using EmbyClient.Api;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PlaybackEpisodeNeighborsTests
{
    [Fact]
    public void Server_order_can_cross_seasons_and_place_specials_without_sorting_episode_numbers()
    {
        var previous = Episode("previous") with { ParentIndexNumber = 1, IndexNumber = 12 };
        var current = Episode("current") with { ParentIndexNumber = 2, IndexNumber = 1 };
        var next = Episode("special") with { ParentIndexNumber = 0, IndexNumber = 3 };

        var neighbors = PlaybackEpisodeNeighbors.Resolve(current, [previous, current, next]);

        Assert.Same(previous, neighbors.Previous);
        Assert.Same(next, neighbors.Next);
    }

    [Theory]
    [InlineData("Movie", "current", "series-a")]
    [InlineData("Episode", "unknown", "series-a")]
    [InlineData("Episode", null, "series-a")]
    [InlineData("Episode", " ", "series-a")]
    [InlineData("Episode", "current", null)]
    [InlineData("Episode", "current", " ")]
    public void A_non_episode_or_unidentified_current_item_has_no_neighbors(string type, string? id, string? seriesId)
    {
        var current = Episode(id, seriesId) with { Type = type };

        var neighbors = PlaybackEpisodeNeighbors.Resolve(current, [Episode("previous"), Episode("current"), Episode("next")]);

        Assert.Equal(new PlaybackEpisodeNeighbors(null, null), neighbors);
    }

    [Fact]
    public void Duplicate_current_ids_make_the_position_ambiguous_even_when_one_copy_is_invalid()
    {
        var current = Episode("current");
        var duplicate = current with { Type = "Movie", SeriesId = "series-b" };

        var neighbors = PlaybackEpisodeNeighbors.Resolve(current, [Episode("previous"), current, Episode("next"), duplicate]);

        Assert.Equal(new PlaybackEpisodeNeighbors(null, null), neighbors);
    }

    [Theory]
    [InlineData("Movie", "series-a")]
    [InlineData("Episode", "series-b")]
    public void An_invalid_server_row_for_the_current_item_disables_both_directions(string type, string seriesId)
    {
        var current = Episode("current");
        var returnedCurrent = current with { Type = type, SeriesId = seriesId };

        var neighbors = PlaybackEpisodeNeighbors.Resolve(current, [Episode("previous"), returnedCurrent, Episode("next")]);

        Assert.Equal(new PlaybackEpisodeNeighbors(null, null), neighbors);
    }

    [Theory]
    [InlineData("previous", "series-b", "Episode", false)]
    [InlineData(null, "series-a", "Episode", false)]
    [InlineData(" ", "series-a", "Episode", false)]
    [InlineData("previous", "series-a", "Movie", false)]
    [InlineData("previous", "series-a", "Episode", true)]
    public void An_invalid_immediate_neighbor_disables_only_its_direction_without_skipping_to_another_episode(
        string? id, string seriesId, string type, bool isFolder)
    {
        var current = Episode("current");
        var invalid = Episode(id, seriesId) with { Type = type, IsFolder = isFolder };
        var next = Episode("next");

        var neighbors = PlaybackEpisodeNeighbors.Resolve(current, [Episode("earlier"), invalid, current, next]);

        Assert.Null(neighbors.Previous);
        Assert.Same(next, neighbors.Next);
        neighbors = PlaybackEpisodeNeighbors.Resolve(current, [next, current, invalid, Episode("later")]);
        Assert.Same(next, neighbors.Previous);
        Assert.Null(neighbors.Next);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void The_series_endpoint_may_omit_series_ids_from_the_current_row_and_its_neighbors(string? seriesId)
    {
        var current = Episode("current");
        var previous = Episode("previous", seriesId);
        var returnedCurrent = current with { SeriesId = seriesId };
        var next = Episode("next", seriesId);

        var neighbors = PlaybackEpisodeNeighbors.Resolve(current, [previous, returnedCurrent, next]);

        Assert.Same(previous, neighbors.Previous);
        Assert.Same(next, neighbors.Next);
    }

    [Fact]
    public void Series_boundaries_and_an_empty_order_do_not_invent_missing_neighbors()
    {
        var first = Episode("first");
        var last = Episode("last");

        Assert.Equal(new PlaybackEpisodeNeighbors(null, last), PlaybackEpisodeNeighbors.Resolve(first, [first, last]));
        Assert.Equal(new PlaybackEpisodeNeighbors(first, null), PlaybackEpisodeNeighbors.Resolve(last, [first, last]));
        Assert.Equal(new PlaybackEpisodeNeighbors(null, null), PlaybackEpisodeNeighbors.Resolve(first, [first]));
        Assert.Equal(new PlaybackEpisodeNeighbors(null, null), PlaybackEpisodeNeighbors.Resolve(first, []));
    }

    private static BaseItemDto Episode(string? id, string? seriesId = "series-a") => new()
    {
        Id = id,
        Type = "Episode",
        SeriesId = seriesId
    };
}
