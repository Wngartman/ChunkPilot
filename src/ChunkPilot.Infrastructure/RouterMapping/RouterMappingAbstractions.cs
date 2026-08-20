using System.Net;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// The LAN identity a mapping is created for: which private address the router should forward to, and
/// which gateway to ask. Chosen once by <see cref="RouterGatewaySelector"/> and then carried unchanged
/// through discovery, creation, renewal and removal so no step can silently pick a different adapter.
/// </summary>
public sealed record RouterLanBinding(
    string InterfaceId,
    string InterfaceName,
    IPAddress LocalAddress,
    int PrefixLength,
    IPAddress GatewayAddress);

/// <summary>One interface's gateways, paired with the shared LAN-interface description.</summary>
public sealed record RouterGatewayCandidate(
    LanInterfaceCandidate Interface,
    IReadOnlyList<IPAddress> Gateways);

/// <summary>What a bounded capability check learned about one mechanism.</summary>
public sealed record RouterDiscoveryResult
{
    public required RouterMappingMechanism Mechanism { get; init; }
    public required bool Supported { get; init; }
    public RouterMappingFailure Failure { get; init; } = RouterMappingFailure.None;

    /// <summary>Router-reported WAN address, when the mechanism can state one without mutating anything.</summary>
    public string ExternalAddress { get; init; } = "";

    /// <summary>Set when the mechanism cannot offer finite leases, which changes cleanup expectations.</summary>
    public bool SupportsFiniteLeases { get; init; } = true;

    /// <summary>UPnP control endpoint, empty for the datagram mechanisms.</summary>
    public string ControlUrl { get; init; } = "";
    public string ServiceType { get; init; } = "";

    /// <summary>
    /// What this response's epoch said about the gateway's mapping state, for mechanisms that carry
    /// one. Always <see cref="GatewayContinuity.Unknown"/> for mechanisms that do not.
    /// </summary>
    public GatewayContinuity Continuity { get; init; } = GatewayContinuity.Unknown;

    /// <summary>Exact provider-level detail for Technical details. Never primary copy.</summary>
    public string Detail { get; init; } = "";
}

/// <summary>What ChunkPilot asks a router to do. External and internal port are equal in v1.</summary>
public sealed record RouterMappingRequest
{
    public required MappingTransport Transport { get; init; }
    public required int InternalPort { get; init; }
    public required int ExternalPort { get; init; }
    public required int LeaseSeconds { get; init; }
    public string Description { get; init; } = RouterMappingPolicy.MappingDescription;

    /// <summary>PCP mapping nonce, hex encoded. Empty asks the provider to mint one.</summary>
    public string OwnershipToken { get; init; } = "";
}

/// <summary>The result of a create, renew or remove attempt.</summary>
public sealed record RouterMappingOutcome
{
    public required bool Success { get; init; }
    public RouterMappingFailure Failure { get; init; } = RouterMappingFailure.None;
    public RouterMappingMechanism Mechanism { get; init; } = RouterMappingMechanism.None;
    public int ExternalPort { get; init; }
    public int LeaseSeconds { get; init; }
    public bool LeaseIsFinite { get; init; } = true;
    public string ExternalAddress { get; init; } = "";
    public string OwnershipToken { get; init; } = "";
    public string ControlUrl { get; init; } = "";
    public string ServiceType { get; init; } = "";

    /// <summary>
    /// What this response's epoch said about the gateway's mapping state.
    /// </summary>
    /// <remarks>
    /// Set on failed outcomes too, whenever the gateway answered at all: a request that a restarted
    /// gateway went on to refuse still proves the entry ChunkPilot thought it held is gone, and that
    /// fact must not be lost because the reply happened to carry a non-zero result code.
    /// </remarks>
    public GatewayContinuity Continuity { get; init; } = GatewayContinuity.Unknown;

    /// <summary>The foreign entry that blocked the request, when the mechanism could report one.</summary>
    public ExistingRouterMapping? Conflicting { get; init; }

    /// <summary>Exact provider-level detail for Technical details. Never primary copy.</summary>
    public string Detail { get; init; } = "";

    public static RouterMappingOutcome Failed(
        RouterMappingMechanism mechanism, RouterMappingFailure failure, string detail) =>
        new() { Success = false, Mechanism = mechanism, Failure = failure, Detail = detail };
}

/// <summary>
/// One router-control mechanism. Implementations own their wire format and nothing else: they do not
/// persist, do not decide policy and never act without an explicit call.
/// </summary>
public interface IRouterMappingProvider
{
    RouterMappingMechanism Mechanism { get; }

    /// <summary>Read-only capability check. Must not create, modify or delete any mapping.</summary>
    Task<RouterDiscoveryResult> DiscoverAsync(RouterLanBinding binding, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what the router reports for one public port. Returns <c>null</c> when the port is free, and
    /// throws nothing for "not supported": mechanisms that cannot read report through
    /// <see cref="CanQueryExistingMappings"/> instead.
    /// </summary>
    Task<ExistingRouterMapping?> QueryAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        MappingTransport transport,
        int externalPort,
        CancellationToken cancellationToken);

    /// <summary>Whether <see cref="QueryAsync"/> can answer at all for this mechanism.</summary>
    bool CanQueryExistingMappings { get; }

    Task<RouterMappingOutcome> CreateAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a mapping the caller has already proven it owns. Providers never verify ownership
    /// themselves; that decision belongs to <see cref="RouterMappingPolicy"/> and the Agent.
    /// </summary>
    Task<RouterMappingOutcome> RemoveAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Sends one datagram to a gateway and waits for that gateway's reply. Bounded and cancellable.</summary>
public interface IGatewayDatagramChannel
{
    /// <param name="onSent">
    /// Invoked once, and only once the complete datagram has actually been handed to the socket, as
    /// confirmed by a send result equal to the payload length. Never invoked when it was not: a socket
    /// that could not be opened, a failed or short send, and a cancellation that arrived first all leave
    /// it unused.
    /// </param>
    /// <remarks>
    /// The callback exists because "the send operation was started" and "the datagram went out" are
    /// different facts, and RFC 6886 section 3.7's obligation turns on the second. Only the transport
    /// knows which has happened, so only the transport may say so. It is deliberately about the send
    /// alone: whether a reply ever arrives is a separate question with a separate answer.
    /// </remarks>
    Task<byte[]?> ExchangeAsync(
        IPAddress localAddress,
        IPEndPoint gateway,
        byte[] payload,
        TimeSpan timeout,
        Action? onSent,
        CancellationToken cancellationToken);
}

public sealed record SsdpDiscoveryResponse(
    string SearchTarget,
    string Location,
    string UniqueServiceName,
    string Server,
    IPAddress Source);

/// <summary>Performs one bounded SSDP M-SEARCH from a chosen local address.</summary>
public interface ISsdpSearchChannel
{
    Task<IReadOnlyList<SsdpDiscoveryResponse>> SearchAsync(
        IPAddress localAddress,
        IReadOnlyList<string> searchTargets,
        TimeSpan maximumWait,
        CancellationToken cancellationToken);
}

public sealed record UpnpSoapResponse(
    bool Success,
    IReadOnlyDictionary<string, string> Values,
    int ErrorCode,
    string ErrorDescription);

/// <summary>Retrieves UPnP descriptions and invokes SOAP actions.</summary>
public interface IUpnpControlChannel
{
    Task<string> GetDescriptionAsync(Uri url, CancellationToken cancellationToken);

    Task<UpnpSoapResponse> InvokeAsync(
        Uri controlUrl,
        string serviceType,
        string action,
        IReadOnlyList<KeyValuePair<string, string>> arguments,
        CancellationToken cancellationToken);
}

/// <summary>Supplies the interfaces and gateways of this machine. Replaced wholesale in tests.</summary>
public interface IRouterNetworkView
{
    IReadOnlyList<RouterGatewayCandidate> Enumerate();
}

/// <summary>
/// Bounded timing and endpoint settings. Production uses the defaults; tests point the datagram
/// mechanisms at a loopback fixture instead of the real port so no real router is ever contacted.
/// </summary>
public sealed record RouterMappingOptions
{
    /// <summary>PCP and NAT-PMP both use 5351 (RFC 6887 section 8.1, RFC 6886 section 3).</summary>
    public int GatewayControlPort { get; init; } = 5351;

    /// <summary>
    /// Bounded interactive retransmission. RFC 6886 describes nine attempts over 64 seconds before
    /// concluding non-support; ChunkPilot stops far earlier and reports "could not be confirmed"
    /// instead of "unsupported" so a slow router is never misreported.
    /// </summary>
    public IReadOnlyList<TimeSpan> DatagramAttemptTimeouts { get; init; } =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromMilliseconds(1000)
    ];

    /// <summary>SSDP MX, and the total time the search collects responses for.</summary>
    public TimeSpan SsdpMaximumWait { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The whole capability check, across every mechanism, is bounded by this.</summary>
    public TimeSpan DiscoveryBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>One create, renew or remove, bounded.</summary>
    public TimeSpan OperationBudget { get; init; } = TimeSpan.FromSeconds(25);
}
