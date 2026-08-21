namespace ChunkPilot.Core;

public enum ServerDeletionMode
{
    RemoveFromChunkPilot,
    MoveToRecovery,
    Permanent
}

public sealed record ServerDeletionPreflightRequest(Guid ServerId);

public enum ManagedOwnershipStatus
{
    External,
    ProvenMarker,
    ReconciledCreationEvidence,
    Ambiguous
}

public sealed record ManagedOwnershipEvidence(string Code, bool Satisfied, string Detail);

public sealed record ManagedInstallEvidence(
    DateTimeOffset InstalledAt,
    string Source,
    string Sha256,
    string Detail);

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
    public ManagedOwnershipStatus OwnershipStatus { get; init; }
    public string OwnershipDetail { get; init; } = "";
    public IReadOnlyList<ManagedOwnershipEvidence> OwnershipEvidence { get; init; } = [];
    public bool CanCreateManagedCopy { get; init; }
    public string ReviewFingerprint { get; init; } = "";
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

public sealed record ManagedCopyConversionRequest(Guid ServerId, Guid PreflightToken);

public sealed record ManagedCopyConversionReceipt
{
    public Guid ServerId { get; init; }
    public string OriginalRoot { get; init; } = "";
    public string ManagedRoot { get; init; } = "";
    public long CopiedBytes { get; init; }
    public int CopiedFiles { get; init; }
    public string Detail { get; init; } = "";
}
