using System.Text.Json.Serialization;

namespace EmbyClient.FixtureServer;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FixtureStats))]
[JsonSerializable(typeof(MediaFixtureMetadata))]
internal partial class FixtureJsonContext : JsonSerializerContext;
