using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// External reachability against a genuinely running server process and a real router-mapping
/// coordinator, so the coupling between the Minecraft lifecycle, the router exposure and the evidence
/// is proven rather than simulated.
/// </summary>
/// <remarks>
/// The server is the repository's fake console server, the router is an in-process scripted gateway,
/// and the probe is a recording fake. Nothing here contacts a real router, a real firewall or any
/// external service, and no test opens a socket to the public internet.
/// </remarks>
public sealed class ExternalReachabilityIntegrationTests : IAsyncLifetime
{
    /// <summary>Outside the private, shared, documentation and reserved ranges, so it classifies as routable.</summary>
    private const string PublicAddress = "93.184.216.34";

    private readonly string root = Path.Combine(Path.GetTempPath(),
        "chunkpilot-external-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;
    private ServerSupervisor supervisor = null!;
    private RouterMappingCoordinator routers = null!;
    private ExternalReachabilityCoordinator coordinator = null!;
    private ScriptedGateway gateway = null!;
    private RecordingProbe probe = null!;
    private ILoggerFactory loggerFactory = null!;

    /// <summary>
    /// Lease renewal is floored at a minute, so proving that renewing the same entry does not disturb
    /// external evidence has to move time rather than wait for it.
    /// </summary>
    private readonly AdvanceableClock clock = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "appdata"), Path.Combine(root, "servers"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), loggerFactory);
        await supervisor.InitializeAsync();
        gateway = new ScriptedGateway();
        var service = new RouterMappingService(new FixedView(), [gateway], new RouterMappingOptions(),
            NullLogger<RouterMappingService>.Instance);
        routers = new RouterMappingCoordinator(store, supervisor, service,
            NullLogger<RouterMappingCoordinator>.Instance, clock);
        probe = new RecordingProbe();
        coordinator = new ExternalReachabilityCoordinator(supervisor, routers, probe,
            NullLogger<ExternalReachabilityCoordinator>.Instance);
    }

    // ── Eligibility ──

    [Fact(Timeout = 60_000)]
    public async Task A_running_server_with_an_active_mapping_is_eligible_and_nothing_has_been_checked()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);

        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.NotChecked, state.Phase);
        Assert.Equal(ExternalReachabilityBlocker.None, state.Blocker);
        Assert.True(state.CanCheck);
        Assert.True(state.Endpoint.IsComplete);
        Assert.Equal(PublicAddress, state.Endpoint.PublicAddress);
        // Reading state is local. Nothing has been asked of any service.
        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_stopped_server_is_ineligible_and_says_which_prerequisite_is_missing()
    {
        var (id, _) = await AddAsync();

        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Ineligible, state.Phase);
        Assert.Equal(ExternalReachabilityBlocker.ServerNotRunning, state.Blocker);
        Assert.False(state.CanCheck);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_running_server_without_direct_internet_is_ineligible_rather_than_unreachable()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);

        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityBlocker.DirectInternetOff, state.Blocker);
        Assert.NotEqual(ExternalReachabilityPhase.Unreachable, state.Phase);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task An_ineligible_server_never_reaches_the_probe_service()
    {
        var (id, _) = await AddAsync();

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Ineligible, state.Phase);
        Assert.Equal(0, probe.Calls);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_build_without_a_probe_endpoint_says_so_instead_of_failing()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Configured = false;

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityBlocker.ProbeNotConfigured, state.Blocker);
        Assert.False(state.ProbeConfigured);
        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>
    /// The check exists to gather real-world evidence, so it must not be gated on ChunkPilot owning
    /// a local firewall rule. A user with a foreign rule, a third-party product or no firewall at all
    /// is still entitled to look outside.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Eligibility_never_depends_on_a_chunkpilot_owned_firewall_rule()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        var router = await routers.GetStateAsync(id, CancellationToken.None);

        foreach (var firewall in new[]
                 {
                     FirewallAccessPhase.Configured, FirewallAccessPhase.ExistingWindowsRule,
                     FirewallAccessPhase.FirewallDisabled, FirewallAccessPhase.NotChecked,
                     FirewallAccessPhase.NeedsPermission, FirewallAccessPhase.Unsupported
                 })
        {
            // The firewall state is deliberately not an input to the decision at all; enumerating it
            // here documents that the policy signature cannot depend on it.
            Assert.Equal(ExternalReachabilityBlocker.None,
                ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566, router));
            Assert.NotEqual(FirewallAccessPhase.Stale, firewall);
        }
        await server.StopAsync();
    }

    // ── Results ──

    [Fact(Timeout = 60_000)]
    public async Task A_successful_probe_is_the_only_thing_that_produces_a_verified_state()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Reachable, state.Phase);
        Assert.True(state.IsVerified);
        Assert.Equal(PublicAddress, state.ObservedAddress);
        Assert.Equal(server.Definition.Port, state.Port);
        Assert.NotNull(state.CheckedAt);
        Assert.Equal(1, probe.Calls);
        // The request never names a host: the expectation is compared, and the port is the mapping's.
        Assert.Equal(PublicAddress, probe.LastRequest!.ExpectedAddress);
        Assert.Equal(server.Definition.Port, probe.LastRequest.Port);
        await server.StopAsync();
    }

    [Theory(Timeout = 60_000)]
    [InlineData(ExternalProbeOutcome.Unreachable, ExternalReachabilityPhase.Unreachable)]
    [InlineData(ExternalProbeOutcome.SourceMismatch, ExternalReachabilityPhase.SourceAddressMismatch)]
    [InlineData(ExternalProbeOutcome.UnsupportedAddressFamily, ExternalReachabilityPhase.UnsupportedAddressFamily)]
    [InlineData(ExternalProbeOutcome.RateLimited, ExternalReachabilityPhase.RateLimited)]
    [InlineData(ExternalProbeOutcome.ServiceUnavailable, ExternalReachabilityPhase.ProbeUnavailable)]
    [InlineData(ExternalProbeOutcome.Timeout, ExternalReachabilityPhase.ProbeUnavailable)]
    [InlineData(ExternalProbeOutcome.ProbeError, ExternalReachabilityPhase.ProbeUnavailable)]
    [InlineData(ExternalProbeOutcome.MalformedResponse, ExternalReachabilityPhase.ProbeUnavailable)]
    [InlineData(ExternalProbeOutcome.InvalidRequest, ExternalReachabilityPhase.ProbeUnavailable)]
    public async Task Only_a_completed_handshake_may_read_as_reachable(
        ExternalProbeOutcome outcome, ExternalReachabilityPhase expected)
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = outcome;

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(expected, state.Phase);
        Assert.False(state.IsVerified);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_cancelled_check_concludes_nothing_and_changes_nothing()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.CancelWhenCalled = coordinator;
        probe.CancelServerId = id;

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Cancelled, state.Phase);
        Assert.False(state.IsVerified);
        await server.StopAsync();
    }

    // ── Lifetime and staleness ──

    [Fact(Timeout = 60_000)]
    public async Task Stopping_the_server_ends_a_verified_claim()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);

        await server.StopAsync();
        await routers.SynchronizeAsync(id, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.False(state.IsVerified);
        Assert.Equal("", state.VerifiedEndpoint);
        // The historical evidence stays readable; it just stops being current.
        Assert.NotNull(state.CheckedAt);
        Assert.True(state.HasHistoricalResult);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_new_run_after_a_stop_does_not_inherit_the_previous_verification()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);
        await server.StopAsync();
        await routers.SynchronizeAsync(id, CancellationToken.None);

        Assert.True((await server.StartAsync()).Success);
        await routers.SynchronizeAsync(id, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.False(state.IsVerified);
        // And nothing re-ran on its own to fix it.
        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task Turning_direct_internet_off_ends_a_verified_claim()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);

        await routers.DisableAsync(id, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.Equal(ExternalReachabilityBlocker.DirectInternetOff, state.Blocker);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_result_that_arrives_after_the_server_stopped_is_discarded_rather_than_applied()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        // The world moves while the request is out: the server stops before the answer lands.
        probe.BeforeReturning = async () =>
        {
            await server.StopAsync();
            await routers.SynchronizeAsync(id, CancellationToken.None);
        };

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Equal(ExternalReachabilityPhase.Ineligible, state.Phase);
        Assert.Null(state.CheckedAt);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_result_that_arrives_after_the_mapping_was_recreated_is_discarded()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        probe.BeforeReturning = async () =>
        {
            // A router that forgot its table and had the entry re-established under a new identity.
            gateway.Table.Clear();
            await routers.EnableAsync(id, true, CancellationToken.None);
            gateway.ExternalAddress = "93.184.216.99";
            await routers.EnableAsync(id, true, CancellationToken.None);
        };

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Null(state.CheckedAt);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_changed_public_address_ends_a_verified_claim()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);

        gateway.ExternalAddress = "93.184.216.99";
        await routers.EnableAsync(id, true, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_router_without_a_public_address_is_diagnosed_rather_than_probed()
    {
        var (id, server) = await AddAsync();
        gateway.ExternalAddress = "100.64.9.12";
        await StartWithMappingAsync(id, server);

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityBlocker.PublicAddressNotRoutable, state.Blocker);
        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    // ── Mapping instance identity (CP-2026-012) ──
    //
    // Every test in this block keeps the same server process running throughout, so the run identity
    // cannot be what invalidates anything. The mapping is the only thing that changes, and it is
    // recreated with values identical in every respect a person or a router can observe.

    [Fact(Timeout = 60_000)]
    public async Task An_identical_mapping_recreated_after_removal_is_a_different_mapping()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        var first = await routers.GetStateAsync(id, CancellationToken.None);

        await routers.DisableAsync(id, CancellationToken.None);
        await routers.EnableAsync(id, consentGranted: true, CancellationToken.None);
        var second = await routers.GetStateAsync(id, CancellationToken.None);

        // Identical in every observable value...
        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Equal(first.RouterReportedExternalAddress, second.RouterReportedExternalAddress);
        Assert.Equal(first.ExternalPort, second.ExternalPort);
        Assert.Equal(first.InternalPort, second.InternalPort);
        Assert.Equal(first.InternalClient, second.InternalClient);
        Assert.Equal(first.Mechanism, second.Mechanism);
        Assert.Equal(first.Transport, second.Transport);
        // ...and still not the same establishment.
        Assert.NotEqual("", first.MappingInstanceId);
        Assert.NotEqual("", second.MappingInstanceId);
        Assert.NotEqual(first.MappingInstanceId, second.MappingInstanceId);
        await server.StopAsync();
    }

    /// <summary>Test A: the ABA transition after a completed verification.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_verification_does_not_survive_an_identical_mapping_being_recreated()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        var verified = await coordinator.CheckAsync(id, CancellationToken.None);
        Assert.True(verified.IsVerified);
        var checkedEndpoint = verified.CheckedEndpoint;

        await routers.DisableAsync(id, CancellationToken.None);
        await routers.EnableAsync(id, consentGranted: true, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        // The new endpoint is identical in every value the old one carried except its identity.
        Assert.Equal(checkedEndpoint.PublicAddress, state.Endpoint.PublicAddress);
        Assert.Equal(checkedEndpoint.ExternalPort, state.Endpoint.ExternalPort);
        Assert.Equal(checkedEndpoint.InternalPort, state.Endpoint.InternalPort);
        Assert.Equal(checkedEndpoint.RunIdentity, state.Endpoint.RunIdentity);
        Assert.NotEqual(checkedEndpoint.MappingIdentity, state.Endpoint.MappingIdentity);

        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.False(state.IsVerified);
        Assert.Equal("", state.VerifiedEndpoint);
        // The only way back to verified is another deliberate check.
        Assert.True(state.CanCheck);
        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>Test E: the classic ABA sequence, ending on values equal to the original.</summary>
    [Fact(Timeout = 60_000)]
    public async Task The_aba_sequence_never_makes_the_original_result_current_again()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);
        var original = (await coordinator.GetStateAsync(id, CancellationToken.None)).Endpoint;

        // A → removed → B (different values) → removed → A-valued again.
        await routers.DisableAsync(id, CancellationToken.None);
        gateway.ExternalAddress = "93.184.216.99";
        await routers.EnableAsync(id, true, CancellationToken.None);
        Assert.False((await coordinator.GetStateAsync(id, CancellationToken.None)).IsVerified);

        await routers.DisableAsync(id, CancellationToken.None);
        gateway.ExternalAddress = PublicAddress;
        await routers.EnableAsync(id, true, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(original.PublicAddress, state.Endpoint.PublicAddress);
        Assert.Equal(original.ExternalPort, state.Endpoint.ExternalPort);
        Assert.Equal(original.RunIdentity, state.Endpoint.RunIdentity);
        Assert.NotEqual(original.MappingIdentity, state.Endpoint.MappingIdentity);
        Assert.Equal(ExternalReachabilityPhase.Stale, state.Phase);
        Assert.False(state.IsVerified);
        await server.StopAsync();
    }

    /// <summary>Test B: the same recreation, but while the probe is still out.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_result_that_arrives_after_an_identical_mapping_was_recreated_is_discarded()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        probe.BeforeReturning = async () =>
        {
            await routers.DisableAsync(id, CancellationToken.None);
            await routers.EnableAsync(id, consentGranted: true, CancellationToken.None);
        };

        var state = await coordinator.CheckAsync(id, CancellationToken.None);

        Assert.False(state.IsVerified);
        Assert.Null(state.CheckedAt);
        Assert.Equal(RouterMappingPhase.Active,
            (await routers.GetStateAsync(id, CancellationToken.None)).Phase);
        await server.StopAsync();
    }

    /// <summary>Test C: reading the same live mapping over and over is not a new establishment.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Reading_the_same_live_mapping_never_manufactures_a_new_generation()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);
        var instance = (await routers.GetStateAsync(id, CancellationToken.None)).MappingInstanceId;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            Assert.Equal(instance, (await routers.GetStateAsync(id, CancellationToken.None)).MappingInstanceId);
            Assert.Equal(instance,
                (await routers.SynchronizeAsync(id, CancellationToken.None)).MappingInstanceId);
            Assert.True((await coordinator.GetStateAsync(id, CancellationToken.None)).IsVerified);
        }

        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>
    /// Test D: renewing a lease asks the router for the same entry on the same terms, so it is the
    /// same establishment continuing and must not disturb evidence gathered about it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Renewing_the_lease_on_the_same_entry_keeps_the_generation_and_the_verification()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);
        var instance = (await routers.GetStateAsync(id, CancellationToken.None)).MappingInstanceId;
        var creates = gateway.Creates;

        clock.Advance(TimeSpan.FromSeconds(RouterMappingPolicy.RequestedLeaseSeconds / 2 + 5));
        var renewed = await routers.SynchronizeAsync(id, CancellationToken.None);

        // The renewal really happened, and it kept the same establishment.
        Assert.True(gateway.Creates > creates);
        Assert.Equal(RouterMappingPhase.Active, renewed.Phase);
        Assert.Equal(instance, renewed.MappingInstanceId);
        Assert.True((await coordinator.GetStateAsync(id, CancellationToken.None)).IsVerified);
        await server.StopAsync();
    }

    /// <summary>A router that forgot its table has its entry re-established, which is a new one.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_mapping_re_established_after_the_router_forgot_it_is_a_new_generation()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);

        // The router drops everything, and the next reconciliation withdraws what it can no longer
        // find and re-creates it on identical terms.
        gateway.Table.Clear();
        await routers.SynchronizeAsync(id, CancellationToken.None);
        await routers.EnableAsync(id, consentGranted: true, CancellationToken.None);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.False(state.IsVerified);
        await server.StopAsync();
    }

    // ── Serialization and background behaviour ──

    [Fact(Timeout = 60_000)]
    public async Task A_second_click_while_a_check_runs_is_answered_by_the_first_rather_than_sent
        ()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        using var release = new SemaphoreSlim(0, 1);
        probe.BeforeReturning = () => release.WaitAsync();

        var first = coordinator.CheckAsync(id, CancellationToken.None);
        await probe.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        var second = coordinator.CheckAsync(id, CancellationToken.None);
        release.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, probe.Calls);
        Assert.All(results, result => Assert.True(result.IsVerified));
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task Lease_revocation_invalidates_verified_rejects_late_success_and_new_generation_is_unverified()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        var sessions = new UiSessionAuthority(new AlwaysAliveUiObserver());
        var registration = sessions.Register(new UiSessionRegistrationRequest(500, 700));
        var credential = new UiSessionCredential
        {
            SessionId = registration.Session.SessionId,
            Capability = registration.Capability
        };
        var leaseRegistry = new PublicConnectivityLeaseRegistry(sessions);
        var firstLease = leaseRegistry.Create(id, credential);
        await using var leasedCoordinator = new ExternalReachabilityCoordinator(
            supervisor, routers, probe, NullLogger<ExternalReachabilityCoordinator>.Instance,
            leases: leaseRegistry);

        probe.Outcome = ExternalProbeOutcome.Reachable;
        var verified = await leasedCoordinator.CheckAsync(id, firstLease, CancellationToken.None);
        Assert.True(verified.IsVerified);
        Assert.Equal(firstLease.LeaseId, verified.Endpoint.PublicConnectivityLeaseId);

        using var release = new SemaphoreSlim(0, 1);
        while (probe.Entered.Wait(0)) { }
        probe.BeforeReturning = () => release.WaitAsync();
        var late = leasedCoordinator.CheckAsync(id, firstLease, CancellationToken.None);
        await probe.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        _ = leaseRegistry.RevokeAll();
        leasedCoordinator.Invalidate(id);
        release.Release();
        var afterRevocation = await late;

        Assert.False(afterRevocation.IsVerified);
        Assert.Null(afterRevocation.CheckedAt);
        Assert.Equal(ExternalReachabilityPhase.Ineligible, afterRevocation.Phase);

        probe.BeforeReturning = null;
        var secondLease = leaseRegistry.Create(id, credential);
        var fresh = await leasedCoordinator.GetStateAsync(id, CancellationToken.None);
        Assert.True(secondLease.Generation > firstLease.Generation);
        Assert.Equal(ExternalReachabilityPhase.NotChecked, fresh.Phase);
        Assert.False(fresh.IsVerified);
        Assert.Equal(secondLease.LeaseId, fresh.Endpoint.PublicConnectivityLeaseId);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task Repeated_state_reads_and_router_reconciliation_never_contact_the_service()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);

        for (var attempt = 0; attempt < 20; attempt++)
        {
            _ = await coordinator.GetStateAsync(id, CancellationToken.None);
            _ = await routers.SynchronizeAsync(id, CancellationToken.None);
        }
        await routers.SynchronizeAsync(id, CancellationToken.None);
        await server.StopAsync();
        await routers.SynchronizeAsync(id, CancellationToken.None);
        Assert.True((await server.StartAsync()).Success);
        await routers.SynchronizeAsync(id, CancellationToken.None);
        _ = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(0, probe.Calls);
        await server.StopAsync();
    }

    /// <summary>An Agent restart forgets point-in-time evidence rather than resurrecting it.</summary>
    [Fact(Timeout = 60_000)]
    public async Task An_agent_restart_reports_not_checked_rather_than_a_remembered_verification()
    {
        var (id, server) = await AddAsync();
        await StartWithMappingAsync(id, server);
        probe.Outcome = ExternalProbeOutcome.Reachable;
        Assert.True((await coordinator.CheckAsync(id, CancellationToken.None)).IsVerified);

        await coordinator.DisposeAsync();
        coordinator = new ExternalReachabilityCoordinator(supervisor, routers, probe,
            NullLogger<ExternalReachabilityCoordinator>.Instance);
        var state = await coordinator.GetStateAsync(id, CancellationToken.None);

        Assert.Equal(ExternalReachabilityPhase.NotChecked, state.Phase);
        Assert.Null(state.CheckedAt);
        Assert.Equal(1, probe.Calls);
        await server.StopAsync();
    }

    private async Task StartWithMappingAsync(Guid id, ManagedServer server)
    {
        Assert.True((await server.StartAsync()).Success);
        var router = await routers.EnableAsync(id, consentGranted: true, CancellationToken.None);
        Assert.Equal(RouterMappingPhase.Active, router.Phase);
    }

    private async Task<(Guid Id, ManagedServer Server)> AddAsync()
    {
        var id = Guid.NewGuid();
        var serverRoot = Path.Combine(root, "servers", id.ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var port = GetFreePort();
        await supervisor.ImportAsync(new ServerDefinition
        {
            Id = id,
            Name = "External reachability fixture",
            RootPath = serverRoot,
            WorkingDirectory = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal",
            ReadinessPattern = @"Done \(.+?\)!|For help, type",
            StartupTimeoutSeconds = 20,
            ShutdownTimeoutSeconds = 10,
            SaveTimeoutSeconds = 10,
            RestartDelaySeconds = 0,
            Port = port,
            Environment = new Dictionary<string, string>
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(CultureInfo.InvariantCulture)
            }
        });
        return (id, supervisor.Get(id));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate ChunkPilot repository root.");
    }

    private static string DotnetPath() => Path.Combine(RepositoryRoot(), ".tools", "dotnet", "dotnet.exe");

    private static string FakeServerDll() => Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer",
        "bin", "Release", "net10.0", "ChunkPilot.FakeServer.dll");

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        await routers.DisposeAsync();
        await supervisor.DisposeAsync();
        await store.DisposeAsync();
        loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class AdvanceableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class AlwaysAliveUiObserver : IUiProcessObserver
    {
        public UiProcessLiveness Observe(int processId, long creationTicks) => UiProcessLiveness.Alive;
    }

    /// <summary>Counts every call so a test can prove that no background path reaches the service.</summary>
    private sealed class RecordingProbe : IExternalReachabilityProbe
    {
        private int calls;

        public SemaphoreSlim Entered { get; } = new(0);
        public int Calls => Volatile.Read(ref calls);
        public ExternalProbeRequest? LastRequest { get; private set; }
        public bool Configured { get; set; } = true;
        public ExternalProbeOutcome Outcome { get; set; } = ExternalProbeOutcome.Unreachable;
        public Func<Task>? BeforeReturning { get; set; }
        public ExternalReachabilityCoordinator? CancelWhenCalled { get; set; }
        public Guid CancelServerId { get; set; }

        public bool IsConfigured => Configured;
        public string ConfigurationDetail => Configured ? "Fixture probe." : "No probe endpoint configured.";

        public async Task<ExternalProbeResult> ProbeAsync(
            ExternalProbeRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            LastRequest = request;
            Entered.Release();
            if (CancelWhenCalled is { } coordinator)
            {
                coordinator.Cancel(CancelServerId);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (BeforeReturning is { } hook)
                await hook().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new ExternalProbeResult
            {
                Outcome = Outcome,
                RequestId = request.RequestId,
                ObservedAddress = Outcome == ExternalProbeOutcome.SourceMismatch
                    ? "198.51.100.42"
                    : request.ExpectedAddress,
                ObservedFamily = "ipv4",
                Port = request.Port,
                ConnectMilliseconds = 118,
                CheckedAt = DateTimeOffset.UtcNow,
                Detail = $"Fixture probe answered {Outcome}."
            };
        }
    }

    private sealed class FixedView : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() =>
        [
            new(new LanInterfaceCandidate(
                    "eth", "Ethernet", "Fixture Ethernet",
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                    System.Net.NetworkInformation.OperationalStatus.Up,
                    1_000, true, true,
                    [new LanAddressCandidate(IPAddress.Parse("192.168.1.50"), 24)]),
                [IPAddress.Parse("192.168.1.1")])
        ];
    }

    private sealed class ScriptedGateway : IRouterMappingProvider
    {
        public ConcurrentDictionary<string, ExistingRouterMapping> Table { get; } = new(StringComparer.Ordinal);
        public string ExternalAddress { get; set; } = PublicAddress;
        public int Creates { get; private set; }

        public RouterMappingMechanism Mechanism => RouterMappingMechanism.UpnpIgd;
        public bool CanQueryExistingMappings => true;

        public Task<RouterDiscoveryResult> DiscoverAsync(
            RouterLanBinding binding, CancellationToken cancellationToken) =>
            Task.FromResult(new RouterDiscoveryResult
            {
                Mechanism = Mechanism,
                Supported = true,
                ExternalAddress = ExternalAddress,
                ControlUrl = "http://192.168.1.1/ctl",
                ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
                Detail = "Scripted gateway."
            });

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult(Table.GetValueOrDefault(Key(transport, externalPort)));

        public Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
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
                ExternalAddress = ExternalAddress,
                ControlUrl = discovery.ControlUrl,
                ServiceType = discovery.ServiceType,
                Detail = $"Scripted create for {request.Transport} {request.ExternalPort}."
            });
        }

        public Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            Table.TryRemove(Key(request.Transport, request.ExternalPort), out _);
            return Task.FromResult(new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                Detail = $"Scripted remove for {request.Transport} {request.ExternalPort}."
            });
        }

        private static string Key(MappingTransport transport, int port) =>
            $"{transport}:{port.ToString(CultureInfo.InvariantCulture)}";
    }
}
