using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ChunkPilot.Infrastructure;

public readonly record struct StorageVolumeSpace(string VolumeIdentity, long AvailableBytes);

public interface IStorageSpaceProbe
{
    StorageVolumeSpace GetSpace(string path);
}

internal sealed class SystemStorageSpaceProbe : IStorageSpaceProbe
{
    public static SystemStorageSpaceProbe Instance { get; } = new(new WindowsStorageVolumeApi());
    private readonly IWindowsStorageVolumeApi windows;

    internal SystemStorageSpaceProbe(IWindowsStorageVolumeApi windows)
    {
        this.windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public StorageVolumeSpace GetSpace(string path)
    {
        var full = Path.GetFullPath(path);
        var existing = NearestExistingPath(full);
        var volumePath = windows.GetVolumePathName(existing);
        var volumeIdentity = windows.GetVolumeIdentity(volumePath);
        var available = windows.GetAvailableBytes(volumePath);
        if (string.IsNullOrWhiteSpace(volumeIdentity) || available < 0)
            throw new IOException("The actual Windows storage volume could not be measured safely.");
        return new StorageVolumeSpace(volumeIdentity, available);
    }

    internal static string NearestExistingPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (Directory.Exists(current) || File.Exists(current))
                return current;
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrWhiteSpace(parent) ||
                parent.Equals(current, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "No existing path could be resolved for the target storage volume.");
            current = parent;
        }
    }
}

internal interface IWindowsStorageVolumeApi
{
    string GetVolumePathName(string path);
    string GetVolumeIdentity(string volumePath);
    long GetAvailableBytes(string volumePath);
}

internal sealed class WindowsStorageVolumeApi : IWindowsStorageVolumeApi
{
    private const int MaximumPathCharacters = 32_768;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileNameNormalized = 0;

    public string GetVolumePathName(string path)
    {
        EnsureWindows();
        var resolvedPath = ResolveFinalPath(path);
        if (resolvedPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ||
            (resolvedPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase) &&
             !resolvedPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)))
            throw new IOException(
                "Network-backed storage is unsupported for transactional server updates.");
        var result = new char[MaximumPathCharacters];
        if (!GetVolumePathNameW(resolvedPath, result, result.Length))
            throw Failure("Windows could not resolve the target volume mount point.");
        var value = EnsureTrailingSeparator(BufferText(result));
        if (string.IsNullOrWhiteSpace(value))
            throw new IOException("Windows returned an empty target volume mount point.");
        return value;
    }

    private static string ResolveFinalPath(string path)
    {
        using var handle = CreateFileW(
            Path.GetFullPath(path),
            desiredAccess: 0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
            throw Failure("Windows could not open the target path for volume resolution.");
        var result = new char[MaximumPathCharacters];
        var length = GetFinalPathNameByHandleW(
            handle, result, (uint)result.Length, FileNameNormalized);
        if (length == 0)
            throw Failure("Windows could not resolve the target path through mount points.");
        if (length >= (uint)result.Length)
            throw new IOException("The resolved target path exceeded the supported Windows path length.");
        return new string(result, 0, checked((int)length));
    }

    public string GetVolumeIdentity(string volumePath)
    {
        EnsureWindows();
        var mountPoint = EnsureTrailingSeparator(Path.GetFullPath(volumePath));
        var result = new char[128];
        if (!GetVolumeNameForVolumeMountPointW(mountPoint, result, result.Length))
            throw Failure("Windows could not resolve the target volume identity.");
        var value = BufferText(result);
        if (!value.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith("}\\", StringComparison.Ordinal))
            throw new IOException("Windows returned an unsupported target volume identity.");
        return value.ToLowerInvariant();
    }

    public long GetAvailableBytes(string volumePath)
    {
        EnsureWindows();
        var mountPoint = EnsureTrailingSeparator(Path.GetFullPath(volumePath));
        if (!GetDiskFreeSpaceExW(
                mountPoint, out var availableToCaller, out _, out _))
            throw Failure("Windows could not measure free space on the target volume.");
        if (availableToCaller > long.MaxValue)
            throw new IOException("The target volume free-space value exceeded the supported range.");
        return checked((long)availableToCaller);
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static string BufferText(char[] buffer)
    {
        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Authoritative storage-volume measurement requires Windows.");
    }

    private static IOException Failure(string message) =>
        new($"{message} Windows error {Marshal.GetLastPInvokeError()}.");

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string fileName,
        [Out] char[] volumePathName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string volumeMountPoint,
        [Out] char[] volumeName,
        int bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(
        string directoryName,
        out ulong freeBytesAvailableToCaller,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);
}

internal readonly record struct StorageSpaceRequirement(
    string Path,
    string Purpose,
    long RequiredBytes);

internal static class StorageSpaceGuard
{
    public const long SafetyReserveBytes = 512L * 1024 * 1024;
    public const long DownloadSafetyReserveBytes = 64L * 1024 * 1024;

    public static void EnsureAvailable(
        IStorageSpaceProbe probe,
        params IReadOnlyList<StorageSpaceRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var byVolume = new Dictionary<string, Aggregate>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            if (string.IsNullOrWhiteSpace(requirement.Path) ||
                string.IsNullOrWhiteSpace(requirement.Purpose))
                throw new ArgumentException("Storage requirements need a path and purpose.");
            ArgumentOutOfRangeException.ThrowIfNegative(requirement.RequiredBytes);
            if (requirement.RequiredBytes == 0)
                continue;
            var space = probe.GetSpace(requirement.Path);
            if (string.IsNullOrWhiteSpace(space.VolumeIdentity) || space.AvailableBytes < 0)
                throw new IOException("The available storage volume could not be measured safely.");
            if (!byVolume.TryGetValue(space.VolumeIdentity, out var aggregate))
            {
                aggregate = new Aggregate(space.AvailableBytes);
                byVolume.Add(space.VolumeIdentity, aggregate);
            }
            aggregate.AvailableBytes = Math.Min(aggregate.AvailableBytes, space.AvailableBytes);
            aggregate.RequiredBytes = SaturatingAdd(
                aggregate.RequiredBytes, requirement.RequiredBytes);
            aggregate.Purposes.Add(requirement.Purpose);
        }

        foreach (var (volume, aggregate) in byVolume)
        {
            if (aggregate.AvailableBytes >= aggregate.RequiredBytes)
                continue;
            var purposes = string.Join(", ", aggregate.Purposes.Order(StringComparer.Ordinal));
            throw new IOException(
                $"{purposes} need about {GiB(aggregate.RequiredBytes):F1} GB free on {volume}; " +
                $"{GiB(aggregate.AvailableBytes):F1} GB is available.");
        }
    }

    internal static long SaturatingAdd(long left, long right)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(left);
        ArgumentOutOfRangeException.ThrowIfNegative(right);
        return right > long.MaxValue - left ? long.MaxValue : left + right;
    }

    internal static long SaturatingMultiply(long value, long multiplier)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        ArgumentOutOfRangeException.ThrowIfNegative(multiplier);
        if (value == 0 || multiplier == 0)
            return 0;
        return value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
    }

    private static double GiB(long bytes) => bytes / 1024d / 1024d / 1024d;

    private sealed class Aggregate(long availableBytes)
    {
        public long AvailableBytes { get; set; } = availableBytes;
        public long RequiredBytes { get; set; }
        public HashSet<string> Purposes { get; } = new(StringComparer.Ordinal);
    }
}
