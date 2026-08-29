using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ChunkPilot.Core;
using Microsoft.Win32.SafeHandles;

namespace ChunkPilot.Certification;

internal sealed record WindowsTcpListenerOwner(int Port, int ProcessId);

internal enum WindowsOwnedEndpointTransport
{
    Tcp,
    Udp
}

internal sealed record WindowsOwnedNetworkEndpoint(
    WindowsOwnedEndpointTransport Transport,
    AddressFamily AddressFamily,
    IPAddress LocalAddress,
    int Port,
    int ProcessId)
{
    public bool IsExactLoopback =>
        IPAddress.IsLoopback(LocalAddress) &&
        !LocalAddress.Equals(IPAddress.Any) &&
        !LocalAddress.Equals(IPAddress.IPv6Any);

    public bool IsWildcard =>
        LocalAddress.Equals(IPAddress.Any) ||
        LocalAddress.Equals(IPAddress.IPv6Any);
}

internal interface IWindowsOwnedNetworkEndpointSource
{
    IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture();
}

internal sealed class WindowsOwnedNetworkEndpointSource : IWindowsOwnedNetworkEndpointSource
{
    public static WindowsOwnedNetworkEndpointSource Instance { get; } = new();

    private WindowsOwnedNetworkEndpointSource()
    {
    }

    public IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture() =>
        WindowsOwnedNetworkEndpoints.Capture();
}

internal static class WindowsOwnedNetworkEndpoints
{
    private const int AddressFamilyInternet = 2;
    private const int AddressFamilyInternetV6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;
    private const uint TcpStateListen = 2;
    private const int MaximumRows = 65_536;
    private const int MaximumTableBytes = 16 * 1024 * 1024;
    private const int TcpIpv4RowBytes = 24;
    private const int TcpIpv6RowBytes = 56;
    private const int UdpIpv4RowBytes = 12;
    private const int UdpIpv6RowBytes = 28;

    public static IReadOnlyList<WindowsOwnedNetworkEndpoint> Capture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Exact endpoint process ownership requires Windows IP Helper tables.");

        var endpoints = new List<WindowsOwnedNetworkEndpoint>();
        endpoints.AddRange(ParseTcpTable(
            ReadTcpTable(AddressFamilyInternet), AddressFamily.InterNetwork));
        endpoints.AddRange(ParseTcpTable(
            ReadTcpTable(AddressFamilyInternetV6), AddressFamily.InterNetworkV6));
        endpoints.AddRange(ParseUdpTable(
            ReadUdpTable(AddressFamilyInternet), AddressFamily.InterNetwork));
        endpoints.AddRange(ParseUdpTable(
            ReadUdpTable(AddressFamilyInternetV6), AddressFamily.InterNetworkV6));
        return endpoints;
    }

    internal static IReadOnlyList<WindowsOwnedNetworkEndpoint> ParseTcpTable(
        ReadOnlySpan<byte> table,
        AddressFamily addressFamily) =>
        ParseTable(table, addressFamily, WindowsOwnedEndpointTransport.Tcp);

    internal static IReadOnlyList<WindowsOwnedNetworkEndpoint> ParseUdpTable(
        ReadOnlySpan<byte> table,
        AddressFamily addressFamily) =>
        ParseTable(table, addressFamily, WindowsOwnedEndpointTransport.Udp);

    private static IReadOnlyList<WindowsOwnedNetworkEndpoint> ParseTable(
        ReadOnlySpan<byte> table,
        AddressFamily addressFamily,
        WindowsOwnedEndpointTransport transport)
    {
        if (addressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentOutOfRangeException(nameof(addressFamily));
        if (table.Length < sizeof(uint))
            throw new InvalidDataException(
                "Windows returned a truncated endpoint ownership table.");

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > MaximumRows)
            throw new InvalidDataException(
                "Windows returned an oversized endpoint ownership table.");
        var rowSize = (transport, addressFamily) switch
        {
            (WindowsOwnedEndpointTransport.Tcp, AddressFamily.InterNetwork) => TcpIpv4RowBytes,
            (WindowsOwnedEndpointTransport.Tcp, AddressFamily.InterNetworkV6) => TcpIpv6RowBytes,
            (WindowsOwnedEndpointTransport.Udp, AddressFamily.InterNetwork) => UdpIpv4RowBytes,
            (WindowsOwnedEndpointTransport.Udp, AddressFamily.InterNetworkV6) => UdpIpv6RowBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
        };
        var requiredBytes = checked(sizeof(uint) + (long)count * rowSize);
        if (requiredBytes > table.Length)
            throw new InvalidDataException(
                "Windows returned a truncated endpoint ownership table.");

        var endpoints = new List<WindowsOwnedNetworkEndpoint>(checked((int)count));
        for (var index = 0; index < count; index++)
        {
            var offset = checked(sizeof(uint) + (int)index * rowSize);
            var row = table.Slice(offset, rowSize);
            if (transport == WindowsOwnedEndpointTransport.Tcp)
            {
                var stateOffset = addressFamily == AddressFamily.InterNetwork ? 0 : 48;
                if (BinaryPrimitives.ReadUInt32LittleEndian(
                        row.Slice(stateOffset, sizeof(uint))) != TcpStateListen)
                    continue;
            }

            var addressOffset = transport == WindowsOwnedEndpointTransport.Tcp &&
                                addressFamily == AddressFamily.InterNetwork
                ? sizeof(uint)
                : 0;
            var addressLength = addressFamily == AddressFamily.InterNetwork ? 4 : 16;
            var scopeId = addressFamily == AddressFamily.InterNetworkV6
                ? BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, sizeof(uint)))
                : 0;
            var localAddress = addressFamily == AddressFamily.InterNetwork
                ? new IPAddress(row.Slice(addressOffset, addressLength))
                : new IPAddress(row.Slice(addressOffset, addressLength), scopeId);
            var portOffset = (transport, addressFamily) switch
            {
                (WindowsOwnedEndpointTransport.Tcp, AddressFamily.InterNetwork) => 8,
                (WindowsOwnedEndpointTransport.Tcp, AddressFamily.InterNetworkV6) => 20,
                (WindowsOwnedEndpointTransport.Udp, AddressFamily.InterNetwork) => 4,
                (WindowsOwnedEndpointTransport.Udp, AddressFamily.InterNetworkV6) => 20,
                _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
            };
            var processOffset = (transport, addressFamily) switch
            {
                (WindowsOwnedEndpointTransport.Tcp, AddressFamily.InterNetwork) => 20,
                (WindowsOwnedEndpointTransport.Tcp, AddressFamily.InterNetworkV6) => 52,
                (WindowsOwnedEndpointTransport.Udp, AddressFamily.InterNetwork) => 8,
                (WindowsOwnedEndpointTransport.Udp, AddressFamily.InterNetworkV6) => 24,
                _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
            };
            var processId = BinaryPrimitives.ReadUInt32LittleEndian(
                row.Slice(processOffset, sizeof(uint)));
            if (processId is 0 or > int.MaxValue)
                throw new InvalidDataException(
                    "Windows returned an invalid endpoint process identity.");

            endpoints.Add(new WindowsOwnedNetworkEndpoint(
                transport,
                addressFamily,
                localAddress,
                ReadNetworkPort(row.Slice(portOffset, sizeof(uint))),
                checked((int)processId)));
        }
        return endpoints;
    }

    private static int ReadNetworkPort(ReadOnlySpan<byte> port) =>
        checked(port[0] * 256 + port[1]);

    private static byte[] ReadTcpTable(int addressFamily)
    {
        uint size = 0;
        var first = GetExtendedTcpTable(
            IntPtr.Zero, ref size, order: false, addressFamily,
            TcpTableClass.OwnerPidListener, reserved: 0);
        ValidateSizedTable(first, size, "TCP listener");

        var allocationSize = size;
        var buffer = Marshal.AllocHGlobal(checked((int)allocationSize));
        try
        {
            var result = GetExtendedTcpTable(
                buffer, ref size, order: false, addressFamily,
                TcpTableClass.OwnerPidListener, reserved: 0);
            return CopyReturnedTable(buffer, allocationSize, size, result, "TCP listener");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] ReadUdpTable(int addressFamily)
    {
        uint size = 0;
        var first = GetExtendedUdpTable(
            IntPtr.Zero, ref size, order: false, addressFamily,
            UdpTableClass.OwnerPid, reserved: 0);
        ValidateSizedTable(first, size, "UDP endpoint");

        var allocationSize = size;
        var buffer = Marshal.AllocHGlobal(checked((int)allocationSize));
        try
        {
            var result = GetExtendedUdpTable(
                buffer, ref size, order: false, addressFamily,
                UdpTableClass.OwnerPid, reserved: 0);
            return CopyReturnedTable(buffer, allocationSize, size, result, "UDP endpoint");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateSizedTable(uint result, uint size, string tableKind)
    {
        if (result != ErrorInsufficientBuffer ||
            size < sizeof(uint) ||
            size > MaximumTableBytes)
            throw new Win32Exception(
                checked((int)result),
                $"Windows did not size the bounded {tableKind} ownership table.");
    }

    private static byte[] CopyReturnedTable(
        IntPtr buffer,
        uint allocationSize,
        uint returnedSize,
        uint result,
        string tableKind)
    {
        if (result != NoError)
            throw new Win32Exception(
                checked((int)result),
                $"Windows did not return the {tableKind} ownership table.");
        if (returnedSize < sizeof(uint) || returnedSize > allocationSize)
            throw new InvalidDataException(
                $"Windows returned an invalid {tableKind} ownership table size.");
        var table = new byte[checked((int)returnedSize)];
        Marshal.Copy(buffer, table, 0, table.Length);
        return table;
    }

    private enum TcpTableClass
    {
        OwnerPidListener = 3
    }

    private enum UdpTableClass
    {
        OwnerPid = 1
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr tcpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        TcpTableClass tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr udpTable,
        ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order,
        int ipVersion,
        UdpTableClass tableClass,
        uint reserved);
}

internal static class WindowsTcpListenerOwners
{
    public static IReadOnlyList<WindowsTcpListenerOwner> ForPort(int port) =>
        WindowsOwnedNetworkEndpoints.Capture()
            .Where(endpoint =>
                endpoint.Transport == WindowsOwnedEndpointTransport.Tcp &&
                endpoint.Port == port)
            .Select(endpoint => new WindowsTcpListenerOwner(endpoint.Port, endpoint.ProcessId))
            .ToArray();
}

internal static class WindowsProcessParents
{
    private const uint SnapshotProcesses = 0x00000002;
    private const int ErrorNoMoreFiles = 18;
    private const int MaximumProcesses = 131_072;

    public static IReadOnlyDictionary<int, int> Capture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Exact process ancestry requires the Windows process snapshot API.");
        using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows did not create the bounded process ancestry snapshot.");
        var entry = new ProcessEntry
        {
            Size = checked((uint)Marshal.SizeOf<ProcessEntry>())
        };
        if (!Process32First(snapshot, ref entry))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows did not begin the process ancestry snapshot.");
        var result = new Dictionary<int, int>();
        while (true)
        {
            if (result.Count >= MaximumProcesses)
                throw new InvalidDataException(
                    "The Windows process ancestry snapshot exceeded its bounded size.");
            if (entry.ProcessId is > 0 and <= int.MaxValue &&
                entry.ParentProcessId <= int.MaxValue)
                result[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
            entry.Size = checked((uint)Marshal.SizeOf<ProcessEntry>());
            if (Process32Next(snapshot, ref entry))
                continue;
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNoMoreFiles)
                throw new Win32Exception(error,
                    "Windows did not complete the process ancestry snapshot.");
            break;
        }
        return result;
    }

    /// <summary>
    /// Proves one live exact-owned process is the root or a descendant of it without treating a
    /// reusable Windows PID as process identity. Every traversed process must be present with the
    /// same creation identity in all bracketing Job snapshots, must still be that live generation,
    /// and each parent must have been created strictly before its child. An exited historical
    /// parent or a PID-reused replacement therefore fails closed.
    /// </summary>
    internal static bool IsLiveStableRootOrDescendant(
        int candidate,
        int root,
        IReadOnlyDictionary<int, int> parents,
        IReadOnlyDictionary<int, long> stableOwnedIdentities,
        Func<int, long, bool> exactOwnedProcessIdentityStillMatches)
    {
        ArgumentNullException.ThrowIfNull(parents);
        ArgumentNullException.ThrowIfNull(stableOwnedIdentities);
        ArgumentNullException.ThrowIfNull(exactOwnedProcessIdentityStillMatches);
        if (candidate <= 0 || root <= 0 ||
            !TryGetLiveStableIdentity(
                candidate,
                stableOwnedIdentities,
                exactOwnedProcessIdentityStillMatches,
                out var currentCreationTicks))
            return false;

        // Minecraft normally owns the listener in the exact Java root. Keep that equality case
        // direct: there is no ancestry link to infer and no parent snapshot entry is required.
        if (candidate == root)
            return true;

        var visited = new HashSet<int>();
        var current = candidate;
        for (var depth = 0; depth < 64 && current > 0 && visited.Add(current); depth++)
        {
            if (!parents.TryGetValue(current, out var parent) || parent <= 0 ||
                !TryGetLiveStableIdentity(
                    parent,
                    stableOwnedIdentities,
                    exactOwnedProcessIdentityStillMatches,
                    out var parentCreationTicks) ||
                parentCreationTicks >= currentCreationTicks)
                return false;
            if (parent == root)
                return true;
            current = parent;
            currentCreationTicks = parentCreationTicks;
        }
        return false;
    }

    private static bool TryGetLiveStableIdentity(
        int processId,
        IReadOnlyDictionary<int, long> stableOwnedIdentities,
        Func<int, long, bool> exactOwnedProcessIdentityStillMatches,
        out long creationTicks)
    {
        if (!stableOwnedIdentities.TryGetValue(processId, out creationTicks) ||
            creationTicks == ProcessCreationIdentity.Unknown)
            return false;
        return exactOwnedProcessIdentityStillMatches(processId, creationTicks);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry entry);
}
