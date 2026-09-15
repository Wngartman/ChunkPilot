using System.Diagnostics;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record AutomationProgramResult(int ExitCode, string Output, string Error);

/// <summary>Runs an already-approved structured command in an exact-owned, kill-on-close process Job.</summary>
public static class OwnedAutomationProgramRunner
{
    public static async Task<AutomationProgramResult> RunAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        ChildProcessEnvironmentPolicy.Apply(start);
        CurseForgeCredentialEnvironment.RemoveFromChild(start);
        cancellationToken.ThrowIfCancellationRequested();
        using var job = WindowsStagedProcessJob.Create();
        using var owned = job.Start(start);
        owned.StandardInput.Close();
        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = ReadBoundedAndDrainAsync(owned.StandardOutput, reads.Token);
        var error = ReadBoundedAndDrainAsync(owned.StandardError, reads.Token);
        try
        {
            await owned.Process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await job.TerminateRemainingAndProveEmptyAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            return new(owned.Process.ExitCode,
                await output.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false),
                await error.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            try { await job.TerminateRemainingAndProveEmptyAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            finally
            {
                await reads.CancelAsync().ConfigureAwait(false);
                try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or TimeoutException) { }
            }
        }
    }

    private static async Task<string> ReadBoundedAndDrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int maximumCharacters = 65_536;
        var output = new StringBuilder();
        var buffer = new char[4_096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length < maximumCharacters)
                output.Append(buffer, 0, Math.Min(count, maximumCharacters - output.Length));
            // Continue draining beyond the retained limit, otherwise the child's stdout pipe blocks.
        }
        return output.ToString();
    }
}
