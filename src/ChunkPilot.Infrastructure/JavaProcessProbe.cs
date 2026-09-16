using System.Diagnostics;

namespace ChunkPilot.Infrastructure;

/// <summary>Queries a selected Java executable with bounded output and exact-owned child cleanup.</summary>
internal static class JavaProcessProbe
{
    public static async Task<AutomationProgramResult> RunAsync(
        ProcessStartInfo start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // The shared runner drains after its 64-KiB retention cap and proves the Job empty
            // even when the executable hangs, spawns a child, or the caller cancels discovery.
            return await OwnedAutomationProgramRunner.RunAsync(start, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The Java version probe exceeded its bounded deadline and was stopped.", exception);
        }
    }
}
