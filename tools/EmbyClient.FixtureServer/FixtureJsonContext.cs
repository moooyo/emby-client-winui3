using System.Text.Json.Serialization;

namespace EmbyClient.FixtureServer;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FixtureStats))]
[JsonSerializable(typeof(MediaFixtureMetadata))]
[JsonSerializable(typeof(FixtureCollectionResult))]
internal partial class FixtureJsonContext : JsonSerializerContext;
