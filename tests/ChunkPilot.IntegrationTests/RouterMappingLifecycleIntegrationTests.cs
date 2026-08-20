using System.Collections.Concurrent;
using System.Net;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The router-mapping coordinator against a real SQLite store, a real supervisor and a controlled
/// in-process gateway.
/// </summary>
/// <remarks>
/// The gateway here is a scripted <see cref="IRouterMappingProvider"/> rather than a socket, because
/// what these tests are about is lifecycle, ownership and persistence — the wire formats are proven
/// byte for byte against loopback sockets in the unit suite. No real router is contacted by either.
/// </remarks>
public sealed class RouterMappingLifecycleIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkpilot-router-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_new_server_has_direct_internet_off_and_the_router_is_never_contacted()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        var state = await harness.Coordinator.GetStateAsync(serverId, CancellationToken.None);

        Assert.False(state.Enabled);
        Assert.Equal(RouterMappingPhase.Off, state.Phase);
        Assert.Equal(0, harness.Gateway.Creates);
        Assert.Equal(0, harness.Gateway.Discoveries);
        Assert.Null(await harness.Store.GetRouterMappingAsync(serverId));
    }

    [Fact]
    public async Task Starting_a_server_with_direct_internet_off_touches_no_router()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(0, harness.Gateway.Creates);
        Assert.Equal(0, harness.Gateway.Discoveries);
    }

    [Fact]
    public async Task Enabling_without_consent_refuses_and_changes_nothing()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        var state = await harness.Coordinator.EnableAsync(serverId, consentGranted: false, CancellationToken.None);

        Assert.False(state.Enabled);
        Assert.Equal(0, harness.Gateway.Creates);
        var stored = await harness.Store.GetRouterMappingAsync(serverId);
        Assert.False(stored!.DirectInternetEnabled);
        Assert.False(stored.ConsentGranted);
    }

    [Fact]
    public async Task Enabling_with_consent_creates_exactly_one_tcp_mapping_on_the_servers_own_port()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25566);

        var state = await harness.Coordinator.EnableAsync(serverId, consentGranted: true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Equal(25566, state.ExternalPort);
        Assert.Equal(MappingTransport.Tcp, state.Transport);
        Assert.Equal(1, harness.Gateway.Creates);
        var created = Assert.Single(harness.Gateway.Table.Values);
        Assert.Equal(MappingTransport.Tcp, created.Transport);
        Assert.Equal(25566, created.InternalPort);
        Assert.Equal(RouterMappingPolicy.MappingDescription, created.Description);
    }

    [Fact]
    public async Task Consent_is_recorded_per_server_and_never_shared()
    {
        await using var harness = await Harness.StartAsync(root);
        var alpha = await harness.AddServerAsync("Alpha", 25565);
        var beta = await harness.AddServerAsync("Beta", 25566);
        await harness.Coordinator.EnableAsync(alpha, consentGranted: true, CancellationToken.None);

        var betaWithoutConsent = await harness.Coordinator.EnableAsync(beta, false, CancellationToken.None);

        Assert.False(betaWithoutConsent.Enabled);
        Assert.Equal(1, harness.Gateway.Creates);
        Assert.True((await harness.Store.GetRouterMappingAsync(alpha))!.ConsentGranted);
        Assert.False((await harness.Store.GetRouterMappingAsync(beta))!.ConsentGranted);
    }

    [Fact]
    public async Task Turning_direct_internet_off_removes_the_mapping_and_forgets_the_consent()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        var state = await harness.Coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.False(state.Enabled);
        Assert.Equal(RouterMappingPhase.Off, state.Phase);
        Assert.Empty(harness.Gateway.Table);
        var stored = await harness.Store.GetRouterMappingAsync(serverId);
        Assert.False(stored!.ConsentGranted);
        Assert.False(stored.HasActiveMapping);
    }

    [Fact]
    public async Task Repeating_enable_or_disable_is_harmless()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Single(harness.Gateway.Table);

        await harness.Coordinator.DisableAsync(serverId, CancellationToken.None);
        var second = await harness.Coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Off, second.Phase);
        Assert.Empty(harness.Gateway.Table);
    }

    /// <summary>The rule that protects other people's router settings.</summary>
    [Fact]
    public async Task A_foreign_entry_on_the_port_is_reported_and_never_overwritten()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        harness.Gateway.Table["TCP:25565"] = new ExistingRouterMapping
        {
            ExternalPort = 25565,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.99",
            InternalPort = 25565,
            Description = "Someone else"
        };

        var state = await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Conflict, state.Phase);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, state.Failure);
        Assert.Equal(0, harness.Gateway.Creates);
        Assert.Equal("Someone else", harness.Gateway.Table["TCP:25565"].Description);
        Assert.False((await harness.Store.GetRouterMappingAsync(serverId))!.HasActiveMapping);
    }

    [Fact]
    public async Task A_foreign_entry_that_appears_later_is_not_deleted_during_cleanup()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        // The router was reconfigured behind ChunkPilot's back.
        harness.Gateway.Table["TCP:25565"] = new ExistingRouterMapping
        {
            ExternalPort = 25565,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.99",
            InternalPort = 25565,
            Description = "Someone else"
        };

        await harness.Coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.Equal(0, harness.Gateway.Removes);
        Assert.Equal("Someone else", harness.Gateway.Table["TCP:25565"].Description);
    }

    /// <summary>
    /// Intent alone must never keep a mapping alive. A stopped server carries Direct internet forward
    /// but holds nothing open, and repeated reconciliation must not start rebuilding it.
    /// </summary>
    [Fact]
    public async Task Reconciling_a_stopped_server_withdraws_once_and_never_rebuilds()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);
        await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);
        await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Equal(1, harness.Gateway.Creates);
        Assert.Equal(1, harness.Gateway.Removes);
        Assert.Empty(harness.Gateway.Table);
        Assert.True((await harness.Store.GetRouterMappingAsync(serverId))!.DirectInternetEnabled);
    }

    [Fact]
    public async Task A_deliberate_stop_closes_the_exposure_immediately()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        var state = await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Empty(harness.Gateway.Table);
        // Configured, with nothing open. This is the state the stopped server was reporting as
        // "Router port is open".
        Assert.Equal(RouterMappingPhase.Inactive, state.Phase);
        // Intent survives, so the next start re-establishes it without asking again.
        Assert.True(state.Enabled);
        Assert.True((await harness.Store.GetRouterMappingAsync(serverId))!.DirectInternetEnabled);
    }

    /// <summary>
    /// No mapping may outlive the server on a timer. A stopped server that is not restarting loses its
    /// exposure on the very first reconciliation, not after a grace period.
    /// </summary>
    [Fact]
    public async Task A_stopped_server_loses_its_exposure_on_the_first_reconciliation()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Single(harness.Gateway.Table);

        await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Empty(harness.Gateway.Table);
        Assert.Equal(1, harness.Gateway.Removes);
    }

    /// <summary>
    /// An Agent that starts up while the server is already stopped must not treat surviving intent as
    /// a reason to open anything.
    /// </summary>
    [Fact]
    public async Task An_agent_restart_while_the_server_is_stopped_opens_nothing()
    {
        Guid serverId;
        await using (var first = await Harness.StartAsync(root))
        {
            serverId = await first.AddServerAsync("Alpha", 25565);
            await first.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
            await first.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);
            Assert.Empty(first.Gateway.Table);
        }

        await using var second = await Harness.StartAsync(root, keepData: true);
        await second.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);
        var state = await second.Coordinator.GetStateAsync(serverId, CancellationToken.None);

        Assert.Empty(second.Gateway.Table);
        Assert.Equal(0, second.Gateway.Creates);
        Assert.Equal(RouterMappingPhase.Inactive, state.Phase);
        Assert.True(state.Enabled);
    }

    [Fact]
    public async Task Intent_and_ownership_survive_an_agent_restart_and_the_mapping_is_reconciled()
    {
        Guid serverId;
        string token;
        await using (var first = await Harness.StartAsync(root))
        {
            serverId = await first.AddServerAsync("Alpha", 25565);
            await first.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
            token = (await first.Store.GetRouterMappingAsync(serverId))!.OwnershipToken;
        }

        await using var second = await Harness.StartAsync(root, keepData: true);
        var restored = await second.Store.GetRouterMappingAsync(serverId);

        Assert.NotNull(restored);
        Assert.True(restored.DirectInternetEnabled);
        Assert.True(restored.ConsentGranted);
        Assert.Equal(RouterMappingMechanism.UpnpIgd, restored.Mechanism);
        Assert.Equal(token, restored.OwnershipToken);
        Assert.Equal(RouterMappingPolicy.MappingDescription, restored.Description);
    }

    [Fact]
    public async Task A_failed_removal_is_retained_and_reported_rather_than_forgotten()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        harness.Gateway.RemoveFails = true;

        var state = await harness.Coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.True(state.RemovalPending);
        Assert.Equal(RouterMappingPhase.NeedsAttention, state.Phase);
        var stored = await harness.Store.GetRouterMappingAsync(serverId);
        Assert.True(stored!.RemovalPending);
        Assert.Equal(RouterMappingFailure.RemovalFailed, stored.LastFailure);
    }

    [Fact]
    public async Task A_retained_removal_is_retried_by_reconciliation_and_then_forgotten()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        harness.Gateway.RemoveFails = true;
        await harness.Coordinator.DisableAsync(serverId, CancellationToken.None);
        harness.Gateway.RemoveFails = false;

        await harness.Coordinator.SynchronizeAsync(serverId, CancellationToken.None);

        Assert.Empty(harness.Gateway.Table);
        Assert.False((await harness.Store.GetRouterMappingAsync(serverId))!.RemovalPending);
    }

    [Fact]
    public async Task Deleting_a_server_closes_its_port_first()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        var note = await harness.Coordinator.PrepareForDeletionAsync(serverId, CancellationToken.None);

        Assert.Equal("", note);
        Assert.Empty(harness.Gateway.Table);
        Assert.Null(await harness.Store.GetRouterMappingAsync(serverId));
    }

    [Fact]
    public async Task Deleting_a_server_whose_port_cannot_be_closed_reports_and_keeps_the_evidence()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        harness.Gateway.RemoveFails = true;

        var note = await harness.Coordinator.PrepareForDeletionAsync(serverId, CancellationToken.None);

        Assert.Contains("could not be closed", note, StringComparison.Ordinal);
        Assert.True((await harness.Store.GetRouterMappingAsync(serverId))!.RemovalPending);
    }

    [Fact]
    public async Task Application_exit_withdraws_every_owned_mapping_and_revokes_intent_and_consent()
    {
        await using var harness = await Harness.StartAsync(root);
        var alpha = await harness.AddServerAsync("Alpha", 25565);
        var beta = await harness.AddServerAsync("Beta", 25566);
        _ = await harness.PublicConnectivity.EnableAsync(harness.EnableRequest(alpha), CancellationToken.None);
        _ = await harness.PublicConnectivity.EnableAsync(harness.EnableRequest(beta), CancellationToken.None);

        var exitEpoch = harness.Sessions.BeginApplicationExit(harness.Credential);
        var revoked = harness.PublicConnectivity.RevokeAllImmediately();
        await harness.PublicConnectivity.CleanupRevokedAsync(revoked, exitEpoch, CancellationToken.None);

        Assert.Empty(harness.Gateway.Table);
        var alphaRecord = (await harness.Store.GetRouterMappingAsync(alpha))!;
        var betaRecord = (await harness.Store.GetRouterMappingAsync(beta))!;
        Assert.False(alphaRecord.DirectInternetEnabled);
        Assert.False(alphaRecord.ConsentGranted);
        Assert.False(betaRecord.DirectInternetEnabled);
        Assert.False(betaRecord.ConsentGranted);
    }

    [Fact]
    public async Task Production_composition_refuses_unauthorized_or_mismatched_requests_before_persistence()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.PublicConnectivity.EnableAsync(
            new EnableRouterMappingRequest(serverId, true), CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.PublicConnectivity.EnableAsync(
            new EnableRouterMappingRequest(serverId, true)
            {
                Session = harness.Credential,
                ConnectivityOperation = PublicConnectivityOperation.DisableRouterMapping
            }, CancellationToken.None));

        Assert.Null(await harness.Store.GetRouterMappingAsync(serverId));
        Assert.Empty(harness.Gateway.Table);

        var enabled = await harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(serverId), CancellationToken.None);
        var lease = enabled.PublicConnectivityLease;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.PublicConnectivity.DisableAsync(
            harness.ServerRequest(serverId, PublicConnectivityOperation.DisableRouterMapping,
                lease with { Generation = lease.Generation + 1 }), CancellationToken.None));

        Assert.True((await harness.Store.GetRouterMappingAsync(serverId))!.DirectInternetEnabled);
        Assert.Single(harness.Gateway.Table);
    }

    [Fact]
    public async Task Per_server_leases_are_independent_and_old_cleanup_cannot_mutate_a_new_generation()
    {
        await using var harness = await Harness.StartAsync(root);
        var alpha = await harness.AddServerAsync("Alpha", 25565);
        var beta = await harness.AddServerAsync("Beta", 25566);
        var firstAlpha = (await harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(alpha), CancellationToken.None)).PublicConnectivityLease;
        var betaLease = (await harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(beta), CancellationToken.None)).PublicConnectivityLease;

        harness.Gateway.OperationDelay = TimeSpan.FromMilliseconds(100);
        var disable = harness.PublicConnectivity.DisableAsync(
            harness.ServerRequest(alpha, PublicConnectivityOperation.DisableRouterMapping, firstAlpha),
            CancellationToken.None);
        var enableAgain = harness.PublicConnectivity.EnableAsync(harness.EnableRequest(alpha), CancellationToken.None);
        await disable;
        var secondAlpha = (await enableAgain).PublicConnectivityLease;

        Assert.True(secondAlpha.Generation > firstAlpha.Generation);
        Assert.NotEqual(firstAlpha.LeaseId, secondAlpha.LeaseId);
        Assert.Equal(betaLease, harness.Leases.Get(beta));
        Assert.Equal(secondAlpha, harness.Leases.Get(alpha));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => harness.PublicConnectivity.RetryAsync(
            harness.ServerRequest(alpha, PublicConnectivityOperation.RetryRouterMapping, firstAlpha),
            CancellationToken.None));
        Assert.Equal(secondAlpha, harness.Leases.Get(alpha));
    }

    [Fact]
    public async Task Revocation_after_authorization_but_before_router_gate_rejects_queued_establishment()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        var lease = harness.Leases.Create(serverId, harness.Credential);
        var authority = RouterOperationAuthority.Exposure(lease, () => harness.Leases.IsCurrent(lease),
            () => harness.Leases.IsLatestRevokedGeneration(lease));
        var barrier = harness.Gateway.BlockNextDiscovery();
        var held = harness.Coordinator.CheckAsync(serverId, CancellationToken.None);
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = harness.Coordinator.EnableAsync(serverId, true, authority, CancellationToken.None);

        _ = harness.Leases.Revoke(serverId, harness.Credential, lease, "synthetic revocation");
        harness.Coordinator.Cancel(serverId);
        barrier.Release();
        await held;
        _ = await queued;

        Assert.Equal(0, harness.Gateway.Creates);
        Assert.False((await harness.Store.GetRouterMappingAsync(serverId))?.DirectInternetEnabled ?? false);
    }

    [Fact(Timeout = 10_000)]
    public async Task Queued_renewal_is_rejected_after_lease_revocation()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        var enabled = await harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(serverId), CancellationToken.None);
        var lease = enabled.PublicConnectivityLease;
        var due = (await harness.Store.GetRouterMappingAsync(serverId))! with
        {
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1)
        };
        await harness.Store.UpsertRouterMappingAsync(due);
        var creates = harness.Gateway.Creates;
        var barrier = harness.Gateway.BlockNextDiscovery();
        var held = harness.Coordinator.CheckAsync(serverId, CancellationToken.None);
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = harness.Coordinator.SynchronizeAsync(serverId,
            RouterOperationAuthority.Exposure(lease, () => harness.Leases.IsCurrent(lease),
                () => harness.Leases.IsLatestRevokedGeneration(lease)),
            CancellationToken.None);

        _ = harness.Leases.Revoke(serverId, harness.Credential, lease, "synthetic revocation");
        harness.Coordinator.Cancel(serverId);
        barrier.Release();
        await held;
        _ = await queued;

        Assert.Equal(creates, harness.Gateway.Creates);
    }

    [Fact(Timeout = 10_000)]
    public async Task Old_cleanup_queued_at_router_gate_cannot_remove_hypothetical_new_generation()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        var enabled = await harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(serverId), CancellationToken.None);
        var first = enabled.PublicConnectivityLease;
        var removes = harness.Gateway.Removes;
        var barrier = harness.Gateway.BlockNextDiscovery();
        var held = harness.Coordinator.CheckAsync(serverId, CancellationToken.None);
        await barrier.Entered.WaitAsync(TimeSpan.FromSeconds(2));
        _ = harness.Leases.Revoke(serverId, harness.Credential, first, "old exit snapshot");
        var oldCleanup = harness.Coordinator.DisableAsync(serverId,
            RouterOperationAuthority.LeaseCleanup(first, first.LifecycleEpoch,
                () => harness.Leases.IsLatestRevokedGeneration(first)), CancellationToken.None);
        var second = harness.Leases.Create(serverId, harness.Credential);

        barrier.Release();
        await held;
        _ = await oldCleanup;

        Assert.True(harness.Leases.IsCurrent(second));
        Assert.Equal(removes, harness.Gateway.Removes);
        Assert.Single(harness.Gateway.Table);
    }

    [Fact(Timeout = 10_000)]
    public async Task Exit_revocation_during_router_create_retains_exact_evidence_then_removes_late_success()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        var create = harness.Gateway.BlockNextCreate();
        var enabling = harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(serverId), CancellationToken.None);
        await create.Entered.WaitAsync(TimeSpan.FromSeconds(2));

        var exitEpoch = harness.Sessions.BeginApplicationExit(harness.Credential);
        var revoked = harness.PublicConnectivity.RevokeAllImmediately();
        var cleanup = harness.PublicConnectivity.CleanupRevokedAsync(
            revoked, exitEpoch, CancellationToken.None);
        create.Release();

        _ = await enabling;
        await cleanup;

        Assert.Equal(1, harness.Gateway.Creates);
        Assert.Equal(1, harness.Gateway.Removes);
        Assert.Empty(harness.Gateway.Table);
        var stored = (await harness.Store.GetRouterMappingAsync(serverId))!;
        Assert.False(stored.DirectInternetEnabled);
        Assert.False(stored.ConsentGranted);
        Assert.False(stored.HasActiveMapping);
        Assert.False(stored.RemovalPending);
    }

    [Fact]
    public async Task Removal_pending_never_renews_or_recreates_and_cleanup_only_retry_clears_it()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        var enabled = await harness.PublicConnectivity.EnableAsync(
            harness.EnableRequest(serverId), CancellationToken.None);
        var creates = harness.Gateway.Creates;
        harness.Gateway.RemoveFails = true;

        var pending = await harness.PublicConnectivity.DisableAsync(
            harness.ServerRequest(serverId, PublicConnectivityOperation.DisableRouterMapping,
                enabled.PublicConnectivityLease), CancellationToken.None);
        Assert.True(pending.RemovalPending);
        Assert.False((await harness.Store.GetRouterMappingAsync(serverId))!.DirectInternetEnabled);

        await harness.PublicConnectivity.ReconcileAllAsync(CancellationToken.None);
        Assert.Equal(creates, harness.Gateway.Creates);
        harness.Gateway.RemoveFails = false;
        var cleared = await harness.PublicConnectivity.RetryAsync(
            harness.ServerRequest(serverId, PublicConnectivityOperation.RetryRouterMapping),
            CancellationToken.None);

        Assert.False(cleared.RemovalPending);
        Assert.Equal(creates, harness.Gateway.Creates);
        Assert.Empty(harness.Gateway.Table);
    }

    [Fact]
    public async Task Fresh_agent_registry_treats_persisted_intent_as_cleanup_only_and_never_recreates_it()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        _ = await harness.PublicConnectivity.EnableAsync(harness.EnableRequest(serverId), CancellationToken.None);
        var creates = harness.Gateway.Creates;
        var restarted = harness.CreateRestartedPublicConnectivity();

        await restarted.ReconcileAllAsync(CancellationToken.None);

        var stored = (await harness.Store.GetRouterMappingAsync(serverId))!;
        Assert.False(stored.DirectInternetEnabled);
        Assert.False(stored.ConsentGranted);
        Assert.False(stored.HasActiveMapping);
        Assert.Equal(creates, harness.Gateway.Creates);
        Assert.Empty(harness.Gateway.Table);
    }

    [Fact]
    public async Task Unavailable_startup_storage_keeps_restoration_and_router_creation_inert()
    {
        var unavailableRoot = Path.Combine(root, "uninitialized-store");
        Directory.CreateDirectory(unavailableRoot);
        var paths = new AppDataPaths(unavailableRoot, Path.Combine(unavailableRoot, "servers"));
        await using var store = new ChunkPilotStore(paths);
        await using var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), NullLoggerFactory.Instance);
        var gateway = new ScriptedGateway();
        var service = new RouterMappingService(Harness.LoopbackView(), [gateway], new RouterMappingOptions(),
            NullLogger<RouterMappingService>.Instance);
        await using var router = new RouterMappingCoordinator(store, supervisor, service,
            NullLogger<RouterMappingCoordinator>.Instance);
        var sessions = new UiSessionAuthority(new Harness.AlwaysAliveObserver());
        var leases = new PublicConnectivityLeaseRegistry(sessions);
        await using var external = new ExternalReachabilityCoordinator(supervisor, router,
            new Harness.DisabledProbe(), NullLogger<ExternalReachabilityCoordinator>.Instance, leases: leases);
        var publicConnectivity = new PublicConnectivityCoordinator(sessions, leases, router, external,
            supervisor, store, NullLogger<PublicConnectivityCoordinator>.Instance);

        var result = await publicConnectivity.RecoverStaleExposureAsync(CancellationToken.None);

        Assert.False(result.StorageAvailable);
        Assert.False(result.MayRestoreOrdinaryStartup);
        Assert.Empty(result.SuppressedServerIds);
        Assert.Equal(0, gateway.Creates);
    }

    /// <summary>Enable and disable arriving together must serialize, not interleave.</summary>
    [Fact]
    public async Task Concurrent_enable_and_disable_serialize_to_one_consistent_result()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        harness.Gateway.OperationDelay = TimeSpan.FromMilliseconds(120);

        var enable = harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);
        var disable = harness.Coordinator.DisableAsync(serverId, CancellationToken.None);
        await Task.WhenAll(enable, disable);

        var stored = await harness.Store.GetRouterMappingAsync(serverId);
        // Whichever ran second decides. The two never overlapped, so the table matches the record.
        Assert.Equal(stored!.HasActiveMapping, harness.Gateway.Table.Count == 1);
        Assert.True(harness.Gateway.MaximumConcurrentOperations <= 1);
    }

    [Fact]
    public async Task Repeated_setup_requests_never_produce_two_mappings()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        harness.Gateway.OperationDelay = TimeSpan.FromMilliseconds(40);

        await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(_ => harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None)));

        Assert.Single(harness.Gateway.Table);
        Assert.True(harness.Gateway.MaximumConcurrentOperations <= 1);
    }

    [Fact]
    public async Task A_cancelled_setup_leaves_a_truthful_state_and_a_later_one_still_works()
    {
        await using var harness = await Harness.StartAsync(root);
        var serverId = await harness.AddServerAsync("Alpha", 25565);
        harness.Gateway.OperationDelay = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        var cancelled = await harness.Coordinator.EnableAsync(serverId, true, cancellation.Token);
        Assert.NotEqual(RouterMappingPhase.Active, cancelled.Phase);

        harness.Gateway.OperationDelay = TimeSpan.Zero;
        var second = await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, second.Phase);
        Assert.Single(harness.Gateway.Table);
    }

    [Fact]
    public async Task When_no_router_can_be_identified_the_state_says_so_and_nothing_is_claimed()
    {
        await using var harness = await Harness.StartAsync(root, withGateway: false);
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        var state = await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Unavailable, state.Phase);
        Assert.Equal(RouterMappingFailure.NoGatewayFound, state.Failure);
        Assert.False(state.HasRouterReportedAddress);
    }

    [Fact]
    public async Task A_shared_address_space_wan_address_is_classified_but_never_asserted_as_cgnat()
    {
        await using var harness = await Harness.StartAsync(root);
        harness.Gateway.ExternalAddress = "100.90.1.5";
        var serverId = await harness.AddServerAsync("Alpha", 25565);

        var state = await harness.Coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RoutableAddressClass.SharedAddressSpace, state.RouterReportedAddressClass);
        Assert.True(state.UpstreamNatSuspected);
        Assert.Equal("100.90.1.5:25565", state.RouterReportedEndpoint);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(ChunkPilotStore store, ServerSupervisor supervisor,
            RouterMappingCoordinator coordinator, ScriptedGateway gateway,
            ExternalReachabilityCoordinator external, UiSessionAuthority sessions,
            PublicConnectivityLeaseRegistry leases, PublicConnectivityCoordinator publicConnectivity,
            UiSessionCredential credential)
        {
            Store = store;
            Supervisor = supervisor;
            Coordinator = coordinator;
            Gateway = gateway;
            External = external;
            Sessions = sessions;
            Leases = leases;
            PublicConnectivity = publicConnectivity;
            Credential = credential;
        }

        public ChunkPilotStore Store { get; }
        public ServerSupervisor Supervisor { get; }
        public RouterMappingCoordinator Coordinator { get; }
        public ScriptedGateway Gateway { get; }
        public ExternalReachabilityCoordinator External { get; }
        public UiSessionAuthority Sessions { get; }
        public PublicConnectivityLeaseRegistry Leases { get; }
        public PublicConnectivityCoordinator PublicConnectivity { get; }
        public UiSessionCredential Credential { get; }

        public static async Task<Harness> StartAsync(string root, bool keepData = false, bool withGateway = true)
        {
            if (!keepData && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            Directory.CreateDirectory(root);
            var paths = new AppDataPaths(root, Path.Combine(root, "servers"));
            var store = new ChunkPilotStore(paths);
            await store.InitializeAsync();
            var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
                new MinecraftStatusClient(), new BackupService(paths, store), NullLoggerFactory.Instance);
            await supervisor.InitializeAsync();
            var gateway = new ScriptedGateway();
            var view = withGateway ? LoopbackView() : new EmptyView();
            var service = new RouterMappingService(view, [gateway], new RouterMappingOptions(),
                NullLogger<RouterMappingService>.Instance);
            var coordinator = new RouterMappingCoordinator(store, supervisor, service,
                NullLogger<RouterMappingCoordinator>.Instance);
            var sessions = new UiSessionAuthority(new AlwaysAliveObserver());
            var registration = sessions.Register(new UiSessionRegistrationRequest(100, 200));
            var credential = new UiSessionCredential
            {
                SessionId = registration.Session.SessionId,
                Capability = registration.Capability
            };
            var leases = new PublicConnectivityLeaseRegistry(sessions);
            var external = new ExternalReachabilityCoordinator(supervisor, coordinator,
                new DisabledProbe(), NullLogger<ExternalReachabilityCoordinator>.Instance, leases: leases);
            var publicConnectivity = new PublicConnectivityCoordinator(sessions, leases, coordinator,
                external, supervisor, store, NullLogger<PublicConnectivityCoordinator>.Instance);
            return new Harness(store, supervisor, coordinator, gateway, external, sessions, leases,
                publicConnectivity, credential);
        }

        public async Task<Guid> AddServerAsync(string name, int port)
        {
            var id = Guid.NewGuid();
            var definition = Definition(id, name, port);
            Directory.CreateDirectory(definition.RootPath);
            await Supervisor.ImportAsync(definition);
            return id;
        }

        public EnableRouterMappingRequest EnableRequest(Guid serverId) =>
            new(serverId, true)
            {
                Session = Credential,
                ConnectivityOperation = PublicConnectivityOperation.EnableRouterMapping
            };

        public ServerIdRequest ServerRequest(
            Guid serverId,
            PublicConnectivityOperation operation,
            PublicConnectivityLeaseIdentity? lease = null) =>
            new(serverId)
            {
                Session = Credential,
                Lease = lease ?? new PublicConnectivityLeaseIdentity(),
                ConnectivityOperation = operation
            };

        public PublicConnectivityCoordinator CreateRestartedPublicConnectivity()
        {
            var sessions = new UiSessionAuthority(new AlwaysAliveObserver());
            var leases = new PublicConnectivityLeaseRegistry(sessions);
            var external = new ExternalReachabilityCoordinator(Supervisor, Coordinator,
                new DisabledProbe(), NullLogger<ExternalReachabilityCoordinator>.Instance, leases: leases);
            return new PublicConnectivityCoordinator(sessions, leases, Coordinator, external,
                Supervisor, Store, NullLogger<PublicConnectivityCoordinator>.Instance);
        }

        private static ServerDefinition Definition(Guid id, string name, int port) => new()
        {
            Id = id,
            Name = name,
            RootPath = Path.Combine(Path.GetTempPath(), "chunkpilot-router-servers", id.ToString("N")),
            Port = port
        };

        public static IRouterNetworkView LoopbackView() => new FixedView();

        public async ValueTask DisposeAsync()
        {
            await External.DisposeAsync();
            await Coordinator.DisposeAsync();
            await Supervisor.DisposeAsync();
            await Store.DisposeAsync();
        }

        public sealed class AlwaysAliveObserver : IUiProcessObserver
        {
            public UiProcessLiveness Observe(int processId, long creationTicks) => UiProcessLiveness.Alive;
        }

        public sealed class DisabledProbe : IExternalReachabilityProbe
        {
            public bool IsConfigured => false;
            public string ConfigurationDetail => "Fixture probe disabled.";
            public Task<ExternalProbeResult> ProbeAsync(
                ExternalProbeRequest request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("The disabled fixture probe must not be called.");
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

        private sealed class EmptyView : IRouterNetworkView
        {
            public IReadOnlyList<RouterGatewayCandidate> Enumerate() => [];
        }
    }

    /// <summary>
    /// A controlled gateway with a readable table, so ownership and conflict decisions are exercised
    /// exactly as they would be against a UPnP router.
    /// </summary>
    private sealed class ScriptedGateway : IRouterMappingProvider
    {
        private int concurrent;
        private DiscoveryBlock? nextDiscoveryBlock;
        private DiscoveryBlock? nextCreateBlock;

        public ConcurrentDictionary<string, ExistingRouterMapping> Table { get; } = new(StringComparer.Ordinal);
        public int Discoveries { get; private set; }
        public int Creates { get; private set; }
        public int Removes { get; private set; }
        public int MaximumConcurrentOperations { get; private set; }
        public bool RemoveFails { get; set; }
        public string ExternalAddress { get; set; } = "203.0.113.4";
        public int LeaseSeconds { get; set; } = 3600;
        public TimeSpan OperationDelay { get; set; }

        public RouterMappingMechanism Mechanism => RouterMappingMechanism.UpnpIgd;
        public bool CanQueryExistingMappings => true;

        public DiscoveryBlock BlockNextDiscovery()
        {
            var block = new DiscoveryBlock();
            nextDiscoveryBlock = block;
            return block;
        }

        public DiscoveryBlock BlockNextCreate()
        {
            var block = new DiscoveryBlock();
            nextCreateBlock = block;
            return block;
        }

        public async Task<RouterDiscoveryResult> DiscoverAsync(
            RouterLanBinding binding, CancellationToken cancellationToken)
        {
            Discoveries++;
            if (Interlocked.Exchange(ref nextDiscoveryBlock, null) is { } block)
            {
                block.MarkEntered();
                await block.WaitAsync(cancellationToken);
            }
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return new RouterDiscoveryResult
                {
                    Mechanism = Mechanism,
                    Supported = true,
                    ExternalAddress = ExternalAddress,
                    ControlUrl = "http://192.168.1.1/ctl",
                    ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
                    Detail = "Scripted gateway."
                };
            }
            finally
            {
                Exit();
            }
        }

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult(Table.GetValueOrDefault(Key(transport, externalPort)));

        public async Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Interlocked.Exchange(ref nextCreateBlock, null) is { } block)
                {
                    block.MarkEntered();
                    // Some router stacks cannot cancel a request already handed to the gateway. This
                    // fixture deliberately returns that late success after lease revocation.
                    await block.WaitAsync(CancellationToken.None);
                }
                Creates++;
                Table[Key(request.Transport, request.ExternalPort)] = new ExistingRouterMapping
                {
                    ExternalPort = request.ExternalPort,
                    Transport = request.Transport,
                    InternalClient = binding.LocalAddress.ToString(),
                    InternalPort = request.InternalPort,
                    Description = request.Description,
                    LeaseSeconds = LeaseSeconds
                };
                return new RouterMappingOutcome
                {
                    Success = true,
                    Mechanism = Mechanism,
                    ExternalPort = request.ExternalPort,
                    LeaseSeconds = LeaseSeconds,
                    LeaseIsFinite = true,
                    ExternalAddress = ExternalAddress,
                    ControlUrl = discovery.ControlUrl,
                    ServiceType = discovery.ServiceType,
                    Detail = $"Scripted create for {request.Transport} {request.ExternalPort}."
                };
            }
            finally
            {
                Exit();
            }
        }

        public async Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (RemoveFails)
                    return RouterMappingOutcome.Failed(Mechanism, RouterMappingFailure.RemovalFailed,
                        "Scripted removal refusal.");
                Removes++;
                Table.TryRemove(Key(request.Transport, request.ExternalPort), out _);
                return new RouterMappingOutcome
                {
                    Success = true,
                    Mechanism = Mechanism,
                    ExternalPort = request.ExternalPort,
                    Detail = "Scripted remove."
                };
            }
            finally
            {
                Exit();
            }
        }

        private async Task EnterAsync(CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref concurrent);
            if (now > MaximumConcurrentOperations)
                MaximumConcurrentOperations = now;
            if (OperationDelay > TimeSpan.Zero)
                await Task.Delay(OperationDelay, cancellationToken).ConfigureAwait(false);
        }

        private void Exit() => Interlocked.Decrement(ref concurrent);

        private static string Key(MappingTransport transport, int port) =>
            $"{(transport == MappingTransport.Tcp ? "TCP" : "UDP")}:{port}";

        public sealed class DiscoveryBlock
        {
            private readonly TaskCompletionSource entered =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource released =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task Entered => entered.Task;
            public void MarkEntered() => entered.TrySetResult();
            public void Release() => released.TrySetResult();
            public Task WaitAsync(CancellationToken cancellationToken) =>
                released.Task.WaitAsync(cancellationToken);
        }
    }
}
