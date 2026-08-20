using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Runs the privileged helper once, through the documented Windows shell elevation verb.
/// </summary>
/// <remarks>
/// <para>
/// <c>runas</c> is the Shell verb Microsoft documents for launching an application as administrator; it
/// is what raises the User Account Control prompt. ChunkPilot itself is never elevated: the App and the
/// Agent keep running as the ordinary user, and only this one short-lived process ever holds
/// administrator rights, for the length of one firewall operation.
/// </para>
/// <para>
/// Dismissing the prompt is reported by Windows as <c>ERROR_CANCELLED</c>. That is surfaced as a
/// cancellation rather than as a failure, because nothing was attempted and nothing went wrong.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class ShellElevationLauncher(Func<string?> resolveHelperPath) : IFirewallElevationLauncher
{
    private const int ErrorCancelled = 1223;

    public async Task<FirewallElevationOutcome> LaunchAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var helper = resolveHelperPath();
        if (helper is null)
            return FirewallElevationOutcome.Failed(
                "ChunkPilot's firewall helper was not found in the ChunkPilot installation folder.",
                FirewallElevationFailure.HelperUnavailable);

        var startInfo = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(helper)!
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        Process? process;
        try
        {
            // Windows shows the prompt synchronously, so starting the process is what waits for the
            // user's answer. It is deliberately off the calling thread.
            process = await Task.Run(() => Process.Start(startInfo), cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return FirewallElevationOutcome.CancelledByUser();
        }
        catch (Win32Exception exception)
        {
            return FirewallElevationOutcome.Failed(
                $"Windows did not start ChunkPilot's firewall helper: {exception.Message}",
                exception.NativeErrorCode == 5
                    ? FirewallElevationFailure.PermissionDenied
                    : FirewallElevationFailure.LaunchFailed);
        }
        catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException)
        {
            return FirewallElevationOutcome.Failed(
                $"Windows did not start ChunkPilot's firewall helper: {exception.Message}");
        }

        if (process is null)
            return FirewallElevationOutcome.Failed("Windows did not start ChunkPilot's firewall helper.");

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return FirewallElevationOutcome.Completed(process.ExitCode,
                $"The firewall helper finished with code {process.ExitCode}.");
        }
    }
}
