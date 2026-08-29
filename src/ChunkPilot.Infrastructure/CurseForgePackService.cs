using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record CurseForgeManifestFile(long ProjectId, long FileId, bool Required);

public sealed record CurseForgePackManifest(
    string Name,
    string Version,
    string Author,
    string MinecraftVersion,
    InstallSourceType Loader,
    string LoaderVersion,
    string OverridesDirectory,
    IReadOnlyList<CurseForgeManifestFile> Files);

public sealed record CurseForgeMaterializedFile(
    long ProjectId,
    long FileId,
    string RelativePath,
    string ProviderSha1,
    string LocalSha256,
    long SizeBytes,
    bool ManifestEntry,
    string SideEvidence);

internal sealed record CurseForgeInstalledFileEvidence(
    string RelativePath,
    string LocalSha256,
    long SizeBytes,
    bool ManifestEntry,
    string SideEvidence);

public sealed record CurseForgePackLaunchResult(
    CurseForgePackManifest Manifest,
    ServerEcosystem Ecosystem,
    string LoaderVersion,
    string InstallerVersion,
    string LaunchRelativePath,
    bool UsesArgumentFile,
    string LoaderArtifactUrl,
    string LoaderArtifactSha256,
    IReadOnlyList<CurseForgeMaterializedFile> MaterializedFiles,
    IReadOnlyList<string> SkippedOptionalProjects,
    IReadOnlyList<string> IgnoredOverridePaths);

/// <summary>Strict reader for the documented CurseForge manifest.json format.</summary>
public sealed class CurseForgePackManifestReader
{
    public const int MaximumManifestBytes = 1024 * 1024;
    public const int MaximumManifestFiles = 10_000;

    public async Task<CurseForgePackManifest> ReadAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(archivePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The CurseForge pack archive was not found.", fullPath);
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Choose a regular CurseForge archive rather than a link or reparse point.");
        if (info.Length is <= 0 or > ServerImportInspectionService.MaximumCompressedBytes)
            throw new InvalidDataException("The CurseForge archive is empty or exceeds the bounded package limit.");

        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var manifests = archive.Entries.Where(entry =>
            entry.FullName.Replace('\\', '/').Equals("manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1)
            throw new InvalidDataException("A CurseForge pack must contain exactly one root manifest.json.");
        var manifestEntry = manifests[0];
        if (manifestEntry.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("The CurseForge manifest is empty or exceeds the 1 MiB limit.");
        await using var manifestStream = manifestEntry.Open();
        using var bounded = new MemoryStream((int)manifestEntry.Length);
        await manifestStream.CopyToAsync(bounded, cancellationToken).ConfigureAwait(false);
        if (bounded.Length != manifestEntry.Length)
            throw new InvalidDataException("The CurseForge manifest changed while it was read.");
        bounded.Position = 0;
        using var document = await JsonDocument.ParseAsync(bounded,
            new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow },
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !Text(root, "manifestType").Equals("minecraftModpack", StringComparison.OrdinalIgnoreCase) ||
            Number(root, "manifestVersion") != 1)
            throw new InvalidDataException("The archive does not contain a supported CurseForge Minecraft modpack manifest.");

        var name = RequiredText(root, "name", 200);
        var version = RequiredText(root, "version", 200);
        var author = Text(root, "author");
        if (author.Length > 300) throw new InvalidDataException("The CurseForge manifest author is too long.");
        var overrides = ValidateRelativeDirectory(RequiredText(root, "overrides", 512));
        if (!root.TryGetProperty("minecraft", out var minecraft) || minecraft.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The CurseForge manifest has no exact Minecraft identity.");
        var minecraftVersion = RequiredText(minecraft, "version", 80);
        if (!minecraft.TryGetProperty("modLoaders", out var loaders) || loaders.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The CurseForge manifest has no exact loader identity.");
        var loaderCandidates = loaders.EnumerateArray().Select(ParseLoader).ToArray();
        var primary = loaderCandidates.Where(loader => loader.Primary).ToArray();
        if (primary.Length != 1)
            throw new InvalidDataException("The CurseForge manifest must declare exactly one primary supported loader.");
        var selected = ParseLoaderIdentity(primary[0].Id);

        if (!root.TryGetProperty("files", out var fileValues) || fileValues.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The CurseForge manifest has no exact project/file inventory.");
        var entries = fileValues.EnumerateArray().ToArray();
        if (entries.Length is <= 0 or > MaximumManifestFiles)
            throw new InvalidDataException($"The CurseForge manifest must contain from 1 through {MaximumManifestFiles:N0} files.");
        var files = new List<CurseForgeManifestFile>(entries.Length);
        var identities = new HashSet<(long Project, long File)>();
        var projects = new HashSet<long>();
        foreach (var value in entries)
        {
            var projectId = Number(value, "projectID");
            var fileId = Number(value, "fileID");
            if (projectId is null or <= 0 || fileId is null or <= 0)
                throw new InvalidDataException("Every CurseForge manifest entry must contain positive exact projectID and fileID values.");
            if (!identities.Add((projectId.Value, fileId.Value)) || !projects.Add(projectId.Value))
                throw new InvalidDataException("The CurseForge manifest contains a duplicate or contradictory project/file entry.");
            var required = !value.TryGetProperty("required", out var requiredValue) ||
                           requiredValue.ValueKind == JsonValueKind.True;
            if (value.TryGetProperty("required", out requiredValue) &&
                requiredValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("A CurseForge manifest file has an invalid required flag.");
            files.Add(new CurseForgeManifestFile(projectId.Value, fileId.Value, required));
        }
        return new CurseForgePackManifest(name, version, author, minecraftVersion, selected.Loader,
            selected.Version, overrides, files);
    }

    private static (string Id, bool Primary) ParseLoader(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("A CurseForge loader entry is malformed.");
        var id = RequiredText(value, "id", 160);
        var primary = value.TryGetProperty("primary", out var primaryValue) && primaryValue.ValueKind == JsonValueKind.True;
        return (id, primary);
    }

    private static (InstallSourceType Loader, string Version) ParseLoaderIdentity(string id)
    {
        var separator = id.IndexOf('-');
        if (separator <= 0 || separator == id.Length - 1)
            throw new InvalidDataException("The CurseForge manifest loader does not contain an exact version.");
        var family = id[..separator].ToLowerInvariant();
        var version = id[(separator + 1)..].Trim();
        var loader = family switch
        {
            "forge" => InstallSourceType.Forge,
            "neoforge" => InstallSourceType.NeoForge,
            "fabric" => InstallSourceType.Fabric,
            "quilt" => InstallSourceType.Quilt,
            _ => throw new InvalidDataException("The CurseForge manifest uses an unsupported server loader.")
        };
        if (version.Length is 0 or > 120 || version.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
            throw new InvalidDataException("The CurseForge manifest loader version is invalid.");
        return (loader, version);
    }

    private static string ValidateRelativeDirectory(string value)
    {
        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0 || Path.IsPathRooted(normalized) || normalized.Contains(':'))
            throw new InvalidDataException("The CurseForge override directory is unsafe.");
        foreach (var segment in normalized.Split('/'))
            if (segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException("The CurseForge override directory is unsafe.");
        return normalized;
    }

    private static string RequiredText(JsonElement value, string property, int maximum)
    {
        var result = Text(value, property).Trim();
        if (result.Length is 0 || result.Length > maximum || result.Any(char.IsControl))
            throw new InvalidDataException($"The CurseForge manifest field '{property}' is missing or invalid.");
        return result;
    }

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? "" : "";

    private static long? Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.TryGetInt64(out var number) ? number : null;
}

/// <summary>
/// Builds a dedicated-server candidate from an exact CurseForge client manifest. Pack-provided
/// scripts and executable launchers are never copied or executed; only narrowly server-safe override
/// roots and exact provider-verified JARs enter the candidate.
/// </summary>
public sealed class CurseForgePackService
{
    private const int MaximumResolvedFiles = 2_000;
    private const long MaximumResolvedBytes = 8L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> AllowedOverrideRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "defaultconfigs"
    };

    private readonly CurseForgePackManifestReader reader;
    private readonly CurseForgePluginProvider mods;
    private readonly CurseForgeApiClient api;
    private readonly LoaderInstallationService loaders;

    public CurseForgePackService(
        CurseForgeApiClient api,
        CurseForgePackManifestReader? reader = null,
        CurseForgePluginProvider? mods = null,
        LoaderInstallationService? loaders = null)
    {
        this.api = api;
        this.reader = reader ?? new CurseForgePackManifestReader();
        this.mods = mods ?? new CurseForgePluginProvider(api);
        this.loaders = loaders ?? new LoaderInstallationService(new LoaderMetadataService());
    }

    public Task<CurseForgePackManifest> InspectAsync(string archivePath, CancellationToken cancellationToken = default) =>
        reader.ReadAsync(archivePath, cancellationToken);

    public async Task<CurseForgePackLaunchResult> MaterializeAndInstallAsync(
        string archivePath,
        string destinationRoot,
        string javaPath,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        if (!api.HasCredential)
            throw new InvalidOperationException("CurseForge is unavailable because the approved local native credential is missing.");
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (!Directory.Exists(destination) || Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The generated CurseForge candidate must start in an empty operation-owned staging directory.");
        var manifest = await reader.ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var isolated = destination + $".cfpack-materialized-{Guid.NewGuid():N}";
        Directory.CreateDirectory(isolated);
        try
        {
            await ServerImportInspectionService.ExtractAsync(archivePath, isolated, cancellationToken).ConfigureAwait(false);
            var ignoredOverrides = await CopySafeOverridesAsync(isolated, destination, manifest.OverridesDirectory,
                cancellationToken).ConfigureAwait(false);
            var (resolved, optional) = await ResolveFilesAsync(manifest, cancellationToken).ConfigureAwait(false);
            var materialized = await DownloadFilesAsync(resolved, destination, cancellationToken).ConfigureAwait(false);
            var installed = await loaders.InstallAsync(manifest.Loader, manifest.MinecraftVersion,
                manifest.LoaderVersion, javaPath, destination, logPath, cancellationToken).ConfigureAwait(false);
            var target = string.IsNullOrWhiteSpace(installed.ArgumentsFile)
                ? installed.LaunchFile : installed.ArgumentsFile;
            var evidenceRoot = Path.Combine(destination, ".chunkpilot");
            Directory.CreateDirectory(evidenceRoot);
            var evidence = new
            {
                schemaVersion = 1,
                provider = "CurseForge",
                manifest.MinecraftVersion,
                loader = manifest.Loader.ToString(),
                manifest.LoaderVersion,
                generatedAtUtc = DateTimeOffset.UtcNow,
                validationState = "pending-first-launch",
                materializedFiles = materialized
                    .Select(file => CreateInstalledFileEvidence(destination, file))
                    .ToArray(),
                skippedOptionalProjectCount = optional.Count,
                ignoredOverridePathCount = ignoredOverrides.Count
            };
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, "curseforge-pack-evidence.json"),
                JsonSerializer.Serialize(evidence, ProtocolJson.Options), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            return new CurseForgePackLaunchResult(manifest, ToEcosystem(manifest.Loader), manifest.LoaderVersion,
                installed.InstallerVersion, Path.GetRelativePath(destination, target),
                !string.IsNullOrWhiteSpace(installed.ArgumentsFile), installed.ArtifactUrl,
                installed.DownloadSha256, materialized, optional, ignoredOverrides);
        }
        finally
        {
            TryDeleteOwnedDirectory(isolated);
        }
    }

    private async Task<(IReadOnlyList<(PluginRelease Release, bool ManifestEntry)> Releases,
        IReadOnlyList<string> Optional)> ResolveFilesAsync(
        CurseForgePackManifest manifest,
        CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, (PluginRelease Release, bool ManifestEntry)>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var optional = new HashSet<string>(StringComparer.Ordinal);
        var incompatible = new List<(string Owner, string Target)>();
        foreach (var entry in manifest.Files)
        {
            if (!entry.Required)
            {
                optional.Add($"{entry.ProjectId}/{entry.FileId}");
                continue;
            }
            await VisitAsync(entry.ProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                entry.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture), true).ConfigureAwait(false);
        }
        foreach (var relation in incompatible)
            if (resolved.ContainsKey(relation.Target))
                throw new InvalidDataException(
                    $"CurseForge project {relation.Owner} declares project {relation.Target} incompatible with this candidate.");
        return (resolved.Values.ToArray(), optional.Order(StringComparer.Ordinal).ToArray());

        async Task VisitAsync(string projectId, string? fileId, bool manifestEntry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (resolved.ContainsKey(projectId)) return;
            if (resolved.Count >= MaximumResolvedFiles)
                throw new InvalidDataException($"The CurseForge dependency graph exceeds {MaximumResolvedFiles:N0} exact files.");
            if (!visiting.Add(projectId)) return; // Cycle: the exact project is already being resolved once.
            try
            {
                var release = await mods.ResolveReleaseAsync(projectId, manifest.MinecraftVersion,
                    manifest.Loader.ToString(), fileId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Required CurseForge project/file {projectId}/{fileId ?? "compatible release"} is unavailable, restricted, or incompatible.");
                if (!release.ProjectId.Equals(projectId, StringComparison.Ordinal) ||
                    fileId is not null && !release.VersionId.Equals(fileId, StringComparison.Ordinal))
                    throw new InvalidDataException("CurseForge returned contradictory exact dependency identity.");
                resolved.Add(projectId, (release, manifestEntry));
                foreach (var dependency in release.Dependencies)
                {
                    switch (dependency.Type.ToLowerInvariant())
                    {
                        case "required":
                            await VisitAsync(dependency.ProjectId,
                                string.IsNullOrWhiteSpace(dependency.VersionId) ? null : dependency.VersionId,
                                false).ConfigureAwait(false);
                            break;
                        case "optional":
                            optional.Add(dependency.ProjectId);
                            break;
                        case "incompatible":
                            incompatible.Add((projectId, dependency.ProjectId));
                            break;
                        case "embedded":
                        case "include":
                        case "tool":
                            break;
                        default:
                            throw new InvalidDataException(
                                $"CurseForge returned an unknown dependency relationship for project {projectId}.");
                    }
                }
            }
            finally
            {
                visiting.Remove(projectId);
            }
        }
    }

    internal static CurseForgeInstalledFileEvidence CreateInstalledFileEvidence(
        string destinationRoot,
        CurseForgeMaterializedFile file)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var relative = PersistentDataClassifier.Normalize(file.RelativePath);
        if (Path.IsPathFullyQualified(relative))
            throw new InvalidDataException("CurseForge installed-file evidence requires a relative path.");
        var installedPath = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureChild(root, installedPath);
        var installed = new FileInfo(installedPath);
        if (!installed.Exists || installed.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("CurseForge installed-file evidence requires a regular installed file.");
        return new CurseForgeInstalledFileEvidence(relative, file.LocalSha256, installed.Length,
            file.ManifestEntry, file.SideEvidence);
    }

    private async Task<IReadOnlyList<CurseForgeMaterializedFile>> DownloadFilesAsync(
        IReadOnlyList<(PluginRelease Release, bool ManifestEntry)> releases,
        string destination,
        CancellationToken cancellationToken)
    {
        var modsRoot = Path.Combine(destination, "mods");
        Directory.CreateDirectory(modsRoot);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CurseForgeMaterializedFile>(releases.Count);
        long totalBytes = 0;
        foreach (var item in releases.OrderBy(item => item.Release.ProjectId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var release = item.Release;
            if (!names.Add(release.FileName))
                throw new InvalidDataException($"CurseForge files collide on the Windows destination name {release.FileName}.");
            totalBytes = checked(totalBytes + release.SizeBytes);
            if (totalBytes > MaximumResolvedBytes)
                throw new InvalidDataException("The generated CurseForge candidate exceeds the 8 GiB resolved-file limit.");
            if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) ||
                !CurseForgeApiClient.IsApprovedDownloadUri(uri))
                throw new InvalidDataException("CurseForge returned an unapproved mod download destination.");
            var target = Path.Combine(modsRoot, release.FileName);
            using var response = await api.SendDownloadAsync(uri, cancellationToken).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } declared && declared != release.SizeBytes)
                throw new InvalidDataException($"CurseForge file {release.VersionId} changed size after review.");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[128 * 1024];
                long copied = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    copied = checked(copied + read);
                    if (copied > release.SizeBytes)
                        throw new InvalidDataException($"CurseForge file {release.VersionId} exceeded its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (copied != release.SizeBytes)
                    throw new InvalidDataException($"CurseForge file {release.VersionId} ended before its declared size.");
            }
#pragma warning disable CA5350 // CurseForge publishes SHA-1 as provider identity; every accepted file also receives a local SHA-256 baseline below.
            var actualSha1 = Hash(target, SHA1.Create());
#pragma warning restore CA5350
            if (!actualSha1.Equals(release.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new InvalidDataException($"CurseForge SHA-1 verification failed for exact file {release.VersionId}.");
            }
            var localSha256 = Hash(target, SHA256.Create());
            result.Add(new CurseForgeMaterializedFile(long.Parse(release.ProjectId,
                    System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(release.VersionId, System.Globalization.CultureInfo.InvariantCulture),
                PersistentDataClassifier.Normalize(Path.GetRelativePath(destination, target)), release.Sha1,
                localSha256, new FileInfo(target).Length, item.ManifestEntry, "unknown-until-staged-validation"));
        }
        return result;
    }

    private static async Task<IReadOnlyList<string>> CopySafeOverridesAsync(
        string extractedRoot,
        string destination,
        string overrideDirectory,
        CancellationToken cancellationToken)
    {
        var sourceRoot = Path.GetFullPath(Path.Combine(extractedRoot,
            overrideDirectory.Replace('/', Path.DirectorySeparatorChar)));
        EnsureChild(extractedRoot, sourceRoot);
        if (!Directory.Exists(sourceRoot))
            throw new InvalidDataException("The exact CurseForge override directory is missing from the archive.");
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("CurseForge overrides cannot contain links or reparse points.");
            var relative = PersistentDataClassifier.Normalize(Path.GetRelativePath(sourceRoot, file));
            var first = relative.Split('/', 2)[0];
            if (!AllowedOverrideRoots.Contains(first))
            {
                ignored.Add(first);
                continue;
            }
            var target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            EnsureChild(destination, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        return ignored.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Hash(string path, HashAlgorithm algorithm)
    {
        using (algorithm)
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   128 * 1024, FileOptions.SequentialScan))
            return Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
    }

    private static ServerEcosystem ToEcosystem(InstallSourceType loader) => loader switch
    {
        InstallSourceType.Fabric => ServerEcosystem.Fabric,
        InstallSourceType.Quilt => ServerEcosystem.Quilt,
        InstallSourceType.Forge => ServerEcosystem.Forge,
        InstallSourceType.NeoForge => ServerEcosystem.NeoForge,
        _ => throw new InvalidDataException("The CurseForge pack loader is unsupported.")
    };

    private static void EnsureChild(string root, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A CurseForge pack path escaped operation-owned staging.");
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        if (!Path.GetFileName(path).Contains(".cfpack-materialized-", StringComparison.Ordinal)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
