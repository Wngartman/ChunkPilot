using System.Net;
using System.Net.Sockets;

namespace ChunkPilot.Core;

/// <summary>Read-only facts. Saved settings, a launch baseline, and observed listeners are not interchangeable.</summary>
public sealed record ServerBindingEvidence
{
    public bool Known { get; init; }
    public string BindAddress { get; init; } = "";
    public int Port { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public string Detail { get; init; } = "Server binding has not been read.";
}

public sealed record ServerListenerEvidence
{
    public int ProcessId { get; init; }
    public long ProcessCreationTicks { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }
    public bool ExactOwnerVerified { get; init; }
    public IReadOnlyList<string> BindAddresses { get; init; } = [];
    public int Port { get; init; }
    public string Detail { get; init; } = "An exact-owned game listener has not been observed.";
}

public sealed record ServerConnectionEvidence
{
    public ServerBindingEvidence Saved { get; init; } = new();
    public ServerBindingEvidence AtLaunch { get; init; } = new();
    public ServerListenerEvidence Listener { get; init; } = new();
}

/// <summary>The same native presentation contract is used by every joining surface.</summary>
public sealed record ServerConnectionSummary
{
    public Guid ServerId { get; init; }
    public NetworkMode RequestedAudience { get; init; }
    public string Audience { get; init; } = "computer";
    public string Label { get; init; } = "Connection status";
    public string Badge { get; init; } = "Connection not verified";
    public string Tone { get; init; } = "neutral";
    public string Explanation { get; init; } = "";
    public string? Address { get; init; }
    public string? Kind { get; init; }
    public string? LocalAddress { get; init; }
    public string? LanAddress { get; init; }
    public string? ConfiguredLocalAddress { get; init; }
    public string? ConfiguredLanAddress { get; init; }
    public bool PendingRestart { get; init; }
    public bool RequestedAudienceNotApplied { get; init; }
    public bool CanApplyBinding { get; init; }
    public string BindingApplyUnavailableReason { get; init; } = "";
    public bool ListenerVerified { get; init; }
    public bool FirewallConfigured { get; init; }
    public bool RouterConfigured { get; init; }
    public string? PublicVerifiedAddress { get; init; }
    public string? RouterReportedAddress { get; init; }
    public ServerConnectionEvidence Evidence { get; init; } = new();
}

public static class ServerConnectionSummaryPolicy
{
    public static ServerConnectionSummary Build(ServerSnapshot server, NetworkMode requested, string lanIp,
        DateTimeOffset now, bool firewallConfigured = false, bool routerConfigured = false,
        string? routerAddress = null, string? publicVerifiedAddress = null, string? lastPublicAddress = null)
    {
        var evidence = server.ConnectionEvidence;
        var saved = evidence.Saved;
        var launched = evidence.AtLaunch;
        var listener = evidence.Listener;
        var savedIp = ParseBinding(saved.BindAddress);
        var configuredLocal = saved.Known && savedIp is not null && (IsLoopback(savedIp) || IsAny(savedIp))
            ? Endpoint(LocalBinding(savedIp), saved.Port) : null;
        var configuredLan = saved.Known && savedIp is not null ? LanEndpoint(savedIp, saved.Port, lanIp) : null;
        var notApplied = saved.Known && savedIp is not null &&
            (IsLoopback(savedIp) && requested is NetworkMode.HomeNetwork or NetworkMode.PortForwarding ||
             !IsLoopback(savedIp) && requested == NetworkMode.ThisComputerOnly);
        var applyUnavailable = ServerBindingApplyPolicy.UnavailableReason(server.Definition);
        if (applyUnavailable.Length == 0 && (!saved.Known || saved.Port != server.Definition.Port))
            applyUnavailable = "The saved binding and port need to be verified before applying access.";
        if (applyUnavailable.Length == 0 && server.State is not (ServerState.Running or ServerState.Stopped))
            applyUnavailable = "Wait until this server is running or fully stopped before applying its binding.";
        var pending = server.RootProcessId is not null && saved.Known && launched.Known &&
            (saved.Port != launched.Port || !Equals(ParseBinding(saved.BindAddress), ParseBinding(launched.BindAddress)));
        var result = new ServerConnectionSummary
        {
            ServerId = server.Definition.Id, RequestedAudience = requested, Evidence = evidence,
            ConfiguredLocalAddress = configuredLocal, ConfiguredLanAddress = configuredLan,
            PendingRestart = pending, RequestedAudienceNotApplied = notApplied,
            CanApplyBinding = applyUnavailable.Length == 0,
            BindingApplyUnavailableReason = applyUnavailable,
            FirewallConfigured = firewallConfigured, RouterConfigured = routerConfigured,
            RouterReportedAddress = routerAddress
        };
        var pendingDetail = pending ? " Saved binding or port changes require a restart." : "";
        var audienceDetail = !notApplied ? "" : requested == NetworkMode.ThisComputerOnly
            ? " Local-only access is selected but not applied: the saved binding still allows other interfaces."
            : " LAN/Internet access is selected but not applied: the saved binding is local-only.";
        if (server.State == ServerState.Stopped)
            return result with { Badge = "Server stopped", Label = "Configured address",
                Audience = configuredLocal is null && configuredLan is not null ? "home" : "computer",
                Address = configuredLocal ?? configuredLan, Kind = configuredLocal is not null ? "local" : configuredLan is not null ? "lan" : null,
                Explanation = "The server is stopped. Configured addresses are not currently available connections." + audienceDetail };
        if (server.State is ServerState.Starting or ServerState.Restarting)
            return result with { Badge = "Server starting", Tone = "info",
                Explanation = "Wait for server readiness and an exact-owned listener check before joining." + pendingDetail + audienceDetail };
        if (server.State is not (ServerState.Running or ServerState.Saving or ServerState.BackingUp))
            return result with { Badge = server.State == ServerState.Stopping ? "Server stopping" : "Connection not verified",
                Explanation = "The server is not in a confirmed ready state." + pendingDetail + audienceDetail };

        // A retained observation cannot certify a replacement process, another port, or a stale session.
        if (!server.LastStartReachedReadiness || !listener.ExactOwnerVerified ||
            !ProcessCreationIdentity.Matches(listener.ProcessCreationTicks, server.RootProcessCreationTicks) ||
            listener.ProcessId != server.RootProcessId || listener.Port != server.Definition.Port ||
            listener.CheckedAt is not { } checkedAt || checkedAt > now || now - checkedAt > TimeSpan.FromSeconds(15))
            return result with { Explanation = "ChunkPilot has not verified a fresh listener owned by this server attempt." + pendingDetail + audienceDetail };
        var addresses = listener.BindAddresses.Select(value => IPAddress.TryParse(value, out var ip) ? ip : null).ToArray();
        if (addresses.Length == 0 || addresses.Any(ip => ip is null))
            return result with { Explanation = "No supported exact-owned game listener was established." + pendingDetail + audienceDetail };
        var local = addresses.FirstOrDefault(ip => IsLoopback(ip!) || IsAny(ip!));
        var localEndpoint = local is null ? null : Endpoint(LocalBinding(local), listener.Port);
        var lanEndpoint = addresses.Select(ip => LanEndpoint(ip!, listener.Port, lanIp)).FirstOrDefault(value => value is not null);
        result = result with { ListenerVerified = true, LocalAddress = localEndpoint, LanAddress = lanEndpoint };
        if (addresses.All(ip => IsLoopback(ip!)))
            return result with { Label = "Join on this computer", Badge = "Only on this computer",
                Address = localEndpoint, Kind = "local",
                RequestedAudienceNotApplied = requested is NetworkMode.HomeNetwork or NetworkMode.PortForwarding,
                Explanation = "The exact-owned game listener accepts connections only on this computer." + pendingDetail + audienceDetail +
                    (!notApplied && requested is NetworkMode.HomeNetwork or NetworkMode.PortForwarding
                        ? " LAN/Internet access is selected but the running listener is still local-only." : "") };
        if (lanEndpoint is null)
            return result with { Address = localEndpoint, Kind = localEndpoint is null ? null : "local",
                Explanation = "A game listener was observed, but a matching home-network address is not established." + pendingDetail + audienceDetail };
        if (requested == NetworkMode.ThisComputerOnly)
        {
            result = result with { RequestedAudienceNotApplied = true };
            pendingDetail += " This computer only is selected, but the running listener is broader. Review the saved binding before restarting.";
        }
        if (requested == NetworkMode.PortForwarding)
            return result with { Audience = "internet", Label = "Share with friends", Address = publicVerifiedAddress ?? routerAddress ?? lastPublicAddress,
                Kind = publicVerifiedAddress is not null ? "public" : routerAddress is not null && routerConfigured ? "router" : routerAddress is not null || lastPublicAddress is not null ? "last" : null,
                PublicVerifiedAddress = publicVerifiedAddress,
                Badge = publicVerifiedAddress is not null ? "Connection confirmed" : routerConfigured && firewallConfigured ? "Internet sharing configured" : !routerConfigured && (routerAddress is not null || lastPublicAddress is not null) ? "Last used" : "Internet setup incomplete",
                Tone = publicVerifiedAddress is not null ? "success" : "warning",
                Explanation = (publicVerifiedAddress is not null ? "An outside-in check reached this server's current endpoint."
                    : "Router and Windows setup are separate from outside-in reachability; friends' access is not verified. Outside networks can still impose limits; retained addresses may have changed.") + pendingDetail };
        return result with { Audience = "home", Label = "Share on your LAN", Address = lanEndpoint, Kind = "lan",
            Badge = "Listening for home-network connections", Tone = "info",
            Explanation = "The exact-owned game listener accepts this interface. This address will not work over the Internet. Windows, the network, or the server allowlist may still block another device; LAN access has not been tested." + pendingDetail };
    }

    private static IPAddress? ParseBinding(string value) => value.Length == 0 ? IPAddress.Any :
        IPAddress.TryParse(value, out var address) ? address : null;
    private static bool IsLoopback(IPAddress value) => IPAddress.IsLoopback(value.IsIPv4MappedToIPv6 ? value.MapToIPv4() : value);
    private static bool IsAny(IPAddress value) => value.Equals(IPAddress.Any) || value.Equals(IPAddress.IPv6Any);
    private static IPAddress LocalBinding(IPAddress value) => value.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback :
        value.Equals(IPAddress.Any) ? IPAddress.Loopback : value;
    private static string Endpoint(IPAddress address, int port) => address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
    private static string? LanEndpoint(IPAddress bind, int port, string lanIp) =>
        IPAddress.TryParse(lanIp, out var lan) && !IsLoopback(lan) && !IsAny(lan) &&
        (bind.Equals(lan) || bind.Equals(IPAddress.Any) && lan.AddressFamily == AddressFamily.InterNetwork)
            ? Endpoint(lan, port) : null; // IPv6 wildcard does not prove dual-stack IPv4 listening.
}
