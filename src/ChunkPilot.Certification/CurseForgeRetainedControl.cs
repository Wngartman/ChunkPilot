using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

/// <summary>Rechecks a stopped, task-owned control without another pack creation or payload transfer.</summary>
internal sealed record RetainedControlSelection(string RunId, Guid ServerId, Guid OperationId,
    string ProjectId, string ClientFileId, string ServerFileId, long ArchiveBytes, string ArchiveSha256,
    string OriginalGitSha)
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
        if (!root.GetProperty("operationStates").EnumerateArray().Any(row =>
                row.GetProperty("operationId").GetGuid() == operation && row.GetProperty("terminal").GetBoolean() &&
                row.GetProperty("success").GetBoolean()))
            throw new InvalidDataException("The prior control did not complete creation.");
        var payload = root.GetProperty("payloads").EnumerateArray().Single(row =>
            row.GetProperty("kind").GetString() == "official-server-pack" &&
            row.GetProperty("operationId").GetGuid() == operation);
        var size = payload.GetProperty("expectedBytes").GetInt64();
        var sha = payload.GetProperty("localSha256").GetString() ?? "";
        if (size <= 0 || payload.GetProperty("downloadedBytes").GetInt64() != size || sha.Length != 64 ||
            !sha.All(Uri.IsHexDigit) || !payload.GetProperty("providerSha1Verified").GetBoolean())
            throw new InvalidDataException("The prior input transfer was not complete and integrity-verified.");
        return new(root.GetProperty("runId").GetString()!, root.GetProperty("serverId").GetGuid(), operation,
            root.GetProperty("projectId").GetString()!, root.GetProperty("clientFileId").GetString()!,
            root.GetProperty("serverPackFileId").GetString()!, size, sha,
            root.GetProperty("candidateGitSha").GetString()!);
    }
}

internal sealed partial class CurseForgeRuntimeCertificationSession
{
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
