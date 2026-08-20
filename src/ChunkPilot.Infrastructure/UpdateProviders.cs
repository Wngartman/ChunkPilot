using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public interface IUpdateProviderAdapter
{
    UpdateProvider Provider { get; }
    Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    bool Contains(string key);
    void SetSecret(string key, string value);
    string? GetSecret(string key);
    void Delete(string key);
}

public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ChunkPilot.UpdateProviders.v1");
    private readonly string path;
    private readonly object gate = new();

    public DpapiSecretStore(AppDataPaths paths) => path = paths.SecretsPath;

    public bool Contains(string key) => GetSecret(key) is not null;

    public void SetSecret(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        lock (gate)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("ChunkPilot protects provider keys with Windows DPAPI.");
            var values = Read();
            var protectedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
            values[key] = Convert.ToBase64String(protectedBytes);
            Write(values);
        }
    }

    public string? GetSecret(string key)
    {
        lock (gate)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("ChunkPilot protects provider keys with Windows DPAPI.");
            var values = Read();
            if (!values.TryGetValue(key, out var encoded))
                return null;
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(encoded), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    public void Delete(string key)
    {
        lock (gate)
        {
            var values = Read();
            if (values.Remove(key))
                Write(values);
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path), ProtocolJson.Options)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private void Write(Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(values, ProtocolJson.Options), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed class UpdateProviderRegistry
{
    private readonly IReadOnlyDictionary<UpdateProvider, IUpdateProviderAdapter> adapters;

    public UpdateProviderRegistry(IEnumerable<IUpdateProviderAdapter> adapters) =>
        this.adapters = adapters.ToDictionary(adapter => adapter.Provider);

    public IUpdateProviderAdapter Get(UpdateProvider provider) =>
        adapters.TryGetValue(provider, out var adapter)
            ? adapter
            : throw new NotSupportedException($"No update provider adapter is registered for {provider}.");
}

/// <summary>Exact same-Minecraft-version Paper build updates from PaperMC's official Fill catalog.</summary>
public sealed class PaperMcUpdateProvider(PaperVersionCatalogService catalog) : IUpdateProviderAdapter
{
    public UpdateProvider Provider => UpdateProvider.PaperMC;

    public async Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        if (!source.ProjectId.Equals("paper", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(source.MinecraftVersion))
            throw new InvalidOperationException("The Paper update source does not identify an exact Minecraft version.");
        var builds = await catalog.GetBuildsAsync(source.MinecraftVersion, forceRefresh: true, cancellationToken)
            .ConfigureAwait(false);
        if (!builds.ProviderAvailable && builds.Builds.Count == 0)
            throw new InvalidOperationException(builds.UnavailableDetail);
        var java = PaperJavaRuntimePolicy.RequiredMajor(source.MinecraftVersion)
            ?? throw new InvalidOperationException("Paper's Java requirement is not established for this Minecraft version.");
        return builds.Builds
            .Where(build => build.IsSelectable)
            .Select(build => new PackVersionInfo
            {
                PackId = "paper",
                VersionId = $"{build.MinecraftVersion}-{build.BuildId}",
                VersionName = $"Paper build {build.BuildId}",
                ReleaseChannel = build.Channel switch
                {
                    PaperBuildChannel.Stable => ReleaseChannel.Stable,
                    PaperBuildChannel.Beta => ReleaseChannel.Beta,
                    _ => ReleaseChannel.Alpha
                },
                PublishedAt = build.PublishedAt ?? DateTimeOffset.MinValue,
                MinecraftVersion = build.MinecraftVersion,
                Loader = ServerEcosystem.Paper.ToString(),
                LoaderVersion = build.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RequiredJavaMajor = java,
                DownloadUrl = build.DownloadUrl,
                FileSize = build.ServerSizeBytes,
                Sha256 = build.ServerSha256,
                FileName = build.FileName,
                PackageType = "jar"
            })
            .Where(version => UpdatePolicy.Allows(version.ReleaseChannel, preferences))
            .OrderByDescending(version => version.PublishedAt)
            .ThenByDescending(version => int.TryParse(version.LoaderVersion, out var id) ? id : 0)
            .ToArray();
    }
}

/// <summary>
/// Exact same-Minecraft-version loader updates from explicitly supported provider strategies. The generic
/// update transaction materializes these artifacts with the loader installer before migration;
/// an installer JAR is never mistaken for the active server JAR.
/// </summary>
public sealed class ManagedLoaderUpdateProvider(ManagedLoaderCatalogService catalog) : IUpdateProviderAdapter
{
    public UpdateProvider Provider => UpdateProvider.ManagedLoader;

    public async Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ManagedLoaderPlatform>(source.Loader, true, out var platform) ||
            !Enum.IsDefined(platform) ||
            string.IsNullOrWhiteSpace(source.MinecraftVersion))
            throw new InvalidOperationException(
                "The managed-loader update source does not identify a known platform and exact Minecraft version.");
        var strategy = ManagedLoaderPlatformStrategies.For(platform);
        if (!strategy.SupportsUpdateMaterialization)
            throw new InvalidOperationException(
                $"{platform} catalog metadata is available, but exact transactional loader updates are not implemented.");

        var builds = await catalog.GetBuildsAsync(platform, source.MinecraftVersion, forceRefresh: true,
            cancellationToken).ConfigureAwait(false);
        if (!builds.ProviderAvailable && builds.Builds.Count == 0)
            throw new InvalidOperationException(builds.UnavailableDetail);

        return builds.Builds
            .Where(build => build.IsSelectable && build.ArtifactSha256.Length == 64)
            .Select(build => new PackVersionInfo
            {
                PackId = platform.ToString().ToLowerInvariant(),
                VersionId = Identity(build),
                VersionName = $"{platform} {build.LoaderVersion}",
                ReleaseChannel = build.Channel switch
                {
                    ManagedLoaderChannel.Stable => ReleaseChannel.Stable,
                    ManagedLoaderChannel.Beta => ReleaseChannel.Beta,
                    _ => ReleaseChannel.Alpha
                },
                PublishedAt = DateTimeOffset.MinValue,
                MinecraftVersion = build.MinecraftVersion,
                Loader = platform.ToString(),
                LoaderVersion = build.LoaderVersion,
                InstallerVersion = build.InstallerVersion,
                RequiredJavaMajor = build.RequiredJavaMajor ?? 0,
                InstallerJavaMajor = ManagedLoaderInstallerJavaPolicy.Resolve(
                    build.Platform, build.InstallerJavaMajor, build.RequiredJavaMajor ?? 0),
                DownloadUrl = build.ArtifactUrl,
                FileSize = build.ArtifactSizeBytes,
                Sha1 = build.ArtifactSha1,
                Sha256 = build.ArtifactSha256,
                FileName = platform switch
                {
                    ManagedLoaderPlatform.Fabric =>
                        $"fabric-server-{build.MinecraftVersion}-{build.LoaderVersion}-{build.InstallerVersion}.jar",
                    ManagedLoaderPlatform.Quilt => $"quilt-installer-{build.InstallerVersion}.jar",
                    ManagedLoaderPlatform.Forge =>
                        $"forge-{build.MinecraftVersion}-{build.LoaderVersion}-installer.jar",
                    ManagedLoaderPlatform.NeoForge => $"neoforge-{build.LoaderVersion}-installer.jar",
                    ManagedLoaderPlatform.LegacyFabric or ManagedLoaderPlatform.Ornithe =>
                        throw new InvalidOperationException(
                            $"{platform} has no managed update artifact materializer."),
                    _ => throw new ArgumentOutOfRangeException(nameof(source), platform,
                        "Unknown managed-loader platform.")
                },
                PackageType = platform switch
                {
                    ManagedLoaderPlatform.Fabric => "fabric-server-launcher",
                    ManagedLoaderPlatform.Quilt => "quilt-installer",
                    ManagedLoaderPlatform.Forge => "forge-installer",
                    ManagedLoaderPlatform.NeoForge => "neoforge-installer",
                    ManagedLoaderPlatform.LegacyFabric or ManagedLoaderPlatform.Ornithe =>
                        throw new InvalidOperationException(
                            $"{platform} has no managed update artifact materializer."),
                    _ => throw new ArgumentOutOfRangeException(nameof(source), platform,
                        "Unknown managed-loader platform.")
                },
                MigrationNotes = "Loader-only update for the currently installed Minecraft version. A verified recovery point is required."
            })
            .Where(version => UpdatePolicy.Allows(version.ReleaseChannel, preferences))
            .ToArray();
    }

    public static string Identity(ManagedLoaderBuild build) =>
        $"{build.MinecraftVersion}-{build.LoaderVersion}-{build.InstallerVersion}";
}

public sealed class ModrinthUpdateProvider : IUpdateProviderAdapter
{
    private readonly HttpClient http;
    public UpdateProvider Provider => UpdateProvider.Modrinth;

    public ModrinthUpdateProvider(HttpClient? client = null)
    {
        http = client ?? UpdateHttp.Create();
    }

    public async Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        RequireProject(source);
        using (var project = await GetJsonAsync(
                   $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(source.ProjectId)}",
                   cancellationToken).ConfigureAwait(false))
        {
            if (project.RootElement.TryGetProperty("server_side", out var serverSide) &&
                serverSide.GetString()?.Equals("unsupported", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidOperationException("The linked Modrinth project declares server-side use unsupported.");
        }

        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(source.MinecraftVersion))
            query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { source.MinecraftVersion })));
        if (!string.IsNullOrWhiteSpace(source.Loader))
            query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { source.Loader.ToLowerInvariant() })));
        query.Add("include_changelog=true");
        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(source.ProjectId)}/version?{string.Join("&", query)}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        return document.RootElement.EnumerateArray()
            .Select(ParseVersion)
            .Where(version => !string.IsNullOrWhiteSpace(version.DownloadUrl))
            .Where(version => UpdatePolicy.Allows(version.ReleaseChannel, preferences))
            .OrderByDescending(version => version.PublishedAt)
            .ToArray();
    }

    private static PackVersionInfo ParseVersion(JsonElement item)
    {
        var files = item.GetProperty("files").EnumerateArray().ToArray();
        static string Name(JsonElement candidate) =>
            candidate.TryGetProperty("filename", out var name) ? name.GetString() ?? "" : "";
        static bool IsServerPackage(JsonElement candidate)
        {
            var name = Name(candidate);
            if (!name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                !candidate.TryGetProperty("url", out var value) ||
                !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri))
                return false;
            return uri.Scheme == Uri.UriSchemeHttps &&
                   uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);
        }
        var file = files.FirstOrDefault(candidate =>
            IsServerPackage(candidate) &&
            Name(candidate).Contains("server", StringComparison.OrdinalIgnoreCase));
        if (file.ValueKind == JsonValueKind.Undefined)
            file = files.FirstOrDefault(candidate =>
                IsServerPackage(candidate) &&
            candidate.TryGetProperty("primary", out var primary) && primary.GetBoolean());
        if (file.ValueKind == JsonValueKind.Undefined)
            file = files.FirstOrDefault(IsServerPackage);
        var hashes = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("hashes", out var value)
            ? value : default;
        return new PackVersionInfo
        {
            PackId = item.GetProperty("project_id").GetString() ?? "",
            VersionId = item.GetProperty("id").GetString() ?? "",
            VersionName = item.TryGetProperty("version_number", out var versionNumber)
                ? versionNumber.GetString() ?? "" : item.GetProperty("name").GetString() ?? "",
            ReleaseChannel = ParseChannel(item.TryGetProperty("version_type", out var type) ? type.GetString() : null),
            PublishedAt = item.TryGetProperty("date_published", out var published)
                ? published.GetDateTimeOffset() : DateTimeOffset.MinValue,
            MinecraftVersion = item.TryGetProperty("game_versions", out var games)
                ? games.EnumerateArray().Select(value => value.GetString()).FirstOrDefault(value => value is not null) ?? "" : "",
            Loader = item.TryGetProperty("loaders", out var loaders)
                ? loaders.EnumerateArray().Select(value => value.GetString()).FirstOrDefault(value => value is not null) ?? "" : "",
            DownloadUrl = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("url", out var download)
                ? download.GetString() ?? "" : "",
            FileName = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("filename", out var fileName)
                ? fileName.GetString() ?? "" : "",
            FileSize = file.ValueKind == JsonValueKind.Object && file.TryGetProperty("size", out var size)
                ? size.GetInt64() : null,
            PackageType = file.ValueKind == JsonValueKind.Object &&
                          Name(file).EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ? "mrpack" : "unavailable",
            Sha1 = hashes.ValueKind == JsonValueKind.Object && hashes.TryGetProperty("sha1", out var sha1)
                ? sha1.GetString() ?? "" : "",
            Sha512 = hashes.ValueKind == JsonValueKind.Object && hashes.TryGetProperty("sha512", out var sha512)
                ? sha512.GetString() ?? "" : "",
            Changelog = item.TryGetProperty("changelog", out var changelog) ? changelog.GetString() ?? "" : ""
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static void RequireProject(UpdateSource source)
    {
        if (string.IsNullOrWhiteSpace(source.ProjectId))
            throw new InvalidOperationException("A Modrinth project ID or slug is required.");
    }

    internal static ReleaseChannel ParseChannel(string? value) =>
        value?.ToLowerInvariant() switch
        {
            "release" or "stable" => ReleaseChannel.Stable,
            "beta" or "prerelease" => ReleaseChannel.Beta,
            "alpha" => ReleaseChannel.Alpha,
            _ => ReleaseChannel.Alpha
        };
}

public sealed class CurseForgeUpdateProvider : IUpdateProviderAdapter
{
    public const string ApiKeyName = "curseforge-api-key";
    private readonly HttpClient http;
    private readonly ISecretStore secrets;
    public UpdateProvider Provider => UpdateProvider.CurseForge;

    public CurseForgeUpdateProvider(ISecretStore secrets, HttpClient? client = null)
    {
        this.secrets = secrets;
        http = client ?? UpdateHttp.Create();
    }

    public async Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var apiKey = secrets.GetSecret(ApiKeyName);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("CurseForge update checking is unavailable until an API key is configured.");
        if (!long.TryParse(source.ProjectId, out _))
            throw new InvalidOperationException("CurseForge requires the numeric project ID.");
        using var document = await GetAsync(
            $"https://api.curseforge.com/v1/mods/{Uri.EscapeDataString(source.ProjectId)}/files?pageSize=50",
            apiKey, cancellationToken).ConfigureAwait(false);
        var parents = document.RootElement.GetProperty("data").EnumerateArray()
            .Where(item =>
            {
                var parsed = ParseFile(source, item);
                return UpdatePolicy.Allows(parsed.ReleaseChannel, preferences) &&
                       (string.IsNullOrWhiteSpace(source.MinecraftVersion) ||
                        parsed.MinecraftVersion.Equals(source.MinecraftVersion, StringComparison.OrdinalIgnoreCase)) &&
                       (string.IsNullOrWhiteSpace(source.Loader) ||
                        parsed.Loader.Equals(source.Loader, StringComparison.OrdinalIgnoreCase));
            })
            .Where(item => item.TryGetProperty("serverPackFileId", out var fileId) &&
                           fileId.ValueKind == JsonValueKind.Number && fileId.GetInt64() > 0)
            .ToArray();
        var results = new List<PackVersionInfo>();
        foreach (var parent in parents)
        {
            var serverPackId = parent.GetProperty("serverPackFileId").GetInt64();
            using var serverPack = await GetAsync(
                $"https://api.curseforge.com/v1/mods/{Uri.EscapeDataString(source.ProjectId)}/files/{serverPackId}",
                apiKey, cancellationToken).ConfigureAwait(false);
            var parentVersion = ParseFile(source, parent);
            var package = ParseFile(source, serverPack.RootElement.GetProperty("data"));
            results.Add(package with
            {
                VersionId = parentVersion.VersionId,
                VersionName = parentVersion.VersionName,
                ReleaseChannel = parentVersion.ReleaseChannel,
                PublishedAt = parentVersion.PublishedAt,
                MinecraftVersion = parentVersion.MinecraftVersion,
                Loader = parentVersion.Loader,
                Changelog = parentVersion.Changelog
            });
        }
        return results.OrderByDescending(version => version.PublishedAt).ToArray();
    }

    private async Task<JsonDocument> GetAsync(
        string url,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("x-api-key", apiKey);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static PackVersionInfo ParseFile(UpdateSource source, JsonElement item)
    {
        var gameVersions = item.TryGetProperty("gameVersions", out var versions)
            ? versions.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : [];
        var loader = gameVersions.FirstOrDefault(IsLoader) ?? "";
        var game = gameVersions.FirstOrDefault(value => value.Length > 0 && char.IsDigit(value[0])) ?? "";
        var hashes = item.TryGetProperty("hashes", out var hashValues)
            ? hashValues.EnumerateArray().ToArray() : [];
        string Hash(int algorithm) => hashes.FirstOrDefault(hash =>
            hash.TryGetProperty("algo", out var algo) && algo.GetInt32() == algorithm) is var match &&
            match.ValueKind == JsonValueKind.Object && match.TryGetProperty("value", out var value)
                ? value.GetString() ?? "" : "";
        return new PackVersionInfo
        {
            PackId = source.ProjectId,
            VersionId = item.GetProperty("id").ToString(),
            VersionName = item.TryGetProperty("displayName", out var display)
                ? display.GetString() ?? "" : item.GetProperty("fileName").GetString() ?? "",
            ReleaseChannel = item.TryGetProperty("releaseType", out var releaseType)
                ? releaseType.GetInt32() switch
                {
                    1 => ReleaseChannel.Stable,
                    2 => ReleaseChannel.Beta,
                    _ => ReleaseChannel.Alpha
                } : ReleaseChannel.Alpha,
            PublishedAt = item.TryGetProperty("fileDate", out var date) ? date.GetDateTimeOffset() : DateTimeOffset.MinValue,
            MinecraftVersion = game,
            Loader = loader,
            DownloadUrl = item.TryGetProperty("downloadUrl", out var url) ? url.GetString() ?? "" : "",
            FileName = item.TryGetProperty("fileName", out var fileName) ? fileName.GetString() ?? "" : "",
            FileSize = item.TryGetProperty("fileLength", out var length) ? length.GetInt64() : null,
            PackageType = item.TryGetProperty("fileName", out var packageName) &&
                          packageName.GetString()?.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) == true
                ? "jar" : "zip",
            Sha1 = Hash(1),
            Sha256 = Hash(3),
            Changelog = item.TryGetProperty("fileName", out var notes) ? notes.GetString() ?? "" : ""
        };
    }

    private static bool IsLoader(string value) =>
        value.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NeoForge", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Fabric", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Quilt", StringComparison.OrdinalIgnoreCase);
}

public sealed class GitHubReleasesUpdateProvider : IUpdateProviderAdapter
{
    private readonly HttpClient http;
    public UpdateProvider Provider => UpdateProvider.GitHubReleases;

    public GitHubReleasesUpdateProvider(HttpClient? client = null) => http = client ?? UpdateHttp.Create();

    public async Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var repository = NormalizeRepository(source.ProjectId, source.SourceUrl);
        using var response = await http.GetAsync($"https://api.github.com/repos/{repository}/releases?per_page=50",
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.EnumerateArray()
            .Where(release => !release.TryGetProperty("draft", out var draft) || !draft.GetBoolean())
            .Select(release => ParseRelease(repository, source.AssetNamePattern, release))
            .Where(version => version is not null)
            .Select(version => version!)
            .Where(version => UpdatePolicy.Allows(version.ReleaseChannel, preferences))
            .OrderByDescending(version => version.PublishedAt)
            .ToArray();
    }

    private static PackVersionInfo? ParseRelease(string repository, string pattern, JsonElement release)
    {
        var assets = release.GetProperty("assets").EnumerateArray();
        var asset = assets.FirstOrDefault(candidate =>
        {
            var name = candidate.GetProperty("name").GetString() ?? "";
            return (string.IsNullOrWhiteSpace(pattern) ||
                    name.Contains(pattern, StringComparison.OrdinalIgnoreCase)) &&
                   (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
        });
        if (asset.ValueKind == JsonValueKind.Undefined)
            return null;
        var prerelease = release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
        var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? "" : "";
        return new PackVersionInfo
        {
            PackId = repository,
            VersionId = release.GetProperty("id").ToString(),
            VersionName = release.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "",
            ReleaseChannel = prerelease ? ReleaseChannel.Beta : ReleaseChannel.Stable,
            PublishedAt = release.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                ? published.GetDateTimeOffset() : DateTimeOffset.MinValue,
            DownloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "",
            FileName = asset.GetProperty("name").GetString() ?? "",
            FileSize = asset.TryGetProperty("size", out var size) ? size.GetInt64() : null,
            PackageType = (asset.GetProperty("name").GetString() ?? "")
                .EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ? "jar" : "zip",
            Sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : "",
            Changelog = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : ""
        };
    }

    internal static string NormalizeRepository(string projectId, string sourceUrl)
    {
        var candidate = string.IsNullOrWhiteSpace(projectId) ? sourceUrl : projectId;
        candidate = candidate.Trim().TrimEnd('/');
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            candidate = uri.AbsolutePath.Trim('/');
        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            candidate = candidate[..^4];
        if (candidate.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2)
            throw new InvalidOperationException("GitHub Releases requires an owner/repository source.");
        return candidate;
    }
}

public sealed record DirectVersionManifest
{
    public string PackId { get; init; } = "";
    public IReadOnlyList<PackVersionInfo> Versions { get; init; } = [];
}

public sealed class DirectManifestUpdateProvider : IUpdateProviderAdapter
{
    private readonly HttpClient http;
    public UpdateProvider Provider => UpdateProvider.DirectManifest;

    public DirectManifestUpdateProvider(HttpClient? client = null) => http = client ?? UpdateHttp.Create();

    public async Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        var uri = new Uri(source.SourceUrl, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Direct update manifests require HTTPS.");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await JsonSerializer.DeserializeAsync<DirectVersionManifest>(
            stream, ProtocolJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The direct update manifest was empty.");
        if (string.IsNullOrWhiteSpace(manifest.PackId))
            throw new InvalidDataException("The direct update manifest is missing packId.");
        if (!string.IsNullOrWhiteSpace(source.ProjectId) &&
            !manifest.PackId.Equals(source.ProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The direct manifest pack ID does not match the linked source.");
        foreach (var version in manifest.Versions)
            ValidateVersion(version);
        return manifest.Versions
            .Select(version => version with { PackId = manifest.PackId })
            .Where(version => UpdatePolicy.Allows(version.ReleaseChannel, preferences))
            .OrderByDescending(version => version.PublishedAt).ToArray();
    }

    internal static void ValidateVersion(PackVersionInfo version)
    {
        if (string.IsNullOrWhiteSpace(version.VersionId) ||
            string.IsNullOrWhiteSpace(version.DownloadUrl) ||
            string.IsNullOrWhiteSpace(version.Sha256))
            throw new InvalidDataException("Every direct-manifest version requires versionId, downloadUrl, and sha256.");
        var uri = new Uri(version.DownloadUrl, UriKind.Absolute);
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Direct-manifest downloads require HTTPS.");
    }
}

public sealed class LocalPackageHistoryUpdateProvider : IUpdateProviderAdapter
{
    public UpdateProvider Provider => UpdateProvider.LocalPackageHistory;

    public Task<IReadOnlyList<PackVersionInfo>> GetVersionsAsync(
        UpdateSource source,
        UpdatePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = source.SourceUrl;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile)
            path = uri.LocalPath;
        if (!File.Exists(path))
            throw new FileNotFoundException("The linked local update package was not found.", path);
        var file = new FileInfo(path);
        IReadOnlyList<PackVersionInfo> versions =
        [
            new()
            {
                PackId = source.ProjectId,
                VersionId = file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                VersionName = Path.GetFileNameWithoutExtension(file.Name),
                ReleaseChannel = ReleaseChannel.Stable,
                PublishedAt = file.LastWriteTimeUtc,
                MinecraftVersion = source.MinecraftVersion,
                Loader = source.Loader,
                LoaderVersion = source.LoaderVersion,
                DownloadUrl = file.FullName,
                FileName = file.Name,
                FileSize = file.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file.FullName))).ToLowerInvariant()
            }
        ];
        return Task.FromResult(versions);
    }
}

internal static class UpdateHttp
{
    public static HttpClient Create()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ChunkPilot", "1.3.0"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(local-Windows-server-manager)"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
}
