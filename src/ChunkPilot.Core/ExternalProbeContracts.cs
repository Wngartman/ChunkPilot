namespace ChunkPilot.Core;

/// <summary>
/// The provider-neutral contract for an external vantage point.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately narrow. A remote service can answer exactly one question — could a TCP connection be
/// established from outside this network to the address this request came from — and this interface
/// is shaped so it cannot answer anything else. There is no hostname parameter and no target
/// parameter, because the target is not the caller's to choose: <see cref="ExternalProbeRequest.ExpectedAddress"/>
/// is compared with the address the service observes and is never connected to.
/// </para>
/// <para>
/// Agent orchestration depends only on this interface. Nothing above Infrastructure knows which
/// provider implements it, what its JSON looks like, or that it is reached over HTTPS at all.
/// </para>
/// </remarks>
public interface IExternalReachabilityProbe
{
    /// <summary>
    /// Whether this build has a usable, validated probe endpoint. False fails the feature closed and
    /// truthfully, rather than producing a fabricated result.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Why the endpoint is unusable, when it is. Technical-details copy, never primary.</summary>
    string ConfigurationDetail { get; }

    Task<ExternalProbeResult> ProbeAsync(ExternalProbeRequest request, CancellationToken cancellationToken);
}

/// <summary>One deliberate probe. Four values, none of which names a host to connect to.</summary>
public sealed record ExternalProbeRequest
{
    /// <summary>Fresh cryptographic randomness per check, so a late reply cannot be mistaken for this one.</summary>
    public required string RequestId { get; init; }

    /// <summary>
    /// The router-reported public IPv4 address. The service compares it with what it observes and
    /// refuses to connect when they differ; it is never used as a destination.
    /// </summary>
    public required string ExpectedAddress { get; init; }

    public required int Port { get; init; }
}

/// <summary>
/// What the provider could establish. Nothing here is a diagnosis: the remote service only knows
/// whether the handshake completed, and ChunkPilot combines that with its own local evidence.
/// </summary>
public enum ExternalProbeOutcome
{
    /// <summary>TCP was established from outside the network to the observed address and port.</summary>
    Reachable,
    /// <summary>The connection did not complete. The cause is not known to the service.</summary>
    Unreachable,
    /// <summary>The observed source address differed from the expected one, so nothing was attempted.</summary>
    SourceMismatch,
    /// <summary>
    /// The request left over IPv6, the observed address was not a routable IPv4 address, or the
    /// address to verify is not one this path covers, so nothing was sent.
    /// </summary>
    UnsupportedAddressFamily,
    /// <summary>The service refused the request itself. A client defect, never a network result.</summary>
    InvalidRequest,
    RateLimited,
    /// <summary>The service failed internally. Nothing about the server was learned.</summary>
    ProbeError,
    /// <summary>No usable probe endpoint is configured in this build.</summary>
    NotConfigured,
    /// <summary>The service did not answer within the client's own bounded wait.</summary>
    Timeout,
    /// <summary>The transport failed, so nothing about the server was learned.</summary>
    ServiceUnavailable,
    /// <summary>The answer did not match the request, or did not match the contract. Refused.</summary>
    MalformedResponse,
    Cancelled
}

/// <summary>The validated result of one probe. Only fields the client has verified are populated.</summary>
public sealed record ExternalProbeResult
{
    public required ExternalProbeOutcome Outcome { get; init; }
    public string RequestId { get; init; } = "";

    /// <summary>The public address the service saw the request arrive from, when it reported one.</summary>
    public string ObservedAddress { get; init; } = "";

    /// <summary>The service's own view of the address family: ipv4, ipv6, or unknown.</summary>
    public string ObservedFamily { get; init; } = "";

    public int Port { get; init; }
    public int ConnectMilliseconds { get; init; }
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>Bounded, non-reflective evidence for Technical details. Never a provider exception.</summary>
    public string Detail { get; init; } = "";

    public static ExternalProbeResult Failed(ExternalProbeOutcome outcome, string requestId, string detail) =>
        new() { Outcome = outcome, RequestId = requestId, Detail = detail };
}
