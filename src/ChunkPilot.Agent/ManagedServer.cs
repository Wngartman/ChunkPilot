using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

public sealed class ManagedServer : IAsyncDisposable
{
    internal static readonly TimeSpan ManualStopGateDeadline = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object processGate = new();
    private readonly LifecycleStateMachine lifecycle = new();
    private readonly BoundedConsoleBuffer console;
    private readonly ProcessStatisticsProvider statistics;
    private readonly MinecraftStatusClient statusClient;
    private readonly ChunkPilotStore store;
    private readonly AppDataPaths paths;
    private readonly JarInventoryService? jarInventory;
    private readonly ILogger<ManagedServer> logger;
    private readonly List<StatisticsSample> samples = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly object operationCancellationGate = new();
    private CancellationTokenSource? activeOperationCancellation;
    private string activeOperationName = "";
    private bool applicationExitRequested;
    private readonly List<ConsoleLineObserver> lineObservers = [];
    private readonly SortedSet<string> onlinePlayerNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When this server last reported each player joining or leaving. Guarded by
    /// <see cref="onlinePlayerNames"/>.
    /// </summary>
    /// <remarks>
    /// The only record of player activity ChunkPilot keeps, and it holds only what this server's own
    /// console said. It belongs to one <see cref="ManagedServer"/>, so one server's players can never
    /// appear in another's, and it starts empty, so a player ChunkPilot has not watched connect has
    /// no entry rather than a guessed one.
    /// </remarks>
    private readonly Dictionary<string, DateTimeOffset> lastSeenByPlayer = new(StringComparer.OrdinalIgnoreCase);
    private long playerAccessRevision;
    private Process? process;
    private Task? stdoutTask;
    private Task? stderrTask;
    private Task? monitorTask;
    private TaskCompletionSource<bool>? readiness;
    private TaskCompletionSource<bool>? saveConfirmation;
    private DateTimeOffset? startedAt;
    private DateTimeOffset? lastSaveAt;
    private DateTimeOffset? lastBackupAt;
    private int? lastExitCode;
    private string lastError = "";
    private readonly object failureGate = new();
    private StartupFailureEvidence? startupFailure;
    private bool intentionalStop;
    private bool hasStartedSuccessfully;
    private bool lastStartReachedReadiness;
    private ServerDefinition? pendingDefinition;
    private int crashAttempts;
    private int? onlinePlayers;
    private int? maxPlayers;
    private PlayerStatusEvidence playerStatus = new()
    {
        Source = PlayerStatusSource.Waiting,
        Detail = "Start the server to collect player status."
    };
    private DateTimeOffset lastPlayerQuery;
    private DateTimeOffset lastPersistedStatistics;
    private LifecycleIntentKind lastIntent;
    private readonly AutostartMode autostartMode;
    private int lifecycleGeneration;
    private ProcessIdentity? detachedIdentity;
    private CrashAnalysisReport? lastCrashAnalysis;

    public ManagedServer(
        ServerDefinition definition,
        ProcessStatisticsProvider statistics,
        MinecraftStatusClient statusClient,
        ChunkPilotStore store,
        AppDataPaths paths,
        ILogger<ManagedServer> logger,
        int consoleCapacity = 5_000,
        JarInventoryService? jarInventory = null,
        AutostartMode autostartMode = AutostartMode.Never)
    {
        Definition = definition;
        this.statistics = statistics;
        this.statusClient = statusClient;
        this.store = store;
        this.paths = paths;
        this.logger = logger;
        this.jarInventory = jarInventory;
        this.autostartMode = autostartMode is AutostartMode.AgentStart or AutostartMode.WindowsLoginWithDelay
            ? autostartMode
            : AutostartMode.Never;
        console = new BoundedConsoleBuffer(consoleCapacity);
    }

    public ServerDefinition Definition { get; private set; }
    public ServerState State => lifecycle.State;
    public bool HasDetachedProcess => detachedIdentity is not null;

    public void RestoreCrashAnalysis(CrashAnalysisReport? report)
    {
        if (report is not null && report.ServerId != Definition.Id)
            throw new ArgumentException("Crash analysis belongs to another server.", nameof(report));
        lastCrashAnalysis = report;
    }

    public void RequestApplicationExitCancellation()
    {
        lock (operationCancellationGate)
        {
            applicationExitRequested = true;
            activeOperationCancellation?.Cancel();
        }
    }

    /// <summary>
    /// Exit-priority lifecycle path. It cooperatively cancels the current transactional operation,
    /// waits only for the supplied gate deadline, then performs save/graceful stop and exact-owned
    /// escalation without releasing the lifecycle gate between those steps.
    /// </summary>
    public async Task<OperationResult> StopForApplicationExitAsync(
        string source,
        TimeSpan gateDeadline,
        CancellationToken cancellationToken = default)
    {
        RequestApplicationExitCancellation();
        using var gateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateTimeout.CancelAfter(gateDeadline);
        try
        {
            await operationGate.WaitAsync(gateTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return OperationResult.Fail(
                $"Safe exit could not acquire the server lifecycle path within {gateDeadline.TotalSeconds:0} seconds; " +
                "the Agent remains alive with public leases revoked and will retry.");
        }

        var timer = Stopwatch.StartNew();
        try
        {
            lastIntent = source.Equals("Windows shutdown", StringComparison.OrdinalIgnoreCase)
                ? LifecycleIntentKind.WindowsShutdown
                : LifecycleIntentKind.ApplicationExit;
            Interlocked.Increment(ref lifecycleGeneration);

            OperationResult result;
            if (detachedIdentity is { } detached)
            {
                result = await ForceTerminateDetachedAsync(detached, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await StopCoreAsync(saveFirst: true, cancellationToken).ConfigureAwait(false);
                if (!result.Success && !result.RequiresForceConfirmation)
                    result = await StopCoreAsync(saveFirst: false, cancellationToken).ConfigureAwait(false);
                if (!result.Success && result.RequiresForceConfirmation)
                    result = await ForceTerminateCurrentProcessCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            await RecordAsync("Application exit safe stop", result, timer, source, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Returns true when the exact process instance owned by this managed server is still alive, or
    /// when Windows refuses enough process evidence that declaring it gone would be unsafe.
    /// </summary>
    public bool HasExactOwnedProcessAlive()
    {
        Process? current;
        lock (processGate)
            current = process;
        try
        {
            if (current is { HasExited: false })
                return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
            return true;
        }

        if (detachedIdentity is not { } detached)
            return false;
        try
        {
            using var detachedProcess = Process.GetProcessById(detached.ProcessId);
            if (detachedProcess.HasExited)
                return false;
            var executable = detachedProcess.MainModule?.FileName;
            return detached.ProcessCreationTicks == ProcessCreationIdentity.Unknown ||
                   string.IsNullOrWhiteSpace(executable) ||
                   ProcessIdentityPolicy.MatchesProcessInstance(
                       detached,
                       detachedProcess.Id,
                       ProcessCreationIdentity.Of(detachedProcess.SafeHandle),
                       executable,
                       out _);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    /// <summary>
    /// True only while a deliberate restart of this server is still running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A restart passes briefly through <see cref="ServerState.Stopped"/> on its way back up, so the
    /// state alone cannot tell an intentional restart apart from a server the owner stopped. This pairs
    /// the recorded lifecycle intent with the fact that the operation still holds this server's gate,
    /// which together answer that question exactly — and stop answering it the moment the restart
    /// finishes, whether it succeeded or failed.
    /// </para>
    /// <para>
    /// Router mapping uses this to decide whether a stopped server's exposure may be preserved.
    /// Crash recovery is deliberately excluded: it is automatic rather than intentional, and closing
    /// the port is the safer default when nobody asked for the restart.
    /// </para>
    /// </remarks>
    public bool IsRestartInProgress =>
        operationGate.CurrentCount == 0 &&
        lastIntent is LifecycleIntentKind.SafeRestart or LifecycleIntentKind.ScheduledRestart
            or LifecycleIntentKind.UpdateRestart;

    /// <summary>
    /// The live root process, or null when nothing is running. Together with <see cref="StartedAt"/>
    /// this identifies one exact run, which is what binds point-in-time evidence — an external
    /// reachability result — to the listener it was actually gathered about.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="Snapshot"/>: reading run identity must not copy the
    /// console buffer and the statistics window every time a surface refreshes.
    /// </remarks>
    public int? RootProcessId
    {
        get
        {
            lock (processGate)
                return process is { HasExited: false } current ? current.Id : null;
        }
    }

    /// <summary>When the current run began, or null when nothing has started.</summary>
    public DateTimeOffset? StartedAt => startedAt;

    public void MarkDetached(ProcessIdentity identity)
    {
        if (identity.ServerId != Definition.Id)
            throw new InvalidOperationException("Detached process identity belongs to another server.");
        detachedIdentity = identity with { ControlState = ProcessControlState.RunningDetached };
        lifecycle.TransitionTo(ServerState.Unknown);
        lastError =
            "A matching prior process is still running, but console ownership could not be safely re-established. " +
            "ChunkPilot will not start a duplicate or attach by PID alone.";
        console.Add("ChunkPilot", lastError);
    }

    public void UpdateDefinition(ServerDefinition definition)
    {
        if (definition.Id != Definition.Id)
            throw new InvalidOperationException("Cannot change a managed server identity.");
        if (State is not ServerState.Stopped and not ServerState.Crashed)
        {
            // Persisted settings such as server-port take effect on restart. Swapping the active
            // definition while the process is running would make health checks probe the new port
            // before Minecraft has moved to it.
            pendingDefinition = definition;
            return;
        }
        Definition = definition;
        pendingDefinition = null;
    }

    /// <summary>Updates presentation metadata immediately without changing any live launch setting.</summary>
    public void UpdateDisplayName(string displayName)
    {
        Definition = Definition with { Name = displayName };
        if (pendingDefinition is not null)
            pendingDefinition = pendingDefinition with { Name = displayName };
    }

    /// <summary>The definition the store owns, including settings queued for the next start.</summary>
    public ServerDefinition PersistedDefinition => pendingDefinition ?? Definition;

    public async Task<OperationResult> StartAsync(string source = "Manual", CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (State is not ServerState.Stopped and not ServerState.Crashed)
                return OperationResult.Fail($"Cannot start while the server is {State}.");
            lastIntent = source.Equals("Crash recovery", StringComparison.OrdinalIgnoreCase)
                ? LifecycleIntentKind.CrashRecovery
                : LifecycleIntentKind.ManualStart;
            var result = await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync("Start", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> SaveAsync(string source = "Manual", CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (State != ServerState.Running)
                return OperationResult.Fail($"Cannot save while the server is {State}.");
            var result = await SaveCoreAsync(transitionState: true, cancellationToken).ConfigureAwait(false);
            await RecordAsync("Save", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> StopAsync(
        bool saveFirst = true,
        string source = "Manual",
        CancellationToken cancellationToken = default)
    {
        var stopIntent = source.Equals("Application exit", StringComparison.OrdinalIgnoreCase)
            ? LifecycleIntentKind.ApplicationExit
            : source.Equals("Windows shutdown", StringComparison.OrdinalIgnoreCase)
                ? LifecycleIntentKind.WindowsShutdown
                : LifecycleIntentKind.ManualStop;
        await RequestStopIntentAsync(stopIntent).ConfigureAwait(false);

        using var gateTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        gateTimeout.CancelAfter(ManualStopGateDeadline);
        try
        {
            await operationGate.WaitAsync(gateTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string operation;
            lock (operationCancellationGate)
                operation = string.IsNullOrWhiteSpace(activeOperationName)
                    ? "another server operation"
                    : activeOperationName;
            return OperationResult.Fail(
                $"Stop could not take control within {ManualStopGateDeadline.TotalSeconds:0} seconds because {operation} did not cancel. " +
                "The authoritative process state is unchanged; retry Stop or use exact-process recovery.");
        }
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (detachedIdentity is not null)
                return OperationResult.Fail(
                    "The server is running but detached. Verify or end the recorded process before stopping it through ChunkPilot.",
                    requiresForce: true);
            if (State == ServerState.Stopped)
                return OperationResult.Ok("Server is already stopped.");
            var result = await StopCoreAsync(saveFirst, cancellationToken).ConfigureAwait(false);
            await PersistStopObservationAsync().ConfigureAwait(false);
            await RecordAsync("Safe stop", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> RestartAsync(string source = "Manual", CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (State != ServerState.Running)
                return OperationResult.Fail($"Cannot restart while the server is {State}.");
            lastIntent = source.Equals("Scheduled", StringComparison.OrdinalIgnoreCase)
                ? LifecycleIntentKind.ScheduledRestart
                : LifecycleIntentKind.SafeRestart;
            Interlocked.Increment(ref lifecycleGeneration);
            lifecycle.TransitionTo(ServerState.Restarting);
            var save = await SaveCoreAsync(transitionState: false, cancellationToken).ConfigureAwait(false);
            if (!save.Success)
            {
                lifecycle.TransitionTo(ServerState.Running);
                await RecordAsync("Safe restart", save, timer, source, cancellationToken).ConfigureAwait(false);
                return save;
            }
            var stop = await StopCoreAsync(saveFirst: false, cancellationToken).ConfigureAwait(false);
            if (!stop.Success)
            {
                await RecordAsync("Safe restart", stop, timer, source, cancellationToken).ConfigureAwait(false);
                return stop;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, Definition.RestartDelaySeconds)), cancellationToken).ConfigureAwait(false);
            var start = await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            var result = start.Success
                ? OperationResult.Ok("Server saved, stopped, and restarted successfully.")
                : start;
            await RecordAsync("Safe restart", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> ForceTerminateAsync(string source = "Manual", CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (detachedIdentity is { } detached)
            {
                var detachedResult = await ForceTerminateDetachedAsync(detached, cancellationToken)
                    .ConfigureAwait(false);
                await RecordAsync("Force terminate detached server", detachedResult, timer, source, cancellationToken)
                    .ConfigureAwait(false);
                return detachedResult;
            }
            var result = await ForceTerminateCurrentProcessCoreAsync(cancellationToken).ConfigureAwait(false);
            await RecordAsync("Force terminate", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (TimeoutException)
        {
            var result = OperationResult.Fail("The process tree did not terminate after the explicit force request.");
            await RecordAsync("Force terminate", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<OperationResult> ForceTerminateCurrentProcessCoreAsync(
        CancellationToken cancellationToken)
    {
        Process? current;
        lock (processGate)
            current = process;
        if (current is null || current.HasExited)
        {
            SafeTransition(ServerState.Stopped);
            return OperationResult.Ok("Server process is already stopped.");
        }
        intentionalStop = true;
        ProcessTree.Kill(current.Id);
        await current.WaitForExitAsync(cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        if (!await WaitForPortReleaseAsync(Definition.Port, cancellationToken).ConfigureAwait(false))
        {
            SafeTransition(ServerState.Unresponsive);
            return OperationResult.Fail(
                $"The exact managed process tree exited, but port {Definition.Port} is still listening. " +
                "Another local process is holding the port.");
        }
        SafeTransition(ServerState.Stopped);
        return OperationResult.Ok(
            $"The exact managed process tree was force terminated and port {Definition.Port} was released.");
    }

    public async Task<OperationResult> SendCommandAsync(string command, string source = "Manual", CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
            return OperationResult.Fail("Command cannot be empty.");
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            var result = await SendCommandCoreAsync(command, cancellationToken).ConfigureAwait(false)
                ? OperationResult.Ok("Command sent to the server console.")
                : OperationResult.Fail("Server stdin is disconnected.");
            await RecordAsync("Console command", result, timer, source, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void MarkBackupCompleted() => lastBackupAt = DateTimeOffset.Now;

    /// <summary>Players the running server has reported as connected, in name order.</summary>
    public IReadOnlyList<string> OnlinePlayerNames
    {
        get
        {
            lock (onlinePlayerNames)
                return onlinePlayerNames.ToArray();
        }
    }

    /// <summary>
    /// When each player was last observed joining or leaving this server.
    /// </summary>
    /// <remarks>
    /// A name is absent unless this server reported a session for it. Whitelisting, granting
    /// operator and banning are not sessions and never create an entry here.
    /// </remarks>
    public IReadOnlyDictionary<string, DateTimeOffset> LastSeenByPlayer
    {
        get
        {
            lock (onlinePlayerNames)
                return new Dictionary<string, DateTimeOffset>(lastSeenByPlayer, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Increments whenever this server reports something that changes player access.
    /// </summary>
    /// <remarks>
    /// Bumped from the console pump for joins, leaves and every moderation reply, and from the
    /// moderation path itself. The Agent folds it into the access stamp so the UI knows to re-read
    /// without watching files of its own.
    /// </remarks>
    public long PlayerAccessRevision => Interlocked.Read(ref playerAccessRevision);

    /// <summary>Marks player access as changed, so the next snapshot tells the UI to re-read.</summary>
    public void MarkPlayerAccessChanged() => Interlocked.Increment(ref playerAccessRevision);

    /// <summary>
    /// Sends one moderation command and waits for the server's own answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reply is the outcome. Writing to stdin only proves the command left ChunkPilot, and reading
    /// <c>whitelist.json</c> before the server has written it returns the state from before the change
    /// - which is exactly why an added player used to fail to appear.
    /// </para>
    /// <para>
    /// A timeout is reported as unconfirmed rather than as success or failure, because the command may
    /// well have worked; the caller re-reads authoritative state either way.
    /// </para>
    /// </remarks>
    public async Task<OperationResult> ModeratePlayerAsync(
        PlayerModerationAction action,
        string playerName,
        string reason = "",
        CancellationToken cancellationToken = default)
    {
        var name = PlayerModerationPolicy.ValidatePlayerName(playerName);
        var command = PlayerModerationPolicy.CommandFor(action, name, reason);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        try
        {
            if (State != ServerState.Running)
                return OperationResult.Fail(
                    $"{PlayerModerationPolicy.Describe(action)} needs a running server. This server is {State}.");

            var reply = new TaskCompletionSource<(bool Success, string Line)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new ConsoleLineObserver(line =>
            {
                if (PlayerModerationPolicy.IsSuccessReply(action, name, line))
                    return (true, line);
                return PlayerModerationPolicy.IsFailureReply(action, name, line) ? (false, line) : null;
            }, reply);
            AddObserver(observer);
            try
            {
                if (!await SendCommandCoreAsync(command, cancellationToken).ConfigureAwait(false))
                    return OperationResult.Fail("Server stdin is disconnected; the command was not sent.");

                OperationResult result;
                try
                {
                    var (success, line) = await reply.Task
                        .WaitAsync(PlayerModerationPolicy.ReplyTimeout, cancellationToken).ConfigureAwait(false);
                    result = success
                        ? OperationResult.Ok(line.Trim())
                        : OperationResult.Fail(line.Trim());
                }
                catch (TimeoutException)
                {
                    result = OperationResult.Fail(PlayerModerationPolicy.UnconfirmedMessage(action, name));
                }
                MarkPlayerAccessChanged();
                await RecordAsync($"Player {action}", result, timer, "Access", cancellationToken)
                    .ConfigureAwait(false);
                return result;
            }
            finally
            {
                RemoveObserver(observer);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>
    /// Asks the running server about each named game rule, and records what it says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All the queries go out together and the answers are collected in one bounded window, so the cost
    /// is one round trip rather than one per rule.
    /// </para>
    /// <para>
    /// Three outcomes, all of them meaningful. A reported value is authoritative. A refusal - Brigadier
    /// echoes the command it could not parse - means this server does not have that rule, so ChunkPilot
    /// must not offer a control for it. Silence means unknown, and an unknown value is never guessed at.
    /// </para>
    /// </remarks>
    public async Task<GameruleQueryResult> QueryGamerulesAsync(
        IReadOnlyList<string> ruleNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ruleNames);
        if (ruleNames.Count == 0)
            return GameruleQueryResult.Empty;
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        try
        {
            if (State != ServerState.Running)
                return GameruleQueryResult.Empty;
            var reported = new Dictionary<string, string>(StringComparer.Ordinal);
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            var pending = new HashSet<string>(ruleNames, StringComparer.Ordinal);
            var completed = new TaskCompletionSource<(bool Success, string Line)>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var observer = new ConsoleLineObserver(line =>
            {
                lock (reported)
                {
                    if (GamerulePolicy.ParseReportedValueAny(line) is { } parsed && pending.Remove(parsed.Name))
                        reported[parsed.Name] = parsed.Value;
                    else if (GamerulePolicy.ParseRejectedRuleName(line) is { } refused && pending.Remove(refused))
                        rejected.Add(refused);
                    else
                        return null;
                    return pending.Count == 0 ? (true, line) : null;
                }
            }, completed);
            AddObserver(observer);
            try
            {
                foreach (var rule in ruleNames)
                {
                    if (!await SendCommandCoreAsync($"gamerule {rule}", cancellationToken).ConfigureAwait(false))
                        break;
                }
                try
                {
                    _ = await completed.Task.WaitAsync(GameruleQueryTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Whatever arrived in the window stands; the rest stay unknown.
                }
            }
            finally
            {
                RemoveObserver(observer);
            }
            lock (reported)
            {
                return new GameruleQueryResult(
                    new Dictionary<string, string>(reported, StringComparer.Ordinal),
                    new HashSet<string>(rejected, StringComparer.Ordinal));
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static readonly TimeSpan GameruleQueryTimeout = TimeSpan.FromSeconds(6);

    private void AddObserver(ConsoleLineObserver observer)
    {
        lock (lineObservers)
            lineObservers.Add(observer);
    }

    private void RemoveObserver(ConsoleLineObserver observer)
    {
        lock (lineObservers)
            lineObservers.Remove(observer);
    }

    /// <summary>
    /// One bounded, single-shot interest in the server's console output.
    /// </summary>
    /// <remarks>
    /// Registered for the length of one operation and always removed in a finally, so a stalled reply
    /// cannot accumulate observers on a long-running server.
    /// </remarks>
    private sealed class ConsoleLineObserver
    {
        private readonly Func<string, (bool Success, string Line)?> match;
        private readonly TaskCompletionSource<(bool Success, string Line)> completion;

        public ConsoleLineObserver(
            Func<string, (bool Success, string Line)?> match,
            TaskCompletionSource<(bool Success, string Line)> completion)
        {
            this.match = match;
            this.completion = completion;
        }

        public void Offer(string line)
        {
            if (completion.Task.IsCompleted)
                return;
            if (match(line) is { } outcome)
                completion.TrySetResult(outcome);
        }
    }

    public async Task<T> RunExclusiveDataOperationAsync<T>(
        string operationName,
        bool requireStopped,
        bool saveIfRunning,
        bool freezeWorldSaving,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        freezeWorldSaving &= GameServerRuntimeProfiles.For(Definition).FreezeAutomaticSavingDuringBackup;
        var savingDisabled = false;
        try
        {
            if (requireStopped && State != ServerState.Stopped)
                throw new InvalidOperationException($"Stop the server before {operationName}.");
            if (saveIfRunning && State == ServerState.Running)
            {
                if (freezeWorldSaving)
                {
                    if (!await SendCommandCoreAsync("save-off", cancellationToken).ConfigureAwait(false))
                        throw new InvalidOperationException("Could not disable automatic world saving before the operation.");
                    savingDisabled = true;
                }
                var save = await SaveCoreAsync(transitionState: true, cancellationToken).ConfigureAwait(false);
                if (!save.Success)
                    throw new InvalidOperationException($"{operationName} cancelled because save was not confirmed: {save.Message}");
            }
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (savingDisabled)
                _ = await SendCommandCoreAsync("save-on", CancellationToken.None).ConfigureAwait(false);
            operationGate.Release();
        }
    }

    private async Task RequestStopIntentAsync(LifecycleIntentKind intent)
    {
        lock (operationCancellationGate)
        {
            lastIntent = intent;
            intentionalStop = true;
            Interlocked.Increment(ref lifecycleGeneration);
            if (!activeOperationName.Equals("Stop", StringComparison.Ordinal))
                activeOperationCancellation?.Cancel();
        }

        try
        {
            await store.SetRunningStateAsync(Definition.Id, CurrentAutostartMode,
                HasExactOwnedProcessAlive(), intent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(exception, "Could not persist stop intent for {Server}", Definition.Name);
        }
    }

    private async Task PersistStopObservationAsync()
    {
        try
        {
            await store.SetRunningStateAsync(Definition.Id, CurrentAutostartMode,
                HasExactOwnedProcessAlive(), lastIntent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(exception, "Could not persist final stop observation for {Server}", Definition.Name);
        }
    }

    private AutostartMode CurrentAutostartMode =>
        Definition.AutoStart ? AutostartMode.AgentStart : autostartMode;

    /// <summary>
    /// Applies a reversible data mutation while stopped. When explicitly requested for a running
    /// server, the Agent saves and stops it, applies the mutation, validates a full restart, and
    /// restores the previous data if startup fails. This keeps restart ownership out of WebUI.
    /// </summary>
    public async Task<T> RunExclusiveRestartableDataOperationAsync<T>(
        string operationName,
        bool restartIfRunning,
        Func<CancellationToken, Task<T>> operation,
        Func<T, CancellationToken, Task> rollback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(rollback);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var wasRunning = State == ServerState.Running;
        try
        {
            if (State is not ServerState.Running and not ServerState.Stopped)
                throw new InvalidOperationException($"Cannot {operationName} while the server is {State}.");
            if (wasRunning && !restartIfRunning)
                throw new InvalidOperationException(
                    $"Stop the server before {operationName}, or explicitly choose Apply and restart now.");

            if (wasRunning)
            {
                lastIntent = LifecycleIntentKind.SafeRestart;
                Interlocked.Increment(ref lifecycleGeneration);
                lifecycle.TransitionTo(ServerState.Restarting);
                var save = await SaveCoreAsync(transitionState: false, cancellationToken).ConfigureAwait(false);
                if (!save.Success)
                {
                    SafeTransition(ServerState.Running);
                    throw new InvalidOperationException(
                        $"{operationName} was cancelled because save was not confirmed: {save.Message}");
                }
                var stop = await StopCoreAsync(saveFirst: false, cancellationToken).ConfigureAwait(false);
                if (!stop.Success)
                    throw new InvalidOperationException(
                        $"{operationName} was cancelled because the server did not stop: {stop.Message}");
            }

            T result;
            try
            {
                result = await operation(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (wasRunning && State == ServerState.Stopped)
                    _ = await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (!wasRunning)
                return result;

            var start = await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (start.Success)
                return result;

            if (State != ServerState.Stopped)
                await TerminateCurrentProcessTreeAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await rollback(result, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                throw new InvalidOperationException(
                    $"The server did not restart after {operationName}, and automatic rollback also failed. " +
                    $"The changed JAR and recovery evidence were preserved. Startup: {start.Message} " +
                    $"Rollback: {rollbackFailure.Message}", rollbackFailure);
            }

            var restoredStart = await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(restoredStart.Success
                ? $"The changed plugin prevented a healthy restart after {operationName}. ChunkPilot restored the previous JAR and restarted the server."
                : $"The changed plugin prevented a healthy restart after {operationName}. ChunkPilot restored the previous JAR, " +
                  $"but the server still did not start: {restoredStart.Message}");
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<UpdateExecutionResult> RunExclusivePackUpdateAsync(
        UpdateInstallRequest request,
        Func<ServerDefinition, CancellationToken, Task<PreparedPackUpdate>> apply,
        Func<ServerDefinition, VersionSnapshot, Guid, CancellationToken, Task> rollback,
        Func<PreparedPackUpdate, CancellationToken, Task> finalize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(rollback);
        ArgumentNullException.ThrowIfNull(finalize);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        var previousDefinition = Definition;
        var wasRunning = State == ServerState.Running;
        try
        {
            if (State is not ServerState.Running and not ServerState.Stopped and not ServerState.Crashed)
                throw new InvalidOperationException($"Cannot update while the server is {State}.");
            if (wasRunning)
            {
                var countdown = Math.Clamp(request.PlayerCountdownSeconds, 0, 300);
                if (countdown > 0)
                {
                    _ = await SendCommandCoreAsync(
                        $"say Server pack update begins in {countdown} seconds. The server will restart.",
                        cancellationToken).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromSeconds(countdown), cancellationToken).ConfigureAwait(false);
                }
                var save = await SaveCoreAsync(transitionState: true, cancellationToken).ConfigureAwait(false);
                if (!save.Success)
                    throw new InvalidOperationException($"Update cancelled because save was not confirmed: {save.Message}");
                var stop = await StopCoreAsync(saveFirst: false, cancellationToken).ConfigureAwait(false);
                if (!stop.Success)
                    throw new InvalidOperationException($"Update cancelled because the server did not stop: {stop.Message}");
            }
            else if (State == ServerState.Crashed)
            {
                SafeTransition(ServerState.Stopped);
            }

            PreparedPackUpdate prepared;
            try
            {
                prepared = await apply(previousDefinition, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (wasRunning && State == ServerState.Stopped)
                    _ = await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            Definition = prepared.Result.UpdatedDefinition;
            await store.UpsertServerAsync(Definition, CancellationToken.None).ConfigureAwait(false);
            if (!request.StartForValidation)
            {
                await finalize(prepared, CancellationToken.None).ConfigureAwait(false);
                var withoutStartup = prepared.Result with
                {
                    WasRunning = wasRunning,
                    Message = prepared.Result.Message + " Startup validation was explicitly deferred."
                };
                await RecordAsync("Server-pack update", OperationResult.Ok(withoutStartup.Message),
                    timer, request.Automatic ? "Automatic update" : "Manual", CancellationToken.None).ConfigureAwait(false);
                return withoutStartup;
            }

            var start = await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (start.Success)
            {
                var reachable = await WaitForLocalStatusAsync(CancellationToken.None).ConfigureAwait(false);
                if (!reachable)
                    start = OperationResult.Fail(
                        "The server reported readiness, but its local Minecraft status endpoint did not become reachable.");
            }
            if (start.Success)
            {
                try
                {
                    if (prepared.Result.ActiveVersion is { } pending)
                        await store.UpsertVersionSnapshotAsync(pending with
                        {
                            Health = VersionHealth.PendingValidation,
                            LastStartupResult =
                                $"Readiness and local Minecraft status query succeeded at {DateTimeOffset.Now:M/d/yyyy h:mm tt}."
                        }, CancellationToken.None).ConfigureAwait(false);
                    await finalize(prepared, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
                {
                    start = OperationResult.Fail(
                        $"The updated server started, but the update transaction could not be committed: {exception.Message}");
                }
            }
            if (start.Success)
            {
                var success = prepared.Result with
                {
                    WasRunning = wasRunning,
                    Message = prepared.Result.Message + " Startup readiness succeeded; user validation is still required."
                };
                await RecordAsync("Server-pack update", OperationResult.Ok(success.Message),
                    timer, request.Automatic ? "Automatic update" : "Manual", CancellationToken.None).ConfigureAwait(false);
                return success;
            }

            await TerminateCurrentProcessTreeAsync(CancellationToken.None).ConfigureAwait(false);
            if (prepared.Result.ActiveVersion is { } failedVersion)
                await store.UpsertVersionSnapshotAsync(failedVersion with
                {
                    IsActive = true,
                    Health = VersionHealth.Failed,
                    LastStartupResult = start.Message
                }, CancellationToken.None).ConfigureAwait(false);
            var previous = prepared.Result.PreviousSnapshot
                           ?? throw new InvalidOperationException("Automatic rollback snapshot is missing.");
            await rollback(previousDefinition, previous, request.OperationId, CancellationToken.None)
                .ConfigureAwait(false);
            await finalize(prepared, CancellationToken.None).ConfigureAwait(false);
            Definition = previousDefinition;
            await store.UpsertServerAsync(Definition, CancellationToken.None).ConfigureAwait(false);
            var restart = wasRunning
                ? await StartCoreAsync(CancellationToken.None).ConfigureAwait(false)
                : OperationResult.Ok("The previous version was stopped before the update and remains stopped.");
            var rolledBack = prepared.Result with
            {
                Success = false,
                RolledBack = true,
                WasRunning = wasRunning,
                UpdatedDefinition = previousDefinition,
                Message =
                    $"Updated version failed startup: {start.Message} Automatic rollback completed. " +
                    (restart.Success ? restart.Message : $"Previous-version restart also failed: {restart.Message}")
            };
            await RecordAsync("Server-pack update", OperationResult.Fail(rolledBack.Message),
                timer, request.Automatic ? "Automatic update" : "Manual", CancellationToken.None).ConfigureAwait(false);
            return rolledBack;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<OperationResult> RunExclusiveVersionRollbackAsync(
        string targetVersion,
        Func<CancellationToken, Task> rollback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rollback);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var trackedOperation = TrackOperation(cancellationToken);
        cancellationToken = trackedOperation.Token;
        var timer = Stopwatch.StartNew();
        var wasRunning = State == ServerState.Running;
        try
        {
            if (State is not ServerState.Running and not ServerState.Stopped and not ServerState.Crashed)
                throw new InvalidOperationException($"Cannot roll back while the server is {State}.");
            if (wasRunning)
            {
                _ = await SendCommandCoreAsync(
                    $"say ChunkPilot is rolling back the server pack to {targetVersion}. The server will restart.",
                    cancellationToken).ConfigureAwait(false);
                var save = await SaveCoreAsync(transitionState: true, cancellationToken).ConfigureAwait(false);
                if (!save.Success)
                    throw new InvalidOperationException($"Rollback cancelled because save was not confirmed: {save.Message}");
                var stop = await StopCoreAsync(saveFirst: false, cancellationToken).ConfigureAwait(false);
                if (!stop.Success)
                    throw new InvalidOperationException($"Rollback cancelled because the server did not stop: {stop.Message}");
            }
            else if (State == ServerState.Crashed)
            {
                SafeTransition(ServerState.Stopped);
            }

            try
            {
                await rollback(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (wasRunning && State == ServerState.Stopped)
                    _ = await StartCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            var restart = wasRunning
                ? await StartCoreAsync(CancellationToken.None).ConfigureAwait(false)
                : OperationResult.Ok($"Version {targetVersion} is active and remains stopped.");
            var result = restart.Success
                ? OperationResult.Ok(wasRunning
                    ? $"Rolled back to {targetVersion} and restarted successfully."
                    : restart.Message)
                : OperationResult.Fail($"Rollback files were restored, but startup failed: {restart.Message}");
            await RecordAsync("Version rollback", result, timer, "Manual", CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<bool> WaitForLocalStatusAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(
            Math.Clamp(Definition.StartupTimeoutSeconds / 2, 15, 60));
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await statusClient.QueryDetailedAsync(
                    "127.0.0.1", Definition.Port, Definition.MinecraftVersion, cancellationToken)
                .ConfigureAwait(false);
            if (status is not null)
            {
                onlinePlayers = status.Online;
                maxPlayers = status.Maximum;
                playerStatus = status;
                return true;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    public ServerSnapshot Snapshot(int consoleLines = 500)
    {
        StatisticsSample? current;
        IReadOnlyList<StatisticsSample> recent;
        lock (samples)
        {
            current = samples.LastOrDefault();
            recent = samples.ToArray();
        }
        Process? currentProcess;
        lock (processGate)
            currentProcess = process;
        return new ServerSnapshot
        {
            Definition = Definition,
            State = State,
            RootProcessId = currentProcess is { HasExited: false } ? currentProcess.Id : null,
            StartedAt = startedAt,
            Uptime = startedAt is { } value && State is not ServerState.Stopped ? DateTimeOffset.Now - value : TimeSpan.Zero,
            LastExitCode = lastExitCode,
            LastError = lastError,
            LastStartReachedReadiness = lastStartReachedReadiness,
            LastSaveAt = lastSaveAt,
            LastBackupAt = lastBackupAt,
            ConsoleConnected = currentProcess is { HasExited: false } && currentProcess.StartInfo.RedirectStandardInput,
            OnlinePlayers = onlinePlayers,
            MaxPlayers = maxPlayers,
            PlayerStatus = playerStatus,
            OnlinePlayerNames = OnlinePlayerNames,
            PlayerAccessStamp = PlayerAccessStamp(),
            CurrentStatistics = current,
            RecentStatistics = recent,
            Console = console.Snapshot(consoleLines),
            LastCrashAnalysis = lastCrashAnalysis
        };
    }

    private async Task<OperationResult> StartCoreAsync(CancellationToken cancellationToken)
    {
        if (pendingDefinition is { } pending)
        {
            Definition = pending;
            pendingDefinition = null;
        }
        lifecycle.TransitionTo(ServerState.Starting);
        lastStartReachedReadiness = false;
        if (!File.Exists(Definition.Executable))
        {
            RecordStartupFailure($"Launch executable does not exist: {Definition.Executable}", 400);
            SafeTransition(ServerState.Crashed);
            return OperationResult.Fail(LastStartupFailure());
        }
        if (!Directory.Exists(Definition.WorkingDirectory))
        {
            RecordStartupFailure($"Working directory does not exist: {Definition.WorkingDirectory}", 400);
            SafeTransition(ServerState.Crashed);
            return OperationResult.Fail(LastStartupFailure());
        }

        // Stop waits for the process, but the redirected streams and monitor can finish a few
        // milliseconds later. Do not let that old attempt observe or mutate the next attempt's state.
        if (monitorTask is not null)
            await IgnoreCancellationAsync(monitorTask).ConfigureAwait(false);
        if (stdoutTask is not null)
            await IgnoreCancellationAsync(stdoutTask).ConfigureAwait(false);
        if (stderrTask is not null)
            await IgnoreCancellationAsync(stderrTask).ConfigureAwait(false);

        intentionalStop = false;
        lastError = "";
        lock (failureGate)
            startupFailure = null;
        // Nobody is connected to a server that has not started yet. Carrying the previous run's set
        // forward would show players as online who are not.
        ClearOnlinePlayers();
        readiness = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attemptReadiness = readiness;
        var startInfo = new ProcessStartInfo
        {
            FileName = Definition.Executable,
            Arguments = ServerLaunchPolicy.EnsureNoGui(
                Definition.Arguments,
                Definition.Ecosystem,
                Definition.RunInBackground),
            WorkingDirectory = Definition.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var pair in Definition.Environment)
            startInfo.Environment[pair.Key] = pair.Value;
        var newProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!newProcess.Start())
            {
                lifecycle.TransitionTo(ServerState.Crashed);
                return OperationResult.Fail("Windows did not start the configured process.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            lifecycle.TransitionTo(ServerState.Crashed);
            lastError = exception.Message;
            return OperationResult.Fail($"Launch failed: {exception.Message}");
        }

        lock (processGate)
            process = newProcess;
        lock (samples)
            samples.Clear();
        statistics.BeginProcessAttempt(newProcess.Id);
        lastPersistedStatistics = DateTimeOffset.MinValue;
        startedAt = DateTimeOffset.Now;
        try
        {
            await store.UpsertProcessIdentityAsync(new ProcessIdentity
            {
                ServerId = Definition.Id,
                ProcessId = newProcess.Id,
                ProcessStartTime = new DateTimeOffset(newProcess.StartTime),
                ProcessCreationTicks = ProcessCreationIdentity.Of(newProcess.SafeHandle),
                ExecutablePath = Path.GetFullPath(Definition.Executable),
                WorkingDirectory = Path.GetFullPath(Definition.WorkingDirectory),
                CommandSignature = ProcessIdentityPolicy.Signature(
                    Definition.Executable, startInfo.Arguments, Definition.WorkingDirectory),
                ParentProcessId = Environment.ProcessId,
                ControlState = ProcessControlState.RunningControlled
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            logger.LogWarning(exception, "Could not persist process identity for {Server}", Definition.Name);
        }
        console.Add("ChunkPilot", $"Started process {newProcess.Id}: {Definition.Executable} {SecretRedactor.Redact(startInfo.Arguments)}");
        stdoutTask = PumpAsync(newProcess.StandardOutput, "stdout", attemptReadiness, lifetime.Token);
        stderrTask = PumpAsync(newProcess.StandardError, "stderr", attemptReadiness, lifetime.Token);
        monitorTask = MonitorAsync(newProcess, attemptReadiness, stdoutTask, stderrTask, lifetime.Token);

        try
        {
            await attemptReadiness.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(5, Definition.StartupTimeoutSeconds)), cancellationToken)
                .ConfigureAwait(false);
            lifecycle.TransitionTo(ServerState.Running);
            hasStartedSuccessfully = true;
            lastStartReachedReadiness = true;
            if (lastIntent is LifecycleIntentKind.SafeRestart or LifecycleIntentKind.ScheduledRestart or
                LifecycleIntentKind.UpdateRestart or LifecycleIntentKind.CrashRecovery)
                lastIntent = LifecycleIntentKind.None;
            await store.SetRunningStateAsync(Definition.Id, CurrentAutostartMode,
                true, lastIntent, CancellationToken.None).ConfigureAwait(false);
            await ApplyPendingGamerulesAsync().ConfigureAwait(false);
            return OperationResult.Ok("Server reported readiness.");
        }
        catch (TimeoutException)
        {
            if (newProcess.HasExited)
            {
                SafeTransition(ServerState.Crashed);
                return OperationResult.Fail(StartupFailureMessage(newProcess.ExitCode));
            }
            SafeTransition(ServerState.Unresponsive);
            RecordStartupFailure("Startup timeout expired before a configured readiness message was observed.", 200);
            return OperationResult.Fail(LastStartupFailure());
        }
        catch (InvalidOperationException exception)
        {
            SafeTransition(ServerState.Crashed);
            RecordStartupFailure($"Server failed before readiness: {exception.Message}", 100);
            return OperationResult.Fail(newProcess.HasExited
                ? StartupFailureMessage(newProcess.ExitCode)
                : LastStartupFailure());
        }
    }

    private async Task<OperationResult> SaveCoreAsync(bool transitionState, CancellationToken cancellationToken)
    {
        if (transitionState)
            lifecycle.TransitionTo(ServerState.Saving);
        saveConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!await SendCommandCoreAsync(Definition.SaveCommand, cancellationToken).ConfigureAwait(false))
        {
            if (transitionState)
                SafeTransition(ServerState.Running);
            return OperationResult.Fail("Server stdin is disconnected; save command was not sent.");
        }

        try
        {
            await saveConfirmation.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(2, Definition.SaveTimeoutSeconds)), cancellationToken)
                .ConfigureAwait(false);
            lastSaveAt = DateTimeOffset.Now;
            if (transitionState)
                lifecycle.TransitionTo(ServerState.Running);
            return OperationResult.Ok($"Save confirmed after '{Definition.SaveCommand}'.");
        }
        catch (TimeoutException) when (!Definition.SaveCommand.Equals(Definition.SaveFallbackCommand, StringComparison.OrdinalIgnoreCase))
        {
            saveConfirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await SendCommandCoreAsync(Definition.SaveFallbackCommand, cancellationToken).ConfigureAwait(false);
            try
            {
                await saveConfirmation.Task.WaitAsync(TimeSpan.FromSeconds(Math.Max(2, Definition.SaveTimeoutSeconds)), cancellationToken)
                    .ConfigureAwait(false);
                lastSaveAt = DateTimeOffset.Now;
                if (transitionState)
                    lifecycle.TransitionTo(ServerState.Running);
                return OperationResult.Ok($"Flush confirmation timed out; fallback '{Definition.SaveFallbackCommand}' was confirmed.");
            }
            catch (TimeoutException)
            {
                if (transitionState)
                    SafeTransition(ServerState.Running);
                return OperationResult.Fail("Save commands were sent, but no recognized confirmation arrived before timeout.");
            }
        }
        catch (TimeoutException)
        {
            if (transitionState)
                SafeTransition(ServerState.Running);
            return OperationResult.Fail("Save command was sent, but no recognized confirmation arrived before timeout.");
        }
    }

    private async Task ApplyPendingGamerulesAsync()
    {
        var key = $"pending-gamerules:{Definition.Id:D}";
        var json = await store.GetSettingAsync(key, CancellationToken.None).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return;
        var changes = JsonSerializer.Deserialize<Dictionary<string, string>>(json, ProtocolJson.Options);
        if (changes is null)
            return;
        foreach (var change in changes)
        {
            if (!await SendCommandCoreAsync($"gamerule {change.Key} {change.Value}", CancellationToken.None)
                    .ConfigureAwait(false))
                return;
        }
        await store.SetSettingAsync(key, "", CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<OperationResult> StopCoreAsync(bool saveFirst, CancellationToken cancellationToken)
    {
        if (saveFirst && State is ServerState.Running or ServerState.Restarting)
        {
            var save = await SaveCoreAsync(transitionState: State == ServerState.Running, cancellationToken).ConfigureAwait(false);
            if (!save.Success)
                return OperationResult.Fail($"Safe stop was cancelled because save was not confirmed: {save.Message}");
        }
        SafeTransition(ServerState.Stopping);
        intentionalStop = true;
        Process? current;
        lock (processGate)
            current = process;
        if (current is null || current.HasExited)
        {
            SafeTransition(ServerState.Stopped);
            return OperationResult.Ok("Server is stopped.");
        }
        var portWasListening = IsTcpPortListening(Definition.Port);
        if (!await SendCommandCoreAsync(Definition.StopCommand, cancellationToken).ConfigureAwait(false))
        {
            SafeTransition(ServerState.Unresponsive);
            return OperationResult.Fail("Server stdin is disconnected; stop command was not sent.", requiresForce: true);
        }
        try
        {
            var launcherOnly = await WaitForServerExitOrLauncherOnlyAsync(
                    current, portWasListening, cancellationToken)
                .ConfigureAwait(false);
            if (launcherOnly)
            {
                console.Add("ChunkPilot",
                    "Minecraft exited and released its port; closing the leftover launcher script.");
                ProcessTree.Kill(current.Id);
                await current.WaitForExitAsync(cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            await WaitForProcessTreeExitAsync(current.Id, cancellationToken).ConfigureAwait(false);
            if (!await WaitForPortReleaseAsync(Definition.Port, cancellationToken).ConfigureAwait(false))
            {
                SafeTransition(ServerState.Unresponsive);
                return OperationResult.Fail(
                    $"The server process exited, but port {Definition.Port} is still listening. " +
                    "Another local process is holding the port.");
            }
            SafeTransition(ServerState.Stopped);
            return OperationResult.Ok($"Server exited cleanly and released port {Definition.Port}.");
        }
        catch (TimeoutException)
        {
            SafeTransition(ServerState.Unresponsive);
            return OperationResult.Fail(
                $"The server exceeded its {Definition.ShutdownTimeoutSeconds}-second graceful shutdown timeout.",
                requiresForce: true);
        }
        catch (OperationCanceledException)
        {
            if (current.HasExited)
            {
                SafeTransition(ServerState.Stopped);
                return OperationResult.Ok(
                    "The stop operation was interrupted after the server process had already exited.");
            }
            SafeTransition(ServerState.Unresponsive);
            return OperationResult.Fail(
                "The stop operation was interrupted while the exact server process was still running. " +
                "Retry Stop or use exact-process recovery.", requiresForce: true);
        }
    }

    private async Task<bool> SendCommandCoreAsync(string command, CancellationToken cancellationToken)
    {
        Process? current;
        lock (processGate)
            current = process;
        if (current is null || current.HasExited)
            return false;
        try
        {
            console.Add("command", $"> {command}");
            await current.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
            await current.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            lastError = exception.Message;
            return false;
        }
    }

    private async Task TerminateCurrentProcessTreeAsync(CancellationToken cancellationToken)
    {
        Process? current;
        lock (processGate)
            current = process;
        if (current is null || current.HasExited)
        {
            SafeTransition(ServerState.Stopped);
            return;
        }
        intentionalStop = true;
        ProcessTree.Kill(current.Id);
        try
        {
            await current.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("The failed update process tree could not be terminated for rollback.");
        }
        SafeTransition(ServerState.Stopped);
    }

    private async Task PumpAsync(
        StreamReader reader,
        string streamName,
        TaskCompletionSource<bool> attemptReadiness,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                console.Add(streamName, line);
                await AppendRollingLogAsync(streamName, line, cancellationToken).ConfigureAwait(false);
                if (Regex.IsMatch(line, Definition.ReadinessPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    attemptReadiness.TrySetResult(true);
                if (GameServerRuntimeProfiles.IsSaveConfirmation(Definition, line))
                    saveConfirmation?.TrySetResult(true);
                if (GameServerRuntimeProfiles.For(Definition).TracksMinecraftJoinLeaveLines)
                    TrackPlayerPresence(line);
                OfferToObservers(line);
                if (line.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
                    RecordStartupFailure("Java ran out of memory while starting. Increase the server memory limit or reduce its workload.", 300);
                if (line.Contains("FAILED TO BIND TO PORT", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Address already in use", StringComparison.OrdinalIgnoreCase))
                    RecordStartupFailure($"Port {Definition.Port} is already in use. Stop the other server or choose another port.", 400);
                else if (KnownStartupFailure(line) is { } known)
                    RecordStartupFailure(known, 250);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Console stream ended for {Server}", Definition.Name);
        }
    }

    private async Task MonitorAsync(
        Process watched,
        TaskCompletionSource<bool> attemptReadiness,
        Task stdoutPump,
        Task stderrPump,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!watched.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var sample = statistics.SampleProcessTree(watched.Id);
                lock (samples)
                {
                    samples.Add(sample);
                    var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                    samples.RemoveAll(item => item.Timestamp < cutoff);
                    if (samples.Count > 2_000)
                    {
                        var keep = StatisticsDownsampler.Downsample(samples, 1_000);
                        samples.Clear();
                        samples.AddRange(keep);
                    }
                }
                if (sample.Timestamp - lastPersistedStatistics > TimeSpan.FromMinutes(1))
                {
                    lastPersistedStatistics = sample.Timestamp;
                    await store.RecordHourlyStatisticsAsync(Definition.Id, sample, cancellationToken).ConfigureAwait(false);
                }
                if (DateTimeOffset.UtcNow - lastPlayerQuery > TimeSpan.FromSeconds(10))
                {
                    lastPlayerQuery = DateTimeOffset.UtcNow;
                    var runtimeProfile = GameServerRuntimeProfiles.For(Definition);
                    var status = runtimeProfile.UsesMinecraftStatusProtocol
                        ? await statusClient.QueryDetailedAsync(
                                "127.0.0.1", Definition.Port, Definition.MinecraftVersion, cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                    if (status is not null)
                    {
                        onlinePlayers = status.Online;
                        maxPlayers = status.Maximum;
                        playerStatus = status;
                    }
                    else
                    {
                        var rosterCount = OnlinePlayerNames.Count;
                        if (rosterCount > 0)
                        {
                            onlinePlayers = rosterCount;
                            maxPlayers = null;
                            playerStatus = new PlayerStatusEvidence
                            {
                                Online = rosterCount,
                                Source = PlayerStatusSource.ConsoleRoster,
                                Exact = false,
                                CheckedAt = DateTimeOffset.UtcNow,
                                Detail = "Console-derived roster; the server-list status protocol did not answer."
                            };
                        }
                        else if (playerStatus.Exact &&
                                 DateTimeOffset.UtcNow - playerStatus.CheckedAt <= TimeSpan.FromSeconds(30))
                        {
                            playerStatus = playerStatus with
                            {
                                Source = PlayerStatusSource.LastExactStatus,
                                Exact = false,
                                Detail = "Last exact server-list count; a newer status check did not answer."
                            };
                        }
                        else if (!runtimeProfile.UsesMinecraftStatusProtocol)
                        {
                            onlinePlayers = null;
                            maxPlayers = null;
                            playerStatus = new PlayerStatusEvidence
                            {
                                Source = PlayerStatusSource.Unsupported,
                                Exact = false,
                                CheckedAt = DateTimeOffset.UtcNow,
                                Detail = runtimeProfile.UnavailablePlayerStatusDetail
                            };
                        }
                        else
                        {
                            onlinePlayers = null;
                            maxPlayers = null;
                            playerStatus = new PlayerStatusEvidence
                            {
                                Source = PlayerStatusSource.StatusCheckFailed,
                                Exact = false,
                                CheckedAt = DateTimeOffset.UtcNow,
                                Detail = "Player status is unavailable; no count has been inferred as zero."
                            };
                        }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            await watched.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
            lastExitCode = watched.ExitCode;
            ClearOnlinePlayers();
            await store.RemoveProcessIdentityAsync(Definition.Id, CancellationToken.None).ConfigureAwait(false);
            attemptReadiness.TrySetException(new InvalidOperationException($"Process exited with code {watched.ExitCode}."));
            if (intentionalStop)
            {
                // StopCoreAsync owns the final transition for an intentional stop. The Java process
                // exiting is not enough: success also means its process tree is gone and the
                // configured listening port has actually been released.
                await store.SetRunningStateAsync(Definition.Id, CurrentAutostartMode,
                    false, lastIntent, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                RecordStartupFailure($"Server process exited unexpectedly with code {watched.ExitCode}.", 50);
                SafeTransition(ServerState.Crashed);
                await AnalyzeUnexpectedExitAsync(watched.ExitCode, CancellationToken.None).ConfigureAwait(false);
                console.Add("ChunkPilot", LastStartupFailure());
                if (startedAt is { } started &&
                    DateTimeOffset.Now - started >= TimeSpan.FromMinutes(5))
                    crashAttempts = 0;
                if (LifecycleIntentPolicy.ShouldCrashRestart(lastIntent,
                        Definition.CrashRestartEnabled, hasStartedSuccessfully, crashAttempts,
                        Definition.CrashRestartLimit))
                    _ = ScheduleCrashRestartAsync(Volatile.Read(ref lifecycleGeneration));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogError(exception, "Process monitor failed for {Server}", Definition.Name);
        }
    }

    private void RecordStartupFailure(string message, int priority)
    {
        lock (failureGate)
        {
            if (startupFailure is not null && startupFailure.Priority > priority)
                return;
            startupFailure = new StartupFailureEvidence(message, priority);
            lastError = message;
        }
    }

    private string LastStartupFailure()
    {
        lock (failureGate)
            return startupFailure?.Message ?? lastError;
    }

    private string StartupFailureMessage(int exitCode)
    {
        lock (failureGate)
        {
            if (startupFailure is { Priority: > 100 } evidence)
                return evidence.Message;
        }
        var generic = $"Server exited during startup with code {exitCode}. Check the console log for the first reported error.";
        RecordStartupFailure(generic, 100);
        return generic;
    }

    private static string? KnownStartupFailure(string line)
    {
        if (line.Contains("Unable to access jarfile", StringComparison.OrdinalIgnoreCase))
            return "The configured server file could not be opened. Check the launch file and working folder.";
        if (line.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase))
            return "This server needs a different Java version. Review the server runtime setting.";
        if (line.Contains("Invalid or corrupt jarfile", StringComparison.OrdinalIgnoreCase))
            return "The configured server file is invalid or damaged. Replace or reinstall that server file.";
        if (line.Contains("Could not find or load main class", StringComparison.OrdinalIgnoreCase))
            return "The configured server main class could not be loaded. Check the launch arguments and server files.";
        return null;
    }

    private sealed record StartupFailureEvidence(string Message, int Priority);

    private async Task ScheduleCrashRestartAsync(int expectedGeneration)
    {
        var attempt = Interlocked.Increment(ref crashAttempts);
        if (attempt > Math.Max(1, Definition.CrashRestartLimit))
        {
            console.Add("ChunkPilot", "Crash-loop protection stopped automatic restarts.");
            return;
        }
        var delay = TimeSpan.FromSeconds(Math.Min(300,
            Math.Max(1, Definition.CrashRestartDelaySeconds) * Math.Pow(2, attempt - 1)));
        console.Add("ChunkPilot", $"Crash restart attempt {attempt}/{Definition.CrashRestartLimit} in {delay.TotalSeconds:F0} seconds.");
        try
        {
            await Task.Delay(delay, lifetime.Token).ConfigureAwait(false);
            if (expectedGeneration != Volatile.Read(ref lifecycleGeneration) ||
                lastIntent is LifecycleIntentKind.ManualStop or LifecycleIntentKind.ApplicationExit or
                    LifecycleIntentKind.WindowsShutdown)
                return;
            await StartAsync("Crash recovery", lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task WaitForProcessTreeExitAsync(int rootProcessId, CancellationToken cancellationToken)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var alive = ProcessTree.GetDescendantsAndSelf(rootProcessId).Any(id =>
            {
                try { using var child = Process.GetProcessById(id); return !child.HasExited; }
                // The exact owned root process has already exited. A PID from the Toolhelp snapshot
                // can disappear or be reused by a protected process before it is opened; neither is
                // evidence that an owned child remains. Port release is verified separately below.
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                                  System.ComponentModel.Win32Exception) { return false; }
            });
            if (!alive)
                return;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("A child process remained after the launcher exited.");
    }

    private async Task<bool> WaitForServerExitOrLauncherOnlyAsync(
        Process current,
        bool portWasListening,
        CancellationToken cancellationToken)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(Math.Max(5, Definition.ShutdownTimeoutSeconds));
        var javaWasObserved = HasLiveJavaProcessInTree(current.Id);
        while (DateTimeOffset.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.HasExited)
                return false;
            var javaIsRunning = HasLiveJavaProcessInTree(current.Id);
            javaWasObserved |= javaIsRunning;
            var listenerWasReleased = portWasListening && !IsTcpPortListening(Definition.Port);
            if (IsKnownLauncherWrapper(current) &&
                !javaIsRunning &&
                (javaWasObserved || listenerWasReleased))
                return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException();
    }

    private static bool IsKnownLauncherWrapper(Process process)
    {
        try
        {
            return process.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasLiveJavaProcessInTree(int rootProcessId)
    {
        foreach (var processId in ProcessTree.GetDescendantsAndSelf(rootProcessId))
        {
            try
            {
                using var candidate = Process.GetProcessById(processId);
                if (!candidate.HasExited &&
                    (candidate.ProcessName.Equals("java", StringComparison.OrdinalIgnoreCase) ||
                     candidate.ProcessName.Equals("javaw", StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                              System.ComponentModel.Win32Exception) { }
        }
        return false;
    }

    private static async Task<bool> WaitForPortReleaseAsync(int port, CancellationToken cancellationToken)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsTcpPortListening(port))
                return true;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        return !IsTcpPortListening(port);
    }

    private static bool IsTcpPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch (NetworkInformationException)
        {
            // If Windows cannot provide its listener table, do not turn a clean process exit into a
            // false failure. The process-tree check above remains authoritative in that rare case.
            return false;
        }
    }

    private async Task<OperationResult> ForceTerminateDetachedAsync(
        ProcessIdentity identity,
        CancellationToken cancellationToken)
    {
        Process? detachedProcess = null;
        try
        {
            detachedProcess = Process.GetProcessById(identity.ProcessId);
            if (detachedProcess.HasExited)
                return await FinishMissingDetachedProcessAsync(cancellationToken).ConfigureAwait(false);
            var executable = detachedProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
                return OperationResult.Fail(
                    $"ChunkPilot refused to terminate PID {identity.ProcessId} because Windows did not expose its executable path.");
            if (!ProcessIdentityPolicy.MatchesProcessInstance(
                    identity,
                    detachedProcess.Id,
                    ProcessCreationIdentity.Of(detachedProcess.SafeHandle),
                    executable,
                    out var reason))
            {
                return OperationResult.Fail(
                    $"ChunkPilot refused to terminate PID {identity.ProcessId} because its identity changed: {reason}");
            }

            intentionalStop = true;
            ProcessTree.Kill(detachedProcess.Id);
            await detachedProcess.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            if (!await WaitForPortReleaseAsync(Definition.Port, cancellationToken).ConfigureAwait(false))
            {
                return OperationResult.Fail(
                    $"The verified detached server process exited, but port {Definition.Port} is still listening. " +
                    "Another local process is holding the port.");
            }
            await ClearDetachedIdentityAsync(cancellationToken).ConfigureAwait(false);
            SafeTransition(ServerState.Stopped);
            return OperationResult.Ok(
                $"Verified detached server process {identity.ProcessId} was terminated and released port {Definition.Port}.");
        }
        catch (ArgumentException)
        {
            return await FinishMissingDetachedProcessAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return OperationResult.Fail(
                $"Verified detached server process {identity.ProcessId} did not terminate within 15 seconds.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return OperationResult.Fail(
                $"ChunkPilot could not verify or terminate detached process {identity.ProcessId}: {exception.Message}");
        }
        finally
        {
            detachedProcess?.Dispose();
        }
    }

    private async Task<OperationResult> FinishMissingDetachedProcessAsync(CancellationToken cancellationToken)
    {
        if (await WaitForPortReleaseAsync(Definition.Port, cancellationToken).ConfigureAwait(false))
        {
            await ClearDetachedIdentityAsync(cancellationToken).ConfigureAwait(false);
            SafeTransition(ServerState.Stopped);
            return OperationResult.Ok(
                $"The detached server process had already exited and port {Definition.Port} is free.");
        }
        return OperationResult.Fail(
            $"The recorded detached process no longer exists, but port {Definition.Port} is held by another local process.");
    }

    private async Task ClearDetachedIdentityAsync(CancellationToken cancellationToken)
    {
        await store.RemoveProcessIdentityAsync(Definition.Id, cancellationToken).ConfigureAwait(false);
        await store.SetRunningStateAsync(Definition.Id, CurrentAutostartMode,
            false, LifecycleIntentKind.ManualStop, cancellationToken).ConfigureAwait(false);
        detachedIdentity = null;
    }

    private async Task AppendRollingLogAsync(string streamName, string line, CancellationToken cancellationToken)
    {
        try
        {
            var directory = Path.Combine(paths.Logs, "Console");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{Definition.Id:N}-{DateTime.Now:yyyyMMdd}.log");
            await File.AppendAllTextAsync(path,
                $"{DateTimeOffset.Now:O} [{streamName}] {line}{Environment.NewLine}",
                Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Could not append console log for {Server}", Definition.Name);
        }
    }

    private async Task RecordAsync(
        string action,
        OperationResult result,
        Stopwatch timer,
        string source,
        CancellationToken cancellationToken)
    {
        await store.AddActivityAsync(new ActivityEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            ServerId = Definition.Id,
            ServerName = Definition.Name,
            Action = action,
            Result = result.Success ? "Success" : "Failed",
            DurationMilliseconds = timer.ElapsedMilliseconds,
            Error = result.Success ? "" : result.Message,
            Source = source
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A cheap value that changes whenever player access could have changed.
    /// </summary>
    /// <remarks>
    /// Two sources, because there are two ways it changes. The revision covers everything the server
    /// reports - joins, leaves, moderation replies - and the file timestamps cover an edit made outside
    /// ChunkPilot. Recomputed at most once a second so the dashboard poll cannot turn into a file-stat
    /// loop, and never reads file contents.
    /// </remarks>
    private string PlayerAccessStamp()
    {
        lock (accessStampGate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - accessStampComputedAt < TimeSpan.FromSeconds(1) && accessStamp.Length > 0)
                return WithRevision(accessStamp);
            accessStampComputedAt = now;
            var builder = new StringBuilder();
            foreach (var name in AccessFiles)
            {
                builder.Append(name).Append('=');
                try
                {
                    var path = Path.Combine(Definition.RootPath, name);
                    builder.Append(File.Exists(path)
                        ? File.GetLastWriteTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : "0");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    builder.Append('?');
                }
                builder.Append(';');
            }
            accessStamp = builder.ToString();
            return WithRevision(accessStamp);
        }
    }

    private string WithRevision(string files) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"r{PlayerAccessRevision};{files}");

    private static readonly string[] AccessFiles =
    [
        "whitelist.json", "ops.json", "banned-players.json", "banned-ips.json",
        "usercache.json", "server.properties"
    ];

    private readonly object accessStampGate = new();
    private string accessStamp = "";
    private DateTimeOffset accessStampComputedAt = DateTimeOffset.MinValue;

    private void OfferToObservers(string line)
    {
        ConsoleLineObserver[] current;
        lock (lineObservers)
        {
            if (lineObservers.Count == 0)
                return;
            current = lineObservers.ToArray();
        }
        foreach (var observer in current)
            observer.Offer(line);
    }

    /// <summary>
    /// Keeps the connected-player set in step with what the server reports.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server list ping gives a count but no names, so per-player online state has to come from the
    /// join and leave lines. Matching is deliberately narrow - the exact wording Vanilla uses, with the
    /// name immediately before it - so a chat message quoting "joined the game" cannot invent a player.
    /// </para>
    /// <para>
    /// A moderation reply also counts as a change, which is what makes an <c>op</c> or <c>whitelist</c>
    /// command typed straight into the Console reach the Access page.
    /// </para>
    /// </remarks>
    private void TrackPlayerPresence(string line)
    {
        var join = PlayerPresencePattern.Match(line);
        if (!join.Success)
        {
            if (MentionsAccessChange(line))
                MarkPlayerAccessChanged();
            return;
        }
        var name = join.Groups["name"].Value;
        var joined = join.Groups["verb"].Value.Equals("joined", StringComparison.OrdinalIgnoreCase);
        if (!PlayerModerationPolicy.IsValidPlayerName(name))
            return;
        var changed = false;
        lock (onlinePlayerNames)
        {
            changed = joined ? onlinePlayerNames.Add(name) : onlinePlayerNames.Remove(name);
            // A join line and a leave line are the only evidence a player was ever here. Both are
            // recorded, so a player who is online has a last-seen of now and one who has left keeps
            // the moment they left.
            if (changed)
                lastSeenByPlayer[name] = DateTimeOffset.Now;
        }
        if (changed)
        {
            if (!playerStatus.Exact || DateTimeOffset.UtcNow - playerStatus.CheckedAt > TimeSpan.FromSeconds(30))
            {
                var rosterCount = OnlinePlayerNames.Count;
                onlinePlayers = rosterCount;
                maxPlayers = null;
                playerStatus = new PlayerStatusEvidence
                {
                    Online = rosterCount,
                    Source = PlayerStatusSource.ConsoleRoster,
                    Exact = false,
                    CheckedAt = DateTimeOffset.UtcNow,
                    Detail = "Console-derived roster; exact server-list status is unavailable."
                };
            }
            MarkPlayerAccessChanged();
        }
    }

    private void ClearOnlinePlayers()
    {
        var had = false;
        lock (onlinePlayerNames)
        {
            had = onlinePlayerNames.Count > 0;
            // The server going away ends every session it was holding, and that moment is the last
            // time those players were seen.
            var now = DateTimeOffset.Now;
            foreach (var name in onlinePlayerNames)
                lastSeenByPlayer[name] = now;
            onlinePlayerNames.Clear();
        }
        if (had)
            MarkPlayerAccessChanged();
        onlinePlayers = null;
        maxPlayers = null;
        playerStatus = new PlayerStatusEvidence
        {
            Source = PlayerStatusSource.Waiting,
            Exact = false,
            CheckedAt = DateTimeOffset.UtcNow,
            Detail = "Start the server to collect player status."
        };
    }

    private static bool MentionsAccessChange(string line) =>
        line.Contains("the whitelist", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("server operator", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Banned", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Unbanned", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Whitelist is now", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Reloaded the whitelist", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex PlayerPresencePattern = new(
        @"(?:^|:\s|\]\s)(?<name>[A-Za-z0-9_]{1,16})\s(?<verb>joined|left)\sthe\sgame\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private void SafeTransition(ServerState target)
    {
        try
        {
            if (lifecycle.CanTransitionTo(target))
                lifecycle.TransitionTo(target);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(exception, "Lifecycle transition failed for {Server}", Definition.Name);
        }
    }

    private async Task AnalyzeUnexpectedExitAsync(int exitCode, CancellationToken cancellationToken)
    {
        try
        {
            var evidence = new List<CrashEvidenceInput>();
            var consoleText = string.Join(Environment.NewLine,
                console.Snapshot(500).Select(line => $"{line.Stream}: {line.Text}"));
            if (!string.IsNullOrWhiteSpace(consoleText))
                evidence.Add(new CrashEvidenceInput("Console tail", consoleText));

            await AddKnownEvidenceAsync(evidence, "Latest log", Path.Combine("logs", "latest.log"), cancellationToken)
                .ConfigureAwait(false);
            await AddKnownEvidenceAsync(evidence, "Debug log", Path.Combine("logs", "debug.log"), cancellationToken)
                .ConfigureAwait(false);
            if (FindNewestEvidenceFile("crash-reports", "*.txt") is { } crashReport)
                await AddKnownEvidenceAsync(evidence, "Crash report", crashReport, cancellationToken).ConfigureAwait(false);
            if (FindNewestEvidenceFile("logs", "*.log", "latest.log", "debug.log") is { } loaderLog)
                await AddKnownEvidenceAsync(evidence, "Loader log", loaderLog, cancellationToken).ConfigureAwait(false);

            if (jarInventory is not null)
            {
                try
                {
                    var inventoryText = string.Join(Environment.NewLine, jarInventory.Inventory(Definition)
                        .Take(200)
                        .Select(item =>
                            $"{item.Id} {item.Version}; {(item.Enabled ? "enabled" : "disabled")}; " +
                            $"{item.Compatibility}: {item.CompatibilityReason}"));
                    if (!string.IsNullOrWhiteSpace(inventoryText))
                        evidence.Add(new CrashEvidenceInput("Add-on inventory", inventoryText));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug(exception, "Crash analysis could not inspect the add-on inventory for {Server}", Definition.Name);
                }
            }

            var java = await store.GetJavaAssignmentAsync(Definition.Id, cancellationToken).ConfigureAwait(false);
            string operationName;
            lock (operationCancellationGate)
                operationName = activeOperationName;
            var runtime = java is null
                ? Path.GetFileName(Definition.Executable)
                : $"{Path.GetFileName(java.JavaPath)} ({java.Source})";
            var identity = $"{Definition.Ecosystem} {Definition.MinecraftVersion}" +
                (string.IsNullOrWhiteSpace(Definition.LoaderVersion) ? "" : $" / {Definition.LoaderVersion}");
            var report = CrashAnalysisService.Analyze(new CrashAnalysisInput
            {
                ServerId = Definition.Id,
                ExitCode = exitCode,
                ConfiguredPort = Definition.Port,
                ReachedReadiness = lastStartReachedReadiness,
                ServerIdentity = identity,
                RuntimeIdentity = runtime,
                ActiveOperation = operationName,
                Evidence = evidence
            });
            lastCrashAnalysis = report;
            await store.RecordCrashAnalysisAsync(report, cancellationToken).ConfigureAwait(false);
            console.Add("ChunkPilot", $"Crash analysis ({ConfidenceText(report.Confidence)}): {report.Title}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                          Microsoft.Data.Sqlite.SqliteException or JsonException)
        {
            logger.LogWarning(exception, "Could not persist crash analysis for {Server}", Definition.Name);
        }
    }

    private async Task AddKnownEvidenceAsync(
        ICollection<CrashEvidenceInput> evidence,
        string source,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var text = await ReadBoundedServerTextAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(text))
            evidence.Add(new CrashEvidenceInput(source, text));
    }

    private string? FindNewestEvidenceFile(string relativeDirectory, string pattern, params string[] excludedNames)
    {
        try
        {
            var root = Path.GetFullPath(Definition.RootPath);
            var directory = Path.GetFullPath(Path.Combine(root, relativeDirectory));
            if (!IsSafeEvidencePath(root, directory) || !Directory.Exists(directory))
                return null;
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Where(path => !excludedNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                .Where(path => IsSafeEvidencePath(root, path))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(path => Path.GetRelativePath(root, path))
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(exception, "Crash analysis could not enumerate {Directory} for {Server}", relativeDirectory, Definition.Name);
            return null;
        }
    }

    private async Task<string?> ReadBoundedServerTextAsync(string relativePath, CancellationToken cancellationToken)
    {
        try
        {
            var root = Path.GetFullPath(Definition.RootPath);
            var path = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!IsSafeEvidencePath(root, path) || !File.Exists(path))
                return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            const int maximumBytes = 64 * 1024;
            if (stream.Length > maximumBytes)
                stream.Seek(-maximumBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                bufferSize: 16 * 1024, leaveOpen: false);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            if (logger.IsEnabled(LogLevel.Debug))
                logger.LogDebug(exception, "Crash analysis could not read {File} for {Server}", relativePath, Definition.Name);
            return null;
        }
    }

    private static bool IsSafeEvidencePath(string root, string path)
    {
        var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var canonicalPath = Path.GetFullPath(path);
        if (!canonicalPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase) &&
            !canonicalPath.Equals(canonicalRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return false;
        var current = canonicalRoot.TrimEnd(Path.DirectorySeparatorChar);
        try
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return false;
            foreach (var segment in Path.GetRelativePath(current, canonicalPath)
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (File.Exists(current) || Directory.Exists(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ConfidenceText(CrashConfidence confidence) => confidence switch
    {
        CrashConfidence.Confirmed => "confirmed",
        CrashConfidence.HighlyLikely => "highly likely",
        CrashConfidence.Possible => "possible",
        _ => "unknown"
    };

    private TrackedOperation TrackOperation(
        CancellationToken cancellationToken,
        [CallerMemberName] string operationName = "")
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (operationCancellationGate)
        {
            activeOperationCancellation = cancellation;
            activeOperationName = operationName.EndsWith("Async", StringComparison.Ordinal)
                ? operationName[..^5]
                : operationName;
            if (applicationExitRequested)
                cancellation.Cancel();
        }
        return new TrackedOperation(this, cancellation);
    }

    private void EndTrackedOperation(CancellationTokenSource cancellation)
    {
        lock (operationCancellationGate)
        {
            if (ReferenceEquals(activeOperationCancellation, cancellation))
            {
                activeOperationCancellation = null;
                activeOperationName = "";
            }
        }
    }

    private sealed class TrackedOperation : IDisposable
    {
        private ManagedServer? owner;
        private readonly CancellationTokenSource cancellation;

        public TrackedOperation(ManagedServer owner, CancellationTokenSource cancellation)
        {
            this.owner = owner;
            this.cancellation = cancellation;
        }

        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref owner, null);
            if (current is null)
                return;
            current.EndTrackedOperation(cancellation);
            cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (State is not ServerState.Stopped)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, Definition.ShutdownTimeoutSeconds)));
            await StopAsync(saveFirst: true, source: "Agent shutdown", timeout.Token).ConfigureAwait(false);
        }
        if (stdoutTask is not null)
            await IgnoreCancellationAsync(stdoutTask).ConfigureAwait(false);
        if (stderrTask is not null)
            await IgnoreCancellationAsync(stderrTask).ConfigureAwait(false);
        if (monitorTask is not null)
            await IgnoreCancellationAsync(monitorTask).ConfigureAwait(false);
        process?.Dispose();
        operationGate.Dispose();
        lifetime.Dispose();
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }
}
