using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace EmbyClient.LumenAcceptance.Tests;

public sealed class OfficialBootstrapTests
{
    private const string SecretMarker = "synthetic-test-only-password-never-print";
    private const string PinnedImage = "emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a";

    [Theory]
    [InlineData("Scope")]
    [InlineData("Server")]
    [InlineData("Expired")]
    [InlineData("Future")]
    [InlineData("Image")]
    [InlineData("Published")]
    [InlineData("Bridge")]
    [InlineData("Port")]
    [InlineData("NonSsh")]
    [InlineData("ExpectedAdminWrongName")]
    [InlineData("ExpectedAdminNonSsh")]
    public async Task Invalid_owned_backend_receipts_are_rejected_without_disclosing_credentials(string defect)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "lumen-official-gate-" + Guid.NewGuid().ToString("N"));
        var artifacts = Path.Combine(workspace, "artifacts", "lumen-official-validation");
        Directory.CreateDirectory(artifacts);
        try
        {
            var credentialsPath = Path.Combine(artifacts, "credentials.json");
            var receiptPath = Path.Combine(artifacts, "owned-backend.json");
            var credentials = new OfficialTestCredentials
            {
                ServerUrl = "http://127.0.0.1:18984",
                Username = defect == "ExpectedAdminNonSsh" ? "lumen-owned-admin" : "lumen-test-viewer",
                Password = SecretMarker,
                ServerId = "owned-test-server",
                ExpectedServerVersion = "4.9.5.0",
                ExpectedIsAdministrator = defect is "ExpectedAdminWrongName" or "ExpectedAdminNonSsh",
                OwnershipReceiptPath = receiptPath
            };
            var receipt = new OfficialOwnershipReceipt
            {
                SchemaVersion = 1,
                Scope = defect == "Scope" ? "Unowned" : "OwnedOfficialEmbySyntheticAcceptance",
                OfficialServer = true,
                SyntheticDataOnly = true,
                Owner = "synthetic-test-owner",
                ServerId = defect == "Server" ? "another-server" : credentials.ServerId,
                ServerVersion = credentials.ExpectedServerVersion,
                OfficialImage = defect == "Image" ? "not-the-pinned-official-image" : PinnedImage,
                InternalBridge = defect != "Bridge",
                DockerPublishedPorts = defect == "Published",
                ContainerId = new string('a', 64),
                LocalTunnelLoopbackOnly = true,
                LocalTunnelPort = defect == "Port" ? 18985 : 18984,
                LocalTunnelPid = Environment.ProcessId,
                LocalTunnelExecutable = Environment.ProcessPath!,
                LocalTunnelExecutableSha256 = "not-a-network-test",
                LocalTunnelStartedAtUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime(),
                CreatedUtc = defect == "Future" ? DateTimeOffset.UtcNow.AddHours(1) : DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresUtc = defect == "Expired" ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddHours(2)
            };
            await File.WriteAllBytesAsync(credentialsPath,
                JsonSerializer.SerializeToUtf8Bytes(credentials, AcceptanceJsonContext.Default.OfficialTestCredentials),
                TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(receiptPath,
                JsonSerializer.SerializeToUtf8Bytes(receipt, AcceptanceJsonContext.Default.OfficialOwnershipReceipt),
                TestContext.Current.CancellationToken);
            var options = BootstrapOptions.Parse(["--workspace-root", workspace, "--run-directory",
                Path.Combine(workspace, "artifacts", "lumen-acceptance", "fresh"),
                "--server-url", credentials.ServerUrl, "--official-credentials-file", credentialsPath]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                OfficialBootstrap.LoadOwnedCredentialsAsync(options, TestContext.Current.CancellationToken));

            Assert.DoesNotContain(SecretMarker, error.ToString());
            Assert.DoesNotContain(credentials.Username, error.ToString());
            if (defect == "ExpectedAdminNonSsh") Assert.Contains("SSH process", error.Message);
        }
        finally
        {
            Assert.True(BootstrapOptions.IsStrictChild(workspace, Path.GetTempPath()));
            Assert.StartsWith("lumen-official-gate-", Path.GetFileName(workspace));
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(null, false, false)]
    [InlineData(null, true, false)]
    public void Login_and_restore_require_the_explicit_expected_role(bool? actual, bool expected, bool matches) =>
        Assert.Equal(matches, OfficialBootstrap.MatchesExpectedRole(actual, expected));
}
