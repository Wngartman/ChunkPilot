using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed class ServerCapabilityDetectionService
{
    private readonly ChunkPilotStore store;

    public ServerCapabilityDetectionService(ChunkPilotStore store)
    {
        this.store = store;
    }

    public async Task<ServerCapabilityProfile> DetectAsync(
        ServerDefinition definition,
        CancellationToken cancellationToken = default)
    {
        var root = definition.RootPath;
        var executableName = Path.GetFileName(definition.Executable);
        var arguments = definition.Arguments;
        var edition = definition.Ecosystem == ServerEcosystem.Custom &&
                      (executableName.Equals("bedrock_server.exe", StringComparison.OrdinalIgnoreCase) ||
                       File.Exists(Path.Combine(root, "bedrock_server.exe")))
            ? ServerEdition.Bedrock
            : ServerEdition.Java;
        var evidence = new ServerCapabilityEvidence
        {
            Edition = edition,
            Ecosystem = definition.Ecosystem,
            HasManagedLaunchProfile = definition.IsManaged && !string.IsNullOrWhiteSpace(definition.Executable),
            UsesScriptLaunch = Path.GetExtension(definition.Executable) is ".bat" or ".cmd" or ".ps1",
            UsesDirectJarLaunch = arguments.Contains("-jar", StringComparison.OrdinalIgnoreCase),
            HasModsDirectory = Directory.Exists(Path.Combine(root, "mods")),
            HasPluginsDirectory = Directory.Exists(Path.Combine(root, "plugins")),
            HasGeyser = HasJar(root, "Geyser"),
            HasFloodgate = HasJar(root, "floodgate"),
            HasViaVersion = HasJar(root, "ViaVersion"),
            HasRconConfiguration = PropertyEnabled(root, "enable-rcon"),
            HasQueryConfiguration = PropertyEnabled(root, "enable-query"),
            DetectionDetail = $"Definition={definition.Ecosystem}; executable={executableName}; " +
                              $"mods={Directory.Exists(Path.Combine(root, "mods"))}; " +
                              $"plugins={Directory.Exists(Path.Combine(root, "plugins"))}"
        };
        var profile = ServerCapabilityPolicy.Build(definition, evidence);
        await store.UpsertCapabilityProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    private static bool HasJar(string root, string name)
    {
        foreach (var folder in new[] { "plugins", "mods" })
        {
            var path = Path.Combine(root, folder);
            if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*.jar", SearchOption.TopDirectoryOnly)
                    .Any(file => Path.GetFileName(file).Contains(name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static bool PropertyEnabled(string root, string key)
    {
        var path = Path.Combine(root, "server.properties");
        if (!File.Exists(path))
            return false;
        return File.ReadLines(path).Any(line =>
            line.Trim().Equals($"{key}=true", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class CanonicalPathLockManager
{
    private readonly object sync = new();
    private readonly Dictionary<string, SemaphoreSlim> locks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IAsyncDisposable> AcquireAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var canonical = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        SemaphoreSlim semaphore;
        lock (sync)
        {
            if (!locks.TryGetValue(canonical, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                locks[canonical] = semaphore;
            }
        }
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser(this, canonical, semaphore);
    }

    private void Release(string canonical, SemaphoreSlim semaphore)
    {
        semaphore.Release();
        lock (sync)
        {
            if (semaphore.CurrentCount == 1)
                locks.Remove(canonical);
        }
    }

    private sealed class Releaser(
        CanonicalPathLockManager owner,
        string canonical,
        SemaphoreSlim semaphore) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Release(canonical, semaphore);
            return ValueTask.CompletedTask;
        }
    }
}

public static class JarClassVersionInspector
{
    public static int GetRequiredJavaMajor(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);
        var highestClassMajor = 0;
        var header = new byte[8];
        foreach (var entry in archive.Entries.Where(entry =>
                     entry.FullName.EndsWith(".class", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            if (stream.Read(header) != header.Length ||
                header[0] != 0xCA || header[1] != 0xFE ||
                header[2] != 0xBA || header[3] != 0xBE)
                continue;
            var major = (header[6] << 8) | header[7];
            highestClassMajor = Math.Max(highestClassMajor, major);
        }
        return highestClassMajor == 0
            ? 0
            : JavaRuntimePolicy.JavaMajorForClassFile((ushort)highestClassMajor);
    }
}

public sealed record DatapackInspection(
    bool Valid,
    int PackFormat,
    string Description,
    CompatibilityState Compatibility,
    string Detail);

public sealed class DatapackService
{
    public DatapackInspection Inspect(string path, string minecraftVersion)
    {
        JsonDocument? document = null;
        try
        {
            if (Directory.Exists(path))
            {
                var metadata = Path.Combine(path, "pack.mcmeta");
                if (!File.Exists(metadata))
                    return Invalid("pack.mcmeta is missing.");
                document = JsonDocument.Parse(File.ReadAllText(metadata, Encoding.UTF8));
            }
            else if (File.Exists(path) &&
                     Path.GetExtension(path).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                var metadata = archive.GetEntry("pack.mcmeta");
                if (metadata is null)
                    return Invalid("pack.mcmeta is missing from the ZIP root.");
                using var stream = metadata.Open();
                document = JsonDocument.Parse(stream);
            }
            else
            {
                return Invalid("Select a datapack ZIP or folder.");
            }

            using (document)
            {
                var pack = document.RootElement.GetProperty("pack");
                var format = pack.GetProperty("pack_format").GetInt32();
                var description = pack.TryGetProperty("description", out var value)
                    ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString()
                    : "";
                var expected = ExpectedPackFormat(minecraftVersion);
                var compatibility = expected == 0 ? CompatibilityState.Unknown :
                    format == expected ? CompatibilityState.Compatible :
                    Math.Abs(format - expected) <= 1 ? CompatibilityState.LikelyCompatible :
                    CompatibilityState.Incompatible;
                return new DatapackInspection(true, format, description, compatibility,
                    expected == 0
                        ? "Minecraft version is unknown; review pack format manually."
                        : $"Pack format {format}; expected {expected} for {minecraftVersion}.");
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or KeyNotFoundException)
        {
            document?.Dispose();
            return Invalid(exception.Message);
        }
    }

    private static DatapackInspection Invalid(string detail) =>
        new(false, 0, "", CompatibilityState.Incompatible, detail);

    private static int ExpectedPackFormat(string version)
    {
        if (!Version.TryParse(version.Split('-')[0], out var parsed))
            return 0;
        if (parsed >= new Version(1, 21, 11)) return 94;
        if (parsed >= new Version(1, 21, 9)) return 88;
        if (parsed >= new Version(1, 21, 7)) return 81;
        if (parsed >= new Version(1, 21, 6)) return 80;
        if (parsed >= new Version(1, 21, 5)) return 71;
        if (parsed >= new Version(1, 21, 4)) return 61;
        if (parsed >= new Version(1, 21, 2)) return 57;
        if (parsed >= new Version(1, 21)) return 48;
        if (parsed >= new Version(1, 20, 5)) return 41;
        if (parsed >= new Version(1, 20, 3)) return 26;
        if (parsed >= new Version(1, 20, 2)) return 18;
        if (parsed >= new Version(1, 20)) return 15;
        if (parsed >= new Version(1, 19, 4)) return 12;
        if (parsed >= new Version(1, 19)) return 10;
        if (parsed >= new Version(1, 18, 2)) return 9;
        return 0;
    }
}

public interface IGuidedCatalogProvider
{
    CatalogProvider Provider { get; }
    bool IsAvailable { get; }
    string AvailabilityDetail { get; }
    Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CatalogGameVersion>> GetGameVersionsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogGameVersion>>([]);

    Task<CatalogItem?> ResolveProjectAsync(
        string projectReference,
        string? exactReleaseReference,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<CatalogItem?>(null);
}

public sealed record CatalogProviderPage(
    IReadOnlyList<CatalogItem> Items,
    int NextIndex,
    bool HasMore,
    int? TotalCount = null);

public interface IPaginatedGuidedCatalogProvider
{
    Task<CatalogProviderPage> BrowsePageAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class GuidedCatalogService
{
    private static readonly TimeSpan ProviderRequestBudget = TimeSpan.FromSeconds(10);
    private readonly AppDataPaths paths;
    private readonly IReadOnlyDictionary<CatalogProvider, IGuidedCatalogProvider> providers;
    private readonly TimeSpan cacheLifetime;
    private readonly TimeSpan offlineCacheLifetime;

    public GuidedCatalogService(
        AppDataPaths paths,
        IEnumerable<IGuidedCatalogProvider> providers,
        TimeSpan? cacheLifetime = null)
    {
        this.paths = paths;
        this.providers = providers.ToDictionary(provider => provider.Provider);
        this.cacheLifetime = cacheLifetime ?? TimeSpan.FromHours(6);
        offlineCacheLifetime = TimeSpan.FromDays(30);
    }

    public IReadOnlyList<CatalogProviderStatus> GetProviderStatuses() =>
        Enum.GetValues<CatalogProvider>().Select(provider =>
        {
            if (!providers.TryGetValue(provider, out var adapter))
                return new CatalogProviderStatus(provider, false, "No automated adapter is registered.");
            return new CatalogProviderStatus(provider, adapter.IsAvailable, adapter.AvailabilityDetail);
        }).ToArray();

    public async Task<CatalogItem?> ResolveProjectAsync(
        CatalogProvider provider,
        string projectReference,
        string? exactReleaseReference,
        CancellationToken cancellationToken = default)
    {
        if (!providers.TryGetValue(provider, out var adapter) || !adapter.IsAvailable)
            return null;
        return await adapter.ResolveProjectAsync(projectReference, exactReleaseReference, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        var selected = query.Provider is { } provider
            ? providers.Values.Where(item => item.Provider == provider)
            : providers.Values.Where(item => item.Provider is CatalogProvider.Modrinth or
                CatalogProvider.Mojang or CatalogProvider.Paper or CatalogProvider.Purpur);
        var results = new List<CatalogItem>();
        foreach (var adapter in selected.Where(adapter => adapter.IsAvailable))
        {
            try
            {
                var items = await adapter.BrowseAsync(query, cancellationToken).ConfigureAwait(false);
                results.AddRange(items);
                if (adapter.Provider != CatalogProvider.CurseForge)
                    await WriteCacheAsync(adapter.Provider, query, items, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                if (adapter.Provider != CatalogProvider.CurseForge)
                {
                    var cached = await ReadCacheAsync(adapter.Provider, query, cancellationToken).ConfigureAwait(false);
                    results.AddRange(cached);
                }
            }
        }
        return CatalogPolicy.Filter(results, query);
    }

    public async Task<CatalogBrowseResult> BrowseCacheAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Provider is not { } provider)
            throw new ArgumentException("A provider is required for cache browsing.", nameof(query));
        if (provider == CatalogProvider.CurseForge)
            return new CatalogBrowseResult
            {
                Provider = provider,
                State = CatalogLoadState.Empty,
                Detail = "CurseForge API results are not stored under the current third-party terms. Refresh this page while online."
            };
        var cached = await ReadCacheEnvelopeAsync(provider, query, cancellationToken).ConfigureAwait(false);
        if (cached is null || DateTimeOffset.UtcNow - cached.CreatedAt > offlineCacheLifetime)
            return new CatalogBrowseResult
            {
                Provider = provider,
                State = CatalogLoadState.Empty,
                Detail = "No cached provider results are available."
            };
        var items = CatalogPolicy.Filter(cached.Items, query);
        return new CatalogBrowseResult
        {
            Provider = provider,
            State = items.Count > 0 ? CatalogLoadState.OfflineCache : CatalogLoadState.Empty,
            Items = items,
            Detail = items.Count > 0
                ? "Showing the last provider results while ChunkPilot refreshes them."
                : "The cached provider result contains no matching server packs.",
            RetrievedAt = cached.CreatedAt,
            FromCache = true,
            Stale = DateTimeOffset.UtcNow - cached.CreatedAt > cacheLifetime,
            NextIndex = query.Index + query.Limit,
            HasMore = items.Count == query.Limit
        };
    }

    public async Task<CatalogBrowseResult> BrowseDetailedAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        if (query.Provider is not { } provider)
            throw new ArgumentException("A provider is required for detailed browsing.", nameof(query));
        if (!providers.TryGetValue(provider, out var adapter))
            return Failure(provider, CatalogLoadState.Failed, "No provider adapter is registered.", "provider");
        if (!adapter.IsAvailable)
            return Failure(provider,
                provider == CatalogProvider.CurseForge
                    ? CatalogLoadState.AuthenticationRequired
                    : CatalogLoadState.Failed,
                adapter.AvailabilityDetail,
                provider == CatalogProvider.CurseForge ? "authentication" : "provider");

        try
        {
            var page = adapter is IPaginatedGuidedCatalogProvider paginated
                ? await paginated.BrowsePageAsync(query, cancellationToken).ConfigureAwait(false)
                : new CatalogProviderPage(
                    await adapter.BrowseAsync(query, cancellationToken).ConfigureAwait(false),
                    query.Index + query.Limit,
                    false,
                    null);
            var items = CatalogPolicy.Filter(page.Items, query);
            if (provider != CatalogProvider.CurseForge)
                await WriteCacheAsync(provider, query, items, cancellationToken).ConfigureAwait(false);
            return new CatalogBrowseResult
            {
                Provider = provider,
                State = items.Count > 0 ? CatalogLoadState.Ready : CatalogLoadState.Empty,
                Items = items,
                Detail = items.Count > 0
                    ? $"Loaded {items.Count} provider result{(items.Count == 1 ? "" : "s")}."
                    : "No packs matched the current filters.",
                RetrievedAt = DateTimeOffset.UtcNow,
                NextIndex = page.NextIndex,
                HasMore = page.HasMore,
                TotalCount = page.TotalCount
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            var state = exception.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => CatalogLoadState.AuthenticationRequired,
                System.Net.HttpStatusCode.TooManyRequests => CatalogLoadState.RateLimited,
                _ => CatalogLoadState.Failed
            };
            return await ProviderFailureAsync(provider, query, state,
                state == CatalogLoadState.AuthenticationRequired
                    ? "The provider rejected the configured credentials."
                    : state == CatalogLoadState.RateLimited
                        ? "The provider rate limit is active. Try again shortly."
                        : "The provider could not be reached.",
                "provider request", cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return await ProviderFailureAsync(provider, query, CatalogLoadState.Failed,
                "The provider request timed out.", "provider request", cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await ProviderFailureAsync(provider, query, CatalogLoadState.Failed,
                "The provider response or local cache could not be read.", "provider response", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<CatalogVersionInventory> GetVersionInventoryAsync(
        CatalogProvider provider,
        bool cacheOnly,
        CancellationToken cancellationToken = default)
    {
        var cached = provider == CatalogProvider.CurseForge
            ? null
            : await ReadVersionInventoryCacheAsync(provider, cancellationToken).ConfigureAwait(false);
        if (cacheOnly)
            return cached ?? VersionInventoryFailure(provider, CatalogLoadState.Empty,
                provider == CatalogProvider.CurseForge
                    ? "CurseForge version data is not stored under the current third-party terms."
                    : "No cached Minecraft version inventory is available.", "cache");
        if (!providers.TryGetValue(provider, out var adapter))
            return cached ?? VersionInventoryFailure(provider, CatalogLoadState.Failed,
                "No provider adapter is registered.", "provider");
        if (!adapter.IsAvailable)
            return cached ?? VersionInventoryFailure(provider,
                provider == CatalogProvider.CurseForge
                    ? CatalogLoadState.AuthenticationRequired
                    : CatalogLoadState.Failed,
                adapter.AvailabilityDetail,
                provider == CatalogProvider.CurseForge ? "authentication" : "provider");

        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestBudget.CancelAfter(ProviderRequestBudget);
        try
        {
            var versions = (await adapter.GetGameVersionsAsync(requestBudget.Token).ConfigureAwait(false))
                .Where(version => !string.IsNullOrWhiteSpace(version.VersionId))
                .GroupBy(version => version.VersionId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(version => version.PublishedAt).First())
                .OrderByDescending(version => version.PublishedAt)
                .ThenByDescending(version => version.VersionId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (versions.Length == 0)
                return cached ?? VersionInventoryFailure(provider, CatalogLoadState.Empty,
                    "The provider returned no Minecraft versions.", "provider response");
            var now = DateTimeOffset.UtcNow;
            if (provider != CatalogProvider.CurseForge)
                await WriteVersionInventoryCacheAsync(provider, versions, now, cancellationToken).ConfigureAwait(false);
            return new CatalogVersionInventory
            {
                Provider = provider,
                State = CatalogLoadState.Ready,
                Versions = versions,
                Detail = $"Loaded {versions.Length} official provider version{(versions.Length == 1 ? "" : "s")}.",
                RetrievedAt = now
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException or
                                           JsonException or InvalidDataException)
        {
            return cached is not null
                ? cached with
                {
                    State = CatalogLoadState.OfflineCache,
                    Detail = "The provider version inventory could not be refreshed. Showing cached versions.",
                    FailedStage = "provider request"
                }
                : VersionInventoryFailure(provider, CatalogLoadState.Failed,
                    exception is TaskCanceledException
                        ? "The provider version request timed out."
                        : "The provider version inventory could not be loaded.",
                    "provider request");
        }
    }

    private async Task<CatalogBrowseResult> ProviderFailureAsync(
        CatalogProvider provider,
        CatalogQuery query,
        CatalogLoadState failureState,
        string detail,
        string failedStage,
        CancellationToken cancellationToken)
    {
        if (provider == CatalogProvider.CurseForge)
            return Failure(provider, failureState, detail, failedStage);
        var cached = await BrowseCacheAsync(query, cancellationToken).ConfigureAwait(false);
        if (cached.Items.Count > 0)
            return cached with
            {
                State = CatalogLoadState.OfflineCache,
                Detail = detail + " Showing cached results instead.",
                FailedStage = failedStage
            };
        return Failure(provider, failureState, detail, failedStage);
    }

    private static CatalogBrowseResult Failure(
        CatalogProvider provider,
        CatalogLoadState state,
        string detail,
        string failedStage) => new()
        {
            Provider = provider,
            State = state,
            Detail = detail,
            FailedStage = failedStage
        };

    private static CatalogVersionInventory VersionInventoryFailure(
        CatalogProvider provider,
        CatalogLoadState state,
        string detail,
        string failedStage) => new()
        {
            Provider = provider,
            State = state,
            Detail = detail,
            FailedStage = failedStage
        };

    private async Task WriteVersionInventoryCacheAsync(
        CatalogProvider provider,
        IReadOnlyList<CatalogGameVersion> versions,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.CatalogCache);
        var path = VersionInventoryCachePath(provider);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(new CatalogVersionInventoryCacheEnvelope(createdAt, versions), ProtocolJson.Options),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    private async Task<CatalogVersionInventory?> ReadVersionInventoryCacheAsync(
        CatalogProvider provider,
        CancellationToken cancellationToken)
    {
        var path = VersionInventoryCachePath(provider);
        if (!File.Exists(path)) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<CatalogVersionInventoryCacheEnvelope>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                ProtocolJson.Options);
            if (envelope is null || DateTimeOffset.UtcNow - envelope.CreatedAt > offlineCacheLifetime)
                return null;
            return new CatalogVersionInventory
            {
                Provider = provider,
                State = CatalogLoadState.OfflineCache,
                Versions = envelope.Versions,
                Detail = "Showing the cached official provider version inventory.",
                RetrievedAt = envelope.CreatedAt,
                FromCache = true,
                Stale = DateTimeOffset.UtcNow - envelope.CreatedAt > cacheLifetime
            };
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync(
        CatalogProvider provider,
        CatalogQuery query,
        IReadOnlyList<CatalogItem> items,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.CatalogCache);
        var path = CachePath(provider, query);
        var temporary = path + ".tmp";
        var envelope = new CatalogCacheEnvelope(DateTimeOffset.UtcNow, items);
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(envelope, ProtocolJson.Options),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    private async Task<IReadOnlyList<CatalogItem>> ReadCacheAsync(
        CatalogProvider provider,
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        var envelope = await ReadCacheEnvelopeAsync(provider, query, cancellationToken).ConfigureAwait(false);
        return envelope is not null && DateTimeOffset.UtcNow - envelope.CreatedAt <= cacheLifetime
            ? envelope.Items
            : [];
    }

    private async Task<CatalogCacheEnvelope?> ReadCacheEnvelopeAsync(
        CatalogProvider provider,
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        var path = CachePath(provider, query);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<CatalogCacheEnvelope>(
                await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
                ProtocolJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private string CachePath(CatalogProvider provider, CatalogQuery query)
    {
        var serialized = JsonSerializer.Serialize(query, ProtocolJson.Options);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized)));
        return Path.Combine(paths.CatalogCache, $"{provider}-{hash}.json");
    }

    private string VersionInventoryCachePath(CatalogProvider provider) =>
        Path.Combine(paths.CatalogCache, $"{provider}-minecraft-versions.json");

    private sealed record CatalogCacheEnvelope(
        DateTimeOffset CreatedAt,
        IReadOnlyList<CatalogItem> Items);

    private sealed record CatalogVersionInventoryCacheEnvelope(
        DateTimeOffset CreatedAt,
        IReadOnlyList<CatalogGameVersion> Versions);
}

public abstract class HttpCatalogProvider
{
    protected HttpClient Http { get; }

    protected HttpCatalogProvider(HttpClient? httpClient = null)
    {
        Http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (Http.DefaultRequestHeaders.UserAgent.Count == 0)
            Http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ChunkPilot/1.3.0 (local Windows Minecraft server manager)");
    }

    protected async Task<JsonDocument> GetJsonAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

public sealed class BuiltInServerCatalogProvider : HttpCatalogProvider, IGuidedCatalogProvider
{
    private readonly ServerDownloadCatalog catalog;
    private readonly InstallSourceType sourceType;

    public BuiltInServerCatalogProvider(
        CatalogProvider provider,
        InstallSourceType sourceType,
        ServerDownloadCatalog catalog)
    {
        Provider = provider;
        this.sourceType = sourceType;
        this.catalog = catalog;
    }

    public CatalogProvider Provider { get; }
    public bool IsAvailable => true;
    public string AvailabilityDetail => "Official metadata adapter is available.";

    public async Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        var versions = await catalog.GetVersionsAsync(sourceType, false, cancellationToken).ConfigureAwait(false);
        return
        [
            new CatalogItem
            {
                Provider = Provider,
                ContentType = CatalogContentType.ServerSoftware,
                ProjectId = Provider.ToString().ToLowerInvariant(),
                Slug = Provider.ToString().ToLowerInvariant(),
                Name = Provider switch
                {
                    CatalogProvider.Mojang => "Official Vanilla",
                    _ => Provider.ToString()
                },
                Author = Provider == CatalogProvider.Mojang ? "Mojang Studios" : Provider.ToString(),
                Summary = Provider == CatalogProvider.Mojang
                    ? "The official Minecraft Java Edition dedicated server."
                    : $"{Provider} server software.",
                ClientRequirement = ClientRequirement.None,
                InstallationSupport = InstallationSupportState.FullyAutomated,
                Categories = ["vanilla+", "server utilities"],
                Versions = versions.Take(100).Select(version => new CatalogVersion
                {
                    VersionId = version,
                    VersionName = version,
                    MinecraftVersion = version,
                    Loader = Provider.ToString(),
                    ReleaseChannel = ReleaseChannel.Stable,
                    HasServerPackage = true,
                    RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(version)
                }).ToArray()
            }
        ];
    }
}

public sealed class ModrinthCatalogProvider : HttpCatalogProvider, IGuidedCatalogProvider,
    IPaginatedGuidedCatalogProvider
{
    public ModrinthCatalogProvider(HttpClient? httpClient = null) : base(httpClient) { }

    public CatalogProvider Provider => CatalogProvider.Modrinth;
    public bool IsAvailable => true;
    public string AvailabilityDetail => "Official Modrinth search and project-version APIs are available.";

    public async Task<IReadOnlyList<CatalogGameVersion>> GetGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(
            "https://api.modrinth.com/v2/tag/game_version", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Modrinth's game-version response was not a version list.");
        return document.RootElement.EnumerateArray().Select(version =>
        {
            var kind = version.TryGetProperty("version_type", out var type) ? type.GetString() : null;
            return new CatalogGameVersion
            {
                VersionId = version.TryGetProperty("version", out var id) ? id.GetString() ?? "" : "",
                Kind = kind switch
                {
                    "release" => CatalogGameVersionKind.Release,
                    "snapshot" => CatalogGameVersionKind.Snapshot,
                    "beta" => CatalogGameVersionKind.Beta,
                    "alpha" => CatalogGameVersionKind.Alpha,
                    _ => CatalogGameVersionKind.Unknown
                },
                PublishedAt = version.TryGetProperty("date", out var date) &&
                              date.TryGetDateTimeOffset(out var published) ? published : null,
                IsMajor = version.TryGetProperty("major", out var major) &&
                          major.ValueKind is JsonValueKind.True or JsonValueKind.False && major.GetBoolean()
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
        => (await BrowsePageAsync(query, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<CatalogProviderPage> BrowsePageAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        if (TryGetProjectReference(query.Search, out var projectReference))
        {
            var exact = await ResolveFullProjectAsync(
                projectReference, null, query with { Search = "" }, cancellationToken).ConfigureAwait(false);
            return new CatalogProviderPage(exact is null ? [] : [exact], exact is null ? 0 : 1, false,
                exact is null ? 0 : 1);
        }

        var facets = new List<IReadOnlyList<string>>
        {
            new List<string> { "project_type:modpack" },
            new List<string> { "server_side:required", "server_side:optional" }
        };
        if (!string.IsNullOrWhiteSpace(query.MinecraftVersion))
            facets.Add(new List<string> { $"versions:{query.MinecraftVersion}" });
        if (!string.IsNullOrWhiteSpace(query.Loader))
            facets.Add(new List<string> { $"categories:{query.Loader.ToLowerInvariant()}" });
        if (!string.IsNullOrWhiteSpace(query.Category))
            facets.Add(new List<string> { $"categories:{query.Category.ToLowerInvariant()}" });
        var facetJson = JsonSerializer.Serialize(facets, ProtocolJson.Options);
        var providerIndex = query.Sort switch
        {
            CatalogSort.Downloads => "downloads",
            CatalogSort.Follows => "follows",
            CatalogSort.Newest => "newest",
            CatalogSort.Relevance => "relevance",
            _ => "updated"
        };
        var pageSize = Math.Clamp(query.Limit, 1, 100);
        var requestedIndex = Math.Max(0, query.Index);
        var url = "https://api.modrinth.com/v2/search?limit=" +
                  pageSize + "&offset=" + requestedIndex +
                  "&index=" + providerIndex + "&query=" + Uri.EscapeDataString(query.Search) +
                  "&facets=" + Uri.EscapeDataString(facetJson);
        using var search = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var hits = search.RootElement.GetProperty("hits").EnumerateArray().Select(hit => hit.Clone()).ToArray();
        int? totalCount = null;
        if (search.RootElement.TryGetProperty("total_hits", out var total) &&
            total.TryGetInt64(out var providerTotal))
        {
            if (providerTotal < 0)
                throw new InvalidDataException("Modrinth returned invalid pagination metadata.");
            totalCount = (int)Math.Min(int.MaxValue, providerTotal);
        }
        IReadOnlyList<CatalogItem> items = hits.Select(ParseSearchSummary).ToArray();
        if (query.Sort == CatalogSort.Name)
            items = items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var nextIndex = checked(requestedIndex + hits.Length);
        var hasMore = hits.Length > 0 &&
                      (totalCount is { } count ? nextIndex < count : hits.Length == pageSize);
        return new CatalogProviderPage(items, nextIndex, hasMore, totalCount);
    }

    public Task<CatalogItem?> ResolveProjectAsync(
        string projectReference,
        string? exactReleaseReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectReference);
        if (!TryNormalizeProjectReference(projectReference, out var normalized))
            return Task.FromResult<CatalogItem?>(null);
        return ResolveFullProjectAsync(normalized, exactReleaseReference, new CatalogQuery
        {
            Provider = CatalogProvider.Modrinth,
            MaximumChannel = ReleaseChannel.Alpha,
            ServerPackRequired = false,
            ExcludeClientOnly = false,
            Limit = 100
        }, cancellationToken);
    }

    private async Task<CatalogItem?> ResolveFullProjectAsync(
        string projectReference,
        string? exactReleaseReference,
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        using var projectDocument = await GetJsonAsync(
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectReference)}",
            cancellationToken).ConfigureAwait(false);
        var project = projectDocument.RootElement;
        if (!project.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Modrinth's project response did not contain an id.");
        var projectId = id.GetString() ?? "";
        var versions = await GetVersionsAsync(projectId, query, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(exactReleaseReference))
        {
            versions = versions.Where(version => version.VersionId.Equals(
                exactReleaseReference.Trim(), StringComparison.Ordinal)).ToArray();
            if (versions.Count == 0) return null;
        }
        return ParseResolvedProject(project, projectId, versions);
    }

    private static CatalogItem ParseResolvedProject(
        JsonElement project,
        string projectId,
        IReadOnlyList<CatalogVersion> versions)
    {
        var serverSide = project.TryGetProperty("server_side", out var server)
            ? server.GetString() : "unknown";
        var slug = project.TryGetProperty("slug", out var slugValue)
            ? slugValue.GetString() ?? "" : "";
        return new CatalogItem
        {
            Provider = CatalogProvider.Modrinth,
            ContentType = CatalogContentType.Modpack,
            ProjectId = projectId,
            Slug = slug,
            Name = project.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Author = "Modrinth project",
            Summary = project.TryGetProperty("description", out var description)
                ? description.GetString() ?? "" : "",
            IconUrl = project.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
            ProjectUrl = "https://modrinth.com/modpack/" + (slug.Length > 0 ? slug : projectId),
            DownloadCount = project.TryGetProperty("downloads", out var downloads) &&
                            downloads.TryGetInt64(out var downloadCount) ? downloadCount : null,
            UpdatedAt = project.TryGetProperty("updated", out var updated) &&
                        updated.TryGetDateTimeOffset(out var updatedAt) ? updatedAt : null,
            ClientRequirement = serverSide == "required"
                ? ClientRequirement.MatchingPackRequired : ClientRequirement.Unknown,
            InstallationSupport = versions.Any(version => version.HasServerPackage)
                ? InstallationSupportState.AutomatedWithReview : InstallationSupportState.ClientOnly,
            Categories = project.TryGetProperty("categories", out var categories) &&
                         categories.ValueKind == JsonValueKind.Array
                ? categories.EnumerateArray().Select(value => value.GetString() ?? "")
                    .Where(value => value.Length > 0).ToArray()
                : [],
            Versions = versions,
            ServerPathChecked = true
        };
    }

    private static CatalogItem ParseSearchSummary(JsonElement hit)
    {
        var projectId = hit.TryGetProperty("project_id", out var id) ? id.GetString() ?? "" : "";
        var slug = hit.TryGetProperty("slug", out var slugValue) ? slugValue.GetString() ?? "" : "";
        var serverSide = hit.TryGetProperty("server_side", out var server) ? server.GetString() : "unknown";
        return new CatalogItem
        {
            Provider = CatalogProvider.Modrinth,
            ContentType = CatalogContentType.Modpack,
            ProjectId = projectId,
            Slug = slug,
            Name = hit.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Author = hit.TryGetProperty("author", out var author) ? author.GetString() ?? "" : "",
            Summary = hit.TryGetProperty("description", out var description)
                ? description.GetString() ?? "" : "",
            IconUrl = hit.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
            ProjectUrl = "https://modrinth.com/modpack/" + (slug.Length > 0 ? slug : projectId),
            DownloadCount = hit.TryGetProperty("downloads", out var downloads) &&
                            downloads.TryGetInt64(out var downloadCount) ? downloadCount : null,
            UpdatedAt = hit.TryGetProperty("date_modified", out var updated) &&
                        updated.TryGetDateTimeOffset(out var updatedAt) ? updatedAt : null,
            ClientRequirement = serverSide == "required"
                ? ClientRequirement.MatchingPackRequired : ClientRequirement.Unknown,
            InstallationSupport = InstallationSupportState.ManualPackageRequired,
            Categories = hit.TryGetProperty("categories", out var categories) &&
                         categories.ValueKind == JsonValueKind.Array
                ? categories.EnumerateArray().Select(value => value.GetString() ?? "")
                    .Where(value => value.Length > 0).ToArray()
                : [],
            Versions = [],
            ServerPathChecked = false
        };
    }

    private static bool TryNormalizeProjectReference(string value, out string projectReference)
    {
        if (TryGetProjectReference(value, out projectReference)) return true;
        var candidate = value.Trim();
        if (candidate.Length is < 1 or > 80 || candidate.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_')))
        {
            projectReference = "";
            return false;
        }
        projectReference = candidate;
        return true;
    }

    private static bool TryGetProjectReference(string value, out string projectReference)
    {
        projectReference = "";
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.IdnHost.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase) ||
              uri.IdnHost.Equals("www.modrinth.com", StringComparison.OrdinalIgnoreCase)))
            return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !segments[0].Equals("modpack", StringComparison.OrdinalIgnoreCase))
            return false;
        var candidate = Uri.UnescapeDataString(segments[1]).Trim();
        if (candidate.Length is < 1 or > 80 || candidate.Any(character =>
                !(char.IsLetterOrDigit(character) || character is '-' or '_')))
            return false;
        projectReference = candidate;
        return true;
    }

    private async Task<IReadOnlyList<CatalogVersion>> GetVersionsAsync(
        string projectId,
        CatalogQuery query,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectId)}/version",
            cancellationToken).ConfigureAwait(false);
        var versions = new List<CatalogVersion>();
        foreach (var version in document.RootElement.EnumerateArray())
        {
            var channel = version.GetProperty("version_type").GetString() switch
            {
                "beta" => ReleaseChannel.Beta,
                "alpha" => ReleaseChannel.Alpha,
                _ => ReleaseChannel.Stable
            };
            var games = version.GetProperty("game_versions").EnumerateArray()
                .Select(item => item.GetString() ?? "").ToArray();
            var loaders = version.GetProperty("loaders").EnumerateArray()
                .Select(item => item.GetString() ?? "").ToArray();
            if (!string.IsNullOrWhiteSpace(query.MinecraftVersion) &&
                !games.Contains(query.MinecraftVersion, StringComparer.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(query.Loader) &&
                !loaders.Contains(query.Loader, StringComparer.OrdinalIgnoreCase))
                continue;
            var file = version.GetProperty("files").EnumerateArray()
                .Where(item =>
                {
                    var name = item.GetProperty("filename").GetString() ?? "";
                    return name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) &&
                           item.TryGetProperty("url", out var value) &&
                           Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) &&
                           uri.Scheme == Uri.UriSchemeHttps &&
                           uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(item => item.TryGetProperty("primary", out var primary) && primary.GetBoolean())
                .FirstOrDefault();
            versions.Add(new CatalogVersion
            {
                VersionId = version.GetProperty("id").GetString() ?? "",
                VersionName = version.GetProperty("name").GetString() ?? "",
                MinecraftVersion = !string.IsNullOrWhiteSpace(query.MinecraftVersion)
                    ? games.FirstOrDefault(game => game.Equals(query.MinecraftVersion, StringComparison.OrdinalIgnoreCase)) ?? ""
                    : games.FirstOrDefault() ?? "",
                Loader = !string.IsNullOrWhiteSpace(query.Loader)
                    ? loaders.FirstOrDefault(loader => loader.Equals(query.Loader, StringComparison.OrdinalIgnoreCase)) ?? ""
                    : loaders.FirstOrDefault() ?? "",
                ReleaseChannel = channel,
                PublishedAt = version.TryGetProperty("date_published", out var published) &&
                              published.TryGetDateTimeOffset(out var date) ? date : null,
                DownloadUrl = file.ValueKind == JsonValueKind.Object
                    ? file.GetProperty("url").GetString() ?? "" : "",
                Sha1 = file.ValueKind == JsonValueKind.Object &&
                       file.TryGetProperty("hashes", out var hashes) &&
                       hashes.TryGetProperty("sha1", out var sha1) ? sha1.GetString() ?? "" : "",
                Sha512 = file.ValueKind == JsonValueKind.Object &&
                         file.TryGetProperty("hashes", out hashes) &&
                         hashes.TryGetProperty("sha512", out var sha512)
                    ? sha512.GetString() ?? "" : "",
                SizeBytes = file.ValueKind == JsonValueKind.Object &&
                            file.TryGetProperty("size", out var size) ? size.GetInt64() : null,
                Changelog = version.TryGetProperty("changelog", out var changelog) ? changelog.GetString() ?? "" : "",
                HasServerPackage = file.ValueKind == JsonValueKind.Object,
                RequiredJavaMajor = games.Length > 0
                    ? JavaRuntimePolicy.TryRequiredMajorForMinecraft(
                        !string.IsNullOrWhiteSpace(query.MinecraftVersion)
                            ? games.FirstOrDefault(game => game.Equals(query.MinecraftVersion, StringComparison.OrdinalIgnoreCase)) ?? games[0]
                            : games[0]) ?? 0 : 0
            });
        }
        return versions;
    }
}

public sealed class CurseForgeCatalogProvider : IGuidedCatalogProvider, IPaginatedGuidedCatalogProvider, IDisposable
{
    private const int MinecraftGameId = 432;
    private const int ModpackClassId = 4471;
    private readonly CurseForgeApiClient api;
    private readonly bool ownsApi;

    public CurseForgeCatalogProvider(ISecretStore secrets)
        : this(new CurseForgeApiClient(secrets), ownsApi: true)
    {
    }

    internal CurseForgeCatalogProvider(ISecretStore secrets, HttpMessageHandler fixtureTransport)
        : this(new CurseForgeApiClient(secrets, fixtureTransport), ownsApi: true)
    {
    }

    public CurseForgeCatalogProvider(CurseForgeApiClient api)
        : this(api, ownsApi: false)
    {
    }

    private CurseForgeCatalogProvider(CurseForgeApiClient api, bool ownsApi)
    {
        this.api = api;
        this.ownsApi = ownsApi;
    }

    public CatalogProvider Provider => CatalogProvider.CurseForge;
    public bool IsAvailable => api.HasCredential;
    public string AvailabilityDetail => IsAvailable
        ? "Approved CurseForge access is available for this local native session."
        : "CurseForge is unavailable because the approved local native credential is missing.";

    public async Task<IReadOnlyList<CatalogGameVersion>> GetGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];
        using var document = await api.GetJsonAsync(
            "/v1/minecraft/version?sortDescending=true", cancellationToken).ConfigureAwait(false);
        var data = RequireArray(document.RootElement, "data", "Minecraft-version");
        return data.EnumerateArray().Select(version =>
        {
            var id = Text(version, "versionString");
            return new CatalogGameVersion
            {
                VersionId = id,
                Kind = ClassifyMinecraftVersion(id),
                PublishedAt = Date(version, "dateModified"),
                IsMajor = ClassifyMinecraftVersion(id) == CatalogGameVersionKind.Release
            };
        }).Where(version => version.VersionId.Length > 0).Take(10_000).ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
        => (await BrowsePageAsync(query, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<CatalogProviderPage> BrowsePageAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return new CatalogProviderPage([], Math.Max(0, query.Index), false, 0);
        var loaderType = LoaderType(query.Loader);
        var categoryId = await ResolveCategoryIdAsync(query.Category, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(query.Category) && categoryId is null)
            return new CatalogProviderPage([], Math.Max(0, query.Index), false, 0);
        var sortField = query.Sort switch
        {
            CatalogSort.Downloads => 6,
            CatalogSort.Newest => 11,
            CatalogSort.Updated => 3,
            CatalogSort.Name => 4,
            _ => 2
        };
        var sortOrder = query.Sort == CatalogSort.Name ? "asc" : "desc";
        var pageSize = Math.Clamp(query.Limit, 1, 50);
        var path = "/v1/mods/search?gameId=" + MinecraftGameId + "&classId=" + ModpackClassId +
                   "&pageSize=" + pageSize +
                   "&index=" + Math.Max(0, query.Index) +
                   "&searchFilter=" + Uri.EscapeDataString(query.Search.Trim()) +
                   $"&sortField={sortField}&sortOrder={sortOrder}" +
                   (string.IsNullOrWhiteSpace(query.MinecraftVersion)
                       ? "" : "&gameVersion=" + Uri.EscapeDataString(query.MinecraftVersion.Trim())) +
                    (loaderType == 0 || string.IsNullOrWhiteSpace(query.MinecraftVersion)
                        ? "" : $"&modLoaderType={loaderType}") +
                    (categoryId is null ? "" : $"&categoryId={categoryId.Value}");
        using var document = await api.GetJsonAsync(path, cancellationToken).ConfigureAwait(false);
        var data = RequireArray(document.RootElement, "data", "search");
        var items = new List<CatalogItem>();
        foreach (var project in data.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = ParseProjectSummary(project);
            if (parsed is not null) items.Add(parsed);
        }
        var requestedIndex = Math.Max(0, query.Index);
        var rawCount = data.GetArrayLength();
        var nextIndex = Math.Min(10_000, requestedIndex + rawCount);
        var hasMore = rawCount == pageSize && nextIndex < 10_000;
        int? totalCount = null;
        if (document.RootElement.TryGetProperty("pagination", out var pagination) &&
            pagination.ValueKind == JsonValueKind.Object)
        {
            var responseIndex = Number(pagination, "index") ?? requestedIndex;
            var resultCount = Number(pagination, "resultCount") ?? rawCount;
            var providerTotal = Number(pagination, "totalCount");
            if (responseIndex < 0 || resultCount < 0 || providerTotal is < 0)
                throw new InvalidDataException("CurseForge returned invalid pagination metadata.");
            nextIndex = checked((int)Math.Min(10_000, responseIndex + resultCount));
            totalCount = providerTotal is { } totalValue
                ? (int)Math.Min(int.MaxValue, totalValue)
                : null;
            hasMore = providerTotal is { } knownTotal
                ? resultCount > 0 && nextIndex < Math.Min(10_000, knownTotal)
                : resultCount == pageSize && nextIndex < 10_000;
        }
        return new CatalogProviderPage(items, nextIndex, hasMore, totalCount);
    }

    private async Task<long?> ResolveCategoryIdAsync(string category, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category)) return null;
        using var document = await api.GetJsonAsync(
            $"/v1/categories?gameId={MinecraftGameId}&classId={ModpackClassId}", cancellationToken)
            .ConfigureAwait(false);
        return RequireArray(document.RootElement, "data", "category inventory").EnumerateArray()
            .Where(item => Text(item, "slug").Equals(category.Trim(), StringComparison.OrdinalIgnoreCase) ||
                           Text(item, "name").Equals(category.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(item => Number(item, "id"))
            .FirstOrDefault(value => value is > 0);
    }

    public async Task<CatalogItem?> ResolveProjectAsync(
        string projectReference,
        string? exactReleaseReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectReference);
        JsonElement project;
        JsonDocument? searchDocument = null;
        JsonDocument? projectDocument = null;
        try
        {
            string projectId;
            if (long.TryParse(projectReference, out var numericId) && numericId > 0)
            {
                projectId = numericId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                searchDocument = await api.GetJsonAsync(
                    $"/v1/mods/search?gameId={MinecraftGameId}&classId={ModpackClassId}&pageSize=50&slug=" +
                    Uri.EscapeDataString(projectReference.Trim()), cancellationToken).ConfigureAwait(false);
                var candidates = RequireArray(searchDocument.RootElement, "data", "project lookup")
                    .EnumerateArray().Where(candidate =>
                        Text(candidate, "slug").Equals(projectReference, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (candidates.Length != 1) return null;
                projectId = candidates[0].GetProperty("id").ToString();
            }

            projectDocument = await api.GetJsonAsync(
                $"/v1/mods/{Uri.EscapeDataString(projectId)}", cancellationToken).ConfigureAwait(false);
            project = RequireObject(projectDocument.RootElement, "data", "project");
            if (!Text(project, "id").Equals(projectId, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(projectReference, out _) &&
                !Text(project, "slug").Equals(projectReference, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!IsProjectAvailable(project)) return null;

            IReadOnlyList<JsonElement> files;
            if (!string.IsNullOrWhiteSpace(exactReleaseReference))
            {
                if (!long.TryParse(exactReleaseReference, out var fileId) || fileId <= 0)
                    return null;
                using var exact = await api.GetJsonAsync(
                    $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{fileId}", cancellationToken)
                    .ConfigureAwait(false);
                var file = RequireObject(exact.RootElement, "data", "exact file").Clone();
                if (Number(file, "modId") is { } parentId &&
                    !parentId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        .Equals(projectId, StringComparison.Ordinal))
                    return null;
                files = [file];
            }
            else
            {
                using var inventory = await api.GetJsonAsync(
                    $"/v1/mods/{Uri.EscapeDataString(projectId)}/files?pageSize=50&index=0",
                    cancellationToken).ConfigureAwait(false);
                files = RequireArray(inventory.RootElement, "data", "file inventory")
                    .EnumerateArray().Select(file => file.Clone()).ToArray();
            }
            return await ParseProjectAsync(project, new CatalogQuery
            {
                Provider = CatalogProvider.CurseForge,
                MaximumChannel = ReleaseChannel.Alpha,
                ServerPackRequired = false,
                ExcludeClientOnly = false,
                Limit = 50
            }, cancellationToken, files).ConfigureAwait(false);
        }
        finally
        {
            searchDocument?.Dispose();
            projectDocument?.Dispose();
        }
    }

    private async Task<CatalogItem?> ParseProjectAsync(
        JsonElement project,
        CatalogQuery query,
        CancellationToken cancellationToken,
        IReadOnlyList<JsonElement>? exactFiles = null)
    {
        if (!IsProjectAvailable(project)) return null;
        var projectId = project.GetProperty("id").ToString();
        var files = exactFiles ?? (project.TryGetProperty("latestFiles", out var latest) &&
                                   latest.ValueKind == JsonValueKind.Array
            ? latest.EnumerateArray().Select(file => file.Clone()).Take(20).ToArray()
            : []);
        var versions = new List<CatalogVersion>();
        foreach (var clientFile in files)
        {
            if (!FileAvailable(clientFile)) continue;
            var candidate = await ResolveClientPackFileAsync(
                projectId, ParseClientFile(clientFile, query), clientFile, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(candidate.ServerPackFileId))
            {
                versions.Add(candidate);
                continue;
            }
            versions.Add(await ResolveServerPackFileAsync(
                projectId, candidate, cancellationToken).ConfigureAwait(false));
        }

        var support = versions.Any(version => version.HasServerPackage)
            ? InstallationSupportState.FullyAutomated
            : versions.Any(version => version.CanGenerateServerCandidate)
                ? InstallationSupportState.AutomatedWithReview
            : versions.Count > 0
                ? InstallationSupportState.ManualPackageRequired
                : InstallationSupportState.ClientOnly;
        return new CatalogItem
        {
            Provider = CatalogProvider.CurseForge,
            ContentType = CatalogContentType.Modpack,
            ProjectId = projectId,
            Slug = Text(project, "slug"),
            Name = Text(project, "name"),
            Author = project.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
                ? string.Join(", ", authors.EnumerateArray().Select(author => Text(author, "name"))
                    .Where(name => name.Length > 0).Take(20)) : "",
            Summary = Text(project, "summary"),
            IconUrl = project.TryGetProperty("logo", out var logo) ? Text(logo, "thumbnailUrl") : "",
            ProjectUrl = project.TryGetProperty("links", out var links) ? Text(links, "websiteUrl") : "",
            DownloadCount = Number(project, "downloadCount"),
            UpdatedAt = Date(project, "dateModified"),
            Categories = project.TryGetProperty("categories", out var categories) &&
                         categories.ValueKind == JsonValueKind.Array
                ? categories.EnumerateArray().Select(category => Text(category, "slug"))
                    .Where(value => value.Length > 0).Take(50).ToArray() : [],
            ClientRequirement = ClientRequirement.MatchingPackRequired,
            InstallationSupport = support,
            Versions = versions.OrderByDescending(version => version.PublishedAt).ToArray()
        };
    }

    private static CatalogItem? ParseProjectSummary(JsonElement project)
    {
        if (!IsProjectAvailable(project)) return null;
        var projectId = project.GetProperty("id").ToString();
        return new CatalogItem
        {
            Provider = CatalogProvider.CurseForge,
            ContentType = CatalogContentType.Modpack,
            ProjectId = projectId,
            Slug = Text(project, "slug"),
            Name = Text(project, "name"),
            Author = project.TryGetProperty("authors", out var authors) && authors.ValueKind == JsonValueKind.Array
                ? string.Join(", ", authors.EnumerateArray().Select(author => Text(author, "name"))
                    .Where(name => name.Length > 0).Take(20)) : "",
            Summary = Text(project, "summary"),
            IconUrl = project.TryGetProperty("logo", out var logo) ? Text(logo, "thumbnailUrl") : "",
            ProjectUrl = project.TryGetProperty("links", out var links) ? Text(links, "websiteUrl") : "",
            DownloadCount = Number(project, "downloadCount"),
            UpdatedAt = Date(project, "dateModified"),
            Categories = project.TryGetProperty("categories", out var categories) &&
                         categories.ValueKind == JsonValueKind.Array
                ? categories.EnumerateArray().Select(category => Text(category, "slug"))
                    .Where(value => value.Length > 0).Take(50).ToArray() : [],
            ClientRequirement = ClientRequirement.MatchingPackRequired,
            InstallationSupport = InstallationSupportState.ManualPackageRequired,
            Versions = [],
            ServerPathChecked = false
        };
    }

    private static CatalogVersion ParseClientFile(JsonElement file, CatalogQuery query)
    {
        var gameVersions = Strings(file, "gameVersions");
        var clientFileId = file.GetProperty("id").ToString();
        var serverPackId = Number(file, "serverPackFileId")?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ?? "";
        var minecraft = !string.IsNullOrWhiteSpace(query.MinecraftVersion)
            ? gameVersions.FirstOrDefault(value => value.Equals(query.MinecraftVersion,
                StringComparison.OrdinalIgnoreCase)) ?? ""
            : gameVersions.FirstOrDefault(IsMinecraftVersion) ?? "";
        var loader = !string.IsNullOrWhiteSpace(query.Loader)
            ? gameVersions.FirstOrDefault(value => value.Equals(query.Loader,
                StringComparison.OrdinalIgnoreCase)) ?? ""
            : gameVersions.FirstOrDefault(IsLoader) ?? "";
        return new CatalogVersion
        {
            VersionId = clientFileId,
            ClientFileId = clientFileId,
            ServerPackFileId = serverPackId,
            VersionName = Text(file, "displayName") is { Length: > 0 } display
                ? display : Text(file, "fileName"),
            MinecraftVersion = minecraft,
            Loader = loader,
            ReleaseChannel = ReleaseType(file),
            PublishedAt = Date(file, "fileDate"),
            HasServerPackage = false,
            CreationPreflightState = CatalogReleasePreflightState.Required,
            CreationPreflightDetail =
                "Inspect the exact client manifest to establish its loader version before creation.",
            Available = FileAvailable(file),
            DistributionAllowed = true,
            RequiredJavaMajor = JavaRuntimePolicy.TryRequiredMajorForMinecraft(minecraft) ?? 0
        };
    }

    private async Task<CatalogVersion> ResolveServerPackFileAsync(
        string projectId,
        CatalogVersion client,
        CancellationToken cancellationToken)
    {
        using var document = await api.GetJsonAsync(
            $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(client.ServerPackFileId)}",
            cancellationToken).ConfigureAwait(false);
        var file = RequireObject(document.RootElement, "data", "server-pack file");
        if (!FileAvailable(file)) return client with { Available = false };
        var serverId = file.GetProperty("id").ToString();
        if (!serverId.Equals(client.ServerPackFileId, StringComparison.Ordinal))
            throw new InvalidDataException("CurseForge returned a contradictory server-pack relationship.");
        if (Number(file, "modId") is { } parent &&
            !parent.ToString(System.Globalization.CultureInfo.InvariantCulture).Equals(projectId, StringComparison.Ordinal))
            throw new InvalidDataException("The CurseForge server-pack file belongs to a different project.");
        var url = Text(file, "downloadUrl");
        if (url.Length == 0)
        {
            using var resolved = await api.GetJsonAsync(
                $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{serverId}/download-url",
                cancellationToken).ConfigureAwait(false);
            if (resolved.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String)
                url = value.GetString() ?? "";
        }
        var approved = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                       CurseForgeApiClient.IsApprovedDownloadUri(uri);
        var sha1 = Hash(file, 1);
        var available = approved && sha1.Length == 40 && Number(file, "fileLength") is > 0;
        return client with
        {
            DownloadUrl = approved ? url : "",
            Sha1 = sha1,
            SizeBytes = Number(file, "fileLength"),
            HasServerPackage = available,
            Available = FileAvailable(file),
            DistributionAllowed = approved
        };
    }

    private async Task<CatalogVersion> ResolveClientPackFileAsync(
        string projectId,
        CatalogVersion client,
        JsonElement file,
        CancellationToken cancellationToken)
    {
        var url = Text(file, "downloadUrl");
        if (url.Length == 0)
        {
            using var resolved = await api.GetJsonAsync(
                $"/v1/mods/{Uri.EscapeDataString(projectId)}/files/{Uri.EscapeDataString(client.ClientFileId)}/download-url",
                cancellationToken).ConfigureAwait(false);
            if (resolved.RootElement.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.String)
                url = value.GetString() ?? "";
        }
        var approved = Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                       CurseForgeApiClient.IsApprovedDownloadUri(uri);
        var sha1 = Hash(file, 1);
        var size = Number(file, "fileLength");
        var fileName = Text(file, "fileName");
        var canGenerate = approved && sha1.Length == 40 && size is > 0 &&
                          fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                          client.MinecraftVersion.Length > 0 && client.Loader.Length > 0;
        return client with
        {
            ClientDownloadUrl = approved ? url : "",
            ClientSha1 = sha1,
            ClientSizeBytes = size,
            CanGenerateServerCandidate = canGenerate
        };
    }

    private static JsonElement RequireArray(JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"CurseForge's {label} response was not an array.");
        return value;
    }

    private static JsonElement RequireObject(JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"CurseForge's {label} response was not an object.");
        return value;
    }

    internal static bool FileAvailable(JsonElement file) =>
        file.TryGetProperty("isAvailable", out var available) && available.ValueKind == JsonValueKind.True;

    private static bool IsProjectAvailable(JsonElement project) =>
        project.TryGetProperty("isAvailable", out var available) && available.ValueKind == JsonValueKind.True &&
        project.TryGetProperty("allowModDistribution", out var distribution) &&
        distribution.ValueKind == JsonValueKind.True;

    internal static string Hash(JsonElement file, int algorithm) =>
        file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Array
            ? hashes.EnumerateArray().FirstOrDefault(hash =>
                hash.TryGetProperty("algo", out var algo) && algo.TryGetInt32(out var value) && value == algorithm)
                is var match && match.ValueKind == JsonValueKind.Object ? Text(match, "value") : ""
            : "";

    internal static string Text(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var text) &&
        text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" :
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out text) &&
        text.ValueKind == JsonValueKind.Number ? text.ToString() : "";

    private static long? Number(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var number) &&
        number.TryGetInt64(out var result) ? result : null;

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var date) &&
        date.ValueKind == JsonValueKind.String && date.TryGetDateTimeOffset(out var result) ? result : null;

    private static string[] Strings(JsonElement value, string property) =>
        value.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
            : [];

    internal static ReleaseChannel ReleaseType(JsonElement file) =>
        Number(file, "releaseType") switch
        {
            1 => ReleaseChannel.Stable,
            2 => ReleaseChannel.Beta,
            _ => ReleaseChannel.Alpha
        };

    private static int LoaderType(string loader) => loader.Trim().ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => 0
    };

    internal static bool IsLoader(string value) => value.Equals("Fabric", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Forge", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("NeoForge", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Quilt", StringComparison.OrdinalIgnoreCase);

    private static bool IsMinecraftVersion(string value) => value.Length > 0 &&
        (char.IsDigit(value[0]) || char.ToLowerInvariant(value[0]) is 'a' or 'b');

    private static CatalogGameVersionKind ClassifyMinecraftVersion(string version)
    {
        var normalized = version.Trim().ToLowerInvariant();
        if (normalized.StartsWith("alpha", StringComparison.Ordinal) ||
            normalized.StartsWith("a1.", StringComparison.Ordinal))
            return CatalogGameVersionKind.Alpha;
        if (normalized.StartsWith("beta", StringComparison.Ordinal) ||
            normalized.StartsWith("b1.", StringComparison.Ordinal))
            return CatalogGameVersionKind.Beta;
        if (normalized.Contains("snapshot", StringComparison.Ordinal) ||
            normalized.Contains("-pre", StringComparison.Ordinal) ||
            normalized.Contains("-rc", StringComparison.Ordinal) ||
            normalized.Length >= 5 && char.IsDigit(normalized[0]) && normalized.Contains('w'))
            return CatalogGameVersionKind.Snapshot;
        return normalized.Length > 0 ? CatalogGameVersionKind.Release : CatalogGameVersionKind.Unknown;
    }

    public void Dispose()
    {
        if (ownsApi) api.Dispose();
    }
}

public sealed class UnavailableCatalogProvider(
    CatalogProvider provider,
    string detail) : IGuidedCatalogProvider
{
    public CatalogProvider Provider => provider;
    public bool IsAvailable => false;
    public string AvailabilityDetail => detail;
    public Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<CatalogItem>>([]);
}

public interface IManagedJavaPackageProvider
{
    Task<ManagedJavaPackage> ResolveAsync(
        int majorVersion,
        CancellationToken cancellationToken = default);
}

public sealed class AdoptiumTemurinProvider : HttpCatalogProvider, IManagedJavaPackageProvider
{
    public AdoptiumTemurinProvider(HttpClient? httpClient = null) : base(httpClient) { }

    public async Task<ManagedJavaPackage> ResolveAsync(
        int majorVersion,
        CancellationToken cancellationToken = default)
    {
        // Temurin stopped publishing separate JRE archives for some historical feature releases
        // (notably Java 16), while the official JDK archive remains available and contains the same
        // managed java.exe runtime. Prefer the smaller JRE and fall back only when it does not exist.
        foreach (var imageType in new[] { "jre", "jdk" })
        {
            using var document = await GetJsonAsync(
                $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot" +
                $"?architecture=x64&heap_size=normal&image_type={imageType}&jvm_impl=hotspot&os=windows&vendor=eclipse",
                cancellationToken).ConfigureAwait(false);
            var release = document.RootElement.EnumerateArray().FirstOrDefault();
            if (release.ValueKind == JsonValueKind.Undefined)
                continue;
            var binary = release.GetProperty("binary").GetProperty("package");
            return new ManagedJavaPackage
            {
                MajorVersion = majorVersion,
                Version = release.TryGetProperty("release_name", out var name) ? name.GetString() ?? "" : "",
                Architecture = "x64",
                DownloadUrl = binary.GetProperty("link").GetString() ??
                              throw new InvalidDataException("Temurin package URL was missing."),
                FileName = binary.GetProperty("name").GetString() ?? $"temurin-{majorVersion}-{imageType}.zip",
                Sha256 = binary.GetProperty("checksum").GetString() ??
                         throw new InvalidDataException("Temurin package checksum was missing."),
                SizeBytes = binary.TryGetProperty("size", out var size) ? size.GetInt64() : null
            };
        }

        throw new InvalidOperationException($"Eclipse Temurin did not return a Windows x64 Java {majorVersion} JRE or JDK runtime.");
    }
}

public sealed class ManagedJavaRuntimeService
{
    private readonly AppDataPaths paths;
    private readonly ChunkPilotStore store;
    private readonly IManagedJavaPackageProvider provider;
    private readonly HttpClient http;

    public ManagedJavaRuntimeService(
        AppDataPaths paths,
        ChunkPilotStore store,
        IManagedJavaPackageProvider provider,
        HttpClient? httpClient = null)
    {
        this.paths = paths;
        this.store = store;
        this.provider = provider;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    }

    public async Task<ManagedJavaRuntime> InstallAsync(
        int majorVersion,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var package = await provider.ResolveAsync(majorVersion, cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var staging = Path.Combine(paths.ManagedJava, $".staging-{operationId:N}");
        var archive = Path.Combine(paths.Staging, $"{operationId:N}-{Path.GetFileName(package.FileName)}");
        var finalRoot = Path.Combine(paths.ManagedJava,
            $"temurin-{majorVersion}-{MakeSafeSegment(package.Version)}-x64");
        if (Directory.Exists(finalRoot))
        {
            var existingJava = FindJava(finalRoot);
            var existing = await InspectAsync(existingJava, true, finalRoot, package, cancellationToken)
                .ConfigureAwait(false);
            await store.UpsertManagedJavaRuntimeAsync(existing, cancellationToken).ConfigureAwait(false);
            return existing;
        }
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report($"Downloading {package.Vendor} Java {majorVersion}.");
            using (var response = await http.GetAsync(package.DownloadUrl,
                       HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(archive, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            VerifySha256(archive, package.Sha256);
            progress?.Report("Extracting the verified runtime into isolated staging.");
            await ExtractZipSafeAsync(archive, staging, cancellationToken).ConfigureAwait(false);
            var java = FindJava(staging);
            var inspected = await InspectAsync(java, true, finalRoot, package, cancellationToken)
                .ConfigureAwait(false);
            var wrapper = SingleWrapperDirectory(staging);
            if (wrapper is not null)
                Directory.Move(wrapper, finalRoot);
            else
                Directory.Move(staging, finalRoot);
            var finalJava = FindJava(finalRoot);
            var runtime = inspected with
            {
                JavaPath = finalJava,
                InstallationRoot = finalRoot,
                InstalledAt = DateTimeOffset.UtcNow
            };
            await store.UpsertManagedJavaRuntimeAsync(runtime, cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, true);
            throw;
        }
        finally
        {
            if (File.Exists(archive))
                File.Delete(archive);
        }
    }

    public async Task<ManagedJavaRuntime> HealthCheckAsync(
        ManagedJavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        var package = new ManagedJavaPackage
        {
            MajorVersion = runtime.MajorVersion,
            Version = runtime.Version,
            DownloadUrl = runtime.SourceUrl,
            Sha256 = runtime.Sha256
        };
        var inspected = await InspectAsync(runtime.JavaPath, runtime.IsManaged,
            runtime.InstallationRoot, package, cancellationToken).ConfigureAwait(false);
        inspected = inspected with { Id = runtime.Id, InstalledAt = runtime.InstalledAt };
        await store.UpsertManagedJavaRuntimeAsync(inspected, cancellationToken).ConfigureAwait(false);
        return inspected;
    }

    public async Task RemoveUnusedAsync(
        ManagedJavaRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        if (!runtime.IsManaged)
            throw new InvalidOperationException("ChunkPilot never removes system or user-selected Java.");
        var root = Path.GetFullPath(runtime.InstallationRoot);
        var managedRoot = Path.GetFullPath(paths.ManagedJava) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The runtime is outside ChunkPilot managed Java storage.");
        await store.DeleteManagedJavaRuntimeAsync(runtime.Id, cancellationToken).ConfigureAwait(false);
        if (Directory.Exists(root))
        {
            Directory.CreateDirectory(paths.Recovery);
            var destination = Path.Combine(paths.Recovery,
                $"{Path.GetFileName(root)}-removed-{DateTimeOffset.Now:yyyyMMdd-HHmmss}");
            Directory.Move(root, destination);
        }
    }

    private static async Task<ManagedJavaRuntime> InspectAsync(
        string javaPath,
        bool managed,
        string root,
        ManagedJavaPackage package,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(javaPath))
            throw new FileNotFoundException("Java executable was not found.", javaPath);
        var start = new ProcessStartInfo
        {
            FileName = javaPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(javaPath)!
        };
        start.ArgumentList.Add("-XshowSettings:properties");
        start.ArgumentList.Add("-version");
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException("Windows did not start the Java health check.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken)
            .ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false) + Environment.NewLine +
                     await errorTask.ConfigureAwait(false);
        var architecture = output.Contains("sun.arch.data.model = 64", StringComparison.OrdinalIgnoreCase) ||
                           output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)
            ? "x64" : output.Contains("32-Bit", StringComparison.OrdinalIgnoreCase) ? "x86" : "Unknown";
        var major = ParseJavaMajor(output);
        var healthy = process.ExitCode == 0 && major > 0 && architecture != "x86";
        return new ManagedJavaRuntime
        {
            Vendor = output.Contains("Temurin", StringComparison.OrdinalIgnoreCase) ||
                     output.Contains("Eclipse Adoptium", StringComparison.OrdinalIgnoreCase)
                ? "Eclipse Temurin" : "Unknown",
            Version = package.Version,
            MajorVersion = major > 0 ? major : package.MajorVersion,
            Architecture = architecture,
            JavaPath = javaPath,
            InstallationRoot = root,
            SourceUrl = package.DownloadUrl,
            Sha256 = package.Sha256,
            IsManaged = managed,
            Health = healthy ? RuntimeHealth.Healthy : RuntimeHealth.Unhealthy,
            LastHealthCheckAt = DateTimeOffset.UtcNow
        };
    }

    private static int ParseJavaMajor(string output)
    {
        var marker = output.Contains("version \"1.", StringComparison.OrdinalIgnoreCase)
            ? "version \"1." : "version \"";
        var index = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return 0;
        var start = index + marker.Length;
        var digits = new string(output.Skip(start).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var major) ? major : 0;
    }

    private static string FindJava(string root) =>
        Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => Path.GetFileName(Path.GetDirectoryName(path)!)
                .Equals("bin", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException("The runtime archive did not contain bin\\java.exe.");

    private static string? SingleWrapperDirectory(string staging)
    {
        var files = Directory.EnumerateFiles(staging, "*", SearchOption.TopDirectoryOnly).Any();
        var directories = Directory.EnumerateDirectories(staging, "*", SearchOption.TopDirectoryOnly).ToArray();
        return !files && directories.Length == 1 ? directories[0] : null;
    }

    private static string MakeSafeSegment(string value) =>
        new(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '.').ToArray());

    internal static void VerifySha256(string path, string expected)
    {
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidDataException("A managed runtime package requires an official SHA-256 checksum.");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Runtime SHA-256 mismatch. Expected {expected}; received {actual}.");
    }

    internal static async Task ExtractZipSafeAsync(
        string archivePath,
        string destination,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.GetFullPath(Path.Combine(destination,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"ZIP entry escapes runtime staging: {entry.FullName}");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous);
            await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class LoaderMetadataService : HttpCatalogProvider
{
    public LoaderMetadataService(HttpClient? httpClient = null) : base(httpClient) { }

    public async Task<LoaderInstallPlan> ResolveAsync(
        InstallSourceType loader,
        string minecraftVersion,
        string requestedLoaderVersion,
        CancellationToken cancellationToken = default)
    {
        return loader switch
        {
            InstallSourceType.Fabric => await ResolveFabricAsync(minecraftVersion,
                requestedLoaderVersion, cancellationToken).ConfigureAwait(false),
            InstallSourceType.Quilt => await ResolveQuiltAsync(minecraftVersion,
                requestedLoaderVersion, cancellationToken).ConfigureAwait(false),
            InstallSourceType.Forge => await ResolveForgeAsync(minecraftVersion,
                requestedLoaderVersion, cancellationToken).ConfigureAwait(false),
            InstallSourceType.NeoForge => await ResolveNeoForgeAsync(minecraftVersion,
                requestedLoaderVersion, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"{loader} is not a managed loader.")
        };
    }

    private async Task<LoaderInstallPlan> ResolveFabricAsync(
        string minecraft,
        string requested,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraft)}",
            cancellationToken).ConfigureAwait(false);
        var item = document.RootElement.EnumerateArray()
            .FirstOrDefault(value => string.IsNullOrWhiteSpace(requested) ||
                value.GetProperty("loader").GetProperty("version").GetString()
                    ?.Equals(requested, StringComparison.OrdinalIgnoreCase) == true);
        if (item.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Fabric loader {requested} is unavailable for {minecraft}.");
        var loader = item.GetProperty("loader").GetProperty("version").GetString() ?? "";
        using var installers = await GetJsonAsync(
            "https://meta.fabricmc.net/v2/versions/installer",
            cancellationToken).ConfigureAwait(false);
        var installerItem = installers.RootElement.EnumerateArray()
            .FirstOrDefault(value =>
                !value.TryGetProperty("stable", out var stable) || stable.GetBoolean());
        if (installerItem.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("Fabric did not publish an installer version.");
        var installer = installerItem.GetProperty("version").GetString()
            ?? throw new InvalidOperationException("Fabric returned an invalid installer version.");
        return new LoaderInstallPlan
        {
            Loader = InstallSourceType.Fabric,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            InstallerVersion = installer,
            DownloadUrl = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraft)}/" +
                          $"{Uri.EscapeDataString(loader)}/{Uri.EscapeDataString(installer)}/server/jar",
            ExpectedLaunchFile = "fabric-server-launch.jar",
            RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft),
            RunsInstaller = false
        };
    }

    private async Task<LoaderInstallPlan> ResolveQuiltAsync(
        string minecraft,
        string requested,
        CancellationToken cancellationToken)
    {
        using var loaders = await GetJsonAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(minecraft)}",
            cancellationToken).ConfigureAwait(false);
        var item = loaders.RootElement.EnumerateArray().FirstOrDefault(value =>
            string.IsNullOrWhiteSpace(requested) ||
            value.GetProperty("loader").GetProperty("version").GetString()
                ?.Equals(requested, StringComparison.OrdinalIgnoreCase) == true);
        if (item.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"Quilt loader {requested} is unavailable for {minecraft}.");
        var loader = item.GetProperty("loader").GetProperty("version").GetString() ?? "";
        var installer = await LatestMavenVersionAsync(
            "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/maven-metadata.xml",
            cancellationToken).ConfigureAwait(false);
        var baseUrl = "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/" +
                      $"{installer}/quilt-installer-{installer}.jar";
        return new LoaderInstallPlan
        {
            Loader = InstallSourceType.Quilt,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            InstallerVersion = installer,
            DownloadUrl = baseUrl,
            Sha1 = await TryGetTextAsync(baseUrl + ".sha1", cancellationToken).ConfigureAwait(false),
            InstallerArgument = $"install server {minecraft} {loader} --download-server --install-dir=.",
            ExpectedLaunchFile = "quilt-server-launch.jar",
            RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft),
            RunsInstaller = true
        };
    }

    private async Task<LoaderInstallPlan> ResolveForgeAsync(
        string minecraft,
        string requested,
        CancellationToken cancellationToken)
    {
        var versions = await MavenVersionsAsync(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
            cancellationToken).ConfigureAwait(false);
        var combined = versions.LastOrDefault(version =>
            version.StartsWith(minecraft + "-", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(requested) ||
             version.Equals($"{minecraft}-{requested}", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"Forge {requested} is unavailable for {minecraft}.");
        var loader = combined[(minecraft.Length + 1)..];
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{combined}/forge-{combined}-installer.jar";
        return new LoaderInstallPlan
        {
            Loader = InstallSourceType.Forge,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            DownloadUrl = url,
            Sha1 = await TryGetTextAsync(url + ".sha1", cancellationToken).ConfigureAwait(false),
            InstallerArgument = "--installServer",
            ExpectedLaunchFile = "run.bat",
            RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft),
            RunsInstaller = true
        };
    }

    private async Task<LoaderInstallPlan> ResolveNeoForgeAsync(
        string minecraft,
        string requested,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(
            ManagedLoaderCatalogService.NeoForgeVersionsApiUrl,
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("versions", out var versionList) ||
            versionList.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("NeoForge's official repository API returned no version list.");
        var versions = versionList.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => item.Length > 0)
            .ToArray();
        var prefix = minecraft.StartsWith("1.", StringComparison.Ordinal) ? minecraft[2..] + "." : minecraft + ".";
        var loader = versions.LastOrDefault(version =>
            (version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
             version.StartsWith(minecraft + "-", StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(requested) ||
             version.Equals(requested, StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException($"NeoForge {requested} is unavailable for {minecraft}.");
        var url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{loader}/neoforge-{loader}-installer.jar";
        return new LoaderInstallPlan
        {
            Loader = InstallSourceType.NeoForge,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            DownloadUrl = url,
            Sha1 = await TryGetTextAsync(url + ".sha1", cancellationToken).ConfigureAwait(false),
            InstallerArgument = "--installServer",
            ExpectedLaunchFile = "run.bat",
            RequiredJavaMajor = JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft),
            RunsInstaller = true
        };
    }

    private async Task<string> LatestMavenVersionAsync(string url, CancellationToken cancellationToken)
    {
        var release = (await MavenDocumentAsync(url, cancellationToken).ConfigureAwait(false))
            .Descendants("release").Select(value => value.Value.Trim()).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(release))
            return release;
        var versions = await MavenVersionsAsync(url, cancellationToken).ConfigureAwait(false);
        return versions.Count > 0
            ? versions[^1]
            : throw new InvalidDataException("Maven metadata did not contain a release version.");
    }

    private async Task<IReadOnlyList<string>> MavenVersionsAsync(
        string url,
        CancellationToken cancellationToken) =>
        (await MavenDocumentAsync(url, cancellationToken).ConfigureAwait(false))
        .Descendants("version").Select(value => value.Value.Trim()).Where(value => value.Length > 0).ToArray();

    private async Task<XDocument> MavenDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TryGetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim().Split(' ')[0]
            : "";
    }
}

public sealed class LoaderInstallationService
{
    private readonly LoaderMetadataService metadata;
    private readonly HttpClient http;

    public LoaderInstallationService(
        LoaderMetadataService metadata,
        HttpClient? httpClient = null)
    {
        this.metadata = metadata;
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    }

    public async Task<LoaderInstallResult> InstallAsync(
        InstallSourceType loader,
        string minecraftVersion,
        string loaderVersion,
        string javaPath,
        string stagingPath,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        var plan = await metadata.ResolveAsync(loader, minecraftVersion, loaderVersion, cancellationToken)
            .ConfigureAwait(false);
        return await InstallPlanAsync(plan, javaPath, stagingPath, logPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<LoaderInstallResult> InstallExactAsync(
        LoaderInstallPlan plan,
        string javaPath,
        string stagingPath,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateOfficialPlan(plan);
        return InstallPlanAsync(plan, javaPath, stagingPath, logPath, cancellationToken);
    }

    /// <summary>
    /// Materializes a provider-verified loader artifact already downloaded by the recovery-backed
    /// update transaction. This never accepts an arbitrary renderer path or skips official-plan and
    /// hash validation.
    /// </summary>
    public async Task<LoaderInstallResult> InstallVerifiedArtifactAsync(
        LoaderInstallPlan plan,
        string javaPath,
        string verifiedArtifactPath,
        string stagingPath,
        string logPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidateOfficialPlan(plan);
        if (!File.Exists(verifiedArtifactPath))
            throw new FileNotFoundException("The verified loader update artifact was not found.", verifiedArtifactPath);
        if (!File.Exists(javaPath))
            throw new FileNotFoundException("The selected absolute Java executable does not exist.", javaPath);
        Directory.CreateDirectory(stagingPath);
        var payload = Path.Combine(stagingPath, plan.RunsInstaller ? "loader-installer.jar" :
            plan.ExpectedLaunchFile);
        await using (var input = new FileStream(verifiedArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        return await CompleteInstallationAsync(plan, javaPath, stagingPath, logPath, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LoaderInstallResult> InstallPlanAsync(
        LoaderInstallPlan plan,
        string javaPath,
        string stagingPath,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(javaPath))
            throw new FileNotFoundException("The selected absolute Java executable does not exist.", javaPath);
        Directory.CreateDirectory(stagingPath);
        var payload = Path.Combine(stagingPath, plan.RunsInstaller ? "loader-installer.jar" :
            plan.ExpectedLaunchFile);
        using (var response = await http.GetAsync(plan.DownloadUrl,
                   HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        }
        return await CompleteInstallationAsync(plan, javaPath, stagingPath, logPath, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LoaderInstallResult> CompleteInstallationAsync(
        LoaderInstallPlan plan,
        string javaPath,
        string stagingPath,
        string logPath,
        string payload,
        CancellationToken cancellationToken)
    {
        var localSha256 = Sha256(payload);
        VerifyHashes(payload, plan.Sha1, plan.Sha256);
        if (!plan.RunsInstaller)
            return new LoaderInstallResult
            {
                LaunchFile = payload,
                DownloadSha256 = localSha256,
                InstallerVersion = plan.InstallerVersion,
                ArtifactUrl = plan.DownloadUrl
            };

        var start = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(javaPath),
            WorkingDirectory = stagingPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-jar");
        start.ArgumentList.Add(payload);
        foreach (var argument in SplitArguments(plan.InstallerArgument))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
                            throw new InvalidOperationException("Windows did not start the loader installer.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromMinutes(10), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(true);
            throw;
        }
        var outputText = await stdout.ConfigureAwait(false) + Environment.NewLine +
                         await stderr.ConfigureAwait(false);
        await File.WriteAllTextAsync(logPath, SecretRedactor.Redact(outputText),
            new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The {plan.Loader} installer exited with code {process.ExitCode}. See {logPath}.");
        var launch = DetectLaunch(stagingPath, plan.Loader, plan.ExpectedLaunchFile);
        File.Delete(payload);
        return new LoaderInstallResult
        {
            LaunchFile = launch.LaunchFile,
            ArgumentsFile = launch.ArgumentsFile,
            InstallerOutput = outputText,
            DownloadSha256 = localSha256,
            InstallerVersion = plan.InstallerVersion,
            ArtifactUrl = plan.DownloadUrl
        };
    }

    private static void ValidateOfficialPlan(LoaderInstallPlan plan)
    {
        if (!Uri.TryCreate(plan.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The loader artifact must use an official HTTPS source.");
        var expectedHost = plan.Loader switch
        {
            InstallSourceType.Fabric => "meta.fabricmc.net",
            InstallSourceType.NeoForge => "maven.neoforged.net",
            InstallSourceType.Forge => "maven.minecraftforge.net",
            InstallSourceType.Quilt => "maven.quiltmc.org",
            _ => ""
        };
        if (expectedHost.Length == 0 || !uri.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The {plan.Loader} artifact did not use its official provider host.");
        if (plan.Loader == InstallSourceType.NeoForge && string.IsNullOrWhiteSpace(plan.Sha256) &&
            string.IsNullOrWhiteSpace(plan.Sha1))
            throw new InvalidDataException("NeoForge installation requires an official Maven checksum.");
    }

    private static (string LaunchFile, string ArgumentsFile) DetectLaunch(
        string staging,
        InstallSourceType loader,
        string expected)
    {
        var expectedPath = Path.Combine(staging, expected);
        if (loader == InstallSourceType.Quilt && File.Exists(expectedPath))
            return (expectedPath, "");
        var arguments = Directory.EnumerateFiles(staging, "win_args.txt", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        if (arguments is not null)
            return (arguments, arguments);
        var jar = Directory.EnumerateFiles(staging, "*server*.jar", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (jar is not null)
            return (jar, "");
        throw new InvalidDataException($"{loader} installation completed but no non-detaching launch profile was found.");
    }

    private static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var character in arguments)
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }
            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(character);
        }
        if (current.Length > 0)
            result.Add(current.ToString());
        return result;
    }

    private static void VerifyHashes(string path, string sha1, string sha256)
    {
        using var stream = File.OpenRead(path);
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            var actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loader installer SHA-256 verification failed.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(sha1))
        {
#pragma warning disable CA5350 // Official Forge-family Maven repositories publish SHA-1 sidecars.
            var actual = Convert.ToHexString(SHA1.HashData(stream));
#pragma warning restore CA5350
            if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Loader installer SHA-1 verification failed.");
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
