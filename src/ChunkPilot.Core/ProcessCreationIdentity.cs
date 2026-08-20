using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace ChunkPilot.Core;

/// <summary>The exact raw Windows creation identity of a process.</summary>
/// <remarks>
/// A PID is reusable. The PID plus the raw <c>FILETIME</c> returned by <c>GetProcessTimes</c>
/// identifies one process instance without local-time conversion or a tolerance window.
/// </remarks>
public static class ProcessCreationIdentity
{
    public const long Unknown = 0;

    public static long OfCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
            return Unknown;
        return ReadCreationTicks(NativeMethods.GetCurrentProcess());
    }

    public static long Of(SafeProcessHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows() || handle.IsInvalid || handle.IsClosed)
            return Unknown;

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            return added ? ReadCreationTicks(handle.DangerousGetHandle()) : Unknown;
        }
        catch (ObjectDisposedException)
        {
            return Unknown;
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    public static bool Matches(long observed, long recorded) =>
        observed != Unknown && recorded != Unknown && observed == recorded;

    [SupportedOSPlatform("windows")]
    private static long ReadCreationTicks(nint handle)
    {
        try
        {
            return NativeMethods.GetProcessTimes(handle, out var creation, out _, out _, out _)
                ? creation
                : Unknown;
        }
        catch (DllNotFoundException)
        {
            return Unknown;
        }
        catch (EntryPointNotFoundException)
        {
            return Unknown;
        }
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [SupportedOSPlatform("windows")]
        public static extern bool GetProcessTimes(
            nint process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll")]
        [SupportedOSPlatform("windows")]
        public static extern nint GetCurrentProcess();
    }
}
