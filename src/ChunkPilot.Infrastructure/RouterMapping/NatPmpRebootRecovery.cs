using System.Security.Cryptography;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// The wait RFC 6886 section 3.7 requires before the first mapping request that follows a detected
/// gateway reboot. Injectable so tests can prove the behaviour without waiting for it.
/// </summary>
public interface INatPmpRebootDelay
{
    /// <summary>A duration drawn uniformly from the interval the RFC specifies.</summary>
    TimeSpan NextDelay();

    Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>The production delay: a real uniform draw and a real, non-blocking wait.</summary>
public sealed class NatPmpRebootDelay : INatPmpRebootDelay
{
    /// <summary>RFC 6886 section 3.7: "uniform random distribution in the range 0 to 5 seconds".</summary>
    public static readonly TimeSpan Maximum = TimeSpan.FromSeconds(5);

    public TimeSpan NextDelay() =>
        TimeSpan.FromMilliseconds(RandomNumberGenerator.GetInt32(0, (int)Maximum.TotalMilliseconds + 1));

    public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) =>
        Task.Delay(duration, cancellationToken);
}

/// <summary>
/// Carries out RFC 6886 section 3.7's recovery for one gateway at a time: the randomised wait a
/// detected reboot obliges, and the one-at-a-time ordering of the mapping requests that follow it.
/// </summary>
/// <remarks>
/// <para>
/// RFC 6886 section 3.7, in full. A client renewing its mappings because Seconds Since Start of Epoch
/// showed a reboot "MUST first delay by a random amount of time selected with uniform random
/// distribution in the range 0 to 5 seconds, and then send its first port mapping request". Then:
/// "After that request is acknowledged by the gateway, the client may then send its second request, and
/// so on… The requests SHOULD be issued serially, one at a time; the client SHOULD NOT issue multiple
/// concurrent requests." Both halves exist to stop a gateway that has only just finished booting being
/// buried by the clients it just dropped, and sharing one wait satisfies only the first: everything
/// waiting on it is released at the same instant, which is the burst the second half forbids.
/// </para>
/// <para>
/// So a reboot arms an obligation rather than a task, and recreation runs through a per-gateway gate
/// that admits one request at a time. The wait starts the moment the response proves the reboot — which
/// is what "delay, then send" means — the first recreation through the gate spends it, and the next
/// one goes out only once the first has been answered. A gateway that has not rebooted has no
/// obligation and no gate, so ordinary renewal is untouched, and gateways never wait for each other.
/// </para>
/// <para>
/// Every detected reboot is its own obligation, numbered, because a second reboot while the first wait
/// is still running is a second event with its own recovery to perform. An older wait finishing
/// discharges only the generation it belongs to; a request held for the newer one keeps waiting rather
/// than slipping out through the door the older one opened. Distinctness comes from the epoch history,
/// which classifies one reset once and reports every response after it as a continuation, so one reboot
/// seen through several replies arms one obligation.
/// </para>
/// <para>
/// An obligation is discharged by a complete datagram genuinely leaving, and by nothing else. Waiting
/// out a timer, winning the gate, or starting a send are all things that can still be abandoned — a
/// cancelled or failed caller sends nothing, so the next one is owed the same wait the RFC asked for. Recreation
/// therefore does not "wait, then return, then send": it hands the operation a <see cref="Dispatch"/>
/// and the operation puts every datagram through it.
/// </para>
/// <para>
/// That leaves two moments, and they are deliberately not the same one. The first is where the
/// obligation is last checked and the send is started, together, under the one lock
/// <see cref="NoteRebooted"/> also takes — so a reboot recorded before it cannot be stepped over, and
/// the generation being served is captured there. The second is where the transport reports that the
/// datagram actually went out, which is the only point at which that captured generation is marked
/// served. Between them a send can still fail, be cancelled, or find its socket unusable, and every one
/// of those leaves the obligation exactly where it was. A reboot recorded in between is a later event
/// with an obligation of its own: the confirmation serves the generation it captured and never
/// "whatever is current now", so the newer one survives for the next recreation to answer.
/// </para>
/// <para>
/// The section's requirement is about when a request may be <em>sent</em> — "delay … and then send its
/// first port mapping request" — so transmission discharges it. A request that goes out and is then
/// refused or never answered has still been delayed as asked, and delaying the next one again would
/// answer a requirement the RFC does not make. What that failure means for the mapping is decided
/// separately, by the outcome and epoch rules that own it.
/// </para>
/// <para>
/// Scoped exactly like the epoch history it follows from: per interface, gateway and control port, held
/// by the provider instance, never static, and forgotten with the Agent. An entry is dropped once its
/// obligation is discharged and nothing is using it, so the steady state carries none at all.
/// </para>
/// </remarks>
internal sealed class NatPmpRebootRecovery
{
    private readonly INatPmpRebootDelay delay;
    private readonly Dictionary<string, Recovery> outstanding = new(StringComparer.Ordinal);
    private readonly Lock gate = new();

    public NatPmpRebootRecovery(INatPmpRebootDelay delay) => this.delay = delay;

    /// <summary>Records one newly detected reboot of a gateway as an obligation of its own.</summary>
    public void NoteRebooted(string gatewayKey)
    {
        lock (gate)
        {
            if (!outstanding.TryGetValue(gatewayKey, out var recovery))
                outstanding[gatewayKey] = recovery = new Recovery();
            recovery.Generation++;
            // Started here rather than at the point of use: the RFC's delay begins when the reboot is
            // detected, not when something eventually wants to send.
            recovery.Wait = delay.WaitAsync(delay.NextDelay(), CancellationToken.None);
        }
    }

    /// <summary>
    /// Runs one recreation under whatever recovery this gateway owes, and hands it the
    /// <see cref="Dispatch"/> its datagrams must go through.
    /// </summary>
    /// <remarks>
    /// A gateway that has not rebooted owes nothing: the operation runs straight through and its
    /// dispatch is a pass-through. Otherwise the gate is taken first, so one recreation at a time uses
    /// the gateway, and the obligation itself is served at the dispatch inside — never here, because a
    /// caller that reaches this point can still be cancelled or fail without ever sending anything.
    /// </remarks>
    public async Task<T> RecreateAsync<T>(
        string gatewayKey, Func<Dispatch, Task<T>> recreate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recreate);
        Recovery? recovery;
        lock (gate)
        {
            // Claimed under the lock and released in the finally below, so an entry can only be dropped
            // while nothing is inside it — which is what stops the request after the last one of a
            // recovery overlapping with it.
            _ = outstanding.TryGetValue(gatewayKey, out recovery);
            if (recovery is not null)
                recovery.Users++;
        }
        if (recovery is null)
            return await recreate(new Dispatch(this, null)).ConfigureAwait(false);
        try
        {
            await recovery.Serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await recreate(new Dispatch(this, recovery)).ConfigureAwait(false);
            }
            finally
            {
                recovery.Serial.Release();
            }
        }
        finally
        {
            lock (gate)
            {
                recovery.Users--;
                if (recovery.Users == 0 && recovery.Served == recovery.Generation &&
                    outstanding.TryGetValue(gatewayKey, out var current) && ReferenceEquals(current, recovery))
                    outstanding.Remove(gatewayKey);
            }
        }
    }

    /// <summary>
    /// Serves whatever the gateway is still owed and then starts one datagram on its way, with nothing
    /// in between that a newly recorded reboot could slip through — and marks the obligation served only
    /// when the transport says the datagram actually left.
    /// </summary>
    private async Task<byte[]?> DispatchAsync(
        Dispatch dispatch, Recovery recovery, Func<Action, Task<byte[]?>> exchange,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<byte[]?>? started = null;
            Task? owed = null;
            var observed = 0L;
            lock (gate)
            {
                // Either an earlier request has already served the generation now current, or this one
                // has waited it out itself. Anything else means a reboot has been recorded since, and it
                // is owed its own wait before anything more is sent.
                if (recovery.Served == recovery.Generation || dispatch.WaitedThrough == recovery.Generation)
                {
                    // Started inside the lock that NoteRebooted also takes, so the last check and the
                    // beginning of the send are one step: a reboot recorded from here on is necessarily
                    // recorded afterwards. The generation is captured rather than consumed — starting a
                    // send is not sending one — and only starting happens here, so no thread is blocked
                    // and no lock spans the send or the reply.
                    var captured = recovery.Generation;
                    started = exchange(() => Confirm(dispatch, recovery, captured));
                }
                else
                {
                    observed = recovery.Generation;
                    owed = recovery.Wait;
                }
            }
            if (started is not null)
                return await started.ConfigureAwait(false);
            await owed!.WaitAsync(cancellationToken).ConfigureAwait(false);
            dispatch.WaitedThrough = observed;
        }
    }

    /// <summary>
    /// The transport has put the complete datagram out. Marks served the generation that send was
    /// carrying, and only that one.
    /// </summary>
    /// <remarks>
    /// A reboot recorded while the datagram was on its way is a later event, and this send answered
    /// nothing about it: consuming "whatever is current" would swallow it. The comparison is monotonic
    /// so an older confirmation arriving late can never move the mark backwards either.
    /// </remarks>
    private void Confirm(Dispatch dispatch, Recovery recovery, long captured)
    {
        lock (gate)
        {
            dispatch.MarkDispatched();
            if (recovery.Served < captured)
                recovery.Served = captured;
        }
    }

    /// <summary>
    /// What one recreation puts each of its datagrams through. Created per operation, so what it
    /// remembers — whether this operation has waited a generation out, and whether it has already sent
    /// — belongs to that operation and to nothing else.
    /// </summary>
    /// <remarks>
    /// Only the first datagram a recreation actually gets out is held. Until one has, every attempt is
    /// checked again — a send that failed at the socket never reached the gateway, so nothing has been
    /// answered and the obligation still stands. Once one has genuinely gone out the request is under
    /// way: its retransmissions and the withdrawal of a substitute port belong to it, and holding those
    /// back for a reboot recorded afterwards would stall a request already in flight instead of
    /// answering the reboot, which the next recreation does.
    /// </remarks>
    internal sealed class Dispatch
    {
        private readonly NatPmpRebootRecovery? owner;
        private readonly Recovery? recovery;
        private bool dispatched;

        internal Dispatch(NatPmpRebootRecovery? owner, Recovery? recovery)
        {
            this.owner = owner;
            this.recovery = recovery;
        }

        /// <summary>The generation this operation has waited out, if any.</summary>
        internal long WaitedThrough { get; set; }

        internal void MarkDispatched() => dispatched = true;

        /// <param name="exchange">
        /// Starts one datagram on its way. It is handed the callback the transport must invoke once the
        /// complete datagram has actually been sent, which is the only thing that discharges an
        /// obligation.
        /// </param>
        public Task<byte[]?> SendAsync(
            Func<Action?, Task<byte[]?>> exchange, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(exchange);
            return recovery is null || dispatched
                ? exchange(null)
                : owner!.DispatchAsync(this, recovery, confirm => exchange(confirm), cancellationToken);
        }
    }

    /// <summary>
    /// One gateway's outstanding recovery. Every field is read and written under the owner's lock,
    /// except <see cref="Serial"/>, which is the gate itself.
    /// </summary>
    internal sealed class Recovery
    {
        public SemaphoreSlim Serial { get; } = new(1, 1);

        /// <summary>How many reboots of this gateway have been detected.</summary>
        public long Generation;

        /// <summary>
        /// The most recent generation a qualifying request was genuinely dispatched for. Advanced at
        /// the wire and nowhere else, so nothing that never sent can discharge an obligation.
        /// </summary>
        public long Served;

        public Task Wait = Task.CompletedTask;

        /// <summary>Recreations inside or queued for the gate right now.</summary>
        public int Users;
    }
}
