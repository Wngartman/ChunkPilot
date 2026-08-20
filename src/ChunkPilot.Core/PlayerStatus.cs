namespace ChunkPilot.Core;

public enum PlayerStatusSource
{
    ModernStatus,
    LegacyExtendedStatus,
    LegacySimpleStatus,
    Query,
    ConsoleList,
    ConsoleRoster,
    LastExactStatus,
    Waiting,
    StatusCheckFailed,
    Unsupported
}

/// <summary>Count evidence with explicit provenance; null remains different from zero.</summary>
public sealed record PlayerStatusEvidence
{
    public int? Online { get; init; }
    public int? Maximum { get; init; }
    public PlayerStatusSource Source { get; init; }
    public bool Exact { get; init; }
    public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
    public string Detail { get; init; } = "";
}
