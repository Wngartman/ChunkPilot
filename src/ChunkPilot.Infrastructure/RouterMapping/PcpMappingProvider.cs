using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Port Control Protocol, RFC 6887, version 2.
/// </summary>
/// <remarks>
/// <para>
/// Implemented: ANNOUNCE (opcode 0) as a strictly read-only capability check — RFC 6887 section 14.1
/// defines it for exactly this purpose and requires a zero requested lifetime — and MAP (opcode 1) for
/// create, renew and delete. IPv4 addresses travel as IPv4-mapped IPv6 as required by section 5.
/// </para>
/// <para>
/// The 96-bit mapping nonce is ChunkPilot's ownership token. A PCP server keys a mapping on the nonce
/// together with the protocol, internal address and internal port, so a delete carrying ChunkPilot's
/// nonce can only ever remove ChunkPilot's own mapping; it is structurally incapable of removing
/// another application's. The nonce is persisted so a restarted agent can still prove ownership.
/// </para>
/// <para>
/// Every response carries an Epoch Time (section 8.5), and it is read on every one. A PCP server that
/// loses its mapping state resets that epoch, which is the only way a client can tell a renewal of a
/// live mapping from a request that a restarted server has just turned into a brand-new mapping — the
/// two messages are identical and so are their replies. The epoch is validated by the RFC's own rule
/// and reported as <see cref="GatewayContinuity"/>; deciding what that means for a mapping's identity
/// belongs to the caller, not here.
/// </para>
/// <para>
/// Not implemented: the PEER opcode, PCP options (including THIRD_PARTY, PREFER_FAILURE and FILTER),
/// authentication, multicast ANNOUNCE listening for rapid recovery, and the unbounded IRT/MRT
/// retransmission schedule of section 8.1.1. ChunkPilot retransmits a bounded three times and reports
/// that capability could not be confirmed rather than asserting the router lacks PCP. Mapping loss after
/// a router restart is recovered by periodic renewal rather than by the rapid-recovery procedure.
/// </para>
/// <para>
/// The internal port is never sent as zero: RFC 6887 section 11.2 gives that the wildcard meaning of
/// "all of this client's mappings", which must never be issued by accident.
/// </para>
/// </remarks>
public sealed class PcpMappingProvider : IRouterMappingProvider
{
    private const byte Version = 2;
    private const byte OpcodeAnnounce = 0;
    private const byte OpcodeMap = 1;
    private const byte ResponseFlag = 0x80;
    private const int HeaderLength = 24;
    private const int MapPayloadLength = 36;
    private const int NonceLength = 12;
    private const byte ProtocolTcp = 6;
    private const byte ProtocolUdp = 17;

    private readonly IGatewayDatagramChannel channel;
    private readonly RouterMappingOptions options;
    private readonly GatewayEpochTracker epochs;

    public PcpMappingProvider(
        IGatewayDatagramChannel channel, RouterMappingOptions options, TimeProvider? clock = null)
    {
        this.channel = channel;
        this.options = options;
        epochs = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, clock ?? TimeProvider.System);
    }

    public RouterMappingMechanism Mechanism => RouterMappingMechanism.Pcp;

    public bool CanQueryExistingMappings => false;

    public async Task<RouterDiscoveryResult> DiscoverAsync(
        RouterLanBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var payload = new byte[HeaderLength];
        WriteHeader(payload, OpcodeAnnounce, requestedLifetime: 0, binding.LocalAddress);

        var reply = await ExchangeAsync(binding, payload, cancellationToken).ConfigureAwait(false);
        if (reply is null)
            return Unsupported(RouterMappingFailure.GatewayDidNotRespond,
                "The gateway did not answer a PCP ANNOUNCE within the bounded retry window.");
        if (reply.Length < HeaderLength)
            return Unsupported(RouterMappingFailure.MalformedReply,
                $"The PCP reply was {reply.Length} bytes; RFC 6887 requires at least 24.");
        if (reply[0] != Version)
            return Unsupported(RouterMappingFailure.MechanismUnsupported,
                $"The gateway answered PCP version {reply[0]}. A version 0 answer is a NAT-PMP-only gateway.");
        if (reply[1] != (ResponseFlag | OpcodeAnnounce))
            return Unsupported(RouterMappingFailure.MalformedReply,
                $"The PCP reply carried opcode byte {reply[1]} instead of {ResponseFlag | OpcodeAnnounce}.");

        // The header is the version and opcode this client speaks, so its Epoch Time field is where
        // RFC 6887 says it is and may be read before the result code is even considered.
        var continuity = ObserveEpoch(binding, reply);
        var resultCode = reply[3];
        if (resultCode != 0)
            return Unsupported(TranslateResult(resultCode),
                $"The gateway answered PCP ANNOUNCE with result code {resultCode} ({ResultName(resultCode)}).")
                with { Continuity = continuity };

        return new RouterDiscoveryResult
        {
            Mechanism = Mechanism,
            Supported = true,
            // ANNOUNCE carries no external address. PCP only states one when it assigns a mapping, so
            // nothing is reported here rather than guessing.
            ExternalAddress = "",
            SupportsFiniteLeases = true,
            Continuity = continuity,
            Detail = $"PCP version 2 (RFC 6887) answered ANNOUNCE from " +
                     $"{binding.GatewayAddress}:{options.GatewayControlPort}." + Describe(continuity)
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
        ArgumentNullException.ThrowIfNull(request);
        var nonce = request.OwnershipToken.Length == 0 ? CreateNonce() : ParseNonce(request.OwnershipToken);
        if (nonce is null)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RequestRejected,
                "The stored PCP mapping nonce could not be read, so ownership could not be proven.");

        var outcome = await MapAsync(binding, request, nonce, request.LeaseSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.Success || outcome.ExternalPort == request.ExternalPort)
            return outcome;

        // PCP assigned a different public port, so the requested one is taken. The substitute belongs to
        // ChunkPilot — its nonce proves that — so withdrawing it cannot affect anyone else's mapping.
        var assigned = outcome.ExternalPort;
        var withdrawal = await MapAsync(binding, request, nonce, 0, CancellationToken.None).ConfigureAwait(false);
        // Both exchanges were validated PCP responses, so both carried an authoritative Epoch Time. The
        // conclusion this operation reaches about the port is a separate matter entirely: a gateway that
        // proved it had restarted has restarted, and that must survive being reclassified as a conflict.
        return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.ForeignMappingPresent,
            $"PCP offered public port {assigned} instead of {request.ExternalPort}, which means the requested " +
            "port is already mapped. The substitute mapping was withdrawn.") with
        {
            Continuity = GatewayContinuityEvidence.Stronger(outcome.Continuity, withdrawal.Continuity)
        };
    }

    public async Task<RouterMappingOutcome> RemoveAsync(
        RouterLanBinding binding,
        RouterDiscoveryResult discovery,
        RouterMappingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nonce = ParseNonce(request.OwnershipToken);
        if (nonce is null)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RemovalFailed,
                "No PCP mapping nonce was stored, so ChunkPilot cannot prove the mapping is its own and will not delete it.");
        return await MapAsync(binding, request, nonce, 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RouterMappingOutcome> MapAsync(
        RouterLanBinding binding,
        RouterMappingRequest request,
        byte[] nonce,
        int lifetimeSeconds,
        CancellationToken cancellationToken)
    {
        if (request.InternalPort is <= 0 or > 65535)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RequestRejected,
                "PCP reserves internal port 0 as a wildcard, so ChunkPilot refuses to send it.");

        var payload = new byte[HeaderLength + MapPayloadLength];
        WriteHeader(payload, OpcodeMap, (uint)Math.Max(0, lifetimeSeconds), binding.LocalAddress);
        nonce.CopyTo(payload.AsSpan(HeaderLength, NonceLength));
        payload[HeaderLength + 12] = request.Transport == MappingTransport.Tcp ? ProtocolTcp : ProtocolUdp;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(HeaderLength + 16, 2), (ushort)request.InternalPort);
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(HeaderLength + 18, 2),
            (ushort)(lifetimeSeconds == 0 ? 0 : request.ExternalPort));
        WriteMappedAddress(payload.AsSpan(HeaderLength + 20, 16), IPAddress.Any);

        var reply = await ExchangeAsync(binding, payload, cancellationToken).ConfigureAwait(false);
        if (reply is null)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.GatewayDidNotRespond,
                "The gateway did not answer the PCP MAP request within the bounded retry window.");
        if (reply.Length < HeaderLength + MapPayloadLength)
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MalformedReply,
                $"The PCP MAP reply was {reply.Length} bytes; RFC 6887 requires at least 60.");
        if (reply[0] != Version || reply[1] != (ResponseFlag | OpcodeMap))
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MalformedReply,
                $"The PCP MAP reply carried version {reply[0]} opcode byte {reply[1]}.");
        if (!reply.AsSpan(HeaderLength, NonceLength).SequenceEqual(nonce))
            return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.MalformedReply,
                "The PCP MAP reply carried a different mapping nonce, so it does not describe ChunkPilot's request.");

        // Read before the result code, and carried on every outcome from here down. A gateway that has
        // restarted may still refuse the request it has just been sent, and the restart is the more
        // important of the two facts.
        var continuity = ObserveEpoch(binding, reply);
        var resultCode = reply[3];
        if (resultCode != 0)
            return RouterMappingOutcome.Failed(Mechanism, TranslateResult(resultCode),
                $"The gateway answered PCP MAP with result code {resultCode} ({ResultName(resultCode)}).")
                with { Continuity = continuity };

        var lifetime = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4, 4));
        var assignedExternalPort = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(HeaderLength + 18, 2));
        var assignedAddress = ReadMappedAddress(reply.AsSpan(HeaderLength + 20, 16));
        var external = assignedAddress is null || assignedAddress.Equals(IPAddress.Any)
            ? ""
            : assignedAddress.ToString();
        return new RouterMappingOutcome
        {
            Success = true,
            Mechanism = Mechanism,
            ExternalPort = assignedExternalPort,
            LeaseSeconds = (int)Math.Min(lifetime, int.MaxValue),
            LeaseIsFinite = lifetime > 0,
            ExternalAddress = external,
            OwnershipToken = Convert.ToHexString(nonce),
            Continuity = continuity,
            Detail = (lifetimeSeconds == 0
                ? $"PCP delete accepted for internal port {request.InternalPort}."
                : $"PCP mapped public port {assignedExternalPort} to internal port {request.InternalPort} " +
                  $"for {lifetime} seconds" + (external.Length == 0 ? "." : $" on external address {external}.")) +
                Describe(continuity)
        };
    }

    /// <summary>
    /// Reads the Epoch Time RFC 6887 places at bytes 8 to 11 of every response header and judges it
    /// against the last one this gateway sent.
    /// </summary>
    private GatewayContinuity ObserveEpoch(RouterLanBinding binding, byte[] reply) =>
        epochs.Observe(binding, options.GatewayControlPort,
            BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8, 4)));

    private static string Describe(GatewayContinuity continuity) => continuity switch
    {
        GatewayContinuity.StateLost =>
            " The gateway's PCP epoch restarted, which RFC 6887 section 8.5 defines as the server having " +
            "lost its mappings; anything it holds now was created by this request rather than continued.",
        GatewayContinuity.Confirmed =>
            " The gateway's PCP epoch continues the previous one, so it has not lost its mappings since.",
        _ => ""
    };

    private static void WriteHeader(Span<byte> payload, byte opcode, uint requestedLifetime, IPAddress clientAddress)
    {
        payload[0] = Version;
        payload[1] = opcode; // R bit is 0 for a request.
        BinaryPrimitives.WriteUInt32BigEndian(payload.Slice(4, 4), requestedLifetime);
        WriteMappedAddress(payload.Slice(8, 16), clientAddress);
    }

    /// <summary>Writes an address as the IPv4-mapped IPv6 form RFC 6887 section 5 requires.</summary>
    private static void WriteMappedAddress(Span<byte> destination, IPAddress address)
    {
        destination.Clear();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            address.GetAddressBytes().CopyTo(destination);
            return;
        }
        destination[10] = 0xFF;
        destination[11] = 0xFF;
        address.GetAddressBytes().CopyTo(destination[12..]);
    }

    private static IPAddress? ReadMappedAddress(ReadOnlySpan<byte> source)
    {
        var isMappedIpv4 = source[..10].IndexOfAnyExcept((byte)0) < 0 && source[10] == 0xFF && source[11] == 0xFF;
        return isMappedIpv4 ? new IPAddress(source[12..].ToArray()) : new IPAddress(source.ToArray());
    }

    private static byte[] CreateNonce() => RandomNumberGenerator.GetBytes(NonceLength);

    private static byte[]? ParseNonce(string token)
    {
        if (token.Length != NonceLength * 2)
            return null;
        try
        {
            return Convert.FromHexString(token);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<byte[]?> ExchangeAsync(
        RouterLanBinding binding, byte[] payload, CancellationToken cancellationToken)
    {
        var gateway = new IPEndPoint(binding.GatewayAddress, options.GatewayControlPort);
        foreach (var timeout in options.DatagramAttemptTimeouts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // PCP has no reboot obligation of its own to serve — RFC 6887 section 14's rapid recovery is
            // deliberately unimplemented — so nothing here is watching for the moment a datagram leaves.
            var reply = await channel
                .ExchangeAsync(binding.LocalAddress, gateway, payload, timeout, onSent: null, cancellationToken)
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
        1 => RouterMappingFailure.MechanismUnsupported,  // UNSUPP_VERSION
        2 => RouterMappingFailure.NotAuthorized,         // NOT_AUTHORIZED
        3 => RouterMappingFailure.MalformedReply,        // MALFORMED_REQUEST
        4 or 5 => RouterMappingFailure.MechanismUnsupported, // UNSUPP_OPCODE, UNSUPP_OPTION
        6 => RouterMappingFailure.RequestRejected,       // MALFORMED_OPTION
        7 => RouterMappingFailure.NetworkFailure,        // NETWORK_FAILURE
        8 or 9 or 10 => RouterMappingFailure.OutOfResources, // NO_RESOURCES, UNSUPP_PROTOCOL, USER_EX_QUOTA
        11 => RouterMappingFailure.ForeignMappingPresent, // CANNOT_PROVIDE_EXTERNAL
        12 => RouterMappingFailure.NetworkFailure,       // ADDRESS_MISMATCH
        13 => RouterMappingFailure.RequestRejected,      // EXCESSIVE_REMOTE_PEERS
        _ => RouterMappingFailure.Unknown
    };

    private static string ResultName(int resultCode) => resultCode switch
    {
        0 => "SUCCESS",
        1 => "UNSUPP_VERSION",
        2 => "NOT_AUTHORIZED",
        3 => "MALFORMED_REQUEST",
        4 => "UNSUPP_OPCODE",
        5 => "UNSUPP_OPTION",
        6 => "MALFORMED_OPTION",
        7 => "NETWORK_FAILURE",
        8 => "NO_RESOURCES",
        9 => "UNSUPP_PROTOCOL",
        10 => "USER_EX_QUOTA",
        11 => "CANNOT_PROVIDE_EXTERNAL",
        12 => "ADDRESS_MISMATCH",
        13 => "EXCESSIVE_REMOTE_PEERS",
        _ => "Unassigned"
    };
}
