using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// The epoch rules of RFC 6887 section 8.5 and RFC 6886 section 3.6, and the two datagram providers
/// applying them to real wire-format replies.
/// </summary>
/// <remarks>
/// Neither PCP nor NAT-PMP can read a router's mapping table, and both protocols make a renewal
/// byte-identical to a creation. The epoch each carries is therefore the only evidence in existence
/// that distinguishes "this entry is still the one I had" from "a restarted gateway has just made me a
/// new one", which is why it is tested against the RFC's own tolerances rather than an invented rule.
/// </remarks>
public sealed class GatewayEpochContinuityTests
{
    private static readonly RouterLanBinding Gateway =
        new("loopback-fixture", "Fixture", System.Net.IPAddress.Loopback, 8, System.Net.IPAddress.Loopback);

    private static readonly RouterLanBinding OtherGateway =
        new("wifi-fixture", "Other", System.Net.IPAddress.Loopback, 8, System.Net.IPAddress.Parse("192.168.9.1"));

    // ── The rule itself ──

    [Fact]
    public void The_first_response_from_a_gateway_proves_nothing_either_way()
    {
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, new FixtureClock());

        Assert.Equal(GatewayContinuity.Unknown, tracker.Observe(Gateway, 5351, 1_000));
    }

    [Fact]
    public void A_pcp_epoch_that_advances_with_the_clock_confirms_continuity()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, clock);
        _ = tracker.Observe(Gateway, 5351, 1_000);

        clock.Advance(TimeSpan.FromSeconds(1_800));

        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(Gateway, 5351, 2_800));
    }

    [Fact]
    public void A_pcp_epoch_that_restarted_proves_the_server_lost_its_mappings()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, clock);
        _ = tracker.Observe(Gateway, 5351, 60_000);

        clock.Advance(TimeSpan.FromSeconds(1_800));

        // The gateway rebooted 12 seconds ago and is counting from zero again.
        Assert.Equal(GatewayContinuity.StateLost, tracker.Observe(Gateway, 5351, 12));
    }

    /// <summary>RFC 6887 section 8.5 tolerates the epoch appearing to go back by up to one second.</summary>
    [Fact]
    public void A_pcp_epoch_one_second_behind_is_reordering_rather_than_a_restart()
    {
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, new FixtureClock());
        _ = tracker.Observe(Gateway, 5351, 1_000);

        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(Gateway, 5351, 999));
        // Two seconds back is past the tolerance the RFC grants.
        Assert.Equal(GatewayContinuity.StateLost, tracker.Observe(Gateway, 5351, 997));
    }

    /// <summary>
    /// The "/16" allowance exists for cheap gateways that keep poor time, and it must not be spent on
    /// declaring a working router restarted.
    /// </summary>
    [Fact]
    public void A_pcp_epoch_drifting_within_the_rfc_tolerance_still_confirms_continuity()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, clock);
        _ = tracker.Observe(Gateway, 5351, 1_000);

        clock.Advance(TimeSpan.FromSeconds(3_600));

        // The gateway's clock ran 5% slow over the hour: well inside the 6.25% the RFC allows.
        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(Gateway, 5351, 1_000 + 3_420));
    }

    [Fact]
    public void A_pcp_epoch_that_leapt_far_ahead_of_the_clock_is_not_a_continuation()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, clock);
        _ = tracker.Observe(Gateway, 5351, 1_000);

        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(GatewayContinuity.StateLost, tracker.Observe(Gateway, 5351, 90_000));
    }

    [Fact]
    public void A_nat_pmp_counter_that_keeps_up_with_the_clock_confirms_continuity()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.NatPortMapping, clock);
        _ = tracker.Observe(Gateway, 5351, 4_000);

        clock.Advance(TimeSpan.FromSeconds(1_800));

        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(Gateway, 5351, 5_800));
    }

    /// <summary>
    /// RFC 6886 section 3.6: the client's estimate is seven eighths of its own elapsed time, so a
    /// gateway running 12.5% slow is still a continuation and is not reported as a reboot.
    /// </summary>
    [Fact]
    public void A_nat_pmp_counter_inside_the_seven_eighths_estimate_confirms_continuity()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.NatPortMapping, clock);
        _ = tracker.Observe(Gateway, 5351, 4_000);

        clock.Advance(TimeSpan.FromSeconds(800));

        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(Gateway, 5351, 4_000 + 700));
    }

    [Fact]
    public void A_nat_pmp_counter_that_restarted_proves_the_table_was_lost()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.NatPortMapping, clock);
        _ = tracker.Observe(Gateway, 5351, 40_000);

        clock.Advance(TimeSpan.FromSeconds(1_800));

        Assert.Equal(GatewayContinuity.StateLost, tracker.Observe(Gateway, 5351, 30));
    }

    [Fact]
    public void A_nat_pmp_counter_that_barely_moved_over_an_hour_is_a_reboot_not_drift()
    {
        var clock = new FixtureClock();
        var tracker = new GatewayEpochTracker(GatewayEpochRule.NatPortMapping, clock);
        _ = tracker.Observe(Gateway, 5351, 4_000);

        clock.Advance(TimeSpan.FromSeconds(3_600));

        Assert.Equal(GatewayContinuity.StateLost, tracker.Observe(Gateway, 5351, 4_010));
    }

    /// <summary>Two routers must never be compared with each other.</summary>
    [Fact]
    public void Each_gateway_keeps_its_own_history()
    {
        var tracker = new GatewayEpochTracker(GatewayEpochRule.PortControlProtocol, new FixtureClock());
        _ = tracker.Observe(Gateway, 5351, 900_000);

        // A different interface and gateway: its low epoch says nothing about the first one.
        Assert.Equal(GatewayContinuity.Unknown, tracker.Observe(OtherGateway, 5351, 5));
        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(Gateway, 5351, 900_000));
        Assert.Equal(GatewayContinuity.Confirmed, tracker.Observe(OtherGateway, 5351, 5));
    }

    // ── PCP, against the real wire format ──

    [Fact]
    public async Task A_pcp_renewal_on_a_gateway_that_kept_its_epoch_reports_continuity()
    {
        var epoch = 1_000u;
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply(epochSeconds: epoch)
                : Pcp.MapReply(request, 0, 3600, Pcp.SuggestedExternalPort(request), "203.0.113.9",
                    epochSeconds: epoch));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(), new FixtureClock());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        epoch += 1;
        var renewed = await provider.CreateAsync(gateway.Binding(), discovery,
            Request() with { OwnershipToken = created.OwnershipToken }, CancellationToken.None);

        // The first response from this gateway could prove nothing; the renewal can and does.
        Assert.Equal(GatewayContinuity.Unknown, discovery.Continuity);
        Assert.Equal(GatewayContinuity.Confirmed, created.Continuity);
        Assert.Equal(GatewayContinuity.Confirmed, renewed.Continuity);
    }

    [Fact]
    public async Task A_pcp_renewal_after_a_reboot_reports_state_loss_however_identical_the_reply_is()
    {
        var epoch = 90_000u;
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply(epochSeconds: epoch)
                : Pcp.MapReply(request, 0, 3600, Pcp.SuggestedExternalPort(request), "203.0.113.9",
                    epochSeconds: epoch));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(), new FixtureClock());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        epoch = 3; // The gateway rebooted and is counting from zero again.
        var renewed = await provider.CreateAsync(gateway.Binding(), discovery,
            Request() with { OwnershipToken = created.OwnershipToken }, CancellationToken.None);

        Assert.True(renewed.Success);
        // Everything a caller could compare is the same...
        Assert.Equal(created.ExternalPort, renewed.ExternalPort);
        Assert.Equal(created.ExternalAddress, renewed.ExternalAddress);
        Assert.Equal(created.LeaseSeconds, renewed.LeaseSeconds);
        Assert.Equal(created.OwnershipToken, renewed.OwnershipToken);
        // ...and the epoch still says this is a new mapping.
        Assert.Equal(GatewayContinuity.StateLost, renewed.Continuity);
    }

    [Fact]
    public async Task A_pcp_reply_that_refuses_the_request_still_reports_the_reboot_it_revealed()
    {
        var epoch = 90_000u;
        byte resultCode = 0;
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply(epochSeconds: epoch)
                : Pcp.MapReply(request, resultCode, 3600, Pcp.SuggestedExternalPort(request), "203.0.113.9",
                    epochSeconds: epoch));
        // Keep the protocol assertion deterministic on busy CI hosts: a delayed loopback UDP
        // baseline must not make the later, valid reboot observation look continuity-unknown.
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 3),
            new FixtureClock());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        epoch = 4;
        resultCode = 8; // NO_RESOURCES from a gateway that has just come back up with nothing.
        var refused = await provider.CreateAsync(gateway.Binding(), discovery,
            Request() with { OwnershipToken = created.OwnershipToken }, CancellationToken.None);

        Assert.False(refused.Success);
        Assert.Equal(RouterMappingFailure.OutOfResources, refused.Failure);
        Assert.Equal(GatewayContinuity.StateLost, refused.Continuity);
    }

    [Fact]
    public async Task A_pcp_announce_reports_the_reboot_it_saw()
    {
        var epoch = 50_000u;
        await using var gateway = FakeDatagramGateway.Start(_ => Pcp.AnnounceReply(epochSeconds: epoch));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(), new FixtureClock());

        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        epoch = 6;
        var second = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(second.Supported);
        Assert.Equal(GatewayContinuity.StateLost, second.Continuity);
    }

    // ── NAT-PMP, against the real wire format ──

    [Fact]
    public async Task A_nat_pmp_renewal_on_a_gateway_that_kept_its_counter_reports_continuity()
    {
        var epoch = 4_000u;
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "203.0.113.7", epoch)
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.SuggestedExternalPort(request), NatPmp.Lifetime(request), epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        epoch += 3;
        var renewed = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.Equal(GatewayContinuity.Unknown, discovery.Continuity);
        Assert.Equal(GatewayContinuity.Confirmed, created.Continuity);
        Assert.Equal(GatewayContinuity.Confirmed, renewed.Continuity);
    }

    [Fact]
    public async Task A_nat_pmp_renewal_after_a_reboot_reports_state_loss_however_identical_the_reply_is()
    {
        var epoch = 40_000u;
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "203.0.113.7", epoch)
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.SuggestedExternalPort(request), NatPmp.Lifetime(request), epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        epoch = 2; // RFC 6886 section 3.6: a gateway that lost its table MUST restart this counter.
        var renewed = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(renewed.Success);
        Assert.Equal(created.ExternalPort, renewed.ExternalPort);
        Assert.Equal(created.LeaseSeconds, renewed.LeaseSeconds);
        Assert.Equal(GatewayContinuity.StateLost, renewed.Continuity);
    }

    [Fact]
    public async Task A_nat_pmp_address_request_reports_the_reboot_it_saw()
    {
        var epoch = 40_000u;
        await using var gateway = FakeDatagramGateway.Start(_ => NatPmp.AddressReply(0, "203.0.113.7", epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock());

        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        epoch = 9;
        var second = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(second.Supported);
        Assert.Equal(GatewayContinuity.StateLost, second.Continuity);
    }

    // ── Continuity must outlive the outcome it happens to end up as ──
    //
    // A gateway that has restarted is free to answer a mapping request with a substitute public port,
    // and ChunkPilot converts that into a conflict because the port it asked for is taken. The reboot
    // is a fact about the gateway; the conflict is a conclusion about the port. Losing the first while
    // recording the second is how a verified public endpoint outlives the mapping it was measured on.

    [Fact]
    public async Task A_pcp_substitute_port_after_a_reboot_reports_the_conflict_and_keeps_the_state_loss()
    {
        var epoch = 90_000u;
        var substitute = 0;
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply(epochSeconds: epoch)
                : Pcp.MapReply(request, 0, Pcp.RequestedLifetime(request),
                    Pcp.RequestedLifetime(request) == 0 ? 0
                        : substitute > 0 ? substitute : Pcp.SuggestedExternalPort(request),
                    "203.0.113.9", epochSeconds: epoch));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        Assert.True(created.Success);

        // The gateway reboots and answers the renewal with a different public port.
        epoch = 5;
        substitute = 51_000;
        var conflicted = await provider.CreateAsync(gateway.Binding(), discovery,
            Request() with { OwnershipToken = created.OwnershipToken }, CancellationToken.None);

        Assert.False(conflicted.Success);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, conflicted.Failure);
        Assert.Equal(GatewayContinuity.StateLost, conflicted.Continuity);
    }

    [Fact]
    public async Task A_nat_pmp_substitute_port_after_a_reboot_reports_the_conflict_and_keeps_the_state_loss()
    {
        var epoch = 40_000u;
        var substitute = 0;
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "203.0.113.7", epoch)
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.Lifetime(request) == 0 ? 0
                        : substitute > 0 ? substitute : NatPmp.SuggestedExternalPort(request),
                    NatPmp.Lifetime(request), epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock(), new InstantRebootDelay());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var created = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        Assert.True(created.Success);

        epoch = 7;
        substitute = 51_000;
        var conflicted = await provider.CreateAsync(gateway.Binding(), discovery, Request(),
            CancellationToken.None);

        Assert.False(conflicted.Success);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, conflicted.Failure);
        Assert.Equal(GatewayContinuity.StateLost, conflicted.Continuity);
    }

    /// <summary>A substitute port on a gateway that did not reboot must not invent state loss.</summary>
    [Fact]
    public async Task A_substitute_port_without_a_reboot_reports_no_state_loss()
    {
        var epoch = 40_000u;
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "203.0.113.7", epoch)
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.Lifetime(request) == 0 ? 0 : 51_000, NatPmp.Lifetime(request), epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock(), new InstantRebootDelay());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var conflicted = await provider.CreateAsync(gateway.Binding(), discovery, Request(),
            CancellationToken.None);

        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, conflicted.Failure);
        Assert.NotEqual(GatewayContinuity.StateLost, conflicted.Continuity);
    }

    // ── A refused discovery has still answered ──

    [Fact]
    public async Task A_refused_pcp_discovery_still_reports_the_reboot_its_header_proved()
    {
        var epoch = 50_000u;
        byte resultCode = 0;
        await using var gateway = FakeDatagramGateway.Start(_ =>
            Pcp.AnnounceReply(resultCode: resultCode, epochSeconds: epoch));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1),
            new FixtureClock());
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        epoch = 4;
        resultCode = 2; // NOT_AUTHORIZED from a gateway that has just come back up.
        var refused = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(refused.Supported);
        Assert.Equal(RouterMappingFailure.NotAuthorized, refused.Failure);
        Assert.Equal(GatewayContinuity.StateLost, refused.Continuity);
    }

    [Fact]
    public async Task A_refused_nat_pmp_discovery_still_reports_the_reboot_its_header_proved()
    {
        var epoch = 40_000u;
        ushort resultCode = 0;
        await using var gateway = FakeDatagramGateway.Start(_ =>
            NatPmp.AddressReply(resultCode, "203.0.113.7", epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 3),
            new FixtureClock(), new InstantRebootDelay());
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        epoch = 6;
        resultCode = 3; // Network Failure.
        var refused = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(refused.Supported);
        Assert.Equal(RouterMappingFailure.NetworkFailure, refused.Failure);
        Assert.Equal(GatewayContinuity.StateLost, refused.Continuity);
    }

    // ── The report that carries it to the Agent ──

    [Fact]
    public void A_capability_report_surfaces_continuity_from_an_attempt_that_was_not_selected()
    {
        var report = new RouterCapabilityReport
        {
            Selected = Attempt(RouterMappingMechanism.NatPmp, true, GatewayContinuity.Confirmed),
            Attempts =
            [
                Attempt(RouterMappingMechanism.Pcp, false, GatewayContinuity.StateLost),
                Attempt(RouterMappingMechanism.NatPmp, true, GatewayContinuity.Confirmed)
            ]
        };

        Assert.Equal(GatewayContinuity.StateLost, report.ContinuityFor(RouterMappingMechanism.Pcp));
        Assert.Equal(GatewayContinuity.Confirmed, report.ContinuityFor(RouterMappingMechanism.NatPmp));
        // A mechanism that was never asked says nothing, and neither does "no mechanism".
        Assert.Equal(GatewayContinuity.Unknown, report.ContinuityFor(RouterMappingMechanism.UpnpIgd));
        Assert.Equal(GatewayContinuity.Unknown, report.ContinuityFor(RouterMappingMechanism.None));
    }

    [Fact]
    public void A_capability_report_with_no_selection_at_all_still_carries_its_evidence()
    {
        var report = new RouterCapabilityReport
        {
            Failure = RouterMappingFailure.MechanismUnsupported,
            Attempts = [Attempt(RouterMappingMechanism.Pcp, false, GatewayContinuity.StateLost)]
        };

        Assert.False(report.Supported);
        Assert.Equal(GatewayContinuity.StateLost, report.ContinuityFor(RouterMappingMechanism.Pcp));
    }

    [Fact]
    public void A_spent_report_no_longer_offers_its_continuity_a_second_time()
    {
        var report = new RouterCapabilityReport
        {
            Selected = Attempt(RouterMappingMechanism.NatPmp, true, GatewayContinuity.Confirmed),
            Attempts =
            [
                Attempt(RouterMappingMechanism.Pcp, false, GatewayContinuity.StateLost),
                Attempt(RouterMappingMechanism.NatPmp, true, GatewayContinuity.Confirmed)
            ]
        };

        var spent = report.WithContinuitySpent();

        Assert.Equal(GatewayContinuity.Unknown, spent.ContinuityFor(RouterMappingMechanism.Pcp));
        Assert.Equal(GatewayContinuity.Unknown, spent.ContinuityFor(RouterMappingMechanism.NatPmp));
        Assert.Equal(GatewayContinuity.Unknown, spent.Selected!.Continuity);
        // Everything else about the report survives being spent.
        Assert.True(spent.Supported);
        Assert.Equal(report.Attempts.Count, spent.Attempts.Count);
    }

    [Theory]
    [InlineData(GatewayContinuity.StateLost, GatewayContinuity.Confirmed, GatewayContinuity.StateLost)]
    [InlineData(GatewayContinuity.Confirmed, GatewayContinuity.StateLost, GatewayContinuity.StateLost)]
    [InlineData(GatewayContinuity.Unknown, GatewayContinuity.StateLost, GatewayContinuity.StateLost)]
    [InlineData(GatewayContinuity.Unknown, GatewayContinuity.Confirmed, GatewayContinuity.Confirmed)]
    [InlineData(GatewayContinuity.Unknown, GatewayContinuity.Unknown, GatewayContinuity.Unknown)]
    public void Evidence_of_loss_always_wins_when_two_readings_are_combined(
        GatewayContinuity first, GatewayContinuity second, GatewayContinuity expected) =>
        Assert.Equal(expected, GatewayContinuityEvidence.Stronger(first, second));

    // ── RFC 6886 section 3.7: the randomised recreation delay ──

    [Fact]
    public void The_production_reboot_delay_is_drawn_from_the_interval_the_rfc_specifies()
    {
        var delay = new NatPmpRebootDelay();

        var draws = Enumerable.Range(0, 500).Select(_ => delay.NextDelay()).ToArray();

        Assert.All(draws, draw =>
        {
            Assert.True(draw >= TimeSpan.Zero, $"{draw} is below zero.");
            Assert.True(draw <= TimeSpan.FromSeconds(5), $"{draw} is above the five second maximum.");
        });
        // A constant would satisfy the bounds and defeat the purpose, so the draw must actually vary.
        Assert.True(draws.Distinct().Count() > 1);
    }

    [Fact]
    public async Task A_normal_nat_pmp_renewal_waits_for_nothing()
    {
        var epoch = 4_000u;
        var delay = new RecordingRebootDelay();
        await using var gateway = FakeDatagramGateway.Start(request => NatPmpReply(request, epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock(), delay);
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        _ = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        epoch += 1;
        _ = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.Empty(delay.Waited);
    }

    [Fact]
    public async Task A_detected_reboot_delays_the_recreation_that_follows_it_exactly_once()
    {
        var epoch = 40_000u;
        var delay = new RecordingRebootDelay();
        await using var gateway = FakeDatagramGateway.Start(request => NatPmpReply(request, epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock(), delay);
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        // The gateway reboots and the next discovery proves it; the recreation that follows waits.
        epoch = 3;
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        _ = await provider.CreateAsync(gateway.Binding(), Discovery(), Request(), CancellationToken.None);
        _ = await provider.CreateAsync(gateway.Binding(), Discovery(), Request(), CancellationToken.None);

        // One reboot, one wait — not one per request, and not one per reconciliation pass.
        Assert.Single(delay.Waited);
        Assert.Equal(delay.Fixed, delay.Waited[0]);
    }

    [Fact]
    public async Task A_second_distinct_reboot_earns_a_second_delay()
    {
        var epoch = 40_000u;
        var delay = new RecordingRebootDelay();
        await using var gateway = FakeDatagramGateway.Start(request => NatPmpReply(request, epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock(), delay);
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        epoch = 3;
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        _ = await provider.CreateAsync(gateway.Binding(), Discovery(), Request(), CancellationToken.None);
        Assert.Single(delay.Waited);

        // It runs for a while, and then reboots again.
        epoch = 60_000;
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        epoch = 2;
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        _ = await provider.CreateAsync(gateway.Binding(), Discovery(), Request(), CancellationToken.None);

        Assert.Equal(2, delay.Waited.Count);
    }

    [Fact]
    public async Task Cancelling_during_the_reboot_delay_aborts_without_sending_anything()
    {
        var epoch = 40_000u;
        var delay = new RecordingRebootDelay { Hold = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var gateway = FakeDatagramGateway.Start(request => NatPmpReply(request, epoch));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(),
            new FixtureClock(), delay);
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        epoch = 3;
        _ = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var sentBefore = gateway.Received.Count;

        using var cancellation = new CancellationTokenSource();
        var create = provider.CreateAsync(gateway.Binding(), Discovery(), Request(), cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => create);
        Assert.Equal(sentBefore, gateway.Received.Count);

        // The request never went out, so the obligation to delay the next one is not discharged: the
        // retry waits for the same outstanding delay rather than sending immediately.
        var retry = provider.CreateAsync(gateway.Binding(), Discovery(), Request(), CancellationToken.None);
        Assert.False(retry.IsCompleted);
        Assert.Equal(sentBefore, gateway.Received.Count);
        delay.Hold.SetResult();
        _ = await retry;

        Assert.True(gateway.Received.Count > sentBefore);
        // One reboot, one wait, however many attempts it took to get a request out.
        Assert.Single(delay.Waited);
    }

    /// <summary>UPnP has no epoch of its own and must never pretend otherwise.</summary>
    [Fact]
    public async Task Upnp_never_claims_continuity_it_cannot_observe()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.Equal(GatewayContinuity.Unknown, discovery.Continuity);
        Assert.Equal(GatewayContinuity.Unknown, outcome.Continuity);
    }

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp,
        InternalPort = 25565,
        ExternalPort = 25565,
        LeaseSeconds = 3600
    };

    private static RouterDiscoveryResult Discovery() =>
        new() { Mechanism = RouterMappingMechanism.NatPmp, Supported = true, ExternalAddress = "203.0.113.7" };

    private static RouterDiscoveryResult Attempt(
        RouterMappingMechanism mechanism, bool supported, GatewayContinuity continuity) =>
        new() { Mechanism = mechanism, Supported = supported, Continuity = continuity };

    private static byte[] NatPmpReply(byte[] request, uint epoch) =>
        NatPmp.IsAddressRequest(request)
            ? NatPmp.AddressReply(0, "203.0.113.7", epoch)
            : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                NatPmp.SuggestedExternalPort(request), NatPmp.Lifetime(request), epoch);

    /// <summary>Satisfies the RFC 6886 section 3.7 wait instantly, for tests that are not about it.</summary>
    private sealed class InstantRebootDelay : INatPmpRebootDelay
    {
        public TimeSpan NextDelay() => TimeSpan.Zero;

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Records every wait the provider asked for, and can hold one open.</summary>
    private sealed class RecordingRebootDelay : INatPmpRebootDelay
    {
        public TimeSpan Fixed { get; init; } = TimeSpan.FromSeconds(2);
        public List<TimeSpan> Waited { get; } = [];
        public TaskCompletionSource? Hold { get; set; }

        public TimeSpan NextDelay() => Fixed;

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            Waited.Add(duration);
            return Hold?.Task ?? Task.CompletedTask;
        }
    }
}
