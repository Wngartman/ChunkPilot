using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ChunkPilot.Infrastructure;

/// <summary>Reads the effective IPv4 route. Query only; it configures nothing.</summary>
internal static class NativeRouting
{
    [DllImport("iphlpapi.dll")]
    internal static extern int GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);
}

/// <summary>
/// Chooses which gateway to ask and which private address to forward to.
/// </summary>
/// <remarks>
/// This reuses <see cref="LanAddressSelector"/>'s interface rule rather than repeating it, so the fix
/// that stopped an ExpressVPN WinTUN adapter being labelled the local network also stops it being
/// offered to a router as the forwarding target. On top of that rule the gateway must be reachable
/// through the same IPv4 subnet as the chosen address: an adapter whose gateway is somewhere else is
/// not evidence of the household router, however fast it is.
/// </remarks>
public static class RouterGatewaySelector
{
    public static RouterLanBinding? Select(IEnumerable<RouterGatewayCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ranked = candidates
            .Where(candidate => LanAddressSelector.IsTrustworthyLanInterface(candidate.Interface))
            .Select(Pair)
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => item.Binding.GatewayAddress is not null && item.IsEffectiveRoute ? 0 : 1)
            .ThenBy(item => item.TypeRank)
            .ThenByDescending(item => item.Speed)
            .ToArray();
        if (ranked.Length == 0)
            return null;

        var best = ranked[0];
        // Without route evidence, two plausible adapters are ambiguous. Asking the wrong router to
        // forward a port is worse than reporting that no router could be identified.
        if (!best.IsEffectiveRoute && ranked.Length > 1)
            return null;
        return best.Binding;
    }

    private static RankedBinding? Pair(RouterGatewayCandidate candidate)
    {
        foreach (var gateway in candidate.Gateways)
        {
            if (gateway.AddressFamily != AddressFamily.InterNetwork ||
                gateway.Equals(IPAddress.Any) || IPAddress.IsLoopback(gateway))
                continue;
            foreach (var address in candidate.Interface.Addresses)
            {
                if (address.Address.AddressFamily != AddressFamily.InterNetwork ||
                    !LanAddressSelector.IsPrivateLanAddress(address.Address) ||
                    address.PrefixLength is <= 0 or > 32 ||
                    !SameIpv4Subnet(address.Address, gateway, address.PrefixLength))
                    continue;
                return new RankedBinding(
                    new RouterLanBinding(
                        candidate.Interface.Id,
                        candidate.Interface.Name,
                        address.Address,
                        address.PrefixLength,
                        gateway),
                    candidate.Interface.IsEffectiveDefaultRoute,
                    candidate.Interface.Type == NetworkInterfaceType.Ethernet ? 0 : 1,
                    candidate.Interface.Speed);
            }
        }
        return null;
    }

    internal static bool SameIpv4Subnet(IPAddress address, IPAddress gateway, int prefixLength)
    {
        var left = address.GetAddressBytes();
        var right = gateway.GetAddressBytes();
        if (left.Length != 4 || right.Length != 4)
            return false;
        var remaining = prefixLength;
        for (var index = 0; index < 4 && remaining > 0; index++)
        {
            var bits = Math.Min(8, remaining);
            var mask = (byte)(0xFF << (8 - bits));
            if ((left[index] & mask) != (right[index] & mask))
                return false;
            remaining -= bits;
        }
        return true;
    }

    private sealed record RankedBinding(
        RouterLanBinding Binding,
        bool IsEffectiveRoute,
        int TypeRank,
        long Speed);
}

/// <summary>Reads this machine's real interfaces. Read-only: enumerating changes nothing.</summary>
public sealed class SystemRouterNetworkView : IRouterNetworkView
{
    public IReadOnlyList<RouterGatewayCandidate> Enumerate()
    {
        var bestInterface = BestIpv4InterfaceIndex();
        var result = new List<RouterGatewayCandidate>();
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = item.GetIPProperties();
                var index = item.Supports(NetworkInterfaceComponent.IPv4)
                    ? properties.GetIPv4Properties()?.Index ?? -1
                    : -1;
                var gateways = properties.GatewayAddresses
                    .Select(gateway => gateway.Address)
                    .Where(address => !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                    .ToArray();
                var candidate = new LanInterfaceCandidate(
                    item.Id,
                    item.Name,
                    item.Description,
                    item.NetworkInterfaceType,
                    item.OperationalStatus,
                    item.Speed,
                    gateways.Length > 0,
                    index >= 0 && index == bestInterface,
                    properties.UnicastAddresses
                        .Select(address => new LanAddressCandidate(address.Address, address.PrefixLength))
                        .ToArray(),
                    index);
                result.Add(new RouterGatewayCandidate(candidate, gateways));
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                // An adapter can disappear between enumeration and property access. It is not evidence.
            }
        }
        return result;
    }

    private static int BestIpv4InterfaceIndex()
    {
        if (!OperatingSystem.IsWindows())
            return -1;
        var destination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes());
        return NativeRouting.GetBestInterface(destination, out var index) == 0 ? checked((int)index) : -1;
    }
}
