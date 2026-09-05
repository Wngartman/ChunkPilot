using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ChunkPilot.Core;

namespace ChunkPilot.Certification;

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
