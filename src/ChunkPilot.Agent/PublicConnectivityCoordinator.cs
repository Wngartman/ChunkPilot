using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

/// <summary>
/// Production composition for public connectivity. The registry owns authority; the router and probe
/// coordinators are executors and never manufacture a lease from persisted intent.
/// </summary>
public sealed class PublicConnectivityCoordinator
{
    private static readonly TimeSpan CleanupDeadline = TimeSpan.FromSeconds(20);
    private readonly UiSessionAuthority sessions;
    private readonly PublicConnectivityLeaseRegistry leases;
    private readonly RouterMappingCoordinator router;
    private readonly ExternalReachabilityCoordinator external;
    private readonly ServerSupervisor supervisor;
    private readonly ChunkPilotStore store;
    private readonly ILogger<PublicConnectivityCoordinator> logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();

    public PublicConnectivityCoordinator(
        UiSessionAuthority sessions,
        PublicConnectivityLeaseRegistry leases,
        RouterMappingCoordinator router,
        ExternalReachabilityCoordinator external,
        ServerSupervisor supervisor,
        ChunkPilotStore store,
        ILogger<PublicConnectivityCoordinator> logger)
    {
        this.sessions = sessions;
        this.leases = leases;
        this.router = router;
        this.external = external;
        this.supervisor = supervisor;
        this.store = store;
        this.logger = logger;
    }

    public async Task<RouterMappingState> GetRouterStateAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandSession(request.Session, request.ConnectivityOperation,
            PublicConnectivityOperation.ReadRouterState, "Reading Direct internet state");
        return Decorate(await router.GetStateAsync(request.ServerId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RouterMappingState> CheckRouterAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandSession(request.Session, request.ConnectivityOperation,
            PublicConnectivityOperation.CheckRouterCapability, "Checking router capability");
        return Decorate(await router.CheckAsync(request.ServerId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<RouterMappingState> EnableAsync(
        EnableRouterMappingRequest request,
        CancellationToken cancellationToken)
    {
        DemandSession(request.Session, request.ConnectivityOperation,
            PublicConnectivityOperation.EnableRouterMapping, "Enabling Direct internet");
        if (request.ExpectedLease.IsPresent)
            throw new UnauthorizedAccessException(
                "Enabling Direct internet was refused because the request replayed an existing lease generation.");

        var gate = Gate(request.ServerId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PublicConnectivityLeaseIdentity? created = null;
        try
        {
            DemandSession(request.Session, request.ConnectivityOperation,
                PublicConnectivityOperation.EnableRouterMapping, "Enabling Direct internet");
            if (leases.Get(request.ServerId).IsPresent)
                throw new UnauthorizedAccessException(
                    "Enabling Direct internet was refused because a lease already exists for this server.");

            // Stale durable evidence is cleanup-only. A fresh generation cannot be minted until an old
            // exact-owned mapping is gone or truthfully remains pending.
            var previousRecord = await store.GetRouterMappingAsync(request.ServerId, cancellationToken)
                .ConfigureAwait(false);
            var previous = await router.GetStateAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
            if (previous.Enabled || previous.Phase == RouterMappingPhase.Active || previous.RemovalPending)
            {
                var stale = previousRecord ?? new RouterMappingRecord { ServerId = request.ServerId };
                previous = await router.DisableAsync(request.ServerId, StaleCleanupAuthority(stale), cancellationToken)
                    .ConfigureAwait(false);
                if (previous.RemovalPending)
                    throw new InvalidOperationException(
                        "The previous exact-owned router mapping is still pending removal. Retry cleanup before enabling a new lease.");
            }

            created = leases.Create(request.ServerId, request.Session);
            var state = await router.EnableAsync(request.ServerId, request.ConsentGranted,
                    ExposureAuthority(created), cancellationToken)
                .ConfigureAwait(false);
            return Decorate(state);
        }
        catch
        {
            if (created is not null)
            {
                try
                {
                    var revoked = leases.Revoke(request.ServerId, request.Session, created,
                        "Rolling back failed Direct internet setup");
                    using var cleanupTimeout = new CancellationTokenSource(CleanupDeadline);
                    _ = await router.DisableAsync(request.ServerId, ManualCleanupAuthority(revoked),
                        cleanupTimeout.Token).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    // A newer generation can only be installed after this per-server gate is released.
                }
                catch (Exception cleanupException) when (cleanupException is not OutOfMemoryException)
                {
                    logger.LogWarning(cleanupException,
                        "Failed Direct internet setup left cleanup evidence for {ServerId}.", request.ServerId);
                }
                external.Invalidate(request.ServerId);
            }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RouterMappingState> DisableAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandLease(request, PublicConnectivityOperation.DisableRouterMapping, "Disabling Direct internet");
        var gate = Gate(request.ServerId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var revoked = leases.Revoke(request.ServerId, request.Session, request.Lease,
                "Disabling Direct internet");
            external.Invalidate(request.ServerId);
            router.Cancel(request.ServerId);
            return Decorate(await router.DisableAsync(request.ServerId, ManualCleanupAuthority(revoked),
                cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RouterMappingState> RetryAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandSession(request.Session, request.ConnectivityOperation,
            PublicConnectivityOperation.RetryRouterMapping, "Retrying Direct internet cleanup");
        var gate = Gate(request.ServerId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = leases.Get(request.ServerId);
            if (current.IsPresent)
                leases.Demand(request.ServerId, request.Session, request.Lease, "Retrying Direct internet");
            var state = current.IsPresent
                ? await router.SynchronizeAsync(request.ServerId, ExposureAuthority(current), cancellationToken)
                    .ConfigureAwait(false)
                : await DisableStaleAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
            return Decorate(state);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RouterMappingState> CancelRouterAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandLease(request, PublicConnectivityOperation.CancelRouterMapping, "Cancelling router setup");
        router.Cancel(request.ServerId);
        return Decorate(await router.GetStateAsync(request.ServerId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<ExternalReachabilityState> GetExternalStateAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandSession(request.Session, request.ConnectivityOperation,
            PublicConnectivityOperation.ReadExternalReachability, "Reading external reachability");
        return await external.GetStateAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExternalReachabilityState> CheckExternalAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandLease(request, PublicConnectivityOperation.CheckExternalReachability,
            "Checking external reachability");
        return await external.CheckAsync(request.ServerId, request.Lease, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExternalReachabilityState> CancelExternalAsync(
        ServerIdRequest request,
        CancellationToken cancellationToken)
    {
        DemandLease(request, PublicConnectivityOperation.CancelExternalReachability,
            "Cancelling external reachability");
        external.Cancel(request.ServerId);
        return await external.GetStateAsync(request.ServerId, cancellationToken).ConfigureAwait(false);
    }

    public void DemandLifecycleAuthority(
        Guid serverId,
        UiSessionCredential session,
        PublicConnectivityLeaseIdentity lease,
        PublicConnectivityOperation presented,
        PublicConnectivityOperation expected,
        string action)
    {
        DemandSession(session, presented, expected, action);
        if (leases.HasActive(serverId))
            leases.Demand(serverId, session, lease, action);
    }

    public void DemandAllLifecycleAuthority(
        AllServersLifecycleRequest request,
        PublicConnectivityOperation expected,
        string action)
    {
        DemandSession(request.Session, request.ConnectivityOperation, expected, action);
        var presented = request.Leases.ToDictionary(lease => lease.ServerId);
        foreach (var definition in supervisor.Definitions)
        {
            var current = leases.Get(definition.Id);
            if (!current.IsPresent)
                continue;
            if (!presented.TryGetValue(definition.Id, out var lease))
                throw new UnauthorizedAccessException(
                    $"{action} was refused because server {definition.Id} has no presented lease generation.");
            leases.Demand(definition.Id, request.Session, lease, action);
        }
    }

    public async Task SynchronizeAfterLifecycleAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var gate = Gate(serverId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var lease = leases.Get(serverId);
            _ = lease.IsPresent
                ? await router.SynchronizeAsync(serverId, ExposureAuthority(lease), cancellationToken)
                    .ConfigureAwait(false)
                : await DisableStaleAsync(serverId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task ReconcileAllAsync(CancellationToken cancellationToken)
    {
        var records = await store.GetRouterMappingsAsync(cancellationToken).ConfigureAwait(false);
        var known = supervisor.Definitions.Select(definition => definition.Id).ToHashSet();
        foreach (var record in records)
        {
            var gate = Gate(record.ServerId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var lease = leases.Get(record.ServerId);
                var state = lease.IsPresent
                    ? await router.SynchronizeAsync(record.ServerId, ExposureAuthority(lease), cancellationToken)
                        .ConfigureAwait(false)
                    : await router.DisableAsync(record.ServerId, StaleCleanupAuthority(record), cancellationToken)
                        .ConfigureAwait(false);
                if (!known.Contains(record.ServerId) && !state.RemovalPending &&
                    state.Phase != RouterMappingPhase.Active)
                    await store.DeleteRouterMappingAsync(record.ServerId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The lease changed while this queued pass waited. Its newer owner will reconcile.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Revokes every lease and probe synchronously before slow cleanup begins.</summary>
    public IReadOnlyList<PublicConnectivityLeaseIdentity> RevokeAllImmediately()
    {
        var revoked = leases.RevokeAll();
        external.InvalidateAll();
        foreach (var lease in revoked)
            router.Cancel(lease.ServerId);
        return revoked;
    }

    public async Task CleanupRevokedAsync(
        IReadOnlyList<PublicConnectivityLeaseIdentity> revoked,
        long exitEpoch,
        CancellationToken cancellationToken)
    {
        foreach (var lease in revoked)
        {
            var gate = Gate(lease.ServerId);
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = await router.DisableAsync(lease.ServerId, ExitCleanupAuthority(lease, exitEpoch),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Stale exit cleanup was rejected for {ServerId}/{Generation}.",
                    lease.ServerId, lease.Generation);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>
    /// A new Agent starts with no leases. Persisted intent is cleanup evidence only, and a managed
    /// listener associated with it is stopped rather than inherited.
    /// </summary>
    public async Task<StaleExposureRecoveryResult> RecoverStaleExposureAsync(
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RouterMappingRecord> records;
        try
        {
            records = await store.GetRouterMappingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception,
                "Stale public exposure evidence could not be read. Startup restoration is disabled for this Agent run.");
            return new StaleExposureRecoveryResult(false, false, new HashSet<Guid>());
        }
        var staleServerIds = records
            .Where(record => record.DirectInternetEnabled || record.ConsentGranted ||
                             record.HasActiveMapping || record.RemovalPending)
            .Select(record => record.ServerId)
            .Distinct()
            .ToArray();
        if (staleServerIds.Length == 0)
            return new StaleExposureRecoveryResult(true, true, new HashSet<Guid>());

        _ = RevokeAllImmediately();
        var stop = await supervisor.StopServersAsync(
            staleServerIds, "Recovered stale public connectivity", escalateOnFailure: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var stillAlive = supervisor.ExactOwnedProcessesStillAlive(staleServerIds);

        using var cleanupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cleanupTimeout.CancelAfter(CleanupDeadline);
        try
        {
            foreach (var record in records.Where(record => staleServerIds.Contains(record.ServerId)))
            {
                var gate = Gate(record.ServerId);
                await gate.WaitAsync(cleanupTimeout.Token).ConfigureAwait(false);
                try
                {
                    _ = await router.DisableAsync(record.ServerId, StaleCleanupAuthority(record), cleanupTimeout.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cleanupTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Stale router cleanup reached its bounded deadline; exact evidence remains pending and associated listeners were stopped.");
        }
        if (stillAlive.Count > 0 || stop.Values.Any(result => !result.Success))
        {
            if (logger.IsEnabled(LogLevel.Critical))
            {
                logger.LogCritical(
                    "Startup restoration remains disabled because stale-exposure listeners did not reach a proven terminal state: {ServerIds}",
                    string.Join(", ", stillAlive));
            }
            return new StaleExposureRecoveryResult(true, false, staleServerIds.ToHashSet());
        }
        return new StaleExposureRecoveryResult(true, true, staleServerIds.ToHashSet());
    }

    private RouterMappingState Decorate(RouterMappingState state) =>
        state with { PublicConnectivityLease = leases.Get(state.ServerId) };

    private async Task<RouterMappingState> DisableStaleAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var record = await store.GetRouterMappingAsync(serverId, cancellationToken).ConfigureAwait(false)
                     ?? new RouterMappingRecord { ServerId = serverId };
        return await router.DisableAsync(serverId, StaleCleanupAuthority(record), cancellationToken)
            .ConfigureAwait(false);
    }

    private RouterOperationAuthority ExposureAuthority(PublicConnectivityLeaseIdentity lease) =>
        RouterOperationAuthority.Exposure(lease, () => leases.IsCurrent(lease),
            () => leases.IsLatestRevokedGeneration(lease));

    private RouterOperationAuthority ManualCleanupAuthority(PublicConnectivityLeaseIdentity lease) =>
        RouterOperationAuthority.LeaseCleanup(lease, lease.LifecycleEpoch,
            () => leases.IsLatestRevokedGeneration(lease));

    private RouterOperationAuthority ExitCleanupAuthority(
        PublicConnectivityLeaseIdentity lease,
        long exitEpoch) =>
        RouterOperationAuthority.LeaseCleanup(lease, exitEpoch,
            () => leases.IsExactRevokedGeneration(lease, exitEpoch));

    private RouterOperationAuthority StaleCleanupAuthority(RouterMappingRecord record)
    {
        var epoch = sessions.LifecycleEpoch;
        return RouterOperationAuthority.StaleCleanup(record, epoch,
            () => !sessions.ApplicationExitStarted && sessions.LifecycleEpoch == epoch &&
                  leases.HasNoLease(record.ServerId));
    }

    private void DemandLease(ServerIdRequest request, PublicConnectivityOperation expected, string action)
    {
        DemandSession(request.Session, request.ConnectivityOperation, expected, action);
        leases.Demand(request.ServerId, request.Session, request.Lease, action);
    }

    private void DemandSession(
        UiSessionCredential credential,
        PublicConnectivityOperation presented,
        PublicConnectivityOperation expected,
        string action)
    {
        if (presented != expected)
            throw new UnauthorizedAccessException(
                $"{action} was refused because the capability was issued for a different operation.");
        sessions.Demand(credential, action);
    }

    private SemaphoreSlim Gate(Guid serverId) => gates.GetOrAdd(serverId, static _ => new SemaphoreSlim(1, 1));
}

public sealed record StaleExposureRecoveryResult(
    bool StorageAvailable,
    bool MayRestoreOrdinaryStartup,
    IReadOnlySet<Guid> SuppressedServerIds);
