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
                await WriteCacheAsync(adapter.Provider, query, items, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                var cached = await ReadCacheAsync(adapter.Provider, query, cancellationToken).ConfigureAwait(false);
                results.AddRange(cached);
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
            Stale = DateTimeOffset.UtcNow - cached.CreatedAt > cacheLifetime
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
            var items = CatalogPolicy.Filter(
                await adapter.BrowseAsync(query, cancellationToken).ConfigureAwait(false), query);
            await WriteCacheAsync(provider, query, items, cancellationToken).ConfigureAwait(false);
            return new CatalogBrowseResult
            {
                Provider = provider,
                State = items.Count > 0 ? CatalogLoadState.Ready : CatalogLoadState.Empty,
                Items = items,
                Detail = items.Count > 0
                    ? $"Loaded {items.Count} exact provider result{(items.Count == 1 ? "" : "s")}."
                    : "No server-capable pack matched the current filters.",
                RetrievedAt = DateTimeOffset.UtcNow
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
        var cached = await ReadVersionInventoryCacheAsync(provider, cancellationToken).ConfigureAwait(false);
        if (cacheOnly)
            return cached ?? VersionInventoryFailure(provider, CatalogLoadState.Empty,
                "No cached Minecraft version inventory is available.", "cache");
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

public sealed class ModrinthCatalogProvider : HttpCatalogProvider, IGuidedCatalogProvider
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
    {
        if (TryGetProjectReference(query.Search, out var projectReference))
        {
            using var projectDocument = await GetJsonAsync(
                $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(projectReference)}",
                cancellationToken).ConfigureAwait(false);
            var project = projectDocument.RootElement;
            var projectId = project.GetProperty("id").GetString() ?? projectReference;
            var versions = await GetVersionsAsync(projectId, query with { Search = "" }, cancellationToken)
                .ConfigureAwait(false);
            var serverSide = project.TryGetProperty("server_side", out var server)
                ? server.GetString() : "unknown";
            return [new CatalogItem
            {
                Provider = CatalogProvider.Modrinth,
                ContentType = CatalogContentType.Modpack,
                ProjectId = projectId,
                Slug = project.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                Name = project.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                Author = "Modrinth project",
                Summary = project.TryGetProperty("description", out var description)
                    ? description.GetString() ?? "" : "",
                IconUrl = project.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
                ProjectUrl = "https://modrinth.com/modpack/" +
                             (project.TryGetProperty("slug", out slug) ? slug.GetString() ?? projectId : projectId),
                DownloadCount = project.TryGetProperty("downloads", out var downloads)
                    ? downloads.GetInt64() : null,
                UpdatedAt = project.TryGetProperty("updated", out var updated) &&
                            updated.TryGetDateTimeOffset(out var updatedAt) ? updatedAt : null,
                ClientRequirement = serverSide == "required"
                    ? ClientRequirement.MatchingPackRequired : ClientRequirement.Unknown,
                InstallationSupport = versions.Any(version => version.HasServerPackage)
                    ? InstallationSupportState.AutomatedWithReview : InstallationSupportState.ClientOnly,
                Categories = project.TryGetProperty("categories", out var categories)
                    ? categories.EnumerateArray().Select(value => value.GetString() ?? "")
                        .Where(value => value.Length > 0).ToArray()
                    : [],
                Versions = versions
            }];
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
        var url = "https://api.modrinth.com/v2/search?limit=" +
                  Math.Clamp(query.Limit, 1, 20) +
                   "&index=" + providerIndex + "&query=" + Uri.EscapeDataString(query.Search) +
                  "&facets=" + Uri.EscapeDataString(facetJson);
        using var search = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        var hits = search.RootElement.GetProperty("hits").EnumerateArray().Select(hit => hit.Clone()).ToArray();
        using var hydrateGate = new SemaphoreSlim(6, 6);
        var itemTasks = hits.Select(async hit =>
        {
            await hydrateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CatalogVersion> versions;
            try
            {
                var projectId = hit.GetProperty("project_id").GetString() ?? "";
                versions = await GetVersionsAsync(projectId, query, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                hydrateGate.Release();
            }
            var resolvedProjectId = hit.GetProperty("project_id").GetString() ?? "";
            var serverSide = hit.TryGetProperty("server_side", out var server) ? server.GetString() : "unknown";
            return new CatalogItem
            {
                Provider = CatalogProvider.Modrinth,
                ContentType = CatalogContentType.Modpack,
                ProjectId = resolvedProjectId,
                Slug = hit.GetProperty("slug").GetString() ?? "",
                Name = hit.GetProperty("title").GetString() ?? "",
                Author = hit.GetProperty("author").GetString() ?? "",
                Summary = hit.GetProperty("description").GetString() ?? "",
                IconUrl = hit.TryGetProperty("icon_url", out var icon) ? icon.GetString() ?? "" : "",
                ProjectUrl = "https://modrinth.com/modpack/" + (hit.GetProperty("slug").GetString() ?? resolvedProjectId),
                DownloadCount = hit.TryGetProperty("downloads", out var downloads) ? downloads.GetInt64() : null,
                UpdatedAt = hit.TryGetProperty("date_modified", out var updated) &&
                            updated.TryGetDateTimeOffset(out var updatedAt) ? updatedAt : null,
                ClientRequirement = serverSide == "required"
                    ? ClientRequirement.MatchingPackRequired : ClientRequirement.Unknown,
                InstallationSupport = versions.Any(version => version.HasServerPackage)
                    ? InstallationSupportState.AutomatedWithReview : InstallationSupportState.ClientOnly,
                Categories = hit.TryGetProperty("categories", out var categories)
                    ? categories.EnumerateArray().Select(value => value.GetString() ?? "").Where(value => value.Length > 0).ToArray()
                    : [],
                Versions = versions
            };
        }).ToArray();
        return await Task.WhenAll(itemTasks).ConfigureAwait(false);
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

public sealed class CurseForgeCatalogProvider : HttpCatalogProvider, IGuidedCatalogProvider
{
    private readonly ISecretStore secrets;

    public CurseForgeCatalogProvider(ISecretStore secrets, HttpClient? httpClient = null) : base(httpClient)
    {
        this.secrets = secrets;
    }

    public CatalogProvider Provider => CatalogProvider.CurseForge;
    public bool IsAvailable => secrets.Contains(CurseForgeUpdateProvider.ApiKeyName);
    public string AvailabilityDetail => IsAvailable
        ? "User-provided API key is configured."
        : "Browsing is hidden until a user-provided CurseForge API key is encrypted with DPAPI.";

    public async Task<IReadOnlyList<CatalogGameVersion>> GetGameVersionsAsync(
        CancellationToken cancellationToken = default)
    {
        var key = secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName);
        if (string.IsNullOrWhiteSpace(key)) return [];
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "https://api.curseforge.com/v1/minecraft/version?sortDescending=true");
        request.Headers.Add("x-api-key", key);
        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("CurseForge's Minecraft-version response was not a version list.");
        return data.EnumerateArray().Select(version =>
        {
            var id = version.TryGetProperty("versionString", out var value) ? value.GetString() ?? "" : "";
            return new CatalogGameVersion
            {
                VersionId = id,
                Kind = ClassifyMinecraftVersion(id),
                PublishedAt = version.TryGetProperty("dateModified", out var date) &&
                              date.TryGetDateTimeOffset(out var published) ? published : null,
                IsMajor = ClassifyMinecraftVersion(id) == CatalogGameVersionKind.Release
            };
        }).ToArray();
    }

    public async Task<IReadOnlyList<CatalogItem>> BrowseAsync(
        CatalogQuery query,
        CancellationToken cancellationToken = default)
    {
        var key = secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName);
        if (string.IsNullOrWhiteSpace(key))
            return [];
        var loaderType = query.Loader.ToLowerInvariant() switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => 0
        };
        var sortField = query.Sort switch
        {
            CatalogSort.Downloads => 6,
            CatalogSort.Newest => 11,
            CatalogSort.Updated => 3,
            _ => 2
        };
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://api.curseforge.com/v1/mods/search?gameId=432&classId=4471&pageSize=" +
            Math.Clamp(query.Limit, 1, 50) +
            "&searchFilter=" + Uri.EscapeDataString(query.Search) +
            $"&sortField={sortField}&sortOrder=desc" +
            (string.IsNullOrWhiteSpace(query.MinecraftVersion)
                ? "" : "&gameVersion=" + Uri.EscapeDataString(query.MinecraftVersion)) +
            (loaderType == 0 || string.IsNullOrWhiteSpace(query.MinecraftVersion)
                ? "" : $"&modLoaderType={loaderType}"));
        request.Headers.Add("x-api-key", key);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var unresolved = document.RootElement.GetProperty("data").EnumerateArray().Select(mod =>
        {
            var latest = mod.TryGetProperty("latestFiles", out var latestFiles)
                ? latestFiles.EnumerateArray().ToArray() : [];
            var versions = latest.Select(file =>
            {
                var gameVersions = file.TryGetProperty("gameVersions", out var values)
                    ? values.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
                var serverFileId = file.TryGetProperty("serverPackFileId", out var serverPack) &&
                                   serverPack.ValueKind == JsonValueKind.Number
                    ? serverPack.GetInt64() : 0;
                var channel = file.TryGetProperty("releaseType", out var releaseType)
                    ? releaseType.GetInt32() switch
                    {
                        2 => ReleaseChannel.Beta,
                        3 => ReleaseChannel.Alpha,
                        _ => ReleaseChannel.Stable
                    }
                    : ReleaseChannel.Stable;
                return new CatalogVersion
                {
                    VersionId = file.GetProperty("id").ToString(),
                    VersionName = file.TryGetProperty("displayName", out var display)
                        ? display.GetString() ?? "" : "",
                    MinecraftVersion = !string.IsNullOrWhiteSpace(query.MinecraftVersion)
                        ? gameVersions.FirstOrDefault(value => value.Equals(query.MinecraftVersion, StringComparison.OrdinalIgnoreCase)) ?? ""
                        : gameVersions.FirstOrDefault(value =>
                            value.StartsWith("1.", StringComparison.Ordinal) ||
                            value.Length > 0 && char.ToLowerInvariant(value[0]) == 'b') ?? "",
                    Loader = !string.IsNullOrWhiteSpace(query.Loader)
                        ? gameVersions.FirstOrDefault(value => value.Equals(query.Loader, StringComparison.OrdinalIgnoreCase)) ?? ""
                        : gameVersions.FirstOrDefault(value =>
                            value is "Fabric" or "Forge" or "NeoForge" or "Quilt") ?? "",
                    ReleaseChannel = channel,
                    PublishedAt = file.TryGetProperty("fileDate", out var dateValue) &&
                                  dateValue.TryGetDateTimeOffset(out var date) ? date : null,
                    HasServerPackage = serverFileId > 0,
                    DownloadUrl = serverFileId > 0
                        ? $"curseforge-file:{serverFileId}" : "",
                    RequiredJavaMajor = JavaRuntimePolicy.TryRequiredMajorForMinecraft(
                        gameVersions.FirstOrDefault(value =>
                            value.StartsWith("1.", StringComparison.Ordinal) ||
                            value.Length > 0 && char.ToLowerInvariant(value[0]) == 'b') ?? "") ?? 0
                };
            }).ToArray();
            return new CatalogItem
            {
                Provider = CatalogProvider.CurseForge,
                ContentType = CatalogContentType.Modpack,
                ProjectId = mod.GetProperty("id").ToString(),
                Slug = mod.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                Name = mod.GetProperty("name").GetString() ?? "",
                Author = mod.TryGetProperty("authors", out var authors)
                    ? string.Join(", ", authors.EnumerateArray().Select(author =>
                        author.TryGetProperty("name", out var name) ? name.GetString() : null).OfType<string>())
                    : "",
                Summary = mod.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "",
                IconUrl = mod.TryGetProperty("logo", out var logo) && logo.TryGetProperty("thumbnailUrl", out var thumbnail)
                    ? thumbnail.GetString() ?? "" : "",
                ProjectUrl = mod.TryGetProperty("links", out var links) && links.TryGetProperty("websiteUrl", out var website)
                    ? website.GetString() ?? "" : "",
                DownloadCount = mod.TryGetProperty("downloadCount", out var downloads)
                    ? downloads.GetInt64() : null,
                UpdatedAt = mod.TryGetProperty("dateModified", out var updated) &&
                            updated.TryGetDateTimeOffset(out var date) ? date : null,
                Categories = mod.TryGetProperty("categories", out var categories)
                    ? categories.EnumerateArray().Select(category =>
                        category.TryGetProperty("slug", out var slugValue) ? slugValue.GetString() ?? "" : "")
                        .Where(value => value.Length > 0).ToArray()
                    : [],
                ClientRequirement = ClientRequirement.MatchingPackRequired,
                InstallationSupport = versions.Any(version => version.HasServerPackage)
                    ? InstallationSupportState.FullyAutomated
                    : InstallationSupportState.ClientOnly,
                Versions = versions
            };
        }).ToArray();

        var resolved = new List<CatalogItem>(unresolved.Length);
        foreach (var item in unresolved)
        {
            var versions = new List<CatalogVersion>(item.Versions.Count);
            foreach (var version in item.Versions)
            {
                if (!version.DownloadUrl.StartsWith(
                        "curseforge-file:", StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse(version.DownloadUrl["curseforge-file:".Length..], out var fileId))
                {
                    versions.Add(version);
                    continue;
                }
                versions.Add(await ResolveServerPackFileAsync(
                    key, item.ProjectId, fileId, version, cancellationToken).ConfigureAwait(false));
            }
            resolved.Add(item with
            {
                Versions = versions,
                InstallationSupport = versions.Any(version =>
                    version.HasServerPackage &&
                    version.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    ? InstallationSupportState.FullyAutomated
                    : InstallationSupportState.ManualPackageRequired
            });
        }
        return resolved;
    }

    private async Task<CatalogVersion> ResolveServerPackFileAsync(
        string apiKey,
        string projectId,
        long fileId,
        CatalogVersion version,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.curseforge.com/v1/mods/{Uri.EscapeDataString(projectId)}/files/{fileId}");
        request.Headers.Add("x-api-key", apiKey);
        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var data = document.RootElement.GetProperty("data");
        var url = data.TryGetProperty("downloadUrl", out var download) &&
                  download.ValueKind == JsonValueKind.String
            ? download.GetString() ?? "" : "";
        var sha1 = "";
        if (data.TryGetProperty("hashes", out var hashes))
        {
            foreach (var hash in hashes.EnumerateArray())
            {
                if (hash.TryGetProperty("algo", out var algorithm) &&
                    algorithm.GetInt32() == 1)
                {
                    sha1 = hash.GetProperty("value").GetString() ?? "";
                    break;
                }
            }
        }
        return version with
        {
            VersionId = fileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            VersionName = data.TryGetProperty("displayName", out var display)
                ? display.GetString() ?? version.VersionName : version.VersionName,
            DownloadUrl = url,
            Sha1 = sha1,
            SizeBytes = data.TryGetProperty("fileLength", out var size)
                ? size.GetInt64() : version.SizeBytes,
            HasServerPackage = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                               !string.IsNullOrWhiteSpace(sha1)
        };
    }

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
