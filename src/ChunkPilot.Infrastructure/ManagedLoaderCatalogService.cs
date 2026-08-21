using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Cache-backed official metadata for managed loader catalogs.</summary>
public sealed partial class ManagedLoaderCatalogService : IManagedLoaderCertificationCatalog,
    IOrnitheHeadlessProfileProvider
{
    public const string FabricMetaRoot = "https://meta.fabricmc.net/v2/versions";
    public const string NeoForgeVersionsApiUrl =
        "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
    private const string NeoForgeMavenRoot =
        "https://maven.neoforged.net/releases/net/neoforged/neoforge";
    // Schema 2 adds typed Ornithe headless-profile and base-server-artifact evidence.
    private const int CacheSchemaVersion = 2;
    private readonly AppDataPaths paths;
    private readonly HttpClient http;
    private readonly TimeSpan cacheLifetime;

    public ManagedLoaderCatalogService(
        AppDataPaths paths,
        HttpClient? httpClient = null,
        TimeSpan? cacheLifetime = null)
    {
        this.paths = paths;
        this.cacheLifetime = cacheLifetime ?? TimeSpan.FromHours(4);
        http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ChunkPilot/1.3.0 (local Windows Minecraft server manager)");
    }

    public Task<ManagedLoaderVersionCatalog> GetVersionsAsync(
        ManagedLoaderPlatform platform,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        CachedAsync(
            VersionCachePath(platform),
            forceRefresh,
            () => platform switch
            {
                ManagedLoaderPlatform.Fabric => RefreshFabricVersionsAsync(cancellationToken),
                ManagedLoaderPlatform.Quilt => RefreshQuiltVersionsAsync(cancellationToken),
                ManagedLoaderPlatform.Forge => RefreshForgeVersionsAsync(cancellationToken),
                ManagedLoaderPlatform.NeoForge => RefreshNeoForgeVersionsAsync(cancellationToken),
                ManagedLoaderPlatform.LegacyFabric => RefreshLegacyFabricVersionsAsync(cancellationToken),
                ManagedLoaderPlatform.Ornithe => RefreshOrnitheVersionsAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform,
                    "Unknown managed-loader catalog platform.")
            },
            cached => cached.Platform == platform,
            detail => ManagedLoaderVersionCatalog.Unavailable(platform, detail),
            cancellationToken);

    public Task<ManagedLoaderBuildCatalog> GetBuildsAsync(
        ManagedLoaderPlatform platform,
        string minecraftVersion,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidateVersion(minecraftVersion);
        return CachedAsync(
            BuildCachePath(platform, minecraftVersion),
            forceRefresh,
            () => platform switch
            {
                ManagedLoaderPlatform.Fabric => RefreshFabricBuildsAsync(minecraftVersion, cancellationToken),
                ManagedLoaderPlatform.Quilt => RefreshQuiltBuildsAsync(minecraftVersion, cancellationToken),
                ManagedLoaderPlatform.Forge => RefreshForgeBuildsAsync(minecraftVersion, cancellationToken),
                ManagedLoaderPlatform.NeoForge => RefreshNeoForgeBuildsAsync(minecraftVersion, cancellationToken),
                ManagedLoaderPlatform.LegacyFabric => RefreshCatalogOnlyBuildsAsync(platform, minecraftVersion),
                ManagedLoaderPlatform.Ornithe => RefreshOrnitheBuildsAsync(minecraftVersion, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(platform), platform,
                    "Unknown managed-loader catalog platform.")
            },
            cached => cached.Platform == platform &&
                      cached.MinecraftVersion.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase),
            detail => ManagedLoaderBuildCatalog.Unavailable(platform, minecraftVersion, detail),
            cancellationToken);
    }

    private async Task<ManagedLoaderVersionCatalog> RefreshFabricVersionsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{FabricMetaRoot}/game", cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Fabric's game-version response was not a list.");
        var versions = document.RootElement.EnumerateArray()
            .Select(item => new
            {
                Id = ReadString(item, "version"),
                Stable = item.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True
            })
            .Where(item => SafeVersion().IsMatch(item.Id))
            .Select(item => ManagedLoaderRuntimeCertificationEvidence.Apply(
                Version(ManagedLoaderPlatform.Fabric, item.Id, item.Stable,
                    "Official Fabric Meta v2 game inventory")))
            .OrderByDescending(item => MinecraftVersionClassification.NumericVersion(item.MinecraftVersion))
            .ThenByDescending(item => item.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (versions.Length == 0)
            throw new InvalidDataException("Fabric returned no readable game versions.");
        return new ManagedLoaderVersionCatalog
        {
            Platform = ManagedLoaderPlatform.Fabric,
            Versions = versions,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true
        };
    }

    private async Task<ManagedLoaderBuildCatalog> RefreshFabricBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        using var loaders = await GetJsonAsync(
            $"{FabricMetaRoot}/loader/{Uri.EscapeDataString(minecraftVersion)}", cancellationToken)
            .ConfigureAwait(false);
        using var installers = await GetJsonAsync($"{FabricMetaRoot}/installer", cancellationToken)
            .ConfigureAwait(false);
        if (loaders.RootElement.ValueKind != JsonValueKind.Array ||
            installers.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Fabric's loader metadata was not a list.");
        var installer = installers.RootElement.EnumerateArray()
            .Select(item => new
            {
                Version = ReadString(item, "version"),
                Stable = !item.TryGetProperty("stable", out var stable) || stable.ValueKind == JsonValueKind.True
            })
            .FirstOrDefault(item => item.Stable && SafeVersion().IsMatch(item.Version))
            ?? throw new InvalidDataException("Fabric returned no stable server-launcher installer version.");
        var java = LoaderJavaMajor(minecraftVersion);
        var builds = loaders.RootElement.EnumerateArray().Select(item =>
            {
                if (!item.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
                    return null;
                var version = ReadString(loader, "version");
                if (!SafeVersion().IsMatch(version)) return null;
                var stable = !loader.TryGetProperty("stable", out var stableValue) ||
                             stableValue.ValueKind == JsonValueKind.True;
                var url = $"{FabricMetaRoot}/loader/{Uri.EscapeDataString(minecraftVersion)}/" +
                          $"{Uri.EscapeDataString(version)}/{Uri.EscapeDataString(installer.Version)}/server/jar";
                return new ManagedLoaderBuild
                {
                    Platform = ManagedLoaderPlatform.Fabric,
                    MinecraftVersion = minecraftVersion,
                    LoaderVersion = version,
                    InstallerVersion = installer.Version,
                    Channel = stable ? ManagedLoaderChannel.Stable : ManagedLoaderChannel.Experimental,
                    ArtifactUrl = url,
                    RequiredJavaMajor = java,
                    Provenance = "Official Fabric Meta v2 loader and server-launcher endpoints",
                    SupportReason = stable
                        ? "Official stable Fabric Loader. The server-launcher endpoint is official but does not publish an independent checksum."
                        : "Fabric marks this Loader build unstable; it requires explicit Experimental acknowledgement."
                };
            })
            .Where(item => item is not null)
            .Select(item => ManagedLoaderRuntimeCertificationEvidence.Apply(item!))
            .ToArray();
        return new ManagedLoaderBuildCatalog
        {
            Platform = ManagedLoaderPlatform.Fabric,
            MinecraftVersion = minecraftVersion,
            Builds = builds,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            UnavailableDetail = builds.Length == 0 ? "Fabric publishes no Loader build for this Minecraft version." : ""
        };
    }

    private async Task<ManagedLoaderVersionCatalog> RefreshNeoForgeVersionsAsync(CancellationToken cancellationToken)
    {
        var versions = await NeoForgeVersionsAsync(cancellationToken).ConfigureAwait(false);
        var mapped = versions.Select(MapNeoForgeMinecraftVersion)
            .Where(item => item is not null)
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(item => ManagedLoaderRuntimeCertificationEvidence.Apply(
                Version(ManagedLoaderPlatform.NeoForge, item, true,
                    "Official NeoForged Maven inventory and documented NeoForge versioning scheme")))
            .OrderByDescending(item => MinecraftVersionClassification.NumericVersion(item.MinecraftVersion))
            .ThenByDescending(item => item.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mapped.Length == 0)
            throw new InvalidDataException("NeoForged Maven metadata contained no supported version mapping.");
        return new ManagedLoaderVersionCatalog
        {
            Platform = ManagedLoaderPlatform.NeoForge,
            Versions = mapped,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true
        };
    }

    private async Task<ManagedLoaderBuildCatalog> RefreshNeoForgeBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var versions = (await NeoForgeVersionsAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => string.Equals(MapNeoForgeMinecraftVersion(item), minecraftVersion,
                StringComparison.OrdinalIgnoreCase))
            .Reverse()
            .ToArray();
        var java = LoaderJavaMajor(minecraftVersion);
        var builds = new List<ManagedLoaderBuild>(versions.Length);
        // Exact checksums are fetched only for the newest bounded window. Older official metadata remains
        // visible but unavailable rather than generating hundreds of provider requests on one page open.
        for (var index = 0; index < versions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loader = versions[index];
            var url = $"{NeoForgeMavenRoot}/{Uri.EscapeDataString(loader)}/" +
                      $"neoforge-{Uri.EscapeDataString(loader)}-installer.jar";
            var sha256 = index < 48
                ? await TryGetTextAsync(url + ".sha256", cancellationToken).ConfigureAwait(false)
                : "";
            var channel = loader.Contains("beta", StringComparison.OrdinalIgnoreCase)
                ? ManagedLoaderChannel.Beta
                : loader.Contains("alpha", StringComparison.OrdinalIgnoreCase)
                    ? ManagedLoaderChannel.Experimental
                    : ManagedLoaderChannel.Stable;
            builds.Add(ManagedLoaderRuntimeCertificationEvidence.Apply(new ManagedLoaderBuild
            {
                Platform = ManagedLoaderPlatform.NeoForge,
                MinecraftVersion = minecraftVersion,
                LoaderVersion = loader,
                InstallerVersion = loader,
                Channel = channel,
                ArtifactUrl = url,
                ArtifactSha256 = NormalizeHash(sha256, 64),
                RequiredJavaMajor = java,
                Provenance = "Official NeoForged Maven metadata and SHA-256 sidecar",
                SupportReason = index >= 48
                    ? "This older official build is inventoried, but its checksum is loaded only on a focused refresh."
                    : channel == ManagedLoaderChannel.Stable
                        ? "Official NeoForge installer with provider SHA-256."
                        : "NeoForge marks this build pre-stable; it requires explicit Experimental acknowledgement."
            }));
        }
        return new ManagedLoaderBuildCatalog
        {
            Platform = ManagedLoaderPlatform.NeoForge,
            MinecraftVersion = minecraftVersion,
            Builds = builds,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            UnavailableDetail = builds.Count == 0 ? "NeoForge publishes no installer for this Minecraft version." : ""
        };
    }

    /// <summary>Maps versions using NeoForged's documented pre-26.1 and current schemes.</summary>
    internal static string? MapNeoForgeMinecraftVersion(string neoForgeVersion)
    {
        var numeric = neoForgeVersion.Split('-', 2)[0].Split('.');
        if (numeric.Length < 3 || !numeric.All(part => int.TryParse(part, out _))) return null;
        var values = numeric.Select(int.Parse).ToArray();
        if (values[0] >= 26)
        {
            if (values.Length < 4) return null;
            return values[2] == 0 ? $"{values[0]}.{values[1]}" : $"{values[0]}.{values[1]}.{values[2]}";
        }
        return values[0] is >= 20 and <= 25 ? $"1.{values[0]}.{values[1]}" : null;
    }

    private static ManagedLoaderMinecraftVersion Version(
        ManagedLoaderPlatform platform,
        string id,
        bool stable,
        string provenance,
        string? providerId = null,
        int? javaOverride = null,
        bool requiresUserSuppliedMinecraftServerJar = false,
        string unavailableReason = "")
    {
        var java = javaOverride ?? LoaderJavaMajor(id);
        return new ManagedLoaderMinecraftVersion
        {
            Platform = platform,
            MinecraftVersion = id,
            ProviderMinecraftVersion = providerId ?? id,
            StableMinecraft = stable,
            RequiredJavaMajor = java,
            RequiresUserSuppliedMinecraftServerJar = requiresUserSuppliedMinecraftServerJar,
            UnavailableReason = unavailableReason,
            Provenance = provenance,
            SupportReason = !string.IsNullOrWhiteSpace(unavailableReason)
                ? unavailableReason
                : !stable
                ? "The loader source marks this Minecraft version unstable."
                : java is null
                    ? "ChunkPilot has not established a safe Java requirement for this Minecraft version."
                    : "Choose an exact loader version next."
        };
    }

    private async Task<IReadOnlyList<string>> NeoForgeVersionsAsync(CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(NeoForgeVersionsApiUrl, cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("NeoForge's official repository API returned no version list.");
        return versions.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim() ?? "")
            .Where(item => SafeVersion().IsMatch(item)).ToArray();
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> TryGetTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim()
            : "";
    }

    private async Task<T> CachedAsync<T>(
        string path,
        bool forceRefresh,
        Func<Task<T>> refresh,
        Func<T, bool> identity,
        Func<string, T> unavailable,
        CancellationToken cancellationToken) where T : class
    {
        var cached = ReadCache<T>(path);
        if (cached is not null && !identity(cached)) cached = null;
        var retrieved = cached switch
        {
            ManagedLoaderVersionCatalog versions => versions.RetrievedUtc,
            ManagedLoaderBuildCatalog builds => builds.RetrievedUtc,
            _ => null
        };
        if (cached is not null && !forceRefresh && retrieved is { } timestamp &&
            DateTimeOffset.UtcNow - timestamp < cacheLifetime)
            return MarkCache(cached, false);
        if (cached is not null && !forceRefresh) return MarkCache(cached, true);
        try
        {
            var current = await refresh().ConfigureAwait(false);
            WriteCache(path, current);
            return current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                             InvalidDataException or JsonException or TaskCanceledException)
        {
            return cached is not null
                ? MarkCache(cached, true)
                : unavailable("The official loader source is unavailable and no saved inventory exists. " +
                              SecretRedactor.Redact(exception.Message));
        }
    }

    private static T MarkCache<T>(T catalog, bool stale) where T : class => catalog switch
    {
        ManagedLoaderVersionCatalog versions => (T)(object)(versions with { IsFromCache = true, IsStale = stale }),
        ManagedLoaderBuildCatalog builds => (T)(object)(builds with { IsFromCache = true, IsStale = stale }),
        _ => catalog
    };

    private string VersionCachePath(ManagedLoaderPlatform platform) =>
        Path.Combine(paths.CatalogCache, $"{platform.ToString().ToLowerInvariant()}-versions.json");

    private string BuildCachePath(ManagedLoaderPlatform platform, string minecraftVersion)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(minecraftVersion)))[..20]
            .ToLowerInvariant();
        return Path.Combine(paths.CatalogCache, $"{platform.ToString().ToLowerInvariant()}-builds-{id}.json");
    }

    private static T? ReadCache<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schema) ||
                schema.GetInt32() != CacheSchemaVersion) return null;
            return document.RootElement.GetProperty("catalog").Deserialize<T>(ProtocolJson.Options);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WriteCache<T>(string path, T catalog)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".partial";
            File.WriteAllText(temporary,
                JsonSerializer.Serialize(new { schemaVersion = CacheSchemaVersion, catalog }, ProtocolJson.Options),
                new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Provider metadata remains usable if optional offline persistence fails.
        }
    }

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string NormalizeHash(string value, int length)
    {
        var token = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return token.Length == length && token.All(Uri.IsHexDigit) ? token.ToLowerInvariant() : "";
    }

    private static void ValidateVersion(string value)
    {
        if (!SafeVersion().IsMatch(value))
            throw new ArgumentException("Select a Minecraft version from the official loader catalog.", nameof(value));
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeVersion();
}
