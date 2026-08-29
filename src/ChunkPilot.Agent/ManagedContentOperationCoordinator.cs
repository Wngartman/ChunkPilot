using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed class ManagedContentOperationCoordinator
{
    private readonly ServerSupervisor supervisor;
    private readonly PluginManagementService plugins;
    private readonly JarInventoryService jars;
    private readonly CurseForgeManagedContentPlanAuthorizationRegistry planAuthorizations;
    private readonly ConcurrentDictionary<Guid, OperationState> operations = new();
    private readonly object beginGate = new();
    internal const int DefaultMaximumPreCancelledOperations = 4_096;
    private readonly int maximumPreCancelledOperations;
    private int preCancelledOperations;
    private bool registrationSealed;

    public ManagedContentOperationCoordinator(
        ServerSupervisor supervisor,
        PluginManagementService plugins,
        JarInventoryService jars,
        CurseForgeManagedContentPlanAuthorizationRegistry planAuthorizations)
        : this(
            supervisor,
            plugins,
            jars,
            planAuthorizations,
            DefaultMaximumPreCancelledOperations)
    {
    }

    internal ManagedContentOperationCoordinator(
        ServerSupervisor supervisor,
        PluginManagementService plugins,
        JarInventoryService jars,
        CurseForgeManagedContentPlanAuthorizationRegistry planAuthorizations,
        int maximumPreCancelledOperations)
    {
        if (maximumPreCancelledOperations is <= 0 or > DefaultMaximumPreCancelledOperations)
            throw new ArgumentOutOfRangeException(
                nameof(maximumPreCancelledOperations),
                $"The retained managed-content cancellation-fence limit must be from 1 through {DefaultMaximumPreCancelledOperations:N0}.");
        this.supervisor = supervisor;
        this.plugins = plugins;
        this.jars = jars;
        this.planAuthorizations = planAuthorizations;
        this.maximumPreCancelledOperations = maximumPreCancelledOperations;
    }

    public ManagedContentOperationSnapshot BeginInstall(BeginManagedContentInstallRequest request)
    {
        if (request.OperationId == Guid.Empty || request.ServerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ProjectId) ||
            string.IsNullOrWhiteSpace(request.VersionId))
            throw new ArgumentException(
                "A client-generated operation identity and exact server, project, and release are required.");
        var requiresPlanAuthorization = request.IncludeDependencies &&
                                        request.Provider == PluginProviderKind.CurseForge;
        if (requiresPlanAuthorization && request.PlanAuthorization is null)
            throw new InvalidOperationException(
                "CurseForge dependency installation requires the exact reviewed plan authorization.");
        if (!requiresPlanAuthorization && request.PlanAuthorization is not null)
            throw new ArgumentException(
                "A dependency-plan authorization is valid only for a CurseForge dependency installation.");
        lock (beginGate)
        {
            var operationId = request.OperationId;
            if (operations.TryGetValue(operationId, out var existing))
            {
                if (!existing.MatchesRequest(request))
                throw new InvalidOperationException(
                    "That managed-content operation identity already belongs to another exact request.");
                return existing.Read();
            }
            if (registrationSealed)
                throw new InvalidOperationException(
                    "Managed-content registration is fail-closed because this Agent lifetime reached its retained cancellation-fence limit. Restart ChunkPilot before starting another add-on operation.");
            _ = supervisor.Get(request.ServerId);
            if (operations.Values.Any(operation => operation.IsActiveFor(
                    request.ServerId, request.ProjectId, request.VersionId, request.Provider)))
                throw new InvalidOperationException("That exact add-on release already has an active operation.");

            PruneCompletedOperations();
            var exactPlan = requiresPlanAuthorization
                ? planAuthorizations.Consume(
                    request.ServerId,
                    request.Provider,
                    request.ProjectId,
                    request.VersionId,
                    request.PlanAuthorization!)
                : null;
            var state = new OperationState(operationId, request, exactPlan);
            if (!operations.TryAdd(operationId, state))
                return operations[operationId].Read();
            state.Task = RunInstallAsync(state);
            return state.Read();
        }
    }

    /// <summary>
    /// Cancels an accepted exact request or reserves the same exact identity as terminal-cancelled
    /// before Begin. Sharing the Begin gate makes delayed request dispatch deterministic.
    /// </summary>
    public ManagedContentCancellationFenceResult CancelOrFenceInstall(
        BeginManagedContentInstallRequest request,
        Action<Guid>? revokeUnconsumedAuthorization = null)
    {
        if (request.OperationId == Guid.Empty || request.ServerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ProjectId) || string.IsNullOrWhiteSpace(request.VersionId))
            throw new ArgumentException(
                "An exact operation, server, project, and release are required for cancellation.");
        var requiresPlanAuthorization = request.IncludeDependencies &&
                                        request.Provider == PluginProviderKind.CurseForge;
        if (requiresPlanAuthorization && request.PlanAuthorization is null)
            throw new InvalidOperationException(
                "The exact CurseForge dependency-plan authorization is required for cancellation fencing.");
        if (!requiresPlanAuthorization && request.PlanAuthorization is not null)
            throw new ArgumentException(
                "A dependency-plan authorization is valid only for a CurseForge dependency installation.");
        lock (beginGate)
        {
            if (operations.TryGetValue(request.OperationId, out var existing))
            {
                if (!existing.MatchesRequest(request))
                    throw new InvalidOperationException(
                        "That managed-content operation identity belongs to another exact request.");
                existing.Cancellation.Cancel();
                return new ManagedContentCancellationFenceResult(
                    true,
                    existing.WasCancelledBeforeStart,
                    existing.Read());
            }
            if (registrationSealed)
            {
                if (request.PlanAuthorization is { } sealedAuthorization)
                    revokeUnconsumedAuthorization?.Invoke(sealedAuthorization.AuthorizationId);
                return SealedCancellationEvidence(request);
            }
            _ = supervisor.Get(request.ServerId);

            var state = new OperationState(request.OperationId, request, exactPlan: null);
            state.CancelBeforeStart();
            if (!operations.TryAdd(request.OperationId, state))
                throw new InvalidOperationException(
                    "The managed-content cancellation fence could not reserve its exact operation identity.");
            preCancelledOperations++;
            if (preCancelledOperations >= maximumPreCancelledOperations)
                registrationSealed = true;
            if (request.PlanAuthorization is { } authorization)
                revokeUnconsumedAuthorization?.Invoke(authorization.AuthorizationId);
            return new ManagedContentCancellationFenceResult(true, true, state.Read());
        }
    }

    private static ManagedContentCancellationFenceResult SealedCancellationEvidence(
        BeginManagedContentInstallRequest request)
    {
        // Once sealed, every previously unseen Begin is rejected under beginGate. A synthetic
        // terminal record therefore proves cancellation without retaining another attacker-sized
        // operation object. Replays of identities retained before the seal still use their exact
        // stored request above.
        var state = new OperationState(request.OperationId, request, exactPlan: null);
        state.CancelBeforeStart();
        var snapshot = state.Read();
        state.Cancellation.Dispose();
        return new ManagedContentCancellationFenceResult(true, true, snapshot);
    }

    private void PruneCompletedOperations()
    {
        const int retainedOperationLimit = 500;
        if (operations.Count < retainedOperationLimit)
            return;
        foreach (var operation in operations.Values
                     .Where(candidate => candidate.Read().IsTerminal && !candidate.WasCancelledBeforeStart)
                     .OrderBy(candidate => candidate.Read().UpdatedAtUtc)
                     .Take(Math.Max(1, operations.Count - retainedOperationLimit + 1)))
            operations.TryRemove(operation.Read().OperationId, out _);
    }

    public ManagedContentOperationSnapshot Get(Guid operationId) =>
        operations.TryGetValue(operationId, out var state)
            ? state.Read()
            : throw new KeyNotFoundException("The managed content operation was not found.");

    public bool TryGet(Guid operationId, out ManagedContentOperationSnapshot? snapshot)
    {
        if (operations.TryGetValue(operationId, out var state))
        {
            snapshot = state.Read();
            return true;
        }
        snapshot = null;
        return false;
    }

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
                    ? InstallPlanAsync(managed.Definition, request, state.ExactPlan, token, progress)
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
                entry.Provider == request.Provider &&
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
            server, request.ProjectId, request.VersionId, progress, request.Provider, cancellationToken)
            .ConfigureAwait(false);
        return new InstalledResult(result, null);
    }

    private async Task<InstalledResult> InstallPlanAsync(
        ServerDefinition server,
        BeginManagedContentInstallRequest request,
        PluginInstallPlan? exactPlan,
        CancellationToken cancellationToken,
        IProgress<ManagedContentProgress> progress)
    {
        var result = request.Provider == PluginProviderKind.CurseForge
            ? await plugins.InstallExactPlanWithReceiptsAsync(
                server,
                request.ProjectId,
                request.VersionId,
                exactPlan ?? throw new InvalidOperationException(
                    "The exact reviewed CurseForge dependency plan is unavailable."),
                progress,
                cancellationToken).ConfigureAwait(false)
            : await plugins.InstallPlanWithReceiptsAsync(
                server, request.ProjectId, request.VersionId, progress, request.Provider, cancellationToken)
                .ConfigureAwait(false);
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

        public OperationState(
            Guid operationId,
            BeginManagedContentInstallRequest request,
            PluginInstallPlan? exactPlan)
        {
            Request = request;
            ExactPlan = exactPlan;
            snapshot = new ManagedContentOperationSnapshot
            {
                OperationId = operationId,
                ServerId = request.ServerId,
                Kind = request.IncludeDependencies
                    ? ManagedContentOperationKind.InstallAddonPlan
                    : ManagedContentOperationKind.InstallAddon,
                Provider = request.Provider.ToString(),
                ProjectId = request.ProjectId,
                VersionId = request.VersionId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
        }

        public BeginManagedContentInstallRequest Request { get; }
        public PluginInstallPlan? ExactPlan { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }
        public bool WasCancelledBeforeStart { get; private set; }

        public bool IsActiveFor(
            Guid serverId,
            string projectId,
            string versionId,
            PluginProviderKind provider)
        {
            lock (gate)
                return !snapshot.IsTerminal && snapshot.ServerId == serverId &&
                    snapshot.Provider.Equals(provider.ToString(), StringComparison.OrdinalIgnoreCase) &&
                    snapshot.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase) &&
                    snapshot.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase);
        }

        public bool MatchesRequest(BeginManagedContentInstallRequest request) =>
            Request.ServerId == request.ServerId &&
            Request.ProjectId.Equals(request.ProjectId, StringComparison.Ordinal) &&
            Request.VersionId.Equals(request.VersionId, StringComparison.Ordinal) &&
            Request.IncludeDependencies == request.IncludeDependencies &&
            Request.RestartIfRunning == request.RestartIfRunning &&
            Request.Provider == request.Provider &&
            Request.PlanAuthorization == request.PlanAuthorization;

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

        public void CancelBeforeStart()
        {
            Cancellation.Cancel();
            lock (gate)
            {
                WasCancelledBeforeStart = true;
                snapshot = snapshot with
                {
                    Progress = new ManagedContentProgress
                    {
                        Stage = ManagedContentOperationStage.Cancelled,
                        Message = "The managed content operation was cancelled before the Agent started any work.",
                        Percent = 0
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
}
