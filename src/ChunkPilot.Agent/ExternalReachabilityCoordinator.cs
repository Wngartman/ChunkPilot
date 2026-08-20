using System.Collections.Concurrent;
using ChunkPilot.Core;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

/// <summary>
/// Owns the only truthful answer ChunkPilot has to "can somebody outside reach this server?".
/// </summary>
/// <remarks>
/// <para>
/// Nothing here runs on its own. There is no timer, no worker, no lifecycle hook and no reconciliation
/// pass that reaches <see cref="CheckAsync"/>; the single caller is one deliberate button. Reading the
/// state, starting a server, stopping one, opening Overview, reconnecting the UI and restarting the
/// Agent all contact nothing outside this machine.
/// </para>
/// <para>
/// A result is evidence about one exact endpoint at one moment, so it is stored with the identity of
/// that endpoint and is compared against the live one every time the state is read. That comparison —
/// rather than a list of invalidation hooks — is what makes a stop, a restart, a new mapping, a port
/// change or a changed public address end a verified claim. Nothing is written to the database:
/// point-in-time evidence that did not survive the Agent has no business surviving a reboot either.
/// </para>
/// </remarks>
public sealed class ExternalReachabilityCoordinator : IAsyncDisposable
{
    private readonly ServerSupervisor supervisor;
    private readonly RouterMappingCoordinator routerMappings;
    private readonly IExternalReachabilityProbe probe;
    private readonly ILogger<ExternalReachabilityCoordinator> logger;
    private readonly ConcurrentDictionary<Guid, ServerRuntime> runtimes = new();
    private readonly TimeProvider clock;
    private readonly PublicConnectivityLeaseRegistry? leases;

    public ExternalReachabilityCoordinator(
        ServerSupervisor supervisor,
        RouterMappingCoordinator routerMappings,
        IExternalReachabilityProbe probe,
        ILogger<ExternalReachabilityCoordinator> logger,
        TimeProvider? clock = null,
        PublicConnectivityLeaseRegistry? leases = null)
    {
        this.supervisor = supervisor;
        this.routerMappings = routerMappings;
        this.probe = probe;
        this.logger = logger;
        this.clock = clock ?? TimeProvider.System;
        this.leases = leases;
    }

    /// <summary>Reads the authoritative state. Contacts nothing outside this machine.</summary>
    public async Task<ExternalReachabilityState> GetStateAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var router = await routerMappings.GetStateAsync(serverId, cancellationToken).ConfigureAwait(false);
        return Project(serverId, router, Runtime(serverId));
    }

    /// <summary>
    /// Runs one external check, because the user asked for exactly one.
    /// </summary>
    /// <remarks>
    /// Serialised per server: a second click while a check is running waits for the first and then
    /// observes its result rather than starting another. A result is discarded unless the endpoint it
    /// was made against is still the current one and the operation still owns this server.
    /// </remarks>
    public async Task<ExternalReachabilityState> CheckAsync(Guid serverId, CancellationToken cancellationToken)
        => await CheckAsync(serverId, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Runs a check bound to the current public-connectivity generation.</summary>
    public async Task<ExternalReachabilityState> CheckAsync(
        Guid serverId,
        PublicConnectivityLeaseIdentity? expectedLease,
        CancellationToken cancellationToken)
    {
        DemandCurrentLease(serverId, expectedLease);
        var runtime = Runtime(serverId);
        // Captured before the gate so a click that arrived while a check was already running can be
        // told apart from a deliberate second check. The first is answered by the running check's
        // result; only the second sends anything.
        var arrivedWhileBusy = runtime.Busy;
        var completionsOnArrival = runtime.Completions;
        await runtime.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (arrivedWhileBusy && runtime.Completions != completionsOnArrival)
                return Project(serverId, await CurrentRouterAsync(serverId).ConfigureAwait(false), runtime);

            var router = await routerMappings.GetStateAsync(serverId, cancellation.Token).ConfigureAwait(false);
            DemandCurrentLease(serverId, expectedLease);
            var blocker = Evaluate(serverId, router);
            if (blocker != ExternalReachabilityBlocker.None)
                // A missing prerequisite is not a failed check. Nothing is sent, and nothing about the
                // server is claimed; the surface explains which single thing is missing.
                return Project(serverId, router, runtime);

            var endpoint = ComposeEndpoint(serverId, router);
            runtime.OperationId = operationId;
            runtime.Cancellation = cancellation;
            runtime.Busy = true;

            var request = new ExternalProbeRequest
            {
                RequestId = ExternalReachabilityPolicy.NewRequestId(),
                ExpectedAddress = endpoint.PublicAddress,
                Port = endpoint.ExternalPort
            };

            ExternalProbeResult outcome;
            try
            {
                outcome = await probe.ProbeAsync(request, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outcome = ExternalProbeResult.Failed(ExternalProbeOutcome.Cancelled, request.RequestId,
                    "The external check was cancelled. Nothing on this computer or your router was changed.");
            }
            catch (Exception exception)
            {
                // A provider defect must never become a claim about the network.
                logger.LogWarning(exception, "The external reachability probe failed for {ServerId}.", serverId);
                outcome = ExternalProbeResult.Failed(ExternalProbeOutcome.ProbeError, request.RequestId,
                    "The external probe service could not complete this check.");
            }

            runtime.Busy = false;

            // A newer check took ownership while this one was in flight; its answer is the current one.
            if (runtime.OperationId != operationId)
                return Project(serverId, await CurrentRouterAsync(serverId).ConfigureAwait(false), runtime);

            // The world may have moved while the request was out. A result about an endpoint that no
            // longer exists is never written, so it can never be shown as current.
            var settledRouter = await CurrentRouterAsync(serverId).ConfigureAwait(false);
            if (!IsCurrentLease(serverId, expectedLease))
            {
                runtime.Result = null;
                return Project(serverId, settledRouter, runtime);
            }
            var settledEndpoint = ComposeEndpoint(serverId, settledRouter);
            if (settledEndpoint != endpoint)
            {
                runtime.Result = null;
                if (logger.IsEnabled(LogLevel.Information))
                    logger.LogInformation(
                        "External reachability result discarded for {ServerId}: the endpoint changed mid-check.",
                        serverId);
                return Project(serverId, settledRouter, runtime);
            }

            runtime.Result = new StoredResult(
                PhaseFor(outcome.Outcome),
                endpoint,
                outcome.ObservedAddress,
                outcome.ConnectMilliseconds,
                // Deliberately this machine's clock rather than the service's. "Verified 7:42 PM" is a
                // statement about when the owner checked, and it must not be something a remote
                // service can set.
                clock.GetUtcNow(),
                outcome.Detail);
            runtime.MarkCompleted();
            return Project(serverId, settledRouter, runtime);
        }
        catch (OperationCanceledException)
        {
            runtime.Busy = false;
            return Project(serverId, await CurrentRouterAsync(serverId).ConfigureAwait(false), runtime);
        }
        finally
        {
            runtime.Busy = false;
            if (ReferenceEquals(runtime.Cancellation, cancellation))
                runtime.Cancellation = null;
            runtime.Gate.Release();
        }
    }

    /// <summary>Cancels a check in flight. Deliberately takes no gate, so it cannot deadlock behind one.</summary>
    /// <remarks>
    /// Racing a check that is finishing is normal and harmless: the source it would have cancelled has
    /// already been disposed, and there is nothing left to stop.
    /// </remarks>
    public void Cancel(Guid serverId)
    {
        try
        {
            Runtime(serverId).Cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Immediately makes every result for one lease non-current and prevents an in-flight late success
    /// from publishing after revocation.
    /// </summary>
    public void Invalidate(Guid serverId)
    {
        var runtime = Runtime(serverId);
        runtime.Result = null;
        runtime.OperationId = Guid.NewGuid();
        Cancel(serverId);
    }

    public void InvalidateAll()
    {
        foreach (var serverId in runtimes.Keys)
            Invalidate(serverId);
    }

    private async Task<RouterMappingState> CurrentRouterAsync(Guid serverId)
    {
        try
        {
            return await routerMappings.GetStateAsync(serverId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Router state could not be re-read for {ServerId}.", serverId);
            return new RouterMappingState { ServerId = serverId };
        }
    }

    private ExternalReachabilityBlocker Evaluate(Guid serverId, RouterMappingState router)
    {
        if (leases is not null && !leases.Get(serverId).IsPresent)
            return ExternalReachabilityBlocker.RouterMappingInactive;
        var server = ServerOf(serverId);
        return ExternalReachabilityPolicy.Evaluate(
            probe.IsConfigured,
            server?.State ?? ServerState.Unknown,
            server?.Definition.Port ?? 0,
            router);
    }

    private ExternalReachabilityEndpoint ComposeEndpoint(Guid serverId, RouterMappingState router)
    {
        var server = ServerOf(serverId);
        var endpoint = ExternalReachabilityPolicy.ComposeEndpoint(
            serverId,
            server?.State ?? ServerState.Unknown,
            server?.Definition.Port ?? 0,
            server?.RootProcessId,
            server?.StartedAt,
            router);
        var lease = leases?.Get(serverId) ?? new PublicConnectivityLeaseIdentity();
        return endpoint with
        {
            PublicConnectivityLeaseId = lease.LeaseId,
            PublicConnectivityLeaseGeneration = lease.Generation
        };
    }

    private void DemandCurrentLease(Guid serverId, PublicConnectivityLeaseIdentity? expected)
    {
        if (expected is null)
            return;
        if (!IsCurrentLease(serverId, expected))
            throw new UnauthorizedAccessException(
                "The external check was refused because its public-connectivity lease is no longer current.");
    }

    private bool IsCurrentLease(Guid serverId, PublicConnectivityLeaseIdentity? expected)
    {
        if (expected is null)
            return true;
        var current = leases?.Get(serverId) ?? new PublicConnectivityLeaseIdentity();
        return current.IsPresent && current.LeaseId == expected.LeaseId &&
               current.Generation == expected.Generation &&
               current.LifecycleEpoch == expected.LifecycleEpoch &&
               current.SessionId == expected.SessionId;
    }

    /// <summary>Turns the live endpoint and whatever result exists into the one state the App renders.</summary>
    private ExternalReachabilityState Project(Guid serverId, RouterMappingState router, ServerRuntime runtime)
    {
        var blocker = Evaluate(serverId, router);
        var endpoint = ComposeEndpoint(serverId, router);
        var result = runtime.Result;
        var busy = runtime.Busy;

        var phase =
            busy ? ExternalReachabilityPhase.Checking
            : result is null
                ? blocker == ExternalReachabilityBlocker.None
                    ? ExternalReachabilityPhase.NotChecked
                    : ExternalReachabilityPhase.Ineligible
                : result.Endpoint == endpoint
                    ? result.Phase
                    : ExternalReachabilityPhase.Stale;

        return new ExternalReachabilityState
        {
            ServerId = serverId,
            Phase = phase,
            Blocker = blocker,
            ProbeConfigured = probe.IsConfigured,
            Endpoint = endpoint,
            CheckedEndpoint = result?.Endpoint ?? new ExternalReachabilityEndpoint(),
            ObservedAddress = result?.ObservedAddress ?? "",
            RouterReportedAddress = router.RouterReportedExternalAddress,
            Port = result?.Endpoint.ExternalPort ?? endpoint.ExternalPort,
            ConnectMilliseconds = result?.ConnectMilliseconds ?? 0,
            CheckedAt = result?.CheckedAt,
            Busy = busy,
            OperationId = runtime.OperationId,
            LastOperationDetail = result?.Detail.Length > 0 ? result.Detail : probe.ConfigurationDetail
        };
    }

    /// <summary>
    /// The provider's outcome, mapped once. Only <see cref="ExternalProbeOutcome.Reachable"/> and
    /// <see cref="ExternalProbeOutcome.Unreachable"/> are statements about the network; every other
    /// outcome is about the check, and none of them may read as an unreachable server.
    /// </summary>
    private static ExternalReachabilityPhase PhaseFor(ExternalProbeOutcome outcome) => outcome switch
    {
        ExternalProbeOutcome.Reachable => ExternalReachabilityPhase.Reachable,
        ExternalProbeOutcome.Unreachable => ExternalReachabilityPhase.Unreachable,
        ExternalProbeOutcome.SourceMismatch => ExternalReachabilityPhase.SourceAddressMismatch,
        ExternalProbeOutcome.UnsupportedAddressFamily => ExternalReachabilityPhase.UnsupportedAddressFamily,
        ExternalProbeOutcome.RateLimited => ExternalReachabilityPhase.RateLimited,
        ExternalProbeOutcome.Cancelled => ExternalReachabilityPhase.Cancelled,
        _ => ExternalReachabilityPhase.ProbeUnavailable
    };

    private ManagedServer? ServerOf(Guid serverId)
    {
        try
        {
            return supervisor.Get(serverId);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private ServerRuntime Runtime(Guid serverId) => runtimes.GetOrAdd(serverId, _ => new ServerRuntime());

    public ValueTask DisposeAsync()
    {
        foreach (var runtime in runtimes.Values)
        {
            try
            {
                runtime.Cancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            runtime.Gate.Dispose();
        }
        runtimes.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>One check's evidence, bound to the endpoint it was made against.</summary>
    private sealed record StoredResult(
        ExternalReachabilityPhase Phase,
        ExternalReachabilityEndpoint Endpoint,
        string ObservedAddress,
        int ConnectMilliseconds,
        DateTimeOffset CheckedAt,
        string Detail);

    /// <summary>In-memory only. External reachability is never persisted.</summary>
    private sealed class ServerRuntime
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Guid OperationId { get; set; }
        public CancellationTokenSource? Cancellation { get; set; }

        private volatile bool busy;
        public bool Busy
        {
            get => busy;
            set => busy = value;
        }

        private StoredResult? result;
        public StoredResult? Result
        {
            get => Volatile.Read(ref result);
            set => Volatile.Write(ref result, value);
        }

        private int completions;

        /// <summary>How many checks have finished. Only ever compared, never interpreted.</summary>
        public int Completions => Volatile.Read(ref completions);

        public void MarkCompleted() => Interlocked.Increment(ref completions);
    }
}
