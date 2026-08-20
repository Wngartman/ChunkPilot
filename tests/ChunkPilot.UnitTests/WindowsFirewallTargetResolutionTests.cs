using System.Net;
using System.Net.NetworkInformation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class WindowsFirewallTargetResolutionTests
{
    private const string Java = @"D:\ChunkPilot\managed-java\bin\java.exe";
    private const string EthernetId = "{D252AABD-78E1-40C5-A7B3-72360C2CB678}";

    [Fact]
    public void Real_machine_topology_correlates_interface_16_to_public()
    {
        var result = Resolve([Ethernet()], [Profile(16, WindowsNetworkCategory.Public)]);

        Assert.True(result.Resolved);
        Assert.Equal(FirewallProfile.Public, result.Profile);
        Assert.Equal(WindowsNetworkCategory.Public, result.Category);
        Assert.Equal("Ethernet", result.InterfaceName);
        Assert.Equal("NootsBoots", result.NetworkName);
        Assert.Equal(16, result.InterfaceIndex);
        Assert.Equal("10.0.0.140", result.LocalAddress);
        Assert.Equal("10.0.0.1", result.GatewayAddress);
        Assert.Equal(25566, result.Port);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
    }

    [Fact]
    public void Wintun_and_link_local_adapters_do_not_displace_routed_ethernet()
    {
        var result = Resolve(
            [
                Interface("{2F276229-9CBA-41C5-A03E-24F1D4155CF3}", "ExpressVPNOpenVPN WinTUN Adapter",
                    "ExpressVPN Tunnel", NetworkInterfaceType.Ethernet, "100.64.100.6", 51, false, false, 24),
                Interface(Guid.NewGuid().ToString("B"), "Wi-Fi", "Wi-Fi", NetworkInterfaceType.Wireless80211,
                    "169.254.144.124", 15, false, false, 16),
                Interface(Guid.NewGuid().ToString("B"), "Bluetooth", "Bluetooth", NetworkInterfaceType.Ethernet,
                    "169.254.98.67", 18, false, false, 16),
                Ethernet()
            ],
            [
                new NetworkCategoryBinding
                {
                    AdapterId = "{2F276229-9CBA-41C5-A03E-24F1D4155CF3}", InterfaceIndex = 51,
                    NetworkName = "VPN", Category = WindowsNetworkCategory.Public, Connected = true
                },
                Profile(16, WindowsNetworkCategory.Public)
            ]);

        Assert.True(result.Resolved);
        Assert.Equal("Ethernet", result.InterfaceName);
        Assert.Equal(FirewallProfile.Public, result.Profile);
    }

    [Theory]
    [InlineData(WindowsNetworkCategory.Private, FirewallProfile.Private)]
    [InlineData(WindowsNetworkCategory.DomainAuthenticated, FirewallProfile.Domain)]
    public void Private_and_domain_profiles_follow_the_same_exact_correlation(
        WindowsNetworkCategory category, FirewallProfile expected)
    {
        var result = Resolve([Ethernet()], [Profile(16, category)]);

        Assert.True(result.Resolved);
        Assert.Equal(expected, result.Profile);
    }

    [Fact]
    public void Interface_index_survives_alias_and_adapter_text_changes()
    {
        var renamed = Ethernet() with { Name = "Ethernet 2", Id = Guid.NewGuid().ToString("B") };
        var result = Resolve([renamed], [Profile(16, WindowsNetworkCategory.Public)]);

        Assert.True(result.Resolved);
        Assert.Equal("Ethernet 2", result.InterfaceName);
        Assert.Equal(FirewallProfile.Public, result.Profile);
    }

    [Fact]
    public void Missing_profile_fails_closed_but_retains_runtime_and_port()
    {
        var result = Resolve([Ethernet()], []);

        Assert.False(result.Resolved);
        Assert.Equal(FirewallTargetProblem.NetworkProfileUnavailable, result.Problem);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
        Assert.Equal(25566, result.Port);
        Assert.Equal(FirewallProfile.None, result.Profile);
    }

    [Fact]
    public void Multiple_profiles_for_the_selected_interface_are_ambiguous_and_fail_closed()
    {
        var result = Resolve(
            [Ethernet()],
            [Profile(16, WindowsNetworkCategory.Private), Profile(16, WindowsNetworkCategory.Public) with
            {
                AdapterId = Guid.NewGuid().ToString("B"), NetworkName = "Second profile"
            }]);

        Assert.False(result.Resolved);
        Assert.Equal(FirewallTargetProblem.NetworkProfileAmbiguous, result.Problem);
        Assert.Equal(25566, result.Port);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
    }

    [Fact]
    public void Java_failure_retains_correlated_profile_and_port()
    {
        var result = Resolve([Ethernet()], [Profile(16, WindowsNetworkCategory.Public)], programPath: null);

        Assert.False(result.Resolved);
        Assert.Equal(FirewallTargetProblem.RuntimeUnavailable, result.Problem);
        Assert.Equal(FirewallProfile.Public, result.Profile);
        Assert.Equal(WindowsNetworkCategory.Public, result.Category);
        Assert.Equal(25566, result.Port);
        Assert.Empty(result.ProgramPath);
    }

    [Fact]
    public void Port_failure_retains_correlated_profile_and_runtime()
    {
        var result = Resolve([Ethernet()], [Profile(16, WindowsNetworkCategory.Private)], port: 0);

        Assert.False(result.Resolved);
        Assert.Equal(FirewallTargetProblem.PortUnavailable, result.Problem);
        Assert.Equal(FirewallProfile.Private, result.Profile);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
        Assert.Equal(0, result.Port);
    }

    [Fact]
    public void Vpn_only_topology_does_not_invent_a_lan_profile()
    {
        var vpn = Interface("{2F276229-9CBA-41C5-A03E-24F1D4155CF3}", "ExpressVPN WinTUN",
            "ExpressVPN Tunnel", NetworkInterfaceType.Ethernet, "100.64.100.6", 51, true, true, 24);
        var result = Resolve([vpn], [new NetworkCategoryBinding
        {
            AdapterId = vpn.Id, InterfaceIndex = 51, NetworkName = "VPN",
            Category = WindowsNetworkCategory.Public, Connected = true
        }]);

        Assert.False(result.Resolved);
        Assert.Equal(FirewallTargetProblem.NetworkPathUnavailable, result.Problem);
        Assert.Equal(25566, result.Port);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
    }

    [Fact]
    public void Genuine_two_physical_path_ambiguity_is_typed_and_retains_target_evidence()
    {
        var ethernet = Ethernet() with { HasDefaultGateway = false, IsEffectiveDefaultRoute = false };
        var wifi = Interface(Guid.NewGuid().ToString("B"), "Wi-Fi", "Intel Wi-Fi",
            NetworkInterfaceType.Wireless80211, "192.168.1.50", 22, false, false, 24);

        var result = Resolve([ethernet, wifi], [Profile(16, WindowsNetworkCategory.Private)]);

        Assert.Equal(FirewallTargetProblem.NetworkPathAmbiguous, result.Problem);
        Assert.Equal(NetworkPathStatus.Ambiguous, result.NetworkPathStatus);
        Assert.Equal(25566, result.Port);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
    }

    [Fact]
    public void Nlm_failure_retains_selected_path_java_port_address_and_gateway()
    {
        var snapshot = NetworkCategorySnapshot.Unavailable(NetworkListStatus.ReadFailed,
            "NLM test failure 0x80004005");
        var result = new WindowsFirewallTargetResolver(
                new NetworkView([Ethernet()]), new SnapshotCategoryView(snapshot), _ => true)
            .Resolve(Java, "managed runtime", 25566);

        Assert.Equal(FirewallTargetProblem.NetworkListUnavailable, result.Problem);
        Assert.Equal(NetworkPathStatus.Available, result.NetworkPathStatus);
        Assert.Equal(NetworkListStatus.ReadFailed, result.NetworkListStatus);
        Assert.Equal(16, result.InterfaceIndex);
        Assert.Equal("10.0.0.140", result.LocalAddress);
        Assert.Equal("10.0.0.1", result.GatewayAddress);
        Assert.Equal(25566, result.Port);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
    }

    [Fact]
    public void Transient_interface_read_failure_becomes_diagnostic_evidence_not_an_exception()
    {
        var result = new WindowsFirewallTargetResolver(
                new FailingNetworkView(), new CategoryView([]), _ => true)
            .Resolve(Java, "managed runtime", 25566);

        Assert.Equal(FirewallTargetProblem.NetworkPathUnavailable, result.Problem);
        Assert.Equal(NetworkPathStatus.Unavailable, result.NetworkPathStatus);
        Assert.Contains("could not be read", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(25566, result.Port);
        Assert.Equal(Path.GetFullPath(Java), result.ProgramPath);
    }

    private static FirewallTargetResolution Resolve(
        IReadOnlyList<LanInterfaceCandidate> interfaces,
        IReadOnlyList<NetworkCategoryBinding> profiles,
        string? programPath = Java,
        int port = 25566) =>
        new WindowsFirewallTargetResolver(new NetworkView(interfaces), new CategoryView(profiles), _ => true)
            .Resolve(programPath, "managed runtime", port);

    private static LanInterfaceCandidate Ethernet() =>
        Interface(EthernetId, "Ethernet", "Realtek PCIe 2.5GbE Family Controller",
            NetworkInterfaceType.Ethernet, "10.0.0.140", 16, true, true, 24);

    private static LanInterfaceCandidate Interface(
        string id,
        string name,
        string description,
        NetworkInterfaceType type,
        string address,
        int index,
        bool gateway,
        bool defaultRoute,
        int prefix) => new(
        id, name, description, type, OperationalStatus.Up, 2_500_000_000,
        gateway, defaultRoute, [new LanAddressCandidate(IPAddress.Parse(address), prefix)], index);

    private static NetworkCategoryBinding Profile(int index, WindowsNetworkCategory category) => new()
    {
        AdapterId = EthernetId,
        InterfaceIndex = index,
        NetworkName = "NootsBoots",
        Category = category,
        Connected = true
    };

    private sealed class NetworkView(IReadOnlyList<LanInterfaceCandidate> interfaces) : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() => interfaces
            .Select(item => new RouterGatewayCandidate(item,
                item.HasDefaultGateway ? [IPAddress.Parse("10.0.0.1")] : []))
            .ToArray();
    }

    private sealed class CategoryView(IReadOnlyList<NetworkCategoryBinding> profiles) : INetworkCategoryView
    {
        public IReadOnlyList<NetworkCategoryBinding> Enumerate() => profiles;
    }

    private sealed class SnapshotCategoryView(NetworkCategorySnapshot snapshot) : INetworkCategoryView
    {
        public IReadOnlyList<NetworkCategoryBinding> Enumerate() => snapshot.Bindings;
        public NetworkCategorySnapshot Read() => snapshot;
    }

    private sealed class FailingNetworkView : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() =>
            throw new NetworkInformationException(1234);
    }
}
