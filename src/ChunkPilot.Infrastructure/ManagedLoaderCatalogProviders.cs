using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class ManagedLoaderCatalogService
{
    public const string QuiltMetaRoot = "https://meta.quiltmc.org/v3/versions";
    public const string QuiltRecommendationsUrl = "https://quiltmc.org/api/v1/latest-version-components";
    public const string ForgeMetadataUrl =
        "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    public const string ForgePromotionsUrl =
        "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string LegacyFabricMetaRoot = "https://meta.legacyfabric.net/v2/versions";
    public const string OrnitheGenerationUrl =
        "https://meta.ornithemc.net/v3/versions/intermediary_generations";

    private const string QuiltInstallerMetadataUrl =
        "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/maven-metadata.xml";
    private const string QuiltInstallerRoot =
        "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer";
    private const string ForgeMavenRoot =
        "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string LegacyFabricHistoricUnavailable =
        "Legacy Fabric's official catalog does not support Minecraft 1.0, b1.8, or b1.8.1. " +
        "Ornithe inventories those versions, but ChunkPilot requires a user-supplied original server JAR because Mojang's official version metadata publishes no server download for them.";
    private const string OrnitheHistoricUnavailable =
        "Ornithe inventories this historical Minecraft version, but Mojang's official version metadata publishes no server download for it. " +
        "ChunkPilot requires a user-supplied original server JAR with exact hash verification; automatic archival download is unavailable.";

    private async Task<ManagedLoaderVersionCatalog> RefreshQuiltVersionsAsync(
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{QuiltMetaRoot}/game", cancellationToken)
            .ConfigureAwait(false);
        var versions = ParseGameVersions(document, ManagedLoaderPlatform.Quilt,
            "Official Quilt Meta v3 game inventory");
        return VersionCatalog(ManagedLoaderPlatform.Quilt, versions);
    }

    private async Task<ManagedLoaderBuildCatalog> RefreshQuiltBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        using var loaders = await GetJsonAsync(
            $"{QuiltMetaRoot}/loader/{Uri.EscapeDataString(minecraftVersion)}", cancellationToken)
            .ConfigureAwait(false);
        using var recommendations = await GetJsonAsync(QuiltRecommendationsUrl, cancellationToken)
            .ConfigureAwait(false);
        var installerMetadata = await GetRequiredTextAsync(QuiltInstallerMetadataUrl, cancellationToken)
            .ConfigureAwait(false);
        var installerVersion = ParseMavenRelease(installerMetadata) ??
            throw new InvalidDataException("Quilt's official installer metadata had no release version.");
        var artifactUrl = $"{QuiltInstallerRoot}/{Uri.EscapeDataString(installerVersion)}/" +
                          $"quilt-installer-{Uri.EscapeDataString(installerVersion)}.jar";
        var artifactSha256 = NormalizeHash(
            await TryGetTextAsync(artifactUrl + ".sha256", cancellationToken).ConfigureAwait(false), 64);
        var recommended = ReadRecommendedQuiltLoader(recommendations.RootElement, minecraftVersion);
        var strategy = ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Quilt);
        if (loaders.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Quilt's compatible-loader response was not a list.");

        var builds = loaders.RootElement.EnumerateArray()
            .Select(item =>
            {
                if (!item.TryGetProperty("loader", out var loader) || loader.ValueKind != JsonValueKind.Object)
                    return null;
                var loaderVersion = ReadString(loader, "version");
                if (!SafeVersion().IsMatch(loaderVersion)) return null;
                var java = LoaderJavaMajor(minecraftVersion);
                if (item.TryGetProperty("launcherMeta", out var launcher) &&
                    launcher.TryGetProperty("min_java_version", out var minimumJava) &&
                    minimumJava.TryGetInt32(out var loaderJava) && loaderJava >= 8)
                    java = Math.Max(java ?? 0, loaderJava);
                var providerRecommended = loaderVersion.Equals(recommended, StringComparison.OrdinalIgnoreCase);
                return new ManagedLoaderBuild
                {
                    Platform = ManagedLoaderPlatform.Quilt,
                    MinecraftVersion = minecraftVersion,
                    LoaderVersion = loaderVersion,
                    InstallerVersion = installerVersion,
                    Channel = LoaderChannel(loaderVersion),
                    ProviderRecommended = providerRecommended,
                    ProviderLatest = false,
                    ArtifactUrl = artifactUrl,
                    ArtifactSha256 = artifactSha256,
                    RequiredJavaMajor = java,
                    // Quilt installer 0.15.1 itself requires Java 17. The resulting server must
                    // still run on the exact Java required by its Minecraft version.
                    InstallerJavaMajor = 17,
                    Provenance = "Official Quilt Meta v3 compatibility, Quilt component recommendation, and installer SHA-256",
                    SupportReason = providerRecommended
                        ? "Quilt recommends this compatible loader. Recommended and stable remain separate provider states."
                        : "Official compatible Quilt loader identity.",
                    UnavailableReason = strategy.CreationUnavailableReason
                };
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.ProviderRecommended)
            .ThenByDescending(item => ComparableVersion(item.LoaderVersion))
            .ThenByDescending(item => item.LoaderVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ManagedLoaderBuildCatalog
        {
            Platform = ManagedLoaderPlatform.Quilt,
            MinecraftVersion = minecraftVersion,
            Builds = builds,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            UnavailableDetail = builds.Length == 0
                ? "Quilt publishes no compatible loader for this exact Minecraft version."
                : "",
            CreationUnavailableDetail = strategy.CreationUnavailableReason
        };
    }

    private async Task<ManagedLoaderVersionCatalog> RefreshForgeVersionsAsync(
        CancellationToken cancellationToken)
    {
        var coordinates = await ForgeCoordinatesAsync(cancellationToken).ConfigureAwait(false);
        var versions = coordinates.Select(MapForgeMinecraftVersion)
            .Where(item => item is not null)
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(item => Version(ManagedLoaderPlatform.Forge, item, true,
                "Official Forge Maven coordinate inventory",
                unavailableReason: ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Forge)
                    .CreationUnavailableReason))
            .OrderByDescending(item => MinecraftVersionClassification.NumericVersion(item.MinecraftVersion))
            .ThenByDescending(item => item.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return VersionCatalog(ManagedLoaderPlatform.Forge, versions);
    }

    private async Task<ManagedLoaderBuildCatalog> RefreshForgeBuildsAsync(
        string minecraftVersion,
        CancellationToken cancellationToken)
    {
        var coordinates = (await ForgeCoordinatesAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => item.StartsWith(minecraftVersion + "-", StringComparison.OrdinalIgnoreCase))
            .Select(item => new { Coordinate = item, Loader = ForgeLoaderVersion(item, minecraftVersion) })
            .Where(item => SafeVersion().IsMatch(item.Loader))
            .ToArray();
        using var promotions = await GetJsonAsync(ForgePromotionsUrl, cancellationToken).ConfigureAwait(false);
        var (recommended, latest) = ParseForgePromotions(promotions.RootElement, minecraftVersion);
        var ordered = coordinates
            .OrderByDescending(item => item.Loader.Equals(recommended, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Loader.Equals(latest, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => ComparableVersion(item.Loader))
            .ThenByDescending(item => item.Loader, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var strategy = ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Forge);
        var java = LoaderJavaMajor(minecraftVersion);
        var builds = new List<ManagedLoaderBuild>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = ordered[index];
            var artifactUrl = $"{ForgeMavenRoot}/{Uri.EscapeDataString(entry.Coordinate)}/" +
                              $"forge-{Uri.EscapeDataString(entry.Coordinate)}-installer.jar";
            var preferred = entry.Loader.Equals(recommended, StringComparison.OrdinalIgnoreCase) ||
                            entry.Loader.Equals(latest, StringComparison.OrdinalIgnoreCase);
            var sha256 = preferred || index < 48
                ? NormalizeHash(await TryGetTextAsync(artifactUrl + ".sha256", cancellationToken)
                    .ConfigureAwait(false), 64)
                : "";
            builds.Add(new ManagedLoaderBuild
            {
                Platform = ManagedLoaderPlatform.Forge,
                MinecraftVersion = minecraftVersion,
                LoaderVersion = entry.Loader,
                InstallerVersion = entry.Coordinate,
                Channel = LoaderChannel(entry.Loader),
                ProviderRecommended = entry.Loader.Equals(recommended, StringComparison.OrdinalIgnoreCase),
                ProviderLatest = entry.Loader.Equals(latest, StringComparison.OrdinalIgnoreCase),
                ArtifactUrl = artifactUrl,
                ArtifactSha256 = sha256,
                RequiredJavaMajor = java,
                Provenance = "Official Forge Maven inventory, promotions feed, and SHA-256 sidecar",
                SupportReason = entry.Loader.Equals(recommended, StringComparison.OrdinalIgnoreCase)
                    ? "Forge marks this exact version Recommended."
                    : entry.Loader.Equals(latest, StringComparison.OrdinalIgnoreCase)
                        ? "Forge marks this exact version Latest."
                        : "Official Forge installer identity; no provider recommendation is claimed.",
                UnavailableReason = strategy.CreationUnavailableReason
            });
        }
        return new ManagedLoaderBuildCatalog
        {
            Platform = ManagedLoaderPlatform.Forge,
            MinecraftVersion = minecraftVersion,
            Builds = builds,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            UnavailableDetail = builds.Count == 0
                ? "Forge publishes no installer coordinate for this exact Minecraft version."
                : "",
            CreationUnavailableDetail = strategy.CreationUnavailableReason
        };
    }

    private async Task<ManagedLoaderVersionCatalog> RefreshLegacyFabricVersionsAsync(
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync($"{LegacyFabricMetaRoot}/game", cancellationToken)
            .ConfigureAwait(false);
        var versions = ParseGameVersions(document, ManagedLoaderPlatform.LegacyFabric,
            "Official Legacy Fabric Meta v2 game inventory");
        return VersionCatalog(ManagedLoaderPlatform.LegacyFabric, versions);
    }

    private async Task<ManagedLoaderVersionCatalog> RefreshOrnitheVersionsAsync(
        CancellationToken cancellationToken)
    {
        var generation = await StableOrnitheGenerationAsync(cancellationToken).ConfigureAwait(false);
        using var document = await GetJsonAsync(
            $"https://meta.ornithemc.net/v3/versions/gen{generation}/intermediary", cancellationToken)
            .ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Ornithe's intermediary-version response was not a list.");
        var strategy = ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Ornithe);
        var versions = document.RootElement.EnumerateArray()
            .Select(item => new
            {
                ProviderId = ReadString(item, "version"),
                Stable = item.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True
            })
            .Where(item => SafeVersion().IsMatch(item.ProviderId))
            .Select(item =>
            {
                var canonical = CanonicalOrnitheMinecraftVersion(item.ProviderId);
                var userSupplied = RequiresUserSuppliedHistoricJar(canonical);
                var exactTarget = OrnitheHistoricalVersionPolicy.IsExactTarget(canonical);
                return Version(ManagedLoaderPlatform.Ornithe, canonical, item.Stable,
                    $"Official Ornithe Meta v3 stable intermediary generation {generation}",
                    providerId: item.ProviderId,
                    javaOverride: exactTarget ? 8 : null,
                    requiresUserSuppliedMinecraftServerJar: userSupplied,
                    unavailableReason: exactTarget
                        ? userSupplied ? OrnitheHistoricUnavailable : strategy.CreationUnavailableReason
                        : strategy.CreationUnavailableReason);
            })
            .OrderByDescending(item => MinecraftVersionClassification.NumericVersion(item.MinecraftVersion))
            .ThenByDescending(item => item.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return VersionCatalog(ManagedLoaderPlatform.Ornithe, versions);
    }

    private Task<ManagedLoaderBuildCatalog> RefreshCatalogOnlyBuildsAsync(
        ManagedLoaderPlatform platform,
        string minecraftVersion)
    {
        var strategy = ManagedLoaderPlatformStrategies.For(platform);
        var detail = platform switch
        {
            ManagedLoaderPlatform.LegacyFabric when RequiresUserSuppliedHistoricJar(minecraftVersion) =>
                LegacyFabricHistoricUnavailable,
            ManagedLoaderPlatform.Ornithe when RequiresUserSuppliedHistoricJar(minecraftVersion) =>
                OrnitheHistoricUnavailable,
            ManagedLoaderPlatform.LegacyFabric =>
                "Legacy Fabric loader identities are inventoried, but exact typed installer materialization is not enabled yet.",
            ManagedLoaderPlatform.Ornithe =>
                "Ornithe loader identities are inventoried, but exact typed installer materialization is not enabled yet.",
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform,
                "This platform is not catalog-only.")
        };
        return Task.FromResult(new ManagedLoaderBuildCatalog
        {
            Platform = platform,
            MinecraftVersion = minecraftVersion,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            UnavailableDetail = detail,
            CreationUnavailableDetail = strategy.CreationUnavailableReason
        });
    }

    internal static string? MapForgeMinecraftVersion(string coordinate)
    {
        var match = Regex.Match(coordinate, @"^(?<minecraft>\d+(?:\.\d+){1,2})-",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["minecraft"].Value : null;
    }

    internal static (string Recommended, string Latest) ParseForgePromotions(
        JsonElement root,
        string minecraftVersion)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("promos", out var promos) || promos.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Forge's promotions response did not contain a promos object.");
        return (ReadString(promos, minecraftVersion + "-recommended"),
            ReadString(promos, minecraftVersion + "-latest"));
    }

    internal static IReadOnlyList<string> ParseMavenVersions(string xml)
    {
        var document = ParseXml(xml);
        return document.Root?.Element("versioning")?.Element("versions")?.Elements("version")
            .Select(item => item.Value.Trim())
            .Where(item => SafeVersion().IsMatch(item))
            .ToArray() ?? [];
    }

    internal static string? ParseMavenRelease(string xml)
    {
        var document = ParseXml(xml);
        var versioning = document.Root?.Element("versioning");
        var release = versioning?.Element("release")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(release) && SafeVersion().IsMatch(release)) return release;
        var latest = versioning?.Element("latest")?.Value.Trim();
        if (!string.IsNullOrWhiteSpace(latest) && SafeVersion().IsMatch(latest)) return latest;
        return versioning?.Element("versions")?.Elements("version")
            .Select(item => item.Value.Trim()).LastOrDefault(item => SafeVersion().IsMatch(item));
    }

    private static XDocument ParseXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.None);
    }

    private async Task<IReadOnlyList<string>> ForgeCoordinatesAsync(CancellationToken cancellationToken) =>
        ParseMavenVersions(await GetRequiredTextAsync(ForgeMetadataUrl, cancellationToken).ConfigureAwait(false));

    private async Task<string> GetRequiredTextAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ManagedLoaderMinecraftVersion[] ParseGameVersions(
        JsonDocument document,
        ManagedLoaderPlatform platform,
        string provenance)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{platform}'s game-version response was not a list.");
        var unavailable = ManagedLoaderPlatformStrategies.For(platform).CreationUnavailableReason;
        return document.RootElement.EnumerateArray()
            .Select(item => new
            {
                Id = ReadString(item, "version"),
                Stable = item.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True
            })
            .Where(item => SafeVersion().IsMatch(item.Id))
            .Select(item => Version(platform, item.Id, item.Stable, provenance,
                unavailableReason: unavailable))
            .OrderByDescending(item => MinecraftVersionClassification.NumericVersion(item.MinecraftVersion))
            .ThenByDescending(item => item.MinecraftVersion, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ManagedLoaderVersionCatalog VersionCatalog(
        ManagedLoaderPlatform platform,
        IReadOnlyList<ManagedLoaderMinecraftVersion> versions)
    {
        if (versions.Count == 0)
            throw new InvalidDataException($"{platform}'s official source returned no readable game versions.");
        return new ManagedLoaderVersionCatalog
        {
            Platform = platform,
            Versions = versions,
            RetrievedUtc = DateTimeOffset.UtcNow,
            ProviderAvailable = true,
            CreationUnavailableDetail = ManagedLoaderPlatformStrategies.For(platform).CreationUnavailableReason
        };
    }

    private static string ReadRecommendedQuiltLoader(JsonElement root, string minecraftVersion) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(minecraftVersion, out var components) &&
        components.ValueKind == JsonValueKind.Object
            ? ReadString(components, "quilt_loader")
            : "";

    private static string ForgeLoaderVersion(string coordinate, string minecraftVersion)
    {
        var loader = coordinate[(minecraftVersion.Length + 1)..];
        var duplicateGameSuffix = "-" + minecraftVersion;
        return loader.EndsWith(duplicateGameSuffix, StringComparison.OrdinalIgnoreCase)
            ? loader[..^duplicateGameSuffix.Length]
            : loader;
    }

    private static ManagedLoaderChannel LoaderChannel(string version)
    {
        if (version.Contains("beta", StringComparison.OrdinalIgnoreCase) ||
            version.Contains("-rc", StringComparison.OrdinalIgnoreCase))
            return ManagedLoaderChannel.Beta;
        return version.Contains("alpha", StringComparison.OrdinalIgnoreCase) ||
               version.Contains("snapshot", StringComparison.OrdinalIgnoreCase)
            ? ManagedLoaderChannel.Experimental
            : ManagedLoaderChannel.Stable;
    }

    private static System.Version ComparableVersion(string value)
    {
        var match = Regex.Match(value, @"^\d+(?:\.\d+){1,3}", RegexOptions.CultureInvariant);
        if (!match.Success) return new System.Version(0, 0);
        var parts = match.Value.Split('.').ToList();
        while (parts.Count < 2) parts.Add("0");
        return System.Version.TryParse(string.Join('.', parts), out var parsed)
            ? parsed
            : new System.Version(0, 0);
    }

    private static int? LoaderJavaMajor(string minecraftVersion) =>
        PaperJavaRuntimePolicy.RequiredMajor(minecraftVersion) ??
        JavaRuntimePolicy.TryRequiredMajorForMinecraft(minecraftVersion);

    private static string CanonicalOrnitheMinecraftVersion(string providerVersion) =>
        providerVersion.Equals("1.0.0", StringComparison.OrdinalIgnoreCase) ? "1.0" : providerVersion;

    private static bool RequiresUserSuppliedHistoricJar(string minecraftVersion) =>
        minecraftVersion.Equals("1.0", StringComparison.OrdinalIgnoreCase) ||
        minecraftVersion.Equals("1.0.0", StringComparison.OrdinalIgnoreCase) ||
        minecraftVersion.Equals("b1.8", StringComparison.OrdinalIgnoreCase) ||
        minecraftVersion.Equals("b1.8.1", StringComparison.OrdinalIgnoreCase);
}
