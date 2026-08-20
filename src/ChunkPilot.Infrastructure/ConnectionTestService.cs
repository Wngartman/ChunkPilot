using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class ConnectionTestService
{
    private readonly MinecraftStatusClient minecraftStatus;
    private readonly HttpClient http;

    public ConnectionTestService(MinecraftStatusClient minecraftStatus, HttpClient? httpClient = null)
    {
        this.minecraftStatus = minecraftStatus;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0 (user-initiated Minecraft reachability check)");
    }

    public async Task<ConnectionTestResult> TestAsync(
        ServerSnapshot server,
        string lanAddress,
        bool includeExternalProbe,
        CancellationToken cancellationToken = default)
    {
        var processRunning = server.State == ServerState.Running;
        var listening = await CanConnectAsync("127.0.0.1", server.Definition.Port, cancellationToken).ConfigureAwait(false);
        var status = listening
            ? await minecraftStatus.QueryAsync("127.0.0.1", server.Definition.Port, cancellationToken).ConfigureAwait(false)
            : null;
        var configuredHost = server.Definition.UserConfiguredHostname.Trim();
        var publicAddress = configuredHost.Length > 0 ? $"{configuredHost}:{server.Definition.Port}" : "";
        var external = "Not tested";
        if (includeExternalProbe)
        {
            external = !processRunning ? "Server not running" :
                publicAddress.Length == 0 ? "Test unavailable: configure a public hostname or address first." :
                await ProbeExternalAsync(publicAddress, cancellationToken).ConfigureAwait(false);
        }
        var interpretation = external == "Publicly reachable"
            ? "An external Minecraft status service successfully contacted the configured public address."
            : !processRunning ? "Start the server before testing connectivity." :
              !listening ? "The server process is not listening locally yet." :
              status is null ? "The port accepts TCP locally, but the Minecraft status handshake did not respond." :
              includeExternalProbe
                  ? "Local checks passed. External failure may mean port forwarding, firewall, CGNAT, an incorrect address, ISP filtering, or startup delay."
                  : "Local checks passed. This does not prove internet reachability; run the explicit external probe if desired.";
        return new ConnectionTestResult
        {
            ProcessRunning = processRunning ? FindingSeverity.Pass : FindingSeverity.Warning,
            PortListening = listening ? FindingSeverity.Pass : FindingSeverity.Warning,
            MinecraftResponds = status is not null ? FindingSeverity.Pass : FindingSeverity.Warning,
            FirewallAssessment = FindingSeverity.Unavailable,
            LocalAddress = $"localhost:{server.Definition.Port}",
            LanAddress = string.IsNullOrWhiteSpace(lanAddress) ? "" : $"{lanAddress}:{server.Definition.Port}",
            PublicAddress = publicAddress,
            ExternalResult = external,
            Interpretation = interpretation
        };
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or TimeoutException or IOException)
        {
            return false;
        }
    }

    private async Task<string> ProbeExternalAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"https://api.mcstatus.io/v2/status/java/{Uri.EscapeDataString(address)}";
            using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return "Test unavailable";
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("online", out var online) && online.GetBoolean()
                ? "Publicly reachable"
                : "Not publicly reachable";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Test unavailable";
        }
    }
}
