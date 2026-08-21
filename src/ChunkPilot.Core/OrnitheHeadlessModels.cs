namespace ChunkPilot.Core;

public enum OrnitheLoaderFamily
{
    Fabric,
    Quilt
}

public enum HistoricalMinecraftServerArtifactSource
{
    OfficialMojang,
    UserSupplied
}

public static class OrnitheHistoricalVersionPolicy
{
    private static readonly IReadOnlyDictionary<string, string> ProviderVersions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["1.0"] = "1.0.0",
            ["1.2.5"] = "1.2.5",
            ["b1.8"] = "b1.8",
            ["b1.8.1"] = "b1.8.1"
        };

    public static IReadOnlyList<string> ExactTargets { get; } = ["1.0", "1.2.5", "b1.8", "b1.8.1"];

    public static bool IsExactTarget(string minecraftVersion) =>
        ProviderVersions.ContainsKey(Canonical(minecraftVersion));

    public static string ProviderVersion(string minecraftVersion) =>
        ProviderVersions.TryGetValue(Canonical(minecraftVersion), out var provider)
            ? provider
            : throw new ArgumentException(
                "ChunkPilot has not established an exact Ornithe historical profile for this Minecraft version.",
                nameof(minecraftVersion));

    public static string Canonical(string minecraftVersion) =>
        minecraftVersion.Equals("1.0.0", StringComparison.OrdinalIgnoreCase) ? "1.0" : minecraftVersion;

    public static HistoricalMinecraftServerArtifactRequirement ServerArtifact(string minecraftVersion)
    {
        var canonical = Canonical(minecraftVersion);
        var provider = ProviderVersion(canonical);
        if (canonical.Equals("1.2.5", StringComparison.OrdinalIgnoreCase))
            return new HistoricalMinecraftServerArtifactRequirement
            {
                MinecraftVersion = canonical,
                ProviderMinecraftVersion = provider,
                Source = HistoricalMinecraftServerArtifactSource.OfficialMojang,
                OfficialUrl =
                    "https://launcher.mojang.com/v1/objects/d8321edc9470e56b8ad5c67bbd16beba25843336/server.jar",
                OfficialSha1 = "d8321edc9470e56b8ad5c67bbd16beba25843336",
                OfficialSizeBytes = 1_408_470,
                Reason = "Mojang's official 1.2.5 version metadata publishes this exact dedicated-server artifact."
            };
        return new HistoricalMinecraftServerArtifactRequirement
        {
            MinecraftVersion = canonical,
            ProviderMinecraftVersion = provider,
            Source = HistoricalMinecraftServerArtifactSource.UserSupplied,
            RequiredTokenKind = "legacy-server-jar",
            Reason =
                "Mojang's current official version metadata does not publish a dedicated-server artifact for this exact historical version. A user-owned server JAR must be selected natively, inspected, and rehashed before use."
        };
    }
}

/// <summary>
/// Describes the legally and technically available Minecraft server input without smuggling a
/// renderer path into the provider model. User-supplied inputs are resolved later through a
/// short-lived native token and must be rehashed before activation.
/// </summary>
public sealed record HistoricalMinecraftServerArtifactRequirement
{
    public string MinecraftVersion { get; init; } = "";
    public string ProviderMinecraftVersion { get; init; } = "";
    public HistoricalMinecraftServerArtifactSource Source { get; init; }
    public string OfficialUrl { get; init; } = "";
    public string OfficialSha1 { get; init; } = "";
    public long? OfficialSizeBytes { get; init; }
    public string RequiredTokenKind { get; init; } = "";
    public string Reason { get; init; } = "";

    public bool IsAutomaticallyAcquirable => Source == HistoricalMinecraftServerArtifactSource.OfficialMojang &&
        Uri.TryCreate(OfficialUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("launcher.mojang.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase)) &&
        OfficialSha1.Length == 40 && OfficialSha1.All(Uri.IsHexDigit) && OfficialSizeBytes is > 0;
}

public sealed record OrnitheHeadlessLibrary
{
    public string MavenCoordinate { get; init; } = "";
    public string RepositoryUrl { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long? SizeBytes { get; init; }
}

/// <summary>An exact official Ornithe Meta v3 server profile; no code has run to obtain it.</summary>
public sealed record OrnitheHeadlessServerProfile
{
    public string ProfileId { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string ProviderMinecraftVersion { get; init; } = "";
    public int IntermediaryGeneration { get; init; }
    public OrnitheLoaderFamily LoaderFamily { get; init; }
    public string LoaderVersion { get; init; } = "";
    public string MainClass { get; init; } = "";
    public IReadOnlyList<string> JvmArguments { get; init; } = [];
    public IReadOnlyList<string> GameArguments { get; init; } = [];
    public IReadOnlyList<OrnitheHeadlessLibrary> Libraries { get; init; } = [];
    public string MetadataUrl { get; init; } = "";
    /// <summary>SHA-256 of the exact received metadata bytes; provider integrity is not implied.</summary>
    public string MetadataSha256 { get; init; } = "";
    public DateTimeOffset RetrievedUtc { get; init; }
}

public enum HeadlessArtifactIntegrityRequirement
{
    ProviderSha256,
    ProviderSha1,
    OfficialMavenSidecar
}

public sealed record HeadlessMaterializationArtifact
{
    public string Identity { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long? SizeBytes { get; init; }
    public HeadlessArtifactIntegrityRequirement IntegrityRequirement { get; init; }
}

/// <summary>
/// Immutable plan consumed by a future Agent-owned materializer. It contains no arbitrary native
/// path and cannot itself download, execute, or persist anything.
/// </summary>
public sealed record OrnitheHeadlessMaterializationPlan
{
    public required ManagedLoaderBuild Build { get; init; }
    public required OrnitheHeadlessServerProfile Profile { get; init; }
    public required HistoricalMinecraftServerArtifactRequirement MinecraftServerArtifact { get; init; }
    public IReadOnlyList<HeadlessMaterializationArtifact> Libraries { get; init; } = [];
    public IReadOnlyList<string> ClassPath { get; init; } = [];
    public IReadOnlyList<string> JvmArguments { get; init; } = [];
    public IReadOnlyList<string> GameArguments { get; init; } = [];
    public string MainClass { get; init; } = "";
    public string MinecraftServerRelativePath { get; init; } = "minecraft-server.jar";
    public string UserSuppliedArtifactToken { get; init; } = "";
    public string EvidenceSummary { get; init; } = "";

    public bool RequiresUserSuppliedArtifact =>
        MinecraftServerArtifact.Source == HistoricalMinecraftServerArtifactSource.UserSupplied;
}

public sealed record OrnitheHeadlessCertificationRequest
{
    public required OrnitheHeadlessMaterializationPlan Plan { get; init; }
    public bool ExplicitDisposableEulaAuthorization { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(4);
}

public enum HeadlessCertificationResult
{
    Passed,
    BlockedMissingServerArtifact,
    BlockedIncompleteIntegrity,
    BlockedUnresolvedJava,
    BlockedEulaAuthorization,
    FailedMaterialization,
    FailedRuntimeStartup,
    FailedReadiness,
    FailedPlayerStatus,
    FailedCleanStop,
    Cancelled
}

/// <summary>Compact exact evidence suitable for a checked-in support manifest.</summary>
public sealed record OrnitheHeadlessCertificationEvidence
{
    public string MinecraftVersion { get; init; } = "";
    public string ProviderMinecraftVersion { get; init; } = "";
    public int IntermediaryGeneration { get; init; }
    public OrnitheLoaderFamily LoaderFamily { get; init; }
    public string LoaderVersion { get; init; } = "";
    public string ProfileMetadataSha256 { get; init; } = "";
    public string MinecraftServerSha256 { get; init; } = "";
    public int JavaMajor { get; init; }
    public HeadlessCertificationResult Result { get; init; }
    public bool RuntimeLaunched { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public PlayerStatusSource PlayerStatusSource { get; init; } = PlayerStatusSource.Unsupported;
    public bool CleanStopConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public bool CleanupSucceeded { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public string Limitation { get; init; } = "";
}

public interface IOrnitheHeadlessProfileProvider
{
    Task<OrnitheHeadlessServerProfile> GetOrnitheHeadlessProfileAsync(
        string minecraftVersion,
        OrnitheLoaderFamily loaderFamily,
        string loaderVersion,
        CancellationToken cancellationToken = default);
}

public interface IOrnitheHeadlessRuntimeCertifier
{
    Task<OrnitheHeadlessCertificationEvidence> CertifyAsync(
        OrnitheHeadlessCertificationRequest request,
        CancellationToken cancellationToken = default);
}
