using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed class ServerUpdateCoordinator
{
    private readonly ChunkPilotStore store;
    private readonly ServerSupervisor supervisor;
    private readonly UpdateSourceDetector sourceDetector;
    private readonly UpdateProviderRegistry providers;
    private readonly PackUpdateCompatibilityService compatibility;
    private readonly ServerPackUpdateService updates;
    private readonly VersionSnapshotService snapshots;
    private readonly ConcurrentDictionary<Guid, OperationState> operations = new();

    public ServerUpdateCoordinator(
        ChunkPilotStore store,
        ServerSupervisor supervisor,
        UpdateSourceDetector sourceDetector,
        UpdateProviderRegistry providers,
        PackUpdateCompatibilityService compatibility,
        ServerPackUpdateService updates,
        VersionSnapshotService snapshots)
    {
        this.store = store;
        this.supervisor = supervisor;
        this.sourceDetector = sourceDetector;
        this.providers = providers;
        this.compatibility = compatibility;
        this.updates = updates;
        this.snapshots = snapshots;
    }

    public async Task<UpdateSourceDetectionResult> DetectSourceAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var definition = supervisor.Get(serverId).Definition;
        var detected = sourceDetector.Detect(definition);
        if (detected.IsTrustworthy && detected.Source is not null)
            await store.UpsertUpdateSourceAsync(detected.Source, cancellationToken).ConfigureAwait(false);
        return detected;
    }

    public async Task LinkSourceAsync(UpdateSource source, CancellationToken cancellationToken = default)
    {
        _ = supervisor.Get(source.ServerId);
        UpdateSourceDetector.ValidateLink(source);
        await store.UpsertUpdateSourceAsync(source with
        {
            IsUserLinked = true,
            DetectionEvidence = "Linked explicitly by the user."
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<UpdateSource?> GetSourceAsync(Guid serverId, CancellationToken cancellationToken = default) =>
        store.GetUpdateSourceAsync(serverId, cancellationToken);

    public async Task<UpdateCheckResult> CheckAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        var server = supervisor.Get(serverId).Definition;
        var source = await store.GetUpdateSourceAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (source is null)
        {
            var detected = sourceDetector.Detect(server);
            if (detected.IsTrustworthy && detected.Source is not null)
            {
                source = detected.Source;
                await store.UpsertUpdateSourceAsync(source, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var missing = new UpdateCheckResult
                {
                    ServerId = serverId,
                    Status = ServerUpdateStatus.SourceNotLinked,
                    CheckedAt = DateTimeOffset.Now,
                    Compatibility = UpdateCompatibility.Unknown,
                    CompatibilityReasons = detected.Evidence,
                    Message = detected.Message
                };
                await store.RecordUpdateCheckAsync(missing, cancellationToken).ConfigureAwait(false);
                return missing;
            }
        }
        if (!source.HasIdentifiedBaseline)
        {
            var baseline = new UpdateCheckResult
            {
                ServerId = serverId,
                Status = ServerUpdateStatus.SourceNotLinked,
                CheckedAt = DateTimeOffset.Now,
                Source = source,
                Compatibility = UpdateCompatibility.ManualReviewRequired,
                CompatibilityReasons = ["Record the currently installed version before comparing provider releases."],
                Message = "The provider is linked, but the installed baseline is not identified."
            };
            await store.RecordUpdateCheckAsync(baseline, cancellationToken).ConfigureAwait(false);
            return baseline;
        }
        try
        {
            var preferences = await store.GetUpdatePreferencesAsync(serverId, cancellationToken).ConfigureAwait(false);
            var available = await providers.Get(source.Provider)
                .GetVersionsAsync(source, preferences, cancellationToken).ConfigureAwait(false);
            if (source.Provider == UpdateProvider.LocalPackageHistory)
            {
                var exactLocalPackage = available.FirstOrDefault(version =>
                    version.VersionId.Equals(source.InstalledVersionId, StringComparison.OrdinalIgnoreCase));
                if (exactLocalPackage is not null &&
                    (!source.InstalledVersionName.Equals(exactLocalPackage.VersionName, StringComparison.Ordinal) ||
                     !source.InstalledFileId.Equals(exactLocalPackage.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    source = source with
                    {
                        InstalledVersionName = exactLocalPackage.VersionName,
                        InstalledFileId = exactLocalPackage.Sha256
                    };
                    await store.UpsertUpdateSourceAsync(source, cancellationToken).ConfigureAwait(false);
                }
            }
            var installed = new PackVersionInfo
            {
                PackId = source.ProjectId,
                VersionId = source.InstalledVersionId,
                VersionName = source.InstalledVersionName,
                ReleaseChannel = source.ReleaseChannel,
                PublishedAt = source.InstalledAt ?? server.ImportedAt,
                MinecraftVersion = source.MinecraftVersion,
                Loader = source.Loader,
                LoaderVersion = source.LoaderVersion
            };
            var result = compatibility.Evaluate(server, source, installed, available, DateTimeOffset.Now);
            await store.RecordUpdateCheckAsync(result, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or
                                           InvalidDataException or InvalidOperationException)
        {
            var unavailable = new UpdateCheckResult
            {
                ServerId = serverId,
                Status = ServerUpdateStatus.CheckUnavailable,
                CheckedAt = DateTimeOffset.Now,
                Source = source,
                Compatibility = UpdateCompatibility.Unknown,
                CompatibilityReasons = [SecretRedactor.Redact(exception.Message)],
                Message = $"Update check unavailable: {SecretRedactor.Redact(exception.Message)}"
            };
            await store.RecordUpdateCheckAsync(unavailable, cancellationToken).ConfigureAwait(false);
            return unavailable;
        }
    }

    public Guid Begin(UpdateInstallRequest request)
    {
        var operationId = request.OperationId == Guid.Empty ? Guid.NewGuid() : request.OperationId;
        var normalized = request with { OperationId = operationId };
        var state = new OperationState(operationId, normalized.ServerId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException($"Update operation {operationId} already exists.");
        state.Task = RunAsync(normalized, state);
        return operationId;
    }

    public UpdateOperationSnapshot Get(Guid operationId)
    {
        if (!operations.TryGetValue(operationId, out var state))
            throw new KeyNotFoundException($"Update operation {operationId} was not found.");
        lock (state.Gate)
            return state.Snapshot;
    }

    public void Cancel(Guid operationId)
    {
        if (!operations.TryGetValue(operationId, out var state))
            throw new KeyNotFoundException($"Update operation {operationId} was not found.");
        state.Cancellation.Cancel();
    }

    public Task<IReadOnlyList<VersionSnapshot>> ListVersionsAsync(
        Guid serverId,
        CancellationToken cancellationToken = default) =>
        store.GetVersionSnapshotsAsync(serverId, cancellationToken);

    public async Task MarkHealthyAsync(
        Guid serverId,
        Guid snapshotId,
        int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var versions = await store.GetVersionSnapshotsAsync(serverId, cancellationToken).ConfigureAwait(false);
        var active = versions.FirstOrDefault(version => version.Id == snapshotId && version.IsActive)
                     ?? throw new InvalidOperationException("Only the active pending version can be marked healthy.");
        await store.UpsertVersionSnapshotAsync(active with
        {
            Health = VersionHealth.Healthy,
            LastStartupResult = "User confirmed the updated server is working."
        }, cancellationToken).ConfigureAwait(false);
        var source = await store.GetUpdateSourceAsync(serverId, cancellationToken).ConfigureAwait(false);
        if (source is not null)
            await store.RecordUpdateCheckAsync(new UpdateCheckResult
            {
                ServerId = serverId,
                Status = ServerUpdateStatus.UpdateSuccessful,
                CheckedAt = DateTimeOffset.Now,
                Source = source,
                InstalledVersion = ToPackVersion(active),
                LatestVersion = ToPackVersion(active),
                Compatibility = UpdateCompatibility.Compatible,
                CompatibilityReasons = ["User marked the active version healthy."],
                Message = $"Update {active.VersionName} is marked healthy."
            }, cancellationToken).ConfigureAwait(false);
        var retainUntil = retentionDays <= 0 ? (DateTimeOffset?)null : DateTimeOffset.Now.AddDays(retentionDays);
        foreach (var previous in versions.Where(version => !version.IsActive && !version.KeepPermanently))
            await store.UpsertVersionSnapshotAsync(previous with { RetainUntil = retainUntil }, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task RollbackAsync(
        Guid serverId,
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var managed = supervisor.Get(serverId);
        var versions = await store.GetVersionSnapshotsAsync(serverId, cancellationToken).ConfigureAwait(false);
        var target = versions.FirstOrDefault(version => version.Id == snapshotId)
                     ?? throw new KeyNotFoundException("Version snapshot was not found.");
        if (target.IsActive)
            throw new InvalidOperationException("The selected version is already active.");
        var source = await store.GetUpdateSourceAsync(serverId, cancellationToken).ConfigureAwait(false)
                     ?? new UpdateSource
                     {
                         ServerId = serverId,
                         Provider = target.SourceProvider,
                         ProjectId = target.Source,
                         InstalledVersionId = versions.FirstOrDefault(version => version.IsActive)?.VersionId ?? "current",
                         InstalledVersionName = versions.FirstOrDefault(version => version.IsActive)?.VersionName ?? "current"
                     };
        var result = await managed.RunExclusiveVersionRollbackAsync(
            target.VersionName,
            async token =>
            {
                _ = await snapshots.CreateAsync(managed.Definition, source,
                    $"Safety snapshot before manual rollback to {target.VersionName}", token).ConfigureAwait(false);
                await updates.RollbackAsync(managed.Definition, target, Guid.NewGuid(), token).ConfigureAwait(false);
                managed.UpdateDefinition(target.Definition with
                {
                    RootPath = managed.Definition.RootPath,
                    WorkingDirectory = managed.Definition.RootPath
                });
                await store.UpsertServerAsync(managed.Definition, token).ConfigureAwait(false);
                await store.UpsertUpdateSourceAsync(source with
                {
                    InstalledVersionId = target.VersionId,
                    InstalledVersionName = target.VersionName,
                    MinecraftVersion = target.MinecraftVersion,
                    Loader = target.Loader,
                    LoaderVersion = target.LoaderVersion,
                    InstalledAt = DateTimeOffset.Now
                }, token).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);
    }

    public Task DeleteVersionAsync(Guid serverId, Guid snapshotId, CancellationToken cancellationToken = default) =>
        snapshots.DeleteAsync(serverId, snapshotId, cancellationToken);

    public async Task<bool> VerifyVersionAsync(
        Guid serverId,
        Guid snapshotId,
        CancellationToken cancellationToken = default)
    {
        var version = (await store.GetVersionSnapshotsAsync(serverId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == snapshotId)
            ?? throw new KeyNotFoundException("Version snapshot was not found.");
        if (string.IsNullOrWhiteSpace(version.SnapshotPath))
            throw new InvalidOperationException("The active version has no compressed snapshot archive to verify.");
        var verified = await VersionSnapshotService.VerifyAsync(version.SnapshotPath, cancellationToken)
            .ConfigureAwait(false);
        await store.UpsertVersionSnapshotAsync(version with { Verified = verified }, cancellationToken)
            .ConfigureAwait(false);
        return verified;
    }

    public async Task UpdateVersionMetadataAsync(
        VersionMetadataRequest request,
        CancellationToken cancellationToken = default)
    {
        var version = (await store.GetVersionSnapshotsAsync(request.ServerId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == request.SnapshotId)
            ?? throw new KeyNotFoundException("Version snapshot was not found.");
        await store.UpsertVersionSnapshotAsync(version with
        {
            KeepPermanently = request.KeepPermanently,
            Description = request.Description.Trim()
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UpdateCenterItem>> GetUpdateCenterAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<UpdateCenterItem>();
        foreach (var server in supervisor.Definitions)
        {
            var latest = await store.GetLatestUpdateCheckAsync(server.Id, cancellationToken).ConfigureAwait(false);
            var versions = await store.GetVersionSnapshotsAsync(server.Id, cancellationToken).ConfigureAwait(false);
            var active = versions.FirstOrDefault(version => version.IsActive);
            var operation = operations.Values.FirstOrDefault(item =>
                item.ServerId == server.Id && !item.Snapshot.IsTerminal)?.Snapshot;
            var operationStatus = operation?.Progress.State switch
            {
                UpdateOperationState.Downloading or UpdateOperationState.Verifying =>
                    ServerUpdateStatus.Downloading,
                null => (ServerUpdateStatus?)null,
                _ => ServerUpdateStatus.Updating
            };
            items.Add(new UpdateCenterItem
            {
                ServerId = server.Id,
                ServerName = server.Name,
                Status = operationStatus ?? (active?.Health == VersionHealth.PendingValidation
                    ? ServerUpdateStatus.PendingValidation
                    : latest?.Status ?? ServerUpdateStatus.SourceNotLinked),
                InstalledVersion = active?.VersionName ?? latest?.Source?.InstalledVersionName ?? "",
                LatestVersion = latest?.LatestVersion?.VersionName ?? "",
                LastCheckedAt = latest?.CheckedAt,
                Detail = operation is not null
                    ? $"{operation.Progress.CurrentStep} · {operation.Progress.Percent:F0}%"
                    : active?.Health == VersionHealth.PendingValidation
                    ? "Updated server is awaiting user validation."
                    : latest?.Message ?? "Link an update source."
            });
        }
        return items.OrderBy(item => Priority(item.Status)).ThenBy(item => item.ServerName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task RunAsync(UpdateInstallRequest request, OperationState state)
    {
        try
        {
            var source = await store.GetUpdateSourceAsync(request.ServerId, state.Cancellation.Token)
                             .ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Link and identify an update source before installing.");
            var managed = supervisor.Get(request.ServerId);
            var progress = new CallbackProgress<UpdateProgress>(update =>
            {
                lock (state.Gate)
                    state.Snapshot = state.Snapshot with { Progress = update };
            });
            if (request.DownloadOnly)
            {
                var downloaded = await updates.DownloadAndVerifyOnlyAsync(
                    managed.Definition, source, request, progress, state.Cancellation.Token).ConfigureAwait(false);
                lock (state.Gate)
                    state.Snapshot = new UpdateOperationSnapshot
                    {
                        OperationId = state.Id,
                        Progress = new UpdateProgress
                        {
                            OperationId = state.Id,
                            State = UpdateOperationState.ReadyToInstall,
                            CurrentStep = "Download verified and ready to install",
                            Percent = 100,
                            Detail = downloaded.Message
                        },
                        IsTerminal = true,
                        Success = true,
                        Result = downloaded
                    };
                return;
            }
            var result = await managed.RunExclusivePackUpdateAsync(
                request,
                (definition, token) => updates.PrepareAndSwitchAsync(
                    definition, source, request, progress, token),
                (definition, snapshot, operationId, token) =>
                    updates.RollbackAsync(definition, snapshot, operationId, token),
                (prepared, token) => updates.FinalizeOperationAsync(prepared, token),
                state.Cancellation.Token).ConfigureAwait(false);
            if (result.RolledBack && result.PreviousSnapshot is not null)
                await store.UpsertUpdateSourceAsync(source with
                {
                    InstalledVersionId = result.PreviousSnapshot.VersionId,
                    InstalledVersionName = result.PreviousSnapshot.VersionName,
                    MinecraftVersion = result.PreviousSnapshot.MinecraftVersion,
                    Loader = result.PreviousSnapshot.Loader,
                    LoaderVersion = result.PreviousSnapshot.LoaderVersion,
                    InstalledAt = DateTimeOffset.Now
                }, CancellationToken.None).ConfigureAwait(false);
            var effectiveSource = await store.GetUpdateSourceAsync(request.ServerId, CancellationToken.None)
                .ConfigureAwait(false) ?? source;
            var effectiveVersion = result.RolledBack ? result.PreviousSnapshot : result.ActiveVersion;
            if (effectiveVersion is not null)
                await store.RecordUpdateCheckAsync(new UpdateCheckResult
                {
                    ServerId = request.ServerId,
                    Status = result.RolledBack
                        ? ServerUpdateStatus.UpdateFailed : ServerUpdateStatus.PendingValidation,
                    CheckedAt = DateTimeOffset.Now,
                    Source = effectiveSource,
                    InstalledVersion = ToPackVersion(effectiveVersion),
                    LatestVersion = request.TargetVersion,
                    Compatibility = result.RolledBack
                        ? UpdateCompatibility.ManualReviewRequired : UpdateCompatibility.Compatible,
                    CompatibilityReasons = result.RolledBack
                        ? [result.Message] : ["Startup readiness and local status succeeded; user validation remains."],
                    Message = result.Message
                }, CancellationToken.None).ConfigureAwait(false);
            lock (state.Gate)
                state.Snapshot = new UpdateOperationSnapshot
                {
                    OperationId = state.Id,
                    Progress = new UpdateProgress
                    {
                        OperationId = state.Id,
                        State = result.RolledBack ? UpdateOperationState.RollingBack :
                            result.Success ? UpdateOperationState.PendingValidation : UpdateOperationState.Failed,
                        CurrentStep = result.RolledBack ? "Failed update rolled back" :
                            result.Success ? "Awaiting user validation" : "Update failed",
                        Percent = 100,
                        Detail = result.Message
                    },
                    IsTerminal = true,
                    Success = result.Success,
                    Error = result.Success ? "" : result.Message,
                    Result = result
                };
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = "Update cancelled before the active-version switch.",
                    Progress = state.Snapshot.Progress with
                    {
                        State = UpdateOperationState.Cancelled,
                        CurrentStep = "Cancelled"
                    }
                };
        }
        catch (MigrationReviewRequiredException exception)
        {
            var definition = supervisor.Get(request.ServerId).Definition;
            var result = new UpdateExecutionResult
            {
                OperationId = request.OperationId,
                ServerId = request.ServerId,
                Success = false,
                PreviousDefinition = definition,
                UpdatedDefinition = definition,
                MigrationPlan = exception.Plan,
                Message = exception.Message
            };
            lock (state.Gate)
                state.Snapshot = new UpdateOperationSnapshot
                {
                    OperationId = state.Id,
                    Progress = new UpdateProgress
                    {
                        OperationId = state.Id,
                        State = UpdateOperationState.PlanningMigration,
                        CurrentStep = "Migration review required",
                        Percent = 65,
                        Detail = exception.Message
                    },
                    IsTerminal = true,
                    Success = false,
                    Error = exception.Message,
                    Result = result
                };
        }
        catch (Exception exception)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Progress = state.Snapshot.Progress with
                    {
                        State = UpdateOperationState.Failed,
                        CurrentStep = "Update failed"
                    }
                };
        }
    }

    private static int Priority(ServerUpdateStatus status) => status switch
    {
        ServerUpdateStatus.UpdateFailed => 0,
        ServerUpdateStatus.PendingValidation => 1,
        ServerUpdateStatus.UpdateAvailable => 2,
        ServerUpdateStatus.Downloading or ServerUpdateStatus.Updating => 3,
        ServerUpdateStatus.RollbackAvailable => 4,
        _ => 10
    };

    private static PackVersionInfo ToPackVersion(VersionSnapshot version) => new()
    {
        PackId = version.Source,
        VersionId = version.VersionId,
        VersionName = version.VersionName,
        PublishedAt = version.InstalledAt,
        MinecraftVersion = version.MinecraftVersion,
        Loader = version.Loader,
        LoaderVersion = version.LoaderVersion,
        Changelog = version.Changelog,
        MigrationNotes = version.UpdateNotes
    };

    private sealed class OperationState
    {
        public OperationState(Guid id, Guid serverId)
        {
            Id = id;
            ServerId = serverId;
            Snapshot = new UpdateOperationSnapshot
            {
                OperationId = id,
                Progress = new UpdateProgress
                {
                    OperationId = id,
                    State = UpdateOperationState.Planned,
                    CurrentStep = "Queued"
                }
            };
        }

        public Guid Id { get; }
        public Guid ServerId { get; }
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }
        public UpdateOperationSnapshot Snapshot { get; set; }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
