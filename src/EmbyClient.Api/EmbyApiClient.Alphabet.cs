using System.Text.Json.Serialization;

namespace EmbyClient.Api;

public sealed partial class EmbyApiClient
{
    /// <summary>Gets available name prefixes within the supplied query and the current user's scope.</summary>
    public Task<ItemPrefixDto[]> GetItemPrefixesAsync(ItemQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var userId = RequireUserId();
        return GetAsync("Items/Prefixes", BuildItemQuery(query).Add("UserId", userId),
            EmbyAlphabetJsonContext.Default.ItemPrefixDtoArray, cancellationToken);
    }
}

/// <summary>Preserves a server prefix label and its opaque value.</summary>
public sealed record ItemPrefixDto
{
    public string? Name { get; init; }
    public string? Value { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ItemPrefixDto[]))]
internal partial class EmbyAlphabetJsonContext : JsonSerializerContext;
