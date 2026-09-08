using System.Text.Json.Serialization;

namespace EmbyClient.Api;

public sealed record ClientIdentity(string Client, string Device, string DeviceId, string Version);

public record PublicSystemInfo
{
    public string? Id { get; init; }
    public string? ServerName { get; init; }
    public string? Version { get; init; }
    public string? LocalAddress { get; init; }
    public string? WanAddress { get; init; }
    public string[]? LocalAddresses { get; init; }
    public string[]? RemoteAddresses { get; init; }
}

public sealed record SystemInfo : PublicSystemInfo
{
    public bool? IsShuttingDown { get; init; }
    public bool? HasPendingRestart { get; init; }
    public string? OperatingSystemDisplayName { get; init; }
    public bool? SupportsHttps { get; init; }
}

public sealed record AuthenticationResult
{
    public UserDto? User { get; init; }
    public SessionInfo? SessionInfo { get; init; }
    public string? AccessToken { get; init; }
    public string? ServerId { get; init; }

    public override string ToString() => nameof(AuthenticationResult);
}

public sealed record SessionInfo
{
    public string? Id { get; init; }
    public string? UserId { get; init; }
    public string? DeviceId { get; init; }
    public string? Client { get; init; }
    public string? DeviceName { get; init; }
}

public sealed record UserDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? ServerId { get; init; }
    public string? PrimaryImageTag { get; init; }
    public bool? HasPassword { get; init; }
    public UserConfiguration? Configuration { get; init; }
    public UserPolicy? Policy { get; init; }
}

public sealed record UserConfiguration
{
    public string? AudioLanguagePreference { get; init; }
    public string? SubtitleLanguagePreference { get; init; }
    public string? SubtitleMode { get; init; }
    public bool? PlayDefaultAudioTrack { get; init; }
    public bool? EnableNextEpisodeAutoPlay { get; init; }
    public bool? RememberAudioSelections { get; init; }
    public bool? RememberSubtitleSelections { get; init; }
}

public sealed record UserPolicy
{
    public bool? IsDisabled { get; init; }
    public bool? EnableMediaPlayback { get; init; }
    public bool? EnableAudioPlaybackTranscoding { get; init; }
    public bool? EnableVideoPlaybackTranscoding { get; init; }
    public bool? EnablePlaybackRemuxing { get; init; }
    public bool? EnableContentDownloading { get; init; }
    public bool? EnableLiveTvAccess { get; init; }
    public long? RemoteClientBitrateLimit { get; init; }
}

public sealed record QueryResult<T>
{
    [JsonRequired]
    public T[] Items { get; init; } = [];
    public int? TotalRecordCount { get; init; }
}

public sealed record BaseItemDto
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Type { get; init; }
    public bool? IsFolder { get; init; }
    public string? MediaType { get; init; }
    public string? CollectionType { get; init; }
    public string? Overview { get; init; }
    public int? ProductionYear { get; init; }
    public DateTimeOffset? PremiereDate { get; init; }
    public string? OfficialRating { get; init; }
    public double? CommunityRating { get; init; }
    public long? RunTimeTicks { get; init; }
    public int? IndexNumber { get; init; }
    public int? IndexNumberEnd { get; init; }
    public int? ParentIndexNumber { get; init; }
    public string? ParentId { get; init; }
    public string? SeriesId { get; init; }
    public string? SeriesName { get; init; }
    public string? SeasonId { get; init; }
    public int? ChildCount { get; init; }
    public string? LocationType { get; init; }
    public string[]? Genres { get; init; }
    public NameLongIdPair[]? GenreItems { get; init; }
    public NameLongIdPair[]? Studios { get; init; }
    public PersonInfo[]? People { get; init; }
    public Dictionary<string, string>? ImageTags { get; init; }
    public string[]? BackdropImageTags { get; init; }
    public double? PrimaryImageAspectRatio { get; init; }
    public string? ParentBackdropItemId { get; init; }
    public string[]? ParentBackdropImageTags { get; init; }
    public string? ParentLogoItemId { get; init; }
    public string? ParentLogoImageTag { get; init; }
    public string? ParentThumbItemId { get; init; }
    public string? ParentThumbImageTag { get; init; }
    public UserItemDataDto? UserData { get; init; }
    public MediaSourceInfo[]? MediaSources { get; init; }
    public MediaStream[]? MediaStreams { get; init; }
    public ChapterInfo[]? Chapters { get; init; }
}

public sealed record NameLongIdPair
{
    public string? Name { get; init; }
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? Id { get; init; }
}

public sealed record PersonInfo
{
    public string? Name { get; init; }
    public string? Id { get; init; }
    public string? Role { get; init; }
    public string? Type { get; init; }
    public string? PrimaryImageTag { get; init; }
}

public sealed record ChapterInfo
{
    public string? Name { get; init; }
    public long StartPositionTicks { get; init; }
    public string? ImageTag { get; init; }
}

public sealed record UserItemDataDto
{
    public string? ItemId { get; init; }
    public string? Key { get; init; }
    public bool? IsFavorite { get; init; }
    public bool? Played { get; init; }
    public long? PlaybackPositionTicks { get; init; }
    public int? PlayCount { get; init; }
    public DateTimeOffset? LastPlayedDate { get; init; }
    public double? PlayedPercentage { get; init; }
    public int? UnplayedItemCount { get; init; }
}

public sealed record ItemQuery
{
    public string? ParentId { get; init; }
    public int? StartIndex { get; init; }
    public int? Limit { get; init; } = 50;
    public bool? Recursive { get; init; }
    public string? SearchTerm { get; init; }
    public string[]? IncludeItemTypes { get; init; }
    public string[]? MediaTypes { get; init; }
    public string[]? SortBy { get; init; }
    public string[]? SortOrder { get; init; }
    public string[]? Fields { get; init; }
    public string[]? Filters { get; init; }
    public string[]? Ids { get; init; }
    public string[]? Genres { get; init; }
    public string[]? Tags { get; init; }
    public bool? IsPlayed { get; init; }
    public bool? IsFavorite { get; init; }
    public bool? EnableUserData { get; init; } = true;
    public bool? EnableImages { get; init; } = true;
    public string[]? EnableImageTypes { get; init; }
    public int? ImageTypeLimit { get; init; }
}

public sealed record LatestItemsQuery
{
    public string? ParentId { get; init; }
    public int? Limit { get; init; } = 20;
    public string[]? IncludeItemTypes { get; init; }
    public string[]? Fields { get; init; }
    public bool? IsPlayed { get; init; }
    public bool? GroupItems { get; init; } = true;
    public bool? EnableUserData { get; init; } = true;
}

public sealed record NextUpQuery
{
    public string? ParentId { get; init; }
    public string? SeriesId { get; init; }
    public int? StartIndex { get; init; }
    public int? Limit { get; init; } = 20;
    public string[]? Fields { get; init; }
    public bool? EnableUserData { get; init; } = true;
}

public sealed record ImageOptions
{
    public string? Tag { get; init; }
    public int? MaxWidth { get; init; } = 480;
    public int? MaxHeight { get; init; }
    public string? Format { get; init; }
    public int? Quality { get; init; }
}

public sealed record AuthenticateByNameRequest(string Username, string Pw)
{
    public override string ToString() => nameof(AuthenticateByNameRequest);
}
