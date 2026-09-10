using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class PosterWallLayoutTests
{
    [Theory]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void Larger_text_adds_reading_space_without_changing_poster_columns(double textScale)
    {
        var normal = PosterWallLayout.Calculate(948, 1.5);
        var enlarged = PosterWallLayout.Calculate(948, 1.5, textScale);

        Assert.Equal(normal.Columns, enlarged.Columns);
        Assert.Equal(normal.ItemWidth, enlarged.ItemWidth);
        Assert.Equal(normal.PosterWidth, enlarged.PosterWidth);
        Assert.Equal(38 * (textScale - 1), enlarged.ItemHeight - normal.ItemHeight, precision: 9);
    }

    [Fact]
    public void A_five_column_wall_distributes_the_old_trailing_blank_space()
    {
        const double viewportWidth = 948;
        const double scale = 1.5;

        var metrics = PosterWallLayout.Calculate(viewportWidth, scale);

        Assert.Equal(5, metrics.Columns);
        Assert.True(metrics.PosterWidth > 156);
        Assert.InRange(viewportWidth - metrics.Columns * metrics.ItemWidth, 0, metrics.Columns / scale);
    }

    [Theory]
    [InlineData(175.99, 1)]
    [InlineData(176, 1)]
    [InlineData(351.99, 1)]
    [InlineData(352, 2)]
    [InlineData(879.99, 4)]
    [InlineData(880, 5)]
    public void New_columns_appear_only_when_the_minimum_poster_and_spacing_fit(double width, int columns)
    {
        var metrics = PosterWallLayout.Calculate(width);

        Assert.Equal(columns, metrics.Columns);
        Assert.True(metrics.ItemWidth * columns <= width);
        if (columns > 1) Assert.True(metrics.PosterWidth >= 156);
    }

    [Theory]
    [InlineData(176, 1)]
    [InlineData(352, 1.25)]
    [InlineData(711.25, 1.5)]
    [InlineData(948, 1.5)]
    [InlineData(1243.8, 1.75)]
    [InlineData(1600.5, 2)]
    [InlineData(2560.2, 2.25)]
    public void Fractional_dpi_sizes_fill_the_row_without_overflow_and_keep_portrait_geometry(double width, double scale)
    {
        var metrics = PosterWallLayout.Calculate(width, scale);
        var physicalWidth = metrics.ItemWidth * scale;

        Assert.True(metrics.Columns > 0);
        Assert.True(metrics.ItemWidth * metrics.Columns <= width);
        Assert.InRange(width - metrics.ItemWidth * metrics.Columns, 0, metrics.Columns / scale);
        Assert.Equal(Math.Round(physicalWidth), physicalWidth, precision: 9);
        Assert.Equal(metrics.PosterWidth + 20, metrics.ItemWidth, precision: 9);
        Assert.Equal(metrics.PosterWidth * 1.5, metrics.ItemHeight - 72, precision: 9);
    }

    [Fact]
    public void A_narrow_viewport_shrinks_one_poster_instead_of_overflowing()
    {
        var metrics = PosterWallLayout.Calculate(120, 1.5);

        Assert.Equal(1, metrics.Columns);
        Assert.Equal(120, metrics.ItemWidth);
        Assert.Equal(100, metrics.PosterWidth);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.MaxValue)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(20.5)]
    public void Invalid_or_unusable_viewports_do_not_produce_layout_dimensions(double width)
    {
        Assert.Equal(default, PosterWallLayout.Calculate(width));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void Invalid_dpi_values_use_the_default_scale(double scale)
    {
        Assert.Equal(PosterWallLayout.Calculate(948.5), PosterWallLayout.Calculate(948.5, scale));
    }
}
