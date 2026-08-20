using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChunkPilot.Core;

public enum ServerEdition
{
    Java,
    Bedrock
}

public enum QuickStartKind
{
    VanillaWithFriends,
    FasterVanilla,
    Modpack,
    PluginsAndMinigames,
    JavaBedrockCrossplay,
    BedrockDedicatedServer,
    ImportExistingServer,
    AdvancedCustomServer
}

public enum CatalogProvider
{
    Mojang,
    Paper,
    Purpur,
    Modrinth,
    CurseForge,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    Ftb,
    DirectHttps,
    LocalPackage
}

public enum CatalogContentType
{
    ServerSoftware,
    Modpack,
    Mod,
    Plugin,
    Datapack,
    ResourcePack,
    ServerUtility
}

public enum InstallationSupportState
{
    FullyAutomated,
    AutomatedWithReview,
    ManualPackageRequired,
    ClientOnly,
    Unsupported
}

public enum ClientRequirement
{
    None,
    Optional,
    MatchingPackRequired,
    Unknown
}

public enum NetworkMode
{
    ThisComputerOnly,
    HomeNetwork,
    PortForwarding,
    OfficialTunnel,
    ConfigureLater
}

public enum ApplicationExitKind
{
    Normal,
    Unexpected,
    WindowsShutdown
}

public enum LifecycleIntentKind
{
    None,
    ManualStart,
    ManualStop,
    SafeRestart,
    ScheduledRestart,
    UpdateRestart,
    CrashRecovery,
    ApplicationExit,
    WindowsShutdown
}

public enum ProcessControlState
{
    Stopped,
    RunningControlled,
    RunningDetached,
    UnknownExternalProcess
}

public enum AutostartMode
{
    Never,
    AgentStart,
    RestorePreviousRunningState,
    WindowsLoginWithDelay
}

public enum RuntimeHealth
{
    Unknown,
    Healthy,
    Unhealthy,
    InUse
}

public sealed record ServerRunningState(
    Guid ServerId,
    AutostartMode AutostartMode,
    bool WasRunning,
    LifecycleIntentKind LastIntent,
    DateTimeOffset UpdatedAt);

public enum AutomationTriggerKind
{
    ServerStarted,
    ServerReady,
    ServerStopped,
    ServerCrashed,
    PlayerJoined,
    FirstPlayerJoined,
    LastPlayerLeft,
    PlayerCountThreshold,
    ScheduledTime,
    BackupCompleted,
    BackupFailed,
    UpdateAvailable,
    LowDiskSpace,
    HighRam,
    ConsolePattern
}

public enum AutomationActionKind
{
    SendCommand,
    SendAnnouncement,
    Save,
    Backup,
    SafeRestart,
    StopAfterEmpty,
    StartAnotherServer,
    RunDiagnostics,
    ShowNotification,
    RecordActivity,
    Wait,
    ExternalProgram
}

public sealed record ServerCapabilityEvidence
{
    public ServerEdition Edition { get; init; } = ServerEdition.Java;
    public ServerEcosystem Ecosystem { get; init; }
    public bool HasManagedLaunchProfile { get; init; }
    public bool UsesScriptLaunch { get; init; }
    public bool UsesDirectJarLaunch { get; init; }
    public bool HasModsDirectory { get; init; }
    public bool HasPluginsDirectory { get; init; }
    public bool HasGeyser { get; init; }
    public bool HasFloodgate { get; init; }
    public bool HasViaVersion { get; init; }
    public bool HasRconConfiguration { get; init; }
    public bool HasQueryConfiguration { get; init; }
    public string DetectionDetail { get; init; } = "";
}

public sealed record ServerCapabilityProfile
{
    public Guid ServerId { get; init; }
    public ServerEdition Edition { get; init; }
    public string Software { get; init; } = "Unknown";
    public bool IsVanilla { get; init; }
    public bool IsPluginServer { get; init; }
    public bool IsModdedServer { get; init; }
    public bool IsHybridServer { get; init; }
    public bool SupportsPlugins { get; init; }
    public bool SupportsMods { get; init; }
    public bool SupportsDatapacks { get; init; }
    public bool SupportsServerResourcePacks { get; init; }
    public bool SupportsLiveWhitelistCommands { get; init; }
    public bool SupportsOperators { get; init; }
    public bool SupportsPlayerBans { get; init; }
    public bool SupportsIpBans { get; init; }
    public bool SupportsGamerules { get; init; }
    public bool SupportsRcon { get; init; }
    public bool SupportsQuery { get; init; }
    public bool SupportsManagedJava { get; init; }
    public bool SupportsServerSoftwareUpdate { get; init; }
    public bool SupportsFullModpackUpdate { get; init; }
    public bool SupportsAutomaticInstallation { get; init; }
    public bool SupportsManagedLaunchProfile { get; init; }
    public bool SupportsScriptLaunch { get; init; }
    public bool SupportsDirectJarLaunch { get; init; }
    public bool SupportsGeyser { get; init; }
    public bool SupportsFloodgate { get; init; }
    public bool SupportsViaVersion { get; init; }
    public bool SupportsWorldSwitching { get; init; }
    public bool RequiresMatchingClientPack { get; init; }
    public bool AllowsUnmodifiedClients { get; init; }
    public bool RequiresRestartForSettingChanges { get; init; }
    public bool SupportsLiveConfigurationCommand { get; init; }
    public bool SupportsAddonDependencyResolution { get; init; }
    public IReadOnlyDictionary<string, string> Evidence { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> UnavailableReasons { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public static class ServerCapabilityPolicy
{
    public static ServerCapabilityProfile Build(ServerDefinition definition, ServerCapabilityEvidence evidence)
    {
        var java = evidence.Edition == ServerEdition.Java;
        var plugins = evidence.Ecosystem is ServerEcosystem.Paper or ServerEcosystem.Purpur or
            ServerEcosystem.Spigot or ServerEcosystem.Bukkit or ServerEcosystem.Hybrid;
        var mods = evidence.Ecosystem is ServerEcosystem.Fabric or ServerEcosystem.Quilt or
            ServerEcosystem.Forge or ServerEcosystem.NeoForge or ServerEcosystem.Hybrid;
        var hybrid = evidence.Ecosystem == ServerEcosystem.Hybrid;
        var vanilla = evidence.Ecosystem == ServerEcosystem.Vanilla;
        var knownJava = java && evidence.Ecosystem != ServerEcosystem.Unknown;
        var evidenceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["edition"] = evidence.Edition.ToString(),
            ["software"] = evidence.Ecosystem.ToString(),
            ["launch"] = evidence.HasManagedLaunchProfile ? "Managed launch profile" :
                evidence.UsesScriptLaunch ? "Reviewed script launch" :
                evidence.UsesDirectJarLaunch ? "Direct JAR launch" : "Launch method undetected",
            ["detection"] = evidence.DetectionDetail
        };
        var unavailable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!java)
        {
            unavailable["managedJava"] = "Bedrock Dedicated Server does not use Java.";
            unavailable["mods"] = "Java mod loaders do not apply to Bedrock Dedicated Server.";
            unavailable["plugins"] = "Java plugin APIs do not apply to Bedrock Dedicated Server.";
        }
        if (!plugins)
            unavailable["plugins"] = evidence.Ecosystem == ServerEcosystem.Unknown
                ? "Plugin support was not detected."
                : $"{evidence.Ecosystem} does not expose a supported Java plugin API.";
        if (!mods)
            unavailable["mods"] = evidence.Ecosystem == ServerEcosystem.Unknown
                ? "Mod-loader support was not detected."
                : $"{evidence.Ecosystem} is not a supported mod-loader profile.";

        return new ServerCapabilityProfile
        {
            ServerId = definition.Id,
            Edition = evidence.Edition,
            Software = evidence.Ecosystem.ToString(),
            IsVanilla = vanilla,
            IsPluginServer = plugins,
            IsModdedServer = mods,
            IsHybridServer = hybrid,
            SupportsPlugins = plugins,
            SupportsMods = mods,
            SupportsDatapacks = knownJava,
            SupportsServerResourcePacks = knownJava,
            SupportsLiveWhitelistCommands = knownJava,
            SupportsOperators = knownJava,
            SupportsPlayerBans = knownJava,
            SupportsIpBans = knownJava,
            SupportsGamerules = knownJava,
            SupportsRcon = java && evidence.HasRconConfiguration,
            SupportsQuery = evidence.HasQueryConfiguration,
            SupportsManagedJava = java,
            SupportsServerSoftwareUpdate = knownJava,
            SupportsFullModpackUpdate = mods,
            SupportsAutomaticInstallation = definition.IsManaged,
            SupportsManagedLaunchProfile = evidence.HasManagedLaunchProfile,
            SupportsScriptLaunch = evidence.UsesScriptLaunch,
            SupportsDirectJarLaunch = evidence.UsesDirectJarLaunch,
            SupportsGeyser = plugins || evidence.Ecosystem == ServerEcosystem.Fabric,
            SupportsFloodgate = evidence.HasGeyser || plugins || evidence.Ecosystem == ServerEcosystem.Fabric,
            SupportsViaVersion = plugins,
            SupportsWorldSwitching = true,
            RequiresMatchingClientPack = mods,
            AllowsUnmodifiedClients = java && !mods,
            RequiresRestartForSettingChanges = true,
            SupportsLiveConfigurationCommand = knownJava,
            SupportsAddonDependencyResolution = plugins || mods,
            Evidence = evidenceMap,
            UnavailableReasons = unavailable
        };
    }
}

public sealed record QuickStartPreset
{
    public QuickStartKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string PlainLanguageSummary { get; init; } = "";
    public InstallSourceType SourceType { get; init; }
    public bool ManagedJava { get; init; }
    public bool WhitelistEnabled { get; init; }
    public bool OnlineMode { get; init; } = true;
    public bool DailyBackup { get; init; }
    public bool BackupBeforeUpdates { get; init; }
    public int MaxPlayers { get; init; } = 8;
    public NetworkMode NetworkMode { get; init; } = NetworkMode.ConfigureLater;
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> ReviewItems { get; init; } = [];

    public override string ToString() => Name;
}

public static class QuickStartPresetFactory
{
    public static QuickStartPreset Create(QuickStartKind kind, InstallSourceType fasterSoftware = InstallSourceType.Paper) =>
        kind switch
        {
            QuickStartKind.VanillaWithFriends => new()
            {
                Kind = kind,
                Name = "Vanilla With Friends",
                PlainLanguageSummary = "Official Minecraft, no client changes, private by default.",
                SourceType = InstallSourceType.Vanilla,
                ManagedJava = true,
                WhitelistEnabled = true,
                OnlineMode = true,
                DailyBackup = true,
                BackupBeforeUpdates = true,
                MaxPlayers = 8,
                NetworkMode = NetworkMode.HomeNetwork,
                Properties = Properties(("online-mode", "true"), ("white-list", "true"),
                    ("max-players", "8"), ("difficulty", "normal"), ("enable-rcon", "false"),
                    ("enable-query", "false")),
                ReviewItems =
                [
                    "Uses the official Vanilla server and accepts unmodified clients.",
                    "Enables online authentication and recommends a whitelist.",
                    "Creates a daily backup schedule and backs up before updates.",
                    "Uses a private managed Java runtime without changing PATH or JAVA_HOME."
                ]
            },
            QuickStartKind.FasterVanilla => new()
            {
                Kind = kind,
                Name = "Faster Vanilla",
                PlainLanguageSummary = "A performance-oriented server with clearly explained behavior differences.",
                SourceType = fasterSoftware is InstallSourceType.Purpur or InstallSourceType.Fabric
                    ? fasterSoftware : InstallSourceType.Paper,
                ManagedJava = true,
                WhitelistEnabled = true,
                DailyBackup = true,
                BackupBeforeUpdates = true,
                Properties = Properties(("online-mode", "true"), ("white-list", "true"),
                    ("max-players", "12"), ("view-distance", "8"), ("simulation-distance", "6")),
                ReviewItems =
                [
                    $"{fasterSoftware} can improve performance but may change some Vanilla mechanics.",
                    fasterSoftware == InstallSourceType.Fabric
                        ? "Fabric performance mods require review; clients normally remain unmodified for server-only mods."
                        : "Paper/Purpur supports plugins and unmodified clients.",
                    "Conservative memory and distance settings are used."
                ]
            },
            QuickStartKind.Modpack => new()
            {
                Kind = kind,
                Name = "Modpack Server",
                PlainLanguageSummary = "An exact official server-pack version with matching client instructions.",
                SourceType = InstallSourceType.CustomPackage,
                ManagedJava = true,
                WhitelistEnabled = true,
                DailyBackup = true,
                BackupBeforeUpdates = true,
                ReviewItems =
                [
                    "Only a provider-confirmed server package can be installed automatically.",
                    "Friends normally need the same client pack and exact version.",
                    "A full backup is created before first start and every pack update."
                ]
            },
            QuickStartKind.PluginsAndMinigames => new()
            {
                Kind = kind,
                Name = "Plugins and Minigames",
                PlainLanguageSummary = "Paper or Purpur with Vanilla-client plugin support.",
                SourceType = InstallSourceType.Paper,
                ManagedJava = true,
                WhitelistEnabled = true,
                DailyBackup = true,
                BackupBeforeUpdates = true,
                ReviewItems =
                [
                    "Players can normally join with an unmodified Vanilla client.",
                    "Plugins are checked against the selected Minecraft and server API versions."
                ]
            },
            QuickStartKind.JavaBedrockCrossplay => new()
            {
                Kind = kind,
                Name = "Java and Bedrock Crossplay",
                PlainLanguageSummary = "Paper with reviewed Geyser/Floodgate configuration.",
                SourceType = InstallSourceType.Paper,
                ManagedJava = true,
                WhitelistEnabled = true,
                DailyBackup = true,
                BackupBeforeUpdates = true,
                ReviewItems =
                [
                    "Java uses TCP; Bedrock normally uses UDP on a separate port.",
                    "Geyser and Floodgate downloads must be verified and their authentication effect reviewed."
                ]
            },
            QuickStartKind.BedrockDedicatedServer => new()
            {
                Kind = kind,
                Name = "Bedrock Dedicated Server",
                PlainLanguageSummary = "Official Windows Bedrock server with Bedrock-only controls.",
                SourceType = InstallSourceType.CustomPackage,
                ManagedJava = false,
                WhitelistEnabled = true,
                DailyBackup = true,
                BackupBeforeUpdates = true,
                ReviewItems =
                [
                    "Java, JVM, Java mods, and Java plugins do not apply.",
                    "Bedrock networking uses UDP and Bedrock-specific properties."
                ]
            },
            QuickStartKind.ImportExistingServer => new()
            {
                Kind = kind,
                Name = "Import Existing Server",
                PlainLanguageSummary = "Read-only detection before registering the existing folder by reference.",
                SourceType = InstallSourceType.ExistingPackageFolder,
                ReviewItems = ["ChunkPilot will not move or rewrite the imported folder during detection."]
            },
            _ => new()
            {
                Kind = kind,
                Name = "Advanced Custom Server",
                PlainLanguageSummary = "Full control over software, Java, launch, memory, networking, world, and content.",
                SourceType = InstallSourceType.CustomPackage,
                ReviewItems = ["Every effective launch and file decision is shown before installation."]
            }
        };

    private static IReadOnlyDictionary<string, string> Properties(
        params (string Key, string Value)[] values) =>
        values.ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
}

public sealed record CatalogVersion
{
    public string VersionId { get; init; } = "";
    public string VersionName { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public ReleaseChannel ReleaseChannel { get; init; } = ReleaseChannel.Stable;
    public DateTimeOffset? PublishedAt { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public long? SizeBytes { get; init; }
    public string Changelog { get; init; } = "";
    public bool HasServerPackage { get; init; }
    public int RequiredJavaMajor { get; init; }
}

public sealed record CatalogItem
{
    public CatalogProvider Provider { get; init; }
    public CatalogContentType ContentType { get; init; }
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Summary { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string ProjectUrl { get; init; } = "";
    public long? DownloadCount { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public ClientRequirement ClientRequirement { get; init; } = ClientRequirement.Unknown;
    public InstallationSupportState InstallationSupport { get; init; }
    public int RecommendedRamMinMb { get; init; }
    public int RecommendedRamMaxMb { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<CatalogVersion> Versions { get; init; } = [];
}

public sealed record CatalogQuery
{
    public string Search { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public CatalogProvider? Provider { get; init; }
    public ReleaseChannel MaximumChannel { get; init; } = ReleaseChannel.Stable;
    public bool ServerPackRequired { get; init; } = true;
    public bool ExcludeClientOnly { get; init; } = true;
    public bool? AllowsVanillaClients { get; init; }
    public string Category { get; init; } = "";
    public int Limit { get; init; } = 50;
    public CatalogSort Sort { get; init; } = CatalogSort.Updated;
}

public enum CatalogSort
{
    Relevance,
    Downloads,
    Follows,
    Newest,
    Updated
}

public sealed record CatalogProviderStatus(
    CatalogProvider Provider,
    bool Available,
    string Detail);

public enum CatalogLoadState
{
    Ready,
    Empty,
    OfflineCache,
    AuthenticationRequired,
    RateLimited,
    Failed
}

public sealed record CatalogBrowseResult
{
    public CatalogProvider Provider { get; init; }
    public CatalogLoadState State { get; init; }
    public IReadOnlyList<CatalogItem> Items { get; init; } = [];
    public string Detail { get; init; } = "";
    public string FailedStage { get; init; } = "";
    public DateTimeOffset? RetrievedAt { get; init; }
    public bool FromCache { get; init; }
    public bool Stale { get; init; }
}

public sealed record LoaderInstallPlan
{
    public InstallSourceType Loader { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string InstallerArgument { get; init; } = "";
    public string ExpectedLaunchFile { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public bool RunsInstaller { get; init; }
}

public sealed record LoaderInstallResult
{
    public string LaunchFile { get; init; } = "";
    public string ArgumentsFile { get; init; } = "";
    public string InstallerOutput { get; init; } = "";
    public string DownloadSha256 { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    public string ArtifactUrl { get; init; } = "";
}

public sealed record ManagedJavaPackage
{
    public string Vendor { get; init; } = "Eclipse Temurin";
    public int MajorVersion { get; init; }
    public string Version { get; init; } = "";
    public string Architecture { get; init; } = "x64";
    public string DownloadUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long? SizeBytes { get; init; }
}

public static class CatalogPolicy
{
    public static IReadOnlyList<CatalogItem> Filter(
        IEnumerable<CatalogItem> items,
        CatalogQuery query)
    {
        var filtered = items.Where(item =>
            (!query.ExcludeClientOnly || item.InstallationSupport != InstallationSupportState.ClientOnly) &&
            (!query.ServerPackRequired || item.Versions.Any(version => version.HasServerPackage)) &&
            (query.Provider is null || item.Provider == query.Provider) &&
            (string.IsNullOrWhiteSpace(query.Search) ||
             item.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase) ||
             item.Summary.Contains(query.Search, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.Category) ||
             item.Categories.Contains(query.Category, StringComparer.OrdinalIgnoreCase)) &&
            (query.AllowsVanillaClients is null ||
             (query.AllowsVanillaClients.Value
                 ? item.ClientRequirement is ClientRequirement.None or ClientRequirement.Optional
                 : item.ClientRequirement == ClientRequirement.MatchingPackRequired)));

        var shaped = filtered.Select(item => item with
            {
                Versions = item.Versions.Where(version =>
                        (string.IsNullOrWhiteSpace(query.MinecraftVersion) ||
                         version.MinecraftVersion.Equals(query.MinecraftVersion, StringComparison.OrdinalIgnoreCase)) &&
                        (string.IsNullOrWhiteSpace(query.Loader) ||
                         version.Loader.Equals(query.Loader, StringComparison.OrdinalIgnoreCase)) &&
                        ChannelAllowed(version.ReleaseChannel, query.MaximumChannel))
                    .OrderByDescending(version => version.PublishedAt)
                    .ToArray()
            })
            .Where(item => !query.ServerPackRequired || item.Versions.Any(version => version.HasServerPackage));
        var ordered = query.Sort switch
        {
            CatalogSort.Downloads => shaped.OrderByDescending(item => item.DownloadCount),
            CatalogSort.Newest => shaped.OrderByDescending(item =>
                item.Versions.Select(version => version.PublishedAt).Max()),
            CatalogSort.Relevance => shaped,
            _ => shaped.OrderByDescending(item => item.UpdatedAt)
        };
        return ordered
            .Take(Math.Clamp(query.Limit, 1, 100))
            .ToArray();
    }

    public static CatalogVersion? SelectDefaultVersion(CatalogItem item, CatalogQuery query)
    {
        var filtered = Filter([item], query);
        return filtered.Count == 0 ? null : filtered[0].Versions
            .OrderBy(version => version.ReleaseChannel)
            .ThenByDescending(version => version.PublishedAt)
            .FirstOrDefault();
    }

    private static bool ChannelAllowed(ReleaseChannel channel, ReleaseChannel maximum) =>
        channel <= maximum;
}

public sealed record ManagedJavaRuntime
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Vendor { get; init; } = "Unknown";
    public string Version { get; init; } = "Unknown";
    public int MajorVersion { get; init; }
    public string Architecture { get; init; } = "Unknown";
    public string JavaPath { get; init; } = "";
    public string InstallationRoot { get; init; } = "";
    public string SourceUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public bool IsManaged { get; init; }
    public RuntimeHealth Health { get; init; }
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastHealthCheckAt { get; init; }
}

/// <summary>
/// ChunkPilot's persisted assignment of a Java runtime to one server. A non-null managed runtime id
/// can be joined to <see cref="ManagedJavaRuntime"/> to prove a stopped server's executable target.
/// </summary>
public sealed record JavaRuntimeAssignment
{
    public Guid ServerId { get; init; }
    public Guid? RuntimeId { get; init; }
    public string JavaPath { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record JavaRuntimeRequirement
{
    public int MinimumMajor { get; init; }
    public int? MaximumMajor { get; init; }
    public bool Require64Bit { get; init; } = true;
    public string Evidence { get; init; } = "";
}

public static class JavaRuntimePolicy
{
    public static int RequiredMajorForMinecraft(string version)
    {
        return TryRequiredMajorForMinecraft(version) ?? 21;
    }

    /// <summary>
    /// Returns a curated historical fallback only when the version id has a numeric release family.
    /// Callers which make support promises must preserve null rather than guessing for snapshots or
    /// historical ids that official metadata does not resolve.
    /// </summary>
    public static int? TryRequiredMajorForMinecraft(string version)
    {
        if (version.Equals("b1.8", StringComparison.OrdinalIgnoreCase) ||
            version.Equals("b1.8.1", StringComparison.OrdinalIgnoreCase))
            return 8;
        if (!Version.TryParse(NormalizeMinecraftVersion(version), out var parsed))
            return null;
        if (parsed >= new Version(1, 20, 5))
            return 21;
        if (parsed >= new Version(1, 18))
            return 17;
        if (parsed >= new Version(1, 17))
            return 16;
        return parsed.Major == 1 ? 8 : null;
    }

    public static int JavaMajorForClassFile(ushort classMajor) =>
        classMajor >= 45 ? classMajor - 44 : 0;

    public static ManagedJavaRuntime? Select(
        IEnumerable<ManagedJavaRuntime> runtimes,
        JavaRuntimeRequirement requirement,
        string explicitPath = "")
    {
        var candidates = runtimes.Where(runtime =>
            runtime.Health is RuntimeHealth.Healthy or RuntimeHealth.InUse &&
            runtime.MajorVersion >= requirement.MinimumMajor &&
            (requirement.MaximumMajor is null || runtime.MajorVersion <= requirement.MaximumMajor) &&
            (!requirement.Require64Bit ||
             runtime.Architecture.Contains("64", StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return candidates.FirstOrDefault(runtime =>
                Path.GetFullPath(runtime.JavaPath).Equals(Path.GetFullPath(explicitPath),
                    StringComparison.OrdinalIgnoreCase));
        return candidates.OrderBy(runtime => runtime.MajorVersion)
            .ThenByDescending(runtime => runtime.IsManaged)
            .FirstOrDefault();
    }

    private static string NormalizeMinecraftVersion(string value)
    {
        var core = value.Split(['-', '+', ' '], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var parts = core.Split('.');
        return parts.Length switch
        {
            1 => core + ".0",
            2 => core + ".0",
            _ => string.Join('.', parts.Take(3))
        };
    }
}

public sealed record NetworkConfiguration
{
    public Guid ServerId { get; init; }
    public NetworkMode Mode { get; init; }
    public int JavaPort { get; init; } = 25565;
    public int? BedrockPort { get; init; }
    public string LanAddress { get; init; } = "";
    public string PublicAddress { get; init; } = "";
    public string TunnelProvider { get; init; } = "";
    public string TunnelAddress { get; init; } = "";
    public bool PublicAddressExternallyConfirmed { get; init; }
}

public sealed record ConnectionReadiness
{
    public FindingSeverity ServerRunning { get; init; }
    public FindingSeverity PortListeningLocally { get; init; }
    public string LocalAddress { get; init; } = "";
    public string LanAddress { get; init; } = "";
    public FindingSeverity FirewallAssessment { get; init; }
    public string PublicAddress { get; init; } = "";
    public FindingSeverity TunnelState { get; init; }
    public FindingSeverity ExternalProbe { get; init; }
    public string RecommendedNextStep { get; init; } = "";
}

public static class NetworkPolicy
{
    public static string CopyPublicAddress(NetworkConfiguration configuration)
    {
        if (!configuration.PublicAddressExternallyConfirmed ||
            string.IsNullOrWhiteSpace(configuration.PublicAddress))
            throw new InvalidOperationException("A public address has not been independently confirmed.");
        if (configuration.PublicAddress.Equals(configuration.LanAddress, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The LAN address cannot be presented as a confirmed public address.");
        return configuration.PublicAddress;
    }

    public static IReadOnlyList<string> Guidance(NetworkMode mode, bool bedrock) =>
        mode switch
        {
            NetworkMode.ThisComputerOnly =>
                ["Use localhost. No router or firewall changes are needed."],
            NetworkMode.HomeNetwork =>
                ["Use the LAN IPv4 address.", "Verify the local listening port.",
                    "Approve a Windows Firewall rule only when you want other devices on this network to connect."],
            NetworkMode.PortForwarding =>
                [$"Forward the server port using {(bedrock ? "UDP" : "TCP")}.",
                    "Reserve the computer's LAN address in the router.",
                    "A local listening test does not prove public reachability.",
                    "If the WAN address is private or shared, ask the ISP about CGNAT."],
            NetworkMode.OfficialTunnel =>
                ["Use only an explicitly linked official tunnel provider.", "The assigned tunnel address is separate from the LAN address."],
            _ => ["Networking is not configured. The server remains local until you choose a method."]
        };
}

public sealed record UnifiedPlayerAccess
{
    public string Name { get; init; } = "";
    public Guid? Uuid { get; init; }
    public string IpAddress { get; init; } = "";
    public bool Whitelisted { get; init; }
    public bool Operator { get; init; }
    public bool PlayerBanned { get; init; }
    public bool IpBanned { get; init; }
    public string BanReason { get; init; } = "";
    /// <summary>Who the ban file records as having issued the ban. Empty when the file does not say.</summary>
    public string BanSource { get; init; } = "";
    /// <summary>When the ban file records the ban as created. Null when the file does not say.</summary>
    public DateTimeOffset? BanCreatedAt { get; init; }
    public DateTimeOffset? BanExpiresAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public bool Online { get; init; }
}

public static class AccessControlPolicy
{
    public static string ExplainJoin(UnifiedPlayerAccess player, bool whitelistEnabled)
    {
        if (player.PlayerBanned)
            return $"Blocked by player ban{Reason(player.BanReason)}.";
        if (player.IpBanned)
            return $"Blocked by IP ban{Reason(player.BanReason)}.";
        if (whitelistEnabled && !player.Whitelisted)
            return "Blocked because the whitelist is enabled and this player is not listed.";
        return "Can join, subject to normal account authentication and server availability.";
    }

    private static string Reason(string value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $": {value}";
}

/// <summary>
/// One game rule ChunkPilot can present as a real control.
/// </summary>
/// <param name="Name">The exact gamerule key, as Minecraft spells it. Always shown somewhere.</param>
/// <param name="Label">Plain-language name for the control.</param>
/// <param name="Kind">Which control the value needs.</param>
/// <param name="DefaultValue">Vanilla's default, used only to describe the rule, never as its value.</param>
/// <param name="MinimumMinecraftVersion">First version that accepts the rule.</param>
/// <param name="Description">What the rule does, in one sentence.</param>
/// <param name="Minimum">Inclusive lower bound for an integer rule.</param>
/// <param name="Maximum">Inclusive upper bound for an integer rule, as ChunkPilot will set it.</param>
public sealed record GameruleDefinition(
    string Name,
    string Label,
    GameruleValueKind Kind,
    string DefaultValue,
    string MinimumMinecraftVersion,
    string Description,
    int Minimum = 0,
    int Maximum = 0);

/// <summary>
/// The game rules ChunkPilot offers, and what may be claimed about them.
/// </summary>
/// <remarks>
/// <para>
/// This list is a floor, not a promise of completeness: Minecraft adds rules, and ChunkPilot will not
/// present a control for a rule it cannot establish for the exact server in front of it. Version
/// gating removes rules the selected version does not have; the values themselves are only ever read
/// from the running server, never assumed from the defaults recorded here.
/// </para>
/// </remarks>
public static class GamerulePolicy
{
    private static readonly GameruleDefinition[] Rules =
    [
        new("keepInventory", "Keep items on death", GameruleValueKind.Boolean, "false", "1.4.2",
            "Players keep their inventory and experience when they die."),
        new("doDaylightCycle", "Day and night cycle", GameruleValueKind.Boolean, "true", "1.6.1",
            "Time of day advances."),
        new("doWeatherCycle", "Weather changes", GameruleValueKind.Boolean, "true", "1.11",
            "Rain, thunder and clear skies change on their own."),
        new("doFireTick", "Fire spreads", GameruleValueKind.Boolean, "true", "1.4.2",
            "Fire spreads to nearby blocks and burns out."),
        new("mobGriefing", "Mobs can change blocks", GameruleValueKind.Boolean, "true", "1.4.2",
            "Creepers, endermen and other mobs can alter the world."),
        new("doMobSpawning", "Mobs spawn naturally", GameruleValueKind.Boolean, "true", "1.4.2",
            "Hostile and passive mobs appear on their own."),
        new("doInsomnia", "Phantoms appear", GameruleValueKind.Boolean, "true", "1.15",
            "Phantoms spawn for players who have not slept."),
        new("announceAdvancements", "Announce advancements", GameruleValueKind.Boolean, "true", "1.12",
            "Advancements are announced in chat."),
        new("commandBlockOutput", "Command block output", GameruleValueKind.Boolean, "true", "1.4.2",
            "Command blocks report what they did in chat."),
        new("showDeathMessages", "Death messages", GameruleValueKind.Boolean, "true", "1.8",
            "Deaths are announced in chat."),
        new("doImmediateRespawn", "Respawn immediately", GameruleValueKind.Boolean, "false", "1.15",
            "Players respawn without the death screen."),
        new("fallDamage", "Fall damage", GameruleValueKind.Boolean, "true", "1.15",
            "Players take damage from falling."),
        new("randomTickSpeed", "Random tick speed", GameruleValueKind.WholeNumber, "3", "1.8",
            "How often blocks grow, melt and decay. Higher values cost performance.", 0, 100),
        new("spawnRadius", "Spawn radius", GameruleValueKind.WholeNumber, "10", "1.9",
            "How far from the spawn point players can appear.", 0, 128),
        new("playersSleepingPercentage", "Sleeping players needed", GameruleValueKind.WholeNumber, "100", "1.17",
            "Percentage of players who must sleep to skip the night.", 0, 100),
        new("maxEntityCramming", "Entity cramming limit", GameruleValueKind.WholeNumber, "24", "1.11",
            "How many entities may occupy one block before they take damage.", 0, 128)
    ];

    /// <summary>
    /// True when the rule names recorded here belong to the given Minecraft version's vocabulary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every name in this table was recorded against the <c>1.x</c> release line, and every one of
    /// them is dated by a <c>1.x</c> introduction version. Minecraft 26.x renamed the vocabulary
    /// wholesale: it rejects all of them, including the query form. ChunkPilot therefore has no
    /// evidence that any rule it knows exists on a version outside that line, and says so instead of
    /// finding out by sending sixteen commands into the user's Console and reading the errors.
    /// </para>
    /// <para>
    /// This is a claim about ChunkPilot's own table, not about the server. When the table is
    /// re-recorded for a later version scheme, this gate is what changes with it.
    /// </para>
    /// </remarks>
    public static bool CarriesRuleNamesFor(string minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion))
            return false;
        var head = minecraftVersion.Split('-')[0];
        return Version.TryParse(head, out var parsed) && parsed.Major == KnownReleaseLine;
    }

    /// <summary>The release line every recorded rule name belongs to.</summary>
    private const int KnownReleaseLine = 1;

    /// <summary>
    /// The rules the given Minecraft version accepts.
    /// </summary>
    /// <remarks>
    /// Empty for a version outside the recorded release line, which is what keeps discovery from
    /// probing a server whose rule names ChunkPilot does not have.
    /// </remarks>
    public static IReadOnlyList<GameruleDefinition> Supported(string minecraftVersion) =>
        !CarriesRuleNamesFor(minecraftVersion)
            ? []
            : Rules.Where(rule => CompareMinecraft(minecraftVersion, rule.MinimumMinecraftVersion) >= 0).ToArray();

    /// <summary>The definition for one key, when ChunkPilot knows it.</summary>
    public static GameruleDefinition? Find(string name) =>
        Rules.FirstOrDefault(rule => rule.Name.Equals(name, StringComparison.Ordinal));

    /// <summary>
    /// The reason a value may not be set, or null when it may.
    /// </summary>
    /// <remarks>
    /// Rejects out-of-range integers and unparseable booleans rather than letting the server refuse a
    /// command the UI already reported as sent.
    /// </remarks>
    public static string? Validate(string name, string value)
    {
        var rule = Find(name);
        if (rule is null)
            return $"{name} is not a game rule ChunkPilot can set.";
        if (rule.Kind == GameruleValueKind.Boolean)
        {
            return bool.TryParse(value, out _)
                ? null
                : $"{rule.Name} is either true or false.";
        }
        if (!int.TryParse(value, out var number))
            return $"{rule.Name} takes a whole number.";
        if (number < rule.Minimum || number > rule.Maximum)
            return $"{rule.Name} must be between {rule.Minimum} and {rule.Maximum}.";
        return null;
    }

    /// <summary>
    /// Validates the command-safe shape of a rule ChunkPilot does not yet carry in its curated table.
    /// The running server remains the authority on whether the name and value exist for that version.
    /// </summary>
    public static string? ValidateCustom(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || !CustomRuleNamePattern.IsMatch(name))
            return "Enter an exact game rule name using letters, numbers, dots, colons, underscores or hyphens.";
        if (string.IsNullOrWhiteSpace(value) || !CustomRuleValuePattern.IsMatch(value))
            return "Enter one command-safe value without spaces.";
        return null;
    }

    private static readonly System.Text.RegularExpressions.Regex CustomRuleNamePattern = new(
        @"^[A-Za-z][A-Za-z0-9_.:-]{0,63}$",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex CustomRuleValuePattern = new(
        @"^[A-Za-z0-9_.:+-]{1,64}$",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The rule a console line reports as not understood, or null when the line is something else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A server's refusal is evidence. Brigadier echoes the command it could not parse with a caret at
    /// the point of failure — <c>gamerule keepInventory&lt;--[HERE]</c> — so a rejected rule names
    /// itself, and ChunkPilot can stop offering a control for a rule that server will not accept.
    /// </para>
    /// <para>
    /// This is why the rule list is a floor rather than a promise. Minecraft 26.2 rejects every rule
    /// name recorded here, so on that version ChunkPilot establishes none of them and says so, instead
    /// of showing switches that would fail on use.
    /// </para>
    /// </remarks>
    public static string? ParseRejectedRule(string consoleLine)
    {
        var name = ParseRejectedRuleName(consoleLine);
        return name is not null && Find(name) is not null ? name : null;
    }

    /// <summary>Returns any syntactically valid rule name rejected by the running server.</summary>
    public static string? ParseRejectedRuleName(string consoleLine)
    {
        if (string.IsNullOrWhiteSpace(consoleLine))
            return null;
        var payload = ConsolePayload(consoleLine);
        if (!payload.StartsWith("gamerule ", StringComparison.OrdinalIgnoreCase))
            return null;
        var match = RejectedRulePattern.Match(payload);
        if (!match.Success)
            return null;
        return match.Groups["name"].Value;
    }

    private static readonly System.Text.RegularExpressions.Regex RejectedRulePattern = new(
        @"gamerule\s+(?<name>[A-Za-z][A-Za-z0-9_.:-]*)(\s+\S+)?<--\[HERE\]",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Parses the reply to <c>gamerule &lt;name&gt;</c>, or null when the line is something else.</summary>
    /// <remarks>
    /// Vanilla answers "Gamerule keepInventory is currently set to: false". Matching the reply is what
    /// lets the page show the server's own value instead of a default it hoped for.
    /// </remarks>
    public static (string Name, string Value)? ParseReportedValue(string consoleLine)
    {
        var parsed = ParseReportedValueAny(consoleLine);
        return parsed is { } result && Find(result.Name) is not null ? result : null;
    }

    /// <summary>Parses a server-reported value without requiring the name in the curated table.</summary>
    public static (string Name, string Value)? ParseReportedValueAny(string consoleLine)
    {
        if (string.IsNullOrWhiteSpace(consoleLine))
            return null;
        var payload = ConsolePayload(consoleLine);
        if (!payload.StartsWith("Gamerule ", StringComparison.OrdinalIgnoreCase))
            return null;
        const string marker = "is currently set to:";
        var markerIndex = payload.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
            return null;
        var head = payload[..markerIndex];
        var value = payload[(markerIndex + marker.Length)..].Trim();
        if (value.Length == 0)
            return null;
        var name = head.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrEmpty(name) || !CustomRuleNamePattern.IsMatch(name))
            return null;
        return (name, value);
    }

    /// <summary>Removes the standard timestamp/thread prefix but leaves chat prefixes intact.</summary>
    private static string ConsolePayload(string consoleLine)
    {
        var prefixEnd = consoleLine.LastIndexOf("]:", StringComparison.Ordinal);
        return (prefixEnd >= 0 ? consoleLine[(prefixEnd + 2)..] : consoleLine).Trim();
    }

    private static int CompareMinecraft(string left, string right)
    {
        _ = Version.TryParse(left.Split('-')[0], out var a);
        _ = Version.TryParse(right, out var b);
        return (a ?? new Version()).CompareTo(b ?? new Version());
    }
}

public sealed record CrossplayConfiguration
{
    public Guid ServerId { get; init; }
    public bool GeyserEnabled { get; init; }
    public bool FloodgateEnabled { get; init; }
    public bool ViaVersionEnabled { get; init; }
    public int BedrockPort { get; init; } = 19132;
    public string AuthenticationMode { get; init; } = "online";
    public IReadOnlyList<string> OwnedFiles { get; init; } = [];
    public IReadOnlyDictionary<string, string> InstalledVersions { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public enum CrossplayPackageKind
{
    Geyser,
    Floodgate,
    ViaVersion
}

public sealed record CrossplayPackage
{
    public CrossplayPackageKind Kind { get; init; }
    public string Version { get; init; } = "";
    public string Platform { get; init; } = "";
    public string FileName { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Sha512 { get; init; } = "";
}

public sealed record CrossplayInstallResult
{
    public CrossplayConfiguration Configuration { get; init; } = new();
    public Guid BackupId { get; init; }
    public bool RestartRequired { get; init; }
    public string Message { get; init; } = "";
}

public sealed record DatapackInventoryItem
{
    public string ItemId { get; init; } = "";
    public Guid ServerId { get; init; }
    public string WorldName { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public int PackFormat { get; init; }
    public CompatibilityState Compatibility { get; init; }
    public string Sha256 { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ResourcePackConfiguration
{
    public Guid ServerId { get; init; }
    public string Url { get; init; } = "";
    public string Sha1 { get; init; } = "";
    public bool Required { get; init; }
    public string Prompt { get; init; } = "";
}

public static class CrossplayPolicy
{
    public static IReadOnlyList<string> Validate(
        ServerCapabilityProfile capabilities,
        CrossplayConfiguration configuration,
        IReadOnlyCollection<int>? occupiedPorts = null)
    {
        var errors = new List<string>();
        if (capabilities.Edition != ServerEdition.Java)
            errors.Add("Geyser crossplay is installed on a compatible Java server, not Bedrock Dedicated Server.");
        if (configuration.GeyserEnabled && !capabilities.SupportsGeyser)
            errors.Add("The selected server does not have a supported Geyser installation path.");
        if (configuration.FloodgateEnabled && !configuration.GeyserEnabled)
            errors.Add("Floodgate requires Geyser.");
        if (configuration.ViaVersionEnabled && !capabilities.SupportsViaVersion)
            errors.Add("ViaVersion requires a supported plugin server.");
        if (configuration.AuthenticationMode is not ("online" or "floodgate"))
            errors.Add("Crossplay authentication must be online or floodgate.");
        if (configuration.AuthenticationMode == "floodgate" && !configuration.FloodgateEnabled)
            errors.Add("Floodgate authentication requires Floodgate.");
        if (configuration.BedrockPort is < 1 or > 65535)
            errors.Add("The Bedrock UDP port must be between 1 and 65535.");
        if (occupiedPorts?.Contains(configuration.BedrockPort) == true)
            errors.Add($"UDP port {configuration.BedrockPort} is already assigned.");
        return errors;
    }
}

public sealed record AutomationStep
{
    public AutomationActionKind Action { get; init; }
    public string Value { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 30;
}

public sealed record ExternalProgramAction
{
    public string Executable { get; init; } = "";
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = "";
}

public sealed record AutomationRecipe
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public string Name { get; init; } = "";
    public AutomationTriggerKind Trigger { get; init; }
    public string TriggerValue { get; init; } = "";
    public IReadOnlyList<AutomationStep> Actions { get; init; } = [];
    public bool Enabled { get; init; } = true;
    public bool ExternalProgramsApproved { get; init; }
}

public static class AutomationPolicy
{
    public static IReadOnlyList<string> Validate(AutomationRecipe recipe)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(recipe.Name))
            errors.Add("Recipe name is required.");
        if (recipe.Actions.Count == 0)
            errors.Add("At least one action is required.");
        if (recipe.Actions.Count > 20)
            errors.Add("A recipe is limited to 20 actions.");
        foreach (var action in recipe.Actions)
        {
            if (action.TimeoutSeconds is < 1 or > 3600)
                errors.Add($"{action.Action} timeout must be between 1 and 3600 seconds.");
            if (action.Action == AutomationActionKind.ExternalProgram && !recipe.ExternalProgramsApproved)
                errors.Add("External program execution is disabled until the exact executable and arguments are approved.");
            if (action.Action == AutomationActionKind.ExternalProgram && recipe.ExternalProgramsApproved)
            {
                try
                {
                    var specification = JsonSerializer.Deserialize<ExternalProgramAction>(
                        action.Value, ProtocolJson.Options);
                    if (specification is null ||
                        !Path.IsPathFullyQualified(specification.Executable) ||
                        specification.Arguments.Count > 100)
                        errors.Add("External program requires an absolute executable path and at most 100 exact arguments.");
                }
                catch (JsonException)
                {
                    errors.Add("External program details must be valid structured JSON.");
                }
            }
            if (action.Action == AutomationActionKind.Wait &&
                (!int.TryParse(action.Value, out var seconds) || seconds is < 1 or > 3600))
                errors.Add("Wait requires a duration from 1 to 3600 seconds.");
        }
        return errors;
    }
}

public static class AutomationRecipeFactory
{
    public static IReadOnlyList<AutomationRecipe> BuiltIns(Guid serverId) =>
    [
        Recipe(serverId, "Backup when last player leaves", AutomationTriggerKind.LastPlayerLeft,
            new AutomationStep { Action = AutomationActionKind.Backup }),
        Recipe(serverId, "Stop empty server after 30 minutes", AutomationTriggerKind.LastPlayerLeft,
            new AutomationStep { Action = AutomationActionKind.StopAfterEmpty, Value = "30", TimeoutSeconds = 1_900 }),
        Recipe(serverId, "Daily restart with five-minute warning", AutomationTriggerKind.ScheduledTime,
            new AutomationStep { Action = AutomationActionKind.SendAnnouncement,
                Value = "Server restarts in five minutes.", TimeoutSeconds = 30 },
            new AutomationStep { Action = AutomationActionKind.Wait, Value = "300", TimeoutSeconds = 310 },
            new AutomationStep { Action = AutomationActionKind.SafeRestart, TimeoutSeconds = 600 }),
        Recipe(serverId, "Backup before restart", AutomationTriggerKind.ScheduledTime,
            new AutomationStep { Action = AutomationActionKind.Backup, TimeoutSeconds = 600 },
            new AutomationStep { Action = AutomationActionKind.SafeRestart, TimeoutSeconds = 600 }),
        Recipe(serverId, "Notify on player join", AutomationTriggerKind.PlayerJoined,
            new AutomationStep { Action = AutomationActionKind.ShowNotification }),
        Recipe(serverId, "Warn when disk space is low", AutomationTriggerKind.LowDiskSpace,
            new AutomationStep { Action = AutomationActionKind.RecordActivity }),
        Recipe(serverId, "Bounded crash recovery", AutomationTriggerKind.ServerCrashed,
            new AutomationStep { Action = AutomationActionKind.RecordActivity })
    ];

    private static AutomationRecipe Recipe(
        Guid serverId,
        string name,
        AutomationTriggerKind trigger,
        params AutomationStep[] actions) => new()
        {
            ServerId = serverId,
            Name = name,
            Trigger = trigger,
            Actions = actions,
            Enabled = false
        };
}

public sealed record ProcessIdentity
{
    public Guid ServerId { get; init; }
    public int ProcessId { get; init; }
    public DateTimeOffset ProcessStartTime { get; init; }
    /// <summary>Exact raw Windows creation FILETIME. Zero means a legacy/unprovable record.</summary>
    public long ProcessCreationTicks { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string CommandSignature { get; init; } = "";
    public int? ParentProcessId { get; init; }
    public ProcessControlState ControlState { get; init; }
    public DateTimeOffset RecordedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record ObservedProcessIdentity
{
    public int ProcessId { get; init; }
    public DateTimeOffset ProcessStartTime { get; init; }
    public long ProcessCreationTicks { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public string CommandSignature { get; init; } = "";
    public int? ParentProcessId { get; init; }
}

public static class ProcessIdentityPolicy
{
    public static string Signature(string executable, string arguments, string workingDirectory)
    {
        var canonical = string.Join('\n',
            Path.GetFullPath(executable).ToUpperInvariant(),
            arguments.Trim(),
            Path.GetFullPath(workingDirectory).ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool Matches(ProcessIdentity expected, ObservedProcessIdentity observed, out string reason)
    {
        if (expected.ProcessId != observed.ProcessId)
        {
            reason = "PID differs.";
            return false;
        }
        if (expected.ProcessCreationTicks == ProcessCreationIdentity.Unknown ||
            observed.ProcessCreationTicks == ProcessCreationIdentity.Unknown)
        {
            reason = "Exact process creation identity is unavailable.";
            return false;
        }
        if (!ProcessCreationIdentity.Matches(expected.ProcessCreationTicks, observed.ProcessCreationTicks))
        {
            reason = "PID was reused by a process with a different exact creation identity.";
            return false;
        }
        if (!Path.GetFullPath(expected.ExecutablePath).Equals(Path.GetFullPath(observed.ExecutablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Executable path differs.";
            return false;
        }
        if (!Path.GetFullPath(expected.WorkingDirectory).Equals(Path.GetFullPath(observed.WorkingDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Working directory differs.";
            return false;
        }
        if (!expected.CommandSignature.Equals(observed.CommandSignature, StringComparison.Ordinal))
        {
            reason = "Command signature differs.";
            return false;
        }
        reason = "PID, start time, executable, working directory, and command signature match.";
        return true;
    }

    /// <summary>
    /// Verifies that a live OS process is the exact process instance ChunkPilot recorded at launch.
    /// </summary>
    /// <remarks>
    /// Windows does not expose another process's original working directory or complete launch
    /// command through <see cref="System.Diagnostics.Process"/>. PID alone is unsafe because Windows reuses it; PID,
    /// start time, and executable path together identify the same still-live process instance while
    /// the persisted working directory and command signature retain its launch provenance.
    /// </remarks>
    public static bool MatchesProcessInstance(
        ProcessIdentity expected,
        int processId,
        long processCreationTicks,
        string executablePath,
        out string reason)
    {
        if (expected.ProcessId != processId)
        {
            reason = "PID differs.";
            return false;
        }
        if (expected.ProcessCreationTicks == ProcessCreationIdentity.Unknown ||
            processCreationTicks == ProcessCreationIdentity.Unknown)
        {
            reason = "Exact process creation identity is unavailable; legacy records are never automatic kill authority.";
            return false;
        }
        if (!ProcessCreationIdentity.Matches(expected.ProcessCreationTicks, processCreationTicks))
        {
            reason = "PID was reused by a process with a different exact creation identity.";
            return false;
        }
        if (!Path.GetFullPath(expected.ExecutablePath).Equals(Path.GetFullPath(executablePath),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "Executable path differs.";
            return false;
        }
        reason = "PID, exact creation identity, and executable path identify the recorded process instance.";
        return true;
    }
}

public static class LifecycleIntentPolicy
{
    public static bool ShouldCrashRestart(
        LifecycleIntentKind lastIntent,
        bool crashRestartEnabled,
        bool startedSuccessfully,
        int attempt,
        int limit) =>
        crashRestartEnabled &&
        startedSuccessfully &&
        lastIntent is not LifecycleIntentKind.ManualStop and
        not LifecycleIntentKind.ApplicationExit and
        not LifecycleIntentKind.WindowsShutdown and
        not LifecycleIntentKind.SafeRestart and
        not LifecycleIntentKind.ScheduledRestart and
        not LifecycleIntentKind.UpdateRestart &&
        attempt < Math.Max(1, limit);

    public static int IntendedRestartAllowance(LifecycleIntentKind intent) =>
        intent is LifecycleIntentKind.SafeRestart or LifecycleIntentKind.ScheduledRestart or
            LifecycleIntentKind.UpdateRestart ? 1 : 0;
}

public sealed record ApplicationSession
{
    public Guid SessionId { get; init; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastHeartbeatAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; init; }
    public ApplicationExitKind? ExitKind { get; init; }
    public int ProcessId { get; init; }
    public long ProcessCreationTicks { get; init; }
    public IReadOnlyList<Guid> RunningServerIds { get; init; } = [];
    public string CrashDetailPath { get; init; } = "";
}

public sealed record SafeApplicationExitRequest(
    Guid SessionId,
    IReadOnlyList<Guid> RunningServerIds,
    DateTimeOffset RequestedAt)
{
    public string SessionCapability { get; init; } = "";
}

public sealed record UiSessionHeartbeatRequest(
    Guid SessionId,
    IReadOnlyList<Guid> RunningServerIds)
{
    public string SessionCapability { get; init; } = "";
}

public sealed record WindowsShutdownRequest(
    Guid SessionId,
    IReadOnlyList<Guid> RunningServerIds,
    DateTimeOffset RequestedAt)
{
    public string SessionCapability { get; init; } = "";
}

public sealed record UiSessionRegistrationResult(
    ApplicationSession Session,
    bool PreviousExitWasUnexpected,
    string RecoveryMessage)
{
    public string SessionCapability { get; init; } = "";
}

public sealed record ContentInventoryIdentity
{
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool ProviderManaged { get; init; }
}

public sealed record ContentReconciliationResult
{
    public IReadOnlyList<ContentInventoryIdentity> Active { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<ContentInventoryIdentity>> DuplicateIds { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<ContentInventoryIdentity>> DuplicateHashes { get; init; } = [];
    public IReadOnlyList<ContentInventoryIdentity> Sideloaded { get; init; } = [];
}

public static class ContentReconciliationPolicy
{
    public static ContentReconciliationResult Reconcile(IEnumerable<ContentInventoryIdentity> items)
    {
        var all = items.ToArray();
        return new ContentReconciliationResult
        {
            Active = all,
            DuplicateIds = all.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => (IReadOnlyList<ContentInventoryIdentity>)group.ToArray())
                .ToArray(),
            DuplicateHashes = all.Where(item => !string.IsNullOrWhiteSpace(item.Sha256))
                .GroupBy(item => item.Sha256, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => (IReadOnlyList<ContentInventoryIdentity>)group.ToArray())
                .ToArray(),
            Sideloaded = all.Where(item => !item.ProviderManaged).ToArray()
        };
    }
}
