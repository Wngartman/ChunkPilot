using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>Which RFC's epoch rule a tracker applies. The two are deliberately not interchangeable.</summary>
internal enum GatewayEpochRule
{
    /// <summary>RFC 6887 section 8.5, PCP Epoch Time.</summary>
    PortControlProtocol,

    /// <summary>RFC 6886 section 3.6, NAT-PMP Seconds Since Start of Epoch.</summary>
    NatPortMapping
}

/// <summary>
/// Remembers the epoch a gateway last reported, so the next response can be judged a plausible
/// continuation of it or evidence that the gateway lost its mapping table.
/// </summary>
/// <remarks>
/// <para>
/// Owned by one provider instance and therefore already scoped to one mechanism: a PCP epoch is never
/// compared with a NAT-PMP one because they are never held by the same tracker. Within a mechanism the
/// history is keyed by the interface the request left through, the gateway it was sent to, and the port
/// it was sent on, so two routers — or the same address reached over a different adapter — never share
/// a history. There is no static state: a tracker lives and dies with its provider, which lives and
/// dies with the Agent. Forgetting on restart is correct, because the mapping identity the history
/// exists to protect is not persisted either.
/// </para>
/// <para>
/// Both rules are the RFC's own, and both are deliberately tolerant. Cheap gateways keep poor time and
/// datagrams take a while to arrive, so an epoch that merely disagrees slightly with the client's clock
/// is not treated as a restart; only a disagreement past the RFC's tolerance is.
/// </para>
/// </remarks>
internal sealed class GatewayEpochTracker
{
    private readonly GatewayEpochRule rule;
    private readonly TimeProvider clock;
    private readonly Dictionary<string, Observation> observations = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public GatewayEpochTracker(GatewayEpochRule rule, TimeProvider clock)
    {
        this.rule = rule;
        this.clock = clock;
    }

    /// <summary>
    /// Records the epoch one response carried and reports what it says about the gateway's state.
    /// </summary>
    /// <remarks>
    /// The first response from a gateway is necessarily valid — RFC 6887 section 8.5 says exactly that —
    /// but it proves no continuity either, because there is nothing yet for it to continue from. It is
    /// therefore reported as <see cref="GatewayContinuity.Unknown"/> rather than confirmed.
    /// </remarks>
    /// <summary>
    /// The identity a gateway's history is filed under: the interface the request left through, the
    /// gateway it was sent to, and the port it was sent on. Shared so anything else that follows from
    /// an epoch observation is scoped in exactly the same way.
    /// </summary>
    public static string KeyFor(RouterLanBinding binding, int gatewayPort)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return $"{binding.InterfaceId}|{binding.GatewayAddress}|{gatewayPort}";
    }

    public GatewayContinuity Observe(RouterLanBinding binding, int gatewayPort, uint gatewayEpochSeconds)
    {
        var key = KeyFor(binding, gatewayPort);
        var now = clock.GetUtcNow();
        lock (gate)
        {
            var continuity = observations.TryGetValue(key, out var previous)
                ? Evaluate(previous, gatewayEpochSeconds, now)
                : GatewayContinuity.Unknown;
            observations[key] = new Observation(gatewayEpochSeconds, now);
            return continuity;
        }
    }

    private GatewayContinuity Evaluate(Observation previous, uint current, DateTimeOffset now)
    {
        // Whole seconds, never negative: the client's own clock is the only thing being measured here
        // and a backwards step in it must not be read as the gateway misbehaving.
        var clientDelta = Math.Max(0L, (long)(now - previous.At).TotalSeconds);
        return rule == GatewayEpochRule.PortControlProtocol
            ? EvaluatePcp(previous.Epoch, current, clientDelta)
            : EvaluateNatPmp(previous.Epoch, current, clientDelta);
    }

    /// <summary>RFC 6887 section 8.5, applied exactly as written.</summary>
    private static GatewayContinuity EvaluatePcp(long previousEpoch, long currentEpoch, long clientDelta)
    {
        // "The server Epoch time apparently going backwards by up to one second is not deemed invalid",
        // which covers reordered responses; anything further back is a restart.
        if (previousEpoch - currentEpoch > 1)
            return GatewayContinuity.StateLost;
        var serverDelta = Math.Max(0L, currentEpoch - previousEpoch);
        // The +2 absorbs quantisation in both clocks and the /16 allows the 6.25% total drift the RFC
        // grants low-cost devices.
        return clientDelta + 2 < serverDelta - serverDelta / 16 ||
               serverDelta + 2 < clientDelta - clientDelta / 16
            ? GatewayContinuity.StateLost
            : GatewayContinuity.Confirmed;
    }

    /// <summary>RFC 6886 section 3.6, applied exactly as written.</summary>
    private static GatewayContinuity EvaluateNatPmp(long previousEpoch, long currentEpoch, long clientDelta)
    {
        // The client's conservative estimate is the last epoch plus seven eighths of its own elapsed
        // time; falling more than two seconds short of it means the gateway restarted.
        var expected = previousEpoch + clientDelta * 7 / 8;
        return currentEpoch < expected - 2 ? GatewayContinuity.StateLost : GatewayContinuity.Confirmed;
    }

    private readonly record struct Observation(long Epoch, DateTimeOffset At);
}
