namespace ChunkPilot.Core;

/// <summary>The release channel assigned by PaperMC Fill to an exact Paper build.</summary>
public enum PaperBuildChannel
{
    Stable,
    Beta,
    Alpha,
    Unknown
}

/// <summary>One Minecraft version advertised by PaperMC's official Fill v3 project inventory.</summary>
public sealed record PaperVersionOption
{
    public string VersionId { get; init; } = "";
    public string VersionGroup { get; init; } = "";
    public MinecraftReleaseKind ReleaseKind { get; init; } = MinecraftReleaseKind.Unknown;
    public int? RequiredJavaMajor { get; init; }
    public string JavaEvidence { get; init; } = "";
    public string Provenance { get; init; } = "Official PaperMC Fill v3 project inventory";
    public string SupportReason { get; init; } = "";
    public MinecraftVersionSupportTier SupportTier { get; init; } = MinecraftVersionSupportTier.Experimental;
    public MinecraftVersionCertification Certification { get; init; } = new()
    {
        Level = MinecraftVersionCertificationLevel.Inventoried,
        Limitations = ["Select an exact Paper build before creation."]
    };
    public bool IsSelectable => ReleaseKind == MinecraftReleaseKind.Release && RequiredJavaMajor is >= 8;
}

/// <summary>The official Paper version inventory plus its cache/provider state.</summary>
public sealed record PaperVersionCatalog
{
    public IReadOnlyList<PaperVersionOption> Versions { get; init; } = [];
    public DateTimeOffset? RetrievedUtc { get; init; }
    public bool IsFromCache { get; init; }
    public bool IsStale { get; init; }
    public bool ProviderAvailable { get; init; }
    public string UnavailableDetail { get; init; } = "";

    public static PaperVersionCatalog Unavailable(string detail) =>
        new() { ProviderAvailable = false, UnavailableDetail = detail };
}

/// <summary>An exact Paper build and its provider-supplied integrity evidence.</summary>
public sealed record PaperBuildOption
{
    public string MinecraftVersion { get; init; } = "";
    public int BuildId { get; init; }
    public PaperBuildChannel Channel { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string FileName { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string ServerSha256 { get; init; } = "";
    public long? ServerSizeBytes { get; init; }
    public string Provenance { get; init; } = "Official PaperMC Fill v3 build metadata";
    public string SupportReason { get; init; } = "";
    public MinecraftVersionSupportTier SupportTier { get; init; } = MinecraftVersionSupportTier.Experimental;
    public MinecraftVersionCertification Certification { get; init; } = new()
    {
        Level = MinecraftVersionCertificationLevel.MetadataValidated,
        Limitations = ["This exact Paper build has not been runtime-certified by ChunkPilot."]
    };

    public bool HasIntegrityMetadata => ServerSizeBytes is > 0 &&
        ServerSha256.Length == 64 && ServerSha256.All(Uri.IsHexDigit);

    /// <summary>Stable is the default; exact beta/alpha builds require the separate Experimental acknowledgement.</summary>
    public bool IsSelectable => (Channel is PaperBuildChannel.Stable or PaperBuildChannel.Beta or PaperBuildChannel.Alpha) && BuildId > 0 &&
        HasIntegrityMetadata && Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;
}

/// <summary>Exact builds for one Minecraft version, including cache/provider state.</summary>
public sealed record PaperBuildCatalog
{
    public string MinecraftVersion { get; init; } = "";
    public IReadOnlyList<PaperBuildOption> Builds { get; init; } = [];
    public DateTimeOffset? RetrievedUtc { get; init; }
    public bool IsFromCache { get; init; }
    public bool IsStale { get; init; }
    public bool ProviderAvailable { get; init; }
    public string UnavailableDetail { get; init; } = "";

    public static PaperBuildCatalog Unavailable(string version, string detail) =>
        new() { MinecraftVersion = version, ProviderAvailable = false, UnavailableDetail = detail };
}

/// <summary>Paper's documented Java support matrix, kept separate from Vanilla metadata.</summary>
public static class PaperJavaRuntimePolicy
{
    public static int? RequiredMajor(string versionId)
    {
        var numeric = MinecraftVersionClassification.NumericVersion(versionId);
        if (numeric is null)
            return null;
        if (numeric >= new Version(26, 1))
            return 25;
        if (numeric >= new Version(1, 20, 5))
            return 21;
        if (numeric >= new Version(1, 18))
            return 17;
        if (numeric >= new Version(1, 17))
            return 16;
        return numeric >= new Version(1, 7, 10) ? 8 : null;
    }

    public static string Evidence(int major) =>
        $"ChunkPilot's Paper compatibility policy requires Java {major} for this Minecraft version.";
}

/// <summary>A complete exact-build Paper creation plan submitted to the Agent.</summary>
public sealed record PaperCreationPlan
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public string ServerName { get; init; } = "";
    public PaperVersionOption Version { get; init; } = new();
    public PaperBuildOption Build { get; init; } = new();
    public VanillaEulaAcceptance Eula { get; init; } = new();
    public int MaxPlayers { get; init; } = 10;
    public int MinimumRamMb { get; init; } = 1_024;
    public int MaximumRamMb { get; init; } = 4_096;
    public int Port { get; init; } = ServerPortPolicy.DefaultPort;
    public VanillaNetworkingPreference NetworkingPreference { get; init; } =
        VanillaNetworkingPreference.FriendsOverInternet;
    public string InstanceRoot { get; init; } = "";
    public DateTimeOffset? MetadataRetrievedUtc { get; init; }
    public bool MetadataFromCache { get; init; }
    public bool ExperimentalRuntimeRiskAccepted { get; init; }
    public CreationWorldSource? InitialWorld { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerName))
            problems.Add("The server has no name.");
        if (!Version.IsSelectable)
            problems.Add("Select a supported stable Minecraft release for Paper.");
        if (!Build.IsSelectable ||
            !Build.MinecraftVersion.Equals(Version.VersionId, StringComparison.OrdinalIgnoreCase))
            problems.Add("Select an exact supported Paper build for the chosen Minecraft version.");
        if (Build.SupportTier == MinecraftVersionSupportTier.Experimental && !ExperimentalRuntimeRiskAccepted)
            problems.Add("Acknowledge that this Paper build has not been runtime-certified by ChunkPilot.");
        if (!Eula.IsAuthorised)
            problems.Add("The Minecraft EULA was not accepted.");
        var memoryProblem = MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb);
        if (memoryProblem is not null)
            problems.Add(memoryProblem);
        var portProblem = ServerPortPolicy.Validate(Port);
        if (portProblem is not null)
            problems.Add(portProblem);
        if (InitialWorld is { } world)
            problems.AddRange(world.Problems());
        return problems;
    }
}

public sealed record PaperCatalogRequest(bool ForceRefresh = false);
public sealed record PaperBuildsRequest(string MinecraftVersion, bool ForceRefresh = false);
public sealed record BeginPaperCreationRequest(PaperCreationPlan Plan);
public sealed record PaperCreationsResult(IReadOnlyList<InstallOperationSnapshot> Operations);
