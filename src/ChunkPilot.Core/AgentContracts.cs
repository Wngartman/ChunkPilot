namespace ChunkPilot.Core;

public sealed record ServerIdRequest(Guid ServerId)
{
    public UiSessionCredential Session { get; init; } = new();
    public PublicConnectivityLeaseIdentity Lease { get; init; } = new();
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}
public sealed record AllServersLifecycleRequest
{
    public UiSessionCredential Session { get; init; } = new();
    public IReadOnlyList<PublicConnectivityLeaseIdentity> Leases { get; init; } = [];
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}
public sealed record DetectServerRequest(string Folder);
public sealed record CommandRequest(Guid ServerId, string Command);
public sealed record StopRequest(Guid ServerId, bool SaveFirst = true)
{
    public UiSessionCredential Session { get; init; } = new();
    public PublicConnectivityLeaseIdentity Lease { get; init; } = new();
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}
public sealed record FilesRequest(Guid ServerId, string RelativePath);
public sealed record WriteFileRequest(Guid ServerId, TextFileContent Content);
public sealed record AddonConfigWriteRequest(
    Guid ServerId,
    string AddonRelativePath,
    TextFileContent Content,
    bool RestartIfRunning = false);
public sealed record BackupRequest(Guid ServerId);
public sealed record BackupIdRequest(Guid BackupId);
public sealed record RestoreRequest(Guid ServerId, Guid BackupId);
public sealed record ScheduleIdRequest(Guid ScheduleId);
public sealed record JarInstallRequest(Guid ServerId, string SourcePath, bool RestartIfRunning = false);
public sealed record JarEnabledRequest(Guid ServerId, string RelativePath, bool Enabled, bool RestartIfRunning = false);
public sealed record SettingsValueRequest(string Key, string Value);
public sealed record ServerPropertiesRequest(Guid ServerId, Dictionary<string, string> Values)
{
    public UiSessionCredential Session { get; init; } = new();
    public PublicConnectivityLeaseIdentity Lease { get; init; } = new();
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}
public sealed record ServerPropertiesResponse(Dictionary<string, string> Values, string Raw);
public sealed record TextResponse(string Value);
public sealed record InstallOperationRequest(Guid OperationId);
public sealed record UpdateOperationRequest(Guid OperationId);
public sealed record LinkUpdateSourceRequest(UpdateSource Source);
public sealed record UpdateSourceResponse(UpdateSource? Source);
public sealed record CheckUpdatesRequest(Guid ServerId);
public sealed record UpdateCheckResponse(UpdateCheckResult? Check);
public sealed record VersionSnapshotRequest(Guid ServerId, Guid SnapshotId);
public sealed record MarkVersionHealthyRequest(Guid ServerId, Guid SnapshotId, int RetentionDays);
public sealed record VersionMetadataRequest(Guid ServerId, Guid SnapshotId, bool KeepPermanently, string Description);
public sealed record WorldRequest(Guid ServerId, string WorldName);
public sealed record WorldImportRequest(Guid ServerId, string ZipPath, string WorldName);
public sealed record WorldExportRequest(Guid ServerId, string WorldName, string DestinationDirectory);
public sealed record IconInstallRequest(
    Guid ServerId,
    string SourcePath,
    double CropX = 0,
    double CropY = 0,
    double CropSize = 1,
    bool SaveToLibrary = true);
public sealed record ServerIconLibraryEntry(string Path, string Name, DateTimeOffset CreatedAt);
public sealed record WhitelistPlayerRequest(Guid ServerId, string PlayerName);
public sealed record InstallVersionsRequest(InstallSourceType SourceType, bool IncludeSnapshots = false);
public sealed record ConnectionTestRequest(Guid ServerId, bool IncludeExternalProbe);
public sealed record RamUpdateRequest(Guid ServerId, int MinimumRamMb, int MaximumRamMb);
public sealed record RenameServerRequest(Guid ServerId, string DisplayName);
public sealed record ManagedJavaInstallRequest(int MajorVersion);
public sealed record ManagedJavaRemoveRequest(Guid RuntimeId);
public sealed record DatapackInspectRequest(string Path, string MinecraftVersion);
public sealed record DatapackInstallRequest(
    Guid ServerId,
    string WorldName,
    string SourcePath,
    bool ReplaceExisting = false);
public sealed record ResourcePackHashRequest(string Path);
public sealed record ResourcePackConfigureRequest(
    Guid ServerId,
    ResourcePackConfiguration Configuration);
public sealed record CrossplayInstallRequest(
    Guid ServerId,
    bool InstallFloodgate,
    bool InstallViaVersion,
    int BedrockPort = 19132);
public sealed record CrossplayRemoveRequest(Guid ServerId);
public sealed record GameruleApplyRequest(Guid ServerId, Dictionary<string, string> Changes);
public sealed record GameruleReadRequest(Guid ServerId);
public sealed record AutomationRecipeIdRequest(Guid RecipeId);

/// <summary>
/// Turns Direct internet on for one server. <paramref name="ConsentGranted"/> carries the user's
/// deliberate confirmation of the exposure for this server and nothing else; it is never inferred.
/// </summary>
public sealed record EnableRouterMappingRequest(Guid ServerId, bool ConsentGranted)
{
    public UiSessionCredential Session { get; init; } = new();
    public PublicConnectivityLeaseIdentity ExpectedLease { get; init; } = new();
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}

/// <summary>
/// Asks the Agent to prepare one privileged Windows Firewall operation for one server.
/// </summary>
/// <param name="PublicApproved">
/// The user separately approved a Public-profile exception for this server. Never inferred, never
/// inherited from another server, and required before a Public rule can even be prepared.
/// </param>
public sealed record PrepareFirewallAccessRequest(
    Guid ServerId,
    FirewallHelperOperation Operation,
    bool PublicApproved)
{
    public UiSessionCredential Session { get; init; } = new();
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}

/// <summary>
/// What the App needs in order to raise the elevation prompt, or why it may not.
/// </summary>
/// <remarks>
/// The arguments are built by the Agent from its own authoritative state and validated before they
/// leave it. The App passes them to the helper unchanged; it never composes a privileged operation.
/// </remarks>
public sealed record FirewallElevationTicket
{
    public bool Ready { get; init; }
    public Guid OperationId { get; init; }
    public FirewallHelperOperation Operation { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string Detail { get; init; } = "";
    public WindowsFirewallState State { get; init; } = new();
}

/// <summary>
/// Reports one privileged run back to the Agent.
/// </summary>
/// <param name="Cancelled">
/// The user dismissed the elevation prompt. A first-class outcome: nothing was changed, nothing failed,
/// and no intent becomes enabled.
/// </param>
public sealed record CompleteFirewallAccessRequest(
    Guid ServerId,
    Guid OperationId,
    bool Cancelled,
    int ExitCode,
    string LauncherDetail,
    FirewallElevationFailure ElevationFailure = FirewallElevationFailure.None)
{
    public UiSessionCredential Session { get; init; } = new();
    public PublicConnectivityOperation ConnectivityOperation { get; init; }
}
