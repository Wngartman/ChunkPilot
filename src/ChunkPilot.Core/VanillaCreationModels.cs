namespace ChunkPilot.Core;

/// <summary>Which Minecraft release stream a version belongs to.</summary>
public enum VanillaReleaseChannel
{
    /// <summary>A finished release. The default and the only thing shown unless asked otherwise.</summary>
    Stable,

    /// <summary>An in-development build. Shown only when the user asks for snapshots.</summary>
    Snapshot,

    /// <summary>An early historical build. Never offered for creation.</summary>
    Historic
}

/// <summary>The exact kind advertised by Mojang, refined from the manifest type and official id.</summary>
public enum MinecraftReleaseKind
{
    Release,
    Snapshot,
    PreRelease,
    ReleaseCandidate,
    ExperimentalSnapshot,
    Beta,
    Alpha,
    Unknown
}

/// <summary>A user-facing support promise. Existence in Mojang's inventory is deliberately separate.</summary>
public enum MinecraftVersionSupportTier
{
    Recommended,
    Verified,
    Experimental,
    Unavailable
}

/// <summary>The launch contract ChunkPilot can establish before a creation is submitted.</summary>
public enum MinecraftLaunchProfileKind
{
    ModernEulaNogui,
    LegacyNogui,
    Unknown
}

/// <summary>How far this exact version has progressed through ChunkPilot's certification gates.</summary>
public enum MinecraftVersionCertificationLevel
{
    Inventoried,
    MetadataValidated,
    RuntimeCertified,
    Failed
}

/// <summary>Named, truthful evidence for one exact version. Metadata proof is never runtime proof.</summary>
public sealed record MinecraftVersionCertification
{
    public MinecraftVersionCertificationLevel Level { get; init; }
    public bool OfficialVersionRecord { get; init; }
    public bool OfficialServerArtifact { get; init; }
    public bool ArtifactIntegrityMetadata { get; init; }
    public bool JavaResolved { get; init; }
    public bool LaunchProfileResolved { get; init; }
    public bool RuntimeLaunched { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public bool CleanShutdownConfirmed { get; init; }
    public bool ExpectedFilesConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public DateTimeOffset? RuntimeValidatedAt { get; init; }
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Version-specific features which the UI may expose without string checks.</summary>
public sealed record MinecraftVersionCapabilities
{
    public bool ServerIcon { get; init; }
    public bool FormattedMotd { get; init; }
    public bool PlayerManagement { get; init; }
    public bool ModernServerProperties { get; init; }
    public bool StatusQuery { get; init; }
    public bool Datapacks { get; init; }
    public bool ManagedVersionChange { get; init; }
}

/// <summary>Central launch behavior and capability evidence for one exact Minecraft version.</summary>
public sealed record MinecraftLaunchProfile
{
    public MinecraftLaunchProfileKind Kind { get; init; } = MinecraftLaunchProfileKind.Unknown;
    public string Arguments { get; init; } = "nogui";
    public string ReadinessPattern { get; init; } = "Done (";
    public string StopCommand { get; init; } = "stop";
    public bool RequiresEulaFile { get; init; }
    public string Evidence { get; init; } = "";
    public MinecraftVersionCapabilities Capabilities { get; init; } = new();
    public bool IsResolved => Kind != MinecraftLaunchProfileKind.Unknown;
}

/// <summary>
/// What ChunkPilot concluded about one Minecraft version, and why.
/// </summary>
/// <remarks>
/// Deliberately not derived from how old a version is. A version is offered when official metadata
/// supplies a dedicated server download and a Java requirement can be established; anything else is
/// named for what is actually missing.
/// </remarks>
public enum VanillaVersionSupport
{
    /// <summary>Offered, and everything needed is established.</summary>
    Supported,

    /// <summary>Offered, but something about it deserves a sentence before the user commits.</summary>
    SupportedWithWarning,

    /// <summary>Official metadata publishes no dedicated server download for this version.</summary>
    NoServerArtifact,

    /// <summary>Neither official metadata nor ChunkPilot's rules establish which Java is needed.</summary>
    JavaRequirementUnknown,

    /// <summary>ChunkPilot has no supported way to create a server from this entry.</summary>
    UnsupportedByChunkPilot
}

/// <summary>Where a Java requirement came from. The distinction reaches the review screen.</summary>
public enum JavaRequirementSource
{
    /// <summary>Not established.</summary>
    Unknown,

    /// <summary>Read from the official version metadata's own <c>javaVersion</c> block.</summary>
    OfficialMetadata,

    /// <summary>Derived by ChunkPilot's version rules because metadata did not say.</summary>
    ChunkPilotPolicy
}

/// <summary>
/// One selectable Minecraft version, carrying only evidence actually supplied or derived.
/// </summary>
/// <remarks>
/// Every field is either copied from official metadata or produced by a named policy. Nothing is
/// guessed from a filename, and an absent value stays absent rather than becoming a plausible
/// default: <see cref="RequiredJavaMajor"/> being null is what blocks creation rather than a
/// hopeful 21.
/// </remarks>
public sealed record VanillaVersionOption
{
    public string VersionId { get; init; } = "";
    public VanillaReleaseChannel Channel { get; init; }

    /// <summary>The raw release type string from the manifest, kept for diagnostics.</summary>
    public string ReleaseType { get; init; } = "";

    public DateTimeOffset? ReleaseTime { get; init; }

    /// <summary>The manifest metadata timestamp, distinct from when the version was released.</summary>
    public DateTimeOffset? MetadataTime { get; init; }

    public MinecraftReleaseKind ReleaseKind { get; init; }

    /// <summary>Official per-version metadata document this entry was resolved from.</summary>
    public string MetadataUrl { get; init; } = "";

    /// <summary>Mojang's digest for the per-version metadata document, used for incremental refresh.</summary>
    public string MetadataSha1 { get; init; } = "";

    public bool HasServerDownload { get; init; }
    public string ServerDownloadUrl { get; init; } = "";

    /// <summary>Provider-supplied SHA-1 for the server jar. Integrity evidence, not a signature.</summary>
    public string ServerSha1 { get; init; } = "";

    public long? ServerSizeBytes { get; init; }

    /// <summary>Null when nothing established it. Never defaulted.</summary>
    public int? RequiredJavaMajor { get; init; }

    public JavaRequirementSource JavaRequirementSource { get; init; } = JavaRequirementSource.Unknown;
    public VanillaVersionSupport Support { get; init; } = VanillaVersionSupport.UnsupportedByChunkPilot;

    public MinecraftVersionSupportTier SupportTier { get; init; } = MinecraftVersionSupportTier.Unavailable;
    public string SupportReason { get; init; } = "";
    public MinecraftLaunchProfile LaunchProfile { get; init; } = new();
    public MinecraftVersionCertification Certification { get; init; } = new();

    /// <summary>Exact evidence used by the support assessment. It is never a claim of a runtime smoke.</summary>
    public IReadOnlyList<string> CertificationEvidence { get; init; } = [];

    /// <summary>Where this entry came from, in words the user can check.</summary>
    public string Provenance { get; init; } = "";

    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>True when the wizard may let the user pick this entry.</summary>
    public bool IsSelectable => Support is VanillaVersionSupport.Supported or VanillaVersionSupport.SupportedWithWarning;

    /// <summary>Composed name so a list row reads as one statement to a screen reader.</summary>
    public string AutomationName =>
        $"Minecraft {VersionId}. {VanillaSupportPolicy.Describe(Support)}.";
}

/// <summary>
/// The version list as it stands, including how fresh it is and whether it came from a cache.
/// </summary>
/// <remarks>
/// A catalog that could not be refreshed is not an empty catalog. `IsFromCache` and `RetrievedUtc`
/// travel with the data so the wizard can say "this is what ChunkPilot last saw, on Tuesday" instead
/// of silently presenting stale entries as current.
/// </remarks>
public sealed record VanillaVersionCatalog
{
    public IReadOnlyList<VanillaVersionOption> Options { get; init; } = [];

    /// <summary>When the underlying manifest was actually retrieved from the provider.</summary>
    public DateTimeOffset? RetrievedUtc { get; init; }

    public bool IsFromCache { get; init; }

    /// <summary>True when the cache is older than its lifetime and a refresh did not succeed.</summary>
    public bool IsStale { get; init; }

    /// <summary>False when neither the provider nor a usable cache produced anything.</summary>
    public bool ProviderAvailable { get; init; }

    /// <summary>Why the catalog is unavailable or degraded. Empty when it is neither.</summary>
    public string UnavailableDetail { get; init; } = "";

    public string ManifestLatestReleaseId { get; init; } = "";
    public string ManifestLatestSnapshotId { get; init; } = "";
    public string LatestVerifiedReleaseId { get; init; } = "";

    public IReadOnlyList<VanillaVersionOption> Stable =>
        Options.Where(option => option.Channel == VanillaReleaseChannel.Stable).ToArray();

    public IReadOnlyList<VanillaVersionOption> Snapshots =>
        Options.Where(option => option.Channel == VanillaReleaseChannel.Snapshot).ToArray();

    public static VanillaVersionCatalog Unavailable(string detail) =>
        new() { ProviderAvailable = false, UnavailableDetail = detail };
}

/// <summary>Classifies official ids without spreading release-name parsing through the product.</summary>
public static class MinecraftVersionClassification
{
    public static MinecraftReleaseKind ReleaseKindFor(string versionId, string releaseType)
    {
        if (releaseType.Equals("release", StringComparison.OrdinalIgnoreCase))
            return MinecraftReleaseKind.Release;
        if (releaseType.Equals("old_beta", StringComparison.OrdinalIgnoreCase))
            return MinecraftReleaseKind.Beta;
        if (releaseType.Equals("old_alpha", StringComparison.OrdinalIgnoreCase))
            return MinecraftReleaseKind.Alpha;
        if (!releaseType.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
            return MinecraftReleaseKind.Unknown;

        if (System.Text.RegularExpressions.Regex.IsMatch(
                versionId, @"(?:-|\s)pre(?:-?release)?(?:-|\s)?\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return MinecraftReleaseKind.PreRelease;
        if (versionId.Contains("release candidate", StringComparison.OrdinalIgnoreCase) ||
            System.Text.RegularExpressions.Regex.IsMatch(versionId, @"-rc\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return MinecraftReleaseKind.ReleaseCandidate;
        if (versionId.Contains("experimental", StringComparison.OrdinalIgnoreCase) ||
            versionId.Contains("combat test", StringComparison.OrdinalIgnoreCase))
            return MinecraftReleaseKind.ExperimentalSnapshot;
        return MinecraftReleaseKind.Snapshot;
    }

    public static VanillaReleaseChannel ChannelFor(MinecraftReleaseKind kind) => kind switch
    {
        MinecraftReleaseKind.Release => VanillaReleaseChannel.Stable,
        MinecraftReleaseKind.Alpha or MinecraftReleaseKind.Beta => VanillaReleaseChannel.Historic,
        _ => VanillaReleaseChannel.Snapshot
    };

    public static Version? NumericVersion(string versionId)
    {
        var match = System.Text.RegularExpressions.Regex.Match(versionId, @"^(?<version>\d+(?:\.\d+){1,2})");
        if (!match.Success)
            return null;
        var components = match.Groups["version"].Value.Split('.');
        var normalized = components.Length == 2 ? match.Groups["version"].Value + ".0" : match.Groups["version"].Value;
        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }
}

/// <summary>One evidence-based launch-profile resolver shared by creation and capability presentation.</summary>
public static class MinecraftLaunchProfileResolver
{
    private static readonly DateTimeOffset EulaProfileBoundary = new(2014, 6, 26, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ReleaseProfileBoundary = new(2011, 11, 18, 0, 0, 0, TimeSpan.Zero);

    public static MinecraftLaunchProfile Resolve(
        string versionId,
        MinecraftReleaseKind releaseKind,
        DateTimeOffset? releaseTime)
    {
        var supportedLegacy = IsSupportedLegacyCandidate(versionId, releaseKind);
        if (releaseKind is MinecraftReleaseKind.Alpha or MinecraftReleaseKind.Beta or MinecraftReleaseKind.Unknown ||
            releaseTime is null || releaseTime < ReleaseProfileBoundary)
        {
            if (supportedLegacy)
                return Legacy(versionId,
                    "ChunkPilot's curated historical profile uses headless JAR launch, the legacy Done readiness line, a simple legacy status ping, and the stop console command. Exact user-supplied artifacts still require isolated runtime validation.");
            return new MinecraftLaunchProfile
            {
                Kind = MinecraftLaunchProfileKind.Unknown,
                Arguments = "",
                ReadinessPattern = "",
                StopCommand = "",
                Evidence = "ChunkPilot has not established a safe managed launch profile for this historical build."
            };
        }

        var numeric = MinecraftVersionClassification.NumericVersion(versionId);
        var modern = releaseTime >= EulaProfileBoundary;
        return new MinecraftLaunchProfile
        {
            Kind = modern ? MinecraftLaunchProfileKind.ModernEulaNogui : MinecraftLaunchProfileKind.LegacyNogui,
            Arguments = "nogui",
            ReadinessPattern = "Done (",
            StopCommand = "stop",
            RequiresEulaFile = modern,
            Evidence = modern
                ? "Official dedicated-server launch uses the headless nogui profile; ChunkPilot owns readiness and shutdown."
                : "The legacy headless profile is metadata-compatible but has not been runtime-certified by this build.",
            Capabilities = new MinecraftVersionCapabilities
            {
                ServerIcon = numeric is not null && numeric >= new Version(1, 7, 2),
                FormattedMotd = true,
                PlayerManagement = true,
                ModernServerProperties = numeric is not null && numeric >= new Version(1, 7, 10),
                StatusQuery = true,
                Datapacks = numeric is not null && numeric >= new Version(1, 13),
                ManagedVersionChange = modern
            }
        };
    }

    private static bool IsSupportedLegacyCandidate(string versionId, MinecraftReleaseKind releaseKind) =>
        versionId.Equals("1.0", StringComparison.OrdinalIgnoreCase) ||
        versionId.Equals("1.2.5", StringComparison.OrdinalIgnoreCase) ||
        releaseKind == MinecraftReleaseKind.Beta &&
        (versionId.Equals("b1.8", StringComparison.OrdinalIgnoreCase) ||
         versionId.Equals("b1.8.1", StringComparison.OrdinalIgnoreCase));

    private static MinecraftLaunchProfile Legacy(string versionId, string evidence) => new()
    {
        Kind = MinecraftLaunchProfileKind.LegacyNogui,
        Arguments = "nogui",
        ReadinessPattern = "Done (",
        StopCommand = "stop",
        RequiresEulaFile = false,
        Evidence = evidence,
        Capabilities = new MinecraftVersionCapabilities
        {
            ServerIcon = false,
            FormattedMotd = true,
            PlayerManagement = true,
            ModernServerProperties = false,
            StatusQuery = true,
            Datapacks = false,
            ManagedVersionChange = false
        }
    };
}

/// <summary>
/// The one place a version's support conclusion and its wording are decided.
/// </summary>
public static class VanillaSupportPolicy
{
    /// <summary>
    /// Concludes whether a resolved version can be created, from the evidence alone.
    /// </summary>
    /// <param name="channel">Which stream the version belongs to.</param>
    /// <param name="hasServerDownload">Whether official metadata published a server jar.</param>
    /// <param name="requiredJavaMajor">The established Java requirement, or null.</param>
    public static VanillaVersionSupport Conclude(
        VanillaReleaseChannel channel,
        bool hasServerDownload,
        int? requiredJavaMajor)
    {
        if (channel == VanillaReleaseChannel.Historic)
            return VanillaVersionSupport.UnsupportedByChunkPilot;
        if (!hasServerDownload)
            return VanillaVersionSupport.NoServerArtifact;
        if (requiredJavaMajor is null or < 8)
            return VanillaVersionSupport.JavaRequirementUnknown;
        return channel == VanillaReleaseChannel.Snapshot
            ? VanillaVersionSupport.SupportedWithWarning
            : VanillaVersionSupport.Supported;
    }

    /// <summary>What the conclusion means, in words a beginner reads.</summary>
    public static string Describe(VanillaVersionSupport support) => support switch
    {
        VanillaVersionSupport.Supported => "Ready to create",
        VanillaVersionSupport.SupportedWithWarning => "Ready to create, with something to read first",
        VanillaVersionSupport.NoServerArtifact => "Mojang publishes no server download for this version",
        VanillaVersionSupport.JavaRequirementUnknown => "ChunkPilot cannot tell which Java this needs",
        _ => "ChunkPilot cannot create a server from this version"
    };

    /// <summary>Short badge wording for a dense list row.</summary>
    public static string BadgeLabel(VanillaVersionSupport support) => support switch
    {
        VanillaVersionSupport.Supported => "Ready",
        VanillaVersionSupport.SupportedWithWarning => "Read first",
        VanillaVersionSupport.NoServerArtifact => "No server download",
        VanillaVersionSupport.JavaRequirementUnknown => "Java unknown",
        _ => "Not supported"
    };

    /// <summary>Classifies a manifest release type into a stream ChunkPilot reasons about.</summary>
    public static VanillaReleaseChannel ChannelFor(string releaseType) => releaseType switch
    {
        "release" => VanillaReleaseChannel.Stable,
        "snapshot" => VanillaReleaseChannel.Snapshot,
        _ => VanillaReleaseChannel.Historic
    };

    /// <summary>Assesses one official inventory entry from complete, named evidence.</summary>
    public static (MinecraftVersionSupportTier Tier, VanillaVersionSupport Compatibility, string Reason)
        Assess(
            MinecraftReleaseKind releaseKind,
            bool hasServerDownload,
            bool hasIntegrityMetadata,
            int? requiredJavaMajor,
            JavaRequirementSource javaSource,
            MinecraftLaunchProfile launchProfile,
            MinecraftVersionCertification certification,
            bool isManifestLatestRelease)
    {
        if (!hasServerDownload)
            return (MinecraftVersionSupportTier.Unavailable, VanillaVersionSupport.NoServerArtifact,
                "Mojang's metadata does not publish a dedicated server artifact for this version.");
        if (!hasIntegrityMetadata)
            return (MinecraftVersionSupportTier.Unavailable, VanillaVersionSupport.UnsupportedByChunkPilot,
                "The official server artifact is missing the checksum or size ChunkPilot requires for verification.");
        if (requiredJavaMajor is null or < 8)
            return (MinecraftVersionSupportTier.Unavailable, VanillaVersionSupport.JavaRequirementUnknown,
                "ChunkPilot could not establish a compatible Java major version.");
        if (!launchProfile.IsResolved)
            return (MinecraftVersionSupportTier.Unavailable, VanillaVersionSupport.UnsupportedByChunkPilot,
                launchProfile.Evidence);

        var unstable = releaseKind is not MinecraftReleaseKind.Release;
        var incompleteCertification = certification.Level is not MinecraftVersionCertificationLevel.RuntimeCertified;
        if (unstable || incompleteCertification)
            return (MinecraftVersionSupportTier.Experimental, VanillaVersionSupport.SupportedWithWarning,
                unstable
                    ? certification.Level is MinecraftVersionCertificationLevel.RuntimeCertified
                        ? "This exact official build passed isolated runtime certification, but it remains Experimental because it is not a stable release."
                        : "This official build is not a stable release and has not been runtime-certified by ChunkPilot."
                    : certification.Level is not MinecraftVersionCertificationLevel.RuntimeCertified
                        ? "Official artifact, integrity, Java, and launch metadata are complete, but this exact version has not been isolated-runtime certified by this ChunkPilot build."
                        : "Exact isolated-runtime certification is incomplete.");

        return (isManifestLatestRelease ? MinecraftVersionSupportTier.Recommended : MinecraftVersionSupportTier.Verified,
            VanillaVersionSupport.Supported,
            isManifestLatestRelease
                ? "Latest stable release that passed exact isolated runtime certification."
                : "Stable release that passed exact isolated runtime certification.");
    }
}

/// <summary>Builds deterministic certification evidence without pretending it launched the version.</summary>
public static class MinecraftVersionCertificationPolicy
{
    public static MinecraftVersionCertification FromMetadata(
        bool hasOfficialRecord,
        bool hasServerArtifact,
        bool hasIntegrityMetadata,
        int? requiredJavaMajor,
        MinecraftLaunchProfile launchProfile)
    {
        var evidence = new List<string>();
        var limitations = new List<string>();
        if (hasOfficialRecord) evidence.Add("Official version record");
        if (hasServerArtifact) evidence.Add("Official dedicated server artifact");
        if (hasIntegrityMetadata) evidence.Add("Official artifact checksum and size");
        if (requiredJavaMajor is not null) evidence.Add($"Java {requiredJavaMajor} requirement resolved");
        if (launchProfile.IsResolved) evidence.Add($"Managed launch profile: {launchProfile.Kind}");
        limitations.Add("This exact version has not been launched, observed ready, and cleanly stopped by the certification harness in this build.");
        var metadataComplete = hasOfficialRecord && hasServerArtifact && hasIntegrityMetadata &&
                               requiredJavaMajor is not null && launchProfile.IsResolved;
        return new MinecraftVersionCertification
        {
            Level = metadataComplete
                ? MinecraftVersionCertificationLevel.MetadataValidated
                : MinecraftVersionCertificationLevel.Inventoried,
            OfficialVersionRecord = hasOfficialRecord,
            OfficialServerArtifact = hasServerArtifact,
            ArtifactIntegrityMetadata = hasIntegrityMetadata,
            JavaResolved = requiredJavaMajor is not null,
            LaunchProfileResolved = launchProfile.IsResolved,
            Evidence = evidence,
            Limitations = limitations
        };
    }
}

/// <summary>
/// Evidence that the user deliberately accepted the Minecraft EULA for one creation.
/// </summary>
/// <remarks>
/// Carries the moment and the official location shown, and nothing else. The legal text itself is
/// not stored: the repository's existing policy records acceptance and its source, and copying the
/// document would add a maintenance burden without adding evidence.
/// </remarks>
public sealed record VanillaEulaAcceptance
{
    /// <summary>
    /// The official location of the Minecraft EULA, shown and offered for opening before acceptance.
    /// </summary>
    /// <remarks>
    /// One constant so the address that is displayed, the address that is opened and the address that
    /// is recorded as evidence cannot drift apart.
    /// </remarks>
    public const string OfficialSourceUrl = "https://www.minecraft.net/eula";

    public bool Accepted { get; init; }
    public DateTimeOffset? AcceptedAtUtc { get; init; }

    /// <summary>The official EULA location that was presented and offered for opening.</summary>
    public string SourceUrl { get; init; } = "";

    /// <summary>True only when acceptance is complete enough to authorise writing eula.txt.</summary>
    public bool IsAuthorised =>
        Accepted && AcceptedAtUtc is not null && !string.IsNullOrWhiteSpace(SourceUrl);
}

/// <summary>
/// A complete, deterministic Vanilla creation plan: everything the Agent needs and nothing else.
/// </summary>
/// <remarks>
/// Holds no provider client, no UI object, no secret and no legal text. It is plain data so it can
/// cross the pipe, be journalled, and be re-read after a restart without losing meaning.
/// </remarks>
public sealed record VanillaCreationPlan
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public string ServerName { get; init; } = "";

    /// <summary>The exact version chosen. Recovery never changes this.</summary>
    public VanillaVersionOption Version { get; init; } = new();

    /// <summary>Deliberate, timestamped acceptance. Creation is refused without it.</summary>
    public VanillaEulaAcceptance Eula { get; init; } = new();

    public int MaxPlayers { get; init; } = 10;
    public int MinimumRamMb { get; init; } = 1_024;
    public int MaximumRamMb { get; init; } = 4_096;
    public int Port { get; init; } = ServerPortPolicy.DefaultPort;

    /// <summary>
    /// What the creator wants to do next. This is guidance only: it is never consent to create a
    /// firewall rule, router mapping, tunnel, or any other network exposure.
    /// </summary>
    public VanillaNetworkingPreference NetworkingPreference { get; init; } =
        VanillaNetworkingPreference.FriendsOverInternet;

    /// <summary>Where managed servers live. Empty means the standard managed root.</summary>
    public string InstanceRoot { get; init; } = "";

    /// <summary>How fresh the metadata behind <see cref="Version"/> was when the plan was built.</summary>
    public DateTimeOffset? MetadataRetrievedUtc { get; init; }

    public bool MetadataFromCache { get; init; }

    /// <summary>Optional existing world copied into the managed creation transaction.</summary>
    public CreationWorldSource? InitialWorld { get; init; }

    /// <summary>
    /// Native-only selection of a user-owned historical dedicated-server JAR. The renderer receives
    /// only a short-lived opaque token; the App consumes and re-hashes it before this plan crosses the
    /// named pipe. The source is copied and is never modified.
    /// </summary>
    public UserSuppliedServerArtifact? UserSuppliedArtifact { get; init; }

    /// <summary>Warnings the user already saw on review, carried for the record.</summary>
    public IReadOnlyList<string> AcknowledgedWarnings { get; init; } = [];

    /// <summary>Everything that must hold before the Agent will act on this plan.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerName))
            problems.Add("The server has no name.");
        if (string.IsNullOrWhiteSpace(Version.VersionId))
            problems.Add("No Minecraft version was chosen.");
        if (!Version.IsSelectable && UserSuppliedArtifact is null)
            problems.Add($"Minecraft {Version.VersionId} cannot be created: {VanillaSupportPolicy.Describe(Version.Support)}.");
        if ((!Version.HasServerDownload || string.IsNullOrWhiteSpace(Version.ServerDownloadUrl)) && UserSuppliedArtifact is null)
            problems.Add("The chosen version has no official server download.");
        if (UserSuppliedArtifact is { } supplied)
        {
            if (!supplied.MinecraftVersion.Equals(Version.VersionId, StringComparison.OrdinalIgnoreCase))
                problems.Add("The supplied server JAR was reviewed for a different Minecraft version.");
            if (string.IsNullOrWhiteSpace(supplied.NativePath) || supplied.SizeBytes <= 0 || supplied.Sha256.Length != 64)
                problems.Add("The supplied server JAR evidence is incomplete.");
        }
        if (Version.RequiredJavaMajor is null)
            problems.Add("The Java version this needs was never established.");
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

    public bool IsReady => Problems().Count == 0;
}

/// <summary>The beginner's desired next networking step after a Vanilla server is created.</summary>
public enum VanillaNetworkingPreference
{
    FriendsOverInternet = 0,
    ThisNetworkOnly = 1,
    DecideLater = 2,
    ThisComputerOnly = 3,
    HomeNetwork = 4
}

public sealed record UserSuppliedServerArtifact
{
    public string NativePath { get; init; } = "";
    public string FileName { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public bool MatchesOfficialHash { get; init; }
    public string IdentityEvidence { get; init; } = "";
}

public static class VanillaNetworkingPreferencePolicy
{
    public static NetworkMode ToNetworkMode(VanillaNetworkingPreference preference) => preference switch
    {
        VanillaNetworkingPreference.FriendsOverInternet => NetworkMode.PortForwarding,
        VanillaNetworkingPreference.ThisNetworkOnly or VanillaNetworkingPreference.HomeNetwork => NetworkMode.HomeNetwork,
        VanillaNetworkingPreference.ThisComputerOnly => NetworkMode.ThisComputerOnly,
        _ => NetworkMode.ConfigureLater
    };
}

/// <summary>Shared parsing and validation for the Java server port shown by Create Server v2.</summary>
public static class ServerPortPolicy
{
    public const int DefaultPort = 25_565;

    public static (int? Port, string Error) Parse(string text)
    {
        if (!int.TryParse(text?.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var port))
            return (null, "Enter a whole-number port from 1 to 65535.");
        return Validate(port) is { } error ? (null, error) : (port, "");
    }

    public static string? Validate(int port) =>
        port is >= 1 and <= 65_535 ? null : "Choose a port from 1 to 65535.";
}

/// <summary>Asks the Agent for the official Vanilla version catalog.</summary>
/// <param name="IncludeSnapshots">True to resolve snapshot entries as well as releases.</param>
/// <param name="ForceRefresh">True to bypass a still-valid cache.</param>
public sealed record VanillaCatalogRequest(bool IncludeSnapshots = false, bool ForceRefresh = false);

/// <summary>Asks the Agent to begin a real Vanilla creation from an approved plan.</summary>
public sealed record BeginVanillaCreationRequest(VanillaCreationPlan Plan);

/// <summary>Asks the Agent where a server of this name would be created, and whether it may be.</summary>
/// <param name="ServerName">The name exactly as the user typed it.</param>
/// <param name="InstanceRoot">Where managed servers live. Empty means the standard managed root.</param>
public sealed record VanillaDestinationRequest(string ServerName, string InstanceRoot = "");

/// <summary>
/// The managed destination a name resolves to, and the destination policy's verdict on it.
/// </summary>
/// <remarks>
/// The folder identity is generated from the display name rather than being the display name, so a
/// name with spaces or punctuation still produces a folder Windows accepts. Nothing here creates or
/// reserves anything: it is the same deterministic answer the transaction will re-derive and re-check
/// immediately before it promotes anything.
/// </remarks>
public sealed record VanillaDestinationPreview
{
    public string ServerName { get; init; } = "";

    /// <summary>The generated folder name. Deterministic for a given display name.</summary>
    public string FolderName { get; init; } = "";

    public string InstanceRoot { get; init; } = "";
    public string CanonicalDestination { get; init; } = "";
    public CreationDestinationVerdict Verdict { get; init; }

    /// <summary>Plain language: what is true, and what to do about it.</summary>
    public string Message { get; init; } = "";

    /// <summary>True when creation may proceed against this destination right now.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>A readable summary for the review screen. Never a candidate id.</summary>
    public string Summary => string.IsNullOrEmpty(CanonicalDestination)
        ? "Not established"
        : CanonicalDestination;
}

/// <summary>Every Vanilla creation this Agent has record of in the current session, newest first.</summary>
/// <param name="Operations">One snapshot per operation, including finished ones.</param>
public sealed record VanillaCreationsResult(IReadOnlyList<InstallOperationSnapshot> Operations);
