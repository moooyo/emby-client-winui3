namespace EmbyClient.App.ViewModels;

public sealed class MediaLibrarySelectionChangedEventArgs(string mediaType, string? libraryId) : EventArgs
{
    public string MediaType { get; } = mediaType;
    public string? LibraryId { get; } = libraryId;
}

public sealed partial class LibraryViewModel
{
    private readonly Dictionary<string, BrowseLocation> _mediaLibraryLocations = new(StringComparer.Ordinal);
    public event EventHandler<MediaLibrarySelectionChangedEventArgs>? MediaLibrarySelectionChanged;

    public bool IsPrimaryNavigationPage(string key) => _location.Kind switch
    {
        LocationKind.Home => key == "home",
        LocationKind.Favorites => key == "favs",
        LocationKind.Library when _location.ItemTypes is ["Movie"] => key == "movies",
        LocationKind.Library when _location.ItemTypes is ["Series"] => key == "series",
        _ => false
    };

    public void RestoreMediaLibrarySelection(string type, string? libraryId)
    {
        var itemTypes = MediaLibraryTypes(type);
        var selectedId = libraryId is not null && GetMediaLibraries(type).Any(library => library.Id == libraryId)
            ? libraryId : null;
        _mediaLibraryLocations[type] = new(LocationKind.Library, selectedId, MediaLibraryTitle(type), ItemTypes: itemTypes);
    }

    public Task ReconcileMediaLibrarySelectionAsync(CancellationToken cancellationToken = default)
    {
        if (_location.Kind == LocationKind.Library && _location.ItemTypes is [var type] && (type is "Movie" or "Series")
            && _location.ItemId is { } libraryId && !GetMediaLibraries(type).Any(library => library.Id == libraryId))
            return SelectMediaLibraryAsync(type, null, cancellationToken);
        return Task.CompletedTask;
    }

    public IReadOnlyList<MediaCardViewModel> GetMediaLibraries(string type)
    {
        _ = MediaLibraryTypes(type);
        return Libraries.Where(library => !string.IsNullOrWhiteSpace(library.Id)
            && LibraryContainsMediaType(library.Item.CollectionType, type)).ToArray();
    }

    public static bool LibraryContainsMediaType(string? collectionType, string type) =>
        collectionType?.Trim().ToLowerInvariant() switch
        {
            "movies" => type == "Movie",
            "tvshows" => type == "Series",
            // Emby also uses an unspecified collection type for mixed libraries.
            null or "" or "mixed" => type is "Movie" or "Series",
            _ => false
        };

    public static string? PrimaryNavigationForLibrary(string? collectionType, string? preferredType = null) =>
        collectionType?.Trim().ToLowerInvariant() switch
        {
            "movies" => "movies",
            "tvshows" => "series",
            null or "" or "mixed" => preferredType == "Series" ? "series" : "movies",
            _ => null
        };

    public Task SelectMediaLibraryAsync(string type, string? libraryId, CancellationToken cancellationToken = default)
    {
        var itemTypes = MediaLibraryTypes(type);
        if (libraryId is not null && !GetMediaLibraries(type).Any(library => library.Id == libraryId))
            return Task.CompletedTask;
        var current = _pendingBrowseLocation ?? _location;
        if (current.Kind == LocationKind.Library && current.ItemTypes is [var currentType]
            && currentType == type && current.ItemId == libraryId && _loadOutcome == PageLoadOutcome.Succeeded)
            return Task.CompletedTask;
        RememberMediaLibraryLocation();
        var source = current.Kind == LocationKind.Library && current.ItemTypes is [var sourceType] && sourceType == type
            ? current : RememberedMediaLibraryLocation(type);
        var location = source with
        {
            Kind = LocationKind.Library,
            ItemId = libraryId,
            Title = MediaLibraryTitle(type),
            ItemTypes = itemTypes,
            Genre = null,
            NameStartsWith = null,
            QualityFilter = null,
            QualityCatalog = null
        };
        CancelSearch();
        // A library selection updates the current primary page without inserting a history entry.
        return NavigateAsync(location, false, cancellationToken, preserveForwardHistory: true);
    }

    public Task ShowLatestMediaTypeAsync(string type, CancellationToken cancellationToken = default)
    {
        var itemTypes = MediaLibraryTypes(type);
        RememberMediaLibraryLocation();
        CancelSearch();
        return NavigateAsync(new(LocationKind.Library, null, MediaLibraryTitle(type), ItemTypes: itemTypes,
            SortKey: "DateCreated", SortDescending: true), false, cancellationToken, resetHistory: true);
    }

    private Task NavigateMediaTypeAsync(string type, CancellationToken cancellationToken)
    {
        _ = MediaLibraryTypes(type);
        RememberMediaLibraryLocation();
        CancelSearch();
        var location = RememberedMediaLibraryLocation(type);
        if (_location == location && _loadOutcome == PageLoadOutcome.Succeeded) return Task.CompletedTask;
        return NavigateAsync(location, false, cancellationToken, resetHistory: true);
    }

    private Task NavigateTypedLibraryAsync(MediaCardViewModel library, string type, CancellationToken cancellationToken)
    {
        RememberMediaLibraryLocation();
        var source = RememberedMediaLibraryLocation(type);
        var changedLibrary = source.ItemId != library.Id;
        var location = source with
        {
            ItemId = library.Id,
            Genre = changedLibrary ? null : source.Genre,
            NameStartsWith = changedLibrary ? null : source.NameStartsWith,
            QualityFilter = changedLibrary ? null : source.QualityFilter,
            QualityCatalog = changedLibrary ? null : source.QualityCatalog
        };
        CancelSearch();
        return NavigateAsync(location, false, cancellationToken, resetHistory: true);
    }

    private BrowseLocation RememberedMediaLibraryLocation(string type)
    {
        var itemTypes = MediaLibraryTypes(type);
        if (!_mediaLibraryLocations.TryGetValue(type, out var location))
            return new(LocationKind.Library, null, MediaLibraryTitle(type), ItemTypes: itemTypes);
        if (location.ItemId is null || GetMediaLibraries(type).Any(library => library.Id == location.ItemId))
            return location with { Title = MediaLibraryTitle(type), ItemTypes = itemTypes };
        // Removed or inaccessible libraries fall back to the account's complete typed collection.
        return location with
        {
            ItemId = null, Title = MediaLibraryTitle(type), ItemTypes = itemTypes,
            Genre = null, NameStartsWith = null, QualityFilter = null, QualityCatalog = null
        };
    }

    private void RememberMediaLibraryLocation()
    {
        if (_location.Kind == LocationKind.Library && _location.ItemTypes is [var type] && (type is "Movie" or "Series"))
        {
            var changed = !_mediaLibraryLocations.TryGetValue(type, out var previous) || previous.ItemId != _location.ItemId;
            _mediaLibraryLocations[type] = _location;
            if (changed) MediaLibrarySelectionChanged?.Invoke(this, new(type, _location.ItemId));
        }
    }

    private static string MediaLibraryTitle(string type) => type == "Movie" ? "Movies" : "TV shows";

    private static string[] MediaLibraryTypes(string type) => type switch
    {
        "Movie" => LumenMovieTypes,
        "Series" => LumenSeriesTypes,
        _ => throw new ArgumentException("The media type must be Movie or Series.", nameof(type))
    };
}
