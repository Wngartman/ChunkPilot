namespace ChunkPilot.Core;

public enum ManagedContentOperationKind
{
    InstallAddon,
    InstallAddonPlan,
    UpdateAddon,
    RemoveAddon,
    InstallPack,
    UpdatePack
}

public enum ManagedContentOperationStage
{
    Queued,
    ResolvingDependencies,
    Downloading,
    Verifying,
    InspectingMetadata,
    Staging,
    Installing,
    PendingRestart,
    Installed,
    Loaded,
    Failed,
    Cancelled
}

public sealed record ManagedContentProgress
{
    public ManagedContentOperationStage Stage { get; init; } = ManagedContentOperationStage.Queued;
    public string Message { get; init; } = "Queued";
    public double? Percent { get; init; }
    public long? BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
}

public sealed record ManagedContentOperationSnapshot
{
    public Guid OperationId { get; init; }
    public Guid ServerId { get; init; }
    public ManagedContentOperationKind Kind { get; init; }
    public string Provider { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ManagedContentProgress Progress { get; init; } = new();
    public bool IsTerminal { get; init; }
    public bool? Success { get; init; }
    public bool IsCancellable { get; init; } = true;
    public string? Error { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record BeginManagedContentInstallRequest(
    Guid ServerId,
    string ProjectId,
    string VersionId,
    bool IncludeDependencies,
    bool RestartIfRunning = false,
    Guid OperationId = default);

public sealed record ManagedContentOperationRequest(Guid OperationId);
public sealed record ManagedContentOperationsRequest(Guid? ServerId = null);

