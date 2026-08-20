using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net.NetworkInformation;
using System.Net;
using System.Net.Sockets;
using Microsoft.Win32;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class ProcessStatisticsProvider
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset Timestamp)> previous = [];
    private readonly object gate = new();
    private ulong lastIdle;
    private ulong lastKernel;
    private ulong lastUser;
    private readonly StaticHostInfo staticInfo = ReadStaticHostInfo();
    private DateTimeOffset lastNetworkSample;
    private long lastNetworkReceived;
    private long lastNetworkSent;
    private string lastNetworkAdapterId = "";
    private DateTimeOffset lastStorageSample;
    private long managedStorageBytes;
    private long backupStorageBytes;

    public StatisticsSample SampleProcessTree(int rootProcessId)
    {
        var processIds = ProcessTree.GetDescendantsAndSelf(rootProcessId);
        var cpu = TimeSpan.Zero;
        long workingSet = 0;
        long peak = 0;
        var threads = 0;
        long reads = 0;
        long writes = 0;
        foreach (var processId in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                cpu += process.TotalProcessorTime;
                workingSet += process.WorkingSet64;
                peak += process.PeakWorkingSet64;
                threads += process.Threads.Count;
                if (OperatingSystem.IsWindows() && GetProcessIoCounters(process.Handle, out var io))
                {
                    reads += checked((long)Math.Min(io.ReadTransferCount, long.MaxValue));
                    writes += checked((long)Math.Min(io.WriteTransferCount, long.MaxValue));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { }
        }

        var now = DateTimeOffset.UtcNow;
        double cpuPercent = 0;
        lock (gate)
        {
            if (previous.TryGetValue(rootProcessId, out var prior))
            {
                var elapsed = (now - prior.Timestamp).TotalSeconds;
                if (elapsed > 0)
                    cpuPercent = Math.Clamp((cpu - prior.Cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
            }
            previous[rootProcessId] = (cpu, now);
        }
        return new StatisticsSample
        {
            Timestamp = now,
            CpuPercent = cpuPercent,
            WorkingSetBytes = workingSet,
            PeakWorkingSetBytes = peak,
            ProcessCount = processIds.Count,
            ThreadCount = threads,
            DiskReadBytes = reads,
            DiskWriteBytes = writes
        };
    }

    /// <summary>Starts a new owned-process sampling session, including when Windows reuses a PID.</summary>
    public void BeginProcessAttempt(int rootProcessId)
    {
        lock (gate)
            previous.Remove(rootProcessId);
    }

    public HostSnapshot SampleHost(AppDataPaths paths)
    {
        var drive = new DriveInfo(Path.GetPathRoot(paths.Root)!);
        var cpu = SampleHostCpu();
        var network = SampleNetwork();
        if (DateTimeOffset.UtcNow - lastStorageSample > TimeSpan.FromMinutes(5))
        {
            lastStorageSample = DateTimeOffset.UtcNow;
            managedStorageBytes = SumDirectory(paths.ManagedServers);
            backupStorageBytes = SumDirectory(paths.Backups);
        }
        if (OperatingSystem.IsWindows() && GetPerformanceInfo(out var info, Marshal.SizeOf<PerformanceInformation>()))
        {
            var total = checked((long)(info.PhysicalTotal.ToUInt64() * info.PageSize.ToUInt64()));
            var available = checked((long)(info.PhysicalAvailable.ToUInt64() * info.PageSize.ToUInt64()));
            return new HostSnapshot
            {
                CpuPercent = cpu,
                TotalMemoryBytes = total,
                UsedMemoryBytes = Math.Max(0, total - available),
                AvailableMemoryBytes = available,
                FreeDiskBytes = drive.AvailableFreeSpace,
                TotalDiskBytes = drive.TotalSize,
                CpuModel = staticInfo.CpuModel,
                PhysicalCoreCount = staticInfo.PhysicalCores,
                LogicalProcessorCount = Environment.ProcessorCount,
                WindowsVersion = RuntimeInformation.OSDescription,
                HostUptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
                LanAddress = network.Address,
                ActiveNetworkAdapter = network.Adapter,
                NetworkReceiveBytesPerSecond = network.ReceivePerSecond,
                NetworkSendBytesPerSecond = network.SendPerSecond,
                ManagedServerStorageBytes = managedStorageBytes,
                BackupStorageBytes = backupStorageBytes
            };
        }
        return new HostSnapshot
        {
            CpuPercent = cpu,
            FreeDiskBytes = drive.AvailableFreeSpace,
            TotalDiskBytes = drive.TotalSize,
            CpuModel = staticInfo.CpuModel,
            PhysicalCoreCount = staticInfo.PhysicalCores,
            LogicalProcessorCount = Environment.ProcessorCount,
            WindowsVersion = RuntimeInformation.OSDescription,
            HostUptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            LanAddress = network.Address,
            ActiveNetworkAdapter = network.Adapter,
            NetworkReceiveBytesPerSecond = network.ReceivePerSecond,
            NetworkSendBytesPerSecond = network.SendPerSecond,
            ManagedServerStorageBytes = managedStorageBytes,
            BackupStorageBytes = backupStorageBytes
        };
    }

    private NetworkSample SampleNetwork()
    {
        var bestInterface = BestIpv4InterfaceIndex();
        var candidates = new List<LanInterfaceCandidate>();
        var interfaces = new Dictionary<string, NetworkInterface>(StringComparer.Ordinal);
        foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = item.GetIPProperties();
                var index = item.Supports(NetworkInterfaceComponent.IPv4)
                    ? properties.GetIPv4Properties()?.Index ?? -1
                    : -1;
                var candidate = new LanInterfaceCandidate(
                    item.Id,
                    item.Name,
                    item.Description,
                    item.NetworkInterfaceType,
                    item.OperationalStatus,
                    item.Speed,
                    properties.GatewayAddresses.Any(gateway => !IsUnspecified(gateway.Address)),
                    index >= 0 && index == bestInterface,
                    properties.UnicastAddresses.Select(address =>
                        new LanAddressCandidate(address.Address, address.PrefixLength)).ToArray(),
                    index);
                candidates.Add(candidate);
                interfaces[item.Id] = item;
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                // An adapter can disappear between enumeration and property access. It is not evidence.
            }
        }

        var selected = LanAddressSelector.Select(candidates);
        if (selected is null || !interfaces.TryGetValue(selected.Interface.Id, out var networkInterface))
            return new NetworkSample("", "", 0, 0);
        var statistics = networkInterface.GetIPv4Statistics();
        var now = DateTimeOffset.UtcNow;
        var sameAdapter = string.Equals(lastNetworkAdapterId, networkInterface.Id, StringComparison.Ordinal);
        var elapsed = sameAdapter ? (now - lastNetworkSample).TotalSeconds : 0;
        var received = elapsed > 0 ? Math.Max(0, (statistics.BytesReceived - lastNetworkReceived) / elapsed) : 0;
        var sent = elapsed > 0 ? Math.Max(0, (statistics.BytesSent - lastNetworkSent) / elapsed) : 0;
        lastNetworkSample = now;
        lastNetworkReceived = statistics.BytesReceived;
        lastNetworkSent = statistics.BytesSent;
        lastNetworkAdapterId = networkInterface.Id;
        return new NetworkSample(networkInterface.Name, selected.Address.Address.ToString(), (long)received, (long)sent);
    }

    private static int BestIpv4InterfaceIndex()
    {
        if (!OperatingSystem.IsWindows())
            return -1;
        var destination = BitConverter.ToUInt32(IPAddress.Parse("8.8.8.8").GetAddressBytes());
        return GetBestInterface(destination, out var index) == 0 ? checked((int)index) : -1;
    }

    private static bool IsUnspecified(IPAddress address) =>
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);

    private static StaticHostInfo ReadStaticHostInfo()
    {
        var model = "";
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            model = key?.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? "";
        }
        return new StaticHostInfo(model, CountPhysicalCores());
    }

    private static int CountPhysicalCores()
    {
        if (!OperatingSystem.IsWindows())
            return Environment.ProcessorCount;
        uint length = 0;
        _ = GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
        if (length == 0)
            return Environment.ProcessorCount;
        var buffer = Marshal.AllocHGlobal(checked((int)length));
        try
        {
            if (!GetLogicalProcessorInformationEx(0, buffer, ref length))
                return Environment.ProcessorCount;
            var count = 0;
            var offset = 0;
            while (offset < length)
            {
                var relationship = Marshal.ReadInt32(buffer, offset);
                var size = Marshal.ReadInt32(buffer, offset + sizeof(int));
                if (relationship == 0)
                    count++;
                if (size <= 0)
                    break;
                offset += size;
            }
            return Math.Max(1, count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static long SumDirectory(string path)
    {
        if (!Directory.Exists(path))
            return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (!File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                        total += new FileInfo(file).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return total;
    }

    private double SampleHostCpu()
    {
        if (!OperatingSystem.IsWindows() ||
            !GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
            return 0;
        var idle = ToUInt64(idleTime);
        var kernel = ToUInt64(kernelTime);
        var user = ToUInt64(userTime);
        lock (gate)
        {
            var idleDelta = idle - lastIdle;
            var totalDelta = kernel - lastKernel + user - lastUser;
            lastIdle = idle;
            lastKernel = kernel;
            lastUser = user;
            return totalDelta == 0 ? 0 : Math.Clamp((1d - (double)idleDelta / totalDelta) * 100, 0, 100);
        }
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public int Size;
        public UIntPtr CommitTotal;
        public UIntPtr CommitLimit;
        public UIntPtr CommitPeak;
        public UIntPtr PhysicalTotal;
        public UIntPtr PhysicalAvailable;
        public UIntPtr SystemCache;
        public UIntPtr KernelTotal;
        public UIntPtr KernelPaged;
        public UIntPtr KernelNonpaged;
        public UIntPtr PageSize;
        public int HandleCount;
        public int ProcessCount;
        public int ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation performanceInformation, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destinationAddress, out uint bestInterfaceIndex);

    private sealed record StaticHostInfo(string CpuModel, int PhysicalCores);
    private sealed record NetworkSample(string Adapter, string Address, long ReceivePerSecond, long SendPerSecond);
}

/// <summary>A testable description of one network interface; collecting it has no network side effect.</summary>
public sealed record LanInterfaceCandidate(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    long Speed,
    bool HasDefaultGateway,
    bool IsEffectiveDefaultRoute,
    IReadOnlyList<LanAddressCandidate> Addresses,
    int InterfaceIndex = 0);

public sealed record LanAddressCandidate(IPAddress Address, int PrefixLength);

public sealed record LanAddressSelection(LanInterfaceCandidate Interface, LanAddressCandidate Address);

public sealed record LanAddressEvaluation(
    NetworkPathStatus Status,
    LanAddressSelection? Selection,
    int TrustworthyCandidateCount);

/// <summary>Selects an ordinary LAN address conservatively, never a VPN or virtual-switch address.</summary>
public static class LanAddressSelector
{
    private static readonly string[] VirtualMarkers =
    [
        "vpn", "wintun", "wireguard", "tailscale", "zerotier", "openvpn", "tap-windows", "tunnel",
        "hyper-v", "virtual", "vmware", "virtualbox", "docker", "loopback"
    ];

    public static LanAddressSelection? Select(IEnumerable<LanInterfaceCandidate> candidates)
        => Evaluate(candidates).Selection;

    /// <summary>Returns typed absence/ambiguity evidence while preserving the established ranking.</summary>
    public static LanAddressEvaluation Evaluate(IEnumerable<LanInterfaceCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var ranked = candidates
            .Where(IsPhysicalLan)
            .Select(candidate => new
            {
                Candidate = candidate,
                Address = candidate.Addresses
                    .Where(IsLanAddress)
                    .OrderBy(AddressRank)
                    .ThenByDescending(address => address.PrefixLength)
                    .FirstOrDefault()
            })
            .Where(item => item.Address is not null)
            .Select(item => new
            {
                item.Candidate,
                Address = item.Address!,
                RouteRank = item.Candidate.IsEffectiveDefaultRoute ? 0 : item.Candidate.HasDefaultGateway ? 1 : 2,
                TypeRank = item.Candidate.Type == NetworkInterfaceType.Ethernet ? 0 : 1
            })
            .OrderBy(item => item.RouteRank)
            .ThenBy(item => item.TypeRank)
            .ThenByDescending(item => item.Candidate.Speed)
            .ToArray();
        if (ranked.Length == 0)
            return new LanAddressEvaluation(NetworkPathStatus.Unavailable, null, 0);

        var first = ranked[0];
        // Without route evidence, multiple physical adapters are ambiguous. Returning no address is
        // safer than confidently labelling a lab/VLAN adapter as the household LAN.
        if (first.RouteRank == 2 && ranked.Length > 1)
            return new LanAddressEvaluation(NetworkPathStatus.Ambiguous, null, ranked.Length);
        return new LanAddressEvaluation(NetworkPathStatus.Available,
            new LanAddressSelection(first.Candidate, first.Address), ranked.Length);
    }

    /// <summary>
    /// Whether an interface may be treated as the household LAN. Shared with router-mapping gateway
    /// selection so both paths reject VPN and virtual adapters by the same rule.
    /// </summary>
    public static bool IsTrustworthyLanInterface(LanInterfaceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IsPhysicalLan(candidate);
    }

    /// <summary>Whether an address is an ordinary private LAN address (RFC 1918 IPv4 or IPv6 ULA).</summary>
    public static bool IsPrivateLanAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return IsLanAddress(new LanAddressCandidate(address, 0));
    }

    private static bool IsPhysicalLan(LanInterfaceCandidate candidate)
    {
        if (candidate.Status != OperationalStatus.Up ||
            candidate.Type is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or
                NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT or
                NetworkInterfaceType.Wireless80211))
            return false;
        var identity = $"{candidate.Name} {candidate.Description}";
        return !VirtualMarkers.Any(marker => identity.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLanAddress(LanAddressCandidate candidate)
    {
        var address = candidate.Address;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
               (address.GetAddressBytes()[0] & 0xfe) == 0xfc;
    }

    private static int AddressRank(LanAddressCandidate candidate) =>
        candidate.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;
}

public static class ProcessTree
{
    private const uint SnapshotProcesses = 0x00000002;

    public static IReadOnlyList<int> GetDescendantsAndSelf(int rootProcessId)
    {
        if (!OperatingSystem.IsWindows())
            return [rootProcessId];
        var parents = EnumerateParents();
        var result = new List<int> { rootProcessId };
        for (var index = 0; index < result.Count; index++)
        {
            var parent = result[index];
            result.AddRange(parents.Where(pair => pair.Value == parent && !result.Contains(pair.Key)).Select(pair => pair.Key));
        }
        return result;
    }

    public static void Kill(int rootProcessId)
    {
        foreach (var id in GetDescendantsAndSelf(rootProcessId).Reverse())
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: false);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { }
        }
    }

    private static Dictionary<int, int> EnumerateParents()
    {
        var result = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == new IntPtr(-1))
            return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return result;
            do
            {
                result[checked((int)entry.ProcessId)] = checked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
