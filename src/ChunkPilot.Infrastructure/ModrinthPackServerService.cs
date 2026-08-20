using ChunkPilot.Core;
using System.Security.Cryptography;

namespace ChunkPilot.Infrastructure;

public sealed record ModrinthPackLaunchResult(
    ModrinthPackManifest Manifest,
    InstallSourceType Loader,
    ServerEcosystem Ecosystem,
    string LoaderVersion,
    string InstallerVersion,
    string LaunchRelativePath,
    bool UsesArgumentFile,
    string ArtifactUrl,
    string ArtifactSha256,
    IReadOnlyList<ModrinthMaterializedFile> MaterializedFiles,
    IReadOnlyList<string> SkippedOptionalFiles,
    IReadOnlyList<string> SkippedUnsupportedFiles);

/// <summary>
/// Converts a validated Modrinth pack archive into a runnable server candidate. It never executes
/// pack-provided scripts and delegates the exact loader installation to the existing official
/// loader service.
/// </summary>
public sealed class ModrinthPackServerService
{
    private readonly ModrinthPackReader reader;
    private readonly ModrinthServerPackMaterializer materializer;
    private readonly LoaderInstallationService loaderInstaller;

    public ModrinthPackServerService(
        ModrinthPackReader? reader = null,
        ModrinthServerPackMaterializer? materializer = null,
        LoaderInstallationService? loaderInstaller = null)
    {
        this.reader = reader ?? new ModrinthPackReader();
        this.materializer = materializer ?? new ModrinthServerPackMaterializer(this.reader);
        this.loaderInstaller = loaderInstaller ?? new LoaderInstallationService(new LoaderMetadataService());
    }

    public async Task<ModrinthPackInspection> InspectAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        var archive = await reader.ReadAsync(archivePath, cancellationToken).ConfigureAwait(false);
        var loader = ResolveLoader(archive.Manifest);
        var minecraft = archive.Manifest.Dependencies["minecraft"];
        var java = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft);
        var required = archive.Manifest.Files.Where(file =>
            file.ServerEnvironment == ModrinthPackEnvironmentSupport.Required).ToArray();
        var optional = archive.Manifest.Files.Count(file =>
            file.ServerEnvironment == ModrinthPackEnvironmentSupport.Optional);
        var excluded = archive.Manifest.Files.Count(file =>
            file.ServerEnvironment == ModrinthPackEnvironmentSupport.Unsupported);
        await using var archiveStream = new FileStream(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var archiveSize = archiveStream.Length;
        var archiveSha512 = Convert.ToHexString(
            await SHA512.HashDataAsync(archiveStream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return new ModrinthPackInspection
        {
            Name = archive.Manifest.Name,
            VersionName = archive.Manifest.VersionId,
            Summary = archive.Manifest.Summary,
            MinecraftVersion = minecraft,
            Loader = loader.Loader.ToString(),
            LoaderVersion = loader.Version,
            RequiredJavaMajor = java,
            RequiredServerFiles = required.Length,
            OptionalServerFiles = optional,
            ExcludedClientFiles = excluded,
            IndexedServerBytes = required.Sum(file => file.FileSize),
            ArchiveSha512 = archiveSha512,
            ArchiveSizeBytes = archiveSize,
            CanCreate = true
        };
    }

    public async Task<ModrinthPackLaunchResult> MaterializeAndInstallAsync(
        string archivePath,
        string destinationRoot,
        string javaPath,
        string logPath,
        IProgress<ModrinthMaterializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException(destination);
        if (Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException("The Modrinth server-pack candidate must start in an empty staging directory.");

        var isolated = destination + $".mrpack-materialized-{Guid.NewGuid():N}";
        ModrinthPackMaterializationResult pack;
        using (var downloads = new ModrinthPackHttpDownloadSource())
        {
            pack = await materializer.MaterializeAsync(
                archivePath,
                isolated,
                downloads,
                new ModrinthPackMaterializationOptions
                {
                    IncludeOptionalServerFiles = false,
                    MaximumConcurrentDownloads = 4
                },
                progress,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var loader = ResolveLoader(pack.Manifest);
            var minecraft = pack.Manifest.Dependencies["minecraft"];
            var requiredJava = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft);
            if (requiredJava <= 0)
                throw new InvalidDataException($"The Java requirement for Minecraft {minecraft} is unknown.");

            MoveMaterializedContents(isolated, destination);
            var installed = await loaderInstaller.InstallAsync(
                loader.Loader,
                minecraft,
                loader.Version,
                javaPath,
                destination,
                logPath,
                cancellationToken).ConfigureAwait(false);
            var target = string.IsNullOrWhiteSpace(installed.ArgumentsFile)
                ? installed.LaunchFile
                : installed.ArgumentsFile;
            return new ModrinthPackLaunchResult(
                pack.Manifest,
                loader.Loader,
                ToEcosystem(loader.Loader),
                loader.Version,
                installed.InstallerVersion,
                Path.GetRelativePath(destination, target),
                !string.IsNullOrWhiteSpace(installed.ArgumentsFile),
                installed.ArtifactUrl,
                installed.DownloadSha256,
                pack.Files,
                pack.SkippedOptionalFiles,
                pack.SkippedUnsupportedFiles);
        }
        finally
        {
            TryDeleteOwnedDirectory(isolated);
        }
    }

    public static (InstallSourceType Loader, string Version) ResolveLoader(ModrinthPackManifest manifest)
    {
        var candidates = new List<(InstallSourceType Loader, string Version)>();
        Add("fabric-loader", InstallSourceType.Fabric);
        Add("quilt-loader", InstallSourceType.Quilt);
        Add("forge", InstallSourceType.Forge);
        Add("neoforge", InstallSourceType.NeoForge);
        if (candidates.Count != 1)
            throw new InvalidDataException(candidates.Count == 0
                ? "The Modrinth pack does not declare one supported server loader."
                : "The Modrinth pack declares multiple server loaders and cannot be installed safely.");
        return candidates[0];

        void Add(string key, InstallSourceType loader)
        {
            if (manifest.Dependencies.TryGetValue(key, out var version) && !string.IsNullOrWhiteSpace(version))
                candidates.Add((loader, version));
        }
    }

    private static ServerEcosystem ToEcosystem(InstallSourceType loader) => loader switch
    {
        InstallSourceType.Fabric => ServerEcosystem.Fabric,
        InstallSourceType.Quilt => ServerEcosystem.Quilt,
        InstallSourceType.Forge => ServerEcosystem.Forge,
        InstallSourceType.NeoForge => ServerEcosystem.NeoForge,
        _ => throw new ArgumentOutOfRangeException(nameof(loader), loader, "Unsupported Modrinth pack loader.")
    };

    private static void MoveMaterializedContents(string source, string destination)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
                throw new IOException($"A staged pack path already exists: {Path.GetFileName(entry)}");
            if (Directory.Exists(entry))
                Directory.Move(entry, target);
            else
                File.Move(entry, target);
        }
        Directory.Delete(source);
    }

    private static void TryDeleteOwnedDirectory(string path)
    {
        if (!Path.GetFileName(path).Contains(".mrpack-materialized-", StringComparison.Ordinal))
            return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
