using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

internal sealed record WindowsTcpListenerOwner(int Port, int ProcessId);
internal enum WindowsOwnedEndpointTransport { Tcp, Udp }

internal sealed record WindowsOwnedNetworkEndpoint(WindowsOwnedEndpointTransport Transport,
    AddressFamily AddressFamily, IPAddress LocalAddress, int Port, int ProcessId)
{
    public TcpState? State { get; init; } = Transport == WindowsOwnedEndpointTransport.Tcp ? TcpState.Listen : null;
    public IPAddress? RemoteAddress { get; init; }
    public int? RemotePort { get; init; }
    public bool IsExactLoopback => IPAddress.IsLoopback(LocalAddress.IsIPv4MappedToIPv6 ? LocalAddress.MapToIPv4() : LocalAddress);
    public bool IsWildcard => LocalAddress.Equals(IPAddress.Any) || LocalAddress.Equals(IPAddress.IPv6Any);
    public StartupEndpointObservation ToObservation(Guid attempt, long creation, bool owned) => new()
    {
        AttemptId = attempt, Transport = (StartupEndpointTransport)Transport, AddressFamily = AddressFamily.ToString(),
        LocalAddress = LocalAddress.ToString(), LocalPort = Port, State = State,
        RemoteAddress = RemoteAddress?.ToString(), RemotePort = RemotePort, ProcessId = ProcessId,
        ProcessCreationTicks = creation, ExactJobOwnershipVerified = owned,
        Executable = owned ? "Exact executable generation verified by controller process ownership boundary" : "",
        ObservedAtUtc = DateTimeOffset.UtcNow
    };
}

internal interface IWindowsOwnedNetworkEndpointSource
{
    IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture();
}
internal sealed class WindowsOwnedNetworkEndpointSource : IWindowsOwnedNetworkEndpointSource
{
    public static WindowsOwnedNetworkEndpointSource Instance { get; } = new();
    private WindowsOwnedNetworkEndpointSource() { }
    public IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture() => WindowsOwnedNetworkEndpoints.Capture();
}

/// <summary>The controller uses the same bounded native parser/collector as production.</summary>
internal static class WindowsOwnedNetworkEndpoints
{
    public static IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture() =>
        WindowsStagedNetworkEndpoints.Capture().Select(Map).ToArray();
    internal static IReadOnlyList<WindowsOwnedNetworkEndpoint> ParseTcpTable(ReadOnlySpan<byte> table, AddressFamily family) =>
        WindowsStagedNetworkEndpoints.ParseTcp(table, family).Select(Map).ToArray();
    internal static IReadOnlyList<WindowsOwnedNetworkEndpoint> ParseUdpTable(ReadOnlySpan<byte> table, AddressFamily family) =>
        WindowsStagedNetworkEndpoints.ParseUdp(table, family).Select(Map).ToArray();
    private static WindowsOwnedNetworkEndpoint Map(WindowsStagedNetworkEndpoint endpoint) => new(
        (WindowsOwnedEndpointTransport)endpoint.Transport, endpoint.AddressFamily, endpoint.LocalAddress,
        endpoint.Port, endpoint.ProcessId)
    {
        State = endpoint.TcpState is null ? null : (TcpState)endpoint.TcpState,
        RemoteAddress = endpoint.RemoteAddress, RemotePort = endpoint.RemotePort
    };
}
internal static class WindowsTcpListenerOwners
{
    public static IReadOnlyList<WindowsTcpListenerOwner> ForPort(int port) =>
        WindowsOwnedNetworkEndpoints.Capture().Where(endpoint =>
            endpoint.Transport == WindowsOwnedEndpointTransport.Tcp && endpoint.State == TcpState.Listen && endpoint.Port == port)
        .Select(endpoint => new WindowsTcpListenerOwner(endpoint.Port, endpoint.ProcessId)).ToArray();
}
