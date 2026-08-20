using System.Net;
using System.Net.Sockets;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The isolation guarantee every fixture port now depends on (CP-2026-014).
/// </summary>
/// <remarks>
/// The agent reconnect fixture used to default to TCP 25565, so any machine running a real Minecraft
/// server failed it for a reason that had nothing to do with the agent. These tests hold the rule that
/// replaced it: a fixture port is chosen, never assumed, and never chosen twice.
/// </remarks>
public sealed class TestPortAllocatorTests
{
    [Fact]
    public void A_fixture_port_is_never_the_well_known_minecraft_port()
    {
        var ports = Enumerable.Range(0, 32).Select(_ => TestPortAllocator.Reserve()).ToArray();

        Assert.DoesNotContain(25565, ports);
    }

    [Fact]
    public void No_two_fixtures_are_handed_the_same_port()
    {
        var ports = Enumerable.Range(0, 64).Select(_ => TestPortAllocator.Reserve()).ToArray();

        Assert.Equal(ports.Length, ports.Distinct().Count());
    }

    [Fact]
    public void A_port_something_else_is_already_holding_is_never_handed_out()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        try
        {
            var taken = ((IPEndPoint)occupied.LocalEndpoint).Port;

            var ports = Enumerable.Range(0, 64).Select(_ => TestPortAllocator.Reserve()).ToArray();

            Assert.DoesNotContain(taken, ports);
        }
        finally
        {
            occupied.Stop();
        }
    }

    [Fact]
    public void A_fixture_can_actually_listen_on_the_port_it_was_given()
    {
        var port = TestPortAllocator.Reserve();

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            Assert.Equal(port, ((IPEndPoint)listener.LocalEndpoint).Port);
        }
        finally
        {
            listener.Stop();
        }
    }
}
