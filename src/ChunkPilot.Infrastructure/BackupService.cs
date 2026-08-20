using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class BackupService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;

    public BackupService(AppDataPaths paths, ChunkPilotStore store)
    {
        this.paths = paths;
        this.store = store;
    }

    public BackupProfile GetDefaultProfile(ServerDefinition server)
    {
        var destination = Path.Combine(paths.Backups, SanitizeFileName(server.Name));
        return new BackupProfile
        {
            ServerId = server.Id,
            DestinationPath = destination
        };
    }

    public async Task<BackupRecord> CreateAsync(
        ServerDefinition server,
        BackupProfile profile,
        string source = "Manual",
        CancellationToken cancellationToken = default)
    {
        ValidateDestination(server.RootPath, profile.DestinationPath);
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
                    if (ShouldExclude(relative, profile.Exclusions) || IsWithin(file, profile.DestinationPath))
                        continue;
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
            // archive is finalised, and only then is a record written.
            if (profile.VerificationEnabled &&
                !await VerifyArchiveAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException(
                    "The backup archive failed verification and was not kept. Nothing in the server folder was changed.");
            File.Move(temporaryPath, finalPath);

            const bool verified = true;
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
        var verified = await VerifyArchiveAsync(record.ArchivePath, cancellationToken).ConfigureAwait(false);
        await store.UpsertBackupAsync(record with
        {
            Verified = verified,
            VerificationMessage = verified ? "Archive and every manifest hash verified." : "Archive verification failed."
        }, cancellationToken).ConfigureAwait(false);
        return verified;
    }

    public async Task RestoreAsync(
        ServerDefinition server,
        BackupRecord record,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(record.ArchivePath))
            throw new FileNotFoundException("Backup archive was not found.", record.ArchivePath);
        if (!await VerifyArchiveAsync(record.ArchivePath, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The backup failed verification and will not be restored.");

        var staging = Path.Combine(Path.GetDirectoryName(server.RootPath)!, $".chunkpilot-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(record.ArchivePath);
            foreach (var entry in archive.Entries.Where(entry => !entry.FullName.StartsWith(".chunkpilot/", StringComparison.Ordinal)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = Path.GetFullPath(Path.Combine(staging, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                var stagingPrefix = Path.TrimEndingDirectorySeparator(staging) + Path.DirectorySeparatorChar;
                if (!output.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Unsafe archive entry: {entry.FullName}");
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await using var source = entry.Open();
                await using var destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }

            foreach (var stagedFile in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(staging, stagedFile);
                var target = Path.GetFullPath(Path.Combine(server.RootPath, relative));
                if (!IsWithin(target, server.RootPath))
                    throw new InvalidDataException($"Restore path escaped the server root: {relative}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + $".chunkpilot-{Guid.NewGuid():N}.tmp";
                File.Copy(stagedFile, temporary);
                if (File.Exists(target))
                    File.Replace(temporary, target, null, ignoreMetadataErrors: true);
                else
                    File.Move(temporary, target);
            }
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public async Task DeleteAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
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
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            retainedBytes += record.SizeBytes;
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

    private static async Task<bool> VerifyArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            var manifestEntry = archive.GetEntry(".chunkpilot/manifest.json");
            if (manifestEntry is null)
                return false;
            BackupManifest? manifest;
            await using (var stream = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
                return false;
            foreach (var expected in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.GetEntry(expected.RelativePath);
                if (entry is null || entry.Length != expected.SizeBytes)
                    return false;
                await using var stream = entry.Open();
                using var sha = SHA256.Create();
                var actual = Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateFilesBounded(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var count = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }
            foreach (var file in files)
            {
                if (++count > 2_000_000)
                    throw new IOException("Backup aborted after reaching the two-million-file safety limit.");
                yield return file;
            }
            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory); }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { continue; }
            foreach (var child in children)
            {
                if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
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

