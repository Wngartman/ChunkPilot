using System.Collections.Concurrent;
using System.Diagnostics;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

public sealed class ServerSupervisor : IAsyncDisposable
{
    public static readonly TimeSpan ApplicationExitGateDeadline = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<Guid, ManagedServer> servers = new();
    private readonly ChunkPilotStore store;
    private readonly AppDataPaths paths;
    private readonly ProcessStatisticsProvider statistics;
    private readonly MinecraftStatusClient statusClient;
    private readonly BackupService backups;
    private readonly ILoggerFactory loggerFactory;
    private readonly JarInventoryService? jarInventory;
    private IReadOnlyList<ServerRunningState> pendingRestorations = [];

    public ServerSupervisor(
        ChunkPilotStore store,
        AppDataPaths paths,
        ProcessStatisticsProvider statistics,
        MinecraftStatusClient statusClient,
        BackupService backups,
        ILoggerFactory loggerFactory,
        JarInventoryService? jarInventory = null)
    {
        this.store = store;
        this.paths = paths;
        this.statistics = statistics;
        this.statusClient = statusClient;
        this.backups = backups;
        this.loggerFactory = loggerFactory;
        this.jarInventory = jarInventory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var definition in await store.GetServersAsync(cancellationToken).ConfigureAwait(false))
            servers[definition.Id] = CreateManaged(definition);
        foreach (var server in servers.Values)
        {
            server.RestoreCrashAnalysis(await store.GetLatestCrashAnalysisAsync(
                server.Definition.Id, cancellationToken).ConfigureAwait(false));
            var identity = await store.GetProcessIdentityAsync(server.Definition.Id, cancellationToken)
                .ConfigureAwait(false);
            if (identity is null)
                continue;
            var observation = ObservePersistedProcess(identity);
            if (observation is PersistedProcessObservation.ExactMatch or PersistedProcessObservation.UnprovableAlive)
                server.MarkDetached(identity);
            else
                await store.RemoveProcessIdentityAsync(server.Definition.Id, cancellationToken).ConfigureAwait(false);
        }
        pendingRestorations = (await store.GetRunningStatesAsync(cancellationToken).ConfigureAwait(false))
            .Where(ShouldRestore)
            .ToArray();
    }

    /// <summary>
    /// Starts ordinary restoration only after startup public-exposure recovery has reached its
    /// terminal safety decision. This task is tracked by Program; it never crosses that boundary as
    /// detached fire-and-forget work.
    /// </summary>
    public async Task RestoreStartupStateAsync(
        IReadOnlySet<Guid> suppressedServerIds,
        CancellationToken cancellationToken = default)
    {
        var restorations = pendingRestorations;
        pendingRestorations = [];
        foreach (var state in restorations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (suppressedServerIds.Contains(state.ServerId))
                continue;
            if (!servers.TryGetValue(state.ServerId, out var server))
                continue;
            if (server.State == ServerState.Unknown)
                continue;
            if (state.AutostartMode == AutostartMode.WindowsLoginWithDelay)
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            _ = await server.StartAsync("State restoration", cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyCollection<ServerDefinition> Definitions =>
        servers.Values.Select(server => server.Definition).OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public ManagedServer Get(Guid id) =>
        servers.TryGetValue(id, out var server)
            ? server
            : throw new KeyNotFoundException($"Server {id} is not imported.");

    private static bool ShouldRestore(ServerRunningState state) =>
        state.AutostartMode == AutostartMode.AgentStart ||
        state.AutostartMode == AutostartMode.WindowsLoginWithDelay ||
        state.AutostartMode == AutostartMode.RestorePreviousRunningState &&
        state.WasRunning &&
        state.LastIntent is not LifecycleIntentKind.ManualStop and
            not LifecycleIntentKind.ApplicationExit and
            not LifecycleIntentKind.WindowsShutdown;

    private static PersistedProcessObservation ObservePersistedProcess(ProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited)
                return PersistedProcessObservation.Gone;
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
                return PersistedProcessObservation.UnprovableAlive;
            if (identity.ProcessCreationTicks == ProcessCreationIdentity.Unknown)
            {
                return Path.GetFullPath(identity.ExecutablePath).Equals(Path.GetFullPath(executable),
                    StringComparison.OrdinalIgnoreCase)
                    ? PersistedProcessObservation.UnprovableAlive
                    : PersistedProcessObservation.Gone;
            }
            return ProcessIdentityPolicy.MatchesProcessInstance(
                identity, process.Id, ProcessCreationIdentity.Of(process.SafeHandle), executable, out _)
                ? PersistedProcessObservation.ExactMatch
                : PersistedProcessObservation.Gone;
        }
        catch (ArgumentException)
        {
            return PersistedProcessObservation.Gone;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          System.ComponentModel.Win32Exception)
        {
            return PersistedProcessObservation.UnprovableAlive;
        }
    }

    private enum PersistedProcessObservation
    {
        Gone,
        ExactMatch,
        UnprovableAlive
    }

    public async Task ImportAsync(ServerDefinition definition, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(definition.RootPath))
            throw new DirectoryNotFoundException(definition.RootPath);
        if (string.IsNullOrWhiteSpace(definition.Name))
            throw new ArgumentException("Server name cannot be empty.", nameof(definition));
        await store.UpsertServerAsync(definition, cancellationToken).ConfigureAwait(false);
        servers.AddOrUpdate(definition.Id,
            _ => CreateManaged(definition),
            (_, existing) =>
            {
                existing.UpdateDefinition(definition);
                return existing;
            });
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var server = Get(id);
        if (server.State is not ServerState.Stopped and not ServerState.Crashed)
            throw new InvalidOperationException("Stop the server before removing it from ChunkPilot.");
        // Persist first. If SQLite refuses the transaction the in-memory registration remains the
        // visible recovery surface instead of disappearing until the Agent restarts.
        await store.DeleteServerAsync(id, cancellationToken).ConfigureAwait(false);
        if (servers.TryRemove(id, out var removed))
            await removed.DisposeAsync().ConfigureAwait(false);
    }

    public async Task<DashboardSnapshot> DashboardAsync(CancellationToken cancellationToken = default)
    {
        var snapshots = servers.Values.Select(server => server.Snapshot()).ToArray();
        var schedules = await store.GetSchedulesAsync(cancellationToken).ConfigureAwait(false);
        return new DashboardSnapshot
        {
            AgentConnected = true,
            Timestamp = DateTimeOffset.Now,
            Host = statistics.SampleHost(paths),
            Servers = snapshots,
            RecentActivity = await store.GetActivityAsync(50, cancellationToken).ConfigureAwait(false),
            NextScheduledTask = schedules.Where(schedule => schedule.Enabled).Select(schedule => schedule.NextRunAt).Where(value => value is not null).Min()
        };
    }

    public async Task<IReadOnlyDictionary<Guid, OperationResult>> StopAllAsync(
        string source = "Application exit",
        bool escalateOnFailure = false,
        CancellationToken cancellationToken = default)
        => await StopServersAsync(servers.Keys, source, escalateOnFailure, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<Guid, OperationResult>> StopAllForApplicationExitAsync(
        string source,
        TimeSpan? gateDeadline = null,
        CancellationToken cancellationToken = default)
    {
        var selected = servers.Values.ToArray();
        foreach (var server in selected)
            server.RequestApplicationExitCancellation();
        var deadline = gateDeadline ?? ApplicationExitGateDeadline;
        var tasks = selected.Select(async server =>
        {
            try
            {
                var result = await server.StopForApplicationExitAsync(source, deadline, cancellationToken)
                    .ConfigureAwait(false);
                return (server.Definition.Id, Result: result);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                return (server.Definition.Id,
                    Result: OperationResult.Fail($"Application-exit shutdown failed: {exception.Message}"));
            }
        });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(pair => pair.Id, pair => pair.Result);
    }

    /// <summary>Changes only ChunkPilot's display metadata; no server-owned path or runtime identity changes.</summary>
    public async Task<OperationResult> RenameAsync(
        Guid serverId,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        var managed = Get(serverId);
        var blocking = CreationNamePolicy.Validate(displayName)
            .Where(issue => issue.Severity == CreationIssueSeverity.Blocking)
            .Select(issue => issue.Message)
            .ToArray();
        if (blocking.Length > 0)
            return OperationResult.Fail(string.Join(" ", blocking));

        var trimmed = displayName.Trim();
        if (string.Equals(trimmed, managed.Definition.Name, StringComparison.Ordinal))
            return OperationResult.Ok("The display name is already up to date.");

        var prior = managed.PersistedDefinition;
        var updated = prior with { Name = trimmed };
        await store.UpsertServerAsync(updated, cancellationToken).ConfigureAwait(false);
        managed.UpdateDisplayName(trimmed);
        try
        {
            await store.AddActivityAsync(new ActivityEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                ServerId = serverId,
                ServerName = trimmed,
                Action = "Rename display name",
                Result = "Succeeded",
                Source = "User"
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            loggerFactory.CreateLogger<ServerSupervisor>().LogWarning(exception,
                "Server display name was saved but its activity entry could not be recorded for {ServerId}.", serverId);
        }
        return OperationResult.Ok($"Display name changed to {trimmed}.");
    }

    public IReadOnlyList<Guid> ExactOwnedProcessesStillAlive() =>
        servers.Values
            .Where(server => server.HasExactOwnedProcessAlive())
            .Select(server => server.Definition.Id)
            .ToArray();

    public IReadOnlyList<Guid> ExactOwnedProcessesStillAlive(IEnumerable<Guid> serverIds)
    {
        var selected = serverIds.ToHashSet();
        return servers.Values
            .Where(server => selected.Contains(server.Definition.Id) && server.HasExactOwnedProcessAlive())
            .Select(server => server.Definition.Id)
            .ToArray();
    }

    /// <summary>
    /// Safely stops the selected managed servers through their existing per-server operation queues.
    /// Exit-only escalation is explicit so ordinary manual Stop behavior remains unchanged.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, OperationResult>> StopServersAsync(
        IEnumerable<Guid> serverIds,
        string source,
        bool escalateOnFailure,
        CancellationToken cancellationToken = default)
    {
        var selected = serverIds.ToHashSet();
        var tasks = servers.Values
            .Where(server => selected.Contains(server.Definition.Id) &&
                             server.State is not ServerState.Stopped and not ServerState.Crashed)
            .Select(async server =>
            {
                var result = await server.StopAsync(saveFirst: true, source: source, cancellationToken)
                    .ConfigureAwait(false);
                // A missing save confirmation is not permission to kill Minecraft. On application exit,
                // still ask the server for its native graceful stop; escalation becomes eligible only
                // if that graceful stop itself cannot complete.
                if (escalateOnFailure && !result.Success && !result.RequiresForceConfirmation &&
                    !server.HasDetachedProcess)
                {
                    result = await server.StopAsync(saveFirst: false, source: source, cancellationToken)
                        .ConfigureAwait(false);
                }
                // A replacement agent cannot recover the dead agent's stdin stream, so it cannot
                // issue Minecraft's graceful stop command. Stop-all and application exit are explicit
                // requests to leave no servers behind: after the process instance has been verified,
                // terminate only that detached process and still verify its port was released.
                if (escalateOnFailure && !result.Success && result.RequiresForceConfirmation)
                    result = await server.ForceTerminateAsync(source, cancellationToken).ConfigureAwait(false);
                return (server.Definition.Id, Result: result);
            });
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(pair => pair.Id, pair => pair.Result);
    }

    public async Task<IReadOnlyDictionary<Guid, OperationResult>> StartAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = servers.Values.Where(server => server.State is ServerState.Stopped or ServerState.Crashed)
            .Select(async server => (server.Definition.Id,
                Result: await server.StartAsync("Start all", cancellationToken).ConfigureAwait(false)));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(pair => pair.Id, pair => pair.Result);
    }

    /// <summary>
    /// Creates a verified backup, coordinating a consistent save state when the server is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>freezeWorldSaving</c> is what makes a running backup consistent rather than merely lucky:
    /// automatic saving is turned off, the world is flushed and the flush is confirmed, and only then is
    /// anything copied. Saving is turned back on in a finally, so it is restored whether the backup
    /// succeeded, failed or was cancelled.
    /// </para>
    /// <para>
    /// The operation gate makes this exclusive with every other data operation on the same server.
    /// </para>
    /// </remarks>
    public async Task<BackupRecord> BackupAsync(Guid serverId, string source, CancellationToken cancellationToken = default)
    {
        var server = Get(serverId);
        var record = await server.RunExclusiveDataOperationAsync("backup", requireStopped: false, saveIfRunning: true,
            freezeWorldSaving: true, async token =>
            {
                var profile = backups.GetDefaultProfile(server.Definition);
                return await backups.CreateAsync(server.Definition, profile, source, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        server.MarkBackupCompleted();
        return record;
    }

    public async Task RestoreAsync(Guid serverId, BackupRecord record, CancellationToken cancellationToken = default)
    {
        var server = Get(serverId);
        await server.RunExclusiveDataOperationAsync("restore", requireStopped: true, saveIfRunning: false,
            freezeWorldSaving: false, async token =>
            {
                var profile = backups.GetDefaultProfile(server.Definition);
                _ = await backups.CreateAsync(server.Definition, profile, "Pre-restore safety backup", token).ConfigureAwait(false);
                await backups.RestoreAsync(server.Definition, record, token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
    }

    private ManagedServer CreateManaged(ServerDefinition definition) =>
        new(definition, statistics, statusClient, store, paths, loggerFactory.CreateLogger<ManagedServer>(),
            jarInventory: jarInventory);

    public async ValueTask DisposeAsync()
    {
        foreach (var server in servers.Values)
            await server.DisposeAsync().ConfigureAwait(false);
        servers.Clear();
    }
}
