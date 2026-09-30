using System.Text.Json;

namespace EmbyClient.Api;

public sealed partial class EmbyApiClient
{
    private const int MaximumCapabilityPageSize = 500;
    private const int MaximumCollectionMutationItems = 100;
    private const int MaximumMetadataMutationBytes = 4 * 1024 * 1024;
    private readonly SemaphoreSlim _metadataMutationGate = new(1, 1);

    public Task<QueryResult<BaseItemDto>> GetSimilarItemsAsync(string itemId, int limit = 16,
        CancellationToken cancellationToken = default)
    {
        ValidateCapabilityLimit(limit);
        return GetItemsResultAsync($"Items/{Segment(itemId)}/Similar",
            new Query().Add("UserId", RequireUserId()).Add("Limit", limit)
                .AddList("Fields", ["Overview", "Genres", "MediaStreams", "PrimaryImageAspectRatio"])
                .Add("EnableUserData", true).Add("EnableImages", true)
                .AddList("EnableImageTypes", ["Primary", "Thumb", "Backdrop"]).Add("ImageTypeLimit", 1),
            cancellationToken);
    }

    public Task<BaseItemDto[]> GetLocalTrailersAsync(string itemId,
        CancellationToken cancellationToken = default) =>
        GetAsync($"Users/{Segment(RequireUserId())}/Items/{Segment(itemId)}/LocalTrailers", null,
            EmbyJsonContext.Default.BaseItemDtoArray, cancellationToken);

    public Task<QueryResult<BaseItemDto>> GetGenresAsync(ItemQuery? query = null,
        CancellationToken cancellationToken = default) =>
        GetItemsByNameAsync("Genres", query, cancellationToken);

    public Task<QueryResult<BaseItemDto>> GetPersonsAsync(ItemQuery? query = null,
        CancellationToken cancellationToken = default) =>
        GetItemsByNameAsync("Persons", query, cancellationToken);

    public Task<UserItemDataDto> RemoveFromResumeAsync(string itemId,
        CancellationToken cancellationToken = default) =>
        SetHideFromResumeAsync(itemId, true, cancellationToken);

    public Task<UserItemDataDto> SetHideFromResumeAsync(string itemId, bool hide,
        CancellationToken cancellationToken = default) =>
        SendJsonAsync(HttpMethod.Post,
            $"Users/{Segment(RequireUserId())}/Items/{Segment(itemId)}/HideFromResume",
            new Query().Add("Hide", hide), null, EmbyJsonContext.Default.UserItemDataDto, cancellationToken);

    public async Task<CollectionCreationResult> CreateCollectionAsync(string name, string[] itemIds,
        bool isLocked = false, CancellationToken cancellationToken = default)
    {
        ValidateMutationText(name, nameof(name), 1000, allowEmpty: false);
        var ids = ValidateCollectionIds(itemIds);
        var result = await SendJsonAsync(HttpMethod.Post, "Collections",
            new Query().Add("Name", name).AddList("Ids", ids).Add("IsLocked", isLocked), null,
            EmbyJsonContext.Default.CollectionCreationResult, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Id))
            throw new EmbyProtocolException("Emby returned a collection without an identifier.");
        return result;
    }

    public Task AddToCollectionAsync(string collectionId, string[] itemIds,
        CancellationToken cancellationToken = default) =>
        SendEmptyAsync(HttpMethod.Post, $"Collections/{Segment(collectionId)}/Items",
            new Query().AddList("Ids", ValidateCollectionIds(itemIds)), null, cancellationToken);

    public Task RefreshItemMetadataAsync(string itemId, MetadataRefreshOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new();
        ValidateRefreshMode(options.MetadataRefreshMode, nameof(options.MetadataRefreshMode));
        ValidateRefreshMode(options.ImageRefreshMode, nameof(options.ImageRefreshMode));
        return PostEmptyAsync($"Items/{Segment(itemId)}/Refresh",
            new Query().Add("Recursive", options.Recursive).Add("MetadataRefreshMode", options.MetadataRefreshMode)
                .Add("ImageRefreshMode", options.ImageRefreshMode).Add("ReplaceAllMetadata", options.ReplaceAllMetadata)
                .Add("ReplaceAllImages", options.ReplaceAllImages),
            new BaseRefreshRequest { ReplaceThumbnailImages = options.ReplaceThumbnailImages },
            EmbyJsonContext.Default.BaseRefreshRequest, cancellationToken);
    }

    public async Task UpdateItemMetadataAsync(string itemId, ItemMetadataUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var itemPath = Segment(itemId);
        var userPath = Segment(RequireUserId());
        ValidateMetadataUpdate(update);
        var changes = JsonSerializer.SerializeToElement(update, EmbyJsonContext.Default.ItemMetadataUpdate);
        if (!changes.EnumerateObject().Any()) return;

        await _metadataMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // The update route is POST, not PATCH. Preserve the full server object, including nested unknown fields.
            var current = await GetAsync($"Users/{userPath}/Items/{itemPath}", null,
                EmbyJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
            EnsureJsonIdentity(current, itemId);
            var merged = MergeJsonObjects(current, SynchronizeTagItemEdits(current, changes, update.Tags));
            await PostEmptyAsync($"Items/{itemPath}", null, merged,
                EmbyJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        }
        finally { _metadataMutationGate.Release(); }
    }

    public async Task UpdateUserConfigurationAsync(UserConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateUserConfiguration(configuration);
        var userId = RequireUserId();
        var userPath = Segment(userId);
        var changes = JsonSerializer.SerializeToElement(configuration, EmbyJsonContext.Default.UserConfiguration);
        if (!changes.EnumerateObject().Any()) return;

        // Serialize read/merge/write cycles on this immutable client so concurrent sparse edits do not overwrite one another.
        await _metadataMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var user = await GetAsync($"Users/{userPath}", null, EmbyJsonContext.Default.JsonElement,
                cancellationToken).ConfigureAwait(false);
            EnsureJsonIdentity(user, userId);
            if (!TryGetJsonProperty(user, "Configuration", out var current)
                || current.ValueKind != JsonValueKind.Object)
                throw new EmbyProtocolException("Emby returned a user without an editable configuration object.");
            var merged = MergeJsonObjects(current, changes);
            await PostEmptyAsync($"Users/{userPath}/Configuration", null, merged,
                EmbyJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        }
        finally { _metadataMutationGate.Release(); }
    }

    private Task<QueryResult<BaseItemDto>> GetItemsByNameAsync(string resource, ItemQuery? query,
        CancellationToken cancellationToken)
    {
        query ??= new();
        var limit = query.Limit ?? MaximumCapabilityPageSize;
        ValidateCapabilityLimit(limit);
        return GetItemsResultAsync(resource, BuildItemQuery(query with { Limit = limit })
            .Add("UserId", RequireUserId()), cancellationToken);
    }

    private static void ValidateCapabilityLimit(int limit)
    {
        if (limit is < 1 or > MaximumCapabilityPageSize)
            throw new ArgumentOutOfRangeException(nameof(limit), "The page size must be between 1 and 500.");
    }

    private static string[] ValidateCollectionIds(string[] itemIds)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        if (itemIds.Length is < 1 or > MaximumCollectionMutationItems)
            throw new ArgumentOutOfRangeException(nameof(itemIds), "A collection mutation must contain between 1 and 100 items.");
        foreach (var id in itemIds)
        {
            ValidateMutationText(id, nameof(itemIds), 1000, allowEmpty: false);
            if (id.Contains(',') || id is "." or "..")
                throw new ArgumentException("A collection identifier cannot contain a list separator or be a relative path segment.", nameof(itemIds));
        }
        return itemIds.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void ValidateRefreshMode(string mode, string parameter)
    {
        if (mode is not ("ValidationOnly" or "Default" or "FullRefresh"))
            throw new ArgumentException("Use a documented metadata refresh mode.", parameter);
    }

    private static void ValidateMetadataUpdate(ItemMetadataUpdate update)
    {
        if (update.Name is not null) ValidateMutationText(update.Name, nameof(update.Name), 1000, allowEmpty: false);
        if (update.OriginalTitle is not null) ValidateMutationText(update.OriginalTitle, nameof(update.OriginalTitle), 1000);
        if (update.Overview is not null) ValidateMutationText(update.Overview, nameof(update.Overview), 20_000, allowMultiline: true);
        if (update.OfficialRating is not null) ValidateMutationText(update.OfficialRating, nameof(update.OfficialRating), 500);
        if (update.ProductionYear is < 1 or > 9999)
            throw new ArgumentOutOfRangeException(nameof(update.ProductionYear), "The production year must be between 1 and 9999.");
        ValidateMetadataList(update.Genres, nameof(update.Genres));
        ValidateMetadataList(update.Tags, nameof(update.Tags));
        ValidateMetadataList(update.LockedFields, nameof(update.LockedFields));
    }

    private static void ValidateUserConfiguration(UserConfiguration configuration)
    {
        if (configuration.AudioLanguagePreference is not null)
            ValidateMutationText(configuration.AudioLanguagePreference, nameof(configuration.AudioLanguagePreference), 100);
        if (configuration.SubtitleLanguagePreference is not null)
            ValidateMutationText(configuration.SubtitleLanguagePreference, nameof(configuration.SubtitleLanguagePreference), 100);
        if (configuration.ResumeRewindSeconds is < 0 or > 300)
            throw new ArgumentOutOfRangeException(nameof(configuration.ResumeRewindSeconds), "Resume rewind must be between 0 and 300 seconds.");
        if (configuration.IntroSkipMode is not (null or "ShowButton" or "AutoSkip" or "None"))
            throw new ArgumentException("Use a documented segment skip mode.", nameof(configuration.IntroSkipMode));
        if (configuration.SubtitleMode is not (null or "Default" or "Always" or "OnlyForced" or "None" or "Smart" or "HearingImpaired"))
            throw new ArgumentException("Use a documented subtitle playback mode.", nameof(configuration.SubtitleMode));
        if (configuration.ExtensionData is not { } extensions) return;
        foreach (var (name, value) in extensions)
        {
            if (value.ValueKind == JsonValueKind.Undefined
                || EmbyJsonContext.Default.UserConfiguration.Properties.Any(property => !property.IsExtensionData
                    && string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Extension data cannot replace a known configuration field or contain undefined JSON.", nameof(configuration));
        }
    }

    private static void ValidateMetadataList(string[]? values, string parameter)
    {
        if (values is null) return;
        if (values.Length > 100) throw new ArgumentOutOfRangeException(parameter, "Metadata lists cannot contain more than 100 values.");
        foreach (var value in values) ValidateMutationText(value, parameter, 500, allowEmpty: false);
    }

    private static void ValidateMutationText(string value, string parameter, int maximumLength,
        bool allowEmpty = true, bool allowMultiline = false)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (value.Length > maximumLength || !allowEmpty && string.IsNullOrWhiteSpace(value)
            || value.Any(character => char.IsControl(character) && !(allowMultiline && character is '\r' or '\n' or '\t')))
            throw new ArgumentException("The value is empty, too long, or contains an unsupported control character.", parameter);
    }

    private static void EnsureJsonIdentity(JsonElement value, string expectedId)
    {
        if (value.ValueKind != JsonValueKind.Object || !TryGetJsonProperty(value, "Id", out var id)
            || id.ValueKind != JsonValueKind.String || !string.Equals(id.GetString(), expectedId, StringComparison.Ordinal))
            throw new EmbyProtocolException("Emby returned metadata for a different or unidentified resource.");
    }

    private static bool TryGetJsonProperty(JsonElement value, string name, out JsonElement result)
    {
        result = default;
        foreach (var property in value.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            result = property.Value;
            return true;
        }
        return false;
    }

    private static JsonElement MergeJsonObjects(JsonElement current, JsonElement changes)
    {
        var edits = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in changes.EnumerateObject())
        {
            if (!edits.TryAdd(property.Name, property.Value))
                throw new ArgumentException("A configuration update contains conflicting JSON properties.", nameof(changes));
        }
        var originalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in current.EnumerateObject())
            {
                if (!originalNames.Add(property.Name))
                    throw new EmbyProtocolException("Emby returned conflicting JSON metadata properties.");
                if (!edits.ContainsKey(property.Name)) property.WriteTo(writer);
            }
            foreach (var property in changes.EnumerateObject()) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        if (stream.Length > MaximumMetadataMutationBytes)
            throw new EmbyProtocolException("The metadata update exceeds the client size limit.");
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement SynchronizeTagItemEdits(JsonElement current, JsonElement changes, string[]? tags)
    {
        if (tags is null) return changes;
        var hasTagItems = TryGetJsonProperty(current, "TagItems", out var tagItems);
        // A response exposing only legacy Tags keeps its existing write contract.
        if (!hasTagItems && TryGetJsonProperty(current, "Tags", out _)) return changes;
        var existing = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (hasTagItems && tagItems.ValueKind != JsonValueKind.Null)
        {
            if (tagItems.ValueKind != JsonValueKind.Array)
                throw new EmbyProtocolException("Emby returned an unsupported tag-item collection.");
            foreach (var item in tagItems.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new EmbyProtocolException("Emby returned an unsupported tag-item value.");
                var named = false;
                string? name = null;
                foreach (var property in item.EnumerateObject())
                {
                    if (!string.Equals(property.Name, "Name", StringComparison.OrdinalIgnoreCase)) continue;
                    if (named || property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw new EmbyProtocolException("Emby returned a conflicting or unsupported tag-item name.");
                    named = true;
                    name = property.Value.GetString();
                }
                if (!string.IsNullOrEmpty(name)) existing.TryAdd(name, item);
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in changes.EnumerateObject()) property.WriteTo(writer);
            writer.WritePropertyName("TagItems");
            writer.WriteStartArray();
            foreach (var name in tags)
            {
                if (existing.TryGetValue(name, out var original)) original.WriteTo(writer);
                else
                {
                    // New names deliberately omit IDs; the server owns identity, deduplication, and ordering.
                    writer.WriteStartObject();
                    writer.WriteString("Name", name);
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }
}
