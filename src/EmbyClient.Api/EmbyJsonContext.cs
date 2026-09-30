using System.Text.Json.Serialization;

namespace EmbyClient.Api;

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(PublicSystemInfo))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(UserDto[]))]
[JsonSerializable(typeof(AuthenticationResult))]
[JsonSerializable(typeof(AuthenticateByNameRequest))]
[JsonSerializable(typeof(BaseItemDto))]
[JsonSerializable(typeof(BaseItemDto[]))]
[JsonSerializable(typeof(NameLongIdPair[]))]
[JsonSerializable(typeof(QueryResult<BaseItemDto>))]
[JsonSerializable(typeof(UserItemDataDto))]
[JsonSerializable(typeof(PlaybackInfoRequest))]
[JsonSerializable(typeof(PlaybackInfoResponse))]
[JsonSerializable(typeof(PlaybackStartInfo))]
[JsonSerializable(typeof(PlaybackProgressInfo))]
[JsonSerializable(typeof(PlaybackStopInfo))]
[JsonSerializable(typeof(LiveStreamRequest))]
[JsonSerializable(typeof(LiveStreamResponse))]
[JsonSerializable(typeof(ClientCapabilities))]
[JsonSerializable(typeof(CollectionCreationResult))]
[JsonSerializable(typeof(BaseRefreshRequest))]
[JsonSerializable(typeof(ItemMetadataUpdate))]
[JsonSerializable(typeof(UserConfiguration))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
public partial class EmbyJsonContext : JsonSerializerContext;
