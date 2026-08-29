using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class JarInventoryService
{
    private string EnsureRecoveryDirectory(ServerDefinition server, params string[] categories)
    {
        var segments = new[] { "Recovery", server.Id.ToString("N") }
            .Concat(categories)
            .Append(Guid.NewGuid().ToString("N"))
            .ToArray();
        return EnsureAppDataDirectory(segments);
    }

    private string EnsureAppDataDirectory(params string[] segments) =>
        EnsureDirectoryChain(paths.Root, Path.Combine(new[] { paths.Root }.Concat(segments).ToArray()));

    private static string EnsureServerDirectory(ServerDefinition server, params string[] segments) =>
        EnsureDirectoryChain(server.RootPath, ResolveServerPath(server, segments));

    private static string EnsureDirectoryChain(string root, string directory)
    {
        var canonicalRoot = CanonicalRoot(root);
        var canonicalDirectory = EnsureWithinRoot(canonicalRoot, directory);
        if (PathEntry(canonicalRoot) != PathEntryKind.Directory)
            throw new DirectoryNotFoundException("The trusted root directory is unavailable or unsafe.");
        var current = canonicalRoot;
        foreach (var segment in RelativeSegments(canonicalRoot, canonicalDirectory))
        {
            current = Path.Combine(current, segment);
            var kind = PathEntry(current);
            if (kind == PathEntryKind.Missing)
            {
                Directory.CreateDirectory(current);
                kind = PathEntry(current);
            }
            if (kind != PathEntryKind.Directory)
                throw new IOException("An add-on directory path was replaced by a file.");
        }
        if (!ValidateDirectoryChain(canonicalRoot, canonicalDirectory, allowMissing: false))
            throw new DirectoryNotFoundException(canonicalDirectory);
        return canonicalDirectory;
    }

    private static bool ValidateDirectoryChain(string root, string directory, bool allowMissing)
    {
        var canonicalRoot = CanonicalRoot(root);
        var canonicalDirectory = EnsureWithinRoot(canonicalRoot, directory);
        if (PathEntry(canonicalRoot) != PathEntryKind.Directory)
            throw new DirectoryNotFoundException("The trusted root directory is unavailable or unsafe.");
        var current = canonicalRoot;
        foreach (var segment in RelativeSegments(canonicalRoot, canonicalDirectory))
        {
            current = Path.Combine(current, segment);
            var kind = PathEntry(current);
            if (kind == PathEntryKind.Missing)
            {
                if (allowMissing)
                    return false;
                throw new DirectoryNotFoundException(current);
            }
            if (kind != PathEntryKind.Directory)
                throw new IOException("An add-on directory path was replaced by a file.");
        }
        return true;
    }

    private static void ValidateRegularFileChain(string root, string file)
    {
        var canonicalRoot = CanonicalRoot(root);
        var canonicalFile = EnsureWithinRoot(canonicalRoot, file);
        var parent = Path.GetDirectoryName(canonicalFile)
            ?? throw new UnauthorizedAccessException("The add-on file has no trusted parent directory.");
        _ = ValidateDirectoryChain(canonicalRoot, parent, allowMissing: false);
        if (PathEntry(canonicalFile) != PathEntryKind.RegularFile)
            throw new FileNotFoundException("The add-on file is unavailable or unsafe.", canonicalFile);
    }

    private static void ValidateOptionalRegularFileChain(string root, string file)
    {
        var canonicalRoot = CanonicalRoot(root);
        var canonicalFile = EnsureWithinRoot(canonicalRoot, file);
        var parent = Path.GetDirectoryName(canonicalFile)
            ?? throw new UnauthorizedAccessException("The add-on file has no trusted parent directory.");
        _ = ValidateDirectoryChain(canonicalRoot, parent, allowMissing: false);
        var kind = PathEntry(canonicalFile);
        if (kind == PathEntryKind.Directory)
            throw new IOException("An add-on file path was replaced by a directory.");
    }

    private static void EnsureSafeExistingParentDirectory(string root, string file)
    {
        var parent = Path.GetDirectoryName(EnsureWithinRoot(CanonicalRoot(root), file))
            ?? throw new UnauthorizedAccessException("The add-on path has no trusted parent directory.");
        _ = EnsureDirectoryChain(root, parent);
    }

    private static IReadOnlyList<string> EnumerateSafeJarFiles(string root, string directory)
    {
        _ = ValidateDirectoryChain(root, directory, allowMissing: false);
        var files = Directory.EnumerateFileSystemEntries(directory, "*.jar", SearchOption.TopDirectoryOnly)
            .Take(5_001)
            .ToArray();
        if (files.Length > 5_000)
            throw new IOException("The add-on folder exceeds the bounded inventory limit.");
        foreach (var file in files)
        {
            if (!Path.GetDirectoryName(file)!.Equals(directory, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The add-on inventory escaped its expected directory.");
            ValidateRegularFileChain(root, file);
        }
        return files;
    }

    private static string ResolveContentJarPath(
        ServerDefinition server,
        string relativePath,
        bool mustExist)
    {
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var segments = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var valid = segments.Length == 2 &&
                    segments[0].Equals(folderName, StringComparison.OrdinalIgnoreCase) ||
                    segments.Length == 3 &&
                    segments[0].Equals(".chunkpilot-disabled", StringComparison.OrdinalIgnoreCase) &&
                    segments[1].Equals(folderName, StringComparison.OrdinalIgnoreCase);
        var fileName = segments.Length == 0 ? "" : segments[^1];
        if (!valid || !Path.GetExtension(fileName).Equals(".jar", StringComparison.OrdinalIgnoreCase) ||
            fileName.Length is 0 or > 240 || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Only a top-level managed mod or plugin JAR is allowed.");
        var path = ResolveServerPath(server, segments);
        if (mustExist)
            ValidateRegularFileChain(server.RootPath, path);
        else
            ValidateOptionalRegularFileChainAllowMissingParent(server.RootPath, path);
        return path;
    }

    private static void ValidateOptionalRegularFileChainAllowMissingParent(string root, string file)
    {
        var canonicalRoot = CanonicalRoot(root);
        var canonicalFile = EnsureWithinRoot(canonicalRoot, file);
        if (PathEntry(canonicalRoot) != PathEntryKind.Directory)
            throw new DirectoryNotFoundException("The trusted root directory is unavailable or unsafe.");
        var parent = Path.GetDirectoryName(canonicalFile)
            ?? throw new UnauthorizedAccessException("The add-on file has no trusted parent directory.");
        var current = canonicalRoot;
        foreach (var segment in RelativeSegments(canonicalRoot, parent))
        {
            current = Path.Combine(current, segment);
            var kind = PathEntry(current);
            if (kind == PathEntryKind.Missing)
                return;
            if (kind != PathEntryKind.Directory)
                throw new IOException("An add-on directory path was replaced by a file.");
        }
        var fileKind = PathEntry(canonicalFile);
        if (fileKind == PathEntryKind.Directory)
            throw new IOException("An add-on file path was replaced by a directory.");
    }

    private string ResolveAppDataRecoveryPath(string value, bool mustExist)
    {
        var path = Path.GetFullPath(value);
        if (!IsWithin(path, paths.Recovery))
            throw new UnauthorizedAccessException("The plugin recovery path is outside ChunkPilot-owned storage.");
        if (mustExist)
            ValidateRegularFileChain(paths.Root, path);
        else
            ValidateOptionalRegularFileChain(paths.Root, path);
        return path;
    }

    private static string ResolveServerPath(ServerDefinition server, string relativePath) =>
        ResolveServerPath(server, relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));

    private static string ResolveServerPath(ServerDefinition server, params string[] segments)
    {
        var root = CanonicalRoot(server.RootPath);
        return EnsureWithinRoot(root, Path.Combine(new[] { root }.Concat(segments).ToArray()));
    }

    private static string CanonicalRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static string EnsureWithinRoot(string root, string candidate)
    {
        var canonical = Path.GetFullPath(candidate);
        if (!canonical.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !canonical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The add-on path escapes its trusted root.");
        return canonical;
    }

    private static string[] RelativeSegments(string root, string candidate) =>
        Path.GetRelativePath(root, candidate)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

    private static PathEntryKind PathEntry(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return PathEntryKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return PathEntryKind.Missing;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException(
                "ChunkPilot will not traverse a junction, symbolic link, or other reparse point for add-on content.");
        return (attributes & FileAttributes.Directory) != 0
            ? PathEntryKind.Directory
            : PathEntryKind.RegularFile;
    }

    private static void ValidateRegularFileNoFollow(string path, string message)
    {
        try
        {
            if (PathEntry(Path.GetFullPath(path)) != PathEntryKind.RegularFile)
                throw new InvalidDataException(message);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new InvalidDataException(message, exception);
        }
    }

    private void ValidateInspectionSource(ServerDefinition server, string path)
    {
        var canonical = Path.GetFullPath(path);
        if (IsWithin(canonical, server.RootPath))
        {
            ValidateRegularFileChain(server.RootPath, canonical);
            return;
        }
        if (IsWithin(canonical, paths.Root))
        {
            ValidateRegularFileChain(paths.Root, canonical);
            return;
        }
        ValidateRegularFileNoFollow(
            canonical, "The selected add-on JAR is unavailable or is a reparse point.");
    }

    private static string RegularFileSha256(string root, string path)
    {
        ValidateRegularFileChain(root, path);
        var hash = Sha256(path);
        ValidateRegularFileChain(root, path);
        return hash;
    }

    private static string RegularFileSha256OrEmpty(string root, string path)
    {
        ValidateOptionalRegularFileChain(root, path);
        return PathEntry(path) == PathEntryKind.Missing ? "" : RegularFileSha256(root, path);
    }

    private static void TryRestoreMovedFile(
        string movedRoot,
        string moved,
        string originalRoot,
        string original)
    {
        try
        {
            ValidateRegularFileChain(movedRoot, moved);
            ValidateOptionalRegularFileChain(originalRoot, original);
            if (PathEntry(original) == PathEntryKind.Missing)
                File.Move(moved, original, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The caller preserves the moved copy as recovery evidence when a safe restoration is impossible.
        }
    }

    private static void TryDeleteRegularTemporary(string root, string temporary)
    {
        try
        {
            ValidateOptionalRegularFileChain(root, temporary);
            if (PathEntry(temporary) == PathEntryKind.RegularFile)
                File.Delete(temporary);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Never follow or force-delete an externally replaced temporary path.
        }
    }

    private Task<IAsyncDisposable> AcquireContentLockAsync(
        ServerDefinition server,
        CancellationToken cancellationToken = default) =>
        pathLocks.AcquireAsync(
            Path.Combine(CanonicalRoot(server.RootPath), ".chunkpilot-addon-content.lock-key"),
            cancellationToken);

    private T WithContentLock<T>(ServerDefinition server, Func<T> action)
    {
        var lease = AcquireContentLockAsync(server).GetAwaiter().GetResult();
        try { return action(); }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private void WithContentLock(ServerDefinition server, Action action) =>
        WithContentLock(server, () =>
        {
            action();
            return true;
        });

    private void WithPathLock(string path, Action action)
    {
        var lease = pathLocks.AcquireAsync(path).GetAwaiter().GetResult();
        try { action(); }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private enum PathEntryKind
    {
        Missing,
        RegularFile,
        Directory
    }
}
