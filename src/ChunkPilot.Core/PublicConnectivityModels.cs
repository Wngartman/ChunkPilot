namespace ChunkPilot.Core;

/// <summary>Agent-minted proof that a request belongs to the current exact UI process.</summary>
public sealed record UiSessionCredential
{
    public Guid SessionId { get; init; }
    public string Capability { get; init; } = "";
    public bool IsPresent => SessionId != Guid.Empty && Capability.Length > 0;
}

/// <summary>One in-memory generation of public connectivity for one managed server.</summary>
public sealed record PublicConnectivityLeaseIdentity
{
    public Guid ServerId { get; init; }
    public Guid LeaseId { get; init; }
    public long Generation { get; init; }
    /// <summary>
    /// In-memory Agent lifecycle epoch that minted this lease. It is deliberately not recoverable
    /// authority: a restarted or exiting Agent can only use persisted values for exact cleanup.
    /// </summary>
    public long LifecycleEpoch { get; init; }
    public Guid SessionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsPresent => ServerId != Guid.Empty && LeaseId != Guid.Empty && Generation > 0 && LifecycleEpoch > 0;
}

/// <summary>
/// Binds a capability-bearing payload to the named-pipe operation that is allowed to consume it.
/// </summary>
public enum PublicConnectivityOperation
{
    None,
    ReadRouterState,
    CheckRouterCapability,
    EnableRouterMapping,
    DisableRouterMapping,
    RetryRouterMapping,
    CancelRouterMapping,
    ReadExternalReachability,
    CheckExternalReachability,
    CancelExternalReachability,
    PrepareFirewallAccess,
    CompleteFirewallAccess,
    CancelFirewallAccess,
    StartServer,
    StopServer,
    RestartServer,
    ForceTerminateServer,
    UpdateServerProperties,
    StartAllServers,
    StopAllServers
}

public sealed record UiSessionRegistrationRequest(int ProcessId, long ProcessCreationTicks);
