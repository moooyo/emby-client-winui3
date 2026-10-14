using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class BufferedPlaybackRangePolicyTests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(10, 20)]
    [InlineData(30, 0)]
    public void A_reported_range_uses_the_native_url_position(long positionSeconds, long expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds),
            BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(positionSeconds), [Range(0, 30)]));
    }

    [Fact]
    public void Unordered_overlapping_and_touching_ranges_form_one_continuous_buffer()
    {
        BufferedPlaybackRange[] ranges = [Range(20, 40), Range(0, 10), Range(8, 20), Range(15, 25)];

        Assert.Equal(TimeSpan.FromSeconds(35), BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(5), ranges));
    }

    [Fact]
    public void Nested_and_duplicate_ranges_do_not_shorten_or_double_count_the_buffer()
    {
        BufferedPlaybackRange[] ranges = [Range(0, 30), Range(10, 20), Range(0, 30), Range(15, 25)];

        Assert.Equal(TimeSpan.FromSeconds(15), BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(15), ranges));
    }

    [Fact]
    public void A_future_buffered_island_does_not_extend_the_current_buffer()
    {
        Assert.Equal(TimeSpan.FromSeconds(5),
            BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(5), [Range(0, 10), Range(20, 40)]));
    }

    [Fact]
    public void A_single_tick_gap_is_not_rounded_into_continuous_buffering()
    {
        BufferedPlaybackRange[] ranges = [new(0, 10), new(11, 30)];

        Assert.Equal(TimeSpan.FromTicks(5), BufferedPlaybackRangePolicy.GetBufferedAhead(5, ranges));
        Assert.Equal(TimeSpan.Zero, BufferedPlaybackRangePolicy.GetBufferedAhead(10, ranges));
    }

    [Fact]
    public void Only_the_island_containing_the_current_position_is_reported()
    {
        Assert.Equal(TimeSpan.FromSeconds(15),
            BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(25), [Range(0, 10), Range(20, 40)]));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(45)]
    public void A_position_outside_every_reported_range_has_no_known_duration(long positionSeconds)
    {
        Assert.Null(BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(positionSeconds), [Range(10, 12), Range(20, 40)]));
    }

    [Fact]
    public void Empty_missing_and_negative_position_inputs_have_no_known_duration()
    {
        Assert.Null(BufferedPlaybackRangePolicy.GetBufferedAhead(0, []));
        Assert.Null(BufferedPlaybackRangePolicy.GetBufferedAhead(0, null));
        Assert.Null(BufferedPlaybackRangePolicy.GetBufferedAhead(-1, [Range(0, 30)]));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 9)]
    [InlineData(10, 10)]
    public void An_invalid_reported_range_makes_the_duration_unavailable(long startTicks, long endTicks)
    {
        Assert.Null(BufferedPlaybackRangePolicy.GetBufferedAhead(5, [new(0, 30), new(startTicks, endTicks)]));
    }

    [Fact]
    public void A_large_valid_media_range_keeps_tick_precision_without_overflow()
    {
        Assert.Equal(TimeSpan.FromTicks(long.MaxValue - 1),
            BufferedPlaybackRangePolicy.GetBufferedAhead(1, [new(0, long.MaxValue)]));
        Assert.Equal(TimeSpan.FromTicks(1),
            BufferedPlaybackRangePolicy.GetBufferedAhead(long.MaxValue - 1, [new(0, long.MaxValue)]));
    }

    [Fact]
    public void Reading_the_ranges_preserves_the_provider_order()
    {
        BufferedPlaybackRange[] ranges = [Range(20, 40), Range(0, 10), Range(8, 20)];
        var original = ranges.ToArray();

        BufferedPlaybackRangePolicy.GetBufferedAhead(Ticks(5), ranges);

        Assert.Equal(original, ranges);
    }

    private static long Ticks(long seconds) => TimeSpan.FromSeconds(seconds).Ticks;
    private static BufferedPlaybackRange Range(long startSeconds, long endSeconds) =>
        new(Ticks(startSeconds), Ticks(endSeconds));
}
