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
