using System.Collections.Concurrent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Agent;

public sealed class InstallationCoordinator
{
    private readonly ManagedServerInstaller installer;
    private readonly ServerSupervisor supervisor;
    private readonly ChunkPilotStore store;
    private readonly ManagedJavaRuntimeService? javaRuntimes;
    private readonly AppDataPaths? paths;
    private readonly ServerDetectionService? detector;
    private readonly ConcurrentDictionary<Guid, OperationState> operations = new();

    /// <summary>
    /// Vanilla creations begun in this Agent's lifetime, oldest first.
    /// </summary>
    /// <remarks>
    /// Kept so a UI that closed and reopened can find the operation it started rather than being told
    /// nothing is happening while a download continues behind it. The Agent owns the work either way;
    /// this only makes it findable.
    /// </remarks>
    private readonly ConcurrentQueue<Guid> vanillaOperations = new();
    private readonly ConcurrentQueue<Guid> paperOperations = new();
    private readonly ConcurrentQueue<Guid> managedLoaderOperations = new();
    private readonly ConcurrentQueue<Guid> modpackOperations = new();
    private readonly ConcurrentQueue<Guid> importOperations = new();

    public InstallationCoordinator(
        ManagedServerInstaller installer,
        ServerSupervisor supervisor,
        ChunkPilotStore store,
        ManagedJavaRuntimeService? javaRuntimes = null,
        AppDataPaths? paths = null,
        ServerDetectionService? detector = null)
    {
        this.installer = installer;
        this.supervisor = supervisor;
        this.store = store;
        this.javaRuntimes = javaRuntimes;
        this.paths = paths;
        this.detector = detector;
    }

    public Guid Begin(ServerInstallRequest request)
    {
        var operationId = request.OperationId == Guid.Empty ? Guid.NewGuid() : request.OperationId;
        var normalized = request with { OperationId = operationId };
        var state = new OperationState(operationId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException($"Install operation {operationId} already exists.");
        state.Task = RunAsync(normalized, state);
        return operationId;
    }

    /// <summary>
    /// Begins a real Vanilla creation from a plan the user reviewed and approved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Agent owns everything that touches the network, the disk or the database. The App submits
    /// a plain plan and then only watches: it never resolves a runtime, downloads an artifact, writes
    /// a file or registers a server.
    /// </para>
    /// <para>
    /// Submitting the same operation id twice is refused rather than queued, so a double click cannot
    /// become two servers.
    /// </para>
    /// </remarks>
    public Guid BeginVanilla(VanillaCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "This creation plan cannot be carried out. " + string.Join(" ", problems));

        var operationId = plan.OperationId == Guid.Empty ? Guid.NewGuid() : plan.OperationId;
        var state = new OperationState(operationId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException(
                $"This creation has already been started. Operation {operationId} is already running.");
        vanillaOperations.Enqueue(operationId);
        state.Task = RunVanillaAsync(plan with { OperationId = operationId }, state);
        return operationId;
    }

    /// <summary>Begins one exact, reviewed Paper build through the hardened managed-install transaction.</summary>
    public Guid BeginPaper(PaperCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "This Paper creation plan cannot be carried out. " + string.Join(" ", problems));

        var operationId = plan.OperationId == Guid.Empty ? Guid.NewGuid() : plan.OperationId;
        var state = new OperationState(operationId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException(
                $"This creation has already been started. Operation {operationId} is already running.");
        paperOperations.Enqueue(operationId);
        state.Task = RunPaperAsync(plan with { OperationId = operationId }, state);
        return operationId;
    }

    /// <summary>Begins one exact official Fabric or NeoForge combination.</summary>
    public Guid BeginManagedLoader(ManagedLoaderCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "This loader creation plan cannot be carried out. " + string.Join(" ", problems));
        var operationId = plan.OperationId == Guid.Empty ? Guid.NewGuid() : plan.OperationId;
        var state = new OperationState(operationId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException(
                $"This creation has already been started. Operation {operationId} is already running.");
        managedLoaderOperations.Enqueue(operationId);
        state.Task = RunManagedLoaderAsync(plan with { OperationId = operationId }, state);
        return operationId;
    }

    /// <summary>Begins one exact reviewed Modrinth-format server-pack creation.</summary>
    public Guid BeginModpack(ModpackCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "This modpack creation plan cannot be carried out. " + string.Join(" ", problems));
        var operationId = plan.OperationId == Guid.Empty ? Guid.NewGuid() : plan.OperationId;
        var state = new OperationState(operationId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException(
                $"This creation has already been started. Operation {operationId} is already running.");
        modpackOperations.Enqueue(operationId);
        state.Task = RunModpackAsync(plan with { OperationId = operationId }, state);
        return operationId;
    }

    /// <summary>Begins a reviewed local ZIP, JAR, or folder import under the same owned operation model.</summary>
    public Guid BeginImport(ServerImportCreationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidOperationException("This local import cannot be carried out. " + string.Join(" ", problems));
        var operationId = plan.OperationId == Guid.Empty ? Guid.NewGuid() : plan.OperationId;
        var state = new OperationState(operationId);
        if (!operations.TryAdd(operationId, state))
            throw new InvalidOperationException($"This import has already been started. Operation {operationId} is already running.");
        importOperations.Enqueue(operationId);
        state.Task = RunImportAsync(plan with { OperationId = operationId }, state);
        return operationId;
    }

    /// <summary>
    /// Every Vanilla creation this Agent knows about, newest first.
    /// </summary>
    /// <remarks>
    /// A reopened window uses this to reattach to work already in progress. It reports snapshots and
    /// nothing else: reading this cannot start, stop or alter an operation.
    /// </remarks>
    public IReadOnlyList<InstallOperationSnapshot> VanillaOperations() =>
        vanillaOperations
            .Reverse()
            .Select(id => operations.TryGetValue(id, out var state) ? state : null)
            .Where(state => state is not null)
            .Select(state =>
            {
                lock (state!.Gate)
                    return state.Snapshot;
            })
            .ToArray();

    public IReadOnlyList<InstallOperationSnapshot> PaperOperations() =>
        paperOperations
            .Reverse()
            .Select(id => operations.TryGetValue(id, out var state) ? state : null)
            .Where(state => state is not null)
            .Select(state =>
            {
                lock (state!.Gate)
                    return state.Snapshot;
            })
            .ToArray();

    public IReadOnlyList<InstallOperationSnapshot> ManagedLoaderOperations() =>
        managedLoaderOperations
            .Reverse()
            .Select(id => operations.TryGetValue(id, out var state) ? state : null)
            .Where(state => state is not null)
            .Select(state =>
            {
                lock (state!.Gate)
                    return state.Snapshot;
            })
            .ToArray();

    public IReadOnlyList<InstallOperationSnapshot> ModpackOperations() =>
        modpackOperations
            .Reverse()
            .Select(id => operations.TryGetValue(id, out var state) ? state : null)
            .Where(state => state is not null)
            .Select(state =>
            {
                lock (state!.Gate)
                    return state.Snapshot;
            })
            .ToArray();

    public IReadOnlyList<InstallOperationSnapshot> ImportOperations() =>
        importOperations
            .Reverse()
            .Select(id => operations.TryGetValue(id, out var state) ? state : null)
            .Where(state => state is not null)
            .Select(state =>
            {
                lock (state!.Gate)
                    return state.Snapshot;
            })
            .ToArray();

    /// <summary>
    /// Answers where a named server would be created and whether that destination may be used.
    /// </summary>
    /// <remarks>
    /// The same deterministic folder identity and the same destination policy the transaction will
    /// apply, asked early so the review screen can show a real path and refuse a real collision. It
    /// creates nothing and reserves nothing; the transaction re-runs the policy immediately before it
    /// promotes anything, because a folder can appear in between.
    /// </remarks>
    public async Task<VanillaDestinationPreview> PreviewDestinationAsync(
        VanillaDestinationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var instanceRoot = CreationPathSafety.Canonical(
            string.IsNullOrWhiteSpace(request.InstanceRoot)
                ? paths?.ManagedServers ??
                  throw new InvalidOperationException("This Agent has no managed-server root configured.")
                : request.InstanceRoot);

        string folderName;
        try
        {
            folderName = ManagedServerInstaller.MakeSafeInstanceName(request.ServerName);
        }
        catch (ArgumentException exception)
        {
            return new VanillaDestinationPreview
            {
                ServerName = request.ServerName,
                InstanceRoot = instanceRoot,
                Verdict = CreationDestinationVerdict.BlockedUnsafePath,
                IsAvailable = false,
                Message = SecretRedactor.Redact(exception.Message)
            };
        }

        var destination = CreationPathSafety.Canonical(Path.Combine(instanceRoot, folderName));
        var operationId = Guid.NewGuid();
        var staging = Path.Combine(instanceRoot, ServerCreationTransaction.StagingFolderName(operationId));
        var servers = await store.GetServersAsync(cancellationToken).ConfigureAwait(false);
        var journals = await store.GetCreationJournalsAsync(cancellationToken).ConfigureAwait(false);
        var active = journals.Where(record => record.Entry is not null).Select(record => record.Entry!).ToArray();
        var decision = CreationDestinationPolicy.Evaluate(
            new CreationDestinationQuery(operationId, destination, staging, servers, active));

        return new VanillaDestinationPreview
        {
            ServerName = request.ServerName,
            FolderName = folderName,
            InstanceRoot = instanceRoot,
            CanonicalDestination = decision.CanonicalPath,
            Verdict = decision.Verdict,
            IsAvailable = decision.IsAllowed,
            Message = decision.Message
        };
    }

    /// <summary>
    /// Prepares the runtime, then hands the work to the same hardened creation transaction every
    /// other managed install uses.
    /// </summary>
    private async Task RunVanillaAsync(VanillaCreationPlan plan, OperationState state)
    {
        try
        {
            Report(state, InstallState.Planned, CreationPhase.Requested, CreationStage.Preparing,
                CreationStagePolicy.Describe(CreationStage.Preparing), 1);
            var java = await PrepareRuntimeAsync(plan, state).ConfigureAwait(false);

            var request = new ServerInstallRequest
            {
                OperationId = plan.OperationId,
                SourceType = plan.UserSuppliedArtifact is null
                    ? InstallSourceType.Vanilla
                    : InstallSourceType.LocalServerJar,
                Source = plan.UserSuppliedArtifact?.NativePath ?? "",
                MinecraftVersion = plan.Version.VersionId,
                ServerName = plan.ServerName,
                InstanceRoot = plan.InstanceRoot,
                JavaPath = java.JavaPath,
                MinimumRamMb = plan.MinimumRamMb,
                MaximumRamMb = plan.MaximumRamMb,
                Port = plan.Port,
                CreationNetworkingPreference = plan.NetworkingPreference,
                MaxPlayers = plan.MaxPlayers,
                EulaAccepted = plan.Eula.Accepted,
                EulaAcceptedAt = plan.Eula.AcceptedAtUtc,
                // The hash the user reviewed is carried through, so an artifact that changed between
                // the review screen and the download is caught rather than silently installed.
                ExpectedSha1 = plan.UserSuppliedArtifact?.Sha1 ?? plan.Version.ServerSha1,
                ExpectedSha256 = plan.UserSuppliedArtifact?.Sha256 ?? "",
                ExpectedSizeBytes = plan.UserSuppliedArtifact?.SizeBytes
            };

            var progress = new CallbackProgress<InstallProgress>(update =>
            {
                lock (state.Gate)
                    state.Snapshot = state.Snapshot with { Progress = update };
            });
            var result = await installer.InstallAsync(request, progress, state.Cancellation.Token)
                .ConfigureAwait(false);
            await supervisor.ImportAsync(result.Definition, CancellationToken.None).ConfigureAwait(false);
            await store.SetJavaAssignmentAsync(result.Definition.Id, java.Id, java.JavaPath,
                $"Managed runtime selected for Minecraft {plan.Version.VersionId}",
                CancellationToken.None).ConfigureAwait(false);

            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    Progress = new InstallProgress
                    {
                        OperationId = state.Id,
                        State = InstallState.Completed,
                        Phase = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationPhase.CleanupPending
                            : CreationPhase.Completed,
                        Stage = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationStage.CompletedWithCleanupWarning
                            : CreationStage.Completed,
                        CurrentStep = CreationStagePolicy.Describe(
                            result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                                ? CreationStage.CompletedWithCleanupWarning
                                : CreationStage.Completed),
                        OverallPercent = 100,
                        StagingLogPath = result.StagingLogPath
                    },
                    IsTerminal = true,
                    Success = true,
                    Result = result,
                    Outcome = result.Outcome,
                    Warnings = result.Warnings
                };
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = "Creation stopped. Nothing was put in place.",
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Cancelled,
                        Phase = CreationPhase.Cancelling,
                        Stage = CreationStage.Cancelled,
                        CurrentStep = CreationStagePolicy.Describe(CreationStage.Cancelled)
                    }
                };
        }
        catch (CreationDestinationBlockedException blocked)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = blocked.Message,
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Failed,
                        Phase = CreationPhase.Failed,
                        Stage = CreationStage.FailedNothingChanged,
                        CurrentStep = "That folder cannot be used"
                    }
                };
        }
        catch (Exception exception)
        {
            var outcome = await ReadOutcomeAsync(state.Id).ConfigureAwait(false);
            lock (state.Gate)
            {
                // The transaction reports its own last phase before it closes the journal, so a
                // rollback that succeeded has already said so. Re-deriving the ending from the journal
                // alone would lose that: a reversed change deletes its row and would then read as
                // "nothing was ever activated", which is true of the folder but not of the attempt.
                var reported = state.Snapshot.Progress.Stage;
                var ending = CreationStagePolicy.IsTerminal(reported) && reported != CreationStage.Completed
                    ? reported
                    : outcome == CreationOutcome.NothingActivated
                        ? CreationStage.FailedNothingChanged
                        : CreationStage.RecoveryRequired;
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Outcome = ending == CreationStage.FailedRolledBack ? CreationOutcome.RolledBack : outcome,
                    Progress = state.Snapshot.Progress with
                    {
                        State = ending == CreationStage.RecoveryRequired
                            ? InstallState.RecoveryRequired
                            : InstallState.Failed,
                        Phase = ending == CreationStage.RecoveryRequired
                            ? CreationPhase.RecoveryRequired
                            : ending == CreationStage.FailedRolledBack
                                ? CreationPhase.RolledBack
                                : CreationPhase.Failed,
                        Stage = ending,
                        CurrentStep = CreationStagePolicy.Describe(ending)
                    }
                };
            }
        }
    }

    private async Task RunPaperAsync(PaperCreationPlan plan, OperationState state)
    {
        try
        {
            Report(state, InstallState.Planned, CreationPhase.Requested, CreationStage.Preparing,
                CreationStagePolicy.Describe(CreationStage.Preparing), 1);
            var requiredJava = plan.Version.RequiredJavaMajor
                               ?? throw new InvalidOperationException(
                                   "The Java version this Paper release needs was never established.");
            var java = await PrepareRuntimeAsync(
                requiredJava,
                $"Paper {plan.Version.VersionId} build {plan.Build.BuildId}",
                state).ConfigureAwait(false);

            var request = new ServerInstallRequest
            {
                OperationId = plan.OperationId,
                SourceType = InstallSourceType.Paper,
                MinecraftVersion = plan.Version.VersionId,
                Build = plan.Build.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ServerName = plan.ServerName,
                InstanceRoot = plan.InstanceRoot,
                JavaPath = java.JavaPath,
                MinimumRamMb = plan.MinimumRamMb,
                MaximumRamMb = plan.MaximumRamMb,
                Port = plan.Port,
                CreationNetworkingPreference = plan.NetworkingPreference,
                MaxPlayers = plan.MaxPlayers,
                EulaAccepted = plan.Eula.Accepted,
                EulaAcceptedAt = plan.Eula.AcceptedAtUtc,
                ExpectedSha256 = plan.Build.ServerSha256
            };

            var progress = new CallbackProgress<InstallProgress>(update =>
            {
                lock (state.Gate)
                    state.Snapshot = state.Snapshot with { Progress = update };
            });
            var result = await installer.InstallAsync(request, progress, state.Cancellation.Token)
                .ConfigureAwait(false);
            await supervisor.ImportAsync(result.Definition, CancellationToken.None).ConfigureAwait(false);
            await store.SetJavaAssignmentAsync(result.Definition.Id, java.Id, java.JavaPath,
                $"Managed runtime selected for Paper {plan.Version.VersionId} build {plan.Build.BuildId}",
                CancellationToken.None).ConfigureAwait(false);
            await store.UpsertUpdateSourceAsync(new UpdateSource
            {
                ServerId = result.Definition.Id,
                Provider = UpdateProvider.PaperMC,
                ProjectName = "Paper",
                ProjectId = "paper",
                InstalledVersionId = $"{plan.Version.VersionId}-{plan.Build.BuildId}",
                InstalledVersionName = $"Paper build {plan.Build.BuildId}",
                InstalledFileId = plan.Build.ServerSha256,
                MinecraftVersion = plan.Version.VersionId,
                Loader = ServerEcosystem.Paper.ToString(),
                LoaderVersion = plan.Build.BuildId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ReleaseChannel = plan.Build.Channel switch
                {
                    PaperBuildChannel.Stable => ReleaseChannel.Stable,
                    PaperBuildChannel.Beta => ReleaseChannel.Beta,
                    _ => ReleaseChannel.Alpha
                },
                SourceUrl = PaperVersionCatalogService.ProjectUrl,
                InstalledAt = DateTimeOffset.UtcNow,
                IsUserLinked = true,
                DetectionEvidence = "Recorded from the exact official PaperMC build selected during managed creation."
            }, CancellationToken.None).ConfigureAwait(false);

            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    Progress = new InstallProgress
                    {
                        OperationId = state.Id,
                        State = InstallState.Completed,
                        Phase = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationPhase.CleanupPending
                            : CreationPhase.Completed,
                        Stage = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationStage.CompletedWithCleanupWarning
                            : CreationStage.Completed,
                        CurrentStep = CreationStagePolicy.Describe(
                            result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                                ? CreationStage.CompletedWithCleanupWarning
                                : CreationStage.Completed),
                        OverallPercent = 100,
                        StagingLogPath = result.StagingLogPath
                    },
                    IsTerminal = true,
                    Success = true,
                    Result = result,
                    Outcome = result.Outcome,
                    Warnings = result.Warnings
                };
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = "Creation stopped. Nothing was put in place.",
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Cancelled,
                        Phase = CreationPhase.Cancelling,
                        Stage = CreationStage.Cancelled,
                        CurrentStep = CreationStagePolicy.Describe(CreationStage.Cancelled)
                    }
                };
        }
        catch (CreationDestinationBlockedException blocked)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = blocked.Message,
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Failed,
                        Phase = CreationPhase.Failed,
                        Stage = CreationStage.FailedNothingChanged,
                        CurrentStep = "That folder cannot be used"
                    }
                };
        }
        catch (Exception exception)
        {
            var outcome = await ReadOutcomeAsync(state.Id).ConfigureAwait(false);
            lock (state.Gate)
            {
                var reported = state.Snapshot.Progress.Stage;
                var ending = CreationStagePolicy.IsTerminal(reported) && reported != CreationStage.Completed
                    ? reported
                    : outcome == CreationOutcome.NothingActivated
                        ? CreationStage.FailedNothingChanged
                        : CreationStage.RecoveryRequired;
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Outcome = ending == CreationStage.FailedRolledBack ? CreationOutcome.RolledBack : outcome,
                    Progress = state.Snapshot.Progress with
                    {
                        State = ending == CreationStage.RecoveryRequired
                            ? InstallState.RecoveryRequired
                            : InstallState.Failed,
                        Phase = ending == CreationStage.RecoveryRequired
                            ? CreationPhase.RecoveryRequired
                            : ending == CreationStage.FailedRolledBack
                                ? CreationPhase.RolledBack
                                : CreationPhase.Failed,
                        Stage = ending,
                        CurrentStep = CreationStagePolicy.Describe(ending)
                    }
                };
            }
        }
    }

    private async Task RunManagedLoaderAsync(ManagedLoaderCreationPlan plan, OperationState state)
    {
        try
        {
            var platformStrategy = ManagedLoaderPlatformStrategies.For(plan.Version.Platform);
            if (!platformStrategy.SupportsTypedCreation)
                throw new InvalidOperationException(platformStrategy.CreationUnavailableReason);
            if (plan.Build.Platform != plan.Version.Platform)
                throw new InvalidOperationException("The selected loader build does not belong to the selected platform.");
            Report(state, InstallState.Planned, CreationPhase.Requested, CreationStage.Preparing,
                CreationStagePolicy.Describe(CreationStage.Preparing), 1);
            var requiredJava = plan.Build.RequiredJavaMajor ?? plan.Version.RequiredJavaMajor ??
                throw new InvalidOperationException("The Java version this loader combination needs was not established.");
            var platformName = plan.Version.Platform.ToString();
            var java = await PrepareRuntimeAsync(requiredJava,
                $"{platformName} {plan.Version.MinecraftVersion} Loader {plan.Build.LoaderVersion}", state)
                .ConfigureAwait(false);
            var installerJavaMajor = ManagedLoaderInstallerJavaPolicy.Resolve(
                plan.Build.Platform, plan.Build.InstallerJavaMajor, requiredJava);
            var installerJava = installerJavaMajor == requiredJava
                ? java
                : await PrepareRuntimeAsync(installerJavaMajor,
                    $"{platformName} installer {plan.Build.InstallerVersion}", state).ConfigureAwait(false);
            var sourceType = plan.Version.Platform switch
            {
                ManagedLoaderPlatform.Fabric => InstallSourceType.Fabric,
                ManagedLoaderPlatform.NeoForge => InstallSourceType.NeoForge,
                ManagedLoaderPlatform.Quilt => InstallSourceType.Quilt,
                ManagedLoaderPlatform.Forge => InstallSourceType.Forge,
                ManagedLoaderPlatform.LegacyFabric or ManagedLoaderPlatform.Ornithe =>
                    throw new InvalidOperationException(platformStrategy.CreationUnavailableReason),
                _ => throw new ArgumentOutOfRangeException(nameof(plan), plan.Version.Platform,
                    "Unknown managed-loader platform.")
            };
            var request = new ServerInstallRequest
            {
                OperationId = plan.OperationId,
                SourceType = sourceType,
                Source = plan.Build.ArtifactUrl,
                MinecraftVersion = plan.Version.MinecraftVersion,
                Build = plan.Build.LoaderVersion,
                InstallerVersion = plan.Build.InstallerVersion,
                ServerName = plan.ServerName,
                InstanceRoot = plan.InstanceRoot,
                JavaPath = java.JavaPath,
                InstallerJavaPath = installerJava.JavaPath,
                MinimumRamMb = plan.MinimumRamMb,
                MaximumRamMb = plan.MaximumRamMb,
                Port = plan.Port,
                CreationNetworkingPreference = plan.NetworkingPreference,
                MaxPlayers = plan.MaxPlayers,
                EulaAccepted = plan.Eula.Accepted,
                EulaAcceptedAt = plan.Eula.AcceptedAtUtc,
                ExpectedSha1 = plan.Build.ArtifactSha1,
                ExpectedSha256 = plan.Build.ArtifactSha256
            };
            var progress = new CallbackProgress<InstallProgress>(update =>
            {
                lock (state.Gate) state.Snapshot = state.Snapshot with { Progress = update };
            });
            var result = await installer.InstallAsync(request, progress, state.Cancellation.Token)
                .ConfigureAwait(false);
            await supervisor.ImportAsync(result.Definition, CancellationToken.None).ConfigureAwait(false);
            await store.SetJavaAssignmentAsync(result.Definition.Id, java.Id, java.JavaPath,
                $"Managed runtime selected for {platformName} {plan.Version.MinecraftVersion}",
                CancellationToken.None).ConfigureAwait(false);
            await store.UpsertUpdateSourceAsync(new UpdateSource
            {
                ServerId = result.Definition.Id,
                Provider = UpdateProvider.ManagedLoader,
                ProjectName = platformName,
                ProjectId = platformName.ToLowerInvariant(),
                InstalledVersionId = ManagedLoaderUpdateProvider.Identity(plan.Build),
                InstalledVersionName = $"{platformName} {plan.Build.LoaderVersion}",
                InstalledFileId = result.Sha256,
                MinecraftVersion = plan.Version.MinecraftVersion,
                Loader = platformName,
                LoaderVersion = plan.Build.LoaderVersion,
                InstallerVersion = plan.Build.InstallerVersion,
                ReleaseChannel = plan.Build.Channel switch
                {
                    ManagedLoaderChannel.Stable => ReleaseChannel.Stable,
                    ManagedLoaderChannel.Beta => ReleaseChannel.Beta,
                    _ => ReleaseChannel.Alpha
                },
                SourceUrl = platformStrategy.OfficialSourceUrl,
                InstalledAt = DateTimeOffset.UtcNow,
                IsUserLinked = true,
                DetectionEvidence = $"Recorded from exact official {platformName} metadata; installer {plan.Build.InstallerVersion}. Same-Minecraft-version loader updates only."
            }, CancellationToken.None).ConfigureAwait(false);
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    Progress = new InstallProgress
                    {
                        OperationId = state.Id,
                        State = InstallState.Completed,
                        Phase = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationPhase.CleanupPending
                            : CreationPhase.Completed,
                        Stage = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationStage.CompletedWithCleanupWarning
                            : CreationStage.Completed,
                        CurrentStep = CreationStagePolicy.Describe(
                            result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                                ? CreationStage.CompletedWithCleanupWarning
                                : CreationStage.Completed),
                        OverallPercent = 100,
                        StagingLogPath = result.StagingLogPath
                    },
                    IsTerminal = true,
                    Success = true,
                    Result = result,
                    Outcome = result.Outcome,
                    Warnings = result.Warnings
                };
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = "Creation stopped. Nothing was put in place.",
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Cancelled,
                        Phase = CreationPhase.Cancelling,
                        Stage = CreationStage.Cancelled,
                        CurrentStep = CreationStagePolicy.Describe(CreationStage.Cancelled)
                    }
                };
        }
        catch (CreationDestinationBlockedException blocked)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = blocked.Message,
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Failed,
                        Phase = CreationPhase.Failed,
                        Stage = CreationStage.FailedNothingChanged,
                        CurrentStep = "That folder cannot be used"
                    }
                };
        }
        catch (Exception exception)
        {
            var outcome = await ReadOutcomeAsync(state.Id).ConfigureAwait(false);
            lock (state.Gate)
            {
                var reported = state.Snapshot.Progress.Stage;
                var ending = CreationStagePolicy.IsTerminal(reported) && reported != CreationStage.Completed
                    ? reported
                    : outcome == CreationOutcome.NothingActivated
                        ? CreationStage.FailedNothingChanged
                        : CreationStage.RecoveryRequired;
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Outcome = ending == CreationStage.FailedRolledBack ? CreationOutcome.RolledBack : outcome,
                    Progress = state.Snapshot.Progress with
                    {
                        State = ending == CreationStage.RecoveryRequired ? InstallState.RecoveryRequired : InstallState.Failed,
                        Phase = ending == CreationStage.RecoveryRequired
                            ? CreationPhase.RecoveryRequired
                            : ending == CreationStage.FailedRolledBack ? CreationPhase.RolledBack : CreationPhase.Failed,
                        Stage = ending,
                        CurrentStep = CreationStagePolicy.Describe(ending)
                    }
                };
            }
        }
    }

    private async Task RunModpackAsync(ModpackCreationPlan plan, OperationState state)
    {
        try
        {
            Report(state, InstallState.Planned, CreationPhase.Requested, CreationStage.Preparing,
                "Preparing the exact modpack release", 1);
            var java = await PrepareRuntimeAsync(
                plan.RequiredJavaMajor,
                $"Modpack {plan.ProjectName} for Minecraft {plan.MinecraftVersion}",
                state).ConfigureAwait(false);
            var request = new ServerInstallRequest
            {
                OperationId = plan.OperationId,
                SourceType = InstallSourceType.ModrinthPack,
                Source = plan.Source,
                MinecraftVersion = plan.MinecraftVersion,
                Build = plan.VersionName,
                ServerName = plan.ServerName,
                InstanceRoot = plan.InstanceRoot,
                JavaPath = java.JavaPath,
                MinimumRamMb = plan.MinimumRamMb,
                MaximumRamMb = plan.MaximumRamMb,
                Port = plan.Port,
                CreationNetworkingPreference = plan.NetworkingPreference,
                MaxPlayers = plan.MaxPlayers,
                EulaAccepted = plan.Eula.Accepted,
                EulaAcceptedAt = plan.Eula.AcceptedAtUtc,
                ExpectedSha1 = plan.ExpectedSha1,
                ExpectedSha512 = plan.ExpectedSha512,
                ExpectedSizeBytes = plan.ExpectedSizeBytes,
                PackProvider = plan.Provider,
                PackProjectId = plan.ProjectId,
                PackProjectName = plan.ProjectName,
                PackVersionId = plan.VersionId,
                PackVersionName = plan.VersionName,
                PackReleaseChannel = plan.ReleaseChannel
            };
            await RunAsync(request, state).ConfigureAwait(false);
            InstallOperationSnapshot snapshot;
            lock (state.Gate) snapshot = state.Snapshot;
            if (snapshot.Success == true && snapshot.Result is { } result)
            {
                try
                {
                    await store.SetJavaAssignmentAsync(
                        result.Definition.Id,
                        java.Id,
                        java.JavaPath,
                        $"Managed runtime selected for {plan.ProjectName} ({plan.MinecraftVersion})",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Registration and the runnable absolute Java path are already durable at this
                    // point. A secondary assignment-index failure must never turn a successfully
                    // activated server into the false claim that nothing changed.
                    lock (state.Gate)
                        state.Snapshot = state.Snapshot with
                        {
                            Warnings = state.Snapshot.Warnings.Concat([
                                "The server was created, but ChunkPilot could not record the managed Java catalog assignment: " +
                                SecretRedactor.Redact(exception.Message)
                            ]).ToArray()
                        };
                }
            }
        }
        catch (Exception exception)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = exception is OperationCanceledException ? InstallState.Cancelled : InstallState.Failed,
                        Phase = exception is OperationCanceledException ? CreationPhase.Cancelling : CreationPhase.Failed,
                        Stage = exception is OperationCanceledException ? CreationStage.Cancelled : CreationStage.FailedNothingChanged,
                        CurrentStep = exception is OperationCanceledException
                            ? "Creation cancelled; nothing was activated"
                            : "The modpack was not activated"
                    }
                };
        }
    }

    private async Task RunImportAsync(ServerImportCreationPlan plan, OperationState state)
    {
        try
        {
            Report(state, InstallState.Planned, CreationPhase.Requested, CreationStage.Preparing,
                "Preparing the reviewed local server source", 1);
            if (plan.ManagementMode == ServerImportManagementMode.ByReference)
            {
                if (detector is null)
                    throw new InvalidOperationException("No read-only server detector is available in this Agent.");
                var detected = await detector.DetectAsync(plan.NativePath, state.Cancellation.Token).ConfigureAwait(false);
                var candidate = detected.Candidates.SingleOrDefault(item =>
                    Path.GetRelativePath(detected.RootPath, item.SourcePath).Replace('\\', '/')
                        .Equals(plan.LaunchRelativePath, StringComparison.OrdinalIgnoreCase));
                candidate ??= detected.Candidates.Count == 1 ? detected.Candidates[0] : null;
                if (candidate is null)
                    throw new InvalidOperationException("The reviewed server launcher is no longer available.");
                Report(state, InstallState.Validating, CreationPhase.VerifyingCandidate,
                    CreationStage.FinalSafetyCheck, "Validating the by-reference server folder", 60);
                var definition = new ServerDefinition
                {
                    Name = plan.ServerName.Trim(),
                    RootPath = detected.RootPath,
                    Executable = candidate.Executable,
                    Arguments = ServerLaunchPolicy.EnsureNoGui(candidate.Arguments, detected.Ecosystem, true),
                    WorkingDirectory = candidate.WorkingDirectory,
                    ReadinessPattern = @"Done \(.+?\)!|For help, type",
                    ShutdownTimeoutSeconds = detected.Ecosystem is ServerEcosystem.Forge or ServerEcosystem.NeoForge ? 120 : 60,
                    Ecosystem = detected.Ecosystem,
                    MinecraftVersion = detected.MinecraftVersion,
                    LoaderVersion = detected.LoaderVersion,
                    Port = plan.Port,
                    MinimumRamMb = plan.MinimumRamMb,
                    MaximumRamMb = plan.MaximumRamMb,
                    RunInBackground = true,
                    IsManaged = false
                };
                await supervisor.ImportAsync(definition, state.Cancellation.Token).ConfigureAwait(false);
                lock (state.Gate)
                    state.Snapshot = state.Snapshot with
                    {
                        IsTerminal = true,
                        Success = true,
                        Outcome = CreationOutcome.Completed,
                        Result = new InstallationResult { Definition = definition, Outcome = CreationOutcome.Completed },
                        Progress = state.Snapshot.Progress with
                        {
                            State = InstallState.Completed,
                            Phase = CreationPhase.Completed,
                            Stage = CreationStage.Completed,
                            CurrentStep = "Server folder added by reference",
                            OverallPercent = 100
                        }
                    };
                return;
            }

            var javaMajor = plan.Inspection.RequiredJavaMajor > 0 ? plan.Inspection.RequiredJavaMajor : 21;
            var java = await PrepareRuntimeAsync(javaMajor,
                $"Imported {plan.Inspection.Platform} server for Minecraft {plan.Inspection.MinecraftVersion}", state)
                .ConfigureAwait(false);
            var request = new ServerInstallRequest
            {
                OperationId = plan.OperationId,
                SourceType = plan.Inspection.SourceKind switch
                {
                    ServerImportSourceKind.ServerJar => InstallSourceType.LocalServerJar,
                    ServerImportSourceKind.ServerFolder => InstallSourceType.ExistingPackageFolder,
                    _ => InstallSourceType.LocalZip
                },
                Source = plan.NativePath,
                MinecraftVersion = plan.Inspection.MinecraftVersion,
                Build = plan.Inspection.LoaderVersion,
                LaunchRelativePath = plan.LaunchRelativePath,
                ServerName = plan.ServerName,
                InstanceRoot = plan.InstanceRoot,
                JavaPath = java.JavaPath,
                MinimumRamMb = plan.MinimumRamMb,
                MaximumRamMb = plan.MaximumRamMb,
                Port = plan.Port,
                CreationNetworkingPreference = plan.NetworkingPreference,
                MaxPlayers = plan.MaxPlayers,
                EulaAccepted = plan.Eula.Accepted,
                EulaAcceptedAt = plan.Eula.AcceptedAtUtc,
                ExpectedSha256 = plan.Inspection.Sha256,
                ExpectedSizeBytes = plan.Inspection.SourceKind == ServerImportSourceKind.ServerFolder
                    ? null : plan.Inspection.SourceSizeBytes
            };
            await RunAsync(request, state).ConfigureAwait(false);
            InstallOperationSnapshot snapshot;
            lock (state.Gate) snapshot = state.Snapshot;
            if (snapshot.Success == true && snapshot.Result is { } result)
                await store.SetJavaAssignmentAsync(result.Definition.Id, java.Id, java.JavaPath,
                    $"Managed runtime selected for imported {plan.Inspection.Platform} server",
                    CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = exception is OperationCanceledException ? InstallState.Cancelled : InstallState.Failed,
                        Phase = exception is OperationCanceledException ? CreationPhase.Cancelling : CreationPhase.Failed,
                        Stage = exception is OperationCanceledException ? CreationStage.Cancelled : CreationStage.FailedNothingChanged,
                        CurrentStep = exception is OperationCanceledException
                            ? "Import cancelled; nothing was activated" : "The local server source was not activated"
                    }
                };
        }
    }

    /// <summary>
    /// Finds a compatible managed runtime or acquires one, never guessing at the requirement.
    /// </summary>
    /// <remarks>
    /// The plan carries a Java major version that was established from official metadata or from
    /// ChunkPilot's own version rules; if neither established it the plan would already have been
    /// refused. Nothing here touches PATH, JAVA_HOME, Program Files or the registry.
    /// </remarks>
    private Task<ManagedJavaRuntime> PrepareRuntimeAsync(VanillaCreationPlan plan, OperationState state)
    {
        var required = plan.Version.RequiredJavaMajor
                       ?? throw new InvalidOperationException(
                           "The Java version this Minecraft release needs was never established.");
        return PrepareRuntimeAsync(required, $"Minecraft {plan.Version.VersionId}", state);
    }

    private async Task<ManagedJavaRuntime> PrepareRuntimeAsync(
        int required,
        string requirementEvidence,
        OperationState state)
    {
        if (javaRuntimes is null)
            throw new InvalidOperationException("No managed Java service is available in this Agent.");

        Report(state, InstallState.Staging, CreationPhase.PreparingStaging, CreationStage.PreparingJava,
            $"Checking for a compatible Java {required} runtime", 4);
        var installed = await store.GetManagedJavaRuntimesAsync(state.Cancellation.Token).ConfigureAwait(false);
        var reusable = JavaRuntimePolicy.Select(installed, new JavaRuntimeRequirement
        {
            MinimumMajor = required,
            MaximumMajor = required,
            Require64Bit = true,
            Evidence = requirementEvidence
        });
        if (reusable is not null)
        {
            Report(state, InstallState.Staging, CreationPhase.PreparingStaging, CreationStage.VerifyingJava,
                $"Using the Java {reusable.MajorVersion} runtime ChunkPilot already has", 8);
            return reusable;
        }

        state.Cancellation.Token.ThrowIfCancellationRequested();
        Report(state, InstallState.Downloading, CreationPhase.MaterializingCandidate, CreationStage.PreparingJava,
            $"Getting a private Java {required} runtime", 10);
        var runtimeProgress = new CallbackProgress<string>(message =>
            Report(state, InstallState.Downloading, CreationPhase.MaterializingCandidate,
                CreationStage.PreparingJava, message, 14));
        var acquired = await javaRuntimes.InstallAsync(required, runtimeProgress, state.Cancellation.Token)
            .ConfigureAwait(false);
        Report(state, InstallState.Validating, CreationPhase.MaterializingCandidate, CreationStage.VerifyingJava,
            $"Checked the Java {acquired.MajorVersion} runtime against its published checksum", 18);
        return acquired;
    }

    private static void Report(
        OperationState state,
        InstallState installState,
        CreationPhase phase,
        CreationStage stage,
        string step,
        double percent)
    {
        lock (state.Gate)
            state.Snapshot = state.Snapshot with
            {
                Progress = state.Snapshot.Progress with
                {
                    OperationId = state.Id,
                    State = installState,
                    Phase = phase,
                    Stage = stage,
                    CurrentStep = step,
                    OverallPercent = percent,
                    // The runtime steps have no byte total of their own, so a determinate bar here
                    // would be invented. Clearing it keeps the interface honest between downloads.
                    BytesDownloaded = 0,
                    TotalBytes = null,
                    BytesPerSecond = 0
                }
            };
    }

    public InstallOperationSnapshot Get(Guid operationId)
    {
        if (!operations.TryGetValue(operationId, out var state))
            throw new KeyNotFoundException($"Install operation {operationId} was not found.");
        lock (state.Gate)
            return state.Snapshot;
    }

    public void Cancel(Guid operationId)
    {
        if (!operations.TryGetValue(operationId, out var state))
            throw new KeyNotFoundException($"Install operation {operationId} was not found.");
        state.Cancellation.Cancel();
    }

    private async Task RunAsync(ServerInstallRequest request, OperationState state)
    {
        try
        {
            var progress = new CallbackProgress<InstallProgress>(update =>
            {
                lock (state.Gate)
                    state.Snapshot = state.Snapshot with { Progress = update };
            });
            var result = await installer.InstallAsync(request, progress, state.Cancellation.Token).ConfigureAwait(false);

            // The installer already registered and verified the server inside its transaction. This
            // attaches the now-durable definition to the running supervisor; the upsert it performs
            // is idempotent and writes the same record again rather than a second one.
            await supervisor.ImportAsync(result.Definition, CancellationToken.None).ConfigureAwait(false);
            if (request.EnableDailyBackup)
            {
                var schedule = new ScheduleEntry
                {
                    ServerId = result.Definition.Id,
                    Name = "Daily verified backup",
                    Action = ScheduledAction.Backup,
                    Kind = ScheduleKind.Daily,
                    TimeOfDay = new TimeSpan(4, 0, 0),
                    Enabled = true
                };
                schedule = schedule with
                {
                    NextRunAt = ScheduleCalculator.NextRun(schedule, DateTimeOffset.Now)
                };
                await store.UpsertScheduleAsync(schedule, state.Cancellation.Token)
                    .ConfigureAwait(false);
            }
            if (request.SourceType == InstallSourceType.LocalZip && File.Exists(request.Source))
                await store.UpsertUpdateSourceAsync(new UpdateSource
                {
                    ServerId = result.Definition.Id,
                    Provider = UpdateProvider.LocalPackageHistory,
                    ProjectName = request.ServerName.Trim(),
                    ProjectId = ManagedServerInstaller.MakeSafeInstanceName(request.ServerName),
                    InstalledVersionId = result.Sha256,
                    InstalledVersionName = string.IsNullOrWhiteSpace(request.Build)
                        ? result.Definition.MinecraftVersion : request.Build,
                    InstalledFileId = result.Sha256,
                    MinecraftVersion = result.Definition.MinecraftVersion,
                    Loader = result.Definition.Ecosystem.ToString(),
                    LoaderVersion = result.Definition.LoaderVersion,
                    SourceUrl = Path.GetFullPath(request.Source),
                    InstalledAt = DateTimeOffset.UtcNow,
                    DetectionEvidence = "Recorded by ChunkPilot managed installation."
                }, state.Cancellation.Token).ConfigureAwait(false);
            if (request.SourceType == InstallSourceType.ModrinthPack)
                await store.UpsertUpdateSourceAsync(new UpdateSource
                {
                    ServerId = result.Definition.Id,
                    Provider = request.PackProvider,
                    ProjectName = request.PackProjectName,
                    ProjectId = request.PackProjectId,
                    InstalledVersionId = request.PackVersionId,
                    InstalledVersionName = request.PackVersionName,
                    InstalledFileId = result.Sha256,
                    MinecraftVersion = result.Definition.MinecraftVersion,
                    Loader = result.Definition.Ecosystem.ToString(),
                    LoaderVersion = result.Definition.LoaderVersion,
                    ReleaseChannel = request.PackReleaseChannel,
                    SourceUrl = request.Source,
                    InstalledAt = DateTimeOffset.UtcNow,
                    IsUserLinked = request.PackProvider == UpdateProvider.Modrinth,
                    DetectionEvidence = request.PackProvider == UpdateProvider.Modrinth
                        ? "Recorded from exact Modrinth catalog identity and a verified .mrpack archive."
                        : "Recorded from a locally selected verified .mrpack archive."
                }, state.Cancellation.Token).ConfigureAwait(false);
            lock (state.Gate)
            {
                var completedStage = CreationStagePolicy.ForSuccessfulOutcome(result.Outcome);
                state.Snapshot = new InstallOperationSnapshot
                {
                    OperationId = state.Id,
                    Progress = new InstallProgress
                    {
                        OperationId = state.Id,
                        State = InstallState.Completed,
                        Phase = result.Outcome == CreationOutcome.CompletedWithCleanupWarning
                            ? CreationPhase.CleanupPending
                            : CreationPhase.Completed,
                        Stage = completedStage,
                        CurrentStep = CreationStagePolicy.Describe(completedStage),
                        OverallPercent = 100,
                        StagingLogPath = result.StagingLogPath
                    },
                    IsTerminal = true,
                    Success = true,
                    Result = result,
                    Outcome = result.Outcome,
                    Warnings = result.Warnings
                };
            }
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = "Installation cancelled.",
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Cancelled,
                        Phase = CreationPhase.Cancelling,
                        CurrentStep = "Cancelled; nothing was put in place"
                    }
                };
        }
        catch (CreationDestinationBlockedException blocked)
        {
            // A refused destination is not a crash: nothing was changed, and the policy's own wording
            // already explains what is true and what the user can do next.
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = blocked.Message,
                    Outcome = CreationOutcome.NothingActivated,
                    Progress = state.Snapshot.Progress with
                    {
                        State = InstallState.Failed,
                        Phase = CreationPhase.Failed,
                        CurrentStep = "That folder cannot be used"
                    }
                };
        }
        catch (Exception exception)
        {
            var outcome = await ReadOutcomeAsync(state.Id).ConfigureAwait(false);
            lock (state.Gate)
                state.Snapshot = state.Snapshot with
                {
                    IsTerminal = true,
                    Success = false,
                    Error = SecretRedactor.Redact(exception.Message),
                    Outcome = outcome,
                    Progress = state.Snapshot.Progress with
                    {
                        State = outcome == CreationOutcome.NothingActivated
                            ? InstallState.Failed
                            : InstallState.RecoveryRequired,
                        Phase = outcome == CreationOutcome.NothingActivated
                            ? CreationPhase.Failed
                            : CreationPhase.RecoveryRequired,
                        CurrentStep = CreationPhasePolicy.Describe(outcome)
                    }
                };
        }
    }

    /// <summary>
    /// Reads the durable outcome the transaction recorded, so a failed install reports what is
    /// actually true rather than a blanket failure.
    /// </summary>
    /// <remarks>
    /// No surviving journal row means the transaction finished its own unwinding and nothing was left
    /// behind; that is the only case where "nothing was activated" can be stated without evidence.
    /// </remarks>
    private async Task<CreationOutcome> ReadOutcomeAsync(Guid operationId)
    {
        try
        {
            var record = await store.GetCreationJournalAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            if (record is null)
                return CreationOutcome.NothingActivated;
            return record.Entry?.Outcome ?? CreationOutcome.Inconsistent;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return CreationOutcome.RecoveryRequired;
        }
    }

    private sealed class OperationState
    {
        private readonly DateTimeOffset startedAtUtc;
        private InstallOperationSnapshot snapshot;
        private long revision;

        public OperationState(Guid id)
        {
            Id = id;
            startedAtUtc = DateTimeOffset.UtcNow;
            revision = 1;
            snapshot = new InstallOperationSnapshot
            {
                OperationId = id,
                Revision = 1,
                StartedAtUtc = startedAtUtc,
                UpdatedAtUtc = startedAtUtc,
                Progress = new InstallProgress
                {
                    OperationId = id,
                    State = InstallState.Planned,
                    CurrentStep = "Queued",
                    OverallPercent = 0
                }
            };
        }

        public Guid Id { get; }
        public object Gate { get; } = new();
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? Task { get; set; }
        public InstallOperationSnapshot Snapshot
        {
            get => snapshot;
            set
            {
                revision += 1;
                snapshot = value with
                {
                    Revision = revision,
                    StartedAtUtc = startedAtUtc,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }
        }
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
