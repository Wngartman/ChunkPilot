using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// What <see cref="RouterMappingState.MappingInstanceId"/> means, proven through the production
/// coordinator and the production providers rather than by injecting identities.
/// </summary>
/// <remarks>
/// <para>
/// The identity names one continuous establishment of a router mapping, and every piece of external
/// reachability evidence is bound to it. It therefore has exactly one job: to survive an entry that
/// really is still the same one, and to change the instant an entry has been replaced — including when
/// the replacement is identical in every value a person or a router can observe.
/// </para>
/// <para>
/// The datagram cases run against a real loopback UDP gateway speaking the real RFC 6886 and RFC 6887
/// wire formats, with an epoch the test controls, because the epoch is the only evidence those
/// protocols offer. The UPnP cases run against a readable table, because that is the evidence UPnP
/// offers. Nothing here contacts a real router.
/// </para>
/// </remarks>
public sealed class RouterMappingContinuityIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "chunkpilot-continuity-" + Guid.NewGuid().ToString("N"));
    private ChunkPilotStore store = null!;
    private ServerSupervisor supervisor = null!;
    private readonly FrozenClock clock = new();

    /// <summary>The network this machine is on. Fixed unless a test deliberately moves it.</summary>
    private readonly MovableView view = new();

    /// <summary>
    /// The RFC 6886 section 3.7 wait, satisfied instantly. These tests are about what continuity
    /// evidence does to a mapping's identity; the wait itself is proven separately, and waiting up to
    /// five real seconds inside each of them would prove nothing extra.
    /// </summary>
    private readonly RecordingRebootDelay rebootDelay = new();

    private AppDataPaths paths = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(root, Path.Combine(root, "servers"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), NullLoggerFactory.Instance);
        await supervisor.InitializeAsync();
    }

    // ── PCP ──

    [Fact(Timeout = 60_000)]
    public async Task A_pcp_renewal_on_a_continuous_epoch_keeps_the_mapping_identity()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var maps = gateway.Maps;
        var elapsed = TimeSpan.FromSeconds(1_800);
        clock.Advance(elapsed);
        gateway.Epoch += (uint)elapsed.TotalSeconds;
        var renewed = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        Assert.Equal(RouterMappingPhase.Active, renewed.Phase);
        Assert.True(gateway.Maps > maps, "The renewal must actually have reached the gateway.");
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.Equal(first.MappingInstanceId, renewed.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_pcp_mapping_recreated_after_a_reboot_is_a_new_identity_despite_identical_values()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        // The gateway rebooted: RFC 6887 section 8.5 requires its Epoch Time to restart, and the
        // renewal it is about to receive creates a mapping rather than continuing one.
        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 4;
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        // Identical in every observable value...
        Assert.Equal(first.Mechanism, second.Mechanism);
        Assert.Equal(first.Transport, second.Transport);
        Assert.Equal(first.ExternalPort, second.ExternalPort);
        Assert.Equal(first.InternalPort, second.InternalPort);
        Assert.Equal(first.InternalClient, second.InternalClient);
        Assert.Equal(first.RouterReportedExternalAddress, second.RouterReportedExternalAddress);
        // ...and still not the same establishment.
        Assert.NotEqual("", second.MappingInstanceId);
        Assert.NotEqual(first.MappingInstanceId, second.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_pcp_reboot_that_the_gateway_then_refuses_still_ends_the_old_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 4;
        gateway.MapResultCode = 8; // NO_RESOURCES from a gateway that has just come back up.
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        // Nothing is open any more, so nothing may carry the identity the verified result was bound to.
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    /// <summary>
    /// The whole point of the identity: a probe that was out while the gateway restarted must not
    /// come back and describe the mapping that replaced the one it was made against.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_pcp_reboot_during_a_probe_leaves_the_late_result_bound_to_the_old_mapping()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var probed = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var probedIdentity = ExternalReachabilityPolicy.DescribeMapping(probed);

        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 3;
        var replaced = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var replacedIdentity = ExternalReachabilityPolicy.DescribeMapping(replaced);

        Assert.NotEqual("", probedIdentity);
        Assert.NotEqual("", replacedIdentity);
        Assert.NotEqual(probedIdentity, replacedIdentity);
    }

    /// <summary>
    /// A capability check is read-only, but a gateway that answers it with a restarted epoch has told
    /// ChunkPilot its mapping is gone. Saying it is still open would be a lie.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_pcp_check_that_reveals_a_reboot_stops_claiming_the_old_mapping_is_open()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        gateway.Epoch = 2;
        var checkedState = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        Assert.NotEqual(RouterMappingPhase.Active, checkedState.Phase);
        Assert.Equal("", checkedState.MappingInstanceId);
        // Intent survives, so the next reconciliation re-opens it — as a new mapping.
        Assert.True(checkedState.Enabled);
        var reopened = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Equal(RouterMappingPhase.Active, reopened.Phase);
        Assert.NotEqual(first.MappingInstanceId, reopened.MappingInstanceId);
    }

    // ── NAT-PMP ──

    [Fact(Timeout = 60_000)]
    public async Task A_nat_pmp_renewal_on_a_continuous_counter_keeps_the_mapping_identity()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var maps = gateway.Maps;
        var elapsed = TimeSpan.FromSeconds(1_800);
        clock.Advance(elapsed);
        gateway.Epoch += (uint)elapsed.TotalSeconds;
        var renewed = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.NatPmp, first.Mechanism);
        Assert.Equal(RouterMappingPhase.Active, renewed.Phase);
        Assert.True(gateway.Maps > maps, "The renewal must actually have reached the gateway.");
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.Equal(first.MappingInstanceId, renewed.MappingInstanceId);
    }

    /// <summary>
    /// RFC 6886 section 3.6 grants a 12.5% allowance for a gateway whose clock runs slow. Spending it
    /// on declaring a working router rebooted would make every renewal invalidate the user's evidence.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_nat_pmp_counter_drifting_inside_the_rfc_tolerance_keeps_the_mapping_identity()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var elapsed = TimeSpan.FromSeconds(1_800);
        clock.Advance(elapsed);
        gateway.Epoch += (uint)(elapsed.TotalSeconds * 0.9);
        var renewed = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, renewed.Phase);
        Assert.Equal(first.MappingInstanceId, renewed.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_nat_pmp_mapping_recreated_after_a_reboot_is_a_new_identity_despite_identical_values()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 6; // Rebooted, counting from zero again.
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Equal(first.ExternalPort, second.ExternalPort);
        Assert.Equal(first.InternalPort, second.InternalPort);
        Assert.Equal(first.InternalClient, second.InternalClient);
        Assert.Equal(first.RouterReportedExternalAddress, second.RouterReportedExternalAddress);
        Assert.NotEqual("", second.MappingInstanceId);
        Assert.NotEqual(first.MappingInstanceId, second.MappingInstanceId);
    }

    // ── A reboot the gateway answered with a substitute port ──
    //
    // A restarted gateway is free to hand back a public port other than the one asked for, and
    // ChunkPilot turns that into a conflict because the port it wanted is taken. The conflict is a
    // conclusion about the port; the reboot is a fact about the gateway, and the fact must outlive the
    // conclusion or evidence gathered about the old mapping stays attached to whatever replaced it.

    [Fact(Timeout = 60_000)]
    public async Task A_pcp_reboot_answered_with_a_substitute_port_still_ends_the_old_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 4;
        gateway.SubstituteExternalPort = 51_000;
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        Assert.NotEqual("", first.MappingInstanceId);
        // The operation ends as a conflict, and the reboot it uncovered is still acted on.
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, second.Failure);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(second));
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
        // Intent survives, so reconciliation may open a fresh mapping later.
        Assert.True(second.Enabled);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_nat_pmp_reboot_answered_with_a_substitute_port_still_ends_the_old_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 6;
        gateway.SubstituteExternalPort = 51_000;
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.NatPmp, first.Mechanism);
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, second.Failure);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
        Assert.True(second.Enabled);
    }

    /// <summary>A substitute port on a gateway that did not reboot is a conflict and nothing more.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_substitute_port_without_a_reboot_does_not_invent_a_lost_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        gateway.SubstituteExternalPort = 51_000;
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        var stored = await store.GetRouterMappingAsync(serverId);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, stored!.LastFailure);
        // Nothing proved the old entry gone, so ChunkPilot has not decided that it is: the mapping it
        // already holds is still open, still its own, and still the same establishment.
        Assert.True(stored.HasActiveMapping);
        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Equal(first.MappingInstanceId, second.MappingInstanceId);
    }

    // ── A reboot proved by a discovery that was never selected ──

    [Fact(Timeout = 60_000)]
    public async Task A_refused_pcp_discovery_that_proves_a_reboot_ends_the_owned_pcp_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        // The gateway comes back up and refuses PCP outright, so nothing at all is selected — but its
        // header still carries the epoch that proves what happened.
        gateway.Epoch = 3;
        gateway.DiscoveryResultCode = 2; // NOT_AUTHORIZED
        var second = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
        Assert.True(second.Enabled);
    }

    /// <summary>
    /// The same evidence reaching an establishment attempt rather than a check: the coordinator must
    /// not wait for a successful replacement before admitting the old entry is gone.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_failed_establishment_that_proves_a_reboot_still_ends_the_old_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        gateway.Epoch = 3;
        gateway.DiscoveryResultCode = 2;
        // The cached capability has to go before a fresh discovery is attempted, which is exactly what
        // a failed check does.
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.Epoch = 60_000;
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        await store.UpsertRouterMappingAsync(
            (await store.GetRouterMappingAsync(serverId))! with { HasActiveMapping = true });
        gateway.Epoch = 2;
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_refused_nat_pmp_discovery_that_proves_a_reboot_ends_the_owned_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        gateway.Epoch = 5;
        gateway.DiscoveryResultCode = 3; // Network Failure
        var second = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.NatPmp, first.Mechanism);
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    /// <summary>
    /// The buried case: PCP owns the mapping, PCP is refused and NAT-PMP is selected instead. The
    /// proof of the reboot is in an attempt nobody chose, and it still has to arrive.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_reboot_proved_by_an_attempt_that_lost_to_another_mechanism_still_arrives()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway), NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Equal(RouterMappingMechanism.Pcp, first.Mechanism);

        // PCP restarted and now refuses; NAT-PMP answers and wins the check.
        gateway.PcpEpoch = 3;
        gateway.PcpDiscoveryResultCode = 2;
        var second = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.NatPmp, second.AvailableMechanism);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    // ── Whose reboot it was ──

    [Fact(Timeout = 60_000)]
    public async Task A_pcp_reboot_never_invalidates_a_nat_pmp_mapping()
    {
        await using var gateway = DatagramGateway.Start();
        // NAT-PMP owns the mapping because PCP was unavailable when it was made.
        gateway.PcpEnabled = false;
        await using var coordinator = Coordinator(PcpProvider(gateway), NatPmpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Equal(RouterMappingMechanism.NatPmp, first.Mechanism);

        // PCP comes back, having plainly restarted. NAT-PMP's own counter never moved.
        gateway.PcpEnabled = true;
        gateway.PcpEpoch = 2;
        var second = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Equal(first.MappingInstanceId, second.MappingInstanceId);
        Assert.True((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_nat_pmp_reboot_never_invalidates_a_upnp_mapping()
    {
        await using var gateway = DatagramGateway.Start();
        var upnp = new TableGateway();
        gateway.NatPmpEnabled = false;
        await using var coordinator = Coordinator(NatPmpProvider(gateway), upnp);
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Equal(RouterMappingMechanism.UpnpIgd, first.Mechanism);

        gateway.NatPmpEnabled = true;
        gateway.NatPmpEpoch = 4;
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var second = await coordinator.GetStateAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Equal(first.MappingInstanceId, second.MappingInstanceId);
    }

    /// <summary>
    /// A different router's epoch is not a statement about the mapping left on this one, and neither is
    /// anything else that router says. The mapping keeps its owner, its mechanism and its record; what
    /// it loses is the claim to be open, because ChunkPilot is no longer anywhere it could confirm that.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_reboot_reported_by_another_gateway_never_invalidates_this_one()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        // The mapping belongs to a router this check is not talking to.
        var owned = new RouterBindingIdentity { InterfaceId = "eth", GatewayAddress = "192.168.7.1" };
        await store.UpsertRouterMappingAsync(
            (await store.GetRouterMappingAsync(serverId))! with { OwnedBinding = owned });
        gateway.Epoch = 2;
        var second = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.NotEqual("", first.MappingInstanceId);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.True(stored.HasActiveMapping);
        Assert.Equal(owned, stored.OwnedBinding);
        Assert.Equal(RouterMappingMechanism.Pcp, stored.Mechanism);
        // The check's own gateway is written down as exactly that, and the mapping is not relabelled as
        // belonging to it — nor presented as open, which nothing here can confirm.
        Assert.Equal("192.168.1.1", stored.DiscoveredBinding.GatewayAddress);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
    }

    /// <summary>An ordinary failure without reset evidence must not destroy proven ownership.</summary>
    [Fact(Timeout = 60_000)]
    public async Task An_ordinary_failed_check_leaves_a_proven_mapping_alone()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        // The gateway refuses, and its epoch says nothing has been lost.
        gateway.DiscoveryResultCode = 2;
        var second = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Equal(first.MappingInstanceId, second.MappingInstanceId);
        Assert.True((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    // ── RFC 6886 section 3.7, through the production path ──

    [Fact(Timeout = 60_000)]
    public async Task A_nat_pmp_recreation_after_a_reboot_waits_before_it_asks_again()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Empty(rebootDelay.Waited);

        // The reboot is proved by a check, and the establishment that follows it waits.
        gateway.Epoch = 4;
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var reopened = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, reopened.Phase);
        Assert.Single(rebootDelay.Waited);
        Assert.InRange(rebootDelay.Waited[0], TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    // ── The whole point: what a reset does to a verified public endpoint ──
    //
    // These run the real server process, the real providers, the real router coordinator and the real
    // external reachability coordinator, so what is proven is the product's own answer to "can somebody
    // outside reach this?" rather than a rule in isolation.

    [Fact(Timeout = 120_000)]
    public async Task A_pcp_reboot_answered_with_a_substitute_port_ends_a_verified_public_endpoint()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var (serverId, server) = await RunningServerAsync(routers, external, probe);

        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 4;
        gateway.SubstituteExternalPort = 51_000;
        _ = await routers.EnableAsync(serverId, true, CancellationToken.None);
        var state = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.Equal("", state.VerifiedEndpoint);
        // Nothing re-ran on its own to fix it: only another deliberate check can.
        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_nat_pmp_reboot_answered_with_a_substitute_port_ends_a_verified_public_endpoint()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(NatPmpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var (serverId, server) = await RunningServerAsync(routers, external, probe);

        clock.Advance(TimeSpan.FromSeconds(1_800));
        gateway.Epoch = 6;
        gateway.SubstituteExternalPort = 51_000;
        _ = await routers.EnableAsync(serverId, true, CancellationToken.None);
        var state = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_refused_pcp_discovery_that_proves_a_reboot_ends_a_verified_public_endpoint()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var (serverId, server) = await RunningServerAsync(routers, external, probe);

        gateway.Epoch = 3;
        gateway.DiscoveryResultCode = 2;
        _ = await routers.CheckAsync(serverId, CancellationToken.None);
        var state = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        await server.StopAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_refused_nat_pmp_discovery_that_proves_a_reboot_ends_a_verified_public_endpoint()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(NatPmpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var (serverId, server) = await RunningServerAsync(routers, external, probe);

        gateway.Epoch = 5;
        gateway.DiscoveryResultCode = 3;
        _ = await routers.CheckAsync(serverId, CancellationToken.None);
        var state = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        await server.StopAsync();
    }

    /// <summary>A probe that was already out when the gateway restarted must not come back verified.</summary>
    [Fact(Timeout = 120_000)]
    public async Task A_probe_answering_after_a_reboot_is_discarded_rather_than_published()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        Assert.Equal(RouterMappingPhase.Active,
            (await routers.EnableAsync(serverId, true, CancellationToken.None)).Phase);

        probe.Outcome = ExternalProbeOutcome.Reachable;
        probe.BeforeReturning = async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(1_800));
            gateway.Epoch = 2;
            _ = await routers.EnableAsync(serverId, true, CancellationToken.None);
        };

        var state = await external.CheckAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Null(state.CheckedAt);
        await server.StopAsync();
    }

    /// <summary>
    /// The same, through the path the substitute-port correction actually runs on: the reset arrives on
    /// a mapping response the gateway answered with a different public port, which the operation then
    /// classifies as a conflict rather than as a reset.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_probe_answering_while_a_substitute_port_reveals_a_reboot_is_discarded()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        Assert.Equal(RouterMappingPhase.Active,
            (await routers.EnableAsync(serverId, true, CancellationToken.None)).Phase);

        probe.Outcome = ExternalProbeOutcome.Reachable;
        probe.BeforeReturning = async () =>
        {
            clock.Advance(TimeSpan.FromSeconds(1_800));
            gateway.Epoch = 4;
            gateway.SubstituteExternalPort = 51_000;
            var conflicted = await routers.EnableAsync(serverId, true, CancellationToken.None);
            Assert.Equal(RouterMappingFailure.ForeignMappingPresent, conflicted.Failure);
            Assert.Equal("", conflicted.MappingInstanceId);
        };

        var state = await external.CheckAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Null(state.CheckedAt);
        Assert.Equal("", state.VerifiedEndpoint);
        await server.StopAsync();
    }

    /// <summary>And through the other one: the reset proved by a discovery that was refused outright.</summary>
    [Fact(Timeout = 120_000)]
    public async Task A_probe_answering_while_a_refused_discovery_reveals_a_reboot_is_discarded()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        Assert.Equal(RouterMappingPhase.Active,
            (await routers.EnableAsync(serverId, true, CancellationToken.None)).Phase);

        probe.Outcome = ExternalProbeOutcome.Reachable;
        probe.BeforeReturning = async () =>
        {
            gateway.Epoch = 3;
            gateway.DiscoveryResultCode = 2; // NOT_AUTHORIZED from a gateway that has just come back up.
            var refused = await routers.CheckAsync(serverId, CancellationToken.None);
            Assert.NotEqual(RouterMappingPhase.Active, refused.Phase);
        };

        var state = await external.CheckAsync(serverId, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Null(state.CheckedAt);
        await server.StopAsync();
    }

    // ── Which network a mapping belongs to ──
    //
    // A mapping is made on one router, reached over one adapter, and it stays there. A later look at the
    // network can find something else — another adapter, another building, the same address on a
    // different router — and none of that moves the entry that already exists. What it may never do is
    // relabel that entry as the new network's, because every decision downstream then names the wrong
    // router: where a deletion is sent, which public address is shown, which endpoint a verification
    // describes.

    [Fact(Timeout = 60_000)]
    public async Task A_check_on_another_network_never_rebinds_the_mapping_it_finds_recorded()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var owned = (await store.GetRouterMappingAsync(serverId))!.OwnedBinding;

        view.MoveToAnotherNetwork();
        var moved = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal("eth", owned.InterfaceId);
        Assert.Equal("192.168.1.1", owned.GatewayAddress);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        // The owned binding is exactly what it was, and what the check found is written down as what it
        // is: a discovery, never an ownership.
        Assert.Equal(owned, stored.OwnedBinding);
        Assert.Equal("wifi", stored.DiscoveredBinding.InterfaceId);
        Assert.Equal("192.168.2.1", stored.DiscoveredBinding.GatewayAddress);
        Assert.True(stored.HasActiveMapping);
        // Nothing here can confirm that mapping, so nothing presents it as open, and the identity every
        // external result is bound to is not handed to the network that happened to be found.
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual(RouterMappingPhase.Active, moved.Phase);
        Assert.True(moved.OwnedNetworkLeft);
        Assert.Equal("", moved.MappingInstanceId);
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(moved));
        // The gateway on screen is still the one the mapping is on, not the one just discovered.
        Assert.Equal("192.168.1.1", moved.GatewayAddress);
    }

    /// <summary>
    /// 192.168.1.1 is the most reused address on any home network there is. Reaching it over another
    /// adapter is not evidence of having reached the same router.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task The_same_gateway_address_over_another_adapter_is_a_different_network()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        view.MoveToWifiOnTheSameAddresses();
        var moved = await coordinator.CheckAsync(serverId, CancellationToken.None);

        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.Equal("eth", stored.OwnedBinding.InterfaceId);
        Assert.Equal("192.168.1.1", stored.OwnedBinding.GatewayAddress);
        Assert.Equal("wifi", stored.DiscoveredBinding.InterfaceId);
        Assert.Equal("192.168.1.1", stored.DiscoveredBinding.GatewayAddress);
        Assert.True(stored.HasActiveMapping);
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual(RouterMappingPhase.Active, moved.Phase);
        Assert.Equal("", moved.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task The_same_adapter_onto_another_gateway_is_a_different_network()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        view.MoveToAnotherGatewayOnTheSameAdapter();
        var moved = await coordinator.CheckAsync(serverId, CancellationToken.None);

        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.Equal("192.168.1.1", stored.OwnedBinding.GatewayAddress);
        Assert.Equal("192.168.3.1", stored.DiscoveredBinding.GatewayAddress);
        Assert.True(stored.HasActiveMapping);
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual(RouterMappingPhase.Active, moved.Phase);
        Assert.Equal("", moved.MappingInstanceId);
    }

    /// <summary>The ordinary case, which must stay completely ordinary.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Repeated_checks_on_the_owned_network_keep_the_mapping_and_its_identity()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var identity = ExternalReachabilityPolicy.DescribeMapping(first);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            gateway.Epoch += 60;
            var again = await coordinator.CheckAsync(serverId, CancellationToken.None);
            Assert.Equal(RouterMappingPhase.Active, again.Phase);
            Assert.False(again.OwnedNetworkLeft);
            Assert.Equal(first.MappingInstanceId, again.MappingInstanceId);
            Assert.Equal(identity, ExternalReachabilityPolicy.DescribeMapping(again));
        }

        Assert.NotEqual("", identity);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.Equal(stored.OwnedBinding, stored.DiscoveredBinding);
    }

    [Fact(Timeout = 120_000)]
    public async Task Moving_to_another_network_ends_the_verification_and_then_maps_the_new_one()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var (serverId, server) = await RunningServerAsync(routers, external, probe);
        var original = await routers.GetStateAsync(serverId, CancellationToken.None);

        view.MoveToAnotherNetwork();
        _ = await routers.CheckAsync(serverId, CancellationToken.None);
        var afterMove = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.False(afterMove.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Stale, afterMove.Phase);

        // Ordinary reconciliation then maps the network this computer is actually on — as a new mapping,
        // not as the old one renamed.
        var reopened = await routers.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, reopened.Phase);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.Equal("wifi", stored.OwnedBinding.InterfaceId);
        Assert.Equal("192.168.2.1", stored.OwnedBinding.GatewayAddress);
        Assert.NotEqual("", original.MappingInstanceId);
        Assert.NotEqual(original.MappingInstanceId, reopened.MappingInstanceId);
        Assert.False((await external.GetStateAsync(serverId, CancellationToken.None)).IsVerified);
        // Nothing re-ran on its own to fix any of it.
        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>
    /// A→B→A. Every value the mapping ever had is identical again, and the evidence gathered before the
    /// move still cannot be presented as current.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Returning_to_the_original_network_never_resurrects_the_old_verification()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(PcpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var (serverId, server) = await RunningServerAsync(routers, external, probe);
        var original = await routers.GetStateAsync(serverId, CancellationToken.None);

        view.MoveToAnotherNetwork();
        _ = await routers.CheckAsync(serverId, CancellationToken.None);
        Assert.Equal(ExternalReachabilityPhase.Stale,
            (await external.GetStateAsync(serverId, CancellationToken.None)).Phase);

        view.MoveBackToEthernet();
        var returned = await routers.CheckAsync(serverId, CancellationToken.None);
        var state = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, returned.Phase);
        Assert.Equal(original.Mechanism, returned.Mechanism);
        Assert.Equal(original.ExternalPort, returned.ExternalPort);
        Assert.Equal(original.InternalPort, returned.InternalPort);
        Assert.Equal(original.InternalClient, returned.InternalClient);
        Assert.Equal(original.GatewayAddress, returned.GatewayAddress);
        Assert.NotEqual(original.MappingInstanceId, returned.MappingInstanceId);
        Assert.False(state.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_removal_is_never_sent_to_a_router_that_does_not_hold_the_mapping()
    {
        var upnp = new TableGateway();
        await using var coordinator = Coordinator(upnp);
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var port = first.ExternalPort;

        view.MoveToAnotherNetwork();
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var disabled = await coordinator.DisableAsync(serverId, CancellationToken.None);

        // Nothing was sent anywhere, and the entry ChunkPilot made is exactly where it was left.
        Assert.Equal(0, upnp.Removes);
        Assert.True(upnp.Table.ContainsKey($"Tcp:{port}"));
        // The exposure is remembered rather than quietly dropped, so it is retried when this computer is
        // back on the network that holds it.
        Assert.True(disabled.RemovalPending);
        Assert.Equal(RouterMappingPhase.NeedsAttention, disabled.Phase);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.True(stored.HasActiveMapping);
        Assert.Equal("eth", stored.OwnedBinding.InterfaceId);
        Assert.Equal("192.168.1.1", stored.OwnedBinding.GatewayAddress);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_datagram_removal_is_never_sent_to_a_router_that_does_not_hold_the_mapping()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var sent = gateway.Maps;

        view.MoveToAnotherNetwork();
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var disabled = await coordinator.DisableAsync(serverId, CancellationToken.None);

        // A NAT-PMP delete is a mapping request like any other, and not one was sent.
        Assert.Equal(sent, gateway.Maps);
        Assert.True(disabled.RemovalPending);
        Assert.True((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    /// <summary>Back on the owning network, the removal that was held is sent for real.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_removal_held_back_by_a_network_change_is_completed_on_returning()
    {
        var upnp = new TableGateway();
        await using var coordinator = Coordinator(upnp);
        var serverId = await AddServerAsync();
        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var port = first.ExternalPort;

        view.MoveToAnotherNetwork();
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        Assert.True((await coordinator.DisableAsync(serverId, CancellationToken.None)).RemovalPending);

        view.MoveBackToEthernet();
        var settled = await coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(1, upnp.Removes);
        Assert.False(upnp.Table.ContainsKey($"Tcp:{port}"));
        Assert.False(settled.RemovalPending);
        Assert.NotEqual(RouterMappingPhase.Active, settled.Phase);
    }

    // ── Rows written before ownership recorded a network ──
    //
    // A build that recorded only a gateway address left rows that claim an open mapping without saying
    // where it is. An address is not a network — the same text over another adapter, or in another
    // building, is another router — so such a row is evidence that an exposure exists somewhere and is
    // never authority to act on one here. The point of every case below is that nothing at all is sent
    // to whatever router this computer happens to reach now.

    [Fact(Timeout = 60_000)]
    public async Task A_row_from_before_ownership_binding_loads_without_a_network_it_can_prove()
    {
        var serverId = await AddServerAsync();
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.NatPmp, "10.9.9.1", 25_600);

        var stored = (await store.GetRouterMappingAsync(serverId))!;

        // The old gateway text survives, because it is the only thing that can tell the owner which
        // router still holds the port — and it survives as what it is: an identity with no interface,
        // which is never fully known and therefore matches nothing.
        Assert.True(stored.HasActiveMapping);
        Assert.Equal("10.9.9.1", stored.OwnedBinding.GatewayAddress);
        Assert.Equal("", stored.OwnedBinding.InterfaceId);
        Assert.False(stored.OwnedBinding.IsKnown);
        Assert.Equal(RouterMappingMechanism.NatPmp, stored.Mechanism);
        Assert.Equal(25_600, stored.ExternalPort);
        Assert.True(RouterMappingPolicy.HasUnknownOwnedNetwork(stored));

        // And it is not written back: once read, the row is in the current shape.
        await store.UpsertRouterMappingAsync(stored);
        Assert.Equal("", (await store.GetRouterMappingAsync(serverId))!.LegacyGatewayAddress);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_legacy_nat_pmp_row_never_sends_a_deletion_to_the_router_reachable_now()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.NatPmp, "10.9.9.1", 25_600);

        var disabled = await coordinator.DisableAsync(serverId, CancellationToken.None);

        // NAT-PMP has no nonce and cannot read a table: a deletion is scoped by this computer's own
        // internal port, so sending one to a router that never held the mapping could only ever remove
        // something else. Not one datagram went out.
        Assert.Equal(0, gateway.Maps);
        Assert.True(disabled.RemovalPending);
        Assert.True(disabled.OwnedNetworkUnknown);
        Assert.Equal(RouterMappingPhase.NeedsAttention, disabled.Phase);
        Assert.True((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_legacy_upnp_row_never_deletes_a_mapping_that_merely_looks_like_it()
    {
        var upnp = new TableGateway();
        await using var coordinator = Coordinator(upnp);
        var serverId = await AddServerAsync();
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.UpnpIgd, "10.9.9.1", 25_600);
        // The router reachable now happens to hold an entry matching the old row in every value there
        // is: same public port, same internal endpoint, same description ChunkPilot always writes.
        upnp.Table["Tcp:25600"] = new ExistingRouterMapping
        {
            ExternalPort = 25_600,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.50",
            InternalPort = 25_600,
            Description = RouterMappingPolicy.MappingDescription,
            LeaseSeconds = 3600
        };

        var disabled = await coordinator.DisableAsync(serverId, CancellationToken.None);

        // Coincidence is not ownership. ChunkPilot's description is a constant every install writes,
        // so an identical entry proves only that some ChunkPilot made it, never that this row did.
        Assert.Equal(0, upnp.Removes);
        Assert.True(upnp.Table.ContainsKey("Tcp:25600"));
        Assert.True(disabled.RemovalPending);
        Assert.True(disabled.OwnedNetworkUnknown);
    }

    /// <summary>
    /// PCP's nonce is strong ownership — a delete carrying it cannot remove another application's
    /// mapping — but strength is not evidence. The mechanism cannot read a table, so nothing can show
    /// that the router reachable now is the one holding the mapping, and a reply cannot tell a deletion
    /// apart from a no-op. It fails closed with the others rather than claiming a closure it cannot
    /// support.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_legacy_pcp_row_fails_closed_despite_holding_its_mapping_nonce()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway));
        var serverId = await AddServerAsync();
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.Pcp, "10.9.9.1", 25_600,
            ownershipToken: "0102030405060708090A0B0C");

        var disabled = await coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.Equal(0, gateway.Maps);
        Assert.True(disabled.RemovalPending);
        Assert.True(disabled.OwnedNetworkUnknown);
    }

    /// <summary>
    /// The trap: the gateway text in the old row is the address this computer reaches now. It still
    /// proves nothing — 192.168.1.1 names a different router on every network that uses it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_legacy_row_naming_the_same_gateway_address_is_still_not_owned_here()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.NatPmp, "192.168.1.1", 25_600);

        var checkedState = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var disabled = await coordinator.DisableAsync(serverId, CancellationToken.None);

        // A check reaches the same address and does not adopt the row: the interface it was made
        // through was never recorded, so the two still cannot be shown to be the same router.
        Assert.Equal(RouterMappingPhase.NeedsAttention, checkedState.Phase);
        Assert.True(checkedState.OwnedNetworkUnknown);
        Assert.Equal("", checkedState.MappingInstanceId);
        var afterCheck = await store.GetRouterMappingAsync(serverId);
        Assert.Equal("", afterCheck!.OwnedBinding.InterfaceId);
        Assert.Equal(0, gateway.Maps);
        Assert.True(disabled.RemovalPending);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_legacy_row_is_never_presented_as_an_open_port_or_a_verified_one()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(NatPmpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.NatPmp, "10.9.9.1",
            server.Definition.Port);

        var router = await routers.GetStateAsync(serverId, CancellationToken.None);
        var reachability = await external.GetStateAsync(serverId, CancellationToken.None);

        Assert.NotEqual(RouterMappingPhase.Active, router.Phase);
        Assert.Equal("", router.MappingInstanceId);
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(router));
        // Nothing may be checked from outside against a mapping ChunkPilot cannot identify, and nothing
        // reads as verified.
        Assert.Equal(ExternalReachabilityBlocker.RouterMappingInactive, reachability.Blocker);
        Assert.False(reachability.CanCheck);
        Assert.False(reachability.IsVerified);
        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_legacy_row_is_replaced_by_a_fresh_mapping_on_the_network_this_computer_is_on()
    {
        await using var gateway = DatagramGateway.Start();
        await using var routers = Coordinator(NatPmpProvider(gateway));
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.NatPmp, "10.9.9.1",
            server.Definition.Port);

        // Ordinary reconciliation, with no removal sent anywhere.
        var reopened = await routers.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, reopened.Phase);
        Assert.False(reopened.OwnedNetworkUnknown);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.Equal("eth", stored.OwnedBinding.InterfaceId);
        Assert.Equal("192.168.1.1", stored.OwnedBinding.GatewayAddress);
        Assert.True(stored.OwnedBinding.IsKnown);
        Assert.NotEqual("", reopened.MappingInstanceId);
        // A fresh establishment, so nothing about the old one carries over and an outside check has to
        // be asked for again.
        Assert.False((await external.GetStateAsync(serverId, CancellationToken.None)).IsVerified);
        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>
    /// The case that matters most, because it is the one where ChunkPilot is about to act: a running
    /// server, reconciliation under way, and a router in front of it holding an entry that matches the
    /// old row in every value there is. ChunkPilot's description is a constant every install writes and
    /// the internal endpoint is this computer's own, so matching proves only that some ChunkPilot made
    /// it — never that this row did.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task A_running_legacy_upnp_row_never_adopts_a_coincident_mapping_on_the_router_reached_now()
    {
        var upnp = new TableGateway();
        await using var routers = Coordinator(upnp);
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        var port = server.Definition.Port;
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.UpnpIgd, "10.9.9.1", port);
        upnp.Table[$"Tcp:{port}"] = new ExistingRouterMapping
        {
            ExternalPort = port,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.50",
            InternalPort = port,
            Description = RouterMappingPolicy.MappingDescription,
            LeaseSeconds = 3600
        };

        var state = await routers.SynchronizeAsync(serverId, CancellationToken.None);

        // Not adopted, not renewed, not deleted, not touched.
        Assert.Equal(0, upnp.Creates);
        Assert.Equal(0, upnp.Removes);
        Assert.True(upnp.Table.ContainsKey($"Tcp:{port}"));
        Assert.Equal(RouterMappingPhase.Conflict, state.Phase);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, state.Failure);
        Assert.Equal("", state.MappingInstanceId);
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(state));
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.False(stored.HasActiveMapping);
        Assert.False(stored.OwnedBinding.IsKnown);
        Assert.False((await external.GetStateAsync(serverId, CancellationToken.None)).IsVerified);
        // And the exposure the old row described is remembered rather than dropped along with the claim.
        Assert.NotNull(stored.LegacyExposure);
        Assert.Equal("10.9.9.1", stored.LegacyExposure!.GatewayAddress);
        Assert.Equal(port, stored.LegacyExposure.ExternalPort);
        Assert.NotNull(state.LegacyExposure);
        await server.StopAsync();
    }

    /// <summary>The same trap with the old row naming the address this computer reaches now.</summary>
    [Fact(Timeout = 120_000)]
    public async Task A_running_legacy_row_naming_the_current_gateway_address_still_adopts_nothing()
    {
        var upnp = new TableGateway();
        await using var routers = Coordinator(upnp);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        var port = server.Definition.Port;
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.UpnpIgd, "192.168.1.1", port);
        upnp.Table[$"Tcp:{port}"] = new ExistingRouterMapping
        {
            ExternalPort = port,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.50",
            InternalPort = port,
            Description = RouterMappingPolicy.MappingDescription,
            LeaseSeconds = 3600
        };

        var state = await routers.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(0, upnp.Creates);
        Assert.Equal(0, upnp.Removes);
        Assert.Equal(RouterMappingPhase.Conflict, state.Phase);
        Assert.Equal("", state.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.OwnedBinding.IsKnown);
        await server.StopAsync();
    }

    [Fact(Timeout = 120_000)]
    public async Task A_running_legacy_upnp_row_on_an_empty_router_becomes_a_fresh_owned_mapping()
    {
        var upnp = new TableGateway();
        await using var routers = Coordinator(upnp);
        var probe = new RecordingProbe();
        await using var external = External(routers, probe);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.UpnpIgd, "10.9.9.1",
            server.Definition.Port);

        var state = await routers.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Equal(1, upnp.Creates);
        Assert.Equal(0, upnp.Removes);
        var stored = (await store.GetRouterMappingAsync(serverId))!;
        Assert.True(stored.OwnedBinding.IsKnown);
        Assert.Equal("eth", stored.OwnedBinding.InterfaceId);
        Assert.Equal("192.168.1.1", stored.OwnedBinding.GatewayAddress);
        Assert.NotEqual("", state.MappingInstanceId);
        // A new mapping here says nothing about the old one somewhere else, so the exposure survives
        // the success rather than being hidden by it — and nothing is verified until somebody asks.
        Assert.NotNull(state.LegacyExposure);
        Assert.Equal("10.9.9.1", state.LegacyExposure!.GatewayAddress);
        Assert.False((await external.GetStateAsync(serverId, CancellationToken.None)).IsVerified);
        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>A finite lease is the point past which the old entry cannot still be there.</summary>
    [Fact(Timeout = 120_000)]
    public async Task A_legacy_exposure_stops_being_reported_once_the_lease_it_recorded_has_run_out()
    {
        var upnp = new TableGateway();
        await using var routers = Coordinator(upnp);
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.UpnpIgd, "10.9.9.1",
            server.Definition.Port);
        Assert.NotNull((await routers.SynchronizeAsync(serverId, CancellationToken.None)).LegacyExposure);

        clock.Advance(TimeSpan.FromHours(2));

        Assert.Null((await routers.GetStateAsync(serverId, CancellationToken.None)).LegacyExposure);
        await server.StopAsync();
    }

    /// <summary>The lifecycle path a stopped server takes must obey exactly the same rule.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Reconciling_a_stopped_server_holding_a_legacy_row_sends_nothing()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(NatPmpProvider(gateway));
        var serverId = await AddServerAsync();
        await SeedLegacyRowAsync(serverId, RouterMappingMechanism.NatPmp, "10.9.9.1", 25_600);

        var settled = await coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(0, gateway.Maps);
        Assert.True(settled.RemovalPending);
        Assert.Equal(RouterMappingPhase.NeedsAttention, settled.Phase);
        Assert.True((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    // ── Mechanism changes ──

    [Fact(Timeout = 60_000)]
    public async Task Falling_back_from_pcp_to_nat_pmp_is_a_different_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        await using var coordinator = Coordinator(PcpProvider(gateway), NatPmpProvider(gateway));
        var serverId = await AddServerAsync();

        var pcp = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        // The gateway stops answering PCP entirely, so the next attempt falls through to NAT-PMP.
        gateway.PcpEnabled = false;
        var natPmp = await coordinator.CheckAsync(serverId, CancellationToken.None);
        Assert.Equal(RouterMappingMechanism.NatPmp, natPmp.AvailableMechanism);
        var reopened = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.Pcp, pcp.Mechanism);
        Assert.Equal(RouterMappingMechanism.NatPmp, reopened.Mechanism);
        Assert.Equal(pcp.ExternalPort, reopened.ExternalPort);
        Assert.NotEqual(pcp.MappingInstanceId, reopened.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task Falling_back_from_nat_pmp_to_upnp_is_a_different_establishment()
    {
        await using var gateway = DatagramGateway.Start();
        var upnp = new TableGateway();
        await using var coordinator = Coordinator(NatPmpProvider(gateway), upnp);
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        gateway.NatPmpEnabled = false;
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.NatPmp, first.Mechanism);
        Assert.Equal(RouterMappingMechanism.UpnpIgd, second.Mechanism);
        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.NotEqual(first.MappingInstanceId, second.MappingInstanceId);
    }

    // ── UPnP, where the router's own table is the evidence ──

    [Fact(Timeout = 60_000)]
    public async Task Reading_the_same_live_upnp_mapping_keeps_one_identity()
    {
        var gateway = new TableGateway();
        await using var coordinator = Coordinator(gateway);
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        for (var attempt = 0; attempt < 10; attempt++)
            Assert.Equal(first.MappingInstanceId,
                (await coordinator.GetStateAsync(serverId, CancellationToken.None)).MappingInstanceId);
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.Equal(1, gateway.Creates);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_upnp_lease_renewal_on_the_live_entry_keeps_the_identity()
    {
        var gateway = new TableGateway();
        await using var coordinator = Coordinator(gateway);
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var creates = gateway.Creates;
        clock.Advance(TimeSpan.FromSeconds(1_800));
        var renewed = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.True(gateway.Creates > creates);
        Assert.Equal(RouterMappingPhase.Active, renewed.Phase);
        Assert.Equal(first.MappingInstanceId, renewed.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task An_identical_upnp_mapping_recreated_after_removal_is_a_new_identity()
    {
        var gateway = new TableGateway();
        await using var coordinator = Coordinator(gateway);
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        await coordinator.DisableAsync(serverId, CancellationToken.None);
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(first.ExternalPort, second.ExternalPort);
        Assert.Equal(first.InternalClient, second.InternalClient);
        Assert.NotEqual(first.MappingInstanceId, second.MappingInstanceId);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_upnp_mapping_the_router_no_longer_lists_loses_its_identity_before_anything_is_recreated()
    {
        var gateway = new TableGateway();
        await using var coordinator = Coordinator(gateway);
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        gateway.Table.Clear();
        gateway.CreateErrorCode = 501; // The router forgot the entry and now refuses to make another.
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        Assert.NotEqual(RouterMappingPhase.Active, second.Phase);
        Assert.Equal("", second.MappingInstanceId);
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    /// <summary>
    /// The foreign-replacement case: something else now holds the public port, on the very same
    /// external and internal port numbers ChunkPilot was using.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_foreign_mapping_that_replaced_chunkpilots_never_inherits_its_identity()
    {
        var gateway = new TableGateway();
        await using var coordinator = Coordinator(gateway);
        var serverId = await AddServerAsync();

        var first = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var port = first.ExternalPort;
        gateway.Table[$"Tcp:{port}"] = new ExistingRouterMapping
        {
            ExternalPort = port,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.99",
            InternalPort = port,
            Description = "Living room console",
            LeaseSeconds = 3600
        };
        clock.Advance(TimeSpan.FromSeconds(1_800));
        var second = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, first.Phase);
        Assert.NotEqual("", first.MappingInstanceId);
        // ChunkPilot no longer owns anything here, so nothing may present as open or verified.
        Assert.Equal(RouterMappingPhase.Conflict, second.Phase);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, second.Failure);
        Assert.Equal("", second.MappingInstanceId);
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(second));
        Assert.False((await store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
        // And the foreign entry was left exactly as it was found.
        Assert.Equal(0, gateway.Removes);
        Assert.Equal("Living room console", gateway.Table[$"Tcp:{port}"].Description);
    }

    /// <summary>A→B→A on the router's table: the original identity must never come back.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_foreign_replacement_that_is_later_undone_never_resurrects_the_original_identity()
    {
        var gateway = new TableGateway();
        await using var coordinator = Coordinator(gateway);
        var serverId = await AddServerAsync();

        var original = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var port = original.ExternalPort;
        gateway.Table[$"Tcp:{port}"] = new ExistingRouterMapping
        {
            ExternalPort = port,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.99",
            InternalPort = port,
            Description = "Living room console",
            LeaseSeconds = 3600
        };
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        // The foreign entry goes away and ChunkPilot's is established again on identical terms.
        gateway.Table.Clear();
        var restored = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, restored.Phase);
        Assert.Equal(original.ExternalPort, restored.ExternalPort);
        Assert.Equal(original.InternalClient, restored.InternalClient);
        Assert.Equal(original.RouterReportedExternalAddress, restored.RouterReportedExternalAddress);
        Assert.NotEqual(original.MappingInstanceId, restored.MappingInstanceId);
    }

    // ── Harness ──

    private RouterMappingCoordinator Coordinator(params IRouterMappingProvider[] providers)
    {
        var service = new RouterMappingService(view, providers, new RouterMappingOptions(),
            NullLogger<RouterMappingService>.Instance);
        return new RouterMappingCoordinator(store, supervisor, service,
            NullLogger<RouterMappingCoordinator>.Instance, clock);
    }

    private ExternalReachabilityCoordinator External(
        RouterMappingCoordinator routers, IExternalReachabilityProbe probe) =>
        new(supervisor, routers, probe, NullLogger<ExternalReachabilityCoordinator>.Instance, clock);

    /// <summary>
    /// A genuinely running server with an established mapping and one verified external result, which
    /// is the state every reset in this block has to be able to end.
    /// </summary>
    private async Task<(Guid ServerId, ManagedServer Server)> RunningServerAsync(
        RouterMappingCoordinator routers, ExternalReachabilityCoordinator external, RecordingProbe probe)
    {
        var serverId = await AddServerAsync(runnable: true);
        var server = supervisor.Get(serverId);
        Assert.True((await server.StartAsync()).Success);
        Assert.Equal(RouterMappingPhase.Active,
            (await routers.EnableAsync(serverId, true, CancellationToken.None)).Phase);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await external.CheckAsync(serverId, CancellationToken.None)).IsVerified);
        return (serverId, server);
    }

    /// <summary>
    /// Writes the row a build before ownership binding would have written: the JSON that build produced,
    /// straight into the table, so what is loaded is genuinely an older row rather than the current type
    /// with some fields left out.
    /// </summary>
    private async Task SeedLegacyRowAsync(
        Guid serverId,
        RouterMappingMechanism mechanism,
        string gatewayAddress,
        int port,
        string ownershipToken = "")
    {
        var json = $$"""
            {
              "serverId": "{{serverId:D}}",
              "directInternetEnabled": true,
              "consentGranted": true,
              "consentGrantedAt": "2026-08-01T12:00:00+00:00",
              "mechanism": "{{mechanism}}",
              "availableMechanism": "{{mechanism}}",
              "candidateInternalClient": "192.168.1.50",
              "transport": "Tcp",
              "externalPort": {{port}},
              "internalPort": {{port}},
              "internalClient": "192.168.1.50",
              "gatewayAddress": "{{gatewayAddress}}",
              "description": "{{RouterMappingPolicy.MappingDescription}}",
              "ownershipToken": "{{ownershipToken}}",
              "controlUrl": "http://127.0.0.1/ctl",
              "serviceType": "urn:schemas-upnp-org:service:WANIPConnection:1",
              "hasActiveMapping": true,
              "leaseIsFinite": true,
              "leaseSeconds": 3600,
              "leaseExpiresAt": "2026-08-09T12:30:00+00:00",
              "establishedAt": "2026-08-09T11:30:00+00:00",
              "routerReportedExternalAddress": "93.184.216.34",
              "routerReportedAddressClass": "GloballyRoutable",
              "removalPending": false,
              "lastFailure": "None",
              "lastOperationDetail": "Written by an earlier version.",
              "lastCheckedAt": "2026-08-01T12:00:00+00:00"
            }
            """;
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT OR REPLACE INTO router_mappings(server_id, json, updated_utc) VALUES($id, $json, $now)";
        command.Parameters.AddWithValue("$id", serverId.ToString("D"));
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private PcpMappingProvider PcpProvider(DatagramGateway gateway) =>
        new(new LoopbackDatagramChannel(gateway.Port), gateway.Options(), clock);

    private NatPmpMappingProvider NatPmpProvider(DatagramGateway gateway) =>
        new(new LoopbackDatagramChannel(gateway.Port), gateway.Options(), clock, rebootDelay);

    private async Task<Guid> AddServerAsync(bool runnable = false)
    {
        var id = Guid.NewGuid();
        var rootPath = Path.Combine(root, "servers", id.ToString("N"));
        Directory.CreateDirectory(rootPath);
        var definition = new ServerDefinition
        {
            Id = id,
            Name = "Continuity fixture",
            RootPath = rootPath,
            WorkingDirectory = runnable ? rootPath : "",
            Executable = runnable ? DotnetPath() : "",
            Arguments = runnable
                ? $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal"
                : "",
            ReadinessPattern = runnable ? @"Done \(.+?\)!|For help, type" : "",
            StartupTimeoutSeconds = 20,
            ShutdownTimeoutSeconds = 10,
            SaveTimeoutSeconds = 10,
            RestartDelaySeconds = 0,
            Port = TestPortAllocator.Reserve()
        };
        if (runnable)
            definition = definition with
            {
                Environment = new Dictionary<string, string>
                {
                    ["CHUNKPILOT_FAKE_STATUS_PORT"] =
                        definition.Port.ToString(CultureInfo.InvariantCulture)
                }
            };
        await supervisor.ImportAsync(definition);
        return id;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string DotnetPath() => Path.Combine(RepositoryRoot(), ".tools", "dotnet", "dotnet.exe");

    private static string FakeServerDll() => Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer",
        "bin", "Release", "net10.0", "ChunkPilot.FakeServer.dll");

    public async Task DisposeAsync()
    {
        await supervisor.DisposeAsync();
        await store.DisposeAsync();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>A probe that never leaves this machine and counts every call it is asked to make.</summary>
    private sealed class RecordingProbe : IExternalReachabilityProbe
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);
        public ExternalProbeOutcome Outcome { get; set; } = ExternalProbeOutcome.Unreachable;
        public Func<Task>? BeforeReturning { get; set; }

        public bool IsConfigured => true;
        public string ConfigurationDetail => "Fixture probe.";

        public async Task<ExternalProbeResult> ProbeAsync(
            ExternalProbeRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            if (BeforeReturning is { } hook)
                await hook().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExternalProbeResult
            {
                Outcome = Outcome,
                RequestId = request.RequestId,
                ObservedAddress = request.ExpectedAddress,
                ObservedFamily = "ipv4",
                Port = request.Port,
                ConnectMilliseconds = 118,
                CheckedAt = DateTimeOffset.UtcNow,
                Detail = $"Fixture probe answered {Outcome}."
            };
        }
    }

    /// <summary>
    /// Records the RFC 6886 section 3.7 waits the provider asked for and satisfies them instantly.
    /// </summary>
    private sealed class RecordingRebootDelay : INatPmpRebootDelay
    {
        private readonly List<TimeSpan> waited = [];

        public IReadOnlyList<TimeSpan> Waited
        {
            get { lock (waited) return waited.ToArray(); }
        }

        public TimeSpan NextDelay() => new NatPmpRebootDelay().NextDelay();

        public Task WaitAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            lock (waited)
                waited.Add(duration);
            return Task.CompletedTask;
        }
    }

    /// <summary>A clock the test drives, so lease renewal and epoch validation are both deterministic.</summary>
    private sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    /// <summary>
    /// The one LAN binding this machine is on, which a test may move.
    /// </summary>
    /// <remarks>
    /// Interface, local address and gateway move independently, because the cases that matter are
    /// exactly the ones where some of them stay the same: home networks reuse 192.168.1.1 constantly, so
    /// the same gateway text over a different adapter is a different router and has to be expressible.
    /// </remarks>
    private sealed class MovableView : IRouterNetworkView
    {
        private volatile Binding current = new("eth", "Ethernet", NetworkInterfaceType.Ethernet,
            "192.168.1.50", "192.168.1.1");

        public string InterfaceId => current.InterfaceId;
        public string GatewayAddress => current.Gateway;

        /// <summary>Another adapter onto a router that happens to answer on the same address.</summary>
        public void MoveToWifiOnTheSameAddresses() =>
            current = new Binding("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211,
                "192.168.1.50", "192.168.1.1");

        /// <summary>Another network entirely, reached over another adapter.</summary>
        public void MoveToAnotherNetwork() =>
            current = new Binding("wifi", "Wi-Fi", NetworkInterfaceType.Wireless80211,
                "192.168.2.50", "192.168.2.1");

        /// <summary>The same adapter, renumbered onto another router.</summary>
        public void MoveToAnotherGatewayOnTheSameAdapter() =>
            current = new Binding("eth", "Ethernet", NetworkInterfaceType.Ethernet,
                "192.168.3.50", "192.168.3.1");

        public void MoveBackToEthernet() =>
            current = new Binding("eth", "Ethernet", NetworkInterfaceType.Ethernet,
                "192.168.1.50", "192.168.1.1");

        public IReadOnlyList<RouterGatewayCandidate> Enumerate()
        {
            var binding = current;
            return
            [
                new(new LanInterfaceCandidate(
                        binding.InterfaceId, binding.Name, $"Fixture {binding.Name}", binding.Type,
                        OperationalStatus.Up, 1_000, true, true,
                        [new LanAddressCandidate(IPAddress.Parse(binding.LocalAddress), 24)]),
                    [IPAddress.Parse(binding.Gateway)])
            ];
        }

        private sealed record Binding(
            string InterfaceId, string Name, NetworkInterfaceType Type, string LocalAddress, string Gateway);
    }

    /// <summary>
    /// Carries the providers' real datagrams to the loopback fixture instead of to the address the
    /// selector chose.
    /// </summary>
    /// <remarks>
    /// The binding has to look like an ordinary home LAN or <see cref="RouterGatewaySelector"/> refuses
    /// it, and an ordinary home LAN address is exactly what a test must never send to. Only the
    /// endpoint is redirected: the frames on the wire, and the parsers that read them, are the real
    /// ones. Nothing leaves this machine.
    /// </remarks>
    private sealed class LoopbackDatagramChannel : IGatewayDatagramChannel
    {
        private readonly UdpGatewayDatagramChannel inner = new();
        private readonly IPEndPoint fixture;

        public LoopbackDatagramChannel(int port) => fixture = new IPEndPoint(IPAddress.Loopback, port);

        public Task<byte[]?> ExchangeAsync(
            IPAddress localAddress,
            IPEndPoint gateway,
            byte[] payload,
            TimeSpan timeout,
            Action? onSent,
            CancellationToken cancellationToken) =>
            inner.ExchangeAsync(IPAddress.Loopback, fixture, payload, timeout, onSent, cancellationToken);
    }

    /// <summary>
    /// A real UDP gateway on loopback that answers real PCP and NAT-PMP frames, with an epoch the test
    /// sets. It exists so the production parsers, not a stub, are what read the epoch.
    /// </summary>
    private sealed class DatagramGateway : IAsyncDisposable
    {
        private const int PcpHeaderLength = 24;

        private readonly UdpClient socket;
        private readonly CancellationTokenSource lifetime = new();
        private readonly Task loop;
        private int maps;

        private DatagramGateway(UdpClient socket)
        {
            this.socket = socket;
            Port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
            loop = Task.Run(async () =>
            {
                try
                {
                    while (!lifetime.Token.IsCancellationRequested)
                    {
                        var request = await socket.ReceiveAsync(lifetime.Token).ConfigureAwait(false);
                        var reply = Answer(request.Buffer);
                        if (reply is not null)
                            await socket.SendAsync(reply, request.RemoteEndPoint, lifetime.Token)
                                .ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or SocketException
                                                      or ObjectDisposedException) { }
            });
        }

        public int Port { get; }

        /// <summary>The epoch every answer reports. Restarting it is how a reboot is simulated.</summary>
        public uint Epoch { get; set; } = 100_000;

        /// <summary>Restarts one protocol's epoch only, so mechanism scoping can be exercised.</summary>
        public uint? PcpEpoch { get; set; }
        public uint? NatPmpEpoch { get; set; }

        public bool PcpEnabled { get; set; } = true;
        public bool NatPmpEnabled { get; set; } = true;

        /// <summary>A non-zero result code for mapping requests only, so a refusal can be scripted.</summary>
        public byte MapResultCode { get; set; }

        /// <summary>A non-zero result code for the read-only capability answers.</summary>
        public byte DiscoveryResultCode { get; set; }

        /// <summary>Refuses one protocol's capability answer only.</summary>
        public byte? PcpDiscoveryResultCode { get; set; }
        public byte? NatPmpDiscoveryResultCode { get; set; }

        /// <summary>A public port the gateway substitutes for the one that was asked for. 0 echoes it.</summary>
        public int SubstituteExternalPort { get; set; }

        public int Maps => Volatile.Read(ref maps);

        public static DatagramGateway Start() => new(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)));

        public RouterMappingOptions Options() => new()
        {
            GatewayControlPort = Port,
            DatagramAttemptTimeouts = [TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(400)],
            DiscoveryBudget = TimeSpan.FromSeconds(20),
            OperationBudget = TimeSpan.FromSeconds(20)
        };

        private byte[]? Answer(byte[] request)
        {
            if (request.Length == 0)
                return null;
            return request[0] switch
            {
                2 when PcpEnabled => Pcp(request),
                0 when NatPmpEnabled => NatPmp(request),
                // A NAT-PMP-only gateway answers a PCP version 2 request with UNSUPP_VERSION, which is
                // how RFC 6887 defines the fall-through.
                2 => PcpAnnounceReply(resultCode: 1),
                _ => null
            };
        }

        private byte[]? Pcp(byte[] request)
        {
            if (request[1] == 0)
                return PcpAnnounceReply(PcpDiscoveryResultCode ?? DiscoveryResultCode);
            if (request[1] != 1 || request.Length < PcpHeaderLength + 36)
                return null;
            Interlocked.Increment(ref maps);
            var lifetimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(4, 4));
            var reply = new byte[PcpHeaderLength + 36];
            reply[0] = 2;
            reply[1] = 0x81;
            reply[3] = MapResultCode;
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), lifetimeSeconds);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8, 4), PcpEpoch ?? Epoch);
            request.AsSpan(PcpHeaderLength, 13).CopyTo(reply.AsSpan(PcpHeaderLength, 13));
            request.AsSpan(PcpHeaderLength + 16, 2).CopyTo(reply.AsSpan(PcpHeaderLength + 16, 2));
            // A deletion carries no external port; anything else may be answered with a substitute.
            var requested = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(PcpHeaderLength + 18, 2));
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(PcpHeaderLength + 18, 2),
                (ushort)(lifetimeSeconds == 0 ? requested
                    : SubstituteExternalPort > 0 ? SubstituteExternalPort : requested));
            WriteMapped(reply.AsSpan(PcpHeaderLength + 20, 16), IPAddress.Parse(ExternalAddress));
            return reply;
        }

        private byte[]? NatPmp(byte[] request)
        {
            if (request.Length == 2 && request[1] == 0)
            {
                var address = new byte[12];
                address[1] = 128;
                BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(2, 2),
                    NatPmpDiscoveryResultCode ?? DiscoveryResultCode);
                BinaryPrimitives.WriteUInt32BigEndian(address.AsSpan(4, 4), NatPmpEpoch ?? Epoch);
                IPAddress.Parse(ExternalAddress).GetAddressBytes().CopyTo(address.AsSpan(8, 4));
                return address;
            }
            if (request.Length != 12 || request[1] is not (1 or 2))
                return null;
            Interlocked.Increment(ref maps);
            var lifetimeSeconds = BinaryPrimitives.ReadUInt32BigEndian(request.AsSpan(8, 4));
            var requested = BinaryPrimitives.ReadUInt16BigEndian(request.AsSpan(6, 2));
            var reply = new byte[16];
            reply[1] = (byte)(128 + request[1]);
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2, 2), MapResultCode);
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(4, 4), NatPmpEpoch ?? Epoch);
            request.AsSpan(4, 2).CopyTo(reply.AsSpan(8, 2));
            BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(10, 2),
                (ushort)(lifetimeSeconds == 0 ? requested
                    : SubstituteExternalPort > 0 ? SubstituteExternalPort : requested));
            request.AsSpan(8, 4).CopyTo(reply.AsSpan(12, 4));
            return reply;
        }

        private byte[] PcpAnnounceReply(byte resultCode)
        {
            var reply = new byte[PcpHeaderLength];
            reply[0] = 2;
            reply[1] = 0x80;
            reply[3] = resultCode;
            BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(8, 4), PcpEpoch ?? Epoch);
            return reply;
        }

        /// <summary>Outside the private, shared and reserved ranges, so it classifies as routable.</summary>
        private const string ExternalAddress = "93.184.216.34";

        private static void WriteMapped(Span<byte> destination, IPAddress address)
        {
            destination.Clear();
            destination[10] = 0xFF;
            destination[11] = 0xFF;
            address.GetAddressBytes().CopyTo(destination[12..]);
        }

        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            socket.Dispose();
            try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            lifetime.Dispose();
        }
    }

    /// <summary>A gateway with a readable mapping table, standing in for a UPnP IGD.</summary>
    private sealed class TableGateway : IRouterMappingProvider
    {
        public ConcurrentDictionary<string, ExistingRouterMapping> Table { get; } = new(StringComparer.Ordinal);
        public int Creates { get; private set; }
        public int Removes { get; private set; }

        /// <summary>A non-zero UPnP error the gateway answers AddPortMapping with.</summary>
        public int CreateErrorCode { get; set; }

        public RouterMappingMechanism Mechanism => RouterMappingMechanism.UpnpIgd;
        public bool CanQueryExistingMappings => true;

        public Task<RouterDiscoveryResult> DiscoverAsync(
            RouterLanBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult(new RouterDiscoveryResult
            {
                Mechanism = Mechanism,
                Supported = true,
                ExternalAddress = "203.0.113.4",
                ControlUrl = "http://127.0.0.1/ctl",
                ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
                Detail = "Table gateway."
            });

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult(Table.GetValueOrDefault(Key(transport, externalPort)));

        public Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            if (CreateErrorCode != 0)
                return Task.FromResult(RouterMappingOutcome.Failed(Mechanism,
                    RouterMappingFailure.RequestRejected,
                    $"UPnP AddPortMapping failed with error {CreateErrorCode}."));
            Creates++;
            Table[Key(request.Transport, request.ExternalPort)] = new ExistingRouterMapping
            {
                ExternalPort = request.ExternalPort,
                Transport = request.Transport,
                InternalClient = binding.LocalAddress.ToString(),
                InternalPort = request.InternalPort,
                Description = request.Description,
                LeaseSeconds = 3600
            };
            return Task.FromResult(new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                LeaseSeconds = 3600,
                LeaseIsFinite = true,
                ExternalAddress = discovery.ExternalAddress,
                ControlUrl = discovery.ControlUrl,
                ServiceType = discovery.ServiceType,
                Detail = $"Table create for {request.Transport} {request.ExternalPort}."
            });
        }

        public Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            Removes++;
            Table.TryRemove(Key(request.Transport, request.ExternalPort), out _);
            return Task.FromResult(new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                Detail = $"Table remove for {request.Transport} {request.ExternalPort}."
            });
        }

        private static string Key(MappingTransport transport, int port) =>
            $"{transport}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}
