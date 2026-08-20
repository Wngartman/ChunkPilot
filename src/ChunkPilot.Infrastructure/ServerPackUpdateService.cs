using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class MigrationReviewRequiredException : InvalidOperationException
{
    public MigrationReviewRequiredException(MigrationPlan plan)
        : base("Migration conflicts require review before installation: " +
               string.Join(" | ", plan.Conflicts.Take(20)))
    {
        Plan = plan;
    }

    public MigrationPlan Plan { get; }
}

public sealed class PackUpdateCompatibilityService
{
    public UpdateCheckResult Evaluate(
        ServerDefinition server,
        UpdateSource source,
        PackVersionInfo? installed,
        IReadOnlyList<PackVersionInfo> available,
        DateTimeOffset checkedAt)
    {
        if (!source.HasIdentifiedBaseline)
            return Result(ServerUpdateStatus.SourceNotLinked, UpdateCompatibility.ManualReviewRequired,
                ["Identify the currently installed pack version before updates can be offered."], null);
        var ordered = available.OrderByDescending(version => version.PublishedAt).ToArray();
        var latest = ordered.FirstOrDefault();
        if (latest is null)
            return Result(ServerUpdateStatus.CheckUnavailable, UpdateCompatibility.Unknown,
                ["The provider returned no versions compatible with the linked filters."], null);
        var installedProviderVersion = ordered.FirstOrDefault(version =>
            version.VersionId.Equals(source.InstalledVersionId, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(source.InstalledVersionName) &&
            version.VersionName.Equals(source.InstalledVersionName, StringComparison.OrdinalIgnoreCase));
        if (installedProviderVersion is not null &&
            latest.PublishedAt <= installedProviderVersion.PublishedAt)
            return Result(ServerUpdateStatus.UpToDate, UpdateCompatibility.Compatible,
                ["The installed baseline matches the latest compatible provider version."], latest);

        var reasons = new List<string>();
        var compatibility = UpdateCompatibility.Compatible;
        if (installedProviderVersion is null)
        {
            compatibility = UpdateCompatibility.ManualReviewRequired;
            reasons.Add(
                $"Installed provider version '{source.InstalledVersionId}' was not present in the returned release history; confirm the upgrade path manually.");
        }
        if (!string.IsNullOrWhiteSpace(latest.PackId) &&
            !string.IsNullOrWhiteSpace(source.ProjectId) &&
            !latest.PackId.Equals(source.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            compatibility = UpdateCompatibility.Incompatible;
            reasons.Add($"Provider pack ID '{latest.PackId}' does not match linked project '{source.ProjectId}'.");
        }
        if (!string.IsNullOrWhiteSpace(latest.MinecraftVersion) &&
            !latest.MinecraftVersion.Equals(server.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
        {
            compatibility = Max(compatibility, UpdateCompatibility.CompatibleWithMigrationWarning);
            reasons.Add($"Minecraft changes from {server.MinecraftVersion} to {latest.MinecraftVersion}; world compatibility requires review.");
        }
        if (!string.IsNullOrWhiteSpace(latest.Loader) &&
            !latest.Loader.Equals(server.Ecosystem.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            compatibility = Max(compatibility, UpdateCompatibility.CompatibleWithMigrationWarning);
            reasons.Add($"Loader changes from {server.Ecosystem} to {latest.Loader}.");
        }
        if (!string.IsNullOrWhiteSpace(latest.LoaderVersion) &&
            !latest.LoaderVersion.Equals(server.LoaderVersion, StringComparison.OrdinalIgnoreCase))
        {
            compatibility = Max(compatibility, UpdateCompatibility.CompatibleWithMigrationWarning);
            reasons.Add($"Loader version changes from {server.LoaderVersion} to {latest.LoaderVersion}.");
        }
        if (latest.RequiredJavaMajor > 0)
            reasons.Add($"Target declares Java {latest.RequiredJavaMajor} or newer.");
        if (!string.IsNullOrWhiteSpace(latest.MigrationNotes))
        {
            compatibility = Max(compatibility, UpdateCompatibility.CompatibleWithMigrationWarning);
            reasons.Add("The publisher supplied migration notes that require review.");
        }
        if (reasons.Count == 0)
            reasons.Add("Pack identity, Minecraft version, and loader match the linked baseline.");
        return Result(ServerUpdateStatus.UpdateAvailable, compatibility, reasons, latest);

        UpdateCheckResult Result(
            ServerUpdateStatus status,
            UpdateCompatibility state,
            IReadOnlyList<string> resultReasons,
            PackVersionInfo? latestVersion) =>
            new()
            {
                ServerId = server.Id,
                Status = status,
                CheckedAt = checkedAt,
                Source = source,
                InstalledVersion = installed,
                LatestVersion = latestVersion,
                Compatibility = state,
                CompatibilityReasons = resultReasons,
                Message = status switch
                {
                    ServerUpdateStatus.UpToDate => "This server is up to date.",
                    ServerUpdateStatus.UpdateAvailable => $"Update {latestVersion?.VersionName} is available.",
                    ServerUpdateStatus.SourceNotLinked => "The installed baseline is not identified.",
                    _ => "Update check did not return an installable version."
                }
            };
    }

    private static UpdateCompatibility Max(UpdateCompatibility current, UpdateCompatibility candidate)
    {
        static int Rank(UpdateCompatibility state) => state switch
        {
            UpdateCompatibility.Compatible => 0,
            UpdateCompatibility.CompatibleWithMigrationWarning => 1,
            UpdateCompatibility.ManualReviewRequired => 2,
            UpdateCompatibility.Unknown => 3,
            UpdateCompatibility.Incompatible => 4,
            _ => 3
        };
        return Rank(candidate) > Rank(current) ? candidate : current;
    }
}

public static class PersistentDataClassifier
{
    private static readonly HashSet<string> PersistentFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "server.properties", "whitelist.json", "ops.json", "banned-players.json", "banned-ips.json",
        "server-icon.png", "user_jvm_args.txt", "eula.txt"
    };

    private static readonly string[] PackManagedPrefixes =
    [
        "mods/", "plugins/", "libraries/", "scripts/", "defaultconfigs/"
    ];

    public static FileOwnership Classify(
        string relativePath,
        IReadOnlyCollection<string> worldRoots,
        IReadOnlyCollection<string>? explicitPersistent = null)
    {
        var normalized = Normalize(relativePath);
        if (PersistentFiles.Contains(normalized))
            return FileOwnership.Persistent;
        if (explicitPersistent?.Any(path =>
                normalized.Equals(Normalize(path), StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(Normalize(path).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)) == true)
            return FileOwnership.Persistent;
        if (worldRoots.Any(world => normalized.Equals(Normalize(world), StringComparison.OrdinalIgnoreCase) ||
                                    normalized.StartsWith(Normalize(world).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
            return FileOwnership.Persistent;
        if (PackManagedPrefixes.Any(prefix => normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return FileOwnership.PackManaged;
        if (normalized.StartsWith("config/", StringComparison.OrdinalIgnoreCase))
            return FileOwnership.Unknown;
        return FileOwnership.UserAdded;
    }

    public static string Normalize(string value) => value.Replace('\\', '/').TrimStart('/');
}

public sealed class PackMigrationPlanner
{
    public async Task<MigrationPlan> BuildAndApplyAsync(
        string currentRoot,
        string candidateRoot,
        IReadOnlyCollection<string> worldRoots,
        IReadOnlyDictionary<string, MigrationResolution>? resolutions = null,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<PackFileChange>();
        var persistent = new List<string>();
        var conflicts = new List<string>();
        var explicitPersistent = ReadExplicitPersistentPaths(currentRoot);
        var candidateFiles = Enumerate(candidateRoot).ToDictionary(
            path => PersistentDataClassifier.Normalize(Path.GetRelativePath(candidateRoot, path)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var oldFile in Enumerate(currentRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = PersistentDataClassifier.Normalize(Path.GetRelativePath(currentRoot, oldFile));
            var ownership = PersistentDataClassifier.Classify(relative, worldRoots, explicitPersistent);
            MigrationResolution? resolution = null;
            if (resolutions is not null)
                resolutions.TryGetValue(relative, out resolution);
            candidateFiles.TryGetValue(relative, out var newFile);
            var oldHash = await Sha256Async(oldFile, cancellationToken).ConfigureAwait(false);
            var newHash = newFile is null ? "" : await Sha256Async(newFile, cancellationToken).ConfigureAwait(false);

            if (ownership == FileOwnership.Persistent)
            {
                await CopyAsync(oldFile, Path.Combine(candidateRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                    cancellationToken).ConfigureAwait(false);
                persistent.Add(relative);
                changes.Add(new PackFileChange
                {
                    RelativePath = relative,
                    Ownership = ownership,
                    Change = newFile is null ? "Preserved" : oldHash == newHash ? "Unchanged" : "Preserved user state",
                    Reason = "World/player/server state is persistent across pack versions.",
                    OldSha256 = oldHash,
                    NewSha256 = newHash
                });
                continue;
            }

            if (newFile is not null)
            {
                if (oldHash != newHash)
                {
                    var change = "Replaced by new pack baseline";
                    var effectiveNewHash = newHash;
                    var reason = ownership == FileOwnership.Unknown
                        ? "Both versions contain this file and no old pack baseline exists for an automatic merge."
                        : "Pack-managed files follow the target server-pack baseline.";
                    if (ownership == FileOwnership.Unknown && resolution is not null)
                    {
                        switch (resolution.Kind)
                        {
                            case MigrationResolutionKind.KeepOld:
                                await CopyAsync(oldFile, newFile, cancellationToken).ConfigureAwait(false);
                                change = "Kept old value by user decision";
                                reason = "The user selected the installed value for this conflict.";
                                effectiveNewHash = oldHash;
                                break;
                            case MigrationResolutionKind.UseMergedText:
                                if (!IsMergeableText(relative, oldFile, newFile))
                                    throw new InvalidDataException(
                                        $"{relative} is not a bounded text file and cannot use a merged-text decision.");
                                await File.WriteAllTextAsync(newFile, resolution.MergedContent,
                                    new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                                change = "Applied user-provided merged text";
                                reason = "The user supplied the complete resolved text for this conflict.";
                                effectiveNewHash = await Sha256Async(newFile, cancellationToken).ConfigureAwait(false);
                                break;
                            case MigrationResolutionKind.NewBaseline:
                                change = "Selected new pack baseline";
                                reason = "The user selected the target pack value for this conflict.";
                                break;
                        }
                    }
                    changes.Add(new PackFileChange
                    {
                        RelativePath = relative,
                        Ownership = ownership,
                        Change = change,
                        Reason = reason,
                        OldSha256 = oldHash,
                        NewSha256 = effectiveNewHash
                    });
                }
                if (ownership == FileOwnership.Unknown && oldHash != newHash && resolution is null)
                    conflicts.Add($"{relative}: old and new pack versions differ; the new baseline is selected and the old file remains in the rollback snapshot.");
                continue;
            }

            if (ownership == FileOwnership.PackManaged)
            {
                if (resolution?.Kind == MigrationResolutionKind.KeepOld)
                {
                    await CopyAsync(oldFile,
                        Path.Combine(candidateRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                        cancellationToken).ConfigureAwait(false);
                    changes.Add(new PackFileChange
                    {
                        RelativePath = relative,
                        Ownership = ownership,
                        Change = "Preserved by explicit user decision",
                        Reason = "The target pack removed this file, but the user chose the installed copy.",
                        OldSha256 = oldHash
                    });
                    continue;
                }
                if (resolution?.Kind == MigrationResolutionKind.UseMergedText)
                    throw new InvalidDataException($"{relative} was removed by the new pack and cannot be text-merged.");
                changes.Add(new PackFileChange
                {
                    RelativePath = relative,
                    Ownership = ownership,
                    Change = "Removed from active pack",
                    Reason = "The target pack does not contain this pack-managed file. It remains recoverable in the snapshot.",
                    OldSha256 = oldHash
                });
                if (relative.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && resolution is null)
                    conflicts.Add($"{relative}: removed JAR will not be copied into the new active pack.");
                continue;
            }

            await CopyAsync(oldFile, Path.Combine(candidateRoot, relative.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken).ConfigureAwait(false);
            changes.Add(new PackFileChange
            {
                RelativePath = relative,
                Ownership = FileOwnership.UserAdded,
                Change = "Preserved user-added file",
                Reason = "The file is outside recognized pack-managed locations and absent from the target package.",
                OldSha256 = oldHash
            });
        }

        foreach (var pair in candidateFiles)
        {
            var old = Path.Combine(currentRoot, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(old))
                changes.Add(new PackFileChange
                {
                    RelativePath = pair.Key,
                    Ownership = PersistentDataClassifier.Classify(pair.Key, worldRoots, explicitPersistent),
                    Change = "Added by new pack",
                    Reason = "The target server pack introduced this file.",
                    NewSha256 = await Sha256Async(pair.Value, cancellationToken).ConfigureAwait(false)
                });
        }

        return new MigrationPlan
        {
            Changes = changes.OrderBy(change => change.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
            PersistentPaths = persistent.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            Conflicts = conflicts
        };
    }

    private static IEnumerable<string> Enumerate(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint));

    private static IReadOnlyCollection<string> ReadExplicitPersistentPaths(string root)
    {
        var path = Path.Combine(root, ".chunkpilot", "persistent-paths.json");
        if (!File.Exists(path))
            return [];
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path), ProtocolJson.Options) ?? [];
            return values.Select(PersistentDataClassifier.Normalize)
                .Where(value => value.Length > 0 &&
                                !value.Equals("..", StringComparison.Ordinal) &&
                                !value.StartsWith("../", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Persistent-path metadata is invalid: {path}", exception);
        }
    }

    private static bool IsMergeableText(string relativePath, string oldFile, string newFile)
    {
        var extension = Path.GetExtension(relativePath);
        var textExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".properties", ".json", ".json5", ".toml", ".yaml", ".yml", ".cfg", ".conf", ".ini", ".xml"
        };
        return textExtensions.Contains(extension) &&
               new FileInfo(oldFile).Length <= 1024 * 1024 &&
               new FileInfo(newFile).Length <= 1024 * 1024;
    }

    private static async Task CopyAsync(string source, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }
}

public sealed class VersionSnapshotService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;

    public VersionSnapshotService(AppDataPaths paths, ChunkPilotStore store)
    {
        this.paths = paths;
        this.store = store;
    }

    public async Task<VersionSnapshot> CreateAsync(
        ServerDefinition server,
        UpdateSource source,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var directory = Path.Combine(paths.VersionSnapshots, server.Id.ToString("D"));
        Directory.CreateDirectory(directory);
        var baseName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Safe(source.InstalledVersionName, "installed")}-{id:N}";
        var temporary = Path.Combine(directory, baseName + ".zip.partial");
        var archivePath = Path.Combine(directory, baseName + ".zip");
        var manifestPath = archivePath + ".manifest.json";
        var entries = new List<BackupManifestEntry>();
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var file in Directory.EnumerateFiles(server.RootPath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException($"Version snapshots refuse reparse-point files: {file}");
                    var relative = PersistentDataClassifier.Normalize(Path.GetRelativePath(server.RootPath, file));
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var destination = entry.Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[128 * 1024];
                    long length = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        hash.AppendData(buffer, 0, read);
                        length += read;
                    }
                    entries.Add(new BackupManifestEntry(relative, length,
                        Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
                }
                var manifest = new VersionSnapshotManifest
                {
                    SnapshotId = id,
                    ServerId = server.Id,
                    VersionId = source.InstalledVersionId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    IncludesWorldData = true,
                    Files = entries
                };
                var manifestEntry = archive.CreateEntry(
                    $".chunkpilot/version-manifest-{id:N}.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, ProtocolJson.Options, cancellationToken)
                    .ConfigureAwait(false);
                await File.WriteAllTextAsync(manifestPath,
                    JsonSerializer.Serialize(manifest, new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
                    cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Close();
            File.Move(temporary, archivePath);
            var verified = await VerifyAsync(archivePath, cancellationToken).ConfigureAwait(false);
            if (!verified)
                throw new InvalidDataException("The pre-update version snapshot failed verification.");
            var snapshot = new VersionSnapshot
            {
                Id = id,
                ServerId = server.Id,
                VersionId = source.InstalledVersionId,
                VersionName = string.IsNullOrWhiteSpace(source.InstalledVersionName)
                    ? source.InstalledVersionId : source.InstalledVersionName,
                InstalledAt = source.InstalledAt ?? server.ImportedAt,
                SourceProvider = source.Provider,
                Source = source.SourceUrl,
                MinecraftVersion = server.MinecraftVersion,
                Loader = server.Ecosystem.ToString(),
                LoaderVersion = server.LoaderVersion,
                SnapshotPath = archivePath,
                ManifestPath = manifestPath,
                SnapshotSize = new FileInfo(archivePath).Length,
                IncludesWorldData = true,
                Verified = true,
                Health = VersionHealth.Healthy,
                UpdateNotes = reason,
                Definition = server
            };
            await store.UpsertVersionSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return snapshot;
        }
        catch
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
            if (!File.Exists(archivePath) && File.Exists(manifestPath))
                File.Delete(manifestPath);
            throw;
        }
    }

    public static async Task<bool> VerifyAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
            return false;
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.Entries.LastOrDefault(entry => IsInternalManifest(entry.FullName));
        if (manifestEntry is null)
            return false;
        VersionSnapshotManifest? manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<VersionSnapshotManifest>(
                stream, ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
            return false;
        foreach (var expected in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(expected.RelativePath);
            if (entry is null || entry.Length != expected.SizeBytes)
                return false;
            await using var stream = entry.Open();
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!hash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    public static async Task ExtractVerifiedAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken = default)
    {
        if (!await VerifyAsync(archivePath, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The selected version snapshot failed verification.");
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        var internalManifest = archive.Entries.LastOrDefault(entry => IsInternalManifest(entry.FullName));
        foreach (var entry in archive.Entries.Where(entry => !ReferenceEquals(entry, internalManifest)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = Path.GetFullPath(Path.Combine(destination,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Snapshot entry escapes the destination: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(output);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using var source = entry.Open();
            await using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<bool> VerifyExtractedAsync(
        string archivePath,
        string directory,
        CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var manifestEntry = archive.Entries.LastOrDefault(entry => IsInternalManifest(entry.FullName));
        if (manifestEntry is null)
            return false;
        VersionSnapshotManifest? manifest;
        await using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<VersionSnapshotManifest>(
                stream, ProtocolJson.Options, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
            return false;
        foreach (var expected in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(directory,
                expected.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || new FileInfo(path).Length != expected.SizeBytes)
                return false;
            if (!string.Equals(
                    await PackMigrationPlanner.Sha256Async(path, cancellationToken).ConfigureAwait(false),
                    expected.Sha256, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static bool IsInternalManifest(string entryName) =>
        entryName.StartsWith(".chunkpilot/version-manifest-", StringComparison.OrdinalIgnoreCase) &&
        entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public async Task DeleteAsync(
        Guid serverId,
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var versions = await store.GetVersionSnapshotsAsync(serverId, cancellationToken).ConfigureAwait(false);
        var candidate = versions.FirstOrDefault(version => version.Id == snapshotId)
                        ?? throw new KeyNotFoundException("Version snapshot was not found.");
        if (!UpdatePolicy.CanDeleteSnapshot(candidate, versions, out var reason))
            throw new InvalidOperationException(reason);
        var snapshotRoot = Path.GetFullPath(Path.Combine(paths.VersionSnapshots, serverId.ToString("D")));
        var recovery = Path.Combine(paths.Recovery, "DeletedVersionSnapshots", serverId.ToString("D"),
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{snapshotId:N}");
        MoveContainedFileToRecovery(snapshotRoot, candidate.SnapshotPath, recovery);
        MoveContainedFileToRecovery(snapshotRoot, candidate.ManifestPath, recovery);
        await store.DeleteVersionSnapshotRecordAsync(snapshotId, cancellationToken).ConfigureAwait(false);
    }

    private static void MoveContainedFileToRecovery(string root, string path, string recovery)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;
        var full = Path.GetFullPath(path);
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Refusing to delete a version file outside the snapshot directory: {full}");
        Directory.CreateDirectory(recovery);
        File.Move(full, Path.Combine(recovery, Path.GetFileName(full)));
    }

    private static string Safe(string value, string fallback)
    {
        var cleaned = new string((string.IsNullOrWhiteSpace(value) ? fallback : value)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character).ToArray());
        return cleaned.Length > 48 ? cleaned[..48] : cleaned;
    }
}

public sealed record PreparedPackUpdate(
    UpdateExecutionResult Result,
    string RollbackSnapshotPath,
    string FailedCandidatePath,
    string PreviousDirectoryPath);

public sealed class ServerPackUpdateService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly VersionSnapshotService snapshots;
    private readonly PackMigrationPlanner migration;
    private readonly ServerDetectionService detection;
    private readonly WorldManager worlds;
    private readonly LoaderInstallationService loaderInstaller;
    private readonly ModrinthPackServerService modrinthPacks;
    private readonly ManagedJavaRuntimeService? managedJava;
    private readonly HttpClient http;

    public ServerPackUpdateService(
        AppDataPaths paths,
        ChunkPilotStore store,
        VersionSnapshotService snapshots,
        PackMigrationPlanner migration,
        ServerDetectionService detection,
        WorldManager worlds,
        LoaderInstallationService? loaderInstaller = null,
        HttpClient? client = null,
        ManagedJavaRuntimeService? managedJava = null)
    {
        this.paths = paths;
        this.store = store;
        this.snapshots = snapshots;
        this.migration = migration;
        this.detection = detection;
        this.worlds = worlds;
        this.loaderInstaller = loaderInstaller ?? new LoaderInstallationService(new LoaderMetadataService());
        this.managedJava = managedJava;
        modrinthPacks = new ModrinthPackServerService(loaderInstaller: this.loaderInstaller);
        http = client ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0 (local Windows server manager)");
    }

    public async Task<UpdateExecutionResult> DownloadAndVerifyOnlyAsync(
        ServerDefinition server,
        UpdateSource source,
        UpdateInstallRequest request,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(server, source, request);
        var target = request.TargetVersion;
        var download = CachePath(target);
        var logPath = Path.Combine(paths.Staging, $"update-{request.OperationId:N}.log");
        progress?.Report(new UpdateProgress
        {
            OperationId = request.OperationId,
            State = UpdateOperationState.Downloading,
            CurrentStep = "Downloading target server pack",
            Percent = 10,
            Detail = target.DownloadUrl,
            LogPath = logPath
        });
        await DownloadAsync(target, download, progress, request.OperationId, logPath, cancellationToken)
            .ConfigureAwait(false);
        progress?.Report(new UpdateProgress
        {
            OperationId = request.OperationId,
            State = UpdateOperationState.Verifying,
            CurrentStep = "Verifying provider hash",
            Percent = 80,
            Detail = target.FileName,
            LogPath = logPath
        });
        try
        {
            await VerifyDownloadAsync(download, target, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            var rejectedSha = await PackMigrationPlanner.Sha256Async(download, CancellationToken.None)
                .ConfigureAwait(false);
            await store.RecordUpdateDownloadAsync(request.OperationId, server.Id, source.Provider, target,
                new FileInfo(download).Length, rejectedSha, "Rejected: provider verification failed",
                CancellationToken.None).ConfigureAwait(false);
            File.Delete(download);
            throw;
        }
        var sha256 = await PackMigrationPlanner.Sha256Async(download, cancellationToken).ConfigureAwait(false);
        await store.RecordUpdateDownloadAsync(request.OperationId, server.Id, source.Provider, target,
            new FileInfo(download).Length, sha256, "Ready to install", cancellationToken).ConfigureAwait(false);
        progress?.Report(new UpdateProgress
        {
            OperationId = request.OperationId,
            State = UpdateOperationState.ReadyToInstall,
            CurrentStep = "Download verified and ready to install",
            Percent = 100,
            Detail = download,
            LogPath = logPath
        });
        return new UpdateExecutionResult
        {
            OperationId = request.OperationId,
            ServerId = server.Id,
            Success = true,
            PreviousDefinition = server,
            UpdatedDefinition = server,
            Message = $"Downloaded and verified {target.VersionName}. The active server was not changed."
        };
    }

    public async Task<PreparedPackUpdate> PrepareAndSwitchAsync(
        ServerDefinition server,
        UpdateSource source,
        UpdateInstallRequest request,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(server, source, request);
        var target = request.TargetVersion;
        ValidateFreeSpace(server, target);
        var parent = Directory.GetParent(server.RootPath)?.FullName
                     ?? throw new IOException("The server root must have a parent directory.");
        var candidate = Path.Combine(parent, $".chunkpilot-update-{request.OperationId:N}");
        var oldActive = Path.Combine(parent, $".chunkpilot-previous-{request.OperationId:N}");
        EnsureChild(parent, candidate);
        EnsureChild(parent, oldActive);
        if (Directory.Exists(candidate) || Directory.Exists(oldActive))
            throw new IOException("A staging directory for this update operation already exists.");
        var logPath = Path.Combine(paths.Staging, $"update-{request.OperationId:N}.log");
        var switched = false;
        VersionSnapshot? previousSnapshot = null;
        VersionSnapshot? activeSnapshot = null;
        await Journal(UpdateOperationState.Planned, "Update planned.", candidate, cancellationToken).ConfigureAwait(false);

        try
        {
            Report(UpdateOperationState.Snapshotting, "Creating verified full rollback snapshot", 10, "");
            var snapshot = await snapshots.CreateAsync(server, source,
                $"Pre-update snapshot before {target.VersionName}", cancellationToken).ConfigureAwait(false);
            previousSnapshot = snapshot;
            var download = CachePath(target);
            if (!File.Exists(download))
            {
                Report(UpdateOperationState.Downloading, "Downloading target server pack", 25, target.DownloadUrl);
                await DownloadAsync(target, download, progress, request.OperationId, logPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                Report(UpdateOperationState.Verifying, "Using previously downloaded cache candidate", 35, download);
            }
            Report(UpdateOperationState.Verifying, "Verifying provider hash", 42, target.FileName);
            try
            {
                await VerifyDownloadAsync(download, target, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                var rejectedSha = await PackMigrationPlanner.Sha256Async(download, CancellationToken.None)
                    .ConfigureAwait(false);
                await store.RecordUpdateDownloadAsync(request.OperationId, server.Id, source.Provider, target,
                    new FileInfo(download).Length, rejectedSha, "Rejected: provider verification failed",
                    CancellationToken.None).ConfigureAwait(false);
                File.Delete(download);
                throw;
            }
            var downloadSha256 = await PackMigrationPlanner.Sha256Async(download, cancellationToken)
                .ConfigureAwait(false);
            await store.RecordUpdateDownloadAsync(request.OperationId, server.Id, source.Provider, target,
                new FileInfo(download).Length, downloadSha256, "Verified", cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(candidate);
            Report(UpdateOperationState.Extracting, "Extracting target package into isolated candidate", 52, candidate);
            if (target.PackageType.Equals("mrpack", StringComparison.OrdinalIgnoreCase))
            {
                if (source.Provider != UpdateProvider.Modrinth)
                    throw new InvalidDataException("Only an exact Modrinth release may use the .mrpack update path.");
                var installedPack = await modrinthPacks.MaterializeAndInstallAsync(
                    download, candidate, server.Executable, logPath, null, cancellationToken).ConfigureAwait(false);
                var minecraft = installedPack.Manifest.Dependencies["minecraft"];
                target = target with
                {
                    MinecraftVersion = minecraft,
                    Loader = installedPack.Ecosystem.ToString(),
                    LoaderVersion = installedPack.LoaderVersion,
                    InstallerVersion = installedPack.InstallerVersion,
                    RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft),
                    PackageType = "mrpack"
                };
            }
            else if (target.PackageType.Equals("fabric-server-launcher", StringComparison.OrdinalIgnoreCase) ||
                target.PackageType.Equals("quilt-installer", StringComparison.OrdinalIgnoreCase) ||
                target.PackageType.Equals("forge-installer", StringComparison.OrdinalIgnoreCase) ||
                target.PackageType.Equals("neoforge-installer", StringComparison.OrdinalIgnoreCase))
            {
                var loader = target.PackageType.ToLowerInvariant() switch
                {
                    "fabric-server-launcher" => InstallSourceType.Fabric,
                    "quilt-installer" => InstallSourceType.Quilt,
                    "forge-installer" => InstallSourceType.Forge,
                    "neoforge-installer" => InstallSourceType.NeoForge,
                    _ => throw new InvalidDataException("The managed-loader update package type is unsupported.")
                };
                var installerJava = await ResolveInstallerJavaAsync(
                    server, target, cancellationToken).ConfigureAwait(false);
                _ = await loaderInstaller.InstallVerifiedArtifactAsync(new LoaderInstallPlan
                {
                    Loader = loader,
                    MinecraftVersion = target.MinecraftVersion,
                    LoaderVersion = target.LoaderVersion,
                    InstallerVersion = target.InstallerVersion,
                    DownloadUrl = target.DownloadUrl,
                    Sha1 = target.Sha1,
                    Sha256 = target.Sha256,
                    InstallerArgument = loader switch
                    {
                        InstallSourceType.Quilt => $"install server {target.MinecraftVersion} {target.LoaderVersion} --download-server --install-dir=.",
                        InstallSourceType.Forge or InstallSourceType.NeoForge => "--installServer",
                        _ => ""
                    },
                    ExpectedLaunchFile = loader switch
                    {
                        InstallSourceType.Fabric => "fabric-server-launch.jar",
                        InstallSourceType.Quilt => "quilt-server-launch.jar",
                        _ => "run.bat"
                    },
                    RequiredJavaMajor = target.RequiredJavaMajor,
                    RunsInstaller = loader is InstallSourceType.Quilt or InstallSourceType.Forge or InstallSourceType.NeoForge
                }, installerJava, download, candidate, logPath, cancellationToken).ConfigureAwait(false);
            }
            else if (target.PackageType.Equals("jar", StringComparison.OrdinalIgnoreCase) ||
                     target.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                File.Copy(download, Path.Combine(candidate, "server.jar"));
            else
                await ManagedServerInstaller.ExtractZipSafeAsync(download, candidate, cancellationToken).ConfigureAwait(false);
            NormalizeSinglePackageRoot(candidate);

            Report(UpdateOperationState.PlanningMigration, "Classifying persistent and pack-managed data", 65, "");
            var worldRoots = worlds.List(server).Select(world => Path.GetRelativePath(server.RootPath, world.FolderPath))
                .ToArray();
            var plan = await migration.BuildAndApplyAsync(
                server.RootPath, candidate, worldRoots, request.MigrationResolutions, cancellationToken)
                .ConfigureAwait(false);
            await store.RecordMigrationDecisionsAsync(plan.Changes.Select(change => new MigrationDecision
            {
                UpdateOperationId = request.OperationId,
                RelativePath = change.RelativePath,
                Decision = change.Change
            }), cancellationToken).ConfigureAwait(false);
            if (plan.RequiresManualReview && !request.ConfirmedMigrationWarnings)
                throw new MigrationReviewRequiredException(plan);

            Report(UpdateOperationState.BuildingCandidate, "Detecting and validating the candidate launch profile", 76, "");
            var detected = await detection.DetectAsync(candidate, cancellationToken).ConfigureAwait(false);
            var launch = detected.Candidates.FirstOrDefault(item => !item.DetachesProcess)
                         ?? throw new InvalidDataException("The target package does not contain a controllable launch candidate.");
            var actualJavaVersion = ValidateJavaRequirement(detected, target);
            var updatedDefinition = BuildDefinition(server, candidate, launch, target);
            ValidateCandidate(candidate, updatedDefinition);

            Report(UpdateOperationState.Switching, "Switching the active instance atomically", 86, server.RootPath);
            Directory.Move(server.RootPath, oldActive);
            try
            {
                Directory.Move(candidate, server.RootPath);
                switched = true;
            }
            catch
            {
                Directory.Move(oldActive, server.RootPath);
                throw;
            }

            var previous = snapshot with { IsActive = false, Health = VersionHealth.Healthy };
            var active = new VersionSnapshot
            {
                ServerId = server.Id,
                VersionId = target.VersionId,
                VersionName = target.VersionName,
                InstalledAt = DateTimeOffset.UtcNow,
                SourceProvider = source.Provider,
                Source = target.DownloadUrl,
                MinecraftVersion = string.IsNullOrWhiteSpace(target.MinecraftVersion)
                    ? server.MinecraftVersion : target.MinecraftVersion,
                Loader = string.IsNullOrWhiteSpace(target.Loader) ? server.Ecosystem.ToString() : target.Loader,
                LoaderVersion = target.LoaderVersion,
                JavaVersion = actualJavaVersion,
                IsActive = true,
                Health = VersionHealth.PendingValidation,
                Verified = true,
                IncludesWorldData = true,
                Changelog = target.Changelog,
                UpdateNotes = target.MigrationNotes,
                Definition = updatedDefinition
            };
            activeSnapshot = active;
            await store.UpsertVersionSnapshotAsync(previous, cancellationToken).ConfigureAwait(false);
            foreach (var version in await store.GetVersionSnapshotsAsync(server.Id, cancellationToken).ConfigureAwait(false))
            {
                if (version.IsActive && version.Id != active.Id)
                    await store.UpsertVersionSnapshotAsync(version with { IsActive = false }, cancellationToken)
                        .ConfigureAwait(false);
            }
            await store.UpsertVersionSnapshotAsync(active, cancellationToken).ConfigureAwait(false);
            var updatedSource = source with
            {
                InstalledVersionId = target.VersionId,
                InstalledVersionName = target.VersionName,
                InstalledFileId = target.VersionId,
                MinecraftVersion = active.MinecraftVersion,
                Loader = active.Loader,
                LoaderVersion = active.LoaderVersion,
                InstallerVersion = target.InstallerVersion,
                InstalledAt = active.InstalledAt
            };
            await store.UpsertUpdateSourceAsync(updatedSource, cancellationToken).ConfigureAwait(false);
            await store.RecordInstanceHistoryAsync(server.Id, "Updated", target.DownloadUrl,
                downloadSha256,
                $"From={source.InstalledVersionName}; To={target.VersionName}; Provider={source.Provider}",
                cancellationToken).ConfigureAwait(false);
            Report(UpdateOperationState.Starting, "Candidate switched; ready for startup validation", 92, "");
            return new PreparedPackUpdate(
                new UpdateExecutionResult
                {
                    OperationId = request.OperationId,
                    ServerId = server.Id,
                    Success = true,
                    PreviousDefinition = server,
                    UpdatedDefinition = updatedDefinition,
                    PreviousSnapshot = previous,
                    ActiveVersion = active,
                    MigrationPlan = plan,
                    Message = $"Updated from {source.InstalledVersionName} to {target.VersionName}; validation is pending."
                },
                snapshot.SnapshotPath,
                Path.Combine(paths.Staging, $"failed-update-{request.OperationId:N}"),
                oldActive);
        }
        catch (Exception updateException)
        {
            Exception? recoveryException = null;
            if (switched && Directory.Exists(oldActive))
            {
                try
                {
                    if (Directory.Exists(candidate))
                        TryDeleteDirectory(candidate);
                    Directory.Move(server.RootPath, candidate);
                    Directory.Move(oldActive, server.RootPath);
                    TryDeleteDirectory(candidate);
                    switched = false;
                    await store.UpsertUpdateSourceAsync(source, CancellationToken.None).ConfigureAwait(false);
                    if (previousSnapshot is not null)
                        await store.UpsertVersionSnapshotAsync(previousSnapshot with
                        {
                            IsActive = true,
                            Health = VersionHealth.Healthy,
                            LastStartupResult = "Update transaction was reverted before startup."
                        }, CancellationToken.None).ConfigureAwait(false);
                    if (activeSnapshot is not null)
                        await store.UpsertVersionSnapshotAsync(activeSnapshot with
                        {
                            IsActive = false,
                            Health = VersionHealth.Failed,
                            LastStartupResult = "Activation transaction failed and the prior directory was restored."
                        }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                  Microsoft.Data.Sqlite.SqliteException)
                {
                    recoveryException = exception;
                }
            }
            else
            {
                TryDeleteDirectory(candidate);
            }
            await Journal(UpdateOperationState.Failed, "Update preparation failed.", candidate, CancellationToken.None)
                .ConfigureAwait(false);
            if (recoveryException is not null)
                throw new AggregateException(
                    "Update failed and automatic transaction recovery also failed. The previous directory was retained for manual recovery.",
                    updateException, recoveryException);
            await store.CompleteOperationAsync(request.OperationId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        void Report(UpdateOperationState state, string step, double percent, string detail)
        {
            progress?.Report(new UpdateProgress
            {
                OperationId = request.OperationId,
                State = state,
                CurrentStep = step,
                Percent = percent,
                Detail = detail,
                LogPath = logPath
            });
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:O} {state}: {step} {detail}{Environment.NewLine}");
            _ = Journal(state, detail, candidate, CancellationToken.None);
        }

        Task Journal(
            UpdateOperationState state,
            string detail,
            string staging,
            CancellationToken token = default) =>
            store.UpsertOperationAsync(request.OperationId, "ServerPackUpdate",
                state is UpdateOperationState.Completed ? InstallState.Completed :
                state is UpdateOperationState.Failed ? InstallState.Failed :
                state is UpdateOperationState.Cancelled ? InstallState.Cancelled : InstallState.Installing,
                server.RootPath, staging, $"{state}: {detail}", token);
    }

    private async Task<string> ResolveInstallerJavaAsync(
        ServerDefinition server,
        PackVersionInfo target,
        CancellationToken cancellationToken)
    {
        var required = target.InstallerJavaMajor > 0
            ? target.InstallerJavaMajor
            : target.RequiredJavaMajor;
        if (required <= 0 || required == target.RequiredJavaMajor)
            return server.Executable;

        var installed = await store.GetManagedJavaRuntimesAsync(cancellationToken).ConfigureAwait(false);
        var reusable = JavaRuntimePolicy.Select(installed, new JavaRuntimeRequirement
        {
            MinimumMajor = required,
            MaximumMajor = required,
            Require64Bit = true,
            Evidence = $"Loader installer for {target.Loader} {target.InstallerVersion}"
        });
        if (reusable is not null)
            return reusable.JavaPath;
        if (managedJava is null)
            throw new InvalidOperationException(
                $"This loader installer needs Java {required}, separate from the server runtime. " +
                "Prepare that private managed Java runtime before applying the update.");

        var acquired = await managedJava.InstallAsync(required, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return acquired.JavaPath;
    }

    public async Task RollbackAsync(
        ServerDefinition server,
        VersionSnapshot snapshot,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.Verified || !File.Exists(snapshot.SnapshotPath))
            throw new InvalidDataException("Rollback requires a verified snapshot archive.");
        var parent = Directory.GetParent(server.RootPath)?.FullName
                     ?? throw new IOException("The server root must have a parent directory.");
        var candidate = Path.Combine(parent, $".chunkpilot-rollback-{operationId:N}");
        var failed = Path.Combine(parent, $".chunkpilot-rolled-back-{operationId:N}");
        EnsureChild(parent, candidate);
        EnsureChild(parent, failed);
        await VersionSnapshotService.ExtractVerifiedAsync(snapshot.SnapshotPath, candidate, cancellationToken)
            .ConfigureAwait(false);
        if (!await VersionSnapshotService.VerifyExtractedAsync(
                snapshot.SnapshotPath, candidate, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The extracted rollback candidate did not match its snapshot manifest.");
        if (Directory.Exists(failed))
            throw new IOException($"Rollback recovery destination already exists: {failed}");
        Directory.Move(server.RootPath, failed);
        try
        {
            Directory.Move(candidate, server.RootPath);
        }
        catch
        {
            Directory.Move(failed, server.RootPath);
            throw;
        }
        TryDeleteDirectory(failed);
        var versions = await store.GetVersionSnapshotsAsync(server.Id, cancellationToken).ConfigureAwait(false);
        foreach (var version in versions)
            await store.UpsertVersionSnapshotAsync(version with
            {
                IsActive = version.Id == snapshot.Id,
                Health = version.Id == snapshot.Id ? VersionHealth.Healthy :
                    version.IsActive
                        ? version.Health == VersionHealth.Failed ? VersionHealth.Failed : VersionHealth.RolledBack
                        : version.Health
            }, cancellationToken).ConfigureAwait(false);
        var source = await store.GetUpdateSourceAsync(server.Id, cancellationToken).ConfigureAwait(false);
        if (source is not null)
            await store.UpsertUpdateSourceAsync(source with
            {
                Provider = snapshot.SourceProvider,
                InstalledVersionId = snapshot.VersionId,
                InstalledVersionName = snapshot.VersionName,
                MinecraftVersion = snapshot.MinecraftVersion,
                Loader = snapshot.Loader,
                LoaderVersion = snapshot.LoaderVersion,
                InstalledAt = DateTimeOffset.UtcNow
            }, cancellationToken).ConfigureAwait(false);
        await store.RecordRollbackAsync(server.Id,
            versions.FirstOrDefault(version => version.IsActive)?.VersionName ?? "unknown",
            snapshot.VersionName, "Success", snapshot.SnapshotPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task FinalizeOperationAsync(
        PreparedPackUpdate prepared,
        CancellationToken cancellationToken = default)
    {
        await store.CompleteOperationAsync(prepared.Result.OperationId, cancellationToken).ConfigureAwait(false);
        TryDeleteDirectory(prepared.PreviousDirectoryPath);
    }

    public async Task<IReadOnlyList<string>> RecoverInterruptedOperationsAsync(
        CancellationToken cancellationToken = default)
    {
        var recovered = new List<string>();
        var servers = await store.GetServersAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in (await store.GetInterruptedOperationsAsync(cancellationToken).ConfigureAwait(false))
                     .Where(item => item.Type.Equals("ServerPackUpdate", StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(operation.Target);
            var parent = Directory.GetParent(target)?.FullName
                         ?? throw new IOException($"Interrupted update target has no parent: {target}");
            var previous = Path.Combine(parent, $".chunkpilot-previous-{operation.Id:N}");
            EnsureChild(parent, previous);
            if (Directory.Exists(previous))
            {
                var interruptedCandidate = Path.Combine(parent, $".chunkpilot-interrupted-{operation.Id:N}");
                EnsureChild(parent, interruptedCandidate);
                if (Directory.Exists(interruptedCandidate))
                    throw new IOException($"Interrupted-update recovery path already exists: {interruptedCandidate}");
                if (Directory.Exists(target))
                    Directory.Move(target, interruptedCandidate);
                Directory.Move(previous, target);
                TryDeleteDirectory(interruptedCandidate);

                var server = servers.FirstOrDefault(item =>
                    Path.GetFullPath(item.RootPath).Equals(target, StringComparison.OrdinalIgnoreCase));
                if (server is not null)
                {
                    var versions = await store.GetVersionSnapshotsAsync(server.Id, cancellationToken)
                        .ConfigureAwait(false);
                    var rollbackVersion = versions
                        .Where(item => item.Verified && !string.IsNullOrWhiteSpace(item.SnapshotPath))
                        .OrderByDescending(item => item.InstalledAt)
                        .FirstOrDefault();
                    if (rollbackVersion is not null)
                    {
                        foreach (var version in versions)
                            await store.UpsertVersionSnapshotAsync(version with
                            {
                                IsActive = version.Id == rollbackVersion.Id,
                                Health = version.Id == rollbackVersion.Id
                                    ? VersionHealth.Healthy
                                    : version.IsActive ? VersionHealth.Failed : version.Health,
                                LastStartupResult = version.Id == rollbackVersion.Id
                                    ? "Restored automatically after ChunkPilot restarted during an update."
                                    : version.LastStartupResult
                            }, cancellationToken).ConfigureAwait(false);
                        var restoredDefinition = rollbackVersion.Definition with
                        {
                            RootPath = target,
                            WorkingDirectory = target
                        };
                        await store.UpsertServerAsync(restoredDefinition, cancellationToken).ConfigureAwait(false);
                        var source = await store.GetUpdateSourceAsync(server.Id, cancellationToken).ConfigureAwait(false);
                        if (source is not null)
                            await store.UpsertUpdateSourceAsync(source with
                            {
                                InstalledVersionId = rollbackVersion.VersionId,
                                InstalledVersionName = rollbackVersion.VersionName,
                                MinecraftVersion = rollbackVersion.MinecraftVersion,
                                Loader = rollbackVersion.Loader,
                                LoaderVersion = rollbackVersion.LoaderVersion,
                                InstalledAt = DateTimeOffset.UtcNow
                            }, cancellationToken).ConfigureAwait(false);
                        await store.RecordRollbackAsync(server.Id, "interrupted update",
                            rollbackVersion.VersionName, "Recovered", rollbackVersion.SnapshotPath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                recovered.Add($"Restored {target} from the retained pre-update directory.");
            }
            else if (Directory.Exists(target))
            {
                if (!string.IsNullOrWhiteSpace(operation.Staging))
                {
                    EnsureChild(parent, operation.Staging);
                    TryDeleteDirectory(operation.Staging);
                }
                recovered.Add($"Cleaned an interrupted pre-switch update for {target}.");
            }
            else
            {
                throw new IOException(
                    $"Interrupted update {operation.Id} cannot be recovered automatically because both the active and previous directories are missing.");
            }
            await store.CompleteOperationAsync(operation.Id, cancellationToken).ConfigureAwait(false);
        }
        return recovered;
    }

    public static async Task VerifyDownloadAsync(
        string path,
        PackVersionInfo target,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The downloaded update package was not found.", path);
        if (target.FileSize is > 0 && new FileInfo(path).Length != target.FileSize.Value)
            throw new InvalidDataException(
                $"Downloaded size mismatch. Expected {target.FileSize.Value} bytes; got {new FileInfo(path).Length}.");
        if (!string.IsNullOrWhiteSpace(target.Sha512))
        {
            await VerifyHashAsync(path, target.Sha512, SHA512.Create(), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrWhiteSpace(target.Sha256))
        {
            await VerifyHashAsync(path, target.Sha256, SHA256.Create(), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!string.IsNullOrWhiteSpace(target.Sha1))
        {
#pragma warning disable CA5350 // Modrinth publishes SHA-1; stronger provider hashes are preferred above it.
            await VerifyHashAsync(path, target.Sha1, SHA1.Create(), cancellationToken).ConfigureAwait(false);
#pragma warning restore CA5350
            return;
        }
        _ = await PackMigrationPlanner.Sha256Async(path, cancellationToken).ConfigureAwait(false);
    }

    private async Task DownloadAsync(
        PackVersionInfo target,
        string destination,
        IProgress<UpdateProgress>? progress,
        Guid operationId,
        string logPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (Uri.TryCreate(target.DownloadUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            File.Copy(uri.LocalPath, destination, overwrite: true);
            return;
        }
        if (File.Exists(target.DownloadUrl))
        {
            File.Copy(target.DownloadUrl, destination, overwrite: true);
            return;
        }
        var downloadUri = new Uri(target.DownloadUrl, UriKind.Absolute);
        if (downloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Server-pack update downloads require HTTPS.");
        using var response = await http.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? target.FileSize;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var timer = Stopwatch.StartNew();
        var buffer = new byte[128 * 1024];
        long bytes = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytes += read;
            progress?.Report(new UpdateProgress
            {
                OperationId = operationId,
                State = UpdateOperationState.Downloading,
                CurrentStep = "Downloading target server pack",
                Percent = total is > 0 ? 25 + 15 * bytes / total.Value : 32,
                BytesDownloaded = bytes,
                TotalBytes = total,
                BytesPerSecond = bytes / Math.Max(0.001, timer.Elapsed.TotalSeconds),
                Detail = target.DownloadUrl,
                LogPath = logPath
            });
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ServerDefinition BuildDefinition(
        ServerDefinition current,
        string candidateRoot,
        LaunchCandidate launch,
        PackVersionInfo target)
    {
        string Remap(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;
            var relative = Path.GetRelativePath(candidateRoot, path);
            return relative.StartsWith("..", StringComparison.Ordinal)
                ? path : Path.Combine(current.RootPath, relative);
        }

        var arguments = launch.Arguments.Replace(candidateRoot, current.RootPath, StringComparison.OrdinalIgnoreCase);
        var ecosystem = Enum.TryParse<ServerEcosystem>(target.Loader, true, out var parsed)
            ? parsed : current.Ecosystem;
        return current with
        {
            Executable = Remap(launch.Executable),
            Arguments = ServerLaunchPolicy.EnsureNoGui(arguments, ecosystem),
            WorkingDirectory = Remap(launch.WorkingDirectory),
            MinecraftVersion = string.IsNullOrWhiteSpace(target.MinecraftVersion)
                ? current.MinecraftVersion : target.MinecraftVersion,
            LoaderVersion = string.IsNullOrWhiteSpace(target.LoaderVersion)
                ? current.LoaderVersion : target.LoaderVersion,
            Ecosystem = ecosystem
        };
    }

    private static void ValidateCandidate(string candidate, ServerDefinition definition)
    {
        if (!Directory.EnumerateFiles(candidate, "*.jar", SearchOption.AllDirectories).Any() &&
            !Directory.EnumerateFiles(candidate, "*.bat", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidDataException("The candidate package contains no server JAR or launch script.");
        if (ServerLaunchPolicy.IsDetachedLaunch(definition.Executable, definition.Arguments))
            throw new InvalidDataException("The candidate launch profile detaches or uses javaw and cannot be validated.");
    }

    private static string ValidateJavaRequirement(ServerDetectionResult detected, PackVersionInfo target)
    {
        var versions = detected.JavaRuntimes.Where(runtime => runtime.Exists)
            .Select(runtime => (runtime.Version, Major: JavaMajor(runtime.Version)))
            .Where(item => item.Major > 0)
            .OrderByDescending(item => item.Major)
            .ToArray();
        if (target.RequiredJavaMajor <= 0)
            return versions.FirstOrDefault().Version ?? "Unknown";
        var compatible = versions.FirstOrDefault(item => item.Major >= target.RequiredJavaMajor);
        if (compatible.Major == 0)
            throw new InvalidDataException(
                $"The target requires Java {target.RequiredJavaMajor} or newer, but no compatible local Java runtime was proven.");
        return compatible.Version;
    }

    private static int JavaMajor(string version)
    {
        var value = version.Trim().Trim('"');
        if (value.StartsWith("1.", StringComparison.Ordinal))
        {
            var legacyDigits = new string(value[2..].TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(legacyDigits, out var legacy))
                return legacy;
        }
        var digits = new string(value.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : 0;
    }

    private static void NormalizeSinglePackageRoot(string candidate)
    {
        if (Directory.EnumerateFiles(candidate, "*", SearchOption.TopDirectoryOnly).Any())
            return;
        var directories = Directory.EnumerateDirectories(candidate, "*", SearchOption.TopDirectoryOnly).ToArray();
        if (directories.Length != 1)
            return;
        var wrapper = directories[0];
        foreach (var file in Directory.EnumerateFiles(wrapper, "*", SearchOption.TopDirectoryOnly))
            File.Move(file, Path.Combine(candidate, Path.GetFileName(file)));
        foreach (var directory in Directory.EnumerateDirectories(wrapper, "*", SearchOption.TopDirectoryOnly))
            Directory.Move(directory, Path.Combine(candidate, Path.GetFileName(directory)));
        Directory.Delete(wrapper);
    }

    private static void Validate(ServerDefinition server, UpdateSource source, UpdateInstallRequest request)
    {
        if (!Directory.Exists(server.RootPath))
            throw new DirectoryNotFoundException(server.RootPath);
        if (source.ServerId != server.Id || request.ServerId != server.Id)
            throw new InvalidOperationException("The update source and request must match the selected server.");
        if (!source.HasIdentifiedBaseline)
            throw new InvalidOperationException("Identify the installed baseline before updating.");
        if (string.IsNullOrWhiteSpace(request.TargetVersion.DownloadUrl))
            throw new InvalidOperationException("The target version has no download URL.");
    }

    private void ValidateFreeSpace(ServerDefinition server, PackVersionInfo target)
    {
        var currentBytes = Directory.EnumerateFiles(server.RootPath, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);
        var packageBytes = Math.Max(target.FileSize ?? 0, 64L * 1024 * 1024);
        var snapshotRequired = checked(currentBytes + packageBytes + 512L * 1024 * 1024);
        var snapshotDrive = new DriveInfo(Path.GetPathRoot(paths.VersionSnapshots)!);
        if (snapshotDrive.AvailableFreeSpace < snapshotRequired)
            throw new IOException(
                $"Snapshot/update storage needs about {snapshotRequired / 1024d / 1024d / 1024d:F1} GB free; " +
                $"{snapshotDrive.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB is available.");
        var candidateRequired = checked(packageBytes * 5 + 512L * 1024 * 1024);
        var serverDrive = new DriveInfo(Path.GetPathRoot(server.RootPath)!);
        if (serverDrive.AvailableFreeSpace < candidateRequired)
            throw new IOException(
                $"Server staging needs about {candidateRequired / 1024d / 1024d / 1024d:F1} GB free; " +
                $"{serverDrive.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB is available.");
    }

    private static async Task VerifyHashAsync(
        string path,
        string expected,
        HashAlgorithm algorithm,
        CancellationToken cancellationToken)
    {
        using (algorithm)
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var actual = Convert.ToHexString(await algorithm.ComputeHashAsync(stream, cancellationToken)
                .ConfigureAwait(false));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Provider hash verification failed. Expected {expected}; got {actual}.");
        }
    }

    private static string SafeDownloadName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "server-pack.zip" : Path.GetFileName(value);
        return new string(name.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '-' : character)
            .ToArray());
    }

    private string CachePath(PackVersionInfo target)
    {
        var identity = !string.IsNullOrWhiteSpace(target.Sha512) ? target.Sha512[..Math.Min(24, target.Sha512.Length)] :
            !string.IsNullOrWhiteSpace(target.Sha256) ? target.Sha256[..Math.Min(24, target.Sha256.Length)] :
            !string.IsNullOrWhiteSpace(target.Sha1) ? target.Sha1[..Math.Min(24, target.Sha1.Length)] :
            SafeDownloadName(target.VersionId);
        return Path.Combine(paths.UpdateCache, $"{SafeDownloadName(identity)}-{SafeDownloadName(target.FileName)}");
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Cleanup is intentionally non-fatal after the active directory is safely established.
        }
        catch (UnauthorizedAccessException)
        {
            // The retained sibling directory remains an auditable recovery copy.
        }
    }

    private static void EnsureChild(string parent, string child)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(child).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Update staging path escapes the server parent: {child}");
    }
}
