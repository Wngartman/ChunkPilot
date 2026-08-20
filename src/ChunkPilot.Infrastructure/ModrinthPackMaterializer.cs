using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

namespace ChunkPilot.Infrastructure;

public sealed record ModrinthMaterializationProgress(
    int CompletedFiles,
    int TotalFiles,
    long CompletedBytes,
    long TotalBytes,
    string CurrentFile);

/// <summary>
/// Builds a new server candidate from a validated .mrpack. The destination is promoted only after
/// every indexed file and override has been written and verified; it never mutates an active server.
/// </summary>
public sealed class ModrinthServerPackMaterializer
{
    private const int BufferSize = 128 * 1024;
    private readonly ModrinthPackReader reader;

    public ModrinthServerPackMaterializer(ModrinthPackReader? reader = null) =>
        this.reader = reader ?? new ModrinthPackReader();

    public async Task<ModrinthPackMaterializationResult> MaterializeAsync(
        string archivePath,
        string destinationRoot,
        IModrinthPackDownloadSource downloadSource,
        ModrinthPackMaterializationOptions? options = null,
        IProgress<ModrinthMaterializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);
        ArgumentNullException.ThrowIfNull(downloadSource);
        options ??= new ModrinthPackMaterializationOptions();
        if (options.MaximumConcurrentDownloads <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Download concurrency must be positive.");

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"The Modrinth materialization destination already exists: {destination}");
        var parent = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("The destination must have a parent directory.", nameof(destinationRoot));
        Directory.CreateDirectory(parent);
        var staging = destination + $".mrpack-staging-{Guid.NewGuid():N}";
        Directory.CreateDirectory(staging);

        try
        {
            var pack = await reader.ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
            var materialized = new ConcurrentDictionary<string, ModrinthMaterializedFile>(StringComparer.OrdinalIgnoreCase);
            var required = pack.Manifest.Files
                .Where(file => file.ShouldInstallOnServer(options.IncludeOptionalServerFiles))
                .ToArray();
            var totalFiles = required.Length + pack.CommonOverrides.Count + pack.ServerOverrides.Count;
            var totalBytes = required.Sum(file => file.FileSize) +
                             pack.CommonOverrides.Sum(file => file.FileSize) +
                             pack.ServerOverrides.Sum(file => file.FileSize);
            var completedFiles = 0;
            long completedBytes = 0;
            progress?.Report(new(0, totalFiles, 0, totalBytes, ""));

            void FileCompleted(string relativePath, long bytes)
            {
                var count = Interlocked.Increment(ref completedFiles);
                var transferred = Interlocked.Add(ref completedBytes, bytes);
                progress?.Report(new(count, totalFiles, transferred, totalBytes, relativePath));
            }

            await Parallel.ForEachAsync(required, new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaximumConcurrentDownloads
            }, async (file, token) =>
            {
                var result = await MaterializeIndexedFileAsync(staging, file, downloadSource, token)
                    .ConfigureAwait(false);
                if (!materialized.TryAdd(file.RelativePath, result))
                    throw new InvalidDataException($"Duplicate materialized Modrinth path: {file.RelativePath}.");
                FileCompleted(file.RelativePath, result.FileSize);
            }).ConfigureAwait(false);

            await ApplyOverridesAsync(pack.ArchivePath, staging, pack.CommonOverrides, materialized,
                    FileCompleted, cancellationToken)
                .ConfigureAwait(false);
            await ApplyOverridesAsync(pack.ArchivePath, staging, pack.ServerOverrides, materialized,
                    FileCompleted, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return new ModrinthPackMaterializationResult
            {
                DestinationRoot = destination,
                Manifest = pack.Manifest,
                Files = materialized.Values.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                SkippedOptionalFiles = pack.Manifest.Files
                    .Where(file => file.ServerEnvironment == ModrinthPackEnvironmentSupport.Optional &&
                                   !options.IncludeOptionalServerFiles)
                    .Select(file => file.RelativePath).ToArray(),
                SkippedUnsupportedFiles = pack.Manifest.Files
                    .Where(file => file.ServerEnvironment == ModrinthPackEnvironmentSupport.Unsupported)
                    .Select(file => file.RelativePath).ToArray()
            };
        }
        catch
        {
            TryDeleteOwnedStaging(staging);
            throw;
        }
    }

    private static async Task<ModrinthMaterializedFile> MaterializeIndexedFileAsync(
        string staging,
        ModrinthPackFile file,
        IModrinthPackDownloadSource downloadSource,
        CancellationToken cancellationToken)
    {
        var destination = DestinationPath(staging, file.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + $".partial-{Guid.NewGuid():N}";
        Exception? lastFailure = null;
        foreach (var download in file.Downloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var input = await downloadSource.OpenReadAsync(download.Uri, cancellationToken)
                    .ConfigureAwait(false);
                var observed = await WriteAndHashAsync(input, partial, FileMode.CreateNew, file.FileSize, cancellationToken)
                    .ConfigureAwait(false);
                if (!observed.Sha1.Equals(file.Hashes.Sha1, StringComparison.OrdinalIgnoreCase) ||
                    !observed.Sha512.Equals(file.Hashes.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Hash verification failed for Modrinth pack file {file.RelativePath}.");
                File.Move(partial, destination);
                return observed with
                {
                    RelativePath = file.RelativePath,
                    SourceLayer = ModrinthPackSourceLayer.ManifestFile
                };
            }
            catch (Exception exception) when (exception is IOException or HttpRequestException or InvalidDataException)
            {
                lastFailure = exception;
                TryDeleteOwnedFile(partial);
            }
        }
        throw new InvalidDataException(
            $"None of the trusted Modrinth download locations produced a verified {file.RelativePath}.",
            lastFailure);
    }

    private static async Task ApplyOverridesAsync(
        string archivePath,
        string staging,
        IReadOnlyList<ModrinthPackOverrideEntry> entries,
        ConcurrentDictionary<string, ModrinthMaterializedFile> materialized,
        Action<string, long>? completed,
        CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
            return;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var model in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries.SingleOrDefault(candidate =>
                candidate.FullName.Equals(model.ArchivePath, StringComparison.Ordinal))
                ?? throw new InvalidDataException($"Validated Modrinth override disappeared: {model.ArchivePath}.");
            if (entry.Length != model.FileSize)
                throw new InvalidDataException($"Validated Modrinth override changed size: {model.ArchivePath}.");
            var destination = DestinationPath(staging, model.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = entry.Open();
            var observed = await WriteAndHashAsync(input, destination, FileMode.Create, model.FileSize, cancellationToken)
                .ConfigureAwait(false);
            materialized[model.RelativePath] = observed with
            {
                RelativePath = model.RelativePath,
                SourceLayer = model.Layer
            };
            completed?.Invoke(model.RelativePath, observed.FileSize);
        }
    }

    private static async Task<ModrinthMaterializedFile> WriteAndHashAsync(
        Stream input,
        string destination,
        FileMode mode,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        await using var output = new FileStream(destination, mode, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                break;
            total = checked(total + count);
            if (total > expectedSize)
                throw new InvalidDataException($"A Modrinth pack file exceeded its declared {expectedSize}-byte size.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            sha1.AppendData(buffer, 0, count);
            sha512.AppendData(buffer, 0, count);
        }
        if (total != expectedSize)
            throw new InvalidDataException($"A Modrinth pack file declared {expectedSize} bytes but supplied {total}.");
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ModrinthMaterializedFile
        {
            FileSize = total,
            Sha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Sha512 = Convert.ToHexString(sha512.GetHashAndReset()).ToLowerInvariant()
        };
    }

    private static string DestinationPath(string root, string relativePath)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var destination = Path.GetFullPath(Path.Combine(canonicalRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Modrinth pack path escapes materialization staging: {relativePath}.");
        return destination;
    }

    private static void TryDeleteOwnedFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // The isolated staging directory cleanup gets another bounded attempt.
        }
        catch (UnauthorizedAccessException)
        {
            // The isolated staging directory cleanup gets another bounded attempt.
        }
    }

    private static void TryDeleteOwnedStaging(string path)
    {
        Debug.Assert(Path.GetFileName(path).Contains(".mrpack-staging-", StringComparison.Ordinal));
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // The caller receives the original failure; this exact operation-owned path is never activated.
        }
        catch (UnauthorizedAccessException)
        {
            // The caller receives the original failure; this exact operation-owned path is never activated.
        }
    }
}
