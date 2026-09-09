using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed class FixtureState
{
    public const string ServerId = "synthetic-server-0001";
    public const string UserId = "synthetic-user-demo";
    public long DurationTicks => Options.Media.DurationTicks;
    private readonly object _gate = new();
    private readonly Dictionary<string, BaseItemDto> _items;
    private readonly Dictionary<string, UserItemDataDto> _userData = [];
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _playSessions = new(StringComparer.Ordinal);
    private readonly List<FixturePlaybackEvent> _events = [];
    private readonly Dictionary<string, byte[]> _posters = [];
    private readonly long _mediaLength;
    private int _loginCount;
    private int _authenticationFailures;
    private int _playbackInfoCount;
    private int _mediaRequests;
    private int _rangeRequests;
    private int _partialResponses;
    private int _capabilityUpdates;
    private int _encodingCleanups;

    public FixtureState(FixtureOptions options)
    {
        Options = options;
        _mediaLength = new FileInfo(options.MediaPath).Length;
        _items = CreateCatalog().ToDictionary(item => item.Id!, StringComparer.Ordinal);
        foreach (var item in _items.Values)
        {
            _userData[item.Id!] = new UserItemDataDto
            {
                ItemId = item.Id, Key = item.Id, IsFavorite = false, Played = false,
                PlaybackPositionTicks = item.Id == "1002" ? 3 * TimeSpan.TicksPerSecond : 0, PlayCount = 0
            };
            var color = item.Id == "1001" ? (48, 108, 183) : item.Id == "1002" ? (163, 70, 103) : (39, 137, 111);
            _posters[item.Id!] = FixturePng.Create(240, 360, color);
        }
    }

    public FixtureOptions Options { get; }

    public PublicSystemInfo PublicInfo => new()
    {
        Id = ServerId, ServerName = "SYNTHETIC Emby Client Fixture", Version = "synthetic-1.0",
        LocalAddress = $"http://127.0.0.1:{Options.Port}", LocalAddresses = [$"http://127.0.0.1:{Options.Port}"]
    };

    public SystemInfo SystemInfo => new()
    {
        Id = ServerId, ServerName = PublicInfo.ServerName, Version = PublicInfo.Version,
        LocalAddress = PublicInfo.LocalAddress, IsShuttingDown = false, SupportsHttps = false
    };

    public UserDto User => new()
    {
        Id = UserId, Name = "demo (synthetic)", ServerId = ServerId, HasPassword = true,
        PrimaryImageTag = "synthetic-poster-v1",
        Configuration = new UserConfiguration { AudioLanguagePreference = "eng", SubtitleMode = "None" },
        Policy = new UserPolicy
        {
            IsDisabled = false, EnableMediaPlayback = true, EnableAudioPlaybackTranscoding = false,
            EnableVideoPlaybackTranscoding = false, EnablePlaybackRemuxing = false,
            EnableContentDownloading = false, EnableLiveTvAccess = false
        }
    };

    public AuthenticationResult? Login(AuthenticateByNameRequest? request)
    {
        if (request?.Username != "demo" || request.Pw != "demo")
        {
            Interlocked.Increment(ref _authenticationFailures);
            return null;
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var sessionId = "synthetic-session-" + Guid.NewGuid().ToString("N");
        _tokens[token] = sessionId;
        Interlocked.Increment(ref _loginCount);
        return new AuthenticationResult
        {
            User = User, AccessToken = token, ServerId = ServerId,
            SessionInfo = new SessionInfo { Id = sessionId, UserId = UserId, Client = "Synthetic fixture client" }
        };
    }

    public bool IsAuthenticated(HttpRequest request)
    {
        var token = request.Headers["X-Emby-Token"].ToString();
        if (token.Length == 0) token = request.Query["api_key"].ToString();
        return token.Length > 0 && _tokens.ContainsKey(token);
    }

    public void Logout(HttpRequest request)
    {
        var token = request.Headers["X-Emby-Token"].ToString();
        _tokens.TryRemove(token, out _);
    }

    public void AuthenticationFailed() => Interlocked.Increment(ref _authenticationFailures);
    public void CapabilityUpdated() => Interlocked.Increment(ref _capabilityUpdates);
    public void EncodingCleaned() => Interlocked.Increment(ref _encodingCleanups);
    public void MediaRequested(bool range) { Interlocked.Increment(ref _mediaRequests); if (range) Interlocked.Increment(ref _rangeRequests); }
    public void PartialResponse() => Interlocked.Increment(ref _partialResponses);

    public BaseItemDto? Item(string itemId)
    {
        lock (_gate)
        {
            return _items.TryGetValue(itemId, out var item) ? WithUserData(item) : null;
        }
    }

    public QueryResult<BaseItemDto> Views() => new()
    {
        Items = [Item("movies")!, Item("shows")!], TotalRecordCount = 2
    };

    public QueryResult<BaseItemDto> Query(IQueryCollection query, string? parentOverride = null,
        string? typeOverride = null, bool resume = false, bool nextUp = false, bool forceRecursive = false)
    {
        lock (_gate)
        {
            IEnumerable<BaseItemDto> items = _items.Values.Where(item => item.Type != "CollectionFolder");
            var parent = parentOverride ?? Value(query, "ParentId");
            if (!string.IsNullOrEmpty(parent))
            {
                items = forceRecursive || Boolean(query, "Recursive") == true
                    ? items.Where(item => IsDescendant(item, parent))
                    : items.Where(item => item.ParentId == parent);
            }
            var types = typeOverride is null ? Values(query, "IncludeItemTypes") : [typeOverride];
            if (types.Length > 0) items = items.Where(item => types.Contains(item.Type, StringComparer.OrdinalIgnoreCase));
            var mediaTypes = Values(query, "MediaTypes");
            if (mediaTypes.Length > 0) items = items.Where(item => mediaTypes.Contains(item.MediaType, StringComparer.OrdinalIgnoreCase));
            var search = Value(query, "SearchTerm");
            if (!string.IsNullOrWhiteSpace(search)) items = items.Where(item => item.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true);
            var ids = Values(query, "Ids");
            if (ids.Length > 0) items = items.Where(item => ids.Contains(item.Id, StringComparer.Ordinal));
            var isFavorite = Boolean(query, "IsFavorite");
            if (isFavorite.HasValue) items = items.Where(item => _userData[item.Id!].IsFavorite == isFavorite);
            var isPlayed = Boolean(query, "IsPlayed");
            if (isPlayed.HasValue) items = items.Where(item => _userData[item.Id!].Played == isPlayed);
            if (resume) items = items.Where(item => item.IsFolder != true && _userData[item.Id!].PlaybackPositionTicks > 0 && _userData[item.Id!].Played != true);
            if (nextUp) items = items.Where(item => item.Type == "Episode" && _userData[item.Id!].Played != true);
            var seriesId = Value(query, "SeriesId");
            if (!string.IsNullOrWhiteSpace(seriesId)) items = items.Where(item => item.SeriesId == seriesId);
            if (nextUp) items = items.GroupBy(item => item.SeriesId).Select(group => group.OrderBy(item => item.IndexNumber).First());
            items = Value(query, "SortOrder")?.StartsWith("Descending", StringComparison.OrdinalIgnoreCase) == true
                ? items.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
            var array = items.ToArray();
            var offset = Integer(query, "StartIndex", 0, 0, 10000);
            var limit = Integer(query, "Limit", 50, 1, 500);
            return new QueryResult<BaseItemDto>
            {
                Items = array.Skip(offset).Take(limit).Select(WithUserData).ToArray(), TotalRecordCount = array.Length
            };
        }
    }

    public BaseItemDto[] Latest(IQueryCollection query)
    {
        var result = Query(query, forceRecursive: true).Items.Where(item => item.Type is "Movie" or "Episode").ToArray();
        if (Boolean(query, "GroupItems") == false) return result;
        return result.Select(item => item.Type == "Episode" ? Item(item.SeriesId!)! : item)
            .DistinctBy(item => item.Id).ToArray();
    }

    public UserItemDataDto? SetUserData(string itemId, bool value, bool favorite)
    {
        lock (_gate)
        {
            if (!_userData.TryGetValue(itemId, out var existing)) return null;
            var updated = favorite ? existing with { IsFavorite = value }
                : existing with { Played = value, PlaybackPositionTicks = 0, PlayCount = value ? Math.Max(1, existing.PlayCount ?? 0) : 0 };
            _userData[itemId] = updated;
            return updated;
        }
    }

    public PlaybackInfoResponse? PlaybackInfo(string itemId, PlaybackInfoRequest? request)
    {
        var item = Item(itemId);
        if (item is null || item.IsFolder == true) return null;
        Interlocked.Increment(ref _playbackInfoCount);
        if (request?.EnableDirectStream == false)
            return new PlaybackInfoResponse { ErrorCode = "NoCompatibleStream", MediaSources = [] };
        var playSessionId = "synthetic-play-" + Guid.NewGuid().ToString("N");
        _playSessions[playSessionId] = itemId;
        return new PlaybackInfoResponse { PlaySessionId = playSessionId, MediaSources = [Source(itemId, playSessionId)] };
    }

    public bool OwnsPlayback(string? itemId, string? playSessionId) => itemId is not null && playSessionId is not null
        && _playSessions.TryGetValue(playSessionId, out var expected) && expected == itemId;

    public bool Record(string kind, string? itemId, string? playSessionId, long? positionTicks,
        string? eventName = null, bool? failed = null)
    {
        if (!OwnsPlayback(itemId, playSessionId)) return false;
        lock (_gate)
        {
            var position = Math.Clamp(positionTicks ?? 0, 0, DurationTicks);
            var existing = _userData[itemId!];
            var completed = kind == "Stop" && failed != true && position >= DurationTicks * 9 / 10;
            _userData[itemId!] = existing with
            {
                PlaybackPositionTicks = completed ? 0 : position,
                Played = completed || existing.Played == true,
                PlayCount = completed ? (existing.PlayCount ?? 0) + 1 : existing.PlayCount,
                LastPlayedDate = DateTimeOffset.UtcNow
            };
            _events.Add(new FixturePlaybackEvent(kind, itemId!, playSessionId!, position, eventName, failed, DateTimeOffset.UtcNow));
            if (_events.Count > 200) _events.RemoveAt(0);
            return true;
        }
    }

    public byte[]? Poster(string itemId) => _posters.GetValueOrDefault(itemId);

    public FixtureStats Stats()
    {
        lock (_gate)
        {
            return new FixtureStats
            {
                Synthetic = true, Description = "Local development fixture; not an Emby compatibility result.",
                LoginCount = Volatile.Read(ref _loginCount), AuthenticationFailures = Volatile.Read(ref _authenticationFailures),
                PlaybackInfoCount = Volatile.Read(ref _playbackInfoCount), MediaRequests = Volatile.Read(ref _mediaRequests),
                RangeRequests = Volatile.Read(ref _rangeRequests), PartialResponses = Volatile.Read(ref _partialResponses),
                CapabilityUpdates = Volatile.Read(ref _capabilityUpdates), EncodingCleanups = Volatile.Read(ref _encodingCleanups),
                MediaLength = _mediaLength, StartCount = _events.Count(item => item.Kind == "Start"),
                ProgressCount = _events.Count(item => item.Kind == "Progress"), StopCount = _events.Count(item => item.Kind == "Stop"),
                Events = _events.ToArray()
            };
        }
    }

    private BaseItemDto WithUserData(BaseItemDto item) => item with { UserData = _userData[item.Id!] };

    private bool IsDescendant(BaseItemDto item, string parent)
    {
        var parentId = item.ParentId;
        for (var count = 0; count < 10 && parentId is not null; count++)
        {
            if (parentId == parent) return true;
            parentId = _items.TryGetValue(parentId, out var ancestor) ? ancestor.ParentId : null;
        }
        return false;
    }

    private MediaSourceInfo Source(string itemId, string? playSessionId = null) => new()
    {
        Id = "synthetic-mp4", Name = "Synthetic H.264 AAC 720p", Container = "mp4", Protocol = "Http",
        RunTimeTicks = DurationTicks, Size = _mediaLength,
        Bitrate = (long)(_mediaLength * 8.0 / TimeSpan.FromTicks(DurationTicks).TotalSeconds),
        SupportsDirectPlay = false, SupportsDirectStream = true, SupportsTranscoding = false,
        DirectStreamUrl = playSessionId is null ? null : $"/emby/Videos/{itemId}/stream?Static=true&MediaSourceId=synthetic-mp4&PlaySessionId={playSessionId}",
        AddApiKeyToDirectStreamUrl = false, DefaultAudioStreamIndex = 1, DefaultSubtitleStreamIndex = -1,
        RequiresOpening = false, RequiresClosing = false, IsInfiniteStream = false,
        MediaStreams =
        [
            new MediaStream { Index = 0, Type = "Video", Codec = "h264", Width = Options.Media.Width, Height = Options.Media.Height, BitDepth = 8, IsDefault = true, DisplayTitle = "Synthetic 720p H.264" },
            new MediaStream { Index = 1, Type = "Audio", Codec = "aac", Language = "eng", Channels = Options.Media.AudioChannels, ChannelLayout = "stereo", IsDefault = true, DisplayTitle = "Synthetic 440 Hz tone - AAC stereo" }
        ]
    };

    private IEnumerable<BaseItemDto> CreateCatalog()
    {
        BaseItemDto Item(string id, string name, string type, string? parent = null) => new()
        {
            Id = id, Name = name, Type = type, ParentId = parent,
            Overview = "SYNTHETIC DEVELOPMENT DATA. This locally generated color-and-tone fixture is not real library content or evidence of Emby compatibility.",
            ImageTags = new Dictionary<string, string> { ["Primary"] = "synthetic-poster-v1" },
            PrimaryImageAspectRatio = 2.0 / 3, ProductionYear = 2026,
            IsFolder = type is "CollectionFolder" or "Series" or "Season",
            MediaType = type is "Movie" or "Episode" ? "Video" : null,
            RunTimeTicks = type is "Movie" or "Episode" ? DurationTicks : null,
            Genres = ["Synthetic"], GenreItems = [new NameLongIdPair { Id = 1, Name = "Synthetic" }],
            MediaSources = type is "Movie" or "Episode" ? [Source(id)] : null
        };
        yield return Item("movies", "Synthetic Movies", "CollectionFolder") with { CollectionType = "movies" };
        yield return Item("shows", "Synthetic Television", "CollectionFolder") with { CollectionType = "tvshows" };
        yield return Item("1001", "Synthetic Color Study", "Movie", "movies");
        yield return Item("1002", "Synthetic Motion Sample", "Movie", "movies");
        yield return Item("2000", "Synthetic Screen Tests", "Series", "shows") with { ChildCount = 2 };
        yield return Item("2100", "Season 1", "Season", "2000") with { SeriesId = "2000", SeriesName = "Synthetic Screen Tests", IndexNumber = 1 };
        yield return Item("2101", "Synthetic Episode One", "Episode", "2100") with { SeriesId = "2000", SeriesName = "Synthetic Screen Tests", SeasonId = "2100", IndexNumber = 1, ParentIndexNumber = 1 };
        yield return Item("2102", "Synthetic Episode Two", "Episode", "2100") with { SeriesId = "2000", SeriesName = "Synthetic Screen Tests", SeasonId = "2100", IndexNumber = 2, ParentIndexNumber = 1 };
    }

    private static string? Value(IQueryCollection query, string name) => query.TryGetValue(name, out var value) ? value.ToString() : null;
    private static string[] Values(IQueryCollection query, string name) => Value(query, name)?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [];
    private static bool? Boolean(IQueryCollection query, string name) => bool.TryParse(Value(query, name), out var value) ? value : null;
    private static int Integer(IQueryCollection query, string name, int fallback, int minimum, int maximum) =>
        int.TryParse(Value(query, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, minimum, maximum) : fallback;
}

internal sealed record FixturePlaybackEvent(string Kind, string ItemId, string PlaySessionId, long PositionTicks,
    string? EventName, bool? Failed, DateTimeOffset Timestamp);

internal sealed class FixtureStats
{
    public bool Synthetic { get; init; }
    public string Description { get; init; } = "";
    public int LoginCount { get; init; }
    public int AuthenticationFailures { get; init; }
    public int PlaybackInfoCount { get; init; }
    public int MediaRequests { get; init; }
    public int RangeRequests { get; init; }
    public int PartialResponses { get; init; }
    public int CapabilityUpdates { get; init; }
    public int EncodingCleanups { get; init; }
    public int StartCount { get; init; }
    public int ProgressCount { get; init; }
    public int StopCount { get; init; }
    public long MediaLength { get; init; }
    public FixturePlaybackEvent[] Events { get; init; } = [];
}
