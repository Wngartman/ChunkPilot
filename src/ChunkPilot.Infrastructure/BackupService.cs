using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class BackupService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly SafeFileService files;
    private readonly CanonicalPathLockManager pathLocks;

    public BackupService(AppDataPaths paths, ChunkPilotStore store, SafeFileService? files = null,
        CanonicalPathLockManager? pathLocks = null)
    {
        this.paths = paths;
        this.store = store;
        this.pathLocks = pathLocks ?? new CanonicalPathLockManager();
        this.files = files ?? new SafeFileService(paths, this.pathLocks);
    }

    public BackupProfile GetDefaultProfile(ServerDefinition server)
    {
        var destination = Path.Combine(paths.Backups, SanitizeFileName(server.Name));
        return new BackupProfile
        {
            // A new random profile ID on each manual backup made retention a permanent no-op.
            // Use a stable ID for future default backups; historical records remain untouched.
            Id = server.Id,
            ServerId = server.Id,
            DestinationPath = destination
        };
    }

    public Task<BackupRecord> CreateAsync(
        ServerDefinition server,
        BackupProfile profile,
        string source = "Manual",
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(server, profile, source, applyRetention: true, cancellationToken);

    public Task<BackupRecord> CreatePreRestoreRecoveryAsync(ServerDefinition server, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(server, GetDefaultProfile(server), "Pre-restore safety backup", applyRetention: false, cancellationToken);

    private async Task<BackupRecord> CreateCoreAsync(ServerDefinition server, BackupProfile profile,
        string source, bool applyRetention, CancellationToken cancellationToken)
    {
        ValidateDestination(server.RootPath, profile.DestinationPath);
        if (profile.ServerId != server.Id)
            throw new InvalidOperationException("The backup profile belongs to a different server.");
        Directory.CreateDirectory(profile.DestinationPath);
        var timer = Stopwatch.StartNew();
        var id = Guid.NewGuid();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var baseName = $"{SanitizeFileName(server.Name)}-{stamp}-{id.ToString("N")[..8]}";
        var finalPath = Path.Combine(profile.DestinationPath, baseName + ".zip");
        var temporaryPath = finalPath + ".partial";
        var manifestPath = finalPath + ".manifest.json";
        var entries = new List<BackupManifestEntry>();

        try
        {
            await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in EnumerateFilesBounded(server.RootPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(server.RootPath, file).Replace('\\', '/');
                    if (ShouldExclude(relative, profile.Exclusions))
                        continue;
                    if (relative.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    ValidateArchiveFileName(relative);
                    _ = files.ResolveWithinRoot(server.RootPath, relative, mustExist: true);
                    var (length, hash) = await CaptureFileAsync(archive, file, relative, cancellationToken).ConfigureAwait(false);
                    entries.Add(new BackupManifestEntry(relative, length, hash));
                }

                var manifest = new BackupManifest
                {
                    BackupId = id,
                    ServerId = server.Id,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ServerName = server.Name,
                    SourceRoot = server.RootPath,
                    GameKind = server.GameKind,
                    GameVersion = server.GameVersion,
                    MinecraftVersion = server.MinecraftVersion,
                    Ecosystem = server.Ecosystem.ToString(),
                    Files = entries
                };
                var manifestEntry = archive.CreateEntry(".chunkpilot/manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
                await File.WriteAllTextAsync(manifestPath,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
                    cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();

            // Verification happens while the archive is still .partial, so a backup that fails it is
            // never renamed into place and can never be offered as a restore point. Only a verified
            // archive is labelled verified; explicitly disabled verification stays unverified.
            if (profile.VerificationEnabled &&
                !await VerifyArchiveAsync(temporaryPath, id, server.Id, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "The backup archive failed verification and was not kept. Nothing in the server folder was changed.");
            File.Move(temporaryPath, finalPath);

            var verified = profile.VerificationEnabled;
            var record = new BackupRecord
            {
                Id = id,
                ServerId = server.Id,
                ProfileId = profile.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                ArchivePath = finalPath,
                ManifestPath = manifestPath,
                SizeBytes = new FileInfo(finalPath).Length,
                DurationMilliseconds = timer.ElapsedMilliseconds,
                Verified = verified,
                VerificationMessage = profile.VerificationEnabled
                    ? "Every file hash in the archive was verified before it was finalised."
                    : "Verification was disabled for this profile.",
                Source = source
            };
            await store.UpsertBackupAsync(record, cancellationToken).ConfigureAwait(false);
            if (applyRetention)
                await ApplyRetentionAsync(profile, cancellationToken).ConfigureAwait(false);
            return record;
        }
        catch
        {
            // Only the two paths this call created are touched, and a cleanup failure never replaces
            // the exception that explains what actually went wrong.
            TryDeleteFile(temporaryPath);
            if (!File.Exists(finalPath))
                TryDeleteFile(manifestPath);
            throw;
        }
    }

    public async Task<bool> VerifyAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        var verified = await VerifyArchiveAsync(record.ArchivePath, record.Id, record.ServerId, cancellationToken).ConfigureAwait(false);
        await store.UpsertBackupAsync(record with
        {
            Verified = verified,
            VerificationMessage = verified ? "Archive and every manifest hash verified." : "Archive verification failed."
        }, cancellationToken).ConfigureAwait(false);
        return verified;
    }

    public Task RestoreAsync(
        ServerDefinition server,
        BackupRecord record,
        CancellationToken cancellationToken = default)
    {
        return RestoreVerifiedAsync(server, record, cancellationToken);
    }

    public async Task DeleteAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        SafeFileService.ValidateExistingPathAncestry(record.ArchivePath);
        SafeFileService.ValidateExistingPathAncestry(record.ManifestPath);
        if (File.Exists(record.ArchivePath))
            File.Delete(record.ArchivePath);
        if (File.Exists(record.ManifestPath))
            File.Delete(record.ManifestPath);
        await store.DeleteBackupRecordAsync(record.Id, cancellationToken).ConfigureAwait(false);
    }

    public static void ValidateDestination(string sourceRoot, string destination)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        SafeFileService.ValidateExistingPathAncestry(source);
        SafeFileService.ValidateExistingPathAncestry(target);
        if (target.Equals(source, StringComparison.OrdinalIgnoreCase) || IsWithin(target, source))
            throw new InvalidOperationException("The backup destination must be outside the server folder.");
    }

    /// <summary>
    /// True when a path is excluded by the profile's patterns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pattern containing no <c>/</c> is a file-name pattern and matches at any depth, the way
    /// ignore files everywhere behave. That distinction is not cosmetic: it is what the running-server
    /// backup failure came down to. The default profile has always excluded <c>session.lock</c>, but
    /// the pattern was anchored at the server root, so <c>world/session.lock</c> was never matched.
    /// Minecraft holds an exclusive byte-range lock on that file for as long as the world is loaded,
    /// so reading it fails with "another process has locked a portion of the file" and took the whole
    /// backup with it.
    /// </para>
    /// <para>
    /// A pattern that does contain a <c>/</c> stays anchored at the server root, so <c>logs/**</c>
    /// cannot start excluding a <c>logs</c> folder inside a datapack.
    /// </para>
    /// </remarks>
    public static bool ShouldExclude(string relativePath, IReadOnlyList<string> patterns)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var fileName = normalized.Contains('/', StringComparison.Ordinal)
            ? normalized[(normalized.LastIndexOf('/') + 1)..]
            : normalized;
        foreach (var raw in patterns)
        {
            var pattern = raw.Replace('\\', '/').TrimStart('/');
            if (GlobMatches(normalized, pattern))
                return true;
            if (!pattern.Contains('/', StringComparison.Ordinal) && GlobMatches(fileName, pattern))
                return true;
        }
        return false;
    }

    private async Task ApplyRetentionAsync(BackupProfile profile, CancellationToken cancellationToken)
    {
        var records = (await store.GetBackupsAsync(profile.ServerId, cancellationToken).ConfigureAwait(false))
            .Where(record => record.ProfileId == profile.Id)
            .OrderByDescending(record => record.CreatedAt)
            .ToList();
        var retainedBytes = 0L;
        var newestVerified = records.FirstOrDefault(record => record.Verified && File.Exists(record.ArchivePath));
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            retainedBytes += record.SizeBytes;
            // Retention limits are soft when the newest/only recovery point exceeds the budget.
            if (index == 0 || record.Id == newestVerified?.Id)
                continue;
            var expired = record.CreatedAt < DateTimeOffset.UtcNow.AddDays(-Math.Max(1, profile.MaximumAgeDays));
            var overCount = index >= Math.Max(1, profile.MaximumCount);
            var overStorage = retainedBytes > Math.Max(1, profile.MaximumStorageBytes);
            if (expired || overCount || overStorage)
                await DeleteAsync(record, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies one file into the archive exactly once, and records what was actually stored.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retry is around <em>opening</em> the file, never around writing the entry. A retry that
    /// wrapped the write added a second entry under the same name every time it fired, because a
    /// <see cref="ZipArchiveMode.Create"/> archive cannot take an entry back, and a duplicate name is
    /// exactly what verification later reads. Only a sharing violation is retried, because that is the
    /// failure that genuinely clears on its own while another process finishes a write.
    /// </para>
    /// <para>
    /// The manifest records the bytes that reached the archive and the hash of those bytes, so
    /// verification compares the archive against itself and stays truthful even for a file the server
    /// appends to while the backup runs, such as the current log. World data does not move during a
    /// backup because saving is frozen and flushed first; see
    /// <c>ManagedServer.RunExclusiveDataOperationAsync</c>.
    /// </para>
    /// </remarks>
    private static async Task<(long Length, string Hash)> CaptureFileAsync(
        ZipArchive archive,
        string file,
        string relative,
        CancellationToken cancellationToken)
    {
        await using var source = await OpenForCaptureAsync(file, relative, cancellationToken).ConfigureAwait(false);
        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
        await using var target = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var captured = 0L;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                captured += read;
            }
        }
        catch (IOException exception) when (IsLockViolation(exception))
        {
            throw LockedFileFailure(relative, exception);
        }
        return (captured, Convert.ToHexString(hash.GetHashAndReset()));
    }

    /// <summary>Opens a file for capture, retrying only the failure that is genuinely transient.</summary>
    private static async Task<FileStream> OpenForCaptureAsync(
        string file,
        string relative,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            FileStream? stream = null;
            try
            {
                stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                // A byte-range lock is invisible until the range is read, so probe before an entry
                // exists. This is what turns a locked file into a clear failure with a path in it
                // rather than a half-written archive.
                await ProbeReadableAsync(stream, cancellationToken).ConfigureAwait(false);
                return stream;
            }
            catch (IOException exception) when (IsLockViolation(exception))
            {
                await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                throw LockedFileFailure(relative, exception);
            }
            catch (IOException) when (attempt < OpenAttemptLimit)
            {
                await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DisposeQuietlyAsync(stream).ConfigureAwait(false);
                throw;
            }
        }
    }

    private const int OpenAttemptLimit = 3;

    private static async Task ProbeReadableAsync(FileStream stream, CancellationToken cancellationToken)
    {
        if (stream.Length == 0)
            return;
        var probe = new byte[1];
        _ = await stream.ReadAsync(probe, cancellationToken).ConfigureAwait(false);
        stream.Position = 0;
    }

    private static async ValueTask DisposeQuietlyAsync(FileStream? stream)
    {
        if (stream is not null)
            await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>ERROR_LOCK_VIOLATION: another handle holds a byte range of this file.</summary>
    /// <remarks>
    /// Distinct from ERROR_SHARING_VIOLATION, which is a whole-file conflict that usually clears in
    /// milliseconds. A range lock is held deliberately, for as long as its owner wants it, so retrying
    /// it just delays the same failure.
    /// </remarks>
    private static bool IsLockViolation(IOException exception) =>
        exception.HResult == unchecked((int)0x80070021);

    private static IOException LockedFileFailure(string relative, IOException inner) =>
        new($"{relative} is locked by another program and could not be copied. " +
            "No backup was created, and nothing in the server folder was changed. " +
            "Close whatever is using that file, or stop the server and back up again.", inner);

    private static async Task<bool> VerifyArchiveAsync(string archivePath, Guid backupId, Guid serverId, CancellationToken cancellationToken)
    {
        try
        {
            SafeFileService.ValidateExistingPathAncestry(archivePath);
            using var archive = ZipFile.OpenRead(archivePath);
            _ = await ReadVerifiedManifestAsync(archive, backupId, serverId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesBounded(string root)
    {
        var pending = new Stack<string>();
        SafeFileService.ValidateExistingPathAncestry(root);
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (++count > 2_000_000)
                    throw new IOException("Backup aborted after reaching the two-million-file safety limit.");
                yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                SafeFileService.ValidateExistingPathAncestry(child);
                pending.Push(child);
            }
        }
    }

    private static bool GlobMatches(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool IsWithin(string path, string root)
    {
        var candidate = Path.GetFullPath(path);
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return candidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Trim();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

