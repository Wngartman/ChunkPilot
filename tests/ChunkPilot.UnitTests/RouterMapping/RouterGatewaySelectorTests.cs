using System.Net;
using System.Net.NetworkInformation;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// Which adapter a port may be forwarded to. This shares the LAN-interface rule with
/// <see cref="LanAddressSelector"/>, so these cases also guard the ExpressVPN WinTUN fix against being
/// re-introduced through a different door.
/// </summary>
public sealed class RouterGatewaySelectorTests
{
    [Fact]
    public void The_default_route_ethernet_adapter_is_chosen_over_a_faster_vpn()
    {
        var selected = RouterGatewaySelector.Select([
            Candidate("vpn", "ExpressVPNOpenVPN WinTUN Adapter", NetworkInterfaceType.Ethernet,
                "100.64.100.6", 32, "100.64.100.1", speed: 10_000, route: true),
            Candidate("eth", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                "192.168.1.42", 24, "192.168.1.1", speed: 1_000, route: true)
        ]);

        Assert.NotNull(selected);
        Assert.Equal("eth", selected.InterfaceId);
        Assert.Equal(IPAddress.Parse("192.168.1.1"), selected.GatewayAddress);
        Assert.Equal(IPAddress.Parse("192.168.1.42"), selected.LocalAddress);
    }

    [Fact]
    public void Wi_fi_is_accepted_when_it_is_the_effective_route()
    {
        var selected = RouterGatewaySelector.Select([
            Candidate("wifi", "Intel Wi-Fi 6", NetworkInterfaceType.Wireless80211,
                "192.168.0.31", 24, "192.168.0.1", route: true)
        ]);

        Assert.Equal("wifi", selected!.InterfaceId);
    }

    [Theory]
    [InlineData("Hyper-V Virtual Ethernet Adapter")]
    [InlineData("VMware Virtual Ethernet Adapter")]
    [InlineData("TAP-Windows Adapter V9")]
    [InlineData("Tailscale Tunnel")]
    public void Virtual_and_tunnel_adapters_are_never_offered_a_port(string description)
    {
        Assert.Null(RouterGatewaySelector.Select([
            Candidate("virtual", description, NetworkInterfaceType.Ethernet,
                "192.168.56.1", 24, "192.168.56.254", route: true)
        ]));
    }

    [Fact]
    public void Loopback_is_not_a_router()
    {
        Assert.Null(RouterGatewaySelector.Select([
            Candidate("loop", "Software Loopback", NetworkInterfaceType.Loopback,
                "127.0.0.1", 8, "127.0.0.1", route: true)
        ]));
    }

    [Fact]
    public void A_disconnected_adapter_is_not_evidence()
    {
        Assert.Null(RouterGatewaySelector.Select([
            Candidate("eth", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                "192.168.1.42", 24, "192.168.1.1", status: OperationalStatus.Down)
        ]));
    }

    [Fact]
    public void No_gateway_means_no_binding()
    {
        Assert.Null(RouterGatewaySelector.Select([
            new RouterGatewayCandidate(
                Interface("eth", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                    "192.168.1.42", 24, OperationalStatus.Up, 1_000, false, false),
                [])
        ]));
    }

    [Fact]
    public void Two_plausible_adapters_without_route_evidence_are_ambiguous()
    {
        Assert.Null(RouterGatewaySelector.Select([
            Candidate("a", "Ethernet one", NetworkInterfaceType.Ethernet, "192.168.1.10", 24, "192.168.1.1"),
            Candidate("b", "Ethernet two", NetworkInterfaceType.Ethernet, "10.10.0.10", 24, "10.10.0.1")
        ]));
    }

    [Fact]
    public void Two_adapters_are_resolved_when_one_owns_the_effective_route()
    {
        var selected = RouterGatewaySelector.Select([
            Candidate("a", "Ethernet one", NetworkInterfaceType.Ethernet, "192.168.1.10", 24, "192.168.1.1"),
            Candidate("b", "Ethernet two", NetworkInterfaceType.Ethernet, "10.10.0.10", 24, "10.10.0.1", route: true)
        ]);

        Assert.Equal("b", selected!.InterfaceId);
    }

    /// <summary>An adapter whose gateway is on a different subnet is not evidence of the home router.</summary>
    [Fact]
    public void A_gateway_outside_the_adapters_subnet_is_rejected()
    {
        Assert.Null(RouterGatewaySelector.Select([
            Candidate("eth", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                "192.168.1.42", 24, "10.0.0.1", route: true)
        ]));
    }

    [Fact]
    public void A_public_address_on_a_physical_adapter_is_not_treated_as_a_lan()
    {
        Assert.Null(RouterGatewaySelector.Select([
            Candidate("eth", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                "203.0.113.5", 24, "203.0.113.1", route: true)
        ]));
    }

    [Fact]
    public void An_ipv6_only_adapter_yields_no_ipv4_mapping_target()
    {
        Assert.Null(RouterGatewaySelector.Select([
            new RouterGatewayCandidate(
                Interface("eth", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                    "fd00::42", 64, OperationalStatus.Up, 1_000, true, true),
                [IPAddress.Parse("fd00::1")])
        ]));
    }

    [Theory]
    [InlineData("192.168.1.42", "192.168.1.1", 24, true)]
    [InlineData("192.168.1.42", "192.168.2.1", 24, false)]
    [InlineData("10.0.5.7", "10.255.255.254", 8, true)]
    [InlineData("172.16.4.4", "172.17.0.1", 12, true)]
    [InlineData("172.16.4.4", "172.17.0.1", 16, false)]
    public void Subnet_membership_is_computed_from_the_prefix(
        string address, string gateway, int prefix, bool expected) =>
        Assert.Equal(expected, RouterGatewaySelector.SameIpv4Subnet(
            IPAddress.Parse(address), IPAddress.Parse(gateway), prefix));

    private static RouterGatewayCandidate Candidate(
        string id,
        string description,
        NetworkInterfaceType type,
        string address,
        int prefix,
        string gateway,
        long speed = 1_000,
        bool route = false,
        OperationalStatus status = OperationalStatus.Up) =>
        new(Interface(id, description, type, address, prefix, status, speed, true, route),
            [IPAddress.Parse(gateway)]);

    private static LanInterfaceCandidate Interface(
        string id,
        string description,
        NetworkInterfaceType type,
        string address,
        int prefix,
        OperationalStatus status,
        long speed,
        bool hasGateway,
        bool route) =>
        new(id, id, description, type, status, speed, hasGateway, route,
            [new LanAddressCandidate(IPAddress.Parse(address), prefix)]);
}
