using System.Text.Json.Serialization;

namespace EmbyClient.ServerValidation.ApiProbe;

internal sealed record Credentials
{
    public string? ServerUrl { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public override string ToString() => nameof(Credentials);
}

internal sealed record ProbeReport
{
    public string SchemaVersion { get; init; } = "1";
    public string Scope { get; init; } = "Official Emby API and authenticated HTTP media transfer";
    public string Target { get; init; } = "http://127.0.0.1:19096";
    public required string ExpectedServerVersion { get; init; }
    public string? ObservedServerVersion { get; set; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset FinishedUtc { get; set; }
    public string Outcome { get; set; } = "Running";
    public string NativeUi { get; init; } = "NotRun";
    public string NativeDecoderAndFirstFrame { get; init; } = "NotRun";
    public bool SyntheticPlaybackReports { get; init; } = true;
    public string PlaybackReportMeaning { get; init; } = "Synthetic API lifecycle reports; no decoded playback or user-visible frames are asserted.";
    public string UserStateRestorationScope { get; init; } = "Cleanup attempts to restore Favorite and Played booleans and verifies readback; individual step results record success or failure. The public client has no API for restoring historical play count or last-played timestamps.";
    public List<ProbeStep> Steps { get; init; } = [];
}

internal sealed record ProbeStep
{
    public required string Name { get; init; }
    public required string Status { get; init; }
    public bool Required { get; init; } = true;
    public long DurationMilliseconds { get; init; }
    public string? ErrorCode { get; init; }
    public int? HttpStatus { get; init; }
    public Dictionary<string, string> Evidence { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(Credentials))]
[JsonSerializable(typeof(ProbeReport))]
internal partial class ProbeJsonContext : JsonSerializerContext;

internal sealed class ProbeAssertionException(string code) : Exception
{
    public string Code { get; } = code;
}

internal sealed class ProbeHttpException(int status) : Exception
{
    public int Status { get; } = status;
}
