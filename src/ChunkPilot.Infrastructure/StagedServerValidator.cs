using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record StagedServerValidationResult(
    bool Succeeded,
    bool ReadinessConfirmed,
    bool LoopbackStatusConfirmed,
    bool CleanStopConfirmed,
    bool NoUnexpectedGuiConfirmed,
    string Summary,
    IReadOnlyList<string> Tail);

public interface IStagedServerValidator
{
    Task<StagedServerValidationResult> ValidateAsync(
        string javaPath,
        string stagingRoot,
        string launchRelativePath,
        bool usesArgumentFile,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs one bounded, loopback-only, owned validation launch. It restores the reviewed server
/// properties before returning and never detaches or adopts a child process.
/// </summary>
public sealed class StagedServerValidator : IStagedServerValidator
{
    private const int MaximumCapturedLines = 4_000;

    public async Task<StagedServerValidationResult> ValidateAsync(
        string javaPath,
        string stagingRoot,
        string launchRelativePath,
        bool usesArgumentFile,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var launch = Path.GetFullPath(Path.Combine(root, launchRelativePath));
        EnsureChild(root, launch);
        if (!File.Exists(launch)) throw new FileNotFoundException("The staged validation launcher was not found.", launch);
        if (!File.Exists(javaPath)) throw new FileNotFoundException("The staged validation Java runtime was not found.", javaPath);
        var propertiesPath = Path.Combine(root, "server.properties");
        var originalProperties = File.Exists(propertiesPath)
            ? await File.ReadAllBytesAsync(propertiesPath, cancellationToken).ConfigureAwait(false)
            : null;
        var port = AllocateLoopbackPort();
        var lines = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Process? process = null;
        var readiness = false;
        var status = false;
        var cleanStop = false;
        var noGui = true;
        string summary;
        try
        {
            var properties = originalProperties is null
                ? ServerPropertiesDocument.Parse("")
                : ServerPropertiesDocument.Parse(Encoding.UTF8.GetString(originalProperties));
            properties.Set("server-ip", "127.0.0.1");
            properties.Set("server-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            properties.Set("online-mode", "false");
            properties.Set("enable-query", "false");
            properties.Set("enable-rcon", "false");
            properties.Set("broadcast-rcon-to-ops", "false");
            properties.Set("enforce-secure-profile", "false");
            await File.WriteAllTextAsync(propertiesPath, properties.ToString(), new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);

            var start = new ProcessStartInfo
            {
                FileName = javaPath,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-Xms256M");
            start.ArgumentList.Add("-Xmx1024M");
            if (usesArgumentFile)
                start.ArgumentList.Add("@" + launch);
            else
            {
                start.ArgumentList.Add("-jar");
                start.ArgumentList.Add(launch);
            }
            start.ArgumentList.Add("nogui");
            ChildProcessEnvironmentPolicy.Apply(start);
            CurseForgeCredentialEnvironment.RemoveFromChild(start);
            process = Process.Start(start) ?? throw new InvalidOperationException("Windows did not start the owned validation server.");
            void Capture(string source, string? line)
            {
                if (line is null) return;
                lines.Enqueue($"[{source}] {SecretRedactor.Redact(line)}");
                while (lines.Count > MaximumCapturedLines) lines.TryDequeue(out _);
                if (line.Contains("Done (", StringComparison.OrdinalIgnoreCase)) ready.TrySetResult();
            }
            process.OutputDataReceived += (_, args) => Capture("stdout", args.Data);
            process.ErrorDataReceived += (_, args) => Capture("stderr", args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout);
            var exit = process.WaitForExitAsync(bounded.Token);
            var winner = await Task.WhenAny(ready.Task, exit).WaitAsync(bounded.Token).ConfigureAwait(false);
            if (winner == exit)
            {
                summary = $"The generated server exited before readiness with code {process.ExitCode}.";
                return Finish(false);
            }
            readiness = true;
            await Task.Delay(TimeSpan.FromSeconds(2), bounded.Token).ConfigureAwait(false);
            if (process.HasExited)
            {
                summary = $"The generated server exited during its stability window with code {process.ExitCode}.";
                return Finish(false);
            }
            noGui = process.MainWindowHandle == IntPtr.Zero;
            status = await new MinecraftStatusClient().QueryAsync("127.0.0.1", port, bounded.Token)
                .ConfigureAwait(false) is not null;
            await process.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
            await process.StandardInput.FlushAsync(bounded.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(bounded.Token).WaitAsync(TimeSpan.FromSeconds(30), bounded.Token)
                .ConfigureAwait(false);
            cleanStop = process.ExitCode == 0;
            var succeeded = readiness && status && cleanStop && noGui;
            summary = succeeded
                ? "Bounded loopback readiness, status, clean stop, and no-GUI validation passed."
                : "One or more bounded generated-server validation checks failed.";
            return Finish(succeeded);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            summary = readiness
                ? "The generated server did not stop within the bounded validation window."
                : "The generated server did not reach readiness within the bounded validation window.";
            return Finish(false);
        }
        catch (TimeoutException)
        {
            summary = "The generated server did not stop cleanly within 30 seconds.";
            return Finish(false);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                                                   System.ComponentModel.Win32Exception) { }
            }
            process?.Dispose();
            if (originalProperties is null)
            {
                try { if (File.Exists(propertiesPath)) File.Delete(propertiesPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            else
            {
                await File.WriteAllBytesAsync(propertiesPath, originalProperties, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        StagedServerValidationResult Finish(bool succeeded) => new(succeeded, readiness, status, cleanStop,
            noGui, summary, lines.TakeLast(200).ToArray());
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void EnsureChild(string root, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The validation launcher escaped operation-owned staging.");
    }
}
