namespace EmbyClient.Api;

public sealed record RemoteTrailer
{
    public string? Url { get; init; }
    public string? Name { get; init; }
}

public sealed record CollectionCreationResult
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}

public sealed record MetadataRefreshOptions
{
    public bool Recursive { get; init; }
    public string MetadataRefreshMode { get; init; } = "Default";
    public string ImageRefreshMode { get; init; } = "Default";
    public bool ReplaceAllMetadata { get; init; }
    public bool ReplaceAllImages { get; init; }
    public bool? ReplaceThumbnailImages { get; init; }
}

/// <summary>Null properties leave the existing metadata unchanged; empty values clear optional fields.</summary>
public sealed record ItemMetadataUpdate
{
    public string? Name { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Overview { get; init; }
    public int? ProductionYear { get; init; }
    public string? OfficialRating { get; init; }
    public string[]? Genres { get; init; }
    public string[]? Tags { get; init; }
    public bool? LockData { get; init; }
    public string[]? LockedFields { get; init; }
}

public sealed record BaseRefreshRequest
{
    public bool? ReplaceThumbnailImages { get; init; }
}
