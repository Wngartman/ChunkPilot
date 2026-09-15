using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class BackupService
{
    private const string ManifestEntryName = ".chunkpilot/manifest.json";
    private const int MaximumManifestBytes = 32 * 1024 * 1024;
    private const int MaximumBackupFiles = 2_000_000;

    private static async Task<BackupManifest> ReadVerifiedManifestAsync(
        ZipArchive archive, Guid backupId, Guid serverId, CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > MaximumBackupFiles + 1)
            throw new InvalidDataException("The backup exceeds the supported file count.");
        var inventory = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateArchiveFileName(entry.FullName);
            if (!inventory.TryAdd(entry.FullName, entry))
                throw new InvalidDataException("The backup contains duplicate or aliased file paths.");
        }
        if (!inventory.TryGetValue(ManifestEntryName, out var manifestEntry) ||
            manifestEntry.FullName != ManifestEntryName || manifestEntry.Length > MaximumManifestBytes)
            throw new InvalidDataException("The backup manifest is missing or exceeds the safe size limit.");

        BackupManifest? manifest;
        try
        {
            await using var stream = manifestEntry.Open();
            manifest = await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ProtocolJson.Options,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The backup manifest is malformed.", exception);
        }
        if (manifest is null || manifest.BackupId != backupId || manifest.ServerId != serverId ||
            manifest.Files is null || manifest.Files.Count != inventory.Count - 1)
            throw new InvalidDataException("The backup identity or complete file inventory does not match its record.");

        var expectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expected in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expected is null)
                throw new InvalidDataException("The backup contains an invalid manifest entry.");
            ValidateArchiveFileName(expected.RelativePath);
            if (expected.RelativePath.Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase) ||
                !expectedNames.Add(expected.RelativePath) || !inventory.TryGetValue(expected.RelativePath, out var entry) ||
                expected.RelativePath != entry.FullName || expected.SizeBytes < 0 || entry.Length != expected.SizeBytes ||
                expected.Sha256 is not { Length: 64 } || expected.Sha256.Any(character => !Uri.IsHexDigit(character)))
                throw new InvalidDataException("The backup manifest does not describe exactly the files in the archive.");
            // A file must not also be another file's parent directory on Windows.
            for (var slash = expected.RelativePath.LastIndexOf('/'); slash >= 0;
                 slash = expected.RelativePath.LastIndexOf('/', slash - 1))
            {
                if (inventory.ContainsKey(expected.RelativePath[..slash]))
                    throw new InvalidDataException("The backup contains conflicting file and directory paths.");
                if (slash == 0)
                    break;
            }
            await using var stream = entry.Open();
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!actual.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The backup contains a file whose hash does not match its manifest.");
        }
        return manifest;
    }

    private static void ValidateArchiveFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000 || path.Contains('\\') || Path.IsPathRooted(path))
            throw new InvalidDataException("The backup contains an unsafe file path.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character, StringComparison.Ordinal)))
                throw new InvalidDataException("The backup contains an unsafe Windows file path.");
            var deviceName = segment.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                (deviceName.Length == 4 && deviceName[3] is >= '1' and <= '9' &&
                 (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("The backup contains a reserved Windows device name.");
        }
    }

    private async Task RestoreVerifiedAsync(ServerDefinition server, BackupRecord record, CancellationToken cancellationToken)
    {
        if (server.Id != record.ServerId)
            throw new InvalidOperationException("The selected backup belongs to a different server.");
        _ = files.ResolveWithinRoot(server.RootPath, "", mustExist: true);
        SafeFileService.ValidateExistingPathAncestry(record.ArchivePath);
        // Verification and extraction use one held handle. A separately reopened archive could be
        // replaced between those steps; FileShare.Read prevents both replacement and external writes.
        await using var archiveStream = new FileStream(record.ArchivePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = await ReadVerifiedManifestAsync(archive, record.Id, server.Id, cancellationToken).ConfigureAwait(false);
        var planned = manifest.Files.Where(entry => !entry.RelativePath.StartsWith(".chunkpilot/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new RestoreFile(entry, files.ResolveWithinRoot(server.RootPath, entry.RelativePath, false)))
            .ToArray();
        // Resolve ALL destinations before extraction or mutation, including currently nonexistent
        // descendants of junctions. Do not change the first file before finding the last unsafe one.
        foreach (var item in planned)
        {
            if (Directory.Exists(item.Target))
                throw new IOException("A backup file conflicts with a directory in the server folder.");
        }
        var staging = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(server.RootPath))!, $".chunkpilot-restore-{Guid.NewGuid():N}");
        SafeFileService.ValidateExistingPathAncestry(staging);
        Directory.CreateDirectory(staging);
        var preserveRecovery = false;
        var activated = new List<RestoreFile>();
        try
        {
            for (var index = 0; index < planned.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = planned[index];
                item.Staged = Path.Combine(staging, $"incoming-{index}");
                item.Recovery = Path.Combine(staging, $"original-{index}");
                await using (var source = archive.GetEntry(item.Entry.RelativePath)!.Open())
                await using (var target = new FileStream(item.Staged, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous))
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                _ = files.ResolveWithinRoot(server.RootPath, item.Entry.RelativePath, false);
                item.Existed = File.Exists(item.Target);
                if (item.Existed)
                {
                    // Capture all originals before activation. A locked/unreadable file fails while
                    // the server is still untouched; the held source forbids writes during capture.
                    await using var original = new FileStream(item.Target, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous);
                    await using var recovery = new FileStream(item.Recovery, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        128 * 1024, FileOptions.Asynchronous);
                    item.OriginalSha256 = Convert.ToHexString(await SHA256.HashDataAsync(original, cancellationToken).ConfigureAwait(false));
                    original.Position = 0;
                    await original.CopyToAsync(recovery, cancellationToken).ConfigureAwait(false);
                }
            }
            foreach (var item in planned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var pathLock = await pathLocks.AcquireAsync(item.Target, cancellationToken).ConfigureAwait(false);
                _ = files.ResolveWithinRoot(server.RootPath, item.Entry.RelativePath, false);
                await EnsureFileUnchangedAsync(item.Target, item.Existed, item.OriginalSha256, cancellationToken).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
                await ReplaceFromStagingAsync(server.RootPath, item.Entry.RelativePath, item.Staged,
                    item.Existed, item.OriginalSha256, cancellationToken).ConfigureAwait(false);
                activated.Add(item);
            }
        }
        catch (Exception failure)
        {
            List<Exception> rollbackFailures = [];
            foreach (var item in activated.AsEnumerable().Reverse())
            {
                try
                {
                    await using var pathLock = await pathLocks.AcquireAsync(item.Target, CancellationToken.None).ConfigureAwait(false);
                    _ = files.ResolveWithinRoot(server.RootPath, item.Entry.RelativePath, false);
                    await EnsureFileUnchangedAsync(item.Target, true, item.Entry.Sha256, CancellationToken.None).ConfigureAwait(false);
                    if (item.Existed)
                        await ReplaceFromStagingAsync(server.RootPath, item.Entry.RelativePath, item.Recovery,
                            true, item.Entry.Sha256, CancellationToken.None).ConfigureAwait(false);
                    else
                        File.Delete(item.Target);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    rollbackFailures.Add(exception);
                }
            }
            if (rollbackFailures.Count != 0)
            {
                preserveRecovery = true;
                throw new IOException($"Restore failed and some files changed externally or remain locked. Original recovery files were retained at {staging}.",
                    new AggregateException(new[] { failure }.Concat(rollbackFailures)));
            }
            throw;
        }
        finally
        {
            if (!preserveRecovery)
                TryDeleteDirectory(staging);
        }
    }

    private async Task ReplaceFromStagingAsync(string root, string relative, string source, bool expectedExists,
        string expectedHash, CancellationToken cancellationToken)
    {
        var target = files.ResolveWithinRoot(root, relative, false);
        var temporary = target + $".chunkpilot-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _ = files.ResolveWithinRoot(root, relative, false);
            await EnsureFileUnchangedAsync(target, expectedExists, expectedHash, cancellationToken).ConfigureAwait(false);
            if (expectedExists)
                File.Replace(temporary, target, null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, target);
        }
        finally { TryDeleteFile(temporary); }
    }

    private static async Task EnsureFileUnchangedAsync(string path, bool expectedExists, string expectedHash, CancellationToken cancellationToken)
    {
        if (File.Exists(path) != expectedExists || Directory.Exists(path))
            throw new IOException("A restore destination changed externally. No external changes will be overwritten.");
        if (!expectedExists)
            return;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A restore destination changed externally. No external changes will be overwritten.");
    }

    private sealed class RestoreFile(BackupManifestEntry entry, string target)
    {
        public BackupManifestEntry Entry { get; } = entry;
        public string Target { get; } = target;
        public string Staged { get; set; } = "";
        public string Recovery { get; set; } = "";
        public bool Existed { get; set; }
        public string OriginalSha256 { get; set; } = "";
    }
}
