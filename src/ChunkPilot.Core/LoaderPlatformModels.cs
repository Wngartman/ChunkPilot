namespace ChunkPilot.Core;

public enum ManagedLoaderPlatform
{
    Fabric = 0,
    NeoForge = 1,
    Quilt = 2,
    Forge = 3,
    LegacyFabric = 4,
    Ornithe = 5
}

public enum ManagedLoaderCatalogStrategy
{
    FabricMeta,
    QuiltMeta,
    ForgeMaven,
    NeoForgeMaven,
    LegacyFabricMeta,
    OrnitheMeta
}

public enum ManagedLoaderInstallationStrategy
{
    DirectServerLauncher,
    JavaInstaller,
    CatalogOnly
}

/// <summary>
/// Provider behavior is explicit so adding a catalog identity cannot silently inherit another
/// loader's install, update, or certification path.
/// </summary>
public sealed record ManagedLoaderPlatformStrategy
{
    public required ManagedLoaderPlatform Platform { get; init; }
    public required ManagedLoaderCatalogStrategy Catalog { get; init; }
    public required ManagedLoaderInstallationStrategy Installation { get; init; }
    public required string OfficialSourceUrl { get; init; }
    public bool SupportsTypedCreation { get; init; }
    public bool SupportsUpdateMaterialization { get; init; }
    public bool SupportsRuntimeCertification { get; init; }
    public bool AllowsArtifactWithoutProviderChecksum { get; init; }
    public string CreationUnavailableReason { get; init; } = "";
}

public static class ManagedLoaderPlatformStrategies
{
    public static ManagedLoaderPlatformStrategy For(ManagedLoaderPlatform platform) => platform switch
    {
        ManagedLoaderPlatform.Fabric => new()
        {
            Platform = platform,
            Catalog = ManagedLoaderCatalogStrategy.FabricMeta,
            Installation = ManagedLoaderInstallationStrategy.DirectServerLauncher,
            OfficialSourceUrl = "https://meta.fabricmc.net/v2/versions",
            SupportsTypedCreation = true,
            SupportsUpdateMaterialization = true,
            SupportsRuntimeCertification = true,
            AllowsArtifactWithoutProviderChecksum = true
        },
        ManagedLoaderPlatform.Quilt => new()
        {
            Platform = platform,
            Catalog = ManagedLoaderCatalogStrategy.QuiltMeta,
            Installation = ManagedLoaderInstallationStrategy.JavaInstaller,
            OfficialSourceUrl = "https://meta.quiltmc.org/v3/versions",
            SupportsTypedCreation = true,
            SupportsUpdateMaterialization = true,
            SupportsRuntimeCertification = true
        },
        ManagedLoaderPlatform.Forge => new()
        {
            Platform = platform,
            Catalog = ManagedLoaderCatalogStrategy.ForgeMaven,
            Installation = ManagedLoaderInstallationStrategy.JavaInstaller,
            OfficialSourceUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml",
            SupportsTypedCreation = true,
            SupportsUpdateMaterialization = true,
            SupportsRuntimeCertification = true
        },
        ManagedLoaderPlatform.NeoForge => new()
        {
            Platform = platform,
            Catalog = ManagedLoaderCatalogStrategy.NeoForgeMaven,
            Installation = ManagedLoaderInstallationStrategy.JavaInstaller,
            OfficialSourceUrl = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge",
            SupportsTypedCreation = true,
            SupportsUpdateMaterialization = true,
            SupportsRuntimeCertification = true
        },
        ManagedLoaderPlatform.LegacyFabric => CatalogOnly(platform, ManagedLoaderCatalogStrategy.LegacyFabricMeta,
            "https://meta.legacyfabric.net/v2/versions",
            "Legacy Fabric is inventoried from its official catalog, but typed installation is not enabled yet."),
        ManagedLoaderPlatform.Ornithe => CatalogOnly(platform, ManagedLoaderCatalogStrategy.OrnitheMeta,
            "https://meta.ornithemc.net/v3/versions",
            "Ornithe is inventoried from its official catalog, but typed installation and user-supplied historical server-JAR import are not enabled yet."),
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unknown managed-loader platform.")
    };

    private static ManagedLoaderPlatformStrategy CatalogOnly(
        ManagedLoaderPlatform platform,
        ManagedLoaderCatalogStrategy catalog,
        string source,
        string reason) => new()
    {
        Platform = platform,
        Catalog = catalog,
        Installation = ManagedLoaderInstallationStrategy.CatalogOnly,
        OfficialSourceUrl = source,
        CreationUnavailableReason = reason
    };
}

public enum ManagedLoaderChannel
{
    Stable,
    Beta,
    Experimental
}

/// <summary>One stable Minecraft release advertised by an official loader source.</summary>
public sealed record ManagedLoaderMinecraftVersion
{
    public ManagedLoaderPlatform Platform { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public string ProviderMinecraftVersion { get; init; } = "";
    public bool StableMinecraft { get; init; }
    public int? RequiredJavaMajor { get; init; }
    public bool RequiresUserSuppliedMinecraftServerJar { get; init; }
    public string UnavailableReason { get; init; } = "";
    public MinecraftVersionSupportTier SupportTier { get; init; } = MinecraftVersionSupportTier.Experimental;
    public string SupportReason { get; init; } = "";
    public string Provenance { get; init; } = "";
    public MinecraftVersionCertification Certification { get; init; } = new()
    {
        Level = MinecraftVersionCertificationLevel.Inventoried,
        Limitations = ["Select an exact loader version before creation."]
    };

    public bool IsSelectable => StableMinecraft && RequiredJavaMajor is >= 8 &&
        !RequiresUserSuppliedMinecraftServerJar &&
        ManagedLoaderPlatformStrategies.For(Platform).SupportsTypedCreation &&
        !string.IsNullOrWhiteSpace(MinecraftVersion);
}

public sealed record ManagedLoaderVersionCatalog
{
    public ManagedLoaderPlatform Platform { get; init; }
    public IReadOnlyList<ManagedLoaderMinecraftVersion> Versions { get; init; } = [];
    public DateTimeOffset? RetrievedUtc { get; init; }
    public bool IsFromCache { get; init; }
    public bool IsStale { get; init; }
    public bool ProviderAvailable { get; init; }
    public string UnavailableDetail { get; init; } = "";
    public string CreationUnavailableDetail { get; init; } = "";

    public static ManagedLoaderVersionCatalog Unavailable(ManagedLoaderPlatform platform, string detail) =>
        new()
        {
            Platform = platform,
            ProviderAvailable = false,
            UnavailableDetail = detail,
            CreationUnavailableDetail = ManagedLoaderPlatformStrategies.For(platform).CreationUnavailableReason
        };
}

/// <summary>An exact official loader/server-launcher or installer identity.</summary>
public sealed record ManagedLoaderBuild
{
    public ManagedLoaderPlatform Platform { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    public ManagedLoaderChannel Channel { get; init; }
    public bool ProviderRecommended { get; init; }
    public bool ProviderLatest { get; init; }
    public string ArtifactUrl { get; init; } = "";
    public string ArtifactSha1 { get; init; } = "";
    public string ArtifactSha256 { get; init; } = "";
    public long? ArtifactSizeBytes { get; init; }
    public int? RequiredJavaMajor { get; init; }
    /// <summary>
    /// Java used only to run a loader installer. This may differ from the Java that runs the
    /// resulting Minecraft server (for example, Quilt installer 0.15.1 needs Java 17 while an
    /// older Minecraft server still needs Java 8 or 16).
    /// </summary>
    public int? InstallerJavaMajor { get; init; }
    public MinecraftVersionSupportTier SupportTier { get; init; } = MinecraftVersionSupportTier.Experimental;
    public string SupportReason { get; init; } = "";
    public string Provenance { get; init; } = "";
    public string UnavailableReason { get; init; } = "";
    public MinecraftVersionCertification Certification { get; init; } = new()
    {
        Level = MinecraftVersionCertificationLevel.MetadataValidated,
        Limitations = ["This exact loader combination has not been runtime-certified by ChunkPilot."]
    };

    public bool HasProviderIntegrity => ArtifactSha256.Length == 64 && ArtifactSha256.All(Uri.IsHexDigit) ||
                                        ArtifactSha1.Length == 40 && ArtifactSha1.All(Uri.IsHexDigit);

    public bool IsSelectable => !string.IsNullOrWhiteSpace(MinecraftVersion) &&
        !string.IsNullOrWhiteSpace(LoaderVersion) && RequiredJavaMajor is >= 8 &&
        Uri.TryCreate(ArtifactUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        ManagedLoaderPlatformStrategies.For(Platform).SupportsTypedCreation &&
        (ManagedLoaderPlatformStrategies.For(Platform).AllowsArtifactWithoutProviderChecksum || HasProviderIntegrity);
}

/// <summary>
/// Separates a loader installer's toolchain from the Java runtime required by the resulting server.
/// The fallback matters for persisted catalogs written before InstallerJavaMajor was introduced.
/// </summary>
public static class ManagedLoaderInstallerJavaPolicy
{
    public static int Resolve(
        ManagedLoaderPlatform platform,
        int? declaredInstallerJavaMajor,
        int runtimeJavaMajor) =>
        declaredInstallerJavaMajor is >= 8
            ? declaredInstallerJavaMajor.Value
            : platform == ManagedLoaderPlatform.Quilt
                ? Math.Max(17, runtimeJavaMajor)
                : runtimeJavaMajor;
}

public sealed record ManagedLoaderBuildCatalog
{
    public ManagedLoaderPlatform Platform { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public IReadOnlyList<ManagedLoaderBuild> Builds { get; init; } = [];
    public DateTimeOffset? RetrievedUtc { get; init; }
    public bool IsFromCache { get; init; }
    public bool IsStale { get; init; }
    public bool ProviderAvailable { get; init; }
    public string UnavailableDetail { get; init; } = "";
    public string CreationUnavailableDetail { get; init; } = "";

    public static ManagedLoaderBuildCatalog Unavailable(
        ManagedLoaderPlatform platform,
        string minecraftVersion,
        string detail) => new()
    {
        Platform = platform,
        MinecraftVersion = minecraftVersion,
        ProviderAvailable = false,
        UnavailableDetail = detail,
        CreationUnavailableDetail = ManagedLoaderPlatformStrategies.For(platform).CreationUnavailableReason
    };
}

public sealed record ManagedLoaderCreationPlan
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public string ServerName { get; init; } = "";
    public ManagedLoaderMinecraftVersion Version { get; init; } = new();
    public ManagedLoaderBuild Build { get; init; } = new();
    public VanillaEulaAcceptance Eula { get; init; } = new();
    public int MaxPlayers { get; init; } = 10;
    public int MinimumRamMb { get; init; } = 2_048;
    public int MaximumRamMb { get; init; } = 6_144;
    public int Port { get; init; } = ServerPortPolicy.DefaultPort;
    public VanillaNetworkingPreference NetworkingPreference { get; init; } = VanillaNetworkingPreference.HomeNetwork;
    public string InstanceRoot { get; init; } = "";
    public DateTimeOffset? MetadataRetrievedUtc { get; init; }
    public bool MetadataFromCache { get; init; }
    public bool ExperimentalRuntimeRiskAccepted { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerName))
            problems.Add("The server has no name.");
        if (!Version.IsSelectable)
            problems.Add(!string.IsNullOrWhiteSpace(Version.UnavailableReason)
                ? Version.UnavailableReason
                : ManagedLoaderPlatformStrategies.For(Version.Platform).SupportsTypedCreation
                    ? "Select a supported stable Minecraft version."
                    : ManagedLoaderPlatformStrategies.For(Version.Platform).CreationUnavailableReason);
        if (!Build.IsSelectable || Build.Platform != Version.Platform ||
            !Build.MinecraftVersion.Equals(Version.MinecraftVersion, StringComparison.OrdinalIgnoreCase))
            problems.Add(!string.IsNullOrWhiteSpace(Build.UnavailableReason)
                ? Build.UnavailableReason
                : "Select an exact compatible loader version for this Minecraft version.");
        if (Build.SupportTier == MinecraftVersionSupportTier.Experimental && !ExperimentalRuntimeRiskAccepted)
            problems.Add("Acknowledge that this exact loader combination has not been runtime-certified by ChunkPilot.");
        if (!Eula.IsAuthorised)
            problems.Add("The Minecraft EULA was not accepted.");
        var memory = MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb);
        if (memory is not null) problems.Add(memory);
        var port = ServerPortPolicy.Validate(Port);
        if (port is not null) problems.Add(port);
        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record ManagedLoaderCatalogRequest(ManagedLoaderPlatform Platform, bool ForceRefresh = false);
public sealed record ManagedLoaderBuildsRequest(
    ManagedLoaderPlatform Platform,
    string MinecraftVersion,
    bool ForceRefresh = false);
public sealed record BeginManagedLoaderCreationRequest(ManagedLoaderCreationPlan Plan);
public sealed record ManagedLoaderCreationsResult(IReadOnlyList<InstallOperationSnapshot> Operations);
