using System.Security.Cryptography;
using System.Text;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Materialises an ownership-uncertain server into a new operation-owned directory without writing
/// to the source. Registration and activation remain the Agent's responsibility.
/// </summary>
public sealed class ManagedInstanceCopyService
{
    public const int MaximumEntries = 500_000;

    public async Task<ManagedInstanceCopyResult> MaterializeAsync(
        string sourceRoot,
        string stagingRoot,
        string destinationRoot,
        Guid operationId,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        sourceRoot = Canonical(sourceRoot);
        stagingRoot = Canonical(stagingRoot);
        destinationRoot = Canonical(destinationRoot);
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException(sourceRoot);
        if (Directory.Exists(stagingRoot) || File.Exists(stagingRoot))
            throw new IOException("The managed-copy staging path already exists.");
        if (Directory.Exists(destinationRoot) || File.Exists(destinationRoot))
            throw new IOException("The managed-copy destination already exists.");
        if (File.GetAttributes(sourceRoot).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException("The source contains a reparse point, so ChunkPilot cannot prove a closed copy boundary.");

        var files = Inventory(sourceRoot);
        var totalBytes = files.Where(item => !item.IsDirectory).Sum(item => item.Length);
        var drive = new DriveInfo(Path.GetPathRoot(stagingRoot)!);
        if (drive.AvailableFreeSpace < totalBytes + Math.Max(64L * 1024 * 1024, totalBytes / 20))
            throw new IOException("There is not enough free space to create and verify a managed copy.");

        Directory.CreateDirectory(stagingRoot);
        await CreationOwnershipMarker.WriteAsync(stagingRoot, new CreationOwnershipMarker(
            CreationOwnershipMarker.CurrentSchemaVersion, operationId, serverId, destinationRoot,
            DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var copied = 0;
        foreach (var item in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(sourceRoot, item.RelativePath);
            var destination = Path.Combine(stagingRoot, item.RelativePath);
            if (File.GetAttributes(source).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"The source changed to a reparse point while copying: {item.RelativePath}");
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                var directoryIdentity = Encoding.UTF8.GetBytes(item.RelativePath.Replace('\\', '/') + "/\0");
                aggregate.AppendData(directoryIdentity);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            byte[] sourceHash;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                sourceHash = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
                input.Position = 0;
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var sourceInfo = new FileInfo(source);
            if (sourceInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || sourceInfo.Length != item.Length ||
                sourceInfo.LastWriteTimeUtc != item.LastWriteUtc)
                throw new IOException($"The source changed while it was being copied: {item.RelativePath}");
            await using var verification = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var destinationHash = await SHA256.HashDataAsync(verification, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
                throw new IOException($"Managed-copy verification failed: {item.RelativePath}");
            File.SetLastWriteTimeUtc(destination, item.LastWriteUtc);
            var identity = Encoding.UTF8.GetBytes(item.RelativePath.Replace('\\', '/') + "\0");
            aggregate.AppendData(identity);
            aggregate.AppendData(sourceHash);
            copied++;
        }

        foreach (var directory in files.Where(item => item.IsDirectory)
                     .OrderByDescending(item => item.RelativePath.Length))
            Directory.SetLastWriteTimeUtc(Path.Combine(stagingRoot, directory.RelativePath), directory.LastWriteUtc);

        var digest = Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
        await ManagedInstanceOwnershipMarker.WriteAsync(stagingRoot, serverId, cancellationToken,
            "VerifiedManagedCopy", digest).ConfigureAwait(false);
        return new ManagedInstanceCopyResult(copied, totalBytes, digest);
    }

    public static void DeleteOperationOwnedCandidate(string root, Guid operationId, Guid serverId)
    {
        if (!Directory.Exists(root)) return;
        if (!CreationOwnershipMarker.Owns(root, operationId, serverId))
            throw new InvalidOperationException("ChunkPilot refused to remove a copy candidate it cannot prove this operation owns.");
        DeleteTree(root);
    }

    private static IReadOnlyList<CopyItem> Inventory(string root)
    {
        var items = new List<CopyItem>();
        var pending = new Stack<string>();
        pending.Push(root);
        var seen = 0;
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (++seen > MaximumEntries)
                    throw new InvalidOperationException($"The server contains more than {MaximumEntries:N0} entries; managed copy was not started.");
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException($"The source contains a reparse point: {Path.GetRelativePath(root, path)}");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    var directoryInfo = new DirectoryInfo(path);
                    items.Add(new CopyItem(Path.GetRelativePath(root, path), 0, directoryInfo.LastWriteTimeUtc, true));
                    pending.Push(path);
                    continue;
                }
                var relative = Path.GetRelativePath(root, path);
                if (relative.Equals(CreationOwnershipMarker.FileName, StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(ManagedInstanceOwnershipMarker.FileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(path);
                items.Add(new CopyItem(relative, info.Length, info.LastWriteTimeUtc, false));
            }
        }
        return items.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void DeleteTree(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException("An operation-owned candidate acquired a reparse point; automatic cleanup stopped.");
            if (attributes.HasFlag(FileAttributes.Directory)) DeleteTree(path);
            else File.Delete(path);
        }
        Directory.Delete(root);
    }

    private static string Canonical(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private sealed record CopyItem(string RelativePath, long Length, DateTime LastWriteUtc, bool IsDirectory);
}

public sealed record ManagedInstanceCopyResult(int FileCount, long ByteCount, string Sha256);
