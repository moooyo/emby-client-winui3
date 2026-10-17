using EmbyClient.App.Views;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class CompactOverlayPresentationPolicyTests
{
    [Fact]
    public void The_480_by_270_prototype_keeps_transport_near_the_center_with_a_small_upward_bias()
    {
        const double height = 270;

        var layout = CompactOverlayPresentationPolicy.CalculateLayout(height, 44, 28);

        AssertReadableBands(layout, height, 44, 28);
        var transportCenter = layout.TransportTop + 24;
        Assert.InRange(height / 2 - transportCenter, 5, 7);
    }

    [Theory]
    [InlineData(180, 44, 28)]
    [InlineData(180, 44, 36)]
    [InlineData(270, 44, 28)]
    [InlineData(270, 48, 36)]
    [InlineData(360, 88, 56)]
    [InlineData(540, 44, 28)]
    public void Supported_player_heights_keep_all_three_measured_bands_separate(
        double height, double headerBottom, double footerHeight)
    {
        var layout = CompactOverlayPresentationPolicy.CalculateLayout(height, headerBottom, footerHeight);

        AssertReadableBands(layout, height, headerBottom, footerHeight);
    }

    [Fact]
    public void A_taller_measured_header_moves_transport_down_only_as_far_as_needed()
    {
        var normal = CompactOverlayPresentationPolicy.CalculateLayout(270, 44, 28);

        var taller = CompactOverlayPresentationPolicy.CalculateLayout(270, 140, 28);

        AssertReadableBands(taller, 270, 140, 28);
        Assert.True(taller.TransportTop > normal.TransportTop);
        Assert.Equal(8, taller.TransportTop - 140, precision: 9);
        Assert.Equal(normal.FooterTop, taller.FooterTop);
        Assert.Equal(normal.SubtitleBottom, taller.SubtitleBottom);
    }

    [Fact]
    public void A_taller_footer_raises_transport_and_subtitles_without_reducing_the_control_height()
    {
        var normal = CompactOverlayPresentationPolicy.CalculateLayout(180, 44, 28);

        var taller = CompactOverlayPresentationPolicy.CalculateLayout(180, 44, 56);

        AssertReadableBands(taller, 180, 44, 56);
        Assert.True(taller.TransportTop < normal.TransportTop);
        Assert.Equal(8, taller.FooterTop - taller.TransportTop - 48, precision: 9);
        Assert.True(taller.SubtitleBottom > normal.SubtitleBottom);
    }

    [Theory]
    [InlineData(217, false)]
    [InlineData(218, true)]
    [InlineData(219, true)]
    public void Very_large_measured_bands_report_the_boundary_where_all_controls_can_fit(double height, bool hasRoom)
    {
        var layout = CompactOverlayPresentationPolicy.CalculateLayout(height, 88, 56);

        Assert.Equal(hasRoom, layout.HasRoom);
        AssertFiniteAndBounded(layout, height);
        if (hasRoom) AssertReadableBands(layout, height, 88, 56);
        if (height == 218)
        {
            Assert.Equal(8, layout.TransportTop - 88, precision: 9);
            Assert.Equal(8, layout.FooterTop - layout.TransportTop - 48, precision: 9);
        }
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Invalid_viewport_heights_have_a_finite_zero_sized_fallback(double height)
    {
        var layout = CompactOverlayPresentationPolicy.CalculateLayout(height, 44, 28);

        AssertFiniteAndBounded(layout, 0);
        Assert.False(layout.HasRoom);
    }

    [Theory]
    [InlineData(double.Epsilon)]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(47.99)]
    [InlineData(48)]
    [InlineData(100)]
    public void Transient_small_viewports_return_a_bounded_position_without_claiming_controls_fit(double height)
    {
        var layout = CompactOverlayPresentationPolicy.CalculateLayout(height, 44, 28);

        AssertFiniteAndBounded(layout, height);
        Assert.False(layout.HasRoom);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void Invalid_band_measurements_use_zero_until_a_finite_measurement_arrives(double measurement)
    {
        var invalidHeader = CompactOverlayPresentationPolicy.CalculateLayout(270, measurement, 28);
        var invalidFooter = CompactOverlayPresentationPolicy.CalculateLayout(270, 44, measurement);

        Assert.Equal(CompactOverlayPresentationPolicy.CalculateLayout(270, 0, 28), invalidHeader);
        Assert.Equal(CompactOverlayPresentationPolicy.CalculateLayout(270, 44, 0), invalidFooter);
        AssertFiniteAndBounded(invalidHeader, 270);
        AssertFiniteAndBounded(invalidFooter, 270);
        Assert.True(invalidHeader.HasRoom);
        Assert.True(invalidFooter.HasRoom);
    }

    [Theory]
    [InlineData(double.MaxValue, double.MaxValue, 0, false)]
    [InlineData(double.MaxValue, 0, double.MaxValue, false)]
    [InlineData(double.MaxValue, 0, 28, true)]
    [InlineData(270, double.MaxValue, double.MaxValue, false)]
    [InlineData(72057594037927936d + 96, 72057594037927936d + 16, 0, false)]
    public void Extreme_finite_measurements_preserve_bounds_and_reject_unrepresentable_control_intervals(
        double height, double headerBottom, double footerHeight, bool hasRoom)
    {
        var layout = CompactOverlayPresentationPolicy.CalculateLayout(height, headerBottom, footerHeight);

        AssertFiniteAndBounded(layout, height);
        Assert.Equal(hasRoom, layout.HasRoom);
    }

    [Theory]
    [InlineData(0, 100, 0, 0)]
    [InlineData(25, 100, 25, .25)]
    [InlineData(1, 3, 1, 1d / 3d)]
    [InlineData(100, 100, 100, 1)]
    [InlineData(200, 100, 100, 1)]
    [InlineData(long.MaxValue, 1, 1, 1)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue, 1)]
    [InlineData(long.MinValue, long.MaxValue, 0, 0)]
    [InlineData(long.MaxValue / 2, long.MaxValue, long.MaxValue / 2, .5)]
    public void Known_positive_duration_uses_the_real_ticks_and_clamps_ended_or_invalid_positions(
        long positionTicks, long durationTicks, long expectedPosition, double expectedProgress)
    {
        var clock = CompactOverlayPresentationPolicy.GetClock(positionTicks, durationTicks);

        Assert.Equal(expectedPosition, clock.PositionTicks);
        Assert.Equal(durationTicks, clock.DurationTicks);
        Assert.True(clock.HasProgress);
        Assert.Equal(expectedProgress, clock.Progress, precision: 9);
        Assert.True(double.IsFinite(clock.Progress));
        Assert.InRange(clock.Progress, 0, 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Unknown_or_negative_duration_keeps_live_elapsed_time_advancing_without_a_sample_total(long? durationTicks)
    {
        var earlierTicks = TimeSpan.FromMinutes(13).Ticks;

        var earlier = CompactOverlayPresentationPolicy.GetClock(earlierTicks, durationTicks);
        var later = CompactOverlayPresentationPolicy.GetClock(long.MaxValue, durationTicks);

        Assert.Equal(earlierTicks, earlier.PositionTicks);
        Assert.Equal(long.MaxValue, later.PositionTicks);
        Assert.True(later.PositionTicks > earlier.PositionTicks);
        Assert.Null(earlier.DurationTicks);
        Assert.Null(later.DurationTicks);
        Assert.False(earlier.HasProgress);
        Assert.False(later.HasProgress);
        Assert.Equal(0, earlier.Progress);
        Assert.Equal(0, later.Progress);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1)]
    public void Negative_live_positions_start_at_zero_without_inventing_a_duration(long positionTicks)
    {
        var clock = CompactOverlayPresentationPolicy.GetClock(positionTicks, null);

        Assert.Equal(0, clock.PositionTicks);
        Assert.Null(clock.DurationTicks);
        Assert.False(clock.HasProgress);
        Assert.Equal(0, clock.Progress);
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(long.MaxValue)]
    public void A_genuine_zero_duration_is_retained_and_has_no_progress(long positionTicks)
    {
        var clock = CompactOverlayPresentationPolicy.GetClock(positionTicks, 0);

        Assert.Equal(0, clock.PositionTicks);
        Assert.Equal(0, clock.DurationTicks);
        Assert.False(clock.HasProgress);
        Assert.Equal(0, clock.Progress);
    }

    private static void AssertReadableBands(CompactOverlayLayout layout,
        double height, double headerBottom, double footerHeight)
    {
        Assert.True(layout.HasRoom);
        AssertFiniteAndBounded(layout, height);
        Assert.True(layout.TransportTop >= headerBottom + 8);
        Assert.True(layout.TransportTop + 48 + 8 <= layout.FooterTop);
        Assert.Equal(10, height - layout.FooterTop - footerHeight, precision: 9);
        Assert.Equal(12, layout.FooterTop - (height - layout.SubtitleBottom), precision: 9);
    }

    private static void AssertFiniteAndBounded(CompactOverlayLayout layout, double height)
    {
        Assert.True(double.IsFinite(layout.TransportTop));
        Assert.True(double.IsFinite(layout.FooterTop));
        Assert.True(double.IsFinite(layout.SubtitleBottom));
        Assert.InRange(layout.TransportTop, 0, Math.Max(0, height - 48));
        Assert.InRange(layout.FooterTop, 0, height);
        Assert.InRange(layout.SubtitleBottom, 0, height);
    }
}
