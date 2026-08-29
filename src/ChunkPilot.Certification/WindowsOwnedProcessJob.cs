using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ChunkPilot.Core;

namespace ChunkPilot.Certification;

internal sealed record WindowsOwnedProcessJobSnapshot(
    IReadOnlyDictionary<int, long> ProcessIdentities,
    uint TotalProcesses,
    uint ActiveProcesses,
    uint TotalTerminatedProcesses);

/// <summary>
/// Owns the isolated Agent and every non-breakaway descendant. Closing the handle is an OS-enforced
/// controller-death guardian; a clean certification additionally proves the job became empty before
/// the handle is closed.
/// </summary>
internal sealed class WindowsOwnedProcessJob : IDisposable
{
    private const uint JobObjectLimitPriorityClass = 0x00000020;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private readonly SafeFileHandle handle;

    private WindowsOwnedProcessJob(
        SafeFileHandle handle,
        bool belowNormalPriorityApplied,
        string priorityWarning)
    {
        this.handle = handle;
        BelowNormalPriorityApplied = belowNormalPriorityApplied;
        PriorityWarning = priorityWarning;
    }

    public bool BelowNormalPriorityApplied { get; }
    public string PriorityWarning { get; }

    public static WindowsOwnedProcessJob Create()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Exact certification process-tree ownership requires Windows Job objects.");
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows did not create the certification process Job.");
        try
        {
            SetLimits(handle, JobObjectLimitKillOnJobClose, priorityClass: 0);
            var priorityApplied = false;
            var warning = "";
            try
            {
                SetLimits(
                    handle,
                    JobObjectLimitKillOnJobClose | JobObjectLimitPriorityClass,
                    BelowNormalPriorityClass);
                priorityApplied = true;
            }
            catch (Win32Exception exception)
            {
                warning = "Windows could not apply BelowNormal priority to the isolated process Job; " +
                          "kill-on-close ownership remains active. " +
                          SecretRedactor.Redact(exception.Message);
            }
            return new WindowsOwnedProcessJob(handle, priorityApplied, warning);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public Process StartSuspendedAssignedAndResume(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute || !startInfo.CreateNoWindow ||
            !string.IsNullOrEmpty(startInfo.Arguments))
            throw new ArgumentException(
                "The exact-owned native launch requires a no-shell, no-window start description with structured arguments.");
        var executable = Path.GetFullPath(startInfo.FileName);
        var environment = BuildEnvironmentBlock(startInfo.Environment);
        var environmentBlock = Marshal.StringToHGlobalUni(environment);
        IntPtr attributeList = IntPtr.Zero;
        IntPtr jobHandleList = IntPtr.Zero;
        ProcessInformation processInformation = default;
        Process? process = null;
        try
        {
            nuint attributeBytes = 0;
            _ = InitializeProcThreadAttributeList(
                IntPtr.Zero, 1, 0, ref attributeBytes);
            if (attributeBytes == 0 || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not size the atomic process Job attribute list.");
            attributeList = Marshal.AllocHGlobal(checked((int)attributeBytes));
            if (!InitializeProcThreadAttributeList(
                    attributeList, 1, 0, ref attributeBytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not initialize the atomic process Job attribute list.");
            jobHandleList = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobHandleList, handle.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(
                    attributeList, 0, ProcThreadAttributeJobList,
                    jobHandleList, checked((nuint)IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows refused atomic process Job assignment.");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = checked((uint)Marshal.SizeOf<StartupInfoEx>())
                },
                AttributeList = attributeList
            };
            var commandText = CommandLineQuoter.QuoteWindowsArgument(executable);
            if (startInfo.ArgumentList.Count > 0)
                commandText += " " + string.Join(
                    " ", startInfo.ArgumentList.Select(CommandLineQuoter.QuoteWindowsArgument));
            var commandLine = (commandText + '\0').ToCharArray();
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    CreateSuspended | CreateUnicodeEnvironment |
                    ExtendedStartupInfoPresent | CreateNoWindow,
                    environmentBlock,
                    Path.GetFullPath(startInfo.WorkingDirectory),
                    ref startup,
                    out processInformation))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not create the exact-owned packaged Agent in a suspended Job.");
            process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            if (!GetActiveProcessIds().Contains(process.Id))
                throw new InvalidOperationException(
                    "The suspended packaged Agent was not atomically assigned to its exact-owned process Job.");
            if (ResumeThread(processInformation.ThreadHandle) == uint.MaxValue)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows did not resume the exact-owned packaged Agent.");
            return process;
        }
        catch
        {
            if (processInformation.ProcessHandle != IntPtr.Zero)
            {
                try
                {
                    Terminate();
                }
                catch (Win32Exception)
                {
                    // The launch failure remains authoritative. Closing this Job's handle still
                    // enforces kill-on-close for any process atomically assigned above.
                }
            }
            process?.Dispose();
            throw;
        }
        finally
        {
            if (processInformation.ThreadHandle != IntPtr.Zero)
                _ = CloseHandle(processInformation.ThreadHandle);
            if (processInformation.ProcessHandle != IntPtr.Zero)
                _ = CloseHandle(processInformation.ProcessHandle);
            if (attributeList != IntPtr.Zero)
                DeleteProcThreadAttributeList(attributeList);
            if (jobHandleList != IntPtr.Zero)
                Marshal.FreeHGlobal(jobHandleList);
            if (attributeList != IntPtr.Zero)
                Marshal.FreeHGlobal(attributeList);
            Marshal.FreeHGlobal(environmentBlock);
        }
    }

    public IReadOnlyDictionary<int, long> CaptureActiveProcessIdentities()
    {
        var result = new Dictionary<int, long>();
        foreach (var processId in GetActiveProcessIds())
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                var identity = ProcessCreationIdentity.Of(process.SafeHandle);
                if (identity == ProcessCreationIdentity.Unknown)
                    throw new InvalidDataException(
                        "An exact-owned process creation identity could not be captured.");
                result.Add(processId, identity);
            }
            catch (ArgumentException)
            {
                // A process may exit between the atomic Job snapshot and handle acquisition. It is
                // no longer capable of owning the live listener being verified.
            }
        }
        return result;
    }

    public WindowsOwnedProcessJobSnapshot CaptureStableProcessSnapshot()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var accountingBefore = ReadAccounting();
            var identities = CaptureActiveProcessIdentities();
            var accountingAfter = ReadAccounting();
            if (accountingBefore.TotalProcesses == accountingAfter.TotalProcesses &&
                accountingBefore.ActiveProcesses == accountingAfter.ActiveProcesses &&
                accountingBefore.TotalTerminatedProcesses ==
                accountingAfter.TotalTerminatedProcesses &&
                accountingAfter.ActiveProcesses == checked((uint)identities.Count))
                return new WindowsOwnedProcessJobSnapshot(
                    new Dictionary<int, long>(identities),
                    accountingAfter.TotalProcesses,
                    accountingAfter.ActiveProcesses,
                    accountingAfter.TotalTerminatedProcesses);
        }
        throw new InvalidDataException(
            "The exact-owned process Job did not produce a stable bounded accounting snapshot.");
    }

    public bool IdentityStillMatches(int processId, long expectedCreationTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return ProcessCreationIdentity.Matches(
                ProcessCreationIdentity.Of(process.SafeHandle), expectedCreationTicks);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public int ActiveProcessCount => checked((int)ReadAccounting().ActiveProcesses);

    public async Task<bool> WaitForEmptyAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ActiveProcessCount == 0)
                return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        }
        return ActiveProcessCount == 0;
    }

    public void Terminate()
    {
        if (!TerminateJobObject(handle, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Windows could not terminate the exact-owned certification process Job.");
    }

    public void Dispose() => handle.Dispose();

    private static string BuildEnvironmentBlock(
        IDictionary<string, string?> environment)
    {
        var result = new StringBuilder();
        foreach (var pair in environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Key.Contains('=') || pair.Key.Contains('\0') || pair.Value?.Contains('\0') == true)
                throw new InvalidOperationException(
                    "The isolated Agent environment contains an unsafe variable.");
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
                        "Windows could not inventory the exact-owned certification process Job.");
                }
                var count = Marshal.ReadInt32(buffer, 4);
                if (count < 0 || count > capacity)
                    throw new InvalidDataException(
                        "Windows returned a contradictory process Job inventory.");
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
            "The exact-owned certification process Job exceeded its bounded process inventory.");
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
                    "Windows could not read exact-owned process Job accounting.");
            return Marshal.PtrToStructure<JobObjectBasicAccountingInformation>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetLimits(SafeFileHandle handle, uint flags, uint priorityClass)
    {
        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = flags,
                PriorityClass = priorityClass
            }
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(
                    handle, JobObjectInformationClass.ExtendedLimitInformation,
                    buffer, checked((uint)size)))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Windows could not configure exact certification process-tree ownership.");
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
