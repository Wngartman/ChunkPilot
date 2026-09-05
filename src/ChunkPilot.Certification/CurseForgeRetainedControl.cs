using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

/// <summary>Rechecks a stopped, task-owned control without another pack creation or payload transfer.</summary>
internal sealed record RetainedControlSelection(string RunId, Guid ServerId, Guid OperationId,
    string ProjectId, string ClientFileId, string ServerFileId, long ArchiveBytes, string ArchiveSha256,
    string OriginalGitSha, bool CreationSucceeded)
{
    public static RetainedControlSelection Read(string runtime, string reportPath)
    {
        var path = OwnedCertificationRuntime.RequireOwnedDescendant(runtime, reportPath, "prior control evidence");
        if (!string.Equals(Path.GetDirectoryName(path), Path.Combine(Path.GetFullPath(runtime), "evidence"), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("certification-", StringComparison.Ordinal) ||
            !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resume requires an exact prior report in this owned runtime evidence root.");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is <= 0 or > 8 * 1024 * 1024 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Prior control evidence is not a bounded regular file.");
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("documentType").GetString() != "ChunkPilot.CurseForgeRuntimeCertificationReport" ||
            !root.GetProperty("freshRunIntentionallyRetained").GetBoolean() ||
            !root.GetProperty("cleanupSucceeded").GetBoolean() ||
            !root.GetProperty("terminalSelectedPortAbsent").GetBoolean() ||
            root.GetProperty("agentJobActiveProcessesAfterCleanup").GetInt32() != 0 ||
            root.GetProperty("agentExitCode").GetInt32() != 0)
            throw new InvalidDataException("The prior control does not prove intentional retention and exact terminal cleanup.");
        var operation = root.GetProperty("creationOperationId").GetGuid();
        var terminal = root.GetProperty("operationStates").EnumerateArray().LastOrDefault(row =>
            row.GetProperty("operationId").GetGuid() == operation && row.GetProperty("terminal").GetBoolean());
        if (terminal.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The prior creation has no terminal operation evidence.");
        var creationSucceeded = terminal.GetProperty("success").GetBoolean();
        var payload = root.GetProperty("payloads").EnumerateArray().Single(row =>
            row.GetProperty("kind").GetString() == "official-server-pack" &&
            row.GetProperty("operationId").GetGuid() == operation);
        var size = payload.GetProperty("expectedBytes").GetInt64();
        var sha = payload.GetProperty("localSha256").GetString() ?? "";
        if (size <= 0 || payload.GetProperty("downloadedBytes").GetInt64() != size || sha.Length != 64 ||
            !sha.All(Uri.IsHexDigit) || !payload.GetProperty("providerSha1Verified").GetBoolean())
            throw new InvalidDataException("The prior input transfer was not complete and integrity-verified.");
        var server = root.GetProperty("serverId");
        return new(root.GetProperty("runId").GetString()!, server.ValueKind == JsonValueKind.Null ? Guid.Empty : server.GetGuid(), operation,
            root.GetProperty("projectId").GetString()!, root.GetProperty("clientFileId").GetString()!,
            root.GetProperty("serverPackFileId").GetString()!, size, sha,
            root.GetProperty("candidateGitSha").GetString()!, creationSucceeded);
    }
}

internal sealed partial class CurseForgeRuntimeCertificationSession
{
    internal static async Task<CreationVerifiedInput?> VerifyFailedCreationTransferAsync(
        CurseForgeRuntimeCertificationOptions options, CatalogItem project, CatalogVersion release,
        InstallOperationSnapshot operation, CancellationToken cancellationToken)
    {
        if (operation.VerifiedInput is not { } proof) return null;
        if (!operation.IsTerminal || proof.OperationId != operation.OperationId || proof.ServerId == Guid.Empty ||
            proof.ProjectId != project.ProjectId || proof.ClientFileId != release.ClientFileId ||
            proof.ServerFileId != release.ServerPackFileId || proof.SizeBytes != release.SizeBytes)
            throw new InvalidDataException("The retained transfer receipt does not match the failed exact creation.");
        var paths = new AppDataPaths(options.DataRoot, options.ManagedServersRoot);
        var destination = Path.Combine(options.ManagedServersRoot, ManagedServerInstaller.MakeSafeInstanceName(options.ServerName));
        var archive = await new VerifiedCreationArchiveStore(paths).VerifyAsync(new CreationJournalEntry
        {
            OperationId = operation.OperationId, ServerId = proof.ServerId,
            CanonicalDestination = destination, VerifiedInput = proof
        }, cancellationToken).ConfigureAwait(false);
        await using var input = File.OpenRead(archive);
#pragma warning disable CA5350 // The independent fresh provider digest supplements the operation-owned local SHA-256 proof.
        if (!Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken)).Equals(release.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The failed creation input no longer matches official transfer integrity.");
#pragma warning restore CA5350
        return proof;
    }

    private string? retainedControlInput;
    private RetainedControlSelection? retainedControl;
    private string? retainedControlDestination;

    private async Task ResumeStoppedControlAsync(CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report, CatalogItem project, CatalogVersion release,
        CancellationToken cancellationToken)
    {
        var prior = RetainedControlSelection.Read(options.RuntimeRoot, options.ResumeReportPath);
        if (prior.RunId != report.RunId || prior.ProjectId != project.ProjectId ||
            prior.ClientFileId != release.ClientFileId || prior.ServerFileId != release.ServerPackFileId ||
            prior.ArchiveBytes != release.SizeBytes)
            throw new InvalidDataException("The retained control differs from the exact fresh official relationship.");
        if (!prior.CreationSucceeded)
        {
            await ResumeFailedCreationAsync(options, report, project, release, prior, cancellationToken);
            return;
        }
        var dashboard = await transport.SendAsync<DashboardSnapshot>("Dashboard", cancellationToken: cancellationToken);
        var server = dashboard.Servers.Single(item => item.Definition.Id == prior.ServerId);
        if (server.State != ServerState.Stopped || server.Definition.Port != options.Port)
            throw new InvalidDataException("Only the exact stopped control and original port can be resumed.");
        var serverRoot = OwnedCertificationRuntime.RequireOwnedDescendant(options.ManagedServersRoot,
            server.Definition.RootPath, "retained control server");
        var installed = (await transport.SendAsync<UpdateSourceResponse>("GetUpdateSource",
            new ServerIdRequest(prior.ServerId), cancellationToken)).Source;
        if (installed is null || installed.Provider != UpdateProvider.CurseForge ||
            installed.ProjectId != prior.ProjectId || installed.InstalledVersionId != prior.ClientFileId ||
            installed.InstalledFileId != prior.ServerFileId)
            throw new InvalidDataException("The Agent's installed control identity does not match the prior evidence.");
        var inputRoot = Path.Combine(options.DataRoot, "Staging", "creation-inputs",
            ServerCreationTransaction.StagingFolderName(prior.OperationId));
        CreationStagingSafety.RequireExactOwnership(inputRoot, prior.OperationId, prior.ServerId, serverRoot);
        retainedControlInput = inputRoot;
        retainedControl = prior;
        retainedControlDestination = serverRoot;
        await VerifyRetainedControlInputAsync(cancellationToken);
        await using (var input = File.OpenRead(Path.Combine(inputRoot, "input.zip")))
        {
#pragma warning disable CA5350 // Fresh provider SHA-1 supplements the local SHA-256 check, not a security signature.
            if (!Convert.ToHexString(await SHA1.HashDataAsync(input, cancellationToken)).Equals(release.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The retained input no longer matches fresh official integrity metadata.");
#pragma warning restore CA5350
        }
        report.CreationOperationId = prior.OperationId;
        report.ServerId = prior.ServerId;
        terminalCreationOperationIds.Add(prior.OperationId);
        report.Steps.Add(new CertificationStepEvidence { Name = "retained control revalidation", Status = "PASSED",
            Detail = "Original creation checkpoint " + prior.OriginalGitSha + "; exact installed IDs, local SHA-256 and fresh provider SHA-1 reverified. Zero new archive payload bytes." });
        ApplyBudget(report, await payloadBudget.GetSnapshotAsync(cancellationToken));
        await ExerciseLifecycleAsync(options, report, prior.ServerId, cancellationToken);
        await VerifyRetainedControlInputAsync(cancellationToken);
        await VerifyCertifiedPostconditionsAsync(options, report, prior.ServerId, cancellationToken);
        report.Success = true;
    }

    private async Task ResumeFailedCreationAsync(CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report, CatalogItem project, CatalogVersion release,
        RetainedControlSelection prior, CancellationToken cancellationToken)
    {
        var failed = await transport.SendAsync<InstallOperationSnapshot>("InstallProgress",
            new InstallOperationRequest(prior.OperationId), cancellationToken);
        var proof = await VerifyFailedCreationTransferAsync(options, project, release, failed, cancellationToken)
            ?? throw new InvalidDataException("The failed creation has no reusable verified input.");
        if (!failed.CanRetry || proof.LocalSha256 != prior.ArchiveSha256 || proof.SizeBytes != prior.ArchiveBytes)
            throw new InvalidDataException("The exact retained failure is no longer retryable.");
        report.CreationOperationId = prior.OperationId;
        activeCreationOperationId = prior.OperationId;
        var accepted = await transport.SendAsync<InstallOperationRequest>("RetryModpackCreation",
            new CreationRecoveryRequest(prior.OperationId, failed.RetryGeneration), cancellationToken);
        if (accepted.OperationId != prior.OperationId)
            throw new InvalidDataException("Retry changed the exact operation identity.");
        report.Steps.Add(new CertificationStepEvidence { Name = "native retained-input retry", Status = "PASSED",
            OperationId = prior.OperationId, Detail = "Fresh exact release and local archive reverified; retry accepted through the production Agent recovery command. No archive reservation or download was requested." });
        var retried = await PollCreationAsync(options, report, prior.OperationId, cancellationToken);
        activeCreationTerminal = true;
        terminalCreationOperationIds.Add(prior.OperationId);
        ApplyBudget(report, await payloadBudget.GetSnapshotAsync(cancellationToken));
        if (!retried.Success || retried.Result is null)
        {
            _ = await VerifyFailedCreationTransferAsync(options, project, release, retried, cancellationToken)
                ?? throw new InvalidDataException("The retry lost its verified input evidence.");
            report.Steps.Add(new CertificationStepEvidence { Name = "retry retained-input integrity", Status = "PASSED",
                Detail = "The same immutable input is still verified after safe failure; zero repeated CurseForge archive payload bytes." });
            throw new InvalidOperationException("The exact retained-input retry failed safely; see its structured validation evidence.");
        }
        if (retried.Result.Definition.Id != proof.ServerId)
            throw new InvalidDataException("Retry promoted a different server identity.");
        await ExerciseLifecycleAsync(options, report, proof.ServerId, cancellationToken);
        await VerifyCertifiedPostconditionsAsync(options, report, proof.ServerId, cancellationToken);
        report.Success = true;
    }

    private async Task VerifyRetainedControlInputAsync(CancellationToken cancellationToken)
    {
        if (retainedControlInput is null || retainedControl is null) return;
        CreationStagingSafety.RequireExactOwnership(retainedControlInput, retainedControl.OperationId,
            retainedControl.ServerId, retainedControlDestination
                ?? throw new InvalidDataException("Retained destination ownership is unavailable."));
        var entries = Directory.EnumerateFileSystemEntries(retainedControlInput).ToArray();
        if (entries.Length != 2 || entries.Any(path =>
                Path.GetFileName(path) is not ("input.zip" or CreationOwnershipMarker.FileName) ||
                !File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)))
            throw new InvalidDataException("Retained input contains unknown, mutable, or redirected entries.");
        await using var archive = File.OpenRead(Path.Combine(retainedControlInput, "input.zip"));
        if (archive.Length != retainedControl.ArchiveBytes ||
            !Convert.ToHexString(await SHA256.HashDataAsync(archive, cancellationToken))
                .Equals(retainedControl.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The retained control archive changed.");
    }
}
