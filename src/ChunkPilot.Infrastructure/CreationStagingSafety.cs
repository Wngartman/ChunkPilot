using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Establishes and verifies the closed filesystem boundary used while a managed server is created.
/// </summary>
internal static class CreationStagingSafety
{
    private const int MaximumOwnedEntries = ServerImportInspectionService.MaximumEntries;

    public static async Task PrepareOwnedDirectoryAsync(
        string instanceRoot,
        string stagingPath,
        CreationOwnershipMarker marker,
        CancellationToken cancellationToken,
        Action<string>? afterDirectoryCreated = null)
    {
        var root = CreationPathSafety.Canonical(instanceRoot);
        var staging = CreationPathSafety.Canonical(stagingPath);
        var parent = Path.GetDirectoryName(staging)
                     ?? throw new InvalidDataException("The creation staging path has no parent directory.");
        if (!CreationPathSafety.IsSamePath(root, parent))
            throw new InvalidDataException("The creation staging directory must be an immediate child of the managed instance root.");
        if (!Path.GetFileName(staging).Equals(
                ServerCreationTransaction.StagingFolderName(marker.OperationId),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The creation staging directory does not match the operation identifier.");

        EnsureNoReparseTraversal(root);
        if (EntryExists(staging))
            throw new IOException("The creation staging path already exists; ChunkPilot will not merge into or remove it.");

        cancellationToken.ThrowIfCancellationRequested();
        CreateDirectoryExclusive(staging);
        var markerWritten = false;
        try
        {
            EnsureNoReparseTraversal(staging);
            if (Directory.EnumerateFileSystemEntries(staging).Any())
                throw new IOException("The new creation staging directory was not empty.");
            afterDirectoryCreated?.Invoke(staging);

            await CreationOwnershipMarker.WriteAsync(staging, marker, cancellationToken).ConfigureAwait(false);
            markerWritten = true;
            RequireExactOwnership(staging, marker.OperationId, marker.ServerId, marker.CanonicalDestination);
            RequireEmptyOrValidOwnershipMarker(staging);
        }
        catch (Exception preparationFailure)
        {
            var cleanupFailure = CleanupAfterFailedMarkerInitialization(
                staging, marker.OperationId, marker.ServerId, marker.CanonicalDestination, markerWritten);
            if (cleanupFailure is not null)
                throw new IOException(
                    "Creation staging initialization failed and its newly created directory could not be removed safely.",
                    new AggregateException(preparationFailure, cleanupFailure));
            throw;
        }
    }

    /// <summary>
    /// Allows a genuinely empty directory or the exact shape produced immediately after the
    /// transaction writes its marker. Materializers use this instead of treating the marker as
    /// provider payload.
    /// </summary>
    public static void RequireEmptyOrValidOwnershipMarker(string directory)
    {
        var root = CreationPathSafety.Canonical(directory);
        EnsureNoReparseTraversal(root);
        var entries = Directory.EnumerateFileSystemEntries(root).Take(2).ToArray();
        if (entries.Length == 0)
            return;
        if (entries.Length != 1 ||
            !Path.GetFileName(entries[0]).Equals(CreationOwnershipMarker.FileName, StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(entries[0]) ||
            CreationPathSafety.IsReparsePoint(entries[0]))
            throw new IOException("The operation-owned staging directory contains unexpected pre-existing content.");

        var marker = CreationOwnershipMarker.TryRead(root);
        if (!IsStructurallyValid(marker))
            throw new IOException("The operation-owned staging directory contains an invalid ownership marker.");
    }

    public static void RequireExactOwnership(
        string directory,
        Guid operationId,
        Guid serverId,
        string canonicalDestination)
    {
        EnsureNoReparseTraversal(directory);
        if (!CreationOwnershipMarker.Owns(directory, operationId, serverId, canonicalDestination))
            throw new IOException("The creation staging directory no longer carries its exact ownership marker.");
    }

    public static void ValidateOwnedTree(
        string directory,
        Guid operationId,
        Guid serverId,
        string canonicalDestination,
        CancellationToken cancellationToken = default)
    {
        var root = CreationPathSafety.Canonical(directory);
        RequireExactOwnership(root, operationId, serverId, canonicalDestination);
        var pending = new Queue<string>();
        pending.Enqueue(root);
        var entries = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            EnsureRegularDirectory(current);
            foreach (var child in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaximumOwnedEntries)
                    throw new InvalidDataException(
                        $"The creation candidate exceeds the {MaximumOwnedEntries:N0}-entry safety limit.");
                var attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException(
                        $"The creation candidate contains a link or reparse point: {Path.GetRelativePath(root, child)}");
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Enqueue(child);
            }
        }
    }

    /// <summary>
    /// Removes payload first and the ownership marker last. If any payload is locked or changes, the
    /// marker remains in place so a later retry can still prove which operation owns the directory.
    /// </summary>
    public static void DeleteOwnedTree(
        string directory,
        Guid operationId,
        Guid serverId,
        string canonicalDestination)
    {
        var root = CreationPathSafety.Canonical(directory);
        ValidateOwnedTree(root, operationId, serverId, canonicalDestination);
        var deletedEntries = 0;
        DeletePayload(root, root, ref deletedEntries);
        RequireExactOwnership(root, operationId, serverId, canonicalDestination);

        var entries = Directory.EnumerateFileSystemEntries(root).ToArray();
        if (entries.Length != 1 ||
            !Path.GetFileName(entries[0]).Equals(CreationOwnershipMarker.FileName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The creation staging directory changed while it was being cleaned.");
        File.Delete(entries[0]);
        Directory.Delete(root, recursive: false);
    }

    /// <summary>Creates each missing directory only after proving every existing ancestor is regular.</summary>
    public static void CreateDirectoryPath(string rootDirectory, string directory)
    {
        var root = CreationPathSafety.Canonical(rootDirectory);
        var target = CreationPathSafety.Canonical(directory);
        if (!CreationPathSafety.IsSamePath(root, target))
            CreationPathSafety.EnsureWithin(root, target);
        EnsureNoReparseTraversal(root);
        if (CreationPathSafety.IsSamePath(root, target))
            return;

        var relative = Path.GetRelativePath(root, target);
        var current = root;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.Exists(current) && !Directory.Exists(current))
                throw new IOException($"A file blocks the creation directory path: {segment}");
            if (!Directory.Exists(current))
                Directory.CreateDirectory(current);
            EnsureRegularDirectory(current);
        }
    }

    /// <summary>Rejects a reparse point in any existing component from the volume root to the path.</summary>
    public static void EnsureNoReparseTraversal(string path)
    {
        var full = CreationPathSafety.Canonical(path);
        var volumeRoot = Path.GetPathRoot(full);
        if (string.IsNullOrWhiteSpace(volumeRoot))
            throw new InvalidDataException("The creation path has no filesystem root.");
        var current = volumeRoot;
        EnsureRegularDirectory(current);
        var relative = Path.GetRelativePath(volumeRoot, full);
        if (relative == ".")
            return;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureRegularDirectory(current);
        }
    }

    public static bool EntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // An inaccessible entry is still occupied and must never be treated as available.
            return true;
        }
    }

    /// <summary>Creates one new leaf directory and fails atomically when that leaf already exists.</summary>
    /// <remarks>
    /// On Windows, <c>CreateDirectoryW</c> performs the leaf-name collision check and creation as one
    /// filesystem operation. This closes the managed <c>Exists</c>/<c>CreateDirectory</c> race, but
    /// it does not pin the identities of parent path components. A different process running as the
    /// same user could still swap an ancestor between path checks. ChunkPilot checks every ancestor
    /// immediately before and after creation and fails closed on a stable reparse point, but does not
    /// claim to defeat a continuously racing same-user process without handle-relative NT operations.
    /// </remarks>
    public static void CreateDirectoryExclusive(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Atomic managed-server staging directory creation is supported only on Windows.");
        var canonical = CreationPathSafety.Canonical(path);
        if (NativeCreateDirectory(ToExtendedLengthPath(canonical), IntPtr.Zero))
            return;
        var error = Marshal.GetLastWin32Error();
        if (error is ErrorFileExists or ErrorAlreadyExists)
            throw new IOException("The operation-owned directory already exists and was preserved.");
        throw new IOException(
            "ChunkPilot could not create the operation-owned directory.",
            new Win32Exception(error));
    }

    /// <summary>
    /// Cleans only evidence this invocation can still prove: an exact-owned tree, or the leaf it
    /// atomically created when that leaf remains a regular empty directory. Unknown or partial
    /// content is deliberately preserved.
    /// </summary>
    public static Exception? CleanupAfterFailedMarkerInitialization(
        string directory,
        Guid operationId,
        Guid serverId,
        string canonicalDestination,
        bool markerWasWrittenByThisInvocation)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;
            if (markerWasWrittenByThisInvocation &&
                CreationOwnershipMarker.Owns(directory, operationId, serverId, canonicalDestination))
            {
                DeleteOwnedTree(directory, operationId, serverId, canonicalDestination);
                return null;
            }

            EnsureNoReparseTraversal(directory);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory, recursive: false);
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return exception;
        }
    }

    private static void EnsureRegularDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"A required creation directory does not exist: {directory}");
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("ChunkPilot could not prove that the creation path is a regular directory.", exception);
        }
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The creation path contains a link or reparse point.");
    }

    private static void DeletePayload(string root, string directory, ref int entries)
    {
        foreach (var child in Directory.EnumerateFileSystemEntries(directory))
        {
            if (CreationPathSafety.IsSamePath(root, directory) &&
                Path.GetFileName(child).Equals(CreationOwnershipMarker.FileName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (++entries > MaximumOwnedEntries)
                throw new InvalidDataException(
                    $"The creation candidate changed beyond the {MaximumOwnedEntries:N0}-entry safety limit during cleanup.");
            var attributes = File.GetAttributes(child);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    $"The creation candidate contains a link or reparse point: {Path.GetRelativePath(root, child)}");
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                DeletePayload(root, child, ref entries);
                Directory.Delete(child, recursive: false);
            }
            else
            {
                File.Delete(child);
            }
        }
    }

    private static bool IsStructurallyValid(CreationOwnershipMarker? marker)
    {
        if (marker is null || marker.SchemaVersion != CreationOwnershipMarker.CurrentSchemaVersion ||
            marker.OperationId == Guid.Empty || marker.ServerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(marker.CanonicalDestination))
            return false;
        try
        {
            return CreationPathSafety.Canonical(marker.CanonicalDestination)
                .Equals(marker.CanonicalDestination, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string ToExtendedLengthPath(string path)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith("\\\\", StringComparison.Ordinal))
            return "\\\\?\\UNC\\" + path[2..];
        return "\\\\?\\" + path;
    }

    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCreateDirectory(string path, IntPtr securityAttributes);
}
