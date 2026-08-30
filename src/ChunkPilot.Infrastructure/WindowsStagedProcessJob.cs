using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ChunkPilot.Core;
using Microsoft.Win32.SafeHandles;

namespace ChunkPilot.Infrastructure;

internal enum WindowsStagedEndpointTransport
{
    Tcp,
    Udp
}

internal enum WindowsStagedTcpState : uint
{
    Closed = 1,
    Listen = 2,
    SynSent = 3,
    SynReceived = 4,
    Established = 5,
    FinWait1 = 6,
    FinWait2 = 7,
    CloseWait = 8,
    Closing = 9,
    LastAck = 10,
    TimeWait = 11,
    DeleteTcb = 12
}

internal sealed record WindowsStagedNetworkEndpoint(
    WindowsStagedEndpointTransport Transport,
    AddressFamily AddressFamily,
    IPAddress LocalAddress,
    int Port,
    int ProcessId,
    WindowsStagedTcpState? TcpState)
{
    public bool IsLoopback => IPAddress.IsLoopback(LocalAddress) &&
                              !LocalAddress.Equals(IPAddress.Any) &&
                              !LocalAddress.Equals(IPAddress.IPv6Any);
    public bool IsTcpListener => Transport == WindowsStagedEndpointTransport.Tcp &&
                                 TcpState == WindowsStagedTcpState.Listen;
}

/// <summary>
/// Starts one staged provider process atomically inside a private kill-on-close Windows Job. Every
/// non-breakaway descendant remains in that Job even when the root exits, so validation cleanup can
/// terminate and prove the complete provider-owned tree instead of relying on a reusable root PID.
/// </summary>
internal sealed class WindowsStagedProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private readonly SafeFileHandle handle;
    private bool disposed;

    private WindowsStagedProcessJob(SafeFileHandle handle) => this.handle = handle;

    public static WindowsStagedProcessJob Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Exact staged process-tree ownership requires Windows Job objects.");
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows did not create the staged validation process Job.");
        try
        {
            SetKillOnClose(handle);
            return new WindowsStagedProcessJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public WindowsStagedProcess Start(ProcessStartInfo startInfo)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute || !startInfo.CreateNoWindow ||
            !startInfo.RedirectStandardInput || !startInfo.RedirectStandardOutput ||
            !startInfo.RedirectStandardError || !string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException(
                "Staged process ownership requires a no-shell, no-window launch with redirected streams and structured arguments.");

        var executable = Path.GetFullPath(startInfo.FileName);
        if (!File.Exists(executable))
            throw new FileNotFoundException("The staged process executable was not found.", executable);
        var workingDirectory = Path.GetFullPath(startInfo.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(workingDirectory);

        SafeFileHandle? parentInput = null;
        SafeFileHandle? childInput = null;
        SafeFileHandle? parentOutput = null;
        SafeFileHandle? childOutput = null;
        SafeFileHandle? parentError = null;
        SafeFileHandle? childError = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr jobHandleList = IntPtr.Zero;
        IntPtr inheritedHandleList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        ProcessInformation processInformation = default;
        Process? process = null;
        WindowsStagedProcess? owned = null;
        try
        {
            (parentInput, childInput) = CreateRedirectedPipe(parentReads: false);
            (parentOutput, childOutput) = CreateRedirectedPipe(parentReads: true);
            (parentError, childError) = CreateRedirectedPipe(parentReads: true);

            nuint attributeBytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref attributeBytes);
            if (attributeBytes == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not size the staged process attribute list.");
            attributeList = Marshal.AllocHGlobal(checked((int)attributeBytes));
            if (!InitializeProcThreadAttributeList(attributeList, 2, 0, ref attributeBytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not initialize the staged process attribute list.");

            jobHandleList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobHandleList, handle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeJobList,
                    jobHandleList, checked((nuint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows refused atomic staged process Job assignment.");

            inheritedHandleList = Marshal.AllocHGlobal(checked(3 * IntPtr.Size));
            Marshal.WriteIntPtr(inheritedHandleList, 0 * IntPtr.Size, childInput.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandleList, 1 * IntPtr.Size, childOutput.DangerousGetHandle());
            Marshal.WriteIntPtr(inheritedHandleList, 2 * IntPtr.Size, childError.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeHandleList,
                    inheritedHandleList, checked((nuint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows refused the bounded staged-process handle inheritance list.");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInfoEx>()),
                    Flags = StartfUseStdHandles,
                    StandardInput = childInput.DangerousGetHandle(),
                    StandardOutput = childOutput.DangerousGetHandle(),
                    StandardError = childError.DangerousGetHandle()
                },
                AttributeList = attributeList
            };
            var commandText = CommandLineQuoter.QuoteWindowsArgument(executable);
            if (startInfo.ArgumentList.Count > 0)
                commandText += " " + string.Join(
                    " ", startInfo.ArgumentList.Select(CommandLineQuoter.QuoteWindowsArgument));
            var commandLine = (commandText + '\0').ToCharArray();
            environmentBlock = Marshal.StringToHGlobalUni(BuildEnvironmentBlock(startInfo.Environment));
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    CreateSuspended | CreateUnicodeEnvironment |
                    ExtendedStartupInfoPresent | CreateNoWindow,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not create the staged process inside its exact-owned Job.");
            if (!IsProcessInJob(processInformation.ProcessHandle, handle, out var inJob) || !inJob)
                throw new InvalidOperationException(
                    "The suspended staged process was not atomically assigned to its exact-owned Job.");

            process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            var inputStream = new FileStream(parentInput, FileAccess.Write, 4_096, isAsync: false);
            parentInput = null;
            var outputStream = new FileStream(parentOutput, FileAccess.Read, 4_096, isAsync: false);
            parentOutput = null;
            var errorStream = new FileStream(parentError, FileAccess.Read, 4_096, isAsync: false);
            parentError = null;
            owned = new WindowsStagedProcess(
                process,
                new StreamWriter(inputStream, new UTF8Encoding(false), 4_096, leaveOpen: false),
                new StreamReader(outputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    4_096, leaveOpen: false),
                new StreamReader(errorStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
                    4_096, leaveOpen: false));
            process = null;
            if (ResumeThread(processInformation.ThreadHandle) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not resume the exact-owned staged process.");
            var result = owned;
            owned = null;
            return result;
        }
        catch
        {
            try
            {
                if (processInformation.ProcessHandle != IntPtr.Zero)
                    Terminate();
            }
            catch (Win32Exception)
            {
                // Closing the Job below remains the fail-safe for any atomically assigned process.
            }
            owned?.Dispose();
            process?.Dispose();
            throw;
        }
        finally
        {
            childInput?.Dispose();
            childOutput?.Dispose();
            childError?.Dispose();
            parentInput?.Dispose();
            parentOutput?.Dispose();
            parentError?.Dispose();
            if (processInformation.ThreadHandle != IntPtr.Zero)
                _ = CloseHandle(processInformation.ThreadHandle);
            if (processInformation.ProcessHandle != IntPtr.Zero)
                _ = CloseHandle(processInformation.ProcessHandle);
            if (attributeList != IntPtr.Zero)
                DeleteProcThreadAttributeList(attributeList);
            if (inheritedHandleList != IntPtr.Zero)
                Marshal.FreeHGlobal(inheritedHandleList);
            if (jobHandleList != IntPtr.Zero)
                Marshal.FreeHGlobal(jobHandleList);
            if (attributeList != IntPtr.Zero)
                Marshal.FreeHGlobal(attributeList);
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
        }
    }

    public int ActiveProcessCount => checked((int)ReadAccounting().ActiveProcesses);

    public async Task<bool> WaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ActiveProcessCount == 0)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
        return ActiveProcessCount == 0;
    }

    public async Task TerminateRemainingAndProveEmptyAsync(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int active;
        try
        {
            active = ActiveProcessCount;
        }
        catch
        {
            try { Terminate(); }
            catch (Win32Exception) { }
            throw;
        }
        if (active > 0)
            Terminate();
        if (!await WaitForEmptyAsync(timeout, CancellationToken.None).ConfigureAwait(false))
            throw new InvalidOperationException(
                "Windows did not prove the staged validation process Job became empty.");
    }

    public IReadOnlyList<WindowsStagedNetworkEndpoint> CaptureStableOwnedNetworkEndpoints()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = GetActiveProcessIds().Order().ToArray();
            if (before.Length == 0)
                return [];
            var first = WindowsStagedNetworkEndpoints.Capture();
            var between = GetActiveProcessIds().Order().ToArray();
            var second = WindowsStagedNetworkEndpoints.Capture();
            var after = GetActiveProcessIds().Order().ToArray();
            if (!before.SequenceEqual(between) || !between.SequenceEqual(after))
                continue;
            var owned = before.ToHashSet();
            return first.Concat(second)
                .Where(listener => owned.Contains(listener.ProcessId))
                .Distinct()
                .ToArray();
        }
        throw new InvalidDataException(
            "The staged validation Job did not produce a stable process/endpoint ownership snapshot.");
    }

    public bool HasStableOwnedVisibleTopLevelWindow()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var before = GetActiveProcessIds().Order().ToArray();
            if (before.Length == 0)
                return false;
            var first = WindowsStagedVisibleTopLevelWindows.CaptureProcessIds();
            var between = GetActiveProcessIds().Order().ToArray();
            var second = WindowsStagedVisibleTopLevelWindows.CaptureProcessIds();
            var after = GetActiveProcessIds().Order().ToArray();
            if (!before.SequenceEqual(between) || !between.SequenceEqual(after))
                continue;
            return WindowsStagedVisibleTopLevelWindows.HasOwnedWindow(
                before,
                first.Concat(second));
        }
        throw new InvalidDataException(
            "The staged validation Job did not produce a stable process/window ownership snapshot.");
    }

    public void Terminate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!TerminateJobObject(handle, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows could not terminate the exact-owned staged process Job.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        handle.Dispose();
    }

    private static (SafeFileHandle Parent, SafeFileHandle Child) CreateRedirectedPipe(bool parentReads)
    {
        var attributes = new SecurityAttributes
        {
            Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
            InheritHandle = true
        };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows did not create a staged-process redirect pipe.");
        var parent = parentReads ? read : write;
        var child = parentReads ? write : read;
        if (!SetHandleInformation(parent, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error,
                "Windows did not isolate the staged-process parent pipe handle.");
        }
        return (parent, child);
    }

    private static string BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var result = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value?.Contains('\0') == true)
                throw new InvalidOperationException(
                    "The staged process environment contains an unsafe variable.");
            result.Append(pair.Key).Append('=').Append(pair.Value ?? "").Append('\0');
        }
        result.Append('\0');
        return result.ToString();
    }

    private IReadOnlyList<int> GetActiveProcessIds()
    {
        for (var capacity = 32; capacity <= 4_096; capacity *= 2)
        {
            var bytes = checked(8 + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!QueryInformationJobObject(
                        handle, JobObjectInformationClass.BasicProcessIdList,
                        buffer, checked((uint)bytes), out _))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorMoreData)
                        continue;
                    throw new Win32Exception(error,
                        "Windows could not inventory the staged validation process Job.");
                }
                var count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > capacity)
                    throw new InvalidDataException(
                        "Windows returned a contradictory staged process Job inventory.");
                var result = new int[count];
                for (var index = 0; index < count; index++)
                {
                    var offset = checked(8 + index * IntPtr.Size);
                    var raw = IntPtr.Size == 8
                        ? Marshal.ReadInt64(buffer, offset)
                        : Marshal.ReadInt32(buffer, offset);
                    result[index] = checked((int)raw);
                }
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        throw new InvalidOperationException(
            "The staged validation Job exceeded its bounded process inventory.");
    }

    private JobObjectBasicAccountingInformation ReadAccounting()
    {
        var size = Marshal.SizeOf<JobObjectBasicAccountingInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    handle, JobObjectInformationClass.BasicAccountingInformation,
                    buffer, checked((uint)size), out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not read staged process Job accounting.");
            return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetKillOnClose(SafeFileHandle job)
    {
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    job, JobObjectInformationClass.ExtendedLimitInformation,
                    buffer, checked((uint)size)))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not configure staged process-tree kill-on-close ownership.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private enum JobObjectInformationClass
    {
        BasicAccountingInformation = 1,
        BasicProcessIdList = 3,
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformation
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr ProcessHandle;
        public IntPtr ThreadHandle;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        IntPtr information,
        uint informationLength,
        out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        [In, Out] char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

internal sealed class WindowsStagedProcess : IDisposable
{
    public WindowsStagedProcess(
        Process process,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        Process = process;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public Process Process { get; }
    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }

    public void Dispose()
    {
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }
}

internal static class WindowsStagedVisibleTopLevelWindows
{
    public static IReadOnlyList<int> CaptureProcessIds()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Exact staged window ownership requires Windows top-level window enumeration.");
        var processIds = new List<int>();
        var invalidIdentity = false;
        EnumWindowsCallback callback = (window, parameter) =>
        {
            _ = parameter;
            if (!IsWindowVisible(window))
                return true;
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
                return true;
            if (processId > int.MaxValue)
            {
                invalidIdentity = true;
                return true;
            }
            processIds.Add(checked((int)processId));
            return true;
        };
        if (!EnumWindows(callback, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows did not enumerate visible top-level windows for staged validation.");
        if (invalidIdentity)
            throw new InvalidDataException(
                "Windows returned an invalid staged top-level window process identity.");
        return processIds;
    }

    internal static bool HasOwnedWindow(
        IEnumerable<int> ownedProcessIds,
        IEnumerable<int> visibleWindowProcessIds)
    {
        var owned = ownedProcessIds.ToHashSet();
        return visibleWindowProcessIds.Any(owned.Contains);
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

internal static class WindowsStagedNetworkEndpoints
{
    private const int AddressFamilyInternet = 2;
    private const int AddressFamilyInternetV6 = 23;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;
    private const int MaximumRows = 65_536;
    private const int MaximumTableBytes = 16 * 1024 * 1024;

    public static IReadOnlyList<WindowsStagedNetworkEndpoint> Capture()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Exact staged endpoint ownership requires Windows IP Helper tables.");
        return ParseTcp(ReadTcp(AddressFamilyInternet), AddressFamily.InterNetwork)
            .Concat(ParseTcp(ReadTcp(AddressFamilyInternetV6), AddressFamily.InterNetworkV6))
            .Concat(ParseUdp(ReadUdp(AddressFamilyInternet), AddressFamily.InterNetwork))
            .Concat(ParseUdp(ReadUdp(AddressFamilyInternetV6), AddressFamily.InterNetworkV6))
            .ToArray();
    }

    internal static IReadOnlyList<WindowsStagedNetworkEndpoint> ParseTcp(
        ReadOnlySpan<byte> table,
        AddressFamily addressFamily) =>
        Parse(table, addressFamily, WindowsStagedEndpointTransport.Tcp);

    internal static IReadOnlyList<WindowsStagedNetworkEndpoint> ParseUdp(
        ReadOnlySpan<byte> table,
        AddressFamily addressFamily) =>
        Parse(table, addressFamily, WindowsStagedEndpointTransport.Udp);

    private static IReadOnlyList<WindowsStagedNetworkEndpoint> Parse(
        ReadOnlySpan<byte> table,
        AddressFamily addressFamily,
        WindowsStagedEndpointTransport transport)
    {
        if (addressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new ArgumentOutOfRangeException(nameof(addressFamily));
        if (table.Length < sizeof(uint))
            throw new InvalidDataException("Windows returned a truncated staged endpoint table.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        if (count > MaximumRows)
            throw new InvalidDataException("Windows returned an oversized staged endpoint table.");
        var rowSize = (transport, addressFamily) switch
        {
            (WindowsStagedEndpointTransport.Tcp, AddressFamily.InterNetwork) => 24,
            (WindowsStagedEndpointTransport.Tcp, AddressFamily.InterNetworkV6) => 56,
            (WindowsStagedEndpointTransport.Udp, AddressFamily.InterNetwork) => 12,
            (WindowsStagedEndpointTransport.Udp, AddressFamily.InterNetworkV6) => 28,
            _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
        };
        if (checked(sizeof(uint) + (long)count * rowSize) > table.Length)
            throw new InvalidDataException("Windows returned a truncated staged endpoint table.");

        var endpoints = new List<WindowsStagedNetworkEndpoint>(checked((int)count));
        for (var index = 0; index < count; index++)
        {
            var row = table.Slice(checked(sizeof(uint) + (int)index * rowSize), rowSize);
            var tcpState = transport == WindowsStagedEndpointTransport.Tcp
                ? (WindowsStagedTcpState?)BinaryPrimitives.ReadUInt32LittleEndian(
                    row.Slice(addressFamily == AddressFamily.InterNetwork ? 0 : 48, sizeof(uint)))
                : null;
            var addressOffset = transport == WindowsStagedEndpointTransport.Tcp &&
                                addressFamily == AddressFamily.InterNetwork
                ? 4
                : 0;
            var addressLength = addressFamily == AddressFamily.InterNetwork ? 4 : 16;
            var scopeId = addressFamily == AddressFamily.InterNetworkV6
                ? BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(16, sizeof(uint)))
                : 0;
            var address = addressFamily == AddressFamily.InterNetwork
                ? new IPAddress(row.Slice(addressOffset, addressLength))
                : new IPAddress(row.Slice(addressOffset, addressLength), scopeId);
            var portOffset = (transport, addressFamily) switch
            {
                (WindowsStagedEndpointTransport.Tcp, AddressFamily.InterNetwork) => 8,
                (WindowsStagedEndpointTransport.Tcp, AddressFamily.InterNetworkV6) => 20,
                (WindowsStagedEndpointTransport.Udp, AddressFamily.InterNetwork) => 4,
                (WindowsStagedEndpointTransport.Udp, AddressFamily.InterNetworkV6) => 20,
                _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
            };
            var processOffset = (transport, addressFamily) switch
            {
                (WindowsStagedEndpointTransport.Tcp, AddressFamily.InterNetwork) => 20,
                (WindowsStagedEndpointTransport.Tcp, AddressFamily.InterNetworkV6) => 52,
                (WindowsStagedEndpointTransport.Udp, AddressFamily.InterNetwork) => 8,
                (WindowsStagedEndpointTransport.Udp, AddressFamily.InterNetworkV6) => 24,
                _ => throw new ArgumentOutOfRangeException(nameof(addressFamily))
            };
            var processId = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(processOffset, sizeof(uint)));
            // OwnerPidAll legitimately returns ownerless TIME_WAIT rows with PID zero. They cannot
            // belong to this Job and therefore are not owned endpoint evidence.
            if (processId == 0)
                continue;
            if (processId > int.MaxValue)
                throw new InvalidDataException("Windows returned an invalid staged endpoint process identity.");
            endpoints.Add(new WindowsStagedNetworkEndpoint(
                transport,
                addressFamily,
                address,
                checked(row[portOffset] * 256 + row[portOffset + 1]),
                checked((int)processId),
                tcpState));
        }
        return endpoints;
    }

    private static byte[] ReadTcp(int addressFamily) => Read(
        "TCP endpoint",
        (IntPtr buffer, ref uint size) => GetExtendedTcpTable(
            buffer, ref size, order: false, addressFamily,
            TcpTableClass.OwnerPidAll, reserved: 0));

    private static byte[] ReadUdp(int addressFamily) => Read(
        "UDP endpoint",
        (IntPtr buffer, ref uint size) => GetExtendedUdpTable(
            buffer, ref size, order: false, addressFamily,
            UdpTableClass.OwnerPid, reserved: 0));

    private static byte[] Read(string kind, NativeTableReader reader)
    {
        uint size = 0;
        var first = reader(IntPtr.Zero, ref size);
        if (first != ErrorInsufficientBuffer || size < sizeof(uint) || size > MaximumTableBytes)
            throw new Win32Exception(checked((int)first),
                $"Windows did not size the bounded staged {kind} table.");
        var allocationSize = size;
        var buffer = Marshal.AllocHGlobal(checked((int)allocationSize));
        try
        {
            var result = reader(buffer, ref size);
            if (result != NoError)
                throw new Win32Exception(checked((int)result),
                    $"Windows did not return the staged {kind} table.");
            if (size < sizeof(uint) || size > allocationSize)
                throw new InvalidDataException($"Windows returned an invalid staged {kind} table size.");
            var table = new byte[checked((int)size)];
            Marshal.Copy(buffer, table, 0, table.Length);
            return table;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate uint NativeTableReader(IntPtr buffer, ref uint size);

    private enum TcpTableClass
    {
        OwnerPidAll = 5
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
