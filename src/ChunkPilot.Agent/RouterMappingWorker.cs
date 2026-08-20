using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

/// <summary>
/// Keeps established router mappings in step with reality: renews finite router lifetimes only while
/// a current public-connectivity lease authorizes it, withdraws exposure once authority or the server
/// ends, and treats Agent-restart evidence as cleanup-only.
/// </summary>
/// <remarks>
/// Persisted intent is not authority. The composed coordinator gates creation and renewal on an
/// in-memory per-server lease minted by an authenticated, explicit enable action.
/// </remarks>
public sealed class RouterMappingWorker
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly PublicConnectivityCoordinator coordinator;
    private readonly ILogger<RouterMappingWorker> logger;

    public RouterMappingWorker(PublicConnectivityCoordinator coordinator, ILogger<RouterMappingWorker> logger)
    {
        this.coordinator = coordinator;
        this.logger = logger;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // Reconcile once at startup so stale exact-owned evidence is removed or retained truthfully as
        // pending. A new Agent owns no public lease, so this pass cannot recreate yesterday's mapping.
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await coordinator.ReconcileAllAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Router mapping reconciliation pass failed.");
        }
    }
}
