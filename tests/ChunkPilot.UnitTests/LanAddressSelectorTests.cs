using System.Net;
using System.Net.NetworkInformation;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class LanAddressSelectorTests
{
    [Fact]
    public void Effective_physical_route_beats_faster_vpn_and_virtual_adapters()
    {
        var selected = LanAddressSelector.Select([
            Candidate("vpn", "ExpressVPNOpenVPN WinTUN Adapter", NetworkInterfaceType.Ethernet,
                "100.64.100.6", speed: 10_000, route: true),
            Candidate("hyperv", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet,
                "172.22.16.1", speed: 10_000, gateway: true),
            Candidate("ethernet", "Realtek PCIe Ethernet", NetworkInterfaceType.Ethernet,
                "192.168.1.42", speed: 1_000, gateway: true, route: true),
            Candidate("wifi", "Intel Wi-Fi", NetworkInterfaceType.Wireless80211,
                "192.168.1.43", speed: 800, gateway: true)
        ]);

        Assert.NotNull(selected);
        Assert.Equal("ethernet", selected.Interface.Id);
        Assert.Equal(IPAddress.Parse("192.168.1.42"), selected.Address.Address);
    }

    [Fact]
    public void Disconnected_loopback_public_cgnat_and_link_local_addresses_are_not_lan_evidence()
    {
        var selected = LanAddressSelector.Select([
            Candidate("down", "Ethernet", NetworkInterfaceType.Ethernet, "192.168.1.2", status: OperationalStatus.Down),
            Candidate("loop", "Loopback", NetworkInterfaceType.Loopback, "127.0.0.1"),
            Candidate("public", "Ethernet", NetworkInterfaceType.Ethernet, "8.8.8.8", route: true),
            Candidate("cgnat", "Ethernet", NetworkInterfaceType.Ethernet, "100.64.1.2", route: true),
            Candidate("link", "Ethernet", NetworkInterfaceType.Ethernet, "169.254.1.2", route: true)
        ]);

        Assert.Null(selected);
    }

    [Fact]
    public void Multiple_physical_private_adapters_without_route_evidence_are_ambiguous()
    {
        Assert.Null(LanAddressSelector.Select([
            Candidate("one", "Ethernet one", NetworkInterfaceType.Ethernet, "10.0.0.4"),
            Candidate("two", "Ethernet two", NetworkInterfaceType.Ethernet, "192.168.2.4")
        ]));
    }

    [Fact]
    public void Unique_local_ipv6_is_supported_when_ipv4_is_absent()
    {
        var selected = LanAddressSelector.Select([
            Candidate("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211, "fd12:3456::44", gateway: true)
        ]);

        Assert.Equal(IPAddress.Parse("fd12:3456::44"), selected!.Address.Address);
    }

    private static LanInterfaceCandidate Candidate(
        string id,
        string description,
        NetworkInterfaceType type,
        string address,
        long speed = 1_000,
        bool gateway = false,
        bool route = false,
        OperationalStatus status = OperationalStatus.Up) =>
        new(id, id, description, type, status, speed, gateway, route,
            [new LanAddressCandidate(IPAddress.Parse(address), address.Contains(':') ? 64 : 24)]);
}
