namespace EmbyClient.App.Services;

public readonly record struct PosterWallMetrics(int Columns, double ItemWidth, double ItemHeight, double PosterWidth);

public static class PosterWallLayout
{
    private const double MinimumPosterWidth = 156;
    private const double HorizontalContainerSpace = 20;
    private const double TextLineSpace = 38;
    private const double VerticalContainerSpace = 34;

    public static PosterWallMetrics Calculate(double viewportWidth, double rasterizationScale = 1, double textScaleFactor = 1)
    {
        if (!double.IsFinite(viewportWidth) || viewportWidth <= HorizontalContainerSpace) return default;

        var columnCount = Math.Floor(viewportWidth / (MinimumPosterWidth + HorizontalContainerSpace));
        if (columnCount > int.MaxValue) return default;
        var columns = Math.Max(1, (int)columnCount);
        var scale = double.IsFinite(rasterizationScale) && rasterizationScale > 0 ? rasterizationScale : 1;
        var physicalItemWidth = Math.Floor(viewportWidth / columns * scale);
        var itemWidth = physicalItemWidth / scale;
        if (!double.IsFinite(itemWidth)) return default;

        // Rounding back to DIPs must not push the final column onto another row.
        if (itemWidth * columns > viewportWidth) itemWidth = (physicalItemWidth - 1) / scale;
        var posterWidth = itemWidth - HorizontalContainerSpace;
        if (posterWidth <= 0) return default;

        var textScale = double.IsFinite(textScaleFactor) && textScaleFactor >= 1 ? textScaleFactor : 1;
        return new(columns, itemWidth, posterWidth * 1.5 + TextLineSpace * textScale + VerticalContainerSpace, posterWidth);
    }
}
