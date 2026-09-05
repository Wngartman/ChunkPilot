using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class WindowsStagedNetworkEndpointTests
{
    [Fact]
    public void Historical_connection_shape_demonstrates_the_old_false_listener_rejection()
    {
        // The saved failed report retained Done and the rejection, not the original socket tuple.
        // Documentation-only addresses reproduce the reported shape without inventing attribution.
        var observed = new[]
        {
            new WindowsStagedNetworkEndpoint(WindowsStagedEndpointTransport.Tcp,
                AddressFamily.InterNetwork, IPAddress.Loopback, 25585, 200, WindowsStagedTcpState.Listen),
            new WindowsStagedNetworkEndpoint(WindowsStagedEndpointTransport.Tcp,
                AddressFamily.InterNetwork, IPAddress.Parse("192.0.2.10"), 49000, 200,
                WindowsStagedTcpState.Established)
        };
        var oldRejection = observed.Any(endpoint => !endpoint.IsLoopback);
        Assert.True(oldRejection); // Production predicate at inspected base.
        Assert.DoesNotContain(observed, endpoint => endpoint.IsTcpListener && !endpoint.IsLoopback);
    }

    [Fact]
    public void Tcp_parser_preserves_established_ipv4_endpoint_instead_of_treating_table_as_listeners_only()
    {
        var endpoint = Assert.Single(WindowsStagedNetworkEndpoints.ParseTcp(
            NativeTcpTable(IPAddress.Parse("192.0.2.10"), 49_000, 200, WindowsStagedTcpState.Established),
            AddressFamily.InterNetwork));

        Assert.Equal(WindowsStagedTcpState.Established, endpoint.TcpState);
        Assert.False(endpoint.IsTcpListener);
        Assert.False(endpoint.IsLoopback);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), endpoint.LocalAddress);
    }

    [Fact]
    public void Tcp_parser_preserves_established_ipv6_endpoint()
    {
        var endpoint = Assert.Single(WindowsStagedNetworkEndpoints.ParseTcp(
            NativeTcpTable(IPAddress.Parse("2001:db8::10"), 49_001, 201, WindowsStagedTcpState.Established),
            AddressFamily.InterNetworkV6));

        Assert.Equal(WindowsStagedTcpState.Established, endpoint.TcpState);
        Assert.False(endpoint.IsTcpListener);
        Assert.False(endpoint.IsLoopback);
        Assert.Equal(IPAddress.Parse("2001:db8::10"), endpoint.LocalAddress);
    }

    [Fact]
    public void Only_tcp_listen_state_is_valid_listener_identity()
    {
        var listening = Assert.Single(WindowsStagedNetworkEndpoints.ParseTcp(
            NativeTcpTable(IPAddress.Loopback, 25_585, 200, WindowsStagedTcpState.Listen),
            AddressFamily.InterNetwork));
        var established = Assert.Single(WindowsStagedNetworkEndpoints.ParseTcp(
            NativeTcpTable(IPAddress.Loopback, 25_585, 200, WindowsStagedTcpState.Established),
            AddressFamily.InterNetwork));

        Assert.True(listening.IsTcpListener);
        Assert.False(established.IsTcpListener);
    }

    [Fact]
    public void Ownerless_time_wait_row_from_owner_pid_all_is_not_owned_endpoint_evidence()
    {
        var endpoints = WindowsStagedNetworkEndpoints.ParseTcp(
            NativeTcpTable(IPAddress.Loopback, 25_585, 0, WindowsStagedTcpState.TimeWait),
            AddressFamily.InterNetwork);

        Assert.Empty(endpoints);
    }

    [Fact]
    public void Tcp_ipv6_scopes_and_remote_ports_use_network_order()
    {
        var bytes = NativeTcpTable(IPAddress.Parse("fe80::10%7"), 49001, 201, WindowsStagedTcpState.Established);
        var row = bytes.AsSpan(4);
        IPAddress.Parse("fe80::20").GetAddressBytes().CopyTo(row.Slice(24, 16));
        BinaryPrimitives.WriteUInt32BigEndian(row.Slice(40, 4), 9);
        WriteNativeNetworkPort(row.Slice(44, 4), 443);
        row[46] = 0xaa; row[47] = 0xbb; // Windows documents only the first two port bytes.
        var parsed = Assert.Single(WindowsStagedNetworkEndpoints.ParseTcp(bytes, AddressFamily.InterNetworkV6));
        Assert.Equal(7, parsed.LocalAddress.ScopeId);
        Assert.Equal(IPAddress.Parse("fe80::20%9"), parsed.RemoteAddress);
        Assert.Equal(443, parsed.RemotePort);
    }

    [Fact]
    public void Invalid_tcp_state_does_not_become_an_empty_clean_inventory()
    {
        var bytes = NativeTcpTable(IPAddress.Loopback, 25585, 200, (WindowsStagedTcpState)999);
        Assert.Throws<InvalidDataException>(() => WindowsStagedNetworkEndpoints.ParseTcp(bytes, AddressFamily.InterNetwork));
    }

    private static byte[] NativeTcpTable(
        IPAddress address,
        int port,
        int processId,
        WindowsStagedTcpState state)
    {
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var rowSize = ipv4 ? 24 : 56;
        var table = new byte[sizeof(uint) + rowSize];
        BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(0, sizeof(uint)), 1);
        var row = table.AsSpan(sizeof(uint), rowSize);
        if (ipv4)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(0, sizeof(uint)), (uint)state);
            address.GetAddressBytes().CopyTo(row.Slice(4, 4));
            WriteNativeNetworkPort(row.Slice(8, sizeof(uint)), port);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(20, sizeof(uint)), checked((uint)processId));
        }
        else
        {
            address.GetAddressBytes().CopyTo(row.Slice(0, 16));
            BinaryPrimitives.WriteUInt32BigEndian(row.Slice(16, sizeof(uint)), checked((uint)address.ScopeId));
            WriteNativeNetworkPort(row.Slice(20, sizeof(uint)), port);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(48, sizeof(uint)), (uint)state);
            BinaryPrimitives.WriteUInt32LittleEndian(row.Slice(52, sizeof(uint)), checked((uint)processId));
        }
        return table;
    }

    private static void WriteNativeNetworkPort(Span<byte> field, int port)
    {
        field.Clear();
        field[0] = checked((byte)(port >> 8));
        field[1] = checked((byte)(port & 0xff));
    }
}
