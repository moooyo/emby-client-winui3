namespace EmbyClient.App.Services;

public enum ReleaseUpdatePhase { Downloading, Verifying, Extracting, Ready }

public enum ReleaseUpdateIssue
{
    InvalidRelease,
    NoCompatibleAsset,
    InvalidChecksum,
    InvalidArchive,
    InvalidPayload,
    UnsupportedInstallation,
    InstallLaunchFailed
}

public sealed class ReleaseUpdateException(
    ReleaseUpdateIssue issue, string message, Exception? innerException = null) : IOException(message, innerException)
{
    public ReleaseUpdateIssue Issue { get; } = issue;
}

public sealed record ReleaseChecksumAsset(string Name, Uri DownloadUri, long Size, string? Sha256);

public sealed record ReleaseUpdateAsset(
    string Name, Uri DownloadUri, long Size, string? Sha256, ReleaseChecksumAsset? Checksum);

public sealed record ReleaseUpdateInfo(
    string Tag, Version Version, Uri ReleasePage, bool IsNewer,
    ReleaseUpdateAsset? Asset, string? IncompatibilityReason);

public sealed record ReleaseUpdateProgress(ReleaseUpdatePhase Phase, long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0
        ? Math.Clamp(100d * BytesReceived / TotalBytes.Value, 0, 100)
        : null;
}

/// <summary>A complete validated download. Installation is a separate explicit action.</summary>
public sealed class StagedReleaseUpdate
{
    public const string StartMenuEntryName = "Lumen Emby Client";

    internal StagedReleaseUpdate(ReleaseUpdateInfo release, string directoryPath, string payloadDirectory,
        string installDirectory, bool checksumVerified, string archiveSha256,
        IReadOnlyList<ReleaseUpdateFile> files, string receiptSha256)
    {
        Release = release;
        DirectoryPath = directoryPath;
        PayloadDirectory = payloadDirectory;
        InstallDirectory = installDirectory;
        ChecksumVerified = checksumVerified;
        ArchiveSha256 = archiveSha256;
        Files = files;
        ReceiptSha256 = receiptSha256;
    }

    public ReleaseUpdateInfo Release { get; }
    public string DirectoryPath { get; }
    public string PayloadDirectory { get; }
    public string InstallDirectory { get; }
    public bool ChecksumVerified { get; }
    public string RecoveryInstructionsPath => Path.Combine(DirectoryPath, "RECOVERY.txt");
    internal string ArchiveSha256 { get; }
    internal IReadOnlyList<ReleaseUpdateFile> Files { get; }
    internal string ReceiptSha256 { get; }
}

internal sealed record ReleaseUpdateFile(string Path, long Length, string Sha256);

internal sealed record ReleaseArchiveFile(System.IO.Compression.ZipArchiveEntry Entry, string Path);

internal sealed record ReleaseArchivePlan(IReadOnlyList<ReleaseArchiveFile> Files, long TotalBytes);

internal readonly record struct ReleaseZipEnvelope(int EntryCount, int DirectoryLength, long DirectoryOffset);

internal sealed record ReleaseInstallLaunchFiles(
    string LaunchId, string HelperPath, string PlanPath, string ReadyPath, string CancelPath);
