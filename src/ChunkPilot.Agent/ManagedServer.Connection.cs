using System.Diagnostics;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed partial class ManagedServer
{
    private readonly SemaphoreSlim connectionObservationGate = new(1, 1);
    private volatile ServerConnectionEvidence connectionEvidence = new();
    private DateTimeOffset lastConnectionObservation;

    /// <summary>Bounded on-demand sampling in the Agent, never in a UI snapshot mapper or per frame.</summary>
    public async Task RefreshConnectionEvidenceAsync(CancellationToken cancellationToken = default)
    {
        if (!await connectionObservationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (DateTimeOffset.UtcNow - lastConnectionObservation < TimeSpan.FromSeconds(5)) return;
            var definition = Definition;
            var baseline = connectionEvidence;
            Process? owned;
            lock (processGate) owned = process;
            var saved = await ServerConnectionObserver.ReadSavedAsync(definition, new SafeFileService(paths), cancellationToken).ConfigureAwait(false);
            var listener = owned is null ? new ServerListenerEvidence() : ServerConnectionObserver.Observe(owned, definition.Port);
            lock (processGate)
            {
                // A delayed read may not replace the launch baseline or publish an old attempt's listener.
                if (!ReferenceEquals(process, owned) || !ReferenceEquals(Definition, definition) ||
                    !ReferenceEquals(connectionEvidence, baseline)) return;
                connectionEvidence = baseline with { Saved = saved, Listener = listener };
                lastConnectionObservation = DateTimeOffset.UtcNow;
            }
        }
        finally { connectionObservationGate.Release(); }
    }
}
