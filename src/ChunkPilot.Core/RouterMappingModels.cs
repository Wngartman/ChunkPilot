using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace ChunkPilot.Core;

/// <summary>
/// Which transport a router mapping forwards, or a Windows Firewall rule permits. Java Edition listens
/// on TCP; Bedrock uses UDP. The two are modelled explicitly so a known port number can never be
/// forwarded — or allowed through the firewall — on the wrong transport.
/// </summary>
public enum MappingTransport
{
    Tcp,
    Udp
}

/// <summary>The mechanism that owns a mapping. <see cref="None"/> means nothing has been established.</summary>
public enum RouterMappingMechanism
{
    None,
    Pcp,
    NatPmp,
    UpnpIgd
}

/// <summary>
/// The single authoritative state a server's Direct internet setup can be in. The App renders exactly
/// these; it never derives a state of its own.
/// </summary>
public enum RouterMappingPhase
{
    /// <summary>Direct internet has not been set up. The default for every server.</summary>
    Off,
    /// <summary>A bounded capability check is running.</summary>
    Checking,
    /// <summary>A mechanism answered and can be asked to create a mapping.</summary>
    Supported,
    /// <summary>
    /// Direct internet is set up for this server, and no mapping is open right now — normally because
    /// the server is stopped. Durable intent and a live mapping are separate facts, and this is the
    /// state that keeps them apart: it must never read as though a port were open.
    /// </summary>
    Inactive,
    /// <summary>The router answered and does not offer automatic forwarding.</summary>
    Unavailable,
    /// <summary>The router answered nothing usable, so capability is genuinely unknown.</summary>
    Undetermined,
    /// <summary>A mapping request is in flight.</summary>
    Creating,
    /// <summary>The router accepted and reports ChunkPilot's mapping. Not proof of reachability.</summary>
    Active,
    /// <summary>Something else already holds the public port and ChunkPilot will not touch it.</summary>
    Conflict,
    /// <summary>The mapping existed but is no longer confirmable, or an operation failed.</summary>
    NeedsAttention,
    /// <summary>A removal is in flight.</summary>
    Removing,
    /// <summary>Recovering after an agent restart or lost mapping.</summary>
    Reconciling
}

/// <summary>
/// What a mechanism's own protocol can say about whether the gateway still holds the mappings it held
/// the last time ChunkPilot heard from it.
/// </summary>
/// <remarks>
/// <para>
/// A renewal and a creation are the same message. RFC 6887 section 8.5 and RFC 6886 sections 3.6 and
/// 3.7 both say so explicitly: a gateway that has restarted treats a client's ordinary renewal as a
/// brand-new mapping request, and the reply it sends back is indistinguishable by value from the reply
/// to a genuine renewal. The epoch each protocol carries in every response is the only thing that tells
/// the two apart, so it is the only thing that may be used to decide whether an entry is the same one
/// continuing.
/// </para>
/// <para>
/// Deliberately three-valued. "Cannot tell" is not "still there": a mechanism with no epoch of its own
/// reports <see cref="Unknown"/> and the caller falls back to whatever other evidence it has, rather
/// than inheriting a continuity nothing proved.
/// </para>
/// </remarks>
public enum GatewayContinuity
{
    /// <summary>The mechanism cannot say. Nothing may be concluded from this in either direction.</summary>
    Unknown,

    /// <summary>The gateway's epoch is a plausible continuation, so it has not lost its mapping state.</summary>
    Confirmed,

    /// <summary>The gateway's epoch proves it restarted or otherwise lost its mapping table.</summary>
    StateLost
}

/// <summary>Combining rules for continuity readings, so every caller combines them the same way.</summary>
public static class GatewayContinuityEvidence
{
    /// <summary>
    /// The more consequential of two readings about the same gateway. Evidence of loss always wins.
    /// </summary>
    /// <remarks>
    /// Continuity describes the gateway, not the request. One operation can produce several validated
    /// responses — a mapping request and the withdrawal of a substitute it was answered with, a
    /// discovery and the request that followed it — and a gateway that proved it had restarted in any
    /// one of them has restarted, whatever the operation went on to conclude. Combining anywhere other
    /// than through this rule is how that fact gets lost.
    /// </remarks>
    public static GatewayContinuity Stronger(GatewayContinuity first, GatewayContinuity second) =>
        first == GatewayContinuity.StateLost || second == GatewayContinuity.StateLost
            ? GatewayContinuity.StateLost
            : first == GatewayContinuity.Confirmed || second == GatewayContinuity.Confirmed
                ? GatewayContinuity.Confirmed
                : GatewayContinuity.Unknown;
}

/// <summary>Why an operation did not produce a mapping. Drives plain-language copy, never raw codes.</summary>
public enum RouterMappingFailure
{
    None,
    Cancelled,
    NoGatewayFound,
    GatewayDidNotRespond,
    MechanismUnsupported,
    ForeignMappingPresent,
    RequestRejected,
    NotAuthorized,
    OutOfResources,
    MalformedReply,
    NetworkFailure,
    RemovalFailed,
    ServerPortUnavailable,
    Unknown
}

/// <summary>How a router-reported WAN address classifies. Evidence only; never a reachability claim.</summary>
public enum RoutableAddressClass
{
    Unknown,
    GloballyRoutable,
    PrivateUse,
    SharedAddressSpace,
    Loopback,
    LinkLocal,
    Documentation,
    Reserved
}

/// <summary>What a router said about the external port, plus whether that suggests another NAT above it.</summary>
public sealed record ExternalAddressAssessment
{
    public string Address { get; init; } = "";
    public RoutableAddressClass Class { get; init; } = RoutableAddressClass.Unknown;
    public bool SuggestsUpstreamNat { get; init; }
    /// <summary>Why the class was chosen. Technical-details copy, never a primary message.</summary>
    public string Evidence { get; init; } = "";
}

/// <summary>
/// The exact local network context a router mapping is created on: the adapter a request leaves through
/// and the gateway it is sent to.
/// </summary>
/// <remarks>
/// <para>
/// A gateway address on its own is not an identity. Home networks reuse 192.168.0.1, 192.168.1.1 and
/// 10.0.0.1 constantly, so the same text over Ethernet, over Wi-Fi and over the network in the next
/// building names three different routers. The interface is what tells them apart, and it is the same
/// identity the per-gateway epoch history is already filed under.
/// </para>
/// <para>
/// Deliberately three-state, like the continuity it guards: an identity that is not fully known matches
/// nothing at all, because "cannot tell" is never "the same one".
/// </para>
/// </remarks>
public sealed record RouterBindingIdentity
{
    /// <summary>The adapter the request leaves through. Empty when nothing has been identified.</summary>
    public string InterfaceId { get; init; } = "";

    public string GatewayAddress { get; init; } = "";

    public bool IsKnown => InterfaceId.Length > 0 && GatewayAddress.Length > 0;

    /// <summary>Whether two identities provably name the same network context.</summary>
    public bool Matches(RouterBindingIdentity? other) =>
        IsKnown && other is { IsKnown: true } &&
        InterfaceId.Equals(other.InterfaceId, StringComparison.Ordinal) &&
        GatewayAddress.Equals(other.GatewayAddress, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A mapping an earlier build may have left open on a network this build cannot identify.
/// </summary>
/// <remarks>
/// <para>
/// Historical information, and deliberately nothing else. It can never prove ownership of anything on
/// the router reachable now, because the build that wrote it never recorded which router it used — so
/// it is kept well away from the fields ownership is decided from, rather than sitting in them where a
/// resemblance could be mistaken for a proof.
/// </para>
/// <para>
/// It exists so an exposure is not silently dropped the moment a replacement mapping succeeds, and it
/// is bounded: a finite lease the earlier build recorded is the point past which the entry cannot still
/// be there, and one that was never finite has no such point and says so.
/// </para>
/// </remarks>
public sealed record LegacyRouterExposure
{
    public int ExternalPort { get; init; }
    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;
    public RouterMappingMechanism Mechanism { get; init; } = RouterMappingMechanism.None;

    /// <summary>The gateway address the earlier build recorded. Several routers answer to any one of them.</summary>
    public string GatewayAddress { get; init; } = "";

    /// <summary>When a finite lease would have run out. Null when none was recorded.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    /// <summary>Whether the entry it describes could still exist at this moment.</summary>
    public bool MayPersistAt(DateTimeOffset now) =>
        ExternalPort > 0 && (LeaseExpiresAt is not { } expires || now < expires);
}

/// <summary>A mapping the router already reports for a public port, whoever owns it.</summary>
public sealed record ExistingRouterMapping
{
    public int ExternalPort { get; init; }
    public MappingTransport Transport { get; init; }
    public string InternalClient { get; init; } = "";
    public int InternalPort { get; init; }
    public string Description { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int LeaseSeconds { get; init; }
}

/// <summary>
/// Everything ChunkPilot durably remembers about one server's Direct internet setup: the user's intent,
/// and the minimum evidence needed to prove later that a mapping on the router is ChunkPilot's own.
/// </summary>
public sealed record RouterMappingRecord
{
    public Guid ServerId { get; init; }

    /// <summary>
    /// The exact ephemeral lease generation that last authorized this intent or mapping. These
    /// values are cleanup evidence after an Agent restart, never restored public authority.
    /// </summary>
    public Guid PublicLeaseId { get; init; }
    public long PublicLeaseGeneration { get; init; }
    public long PublicLifecycleEpoch { get; init; }

    /// <summary>Durable intent. False for every server that has never opted in.</summary>
    public bool DirectInternetEnabled { get; init; }

    /// <summary>Consent is per server and is never reused for another one.</summary>
    public bool ConsentGranted { get; init; }
    public DateTimeOffset? ConsentGrantedAt { get; init; }

    /// <summary>Retained once a mechanism successfully owns the mapping, so renewal never rediscovers.</summary>
    public RouterMappingMechanism Mechanism { get; init; } = RouterMappingMechanism.None;

    /// <summary>
    /// The mechanism the last capability check found answering. Deliberately separate from
    /// <see cref="Mechanism"/>: answering is not owning, and only owning may authorise a removal.
    /// </summary>
    public RouterMappingMechanism AvailableMechanism { get; init; } = RouterMappingMechanism.None;

    /// <summary>
    /// The private address the last check would forward to. Diagnostic only — it is never ownership
    /// evidence, so it can never make a mapping look like ChunkPilot's.
    /// </summary>
    public string CandidateInternalClient { get; init; } = "";

    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;

    public int ExternalPort { get; init; }
    public int InternalPort { get; init; }
    public string InternalClient { get; init; } = "";

    /// <summary>
    /// The network binding the mapping this record owns was actually established on.
    /// </summary>
    /// <remarks>
    /// Ownership evidence, and the only thing that may authorise a removal, a continuity judgement or a
    /// continued identity. It is written when a mapping is successfully established and is never
    /// rewritten by an observation made somewhere else. A check that reaches another network says
    /// nothing whatsoever about the entry left on this one, and relabelling that entry as the other
    /// network's would make every later decision — where a deletion is sent, which public address is
    /// shown, which endpoint a verification describes — quietly name the wrong router.
    /// </remarks>
    public RouterBindingIdentity OwnedBinding { get; init; } = new();

    /// <summary>
    /// The binding the last check or attempt actually reached. Diagnostic only, exactly like
    /// <see cref="CandidateInternalClient"/>: it never makes a mapping look like ChunkPilot's, and it
    /// never moves one.
    /// </summary>
    public RouterBindingIdentity DiscoveredBinding { get; init; } = new();

    /// <summary>
    /// The gateway a build before ownership binding wrote on its own, read once when such a row is
    /// loaded and folded into <see cref="OwnedBinding"/> by
    /// <see cref="RouterMappingPolicy.UpgradeStoredRecord"/>. Never written back.
    /// </summary>
    [JsonPropertyName("gatewayAddress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string LegacyGatewayAddress { get; init; } = "";

    /// <summary>
    /// What an unprovable ownership claim became once it was ended: a possible exposure on a network
    /// nothing here can identify, remembered rather than dropped, and unable to authorise anything.
    /// </summary>
    public LegacyRouterExposure? LegacyExposure { get; init; }

    /// <summary>Stable, non-identifying description written into the router's mapping table.</summary>
    public string Description { get; init; } = "";

    /// <summary>PCP mapping nonce, hex encoded. Empty for mechanisms without a nonce.</summary>
    public string OwnershipToken { get; init; } = "";

    /// <summary>UPnP control endpoint that established the mapping.</summary>
    public string ControlUrl { get; init; } = "";
    public string ServiceType { get; init; } = "";

    public bool HasActiveMapping { get; init; }
    public bool LeaseIsFinite { get; init; }
    public int LeaseSeconds { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset? EstablishedAt { get; init; }

    public string RouterReportedExternalAddress { get; init; } = "";
    public RoutableAddressClass RouterReportedAddressClass { get; init; } = RoutableAddressClass.Unknown;

    /// <summary>A removal that failed. Retained so recovery retries it instead of forgetting the exposure.</summary>
    public bool RemovalPending { get; init; }

    public RouterMappingFailure LastFailure { get; init; } = RouterMappingFailure.None;
    public string LastOperationDetail { get; init; } = "";
    public DateTimeOffset? LastCheckedAt { get; init; }
}

/// <summary>The authoritative snapshot the App renders. The App adds copy, never state.</summary>
public sealed record RouterMappingState
{
    public Guid ServerId { get; init; }
    public bool Enabled { get; init; }
    public bool ConsentGranted { get; init; }
    public RouterMappingPhase Phase { get; init; } = RouterMappingPhase.Off;
    public RouterMappingMechanism Mechanism { get; init; } = RouterMappingMechanism.None;

    /// <summary>The mechanism that answered the last check. Answering is not owning.</summary>
    public RouterMappingMechanism AvailableMechanism { get; init; } = RouterMappingMechanism.None;

    public MappingTransport Transport { get; init; } = MappingTransport.Tcp;
    public RouterMappingFailure Failure { get; init; } = RouterMappingFailure.None;

    /// <summary>
    /// The gateway this server's Direct internet setup is about: the one the open mapping is on where
    /// there is one, and otherwise the one the last check reached.
    /// </summary>
    public string GatewayAddress { get; init; } = "";

    /// <summary>
    /// ChunkPilot last looked at a different network from the one this server's mapping was created on.
    /// </summary>
    /// <remarks>
    /// Nothing about that mapping can be confirmed from here while this is true, and nothing gathered
    /// about it — least of all an external verification — is current. The mapping is not forgotten; it
    /// is simply no longer presented as open.
    /// </remarks>
    public bool OwnedNetworkLeft { get; init; }

    /// <summary>
    /// An earlier build opened this port without recording which router it used, so ChunkPilot cannot
    /// identify the mapping it is holding evidence of.
    /// </summary>
    /// <remarks>
    /// Nothing may be concluded from it and nothing may be sent because of it: not a removal, not a
    /// continuity judgement, and certainly not a claim that the port is open right now. It stays
    /// visible so the owner can close it themselves, and a fresh mapping on the network this computer
    /// is actually on replaces it under the ordinary rules.
    /// </remarks>
    public bool OwnedNetworkUnknown { get; init; }

    /// <summary>
    /// A port an earlier build may have left open on a router this build cannot identify, shown while it
    /// could still be there. Never a claim that anything is open right now.
    /// </summary>
    public LegacyRouterExposure? LegacyExposure { get; init; }

    public string InternalClient { get; init; } = "";

    /// <summary>The private address a mapping would forward to. Diagnostic only.</summary>
    public string CandidateInternalClient { get; init; } = "";
    public int InternalPort { get; init; }
    public int ExternalPort { get; init; }

    public string RouterReportedExternalAddress { get; init; } = "";
    public RoutableAddressClass RouterReportedAddressClass { get; init; } = RoutableAddressClass.Unknown;
    public bool UpstreamNatSuspected { get; init; }

    /// <summary>
    /// Identifies one continuous establishment of the mapping that is open right now, and is empty
    /// whenever nothing is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opaque and deliberately value-free. Mechanism, transport, addresses and ports all describe what
    /// a mapping <em>is</em>; none of them can tell one establishment from another, because a router
    /// that drops an entry and is asked for the same one again produces something identical in every
    /// observable way. Point-in-time evidence gathered about the first entry must not survive into the
    /// second, so the two are told apart by identity rather than by resemblance.
    /// </para>
    /// <para>
    /// The value is minted by the Agent while a mapping is open and dropped the moment none is. It is
    /// never persisted: it names a live router entry, and an entry that did not outlive the Agent has
    /// no identity worth restoring.
    /// </para>
    /// </remarks>
    public string MappingInstanceId { get; init; } = "";

    public bool LeaseIsFinite { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset? LastCheckedAt { get; init; }
    public bool RemovalPending { get; init; }

    public string LastOperationDetail { get; init; } = "";

    /// <summary>An operation owns this server right now; the App disables competing commands.</summary>
    public bool Busy { get; init; }
    public Guid OperationId { get; init; }

    /// <summary>The current Agent-only public-connectivity generation, or empty when none exists.</summary>
    public PublicConnectivityLeaseIdentity PublicConnectivityLease { get; init; } = new();

    public bool HasRouterReportedAddress => RouterReportedExternalAddress.Length > 0;

    /// <summary>
    /// What the router said about itself, labelled by the App as router-reported rather than verified.
    /// </summary>
    /// <remarks>
    /// The port is appended only while a mapping actually holds it. Before that — and after a stop has
    /// withdrawn it — the address is shown on its own rather than joined to a port nothing is
    /// forwarding, and rather than rendering an empty row beside a copy button.
    /// </remarks>
    public string RouterReportedEndpoint =>
        RouterReportedExternalAddress.Length == 0
            ? ""
            : Phase == RouterMappingPhase.Active && ExternalPort > 0
                ? $"{RouterReportedExternalAddress}:{ExternalPort}"
                : RouterReportedExternalAddress;

    public static RouterMappingState Off(Guid serverId, int internalPort) =>
        new() { ServerId = serverId, InternalPort = internalPort, Phase = RouterMappingPhase.Off };
}

/// <summary>
/// Provider-neutral rules for router mappings. Everything here is a pure decision so the same rule that
/// runs in the Agent can be proven by a unit test.
/// </summary>
public static class RouterMappingPolicy
{
    /// <summary>
    /// The description ChunkPilot writes into a router's mapping table. Deliberately constant and
    /// non-identifying: it names the application, never the server, the user or the machine.
    /// </summary>
    public const string MappingDescription = "ChunkPilot Minecraft";

    /// <summary>
    /// Requested lease length. RFC 6886 recommends 7200 seconds; ChunkPilot deliberately asks for less so
    /// that an unclean shutdown closes the exposure sooner, and renews while the server is running.
    /// </summary>
    public const int RequestedLeaseSeconds = 3600;

    /// <summary>Never renew more often than this, whatever a router shortens the lease to.</summary>
    public static readonly TimeSpan MinimumRenewalInterval = TimeSpan.FromSeconds(60);

    /// <summary>Renew once half the lease has passed, so one lost renewal is not immediately fatal.</summary>
    public static DateTimeOffset RenewalDueAt(DateTimeOffset establishedAt, int leaseSeconds)
    {
        var lease = TimeSpan.FromSeconds(Math.Max(0, leaseSeconds));
        var half = TimeSpan.FromTicks(lease.Ticks / 2);
        return establishedAt + (half < MinimumRenewalInterval ? MinimumRenewalInterval : half);
    }

    /// <summary>
    /// Whether ChunkPilot last looked at a different network from the one its mapping is on.
    /// </summary>
    /// <remarks>
    /// A mapping stays exactly where it was made; this computer does not. Moving to another network,
    /// changing adapter, or a router replaced underneath the same address all end ChunkPilot's ability
    /// to confirm the entry it holds — and, more to the point, end any claim that the entry is what the
    /// outside world would reach. Requires both identities to be fully known, so an older record that
    /// never recorded one is left exactly as it was rather than declared lost on no evidence.
    /// </remarks>
    public static bool HasLeftOwnedNetwork(RouterMappingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.HasActiveMapping && !record.OwnedBinding.Matches(record.DiscoveredBinding) &&
               record.OwnedBinding.IsKnown && record.DiscoveredBinding.IsKnown;
    }

    /// <summary>
    /// Whether this record claims an open mapping without saying which network it is on.
    /// </summary>
    /// <remarks>
    /// The only way to reach this state is to load a row written by a build that recorded a gateway
    /// address and nothing else. An address is not a network: the same text over another adapter, or on
    /// another site, is another router entirely, so a row like this names a mapping that exists
    /// somewhere without saying where. Nothing observable from here can identify it, which makes it
    /// evidence of an exposure and never authority to act on one.
    /// </remarks>
    public static bool HasUnknownOwnedNetwork(RouterMappingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.HasActiveMapping && !record.OwnedBinding.IsKnown;
    }

    /// <summary>Whether nothing reachable from here can confirm the mapping this record claims.</summary>
    public static bool CannotConfirmOwnedMapping(RouterMappingRecord record) =>
        HasLeftOwnedNetwork(record) || HasUnknownOwnedNetwork(record);

    /// <summary>
    /// Ends an ownership claim no router reachable from here could ever confirm, keeping what it was as
    /// a possible exposure instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A claim written without a network is one bit doing two jobs: it records that a mapping may exist
    /// somewhere, and it asserts that ChunkPilot owns one here. The second is never true — the build
    /// that wrote it never recorded where "here" was — and leaving it standing is what lets a router
    /// reachable now be judged against it. The two are separated before anything on that router is
    /// evaluated: the claim ends, and the exposure it described is remembered somewhere that no
    /// ownership rule reads.
    /// </para>
    /// <para>
    /// A lapsed exposure is forgotten in the same step. It described an entry with a finite lease, and
    /// once that has run out the entry is gone whoever was holding it.
    /// </para>
    /// </remarks>
    public static RouterMappingRecord ResolveUnprovableClaim(RouterMappingRecord record, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        var carried = record.LegacyExposure is { } existing && existing.MayPersistAt(now)
            ? existing
            : null;
        if (record.OwnedBinding.IsKnown || !record.HasActiveMapping && !record.RemovalPending)
            return ReferenceEquals(carried, record.LegacyExposure)
                ? record
                : record with { LegacyExposure = carried };
        return record with
        {
            HasActiveMapping = false,
            // The obligation to close it moves with the claim: nothing here can retry a removal that
            // has no router to be sent to, and saying otherwise would promise something impossible.
            RemovalPending = false,
            LeaseExpiresAt = null,
            EstablishedAt = null,
            OwnedBinding = new RouterBindingIdentity(),
            LegacyExposure = Retired(record) is { } retired && retired.MayPersistAt(now) ? retired : carried
        };
    }

    /// <summary>What an unprovable claim describes, as a possible exposure rather than an ownership.</summary>
    private static LegacyRouterExposure? Retired(RouterMappingRecord record) =>
        record.ExternalPort > 0
            ? new LegacyRouterExposure
            {
                ExternalPort = record.ExternalPort,
                Transport = record.Transport,
                Mechanism = record.Mechanism,
                GatewayAddress = record.OwnedBinding.GatewayAddress,
                LeaseExpiresAt = record.LeaseIsFinite ? record.LeaseExpiresAt : null
            }
            : null;

    /// <summary>
    /// Brings a stored row up to the current shape as it is read.
    /// </summary>
    /// <remarks>
    /// A build before ownership binding stored a bare gateway address. It is kept, because it is the
    /// only thing that can tell the owner which router still holds a port ChunkPilot can no longer
    /// close — but it is kept as what it is: an identity missing its interface, which
    /// <see cref="RouterBindingIdentity.IsKnown"/> reports as not known and which therefore matches
    /// nothing and authorises nothing. Promoting it to a complete ownership would assert the one thing
    /// the old build never recorded.
    /// </remarks>
    public static RouterMappingRecord UpgradeStoredRecord(RouterMappingRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.LegacyGatewayAddress.Length == 0)
            return record;
        return record with
        {
            LegacyGatewayAddress = "",
            OwnedBinding = record.OwnedBinding.GatewayAddress.Length > 0
                ? record.OwnedBinding
                : new RouterBindingIdentity { GatewayAddress = record.LegacyGatewayAddress }
        };
    }

    public static bool IsRenewalDue(RouterMappingRecord record, DateTimeOffset now) =>
        record is { DirectInternetEnabled: true, HasActiveMapping: true, LeaseIsFinite: true } &&
        record.EstablishedAt is { } established &&
        now >= RenewalDueAt(established, record.LeaseSeconds);

    /// <summary>
    /// Whether a mapping the router reports can be proven to be ChunkPilot's own.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. Persisted evidence that ChunkPilot created a mapping on this exact
    /// public port is required first: without it the entry belongs to somebody else no matter how much
    /// the rest matches. The internal client and internal port must then agree, and a description the
    /// router does report must be ChunkPilot's. A router that reports no description weakens nothing,
    /// because the persisted evidence and the internal endpoint already had to match.
    /// </remarks>
    public static bool ProvesOwnership(RouterMappingRecord record, ExistingRouterMapping existing)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(existing);
        if (!record.HasActiveMapping && !record.RemovalPending)
            return false;
        if (record.ExternalPort == 0 || record.ExternalPort != existing.ExternalPort)
            return false;
        if (record.Transport != existing.Transport)
            return false;
        if (record.InternalPort == 0 || record.InternalPort != existing.InternalPort)
            return false;
        if (record.InternalClient.Length == 0 ||
            !record.InternalClient.Equals(existing.InternalClient, StringComparison.OrdinalIgnoreCase))
            return false;
        return existing.Description.Length == 0 ||
               existing.Description.Equals(record.Description, StringComparison.Ordinal);
    }

    /// <summary>
    /// Classifies an address a router claims as its WAN side. Nothing here proves reachability; a
    /// globally routable class only means no local evidence contradicts it.
    /// </summary>
    public static ExternalAddressAssessment ClassifyExternalAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out var parsed))
            return new ExternalAddressAssessment
            {
                Address = address?.Trim() ?? "",
                Class = RoutableAddressClass.Unknown,
                Evidence = "The router did not report an external address that could be read."
            };
        return ClassifyExternalAddress(parsed);
    }

    public static ExternalAddressAssessment ClassifyExternalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var text = address.ToString();
        var (classification, evidence) = Classify(address);
        return new ExternalAddressAssessment
        {
            Address = text,
            Class = classification,
            SuggestsUpstreamNat = classification is RoutableAddressClass.PrivateUse or
                RoutableAddressClass.SharedAddressSpace,
            Evidence = evidence
        };
    }

    private static (RoutableAddressClass Class, string Evidence) Classify(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return (RoutableAddressClass.Loopback, $"{address} is a loopback address.");
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal)
                return (RoutableAddressClass.LinkLocal, $"{address} is an IPv6 link-local address.");
            var v6 = address.GetAddressBytes();
            if ((v6[0] & 0xfe) == 0xfc)
                return (RoutableAddressClass.PrivateUse, $"{address} is an IPv6 unique local address (fc00::/7).");
            if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8)
                return (RoutableAddressClass.Documentation, $"{address} is in the IPv6 documentation range 2001:db8::/32.");
            if (address.Equals(IPAddress.IPv6Any))
                return (RoutableAddressClass.Reserved, "The router reported the unspecified IPv6 address.");
            return (RoutableAddressClass.GloballyRoutable, $"{address} is outside the IPv6 private and reserved ranges.");
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return (RoutableAddressClass.Unknown, $"{address} is not an IPv4 or IPv6 address.");

        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0)
            return (RoutableAddressClass.Reserved, $"{address} is in the reserved 0.0.0.0/8 range.");
        if (bytes[0] == 10)
            return (RoutableAddressClass.PrivateUse, $"{address} is in the private range 10.0.0.0/8 (RFC 1918).");
        if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            return (RoutableAddressClass.PrivateUse, $"{address} is in the private range 172.16.0.0/12 (RFC 1918).");
        if (bytes[0] == 192 && bytes[1] == 168)
            return (RoutableAddressClass.PrivateUse, $"{address} is in the private range 192.168.0.0/16 (RFC 1918).");
        if (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
            return (RoutableAddressClass.SharedAddressSpace,
                $"{address} is in Shared Address Space 100.64.0.0/10 (RFC 6598), which providers use for carrier-grade NAT.");
        if (bytes[0] == 169 && bytes[1] == 254)
            return (RoutableAddressClass.LinkLocal, $"{address} is a link-local address (169.254.0.0/16).");
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 ||
            bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
            bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
            return (RoutableAddressClass.Documentation, $"{address} is in a documentation range (RFC 5737).");
        if (bytes[0] == 198 && bytes[1] is 18 or 19)
            return (RoutableAddressClass.Reserved, $"{address} is in the benchmarking range 198.18.0.0/15.");
        if (bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0)
            return (RoutableAddressClass.Reserved, $"{address} is in the IETF protocol assignments range 192.0.0.0/24.");
        if (bytes[0] >= 224)
            return (RoutableAddressClass.Reserved, $"{address} is a multicast or reserved address (224.0.0.0/4 and above).");
        return (RoutableAddressClass.GloballyRoutable,
            $"{address} is outside the private, shared and reserved IPv4 ranges.");
    }
}
