using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public interface IPluginCatalogProvider
{
    PluginProviderKind Provider { get; }
    PluginProviderStatus Status { get; }
    Task<IReadOnlyList<PluginProject>> SearchAsync(PluginCatalogQuery query, CancellationToken cancellationToken = default);
    Task<PluginRelease?> ResolveReleaseAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        string? versionId = null,
        CancellationToken cancellationToken = default);
}

public sealed class PluginProviderRegistry
{
    private readonly IReadOnlyDictionary<PluginProviderKind, IPluginCatalogProvider> providers;

    public PluginProviderRegistry(IEnumerable<IPluginCatalogProvider> providers) =>
        this.providers = providers.ToDictionary(provider => provider.Provider);

    public IReadOnlyList<PluginProviderStatus> Statuses =>
        providers.Values.Select(provider => provider.Status).OrderBy(status => status.Provider).ToArray();

    public IPluginCatalogProvider Get(PluginProviderKind provider) =>
        providers.TryGetValue(provider, out var value)
            ? value
            : throw new NotSupportedException($"No plugin provider is registered for {provider}.");
}

public sealed class HangarUnavailablePluginProvider : IPluginCatalogProvider
{
    public PluginProviderKind Provider => PluginProviderKind.Hangar;
    public PluginProviderStatus Status => new(Provider, false,
        "Hangar browsing is unavailable in this build. ChunkPilot does not scrape web pages or infer downloads.");

    public Task<IReadOnlyList<PluginProject>> SearchAsync(PluginCatalogQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PluginProject>>([]);

    public Task<PluginRelease?> ResolveReleaseAsync(
        string projectId, string minecraftVersion, string loader, string? versionId = null,
        CancellationToken cancellationToken = default) => Task.FromResult<PluginRelease?>(null);
}

public sealed class CurseForgePluginProvider : IPluginCatalogProvider
{
    private const int MinecraftGameId = 432;
    private const int ModClassId = 6;
    private readonly CurseForgeApiClient api;

    public CurseForgePluginProvider(CurseForgeApiClient api) => this.api = api;

    public PluginProviderKind Provider => PluginProviderKind.CurseForge;
    public PluginProviderStatus Status => new(Provider, api.CanAccess, api.AccessDetail);

    public async Task<IReadOnlyList<PluginProject>> SearchAsync(
        PluginCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Kind != ManagedAddonKind.Mod || !api.CanAccess) return [];
        var loaderType = LoaderType(query.Loader);
        if (loaderType == 0) return [];
        var path = $"/v1/mods/search?gameId={MinecraftGameId}&classId={ModClassId}" +
                   $"&pageSize={Math.Clamp(query.Limit, 1, 40)}&index=0&sortField=6&sortOrder=desc" +
                   "&searchFilter=" + Uri.EscapeDataString(query.Search.Trim()) +
                   "&gameVersion=" + Uri.EscapeDataString(query.MinecraftVersion.Trim()) +
                   $"&modLoaderType={loaderType}";
        using var document = await api.GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("CurseForge's mod search response was malformed.");
        return data.EnumerateArray()
            .Where(ProjectAvailable)
            .Select(project => new PluginProject
            {
                Kind = ManagedAddonKind.Mod,
                Provider = Provider,
                ProjectId = project.GetProperty("id").ToString(),
                Slug = Text(project, "slug"),
                Name = Text(project, "name"),
                Author = project.TryGetProperty("authors", out var authors) &&
                         authors.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", authors.EnumerateArray().Select(author => Text(author, "name"))
                        .Where(value => value.Length > 0).Take(20)) : "",
                Summary = Text(project, "summary"),
                IconUrl = project.TryGetProperty("logo", out var logo) ? Text(logo, "thumbnailUrl") : "",
                ProjectUrl = project.TryGetProperty("links", out var links) ? Text(links, "websiteUrl") : "",
                Downloads = Long(project, "downloadCount"),
                UpdatedAt = Date(project, "dateModified"),
                ServerSide = "unknown",
                ClientSide = "unknown",
                ClientRequirement = "Unknown"
            })
            .Where(project => project.ProjectId.Length > 0 && project.Name.Length > 0)
            .ToArray();
    }

    public async Task<PluginRelease?> ResolveReleaseAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        string? versionId = null,
        CancellationToken cancellationToken = default)
        => (await ResolvePackFileAsync(projectId, minecraftVersion, loader, versionId,
            allowResourcePacks: false, cancellationToken).ConfigureAwait(false))?.Release;

    internal async Task<CurseForgePackFileRelease?> ResolvePackFileAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        string? versionId,
        bool allowResourcePacks,
        CancellationToken cancellationToken = default)
    {
        if (!api.CanAccess || !long.TryParse(projectId, out var numericProject) || numericProject <= 0)
            return null;
        var loaderType = LoaderType(loader);
        if (loaderType == 0) return null;
        var contentKind = CurseForgeGeneratedContentKind.Mod;
        using (var projectDocument = await api.GetJsonAsync(
                   $"/v1/mods/{numericProject}", cancellationToken).ConfigureAwait(false))
        {
            if (!projectDocument.RootElement.TryGetProperty("data", out var project) ||
                project.ValueKind != JsonValueKind.Object || Long(project, "id") != numericProject ||
                Long(project, "gameId") != MinecraftGameId ||
                !ProjectAvailable(project))
                return null;
            var classId = Long(project, "classId");
            if (classId != ModClassId)
            {
                // CurseForge Minecraft resource packs are content class 12, not loader-specific
                // mods. Preserve the exact ZIP in resourcepacks; never quietly omit it.
                if (!allowResourcePacks || classId != 12) return null;
                contentKind = CurseForgeGeneratedContentKind.ResourcePack;
            }
        }
        IReadOnlyList<JsonElement> files;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            if (!long.TryParse(versionId, out var numericFile) || numericFile <= 0) return null;
            using var exact = await api.GetJsonAsync(
                $"/v1/mods/{numericProject}/files/{numericFile}", cancellationToken).ConfigureAwait(false);
            if (!exact.RootElement.TryGetProperty("data", out var file) || file.ValueKind != JsonValueKind.Object)
                return null;
            files = [file.Clone()];
        }
        else
        {
            using var inventory = await api.GetJsonAsync(
                $"/v1/mods/{numericProject}/files?gameVersion=" + Uri.EscapeDataString(minecraftVersion) +
                (contentKind == CurseForgeGeneratedContentKind.Mod ? $"&modLoaderType={loaderType}" : "") +
                "&pageSize=50&index=0", cancellationToken).ConfigureAwait(false);
            if (!inventory.RootElement.TryGetProperty("data", out var values) ||
                values.ValueKind != JsonValueKind.Array) return null;
            files = values.EnumerateArray().Select(value => value.Clone()).ToArray();
        }

        foreach (var file in files.Where(CurseForgeCatalogProvider.FileAvailable)
                     .OrderBy(file => ReleaseRank(CurseForgeCatalogProvider.ReleaseType(file)))
                     .ThenByDescending(file => Date(file, "fileDate")))
        {
            if (Long(file, "modId") != numericProject) continue;
            if (!string.IsNullOrWhiteSpace(versionId) &&
                !CurseForgeCatalogProvider.Text(file, "id").Equals(versionId, StringComparison.Ordinal)) continue;
            var gameVersions = Strings(file, "gameVersions");
            if (!gameVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase) ||
                contentKind == CurseForgeGeneratedContentKind.Mod &&
                !gameVersions.Contains(loader, StringComparer.OrdinalIgnoreCase)) continue;
            var fileId = file.GetProperty("id").ToString();
            var url = Text(file, "downloadUrl");
            if (url.Length == 0)
            {
                using var resolved = await api.GetJsonAsync(
                    $"/v1/mods/{numericProject}/files/{fileId}/download-url", cancellationToken)
                    .ConfigureAwait(false);
                if (resolved.RootElement.TryGetProperty("data", out var download) &&
                    download.ValueKind == JsonValueKind.String)
                    url = download.GetString() ?? "";
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !CurseForgeApiClient.IsApprovedDownloadUri(uri)) continue;
            var fileName = Text(file, "fileName");
            var sha1 = CurseForgeCatalogProvider.Hash(file, 1);
            var size = Long(file, "fileLength") ?? 0;
            var extension = contentKind == CurseForgeGeneratedContentKind.Mod ? ".jar" : ".zip";
            if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                sha1.Length != 40 || size is <= 0 or > JarInventoryService.MaximumJarBytes) continue;
            if (file.TryGetProperty("dependencies", out var boundedRelations) &&
                boundedRelations.ValueKind == JsonValueKind.Array && boundedRelations.GetArrayLength() > 128)
                throw new InvalidDataException("The exact CurseForge file exceeds the bounded dependency relationship limit; no relationships were silently discarded.");
            var dependencies = file.TryGetProperty("dependencies", out var relationValues) &&
                               relationValues.ValueKind == JsonValueKind.Array
                ? relationValues.EnumerateArray().Select(relation => new PluginDependency
                {
                    ProjectId = Long(relation, "modId")?.ToString(
                        System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    Type = RelationType(Long(relation, "relationType"))
                }).Where(relation => relation.ProjectId.Length > 0).Take(128).ToArray()
                : [];
            return new CurseForgePackFileRelease(new PluginRelease
            {
                Kind = ManagedAddonKind.Mod,
                Provider = Provider,
                ProjectId = numericProject.ToString(System.Globalization.CultureInfo.InvariantCulture),
                VersionId = fileId,
                VersionName = Text(file, "displayName") is { Length: > 0 } display
                    ? display : fileName,
                MinecraftVersion = minecraftVersion,
                Loader = loader,
                ReleaseChannel = CurseForgeCatalogProvider.ReleaseType(file).ToString().ToLowerInvariant(),
                PublishedAt = Date(file, "fileDate") ?? DateTimeOffset.MinValue,
                DownloadUrl = url,
                FileName = fileName,
                SizeBytes = size,
                Sha1 = sha1,
                ServerSide = "unknown",
                ClientSide = "unknown",
                ClientRequirement = "Unknown",
                Dependencies = dependencies
            }, contentKind);
        }
        return null;
    }

    private static bool ProjectAvailable(JsonElement project) =>
        project.TryGetProperty("isAvailable", out var available) && available.ValueKind == JsonValueKind.True &&
        project.TryGetProperty("allowModDistribution", out var distribution) &&
        distribution.ValueKind == JsonValueKind.True;

    private static int LoaderType(string loader) => loader.Trim().ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0
    };

    private static int ReleaseRank(ReleaseChannel channel) => channel switch
    {
        ReleaseChannel.Stable => 0,
        ReleaseChannel.Beta => 1,
        _ => 2
    };

    private static string RelationType(long? value) => value switch
    {
        1 => "embedded",
        2 => "optional",
        3 => "required",
        4 => "tool",
        5 => "incompatible",
        6 => "include",
        _ => "unknown"
    };

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() ?? "" : "";

    private static long? Long(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.Number && result.TryGetInt64(out var number)
            ? number : null;

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String &&
        result.TryGetDateTimeOffset(out var date) ? date : null;

    private static string[] Strings(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.Array
            ? result.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "").ToArray() : [];
}

internal sealed record CurseForgePackFileRelease(
    PluginRelease Release,
    CurseForgeGeneratedContentKind ContentKind);

public sealed class ModrinthPluginProvider : IPluginCatalogProvider
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<string> PluginFacet = ["all_project_types:plugin"];
    private static readonly IReadOnlyList<string> ModFacet = ["all_project_types:mod"];
    private static readonly IReadOnlyList<string> ServerSideFacets = ["server_side:required", "server_side:optional"];
    internal const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient http;
    private readonly string? diskCacheRoot;
    private readonly ConcurrentDictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    public ModrinthPluginProvider(HttpClient? httpClient = null)
        : this(paths: null, httpClient, initialize: true)
    {
    }

    public ModrinthPluginProvider(AppDataPaths paths)
        : this(paths, httpClient: null, initialize: true)
    {
    }

    public ModrinthPluginProvider(AppDataPaths paths, HttpClient httpClient)
        : this((AppDataPaths?)paths, httpClient, initialize: true)
    {
    }

    private ModrinthPluginProvider(AppDataPaths? paths, HttpClient? httpClient, bool initialize)
    {
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        diskCacheRoot = paths is null ? null : Path.Combine(paths.CatalogCache, "plugins", "modrinth");
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3 (local Windows Minecraft server manager)");
        if (http.DefaultRequestHeaders.Accept.Count == 0)
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public PluginProviderKind Provider => PluginProviderKind.Modrinth;
    public PluginProviderStatus Status => new(Provider, true,
        "Official Modrinth search and version metadata are available on demand.");

    public async Task<IReadOnlyList<PluginProject>> SearchAsync(
        PluginCatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        var search = (query.Search ?? "").Trim();
        if (search.Length > 120)
            throw new ArgumentException("Plugin search is limited to 120 characters.", nameof(query));
        var loader = NormalizeLoader(query.Loader);
        var facets = new List<IReadOnlyList<string>>
        {
            query.Kind == ManagedAddonKind.Mod ? ModFacet : PluginFacet,
            ServerSideFacets,
            LoaderFacets(loader)
        };
        if (!string.IsNullOrWhiteSpace(query.MinecraftVersion))
            facets.Add(new[] { $"versions:{query.MinecraftVersion.Trim()}" });
        var url = "https://api.modrinth.com/v2/search?index=downloads&limit=" +
                  Math.Clamp(query.Limit, 1, 40) + "&query=" + Uri.EscapeDataString(search) +
                  "&facets=" + Uri.EscapeDataString(JsonSerializer.Serialize(facets, ProtocolJson.Options));
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return [];
        return hits.EnumerateArray().Select(hit => new PluginProject
        {
            Kind = query.Kind,
            Provider = Provider,
            ProjectId = Text(hit, "project_id"),
            Slug = Text(hit, "slug"),
            Name = Text(hit, "title"),
            Author = Text(hit, "author"),
            Summary = Text(hit, "description"),
            IconUrl = Text(hit, "icon_url"),
            ProjectUrl = $"https://modrinth.com/{(query.Kind == ManagedAddonKind.Mod ? "mod" : "plugin")}/" +
                         (Text(hit, "slug") is { Length: > 0 } slug ? slug : Text(hit, "project_id")),
            Downloads = hit.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : null,
            UpdatedAt = hit.TryGetProperty("date_modified", out var modified) && modified.TryGetDateTimeOffset(out var date) ? date : null,
            ServerSide = Text(hit, "server_side") is { Length: > 0 } side ? side : "unknown",
            ClientSide = Text(hit, "client_side") is { Length: > 0 } client ? client : "unknown",
            ClientRequirement = ClientRequirement(Text(hit, "server_side"), Text(hit, "client_side"))
        }).Where(project => project.ProjectId.Length > 0 && project.Name.Length > 0).ToArray();
    }

    public async Task<PluginRelease?> ResolveReleaseAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(minecraftVersion);
        loader = NormalizeLoader(loader);
        var loaderNames = LoaderNames(loader);
        var kind = loader is "fabric" or "neoforge" ? ManagedAddonKind.Mod : ManagedAddonKind.Plugin;
        using var project = await GetJsonAsync(
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId.Trim())}", cancellationToken)
            .ConfigureAwait(false);
        var projectType = Text(project.RootElement, "project_type");
        if (!projectType.Equals(kind == ManagedAddonKind.Mod ? "mod" : "plugin", StringComparison.OrdinalIgnoreCase))
            return null;
        var serverSide = Text(project.RootElement, "server_side");
        var clientSide = Text(project.RootElement, "client_side");
        if (serverSide.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
            return null;
        var url = $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId.Trim())}/version" +
                  "?game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { minecraftVersion.Trim() })) +
                  "&loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(loaderNames));
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;
        var releases = document.RootElement.EnumerateArray()
            .Where(item => string.IsNullOrWhiteSpace(versionId) || Text(item, "id").Equals(versionId, StringComparison.Ordinal))
            .Where(item => item.TryGetProperty("game_versions", out var games) &&
                           games.EnumerateArray().Any(value => value.GetString()?.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase) == true))
            .Where(item => item.TryGetProperty("loaders", out var loaders) &&
                           loaders.EnumerateArray().Any(value => loaderNames.Contains(value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)))
            .Select(item => ParseRelease(item, minecraftVersion, loader, kind, serverSide, clientSide))
            .Where(release => release is not null)
            .Cast<PluginRelease>()
            .OrderBy(release => release.ReleaseChannel == "release" ? 0 : release.ReleaseChannel == "beta" ? 1 : 2)
            .ThenByDescending(release => release.PublishedAt)
            .ToArray();
        return releases.FirstOrDefault();
    }

    private static PluginRelease? ParseRelease(
        JsonElement item,
        string minecraftVersion,
        string loader,
        ManagedAddonKind kind,
        string serverSide,
        string clientSide)
    {
        if (!item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return null;
        var candidates = files.EnumerateArray()
            .Where(file => Text(file, "filename").EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True)
            .ToArray();
        if (candidates.Length == 0)
            return null;
        var file = candidates[0];
        var hashes = file.TryGetProperty("hashes", out var values) ? values : default;
        var sha512 = hashes.ValueKind == JsonValueKind.Object ? Text(hashes, "sha512") : "";
        if (sha512.Length != 128)
            return null;
        var dependencies = item.TryGetProperty("dependencies", out var dependencyValues) &&
                           dependencyValues.ValueKind == JsonValueKind.Array
            ? dependencyValues.EnumerateArray().Select(value => new PluginDependency
            {
                ProjectId = Text(value, "project_id"),
                VersionId = Text(value, "version_id"),
                FileName = Text(value, "file_name"),
                Type = Text(value, "dependency_type") is { Length: > 0 } type ? type : "required"
            }).Take(128).ToArray()
            : [];
        return new PluginRelease
        {
            Kind = kind,
            Provider = PluginProviderKind.Modrinth,
            ProjectId = Text(item, "project_id"),
            VersionId = Text(item, "id"),
            VersionName = Text(item, "version_number") is { Length: > 0 } number ? number : Text(item, "name"),
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            ReleaseChannel = Text(item, "version_type") is { Length: > 0 } channel ? channel : "alpha",
            PublishedAt = item.TryGetProperty("date_published", out var published) && published.TryGetDateTimeOffset(out var date)
                ? date : DateTimeOffset.MinValue,
            DownloadUrl = Text(file, "url"),
            FileName = Text(file, "filename"),
            SizeBytes = file.TryGetProperty("size", out var size) && size.TryGetInt64(out var length) ? length : 0,
            Sha1 = hashes.ValueKind == JsonValueKind.Object ? Text(hashes, "sha1") : "",
            Sha512 = sha512,
            ServerSide = string.IsNullOrWhiteSpace(serverSide) ? "unknown" : serverSide,
            ClientSide = string.IsNullOrWhiteSpace(clientSide) ? "unknown" : clientSide,
            ClientRequirement = ClientRequirement(serverSide, clientSide),
            Dependencies = dependencies
        };
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(url, out var cached) && DateTimeOffset.UtcNow - cached.StoredAt < CacheDuration)
            return JsonDocument.Parse(cached.Json, new JsonDocumentOptions { MaxDepth = 64 });
        var disk = DiskCachePath(url);
        if (disk is not null && ReadDiskCache(disk, requireFresh: true) is { } fresh)
        {
            cache[url] = new CacheEntry(DateTimeOffset.UtcNow, fresh);
            return JsonDocument.Parse(fresh, new JsonDocumentOptions { MaxDepth = 64 });
        }
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
                throw new InvalidDataException("The plugin provider response exceeded the bounded cache limit.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[32 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new InvalidDataException("The plugin provider response exceeded the bounded cache limit.");
                await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var json = buffer.ToArray();
            using var validation = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            cache[url] = new CacheEntry(DateTimeOffset.UtcNow, json);
            if (disk is not null)
                WriteDiskCache(disk, json);
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                          exception is HttpRequestException or TaskCanceledException)
        {
            if (disk is not null && ReadDiskCache(disk, requireFresh: false) is { } stale)
                return JsonDocument.Parse(stale, new JsonDocumentOptions { MaxDepth = 64 });
            throw;
        }
    }

    private string? DiskCachePath(string url) => diskCacheRoot is null
        ? null
        : Path.Combine(diskCacheRoot,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".json");

    private static byte[]? ReadDiskCache(string path, bool requireFresh)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumResponseBytes ||
                requireFresh && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) >= CacheDuration)
                return null;
            var bytes = File.ReadAllBytes(path);
            using var validation = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            return bytes;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteDiskCache(string path, byte[] json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var partial = path + $".{Guid.NewGuid():N}.partial";
            File.WriteAllBytes(partial, json);
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Offline caching is a best-effort optimisation and never changes a valid provider response.
        }
    }

    private static string NormalizeLoader(string loader) =>
        string.IsNullOrWhiteSpace(loader) ? "paper" : loader.Trim().ToLowerInvariant();

    private static string[] LoaderNames(string loader) => loader switch
    {
        "paper" or "purpur" => ["paper", "purpur", "bukkit", "spigot"],
        _ => [loader]
    };

    private static string[] LoaderFacets(string loader) => LoaderNames(loader)
        .Select(value => $"categories:{value}").ToArray();

    private static string ClientRequirement(string serverSide, string clientSide)
    {
        if (serverSide.Equals("unsupported", StringComparison.OrdinalIgnoreCase)) return "ClientOnly";
        if (clientSide.Equals("required", StringComparison.OrdinalIgnoreCase)) return "ClientAndServer";
        if (clientSide.Equals("optional", StringComparison.OrdinalIgnoreCase)) return "ClientOptional";
        if (clientSide.Equals("unsupported", StringComparison.OrdinalIgnoreCase)) return "ServerOnly";
        return "Unknown";
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private sealed record CacheEntry(DateTimeOffset StoredAt, byte[] Json);
}
