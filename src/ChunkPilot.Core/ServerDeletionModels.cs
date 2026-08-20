namespace ChunkPilot.Core;

public enum ServerDeletionMode
{
    RemoveFromChunkPilot,
    MoveToRecovery,
    Permanent
}

public sealed record ServerDeletionPreflightRequest(Guid ServerId);

public sealed record ServerDeletionPreflight
{
    public Guid Token { get; init; } = Guid.NewGuid();
    public Guid ServerId { get; init; }
    public string ServerName { get; init; } = "";
    public string Platform { get; init; } = "";
    public string Version { get; init; } = "";
    public ServerState State { get; init; }
    public bool IsManaged { get; init; }
    public bool OwnershipProven { get; init; }
    public string ManagedRoot { get; init; } = "";
    public string WorldLocation { get; init; } = "";
    public int BackupCount { get; init; }
    public IReadOnlyList<string> ManagedBackupPaths { get; init; } = [];
    public IReadOnlyList<string> ProtectedExternalPaths { get; init; } = [];
    public int ActiveScheduleCount { get; init; }
    public bool InternetSharingConfigured { get; init; }
    public bool FirewallRemovalRequired { get; init; }
    public IReadOnlyList<string> Blockers { get; init; } = [];
    public DateTimeOffset ExpiresAt { get; init; } = DateTimeOffset.UtcNow.AddMinutes(5);
}

public sealed record ServerDeletionRequest(
    Guid ServerId,
    Guid PreflightToken,
    ServerDeletionMode Mode,
    string ConfirmationName = "",
    bool AcknowledgeWorldDeletion = false,
    bool AcknowledgeManagedBackupDeletion = false);

public sealed record ServerDeletionReceipt
{
    public Guid ServerId { get; init; }
    public ServerDeletionMode Mode { get; init; }
    public bool Removed { get; init; }
    public string RecoveryPath { get; init; } = "";
    public string Detail { get; init; } = "";
}
