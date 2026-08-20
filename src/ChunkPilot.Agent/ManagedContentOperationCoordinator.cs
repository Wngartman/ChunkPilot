using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed class ManagedContentOperationCoordinator
{
    private readonly ServerSupervisor supervisor;
    private readonly PluginManagementService plugins;
    private readonly JarInventoryService jars;
    private readonly ConcurrentDictionary<Guid, OperationState> operations = new();
    private readonly object beginGate = new();

    public ManagedContentOperationCoordinator(
        ServerSupervisor supervisor,
        PluginManagementService plugins,
        JarInventoryService jars)
    {
        this.supervisor = supervisor;
        this.plugins = plugins;
        this.jars = jars;
    }

    public ManagedContentOperationSnapshot BeginInstall(BeginManagedContentInstallRequest request)
    {
        if (request.ServerId == Guid.Empty || string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.VersionId))
            throw new ArgumentException("An exact server, project, and release are required.");
        lock (beginGate)
        {
            var operationId = request.OperationId == Guid.Empty ? Guid.NewGuid() : request.OperationId;
            if (operations.TryGetValue(operationId, out var existing))
                return existing.Read();
            if (operations.Values.Any(operation => operation.IsActiveFor(
                    request.ServerId, request.ProjectId, request.VersionId)))
                throw new InvalidOperationException("That exact add-on release already has an active operation.");

            PruneCompletedOperations();
            var state = new OperationState(operationId, request);
            if (!operations.TryAdd(operationId, state))
                return operations[operationId].Read();
            state.Task = RunInstallAsync(state);
            return state.Read();
        }
    }

    private void PruneCompletedOperations()
    {
        const int retainedOperationLimit = 500;
        if (operations.Count < retainedOperationLimit)
            return;
        foreach (var operation in operations.Values
                     .Where(candidate => candidate.Read().IsTerminal)
                     .OrderBy(candidate => candidate.Read().UpdatedAtUtc)
                     .Take(Math.Max(1, operations.Count - retainedOperationLimit + 1)))
            operations.TryRemove(operation.Read().OperationId, out _);
    }

    public ManagedContentOperationSnapshot Get(Guid operationId) =>
        operations.TryGetValue(operationId, out var state)
            ? state.Read()
            : throw new KeyNotFoundException("The managed content operation was not found.");

    public IReadOnlyList<ManagedContentOperationSnapshot> List(Guid? serverId = null) => operations.Values
        .Select(state => state.Read())
        .Where(snapshot => serverId is null || snapshot.ServerId == serverId)
        .OrderByDescending(snapshot => snapshot.StartedAtUtc)
        .Take(100)
        .ToArray();

    public void Cancel(Guid operationId)
    {
        if (!operations.TryGetValue(operationId, out var state))
            throw new KeyNotFoundException("The managed content operation was not found.");
        if (state.Read().IsTerminal)
            return;
        state.Cancellation.Cancel();
    }

    private async Task RunInstallAsync(OperationState state)
    {
        var request = state.Request;
        var managed = supervisor.Get(request.ServerId);
        var wasRunning = managed.State == ServerState.Running;
        var progress = new InlineProgress<ManagedContentProgress>(update => state.UpdateProgress(
            update.Stage == ManagedContentOperationStage.PendingRestart && !wasRunning
                ? update with { Stage = ManagedContentOperationStage.Installed,
                    Message = "The verified add-on is installed for the stopped server." }
                : update));
        try
        {
            state.UpdateProgress(new ManagedContentProgress
            {
                Stage = ManagedContentOperationStage.Queued,
                Message = "Queued behind the server's serialized operation gate."
            });
            var installed = await managed.RunExclusiveRestartableDataOperationAsync(
                request.IncludeDependencies
                    ? "installing a verified add-on dependency plan"
                    : "installing a verified add-on release",
                request.RestartIfRunning,
                token => request.IncludeDependencies
                    ? InstallPlanAsync(managed.Definition, request, token, progress)
                    : InstallReleaseAsync(managed.Definition, request, token, progress),
                (result, _) =>
                {
                    if (result.Plan is not null)
                        plugins.RollbackPlan(managed.Definition, result.Plan);
                    else if (result.Release is not null)
                        jars.RollbackInstall(managed.Definition, result.Release.Receipt);
                    return Task.CompletedTask;
                },
                state.Cancellation.Token).ConfigureAwait(false);

            var inventory = jars.Inventory(managed.Definition);
            var target = inventory.FirstOrDefault(entry =>
                entry.Provider == PluginProviderKind.Modrinth &&
                entry.ProviderProjectId.Equals(request.ProjectId, StringComparison.OrdinalIgnoreCase) &&
                entry.ProviderVersionId.Equals(request.VersionId, StringComparison.OrdinalIgnoreCase));
            if (target is null)
                throw new InvalidOperationException("The operation finished without authoritative inventory evidence for the exact release.");
            state.Complete(ManagedContentOperationStage.Installed,
                $"{target.Name} {target.Version} is installed. Current-session load evidence is reported separately from authoritative inventory and console evidence.");
            _ = installed;
        }
        catch (OperationCanceledException)
        {
            state.Cancelled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            state.Fail(SecretRedactor.Redact(exception.Message));
        }
    }

    private async Task<InstalledResult> InstallReleaseAsync(
        ServerDefinition server,
        BeginManagedContentInstallRequest request,
        CancellationToken cancellationToken,
        IProgress<ManagedContentProgress> progress)
    {
        var result = await plugins.InstallWithReceiptAsync(
            server, request.ProjectId, request.VersionId, progress, cancellationToken).ConfigureAwait(false);
        return new InstalledResult(result, null);
    }

    private async Task<InstalledResult> InstallPlanAsync(
        ServerDefinition server,
        BeginManagedContentInstallRequest request,
        CancellationToken cancellationToken,
        IProgress<ManagedContentProgress> progress)
    {
        var result = await plugins.InstallPlanWithReceiptsAsync(
            server, request.ProjectId, request.VersionId, progress, cancellationToken).ConfigureAwait(false);
        return new InstalledResult(null, result);
    }

    private sealed record InstalledResult(PluginInstallResult? Release, PluginInstallPlanResult? Plan);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class OperationState
    {
        private readonly object gate = new();
        private ManagedContentOperationSnapshot snapshot;

        public OperationState(Guid operationId, BeginManagedContentInstallRequest request)
        {
            Request = request;
            snapshot = new ManagedContentOperationSnapshot
            {
                OperationId = operationId,
                ServerId = request.ServerId,
                Kind = request.IncludeDependencies
                    ? ManagedContentOperationKind.InstallAddonPlan
                    : ManagedContentOperationKind.InstallAddon,
                Provider = "Modrinth",
                ProjectId = request.ProjectId,
                VersionId = request.VersionId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public BeginManagedContentInstallRequest Request { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }

        public bool IsActiveFor(Guid serverId, string projectId, string versionId)
        {
            lock (gate)
                return !snapshot.IsTerminal && snapshot.ServerId == serverId &&
                    snapshot.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase);
        }

        public ManagedContentOperationSnapshot Read()
        {
            lock (gate) return snapshot;
        }

        public void UpdateProgress(ManagedContentProgress progress)
        {
            lock (gate)
                snapshot = snapshot with { Progress = progress, UpdatedAtUtc = DateTimeOffset.UtcNow };
        }

        public void Complete(ManagedContentOperationStage stage, string message)
        {
            lock (gate)
                snapshot = snapshot with
                {
                    Progress = new ManagedContentProgress { Stage = stage, Message = message, Percent = 100 },
                    IsTerminal = true,
                    Success = true,
                    IsCancellable = false,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
        }

        public void Fail(string message)
        {
            lock (gate)
                snapshot = snapshot with
                {
                    Progress = new ManagedContentProgress { Stage = ManagedContentOperationStage.Failed, Message = message },
                    IsTerminal = true,
                    Success = false,
                    IsCancellable = false,
                    Error = message,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
        }

        public void Cancelled()
        {
            lock (gate)
                snapshot = snapshot with
                {
                    Progress = new ManagedContentProgress
                    {
                        Stage = ManagedContentOperationStage.Cancelled,
                        Message = "The managed content operation was cancelled."
                    },
                    IsTerminal = true,
                    Success = false,
                    IsCancellable = false,
                    Error = "The operation was cancelled.",
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
        }
    }
}
