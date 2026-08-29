using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record JarInstallReceipt(
    string AppliedRelativePath,
    string? PreviousRelativePath,
    string? RecoveryPath,
    string AppliedSha256 = "",
    string PreviousSha256 = "");

public sealed record JarMoveReceipt(
    string SourceRelativePath,
    string DestinationPath,
    bool Changed,
    string MovedSha256 = "");

public sealed partial class JarInventoryService
{
    internal const long MaximumJarBytes = 512L * 1024 * 1024;
    internal const long MaximumMetadataBytes = 512L * 1024;
    internal const int MaximumArchiveEntries = 20_000;
    internal const int MaximumDependencies = 256;
    private readonly AppDataPaths paths;
    private readonly CanonicalPathLockManager pathLocks;

    public JarInventoryService(
        SafeFileService files,
        AppDataPaths paths,
        CanonicalPathLockManager? pathLocks = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        this.paths = paths;
        this.pathLocks = pathLocks ?? new CanonicalPathLockManager();
    }

    public IReadOnlyList<ModPluginEntry> Inventory(ServerDefinition server) =>
        WithContentLock(server, () => InventoryCore(server));

    private IReadOnlyList<ModPluginEntry> InventoryCore(ServerDefinition server)
    {
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var folder = ResolveServerPath(server, folderName);
        var disabledFolder = ResolveServerPath(server, ".chunkpilot-disabled", folderName);
        var activeExists = ValidateDirectoryChain(server.RootPath, folder, allowMissing: true);
        var disabledExists = ValidateDirectoryChain(server.RootPath, disabledFolder, allowMissing: true);
        if (!activeExists && !disabledExists)
            return [];

        var active = activeExists ? EnumerateSafeJarFiles(server.RootPath, folder) : [];
        var disabled = disabledExists ? EnumerateSafeJarFiles(server.RootPath, disabledFolder) : [];
        var entries = active
            .Select(path => ReadMetadata(server, path, enabled: true))
            .Concat(disabled.Select(path => ReadMetadata(server, path, enabled: false)))
            .Take(5_000)
            .ToList();
        var duplicates = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(entry => entry.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var provenance = LoadProvenance(server.Id);
        return entries.Select(entry => ApplyProvenance(
                entry with { DuplicateId = duplicates.Contains(entry.RelativePath) }, provenance))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public void ValidateConfigOwnership(
        ServerDefinition server,
        string addonRelativePath,
        string configRelativePath)
    {
        var addon = Inventory(server).FirstOrDefault(entry =>
            entry.RelativePath.Equals(addonRelativePath, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected add-on is no longer in the current inventory.");
        var extension = Path.GetExtension(configRelativePath).ToLowerInvariant();
        if (extension is not (".yml" or ".yaml" or ".json" or ".jsonc" or ".toml" or ".properties" or ".conf"))
            throw new InvalidOperationException("This add-on configuration type is not enabled for editing.");

        var configPath = ResolveServerPath(server, configRelativePath);
        ValidateRegularFileChain(server.RootPath, configPath);
        var parts = configRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var names = new[] { addon.Id, addon.Name }
            .Where(IsSafeConfigIdentity)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isMod = server.Ecosystem is ServerEcosystem.Fabric or ServerEcosystem.NeoForge;
        var owned = isMod
            ? parts.Length == 3 && parts[0].Equals("config", StringComparison.OrdinalIgnoreCase) && names.Contains(parts[1]) ||
              parts.Length == 2 && parts[0].Equals("config", StringComparison.OrdinalIgnoreCase) &&
              names.Contains(Path.GetFileNameWithoutExtension(parts[1]))
            : parts.Length == 3 && parts[0].Equals("plugins", StringComparison.OrdinalIgnoreCase) && names.Contains(parts[1]);
        if (!owned)
            throw new UnauthorizedAccessException(
                "The configuration path is not owned by the selected add-on. ChunkPilot will not guess file ownership.");
    }

    public async Task InstallAsync(ServerDefinition server, string sourceJar, CancellationToken cancellationToken = default) =>
        _ = await InstallWithReceiptAsync(server, sourceJar, replaceRelativePath: null, cancellationToken)
            .ConfigureAwait(false);

    public async Task InstallAsync(
        ServerDefinition server,
        string sourceJar,
        string? replaceRelativePath,
        CancellationToken cancellationToken = default) =>
        _ = await InstallWithReceiptAsync(server, sourceJar, replaceRelativePath, cancellationToken)
            .ConfigureAwait(false);

    public async Task<JarInstallReceipt> InstallWithReceiptAsync(
        ServerDefinition server,
        string sourceJar,
        string? replaceRelativePath = null,
        CancellationToken cancellationToken = default) =>
        await InstallCoreAsync(
                server, sourceJar, replaceRelativePath, reviewedRelease: null, reviewedExisting: null,
                cancellationToken)
            .ConfigureAwait(false);

    internal async Task<JarInstallReceipt> InstallReviewedProviderWithReceiptAsync(
        ServerDefinition server,
        string sourceJar,
        PluginRelease reviewedRelease,
        ModPluginEntry? reviewedExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewedRelease);
        if (!Path.GetFileName(sourceJar).Equals(reviewedRelease.FileName, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(reviewedRelease.ProjectId))
            throw new InvalidDataException("The staged add-on does not match the exact reviewed provider release.");
        return await InstallCoreAsync(
                server, sourceJar, reviewedExisting?.RelativePath, reviewedRelease, reviewedExisting,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<JarInstallReceipt> InstallCoreAsync(
        ServerDefinition server,
        string sourceJar,
        string? replaceRelativePath,
        PluginRelease? reviewedRelease,
        ModPluginEntry? reviewedExisting,
        CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(sourceJar).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local .jar file.");
        ValidateInspectionSource(server, sourceJar);
        var incoming = ReadMetadata(server with { RootPath = Path.GetDirectoryName(sourceJar)! }, sourceJar, enabled: true);
        if (incoming.Compatibility == CompatibilityState.Incompatible)
            throw new InvalidOperationException(incoming.CompatibilityReason);
        var incomingSha256 = incoming.Sha256;
        await using var contentLock = await AcquireContentLockAsync(server, cancellationToken).ConfigureAwait(false);
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var destinationDirectory = EnsureServerDirectory(server, folderName);
        var destination = ResolveServerPath(server, folderName, Path.GetFileName(sourceJar));
        var inventory = InventoryCore(server);

        string? replacement;
        string expectedDestinationSha256;
        string expectedReplacementSha256;
        if (reviewedRelease is not null)
        {
            replacement = ValidateReviewedProviderActivation(
                server, reviewedRelease, reviewedExisting, inventory, destination);
            expectedDestinationSha256 = replacement is not null &&
                replacement.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    ? reviewedExisting!.Sha256
                    : "";
            expectedReplacementSha256 = replacement is not null &&
                !replacement.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    ? reviewedExisting!.Sha256
                    : "";
        }
        else
        {
            replacement = string.IsNullOrWhiteSpace(replaceRelativePath)
                ? null
                : ResolveContentJarPath(server, replaceRelativePath, mustExist: true);
            var duplicate = inventory.FirstOrDefault(entry =>
                incoming.Id.Length > 0 && entry.Id.Equals(incoming.Id, StringComparison.OrdinalIgnoreCase) &&
                !entry.FileName.Equals(Path.GetFileName(sourceJar), StringComparison.OrdinalIgnoreCase) &&
                (replacement is null || !ResolveContentJarPath(server, entry.RelativePath, mustExist: true)
                    .Equals(replacement, StringComparison.OrdinalIgnoreCase)));
            if (duplicate is not null)
                throw new InvalidOperationException(
                    $"A different file already provides ID '{incoming.Id}': {duplicate.RelativePath}");
            expectedDestinationSha256 = RegularFileSha256OrEmpty(server.RootPath, destination);
            expectedReplacementSha256 = replacement is not null &&
                !replacement.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    ? RegularFileSha256(server.RootPath, replacement)
                    : "";
        }

        if (replacement is not null && !replacement.Equals(destination, StringComparison.OrdinalIgnoreCase) &&
            PathEntry(destination) != PathEntryKind.Missing)
            throw new IOException(
                $"A different add-on JAR already uses the update filename: {Path.GetFileName(destination)}");

        await using var input = new FileStream(
            sourceJar, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var temporary = destination + $".chunkpilot-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            ValidateRegularFileChain(server.RootPath, temporary);
            EnsureUnchangedActivationState(
                server, reviewedRelease, reviewedExisting, destination, replacement,
                expectedDestinationSha256, expectedReplacementSha256);
            var receipt = ActivatePreparedJar(
                server, temporary, destination, replacement, incomingSha256,
                expectedDestinationSha256, expectedReplacementSha256);
            if (reviewedRelease is not null)
            {
                try
                {
                    WithPathLock(
                        ProvenancePath(server.Id),
                        () => RecordProviderProvenanceCore(server, sourceJar, reviewedRelease));
                }
                catch (Exception provenanceFailure) when (provenanceFailure is
                           IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    try { RollbackInstallCore(server, receipt); }
                    catch (Exception rollbackFailure) when (rollbackFailure is
                               IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        throw new AggregateException(
                            "The add-on activated, but provider ownership could not be recorded and rollback also failed. Recovery evidence was preserved.",
                            provenanceFailure, rollbackFailure);
                    }
                    throw;
                }
            }
            return receipt;
        }
        finally
        {
            TryDeleteRegularTemporary(server.RootPath, temporary);
        }
    }

    public void RollbackInstall(ServerDefinition server, JarInstallReceipt receipt) =>
        WithContentLock(server, () => RollbackInstallCore(server, receipt));

    private void RollbackInstallCore(ServerDefinition server, JarInstallReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var applied = ResolveContentJarPath(server, receipt.AppliedRelativePath, mustExist: false);
        if (PathEntry(applied) != PathEntryKind.Missing)
        {
            ValidateRegularFileChain(server.RootPath, applied);
            if (receipt.AppliedSha256.Length != 64 ||
                !RegularFileSha256(server.RootPath, applied).Equals(
                    receipt.AppliedSha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException(
                    "The activated add-on path changed after installation, so rollback preserved it in place.");
            var rollbackFolder = EnsureRecoveryDirectory(
                server, "failed-plugin-activation", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            var failed = Path.Combine(rollbackFolder, Path.GetFileName(applied));
            if (PathEntry(failed) != PathEntryKind.Missing)
                failed = Path.Combine(rollbackFolder,
                    $"{Path.GetFileNameWithoutExtension(applied)}-{Guid.NewGuid():N}.jar");
            File.Move(applied, failed, overwrite: false);
            ValidateRegularFileChain(paths.Root, failed);
            if (!RegularFileSha256(paths.Root, failed).Equals(
                    receipt.AppliedSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryRestoreMovedFile(paths.Root, failed, server.RootPath, applied);
                throw new IOException(
                    "The activated add-on changed at the rollback boundary and was preserved.");
            }
        }

        if (string.IsNullOrWhiteSpace(receipt.PreviousRelativePath) ||
            string.IsNullOrWhiteSpace(receipt.RecoveryPath))
            return;
        var recovery = ResolveAppDataRecoveryPath(receipt.RecoveryPath, mustExist: true);
        if (receipt.PreviousSha256.Length != 64 ||
            !RegularFileSha256(paths.Root, recovery).Equals(
                receipt.PreviousSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The known-good plugin recovery copy is unavailable.");
        var previous = ResolveContentJarPath(server, receipt.PreviousRelativePath, mustExist: false);
        if (PathEntry(previous) != PathEntryKind.Missing)
            throw new IOException("The previous plugin path is no longer empty, so rollback stopped safely.");
        EnsureSafeExistingParentDirectory(server.RootPath, previous);
        File.Move(recovery, previous, overwrite: false);
        ValidateRegularFileChain(server.RootPath, previous);
        if (!RegularFileSha256(server.RootPath, previous).Equals(
                receipt.PreviousSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryRestoreMovedFile(server.RootPath, previous, paths.Root, recovery);
            throw new IOException("The known-good add-on changed at the rollback boundary and was preserved.");
        }
    }

    public void RecordProviderProvenance(ServerDefinition server, string sourceJar, PluginRelease release)
    {
        var provenancePath = ProvenancePath(server.Id);
        WithPathLock(provenancePath, () => RecordProviderProvenanceCore(server, sourceJar, release));
    }

    private void RecordProviderProvenanceCore(ServerDefinition server, string sourceJar, PluginRelease release)
    {
        var inspected = Inspect(server, sourceJar);
        if (inspected.Sha256.Length != 64)
            throw new InvalidDataException("The plugin JAR did not produce a valid SHA-256 identity.");
        var entries = LoadProvenanceEntries(server.Id).ToList();
        entries.RemoveAll(entry => entry.Sha256.Equals(inspected.Sha256, StringComparison.OrdinalIgnoreCase));
        entries.Add(new PluginProvenanceEntry(
            inspected.Sha256,
            release.Provider,
            release.ProjectId,
            release.VersionId,
            release.Provider == PluginProviderKind.CurseForge ? "" : release.VersionName,
            ProviderIdentityOrigin.ApiDerivedOperationalIdentity,
            DateTimeOffset.UtcNow));
        if (entries.Count > 5_000)
            entries = entries.OrderByDescending(entry => entry.RecordedAt).Take(5_000).ToList();
        var path = ProvenancePath(server.Id);
        _ = EnsureAppDataDirectory("PluginProvenance");
        ValidateOptionalRegularFileChain(paths.Root, path);
        var temporary = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            File.WriteAllText(
                temporary, JsonSerializer.Serialize(entries, ProtocolJson.Options), new UTF8Encoding(false));
            ValidateRegularFileChain(paths.Root, temporary);
            ValidateOptionalRegularFileChain(paths.Root, path);
            File.Move(temporary, path, overwrite: true);
            ValidateRegularFileChain(paths.Root, path);
        }
        finally
        {
            TryDeleteRegularTemporary(paths.Root, temporary);
        }
    }

    public ModPluginEntry Inspect(ServerDefinition server, string sourceJar)
    {
        if (!Path.GetExtension(sourceJar).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local .jar file.");
        ValidateInspectionSource(server, sourceJar);
        return ReadMetadata(server with { RootPath = Path.GetDirectoryName(sourceJar)! }, sourceJar, enabled: true);
    }

    public void SetEnabled(ServerDefinition server, string relativePath, bool enabled) =>
        _ = SetEnabledWithReceipt(server, relativePath, enabled);

    public JarMoveReceipt SetEnabledWithReceipt(ServerDefinition server, string relativePath, bool enabled) =>
        WithContentLock(server, () => SetEnabledWithReceiptCore(server, relativePath, enabled));

    private JarMoveReceipt SetEnabledWithReceiptCore(
        ServerDefinition server,
        string relativePath,
        bool enabled)
    {
        var source = ResolveContentJarPath(server, relativePath, mustExist: true);
        var movedSha256 = RegularFileSha256(server.RootPath, source);
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var activeDirectory = EnsureServerDirectory(server, folderName);
        var disabledDirectory = EnsureServerDirectory(server, ".chunkpilot-disabled", folderName);
        var sourceInDisabled = source.StartsWith(
            Path.TrimEndingDirectorySeparator(disabledDirectory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        var destination = enabled
            ? Path.Combine(activeDirectory, Path.GetFileName(source))
            : Path.Combine(disabledDirectory, Path.GetFileName(source));
        if (enabled == !sourceInDisabled || source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            return new JarMoveReceipt(relativePath, destination, Changed: false, movedSha256);
        ValidateOptionalRegularFileChain(server.RootPath, destination);
        if (PathEntry(destination) != PathEntryKind.Missing)
            throw new IOException($"The destination already exists: {destination}");
        EnsureRegularFileUnchanged(server.RootPath, source, movedSha256,
            "The selected add-on changed before it could be moved.");
        File.Move(source, destination, overwrite: false);
        if (!RegularFileSha256(server.RootPath, destination).Equals(movedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryRestoreMovedFile(server.RootPath, destination, server.RootPath, source);
            throw new IOException("The selected add-on changed while it was being moved.");
        }
        return new JarMoveReceipt(relativePath, destination, Changed: true, movedSha256);
    }

    /// <summary>
    /// Moves only the selected JAR into ChunkPilot recovery storage. Plugin-owned configuration
    /// directories remain untouched so removal is reversible and user data is preserved.
    /// </summary>
    public void Remove(ServerDefinition server, string relativePath) =>
        _ = RemoveWithReceipt(server, relativePath);

    public JarMoveReceipt RemoveWithReceipt(ServerDefinition server, string relativePath) =>
        WithContentLock(server, () => RemoveWithReceiptCore(server, relativePath));

    private JarMoveReceipt RemoveWithReceiptCore(ServerDefinition server, string relativePath)
    {
        var source = ResolveContentJarPath(server, relativePath, mustExist: true);
        var movedSha256 = RegularFileSha256(server.RootPath, source);
        var recovery = EnsureRecoveryDirectory(
            server, "content", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        var destination = Path.Combine(recovery, Path.GetFileName(source));
        if (PathEntry(destination) != PathEntryKind.Missing)
            destination = Path.Combine(recovery, $"{Path.GetFileNameWithoutExtension(source)}-{Guid.NewGuid():N}.jar");
        EnsureRegularFileUnchanged(server.RootPath, source, movedSha256,
            "The selected add-on changed before it could be removed.");
        File.Move(source, destination, overwrite: false);
        if (!RegularFileSha256(paths.Root, destination).Equals(movedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryRestoreMovedFile(paths.Root, destination, server.RootPath, source);
            throw new IOException("The selected add-on changed while it was being moved to recovery.");
        }
        return new JarMoveReceipt(relativePath, destination, Changed: true, movedSha256);
    }

    public void RollbackMove(ServerDefinition server, JarMoveReceipt receipt) =>
        WithContentLock(server, () => RollbackMoveCore(server, receipt));

    private void RollbackMoveCore(ServerDefinition server, JarMoveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Changed)
            return;
        var destination = Path.GetFullPath(receipt.DestinationPath);
        if (!IsWithin(destination, server.RootPath) && !IsWithin(destination, paths.Recovery))
            throw new UnauthorizedAccessException("The plugin rollback source is outside ChunkPilot-owned storage.");
        if (IsWithin(destination, server.RootPath))
            ValidateRegularFileChain(server.RootPath, destination);
        else
            ValidateRegularFileChain(paths.Root, destination);
        if (receipt.MovedSha256.Length != 64 ||
            !Sha256(destination).Equals(receipt.MovedSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The plugin rollback source changed, so rollback preserved it in place.");
        var source = ResolveContentJarPath(server, receipt.SourceRelativePath, mustExist: false);
        if (PathEntry(source) != PathEntryKind.Missing)
            throw new IOException("The original plugin path is no longer empty, so rollback stopped safely.");
        EnsureSafeExistingParentDirectory(server.RootPath, source);
        File.Move(destination, source, overwrite: false);
        ValidateRegularFileChain(server.RootPath, source);
        if (!RegularFileSha256(server.RootPath, source).Equals(
                receipt.MovedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryRestoreMovedFile(
                server.RootPath, source,
                IsWithin(destination, server.RootPath) ? server.RootPath : paths.Root,
                destination);
            throw new IOException("The plugin rollback source changed at the move boundary and was preserved.");
        }
    }

    private static bool IsSafeConfigIdentity(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 120 &&
        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        value is not "." and not "..";

    private static ModPluginEntry ReadMetadata(ServerDefinition server, string path, bool enabled)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var version = "Unknown";
        var id = "";
        var loader = "Unknown";
        var clientRequirement = "Unknown";
        var dependencies = new List<string>();
        var dependencyDetails = new List<ContentDependency>();
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaximumJarBytes)
                throw new InvalidDataException("The Java archive is too large to inspect safely.");
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count > MaximumArchiveEntries)
                throw new InvalidDataException("The Java archive contains too many entries to inspect safely.");

            if (FindMetadata(archive, "fabric.mod.json") is { } fabric)
            {
                using var document = JsonDocument.Parse(ReadMetadataBytes(fabric), new JsonDocumentOptions
                {
                    MaxDepth = 32,
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                id = document.RootElement.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? "" : "";
                name = document.RootElement.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? name : name;
                version = document.RootElement.TryGetProperty("version", out var versionProperty) ? versionProperty.GetString() ?? version : version;
                loader = "Fabric";
                clientRequirement = document.RootElement.TryGetProperty("environment", out var environment) &&
                                    environment.ValueKind == JsonValueKind.String
                    ? environment.GetString()?.ToLowerInvariant() switch
                    {
                        "server" => "ServerOnly",
                        "client" => "ClientOnly",
                        _ => "Unknown"
                    }
                    : "Unknown";
                AddJsonDependencies(document.RootElement, "depends", ContentDependencyKind.Required, dependencies, dependencyDetails);
                AddJsonDependencies(document.RootElement, "recommends", ContentDependencyKind.Optional, dependencies, dependencyDetails);
                AddJsonDependencies(document.RootElement, "suggests", ContentDependencyKind.Optional, dependencies, dependencyDetails);
                AddJsonDependencies(document.RootElement, "conflicts", ContentDependencyKind.Incompatible, dependencies, dependencyDetails);
                AddJsonDependencies(document.RootElement, "breaks", ContentDependencyKind.Incompatible, dependencies, dependencyDetails);
            }
            else if (FindMetadata(archive, "quilt.mod.json") is { } quilt)
            {
                using var document = JsonDocument.Parse(ReadMetadataBytes(quilt), new JsonDocumentOptions { MaxDepth = 32 });
                var quiltLoader = document.RootElement.GetProperty("quilt_loader");
                var metadata = quiltLoader.GetProperty("metadata");
                name = metadata.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() ?? name : name;
                id = quiltLoader.GetProperty("id").GetString() ?? "";
                version = quiltLoader.GetProperty("version").GetString() ?? version;
                loader = "Quilt";
            }
            else if ((FindMetadata(archive, "paper-plugin.yml") ?? FindMetadata(archive, "plugin.yml")) is { } plugin)
            {
                var yaml = ReadMetadataText(plugin);
                name = YamlValue(yaml, "name") ?? name;
                version = YamlValue(yaml, "version") ?? version;
                id = name;
                loader = plugin.FullName.StartsWith("paper", StringComparison.OrdinalIgnoreCase) ? "Paper" : "Bukkit";
                AddYamlDependencies(yaml, "depend", ContentDependencyKind.Required, dependencies, dependencyDetails);
                AddYamlDependencies(yaml, "softdepend", ContentDependencyKind.Optional, dependencies, dependencyDetails);
                AddYamlDependencies(yaml, "loadbefore", ContentDependencyKind.LoadBefore, dependencies, dependencyDetails);
            }
            else if ((FindMetadata(archive, "META-INF/neoforge.mods.toml") ?? FindMetadata(archive, "META-INF/mods.toml")) is { } modsToml)
            {
                var toml = ReadMetadataText(modsToml);
                id = TomlValueRegex("modId").Match(toml).Groups["value"].Value;
                version = TomlValueRegex("version").Match(toml).Groups["value"].Value;
                name = TomlValueRegex("displayName").Match(toml).Groups["value"].Value is { Length: > 0 } display ? display : name;
                loader = modsToml.FullName.Contains("neoforge", StringComparison.OrdinalIgnoreCase) ? "NeoForge" : "Forge";
                AddNeoForgeDependencies(toml, id, dependencies, dependencyDetails);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or KeyNotFoundException)
        {
            loader = "Unreadable metadata";
        }

        var file = new FileInfo(path);
        var compatibility = Compatibility(server.Ecosystem, loader, clientRequirement);
        return new ModPluginEntry
        {
            Name = name,
            FileName = file.Name,
            RelativePath = Path.GetRelativePath(server.RootPath, path),
            Version = version,
            Id = id,
            Loader = loader,
            SizeBytes = file.Length,
            ModifiedAt = file.LastWriteTimeUtc,
            Enabled = enabled,
            Dependencies = dependencies.Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaximumDependencies).ToArray(),
            DependencyDetails = dependencyDetails
                .Where(value => value.Id.Length > 0)
                .DistinctBy(value => $"{value.Kind}:{value.Id}", StringComparer.OrdinalIgnoreCase)
                .Take(MaximumDependencies).ToArray(),
            Compatibility = compatibility.State,
            CompatibilityReason = compatibility.Reason,
            ClientRequirement = clientRequirement,
            Sha256 = Sha256(path)
        };
    }

    private static void AddJsonDependencies(
        JsonElement root,
        string property,
        ContentDependencyKind kind,
        ICollection<string> dependencies,
        ICollection<ContentDependency> details)
    {
        foreach (var dependency in JsonDependencyKeys(root, property))
        {
            dependencies.Add(dependency);
            details.Add(new ContentDependency(dependency, kind));
        }
    }

    private static void AddYamlDependencies(
        string yaml,
        string property,
        ContentDependencyKind kind,
        ICollection<string> dependencies,
        ICollection<ContentDependency> details)
    {
        foreach (var dependency in YamlList(yaml, property))
        {
            dependencies.Add(dependency);
            details.Add(new ContentDependency(dependency, kind));
        }
    }

    private static void AddNeoForgeDependencies(
        string toml,
        string ownerId,
        ICollection<string> dependencies,
        ICollection<ContentDependency> details)
    {
        if (string.IsNullOrWhiteSpace(ownerId))
            return;
        var header = new Regex($@"(?im)^\s*\[\[\s*dependencies\.{Regex.Escape(ownerId)}\s*\]\]\s*$",
            RegexOptions.CultureInvariant);
        var matches = header.Matches(toml);
        for (var index = 0; index < matches.Count && details.Count < MaximumDependencies; index++)
        {
            var start = matches[index].Index + matches[index].Length;
            var end = index + 1 < matches.Count ? matches[index + 1].Index : toml.Length;
            var block = toml[start..end];
            var dependencyId = TomlValueRegex("modId").Match(block).Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(dependencyId) ||
                dependencyId.Equals("minecraft", StringComparison.OrdinalIgnoreCase) ||
                dependencyId.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ||
                dependencyId.Equals("forge", StringComparison.OrdinalIgnoreCase))
                continue;
            var type = TomlValueRegex("type").Match(block).Groups["value"].Value;
            var mandatory = Regex.Match(block, @"(?im)^\s*mandatory\s*=\s*(?<value>true|false)\s*$",
                RegexOptions.CultureInvariant).Groups["value"].Value;
            var kind = type.ToLowerInvariant() switch
            {
                "optional" => ContentDependencyKind.Optional,
                "incompatible" or "discouraged" => ContentDependencyKind.Incompatible,
                _ when mandatory.Equals("false", StringComparison.OrdinalIgnoreCase) => ContentDependencyKind.Optional,
                _ => ContentDependencyKind.Required
            };
            dependencies.Add(dependencyId);
            details.Add(new ContentDependency(dependencyId, kind));
        }
    }

    private static (CompatibilityState State, string Reason) Compatibility(
        ServerEcosystem ecosystem,
        string loader,
        string clientRequirement)
    {
        if (loader is "Unknown" or "Unreadable metadata")
            return (CompatibilityState.Unknown, "The JAR does not expose readable loader metadata; confirm before installation.");
        if (clientRequirement == "ClientOnly")
            return (CompatibilityState.Incompatible,
                "The add-on declares a client-only Fabric environment and cannot run on a dedicated server.");
        var compatible =
            loader == "Fabric" && ecosystem == ServerEcosystem.Fabric ||
            loader == "Quilt" && ecosystem == ServerEcosystem.Quilt ||
            loader == "Forge" && ecosystem == ServerEcosystem.Forge ||
            loader == "NeoForge" && ecosystem == ServerEcosystem.NeoForge ||
            loader is "Paper" or "Bukkit" && IsPluginEcosystem(ecosystem);
        if (compatible)
            return (CompatibilityState.LikelyCompatible, $"Loader metadata matches the server ecosystem ({ecosystem}); exact Minecraft-version support is not declared.");
        return (CompatibilityState.Incompatible, $"JAR loader '{loader}' does not match server ecosystem '{ecosystem}'.");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private ModPluginEntry ApplyProvenance(
        ModPluginEntry entry,
        IReadOnlyDictionary<string, PluginProvenanceEntry> provenance) =>
        provenance.TryGetValue(entry.Sha256, out var source)
            ? entry with
            {
                InstallSource = source.Provider.ToString(),
                Provider = source.Provider,
                ProviderProjectId = source.ProjectId,
                ProviderVersionId = source.VersionId
            }
            : entry;

    private IReadOnlyDictionary<string, PluginProvenanceEntry> LoadProvenance(Guid serverId) =>
        LoadProvenanceEntries(serverId)
            .GroupBy(entry => entry.Sha256, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.RecordedAt).First(),
                StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<PluginProvenanceEntry> LoadProvenanceEntries(Guid serverId)
    {
        var path = ProvenancePath(serverId);
        try
        {
            _ = ValidateDirectoryChain(paths.Root, paths.PluginProvenance, allowMissing: false);
            ValidateOptionalRegularFileChain(paths.Root, path);
            if (PathEntry(path) == PathEntryKind.Missing || new FileInfo(path).Length > 2 * 1024 * 1024)
                return [];
            return JsonSerializer.Deserialize<IReadOnlyList<PluginProvenanceEntry>>(
                       File.ReadAllText(path, Encoding.UTF8), ProtocolJson.Options) ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return [];
        }
    }

    private string ProvenancePath(Guid serverId) =>
        Path.Combine(paths.PluginProvenance, serverId.ToString("N") + ".json");

    private static bool IsPluginEcosystem(ServerEcosystem ecosystem) =>
        ecosystem is ServerEcosystem.Paper or ServerEcosystem.Purpur or ServerEcosystem.Spigot or
            ServerEcosystem.Bukkit or ServerEcosystem.Hybrid;

    private static bool IsWithin(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var candidate = Path.GetFullPath(path);
        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static ZipArchiveEntry? FindMetadata(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static byte[] ReadMetadataBytes(ZipArchiveEntry entry)
    {
        if (entry.Length < 0 || entry.Length > MaximumMetadataBytes)
            throw new InvalidDataException($"Metadata entry '{entry.FullName}' exceeds the inspection limit.");
        using var source = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        var buffer = new byte[16 * 1024];
        long total = 0;
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total += read;
            if (total > MaximumMetadataBytes)
                throw new InvalidDataException($"Metadata entry '{entry.FullName}' expanded beyond the inspection limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static string ReadMetadataText(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(new MemoryStream(ReadMetadataBytes(entry)), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static IEnumerable<string> JsonDependencyKeys(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var dependencies) || dependencies.ValueKind != JsonValueKind.Object)
            return [];
        return dependencies.EnumerateObject().Select(value => value.Name).Take(MaximumDependencies).ToArray();
    }

    private static IReadOnlyList<string> YamlList(string yaml, string key)
    {
        var raw = YamlValue(yaml, key);
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        var value = raw.Trim();
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];
        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim(' ', '\'', '"')).Where(item => item.Length > 0)
            .Take(MaximumDependencies).ToArray();
    }

    private static string? YamlValue(string yaml, string key)
    {
        var match = Regex.Match(yaml, $@"(?im)^\s*{Regex.Escape(key)}\s*:\s*['""]?(?<value>[^'""#\r\n]+)");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static Regex TomlValueRegex(string key) =>
        new($@"(?im)^\s*{Regex.Escape(key)}\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.CultureInvariant);

    private sealed record PluginProvenanceEntry(
        string Sha256,
        PluginProviderKind Provider,
        string ProjectId,
        string VersionId,
        string VersionName,
        ProviderIdentityOrigin IdentityOrigin,
        DateTimeOffset RecordedAt);
}
