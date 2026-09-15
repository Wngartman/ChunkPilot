using System.Diagnostics;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// One server attempt born inside a private Windows Job. Descendant ownership survives launcher
/// exit, intermediate-parent exit, and PID recycling; closing the owner also closes its Job.
/// </summary>
public sealed class OwnedServerProcess : IDisposable
{
    private readonly WindowsStagedProcessJob job;
    private readonly WindowsStagedProcess owned;

    private OwnedServerProcess(WindowsStagedProcessJob job, WindowsStagedProcess owned)
    {
        this.job = job;
        this.owned = owned;
    }

    public static OwnedServerProcess Start(ProcessStartInfo startInfo)
    {
        var job = WindowsStagedProcessJob.Create();
        try { return new OwnedServerProcess(job, job.StartApprovedServer(startInfo)); }
        catch { job.Dispose(); throw; }
    }

    public Process Process => owned.Process;
    public StreamWriter StandardInput => owned.StandardInput;
    public StreamReader StandardOutput => owned.StandardOutput;
    public StreamReader StandardError => owned.StandardError;
    public bool HasLiveProcesses => job.ActiveProcessCount != 0;
    public void Terminate() => job.Terminate();
    public Task<bool> WaitForEmptyAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        job.WaitForEmptyAsync(timeout, cancellationToken);

    public void Dispose()
    {
        // Closing the Job first releases any inherited redirected handles before stream disposal.
        job.Dispose();
        owned.Dispose();
    }
}
