using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class DatapackManagementService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly BackupService backups;
    private readonly DatapackService inspection;
    private readonly SafeFileService files;
    private readonly CanonicalPathLockManager pathLocks;

    public DatapackManagementService(
        AppDataPaths paths,
        ChunkPilotStore store,
        BackupService backups,
        DatapackService inspection,
        SafeFileService files,
        CanonicalPathLockManager pathLocks)
    {
        this.paths = paths;
        this.store = store;
        this.backups = backups;
        this.inspection = inspection;
        this.files = files;
        this.pathLocks = pathLocks;
    }

    public async Task<DatapackInventoryItem> InstallAsync(
        ServerDefinition server,
        DatapackInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var world = ResolveWorld(server.RootPath, request.WorldName);
        if (!File.Exists(Path.Combine(world, "level.dat")))
            throw new InvalidDataException("The selected world does not contain level.dat.");
        var inspected = inspection.Inspect(request.SourcePath, server.MinecraftVersion);
        if (!inspected.Valid || inspected.Compatibility == CompatibilityState.Incompatible)
            throw new InvalidDataException(inspected.Detail);

        await using var pathLock = await pathLocks.AcquireAsync(
            Path.Combine(world, "datapacks"), cancellationToken).ConfigureAwait(false);
        _ = await backups.CreateAsync(
            server, backups.GetDefaultProfile(server), "Before datapack installation", cancellationToken)
            .ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var staging = Path.Combine(paths.Staging, $"datapack-{operationId:N}");
        Directory.CreateDirectory(staging);
        string? target = null;
        string? recovered = null;
        try
        {
            var sourceName = SafeName(
                Directory.Exists(request.SourcePath)
                    ? new DirectoryInfo(request.SourcePath).Name
                    : Path.GetFileName(request.SourcePath));
            var staged = Path.Combine(staging, sourceName);
            if (Directory.Exists(request.SourcePath))
                await CopyDirectoryAsync(request.SourcePath, staged, cancellationToken)
                    .ConfigureAwait(false);
            else
                File.Copy(request.SourcePath, staged, overwrite: false);

            var datapacks = Path.Combine(world, "datapacks");
            Directory.CreateDirectory(datapacks);
            target = Path.Combine(datapacks, sourceName);
            if (File.Exists(target) || Directory.Exists(target))
            {
                if (!request.ReplaceExisting)
                    throw new IOException(
                        $"Datapack {sourceName} already exists. Review and explicitly replace it.");
                recovered = Path.Combine(
                    paths.Recovery,
                    $"datapack-{server.Id:N}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{operationId:N}",
                    sourceName);
                Directory.CreateDirectory(Path.GetDirectoryName(recovered)!);
                if (File.Exists(target))
                    File.Move(target, recovered);
                else
                    Directory.Move(target, recovered);
            }

            if (File.Exists(staged))
                File.Move(staged, target);
            else
                Directory.Move(staged, target);
            var sha256 = ContentHash(target);
            var relative = Path.GetRelativePath(server.RootPath, target);
            var item = new DatapackInventoryItem
            {
                ItemId = Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(
                        $"{request.WorldName}\n{sourceName}\n{sha256}"))),
                ServerId = server.Id,
                WorldName = request.WorldName,
                RelativePath = relative,
                PackFormat = inspected.PackFormat,
                Compatibility = inspected.Compatibility,
                Sha256 = sha256
            };
            await store.UpsertDatapackInventoryAsync(item, cancellationToken).ConfigureAwait(false);
            return item;
        }
        catch
        {
            if (target is not null)
            {
                if (File.Exists(target))
                    File.Delete(target);
                else if (Directory.Exists(target))
                    Directory.Delete(target, true);
                if (recovered is not null)
                {
                    if (File.Exists(recovered))
                        File.Move(recovered, target);
                    else if (Directory.Exists(recovered))
                        Directory.Move(recovered, target);
                }
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    public Task<IReadOnlyList<DatapackInventoryItem>> ListAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        store.GetDatapackInventoryAsync(serverId, cancellationToken);

    public async Task<ResourcePackConfiguration> ConfigureResourcePackAsync(
        ServerDefinition server,
        ResourcePackConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        if (configuration.ServerId != server.Id)
            throw new InvalidDataException("Resource-pack server ID does not match.");
        if (!string.IsNullOrWhiteSpace(configuration.Url) &&
            (!Uri.TryCreate(configuration.Url, UriKind.Absolute, out var uri) ||
             uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("A server resource-pack URL must be an absolute HTTPS URL.");
        if (!string.IsNullOrWhiteSpace(configuration.Url) &&
            (configuration.Sha1.Length != 40 ||
             configuration.Sha1.Any(character => !Uri.IsHexDigit(character))))
            throw new InvalidDataException("Server resource-pack SHA-1 must be exactly 40 hexadecimal characters.");
        if (configuration.Prompt.Length > 1_024)
            throw new InvalidDataException("Resource-pack prompt is limited to 1024 characters.");

        await using var pathLock = await pathLocks.AcquireAsync(server.RootPath, cancellationToken)
            .ConfigureAwait(false);
        _ = await backups.CreateAsync(
            server, backups.GetDefaultProfile(server), "Before resource-pack configuration", cancellationToken)
            .ConfigureAwait(false);
        var content = await files.ReadTextAsync(
            server.RootPath, "server.properties", cancellationToken).ConfigureAwait(false);
        var document = ServerPropertiesDocument.Parse(content.Content);
        document.Set("resource-pack", configuration.Url);
        document.Set("resource-pack-sha1", configuration.Sha1.ToLowerInvariant());
        document.Set("require-resource-pack", configuration.Required ? "true" : "false");
        document.Set("resource-pack-prompt", configuration.Prompt);
        await files.WriteTextAtomicAsync(
            server.RootPath,
            content with { Content = document.ToString() },
            createRecoveryCopy: true,
            cancellationToken).ConfigureAwait(false);
        await store.UpsertResourcePackConfigurationAsync(configuration, cancellationToken)
            .ConfigureAwait(false);
        return configuration;
    }

    public static string CalculateResourcePackSha1(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected resource-pack ZIP was not found.", path);
        using var stream = File.OpenRead(path);
#pragma warning disable CA5350 // Minecraft's server resource-pack property requires SHA-1.
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static string ResolveWorld(string root, string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName) || Path.IsPathRooted(worldName))
            throw new InvalidDataException("Choose a world managed by this server.");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        var world = Path.GetFullPath(Path.Combine(root, worldName));
        if (!world.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(world))
            throw new InvalidDataException("The selected world is outside the server root or missing.");
        return world;
    }

    private static string SafeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Datapack name is invalid.");
        return value;
    }

    private static async Task CopyDirectoryAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(
                     source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.Combine(
                destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
                FileOptions.Asynchronous);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ContentHash(string path)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                     .OrderBy(item => Path.GetRelativePath(path, item), StringComparer.Ordinal))
        {
            var relative = Encoding.UTF8.GetBytes(
                Path.GetRelativePath(path, file).Replace('\\', '/'));
            hash.AppendData(relative);
            using var stream = File.OpenRead(file);
            var buffer = new byte[81_920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, count);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
