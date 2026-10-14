namespace EmbyClient.App.Playback;

/// <summary>A native subtitle bitmap crop expressed in the video's native pixel coordinates.</summary>
public readonly record struct AssSubtitleGeometry(
    int VideoWidth,
    int VideoHeight,
    double PixelAspectRatio,
    int X,
    int Y,
    int Width,
    int Height);

public readonly record struct AssSubtitleRectangle(double X, double Y, double Width, double Height);

public readonly record struct AssSubtitlePlacement(AssSubtitleRectangle Video, AssSubtitleRectangle Image);

/// <summary>Maps a native subtitle bitmap crop into a centered, uniformly fitted video viewport.</summary>
public static class AssSubtitleLayoutPolicy
{
    public static bool IsValid(AssSubtitleGeometry geometry)
    {
        if (geometry.VideoWidth <= 0 || geometry.VideoHeight <= 0 ||
            geometry.X < 0 || geometry.Y < 0 || geometry.Width <= 0 || geometry.Height <= 0 ||
            !double.IsFinite(geometry.PixelAspectRatio) || geometry.PixelAspectRatio <= 0)
            return false;

        var displayWidth = geometry.VideoWidth * geometry.PixelAspectRatio;
        if (!double.IsFinite(displayWidth) || displayWidth <= 0)
            return false;

        // Wider arithmetic prevents a malformed crop from wrapping back into the video bounds.
        return (long)geometry.X + geometry.Width <= geometry.VideoWidth &&
            (long)geometry.Y + geometry.Height <= geometry.VideoHeight;
    }

    public static bool TryPlace(
        double viewportWidth,
        double viewportHeight,
        AssSubtitleGeometry geometry,
        out AssSubtitlePlacement placement)
    {
        placement = default;
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 || !IsValid(geometry))
            return false;

        var displayWidth = geometry.VideoWidth * geometry.PixelAspectRatio;
        var widthScale = viewportWidth / displayWidth;
        var heightScale = viewportHeight / geometry.VideoHeight;
        var widthLimited = widthScale <= heightScale;
        var videoWidth = widthLimited ? viewportWidth : displayWidth * heightScale;
        var videoHeight = widthLimited ? geometry.VideoHeight * widthScale : viewportHeight;
        if (!double.IsFinite(videoWidth) || !double.IsFinite(videoHeight) || videoWidth <= 0 || videoHeight <= 0)
            return false;

        // Keep floating-point rounding from extending the fitted video beyond the viewport.
        videoWidth = Math.Min(viewportWidth, videoWidth);
        videoHeight = Math.Min(viewportHeight, videoHeight);
        var video = new AssSubtitleRectangle(
            (viewportWidth - videoWidth) / 2,
            (viewportHeight - videoHeight) / 2,
            videoWidth,
            videoHeight);

        var image = new AssSubtitleRectangle(
            video.X + (double)geometry.X / geometry.VideoWidth * video.Width,
            video.Y + (double)geometry.Y / geometry.VideoHeight * video.Height,
            (double)geometry.Width / geometry.VideoWidth * video.Width,
            (double)geometry.Height / geometry.VideoHeight * video.Height);
        if (!double.IsFinite(image.X) || !double.IsFinite(image.Y) ||
            !double.IsFinite(image.Width) || !double.IsFinite(image.Height) ||
            image.Width <= 0 || image.Height <= 0)
            return false;

        placement = new(video, image);
        return true;
    }

    /// <summary>Raises an overlapping bitmap above bottom controls without changing its scale or video bounds.</summary>
    public static AssSubtitlePlacement AvoidBottomControls(
        AssSubtitlePlacement placement,
        AssSubtitleRectangle? controls,
        double clearance)
    {
        if (controls is not { } obstacle || !IsFiniteRectangle(obstacle)
            || !IsFiniteRectangle(placement.Video) || !IsFiniteRectangle(placement.Image)
            || !double.IsFinite(clearance) || clearance < 0)
            return placement;

        var image = placement.Image;
        if (image.X >= obstacle.X + obstacle.Width || image.X + image.Width <= obstacle.X
            || image.Y >= obstacle.Y + obstacle.Height || image.Y + image.Height <= obstacle.Y)
            return placement;

        var top = obstacle.Y - clearance - image.Height;
        // Oversized images retain their authored geometry rather than becoming smaller or clipped.
        if (!double.IsFinite(top) || top < placement.Video.Y || top >= image.Y
            || image.X < placement.Video.X || image.X + image.Width > placement.Video.X + placement.Video.Width
            || image.Y < placement.Video.Y || image.Y + image.Height > placement.Video.Y + placement.Video.Height)
            return placement;

        return new(placement.Video, image with { Y = top });
    }

    private static bool IsFiniteRectangle(AssSubtitleRectangle rectangle) =>
        double.IsFinite(rectangle.X) && double.IsFinite(rectangle.Y)
        && double.IsFinite(rectangle.Width) && double.IsFinite(rectangle.Height)
        && double.IsFinite(rectangle.X + rectangle.Width) && double.IsFinite(rectangle.Y + rectangle.Height)
        && rectangle.Width > 0 && rectangle.Height > 0;
}
