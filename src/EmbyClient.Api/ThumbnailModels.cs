namespace EmbyClient.Api;

/// <summary>The official RokuMetadata.Api.ThumbnailSetInfo response.</summary>
public sealed record ThumbnailSetInfo
{
    public double? AspectRatio { get; init; }
    public ThumbnailInfo[]? Thumbnails { get; init; }
}

/// <summary>A server-generated sample position and tag. The contract supplies no image URL or image index.</summary>
public sealed record ThumbnailInfo
{
    // A missing timestamp must not become an invented sample at the start of the video.
    public long? PositionTicks { get; init; }
    public string? ImageTag { get; init; }
}

/// <summary>A bounded selection of images and original-media timestamps from a server-delivered Roku BIF archive.</summary>
public sealed record BifThumbnailSet(int TotalImageCount, int AvailableImageCount,
    uint TimestampMultiplierMilliseconds, BifThumbnailSample[] Thumbnails);

/// <summary>A JPEG supplied by the server, with its actual BIF timestamp and declared image dimensions.</summary>
public sealed record BifThumbnailSample(long PositionTicks, byte[] JpegBytes, int PixelWidth, int PixelHeight);
