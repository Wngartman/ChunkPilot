using ChunkPilot.Core;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Infrastructure;

/// <summary>What one bounded capability check concluded.</summary>
public sealed record RouterCapabilityReport
{
    public RouterLanBinding? Binding { get; init; }
    public RouterDiscoveryResult? Selected { get; init; }
    public IReadOnlyList<RouterDiscoveryResult> Attempts { get; init; } = [];
    public RouterMappingFailure Failure { get; init; } = RouterMappingFailure.None;
    public string Detail { get; init; } = "";

    public bool Supported => Selected is { Supported: true };

    /// <summary>
    /// What one mechanism's own answers said about the gateway's state, whether or not that mechanism
    /// was the one selected — or whether anything was selected at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A mechanism that answers and is then rejected has still answered. A PCP gateway that has just
    /// restarted may refuse the very request whose header proves the restart, and that refusal takes it
    /// out of the running for this check; the proof it carried is no less true for that. Reading
    /// continuity only from <see cref="Selected"/> loses exactly the evidence that matters most,
    /// because losing state is a good reason for a gateway to start refusing things.
    /// </para>
    /// <para>
    /// Scoped by mechanism on purpose. Every attempt in one report was made against the same binding,
    /// so the gateway and interface are already common to all of them, but the mechanisms are not:
    /// a PCP epoch says nothing whatsoever about a NAT-PMP mapping, and the caller asks about the one
    /// mechanism that actually owns something.
    /// </para>
    /// </remarks>
    public GatewayContinuity ContinuityFor(RouterMappingMechanism mechanism) =>
        mechanism == RouterMappingMechanism.None
            ? GatewayContinuity.Unknown
            : Attempts
                .Where(attempt => attempt.Mechanism == mechanism)
                .Aggregate(GatewayContinuity.Unknown,
                    (strongest, attempt) => GatewayContinuityEvidence.Stronger(strongest, attempt.Continuity));

    /// <summary>Returns a copy whose continuity readings have all been spent.</summary>
    /// <remarks>
    /// A report can be cached and re-used for later operations. The epoch reading inside it is a
    /// one-off observation that was acted on when it was taken, so re-using the report must not present
    /// it as news a second time.
    /// </remarks>
    public RouterCapabilityReport WithContinuitySpent() => this with
    {
        Selected = Selected is { } selected ? selected with { Continuity = GatewayContinuity.Unknown } : null,
        Attempts = [.. Attempts.Select(attempt => attempt with { Continuity = GatewayContinuity.Unknown })]
    };
}

/// <summary>
/// Chooses a mechanism and drives it. Owns provider selection and the bounded budgets; owns no intent,
/// no persistence and no user-facing wording.
/// </summary>
/// <remarks>
/// <para>
/// Mechanisms are probed strictly in sequence — PCP, then NAT-PMP, then UPnP IGD — and the first one to
/// answer wins. Never in parallel: two mechanisms asking the same router to forward the same port at the
/// same time is exactly the race that produces duplicate or orphaned mappings.
/// </para>
/// <para>
/// PCP and NAT-PMP share UDP port 5351 and are probed first because their checks are a single datagram
/// and are read-only by construction (PCP ANNOUNCE, NAT-PMP external address request). A NAT-PMP-only
/// gateway answers a PCP version 2 request with UNSUPP_VERSION, so the fall-through is a defined
/// outcome rather than a timeout. UPnP IGD comes last because SSDP plus two HTTP round trips is the
/// most expensive check, even though it is the mechanism most consumer routers actually enable.
/// </para>
/// <para>
/// Once a mechanism has established a mapping the Agent stores it and asks for that mechanism by name,
/// so renewal and removal never rediscover and never switch mechanism underneath a live mapping.
/// </para>
/// </remarks>
public sealed class RouterMappingService
{
    private static readonly RouterMappingMechanism[] PreferenceOrder =
    [
        RouterMappingMechanism.Pcp,
        RouterMappingMechanism.NatPmp,
        RouterMappingMechanism.UpnpIgd
    ];

    private readonly IRouterNetworkView network;
    private readonly IReadOnlyList<IRouterMappingProvider> providers;
    private readonly RouterMappingOptions options;
    private readonly ILogger<RouterMappingService> logger;

    public RouterMappingService(
        IRouterNetworkView network,
        IEnumerable<IRouterMappingProvider> providers,
        RouterMappingOptions options,
        ILogger<RouterMappingService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        this.network = network;
        this.providers = providers.ToArray();
        this.options = options;
        this.logger = logger;
    }

    public RouterMappingOptions Options => options;

    /// <summary>Selects the LAN identity to forward to, or nothing when no router can be identified.</summary>
    public RouterLanBinding? ResolveBinding() => RouterGatewaySelector.Select(network.Enumerate());

    /// <summary>
    /// Runs a bounded, cancellable capability check. Mutates nothing on the router.
    /// </summary>
    /// <param name="preferred">
    /// A mechanism that has already owned a mapping for this server. It is tried first and, if it
    /// answers, no other mechanism is contacted.
    /// </param>
    public async Task<RouterCapabilityReport> CheckAsync(
        RouterMappingMechanism preferred, CancellationToken cancellationToken)
    {
        var binding = ResolveBinding();
        if (binding is null)
            return new RouterCapabilityReport
            {
                Failure = RouterMappingFailure.NoGatewayFound,
                Detail = "No trustworthy local network adapter with a router on the same subnet was found. " +
                         "VPN, tunnel and virtual adapters are deliberately excluded."
            };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.DiscoveryBudget);
        var attempts = new List<RouterDiscoveryResult>();
        try
        {
            foreach (var mechanism in Order(preferred))
            {
                var provider = Find(mechanism);
                if (provider is null)
                    continue;
                budget.Token.ThrowIfCancellationRequested();
                var result = await provider.DiscoverAsync(binding, budget.Token).ConfigureAwait(false);
                attempts.Add(result);
                if (!result.Supported)
                    continue;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation("Router capability check selected {Mechanism} at {Gateway}.",
                        mechanism, binding.GatewayAddress);
                return new RouterCapabilityReport
                {
                    Binding = binding,
                    Selected = result,
                    Attempts = attempts,
                    Detail = result.Detail
                };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new RouterCapabilityReport
            {
                Binding = binding,
                Attempts = attempts,
                Failure = RouterMappingFailure.Cancelled,
                Detail = "The router capability check was cancelled."
            };
        }
        catch (OperationCanceledException)
        {
            return new RouterCapabilityReport
            {
                Binding = binding,
                Attempts = attempts,
                Failure = RouterMappingFailure.GatewayDidNotRespond,
                Detail = $"The router capability check reached its {options.DiscoveryBudget.TotalSeconds:0} second limit."
            };
        }

        // Every mechanism was tried. "Did not respond" and "answered but cannot do this" are different
        // conclusions and must not collapse into one.
        var everyoneSilent = attempts.Count > 0 &&
                             attempts.All(attempt => attempt.Failure is RouterMappingFailure.GatewayDidNotRespond
                                 or RouterMappingFailure.MalformedReply);
        return new RouterCapabilityReport
        {
            Binding = binding,
            Attempts = attempts,
            Failure = everyoneSilent
                ? RouterMappingFailure.GatewayDidNotRespond
                : RouterMappingFailure.MechanismUnsupported,
            Detail = attempts.Count == 0
                ? "No router-control mechanism is registered."
                : string.Join(" ", attempts.Select(attempt => $"[{attempt.Mechanism}] {attempt.Detail}"))
        };
    }

    /// <summary>Reads what the router reports for a public port, when the mechanism can read at all.</summary>
    public async Task<ExistingRouterMapping?> QueryAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        MappingTransport transport,
        int externalPort,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        var provider = Find(discovery.Mechanism);
        if (provider is null || !provider.CanQueryExistingMappings)
            return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.OperationBudget);
        return await provider.QueryAsync(binding, discovery, transport, externalPort, budget.Token)
            .ConfigureAwait(false);
    }

    public Task<RouterMappingOutcome> CreateAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(discovery, (provider, token) => provider.CreateAsync(binding, discovery, request, token),
            cancellationToken);

    public Task<RouterMappingOutcome> RemoveAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken) =>
        RunAsync(discovery, (provider, token) => provider.RemoveAsync(binding, discovery, request, token),
            cancellationToken);

    private async Task<RouterMappingOutcome> RunAsync(
        RouterDiscoveryResult discovery,
        Func<IRouterMappingProvider, CancellationToken, Task<RouterMappingOutcome>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(operation);
        var provider = Find(discovery.Mechanism);
        if (provider is null)
            return RouterMappingOutcome.Failed(discovery.Mechanism, RouterMappingFailure.MechanismUnsupported,
                $"No provider is registered for {discovery.Mechanism}.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.OperationBudget);
        try
        {
            return await operation(provider, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RouterMappingOutcome.Failed(discovery.Mechanism, RouterMappingFailure.Cancelled,
                "The router operation was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return RouterMappingOutcome.Failed(discovery.Mechanism, RouterMappingFailure.GatewayDidNotRespond,
                $"The router operation reached its {options.OperationBudget.TotalSeconds:0} second limit.");
        }
    }

    private IEnumerable<RouterMappingMechanism> Order(RouterMappingMechanism preferred) =>
        preferred == RouterMappingMechanism.None
            ? PreferenceOrder
            : [preferred, .. PreferenceOrder.Where(mechanism => mechanism != preferred)];

    private IRouterMappingProvider? Find(RouterMappingMechanism mechanism) =>
        providers.FirstOrDefault(provider => provider.Mechanism == mechanism);
}
