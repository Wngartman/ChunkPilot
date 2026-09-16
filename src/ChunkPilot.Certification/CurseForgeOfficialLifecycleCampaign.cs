using System.Diagnostics;

namespace ChunkPilot.Certification;

internal sealed partial class CurseForgeRuntimeCertificationSession
{
    /// <summary>Reopens only the same freshly created server after its first exact stop is proven.</summary>
    internal static async Task ExerciseFreshOfficialLifecyclesAsync(
        CurseForgeRuntimeCertificationReport report,
        Func<CancellationToken, Task> exerciseLifecycle,
        Func<CancellationToken, Task<CertificationCleanupPostconditionsEvidence>> verifyCleanup,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(exerciseLifecycle);
        ArgumentNullException.ThrowIfNull(verifyCleanup);
        for (var cycleIndex = 0; cycleIndex < 2; cycleIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cycle = cycleIndex == 0 ? "initial" : "restart";
            var evidenceStart = report.Lifecycle.Count;
            var watch = Stopwatch.StartNew();
            var passed = false;
            try
            {
                await exerciseLifecycle(cancellationToken).ConfigureAwait(false);
                var cleanup = await verifyCleanup(cancellationToken).ConfigureAwait(false);
                if (!ServerStoppedPostconditionsPassed(cleanup))
                    throw new InvalidOperationException(
                        "The exact server process, listener, or staging cleanup was not proven; no further launch is permitted.");
                passed = true;
            }
            finally
            {
                for (var index = evidenceStart; index < report.Lifecycle.Count; index++)
                    report.Lifecycle[index] = report.Lifecycle[index] with { Cycle = cycle };
                report.Steps.Add(new CertificationStepEvidence
                {
                    Name = cycle + " server lifecycle and cleanup",
                    Status = passed ? "PASSED" : cancellationToken.IsCancellationRequested ? "CANCELLED" : "FAILED",
                    Detail = passed
                        ? "The same exact server completed start, loopback status, save-first stop, and owned cleanup."
                        : "This lifecycle cycle did not complete every required lifecycle and owned-cleanup check.",
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
                });
            }
        }
    }
}
