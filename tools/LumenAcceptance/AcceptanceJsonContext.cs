using System.Text.Json.Serialization;

namespace EmbyClient.LumenAcceptance;

internal sealed class BootstrapReceipt
{
    public string Scope { get; init; } = "Synthetic local profile only; not real Emby compatibility or native UI evidence.";
    public DateTimeOffset CompletedAtUtc { get; init; }
    public int FixturePort { get; init; }
    public string ServerId { get; init; } = "";
    public string UserId { get; init; } = "";
    public string AccountKey { get; init; } = "";
    public string Theme { get; init; } = "";
    public string ProfileDirectory { get; init; } = "";
    public bool ProductionRestoreVerified { get; init; }
    public bool OfficialServer { get; init; }
    public bool DesignCatalog { get; init; }
    public bool IsAdministrator { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BootstrapReceipt))]
[JsonSerializable(typeof(OfficialTestCredentials))]
[JsonSerializable(typeof(OfficialOwnershipReceipt))]
internal partial class AcceptanceJsonContext : JsonSerializerContext;
