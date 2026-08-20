using ChunkPilot.Core;
using System.Net.NetworkInformation;

namespace ChunkPilot.Infrastructure;

/// <summary>The trusted LAN interface a firewall profile was chosen from, and its Windows network.</summary>
public sealed record FirewallNetworkSelection(
    LanInterfaceCandidate Interface,
    LanAddressCandidate Address,
    NetworkCategoryBinding Network);

public sealed record FirewallNetworkEvaluation(
    NetworkPathStatus PathStatus,
    NetworkListStatus NetworkListStatus,
    LanAddressSelection? Path,
    NetworkCategoryBinding? Network,
    FirewallTargetProblem Problem,
    string Detail);

/// <summary>
/// Pairs the LAN interface ChunkPilot already trusts with the Windows network category that exact
/// adapter is attached to.
/// </summary>
/// <remarks>
/// <para>
/// The interface rule is <see cref="LanAddressSelector"/>'s, unchanged, so the correction that stopped
/// an ExpressVPN WinTUN adapter being called the local network also stops it deciding which firewall
/// profile a rule belongs to. On top of that, the category must come from that same adapter: a machine
/// with private Wi-Fi and a public-classified VPN up at once has two active profiles, and "one of them
/// is Public" must never become "create a Public rule".
/// </para>
/// <para>
/// When the adapter cannot be matched to a connected Windows network, this returns nothing. No profile
/// is guessed, and nothing is created.
/// </para>
/// </remarks>
public static class WindowsFirewallNetworkSelector
{
    public static FirewallNetworkSelection? Select(
        IReadOnlyList<LanInterfaceCandidate> interfaces,
        IReadOnlyList<NetworkCategoryBinding> networks)
    {
        var evaluated = Evaluate(interfaces, new NetworkCategorySnapshot { Bindings = networks });
        return evaluated.Path is not null && evaluated.Network is not null
            ? new FirewallNetworkSelection(evaluated.Path.Interface, evaluated.Path.Address, evaluated.Network)
            : null;
    }

    public static FirewallNetworkEvaluation Evaluate(
        IReadOnlyList<LanInterfaceCandidate> interfaces,
        NetworkCategorySnapshot networks)
    {
        ArgumentNullException.ThrowIfNull(interfaces);
        ArgumentNullException.ThrowIfNull(networks);
        var path = LanAddressSelector.Evaluate(interfaces);
        if (path.Selection is null)
            return new FirewallNetworkEvaluation(path.Status, networks.Status, null, null,
                path.Status == NetworkPathStatus.Ambiguous
                    ? FirewallTargetProblem.NetworkPathAmbiguous
                    : FirewallTargetProblem.NetworkPathUnavailable,
                path.Status == NetworkPathStatus.Ambiguous
                    ? "More than one trustworthy physical network path remains after route evaluation."
                    : "No trustworthy routed physical LAN path is available.");
        if (!networks.Available)
            return new FirewallNetworkEvaluation(path.Status, networks.Status, path.Selection, null,
                FirewallTargetProblem.NetworkListUnavailable, networks.Detail);

        var adapterId = NetworkCategoryView.Normalize(path.Selection.Interface.Id);
        var matches = networks.Bindings.Where(network =>
                network.Connected &&
                network.Category != WindowsNetworkCategory.Unknown &&
                (path.Selection.Interface.InterfaceIndex > 0 &&
                 network.InterfaceIndex == path.Selection.Interface.InterfaceIndex ||
                 NetworkCategoryView.Normalize(network.AdapterId).Equals(adapterId, StringComparison.Ordinal)))
            .ToArray();

        // Windows can expose more than one connection/profile at once. A single exact interface-index
        // or adapter-GUID match is evidence; competing matches are ambiguity, never permission to pick
        // the least restrictive profile.
        if (matches.Length == 1)
            return new FirewallNetworkEvaluation(path.Status, networks.Status, path.Selection, matches[0],
                FirewallTargetProblem.None,
                $"{path.Selection.Interface.Name} matched one connected Windows network profile.");
        return new FirewallNetworkEvaluation(path.Status, networks.Status, path.Selection, null,
            matches.Length == 0
                ? FirewallTargetProblem.NetworkProfileUnavailable
                : FirewallTargetProblem.NetworkProfileAmbiguous,
            matches.Length == 0
                ? "The trusted LAN path has no usable connected Windows network profile."
                : "The trusted LAN path matched more than one connected Windows network profile.");
    }
}

/// <summary>
/// Establishes the three authoritative facts a ChunkPilot firewall rule needs: the exact executable,
/// the exact port, and the single Windows profile the server's own network uses.
/// </summary>
/// <remarks>
/// Everything comes from ChunkPilot's own state and from Windows. Nothing is taken from
/// <c>PATH</c>, from the name <c>java.exe</c> on its own, from the first Java installation on the
/// machine, from a launcher wrapper, or from free text the user typed. When any one of the three
/// cannot be established the resolution fails, and the caller creates no rule rather than a wider one.
/// </remarks>
public sealed class WindowsFirewallTargetResolver(
    IRouterNetworkView network,
    INetworkCategoryView categories,
    Func<string, bool>? fileExists = null)
{
    private readonly Func<string, bool> fileExists = fileExists ?? File.Exists;

    public FirewallTargetResolution Resolve(
        string? programPath,
        string runtimeSource,
        int port,
        string? runtimeProblemDetail = null)
    {
        var runtimeDetail = runtimeProblemDetail ?? "";
        var resolved = "";
        if (!WindowsFirewallPolicy.IsTrustworthyJavaRuntime(programPath, out var reason))
            runtimeDetail = runtimeDetail.Length > 0 ? runtimeDetail : reason;
        else
        {
            resolved = Path.GetFullPath(programPath!.Trim());
            if (!fileExists(resolved))
            {
                runtimeDetail = "The Java runtime recorded for this server is no longer on disk.";
                resolved = "";
            }
        }

        var portDetail = port is < 1 or > 65535 ? "This server has no usable port yet." : "";

        IReadOnlyList<RouterGatewayCandidate> candidates;
        var networkReadDetail = "";
        try
        {
            candidates = network.Enumerate();
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            candidates = [];
            networkReadDetail = $"Windows network interfaces could not be read (0x{exception.HResult:X8}).";
        }
        var categorySnapshot = categories.Read();
        var networkResult = WindowsFirewallNetworkSelector.Evaluate(
            candidates.Select(candidate => candidate.Interface).ToArray(), categorySnapshot);
        var selection = networkResult.Path;
        var profile = networkResult.Network is null
            ? FirewallProfile.None
            : WindowsFirewallPolicy.ProfileFor(networkResult.Network.Category);
        var networkDetail = networkResult.Problem != FirewallTargetProblem.None
            ? networkReadDetail.Length > 0 ? networkReadDetail : networkResult.Detail
            : profile == FirewallProfile.None
                ? "Windows did not report a category for this computer's routed LAN network."
                : "";

        var gateway = selection is null ? null : candidates
            .Where(candidate => NetworkCategoryView.Normalize(candidate.Interface.Id)
                .Equals(NetworkCategoryView.Normalize(selection.Interface.Id), StringComparison.Ordinal))
            .SelectMany(candidate => candidate.Gateways)
            .FirstOrDefault(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                RouterGatewaySelector.SameIpv4Subnet(selection.Address.Address, candidate,
                    selection.Address.PrefixLength));

        var problem = runtimeDetail.Length > 0 ? FirewallTargetProblem.RuntimeUnavailable
            : portDetail.Length > 0 ? FirewallTargetProblem.PortUnavailable
            : networkResult.Problem != FirewallTargetProblem.None ? networkResult.Problem
            : FirewallTargetProblem.None;
        var detail = problem switch
        {
            FirewallTargetProblem.RuntimeUnavailable => runtimeDetail,
            FirewallTargetProblem.PortUnavailable => portDetail,
            FirewallTargetProblem.NetworkPathUnavailable or FirewallTargetProblem.NetworkPathAmbiguous or
                FirewallTargetProblem.NetworkListUnavailable or FirewallTargetProblem.NetworkProfileUnavailable or
                FirewallTargetProblem.NetworkProfileAmbiguous => networkDetail,
            _ => $"{selection!.Interface.Name} is attached to '{networkResult.Network!.NetworkName}', which Windows " +
                 $"classifies as {networkResult.Network.Category}."
        };

        return new FirewallTargetResolution
        {
            Resolved = problem == FirewallTargetProblem.None,
            ProgramPath = resolved,
            RuntimeSource = runtimeSource,
            Port = portDetail.Length == 0 ? port : 0,
            Transport = MappingTransport.Tcp,
            Profile = profile,
            Category = networkResult.Network?.Category ?? WindowsNetworkCategory.Unknown,
            NetworkName = networkResult.Network?.NetworkName ?? "",
            InterfaceName = selection?.Interface.Name ?? "",
            InterfaceType = selection?.Interface.Type == NetworkInterfaceType.Wireless80211
                ? "Wireless"
                : selection is null ? "" : "Lan",
            InterfaceIndex = selection?.Interface.InterfaceIndex ?? 0,
            LocalAddress = selection?.Address.Address.ToString() ?? "",
            GatewayAddress = gateway?.ToString() ?? "",
            NetworkPathStatus = networkResult.PathStatus,
            NetworkListStatus = networkResult.NetworkListStatus,
            NetworkListDetail = categorySnapshot.Detail,
            Problem = problem,
            Detail = detail
        };
    }
}
