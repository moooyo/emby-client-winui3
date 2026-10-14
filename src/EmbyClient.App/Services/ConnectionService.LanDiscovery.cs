using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbyClient.Api;

namespace EmbyClient.App.Services;

public sealed record LanDiscoveredServer(string ApiRoot, string ServerId, string Name);

public sealed class ServerIdentityMismatchException() : Exception(
    "This address belongs to a different server. Select or add the server again before signing in.");

public sealed partial class ConnectionService
{
    public const int LanDiscoveryPort = 7359;
    public const string LanDiscoveryRequest = "who is EmbyServer?";
    private static readonly TimeSpan LanDiscoveryDuration = TimeSpan.FromSeconds(4);
    private const int MaximumDiscoveryResponses = 256;

    /// <summary>Finds nearby server candidates; HTTP public identity validation remains mandatory before sign-in.</summary>
    public async Task<IReadOnlyList<LanDiscoveredServer>> DiscoverLanServersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(LanDiscoveryDuration);
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var request = Encoding.UTF8.GetBytes(LanDiscoveryRequest);
        var servers = new List<LanDiscoveredServer>();
        var sent = false;
        SocketException? sendFailure = null;
        try
        {
            foreach (var target in LanDiscoveryTargets())
            {
                try
                {
                    await udp.SendAsync(request.AsMemory(), new IPEndPoint(target, LanDiscoveryPort), deadline.Token).ConfigureAwait(false);
                    sent = true;
                }
                catch (SocketException error) { sendFailure = error; }
            }
            if (!sent && sendFailure is not null) throw sendFailure;
            for (var count = 0; count < MaximumDiscoveryResponses; count++)
            {
                var response = await udp.ReceiveAsync(deadline.Token).ConfigureAwait(false);
                if (TryParseLanDiscoveryResponse(response.Buffer, out var server)) servers.Add(server);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The discovery window is a successful, bounded collection rather than a connection timeout.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return DeduplicateLanServers(servers);
    }

    public static bool TryParseLanDiscoveryResponse(ReadOnlySpan<byte> bytes,
        [NotNullWhen(true)] out LanDiscoveredServer? server)
    {
        server = null;
        if (bytes.IsEmpty || bytes.Length > 16 * 1024) return false;
        try
        {
            var response = JsonSerializer.Deserialize(bytes, LanDiscoveryJsonContext.Default.LanDiscoveryResponse);
            if (response is null) return false;
            var candidate = SavedServer.Create(response.Address, response.Id, response.Name);
            server = new(candidate.ApiRoot, candidate.ServerId, candidate.Name);
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { return false; }
    }

    internal static IReadOnlyList<LanDiscoveredServer> DeduplicateLanServers(IEnumerable<LanDiscoveredServer> candidates)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var servers = new List<LanDiscoveredServer>();
        foreach (var candidate in candidates)
        {
            if (identities.Contains(candidate.ServerId) || addresses.Contains(candidate.ApiRoot)) continue;
            identities.Add(candidate.ServerId);
            addresses.Add(candidate.ApiRoot);
            servers.Add(candidate);
        }
        return servers.OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(server => server.ApiRoot, StringComparer.Ordinal).ToArray();
    }

    internal static void RequireExpectedServerIdentity(PublicSystemInfo server, string? expectedServerId)
    {
        RequireServerIdentity(server);
        if (expectedServerId is null) return;
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedServerId);
        if (!string.Equals(server.Id, expectedServerId, StringComparison.Ordinal)) throw new ServerIdentityMismatchException();
    }

    private static IReadOnlyList<IPAddress> LanDiscoveryTargets()
    {
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up
                    || network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var address in network.GetIPProperties().UnicastAddresses)
                {
                    if (targets.Count >= 32) break;
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask is null) continue;
                    var bytes = address.Address.GetAddressBytes();
                    var mask = address.IPv4Mask.GetAddressBytes();
                    for (var index = 0; index < bytes.Length; index++) bytes[index] |= (byte)~mask[index];
                    targets.Add(new IPAddress(bytes));
                }
            }
        }
        catch (NetworkInformationException)
        {
            // The limited broadcast remains available if adapter metadata cannot be read.
        }
        return targets.ToArray();
    }
}

internal sealed class LanDiscoveryResponse
{
    [JsonRequired]
    public string Address { get; set; } = string.Empty;
    [JsonRequired]
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, GenerationMode = JsonSourceGenerationMode.Metadata, MaxDepth = 8)]
[JsonSerializable(typeof(LanDiscoveryResponse))]
internal partial class LanDiscoveryJsonContext : JsonSerializerContext;
