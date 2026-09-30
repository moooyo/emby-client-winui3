using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace EmbyClient.LumenAcceptance;

internal static class OfficialBootstrap
{
    private const string OfficialImage = "emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a";
    private const string OfficialPackageSha256 = "1d718ffa0169c393de3eafda65b1b057a3db4ead93ffeb5883abd01735de9843";

    public static async Task<OfficialTestCredentials> LoadOwnedCredentialsAsync(BootstrapOptions options,
        CancellationToken cancellationToken)
    {
        var artifactRoot = Path.Combine(options.WorkspaceRoot, "artifacts", "lumen-official-validation");
        var credentialsPath = options.OfficialCredentialsFile ?? throw new ArgumentException("Explicit official mode is required.");
        CheckArtifactFile(credentialsPath, artifactRoot);
        var credentials = await ReadAsync(credentialsPath, AcceptanceJsonContext.Default.OfficialTestCredentials, cancellationToken)
            ?? throw new InvalidOperationException("Owned official credentials were not valid JSON.");
        if (BootstrapOptions.NormalizeFixtureUrl(credentials.ServerUrl).AbsoluteUri != options.ServerUrl.AbsoluteUri
            || string.IsNullOrWhiteSpace(credentials.Username) || !credentials.Username.StartsWith("lumen-", StringComparison.Ordinal)
            || credentials.Username.Any(char.IsControl) || string.IsNullOrWhiteSpace(credentials.Password)
            || credentials.ExpectedIsAdministrator && credentials.Username != "lumen-owned-admin"
            || credentials.Password.Length is < 16 or > 1024
            || string.IsNullOrWhiteSpace(credentials.ServerId) || credentials.ExpectedServerVersion != "4.9.5.0")
            throw new InvalidOperationException("Owned official credentials did not match the dedicated loopback test account.");
        CheckArtifactFile(credentials.OwnershipReceiptPath, artifactRoot);
        var receipt = await ReadAsync(credentials.OwnershipReceiptPath, AcceptanceJsonContext.Default.OfficialOwnershipReceipt, cancellationToken)
            ?? throw new InvalidOperationException("Owned official backend receipt was not valid JSON.");
        var containerSource = receipt.OfficialImage == OfficialImage && receipt.InternalBridge && !receipt.DockerPublishedPorts
            && receipt.ContainerId.Length == 64 && receipt.ContainerId.All(Uri.IsHexDigit);
        var packageSource = receipt.OfficialDebianPackageSha256 == OfficialPackageSha256 && receipt.NetworkNamespaceIsolated;
        if (receipt.SchemaVersion != 1 || receipt.Scope != "OwnedOfficialEmbySyntheticAcceptance"
            || !receipt.OfficialServer || !receipt.SyntheticDataOnly || !receipt.LocalTunnelLoopbackOnly
            || string.IsNullOrWhiteSpace(receipt.Owner) || receipt.ServerId != credentials.ServerId
            || receipt.ServerVersion != credentials.ExpectedServerVersion || receipt.LocalTunnelPort != options.ServerUrl.Port
            || receipt.CreatedUtc > DateTimeOffset.UtcNow.AddMinutes(1) || receipt.ExpiresUtc <= DateTimeOffset.UtcNow
            || receipt.ExpiresUtc - receipt.CreatedUtc > TimeSpan.FromDays(2) || (!containerSource && !packageSource))
            throw new InvalidOperationException("The official backend receipt does not identify a current isolated test deployment.");
        using var tunnel = Process.GetProcessById(receipt.LocalTunnelPid);
        if (!string.Equals(tunnel.ProcessName, "ssh", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(tunnel.MainModule?.FileName, receipt.LocalTunnelExecutable, StringComparison.OrdinalIgnoreCase)
            || tunnel.StartTime.ToUniversalTime() != receipt.LocalTunnelStartedAtUtc.UtcDateTime)
            throw new InvalidOperationException("The current SSH process does not match the owned tunnel receipt.");
        using var executable = File.OpenRead(receipt.LocalTunnelExecutable);
        var executableHash = Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken));
        if (!string.Equals(executableHash, receipt.LocalTunnelExecutableSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The owned tunnel executable identity changed.");
        return credentials;
    }

    internal static bool MatchesExpectedRole(bool? actualRole, bool expectedRole) =>
        actualRole.HasValue && actualRole.Value == expectedRole;

    private static void CheckArtifactFile(string path, string artifactRoot)
    {
        if (!Path.IsPathFullyQualified(path) || !BootstrapOptions.IsStrictChild(path, artifactRoot)
            || !File.Exists(path) || new FileInfo(path).Length is <= 0 or > 64 * 1024)
            throw new InvalidOperationException("The owned official artifact path is unavailable or outside its dedicated root.");
        for (var node = new FileInfo(path).Directory; node is not null && BootstrapOptions.IsStrictChild(node.FullName, artifactRoot); node = node.Parent)
            if ((node.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Owned official artifacts cannot traverse a reparse point.");
        if ((new DirectoryInfo(artifactRoot).Attributes & FileAttributes.ReparsePoint) != 0
            || (new FileInfo(path).Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Owned official artifacts cannot be reparse points.");
    }

    private static async Task<T?> ReadAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
    }
}

internal sealed class OfficialTestCredentials
{
    public string ServerUrl { get; init; } = "";
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string ServerId { get; init; } = "";
    public string ExpectedServerVersion { get; init; } = "";
    public bool ExpectedIsAdministrator { get; init; }
    public string OwnershipReceiptPath { get; init; } = "";
}

internal sealed class OfficialOwnershipReceipt
{
    public int SchemaVersion { get; init; }
    public string Scope { get; init; } = "";
    public bool OfficialServer { get; init; }
    public bool SyntheticDataOnly { get; init; }
    public string Owner { get; init; } = "";
    public string ServerId { get; init; } = "";
    public string ServerVersion { get; init; } = "";
    public string OfficialImage { get; init; } = "";
    public string OfficialDebianPackageSha256 { get; init; } = "";
    public bool InternalBridge { get; init; }
    public bool DockerPublishedPorts { get; init; }
    public bool NetworkNamespaceIsolated { get; init; }
    public string ContainerId { get; init; } = "";
    public int LocalTunnelPid { get; init; }
    public int LocalTunnelPort { get; init; }
    public bool LocalTunnelLoopbackOnly { get; init; }
    public string LocalTunnelExecutable { get; init; } = "";
    public string LocalTunnelExecutableSha256 { get; init; } = "";
    public DateTimeOffset LocalTunnelStartedAtUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
}
