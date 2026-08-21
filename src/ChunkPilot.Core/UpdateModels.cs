namespace ChunkPilot.Core;

public enum UpdateProvider
{
    None,
    PaperMC,
    Modrinth,
    CurseForge,
    GitHubReleases,
    DirectManifest,
    LocalPackageHistory,
    ManagedLoader
}

public enum ReleaseChannel
{
    Stable,
    Beta,
    Alpha
}

public enum ServerUpdateStatus
{
    SourceNotLinked,
    UpToDate,
    UpdateAvailable,
    Checking,
    Downloading,
    ReadyToInstall,
    Updating,
    PendingValidation,
    UpdateSuccessful,
    UpdateFailed,
    RollbackAvailable,
    CheckUnavailable
}

public enum UpdateCompatibility
{
    Compatible,
    CompatibleWithMigrationWarning,
    ManualReviewRequired,
    Incompatible,
    Unknown
}

public enum VersionHealth
{
    Healthy,
    PendingValidation,
    Failed,
    RolledBack
}

public enum UpdateOperationState
{
    Planned,
    WarningPlayers,
    Saving,
    Stopping,
    Snapshotting,
    Downloading,
    Verifying,
    ReadyToInstall,
    Extracting,
    PlanningMigration,
    BuildingCandidate,
    Switching,
    Starting,
    Querying,
    PendingValidation,
    RollingBack,
    Completed,
    Failed,
    Cancelled
}

public enum FileOwnership
{
    Persistent,
    PackManaged,
    UserAdded,
    Unknown
}

public enum MigrationResolutionKind
{
    NewBaseline,
    KeepOld,
    UseMergedText
}

public sealed record UpdateSource
{
    public Guid ServerId { get; init; }
    public UpdateProvider Provider { get; init; }
    public string ProjectName { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string InstalledVersionId { get; init; } = "";
    public string InstalledVersionName { get; init; } = "";
    public string InstalledFileId { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    public ReleaseChannel ReleaseChannel { get; init; } = ReleaseChannel.Stable;
    public string SourceUrl { get; init; } = "";
    public string AssetNamePattern { get; init; } = "";
    public DateTimeOffset? InstalledAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public bool IsUserLinked { get; init; }
    public string DetectionEvidence { get; init; } = "";
    public bool HasIdentifiedBaseline =>
        !string.IsNullOrWhiteSpace(InstalledVersionId) || !string.IsNullOrWhiteSpace(InstalledVersionName);
}

public sealed record PackVersionInfo
{
    public string PackId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string VersionName { get; init; } = "";
    public ReleaseChannel ReleaseChannel { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string InstallerVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public int InstallerJavaMajor { get; init; }
    public string DownloadUrl { get; init; } = "";
    public long? FileSize { get; init; }
    public string Sha1 { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Changelog { get; init; } = "";
    public string MigrationNotes { get; init; } = "";
    public string PackageType { get; init; } = "zip";
    public IReadOnlyList<string> DeclaredFiles { get; init; } = [];
}

public sealed record UpdatePreferences
{
    public Guid ServerId { get; init; }
    public bool AutomaticChecksEnabled { get; init; } = true;
    public int CheckIntervalHours { get; init; } = 24;
    public bool IncludeBeta { get; init; }
    public bool IncludeAlpha { get; init; }
    public bool AutomaticInstallEnabled { get; init; }
    public TimeSpan MaintenanceWindow { get; init; } = new(4, 0, 0);
    public bool AllowMinecraftVersionChange { get; init; }
    public int SnapshotRetentionDays { get; init; } = 30;
}

public sealed record UpdateCheckResult
{
    public Guid ServerId { get; init; }
    public ServerUpdateStatus Status { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public UpdateSource? Source { get; init; }
    public PackVersionInfo? InstalledVersion { get; init; }
    public PackVersionInfo? LatestVersion { get; init; }
    public UpdateCompatibility Compatibility { get; init; } = UpdateCompatibility.Unknown;
    public IReadOnlyList<string> CompatibilityReasons { get; init; } = [];
    public string Message { get; init; } = "";
}

public sealed record VersionSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public string VersionId { get; init; } = "";
    public string VersionName { get; init; } = "";
    public DateTimeOffset InstalledAt { get; init; } = DateTimeOffset.UtcNow;
    public UpdateProvider SourceProvider { get; init; }
    public string Source { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public string JavaVersion { get; init; } = "";
    public bool IsActive { get; init; }
    public VersionHealth Health { get; init; } = VersionHealth.PendingValidation;
    public string SnapshotPath { get; init; } = "";
    public string ManifestPath { get; init; } = "";
    public long SnapshotSize { get; init; }
    public bool IncludesWorldData { get; init; } = true;
    public bool Verified { get; init; }
    public bool KeepPermanently { get; init; }
    public DateTimeOffset? RetainUntil { get; init; }
    public string Changelog { get; init; } = "";
    public string UpdateNotes { get; init; } = "";
    public string Description { get; init; } = "";
    public string LastStartupResult { get; init; } = "";
    public ServerDefinition Definition { get; init; } = new();
}

public sealed record VersionSnapshotManifest
{
    public Guid SnapshotId { get; init; }
    public Guid ServerId { get; init; }
    public string VersionId { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public bool IncludesWorldData { get; init; }
    public IReadOnlyList<BackupManifestEntry> Files { get; init; } = [];
    public IReadOnlyList<VersionSnapshotContentObject> ContentObjects { get; init; } = [];
}

/// <summary>
/// A snapshot file whose bytes live in the server's hash-addressed snapshot object store. The
/// object key is relative to the snapshot directory; rollback never trusts or follows an absolute
/// path from a manifest.
/// </summary>
public sealed record VersionSnapshotContentObject(
    string RelativePath,
    string ObjectKey,
    long SizeBytes,
    string Sha256);

public sealed record PackFileChange
{
    public string RelativePath { get; init; } = "";
    public FileOwnership Ownership { get; init; }
    public string Change { get; init; } = "";
    public string Reason { get; init; } = "";
    public string OldSha256 { get; init; } = "";
    public string NewSha256 { get; init; } = "";
}

public sealed record MigrationPlan
{
    public IReadOnlyList<PackFileChange> Changes { get; init; } = [];
    public IReadOnlyList<string> PersistentPaths { get; init; } = [];
    public IReadOnlyList<string> Conflicts { get; init; } = [];
    public bool RequiresManualReview => Conflicts.Count > 0;
}

public sealed record MigrationDecision
{
    public Guid UpdateOperationId { get; init; }
    public string RelativePath { get; init; } = "";
    public string Decision { get; init; } = "";
    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record MigrationResolution
{
    public MigrationResolutionKind Kind { get; init; } = MigrationResolutionKind.NewBaseline;
    public string MergedContent { get; init; } = "";
}

public sealed record UpdateInstallRequest
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public required PackVersionInfo TargetVersion { get; init; }
    public int PlayerCountdownSeconds { get; init; } = 30;
    public bool StartForValidation { get; init; } = true;
    public bool Automatic { get; init; }
    public bool ConfirmedMigrationWarnings { get; init; }
    public bool DownloadOnly { get; init; }
    public IReadOnlyDictionary<string, MigrationResolution> MigrationResolutions { get; init; } =
        new Dictionary<string, MigrationResolution>(StringComparer.OrdinalIgnoreCase);
}

public sealed record UpdateProgress
{
    public Guid OperationId { get; init; }
    public UpdateOperationState State { get; init; }
    public string CurrentStep { get; init; } = "";
    public double Percent { get; init; }
    public long BytesDownloaded { get; init; }
    public long? TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public string Detail { get; init; } = "";
    public string LogPath { get; init; } = "";
}

public sealed record UpdateExecutionResult
{
    public Guid OperationId { get; init; }
    public Guid ServerId { get; init; }
    public bool Success { get; init; }
    public bool RolledBack { get; init; }
    public bool WasRunning { get; init; }
    public ServerDefinition PreviousDefinition { get; init; } = new();
    public ServerDefinition UpdatedDefinition { get; init; } = new();
    public VersionSnapshot? PreviousSnapshot { get; init; }
    public VersionSnapshot? ActiveVersion { get; init; }
    public MigrationPlan MigrationPlan { get; init; } = new();
    public string Message { get; init; } = "";
}

public sealed record UpdateOperationSnapshot
{
    public Guid OperationId { get; init; }
    public UpdateProgress Progress { get; init; } = new();
    public bool IsTerminal { get; init; }
    public bool Success { get; init; }
    public string Error { get; init; } = "";
    public UpdateExecutionResult? Result { get; init; }
}

public sealed record UpdateSourceDetectionResult
{
    public UpdateSource? Source { get; init; }
    public bool IsTrustworthy { get; init; }
    public bool RequiresBaseline { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = [];
}

public sealed record UpdateCenterItem
{
    public Guid ServerId { get; init; }
    public string ServerName { get; init; } = "";
    public ServerUpdateStatus Status { get; init; }
    public string InstalledVersion { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public DateTimeOffset? LastCheckedAt { get; init; }
    public string Detail { get; init; } = "";
}

public sealed record UpdateHistoryEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string Kind { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Success { get; init; }
}

public static class UpdatePolicy
{
    public static bool Allows(ReleaseChannel channel, UpdatePreferences preferences) =>
        channel switch
        {
            ReleaseChannel.Stable => true,
            ReleaseChannel.Beta => preferences.IncludeBeta || preferences.IncludeAlpha,
            ReleaseChannel.Alpha => preferences.IncludeAlpha,
            _ => false
        };

    public static IReadOnlyList<string> ValidateAutomaticInstall(
        UpdateCheckResult check,
        UpdatePreferences preferences)
    {
        var reasons = new List<string>();
        if (!preferences.AutomaticInstallEnabled)
            reasons.Add("Automatic installation is disabled.");
        if (check.LatestVersion is null)
            reasons.Add("No target version is available.");
        if (check.LatestVersion is { ReleaseChannel: not ReleaseChannel.Stable })
            reasons.Add("Unattended updates are limited to stable releases.");
        if (check.Compatibility != UpdateCompatibility.Compatible)
            reasons.Add("Unattended updates require a compatibility result with no migration warning.");
        if (check.InstalledVersion is not null && check.LatestVersion is not null &&
            !string.Equals(check.InstalledVersion.MinecraftVersion, check.LatestVersion.MinecraftVersion,
                StringComparison.OrdinalIgnoreCase) && !preferences.AllowMinecraftVersionChange)
            reasons.Add("Minecraft version changes require explicit permission.");
        return reasons;
    }

    public static bool CanDeleteSnapshot(
        VersionSnapshot candidate,
        IReadOnlyCollection<VersionSnapshot> versions,
        out string reason)
    {
        if (candidate.IsActive)
        {
            reason = "The active version cannot be deleted.";
            return false;
        }
        if (versions.Count(version => version.Verified && version.Health != VersionHealth.Failed) <= 1)
        {
            reason = "At least one verified usable version must remain.";
            return false;
        }
        reason = "";
        return true;
    }

    public static string FormatUiTimestamp(DateTimeOffset value) =>
        value.ToLocalTime().ToString("M/d/yyyy h:mm tt", System.Globalization.CultureInfo.CurrentCulture);
}
