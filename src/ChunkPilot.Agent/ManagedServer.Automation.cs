using System.Diagnostics;
using ChunkPilot.Core;

namespace ChunkPilot.Agent;

public sealed partial class ManagedServer
{
    public async Task<OperationResult> StopKnownEmptyAsync(ServerSnapshot original,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (!AutomationObservationPolicy.KnownEmpty(Snapshot(0), original, DateTimeOffset.UtcNow))
                return OperationResult.Fail("Empty-server stop cancelled: fresh empty-player evidence for the original process is no longer available.");
            lastIntent = LifecycleIntentKind.ManualStop;
            Interlocked.Increment(ref lifecycleGeneration);
            var result = await StopCoreAsync(saveFirst: true, cancellationToken).ConfigureAwait(false);
            await PersistStopObservationAsync().ConfigureAwait(false);
            await RecordAsync("Stop known-empty server", result, timer, "Automation", cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally { operationGate.Release(); }
    }
}
