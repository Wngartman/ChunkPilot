using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

internal static class CurseForgeRuntimeCertificationCommand
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        string? keySourceForRedaction = null;
        try
        {
            if (Has(arguments, "--key-file"))
                throw new ArgumentException(
                    "Credential paths are forbidden on the controller command line. Use the script wrapper.");
            var keyFile = Environment.GetEnvironmentVariable(
                CurseForgeCredentialProvisioner.KeyFileEnvironmentVariable);
            CurseForgeCredentialEnvironment.ClearFromCurrentProcess();
            keySourceForRedaction = keyFile;
            if (string.IsNullOrWhiteSpace(keyFile))
                throw new ArgumentException(
                    "The controller did not receive the approved path through its private environment boundary.");

            var repository = FindRepositoryRoot();
            var phaseText = Has(arguments, "--dry-metadata")
                ? "Metadata"
                : Read(arguments, "--phase") ?? "Metadata";
            if (!Enum.TryParse<CurseForgeRuntimeCertificationPhase>(phaseText, true, out var phase))
                throw new ArgumentException("--phase must be Metadata, Official, or Full.");
            var runtimeRoot = OwnedCertificationRuntime.Prepare(
                repository,
                Read(arguments, "--runtime-root") ??
                Path.Combine(repository, "artifacts", "curseforge-runtime-headless"));
            using var runtimeLease = OwnedCertificationRuntime.AcquireExclusiveLease(
                repository, runtimeRoot);
            if (Read(arguments, "--data-root") is not null || Read(arguments, "--servers-root") is not null)
                throw new ArgumentException(
                    "Data/server root overrides are refused; each certification receives a fresh isolated run root.");
            var runId = Read(arguments, "--run-id") ??
                        throw new ArgumentException(
                            "The wrapper-generated fresh run identity is required.");
            if (!SafeRunId(runId))
                throw new ArgumentException(
                    "The wrapper-generated fresh run identity is invalid.");
            var runRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
                runtimeRoot, Path.Combine(runtimeRoot, "runs", runId), "fresh run root");
            if (!Directory.Exists(runRoot) || File.Exists(runRoot))
                throw new ArgumentException(
                    "The wrapper-generated fresh run root is unavailable.");
            var dataRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
                runtimeRoot,
                Path.Combine(runRoot, "data"),
                "certification data root");
            var serversRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
                runtimeRoot,
                Path.Combine(runRoot, "servers"),
                "managed-server root");
            var temporaryRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
                runtimeRoot,
                Path.Combine(runRoot, "temp"),
                "task temporary root");
            var evidence = ResolveEvidencePaths(
                runtimeRoot, runId, Read(arguments, "--report"),
                Has(arguments, "--payload-ledger"));
            var reportPath = evidence.ReportPath;
            var ledgerPath = evidence.PayloadLedgerPath;
            if (OwnedCertificationRuntime.IsSameOrDescendant(reportPath, runRoot) ||
                OwnedCertificationRuntime.IsSameOrDescendant(ledgerPath, runRoot))
                throw new ArgumentException(
                    "The sanitized report and campaign payload ledger must remain outside the recoverable fresh run root.");
            var packagedAgent = Path.GetFullPath(Path.Combine(
                repository, "artifacts", "dev-current", "Agent", "ChunkPilot.Agent.exe"));
            if (Read(arguments, "--agent") is { } agentOverride &&
                !Path.GetFullPath(agentOverride).Equals(packagedAgent, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "The controller certifies only this worktree's artifacts\\dev-current packaged Agent.");
            var generatedFallbackProject = Read(arguments, "--generated-fallback-project");
            var generatedFallbackFileId = Read(arguments, "--generated-fallback-client-file-id");
            if (phase == CurseForgeRuntimeCertificationPhase.Full &&
                string.IsNullOrWhiteSpace(generatedFallbackProject) !=
                string.IsNullOrWhiteSpace(generatedFallbackFileId))
                throw new ArgumentException(
                    "Full generated fallback project and client file ID must be supplied together.");
            var fullCampaign = phase == CurseForgeRuntimeCertificationPhase.Full
                ? new CurseForgeFullCampaignOptions
                {
                    OfficialNewer = new CurseForgeExactReleaseSelection
                    {
                        ProjectReference = Read(arguments, "--official-new-project") ??
                                           throw new ArgumentException(
                                               "--official-new-project is required for Full."),
                        ClientFileId = Read(arguments, "--official-new-client-file-id") ??
                                       throw new ArgumentException(
                                           "--official-new-client-file-id is required for Full.")
                    },
                    Generated = new CurseForgeExactReleaseSelection
                    {
                        ProjectReference = Read(arguments, "--generated-project") ??
                                           throw new ArgumentException(
                                               "--generated-project is required for Full."),
                        ClientFileId = Read(arguments, "--generated-client-file-id") ??
                            throw new ArgumentException(
                                                "--generated-client-file-id is required for Full.")
                    },
                    GeneratedFallback = string.IsNullOrWhiteSpace(generatedFallbackProject)
                        ? null
                        : new CurseForgeExactReleaseSelection
                        {
                            ProjectReference = generatedFallbackProject,
                            ClientFileId = generatedFallbackFileId!
                        },
                    ForgeMinecraftVersion = Read(arguments, "--forge-minecraft-version") ??
                                            throw new ArgumentException(
                                                "--forge-minecraft-version is required for Full."),
                    ForgeLoaderVersion = Read(arguments, "--forge-loader-version") ??
                                         throw new ArgumentException(
                                             "--forge-loader-version is required for Full."),
                    ModProjectId = Read(arguments, "--mod-project-id") ??
                                   throw new ArgumentException(
                                       "--mod-project-id is required for Full."),
                    ModFileId = Read(arguments, "--mod-file-id") ??
                                throw new ArgumentException(
                                    "--mod-file-id is required for Full.")
                }
                : null;
            var options = new CurseForgeRuntimeCertificationOptions
            {
                RepositoryRoot = repository,
                RuntimeRoot = runtimeRoot,
                ExpectedGitSha = CertificationPackageFreshness.ReadHead(repository),
                AgentExecutablePath = packagedAgent,
                DataRoot = dataRoot,
                ManagedServersRoot = serversRoot,
                TemporaryRoot = temporaryRoot,
                ApprovedKeyFilePath = Path.GetFullPath(keyFile),
                PayloadLedgerPath = ledgerPath,
                ProjectReference = Read(arguments, "--project") ??
                                   throw new ArgumentException("--project is required."),
                ClientFileId = Read(arguments, "--client-file-id") ??
                               throw new ArgumentException("--client-file-id is required."),
                FullCampaign = fullCampaign,
                ServerName = Read(arguments, "--server-name") ?? "ChunkPilot CurseForge Certification",
                Phase = phase,
                ExplicitEulaAuthorization = Has(arguments, "--accept-minecraft-eula-for-certification"),
                Port = ReadInt(arguments, "--port", 25_585, 1, 65_535),
                MinimumRamMb = ReadInt(arguments, "--minimum-ram-mb", 2_048, 512, 24 * 1024),
                MaximumRamMb = ReadInt(arguments, "--maximum-ram-mb", 6_144, 1_024, 24 * 1024),
                MaxPlayers = ReadInt(arguments, "--max-players", 10, 1, 1_000),
                AgentStartupTimeout = TimeSpan.FromSeconds(
                    ReadInt(arguments, "--agent-timeout-seconds", 30, 5, 300)),
                CreationTimeout = TimeSpan.FromSeconds(
                    ReadInt(arguments, "--creation-timeout-seconds", 2_700, 60, 7_200)),
                LifecycleTimeout = TimeSpan.FromSeconds(
                    ReadInt(arguments, "--lifecycle-timeout-seconds", 300, 30, 1_800)),
                AgentExitTimeout = TimeSpan.FromSeconds(
                    ReadInt(arguments, "--agent-exit-timeout-seconds", 240, 30, 900))
            };

            using var cancellation = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.WriteLine($"Headless CurseForge Agent named-pipe certification phase: {phase}");
            Console.WriteLine($"Sanitized report: {reportPath}");
            var journal = new CertificationEvidenceJournal(reportPath, evidence.PendingPath);
            var report = await new CurseForgeRuntimeCertificationController(
                    evidenceJournal: journal)
                .RunAsync(options, cancellation.Token, runtimeLease).ConfigureAwait(false);
            await journal.FinalizeAsync(report, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine($"Result: {report.Result}");
            return report.Success ? 0 : cancellation.IsCancellationRequested ? 130 : 2;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(SanitizeCommandError(exception.Message, keySourceForRedaction));
            WriteUsage();
            return 64;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine(SanitizeCommandError(exception.Message, keySourceForRedaction));
            return 2;
        }
    }

    private static string SanitizeCommandError(string message, string? keySource)
    {
        var result = SecretRedactor.Redact(message);
        if (!string.IsNullOrWhiteSpace(keySource))
        {
            result = result.Replace(keySource, "[approved-key-file]", StringComparison.OrdinalIgnoreCase);
            try
            {
                result = result.Replace(
                    Path.GetFullPath(keySource), "[approved-key-file]",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or
                                               PathTooLongException)
            {
                // The raw value was already removed above; invalid paths need no further expansion.
            }
        }
        return result;
    }

    public static void WriteUsage()
    {
        Console.Error.WriteLine(
            "       ChunkPilot.Certification certify-curseforge-runtime --project <id-or-slug> --client-file-id <id> [--phase Metadata|Official|Full] [options]");
        Console.Error.WriteLine(
            "       Use scripts\\certify-curseforge-runtime.ps1 for environment-only credential handoff. Official and Full require --accept-minecraft-eula-for-certification; Metadata performs no payload download.");
        Console.Error.WriteLine(
            "       Full additionally requires exact official-new, generated, Forge Minecraft/loader, and CurseForge mod project/file selections; an optional exact generated fallback pair is supported.");
    }

    internal static CertificationEvidencePaths ResolveEvidencePaths(
        string runtimeRoot,
        string runId,
        string? requestedReport,
        bool payloadLedgerOverridden)
    {
        if (payloadLedgerOverridden)
            throw new ArgumentException(
                "The campaign payload ledger path is fixed and cannot be overridden.");
        var evidenceRoot = OwnedCertificationRuntime.RequireOwnedDescendant(
            runtimeRoot, Path.Combine(runtimeRoot, "evidence"), "campaign evidence directory");
        Directory.CreateDirectory(evidenceRoot);
        _ = OwnedCertificationRuntime.RequireOwnedDescendant(
            runtimeRoot, evidenceRoot, "campaign evidence directory");
        var reportPath = OwnedCertificationRuntime.RequireOwnedDescendant(
            runtimeRoot,
            requestedReport ?? Path.Combine(evidenceRoot, $"certification-{runId}.json"),
            "sanitized report");
        if (!Path.GetDirectoryName(reportPath)!.Equals(
                evidenceRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(reportPath).StartsWith(
                "certification-", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(reportPath).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(reportPath).Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')) ||
            File.Exists(reportPath) || Directory.Exists(reportPath))
            throw new ArgumentException(
                "The immutable report must be a new safe certification-*.json file directly under RuntimeRoot\\evidence.");
        var ledgerPath = OwnedCertificationRuntime.RequireOwnedDescendant(
            runtimeRoot,
            Path.Combine(evidenceRoot, "curseforge-payload-ledger.json"),
            "payload ledger");
        var ledgerLockPath = ledgerPath + ".lock";
        if (Directory.Exists(ledgerLockPath) ||
            File.Exists(ledgerLockPath) &&
            (File.GetAttributes(ledgerLockPath) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException(
                "The campaign payload-ledger lock path is unsafe.");
        if (reportPath.Equals(ledgerPath, StringComparison.OrdinalIgnoreCase) ||
            reportPath.Equals(ledgerPath + ".lock", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The immutable report cannot alias campaign payload accounting evidence.");
        var pendingPath = Path.Combine(
            evidenceRoot,
            Path.GetFileNameWithoutExtension(reportPath) + ".pending.json");
        if (File.Exists(pendingPath) || Directory.Exists(pendingPath) ||
            pendingPath.Equals(ledgerPath, StringComparison.OrdinalIgnoreCase) ||
            pendingPath.Equals(ledgerPath + ".lock", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The pending certification evidence path is occupied or aliases payload accounting.");
        return new CertificationEvidencePaths(reportPath, pendingPath, ledgerPath);
    }

    private static string FindRepositoryRoot()
    {
        for (var candidate = new DirectoryInfo(Environment.CurrentDirectory);
             candidate is not null;
             candidate = candidate.Parent)
        {
            if (File.Exists(Path.Combine(candidate.FullName, "ChunkPilot.sln")) &&
                (Directory.Exists(Path.Combine(candidate.FullName, ".git")) ||
                 File.Exists(Path.Combine(candidate.FullName, ".git"))))
                return candidate.FullName;
        }
        throw new DirectoryNotFoundException("Run the certification controller from a ChunkPilot worktree.");
    }

    private static bool Has(IReadOnlyCollection<string> arguments, string name) =>
        arguments.Any(argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Read(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
        {
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                return arguments[index + 1];
        }
        return null;
    }

    private static int ReadInt(
        IReadOnlyList<string> arguments,
        string name,
        int fallback,
        int minimum,
        int maximum) =>
        int.TryParse(Read(arguments, name), out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static bool SafeRunId(string value) =>
        value.Length == 52 &&
        value.StartsWith("run-", StringComparison.Ordinal) &&
        value[4..12].All(char.IsAsciiDigit) && value[12] == '-' &&
        value[13..19].All(char.IsAsciiDigit) && value[19] == '-' &&
        value[20..].All(Uri.IsHexDigit);
}

internal sealed record CertificationEvidencePaths(
    string ReportPath,
    string PendingPath,
    string PayloadLedgerPath);
