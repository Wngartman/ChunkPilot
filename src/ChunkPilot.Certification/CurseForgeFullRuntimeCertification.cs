using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

internal sealed record CurseForgeExactReleaseSelection
{
    public required string ProjectReference { get; init; }
    public required string ClientFileId { get; init; }

    public void Validate(string label)
    {
        if (string.IsNullOrWhiteSpace(ProjectReference) || ProjectReference.Length > 80 ||
            !long.TryParse(ClientFileId, out var fileId) || fileId <= 0)
            throw new ArgumentException(
                $"The {label} selection requires a bounded project reference and positive client file ID.");
    }
}

internal sealed record CurseForgeFullCampaignOptions
{
    public required CurseForgeExactReleaseSelection OfficialNewer { get; init; }
    public required CurseForgeExactReleaseSelection Generated { get; init; }
    public CurseForgeExactReleaseSelection? GeneratedFallback { get; init; }
    public required string ForgeMinecraftVersion { get; init; }
    public required string ForgeLoaderVersion { get; init; }
    public required string ModProjectId { get; init; }
    public required string ModFileId { get; init; }

    public void Validate()
    {
        OfficialNewer.Validate("official newer release");
        Generated.Validate("generated candidate");
        GeneratedFallback?.Validate("generated fallback candidate");
        if (string.IsNullOrWhiteSpace(ForgeMinecraftVersion) || ForgeMinecraftVersion.Length > 40 ||
            string.IsNullOrWhiteSpace(ForgeLoaderVersion) || ForgeLoaderVersion.Length > 80)
            throw new ArgumentException(
                "Full certification requires exact bounded Forge Minecraft and loader versions.");
        if (!PositiveId(ModProjectId) || !PositiveId(ModFileId))
            throw new ArgumentException(
                "Full certification requires exact positive CurseForge mod project and file IDs.");
        if (GeneratedFallback is not null &&
            GeneratedFallback.ProjectReference.Equals(
                Generated.ProjectReference, StringComparison.OrdinalIgnoreCase) &&
            GeneratedFallback.ClientFileId.Equals(Generated.ClientFileId, StringComparison.Ordinal))
            throw new ArgumentException(
                "The generated fallback must identify a different exact CurseForge release.");
    }

    private static bool PositiveId(string value) => long.TryParse(value, out var parsed) && parsed > 0;
}

internal sealed record FullCreationEvidence
{
    public string Name { get; init; } = "";
    public string SourceKind { get; init; } = "";
    public string ProjectId { get; init; } = "";
    public string ClientFileId { get; init; } = "";
    public string ServerPackFileId { get; init; } = "";
    public Guid? PreflightOperationId { get; init; }
    public Guid CreationOperationId { get; init; }
    public Guid ServerId { get; init; }
    public int GeneratedRequiredFileCount { get; init; }
    public long ExpectedCurseForgeBytes { get; init; }
    public string LocalSha256 { get; init; } = "";
    public bool LifecycleVerified { get; init; }
    public bool CleanupVerified { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string Status { get; init; } = "FAILED";
}

internal sealed record FullManagedContentReleaseEvidence
{
    public string ProjectId { get; init; } = "";
    public string FileId { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public string LocalSha256 { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public bool Removed { get; init; }
}

internal sealed record FullManagedContentEvidence
{
    public Guid ServerId { get; init; }
    public string RequestedProjectId { get; init; } = "";
    public string RequestedFileId { get; init; } = "";
    public Guid InstallOperationId { get; init; }
    public bool ExactPlanAuthorized { get; init; }
    public bool WrongServerAuthorizationRejected { get; init; }
    public bool WrongServerInventoriesUnchanged { get; init; }
    public bool InstalledLifecycleVerified { get; init; }
    public string RefreshedProviderFileId { get; init; } = "";
    public string UpdateState { get; init; } = "";
    public IReadOnlyList<FullManagedContentReleaseEvidence> Releases { get; init; } = [];
    public string SentinelSha256 { get; init; } = "";
    public bool SentinelPreserved { get; init; }
    public bool InventoryRemovalVerified { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string Status { get; init; } = "FAILED";
}

internal sealed record FullUpdateEvidence
{
    public Guid ServerId { get; init; }
    public string FromClientFileId { get; init; } = "";
    public string ToClientFileId { get; init; } = "";
    public Guid PrimaryOperationId { get; init; }
    public Guid? ConfirmedOperationId { get; init; }
    public bool CheckUpdatesIdentityVerified { get; init; }
    public bool MigrationReviewRequired { get; init; }
    public bool Success { get; init; }
    public bool AutomaticRollback { get; init; }
    public Guid? PreviousSnapshotId { get; init; }
    public Guid? ActiveSnapshotId { get; init; }
    public string ActiveProviderFileId { get; init; } = "";
    public string DownloadLocalSha256 { get; init; } = "";
    public long DownloadedServerPackBytes { get; init; }
    public bool SentinelsPreserved { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string Status { get; init; } = "FAILED";
}

internal sealed record FullRollbackEvidence
{
    public Guid ServerId { get; init; }
    public Guid TargetSnapshotId { get; init; }
    public string ActiveClientFileId { get; init; } = "";
    public string ActiveProviderFileId { get; init; } = "";
    public bool ServerStopped { get; init; }
    public bool PortAbsent { get; init; }
    public bool LifecycleVerified { get; init; }
    public bool SentinelsPreserved { get; init; }
    public double ElapsedMilliseconds { get; init; }
    public string Status { get; init; } = "FAILED";
}

internal sealed class FullSentinelPreservationEvidence
{
    public string ManifestSha256 { get; init; } = "";
    public int FileCount { get; init; }
    public int MinimumRamMb { get; init; }
    public int MaximumRamMb { get; init; }
    public bool BaselineLifecycleVerified { get; init; }
    public bool AfterSuccessfulUpdate { get; set; }
    public bool AfterExplicitRollback { get; set; }
    public bool AfterControlledFailureRollback { get; set; }
}

internal sealed record CurseForgeFullCampaignEvidence
{
    public FullCreationEvidence? OfficialOlder { get; set; }
    public FullCreationEvidence? OfficialNewer { get; set; }
    public FullCreationEvidence? GeneratedCandidate { get; set; }
    public IReadOnlyList<FullCreationEvidence> GeneratedCandidateAttempts { get; set; } = [];
    public FullCreationEvidence? ForgeServer { get; set; }
    public FullManagedContentEvidence? ManagedContent { get; set; }
    public FullUpdateEvidence? SuccessfulUpdate { get; set; }
    public FullRollbackEvidence? ExplicitRollback { get; set; }
    public FullUpdateEvidence? ControlledFailureRollback { get; set; }
    public FullSentinelPreservationEvidence? SentinelPreservation { get; set; }
    public bool AllServersStopped { get; set; }
    public bool SelectedPortAbsent { get; set; }
    public string Status { get; set; } = "FAILED";
}

internal sealed partial class CurseForgeRuntimeCertificationSession
{
    private sealed record CertifiedCreation(
        CatalogItem? Project,
        CatalogVersion? Release,
        CurseForgeModpackPreflightResult? Preflight,
        InstallationResult Result,
        FullCreationEvidence Evidence);

    private sealed record ResolvedOfficial(CatalogItem Project, CatalogVersion Release);

    private sealed record PayloadReservation(
        Guid OperationId,
        string Kind,
        string ProjectId,
        string FileId,
        long ExpectedBytes,
        string ProviderSha1);

    private sealed record CompletedUpdateRun(
        FullUpdateEvidence Evidence,
        UpdateOperationSnapshot Terminal,
        VersionSnapshot PreviousSnapshot,
        VersionSnapshot ActiveSnapshot);

    private sealed record SentinelEvidence(string RelativePath, string Sha256);

    private sealed record UpdateSentinelFile(
        string RelativePath,
        long SizeBytes,
        string Sha256);

    private sealed record UpdateSentinelManifest(
        Guid ServerId,
        string ServerRoot,
        int MinimumRamMb,
        int MaximumRamMb,
        IReadOnlyList<UpdateSentinelFile> Files,
        string ManifestSha256);

    internal readonly record struct KnownPayloadProjectionEvidence(
        long OfficialOlderBytes,
        long GeneratedWorstCaseBytes,
        long UpdateWorstCaseBytes)
    {
        public long TotalBytes => checked(
            OfficialOlderBytes + GeneratedWorstCaseBytes + UpdateWorstCaseBytes);
    }

    private sealed record UpdateAttemptReservations(
        PayloadReservation Client,
        PayloadReservation? Server);

    private sealed class GeneratedCandidateUnsupportedException(
        FullCreationEvidence evidence,
        string message,
        Exception? innerException = null) : InvalidOperationException(message, innerException)
    {
        public FullCreationEvidence Evidence { get; } = evidence;
    }

    public async Task ExecuteFullCampaignAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken)
    {
        var fullOptions = options.FullCampaign ?? throw new InvalidOperationException(
            "The Full campaign selections are unavailable.");
        var campaign = new CurseForgeFullCampaignEvidence();
        report.FullCampaign = campaign;
        RecordPortAvailability(options, report, "full campaign pre-payload TCP port ownership");

        var olderSelection = new CurseForgeExactReleaseSelection
        {
            ProjectReference = options.ProjectReference,
            ClientFileId = options.ClientFileId
        };
        var olderProject = await ResolveFullSelectionAsync(
            options, report, olderSelection, "official older exact project and file resolution",
            cancellationToken).ConfigureAwait(false);
        var olderRelease = ExactRelease(olderProject, olderSelection.ClientFileId);
        RequireOfficialRelease(olderRelease, "official older");
        BindResolvedIdentity(report, olderProject, olderRelease);
        var newerProject = await ResolveFullSelectionAsync(
            options, report, fullOptions.OfficialNewer,
            "official newer exact project and file resolution", cancellationToken).ConfigureAwait(false);
        var newerRelease = ExactRelease(newerProject, fullOptions.OfficialNewer.ClientFileId);
        RequireOfficialRelease(newerRelease, "official newer");
        if (!olderProject.ProjectId.Equals(newerProject.ProjectId, StringComparison.Ordinal) ||
            olderRelease.ClientFileId.Equals(newerRelease.ClientFileId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The official update pair must be two distinct client releases of the same exact CurseForge project.");
        var generatedProject = await ResolveFullSelectionAsync(
            options, report, fullOptions.Generated,
            "generated exact project and file resolution", cancellationToken).ConfigureAwait(false);
        var generatedRelease = ExactRelease(generatedProject, fullOptions.Generated.ClientFileId);
        (CatalogItem Project, CatalogVersion Release)? generatedFallback = null;
        if (fullOptions.GeneratedFallback is { } fallbackSelection)
        {
            var fallbackProject = await ResolveFullSelectionAsync(
                options, report, fallbackSelection,
                "generated fallback exact project and file resolution", cancellationToken)
                .ConfigureAwait(false);
            generatedFallback = (fallbackProject,
                ExactRelease(fallbackProject, fallbackSelection.ClientFileId));
        }
        var initialBudget = await payloadBudget.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var knownProjection = KnownPayloadProjection(
            olderRelease, newerRelease, generatedRelease, generatedFallback?.Release);
        ValidateKnownPayloadProjection(initialBudget, knownProjection);
        report.Steps.Add(new CertificationStepEvidence
        {
            Name = "pre-campaign known CurseForge payload projection",
            Status = "PASSED",
            Detail =
                $"Known network paths require at most {knownProjection.TotalBytes} bytes before generated-file and managed-content plans; those later exact files remain independently guarded before every transfer.",
            State = "Guarded"
        });

        var older = await CreateOfficialFullAsync(
            options, report, olderSelection, olderProject, olderRelease,
            "official older", cancellationToken).ConfigureAwait(false);
        campaign.OfficialOlder = older.Evidence;
        report.DpapiRelaunchAuthenticatedCatalogResolve = true;
        report.ProjectId = older.Evidence.ProjectId;
        report.ClientFileId = older.Evidence.ClientFileId;
        report.ServerPackFileId = older.Evidence.ServerPackFileId;

        var newer = new ResolvedOfficial(newerProject, newerRelease);

        CertifiedCreation generated;
        try
        {
            generated = await CreateGeneratedFullAsync(
                options, report, fullOptions.Generated, generatedProject, generatedRelease,
                cancellationToken).ConfigureAwait(false);
        }
        catch (GeneratedCandidateUnsupportedException exception)
        {
            campaign.GeneratedCandidateAttempts = [exception.Evidence];
            if (fullOptions.GeneratedFallback is null || generatedFallback is null)
            {
                campaign.Status = "UNSUPPORTED";
                throw;
            }
            try
            {
                generated = await CreateGeneratedFullAsync(
                    options, report, fullOptions.GeneratedFallback,
                    generatedFallback.Value.Project, generatedFallback.Value.Release,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (GeneratedCandidateUnsupportedException fallbackException)
            {
                campaign.GeneratedCandidateAttempts = campaign.GeneratedCandidateAttempts
                    .Concat([fallbackException.Evidence]).ToArray();
                campaign.Status = "UNSUPPORTED";
                throw;
            }
        }
        campaign.GeneratedCandidate = generated.Evidence;
        campaign.GeneratedCandidateAttempts = campaign.GeneratedCandidateAttempts
            .Concat([generated.Evidence]).ToArray();

        var forge = await CreateForgeFullAsync(
            options, report, fullOptions, cancellationToken).ConfigureAwait(false);
        campaign.ForgeServer = forge.Evidence;
        campaign.ManagedContent = await ExerciseManagedContentFullAsync(
            options, report, fullOptions, forge.Result.Definition,
            generated.Result.Definition.Id, cancellationToken).ConfigureAwait(false);

        var sentinelManifest = await CreateUpdateSentinelManifestAsync(
            options, report, older.Result.Definition, cancellationToken).ConfigureAwait(false);
        campaign.SentinelPreservation = new FullSentinelPreservationEvidence
        {
            ManifestSha256 = sentinelManifest.ManifestSha256,
            FileCount = sentinelManifest.Files.Count,
            MinimumRamMb = sentinelManifest.MinimumRamMb,
            MaximumRamMb = sentinelManifest.MaximumRamMb,
            BaselineLifecycleVerified = true
        };

        var update = await ExerciseSuccessfulUpdateAsync(
            options, report, older, newer, cancellationToken).ConfigureAwait(false);
        await VerifyUpdateSentinelsAsync(
            options, sentinelManifest, cancellationToken).ConfigureAwait(false);
        campaign.SentinelPreservation.AfterSuccessfulUpdate = true;
        update = update with { Evidence = update.Evidence with { SentinelsPreserved = true } };
        campaign.SuccessfulUpdate = update.Evidence;
        campaign.OfficialNewer = new FullCreationEvidence
        {
            Name = "official newer",
            SourceKind = "CurseForgeOfficialUpdate",
            ProjectId = newer.Project.ProjectId,
            ClientFileId = newer.Release.ClientFileId,
            ServerPackFileId = newer.Release.ServerPackFileId,
            CreationOperationId = update.Evidence.ConfirmedOperationId ??
                                  update.Evidence.PrimaryOperationId,
            ServerId = older.Result.Definition.Id,
            ExpectedCurseForgeBytes = checked(
                newer.Release.ClientSizeBytes!.Value + newer.Release.SizeBytes!.Value),
            LocalSha256 = update.Evidence.DownloadLocalSha256,
            LifecycleVerified = true,
            CleanupVerified = true,
            ElapsedMilliseconds = update.Evidence.ElapsedMilliseconds,
            Status = "PASSED"
        };
        campaign.ExplicitRollback = await ExerciseExplicitRollbackAsync(
            options, report, older, newer, update, cancellationToken).ConfigureAwait(false);
        await VerifyUpdateSentinelsAsync(
            options, sentinelManifest, cancellationToken).ConfigureAwait(false);
        campaign.SentinelPreservation.AfterExplicitRollback = true;
        campaign.ExplicitRollback = campaign.ExplicitRollback with { SentinelsPreserved = true };
        campaign.ControlledFailureRollback = await ExerciseControlledFailureUpdateAsync(
            options, report, older, newer, update.Evidence.MigrationReviewRequired, cancellationToken)
            .ConfigureAwait(false);
        await VerifyUpdateSentinelsAsync(
            options, sentinelManifest, cancellationToken).ConfigureAwait(false);
        campaign.SentinelPreservation.AfterControlledFailureRollback = true;
        campaign.ControlledFailureRollback = campaign.ControlledFailureRollback with
        {
            SentinelsPreserved = true
        };

        var dashboard = await transport.SendAsync<DashboardSnapshot>(
            "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
        campaign.AllServersStopped = dashboard.Servers.All(server =>
            server.State is ServerState.Stopped or ServerState.Crashed);
        campaign.SelectedPortAbsent = !PortHasListener(options.Port);
        if (!campaign.AllServersStopped || !campaign.SelectedPortAbsent)
            throw new InvalidOperationException(
                "The Full campaign ended with an active task server or selected-port listener.");
        var budget = await payloadBudget.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, budget);
        campaign.Status = "PASSED";
        report.Success = true;
    }

    private static void RecordPortAvailability(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        string name)
    {
        var watch = Stopwatch.StartNew();
        EnsurePortAvailable(options.Port);
        watch.Stop();
        report.Steps.Add(new CertificationStepEvidence
        {
            Name = name,
            Status = "PASSED",
            Detail = "The selected loopback and wildcard TCP port was bind-available before payload work.",
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
        });
    }

    private async Task<CertifiedCreation> CreateOfficialFullAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CurseForgeExactReleaseSelection selection,
        CatalogItem project,
        CatalogVersion release,
        string label,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        RecordPortAvailability(options, report, label + " pre-payload TCP port ownership");
        RequireOfficialRelease(release, label);

        var preflightOperationId = Guid.NewGuid();
        var clientKind = "full-" + label.Replace(' ', '-') + "-client-preflight";
        var clientReservation = new PayloadReservation(
            preflightOperationId, clientKind, project.ProjectId, release.ClientFileId,
            release.ClientSizeBytes!.Value, release.ClientSha1);
        await ReserveAsync(report, clientReservation, cancellationToken).ConfigureAwait(false);
        var preflight = await StepAsync(
            report, options, label + " exact client-manifest preflight",
            () => transport.SendAsync<CurseForgeModpackPreflightResult>(
                "PreflightCurseForgeModpack",
                new CurseForgeModpackPreflightRequest(
                    preflightOperationId, project.ProjectId, release.ClientFileId,
                    release.ServerPackFileId),
                cancellationToken),
            preflightOperationId).ConfigureAwait(false);
        ValidatePreflight(preflightOperationId, project, release, preflight);
        if (preflight.State != CatalogReleasePreflightState.Ready)
            throw new InvalidOperationException(
                $"The {label} client-manifest preflight did not reach Ready.");
        await CompleteAsync(
            report, clientReservation, preflight.ClientSizeBytes, preflight.ClientSha256,
            preflight.State.ToString(), true, cancellationToken).ConfigureAwait(false);

        var refreshedProject = await ResolveFullSelectionAsync(
            options, report, selection, label + " provider identity recheck", cancellationToken)
            .ConfigureAwait(false);
        var refreshedRelease = ExactRelease(refreshedProject, selection.ClientFileId);
        if (!SameProviderIdentity(project, release, refreshedProject, refreshedRelease))
            throw new InvalidDataException(
                $"The exact {label} provider identity changed after preflight.");
        project = refreshedProject;
        release = refreshedRelease;

        var creationOperationId = Guid.NewGuid();
        activeCreationOperationId = creationOperationId;
        activeCreationTerminal = false;
        var serverKind = "full-" + label.Replace(' ', '-') + "-server-pack";
        var serverReservation = new PayloadReservation(
            creationOperationId, serverKind, project.ProjectId, release.ServerPackFileId,
            preflight.ServerPackSizeBytes!.Value, preflight.ServerPackSha1);
        await ReserveAsync(report, serverReservation, cancellationToken).ConfigureAwait(false);
        var plan = BuildPlan(
            options with { ServerName = FullServerName(options.ServerName, label) },
            creationOperationId, project, release, preflight);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidDataException(string.Join(" ", problems));
        var started = await StepAsync(
            report, options, "begin " + label + " server-pack creation",
            () => transport.SendAsync<InstallOperationRequest>(
                "BeginModpackCreation", new BeginModpackCreationRequest(plan), cancellationToken),
            creationOperationId).ConfigureAwait(false);
        if (started.OperationId != creationOperationId)
            throw new InvalidDataException(
                $"The Agent registered {label} creation under a different operation identity.");
        var terminal = await PollInstallAsync(
            options, report, creationOperationId, label + " server-pack creation", cancellationToken)
            .ConfigureAwait(false);
        activeCreationTerminal = true;
        terminalCreationOperationIds.Add(creationOperationId);
        if (!terminal.Success || terminal.Result is null)
            throw new InvalidOperationException(
                $"The {label} server-pack creation reached a failed terminal state.");
        await CompleteAsync(
            report, serverReservation, serverReservation.ExpectedBytes,
            terminal.Result.Sha256, terminal.Progress.State.ToString(), true, cancellationToken)
            .ConfigureAwait(false);

        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, terminal.Result.Definition.Id, cancellationToken).ConfigureAwait(false);
        var cleanup = await VerifyCertifiedPostconditionsAsync(
            options, report, terminal.Result.Definition.Id, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new CertifiedCreation(
            project, release, preflight, terminal.Result,
            new FullCreationEvidence
            {
                Name = label,
                SourceKind = ModpackCreationSource.CurseForgeOfficialServerPack.ToString(),
                ProjectId = project.ProjectId,
                ClientFileId = release.ClientFileId,
                ServerPackFileId = release.ServerPackFileId,
                PreflightOperationId = preflight.OperationId,
                CreationOperationId = creationOperationId,
                ServerId = terminal.Result.Definition.Id,
                ExpectedCurseForgeBytes = checked(
                    preflight.ClientSizeBytes + preflight.ServerPackSizeBytes!.Value),
                LocalSha256 = terminal.Result.Sha256,
                LifecycleVerified = true,
                CleanupVerified = CleanupPassed(cleanup),
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                Status = "PASSED"
            });
    }

    private async Task<CertifiedCreation> CreateGeneratedFullAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CurseForgeExactReleaseSelection selection,
        CatalogItem project,
        CatalogVersion release,
        CancellationToken cancellationToken)
    {
        const string label = "generated candidate";
        var watch = Stopwatch.StartNew();
        RecordPortAvailability(options, report, label + " pre-payload TCP port ownership");
        if (release.HasServerPackage || !string.IsNullOrWhiteSpace(release.ServerPackFileId) ||
            !release.CanGenerateServerCandidate || release.ClientSizeBytes is not > 0 ||
            release.ClientSha1.Length != 40)
            throw new InvalidOperationException(
                "The generated selection is not an integrity-verifiable release without an official server pack.");

        var preflightOperationId = Guid.NewGuid();
        var clientReservation = new PayloadReservation(
            preflightOperationId, "full-generated-client-preflight", project.ProjectId,
            release.ClientFileId, release.ClientSizeBytes.Value, release.ClientSha1);
        await ReserveAsync(report, clientReservation, cancellationToken).ConfigureAwait(false);
        var preflight = await StepAsync(
            report, options, "generated exact client-manifest preflight",
            () => transport.SendAsync<CurseForgeModpackPreflightResult>(
                "PreflightCurseForgeModpack",
                new CurseForgeModpackPreflightRequest(
                    preflightOperationId, project.ProjectId, release.ClientFileId, ""),
                cancellationToken),
            preflightOperationId).ConfigureAwait(false);
        ValidateGeneratedClientIdentity(
            preflightOperationId, project, release, preflight);
        await CompleteAsync(
            report, clientReservation, preflight.ClientSizeBytes, preflight.ClientSha256,
            preflight.State.ToString(), true, cancellationToken).ConfigureAwait(false);
        if (preflight.State != CatalogReleasePreflightState.Ready ||
            preflight.GeneratedPackPlan is null)
        {
            watch.Stop();
            throw new GeneratedCandidateUnsupportedException(
                new FullCreationEvidence
                {
                    Name = label,
                    SourceKind = ModpackCreationSource.CurseForgeGeneratedCandidate.ToString(),
                    ProjectId = project.ProjectId,
                    ClientFileId = release.ClientFileId,
                    PreflightOperationId = preflight.OperationId,
                    ExpectedCurseForgeBytes = preflight.ClientSizeBytes,
                    LocalSha256 = preflight.ClientSha256,
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    Status = "UNSUPPORTED"
                },
                "The exact generated candidate was truthfully unsupported after native preflight.");
        }
        var generatedPlan = ValidateGeneratedPreflight(
            preflightOperationId, project, release, preflight);

        var additionalGeneratedBytes = CalculateGeneratedCreationAdditionalBytes(
            preflight.ClientSizeBytes,
            generatedPlan.RequiredFiles.Select(file => file.SizeBytes).ToArray());
        var budgetBeforeGeneratedCreation = await payloadBudget.GetSnapshotAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            RequireKnownPayloadProjectionFits(
                budgetBeforeGeneratedCreation.LimitBytes,
                budgetBeforeGeneratedCreation.GuardedCumulativeBytes,
                additionalGeneratedBytes);
        }
        catch (InvalidOperationException exception)
        {
            watch.Stop();
            throw new GeneratedCandidateUnsupportedException(
                new FullCreationEvidence
                {
                    Name = label,
                    SourceKind = ModpackCreationSource.CurseForgeGeneratedCandidate.ToString(),
                    ProjectId = project.ProjectId,
                    ClientFileId = release.ClientFileId,
                    PreflightOperationId = preflight.OperationId,
                    GeneratedRequiredFileCount = generatedPlan.RequiredFiles.Count,
                    ExpectedCurseForgeBytes = checked(
                        preflight.ClientSizeBytes + additionalGeneratedBytes),
                    LocalSha256 = preflight.ClientSha256,
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    Status = "UNSUPPORTED"
                },
                "The exact generated candidate exceeds the remaining hard payload budget; " +
                "no creation archive or required file was reserved or transferred.",
                exception);
        }

        var refreshedProject = await ResolveFullSelectionAsync(
            options, report, selection, "generated provider identity recheck", cancellationToken)
            .ConfigureAwait(false);
        var refreshedRelease = ExactRelease(refreshedProject, selection.ClientFileId);
        if (!SameGeneratedProviderIdentity(project, release, refreshedProject, refreshedRelease))
            throw new InvalidDataException(
                "The exact generated-candidate provider identity changed after preflight.");
        project = refreshedProject;
        release = refreshedRelease;

        var creationOperationId = Guid.NewGuid();
        activeCreationOperationId = creationOperationId;
        activeCreationTerminal = false;
        var archiveReservation = new PayloadReservation(
            creationOperationId, "full-generated-client-creation", project.ProjectId,
            release.ClientFileId, preflight.ClientSizeBytes, preflight.ClientSha1);
        var fileReservations = new List<(PayloadReservation Reservation, CurseForgeGeneratedFilePlan File)>();
        foreach (var file in generatedPlan.RequiredFiles)
        {
            var reservation = new PayloadReservation(
                Guid.NewGuid(), "full-generated-required-file", file.ProjectId, file.FileId,
                file.SizeBytes, file.ProviderSha1);
            fileReservations.Add((reservation, file));
        }
        await ReserveBatchAsync(
            report,
            [archiveReservation, .. fileReservations.Select(item => item.Reservation)],
            cancellationToken).ConfigureAwait(false);

        var plan = BuildGeneratedPlan(
            options with { ServerName = FullServerName(options.ServerName, label) },
            creationOperationId, project, release, preflight);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidDataException(string.Join(" ", problems));
        var started = await StepAsync(
            report, options, "begin generated candidate creation",
            () => transport.SendAsync<InstallOperationRequest>(
                "BeginModpackCreation", new BeginModpackCreationRequest(plan), cancellationToken),
            creationOperationId).ConfigureAwait(false);
        if (started.OperationId != creationOperationId)
            throw new InvalidDataException(
                "The Agent registered generated creation under a different operation identity.");
        var terminal = await PollInstallAsync(
            options, report, creationOperationId, "generated candidate creation", cancellationToken)
            .ConfigureAwait(false);
        activeCreationTerminal = true;
        terminalCreationOperationIds.Add(creationOperationId);
        if (!terminal.Success || terminal.Result is null)
            throw new InvalidOperationException(
                "The generated candidate creation reached a failed terminal state.");
        if (!terminal.Result.Sha256.Equals(preflight.ClientSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The generated creation did not retain the exact preflight client-archive identity.");
        await CompleteAsync(
            report, archiveReservation, archiveReservation.ExpectedBytes,
            terminal.Result.Sha256, terminal.Progress.State.ToString(), true, cancellationToken)
            .ConfigureAwait(false);

        var inventory = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(terminal.Result.Definition.Id), cancellationToken)
            .ConfigureAwait(false);
        foreach (var (reservation, file) in fileReservations)
        {
            var installed = inventory.SingleOrDefault(item =>
                item.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase) &&
                item.SizeBytes == file.SizeBytes && item.Sha256.Length == 64)
                ?? throw new InvalidDataException(
                    "Generated creation inventory did not prove one exact planned JAR.");
            await CompleteAsync(
                report, reservation, reservation.ExpectedBytes, installed.Sha256,
                "Installed", true, cancellationToken, installed.RelativePath).ConfigureAwait(false);
        }

        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, terminal.Result.Definition.Id, cancellationToken).ConfigureAwait(false);
        var cleanup = await VerifyCertifiedPostconditionsAsync(
            options, report, terminal.Result.Definition.Id, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new CertifiedCreation(
            project, release, preflight, terminal.Result,
            new FullCreationEvidence
            {
                Name = label,
                SourceKind = ModpackCreationSource.CurseForgeGeneratedCandidate.ToString(),
                ProjectId = project.ProjectId,
                ClientFileId = release.ClientFileId,
                PreflightOperationId = preflight.OperationId,
                CreationOperationId = creationOperationId,
                ServerId = terminal.Result.Definition.Id,
                GeneratedRequiredFileCount = generatedPlan.RequiredFiles.Count,
                ExpectedCurseForgeBytes = checked(
                    preflight.ClientSizeBytes * 2 + generatedPlan.TotalResolvedBytes),
                LocalSha256 = terminal.Result.Sha256,
                LifecycleVerified = true,
                CleanupVerified = CleanupPassed(cleanup),
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                Status = "PASSED"
            });
    }

    private async Task<CertifiedCreation> CreateForgeFullAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CurseForgeFullCampaignOptions fullOptions,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        RecordPortAvailability(options, report, "Forge pre-creation TCP port ownership");
        var versions = await StepAsync(
            report, options, "resolve exact Forge Minecraft version",
            () => transport.SendAsync<ManagedLoaderVersionCatalog>(
                "ManagedLoaderVersions",
                new ManagedLoaderCatalogRequest(ManagedLoaderPlatform.Forge, ForceRefresh: true),
                cancellationToken)).ConfigureAwait(false);
        var version = versions.Versions.SingleOrDefault(item =>
            item.Platform == ManagedLoaderPlatform.Forge && item.IsSelectable &&
            item.MinecraftVersion.Equals(
                fullOptions.ForgeMinecraftVersion, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                "The exact requested Forge Minecraft version is not selectable in refreshed official metadata.");
        var builds = await StepAsync(
            report, options, "resolve exact Forge loader version",
            () => transport.SendAsync<ManagedLoaderBuildCatalog>(
                "ManagedLoaderBuilds",
                new ManagedLoaderBuildsRequest(
                    ManagedLoaderPlatform.Forge, version.MinecraftVersion, ForceRefresh: true),
                cancellationToken)).ConfigureAwait(false);
        var build = builds.Builds.SingleOrDefault(item =>
            item.Platform == ManagedLoaderPlatform.Forge && item.IsSelectable &&
            item.LoaderVersion.Equals(fullOptions.ForgeLoaderVersion, StringComparison.Ordinal))
            ?? throw new InvalidDataException(
                "The exact requested Forge loader version is not selectable in refreshed official metadata.");
        var operationId = Guid.NewGuid();
        activeCreationOperationId = operationId;
        activeCreationTerminal = false;
        var plan = new ManagedLoaderCreationPlan
        {
            OperationId = operationId,
            ServerName = FullServerName(options.ServerName, "Forge disposable"),
            Version = version,
            Build = build,
            Eula = CertificationEula(),
            MaxPlayers = options.MaxPlayers,
            MinimumRamMb = options.MinimumRamMb,
            MaximumRamMb = options.MaximumRamMb,
            Port = options.Port,
            NetworkingPreference = VanillaNetworkingPreference.ThisComputerOnly,
            InstanceRoot = Path.GetFullPath(options.ManagedServersRoot),
            MetadataRetrievedUtc = builds.RetrievedUtc ?? versions.RetrievedUtc,
            MetadataFromCache = builds.IsFromCache || versions.IsFromCache,
            ExperimentalRuntimeRiskAccepted = true
        };
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidDataException(string.Join(" ", problems));
        var started = await StepAsync(
            report, options, "begin exact Forge disposable server creation",
            () => transport.SendAsync<InstallOperationRequest>(
                "BeginManagedLoaderCreation",
                new BeginManagedLoaderCreationRequest(plan), cancellationToken),
            operationId).ConfigureAwait(false);
        if (started.OperationId != operationId)
            throw new InvalidDataException(
                "The Agent registered Forge creation under a different operation identity.");
        var terminal = await PollInstallAsync(
            options, report, operationId, "Forge disposable server creation", cancellationToken)
            .ConfigureAwait(false);
        activeCreationTerminal = true;
        terminalCreationOperationIds.Add(operationId);
        if (!terminal.Success || terminal.Result is null)
            throw new InvalidOperationException(
                "The exact Forge disposable server creation failed.");
        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, terminal.Result.Definition.Id, cancellationToken).ConfigureAwait(false);
        var cleanup = await VerifyCertifiedPostconditionsAsync(
            options, report, terminal.Result.Definition.Id, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new CertifiedCreation(
            null, null, null, terminal.Result,
            new FullCreationEvidence
            {
                Name = "Forge disposable",
                SourceKind = ManagedLoaderPlatform.Forge.ToString(),
                CreationOperationId = operationId,
                ServerId = terminal.Result.Definition.Id,
                LocalSha256 = terminal.Result.Sha256,
                LifecycleVerified = true,
                CleanupVerified = CleanupPassed(cleanup),
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                Status = "PASSED"
            });
    }

    private async Task<FullManagedContentEvidence> ExerciseManagedContentFullAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CurseForgeFullCampaignOptions fullOptions,
        ServerDefinition forgeServer,
        Guid wrongServerId,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var sentinel = await CreateUnrelatedSentinelAsync(
            options, forgeServer, cancellationToken).ConfigureAwait(false);
        var before = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(forgeServer.Id), cancellationToken).ConfigureAwait(false);
        RequireSentinel(before, sentinel);

        var wrongBefore = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(wrongServerId), cancellationToken).ConfigureAwait(false);
        var wrongPlan = await StepAsync(
            report, options, "plan fresh wrong-server CurseForge authority probe",
            () => transport.SendAsync<PluginInstallPlan>(
                "PlanPluginProviderRelease",
                new PluginProviderPlanRequest(
                    forgeServer.Id, fullOptions.ModProjectId, fullOptions.ModFileId,
                    PluginProviderKind.CurseForge),
                cancellationToken)).ConfigureAwait(false);
        var wrongAuthorization = wrongPlan.Authorization;
        if (!wrongPlan.CanInstall || wrongAuthorization is null ||
            wrongAuthorization.AuthorizationId == Guid.Empty)
            throw new InvalidDataException(
                "The wrong-server authority probe did not receive a fresh exact plan.");
        var wrongServerRejected = false;
        try
        {
            _ = await transport.SendAsync<ManagedContentOperationSnapshot>(
                "BeginManagedContentInstall",
                new BeginManagedContentInstallRequest(
                    wrongServerId, fullOptions.ModProjectId, fullOptions.ModFileId,
                    IncludeDependencies: true, RestartIfRunning: false,
                    OperationId: Guid.NewGuid(), Provider: PluginProviderKind.CurseForge,
                    PlanAuthorization: wrongAuthorization),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not (OutOfMemoryException or OperationCanceledException))
        {
            wrongServerRejected = true;
        }
        if (!wrongServerRejected)
            throw new InvalidOperationException(
                "A server-bound CurseForge plan authority was accepted for another campaign server.");
        var forgeAfterWrong = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(forgeServer.Id), cancellationToken).ConfigureAwait(false);
        var wrongAfter = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(wrongServerId), cancellationToken).ConfigureAwait(false);
        if (!SameInventory(before, forgeAfterWrong) || !SameInventory(wrongBefore, wrongAfter))
            throw new InvalidDataException(
                "The rejected wrong-server plan attempt mutated authoritative JAR inventory.");

        var plan = await StepAsync(
            report, options, "plan exact CurseForge mod and dependencies",
            () => transport.SendAsync<PluginInstallPlan>(
                "PlanPluginProviderRelease",
                new PluginProviderPlanRequest(
                    forgeServer.Id, fullOptions.ModProjectId, fullOptions.ModFileId,
                    PluginProviderKind.CurseForge),
                cancellationToken)).ConfigureAwait(false);
        var authorization = plan.Authorization;
        if (!plan.CanInstall || authorization is null ||
            authorization.AuthorizationId == Guid.Empty || authorization.Digest.Length != 64 ||
            authorization.Digest.Any(character => !Uri.IsHexDigit(character)) ||
            !plan.Releases.Any(release =>
                release.Provider == PluginProviderKind.CurseForge &&
                release.ProjectId.Equals(fullOptions.ModProjectId, StringComparison.Ordinal) &&
                release.VersionId.Equals(fullOptions.ModFileId, StringComparison.Ordinal)))
            throw new InvalidDataException(
                "The Agent did not return an exact authorized CurseForge dependency plan for the requested mod.");
        var exactReleases = ValidateManagedContentPlan(plan.Releases);
        var reservations = new List<(PayloadReservation Reservation, PluginRelease Release)>();
        foreach (var release in exactReleases)
        {
            var reservation = new PayloadReservation(
                Guid.NewGuid(), "full-managed-content-file", release.ProjectId,
                release.VersionId, release.SizeBytes, release.Sha1);
            await ReserveAsync(report, reservation, cancellationToken).ConfigureAwait(false);
            reservations.Add((reservation, release));
        }

        var operationId = Guid.NewGuid();
        var initial = await StepAsync(
            report, options, "begin exact authorized CurseForge mod installation",
            () => transport.SendAsync<ManagedContentOperationSnapshot>(
                "BeginManagedContentInstall",
                new BeginManagedContentInstallRequest(
                    forgeServer.Id, fullOptions.ModProjectId, fullOptions.ModFileId,
                    IncludeDependencies: true, RestartIfRunning: false,
                    OperationId: operationId, Provider: PluginProviderKind.CurseForge,
                    PlanAuthorization: authorization),
                cancellationToken),
            operationId).ConfigureAwait(false);
        if (initial.OperationId != operationId || initial.ServerId != forgeServer.Id)
            throw new InvalidDataException(
                "The managed-content operation was registered under a contradictory identity.");
        var terminal = await PollManagedContentAsync(
            options, report, operationId, cancellationToken).ConfigureAwait(false);
        if (terminal.Success != true)
            throw new InvalidOperationException(
                "The exact authorized CurseForge mod dependency plan did not install successfully.");

        var installedInventory = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(forgeServer.Id), cancellationToken).ConfigureAwait(false);
        RequireSentinel(installedInventory, sentinel);
        var installed = new List<(PayloadReservation Reservation, PluginRelease Release, ModPluginEntry Entry)>();
        foreach (var pair in reservations)
        {
            var entry = installedInventory.SingleOrDefault(item =>
                item.Provider == PluginProviderKind.CurseForge &&
                item.ProviderProjectId.Equals(pair.Release.ProjectId, StringComparison.Ordinal) &&
                item.ProviderVersionId.Equals(pair.Release.VersionId, StringComparison.Ordinal) &&
                item.SizeBytes == pair.Release.SizeBytes && item.Sha256.Length == 64)
                ?? throw new InvalidDataException(
                    "Authoritative JAR inventory did not prove one exact installed CurseForge plan release.");
            await CompleteAsync(
                report, pair.Reservation, pair.Reservation.ExpectedBytes, entry.Sha256,
                "Installed", true, cancellationToken, entry.RelativePath).ConfigureAwait(false);
            installed.Add((pair.Reservation, pair.Release, entry));
        }

        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, forgeServer.Id, cancellationToken).ConfigureAwait(false);
        _ = await VerifyCertifiedPostconditionsAsync(
            options, report, forgeServer.Id, cancellationToken).ConfigureAwait(false);
        var refreshedRelease = await StepAsync(
            report, options, "refresh installed CurseForge mod release state",
            () => transport.SendAsync<PluginRelease?>(
                "PluginRelease",
                new PluginReleaseRequest(
                    forgeServer.Id, fullOptions.ModProjectId, PluginProviderKind.CurseForge),
                cancellationToken)).ConfigureAwait(false)
            ?? throw new InvalidDataException(
                "The installed CurseForge mod no longer had an exact compatible provider release.");
        if (refreshedRelease.Provider != PluginProviderKind.CurseForge ||
            !refreshedRelease.ProjectId.Equals(fullOptions.ModProjectId, StringComparison.Ordinal) ||
            !PositiveId(refreshedRelease.VersionId))
            throw new InvalidDataException(
                "The refreshed CurseForge mod release state contradicted its exact provider project.");
        var updateState = refreshedRelease.VersionId.Equals(
            fullOptions.ModFileId, StringComparison.Ordinal) ? "UpToDate" : "UpdateAvailable";

        foreach (var item in installed.OrderByDescending(item => item.Entry.RelativePath.Length))
        {
            var removed = await StepAsync(
                report, options, "remove exact planned CurseForge JAR",
                () => transport.SendAsync<OperationResult>(
                    "RemoveJar",
                    new PluginRemoveRequest(forgeServer.Id, item.Entry.RelativePath, false),
                    cancellationToken)).ConfigureAwait(false);
            if (!removed.Success)
                throw new InvalidOperationException(
                    "The Agent refused exact stopped-server removal for a planned CurseForge JAR.");
        }
        var after = await transport.SendAsync<IReadOnlyList<ModPluginEntry>>(
            "Inventory", new ServerIdRequest(forgeServer.Id), cancellationToken).ConfigureAwait(false);
        RequireSentinel(after, sentinel);
        var removedAll = installed.All(item => !after.Any(entry =>
            entry.Provider == PluginProviderKind.CurseForge &&
            entry.ProviderProjectId.Equals(item.Release.ProjectId, StringComparison.Ordinal) &&
            entry.ProviderVersionId.Equals(item.Release.VersionId, StringComparison.Ordinal)));
        if (!removedAll)
            throw new InvalidDataException(
                "Authoritative JAR inventory still contains a release from the removed exact plan.");
        watch.Stop();
        return new FullManagedContentEvidence
        {
            ServerId = forgeServer.Id,
            RequestedProjectId = fullOptions.ModProjectId,
            RequestedFileId = fullOptions.ModFileId,
            InstallOperationId = operationId,
            ExactPlanAuthorized = true,
            WrongServerAuthorizationRejected = true,
            WrongServerInventoriesUnchanged = true,
            InstalledLifecycleVerified = true,
            RefreshedProviderFileId = refreshedRelease.VersionId,
            UpdateState = updateState,
            Releases = installed.Select(item => new FullManagedContentReleaseEvidence
            {
                ProjectId = item.Release.ProjectId,
                FileId = item.Release.VersionId,
                ExpectedBytes = item.Release.SizeBytes,
                LocalSha256 = item.Entry.Sha256,
                RelativePath = item.Entry.RelativePath,
                Removed = true
            }).ToArray(),
            SentinelSha256 = sentinel.Sha256,
            SentinelPreserved = true,
            InventoryRemovalVerified = true,
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            Status = "PASSED"
        };
    }

    private async Task<ManagedContentOperationSnapshot> PollManagedContentAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < options.CreationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await transport.SendAsync<ManagedContentOperationSnapshot>(
                "ManagedContentOperation", new ManagedContentOperationRequest(operationId),
                cancellationToken).ConfigureAwait(false);
            if (snapshot.OperationId != operationId)
                throw new InvalidDataException(
                    "ManagedContentOperation returned a different operation identity.");
            if (snapshot.IsTerminal)
            {
                watch.Stop();
                report.Steps.Add(new CertificationStepEvidence
                {
                    Name = "authorized CurseForge managed-content poll",
                    Status = snapshot.Success == true ? "PASSED" : "FAILED",
                    Detail = snapshot.Success == true
                        ? "The exact authorized dependency plan reached Installed while the server remained stopped."
                        : options.Sanitize(snapshot.Error ?? "Managed-content installation failed."),
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    OperationId = operationId,
                    State = snapshot.Progress.Stage.ToString()
                });
                return snapshot;
            }
            await delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            "The exact managed-content operation did not become terminal before its deadline.");
    }

    private async Task<CompletedUpdateRun> ExerciseSuccessfulUpdateAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CertifiedCreation older,
        ResolvedOfficial newer,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var oldRelease = older.Release ?? throw new InvalidOperationException(
            "The older official release evidence is unavailable.");
        var newProject = newer.Project;
        var newRelease = newer.Release;
        var serverId = older.Result.Definition.Id;
        var target = BuildUpdateTarget(newProject, newRelease);

        var primaryId = Guid.NewGuid();
        await ValidateUpdateRefreshAsync(
            options, report, serverId, newProject, newRelease, cancellationToken).ConfigureAwait(false);
        var primaryReservations = await ReserveUpdateAttemptAsync(
            report, primaryId, newProject, newRelease, serverPackTransfer: true,
            cancellationToken).ConfigureAwait(false);
        var primary = await BeginAndPollUpdateAsync(
            options, report,
            new UpdateInstallRequest
            {
                OperationId = primaryId,
                ServerId = serverId,
                TargetVersion = target,
                PlayerCountdownSeconds = 0,
                StartForValidation = true
            },
            "successful update primary", cancellationToken).ConfigureAwait(false);
        await CompleteUpdateAttemptAsync(
            options, report, primaryReservations, newRelease, primary,
            serverPackTransferred: true, cancellationToken).ConfigureAwait(false);

        var reviewRequired = IsMigrationReview(primary);
        UpdateOperationSnapshot terminal;
        Guid? confirmedId = null;
        if (reviewRequired)
        {
            confirmedId = Guid.NewGuid();
            await ValidateUpdateRefreshAsync(
                options, report, serverId, newProject, newRelease, cancellationToken)
                .ConfigureAwait(false);
            var confirmedReservations = await ReserveUpdateAttemptAsync(
                report, confirmedId.Value, newProject, newRelease, serverPackTransfer: false,
                cancellationToken)
                .ConfigureAwait(false);
            terminal = await BeginAndPollUpdateAsync(
                options, report,
                BuildConfirmedUpdateRequest(
                    confirmedId.Value, serverId, target, primaryId, primary.Result!.MigrationPlan),
                "successful update reviewed confirmation", cancellationToken).ConfigureAwait(false);
            await CompleteUpdateAttemptAsync(
                options, report, confirmedReservations, newRelease, terminal,
                serverPackTransferred: false, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            terminal = primary;
        }
        if (!terminal.Success || terminal.Result is not { Success: true, RolledBack: false } result ||
            result.PreviousSnapshot is null || result.ActiveVersion is null)
            throw new InvalidOperationException(
                "The exact official update did not reach a successful non-rollback terminal state.");
        if (!result.ActiveVersion.VersionId.Equals(newRelease.ClientFileId, StringComparison.Ordinal) ||
            !result.ActiveVersion.ProviderFileId.Equals(newRelease.ServerPackFileId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The successful update activated a version outside the exact newer release identity.");

        ResetLifecycleIdentity();
        await ExerciseAlreadyRunningLifecycleAsync(
            options, report, serverId, cancellationToken).ConfigureAwait(false);
        _ = await VerifyCertifiedPostconditionsAsync(
            options, report, serverId, cancellationToken).ConfigureAwait(false);
        var downloadHash = LocalUpdatePackageSha256(options, primaryId, newRelease.SizeBytes!.Value);
        watch.Stop();
        return new CompletedUpdateRun(
            new FullUpdateEvidence
            {
                ServerId = serverId,
                FromClientFileId = oldRelease.ClientFileId,
                ToClientFileId = newRelease.ClientFileId,
                PrimaryOperationId = primaryId,
                ConfirmedOperationId = confirmedId,
                CheckUpdatesIdentityVerified = true,
                MigrationReviewRequired = reviewRequired,
                Success = true,
                PreviousSnapshotId = result.PreviousSnapshot.Id,
                ActiveSnapshotId = result.ActiveVersion.Id,
                ActiveProviderFileId = result.ActiveVersion.ProviderFileId,
                DownloadLocalSha256 = downloadHash,
                DownloadedServerPackBytes = newRelease.SizeBytes.Value,
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                Status = "PASSED"
            },
            terminal,
            result.PreviousSnapshot,
            result.ActiveVersion);
    }

    private async Task<FullRollbackEvidence> ExerciseExplicitRollbackAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CertifiedCreation older,
        ResolvedOfficial newer,
        CompletedUpdateRun update,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var oldRelease = older.Release!;
        var newRelease = newer.Release;
        var serverId = older.Result.Definition.Id;
        var rollback = await StepAsync(
            report, options, "explicit verified version rollback",
            () => transport.SendAsync<OperationResult>(
                "RollbackVersion",
                new VersionSnapshotRequest(serverId, update.PreviousSnapshot.Id),
                cancellationToken)).ConfigureAwait(false);
        if (!rollback.Success)
            throw new InvalidOperationException(
                "The Agent refused the exact verified previous-version rollback.");
        var versions = await transport.SendAsync<IReadOnlyList<VersionSnapshot>>(
            "ListVersions", new ServerIdRequest(serverId), cancellationToken).ConfigureAwait(false);
        var active = versions.SingleOrDefault(version => version.IsActive)
                     ?? throw new InvalidDataException(
                         "Explicit rollback did not leave exactly one active version.");
        if (!active.VersionId.Equals(oldRelease.ClientFileId, StringComparison.Ordinal) ||
            !active.ProviderFileId.Equals(oldRelease.ServerPackFileId, StringComparison.Ordinal) ||
            active.VersionId.Equals(newRelease.ClientFileId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Explicit rollback did not restore the exact older provider identity.");
        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, serverId, cancellationToken).ConfigureAwait(false);
        var dashboard = await transport.SendAsync<DashboardSnapshot>(
            "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
        var stopped = dashboard.Servers.Single(server => server.Definition.Id == serverId).State ==
                      ServerState.Stopped;
        var portAbsent = !PortHasListener(options.Port);
        if (!stopped || !portAbsent)
            throw new InvalidOperationException(
                "Explicit rollback did not preserve the stopped, listener-free server state.");
        _ = await VerifyCertifiedPostconditionsAsync(
            options, report, serverId, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new FullRollbackEvidence
        {
            ServerId = serverId,
            TargetSnapshotId = update.PreviousSnapshot.Id,
            ActiveClientFileId = active.VersionId,
            ActiveProviderFileId = active.ProviderFileId,
            ServerStopped = true,
            PortAbsent = true,
            LifecycleVerified = true,
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            Status = "PASSED"
        };
    }

    private async Task<FullUpdateEvidence> ExerciseControlledFailureUpdateAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CertifiedCreation older,
        ResolvedOfficial newer,
        bool expectReview,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var project = newer.Project;
        var release = newer.Release;
        var oldRelease = older.Release!;
        var serverId = older.Result.Definition.Id;
        var target = BuildUpdateTarget(project, release);
        var primaryId = Guid.NewGuid();
        await ValidateUpdateRefreshAsync(
            options, report, serverId, project, release, cancellationToken).ConfigureAwait(false);
        var primaryReservations = await ReserveUpdateAttemptAsync(
            report, primaryId, project, release, serverPackTransfer: true,
            cancellationToken).ConfigureAwait(false);
        if (!expectReview)
            await ArmCertificationUpdateFailureAsync(
                options, report, serverId, primaryId, cancellationToken).ConfigureAwait(false);
        var primary = await BeginAndPollUpdateAsync(
            options, report,
            new UpdateInstallRequest
            {
                OperationId = primaryId,
                ServerId = serverId,
                TargetVersion = target,
                PlayerCountdownSeconds = 0,
                StartForValidation = true
            },
            "controlled-failure update primary", cancellationToken).ConfigureAwait(false);
        await CompleteUpdateAttemptAsync(
            options, report, primaryReservations, release, primary,
            serverPackTransferred: true, cancellationToken).ConfigureAwait(false);

        Guid actualSwitchOperationId = primaryId;
        Guid? confirmedId = null;
        UpdateOperationSnapshot terminal = primary;
        if (expectReview)
        {
            if (!IsMigrationReview(primary))
                throw new InvalidOperationException(
                    "The controlled rerun did not reproduce the exact migration-review boundary.");
            confirmedId = Guid.NewGuid();
            actualSwitchOperationId = confirmedId.Value;
            await ValidateUpdateRefreshAsync(
                options, report, serverId, project, release, cancellationToken).ConfigureAwait(false);
            var confirmedReservations = await ReserveUpdateAttemptAsync(
                report, confirmedId.Value, project, release, serverPackTransfer: false,
                cancellationToken).ConfigureAwait(false);
            await ArmCertificationUpdateFailureAsync(
                options, report, serverId, confirmedId.Value, cancellationToken).ConfigureAwait(false);
            terminal = await BeginAndPollUpdateAsync(
                options, report,
                BuildConfirmedUpdateRequest(
                    confirmedId.Value, serverId, target, primaryId, primary.Result!.MigrationPlan),
                "controlled-failure reviewed switch", cancellationToken).ConfigureAwait(false);
            await CompleteUpdateAttemptAsync(
                options, report, confirmedReservations, release, terminal,
                serverPackTransferred: false, cancellationToken).ConfigureAwait(false);
        }
        if (terminal.OperationId != actualSwitchOperationId || terminal.Success ||
            terminal.Result is not { Success: false, RolledBack: true } result ||
            result.PreviousSnapshot is null)
            throw new InvalidOperationException(
                "The armed exact update did not prove a controlled post-switch automatic rollback.");
        var versions = await transport.SendAsync<IReadOnlyList<VersionSnapshot>>(
            "ListVersions", new ServerIdRequest(serverId), cancellationToken).ConfigureAwait(false);
        var active = versions.SingleOrDefault(version => version.IsActive)
                     ?? throw new InvalidDataException(
                         "Controlled rollback did not leave exactly one active version.");
        if (!active.VersionId.Equals(oldRelease.ClientFileId, StringComparison.Ordinal) ||
            !active.ProviderFileId.Equals(oldRelease.ServerPackFileId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Controlled rollback did not restore the exact older provider identity.");
        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, serverId, cancellationToken).ConfigureAwait(false);
        var dashboard = await transport.SendAsync<DashboardSnapshot>(
            "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
        if (dashboard.Servers.Single(server => server.Definition.Id == serverId).State != ServerState.Stopped ||
            PortHasListener(options.Port))
            throw new InvalidOperationException(
                "Controlled rollback did not return to the stopped, listener-free baseline.");
        _ = await VerifyCertifiedPostconditionsAsync(
            options, report, serverId, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        return new FullUpdateEvidence
        {
            ServerId = serverId,
            FromClientFileId = oldRelease.ClientFileId,
            ToClientFileId = release.ClientFileId,
            PrimaryOperationId = primaryId,
            ConfirmedOperationId = confirmedId,
            CheckUpdatesIdentityVerified = true,
            MigrationReviewRequired = expectReview,
            Success = false,
            AutomaticRollback = true,
            PreviousSnapshotId = result.PreviousSnapshot.Id,
            ActiveSnapshotId = active.Id,
            ActiveProviderFileId = active.ProviderFileId,
            DownloadLocalSha256 = LocalUpdatePackageSha256(
                options, primaryId, release.SizeBytes!.Value),
            DownloadedServerPackBytes = release.SizeBytes.Value,
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            Status = "PASSED"
        };
    }

    private async Task<CatalogItem> ResolveFullSelectionAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CurseForgeExactReleaseSelection selection,
        string stepName,
        CancellationToken cancellationToken)
    {
        var project = await StepAsync(
            report, options, stepName,
            () => transport.SendAsync<CatalogItem?>(
                "ResolveCatalogProject",
                new CatalogProjectRequest(
                    CatalogProvider.CurseForge, selection.ProjectReference,
                    selection.ClientFileId),
                cancellationToken)).ConfigureAwait(false);
        return project is { Provider: CatalogProvider.CurseForge }
            ? project
            : throw new InvalidOperationException(
                "The exact CurseForge project/file selection could not be resolved.");
    }

    private static void RequireOfficialRelease(CatalogVersion release, string label)
    {
        if (!release.HasServerPackage || !PositiveId(release.ServerPackFileId) ||
            release.SizeBytes is not > 0 || release.Sha1.Length != 40 ||
            release.ClientSizeBytes is not > 0 || release.ClientSha1.Length != 40)
            throw new InvalidOperationException(
                $"The {label} release has no integrity-verifiable official server pack.");
    }

    private static KnownPayloadProjectionEvidence KnownPayloadProjection(
        CatalogVersion older,
        CatalogVersion newer,
        CatalogVersion generated,
        CatalogVersion? generatedFallback)
    {
        RequireOfficialRelease(older, "projection older");
        RequireOfficialRelease(newer, "projection newer");
        if (generated.ClientSizeBytes is not > 0)
            throw new InvalidDataException(
                "The generated candidate has no positive client payload size for campaign projection.");

        if (generatedFallback is not null && generatedFallback.ClientSizeBytes is not > 0)
            throw new InvalidDataException(
                "The generated fallback has no positive client payload size for campaign projection.");
        return CalculateKnownPayloadProjection(
            older.ClientSizeBytes!.Value,
            older.SizeBytes!.Value,
            newer.ClientSizeBytes!.Value,
            newer.SizeBytes!.Value,
            generated.ClientSizeBytes.Value,
            generatedFallback?.ClientSizeBytes);
    }

    internal static KnownPayloadProjectionEvidence CalculateKnownPayloadProjection(
        long olderClientBytes,
        long olderServerBytes,
        long newerClientBytes,
        long newerServerBytes,
        long generatedClientBytes,
        long? generatedFallbackClientBytes)
    {
        if (olderClientBytes <= 0 || olderServerBytes <= 0 || newerClientBytes <= 0 ||
            newerServerBytes <= 0 || generatedClientBytes <= 0 ||
            generatedFallbackClientBytes is <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(olderClientBytes), "Every known payload projection size must be positive.");
        var officialOlder = checked(olderClientBytes + olderServerBytes);
        var generatedPrimarySuccess = checked(generatedClientBytes * 2);
        var generatedWorstCase = generatedFallbackClientBytes is { } fallback
            ? Math.Max(generatedPrimarySuccess, checked(generatedClientBytes + fallback * 2))
            : generatedPrimarySuccess;
        // Each update run may transfer the client manifest once for primary preflight and once for
        // a single reviewed confirmation. Only the primary operation can transfer the server pack.
        var updateWorstCase = checked(newerClientBytes * 4 + newerServerBytes * 2);
        return new KnownPayloadProjectionEvidence(
            officialOlder, generatedWorstCase, updateWorstCase);
    }

    private static void ValidateKnownPayloadProjection(
        CurseForgePayloadBudgetSnapshot current,
        KnownPayloadProjectionEvidence projection)
    {
        RequireKnownPayloadProjectionFits(
            current.LimitBytes, current.GuardedCumulativeBytes, projection.TotalBytes);
    }

    internal static void RequireKnownPayloadProjectionFits(
        long limitBytes,
        long guardedBytes,
        long projectedBytes)
    {
        if (guardedBytes < 0 || limitBytes <= 0 || guardedBytes > limitBytes ||
            projectedBytes <= 0 || projectedBytes > limitBytes - guardedBytes)
            throw new InvalidOperationException(
                "The known worst-case CurseForge campaign paths do not fit inside the remaining hard payload budget.");
    }

    internal static long CalculateGeneratedCreationAdditionalBytes(
        long clientArchiveBytes,
        IReadOnlyList<long> requiredFileBytes)
    {
        ArgumentNullException.ThrowIfNull(requiredFileBytes);
        if (clientArchiveBytes <= 0 || requiredFileBytes.Any(bytes => bytes <= 0))
            throw new ArgumentOutOfRangeException(
                nameof(clientArchiveBytes),
                "The generated creation archive and every required file must have a positive declared size.");
        return requiredFileBytes.Aggregate(
            clientArchiveBytes,
            static (total, bytes) => checked(total + bytes));
    }

    private async Task ReserveAsync(
        CurseForgeRuntimeCertificationReport report,
        PayloadReservation reservation,
        CancellationToken cancellationToken)
    {
        var snapshot = await payloadBudget.ReserveAsync(
            reservation.OperationId, reservation.Kind, reservation.ProjectId,
            reservation.FileId, reservation.ExpectedBytes, reservation.ProviderSha1,
            cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, snapshot);
    }

    private async Task ReserveBatchAsync(
        CurseForgeRuntimeCertificationReport report,
        IReadOnlyList<PayloadReservation> reservations,
        CancellationToken cancellationToken)
    {
        var snapshot = await payloadBudget.ReserveBatchAsync(
            reservations.Select(reservation => new CurseForgePayloadReservationRequest(
                reservation.OperationId, reservation.Kind, reservation.ProjectId,
                reservation.FileId, reservation.ExpectedBytes, reservation.ProviderSha1)).ToArray(),
            cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, snapshot);
    }

    private async Task CompleteAsync(
        CurseForgeRuntimeCertificationReport report,
        PayloadReservation reservation,
        long downloadedBytes,
        string localSha256,
        string state,
        bool transferCompleted,
        CancellationToken cancellationToken,
        string relativePath = "")
    {
        var snapshot = await payloadBudget.CompleteAsync(
            reservation.OperationId, reservation.Kind, downloadedBytes, localSha256,
            state, transferCompleted, cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, snapshot);
        report.Payloads.Add(new CertificationPayloadEvidence
        {
            Kind = reservation.Kind,
            OperationId = reservation.OperationId,
            ProjectId = reservation.ProjectId,
            FileId = reservation.FileId,
            ExpectedBytes = reservation.ExpectedBytes,
            DownloadedBytes = downloadedBytes,
            ProviderSha1Verified = downloadedBytes == reservation.ExpectedBytes,
            LocalSha256 = localSha256,
            RelativePath = relativePath,
            State = state
        });
    }

    private async Task<InstallOperationSnapshot> PollInstallAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid operationId,
        string label,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        CertificationOperationStateEvidence? previous = null;
        while (watch.Elapsed < options.CreationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await transport.SendAsync<InstallOperationSnapshot>(
                "InstallProgress", new InstallOperationRequest(operationId), cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.OperationId != operationId)
                throw new InvalidDataException(
                    "InstallProgress returned a different operation identity.");
            var evidence = new CertificationOperationStateEvidence(
                snapshot.OperationId, snapshot.Revision,
                snapshot.Progress.State.ToString(), snapshot.Progress.Stage.ToString(),
                options.Sanitize(snapshot.Progress.CurrentStep),
                snapshot.Progress.BytesDownloaded, snapshot.Progress.TotalBytes,
                snapshot.IsTerminal, snapshot.Success);
            if (previous != evidence && report.OperationStates.Count < 512)
            {
                report.OperationStates.Add(evidence);
                previous = evidence;
            }
            if (snapshot.IsTerminal)
            {
                watch.Stop();
                report.Steps.Add(new CertificationStepEvidence
                {
                    Name = label + " poll",
                    Status = snapshot.Success ? "PASSED" : "FAILED",
                    Detail = snapshot.Success
                        ? "The exact creation operation reached a successful terminal state."
                        : options.Sanitize(snapshot.Error),
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    OperationId = operationId,
                    State = snapshot.Progress.Stage.ToString()
                });
                return snapshot;
            }
            await delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"The {label} operation did not become terminal before its deadline.");
    }

    private void ResetLifecycleIdentity()
    {
        taskServerProcessId = 0;
        taskServerProcessCreationTicks = ProcessCreationIdentity.Unknown;
        listenerPidOwnershipVerified = false;
    }

    private static bool CleanupPassed(CertificationCleanupPostconditionsEvidence cleanup) =>
        cleanup.TaskServerInactive && cleanup.TaskServerProcessIdentityCaptured &&
        cleanup.TaskServerRootProcessExited && cleanup.PortListenerAbsent &&
        cleanup.NoPartialArtifacts && cleanup.NoUnsafeStagingResidue &&
        cleanup.ListenerPidOwnershipVerified;

    private static string FullServerName(string baseName, string suffix)
    {
        var normalized = string.Join(' ', baseName.Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        var boundedSuffix = " - " + suffix;
        var maximumBase = Math.Max(1, 80 - boundedSuffix.Length);
        return normalized[..Math.Min(normalized.Length, maximumBase)] + boundedSuffix;
    }

    private static VanillaEulaAcceptance CertificationEula() => new()
    {
        Accepted = true,
        AcceptedAtUtc = DateTime.UtcNow,
        SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
    };

    internal static CurseForgeGeneratedPackPlan ValidateGeneratedPreflight(
        Guid expectedOperationId,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult result)
    {
        if (result.OperationId != expectedOperationId ||
            !result.ProjectId.Equals(project.ProjectId, StringComparison.Ordinal) ||
            !result.ClientFileId.Equals(release.ClientFileId, StringComparison.Ordinal) ||
            result.ServerPackFileId.Length > 0 ||
            result.ServerPackDownloadUrl.Length > 0 || result.ServerPackSha1.Length > 0 ||
            result.ServerPackSizeBytes is not null ||
            result.State != CatalogReleasePreflightState.Ready ||
            !result.ClientDownloadUrl.Equals(release.ClientDownloadUrl, StringComparison.Ordinal) ||
            !result.ClientSha1.Equals(release.ClientSha1, StringComparison.OrdinalIgnoreCase) ||
            result.ClientSizeBytes != release.ClientSizeBytes ||
            result.ClientSha256.Length != 64 ||
            result.ClientSha256.Any(character => !Uri.IsHexDigit(character)) ||
            !Uri.TryCreate(result.ClientDownloadUrl, UriKind.Absolute, out var clientUri) ||
            !CurseForgeApiClient.IsApprovedDownloadUri(clientUri) ||
            string.IsNullOrWhiteSpace(result.MinecraftVersion) ||
            string.IsNullOrWhiteSpace(result.Loader) ||
            string.IsNullOrWhiteSpace(result.LoaderVersion) || result.RequiredJavaMajor <= 0 ||
            result.GeneratedPackPlan is null)
            throw new InvalidDataException(
                "Generated preflight returned contradictory identity, integrity, or platform evidence.");
        var plan = CurseForgeGeneratedPackPlanService.ValidateAndClone(result.GeneratedPackPlan);
        if (!plan.MinecraftVersion.Equals(result.MinecraftVersion, StringComparison.Ordinal) ||
            !plan.Loader.Equals(result.Loader, StringComparison.Ordinal) ||
            !plan.LoaderVersion.Equals(result.LoaderVersion, StringComparison.Ordinal) ||
            plan.TotalResolvedBytes != plan.RequiredFiles.Sum(file => file.SizeBytes))
            throw new InvalidDataException(
                "The generated file plan contradicts its exact preflight platform or byte identity.");
        return plan;
    }

    internal static void ValidateGeneratedClientIdentity(
        Guid expectedOperationId,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult result)
    {
        if (result.OperationId != expectedOperationId ||
            !result.ProjectId.Equals(project.ProjectId, StringComparison.Ordinal) ||
            !result.ClientFileId.Equals(release.ClientFileId, StringComparison.Ordinal) ||
            result.ServerPackFileId.Length > 0 || result.ServerPackDownloadUrl.Length > 0 ||
            result.ServerPackSha1.Length > 0 || result.ServerPackSizeBytes is not null ||
            result.State is not (CatalogReleasePreflightState.Ready or
                CatalogReleasePreflightState.Unsupported) ||
            !result.ClientDownloadUrl.Equals(release.ClientDownloadUrl, StringComparison.Ordinal) ||
            !result.ClientSha1.Equals(release.ClientSha1, StringComparison.OrdinalIgnoreCase) ||
            result.ClientSizeBytes != release.ClientSizeBytes ||
            result.ClientSha256.Length != 64 ||
            result.ClientSha256.Any(character => !Uri.IsHexDigit(character)) ||
            !Uri.TryCreate(result.ClientDownloadUrl, UriKind.Absolute, out var clientUri) ||
            !CurseForgeApiClient.IsApprovedDownloadUri(clientUri))
            throw new InvalidDataException(
                "Generated preflight returned contradictory client identity or integrity evidence.");
    }

    private static bool SameGeneratedProviderIdentity(
        CatalogItem firstProject,
        CatalogVersion first,
        CatalogItem secondProject,
        CatalogVersion second) =>
        firstProject.ProjectId.Equals(secondProject.ProjectId, StringComparison.Ordinal) &&
        first.ClientFileId.Equals(second.ClientFileId, StringComparison.Ordinal) &&
        first.ClientDownloadUrl.Equals(second.ClientDownloadUrl, StringComparison.Ordinal) &&
        first.ClientSha1.Equals(second.ClientSha1, StringComparison.OrdinalIgnoreCase) &&
        first.ClientSizeBytes == second.ClientSizeBytes &&
        !first.HasServerPackage && !second.HasServerPackage &&
        first.ServerPackFileId.Length == 0 && second.ServerPackFileId.Length == 0 &&
        second.CanGenerateServerCandidate;

    internal static ModpackCreationPlan BuildGeneratedPlan(
        CurseForgeRuntimeCertificationOptions options,
        Guid operationId,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult preflight) => new()
        {
            OperationId = operationId,
            SourceKind = ModpackCreationSource.CurseForgeGeneratedCandidate,
            Source = preflight.ClientDownloadUrl,
            Provider = UpdateProvider.CurseForge,
            ProjectId = project.ProjectId,
            ProjectSlug = project.Slug,
            ProjectName = project.Name,
            VersionId = release.ClientFileId,
            VersionName = release.VersionName,
            ReleaseChannel = release.ReleaseChannel,
            MinecraftVersion = preflight.MinecraftVersion,
            Loader = preflight.Loader,
            LoaderVersion = preflight.LoaderVersion,
            RequiredJavaMajor = preflight.RequiredJavaMajor,
            ExpectedSha1 = preflight.ClientSha1,
            ExpectedSha256 = preflight.ClientSha256,
            ExpectedSizeBytes = preflight.ClientSizeBytes,
            VerifiedClientArchiveSha256 = preflight.ClientSha256,
            PreflightOperationId = preflight.OperationId,
            ServerName = options.ServerName,
            Eula = CertificationEula(),
            MinimumRamMb = options.MinimumRamMb,
            MaximumRamMb = options.MaximumRamMb,
            Port = options.Port,
            MaxPlayers = options.MaxPlayers,
            InstanceRoot = Path.GetFullPath(options.ManagedServersRoot),
            NetworkingPreference = VanillaNetworkingPreference.ThisComputerOnly,
            ExperimentalRuntimeRiskAccepted = true
        };

    private async Task<SentinelEvidence> CreateUnrelatedSentinelAsync(
        CurseForgeRuntimeCertificationOptions options,
        ServerDefinition server,
        CancellationToken cancellationToken)
    {
        var serverRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
            options.ManagedServersRoot, server.RootPath, "Forge disposable server root");
        var mods = OwnedCertificationRuntime.RequireOwnedDescendant(
            serverRoot, Path.Combine(serverRoot, "mods"), "Forge mod directory");
        Directory.CreateDirectory(mods);
        if ((File.GetAttributes(mods) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "The Forge mod directory cannot be a reparse point.");
        var relative = Path.Combine("mods", "chunkpilot-certification-unrelated-sentinel.jar");
        var path = OwnedCertificationRuntime.RequireOwnedDescendant(
            serverRoot, Path.Combine(serverRoot, relative), "unrelated sentinel JAR");
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException("The exact unrelated sentinel path is already occupied.");
        await using (var stream = new FileStream(
                         path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                         16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            var manifest = archive.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.NoCompression);
            await using var output = manifest.Open();
            var bytes = Encoding.UTF8.GetBytes(
                "Manifest-Version: 1.0\r\nImplementation-Title: ChunkPilot certification unrelated sentinel\r\n\r\n");
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 1 or > 1024 * 1024)
            throw new InvalidDataException("The unrelated sentinel JAR is not a bounded regular file.");
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(
            await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return new SentinelEvidence(relative, sha256);
    }

    private async Task<UpdateSentinelManifest> CreateUpdateSentinelManifestAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        ServerDefinition originalServer,
        CancellationToken cancellationToken)
    {
        var dashboard = await transport.SendAsync<DashboardSnapshot>(
            "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
        var snapshot = dashboard.Servers.Single(server => server.Definition.Id == originalServer.Id);
        if (snapshot.State != ServerState.Stopped)
            throw new InvalidOperationException(
                "Update sentinels can only be created while the exact task server is stopped.");
        var serverRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
            options.ManagedServersRoot, snapshot.Definition.RootPath,
            "official update sentinel server root");

        var properties = await transport.SendAsync<ServerPropertiesResponse>(
            "GetServerProperties", new ServerIdRequest(originalServer.Id), cancellationToken)
            .ConfigureAwait(false);
        var levelName = properties.Values.TryGetValue("level-name", out var configuredLevelName)
            ? configuredLevelName
            : "world";
        if (string.IsNullOrWhiteSpace(levelName) || Path.IsPathRooted(levelName) ||
            levelName.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
            throw new InvalidDataException(
                "The task server's world path is not a safe relative update-sentinel location.");

        var propertiesUpdated = await transport.SendAsync<OperationResult>(
            "UpdateServerProperties",
            new ServerPropertiesRequest(originalServer.Id, new Dictionary<string, string>
            {
                ["motd"] = "ChunkPilot certification update sentinel",
                ["spawn-protection"] = "17"
            })
            {
                Session = session,
                ConnectivityOperation = PublicConnectivityOperation.UpdateServerProperties
            }, cancellationToken).ConfigureAwait(false);
        if (!propertiesUpdated.Success)
            throw new InvalidOperationException(
                "The Agent refused the synthetic server.properties sentinel update.");

        var fileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine(levelName, "chunkpilot-certification-world.txt")] =
                "ChunkPilot synthetic world sentinel v1\n",
            ["whitelist.json"] =
                "[{\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"name\":\"CPWhitelistSentinel\"}]\n",
            ["ops.json"] =
                "[{\"uuid\":\"00000000-0000-0000-0000-000000000002\",\"name\":\"CPOpSentinel\",\"level\":1,\"bypassesPlayerLimit\":false}]\n",
            ["banned-players.json"] =
                "[{\"uuid\":\"00000000-0000-0000-0000-000000000003\",\"name\":\"CPBanSentinel\",\"created\":\"2026-08-29 00:00:00 +0000\",\"source\":\"ChunkPilot certification\",\"expires\":\"forever\",\"reason\":\"Synthetic fixture\"}]\n",
            ["banned-ips.json"] =
                "[{\"ip\":\"192.0.2.1\",\"created\":\"2026-08-29 00:00:00 +0000\",\"source\":\"ChunkPilot certification\",\"expires\":\"forever\",\"reason\":\"Synthetic fixture\"}]\n",
            [Path.Combine("config", "chunkpilot-certification-user.cfg")] =
                "# Synthetic user-owned configuration sentinel\nvalue=preserve-me\n"
        };
        foreach (var pair in fileContents)
        {
            var written = await transport.SendAsync<OperationResult>(
                "WriteFile",
                new WriteFileRequest(originalServer.Id, new TextFileContent
                {
                    RelativePath = pair.Key,
                    Content = pair.Value,
                    EncodingName = "utf-8",
                    HasBom = false,
                    LineEnding = "\n"
                }), cancellationToken).ConfigureAwait(false);
            if (!written.Success)
                throw new InvalidOperationException(
                    "The Agent refused one synthetic user-owned update sentinel file.");
        }

        var maximumRamMb = snapshot.Definition.MaximumRamMb;
        if (maximumRamMb < 640)
            throw new InvalidOperationException(
                "The exact task server has no safe distinct JVM memory sentinel value.");
        var minimumRamMb = snapshot.Definition.MinimumRamMb + 128 <= maximumRamMb
            ? snapshot.Definition.MinimumRamMb + 128
            : maximumRamMb - 128;
        if (minimumRamMb == snapshot.Definition.MinimumRamMb)
            minimumRamMb = Math.Max(512, minimumRamMb - 128);
        var ramUpdated = await transport.SendAsync<OperationResult>(
            "UpdateRam", new RamUpdateRequest(originalServer.Id, minimumRamMb, maximumRamMb),
            cancellationToken).ConfigureAwait(false);
        if (!ramUpdated.Success)
            throw new InvalidOperationException(
                "The Agent refused the synthetic JVM memory sentinel update.");

        ResetLifecycleIdentity();
        await ExerciseLifecycleAsync(
            options, report, originalServer.Id, cancellationToken).ConfigureAwait(false);
        _ = await VerifyCertifiedPostconditionsAsync(
            options, report, originalServer.Id, cancellationToken).ConfigureAwait(false);

        var relativePaths = fileContents.Keys
            .Append("server.properties")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = new List<UpdateSentinelFile>(relativePaths.Length);
        foreach (var relativePath in relativePaths)
            files.Add(await HashUpdateSentinelFileAsync(
                serverRoot, relativePath, cancellationToken).ConfigureAwait(false));
        var manifestHash = ComputeSentinelManifestHash(
            originalServer.Id, minimumRamMb, maximumRamMb, files);
        var manifest = new UpdateSentinelManifest(
            originalServer.Id, serverRoot, minimumRamMb, maximumRamMb, files, manifestHash);
        await VerifyUpdateSentinelsAsync(options, manifest, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private async Task VerifyUpdateSentinelsAsync(
        CurseForgeRuntimeCertificationOptions options,
        UpdateSentinelManifest manifest,
        CancellationToken cancellationToken)
    {
        var serverRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
            options.ManagedServersRoot, manifest.ServerRoot, "official update sentinel server root");
        foreach (var expected in manifest.Files)
        {
            var actual = await HashUpdateSentinelFileAsync(
                serverRoot, expected.RelativePath, cancellationToken).ConfigureAwait(false);
            if (actual.SizeBytes != expected.SizeBytes ||
                !actual.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "A synthetic user-owned file changed across the update or rollback boundary.");
        }
        if (!ComputeSentinelManifestHash(
                manifest.ServerId, manifest.MinimumRamMb, manifest.MaximumRamMb, manifest.Files)
            .Equals(manifest.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The in-memory update sentinel manifest changed.");

        var dashboard = await transport.SendAsync<DashboardSnapshot>(
            "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
        var server = dashboard.Servers.Single(item => item.Definition.Id == manifest.ServerId);
        if (server.Definition.MinimumRamMb != manifest.MinimumRamMb ||
            server.Definition.MaximumRamMb != manifest.MaximumRamMb)
            throw new InvalidDataException(
                "The synthetic user-selected JVM memory setting changed across update or rollback.");
    }

    private static async Task<UpdateSentinelFile> HashUpdateSentinelFileAsync(
        string serverRoot,
        string relativePath,
        CancellationToken cancellationToken)
    {
        var path = OwnedCertificationRuntime.RequireOwnedDescendant(
            serverRoot, Path.Combine(serverRoot, relativePath), "update sentinel file");
        if (!File.Exists(path) || Directory.Exists(path))
            throw new FileNotFoundException(
                "A synthetic user-owned update sentinel file is unavailable.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length is < 1 or > 2 * 1024 * 1024)
            throw new InvalidDataException(
                "A synthetic user-owned update sentinel is not a bounded regular file.");
        await using var input = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        return new UpdateSentinelFile(relativePath, info.Length, sha256);
    }

    private static string ComputeSentinelManifestHash(
        Guid serverId,
        int minimumRamMb,
        int maximumRamMb,
        IReadOnlyList<UpdateSentinelFile> files)
    {
        var canonical = new StringBuilder()
            .Append(serverId.ToString("N")).Append('\n')
            .Append(minimumRamMb).Append(':').Append(maximumRamMb).Append('\n');
        foreach (var file in files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            canonical.Append(file.RelativePath.ToLowerInvariant()).Append('\0')
                .Append(file.SizeBytes).Append('\0').Append(file.Sha256.ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void RequireSentinel(
        IReadOnlyList<ModPluginEntry> inventory,
        SentinelEvidence sentinel)
    {
        var exact = inventory.SingleOrDefault(item =>
            item.RelativePath.Equals(sentinel.RelativePath, StringComparison.OrdinalIgnoreCase));
        if (exact is null || !exact.Sha256.Equals(sentinel.Sha256, StringComparison.OrdinalIgnoreCase) ||
            exact.Provider is not null)
            throw new InvalidDataException(
                "The unrelated user-owned sentinel JAR was missing, changed, or provider-attributed.");
    }

    internal static bool SameInventory(
        IReadOnlyList<ModPluginEntry> first,
        IReadOnlyList<ModPluginEntry> second)
    {
        static string Identity(ModPluginEntry item) => string.Join('\u001f',
            item.RelativePath,
            item.FileName,
            item.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            item.Sha256,
            item.Provider?.ToString() ?? "",
            item.ProviderProjectId,
            item.ProviderVersionId,
            item.Enabled.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return first.Select(Identity).Order(StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                second.Select(Identity).Order(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
    }

    internal static IReadOnlyList<PluginRelease> ValidateManagedContentPlan(
        IReadOnlyList<PluginRelease> releases)
    {
        if (releases.Count is < 1 or > 256)
            throw new InvalidDataException(
                "The exact CurseForge dependency plan has an invalid bounded release count.");
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in releases)
        {
            if (release.Provider != PluginProviderKind.CurseForge ||
                !PositiveId(release.ProjectId) || !PositiveId(release.VersionId) ||
                release.SizeBytes is <= 0 or > 512L * 1024 * 1024 ||
                release.Sha1.Length != 40 || release.Sha1.Any(character => !Uri.IsHexDigit(character)) ||
                !Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri) ||
                !CurseForgeApiClient.IsApprovedDownloadUri(uri) ||
                !identities.Add(release.ProjectId + ":" + release.VersionId))
                throw new InvalidDataException(
                    "The exact CurseForge dependency plan contains a duplicate or unsafe release identity.");
        }
        return releases.ToArray();
    }

    internal static PackVersionInfo BuildUpdateTarget(CatalogItem project, CatalogVersion release)
    {
        RequireOfficialRelease(release, "update target");
        if (!PositiveId(project.ProjectId))
            throw new InvalidDataException(
                "The update target project does not have an exact positive provider identity.");
        return new PackVersionInfo
        {
            PackId = project.ProjectId,
            VersionId = release.ClientFileId,
            ProviderFileId = release.ServerPackFileId,
            VersionName = release.VersionName,
            ReleaseChannel = release.ReleaseChannel,
            PublishedAt = release.PublishedAt ?? DateTimeOffset.MinValue,
            MinecraftVersion = release.MinecraftVersion,
            Loader = release.Loader,
            LoaderVersion = release.LoaderVersion,
            RequiredJavaMajor = release.RequiredJavaMajor,
            DownloadUrl = release.DownloadUrl,
            FileSize = release.SizeBytes,
            Sha1 = release.Sha1,
            FileName = Path.GetFileName(
                Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var uri)
                    ? uri.AbsolutePath
                    : "server-pack.zip"),
            PackageType = "zip"
        };
    }

    private async Task ValidateUpdateRefreshAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid serverId,
        CatalogItem project,
        CatalogVersion release,
        CancellationToken cancellationToken)
    {
        var check = await StepAsync(
            report, options, "refresh exact update availability",
            () => transport.SendAsync<UpdateCheckResult>(
                "CheckUpdates", new CheckUpdatesRequest(serverId), cancellationToken))
            .ConfigureAwait(false);
        ValidateUpdateRefreshIdentity(check, serverId, project, release);
    }

    internal static void ValidateUpdateRefreshIdentity(
        UpdateCheckResult check,
        Guid serverId,
        CatalogItem project,
        CatalogVersion release)
    {
        var latest = check.LatestVersion;
        if (check.ServerId != serverId ||
            check.Source is not { Provider: UpdateProvider.CurseForge } source ||
            !source.ProjectId.Equals(project.ProjectId, StringComparison.Ordinal) ||
            latest is null ||
            !latest.PackId.Equals(project.ProjectId, StringComparison.Ordinal) ||
            !latest.VersionId.Equals(release.ClientFileId, StringComparison.Ordinal) ||
            !latest.ProviderFileId.Equals(release.ServerPackFileId, StringComparison.Ordinal) ||
            latest.FileSize != release.SizeBytes ||
            !latest.Sha1.Equals(release.Sha1, StringComparison.OrdinalIgnoreCase) ||
            !latest.DownloadUrl.Equals(release.DownloadUrl, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The refreshed update source/latest identity disagreed with the independently resolved exact newer release.");
    }

    private async Task<UpdateAttemptReservations> ReserveUpdateAttemptAsync(
        CurseForgeRuntimeCertificationReport report,
        Guid operationId,
        CatalogItem project,
        CatalogVersion release,
        bool serverPackTransfer,
        CancellationToken cancellationToken)
    {
        var client = new PayloadReservation(
            operationId, "full-update-client-preflight", project.ProjectId,
            release.ClientFileId, release.ClientSizeBytes!.Value, release.ClientSha1);
        await ReserveAsync(report, client, cancellationToken).ConfigureAwait(false);
        PayloadReservation? server = null;
        if (serverPackTransfer)
        {
            server = new PayloadReservation(
                operationId, "full-update-server-pack", project.ProjectId,
                release.ServerPackFileId, release.SizeBytes!.Value, release.Sha1);
            await ReserveAsync(report, server, cancellationToken).ConfigureAwait(false);
        }
        return new UpdateAttemptReservations(client, server);
    }

    private async Task<UpdateOperationSnapshot> BeginAndPollUpdateAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        UpdateInstallRequest request,
        string label,
        CancellationToken cancellationToken)
    {
        var started = await StepAsync(
            report, options, "begin " + label,
            () => transport.SendAsync<UpdateOperationRequest>(
                "BeginPackUpdate", request, cancellationToken),
            request.OperationId).ConfigureAwait(false);
        if (started.OperationId != request.OperationId)
            throw new InvalidDataException(
                "BeginPackUpdate registered a different operation identity.");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < options.CreationTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await transport.SendAsync<UpdateOperationSnapshot>(
                "GetPackUpdate", new UpdateOperationRequest(request.OperationId),
                cancellationToken).ConfigureAwait(false);
            if (snapshot.OperationId != request.OperationId)
                throw new InvalidDataException(
                    "GetPackUpdate returned a different operation identity.");
            if (snapshot.IsTerminal)
            {
                terminalUpdateOperationIds.Add(request.OperationId);
                watch.Stop();
                report.Steps.Add(new CertificationStepEvidence
                {
                    Name = label + " poll",
                    Status = snapshot.Success || IsMigrationReview(snapshot) ||
                             snapshot.Result?.RolledBack == true ? "PASSED" : "FAILED",
                    Detail = snapshot.Success
                        ? "The exact update reached a successful terminal state."
                        : options.Sanitize(snapshot.Error),
                    ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                    OperationId = request.OperationId,
                    State = snapshot.Progress.State.ToString()
                });
                return snapshot;
            }
            await delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"The {label} operation did not become terminal before its deadline.");
    }

    private async Task CompleteUpdateAttemptAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        UpdateAttemptReservations reservations,
        CatalogVersion release,
        UpdateOperationSnapshot terminal,
        bool serverPackTransferred,
        CancellationToken cancellationToken)
    {
        var reachedVerifiedArtifact = terminal.Success || IsMigrationReview(terminal) ||
                                      terminal.Result?.RolledBack == true;
        await CompleteAsync(
            report, reservations.Client,
            downloadedBytes: 0,
            localSha256: "",
            state: reachedVerifiedArtifact
                ? "AgentVerifiedArtifactBytesNotExposed"
                : "Failed",
            transferCompleted: false, cancellationToken).ConfigureAwait(false);
        if (serverPackTransferred && reservations.Server is { } server && reachedVerifiedArtifact)
        {
            var sha256 = LocalUpdatePackageSha256(
                options, server.OperationId, release.SizeBytes!.Value);
            await CompleteAsync(
                report, server, server.ExpectedBytes, sha256,
                terminal.Result?.RolledBack == true ? "VerifiedBeforeRollback" : "Verified",
                transferCompleted: true, cancellationToken).ConfigureAwait(false);
        }
        else if (reservations.Server is { } failedServer)
        {
            await CompleteAsync(
                report, failedServer, 0, "", "Failed",
                transferCompleted: false, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsMigrationReview(UpdateOperationSnapshot snapshot) =>
        snapshot is
        {
            IsTerminal: true,
            Success: false,
            Progress.State: UpdateOperationState.PlanningMigration,
            Result.MigrationPlan.RequiresManualReview: true
        };

    internal static UpdateInstallRequest BuildConfirmedUpdateRequest(
        Guid operationId,
        Guid serverId,
        PackVersionInfo target,
        Guid reviewedOperationId,
        MigrationPlan plan) => new()
        {
            OperationId = operationId,
            ServerId = serverId,
            TargetVersion = target,
            ReviewedOperationId = reviewedOperationId,
            PlayerCountdownSeconds = 0,
            StartForValidation = true,
            ConfirmedMigrationWarnings = true,
            MigrationResolutions = plan.Conflicts.Distinct(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    path => path,
                    _ => new MigrationResolution { Kind = MigrationResolutionKind.NewBaseline },
                    StringComparer.OrdinalIgnoreCase)
        };

    private static string LocalUpdatePackageSha256(
        CurseForgeRuntimeCertificationOptions options,
        Guid operationId,
        long expectedBytes)
    {
        var path = OwnedCertificationRuntime.RequireOwnedDescendant(
            options.DataRoot,
            Path.Combine(options.DataRoot, "Cache", "Updates", $"local-{operationId:N}.package"),
            "verified update package cache");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "The exact verified update package cache is unavailable.");
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length != expectedBytes)
            throw new InvalidDataException(
                "The exact update package cache has a contradictory type or size.");
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private async Task ExerciseAlreadyRunningLifecycleAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var running = await WaitForServerStateAsync(
            options, serverId, ServerState.Running, cancellationToken).ConfigureAwait(false);
        if (!running.LastStartReachedReadiness)
            throw new InvalidOperationException(
                "The updated server did not reach its configured readiness signal.");
        CaptureTaskServerProcessIdentity(running);
        await VerifyLoopbackBindingAsync(options, report, running.Definition, cancellationToken)
            .ConfigureAwait(false);
        var connection = await transport.SendAsync<ConnectionTestResult>(
            "ConnectionTest", new ConnectionTestRequest(serverId, IncludeExternalProbe: false),
            cancellationToken).ConfigureAwait(false);
        var queryPassed = connection.ProcessRunning == FindingSeverity.Pass &&
                          connection.PortListening == FindingSeverity.Pass &&
                          connection.MinecraftResponds == FindingSeverity.Pass &&
                          connection.ExternalResult.Equals("Not tested", StringComparison.Ordinal);
        report.ConnectionTest = new CertificationConnectionEvidence
        {
            ProcessRunning = connection.ProcessRunning,
            PortListening = connection.PortListening,
            MinecraftResponds = connection.MinecraftResponds,
            LocalAddress = connection.LocalAddress,
            ExternalResult = connection.ExternalResult
        };
        if (!queryPassed)
            throw new InvalidOperationException(
                "The updated server did not pass its local-only Minecraft status query.");
        var stoppedResult = await transport.SendAsync<OperationResult>(
            "Stop",
            new StopRequest(serverId, SaveFirst: true)
            {
                Session = session,
                ConnectivityOperation = PublicConnectivityOperation.StopServer
            }, cancellationToken).ConfigureAwait(false);
        var stopped = await WaitForServerStateAsync(
            options, serverId, ServerState.Stopped, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        report.Lifecycle.Add(new CertificationLifecycleEvidence(
            "Updated release readiness/query/stop", stopped.State.ToString(),
            stoppedResult.Success && queryPassed,
            "The updated release was loopback-bound, answered locally, and stopped cleanly.",
            watch.Elapsed.TotalMilliseconds));
        if (!stoppedResult.Success)
            throw new InvalidOperationException("The updated server did not stop cleanly.");
    }

    private static bool PositiveId(string value) => long.TryParse(value, out var parsed) && parsed > 0;
}
