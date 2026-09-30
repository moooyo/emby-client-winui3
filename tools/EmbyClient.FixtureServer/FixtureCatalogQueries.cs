using System.Globalization;
using EmbyClient.Api;

namespace EmbyClient.FixtureServer;

internal sealed partial class FixtureState
{
    private readonly Dictionary<string, FixtureImage> _generatedImages = new(StringComparer.Ordinal);
    private byte[]? _userPhoto;

    public static bool IsPlayable(BaseItemDto item) => item.MediaType == "Video"
        && item.Type is "Movie" or "Episode" or "Trailer" && item.MediaSources is { Length: > 0 };

    public FixtureImage? Image(string itemId, string imageType, int index = 0)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var item)) return null;
            if (!Options.LumenCatalog)
                return Poster(itemId) is { } poster ? new FixtureImage(poster, "image/png", "synthetic-poster-v1") : null;
            if (imageType is not ("Primary" or "Backdrop" or "Thumb" or "Chapter") || index < 0) return null;
            if (imageType == "Chapter")
            {
                if (item.Chapters is null || index >= item.Chapters.Length) return null;
            }
            else if (index != 0) return null;
            if (_artwork is not null && _artworkSelections.TryGetValue(itemId, out var selection))
                return _artwork.Get(selection, imageType);
            var key = itemId + "/" + imageType;
            if (_generatedImages.TryGetValue(key, out var cached)) return cached;
            var seed = itemId.Sum(character => (int)character);
            var landscape = imageType != "Primary" || item.PrimaryImageAspectRatio > 1;
            var width = item.Type == "Person" ? 240 : landscape ? 480 : 240;
            var height = item.Type == "Person" ? 240 : landscape ? 270 : 360;
            var bytes = FixturePng.Create(width, height, (40 + seed * 17 % 140, 50 + seed * 31 % 135, 65 + seed * 11 % 125));
            return _generatedImages[key] = new FixtureImage(bytes, "image/png", "lumen-generated-" + key.Replace('/', '-') + "-v1");
        }
    }

    public FixtureImage UserImage()
    {
        lock (_gate)
        {
            _userPhoto ??= FixturePng.Create(240, 240, (65, 116, 154));
            return new FixtureImage(_userPhoto, "image/png", "synthetic-user-v1");
        }
    }

    private BaseItemDto WithArtworkTags(BaseItemDto item)
    {
        if (_artwork is null || !_artworkSelections.TryGetValue(item.Id!, out var selection)) return item;
        var tags = new Dictionary<string, string>(item.ImageTags ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            ["Primary"] = _artwork.Get(selection, "Primary").Tag,
            ["Thumb"] = _artwork.Get(selection, "Thumb").Tag
        };
        return item with
        {
            ImageTags = tags, BackdropImageTags = [_artwork.Get(selection, "Backdrop").Tag],
            Chapters = item.Chapters?.Select(chapter => chapter with { ImageTag = _artwork.Get(selection, "Chapter").Tag }).ToArray()
        };
    }

    public QueryResult<BaseItemDto>? Similar(string itemId, IQueryCollection query)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(itemId, out var source)) return null;
            var candidates = _items.Values.Where(item => item.Id != itemId && item.Type == source.Type && item.Type is "Movie" or "Series").ToArray();
            var related = candidates.Where(item => (item.Genres ?? []).Intersect(source.Genres ?? [], StringComparer.OrdinalIgnoreCase).Any()).ToArray();
            return Query(query, parentOverride: "", operation: "Similar",
                idsOverride: (related.Length > 0 ? related : candidates).Select(item => item.Id!));
        }
    }

    public BaseItemDto[]? LocalTrailers(string itemId)
    {
        lock (_gate)
        {
            if (!_items.ContainsKey(itemId)) return null;
            return (_localTrailers.GetValueOrDefault(itemId) ?? []).Select(id => WithUserData(_items[id])).ToArray();
        }
    }

    public QueryResult<BaseItemDto> Facets(IQueryCollection query, string type)
    {
        lock (_gate)
        {
            IEnumerable<BaseItemDto> scope = _items.Values.Where(item => item.Type is "Movie" or "Series" or "Episode");
            var parent = Value(query, "ParentId");
            if (!string.IsNullOrEmpty(parent))
                scope = _collectionMembers.TryGetValue(parent, out var members) ? scope.Where(item => members.Contains(item.Id!))
                    : scope.Where(item => IsDescendant(item, parent));
            var types = Values(query, "IncludeItemTypes");
            if (types.Length > 0) scope = scope.Where(item => types.Contains(item.Type, StringComparer.OrdinalIgnoreCase));
            scope = ApplyMediaFilters(scope, query);
            var media = scope.ToArray();
            var selectedIds = type == "Person" ? media.SelectMany(item => item.People ?? []).Select(person => person.Id!)
                : _items.Values.Where(item => item.Type == "Genre" && media.Any(source => source.Genres?.Contains(item.Name!, StringComparer.OrdinalIgnoreCase) == true)).Select(item => item.Id!);
            return Query(query, parentOverride: "", typeOverride: type, operation: type == "Person" ? "Persons" : "Genres",
                idsOverride: selectedIds, facet: true);
        }
    }

    private bool MatchesSearch(BaseItemDto item, string search) => item.Name?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
        || Options.LumenCatalog && (item.OriginalTitle?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || item.Overview?.Contains(search, StringComparison.OrdinalIgnoreCase) == true
            || item.Tags?.Any(tag => tag.Contains(search, StringComparison.OrdinalIgnoreCase)) == true);

    private IEnumerable<BaseItemDto> ApplyMediaFilters(IEnumerable<BaseItemDto> items, IQueryCollection query)
    {
        var genres = DelimitedValues(query, "Genres");
        if (genres.Length > 0) items = items.Where(item => (item.Genres ?? []).Intersect(genres, StringComparer.OrdinalIgnoreCase).Any());
        var genreIds = Values(query, "GenreIds");
        if (genreIds.Length > 0) items = items.Where(item => (item.GenreItems ?? []).Any(genre => genreIds.Contains(genre.Id?.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)));
        var tags = DelimitedValues(query, "Tags");
        if (tags.Length > 0) items = items.Where(item => (item.Tags ?? []).Intersect(tags, StringComparer.OrdinalIgnoreCase).Any());
        var people = Values(query, "PersonIds");
        if (people.Length > 0) items = items.Where(item => (item.People ?? []).Any(person => people.Contains(person.Id, StringComparer.Ordinal)));
        var filters = Values(query, "Filters");
        if (filters.Contains("IsFavorite", StringComparer.OrdinalIgnoreCase)) items = items.Where(item => _userData[item.Id!].IsFavorite == true);
        if (filters.Contains("IsPlayed", StringComparer.OrdinalIgnoreCase)) items = items.Where(item => _userData[item.Id!].Played == true);
        if (filters.Contains("IsUnplayed", StringComparer.OrdinalIgnoreCase)) items = items.Where(item => _userData[item.Id!].Played != true);
        return items;
    }

    private static IEnumerable<BaseItemDto> ApplyNameFilters(IEnumerable<BaseItemDto> items, IQueryCollection query)
    {
        var starts = Value(query, "NameStartsWith");
        if (!string.IsNullOrEmpty(starts)) items = items.Where(item => SortName(item).StartsWith(starts, StringComparison.OrdinalIgnoreCase));
        var lower = Value(query, "NameStartsWithOrGreater");
        if (!string.IsNullOrEmpty(lower)) items = items.Where(item => StringComparer.OrdinalIgnoreCase.Compare(SortName(item), lower) >= 0);
        var upper = Value(query, "NameLessThan");
        if (!string.IsNullOrEmpty(upper)) items = items.Where(item => StringComparer.OrdinalIgnoreCase.Compare(SortName(item), upper) < 0);
        return items;
    }

    private IEnumerable<BaseItemDto> SortItems(IEnumerable<BaseItemDto> items, IQueryCollection query, string? typeOverride, string operation)
    {
        var fields = Values(query, "SortBy");
        var orders = Values(query, "SortOrder");
        if (Options.LumenDesignCatalog && operation == "Resume" && fields.Length == 0)
            return items.OrderByDescending(item => _userData[item.Id!].LastPlayedDate).ThenBy(item => item.Id, StringComparer.Ordinal);
        if (Options.LumenDesignCatalog && (fields.FirstOrDefault()?.Equals("DateCreated", StringComparison.OrdinalIgnoreCase) == true
            || fields.Length == 0 && operation == "Latest"))
        {
            var descending = orders.Length == 0 || orders[0].Equals("Descending", StringComparison.OrdinalIgnoreCase);
            return descending ? items.OrderBy(DesignLatestRank).ThenBy(item => item.Id, StringComparer.Ordinal)
                : items.OrderByDescending(DesignLatestRank).ThenBy(item => item.Id, StringComparer.Ordinal);
        }
        if (fields.Length == 0) fields = operation == "Latest" ? ["PremiereDate"]
            : typeOverride == "Episode" || operation == "NextUp" ? ["ParentIndexNumber", "IndexNumber"]
            : typeOverride == "Season" ? ["IndexNumber"] : ["SortName"];
        return items.OrderBy(item => item, Comparer<BaseItemDto>.Create((left, right) =>
        {
            for (var index = 0; index < fields.Length; index++)
            {
                var comparison = fields[index].ToUpperInvariant() switch
                {
                    "PRODUCTIONYEAR" => Nullable.Compare(left.ProductionYear, right.ProductionYear),
                    "DATECREATED" or "PREMIEREDATE" => Nullable.Compare(left.PremiereDate, right.PremiereDate),
                    "COMMUNITYRATING" => Nullable.Compare(left.CommunityRating, right.CommunityRating),
                    "RUNTIME" => Nullable.Compare(left.RunTimeTicks, right.RunTimeTicks),
                    "PARENTINDEXNUMBER" => Nullable.Compare(left.ParentIndexNumber, right.ParentIndexNumber),
                    "INDEXNUMBER" => Nullable.Compare(left.IndexNumber, right.IndexNumber),
                    "DATEPLAYED" => Nullable.Compare(_userData[left.Id!].LastPlayedDate, _userData[right.Id!].LastPlayedDate),
                    _ => StringComparer.OrdinalIgnoreCase.Compare(SortName(left), SortName(right))
                };
                var order = orders.Length > index ? orders[index] : orders.FirstOrDefault();
                if (order?.Equals("Descending", StringComparison.OrdinalIgnoreCase) == true || orders.Length == 0 && operation == "Latest")
                    comparison = -comparison;
                if (comparison != 0) return comparison;
            }
            return StringComparer.Ordinal.Compare(left.Id, right.Id);
        }));
    }

    private static string SortName(BaseItemDto item) => item.SortName ?? item.Name ?? "";
    private static string[] DelimitedValues(IQueryCollection query, string name) => Value(query, name)?.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}
