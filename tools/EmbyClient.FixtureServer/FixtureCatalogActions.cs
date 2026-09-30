using System.Globalization;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed partial class FixtureState
{
    private readonly HashSet<string> _hiddenResume = new(StringComparer.Ordinal);
    private UserConfiguration _configuration = new() { AudioLanguagePreference = "eng", SubtitleMode = "None" };
    private int _metadataUpdates;
    private int _metadataRefreshRequests;
    private int _collectionUpdates;
    private int _configurationUpdates;
    private int _createdCollections;

    public UserItemDataDto? HideFromResume(string itemId, bool hide)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item)) return null;
            if (!IsPlayable(item) && item.Type != "Series") return null;
            var affected = item.Type == "Series" ? _items.Values.Where(candidate => IsPlayable(candidate) && IsDescendant(candidate, itemId)).Select(candidate => candidate.Id!).ToArray()
                : [itemId];
            foreach (var id in affected)
            {
                if (hide) _hiddenResume.Add(id);
                else _hiddenResume.Remove(id);
            }
            return WithUserData(item).UserData;
        }
    }

    public (int StatusCode, FixtureCollectionResult? Collection) CreateCollection(string? name, string[] ids)
    {
        lock (_gate)
        {
            if (!Options.LumenCatalog) return (StatusCodes.Status403Forbidden, null);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || !ValidCollectionMembers(ids)
                || _collectionMembers.Count >= 32) return (StatusCodes.Status400BadRequest, null);
            var id = $"collection-created-{++_createdCollections:D4}";
            var members = ids.ToHashSet(StringComparer.Ordinal);
            var art = ids.Select(member => _artworkSelections.GetValueOrDefault(member)).FirstOrDefault(selection => selection is not null)
                ?? _artworkSelections["1001"];
            var item = LumenItem(id, name.Trim(), "BoxSet", "collections", art.Primary, art.Backdrop, art.Thumb) with
            {
                Overview = "An in-memory collection created in the synthetic Lumen fixture.", ChildCount = members.Count,
                MovieCount = members.Count(member => _items[member].Type == "Movie"), SeriesCount = members.Count(member => _items[member].Type == "Series")
            };
            _items[id] = item;
            _userData[id] = new UserItemDataDto { ItemId = id, Key = id, IsFavorite = false, Played = false, PlaybackPositionTicks = 0, PlayCount = 0 };
            _collectionMembers[id] = members;
            _items["collections"] = _items["collections"] with { ChildCount = _collectionMembers.Count };
            _collectionUpdates++;
            return (StatusCodes.Status200OK, new FixtureCollectionResult { Id = id, Name = item.Name });
        }
    }

    public int UpdateCollection(string collectionId, string[] ids, bool remove = false)
    {
        lock (_gate)
        {
            if (!Options.LumenCatalog) return StatusCodes.Status403Forbidden;
            if (!_collectionMembers.TryGetValue(collectionId, out var members)) return StatusCodes.Status404NotFound;
            if (!ValidCollectionMembers(ids)) return StatusCodes.Status400BadRequest;
            if (remove) members.ExceptWith(ids);
            else members.UnionWith(ids);
            _collectionUpdates++;
            return StatusCodes.Status204NoContent;
        }
    }

    private bool ValidCollectionMembers(string[] ids) => ids.Length is > 0 and <= 50
        && ids.All(id => _items.TryGetValue(id, out var item) && item.Type is "Movie" or "Series");

    public int RefreshMetadata(string itemId)
    {
        lock (_gate)
        {
            if (!Options.LumenCatalog) return StatusCodes.Status403Forbidden;
            if (!_items.TryGetValue(itemId, out var item)) return StatusCodes.Status404NotFound;
            if (item.CanEditItems != true) return StatusCodes.Status403Forbidden;
            _metadataRefreshRequests++;
            return StatusCodes.Status204NoContent;
        }
    }

    public int UpdateMetadata(string itemId, BaseItemDto? request)
    {
        lock (_gate)
        {
            if (!Options.LumenCatalog) return StatusCodes.Status403Forbidden;
            if (!_items.TryGetValue(itemId, out var item)) return StatusCodes.Status404NotFound;
            if (item.CanEditItems != true) return StatusCodes.Status403Forbidden;
            if (request is null || request.Id is not null && request.Id != itemId
                || request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 256)
                || request.OriginalTitle?.Length > 256 || request.Overview?.Length > 32768 || request.OfficialRating?.Length > 64
                || request.ProductionYear is < 1000 or > 3000 || !ValidLabels(request.Genres) || !ValidLabels(request.Tags)
                || !ValidLabels(request.LockedFields)) return StatusCodes.Status400BadRequest;
            var genres = request.Genres ?? item.Genres;
            if (genres is not null && _items.Values.Count(candidate => candidate.Type == "Genre")
                + genres.Distinct(StringComparer.OrdinalIgnoreCase).Count(genre => !_items.Values.Any(candidate => candidate.Type == "Genre"
                    && string.Equals(candidate.Name, genre, StringComparison.OrdinalIgnoreCase))) > 64)
                return StatusCodes.Status400BadRequest;
            if (genres is not null) EnsureGenreNodes(genres);
            _items[itemId] = item with
            {
                Name = request.Name?.Trim() ?? item.Name, SortName = request.Name?.Trim() ?? item.SortName,
                OriginalTitle = request.OriginalTitle ?? item.OriginalTitle, Overview = request.Overview ?? item.Overview,
                ProductionYear = request.ProductionYear ?? item.ProductionYear, OfficialRating = request.OfficialRating ?? item.OfficialRating,
                Genres = genres, GenreItems = genres?.Select(genre => new NameLongIdPair
                {
                    Name = genre, Id = long.Parse(_items.Values.Single(candidate => candidate.Type == "Genre" && string.Equals(candidate.Name, genre, StringComparison.OrdinalIgnoreCase)).Id!, CultureInfo.InvariantCulture)
                }).ToArray(),
                Tags = request.Tags ?? item.Tags, LockData = request.LockData ?? item.LockData, LockedFields = request.LockedFields ?? item.LockedFields
            };
            _metadataUpdates++;
            return StatusCodes.Status204NoContent;
        }
    }

    private void EnsureGenreNodes(string[] genres)
    {
        foreach (var genre in genres.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_items.Values.Any(item => item.Type == "Genre" && string.Equals(item.Name, genre, StringComparison.OrdinalIgnoreCase))) continue;
            var id = (50001 + _items.Values.Count(item => item.Type == "Genre")).ToString(CultureInfo.InvariantCulture);
            _items[id] = LumenItem(id, genre, "Genre", null, "515") with
            {
                CanEditItems = false, Overview = "A genre added to the in-memory synthetic Lumen catalog."
            };
            _userData[id] = new UserItemDataDto { ItemId = id, Key = id, IsFavorite = false, Played = false, PlaybackPositionTicks = 0, PlayCount = 0 };
        }
    }

    private static bool ValidLabels(string[]? labels) => labels is null || labels.Length <= 32
        && labels.All(label => !string.IsNullOrWhiteSpace(label) && label.Length <= 128);

    public int UpdateConfiguration(UserConfiguration? configuration)
    {
        lock (_gate)
        {
            if (configuration is null || configuration.ResumeRewindSeconds is < 0 or > 60
                || configuration.IntroSkipMode is not (null or "ShowButton" or "AutoSkip" or "None")
                || configuration.AudioLanguagePreference?.Length > 16 || configuration.SubtitleLanguagePreference?.Length > 16
                || configuration.SubtitleMode?.Length > 32) return StatusCodes.Status400BadRequest;
            _configuration = configuration;
            _configurationUpdates++;
            return StatusCodes.Status204NoContent;
        }
    }
}

internal sealed class FixtureCollectionResult
{
    public string? Id { get; init; }
    public string? Name { get; init; }
}
