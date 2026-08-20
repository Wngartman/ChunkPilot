using System.Net;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// RFC 6886 section 3.7 in full: the randomised wait one detected reboot obliges, one obligation per
/// reboot, and the recreations that follow issued one at a time rather than released together.
/// </summary>
/// <remarks>
/// <para>
/// The section has two halves and they fail differently. Missing the wait sends every client on the
/// network at a gateway that has only just finished booting. Sharing one wait between every pending
/// request satisfies the wait and then releases all of them at the same instant, which is the burst the
/// second half — "The requests SHOULD be issued serially, one at a time; the client SHOULD NOT issue
/// multiple concurrent requests" — exists to prevent. Both are proven here through the real provider
/// building and parsing real RFC 6886 frames.
/// </para>
/// <para>
/// Deterministic throughout. The wait is injected and released by the test, so nothing waits for real
/// time, and the transport is a fixture that never leaves this process.
/// </para>
/// </remarks>
public sealed class NatPmpRebootRecoveryTests
{
    /// <summary>
    /// Long enough for a continuation that was going to run to have run. Every assertion that uses it
    /// is of the form "this must still not have happened", so it can only ever fail towards the defect.
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    // ── The obligation ──

    [Fact]
    public async Task A_first_mapping_on_a_gateway_that_has_not_rebooted_waits_for_nothing()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay();
        var provider = Provider(channel, delay);

        var outcome = await provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(delay.Waited);
        Assert.Equal(1, channel.MappingSends);
    }

    [Fact]
    public async Task A_detected_reboot_holds_the_first_recreation_until_the_wait_is_over()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        var create = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        await Task.Delay(Settle);

        Assert.False(create.IsCompleted);
        Assert.Equal(0, channel.MappingSends);

        delay.Release(0);

        Assert.True((await create).Success);
        Assert.Single(delay.Waited);
        Assert.InRange(delay.Waited[0], TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(1, channel.MappingSends);
    }

    /// <summary>
    /// The case an outstanding-task-per-gateway cannot express. A gateway that reboots again while the
    /// first wait is still running has rebooted twice, and the second one is owed its own recovery.
    /// </summary>
    [Fact]
    public async Task A_second_reboot_while_the_first_wait_is_running_is_not_lost()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        var create = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(create.IsCompleted);

        // It reboots again before anything has been recreated.
        await RebootAsync(provider, channel, Binding());
        Assert.Equal(2, delay.Waited.Count);

        // The first wait finishing discharges the first reboot and nothing else. A request that is now
        // owed the second one does not slip out through the door the first one opened.
        delay.Release(0);
        await Task.Delay(Settle);
        Assert.False(create.IsCompleted);
        Assert.Equal(0, channel.MappingSends);

        delay.Release(1);

        Assert.True((await create).Success);
        Assert.Equal(1, channel.MappingSends);
    }

    /// <summary>
    /// One reboot seen through several replies is one reboot. The epoch history classifies a reset once
    /// and reports every response after it as a continuation, so nothing multiplies the obligation.
    /// </summary>
    [Fact]
    public async Task The_same_reset_seen_through_several_replies_arms_one_wait()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        // The gateway keeps answering, still counting from where it restarted.
        for (var reply = 0; reply < 3; reply++)
        {
            channel.Epoch += 1;
            _ = await provider.DiscoverAsync(Binding(), CancellationToken.None);
        }

        Assert.Single(delay.Waited);
        delay.Release(0);
        Assert.True((await provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None))
            .Success);
        Assert.Single(delay.Waited);
    }

    [Fact]
    public async Task A_withdrawal_is_never_held_back_by_a_reboot()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        // Withdrawal is not recreation. Closing an exposure is never made to wait.
        var removed = await provider.RemoveAsync(Binding(), Discovery(), Request(), CancellationToken.None);

        Assert.True(removed.Success);
        Assert.Equal(1, channel.MappingSends);
    }

    // ── One at a time ──

    [Fact]
    public async Task Recreations_after_a_reboot_are_sent_one_at_a_time()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());
        channel.HoldMappingSends = true;

        var first = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        var second = provider.CreateAsync(Binding(), Discovery(),
            Request() with { InternalPort = 25_566, ExternalPort = 25_566 }, CancellationToken.None);
        delay.Release(0);

        // The wait opened for both of them, and exactly one request went out.
        Assert.True(await channel.WaitForMappingSendAsync());
        await Task.Delay(Settle);
        Assert.Equal(1, channel.MappingSends);

        // The second goes out only once the first has been answered, which is what the RFC's "after
        // that request is acknowledged by the gateway" asks for.
        channel.ReleaseNextMappingSend();
        Assert.True(await channel.WaitForMappingSendAsync());
        await Task.Delay(Settle);
        Assert.Equal(2, channel.MappingSends);
        channel.ReleaseNextMappingSend();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(1, channel.MaxConcurrentMappingSends);
    }

    [Fact]
    public async Task Different_gateways_never_wait_for_each_other()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        var held = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(held.IsCompleted);

        // Another router's recovery is not this router's business.
        var elsewhere = await provider.CreateAsync(Other(), Discovery(), Request(), CancellationToken.None);

        Assert.True(elsewhere.Success);
        Assert.False(held.IsCompleted);
        delay.Release(0);
        Assert.True((await held).Success);
    }

    /// <summary>
    /// The serialisation belongs to the recovery, not to the provider. Once the obligation is
    /// discharged and nothing is using it, ordinary mapping requests are not queued behind each other.
    /// </summary>
    [Fact]
    public async Task Once_a_recovery_is_over_ordinary_mappings_are_not_held_one_at_a_time()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay();
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());
        Assert.True((await provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None))
            .Success);

        channel.HoldMappingSends = true;
        var one = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        var two = provider.CreateAsync(Binding(), Discovery(),
            Request() with { InternalPort = 25_566, ExternalPort = 25_566 }, CancellationToken.None);
        Assert.True(await channel.WaitForMappingSendAsync());
        Assert.True(await channel.WaitForMappingSendAsync());
        channel.ReleaseNextMappingSend();
        channel.ReleaseNextMappingSend();

        Assert.True((await one).Success);
        Assert.True((await two).Success);
        Assert.Equal(2, channel.MaxConcurrentMappingSends);
        Assert.Single(delay.Waited);
    }

    // ── Cancellation ──

    [Fact]
    public async Task A_caller_cancelled_during_the_wait_leaves_the_obligation_for_the_next_one()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay { Hold = true };
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        using var cancellation = new CancellationTokenSource();
        var abandoned = provider.CreateAsync(Binding(), Discovery(), Request(), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.Equal(0, channel.MappingSends);

        // The request it was holding back never went out, so the obligation is not discharged — and the
        // next caller is not left stuck behind a gate the cancelled one still holds.
        var next = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(next.IsCompleted);
        delay.Release(0);

        Assert.True((await next).Success);
        Assert.Equal(1, channel.MappingSends);
        Assert.Single(delay.Waited);
    }

    [Fact]
    public async Task A_caller_cancelled_while_queued_for_the_gate_leaves_it_usable()
    {
        var channel = new ScriptedChannel();
        var delay = new ScriptedRebootDelay();
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());
        channel.HoldMappingSends = true;

        var first = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        Assert.True(await channel.WaitForMappingSendAsync());

        using var cancellation = new CancellationTokenSource();
        var queued = provider.CreateAsync(Binding(), Discovery(), Request(), cancellation.Token);
        await Task.Delay(Settle);
        Assert.False(queued.IsCompleted);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        channel.ReleaseNextMappingSend();
        Assert.True((await first).Success);

        // Nothing was left locked and no permit was lost with the caller that gave up.
        channel.HoldMappingSends = false;
        Assert.True((await provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None))
            .Success);
        Assert.Equal(2, channel.MappingSends);
        Assert.Equal(1, channel.MaxConcurrentMappingSends);
    }

    // ── The dispatch boundary, and the wire ──
    //
    // An obligation is discharged by a datagram genuinely leaving and by nothing else, and that is two
    // moments rather than one: where the last check happens and the send begins, and where the transport
    // says the datagram actually went out. Everything in between — a cancellation, a socket that refuses
    // it, another reboot — has to leave the obligation exactly where it was. These drive the recovery
    // directly, because the interleavings they force are between one recreation and the wire.

    /// <summary>
    /// The gap a "wait, then return, then send" shape leaves open: the delay is behind us, nothing has
    /// gone out, and the gateway restarts again.
    /// </summary>
    [Fact]
    public async Task A_reboot_recorded_after_the_gate_but_before_the_wire_is_not_stepped_over()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var send = new ScriptedSend();

        var recreation = recovery.RecreateAsync(Key, dispatch =>
        {
            delay.Hold = true;
            recovery.NoteRebooted(Key);
            return dispatch.SendAsync(onSent => send.RunAsync(onSent, CancellationToken.None),
                CancellationToken.None);
        }, CancellationToken.None);
        await Task.Delay(Settle);

        Assert.False(send.Started.IsCompleted);
        Assert.Equal(2, delay.Waited.Count);

        delay.Release(1);
        await send.Started;
        send.Deliver();
        _ = await recreation;

        Assert.Equal(1, send.Confirmations);
    }

    /// <summary>The same instant, reached from the other side: the wait itself ends into a reboot.</summary>
    [Fact]
    public async Task A_reboot_recorded_as_the_wait_ends_is_not_stepped_over()
    {
        var delay = new ScriptedRebootDelay { Hold = true };
        var recovery = new NatPmpRebootRecovery(delay);
        delay.OnceOnRelease = () => recovery.NoteRebooted(Key);
        recovery.NoteRebooted(Key);
        var send = new ScriptedSend();

        var recreation = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => send.RunAsync(onSent, CancellationToken.None), CancellationToken.None),
            CancellationToken.None);
        delay.Release(0);
        await Task.Delay(Settle);

        Assert.False(send.Started.IsCompleted);
        Assert.Equal(2, delay.Waited.Count);

        delay.Release(1);
        await send.Started;
        send.Deliver();
        _ = await recreation;

        Assert.Equal(1, send.Confirmations);
    }

    [Fact]
    public async Task A_caller_cancelled_after_the_gate_but_before_the_wire_leaves_the_obligation_standing()
    {
        var delay = new ScriptedRebootDelay { Hold = true };
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var abandoned = new ScriptedSend();
        var next = new ScriptedSend();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecreateAsync(Key,
            async dispatch =>
            {
                // Inside the gate, with nothing sent yet.
                await cancellation.CancelAsync().ConfigureAwait(false);
                return await dispatch
                    .SendAsync(onSent => abandoned.RunAsync(onSent, cancellation.Token), cancellation.Token)
                    .ConfigureAwait(false);
            }, cancellation.Token));

        Assert.False(abandoned.Started.IsCompleted);

        // Nothing went out, so nothing was served: the next recreation is owed the same wait, and the
        // gate the cancelled one left behind is free.
        var recreation = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => next.RunAsync(onSent, CancellationToken.None), CancellationToken.None),
            CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(next.Started.IsCompleted);
        Assert.Single(delay.Waited);

        delay.Release(0);
        await next.Started;
        next.Deliver();
        _ = await recreation;

        Assert.Equal(1, next.Confirmations);
    }

    [Fact]
    public async Task A_caller_cancelled_as_its_wait_ends_still_sends_nothing()
    {
        var delay = new ScriptedRebootDelay { Hold = true };
        var recovery = new NatPmpRebootRecovery(delay);
        using var cancellation = new CancellationTokenSource();
        delay.OnceOnRelease = cancellation.Cancel;
        recovery.NoteRebooted(Key);
        var send = new ScriptedSend();

        var recreation = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => send.RunAsync(onSent, cancellation.Token), cancellation.Token),
            cancellation.Token);
        delay.Release(0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recreation);
        Assert.False(send.Started.IsCompleted);
        Assert.Single(delay.Waited);
    }

    /// <summary>
    /// The datagram was already on its way when the caller gave up. It never went out, so the RFC's
    /// requirement has not been met and the obligation is exactly where it was.
    /// </summary>
    [Fact]
    public async Task A_caller_cancelled_while_its_datagram_is_still_going_out_serves_nothing()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var abandoned = new ScriptedSend();
        var next = new ScriptedSend();
        using var cancellation = new CancellationTokenSource();

        var recreation = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => abandoned.RunAsync(onSent, cancellation.Token), cancellation.Token),
            cancellation.Token);
        await abandoned.Started;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recreation);
        Assert.Equal(0, abandoned.Confirmations);

        // Nothing was served, so the recovery is still outstanding — which shows in the ordering it
        // still imposes. Had the abandoned attempt discharged it, the two that follow would have found
        // no recovery left and gone out together.
        var alongside = new ScriptedSend();
        var first = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => next.RunAsync(onSent, CancellationToken.None), CancellationToken.None),
            CancellationToken.None);
        var second = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => alongside.RunAsync(onSent, CancellationToken.None),
                CancellationToken.None), CancellationToken.None);
        await next.Started;
        await Task.Delay(Settle);
        Assert.False(alongside.Started.IsCompleted);

        next.Deliver();
        _ = await first;
        await alongside.Started;
        alongside.Deliver();
        _ = await second;

        Assert.Equal(1, next.Confirmations);
        Assert.Equal(1, alongside.Confirmations);
    }

    /// <summary>
    /// A socket that refuses the datagram is not a send. The attempt discharges nothing and does not
    /// mark the operation as under way, so its own retransmission is checked again — and a reboot
    /// recorded in the meantime holds it.
    /// </summary>
    [Fact]
    public async Task A_send_the_transport_refused_leaves_the_obligation_standing()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var refused = new ScriptedSend();
        var retransmission = new ScriptedSend();

        var recreation = recovery.RecreateAsync(Key, async dispatch =>
        {
            _ = await dispatch
                .SendAsync(onSent => refused.RunAsync(onSent, CancellationToken.None), CancellationToken.None)
                .ConfigureAwait(false);
            delay.Hold = true;
            recovery.NoteRebooted(Key);
            return await dispatch
                .SendAsync(onSent => retransmission.RunAsync(onSent, CancellationToken.None),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }, CancellationToken.None);

        await refused.Started;
        refused.Refuse();
        await Task.Delay(Settle);

        Assert.Equal(0, refused.Confirmations);
        Assert.False(retransmission.Started.IsCompleted);
        Assert.Equal(2, delay.Waited.Count);

        delay.Release(1);
        await retransmission.Started;
        retransmission.Deliver();
        _ = await recreation;

        Assert.Equal(1, retransmission.Confirmations);
    }

    /// <summary>
    /// A reboot recorded while the datagram is still going out belongs to the next recreation. The
    /// confirmation serves the generation the send was carrying, never whichever is current when it
    /// lands.
    /// </summary>
    [Fact]
    public async Task A_reboot_recorded_while_the_datagram_is_going_out_is_not_served_by_it()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var send = new ScriptedSend();
        var next = new ScriptedSend();

        var recreation = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => send.RunAsync(onSent, CancellationToken.None), CancellationToken.None),
            CancellationToken.None);
        await send.Started;

        delay.Hold = true;
        recovery.NoteRebooted(Key);
        send.Deliver();
        _ = await recreation;

        Assert.Equal(1, send.Confirmations);
        Assert.Equal(2, delay.Waited.Count);

        // The confirmed send served the first reboot only.
        var second = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => next.RunAsync(onSent, CancellationToken.None), CancellationToken.None),
            CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(next.Started.IsCompleted);

        delay.Release(1);
        await next.Started;
        next.Deliver();
        _ = await second;

        Assert.Equal(1, next.Confirmations);
    }

    /// <summary>
    /// Past the wire, the RFC's requirement has been met — it is about when a request may be sent — so
    /// a request that goes out and is then abandoned does not oblige the next one to wait again.
    /// </summary>
    [Fact]
    public async Task A_caller_cancelled_after_the_wire_leaves_the_gate_usable()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var sends = 0;
        using var cancellation = new CancellationTokenSource();
        Func<Action?, Task<byte[]?>> abandoned = async onSent =>
        {
            Interlocked.Increment(ref sends);
            onSent?.Invoke();
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.RecreateAsync(Key,
            dispatch => dispatch.SendAsync(abandoned, cancellation.Token), cancellation.Token));

        Assert.Equal(1, Volatile.Read(ref sends));

        _ = await recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => { Interlocked.Increment(ref sends); onSent?.Invoke(); return Delivered; },
                CancellationToken.None), CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref sends));
        Assert.Single(delay.Waited);
    }

    [Fact]
    public async Task A_reboot_recorded_after_the_wire_is_left_for_the_next_recreation()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var sends = 0;

        _ = await recovery.RecreateAsync(Key, async dispatch =>
        {
            var reply = await dispatch
                .SendAsync(onSent => { Interlocked.Increment(ref sends); onSent?.Invoke(); return Delivered; },
                    CancellationToken.None)
                .ConfigureAwait(false);
            // The reply proved the gateway had restarted again while this request was in flight.
            delay.Hold = true;
            recovery.NoteRebooted(Key);
            return reply;
        }, CancellationToken.None);

        Assert.Equal(1, Volatile.Read(ref sends));
        Assert.Equal(2, delay.Waited.Count);

        // Finishing the request that served the first reboot did not serve the second.
        var next = recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent => { Interlocked.Increment(ref sends); onSent?.Invoke(); return Delivered; },
                CancellationToken.None), CancellationToken.None);
        await Task.Delay(Settle);
        Assert.False(next.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref sends));

        delay.Release(1);
        _ = await next;

        Assert.Equal(2, Volatile.Read(ref sends));
    }

    /// <summary>
    /// A request already under way is not stalled by a reboot recorded behind it. Its retransmissions
    /// and the withdrawal of a substitute port belong to it; the reboot belongs to the next recreation.
    /// </summary>
    [Fact]
    public async Task A_request_already_under_way_is_not_held_back_by_a_later_reboot()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        var sends = 0;

        _ = await recovery.RecreateAsync(Key, async dispatch =>
        {
            _ = await dispatch
                .SendAsync(onSent => { Interlocked.Increment(ref sends); onSent?.Invoke(); return Delivered; },
                    CancellationToken.None)
                .ConfigureAwait(false);
            delay.Hold = true;
            recovery.NoteRebooted(Key);
            // The second datagram of the same operation, which must not be made to wait.
            return await dispatch
                .SendAsync(onSent => { Interlocked.Increment(ref sends); onSent?.Invoke(); return Delivered; },
                    CancellationToken.None)
                .ConfigureAwait(false);
        }, CancellationToken.None);

        Assert.Equal(2, Volatile.Read(ref sends));
    }

    // ── What the real transport confirms ──

    [Fact]
    public void A_complete_send_result_confirms_the_datagram_exactly_once()
    {
        var confirmations = 0;

        var complete = UdpGatewayDatagramChannel.ConfirmCompleteDatagram(
            bytesSent: 12, payloadLength: 12, onSent: () => confirmations++);

        Assert.True(complete);
        Assert.Equal(1, confirmations);
    }

    [Fact]
    public void A_short_send_result_confirms_nothing_and_fails_the_exchange()
    {
        var confirmations = 0;

        var complete = UdpGatewayDatagramChannel.ConfirmCompleteDatagram(
            bytesSent: 11, payloadLength: 12, onSent: () => confirmations++);

        Assert.False(complete);
        Assert.Equal(0, confirmations);
    }

    [Fact]
    public async Task A_short_send_result_leaves_reboot_recovery_for_a_later_complete_send()
    {
        var delay = new ScriptedRebootDelay();
        var recovery = new NatPmpRebootRecovery(delay);
        recovery.NoteRebooted(Key);
        Action? shortConfirmation = null;

        var shortResult = await recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent =>
            {
                shortConfirmation = onSent;
                var complete = UdpGatewayDatagramChannel.ConfirmCompleteDatagram(11, 12, onSent);
                return Task.FromResult<byte[]?>(complete ? [] : null);
            }, CancellationToken.None), CancellationToken.None);

        Assert.Null(shortResult);
        Assert.NotNull(shortConfirmation);

        Action? completeConfirmation = null;
        var completeResult = await recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent =>
            {
                completeConfirmation = onSent;
                var complete = UdpGatewayDatagramChannel.ConfirmCompleteDatagram(12, 12, onSent);
                return Task.FromResult<byte[]?>(complete ? [] : null);
            }, CancellationToken.None), CancellationToken.None);

        Assert.NotNull(completeResult);
        Assert.NotNull(completeConfirmation);
        Assert.Single(delay.Waited);

        Action? afterRecovery = null;
        _ = await recovery.RecreateAsync(Key, dispatch =>
            dispatch.SendAsync(onSent =>
            {
                afterRecovery = onSent;
                return Delivered;
            }, CancellationToken.None), CancellationToken.None);

        Assert.Null(afterRecovery);
    }

    [Fact]
    public async Task The_udp_channel_confirms_a_datagram_once_it_has_actually_been_sent()
    {
        await using var gateway = FakeDatagramGateway.Start(request => NatPmp.AddressReply(0, "203.0.113.7"));
        var channel = new UdpGatewayDatagramChannel();
        var confirmations = 0;

        var reply = await channel.ExchangeAsync(IPAddress.Loopback,
            new IPEndPoint(IPAddress.Loopback, gateway.Port), [0, 0], TimeSpan.FromSeconds(5),
            () => Interlocked.Increment(ref confirmations), CancellationToken.None);

        Assert.NotNull(reply);
        Assert.Equal(1, Volatile.Read(ref confirmations));
    }

    /// <summary>
    /// Confirmation is about the send, not the answer. A gateway that says nothing back has still been
    /// written to, and the RFC's requirement was about writing to it.
    /// </summary>
    [Fact]
    public async Task The_udp_channel_confirms_a_datagram_even_when_nothing_answers()
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var channel = new UdpGatewayDatagramChannel();
        var confirmations = 0;

        var reply = await channel.ExchangeAsync(IPAddress.Loopback,
            new IPEndPoint(IPAddress.Loopback, gateway.Port), [0, 0], TimeSpan.FromMilliseconds(300),
            () => Interlocked.Increment(ref confirmations), CancellationToken.None);

        Assert.Null(reply);
        Assert.Equal(1, Volatile.Read(ref confirmations));
    }

    [Fact]
    public async Task The_udp_channel_confirms_nothing_when_the_send_never_happens()
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var channel = new UdpGatewayDatagramChannel();
        var confirmations = 0;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ExchangeAsync(IPAddress.Loopback,
            new IPEndPoint(IPAddress.Loopback, gateway.Port), [0, 0], TimeSpan.FromSeconds(5),
            () => Interlocked.Increment(ref confirmations), cancellation.Token));

        Assert.Equal(0, Volatile.Read(ref confirmations));
    }

    // ── The same, through the provider ──

    [Fact]
    public async Task A_recreation_whose_sends_are_all_refused_confirms_nothing_and_fails_truthfully()
    {
        var channel = new ScriptedChannel { FailMappingSends = true };
        var delay = new ScriptedRebootDelay();
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        var outcome = await provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, outcome.Failure);
        Assert.True(channel.MappingSends > 0);
        Assert.Equal(0, channel.ConfirmedMappingSends);
    }

    /// <summary>
    /// The other side of the boundary: the datagram went out and the gateway said nothing. The
    /// operation fails for what it is, and the reboot delay it already served is not owed again.
    /// </summary>
    [Fact]
    public async Task A_confirmed_send_that_is_never_answered_still_serves_its_obligation()
    {
        var channel = new ScriptedChannel { AnswerMappingSends = false };
        var delay = new ScriptedRebootDelay();
        var provider = Provider(channel, delay);
        await RebootAsync(provider, channel, Binding());

        var outcome = await provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, outcome.Failure);
        Assert.True(channel.ConfirmedMappingSends > 0);
        Assert.Single(delay.Waited);

        // The obligation is discharged, so what follows is ordinary traffic again: nothing is queued
        // behind anything and no fresh delay was armed.
        channel.AnswerMappingSends = true;
        channel.HoldMappingSends = true;
        var one = provider.CreateAsync(Binding(), Discovery(), Request(), CancellationToken.None);
        var two = provider.CreateAsync(Binding(), Discovery(),
            Request() with { InternalPort = 25_566, ExternalPort = 25_566 }, CancellationToken.None);
        Assert.True(await channel.WaitForMappingSendAsync());
        Assert.True(await channel.WaitForMappingSendAsync());
        channel.ReleaseNextMappingSend();
        channel.ReleaseNextMappingSend();
        Assert.True((await one).Success);
        Assert.True((await two).Success);
        Assert.Equal(2, channel.MaxConcurrentMappingSends);
        Assert.Single(delay.Waited);
    }

    // ── Harness ──

    private const string Key = "eth|192.168.1.1|5351";

    private static Task<byte[]?> Delivered => Task.FromResult<byte[]?>([]);

    private static NatPmpMappingProvider Provider(ScriptedChannel channel, INatPmpRebootDelay delay) =>
        new(channel,
            new RouterMappingOptions
            {
                GatewayControlPort = 5351,
                DatagramAttemptTimeouts = [TimeSpan.FromSeconds(30)]
            },
            new FixtureClock(),
            delay);

    /// <summary>
    /// Makes the gateway prove one fresh reset the way a real one does: a counter that was climbing,
    /// and then a reply carrying a counter that plainly restarted.
    /// </summary>
    private static async Task RebootAsync(
        NatPmpMappingProvider provider, ScriptedChannel channel, RouterLanBinding binding)
    {
        channel.Epoch += 40_000;
        _ = await provider.DiscoverAsync(binding, CancellationToken.None);
        channel.Epoch = 2;
        var restarted = await provider.DiscoverAsync(binding, CancellationToken.None);
        Assert.Equal(GatewayContinuity.StateLost, restarted.Continuity);
    }

    private static RouterLanBinding Binding() =>
        new("eth", "Ethernet", IPAddress.Parse("192.168.1.50"), 24, IPAddress.Parse("192.168.1.1"));

    private static RouterLanBinding Other() =>
        new("wifi", "Wi-Fi", IPAddress.Parse("192.168.2.50"), 24, IPAddress.Parse("192.168.2.1"));

    private static RouterDiscoveryResult Discovery() =>
        new() { Mechanism = RouterMappingMechanism.NatPmp, Supported = true, ExternalAddress = "203.0.113.7" };

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp,
        InternalPort = 25_565,
        ExternalPort = 25_565,
        LeaseSeconds = 3_600
    };

    /// <summary>
    /// One datagram, driven through the same two moments the real transport has: it is started, and
    /// then — separately, and only if the test says so — it actually goes out.
    /// </summary>
    /// <remarks>
    /// The gap between those two is where a socket refuses the datagram, where a caller gives up, and
    /// where another reboot can be recorded. A fixture that collapses them into one call cannot express
    /// any of that, which is exactly what made the defect invisible.
    /// </remarks>
    private sealed class ScriptedSend
    {
        private readonly TaskCompletionSource started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<byte[]?> finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Action? confirm;
        private int confirmations;

        /// <summary>Completes when the send has been started, which is not yet a send.</summary>
        public Task Started => started.Task;

        public int Confirmations => Volatile.Read(ref confirmations);

        public Task<byte[]?> RunAsync(Action? onSent, CancellationToken cancellationToken)
        {
            confirm = onSent;
            started.TrySetResult();
            return finished.Task.WaitAsync(cancellationToken);
        }

        /// <summary>The datagram went out, and an answer came back.</summary>
        public void Deliver()
        {
            Interlocked.Increment(ref confirmations);
            confirm?.Invoke();
            finished.TrySetResult([]);
        }

        /// <summary>The socket refused it: nothing left, so nothing is confirmed and nothing answers.</summary>
        public void Refuse() => finished.TrySetResult(null);
    }

    /// <summary>
    /// A NAT-PMP gateway the test drives directly: it answers real RFC 6886 frames, reports whatever
    /// epoch the test sets, and can hold each mapping request open so overlap is observable.
    /// </summary>
    /// <remarks>
    /// Address requests are answered immediately and are not counted. Only mapping requests are held and
    /// measured, because "one at a time" is a statement about those.
    /// </remarks>
    private sealed class ScriptedChannel : IGatewayDatagramChannel
    {
        private readonly Lock gate = new();
        private readonly Queue<TaskCompletionSource> holds = new();
        private readonly SemaphoreSlim arrivals = new(0);
        private int concurrent;

        /// <summary>Seconds Since Start of Epoch every reply reports.</summary>
        public volatile uint Epoch = 40_000;

        public bool HoldMappingSends { get; set; }

        /// <summary>The socket refuses the next sends: they are attempted and never go out.</summary>
        public bool FailMappingSends { get; set; }

        /// <summary>Whether a confirmed send is answered, or left to time out like a silent gateway.</summary>
        public bool AnswerMappingSends { get; set; } = true;

        /// <summary>Mapping requests attempted, whether or not any of them left.</summary>
        public int MappingSends
        {
            get { lock (gate) return sends; }
        }

        /// <summary>Mapping requests the transport reported as genuinely sent.</summary>
        public int ConfirmedMappingSends
        {
            get { lock (gate) return confirmed; }
        }

        public int MaxConcurrentMappingSends
        {
            get { lock (gate) return peak; }
        }

        private int sends;
        private int confirmed;
        private int peak;

        public async Task<byte[]?> ExchangeAsync(
            IPAddress localAddress,
            IPEndPoint gateway,
            byte[] payload,
            TimeSpan timeout,
            Action? onSent,
            CancellationToken cancellationToken)
        {
            if (NatPmp.IsAddressRequest(payload))
                return NatPmp.AddressReply(0, "203.0.113.7", Epoch);

            TaskCompletionSource? hold = null;
            bool refuse;
            lock (gate)
            {
                sends++;
                concurrent++;
                if (concurrent > peak)
                    peak = concurrent;
                refuse = FailMappingSends;
                if (HoldMappingSends)
                {
                    hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    holds.Enqueue(hold);
                }
            }
            arrivals.Release();
            try
            {
                if (hold is not null)
                    await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                // Exactly where the real channel stands: everything that could stop the datagram has
                // either happened by now or has not, and only past this line has one actually gone out.
                if (refuse)
                    return null;
                lock (gate)
                    confirmed++;
                onSent?.Invoke();
                return AnswerMappingSends
                    ? NatPmp.MapReply(NatPmp.Opcode(payload), 0, NatPmp.InternalPort(payload),
                        NatPmp.SuggestedExternalPort(payload), NatPmp.Lifetime(payload), Epoch)
                    : null;
            }
            finally
            {
                lock (gate)
                    concurrent--;
            }
        }

        /// <summary>Waits for one mapping request to arrive. False means none did.</summary>
        public Task<bool> WaitForMappingSendAsync() => arrivals.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseNextMappingSend()
        {
            lock (gate)
                if (holds.Count > 0)
                    holds.Dequeue().TrySetResult();
        }
    }

    /// <summary>The RFC 6886 section 3.7 wait, held open until the test lets it finish.</summary>
    /// <remarks>
    /// One entry per detected reboot, in order, whether or not that one was held, so a test that arms
    /// some reboots instantly and others held still refers to each by the reboot it belongs to.
    /// </remarks>
    private sealed class ScriptedRebootDelay : INatPmpRebootDelay
    {
        private readonly List<TaskCompletionSource?> holds = [];
        private readonly List<TimeSpan> waited = [];
        private readonly Lock gate = new();

        /// <summary>Whether waits armed from now on are held for the test, or satisfied at once.</summary>
        public bool Hold { get; set; }

        /// <summary>
        /// Runs when the next wait armed from now on ends, before whoever was waiting resumes. The one
        /// deterministic way to place an event in the instant between "the delay is over" and "the
        /// request goes out".
        /// </summary>
        public Action? OnceOnRelease { get; set; }

        public IReadOnlyList<TimeSpan> Waited
        {
            get { lock (gate) return waited.ToArray(); }
        }

        public TimeSpan NextDelay() => TimeSpan.FromSeconds(2);

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Action? hook;
            TaskCompletionSource? source = null;
            lock (gate)
            {
                waited.Add(duration);
                if (Hold)
                    source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                holds.Add(source);
                hook = OnceOnRelease;
                OnceOnRelease = null;
            }
            var wait = source?.Task ?? Task.CompletedTask;
            return hook is null ? wait : RunOnRelease(wait, hook);
        }

        /// <summary>Lets the wait armed by the nth detected reboot finish.</summary>
        public void Release(int reboot)
        {
            lock (gate)
                holds[reboot]?.TrySetResult();
        }

        private static async Task RunOnRelease(Task wait, Action hook)
        {
            await wait.ConfigureAwait(false);
            hook();
        }
    }
}
