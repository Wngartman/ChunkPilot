using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record StagedValidationProgress(string Stage, double ElapsedSeconds, string LastMilestone,
    double? SecondsSinceMilestone, string? RecentStatus, bool NewOutputObserved);

public sealed class StagedServerCleanupException(string message, Exception innerException)
    : IOException(message, innerException)
{
    public StagedServerValidationResult? Validation { get; init; }
}

public sealed class StagedServerCancelledException(OperationCanceledException cause, StagedServerValidationResult validation)
    : OperationCanceledException(cause.Message, cause, cause.CancellationToken)
{
    public StagedServerValidationResult Validation { get; } = validation;
}

public sealed record StagedServerValidationResult(
    bool Succeeded, bool ReadinessConfirmed, bool LoopbackStatusConfirmed, bool CleanStopConfirmed,
    bool NoUnexpectedGuiConfirmed, string Summary, IReadOnlyList<string> Tail)
{
    public Guid AttemptId { get; init; }
    public StartupNetworkDecision? Network { get; init; }
    public IReadOnlyList<StartupEndpointFinding> EndpointHistory { get; init; } = [];
    public bool JobEmptyConfirmed { get; init; }
    public bool? SelectedPortListenerAbsent { get; init; }
    public int ValidationPort { get; init; }
    public bool ConfigurationRestored { get; init; }
    public bool ValidationWorldRemoved { get; init; }
    public double ElapsedSeconds { get; init; }
}

public interface IStagedServerValidator
{
    Task<StagedServerValidationResult> ValidateAsync(string javaPath, string stagingRoot,
        string launchRelativePath, bool usesArgumentFile, int minimumRamMb, int maximumRamMb,
        TimeSpan timeout, IProgress<StagedValidationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Observed inbound-startup check, not preventive isolation or an air gap. Root and descendants are
/// born in an exact-owned Job. Original configuration is restored only after Job-empty proof.
/// </summary>
public sealed class StagedServerValidator : IStagedServerValidator
{
    private static readonly string[] ValidationPropertyKeys = ["server-ip", "server-port", "level-name", "enable-query", "enable-rcon"];
    private readonly Func<WindowsStagedProcessJob, Guid, IReadOnlyList<StartupEndpointObservation>> collect;
    public StagedServerValidator() : this((job, attempt) =>
        job.CaptureStableOwnedNetworkEndpoints().Select(endpoint => endpoint.ToObservation(attempt)).ToArray()) { }
    internal StagedServerValidator(
        Func<WindowsStagedProcessJob, Guid, IReadOnlyList<StartupEndpointObservation>> collect) => this.collect = collect;

    public async Task<StagedServerValidationResult> ValidateAsync(string javaPath, string stagingRoot,
        string launchRelativePath, bool usesArgumentFile, int minimumRamMb, int maximumRamMb,
        TimeSpan timeout, IProgress<StagedValidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var root = CreationPathSafety.Canonical(stagingRoot);
        var launch = Path.GetFullPath(Path.Combine(root, launchRelativePath));
        CreationPathSafety.EnsureWithin(root, launch);
        EnsureRegularFilePath(launch);
        if (!File.Exists(launch) || !File.Exists(javaPath))
            throw new FileNotFoundException("The staged launcher or Java runtime was not found.");
        if (MemoryAllocationPolicy.ValidatePair(minimumRamMb, maximumRamMb) is { } memoryProblem)
            throw new ArgumentOutOfRangeException(nameof(maximumRamMb), memoryProblem);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var attempt = Guid.NewGuid();
        var watch = Stopwatch.StartNew();
        var propertiesPath = Path.Combine(root, "server.properties");
        EnsureRegularFilePath(propertiesPath);
        var original = File.Exists(propertiesPath) ? await File.ReadAllBytesAsync(propertiesPath, cancellationToken) : null;
        var port = AllocateLoopbackPort();
        var worldName = ServerCreationTransaction.StagingFolderName(attempt);
        var worldPath = Path.Combine(root, worldName);
        var marker = new CreationOwnershipMarker(CreationOwnershipMarker.CurrentSchemaVersion,
            attempt, attempt, worldPath, DateTimeOffset.UtcNow);
        var lines = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fatal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidence = new Dictionary<string, StartupEndpointFinding>(StringComparer.Ordinal);
        var evidenceLock = new object();
        var lastMilestone = "Starting Java";
        var lastMilestoneAt = 0d;
        var outputCount = 0;
        var reportedOutputCount = 0;
        string? recent = null;
        WindowsStagedProcessJob? job = null;
        WindowsStagedProcess? owned = null;
        Task output = Task.CompletedTask, error = Task.CompletedTask;
        var readiness = false;
        var status = false;
        var cleanStop = false;
        var noGui = true;
        var empty = false;
        var restored = false;
        var worldRemoved = false;
        var worldPrepared = false;
        bool? portAbsent = null;
        StartupNetworkDecision? network = null;
        var summary = "Startup check could not be completed.";
        Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            await CreationStagingSafety.PrepareOwnedDirectoryAsync(root, worldPath, marker, cancellationToken);
            worldPrepared = true;
            var properties = ServerPropertiesDocument.Parse(original is null ? "" : Encoding.UTF8.GetString(original));
            properties.Set("server-ip", "127.0.0.1");
            properties.Set("server-port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            properties.Set("level-name", worldName);
            properties.Set("online-mode", "false");
            properties.Set("enable-query", "false");
            properties.Set("enable-rcon", "false");
            properties.Set("broadcast-rcon-to-ops", "false");
            properties.Set("enforce-secure-profile", "false");
            var validationBytes = new UTF8Encoding(false).GetBytes(properties.ToString());
            await File.WriteAllBytesAsync(propertiesPath, validationBytes, cancellationToken);
            var temp = Path.Combine(worldPath, ".runtime-temp");
            CreationStagingSafety.CreateDirectoryPath(worldPath, temp);
            var start = new ProcessStartInfo
            {
                FileName = javaPath, WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add($"-Xms{minimumRamMb}M");
            start.ArgumentList.Add($"-Xmx{maximumRamMb}M");
            start.ArgumentList.Add("-Djava.io.tmpdir=" + temp);
            if (usesArgumentFile) start.ArgumentList.Add("@" + launch);
            else { start.ArgumentList.Add("-jar"); start.ArgumentList.Add(launch); }
            start.ArgumentList.Add("nogui");
            ChildProcessEnvironmentPolicy.Apply(start);
            CurseForgeCredentialEnvironment.RemoveFromChild(start);
            start.Environment["TEMP"] = temp;
            start.Environment["TMP"] = temp;
            if (!(await File.ReadAllBytesAsync(propertiesPath, cancellationToken)).SequenceEqual(validationBytes))
                throw new IOException("Validation configuration changed before launch.");
            job = WindowsStagedProcessJob.Create();
            owned = job.Start(start);
            output = PumpAsync(owned.StandardOutput, "stdout");
            error = PumpAsync(owned.StandardError, "stderr");
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(timeout); // Absolute deadline: warning spam cannot extend it.
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (owned.Process.HasExited)
                {
                    summary = $"The server exited before readiness with code {owned.Process.ExitCode}.";
                    break;
                }
                if (fatal.Task.IsCompletedSuccessfully) { summary = "Server could not start: " + fatal.Task.Result; break; }
                if (!Observe()) break;
                Publish("Starting for the first time");
                if (ready.Task.IsCompletedSuccessfully)
                {
                    readiness = true;
                    var stableUntil = watch.Elapsed + TimeSpan.FromSeconds(2);
                    while (watch.Elapsed < stableUntil && !owned.Process.HasExited)
                    {
                        await Task.Delay(500, startup.Token);
                        if (!Observe()) break;
                    }
                    if (network is null || !network.Passed || owned.Process.HasExited)
                    { summary = network?.Summary ?? "Startup listener could not be verified."; break; }
                    noGui = !job.HasStableOwnedVisibleTopLevelWindow();
                    if (!noGui) { summary = "The staged server opened an unexpected visible window."; break; }
                    // Inspect only after readiness, when the server has finished normal initial properties writes.
                    var actual = ServerPropertiesDocument.Parse(await File.ReadAllTextAsync(propertiesPath, startup.Token));
                    if (ValidationPropertyKeys
                        .Any(key => actual.Get(key) != properties.Get(key)))
                    { summary = "Startup check configuration was rewritten by the server; intended files were not promoted."; break; }
                    Publish("Checking startup");
                    status = await new MinecraftStatusClient().QueryAsync("127.0.0.1", port, startup.Token) is not null;
                    if (!Observe() || !network!.Passed) { status = false; break; }
                    summary = status ? "Local readiness and status checks passed." : "The local Minecraft status check did not respond.";
                    break;
                }
                await Task.Delay(500, startup.Token);
            }
        }
        catch (OperationCanceledException exception)
        {
            operationFailure = cancellationToken.IsCancellationRequested ? exception : null;
            summary = cancellationToken.IsCancellationRequested
                ? "Startup check cancelled; cleaning up exact-owned work."
                : "The server did not complete the bounded startup check before its deadline.";
        }
        catch (Exception exception)
        {
            operationFailure = exception;
            summary = "Startup check could not be completed: " + SecretRedactor.Redact(exception.Message);
        }
        finally
        {
            // Failure and cancellation take the same terminal cleanup path, independent of startup deadline.
            if (owned is not null && job is not null)
            {
                try
                {
                    if (network?.UnexpectedInboundListener == true) job.Terminate();
                    Publish("Stopping validation server");
                    if (!owned.Process.HasExited)
                    {
                        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                        await owned.StandardInput.WriteLineAsync("stop".AsMemory(), shutdown.Token);
                        await owned.StandardInput.FlushAsync(shutdown.Token);
                        await owned.Process.WaitForExitAsync(shutdown.Token);
                    }
                    cleanStop = owned.Process.ExitCode == 0 &&
                        await job.WaitForEmptyAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException)
                { /* Exact Job termination below remains mandatory; cleanStop stays false. */ }
            }
            try
            {
                if (job is not null) await job.TerminateRemainingAndProveEmptyAsync(TimeSpan.FromSeconds(30));
                empty = true;
            }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (Exception exception) { cleanupFailures.Add(exception); }
            if (empty)
            {
                try
                {
                    // This is independent of Job emptiness. A new/unrelated owner is not killed.
                    portAbsent = !WindowsStagedNetworkEndpoints.Capture().Any(e => e.IsTcpListener && e.Port == port);
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
                try
                {
                    EnsureRegularFilePath(propertiesPath);
                    if (original is null) { if (File.Exists(propertiesPath)) File.Delete(propertiesPath); }
                    else await File.WriteAllBytesAsync(propertiesPath, original, CancellationToken.None);
                    restored = true;
                    if (worldPrepared)
                        CreationStagingSafety.DeleteOwnedTree(worldPath, attempt, attempt, worldPath);
                    worldRemoved = !Directory.Exists(worldPath);
                }
                catch (Exception exception) { cleanupFailures.Add(exception); }
            }
            try { owned?.Dispose(); } catch (Exception exception) { cleanupFailures.Add(exception); }
            try { job?.Dispose(); } catch (Exception exception) { cleanupFailures.Add(exception); }
        }
        var success = operationFailure is null && cleanupFailures.Count == 0 && readiness && status &&
            network?.Passed == true && cleanStop && noGui && empty && portAbsent == true && restored && worldRemoved;
        var result = new StagedServerValidationResult(success, readiness, status, cleanStop, noGui, summary, lines.TakeLast(200).ToArray())
        {
            AttemptId = attempt, Network = network, EndpointHistory = evidence.Values.ToArray(),
            JobEmptyConfirmed = empty, SelectedPortListenerAbsent = portAbsent, ValidationPort = port,
            ConfigurationRestored = restored, ValidationWorldRemoved = worldRemoved, ElapsedSeconds = watch.Elapsed.TotalSeconds
        };
        if (cleanupFailures.Count > 0)
            throw new StagedServerCleanupException("Cleanup needs attention. " + summary,
                new AggregateException(operationFailure is null ? cleanupFailures : new[] { operationFailure }.Concat(cleanupFailures)))
                { Validation = result };
        if (operationFailure is OperationCanceledException cancelled)
            throw new StagedServerCancelledException(cancelled, result);
        return result;

        bool Observe()
        {
            try
            {
                network = StartupNetworkPolicy.Evaluate(attempt, collect(job!, attempt), IPAddress.Loopback, port);
                foreach (var finding in network.Findings)
                {
                    var e = finding.Observation;
                    var key = $"{e.Transport}|{e.LocalAddress}|{e.LocalPort}|{e.RemoteAddress}|{e.RemotePort}|{e.State}|{e.ProcessId}|{e.ProcessCreationTicks}";
                    if (evidence.Count >= 256 && !evidence.ContainsKey(key))
                        throw new InvalidDataException("The bounded endpoint evidence limit was reached.");
                    evidence[key] = finding;
                }
                if (network.UnexpectedInboundListener || network.Unresolved)
                { summary = network.Summary; return false; }
                return true; // A main listener is not expected until readiness; required before status/promotion.
            }
            catch (Exception exception)
            {
                network = StartupNetworkPolicy.Evaluate(attempt, [], IPAddress.Loopback, port, false, false);
                summary = "Startup check could not verify the endpoint inventory: " + SecretRedactor.Redact(exception.Message);
                return false;
            }
        }

        void Publish(string stage)
        {
            lock (evidenceLock)
            {
                progress?.Report(new(stage, watch.Elapsed.TotalSeconds, lastMilestone,
                    watch.Elapsed.TotalSeconds - lastMilestoneAt, recent, outputCount != reportedOutputCount));
                reportedOutputCount = outputCount;
            }
        }

        async Task PumpAsync(StreamReader reader, string source)
        {
            while (await reader.ReadLineAsync(CancellationToken.None) is { } line)
            {
                var safe = SecretRedactor.Redact(line);
                safe = safe[..Math.Min(safe.Length, 1024)];
                lines.Enqueue($"[{source}] {safe}");
                while (lines.Count > 200) lines.TryDequeue(out _);
                lock (evidenceLock)
                {
                    outputCount++;
                    recent = safe;
                    var milestone = line.Contains("Done (", StringComparison.OrdinalIgnoreCase) ? "Server reported ready"
                        : line.Contains("Preparing start region", StringComparison.OrdinalIgnoreCase) ? "Preparing validation spawn"
                        : line.Contains("Preparing level", StringComparison.OrdinalIgnoreCase) ? "Preparing disposable validation world"
                        : line.Contains("Starting minecraft server version", StringComparison.OrdinalIgnoreCase) ? "Minecraft startup began"
                        : null;
                    if (milestone is not null && milestone != lastMilestone)
                    { lastMilestone = milestone; lastMilestoneAt = watch.Elapsed.TotalSeconds; }
                }
                if (line.Contains("Done (", StringComparison.OrdinalIgnoreCase)) ready.TrySetResult();
                if (line.Contains("Failed to start the minecraft server", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Could not reserve enough space", StringComparison.OrdinalIgnoreCase))
                    fatal.TrySetResult(safe);
            }
        }
    }

    private static int AllocateLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static void EnsureRegularFilePath(string path)
    {
        CreationStagingSafety.EnsureNoReparseTraversal(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("A validation file is redirected.");
    }
}
