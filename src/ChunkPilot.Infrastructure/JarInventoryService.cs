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
    string? RecoveryPath);

public sealed record JarMoveReceipt(
    string SourceRelativePath,
    string DestinationPath,
    bool Changed);

public sealed partial class JarInventoryService
{
    internal const long MaximumJarBytes = 512L * 1024 * 1024;
    internal const long MaximumMetadataBytes = 512L * 1024;
    internal const int MaximumArchiveEntries = 20_000;
    internal const int MaximumDependencies = 256;
    private readonly SafeFileService files;
    private readonly AppDataPaths paths;

    public JarInventoryService(SafeFileService files, AppDataPaths paths)
    {
        this.files = files;
        this.paths = paths;
    }

    public IReadOnlyList<ModPluginEntry> Inventory(ServerDefinition server)
    {
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var folder = Path.Combine(server.RootPath, folderName);
        var disabledFolder = Path.Combine(server.RootPath, ".chunkpilot-disabled", folderName);
        if (!Directory.Exists(folder) && !Directory.Exists(disabledFolder))
            return [];

        var active = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.jar", SearchOption.TopDirectoryOnly) : [];
        var disabled = Directory.Exists(disabledFolder) ? Directory.EnumerateFiles(disabledFolder, "*.jar", SearchOption.TopDirectoryOnly) : [];
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

        _ = files.ResolveWithinRoot(server.RootPath, configRelativePath, mustExist: true);
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
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceJar) || !Path.GetExtension(sourceJar).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local .jar file.");
        var incoming = ReadMetadata(server with { RootPath = Path.GetDirectoryName(sourceJar)! }, sourceJar, enabled: true);
        if (incoming.Compatibility == CompatibilityState.Incompatible)
            throw new InvalidOperationException(incoming.CompatibilityReason);
        var replacement = string.IsNullOrWhiteSpace(replaceRelativePath)
            ? null
            : files.ResolveWithinRoot(server.RootPath, replaceRelativePath, mustExist: true);
        var duplicate = Inventory(server).FirstOrDefault(entry =>
            incoming.Id.Length > 0 && entry.Id.Equals(incoming.Id, StringComparison.OrdinalIgnoreCase) &&
            !entry.FileName.Equals(Path.GetFileName(sourceJar), StringComparison.OrdinalIgnoreCase) &&
            (replacement is null || !files.ResolveWithinRoot(server.RootPath, entry.RelativePath, mustExist: true)
                .Equals(replacement, StringComparison.OrdinalIgnoreCase)));
        if (duplicate is not null)
            throw new InvalidOperationException($"A different file already provides ID '{incoming.Id}': {duplicate.RelativePath}");
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var destinationDirectory = Path.Combine(server.RootPath, folderName);
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(sourceJar));
        if (replacement is not null &&
            !IsDirectChild(replacement, destinationDirectory) &&
            !IsDirectChild(replacement, Path.Combine(server.RootPath, ".chunkpilot-disabled", folderName)))
            throw new InvalidOperationException("Only a top-level managed mod or plugin JAR can be replaced.");
        if (replacement is not null && File.Exists(destination) &&
            !destination.Equals(replacement, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"A different plugin JAR already uses the update filename: {Path.GetFileName(destination)}");
        string? recoveryPath = null;
        string? previousRelativePath = null;
        if (File.Exists(destination))
        {
            var recovery = Path.Combine(paths.Recovery, server.Id.ToString("N"), DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(recovery);
            recoveryPath = Path.Combine(recovery, Path.GetFileName(destination));
            File.Copy(destination, recoveryPath, overwrite: false);
            previousRelativePath = Path.GetRelativePath(server.RootPath, destination);
        }
        await using var input = new FileStream(sourceJar, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        var temporary = destination + $".chunkpilot-{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            if (File.Exists(destination))
                File.Replace(temporary, destination, null, ignoreMetadataErrors: true);
            else
            {
                string? recoveredReplacement = null;
                if (replacement is not null && File.Exists(replacement))
                {
                    var recovery = Path.Combine(paths.Recovery, server.Id.ToString("N"), "content",
                        DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                    Directory.CreateDirectory(recovery);
                    recoveredReplacement = Path.Combine(recovery, Path.GetFileName(replacement));
                    File.Move(replacement, recoveredReplacement);
                    recoveryPath = recoveredReplacement;
                    previousRelativePath = Path.GetRelativePath(server.RootPath, replacement);
                }
                try
                {
                    File.Move(temporary, destination);
                }
                catch
                {
                    if (recoveredReplacement is not null && replacement is not null &&
                        File.Exists(recoveredReplacement) && !File.Exists(replacement))
                        File.Move(recoveredReplacement, replacement);
                    throw;
                }
            }
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new JarInstallReceipt(
            Path.GetRelativePath(server.RootPath, destination),
            previousRelativePath,
            recoveryPath);
    }

    public void RollbackInstall(ServerDefinition server, JarInstallReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var applied = files.ResolveWithinRoot(server.RootPath, receipt.AppliedRelativePath, mustExist: false);
        var rollbackFolder = Path.Combine(paths.Recovery, server.Id.ToString("N"), "failed-plugin-activation",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(rollbackFolder);
        if (File.Exists(applied))
        {
            var failed = Path.Combine(rollbackFolder, Path.GetFileName(applied));
            if (File.Exists(failed))
                failed = Path.Combine(rollbackFolder,
                    $"{Path.GetFileNameWithoutExtension(applied)}-{Guid.NewGuid():N}.jar");
            File.Move(applied, failed);
        }

        if (string.IsNullOrWhiteSpace(receipt.PreviousRelativePath) ||
            string.IsNullOrWhiteSpace(receipt.RecoveryPath))
            return;
        var recovery = Path.GetFullPath(receipt.RecoveryPath);
        if (!IsWithin(recovery, paths.Recovery) || !File.Exists(recovery))
            throw new InvalidOperationException("The known-good plugin recovery copy is unavailable.");
        var previous = files.ResolveWithinRoot(server.RootPath, receipt.PreviousRelativePath, mustExist: false);
        if (File.Exists(previous))
            throw new IOException("The previous plugin path is no longer empty, so rollback stopped safely.");
        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
        File.Move(recovery, previous);
    }

    public void RecordProviderProvenance(ServerDefinition server, string sourceJar, PluginRelease release)
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
            release.VersionName,
            DateTimeOffset.UtcNow));
        if (entries.Count > 5_000)
            entries = entries.OrderByDescending(entry => entry.RecordedAt).Take(5_000).ToList();
        var path = ProvenancePath(server.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.partial";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, ProtocolJson.Options), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    public ModPluginEntry Inspect(ServerDefinition server, string sourceJar)
    {
        if (!File.Exists(sourceJar) || !Path.GetExtension(sourceJar).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Select a local .jar file.");
        return ReadMetadata(server with { RootPath = Path.GetDirectoryName(sourceJar)! }, sourceJar, enabled: true);
    }

    public void SetEnabled(ServerDefinition server, string relativePath, bool enabled) =>
        _ = SetEnabledWithReceipt(server, relativePath, enabled);

    public JarMoveReceipt SetEnabledWithReceipt(ServerDefinition server, string relativePath, bool enabled)
    {
        var source = files.ResolveWithinRoot(server.RootPath, relativePath, mustExist: true);
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var activeDirectory = Path.Combine(server.RootPath, folderName);
        var disabledDirectory = Path.Combine(server.RootPath, ".chunkpilot-disabled", folderName);
        Directory.CreateDirectory(activeDirectory);
        Directory.CreateDirectory(disabledDirectory);
        var sourceInDisabled = source.StartsWith(
            Path.TrimEndingDirectorySeparator(disabledDirectory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
        var destination = enabled
            ? Path.Combine(activeDirectory, Path.GetFileName(source))
            : Path.Combine(disabledDirectory, Path.GetFileName(source));
        if (enabled == !sourceInDisabled || source.Equals(destination, StringComparison.OrdinalIgnoreCase))
            return new JarMoveReceipt(relativePath, destination, Changed: false);
        if (File.Exists(destination))
            throw new IOException($"The destination already exists: {destination}");
        File.Move(source, destination);
        return new JarMoveReceipt(relativePath, destination, Changed: true);
    }

    /// <summary>
    /// Moves only the selected JAR into ChunkPilot recovery storage. Plugin-owned configuration
    /// directories remain untouched so removal is reversible and user data is preserved.
    /// </summary>
    public void Remove(ServerDefinition server, string relativePath) =>
        _ = RemoveWithReceipt(server, relativePath);

    public JarMoveReceipt RemoveWithReceipt(ServerDefinition server, string relativePath)
    {
        var source = files.ResolveWithinRoot(server.RootPath, relativePath, mustExist: true);
        var folderName = IsPluginEcosystem(server.Ecosystem) ? "plugins" : "mods";
        var activeDirectory = Path.Combine(server.RootPath, folderName);
        var disabledDirectory = Path.Combine(server.RootPath, ".chunkpilot-disabled", folderName);
        if (!IsDirectChild(source, activeDirectory) && !IsDirectChild(source, disabledDirectory))
            throw new InvalidOperationException("Only a top-level managed mod or plugin JAR can be removed.");
        if (!Path.GetExtension(source).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a Java archive can be removed from content inventory.");

        var recovery = Path.Combine(paths.Recovery, server.Id.ToString("N"), "content",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(recovery);
        var destination = Path.Combine(recovery, Path.GetFileName(source));
        if (File.Exists(destination))
            destination = Path.Combine(recovery, $"{Path.GetFileNameWithoutExtension(source)}-{Guid.NewGuid():N}.jar");
        File.Move(source, destination);
        return new JarMoveReceipt(relativePath, destination, Changed: true);
    }

    public void RollbackMove(ServerDefinition server, JarMoveReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.Changed)
            return;
        var destination = Path.GetFullPath(receipt.DestinationPath);
        if (!IsWithin(destination, server.RootPath) && !IsWithin(destination, paths.Recovery))
            throw new UnauthorizedAccessException("The plugin rollback source is outside ChunkPilot-owned storage.");
        if (!File.Exists(destination))
            throw new FileNotFoundException("The plugin rollback source no longer exists.", destination);
        var source = files.ResolveWithinRoot(server.RootPath, receipt.SourceRelativePath, mustExist: false);
        if (File.Exists(source))
            throw new IOException("The original plugin path is no longer empty, so rollback stopped safely.");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.Move(destination, source);
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
            if (!File.Exists(path) || new FileInfo(path).Length > 2 * 1024 * 1024)
                return [];
            return JsonSerializer.Deserialize<IReadOnlyList<PluginProvenanceEntry>>(
                       File.ReadAllText(path, Encoding.UTF8), ProtocolJson.Options) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private string ProvenancePath(Guid serverId) =>
        Path.Combine(paths.PluginProvenance, serverId.ToString("N") + ".json");

    private static bool IsPluginEcosystem(ServerEcosystem ecosystem) =>
        ecosystem is ServerEcosystem.Paper or ServerEcosystem.Purpur or ServerEcosystem.Spigot or
            ServerEcosystem.Bukkit or ServerEcosystem.Hybrid;

    private static bool IsDirectChild(string path, string directory) =>
        Path.GetDirectoryName(path)?.Equals(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase) == true;

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
        DateTimeOffset RecordedAt);
}
