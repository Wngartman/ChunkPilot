using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChunkPilot.Core;

public enum ServerState
{
    Stopped,
    Starting,
    Running,
    Saving,
    Stopping,
    Restarting,
    BackingUp,
    Restoring,
    Crashed,
    Unresponsive,
    Unknown
}

public enum ServerEcosystem
{
    Unknown,
    Vanilla,
    Paper,
    Purpur,
    Spigot,
    Bukkit,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    Hybrid,
    Custom
}

/// <summary>The game protocol and lifecycle family owned by one server definition.</summary>
public enum ServerGameKind
{
    Minecraft,
    Terraria
}

public enum FindingSeverity
{
    Pass,
    Information,
    Warning,
    Error,
    Unavailable
}

public enum ScheduleKind
{
    OneTime,
    Interval,
    Daily,
    Weekly,
    Monthly,
    Cron
}

public enum ScheduledAction
{
    Start,
    Save,
    Stop,
    Restart,
    Backup,
    SendCommand,
    DeleteOldLogs,
    VerifyBackups
}

public enum RecommendationLevel
{
    Recommended,
    Alternative,
    ManualConfigurationRequired
}

public enum CompatibilityState
{
    Compatible,
    LikelyCompatible,
    Incompatible,
    Unknown
}

public enum InstallSourceType
{
    Vanilla,
    Paper,
    Purpur,
    Fabric,
    Quilt,
    Forge,
    NeoForge,
    LocalZip,
    DirectUrl,
    ExistingPackageFolder,
    CustomPackage,
    ModrinthPack,

    /// <summary>A user-owned server JAR copied through the managed creation transaction.</summary>
    LocalServerJar
}

public enum InstallState
{
    Planned,
    Staging,
    Downloading,
    Extracting,
    Installing,
    Validating,
    Finalizing,
    Completed,
    Cancelled,
    Failed,

    /// <summary>The files are in place and the server record is being written and checked.</summary>
    Registering,

    /// <summary>An owned change is being reversed after a failure.</summary>
    RollingBack,

    /// <summary>Automatic reconciliation was not provably safe. Nothing further will be changed.</summary>
    RecoveryRequired
}

public sealed record ServerDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Minecraft Server";
    public string RootPath { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string SaveCommand { get; init; } = "save-all flush";
    public string SaveFallbackCommand { get; init; } = "save-all";
    public string StopCommand { get; init; } = "stop";
    public string ReadinessPattern { get; init; } = @"Done \(.+?\)!|For help, type";
    /// <summary>Optional game-specific save confirmation. Empty uses the centralized game profile.</summary>
    public string SaveConfirmationPattern { get; init; } = "";
    public int StartupTimeoutSeconds { get; init; } = 180;
    public int ShutdownTimeoutSeconds { get; init; } = 120;
    public int SaveTimeoutSeconds { get; init; } = 30;
    public int RestartDelaySeconds { get; init; } = 3;
    public int Port { get; init; } = 25565;
    /// <summary>Defaults to Minecraft so existing persisted definitions remain backward compatible.</summary>
    public ServerGameKind GameKind { get; init; } = ServerGameKind.Minecraft;
    /// <summary>Game-native version for non-Minecraft runtimes. Minecraft continues to use MinecraftVersion.</summary>
    public string GameVersion { get; init; } = "";
    public ServerEcosystem Ecosystem { get; init; } = ServerEcosystem.Unknown;
    public string MinecraftVersion { get; init; } = "Unknown";
    public string LoaderVersion { get; init; } = "";
    public bool AutoStart { get; init; }
    public bool CrashRestartEnabled { get; init; }
    public int CrashRestartLimit { get; init; } = 3;
    public int CrashRestartDelaySeconds { get; init; } = 15;
    public DateTimeOffset ImportedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsManaged { get; init; }
    public string ManagedInstanceRoot { get; init; } = "";
    public bool RunInBackground { get; init; } = true;
    public int MinimumRamMb { get; init; } = 1_024;
    public int MaximumRamMb { get; init; } = 4_096;
    public string RamArgumentSource { get; init; } = "Launch profile";
    public string UserConfiguredHostname { get; init; } = "";

    /// <summary>
    /// The next-step preference chosen during creation. It never represents network consent or an
    /// active exposure; existing and imported servers default to <see cref="VanillaNetworkingPreference.DecideLater"/>.
    /// </summary>
    public VanillaNetworkingPreference CreationNetworkingPreference { get; init; } =
        VanillaNetworkingPreference.DecideLater;
}

public sealed record LaunchCandidate
{
    public string DisplayName { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Arguments { get; init; } = "";
    public string WorkingDirectory { get; init; } = "";
    public RecommendationLevel Recommendation { get; init; } = RecommendationLevel.Alternative;
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> Problems { get; init; } = [];
    public bool DetachesProcess { get; init; }
}

public sealed record ServerDetectionResult
{
    public string RootPath { get; init; } = "";
    public string SuggestedName { get; init; } = "";
    public ServerEcosystem Ecosystem { get; init; }
    public string MinecraftVersion { get; init; } = "Unknown";
    public string LoaderVersion { get; init; } = "";
    public int Port { get; init; } = 25565;
    public IReadOnlyList<LaunchCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<JavaRuntimeInfo> JavaRuntimes { get; init; } = [];
    public IReadOnlyList<DiagnosticFinding> Findings { get; init; } = [];
}

public sealed record JavaRuntimeInfo
{
    public string Path { get; init; } = "";
    public string Version { get; init; } = "Unknown";
    public string Vendor { get; init; } = "Unknown";
    public string Architecture { get; init; } = "Unknown";
    public string Source { get; init; } = "";
    public bool Exists { get; init; }
    public string Compatibility { get; init; } = "Unknown";
}

public sealed record ConsoleLine(long Sequence, DateTimeOffset Timestamp, string Stream, string Text);

public sealed record StatisticsSample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public double CpuPercent { get; init; }
    public long WorkingSetBytes { get; init; }
    public long PeakWorkingSetBytes { get; init; }
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public long DiskReadBytes { get; init; }
    public long DiskWriteBytes { get; init; }
}

public sealed record ServerSnapshot
{
    public required ServerDefinition Definition { get; init; }
    public ServerState State { get; init; }
    public int? RootProcessId { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public TimeSpan Uptime { get; init; }
    public int? LastExitCode { get; init; }
    public string LastError { get; init; } = "";
    /// <summary>
    /// Whether the most recent start attempt reached the configured readiness signal.
    /// A crashed process is not necessarily a failed start: it may have run successfully first.
    /// </summary>
    public bool LastStartReachedReadiness { get; init; }
    public DateTimeOffset? LastSaveAt { get; init; }
    public DateTimeOffset? LastBackupAt { get; init; }
    public bool ConsoleConnected { get; init; }
    public int? OnlinePlayers { get; init; }
    public int? MaxPlayers { get; init; }
    public PlayerStatusEvidence? PlayerStatus { get; init; }

    /// <summary>
    /// Players the server itself has reported as connected, taken from its console output.
    /// </summary>
    /// <remarks>
    /// The server list ping returns a count, not names, so per-player online status has to come from
    /// the join and leave lines the server prints. Empty whenever the server is not running.
    /// </remarks>
    public IReadOnlyList<string> OnlinePlayerNames { get; init; } = [];

    /// <summary>
    /// Changes whenever player access changes: whitelist, operators, bans, or who is connected.
    /// </summary>
    /// <remarks>
    /// The UI compares this against the state it last loaded and re-reads only when it differs. That
    /// is what makes an <c>op</c> typed into the Console, or a player joining, reach the Access page
    /// without the UI polling those files itself.
    /// </remarks>
    public string PlayerAccessStamp { get; init; } = "";
    public StatisticsSample? CurrentStatistics { get; init; }
    public IReadOnlyList<StatisticsSample> RecentStatistics { get; init; } = [];
    public IReadOnlyList<ConsoleLine> Console { get; init; } = [];
    public CrashAnalysisReport? LastCrashAnalysis { get; init; }
}

public sealed record HostSnapshot
{
    public double CpuPercent { get; init; }
    public long UsedMemoryBytes { get; init; }
    public long TotalMemoryBytes { get; init; }
    public long AvailableMemoryBytes { get; init; }
    public long FreeDiskBytes { get; init; }
    public long TotalDiskBytes { get; init; }
    public string CpuModel { get; init; } = "";
    public int PhysicalCoreCount { get; init; }
    public int LogicalProcessorCount { get; init; }
    public string WindowsVersion { get; init; } = "";
    public TimeSpan HostUptime { get; init; }
    public string LanAddress { get; init; } = "";
    public string ActiveNetworkAdapter { get; init; } = "";
    public long NetworkReceiveBytesPerSecond { get; init; }
    public long NetworkSendBytesPerSecond { get; init; }
    public long ManagedServerStorageBytes { get; init; }
    public long BackupStorageBytes { get; init; }
}

public sealed record DashboardSnapshot
{
    public bool AgentConnected { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public HostSnapshot Host { get; init; } = new();
    public IReadOnlyList<ServerSnapshot> Servers { get; init; } = [];
    public IReadOnlyList<ActivityEntry> RecentActivity { get; init; } = [];
    public DateTimeOffset? NextScheduledTask { get; init; }
}

public sealed record ActivityEntry
{
    public long Id { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid? ServerId { get; init; }
    public string ServerName { get; init; } = "";
    public string Action { get; init; } = "";
    public string Result { get; init; } = "";
    public long DurationMilliseconds { get; init; }
    public string Error { get; init; } = "";
    public string Source { get; init; } = "Manual";
}

public sealed record BackupProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public string Name { get; init; } = "Default";
    public string DestinationPath { get; init; } = "";
    public bool Compression { get; init; } = true;
    public IReadOnlyList<string> Exclusions { get; init; } =
        ["logs/**", "crash-reports/**", "*.tmp", "*.lock", "session.lock"];
    public int MaximumCount { get; init; } = 10;
    public int MaximumAgeDays { get; init; } = 30;
    public long MaximumStorageBytes { get; init; } = 50L * 1024 * 1024 * 1024;
    public bool VerificationEnabled { get; init; } = true;
}

public sealed record BackupRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public Guid ProfileId { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ArchivePath { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public string Description { get; init; } = "";
    public long SizeBytes { get; init; }
    public long DurationMilliseconds { get; init; }
    public bool Verified { get; init; }
    public string VerificationMessage { get; init; } = "";
    public string Source { get; init; } = "Manual";
}

public sealed record BackupManifest
{
    public Guid BackupId { get; init; }
    public Guid ServerId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string ServerName { get; init; } = "";
    public string SourceRoot { get; init; } = "";
    public ServerGameKind GameKind { get; init; } = ServerGameKind.Minecraft;
    public string GameVersion { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Ecosystem { get; init; } = "";
    public IReadOnlyList<BackupManifestEntry> Files { get; init; } = [];
}

public sealed record BackupManifestEntry(string RelativePath, long SizeBytes, string Sha256);

public sealed record ScheduleEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public string Name { get; init; } = "Scheduled task";
    public ScheduledAction Action { get; init; }
    public ScheduleKind Kind { get; init; }
    public DateTimeOffset? OneTimeAt { get; init; }
    public int IntervalMinutes { get; init; } = 60;
    public TimeSpan TimeOfDay { get; init; } = new(4, 0, 0);
    public DayOfWeek DayOfWeek { get; init; } = DayOfWeek.Sunday;
    public int DayOfMonth { get; init; } = 1;
    public string CronExpression { get; init; } = "";
    public string Command { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool AllowOverlap { get; init; }
    public int RestartCountdownSeconds { get; init; } = 60;
    public bool BackupBeforeRestart { get; init; }
    public int RetryLimit { get; init; } = 1;
    public DateTimeOffset? LastRunAt { get; init; }
    public DateTimeOffset? NextRunAt { get; init; }
}

public sealed record FileSystemEntry
{
    public string Name { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
}

public sealed record TextFileContent
{
    public string RelativePath { get; init; } = "";
    public string Content { get; init; } = "";
    public string EncodingName { get; init; } = "utf-8";
    public bool HasBom { get; init; }
    public string LineEnding { get; init; } = "\r\n";
    public string LoadedSha256 { get; init; } = "";
    public DateTimeOffset? LoadedLastWriteAt { get; init; }
}

public sealed record ModPluginEntry
{
    public string Name { get; init; } = "";
    public string FileName { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string Version { get; init; } = "Unknown";
    public string Id { get; init; } = "";
    public string Loader { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public bool Enabled { get; init; } = true;
    public bool DuplicateId { get; init; }
    public IReadOnlyList<string> Dependencies { get; init; } = [];
    public IReadOnlyList<ContentDependency> DependencyDetails { get; init; } = [];
    public CompatibilityState Compatibility { get; init; } = CompatibilityState.Unknown;
    public string CompatibilityReason { get; init; } = "Provider compatibility metadata is unavailable.";
    public string InstallSource { get; init; } = "Local file";
    public PluginProviderKind? Provider { get; init; }
    public string ProviderProjectId { get; init; } = "";
    public string ProviderVersionId { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string ClientRequirement { get; init; } = "Unknown";
}

public enum ContentDependencyKind
{
    Required,
    Optional,
    LoadBefore,
    Incompatible,
    Embedded,
    Unknown
}

public sealed record ContentDependency(string Id, ContentDependencyKind Kind);

public sealed record DiagnosticFinding
{
    public string Code { get; init; } = "";
    public FindingSeverity Severity { get; init; }
    public string Title { get; init; } = "";
    public string Evidence { get; init; } = "";
    public string LikelyCause { get; init; } = "";
    public string SuggestedAction { get; init; } = "";
    public string RelevantPath { get; init; } = "";
}

public sealed record WorldEntry
{
    public string Name { get; init; } = "";
    public string FolderPath { get; init; } = "";
    public bool IsActive { get; init; }
    public DateTimeOffset ModifiedAt { get; init; }
    public long SizeBytes { get; init; }
    public IReadOnlyList<string> DimensionFolders { get; init; } = [];
}

public sealed record WhitelistEntry
{
    public string Name { get; init; } = "";
    public Guid? Uuid { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
    public bool IsOnline { get; init; }
    public bool IsOperator { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}

public sealed record ServerInstallRequest
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public InstallSourceType SourceType { get; init; }
    public string Source { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Build { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    /// <summary>A reviewed relative launcher inside a local ZIP/folder; never an arbitrary native path.</summary>
    public string LaunchRelativePath { get; init; } = "";
    public string ServerName { get; init; } = "";
    public string InstanceRoot { get; init; } = "";
    public string JavaPath { get; init; } = "";
    /// <summary>Optional private Java used only while materializing an installer-based payload.</summary>
    public string InstallerJavaPath { get; init; } = "";
    public int MinimumRamMb { get; init; } = 1_024;
    public int MaximumRamMb { get; init; } = 4_096;
    public int Port { get; init; } = 25_565;
    public VanillaNetworkingPreference CreationNetworkingPreference { get; init; } =
        VanillaNetworkingPreference.DecideLater;
    public int MaxPlayers { get; init; } = 20;
    public IReadOnlyDictionary<string, string> InitialProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool EnableDailyBackup { get; init; }
    public bool EulaAccepted { get; init; }
    public DateTimeOffset? EulaAcceptedAt { get; init; }
    public bool AllowHttp { get; init; }
    public string ExpectedSha1 { get; init; } = "";
    public string ExpectedSha256 { get; init; } = "";
    public string ExpectedSha512 { get; init; } = "";
    public long? ExpectedSizeBytes { get; init; }
    /// <summary>Trusted provider identity recorded by the native creation path. Never inferred from the pack index.</summary>
    public UpdateProvider PackProvider { get; init; }
    public string PackProjectId { get; init; } = "";
    public string PackProjectName { get; init; } = "";
    public string PackVersionId { get; init; } = "";
    public string PackVersionName { get; init; } = "";
    public ReleaseChannel PackReleaseChannel { get; init; } = ReleaseChannel.Stable;
}

public sealed record InstallProgress
{
    public Guid OperationId { get; init; }
    public InstallState State { get; init; }

    /// <summary>
    /// The creation transaction's phase. Meaningful for server creation only; other operations that
    /// reuse this record leave it at its default.
    /// </summary>
    public CreationPhase Phase { get; init; } = CreationPhase.Requested;

    /// <summary>
    /// What the operation is doing, in the vocabulary the user is shown.
    /// </summary>
    /// <remarks>
    /// Reported by whoever knows: the coordinator names its runtime steps, the installer names the
    /// download and its verification, and the transaction derives the rest from its phase. Operations
    /// that are not server creations leave it at <see cref="CreationStage.NotStarted"/>.
    /// </remarks>
    public CreationStage Stage { get; init; } = CreationStage.NotStarted;

    public string CurrentStep { get; init; } = "";
    public double OverallPercent { get; init; }
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public string Detail { get; init; } = "";
    public string StagingLogPath { get; init; } = "";
}

public sealed record InstallationResult
{
    public required ServerDefinition Definition { get; init; }
    public string SourceUrl { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string StagingLogPath { get; init; } = "";

    /// <summary>What is true about the created server, including a successful-but-untidy outcome.</summary>
    public CreationOutcome Outcome { get; init; } = CreationOutcome.Completed;

    /// <summary>Things worth reading that did not stop the creation.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record InstallOperationSnapshot
{
    public Guid OperationId { get; init; }
    public long Revision { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public InstallProgress Progress { get; init; } = new();
    public bool IsTerminal { get; init; }
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public InstallationResult? Result { get; init; }

    /// <summary>
    /// What is actually true about the destination and the database right now.
    /// </summary>
    /// <remarks>
    /// <see cref="Success"/> alone cannot express "created, but temporary files remain" or
    /// "the files are in place and the record is not", and both of those need to reach the user.
    /// </remarks>
    public CreationOutcome Outcome { get; init; } = CreationOutcome.InProgress;

    /// <summary>Things that are true and worth reading, none of which stopped the operation.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record RamRecommendation
{
    public int MinimumMb { get; init; }
    public int RecommendedMb { get; init; }
    public int MaximumSafeMb { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string Explanation { get; init; } = "";
}

public sealed record ConnectionTestResult
{
    public FindingSeverity ProcessRunning { get; init; }
    public FindingSeverity PortListening { get; init; }
    public FindingSeverity MinecraftResponds { get; init; }
    public FindingSeverity FirewallAssessment { get; init; }
    public string LocalAddress { get; init; } = "";
    public string LanAddress { get; init; } = "";
    public string PublicAddress { get; init; } = "";
    public string ExternalResult { get; init; } = "Not tested";
    public string Interpretation { get; init; } = "";
}

public sealed record SelfTestItem(string Name, FindingSeverity Status, string Detail);

public sealed record OperationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public bool RequiresForceConfirmation { get; init; }
    public string? Path { get; init; }

    public static OperationResult Ok(string message, string? path = null) =>
        new() { Success = true, Message = message, Path = path };

    public static OperationResult Fail(string message, bool requiresForce = false) =>
        new() { Success = false, Message = message, RequiresForceConfirmation = requiresForce };
}

public sealed record AgentRequest
{
    public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    public string Operation { get; init; } = "";
    public JsonElement Payload { get; init; }
}

public sealed record AgentResponse
{
    public string RequestId { get; init; } = "";
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public JsonElement? Payload { get; init; }
}

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}

public static class ChunkPilotConstants
{
    public static string PipeName => PipeNameFor(Environment.GetEnvironmentVariable("CHUNKPILOT_INSTANCE_ID"));
    public static string AgentMutexName => MutexNameFor(Environment.GetEnvironmentVariable("CHUNKPILOT_INSTANCE_ID"));
    public static string AppMutexName => AppMutexNameFor(Environment.GetEnvironmentVariable("CHUNKPILOT_INSTANCE_ID"));

    public static string PipeNameFor(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? "ChunkPilot.Agent.v1" : $"ChunkPilot.Agent.v1.{Sanitize(instanceId)}";

    public static string MutexNameFor(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? "Local\\ChunkPilot.Agent" : $"Local\\ChunkPilot.Agent.{Sanitize(instanceId)}";

    public static string AppMutexNameFor(string? instanceId) =>
        string.IsNullOrWhiteSpace(instanceId) ? "Local\\ChunkPilot.App" : $"Local\\ChunkPilot.App.{Sanitize(instanceId)}";

    private static string Sanitize(string value) =>
        new(value.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').Take(64).ToArray());
}
