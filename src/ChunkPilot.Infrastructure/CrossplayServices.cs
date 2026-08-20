using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public interface ICrossplayPackageProvider
{
    Task<CrossplayPackage> ResolveAsync(
        CrossplayPackageKind kind,
        string platform,
        CancellationToken cancellationToken = default);
}

public sealed class OfficialCrossplayPackageProvider : ICrossplayPackageProvider
{
    private readonly HttpClient http;

    public OfficialCrossplayPackageProvider(HttpClient? httpClient = null)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ChunkPilot/1.3.0 (local Windows Minecraft server manager)");
    }

    public async Task<CrossplayPackage> ResolveAsync(
        CrossplayPackageKind kind,
        string platform,
        CancellationToken cancellationToken = default) =>
        kind switch
        {
            CrossplayPackageKind.Geyser =>
                await ResolveGeyserProjectAsync("geyser", kind, platform, cancellationToken)
                    .ConfigureAwait(false),
            CrossplayPackageKind.Floodgate =>
                await ResolveGeyserProjectAsync("floodgate", kind, platform, cancellationToken)
                    .ConfigureAwait(false),
            CrossplayPackageKind.ViaVersion =>
                await ResolveViaVersionAsync(cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Unsupported crossplay package {kind}.")
        };

    private async Task<CrossplayPackage> ResolveGeyserProjectAsync(
        string project,
        CrossplayPackageKind kind,
        string platform,
        CancellationToken cancellationToken)
    {
        var metadataUrl =
            $"https://download.geysermc.org/v2/projects/{project}/versions/latest/builds/latest";
        using var document = await GetJsonAsync(metadataUrl, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var downloads = root.GetProperty("downloads");
        if (!downloads.TryGetProperty(platform, out var download))
            throw new InvalidOperationException(
                $"{kind} does not publish a supported {platform} package.");
        var sha256 = download.TryGetProperty("sha256", out var hash)
            ? hash.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(sha256))
            throw new InvalidDataException($"{kind} metadata did not include a SHA-256 checksum.");
        var version = root.TryGetProperty("version", out var versionValue)
            ? versionValue.GetString() ?? "latest" : "latest";
        var build = root.TryGetProperty("build", out var buildValue)
            ? buildValue.ToString() : "latest";
        return new CrossplayPackage
        {
            Kind = kind,
            Version = $"{version}+{build}",
            Platform = platform,
            FileName = download.TryGetProperty("name", out var name)
                ? name.GetString() ?? $"{project}-{platform}.jar"
                : $"{project}-{platform}.jar",
            DownloadUrl =
                $"https://download.geysermc.org/v2/projects/{project}/versions/latest/builds/latest/downloads/{platform}",
            Sha256 = sha256
        };
    }

    private async Task<CrossplayPackage> ResolveViaVersionAsync(
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            "https://api.modrinth.com/v2/project/viaversion/version?loaders=%5B%22paper%22%5D&featured=true",
            cancellationToken).ConfigureAwait(false);
        var version = document.RootElement.EnumerateArray()
            .FirstOrDefault(item =>
                !item.TryGetProperty("version_type", out var channel) ||
                channel.GetString() == "release");
        if (version.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("ViaVersion has no stable Paper package.");
        var file = version.GetProperty("files").EnumerateArray()
            .OrderByDescending(item =>
                item.TryGetProperty("primary", out var primary) && primary.GetBoolean())
            .FirstOrDefault();
        if (file.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("ViaVersion metadata did not include a package.");
        var hashes = file.GetProperty("hashes");
        var sha512 = hashes.TryGetProperty("sha512", out var hash)
            ? hash.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(sha512))
            throw new InvalidDataException("ViaVersion metadata did not include a SHA-512 checksum.");
        return new CrossplayPackage
        {
            Kind = CrossplayPackageKind.ViaVersion,
            Version = version.TryGetProperty("version_number", out var number)
                ? number.GetString() ?? "" : "",
            Platform = "paper",
            FileName = file.GetProperty("filename").GetString() ?? "ViaVersion.jar",
            DownloadUrl = file.GetProperty("url").GetString()
                ?? throw new InvalidDataException("ViaVersion package URL was missing."),
            Sha512 = sha512
        };
    }

    private async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}

public sealed class CrossplayPackageService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly BackupService backups;
    private readonly CanonicalPathLockManager pathLocks;
    private readonly ICrossplayPackageProvider provider;
    private readonly HttpClient http;

    public CrossplayPackageService(
        AppDataPaths paths,
        ChunkPilotStore store,
        BackupService backups,
        CanonicalPathLockManager pathLocks,
        ICrossplayPackageProvider provider,
        HttpClient? httpClient = null)
    {
        this.paths = paths;
        this.store = store;
        this.backups = backups;
        this.pathLocks = pathLocks;
        this.provider = provider;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<CrossplayInstallResult> InstallAsync(
        ServerDefinition server,
        ServerCapabilityProfile capabilities,
        CrossplayInstallRequest request,
        IReadOnlyCollection<int>? occupiedUdpPorts = null,
        CancellationToken cancellationToken = default)
    {
        var desired = new CrossplayConfiguration
        {
            ServerId = server.Id,
            GeyserEnabled = true,
            FloodgateEnabled = request.InstallFloodgate,
            ViaVersionEnabled = request.InstallViaVersion,
            BedrockPort = request.BedrockPort,
            AuthenticationMode = request.InstallFloodgate ? "floodgate" : "online"
        };
        var errors = CrossplayPolicy.Validate(capabilities, desired, occupiedUdpPorts);
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        var platform = server.Ecosystem == ServerEcosystem.Fabric ? "fabric" : "spigot";
        if (platform == "fabric" && request.InstallViaVersion)
            throw new InvalidOperationException("ViaVersion is supported here only for Paper-compatible plugin servers.");
        var kinds = new List<CrossplayPackageKind> { CrossplayPackageKind.Geyser };
        if (request.InstallFloodgate)
            kinds.Add(CrossplayPackageKind.Floodgate);
        if (request.InstallViaVersion)
            kinds.Add(CrossplayPackageKind.ViaVersion);

        var packages = new List<CrossplayPackage>();
        foreach (var kind in kinds)
            packages.Add(await provider.ResolveAsync(kind, platform, cancellationToken)
                .ConfigureAwait(false));

        await using var pathLock = await pathLocks.AcquireAsync(server.RootPath, cancellationToken)
            .ConfigureAwait(false);
        var backup = await backups.CreateAsync(server, backups.GetDefaultProfile(server),
            "Before crossplay installation", cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var staging = Path.Combine(paths.Staging, $"crossplay-{operationId:N}");
        var recovery = Path.Combine(paths.Recovery,
            $"crossplay-{server.Id:N}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{operationId:N}");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(recovery);
        var existing = await store.GetCrossplayConfigurationAsync(server.Id, cancellationToken)
            .ConfigureAwait(false);
        var previousOwned = existing?.OwnedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var installed = new List<string>();
        var moved = new List<(string Original, string Recovery)>();
        try
        {
            foreach (var package in packages)
            {
                var staged = Path.Combine(staging, SafeFileName(package.FileName));
                await DownloadAsync(package.DownloadUrl, staged, cancellationToken).ConfigureAwait(false);
                ManagedServerInstaller.VerifyHash(
                    staged, expectedSha1: "", expectedSha256: package.Sha256,
                    expectedSha512: package.Sha512);
            }

            foreach (var relative in previousOwned)
            {
                var original = ResolveOwnedPath(server.RootPath, relative);
                if (!File.Exists(original))
                    continue;
                var recovered = Path.Combine(recovery, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(recovered)!);
                File.Move(original, recovered);
                moved.Add((original, recovered));
            }

            var contentFolder = platform == "fabric" ? "mods" : "plugins";
            foreach (var package in packages)
            {
                var relative = Path.Combine(contentFolder, OwnedName(package.Kind, platform));
                var target = ResolveOwnedPath(server.RootPath, relative);
                if (File.Exists(target) && !previousOwned.Contains(relative))
                    throw new IOException(
                        $"Crossplay installation will not overwrite unowned file {relative}.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(Path.Combine(staging, SafeFileName(package.FileName)), target);
                installed.Add(relative);
            }

            var versions = packages.ToDictionary(
                item => item.Kind.ToString(), item => item.Version, StringComparer.OrdinalIgnoreCase);
            var configuration = desired with
            {
                OwnedFiles = installed.ToArray(),
                InstalledVersions = versions
            };
            await store.UpsertCrossplayConfigurationAsync(configuration, cancellationToken)
                .ConfigureAwait(false);
            return new CrossplayInstallResult
            {
                Configuration = configuration,
                BackupId = backup.Id,
                RestartRequired = true,
                Message = "Verified crossplay packages installed. Restart the server once so Geyser can create its configuration, then review the Bedrock UDP port and authentication mode."
            };
        }
        catch
        {
            foreach (var relative in installed)
            {
                var path = ResolveOwnedPath(server.RootPath, relative);
                if (File.Exists(path))
                    File.Delete(path);
            }
            foreach (var (original, recovered) in moved.AsEnumerable().Reverse())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original)!);
                if (File.Exists(recovered))
                    File.Move(recovered, original, true);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
        }
    }

    public async Task<OperationResult> RemoveAsync(
        ServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        await using var pathLock = await pathLocks.AcquireAsync(server.RootPath, cancellationToken)
            .ConfigureAwait(false);
        var configuration = await store.GetCrossplayConfigurationAsync(server.Id, cancellationToken)
            .ConfigureAwait(false);
        if (configuration is null || configuration.OwnedFiles.Count == 0)
            return OperationResult.Ok("No ChunkPilot-owned crossplay packages are installed.");
        _ = await backups.CreateAsync(server, backups.GetDefaultProfile(server),
            "Before crossplay removal", cancellationToken).ConfigureAwait(false);
        var recovery = Path.Combine(paths.Recovery,
            $"crossplay-removed-{server.Id:N}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
        foreach (var relative in configuration.OwnedFiles)
        {
            var source = ResolveOwnedPath(server.RootPath, relative);
            if (!File.Exists(source))
                continue;
            var target = Path.Combine(recovery, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target);
        }
        await store.UpsertCrossplayConfigurationAsync(
            configuration with
            {
                GeyserEnabled = false,
                FloodgateEnabled = false,
                ViaVersionEnabled = false,
                OwnedFiles = [],
                InstalledVersions = new Dictionary<string, string>()
            },
            cancellationToken).ConfigureAwait(false);
        return OperationResult.Ok(
            "Only ChunkPilot-owned crossplay packages were moved to Recovery. Generated configuration was preserved.");
    }

    private async Task DownloadAsync(
        string url,
        string destination,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Crossplay downloads require an official HTTPS URL.");
        using var response = await http.GetAsync(
            uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private static string OwnedName(CrossplayPackageKind kind, string platform) =>
        kind switch
        {
            CrossplayPackageKind.Geyser => $"Geyser-{platform}.jar",
            CrossplayPackageKind.Floodgate => $"floodgate-{platform}.jar",
            _ => "ViaVersion.jar"
        };

    private static string SafeFileName(string value)
    {
        var name = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("Provider returned an invalid package filename.");
        return name;
    }

    private static string ResolveOwnedPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException("Crossplay ownership paths must be relative.");
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                            Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Crossplay ownership path escapes the server root.");
        return target;
    }
}
