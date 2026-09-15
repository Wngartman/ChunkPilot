using System.Diagnostics;
using ChunkPilot.Core;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

public sealed partial class ManagedServer
{
    private readonly object startupProgressGate = new();
    private ServerStartupProgress? startupProgress;

    private Guid BeginStartupProgress(ServerStartupStage stage)
    {
        lock (startupProgressGate)
        {
            var now = DateTimeOffset.UtcNow;
            startupProgress = new()
            {
                ServerId = Definition.Id, AttemptId = Guid.NewGuid(), Stage = stage,
                Title = ServerStartupProgressPolicy.Title(stage), StartedAt = now, UpdatedAt = now,
                IsActive = true,
                Detail = "Progress is reported by the Agent and server output; no completion time is estimated."
            };
            return startupProgress.AttemptId;
        }
    }

    private void UpdateStartupProgress(Guid attemptId, ServerStartupStage stage,
        string detail = "", int? processId = null)
    {
        lock (startupProgressGate)
        {
            if (startupProgress is not { } current || current.AttemptId != attemptId)
                return;
            startupProgress = current with
            {
                Stage = stage, Title = ServerStartupProgressPolicy.Title(stage),
                Detail = detail, ProcessId = processId ?? current.ProcessId,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsActive = stage is not ServerStartupStage.Ready and not ServerStartupStage.Failed and not ServerStartupStage.Cancelled
            };
        }
    }

    private void ObserveStartupOutput(TaskCompletionSource<bool> attemptReadiness, string line)
    {
        lock (startupProgressGate)
        {
            if (!ReferenceEquals(readiness, attemptReadiness) || startupProgress is not { IsActive: true } current)
                return;
            if (current.Stage is ServerStartupStage.Preflight or ServerStartupStage.RestartSaving or
                ServerStartupStage.RestartStopping or ServerStartupStage.RestartDelay)
                return;
            var now = DateTimeOffset.UtcNow;
            var stage = ServerStartupProgressPolicy.StageFromOutput(line);
            // No per-line snapshot churn. A new explicit stage or one heartbeat per second suffices.
            if (stage is null && current.LastOutputAt is { } previous && now - previous < TimeSpan.FromSeconds(1))
                return;
            startupProgress = current with
            {
                Stage = stage ?? current.Stage,
                Title = stage is { } observed ? ServerStartupProgressPolicy.Title(observed) : current.Title,
                LastOutputAt = now, UpdatedAt = now,
                Detail = stage is not null
                    ? "An explicit phase marker was observed in this process attempt's console output."
                    : current.Detail
            };
        }
    }

    private ServerStartupProgress? StartupProgressSnapshot()
    {
        lock (startupProgressGate)
            return startupProgress;
    }

    private async Task ObserveLateReadinessAsync(Process owned, TaskCompletionSource<bool> attemptReadiness,
        Guid progressAttempt, int expectedGeneration)
    {
        try
        {
            // The existing process monitor completes this promise on exit. No new poller or process
            // is created, and an Agent lifetime cancellation releases this wait.
            await attemptReadiness.Task.WaitAsync(lifetime.Token).ConfigureAwait(false);
            await operationGate.WaitAsync(lifetime.Token).ConfigureAwait(false);
            try
            {
                lock (operationCancellationGate)
                {
                    lock (processGate)
                    {
                        if (!ReferenceEquals(process, owned) || !ReferenceEquals(readiness, attemptReadiness) ||
                            owned.HasExited || HasDetachedProcess || State != ServerState.Unresponsive ||
                            expectedGeneration != Volatile.Read(ref lifecycleGeneration) || intentionalStop || applicationExitRequested)
                            return;
                    }
                    lifecycle.TransitionTo(ServerState.Running);
                    hasStartedSuccessfully = true;
                    lastStartReachedReadiness = true;
                }
                lock (failureGate)
                {
                    startupFailure = null;
                    lastError = "";
                }
                if (lastIntent is LifecycleIntentKind.SafeRestart or LifecycleIntentKind.ScheduledRestart or
                    LifecycleIntentKind.UpdateRestart or LifecycleIntentKind.CrashRecovery)
                    lastIntent = LifecycleIntentKind.None;
                UpdateStartupProgress(progressAttempt, ServerStartupStage.Ready,
                    "This exact process reported readiness after the initial startup deadline. Internet reachability is not implied.");
                await store.SetRunningStateAsync(Definition.Id, CurrentAutostartMode,
                    true, lastIntent, CancellationToken.None).ConfigureAwait(false);
                await ApplyPendingGamerulesAsync().ConfigureAwait(false);
            }
            finally { operationGate.Release(); }
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or
                                          System.ComponentModel.Win32Exception or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            // Process exit, replacement, shutdown, and inaccessible evidence never imply readiness.
            // Storage errors do not turn an already observed server-ready event into an invented stop.
            if (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
                logger.LogWarning(exception, "Could not persist late readiness for {Server}", Definition.Name);
        }
    }
}
