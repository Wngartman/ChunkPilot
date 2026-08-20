using System.Security.Cryptography;

namespace ChunkPilot.Core;

/// <summary>
/// The one authoritative state a server's external reachability evidence can be in. The App renders
/// exactly these and never derives a state of its own.
/// </summary>
/// <remarks>
/// A result here describes one point in time and one exact endpoint. It is not a setting, not a
/// capability, and never a claim that the server will still be reachable a minute later.
/// </remarks>
public enum ExternalReachabilityPhase
{
    /// <summary>Everything needed is in place and nobody has asked yet. The default.</summary>
    NotChecked,

    /// <summary>Something the check depends on is missing. <see cref="ExternalReachabilityBlocker"/> says what.</summary>
    Ineligible,

    /// <summary>One deliberate check is running.</summary>
    Checking,

    /// <summary>
    /// An external service established TCP to this exact public endpoint while the mapping and the
    /// running server still matched the request. The strongest evidence ChunkPilot can produce.
    /// </summary>
    Reachable,

    /// <summary>The external service could not establish TCP. It says nothing about why.</summary>
    Unreachable,

    /// <summary>
    /// The probe saw the request arrive from a different public address than the router reports, so
    /// no connection was attempted. Not a reachability failure.
    /// </summary>
    SourceAddressMismatch,

    /// <summary>
    /// An address family this check cannot be made with was involved: the request left this computer
    /// over one, or the address the router reported is not an IPv4 address at all.
    /// </summary>
    UnsupportedAddressFamily,

    /// <summary>No probe endpoint is configured, or the service did not answer. Nothing was concluded.</summary>
    ProbeUnavailable,

    /// <summary>The service asked ChunkPilot to slow down. Nothing was concluded.</summary>
    RateLimited,

    /// <summary>The user stopped the check. Nothing was changed and nothing was concluded.</summary>
    Cancelled,

    /// <summary>
    /// A result exists, but the endpoint it describes is no longer the current one. Kept visible as
    /// history; never presented as currently verified.
    /// </summary>
    Stale
}

/// <summary>
/// The single prerequisite that is missing. Deliberately specific: telling somebody the external
/// check failed when their server is not running is worse than saying nothing.
/// </summary>
public enum ExternalReachabilityBlocker
{
    None,
    /// <summary>This build has no external probe endpoint configured, so the feature cannot run at all.</summary>
    ProbeNotConfigured,
    ServerNotRunning,
    LocalPortUnknown,
    DirectInternetOff,
    RouterMappingInactive,
    ExternalPortUnknown,
    PublicAddressUnknown,
    /// <summary>The router's own WAN address is private, shared or otherwise not an internet address.</summary>
    PublicAddressNotRoutable
}

/// <summary>
/// Everything a reachability result is bound to. Two results are about the same thing only when every
/// field matches, which is what makes a stale answer detectable rather than merely unlikely.
/// </summary>
/// <remarks>
/// <see cref="RunIdentity"/> covers the whole listener identity in one value: a stop, a start, a
/// restart, a Java change and a port change all end the process this identity names, so a result
/// cannot survive any of them. <see cref="MappingIdentity"/> does the same for the router entry.
/// </remarks>
public sealed record ExternalReachabilityEndpoint
{
    public Guid ServerId { get; init; }

    /// <summary>The router-reported public IPv4 address the check was made against.</summary>
    public string PublicAddress { get; init; } = "";

    public int ExternalPort { get; init; }

    /// <summary>The server's own authoritative listen port, which the mapping forwards to.</summary>
    public int InternalPort { get; init; }

    /// <summary>
    /// The exact router mapping establishment: its opaque instance identity, then the mechanism,
    /// transport and internal endpoint that describe it.
    /// </summary>
    public string MappingIdentity { get; init; } = "";

    /// <summary>The exact running process this evidence belongs to.</summary>
    public string RunIdentity { get; init; } = "";
    public Guid PublicConnectivityLeaseId { get; init; }
    public long PublicConnectivityLeaseGeneration { get; init; }

    public bool IsComplete =>
        ServerId != Guid.Empty && PublicAddress.Length > 0 && ExternalPort > 0 && InternalPort > 0 &&
        MappingIdentity.Length > 0 && RunIdentity.Length > 0;
}

/// <summary>The authoritative snapshot the App renders. The App adds copy, never state.</summary>
public sealed record ExternalReachabilityState
{
    public Guid ServerId { get; init; }
    public ExternalReachabilityPhase Phase { get; init; } = ExternalReachabilityPhase.NotChecked;
    public ExternalReachabilityBlocker Blocker { get; init; } = ExternalReachabilityBlocker.None;

    /// <summary>False when this build has no probe endpoint. The feature then says so rather than failing.</summary>
    public bool ProbeConfigured { get; init; }

    /// <summary>What a check would be made against right now.</summary>
    public ExternalReachabilityEndpoint Endpoint { get; init; } = new();

    /// <summary>What the stored result was actually made against. Empty when there is no result.</summary>
    public ExternalReachabilityEndpoint CheckedEndpoint { get; init; } = new();

    /// <summary>The public address the external service saw the request arrive from.</summary>
    public string ObservedAddress { get; init; } = "";

    /// <summary>Retained separately so a mismatch can show both sides of the disagreement.</summary>
    public string RouterReportedAddress { get; init; } = "";

    public int Port { get; init; }
    public int ConnectMilliseconds { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>An external check owns this server right now; the App disables competing commands.</summary>
    public bool Busy { get; init; }
    public Guid OperationId { get; init; }

    /// <summary>Exact evidence for Technical details. Never primary copy.</summary>
    public string LastOperationDetail { get; init; } = "";

    /// <summary>Whether pressing the button right now would actually reach the service.</summary>
    public bool CanCheck => !Busy && ProbeConfigured && Blocker == ExternalReachabilityBlocker.None;

    /// <summary>The only condition under which ChunkPilot may say public access is verified.</summary>
    public bool IsVerified => Phase == ExternalReachabilityPhase.Reachable;

    /// <summary>A result exists but no longer describes the current endpoint.</summary>
    public bool HasHistoricalResult => Phase == ExternalReachabilityPhase.Stale && CheckedAt is not null;

    /// <summary>The endpoint a verified result names, formatted for display and copying.</summary>
    public string VerifiedEndpoint => IsVerified && ObservedAddress.Length > 0 && Port > 0
        ? $"{ObservedAddress}:{Port}"
        : "";

    public static ExternalReachabilityState NotChecked(Guid serverId) => new() { ServerId = serverId };
}

/// <summary>
/// Provider-neutral rules for external reachability. Everything here is a pure decision so the same
/// rule the Agent runs can be proven by a unit test.
/// </summary>
public static class ExternalReachabilityPolicy
{
    /// <summary>The probe contract version this build speaks. A response carrying another is rejected.</summary>
    public const int ApiVersion = 1;

    /// <summary>The path the client appends to the configured service origin. Never caller-supplied.</summary>
    public const string ProbePath = "/v1/probe";

    /// <summary>
    /// Decides whether an external check may be attempted, and which single prerequisite is missing.
    /// </summary>
    /// <remarks>
    /// Deliberately no firewall condition. A user may hold an exact foreign rule, have Windows
    /// Firewall switched off, or run a third-party product; refusing to look outside because
    /// ChunkPilot does not own a rule would substitute local inference for the stronger real-world
    /// evidence this check exists to gather.
    /// </remarks>
    public static ExternalReachabilityBlocker Evaluate(
        bool probeConfigured, ServerState serverState, int internalPort, RouterMappingState router)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (!probeConfigured)
            return ExternalReachabilityBlocker.ProbeNotConfigured;
        if (serverState != ServerState.Running)
            return ExternalReachabilityBlocker.ServerNotRunning;
        if (internalPort is <= 0 or > 65535)
            return ExternalReachabilityBlocker.LocalPortUnknown;
        if (!router.Enabled)
            return ExternalReachabilityBlocker.DirectInternetOff;
        if (router.Phase != RouterMappingPhase.Active)
            return ExternalReachabilityBlocker.RouterMappingInactive;
        if (router.ExternalPort is <= 0 or > 65535)
            return ExternalReachabilityBlocker.ExternalPortUnknown;
        if (router.RouterReportedExternalAddress.Length == 0)
            return ExternalReachabilityBlocker.PublicAddressUnknown;
        return RouterMappingPolicy.ClassifyExternalAddress(router.RouterReportedExternalAddress).Class
            != RoutableAddressClass.GloballyRoutable
            ? ExternalReachabilityBlocker.PublicAddressNotRoutable
            : ExternalReachabilityBlocker.None;
    }

    /// <summary>
    /// Composes the identity a result is bound to. An incomplete endpoint can never equal a complete
    /// one, so a check made while everything was in place is invalidated the moment anything is not.
    /// </summary>
    public static ExternalReachabilityEndpoint ComposeEndpoint(
        Guid serverId,
        ServerState serverState,
        int internalPort,
        int? rootProcessId,
        DateTimeOffset? startedAt,
        RouterMappingState router)
    {
        ArgumentNullException.ThrowIfNull(router);
        return new ExternalReachabilityEndpoint
        {
            ServerId = serverId,
            PublicAddress = router.RouterReportedExternalAddress,
            ExternalPort = router.Phase == RouterMappingPhase.Active ? router.ExternalPort : 0,
            InternalPort = internalPort,
            MappingIdentity = DescribeMapping(router),
            RunIdentity = DescribeRun(serverState, rootProcessId, startedAt)
        };
    }

    /// <summary>Identifies the exact router entry, or nothing at all when none is provably open.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="RouterMappingState.MappingInstanceId"/> is what makes this an identity rather than a
    /// description. Without it, a router that drops an entry and is asked for the same one again
    /// produces a mapping equal in mechanism, transport, client and both ports — so a check made
    /// against the first entry would still look current against the second, which it is not. The
    /// values are kept alongside it because they are what a person reads in technical details; the
    /// instance is what the comparison actually turns on.
    /// </para>
    /// <para>
    /// An active mapping whose instance is unknown deliberately yields no identity at all, so evidence
    /// can never be bound to a mapping ChunkPilot cannot tell apart from another.
    /// </para>
    /// </remarks>
    public static string DescribeMapping(RouterMappingState router)
    {
        ArgumentNullException.ThrowIfNull(router);
        if (router.Phase != RouterMappingPhase.Active || router.ExternalPort <= 0 ||
            router.MappingInstanceId.Length == 0)
            return "";
        var client = router.InternalClient.Length > 0 ? router.InternalClient : "unknown";
        return $"{router.MappingInstanceId}:{router.Mechanism}/{router.Transport}/" +
               $"{client}:{router.InternalPort}->{router.ExternalPort}";
    }

    /// <summary>
    /// Identifies the exact process the listener belongs to. A restart changes the start time, and a
    /// changed Java runtime or port requires a restart, so one value covers every listener change.
    /// </summary>
    public static string DescribeRun(ServerState serverState, int? rootProcessId, DateTimeOffset? startedAt)
    {
        if (serverState != ServerState.Running || rootProcessId is not { } processId || startedAt is not { } started)
            return "";
        return $"{processId}@{started.UtcDateTime.Ticks}";
    }

    /// <summary>
    /// 128 bits of cryptographic randomness, lowercase hex. Correlation only: it is generated per
    /// check, never stored, never reused, and is not a credential of any kind.
    /// </summary>
    public static string NewRequestId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
