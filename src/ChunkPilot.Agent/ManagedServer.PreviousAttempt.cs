namespace ChunkPilot.Agent;

public sealed partial class ManagedServer
{
    internal static async Task WaitForPreviousAttemptAsync(Task? monitor, Task? stdout, Task? stderr,
        CancellationToken cancellationToken, TimeSpan? deadline = null)
    {
        var tasks = new[] { monitor, stdout, stderr }.OfType<Task>().ToArray();
        if (tasks.Length == 0) return;
        // Inherited redirected handles can remain open after a launcher exits. They must not wedge
        // a subsequent Start under the lifecycle gate, nor permit an overlapping process attempt.
        await Task.WhenAll(tasks).WaitAsync(deadline ?? TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
    }
}
