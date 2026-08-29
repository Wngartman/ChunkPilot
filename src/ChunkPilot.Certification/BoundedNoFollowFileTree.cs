namespace ChunkPilot.Certification;

internal sealed record BoundedFileTreeEntry(
    string Path,
    FileAttributes Attributes,
    long SizeBytes)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

/// <summary>
/// Inventories a stopped, task-owned tree without allowing recursive enumeration to cross a
/// junction or symbolic link. Callers choose a purpose-specific maximum entry count.
/// </summary>
internal static class BoundedNoFollowFileTree
{
    public static IReadOnlyList<BoundedFileTreeEntry> Inventory(
        string root,
        int maximumEntries,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        var canonical = Path.GetFullPath(root);
        if (!Directory.Exists(canonical))
            throw new DirectoryNotFoundException(
                "The bounded task-owned inventory root is unavailable.");
        RejectReparse(canonical);

        var result = new List<BoundedFileTreeEntry>(Math.Min(maximumEntries, 4_096));
        var pending = new Stack<string>();
        pending.Push(canonical);
        while (pending.TryPop(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Count >= maximumEntries)
                    throw new InvalidOperationException(
                        "The task-owned tree exceeded its bounded inventory limit.");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException(
                        "The task-owned tree contains a reparse point and was refused.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var entry = new BoundedFileTreeEntry(
                    path,
                    attributes,
                    isDirectory ? 0 : new FileInfo(path).Length);
                result.Add(entry);
                if (isDirectory)
                    pending.Push(path);
            }
        }
        return result;
    }

    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The task-owned inventory root cannot be a reparse point.");
    }
}
