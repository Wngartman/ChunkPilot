using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

/// <summary>
/// Owns Direct internet router mapping for every managed server: the user's intent, the ownership
/// evidence, and every mutation of the router.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here runs on its own. A mapping is created only when the user has chosen Direct internet for
/// a specific server and separately confirmed the exposure for that same server. Starting a server,
/// opening Overview, connecting the UI and restarting the agent all reconcile within that recorded
/// intent and never create it.
/// </para>
/// <para>
/// Every mutation for one server runs under that server's gate and carries an operation id. A cancelled
/// operation's result is discarded rather than written, so a stale completion can never overwrite the
/// state a newer operation established.
/// </para>
/// </remarks>
public sealed class RouterMappingCoordinator : IAsyncDisposable
{
    private readonly ChunkPilotStore store;
    private readonly ServerSupervisor supervisor;
    private readonly RouterMappingService mappings;
    private readonly ILogger<RouterMappingCoordinator> logger;
    private readonly ConcurrentDictionary<Guid, ServerRuntime> runtimes = new();
    private readonly TimeProvider clock;

    public RouterMappingCoordinator(
        ChunkPilotStore store,
        ServerSupervisor supervisor,
        RouterMappingService mappings,
        ILogger<RouterMappingCoordinator> logger,
        TimeProvider? clock = null)
    {
        this.store = store;
        this.supervisor = supervisor;
        this.mappings = mappings;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Reads the authoritative state without contacting the router.</summary>
    public async Task<RouterMappingState> GetStateAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var record = await store.GetRouterMappingAsync(serverId, cancellationToken).ConfigureAwait(false);
        return Project(serverId, record, Runtime(serverId));
    }

    /// <summary>
    /// Runs the bounded capability check the user asked for. Reads only; creates nothing.
    /// </summary>
    public Task<RouterMappingState> CheckAsync(Guid serverId, CancellationToken cancellationToken) =>
        RunExclusiveAsync(serverId, RouterMappingPhase.Checking, async (runtime, record, token) =>
        {
            var report = await mappings.CheckAsync(record.Mechanism, token).ConfigureAwait(false);
            var discovered = Identify(report.Binding);
            var continuity = OwnedContinuity(report, record);
            runtime.Capability = Continuable(report);
            // Everything the check learned is written down, including on failure. A later screen that
            // says the gateway is unknown while quoting the gateway's own answer is worse than useless.
            // It is written down as what it is — what this check found — and never as what the mapping
            // this record owns is made of.
            var updated = record with
            {
                LastCheckedAt = clock.GetUtcNow(),
                DiscoveredBinding = discovered,
                CandidateInternalClient = report.Binding?.LocalAddress.ToString() ?? "",
                AvailableMechanism = report.Selected?.Mechanism ?? RouterMappingMechanism.None,
                LastFailure = report.Supported ? RouterMappingFailure.None : report.Failure,
                LastOperationDetail = report.Detail
            };
            // The router-reported address describes whichever router answered, so it may only be
            // attached to a mapping that is actually on that router. A different network's WAN address
            // written over an owned mapping's own is an address the owner would copy and hand out.
            if (report.Supported && report.Selected is { } selected && DescribesOwnedMapping(record, discovered))
                updated = updated with
                {
                    RouterReportedExternalAddress = selected.ExternalAddress,
                    RouterReportedAddressClass = RouterMappingPolicy
                        .ClassifyExternalAddress(selected.ExternalAddress).Class
                };
            // The check reads only, but reading can still learn that what ChunkPilot believed it held
            // is gone: a gateway whose epoch restarted has lost its mapping table, so the entry this
            // record describes no longer exists. Saying so is the truth, and the reconciliation that
            // follows re-establishes it as what it will then be — a new mapping. This holds however the
            // check itself ended: a mechanism that answered and was then refused still answered.
            return continuity == GatewayContinuity.StateLost ? Withdrawn(updated) : updated;
        }, cancellationToken);

    /// <summary>
    /// Records the user's deliberate choice for this one server and establishes the mapping.
    /// </summary>
    /// <param name="consentGranted">
    /// The user confirmed the exposure for this server. Consent is never inherited from another server
    /// and is never assumed; without it nothing on the router is touched.
    /// </param>
    public Task<RouterMappingState> EnableAsync(
        Guid serverId, bool consentGranted, CancellationToken cancellationToken) =>
        EnableAsync(serverId, consentGranted, RouterOperationAuthority.IsolatedFixture(serverId), cancellationToken);

    public Task<RouterMappingState> EnableAsync(
        Guid serverId, bool consentGranted, RouterOperationAuthority authority,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(serverId, RouterMappingPhase.Creating, async (runtime, record, token) =>
        {
            authority.Demand(record, "persisting Direct internet intent");
            if (!consentGranted && !record.ConsentGranted)
                // Refusing to act is not a failure of anything, so no failure is recorded: the surface
                // stays exactly as the user left it and nothing on the router was touched.
                return record with
                {
                    LastOperationDetail = "Direct internet was not enabled because the exposure was not confirmed."
                };
            var now = clock.GetUtcNow();
            var intent = authority.Bind(record with
            {
                DirectInternetEnabled = true,
                ConsentGranted = true,
                ConsentGrantedAt = record.ConsentGrantedAt ?? now,
                Transport = MappingTransport.Tcp,
                Description = RouterMappingPolicy.MappingDescription
            });
            // Intent is written before the attempt so that an Agent that dies mid-create still knows
            // to finish what the owner consented to. Cancelling is the opposite of that, and both the
            // ways it can arrive have to put the intent back: leaving it standing would hand the owner
            // the exposure they just backed out of, opened by the next reconciliation.
            await store.UpsertRouterMappingAsync(intent, token).ConfigureAwait(false);
            try
            {
                var established = await EstablishAsync(runtime, intent, authority, token).ConfigureAwait(false);
                return established is { LastFailure: RouterMappingFailure.Cancelled, HasActiveMapping: false }
                    ? record with { LastOperationDetail = established.LastOperationDetail }
                    : established;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    authority.Demand(record, "rolling back cancelled Direct internet intent");
                    await PersistBoundedAsync(record).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Exact cleanup queued behind this stale operation owns the rollback now.
                }
                throw;
            }
        }, authority, cancellationToken);

    /// <summary>Withdraws the mapping and clears the intent. Safe to call when nothing is established.</summary>
    public Task<RouterMappingState> DisableAsync(Guid serverId, CancellationToken cancellationToken) =>
        DisableAsync(serverId, RouterOperationAuthority.IsolatedFixture(serverId, mayEstablish: false),
            cancellationToken);

    public Task<RouterMappingState> DisableAsync(
        Guid serverId, RouterOperationAuthority authority, CancellationToken cancellationToken) =>
        RunExclusiveAsync(serverId, RouterMappingPhase.Removing, async (runtime, record, token) =>
        {
            authority.Demand(record, "persisting revoked Direct internet intent");
            // Logical authority ends before any router I/O. A gateway that is unreachable may leave
            // exact ownership evidence pending, but it can never leave renewal intent standing.
            var revoked = record with
            {
                DirectInternetEnabled = false,
                ConsentGranted = false,
                ConsentGrantedAt = null,
                LastOperationDetail = "Public connectivity was revoked; exact-owned router cleanup is in progress."
            };
            await PersistBoundedAsync(revoked).ConfigureAwait(false);
            var released = await ReleaseAsync(revoked, authority, token).ConfigureAwait(false);
            return released with
            {
                // Turning Direct internet off genuinely returns this server to "not set up", so the
                // check result that kept the surface alive is cleared with it. A removal that failed
                // still shows through RemovalPending and is never hidden by this.
                LastCheckedAt = released.RemovalPending ? released.LastCheckedAt : null,
                AvailableMechanism = RouterMappingMechanism.None,
                LastFailure = released.RemovalPending
                    ? released.LastFailure
                    : RouterMappingFailure.None
            };
        }, authority, cancellationToken);

    /// <summary>
    /// Brings the router into line with the recorded intent and the server's current lifecycle state.
    /// Called after deliberate lifecycle commands and by periodic reconciliation.
    /// </summary>
    /// <remarks>
    /// Durable intent and the live mapping are separate facts. Intent says the owner wants Direct
    /// internet for this server; it never on its own means a port is open. A stopped server keeps its
    /// intent and loses its mapping.
    /// </remarks>
    public Task<RouterMappingState> SynchronizeAsync(Guid serverId, CancellationToken cancellationToken) =>
        SynchronizeAsync(serverId, RouterOperationAuthority.IsolatedFixture(serverId), cancellationToken);

    public Task<RouterMappingState> SynchronizeAsync(
        Guid serverId, RouterOperationAuthority authority, CancellationToken cancellationToken) =>
        RunExclusiveAsync(serverId, null, async (runtime, record, token) =>
        {
            if (!record.DirectInternetEnabled)
            {
                // Intent is off. Anything still mapped from a previous intent is ChunkPilot's to remove.
                if (!record.HasActiveMapping && !record.RemovalPending)
                    return record;
                return await ReleaseAsync(record, authority, token).ConfigureAwait(false);
            }

            var state = ServerStateOf(serverId);
            var now = clock.GetUtcNow();
            if (state is ServerState.Running)
            {
                var port = ServerPortOf(serverId);
                // An unprovable claim is ended first, so the withdrawal below is only ever asked for a
                // mapping ChunkPilot can actually address. Sending one to the router in front of us for
                // an entry that belongs to a network we cannot name is the mistake this exists to stop.
                record = RouterMappingPolicy.ResolveUnprovableClaim(record, now);
                if (record.HasActiveMapping && record.ExternalPort != port && port > 0)
                {
                    // The server's authoritative port changed. Withdraw the mapping ChunkPilot owns on
                    // the old port before asking for the new one; never leave the old one behind.
                    record = await ReleaseAsync(record, authority, token).ConfigureAwait(false);
                    if (record.RemovalPending)
                        return record;
                }
                // A mapping ChunkPilot cannot confirm from here carries nothing for this server any
                // more — whether because the computer has left that network or because the build that
                // made it never recorded which one it was — so reconciliation establishes one where it
                // provably is rather than waiting for half a lease to pass first.
                if (record.HasActiveMapping && !RouterMappingPolicy.IsRenewalDue(record, now) &&
                    !RouterMappingPolicy.CannotConfirmOwnedMapping(record))
                    return record;
                return await EstablishAsync(runtime, record, authority, token).ConfigureAwait(false);
            }

            if (state is ServerState.Stopped or ServerState.Crashed)
            {
                // A deliberate restart passes briefly through Stopped, and tearing the mapping down
                // and rebuilding it around every restart is churn the router does not need. That is
                // the only reason a stopped server may keep its exposure, it is answered by the
                // restart operation itself rather than by a timer, and it stops being true the moment
                // that operation ends � including when it ends in failure.
                if (IsRestartInProgress(serverId))
                    return record;
                if (!record.HasActiveMapping && !record.RemovalPending)
                    return record;
                return await ReleaseAsync(record, authority, token).ConfigureAwait(false);
            }

            // Starting, Stopping, Restarting, Saving, Unresponsive or Unknown: a transition is under
            // way and is not evidence about the router. Renew if the lease needs it and change nothing
            // else. Renewal requires an existing mapping, so this can never create one.
            return RouterMappingPolicy.IsRenewalDue(record, now)
                ? await EstablishAsync(runtime, record, authority, token).ConfigureAwait(false)
                : record;
        }, authority, cancellationToken);

    /// <summary>
    /// Withdraws a provably owned mapping before a server is deleted. Returns text to append to the
    /// deletion result when the exposure could not be closed, so the failure is reported rather than lost.
    /// </summary>
    public async Task<string> PrepareForDeletionAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var record = await store.GetRouterMappingAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.HasActiveMapping && !record.RemovalPending)
        {
            if (record is not null)
                await store.DeleteRouterMappingAsync(serverId, cancellationToken).ConfigureAwait(false);
            return "";
        }
        var state = await RunExclusiveAsync(serverId, RouterMappingPhase.Removing,
            (_, current, token) => ReleaseAsync(current,
                RouterOperationAuthority.IsolatedFixture(serverId, mayEstablish: false), token),
            cancellationToken).ConfigureAwait(false);
        if (!state.RemovalPending)
        {
            await store.DeleteRouterMappingAsync(serverId, cancellationToken).ConfigureAwait(false);
            return "";
        }
        return " The router port ChunkPilot opened for this server could not be closed; it is remembered and " +
               "will be retried.";
    }

    /// <summary>Cancels any router operation in flight for one server.</summary>
    public void Cancel(Guid serverId) => Runtime(serverId).Cancellation?.Cancel();

    private async Task<RouterMappingRecord> EstablishAsync(
        ServerRuntime runtime, RouterMappingRecord record, RouterOperationAuthority authority,
        CancellationToken cancellationToken)
    {
        authority.Demand(record, "router establishment");
        var port = ServerPortOf(record.ServerId);
        if (port is <= 0 or > 65535)
            return record with
            {
                LastFailure = RouterMappingFailure.ServerPortUnavailable,
                LastOperationDetail = "The server has no usable port to forward."
            };

        // Before a single thing about the router in front of us is evaluated. An ownership claim with no
        // network behind it is history, not title: leaving it standing is what would let this router's
        // table be read as evidence about it, and let an entry that merely resembles it be renewed as
        // though it were the same one. What it described is kept as a possible exposure; what it
        // asserted is ended here.
        record = RouterMappingPolicy.ResolveUnprovableClaim(record, clock.GetUtcNow());

        // A cached report was already acted on when it was taken, so whatever its epoch said then must
        // not be acted on a second time. Only a report this operation fetched itself carries evidence
        // that is still news.
        authority.Demand(record, "router discovery");
        var cached = runtime.Capability;
        var report = cached ?? await mappings.CheckAsync(record.Mechanism, cancellationToken)
            .ConfigureAwait(false);
        var discoveryContinuity = OwnedContinuity(report, record);
        if (!report.Supported || report.Binding is null || report.Selected is null)
        {
            runtime.Capability = null;
            // A check that found nothing usable can still have been told that the gateway restarted.
            // The old entry is gone whether or not anything is available to replace it, and admitting
            // that must not wait for a successful replacement that may never come.
            var unusable = record with
            {
                LastCheckedAt = clock.GetUtcNow(),
                DiscoveredBinding = report.Binding is null
                    ? record.DiscoveredBinding
                    : Identify(report.Binding),
                CandidateInternalClient = report.Binding?.LocalAddress.ToString() ?? "",
                AvailableMechanism = RouterMappingMechanism.None,
                LastFailure = report.Failure,
                LastOperationDetail = report.Detail
            };
            return discoveryContinuity == GatewayContinuity.StateLost ? Withdrawn(unusable) : unusable;
        }
        runtime.Capability = Continuable(report);
        var binding = report.Binding;
        var discovery = report.Selected;
        var discovered = Identify(binding);

        var request = new RouterMappingRequest
        {
            Transport = MappingTransport.Tcp,
            InternalPort = port,
            ExternalPort = port,
            LeaseSeconds = RouterMappingPolicy.RequestedLeaseSeconds,
            Description = RouterMappingPolicy.MappingDescription,
            OwnershipToken = record.OwnershipToken
        };

        // Read the port first where the mechanism can. Something ChunkPilot cannot prove it owns is
        // never overwritten, whatever the user asked for.
        authority.Demand(record, "router ownership query");
        var existing = await mappings
            .QueryAsync(binding, discovery, request.Transport, port, cancellationToken).ConfigureAwait(false);
        // What the attempt learned, kept separate from what it owns. Only the success path below may
        // write ownership evidence; an attempt that created nothing must never leave any behind.
        var attempted = record with
        {
            DiscoveredBinding = discovered,
            CandidateInternalClient = binding.LocalAddress.ToString(),
            AvailableMechanism = discovery.Mechanism,
            LastCheckedAt = clock.GetUtcNow()
        };

        // The router's own table outranks anything ChunkPilot wrote down. Where the mechanism can read
        // it, an entry that has vanished — or that now belongs to somebody else — has ended the
        // establishment ChunkPilot was holding, and it is ended here, before any branch below can carry
        // that ownership forward. Nothing is sent to remove it: what is there is not ChunkPilot's, and
        // what was ChunkPilot's is no longer there. Only the mechanism that created the entry may
        // judge it, because one mechanism's view of the router says nothing about another's — and only
        // the router that holds it, because one router's table says nothing about another's either.
        if (MechanismCanRead(discovery.Mechanism) && record.HasActiveMapping &&
            record.Mechanism == discovery.Mechanism && DescribesOwnedMapping(record, discovered) &&
            (existing is null || !RouterMappingPolicy.ProvesOwnership(record, existing)))
        {
            record = Withdrawn(record);
            attempted = Withdrawn(attempted);
        }

        var candidate = attempted with
        {
            Mechanism = discovery.Mechanism,
            Transport = request.Transport,
            ExternalPort = port,
            InternalPort = port,
            InternalClient = binding.LocalAddress.ToString(),
            Description = request.Description
        };
        if (existing is not null && !RouterMappingPolicy.ProvesOwnership(candidate, existing))
            return attempted with
            {
                LastFailure = RouterMappingFailure.ForeignMappingPresent,
                LastOperationDetail =
                    $"The router already forwards public port {port} to {Describe(existing)}. ChunkPilot did not " +
                    "change it because it cannot prove that mapping is its own."
            };

        authority.Demand(record, "router create or renewal request");
        var outcome = await mappings.CreateAsync(binding, discovery, request, cancellationToken)
            .ConfigureAwait(false);
        var revokedAfterWireSuccess = false;
        try
        {
            authority.Demand(record, "accepting router create or renewal result");
        }
        catch (OperationCanceledException) when (outcome.Success &&
                                                  authority.CanRetainRevokedCleanupEvidence(record))
        {
            revokedAfterWireSuccess = true;
        }
        var now = clock.GetUtcNow();

        // The gateway's own epoch, from this operation's discovery or from the request itself. A
        // restart ends the establishment ChunkPilot was holding whether or not the request that
        // revealed it went on to succeed, and whatever that request was finally classified as.
        var continuity = GatewayContinuityEvidence.Stronger(discoveryContinuity,
            record.Mechanism == discovery.Mechanism ? outcome.Continuity : GatewayContinuity.Unknown);
        if (continuity == GatewayContinuity.StateLost)
        {
            record = Withdrawn(record);
            attempted = Withdrawn(attempted);
        }

        if (!outcome.Success)
            return attempted with
            {
                LastCheckedAt = now,
                LastFailure = outcome.Failure,
                LastOperationDetail = outcome.Detail
            };

        // Whether this is the same entry continuing or a different one taking its place. A mapping that
        // was not open a moment ago is always a new establishment, however exactly it resembles the one
        // before it; renewing a live entry on unchanged terms is the same one continuing. Resemblance
        // is necessary and never sufficient — the values a recreated mapping carries are identical by
        // construction — so continuation additionally requires affirmative evidence from the mechanism
        // itself, and where none exists the identity is replaced rather than assumed.
        var continues =
            record is { HasActiveMapping: true, RemovalPending: false } &&
            record.Mechanism == discovery.Mechanism &&
            // On the same router, reached the same way. A different one cannot be continuing an entry
            // it never held, however exactly the values line up.
            record.OwnedBinding.Matches(discovered) &&
            record.Transport == request.Transport &&
            record.ExternalPort == port &&
            record.InternalPort == port &&
            record.InternalClient.Equals(binding.LocalAddress.ToString(), StringComparison.OrdinalIgnoreCase) &&
            ProvesContinuation(discovery.Mechanism, existing, continuity);
        if (!continues)
            runtime.ReplaceMappingInstance();

        var externalAddress = outcome.ExternalAddress.Length > 0
            ? outcome.ExternalAddress
            : discovery.ExternalAddress;
        var established = candidate with
        {
            // The one place ownership binding is written: a mapping belongs to the network it was just
            // created on, and to no other.
            OwnedBinding = discovered,
            ControlUrl = outcome.ControlUrl.Length > 0 ? outcome.ControlUrl : discovery.ControlUrl,
            ServiceType = outcome.ServiceType.Length > 0 ? outcome.ServiceType : discovery.ServiceType,
            OwnershipToken = outcome.OwnershipToken.Length > 0 ? outcome.OwnershipToken : record.OwnershipToken,
            HasActiveMapping = true,
            RemovalPending = false,
            LeaseIsFinite = outcome.LeaseIsFinite,
            LeaseSeconds = outcome.LeaseSeconds,
            LeaseExpiresAt = outcome.LeaseIsFinite ? now.AddSeconds(outcome.LeaseSeconds) : null,
            EstablishedAt = now,
            LastCheckedAt = now,
            RouterReportedExternalAddress = externalAddress,
            RouterReportedAddressClass = RouterMappingPolicy.ClassifyExternalAddress(externalAddress).Class,
            LastFailure = RouterMappingFailure.None,
            LastOperationDetail = outcome.Detail
        };
        return revokedAfterWireSuccess
            ? established with
            {
                DirectInternetEnabled = false,
                ConsentGranted = false,
                ConsentGrantedAt = null,
                RemovalPending = true,
                LastOperationDetail =
                    "The lease was revoked while the router request was in flight; exact-owned cleanup is pending."
            }
            : established;
    }

    private async Task<RouterMappingRecord> ReleaseAsync(
        RouterMappingRecord record, RouterOperationAuthority authority, CancellationToken cancellationToken)
    {
        authority.Demand(record, "router cleanup");
        if (!record.HasActiveMapping && !record.RemovalPending)
            return record with { RemovalPending = false };

        var binding = mappings.ResolveBinding();
        var discovery = RebuildDiscovery(record);
        if (binding is null || discovery is null)
            return Retain(record,
                binding is null
                    ? "The mapping could not be withdrawn because no trustworthy local network adapter with a " +
                      "router on the same subnet was available."
                    : "The mapping could not be withdrawn because the mechanism that created it is no longer known.");

        // A mapping lives on the router it was made on, and a deletion may only ever be sent to that
        // router. Anything else asks a stranger's gateway about a port ChunkPilot never opened on it,
        // while leaving the real exposure standing and unrecorded. Proof is required, so an unproven
        // network is refused exactly like a wrong one — including the record written by a build that
        // never recorded a network at all, where no proof can exist. Nothing is sent either way; the
        // record keeps its owner, and the withdrawal succeeds if this computer is ever provably back on
        // the network that holds it.
        var current = Identify(binding);
        if (!record.OwnedBinding.Matches(current))
            return Retain(record, record.OwnedBinding.IsKnown
                ? $"The mapping was created through {record.OwnedBinding.GatewayAddress} on another network " +
                  $"connection, and this computer now reaches {current.GatewayAddress} instead. Nothing was sent, " +
                  "because a deletion must only ever reach the router that holds the mapping."
                : $"An earlier version of ChunkPilot opened public port {record.ExternalPort} without recording " +
                  $"which network connection it used{DescribeLegacyGateway(record)}, so nothing can prove that " +
                  $"the router reachable now ({current.GatewayAddress}) is the one holding it. Nothing was sent, " +
                  "because a deletion must only ever reach the router that holds the mapping.");

        if (!binding.LocalAddress.ToString().Equals(record.InternalClient, StringComparison.OrdinalIgnoreCase) &&
            logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "This computer's local address changed from {Recorded} to {Current}; the recorded mapping is " +
                "still addressed by its public port.", record.InternalClient, binding.LocalAddress);

        var request = new RouterMappingRequest
        {
            Transport = record.Transport,
            InternalPort = record.InternalPort,
            ExternalPort = record.ExternalPort,
            LeaseSeconds = 0,
            Description = record.Description,
            OwnershipToken = record.OwnershipToken
        };

        // Where the router can be read, confirm ownership once more before deleting anything.
        authority.Demand(record, "router cleanup ownership query");
        var existing = await mappings
            .QueryAsync(binding, discovery, record.Transport, record.ExternalPort, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null && MechanismCanRead(record.Mechanism))
            return record with
            {
                HasActiveMapping = false,
                RemovalPending = false,
                LeaseExpiresAt = null,
                EstablishedAt = null,
                LastCheckedAt = clock.GetUtcNow(),
                LastFailure = RouterMappingFailure.None,
                LastOperationDetail = $"The router no longer lists public port {record.ExternalPort}; nothing to withdraw."
            };
        if (existing is not null && !RouterMappingPolicy.ProvesOwnership(record, existing))
            return record with
            {
                HasActiveMapping = false,
                RemovalPending = false,
                LastCheckedAt = clock.GetUtcNow(),
                LastFailure = RouterMappingFailure.ForeignMappingPresent,
                LastOperationDetail =
                    $"Public port {record.ExternalPort} is now mapped to {Describe(existing)}, which ChunkPilot " +
                    "cannot prove is its own. It was left untouched."
            };

        authority.Demand(record, "router removal request");
        var outcome = await mappings.RemoveAsync(binding, discovery, request, cancellationToken)
            .ConfigureAwait(false);
        authority.Demand(record, "accepting router removal result");
        if (!outcome.Success)
            return Retain(record, outcome.Detail);
        return record with
        {
            HasActiveMapping = false,
            RemovalPending = false,
            LeaseExpiresAt = null,
            EstablishedAt = null,
            LastCheckedAt = clock.GetUtcNow(),
            LastFailure = RouterMappingFailure.None,
            LastOperationDetail = outcome.Detail
        };
    }

    private RouterMappingRecord Retain(RouterMappingRecord record, string detail)
    {
        logger.LogWarning("Router mapping for {ServerId} could not be withdrawn: {Detail}", record.ServerId, detail);
        return record with
        {
            RemovalPending = true,
            LastCheckedAt = clock.GetUtcNow(),
            LastFailure = RouterMappingFailure.RemovalFailed,
            LastOperationDetail = detail
        };
    }

    private static bool MechanismCanRead(RouterMappingMechanism mechanism) =>
        mechanism == RouterMappingMechanism.UpnpIgd;

    /// <summary>
    /// Whether the mapping the router now holds is provably the one ChunkPilot already had, rather than
    /// an identical-looking replacement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two mechanism families answer this with completely different evidence, and neither can be
    /// substituted for the other. A mechanism that can read the table answers it directly: the entry
    /// was there immediately before the request, so the request renewed it. The datagram mechanisms
    /// cannot read anything, and their renewal message is byte-for-byte a creation message — RFC 6886
    /// section 3.7 says so outright — so the only thing that distinguishes the two is the gateway's own
    /// epoch, and nothing less than an affirmative continuation of it will do.
    /// </para>
    /// <para>
    /// Failing closed here costs a fresh mapping identity and therefore a stale external result, and
    /// nothing else: the mapping itself is still active and still ChunkPilot's. That is the correct way
    /// round. The opposite mistake would let evidence gathered about one entry be presented as current
    /// for another.
    /// </para>
    /// </remarks>
    private static bool ProvesContinuation(
        RouterMappingMechanism mechanism, ExistingRouterMapping? existing, GatewayContinuity continuity) =>
        MechanismCanRead(mechanism) ? existing is not null : continuity == GatewayContinuity.Confirmed;

    /// <summary>
    /// What a capability check said about the gateway this record's own mapping was established
    /// against, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three separate things have to line up before a check's evidence may be turned against an
    /// existing mapping, and none of them implies the others. There has to be a mapping to invalidate.
    /// The evidence has to come from the mechanism that owns it, because a PCP epoch says nothing about
    /// a NAT-PMP entry and neither says anything about a UPnP one. And it has to come from the same
    /// gateway: a laptop that moved to another network gets a new router, and that router's epoch is
    /// not a statement about the mapping left behind on the old one.
    /// </para>
    /// <para>
    /// Sameness is judged on the whole binding rather than on the gateway's text, because the text is
    /// not unique: 192.168.1.1 over Wi-Fi is a different router from 192.168.1.1 over Ethernet, and the
    /// epoch history is already filed per interface for exactly that reason.
    /// </para>
    /// </remarks>
    private static GatewayContinuity OwnedContinuity(
        RouterCapabilityReport report, RouterMappingRecord record)
    {
        if (!record.HasActiveMapping || record.Mechanism == RouterMappingMechanism.None)
            return GatewayContinuity.Unknown;
        if (report.Binding is not { } binding || !record.OwnedBinding.Matches(Identify(binding)))
            return GatewayContinuity.Unknown;
        return report.ContinuityFor(record.Mechanism);
    }

    /// <summary>
    /// Whether what was just observed through this binding is an observation of the mapping this record
    /// owns, and may therefore be written against it.
    /// </summary>
    /// <remarks>
    /// Owning nothing makes the question moot: a check that found a router is exactly how an unowned
    /// record learns what it learned. Owning something requires proof, and a record that owns a mapping
    /// without recording which network it is on has none — it was written by a build that never
    /// recorded one, so no router reachable from here can be shown to be the right one.
    /// </remarks>
    private static bool DescribesOwnedMapping(RouterMappingRecord record, RouterBindingIdentity discovered) =>
        !record.HasActiveMapping || record.OwnedBinding.Matches(discovered);

    /// <summary>
    /// The gateway an older build recorded, phrased for a technical detail line, or nothing when even
    /// that was not written down.
    /// </summary>
    private static string DescribeLegacyGateway(RouterMappingRecord record) =>
        record.OwnedBinding.GatewayAddress.Length > 0
            ? $" (it recorded the address {record.OwnedBinding.GatewayAddress}, which several routers share)"
            : "";

    /// <summary>Names the network context a chosen binding is, in the terms a record remembers.</summary>
    private static RouterBindingIdentity Identify(RouterLanBinding? binding) =>
        binding is null
            ? new RouterBindingIdentity()
            : new RouterBindingIdentity
            {
                InterfaceId = binding.InterfaceId,
                GatewayAddress = binding.GatewayAddress.ToString()
            };

    /// <summary>
    /// Drops the ownership evidence for an entry that is provably no longer on the router.
    /// </summary>
    /// <remarks>
    /// Deliberately silent: nothing is sent, because there is nothing left to withdraw. It leaves
    /// <see cref="RouterMappingRecord.RemovalPending"/> alone, so a removal that genuinely failed
    /// earlier is still remembered and still retried.
    /// </remarks>
    private static RouterMappingRecord Withdrawn(RouterMappingRecord record) =>
        record.HasActiveMapping
            ? record with { HasActiveMapping = false, LeaseExpiresAt = null, EstablishedAt = null }
            : record;

    /// <summary>
    /// Caches a capability report with its epoch reading spent, so re-using the report cannot make the
    /// same one-off piece of evidence look like news a second time.
    /// </summary>
    private static RouterCapabilityReport? Continuable(RouterCapabilityReport report) =>
        report.Supported ? report.WithContinuitySpent() : null;

    /// <summary>
    /// Rebuilds the mechanism handle for a mapping that is already established, so renewal and removal
    /// use the mechanism that owns it rather than rediscovering and possibly choosing another.
    /// </summary>
    private static RouterDiscoveryResult? RebuildDiscovery(RouterMappingRecord record) =>
        record.Mechanism == RouterMappingMechanism.None
            ? null
            : new RouterDiscoveryResult
            {
                Mechanism = record.Mechanism,
                Supported = true,
                ExternalAddress = record.RouterReportedExternalAddress,
                SupportsFiniteLeases = record.LeaseIsFinite,
                ControlUrl = record.ControlUrl,
                ServiceType = record.ServiceType,
                Detail = "Rebuilt from the mechanism that established this mapping."
            };

    private Task<RouterMappingState> RunExclusiveAsync(
        Guid serverId,
        RouterMappingPhase? busyPhase,
        Func<ServerRuntime, RouterMappingRecord, CancellationToken, Task<RouterMappingRecord>> operation,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(serverId, busyPhase, operation,
            RouterOperationAuthority.IsolatedFixture(serverId, mayEstablish: false), cancellationToken);

    private async Task<RouterMappingState> RunExclusiveAsync(
        Guid serverId,
        RouterMappingPhase? busyPhase,
        Func<ServerRuntime, RouterMappingRecord, CancellationToken, Task<RouterMappingRecord>> operation,
        RouterOperationAuthority authority,
        CancellationToken cancellationToken)
    {
        var runtime = Runtime(serverId);
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            runtime.OperationId = operationId;
            runtime.Cancellation = cancellation;
            runtime.LivePhase = busyPhase;
            var record = await store.GetRouterMappingAsync(serverId, cancellation.Token).ConfigureAwait(false)
                         ?? new RouterMappingRecord { ServerId = serverId };
            authority.Demand(record, "serialized router gate execution");
            var updated = await operation(runtime, record, cancellation.Token).ConfigureAwait(false);

            // The operation has finished, so nothing is in flight any more. Clearing this before the
            // projection is what stops a completed check reporting itself as still checking.
            ClearLivePhase(runtime, operationId);

            // A newer operation may have taken ownership while this one was cancelled. Discard rather
            // than overwrite what the newer one established.
            if (runtime.OperationId != operationId)
                return Project(serverId, record, runtime);
            if (!ReferenceEquals(updated, record))
            {
                authority.DemandPersistence(updated, "persisting the router operation result");
                await PersistBoundedAsync(updated).ConfigureAwait(false);
            }
            return Project(serverId, updated, runtime);
        }
        catch (OperationCanceledException)
        {
            // A cancelled operation created nothing, so the durable record is still the truth and the
            // surface falls back to whatever it described before.
            ClearLivePhase(runtime, operationId);
            RouterMappingRecord? current = null;
            try
            {
                using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                current = await store.GetRouterMappingAsync(serverId, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Reading router state after cancellation reached its bounded persistence deadline for {ServerId}.",
                    serverId);
            }
            return Project(serverId, current, runtime);
        }
        finally
        {
            if (runtime.OperationId == operationId)
                runtime.Cancellation = null;
            ClearLivePhase(runtime, operationId);
            runtime.Gate.Release();
        }
    }

    private async Task PersistBoundedAsync(RouterMappingRecord record)
    {
        using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await store.UpsertRouterMappingAsync(record, persistenceTimeout.Token).ConfigureAwait(false);
    }

    private static void ClearLivePhase(ServerRuntime runtime, Guid operationId)
    {
        if (runtime.OperationId == operationId)
            runtime.LivePhase = null;
    }

    private RouterMappingState Project(Guid serverId, RouterMappingRecord? record, ServerRuntime runtime)
    {
        var port = ServerPortOf(serverId);
        if (record is null)
            return RouterMappingState.Off(serverId, port) with { Busy = runtime.LivePhase is not null };

        var assessment = RouterMappingPolicy.ClassifyExternalAddress(record.RouterReportedExternalAddress);
        var phase = ResolvePhase(record, runtime);
        return new RouterMappingState
        {
            ServerId = serverId,
            Enabled = record.DirectInternetEnabled,
            ConsentGranted = record.ConsentGranted,
            Phase = phase,
            Mechanism = record.Mechanism,
            Transport = record.Transport,
            // A settled good state reports no failure, whatever an abandoned earlier attempt left behind.
            Failure = phase is RouterMappingPhase.Active or RouterMappingPhase.Off
                or RouterMappingPhase.Supported or RouterMappingPhase.Inactive
                ? RouterMappingFailure.None
                : record.LastFailure,
            AvailableMechanism = record.AvailableMechanism,
            // The gateway a mapping is actually on outranks the one a check last reached. With nothing
            // owned, the check's answer is the only one there is.
            GatewayAddress = record.HasActiveMapping || record.RemovalPending
                ? record.OwnedBinding.GatewayAddress
                : record.DiscoveredBinding.GatewayAddress,
            OwnedNetworkLeft = RouterMappingPolicy.HasLeftOwnedNetwork(record),
            OwnedNetworkUnknown = RouterMappingPolicy.HasUnknownOwnedNetwork(record),
            // Shown only while the entry it describes could still be there. A lease that has run out
            // takes the exposure with it, and nothing here is a claim that a port is open now.
            LegacyExposure = record.LegacyExposure is { } exposure && exposure.MayPersistAt(clock.GetUtcNow())
                ? exposure
                : null,
            InternalClient = record.InternalClient,
            CandidateInternalClient = record.CandidateInternalClient,
            InternalPort = port > 0 ? port : record.InternalPort,
            ExternalPort = record.ExternalPort,
            RouterReportedExternalAddress = record.RouterReportedExternalAddress,
            RouterReportedAddressClass = assessment.Class,
            UpstreamNatSuspected = assessment.SuggestsUpstreamNat,
            // Resolved from the phase that was just decided, so the identity of an open mapping is
            // minted once and then held for as long as that same entry stays open, and is dropped the
            // instant the phase says nothing is.
            MappingInstanceId = runtime.ResolveMappingInstance(phase == RouterMappingPhase.Active),
            LeaseIsFinite = record.LeaseIsFinite,
            LeaseExpiresAt = record.LeaseExpiresAt,
            LastCheckedAt = record.LastCheckedAt,
            RemovalPending = record.RemovalPending,
            LastOperationDetail = record.LastOperationDetail,
            Busy = runtime.LivePhase is not null,
            OperationId = runtime.OperationId
        };
    }

    /// <summary>
    /// Turns durable evidence into the one state the App renders.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record is the only source of truth for a settled state; the runtime carries nothing but
    /// "an operation is in flight right now". That split is deliberate. The previous version consulted
    /// the user's intent first and returned <see cref="RouterMappingPhase.Off"/> whenever Direct
    /// internet had not been enabled yet — which is *always* true at the moment a capability check
    /// finishes, because intent is only written when the exposure is confirmed. A router that answered
    /// perfectly therefore rendered as "Not set up", the confirmation never appeared, and the feature
    /// could not be reached at all. A check that failed rendered as "Not set up" too, so no failure
    /// could ever be explained either.
    /// </para>
    /// <para>
    /// Deriving the phase from what is written down also means a failure survives a ViewModel refresh
    /// and an Agent restart: it stays visible until the user retries, turns Direct internet off, or
    /// something authoritative changes.
    /// </para>
    /// </remarks>
    private static RouterMappingPhase ResolvePhase(RouterMappingRecord record, ServerRuntime runtime)
    {
        if (runtime.LivePhase is { } live)
            return live;
        if (record.RemovalPending)
            return RouterMappingPhase.NeedsAttention;
        // The mapping is on the router it was made on, and this computer either is somewhere else or
        // cannot show it is not — the second being every row written before ownership recorded a
        // network. Nothing about it can be confirmed from here and nothing it was carrying is current,
        // so it stops being presented as open, and the identity that presentation mints is dropped with
        // it. That is what keeps a verification gathered beforehand from becoming current again, and it
        // is why an upgrade cannot inherit one. The record is left intact, because the entry may still
        // have to be withdrawn.
        if (RouterMappingPolicy.CannotConfirmOwnedMapping(record))
            return RouterMappingPhase.NeedsAttention;
        if (record.HasActiveMapping)
            return RouterMappingPhase.Active;
        // Cancellation is the user stopping ChunkPilot, not the router refusing it, and it creates
        // nothing. The surface falls back to the last settled truth rather than reporting a fault.
        if (record.LastFailure is not RouterMappingFailure.None and not RouterMappingFailure.Cancelled)
            return PhaseForFailure(record.LastFailure);
        // Set up, with nothing open. This is a stopped server's normal resting state and is the whole
        // reason intent and mapping are separate: the intent survives, the exposure does not.
        if (record.DirectInternetEnabled)
            return RouterMappingPhase.Inactive;
        // A check answered and nothing has failed since. Nothing is exposed by this state; it only
        // says the router can be asked.
        return record.LastCheckedAt is null ? RouterMappingPhase.Off : RouterMappingPhase.Supported;
    }

    private static RouterMappingPhase PhaseForFailure(RouterMappingFailure failure) => failure switch
    {
        RouterMappingFailure.None => RouterMappingPhase.Supported,
        RouterMappingFailure.Cancelled => RouterMappingPhase.Off,
        RouterMappingFailure.NoGatewayFound or RouterMappingFailure.MechanismUnsupported =>
            RouterMappingPhase.Unavailable,
        RouterMappingFailure.GatewayDidNotRespond or RouterMappingFailure.MalformedReply =>
            RouterMappingPhase.Undetermined,
        RouterMappingFailure.ForeignMappingPresent => RouterMappingPhase.Conflict,
        _ => RouterMappingPhase.NeedsAttention
    };

    private static string Describe(ExistingRouterMapping existing)
    {
        var target = existing.InternalClient.Length > 0
            ? $"{existing.InternalClient}:{existing.InternalPort}"
            : "another device";
        return existing.Description.Length > 0 ? $"{target} ({existing.Description})" : target;
    }

    /// <summary>
    /// Whether a deliberate restart of this server is still running, which is the only reason a
    /// stopped server may keep a router mapping.
    /// </summary>
    private bool IsRestartInProgress(Guid serverId)
    {
        try
        {
            return supervisor.Get(serverId).IsRestartInProgress;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private ServerState ServerStateOf(Guid serverId)
    {
        try
        {
            return supervisor.Get(serverId).State;
        }
        catch (KeyNotFoundException)
        {
            return ServerState.Unknown;
        }
    }

    private int ServerPortOf(Guid serverId)
    {
        try
        {
            return supervisor.Get(serverId).Definition.Port;
        }
        catch (KeyNotFoundException)
        {
            return 0;
        }
    }

    private ServerRuntime Runtime(Guid serverId) => runtimes.GetOrAdd(serverId, _ => new ServerRuntime());

    public ValueTask DisposeAsync()
    {
        foreach (var runtime in runtimes.Values)
        {
            runtime.Cancellation?.Cancel();
            runtime.Gate.Dispose();
        }
        runtimes.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>In-memory only. Transient phases and cached discovery are never persisted.</summary>
    private sealed class ServerRuntime
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Guid OperationId { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }

        /// <summary>
        /// The phase of an operation running right now, and nothing else. Null means settled, and a
        /// settled state is read from the record so that it survives a refresh and an Agent restart.
        /// </summary>
        public RouterMappingPhase? LivePhase { get; set; }

        public RouterCapabilityReport? Capability { get; set; }

        private string? mappingInstance;

        /// <summary>
        /// The identity of the mapping that is open right now, minted the first time one is observed
        /// open and dropped the first time none is.
        /// </summary>
        /// <remarks>
        /// Deliberately resolved on the projection path rather than only on the mutation path. Every
        /// operation and every read ends in a projection, so an entry that stops being open always has
        /// its identity dropped, and the next entry — however identical it looks — can never inherit
        /// it. The lazy mint is idempotent for as long as the mapping stays open, so ordinary polling
        /// and lease renewal observe one stable value.
        /// </remarks>
        public string ResolveMappingInstance(bool active)
        {
            if (!active)
            {
                Volatile.Write(ref mappingInstance, null);
                return "";
            }
            var current = Volatile.Read(ref mappingInstance);
            if (current is not null)
                return current;
            var minted = Guid.NewGuid().ToString("N");
            return Interlocked.CompareExchange(ref mappingInstance, minted, null) ?? minted;
        }

        /// <summary>
        /// Drops the current identity because the entry now on the router is a different establishment,
        /// not the previous one continuing. The next projection mints a fresh one.
        /// </summary>
        public void ReplaceMappingInstance() => Volatile.Write(ref mappingInstance, null);
    }
}
