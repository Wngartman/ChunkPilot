using System.Buffers.Binary;
using System.Net;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// NAT Port Mapping Protocol, RFC 6886.
/// </summary>
/// <remarks>
/// <para>
/// Implemented: the external address request (opcode 0) as a read-only capability check, and the TCP
/// and UDP mapping requests (opcodes 2 and 1) for create, renew and delete. Deletion is a mapping
/// request with a requested lifetime of zero and a suggested external port of zero, which RFC 6886
/// section 3.4 scopes to the requesting client's own internal port; it cannot reach another host's
/// mapping.
/// </para>
/// <para>
/// Every response carries Seconds Since Start of Epoch (section 3.6), and it is read on every one. A
/// gateway that loses its port mapping table MUST restart that counter, which is the only way a client
/// can tell a renewal of a live mapping from a request that a rebooted gateway has just turned into a
/// new one: section 3.7 states outright that the two messages are identical and that the gateway sees a
/// renewal as a creation. The value is validated by the RFC's own conservative estimate and reported as
/// <see cref="GatewayContinuity"/>; what that means for a mapping's identity is the caller's decision.
/// </para>
/// <para>
/// Section 3.7's recovery is carried out in full: a response proving the gateway restarted arms the
/// randomised 0-to-5-second delay there and then, and the mapping requests that follow are issued one
/// at a time rather than released together. <see cref="NatPmpRebootRecovery"/> owns both halves.
/// </para>
/// <para>
/// Not implemented: unsolicited multicast address-change announcements (section 3.2.1) and the full
/// nine-attempt, 64-second retransmission schedule. ChunkPilot retransmits a bounded three times and
/// then reports that the router's capability could not be confirmed, rather than asserting that NAT-PMP
/// is unsupported on evidence it does not have.
/// </para>
/// <para>
/// NAT-PMP has no way to read an existing mapping and no description field, so it cannot prove who owns
/// a public port. When the gateway assigns an external port other than the one requested, that is
/// treated as the port being taken: ChunkPilot deletes the mapping it just created — which is provably
/// its own, being keyed to its own internal port — and reports the conflict rather than presenting a
/// different public port as the user's address.
/// </para>
/// </remarks>
public sealed class NatPmpMappingProvider : IRouterMappingProvider
{
    private const byte Version = 0;
    private const byte OpcodeExternalAddress = 0;
    private const byte OpcodeMapUdp = 1;
    private const byte OpcodeMapTcp = 2;
    private const byte ResponseFlag = 128;

    private readonly IGatewayDatagramChannel channel;
    private readonly RouterMappingOptions options;
    private readonly GatewayEpochTracker epochs;
    private readonly NatPmpRebootRecovery recovery;

    public NatPmpMappingProvider(
        IGatewayDatagramChannel channel,
        RouterMappingOptions options,
        TimeProvider? clock = null,
        INatPmpRebootDelay? rebootDelay = null)
    {
        this.channel = channel;
        this.options = options;
        epochs = new GatewayEpochTracker(GatewayEpochRule.NatPortMapping, clock ?? TimeProvider.System);
        recovery = new NatPmpRebootRecovery(rebootDelay ?? new NatPmpRebootDelay());
    }

    public RouterMappingMechanism Mechanism => RouterMappingMechanism.NatPmp;

    public bool CanQueryExistingMappings => false;

    public async Task<RouterDiscoveryResult> DiscoverAsync(
        RouterLanBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var reply = await ExchangeAsync(binding, [Version, OpcodeExternalAddress], dispatch: null,
            cancellationToken).ConfigureAwait(false);
        if (reply is null)
            return Unsupported(RouterMappingFailure.GatewayDidNotRespond,
                "The gateway did not answer a NAT-PMP external address request within the bounded retry window.");
        if (reply.Length < 12)
            return Unsupported(RouterMappingFailure.MalformedReply,
                $"The NAT-PMP reply was {reply.Length} bytes; RFC 6886 requires 12 for opcode 128.");
        if (reply[0] != Version || reply[1] != ResponseFlag + OpcodeExternalAddress)
            return Unsupported(RouterMappingFailure.MalformedReply,
                $"The NAT-PMP reply carried version {reply[0]} opcode {reply[1]} instead of version 0 opcode 128.");

        // The version and opcode are this client's own, so the epoch field is where RFC 6886 says it is
        // and may be read before the result code is even considered.
        var continuity = ObserveEpoch(binding, reply);
        var resultCode = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2, 2));
        if (resultCode != 0)
            return Unsupported(TranslateResult(resultCode),
                $"The gateway answered the NAT-PMP external address request with result code {resultCode} ({ResultName(resultCode)}).")
                with { Continuity = continuity };

        var external = new IPAddress(reply.AsSpan(8, 4).ToArray());
        return new RouterDiscoveryResult
        {
            Mechanism = Mechanism,
            Supported = true,
            ExternalAddress = external.ToString(),
            SupportsFiniteLeases = true,
            Continuity = continuity,
            Detail = $"NAT-PMP (RFC 6886) answered from {binding.GatewayAddress}:{options.GatewayControlPort} " +
                     $"and reported external address {external}." + Describe(continuity)
        };
    }

    public Task<ExistingRouterMapping?> QueryAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        MappingTransport transport,
        int externalPort,
        CancellationToken cancellationToken) => Task.FromResult<ExistingRouterMapping?>(null);

    public async Task<RouterMappingOutcome> CreateAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(request);
        if (request.InternalPort is <= 0 or > 65535)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RequestRejected,
                "NAT-PMP requires a non-zero internal port.");

        // RFC 6886 section 3.7: after a gateway reboot the first mapping request waits out a randomised
        // interval so every client on the network does not re-register at once, and the requests that
        // follow are sent one at a time rather than all together. Only creation is held; withdrawal is
        // not recreation, and a gateway that has not rebooted owes nothing and waits for nothing. The
        // withdrawal of a substitute belongs inside, because it is another request to the same gateway.
        // Every datagram goes through the dispatch, which is what ties the obligation to the wire.
        return await recovery.RecreateAsync(
            GatewayEpochTracker.KeyFor(binding, options.GatewayControlPort),
            async dispatch =>
            {
                var outcome = await MapAsync(binding, request, request.LeaseSeconds, request.ExternalPort,
                    dispatch, cancellationToken).ConfigureAwait(false);
                if (!outcome.Success || outcome.ExternalPort == request.ExternalPort)
                    return outcome with { ExternalAddress = discovery.ExternalAddress };

                // The gateway gave a different public port, which RFC 6886 does when the requested one is
                // taken. The mapping just created is keyed to this computer's own internal port, so
                // deleting it is safe and never touches whatever else holds the requested port.
                var assigned = outcome.ExternalPort;
                var withdrawal = await MapAsync(binding, request, 0, 0, dispatch, CancellationToken.None)
                    .ConfigureAwait(false);
                // Both exchanges were validated NAT-PMP responses, so both carried an authoritative
                // Seconds Since Start of Epoch. What this operation concludes about the port is a
                // separate matter: a gateway that proved it had rebooted has rebooted, and that must
                // survive being reclassified.
                return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.ForeignMappingPresent,
                    $"The gateway offered public port {assigned} instead of {request.ExternalPort}, which means " +
                    "the requested port is already mapped. The substitute mapping was withdrawn.") with
                {
                    ExternalAddress = discovery.ExternalAddress,
                    Continuity = GatewayContinuityEvidence.Stronger(outcome.Continuity, withdrawal.Continuity)
                };
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<RouterMappingOutcome> RemoveAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // RFC 6886 section 3.4: lifetime zero with a suggested external port of zero deletes the
        // requesting client's mappings for that internal port only. Withdrawal is not recreation, so it
        // carries no dispatch and is never held back by a reboot obligation.
        return MapAsync(binding, request, 0, 0, dispatch: null, cancellationToken);
    }

    private async Task<RouterMappingOutcome> MapAsync(
        RouterLanBinding binding,
        RouterMappingRequest request,
        int lifetimeSeconds,
        int suggestedExternalPort,
        NatPmpRebootRecovery.Dispatch? dispatch,
        CancellationToken cancellationToken)
    {
        var opcode = request.Transport == MappingTransport.Tcp ? OpcodeMapTcp : OpcodeMapUdp;
        var payload = new byte[12];
        payload[0] = Version;
        payload[1] = opcode;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), (ushort)request.InternalPort);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), (ushort)suggestedExternalPort);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), (uint)Math.Max(0, lifetimeSeconds));

        var reply = await ExchangeAsync(binding, payload, dispatch, cancellationToken).ConfigureAwait(false);
        if (reply is null)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.GatewayDidNotRespond,
                "The gateway did not answer the NAT-PMP mapping request within the bounded retry window.");
        if (reply.Length < 16)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MalformedReply,
                $"The NAT-PMP mapping reply was {reply.Length} bytes; RFC 6886 requires 16.");
        if (reply[0] != Version || reply[1] != ResponseFlag + opcode)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MalformedReply,
                $"The NAT-PMP mapping reply carried version {reply[0]} opcode {reply[1]} instead of version 0 opcode {ResponseFlag + opcode}.");

        // Read before the result code, and carried on every outcome from here down. A gateway that has
        // rebooted may still refuse the request it has just been sent, and the reboot is the more
        // important of the two facts.
        var continuity = ObserveEpoch(binding, reply);
        var resultCode = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(2, 2));
        if (resultCode != 0)
            return RouterMappingOutcome.Failed(Mechanism, TranslateResult(resultCode),
                $"The gateway answered the NAT-PMP mapping request with result code {resultCode} ({ResultName(resultCode)}).")
                with { Continuity = continuity };

        var echoedInternalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8, 2));
        if (echoedInternalPort != request.InternalPort)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MalformedReply,
                $"The gateway answered for internal port {echoedInternalPort} instead of {request.InternalPort}.")
                with { Continuity = continuity };

        var assignedExternalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(10, 2));
        var lifetime = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12, 4));
        return new RouterMappingOutcome
        {
            Success = true,
            Mechanism = Mechanism,
            ExternalPort = assignedExternalPort,
            LeaseSeconds = (int)Math.Min(lifetime, int.MaxValue),
            LeaseIsFinite = lifetime > 0,
            Continuity = continuity,
            Detail = (lifetimeSeconds == 0
                ? $"NAT-PMP delete accepted for internal port {request.InternalPort}."
                : $"NAT-PMP mapped public port {assignedExternalPort} to internal port {request.InternalPort} " +
                  $"for {lifetime} seconds.") + Describe(continuity)
        };
    }

    /// <summary>
    /// Reads the Seconds Since Start of Epoch RFC 6886 places at bytes 4 to 7 of every response and
    /// judges it against the last one this gateway sent.
    /// </summary>
    private GatewayContinuity ObserveEpoch(RouterLanBinding binding, byte[] reply)
    {
        var continuity = epochs.Observe(binding, options.GatewayControlPort,
            BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4, 4)));
        if (continuity == GatewayContinuity.StateLost)
            recovery.NoteRebooted(GatewayEpochTracker.KeyFor(binding, options.GatewayControlPort));
        return continuity;
    }

    private static string Describe(GatewayContinuity continuity) => continuity switch
    {
        GatewayContinuity.StateLost =>
            " The gateway's seconds-since-epoch counter restarted, which RFC 6886 section 3.6 defines as " +
            "the loss of its port mapping table; anything it holds now was created by this request " +
            "rather than continued.",
        GatewayContinuity.Confirmed =>
            " The gateway's seconds-since-epoch counter continues the previous one, so it has not lost " +
            "its port mapping table since.",
        _ => ""
    };

    /// <remarks>
    /// A <paramref name="dispatch"/> is supplied only for recreation. It owns the moment the datagram
    /// goes out, so that RFC 6886 section 3.7's obligation is served at the wire rather than beforehand.
    /// </remarks>
    private async Task<byte[]?> ExchangeAsync(
        RouterLanBinding binding,
        byte[] payload,
        NatPmpRebootRecovery.Dispatch? dispatch,
        CancellationToken cancellationToken)
    {
        var gateway = new IPEndPoint(binding.GatewayAddress, options.GatewayControlPort);
        foreach (var timeout in options.DatagramAttemptTimeouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reply = await (dispatch is null
                    ? channel.ExchangeAsync(binding.LocalAddress, gateway, payload, timeout, onSent: null,
                        cancellationToken)
                    : dispatch.SendAsync(
                        onSent => channel.ExchangeAsync(binding.LocalAddress, gateway, payload, timeout,
                            onSent, cancellationToken),
                        cancellationToken))
                .ConfigureAwait(false);
            if (reply is not null)
                return reply;
        }
        return null;
    }

    private RouterDiscoveryResult Unsupported(RouterMappingFailure failure, string detail) =>
        new() { Mechanism = Mechanism, Supported = false, Failure = failure, Detail = detail };

    private static RouterMappingFailure TranslateResult(int resultCode) => resultCode switch
    {
        1 => RouterMappingFailure.MechanismUnsupported,  // Unsupported Version
        2 => RouterMappingFailure.NotAuthorized,         // Not Authorized/Refused
        3 => RouterMappingFailure.NetworkFailure,        // Network Failure
        4 => RouterMappingFailure.OutOfResources,        // Out of resources
        5 => RouterMappingFailure.MechanismUnsupported,  // Unsupported opcode
        _ => RouterMappingFailure.Unknown
    };

    private static string ResultName(int resultCode) => resultCode switch
    {
        0 => "Success",
        1 => "Unsupported Version",
        2 => "Not Authorized/Refused",
        3 => "Network Failure",
        4 => "Out of resources",
        5 => "Unsupported opcode",
        _ => "Unassigned"
    };
}
