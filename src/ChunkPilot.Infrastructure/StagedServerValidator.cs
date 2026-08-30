using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
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
        int minimumRamMb,
        int maximumRamMb,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs one bounded, loopback-only validation launch. The provider process is born inside a
/// private kill-on-close Windows Job, so every non-breakaway descendant remains exactly owned even
/// if the Java root exits. Reviewed server properties are restored only after that Job is empty.
/// </summary>
public sealed class StagedServerValidator : IStagedServerValidator
{
    private const int MaximumCapturedLines = 4_000;

    public async Task<StagedServerValidationResult> ValidateAsync(
        string javaPath,
        string stagingRoot,
        string launchRelativePath,
        bool usesArgumentFile,
        int minimumRamMb,
        int maximumRamMb,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingRoot));
        var launch = Path.GetFullPath(Path.Combine(root, launchRelativePath));
        EnsureChild(root, launch);
        if (!File.Exists(launch)) throw new FileNotFoundException("The staged validation launcher was not found.", launch);
        if (!File.Exists(javaPath)) throw new FileNotFoundException("The staged validation Java runtime was not found.", javaPath);
        if (MemoryAllocationPolicy.ValidatePair(minimumRamMb, maximumRamMb) is { } memoryProblem)
            throw new ArgumentOutOfRangeException(nameof(maximumRamMb), memoryProblem);
        var propertiesPath = Path.Combine(root, "server.properties");
        var originalProperties = File.Exists(propertiesPath)
            ? await File.ReadAllBytesAsync(propertiesPath, cancellationToken).ConfigureAwait(false)
            : null;
        var port = AllocateLoopbackPort();
        var lines = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WindowsStagedProcessJob? processJob = null;
        WindowsStagedProcess? ownedProcess = null;
        Process? process = null;
        Task outputPump = Task.CompletedTask;
        Task errorPump = Task.CompletedTask;
        var readiness = false;
        var status = false;
        var cleanStop = false;
        var noGui = true;
        string summary;
        async Task<StagedServerValidationResult> ExecuteValidationAsync()
        {
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
                start.ArgumentList.Add($"-Xms{minimumRamMb}M");
                start.ArgumentList.Add($"-Xmx{maximumRamMb}M");
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
                void Capture(string source, string? line)
                {
                    if (line is null) return;
                    lines.Enqueue($"[{source}] {SecretRedactor.Redact(line)}");
                    while (lines.Count > MaximumCapturedLines) lines.TryDequeue(out _);
                    if (line.Contains("Done (", StringComparison.OrdinalIgnoreCase)) ready.TrySetResult();
                }
                processJob = WindowsStagedProcessJob.Create();
                ownedProcess = processJob.Start(start);
                process = ownedProcess.Process;
                outputPump = PumpAsync(ownedProcess.StandardOutput, "stdout", Capture);
                errorPump = PumpAsync(ownedProcess.StandardError, "stderr", Capture);
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
                noGui = !processJob.HasStableOwnedVisibleTopLevelWindow();
                if (!noGui)
                {
                    summary = "The generated server opened an unexpected visible top-level window during isolated validation.";
                    return Finish(false);
                }
                var ownedEndpoints = processJob.CaptureStableOwnedNetworkEndpoints();
                if (ownedEndpoints.Any(endpoint => !endpoint.IsLoopback))
                {
                    summary = "The generated server opened an unexpected non-loopback network endpoint during isolated validation.";
                    return Finish(false);
                }
                var exactLoopbackListener = ownedEndpoints.Any(endpoint =>
                    endpoint.IsTcpListener &&
                    endpoint.IsLoopback &&
                    endpoint.Port == port);
                status = exactLoopbackListener &&
                         await new MinecraftStatusClient().QueryAsync("127.0.0.1", port, bounded.Token)
                             .ConfigureAwait(false) is not null;
                await ownedProcess.StandardInput.WriteLineAsync("stop").ConfigureAwait(false);
                await ownedProcess.StandardInput.FlushAsync(bounded.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(bounded.Token).WaitAsync(TimeSpan.FromSeconds(30), bounded.Token)
                    .ConfigureAwait(false);
                cleanStop = process.ExitCode == 0 &&
                            await processJob.WaitForEmptyAsync(TimeSpan.FromSeconds(2), bounded.Token)
                                .ConfigureAwait(false);
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
        }

        StagedServerValidationResult? outcome = null;
        ExceptionDispatchInfo? operationFailure = null;
        try
        {
            outcome = await ExecuteValidationAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            operationFailure = ExceptionDispatchInfo.Capture(exception);
        }

        var cleanupFailure = await CleanupAsync().ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            if (operationFailure is not null)
                throw new AggregateException(
                    "Staged validation failed and exact process-tree cleanup could not be proven.",
                    operationFailure.SourceException,
                    cleanupFailure);
            throw new InvalidOperationException(
                "Exact staged validation process-tree cleanup could not be proven.", cleanupFailure);
        }
        operationFailure?.Throw();
        return outcome ?? throw new InvalidOperationException("Staged validation produced no result.");

        StagedServerValidationResult Finish(bool succeeded) => new(succeeded, readiness, status, cleanStop,
            noGui, summary, lines.TakeLast(200).ToArray());

        async Task<Exception?> CleanupAsync()
        {
            var failures = new List<Exception>();
            if (processJob is not null)
            {
                try
                {
                    await processJob.TerminateRemainingAndProveEmptyAsync(TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }
            if (ownedProcess is not null)
            {
                try { ownedProcess.StandardInput.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
                try
                {
                    await Task.WhenAll(outputPump, errorPump)
                        .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
                try { ownedProcess.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            if (processJob is not null)
            {
                try { processJob.Dispose(); }
                catch (Exception exception) { failures.Add(exception); }
            }
            try
            {
                if (originalProperties is null)
                {
                    if (File.Exists(propertiesPath))
                        File.Delete(propertiesPath);
                }
                else
                {
                    await File.WriteAllBytesAsync(propertiesPath, originalProperties, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            return failures.Count switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException(failures)
            };
        }

        static async Task PumpAsync(
            StreamReader reader,
            string source,
            Action<string, string?> capture)
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
                capture(source, line);
        }
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
