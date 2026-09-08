using System.Text.Json.Serialization;

namespace EmbyClient.App.Services;

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SavedAccount))]
public partial class SettingsJsonContext : JsonSerializerContext;
