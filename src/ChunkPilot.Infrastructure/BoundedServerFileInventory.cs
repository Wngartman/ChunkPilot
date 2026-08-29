namespace ChunkPilot.Infrastructure;

/// <summary>
/// Captures a bounded metadata-only view of one server tree without following links. Snapshot,
/// migration, and storage forecasting share this boundary so they cannot disagree about which
/// files are safe to read or duplicate.
/// </summary>
internal static class BoundedServerFileInventory
{
    internal const int MaximumEntries = 500_000;

    internal static ServerFileInventory Capture(
        string rootPath,
        int maximumEntries = MaximumEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        if (maximumEntries is <= 0 or > MaximumEntries)
            throw new ArgumentOutOfRangeException(
                nameof(maximumEntries),
                $"The server inventory limit must be from 1 through {MaximumEntries:N0} entries.");

        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);
        RejectReparsePoint(root, root);

        var files = new List<ServerFileInventoryEntry>();
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var seenEntries = 0;
        long totalBytes = 0;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            RejectReparsePoint(root, directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++seenEntries > maximumEntries)
                    throw new InvalidDataException(
                        $"The server contains more than {maximumEntries:N0} filesystem entries; " +
                        "snapshot and update preparation were not started.");

                var fullPath = Path.GetFullPath(child);
                EnsureWithin(root, fullPath);
                var attributes = File.GetAttributes(fullPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"The server tree contains a link or reparse point: {Relative(root, fullPath)}.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(fullPath);
                    continue;
                }

                var relative = Relative(root, fullPath);
                if (!relativePaths.Add(relative))
                    throw new InvalidDataException(
                        $"The server contains duplicate or case-colliding file paths: {relative}.");
                var info = new FileInfo(fullPath);
                info.Refresh();
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException(
                        $"The server tree contains an unsafe or missing file: {relative}.");
                totalBytes = StorageSpaceGuard.SaturatingAdd(totalBytes, info.Length);
                files.Add(new ServerFileInventoryEntry(
                    root,
                    fullPath,
                    relative,
                    info.Length,
                    info.LastWriteTimeUtc));
            }
            RejectReparsePoint(root, directory);
        }

        return new ServerFileInventory(
            root,
            files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            seenEntries,
            totalBytes);
    }

    internal static void ValidateUnchanged(ServerFileInventoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        EnsureWithin(entry.RootPath, entry.FullPath);
        RejectReparseAncestors(entry.RootPath, entry.FullPath);
        var info = new FileInfo(entry.FullPath);
        info.Refresh();
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"The server file changed to a link, reparse point, or missing file: {entry.RelativePath}.");
        if (info.Length != entry.Length || info.LastWriteTimeUtc != entry.LastWriteTimeUtc)
            throw new IOException(
                $"The server file changed after its bounded inventory was captured: {entry.RelativePath}.");
    }

    private static void RejectReparseAncestors(string root, string file)
    {
        RejectReparsePoint(root, root);
        var current = Path.GetDirectoryName(file);
        while (!string.IsNullOrWhiteSpace(current) &&
               !current.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            RejectReparsePoint(root, current);
            current = Path.GetDirectoryName(current);
        }
        if (string.IsNullOrWhiteSpace(current))
            throw new InvalidDataException("The server file escaped its bounded inventory root.");
    }

    private static void RejectReparsePoint(string root, string path)
    {
        EnsureWithinOrEqual(root, path);
        if (!Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"The server tree contains an unsafe, missing, or reparse-point directory: {Relative(root, path)}.");
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative == "." ? "." : PersistentDataClassifier.Normalize(relative);
    }

    private static void EnsureWithin(string root, string candidate)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The server inventory escaped its canonical root.");
    }

    private static void EnsureWithinOrEqual(string root, string candidate)
    {
        if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase))
            return;
        EnsureWithin(root, candidate);
    }
}

internal sealed record ServerFileInventory(
    string RootPath,
    IReadOnlyList<ServerFileInventoryEntry> Files,
    int EntryCount,
    long TotalBytes);

internal sealed record ServerFileInventoryEntry(
    string RootPath,
    string FullPath,
    string RelativePath,
    long Length,
    DateTime LastWriteTimeUtc);
