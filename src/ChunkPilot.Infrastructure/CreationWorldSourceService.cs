using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record CreationWorldCopyProgress(
    long CopiedBytes,
    long TotalBytes,
    int CopiedFiles,
    int TotalFiles,
    string CurrentFile);

/// <summary>
/// Inspects and copies an existing Minecraft world without modifying the selected source. All
/// writes are confined to a caller-owned managed creation staging directory.
/// </summary>
public sealed class CreationWorldSourceService
{
    private const string ReviewFolderPrefix = "review-";
    private const string StagingFolderPrefix = ".chunkpilot-world-import-";

    public Task<CreationWorldSource> InspectAsync(
        string path,
        CreationWorldSourceKind kind,
        CancellationToken cancellationToken = default) => kind switch
    {
        CreationWorldSourceKind.Folder => InspectFolderAsync(path, cancellationToken),
        CreationWorldSourceKind.ZipArchive => InspectZipAsync(path, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown world source kind.")
    };

    public async Task VerifyUnchangedAsync(
        CreationWorldSource source,
        CancellationToken cancellationToken = default)
    {
        var problems = source.Problems();
        if (problems.Count > 0)
            throw new InvalidDataException(string.Join(" ", problems));
        if (source.Kind == CreationWorldSourceKind.ZipArchive)
        {
            await VerifyZipIdentityAsync(source, cancellationToken).ConfigureAwait(false);
            return;
        }
        var current = await InspectFolderAsync(source.NativePath, cancellationToken).ConfigureAwait(false);
        EnsureSameSelection(source, current, compareFingerprint: true);
    }

    public async Task MaterializeAsync(
        CreationWorldSource source,
        string serverStagingPath,
        CancellationToken cancellationToken = default) =>
        await MaterializeAsync(source, serverStagingPath, null, cancellationToken).ConfigureAwait(false);

    public async Task MaterializeAsync(
        CreationWorldSource source,
        string serverStagingPath,
        IProgress<CreationWorldCopyProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var stagingRoot = Path.GetFullPath(serverStagingPath);
        if (!Directory.Exists(stagingRoot))
            throw new DirectoryNotFoundException("The managed server staging directory was not created.");
        var sourcePath = Path.GetFullPath(source.NativePath);
        if (CreationPathSafety.Overlaps(stagingRoot, sourcePath))
            throw new InvalidDataException("The selected world source must be outside the managed creation staging directory.");

        if (source.Kind == CreationWorldSourceKind.Folder)
        {
            var current = await InspectFolderAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            EnsureSameSelection(source, current, compareFingerprint: true);
            await CopySelectionAsync(current, sourcePath, stagingRoot, progress, cancellationToken).ConfigureAwait(false);
            var afterCopy = await InspectFolderAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            EnsureSameSelection(source, afterCopy, compareFingerprint: true);
            return;
        }

        await VerifyZipIdentityAsync(source, cancellationToken).ConfigureAwait(false);
        var extractionRoot = Path.Combine(stagingRoot, StagingFolderPrefix + Guid.NewGuid().ToString("N"));
        CreationPathSafety.EnsureWithin(stagingRoot, extractionRoot);
        Directory.CreateDirectory(extractionRoot);
        try
        {
            await ServerImportInspectionService.ExtractAsync(sourcePath, extractionRoot, cancellationToken)
                .ConfigureAwait(false);
            var extracted = await InspectTreeAsync(
                extractionRoot,
                Path.GetFileNameWithoutExtension(sourcePath),
                sourcePath,
                CreationWorldSourceKind.ZipArchive,
                source.SourceSizeBytes,
                source.SourceFingerprint,
                cancellationToken).ConfigureAwait(false);
            EnsureSameSelection(source, extracted, compareFingerprint: false);
            await VerifyZipIdentityAsync(source, cancellationToken).ConfigureAwait(false);
            await CopySelectionAsync(extracted, extractionRoot, stagingRoot, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteOwnedDirectory(extractionRoot, stagingRoot, StagingFolderPrefix);
        }
    }

    private static async Task<CreationWorldSource> InspectFolderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The selected world folder no longer exists.");
        if (File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Choose a regular world folder rather than a link or reparse point.");
        return await InspectTreeAsync(
            root,
            new DirectoryInfo(root).Name,
            root,
            CreationWorldSourceKind.Folder,
            0,
            "",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CreationWorldSource> InspectZipAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Path.GetExtension(fullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a ZIP archive containing one Minecraft world.");
        var package = await new ServerImportInspectionService().InspectFileAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        var reviewParent = Path.Combine(Path.GetTempPath(), "ChunkPilot", "WorldReview");
        Directory.CreateDirectory(reviewParent);
        var reviewRoot = Path.Combine(reviewParent, ReviewFolderPrefix + Guid.NewGuid().ToString("N"));
        CreationPathSafety.EnsureWithin(reviewParent, reviewRoot);
        Directory.CreateDirectory(reviewRoot);
        try
        {
            await ServerImportInspectionService.ExtractAsync(fullPath, reviewRoot, cancellationToken)
                .ConfigureAwait(false);
            return await InspectTreeAsync(
                reviewRoot,
                Path.GetFileNameWithoutExtension(fullPath),
                fullPath,
                CreationWorldSourceKind.ZipArchive,
                package.SourceSizeBytes,
                package.Sha256,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteOwnedDirectory(reviewRoot, reviewParent, ReviewFolderPrefix);
        }
    }

    private static Task<CreationWorldSource> InspectTreeAsync(
        string root,
        string preferredWorldName,
        string sourcePath,
        CreationWorldSourceKind kind,
        long sourceSizeBytes,
        string fixedFingerprint,
        CancellationToken cancellationToken) => Task.Run(() => InspectTree(
            root, preferredWorldName, sourcePath, kind, sourceSizeBytes, fixedFingerprint,
            cancellationToken), cancellationToken);

    private static CreationWorldSource InspectTree(
        string root,
        string preferredWorldName,
        string sourcePath,
        CreationWorldSourceKind kind,
        long sourceSizeBytes,
        string fixedFingerprint,
        CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        var files = new List<FileInfo>();
        var levelDirectories = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(fullRoot);
        long reviewedBytes = 0;
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The selected world contains a directory link or reparse point.");
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The selected world contains a directory link or reparse point.");
                pending.Enqueue(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (files.Count >= ServerImportInspectionService.MaximumEntries)
                    throw new InvalidDataException($"The selected world exceeds the {ServerImportInspectionService.MaximumEntries:N0}-file review limit.");
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The selected world contains a file link or reparse point.");
                reviewedBytes = checked(reviewedBytes + info.Length);
                if (reviewedBytes > ServerImportInspectionService.MaximumExpandedBytes)
                    throw new InvalidDataException("The selected world exceeds the 16 GB expanded-size limit.");
                files.Add(info);
                if (info.Name.Equals("level.dat", StringComparison.OrdinalIgnoreCase))
                    levelDirectories.Add(info.DirectoryName!);
            }
        }

        var candidateSet = levelDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mainCandidates = levelDirectories.Where(directory => !IsDimensionSibling(directory, candidateSet)).ToArray();
        if (mainCandidates.Length == 0)
            throw new InvalidDataException("No Minecraft world was found. Choose a folder or ZIP containing level.dat.");
        if (mainCandidates.Length > 1)
            throw new InvalidDataException("More than one main Minecraft world was found. Choose a folder or ZIP containing exactly one main world.");

        var main = Path.GetFullPath(mainCandidates[0]);
        CreationPathSafety.EnsureWithin(fullRoot, main);
        var parent = Path.GetDirectoryName(main)!;
        var sourceFolderName = new DirectoryInfo(main).Name;
        var nether = Path.Combine(parent, sourceFolderName + "_nether");
        var end = Path.Combine(parent, sourceFolderName + "_the_end");
        if (!candidateSet.Contains(nether)) nether = "";
        if (!candidateSet.Contains(end)) end = "";

        var selectedRoots = new[] { main, nether, end }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        var selectedFiles = files.Where(file => !file.Name.Equals("session.lock", StringComparison.OrdinalIgnoreCase) &&
                selectedRoots.Any(selected =>
                (CreationPathSafety.IsSamePath(selected, file.FullName) ||
                 CreationPathSafety.IsUnder(selected, file.FullName))))
            .OrderBy(file => Path.GetRelativePath(fullRoot, file.FullName), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expandedBytes = selectedFiles.Sum(file => file.Length);
        if (expandedBytes <= 0)
            throw new InvalidDataException("The selected world contains no readable files.");
        var worldName = MakeSafeWorldName(main.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? preferredWorldName
            : sourceFolderName);
        var fingerprint = string.IsNullOrWhiteSpace(fixedFingerprint)
            ? ComputeDirectoryFingerprint(fullRoot, selectedFiles)
            : fixedFingerprint.ToLowerInvariant();
        return new CreationWorldSource
        {
            Kind = kind,
            NativePath = Path.GetFullPath(sourcePath),
            WorldName = worldName,
            MainWorldRelativePath = Relative(fullRoot, main),
            NetherWorldRelativePath = string.IsNullOrWhiteSpace(nether) ? "" : Relative(fullRoot, nether),
            EndWorldRelativePath = string.IsNullOrWhiteSpace(end) ? "" : Relative(fullRoot, end),
            SourceFingerprint = fingerprint,
            SourceSizeBytes = sourceSizeBytes > 0 ? sourceSizeBytes : expandedBytes,
            ExpandedSizeBytes = expandedBytes,
            FileCount = selectedFiles.Length
        };
    }

    private static bool IsDimensionSibling(string directory, IReadOnlySet<string> candidates)
    {
        var name = Path.GetFileName(directory);
        var suffix = name.EndsWith("_nether", StringComparison.OrdinalIgnoreCase) ? "_nether"
            : name.EndsWith("_the_end", StringComparison.OrdinalIgnoreCase) ? "_the_end" : "";
        if (suffix.Length == 0)
            return false;
        var baseName = name[..^suffix.Length];
        return candidates.Contains(Path.Combine(Path.GetDirectoryName(directory)!, baseName));
    }

    private static async Task VerifyZipIdentityAsync(
        CreationWorldSource source,
        CancellationToken cancellationToken)
    {
        var path = Path.GetFullPath(source.NativePath);
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("The selected world ZIP no longer exists.", path);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length != source.SourceSizeBytes)
            throw new InvalidDataException("The selected world ZIP changed after review. Choose it again.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!hash.Equals(source.SourceFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected world ZIP changed after review. Choose it again.");
    }

    private static async Task CopySelectionAsync(
        CreationWorldSource source,
        string sourceRoot,
        string stagingRoot,
        IProgress<CreationWorldCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        var state = new CopyState(source.ExpandedSizeBytes, source.FileCount);
        var mappings = new[]
        {
            (source.MainWorldRelativePath, source.WorldName),
            (source.NetherWorldRelativePath, source.WorldName + "_nether"),
            (source.EndWorldRelativePath, source.WorldName + "_the_end")
        }.Where(item => !string.IsNullOrWhiteSpace(item.Item1));
        foreach (var (relative, destinationName) in mappings)
        {
            var sourceDirectory = Path.GetFullPath(Path.Combine(sourceRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            CreationPathSafety.EnsureWithin(sourceRoot, sourceDirectory);
            var destination = Path.GetFullPath(Path.Combine(stagingRoot, destinationName));
            CreationPathSafety.EnsureWithin(stagingRoot, destination);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new InvalidDataException($"The server package already contains '{destinationName}'. ChunkPilot did not overwrite it.");
            await CopyWorldDirectoryAsync(sourceDirectory, destination, state, progress, cancellationToken)
                .ConfigureAwait(false);
            if (!File.Exists(Path.Combine(destination, "level.dat")))
                throw new InvalidDataException($"The copied world '{destinationName}' is missing level.dat.");
        }
    }

    private static async Task CopyWorldDirectoryAsync(
        string source,
        string destination,
        CopyState state,
        IProgress<CreationWorldCopyProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        var pending = new Queue<string>();
        pending.Enqueue(Path.GetFullPath(source));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The selected world changed to include a directory link while it was being copied.");
                var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, child)));
                CreationPathSafety.EnsureWithin(destination, target);
                Directory.CreateDirectory(target);
                pending.Enqueue(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Path.GetFileName(file).Equals("session.lock", StringComparison.OrdinalIgnoreCase))
                    continue;
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The selected world changed to include a file link while it was being copied.");
                var target = Path.GetFullPath(Path.Combine(destination, Path.GetRelativePath(source, file)));
                CreationPathSafety.EnsureWithin(destination, target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                if (output.Length != info.Length)
                    throw new IOException($"The selected world changed while ChunkPilot copied {Path.GetRelativePath(source, file)}.");
                state.CopiedBytes = checked(state.CopiedBytes + output.Length);
                state.CopiedFiles++;
                progress?.Report(new CreationWorldCopyProgress(
                    state.CopiedBytes,
                    state.TotalBytes,
                    state.CopiedFiles,
                    state.TotalFiles,
                    Path.GetRelativePath(source, file).Replace('\\', '/')));
            }
        }
    }

    private static void EnsureSameSelection(
        CreationWorldSource expected,
        CreationWorldSource actual,
        bool compareFingerprint)
    {
        var changed = expected.Kind != actual.Kind || expected.SourceSizeBytes != actual.SourceSizeBytes ||
                      expected.ExpandedSizeBytes != actual.ExpandedSizeBytes || expected.FileCount != actual.FileCount ||
                      !expected.WorldName.Equals(actual.WorldName, StringComparison.Ordinal) ||
                      !expected.MainWorldRelativePath.Equals(actual.MainWorldRelativePath, StringComparison.OrdinalIgnoreCase) ||
                      !expected.NetherWorldRelativePath.Equals(actual.NetherWorldRelativePath, StringComparison.OrdinalIgnoreCase) ||
                      !expected.EndWorldRelativePath.Equals(actual.EndWorldRelativePath, StringComparison.OrdinalIgnoreCase) ||
                      (compareFingerprint && !expected.SourceFingerprint.Equals(actual.SourceFingerprint,
                          StringComparison.OrdinalIgnoreCase));
        if (changed)
            throw new InvalidDataException("The selected world changed after review. Choose it again.");
    }

    private static string ComputeDirectoryFingerprint(string root, IEnumerable<FileInfo> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            var line = $"{Relative(root, file.FullName)}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(line));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return string.IsNullOrWhiteSpace(relative) ? "." : relative;
    }

    private static string MakeSafeWorldName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Trim().Select(character => invalid.Contains(character) ? '-' : character).ToArray())
            .TrimEnd(' ', '.');
        if (result.Length > 64) result = result[..64].TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(result) || result is "." or "..")
            result = "world";
        return result;
    }

    private static void DeleteOwnedDirectory(string path, string expectedParent, string expectedPrefix)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetFullPath(expectedParent);
        if (!CreationPathSafety.IsUnder(parent, fullPath) ||
            !Path.GetFileName(fullPath).StartsWith(expectedPrefix, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean an unowned world-import directory.");
        if (Directory.Exists(fullPath))
            Directory.Delete(fullPath, recursive: true);
    }

    private sealed class CopyState(long totalBytes, int totalFiles)
    {
        public long TotalBytes { get; } = totalBytes;
        public int TotalFiles { get; } = totalFiles;
        public long CopiedBytes { get; set; }
        public int CopiedFiles { get; set; }
    }
}
