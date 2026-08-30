using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.Certification;

internal enum CurseForgeRuntimeCertificationPhase
{
    Metadata,
    Official,
    Full
}

internal sealed record CurseForgeRuntimeCertificationOptions
{
    public required string RepositoryRoot { get; init; }
    public required string RuntimeRoot { get; init; }
    public required string ExpectedGitSha { get; init; }
    public required string AgentExecutablePath { get; init; }
    public required string DataRoot { get; init; }
    public required string ManagedServersRoot { get; init; }
    public string TemporaryRoot { get; init; } = "";
    public required string ApprovedKeyFilePath { get; init; }
    public required string PayloadLedgerPath { get; init; }
    public required string ProjectReference { get; init; }
    public required string ClientFileId { get; init; }
    public CurseForgeFullCampaignOptions? FullCampaign { get; init; }
    public string ServerName { get; init; } = "ChunkPilot CurseForge Certification";
    public CurseForgeRuntimeCertificationPhase Phase { get; init; }
    public bool ExplicitEulaAuthorization { get; init; }
    public int Port { get; init; } = 25_585;
    public int MinimumRamMb { get; init; } = 2_048;
    public int MaximumRamMb { get; init; } = 6_144;
    public int MaxPlayers { get; init; } = 10;
    public TimeSpan AgentStartupTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CreationTimeout { get; init; } = TimeSpan.FromMinutes(45);
    public TimeSpan LifecycleTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan AgentExitTimeout { get; init; } = TimeSpan.FromMinutes(4);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public void Validate()
    {
        var repository = Path.GetFullPath(RepositoryRoot);
        if (!Directory.Exists(repository) || !File.Exists(Path.Combine(repository, "ChunkPilot.sln")))
            throw new InvalidOperationException("The current ChunkPilot repository root could not be proven.");
        var runtime = OwnedCertificationRuntime.Prepare(repository, RuntimeRoot);
        var agent = Path.GetFullPath(AgentExecutablePath);
        var data = Path.GetFullPath(DataRoot);
        var servers = Path.GetFullPath(ManagedServersRoot);
        var temporary = Path.GetFullPath(string.IsNullOrWhiteSpace(TemporaryRoot)
            ? Path.Combine(Path.GetDirectoryName(data)!, "temp")
            : TemporaryRoot);
        _ = OwnedCertificationRuntime.RequireOwnedDescendant(runtime, data, "certification data root");
        _ = OwnedCertificationRuntime.RequireOwnedDescendant(runtime, servers, "managed-server root");
        _ = OwnedCertificationRuntime.RequireOwnedDescendant(runtime, temporary, "task temporary root");
        _ = OwnedCertificationRuntime.RequireOwnedDescendant(runtime, PayloadLedgerPath, "payload ledger");
        var key = Path.GetFullPath(ApprovedKeyFilePath);
        var dataParent = Path.GetDirectoryName(data);
        var serversParent = Path.GetDirectoryName(servers);
        var temporaryParent = Path.GetDirectoryName(temporary);
        if (string.IsNullOrWhiteSpace(dataParent) || string.IsNullOrWhiteSpace(serversParent) ||
            string.IsNullOrWhiteSpace(temporaryParent) ||
            !dataParent.Equals(serversParent, StringComparison.OrdinalIgnoreCase) ||
            !dataParent.Equals(temporaryParent, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(data).Equals("data", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(servers).Equals("servers", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(temporary).Equals("temp", StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(temporary) || File.Exists(temporary) ||
            (File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0 ||
            Directory.Exists(data) || Directory.Exists(servers) || File.Exists(data) || File.Exists(servers))
            throw new InvalidOperationException(
                "Certification data, server, and temporary roots must be exact fresh task-owned siblings.");
        var initialRunEntries = Directory.EnumerateFileSystemEntries(dataParent).ToArray();
        if (initialRunEntries.Length != 1 ||
            !initialRunEntries[0].Equals(temporary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The fresh certification run root must initially contain only its task-owned temporary directory.");
        foreach (var variable in new[] { "TEMP", "TMP", "DOTNET_BUNDLE_EXTRACT_BASE_DIR" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value) ||
                !Path.GetFullPath(value).Equals(temporary, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The controller temporary environment is not bound to the exact fresh task run.");
        }
        if (OwnedCertificationRuntime.IsSameOrDescendant(PayloadLedgerPath, dataParent))
            throw new InvalidOperationException(
                "The campaign payload ledger must remain outside the recoverable fresh run root.");
        if (ExpectedGitSha.Length != 40 || ExpectedGitSha.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The current repository commit identity is invalid.");
        if (!Path.GetFileName(agent).Equals("ChunkPilot.Agent.exe", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("The packaged Agent path is invalid.");
        CertificationPackageFreshness.Validate(repository, ExpectedGitSha, agent);
        if (!File.Exists(key))
            throw new FileNotFoundException("The approved CurseForge key file is unavailable.");
        if ((File.GetAttributes(key) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The approved CurseForge key source cannot be a reparse point.");
        var evidenceRoot = Path.GetFullPath(Path.Combine(runtime, "evidence"));
        var ledger = Path.GetFullPath(PayloadLedgerPath);
        if (!Path.GetDirectoryName(ledger)!.Equals(
                evidenceRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(ledger).Equals(
                "curseforge-payload-ledger.json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The hard payload ledger must use its fixed campaign evidence path.");
        if (Path.GetPathRoot(data)!.Equals(data, StringComparison.OrdinalIgnoreCase) ||
            Path.GetPathRoot(servers)!.Equals(servers, StringComparison.OrdinalIgnoreCase) ||
            data.Equals(servers, StringComparison.OrdinalIgnoreCase) ||
            data.StartsWith(servers + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            servers.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Certification data and managed-server roots must be distinct scoped directories.");
        if (string.IsNullOrWhiteSpace(ProjectReference) || ProjectReference.Length > 80 ||
            !long.TryParse(ClientFileId, out var fileId) || fileId <= 0)
            throw new ArgumentException("An exact CurseForge project reference and positive client file ID are required.");
        if (string.IsNullOrWhiteSpace(ServerName) || ServerName.Length > 80)
            throw new ArgumentException("Use a bounded certification server name.");
        if (ServerPortPolicy.Validate(Port) is { } portProblem)
            throw new ArgumentException(portProblem);
        if (MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb) is { } memoryProblem)
            throw new ArgumentException(memoryProblem);
        if ((Phase is CurseForgeRuntimeCertificationPhase.Official or
             CurseForgeRuntimeCertificationPhase.Full) && !ExplicitEulaAuthorization)
            throw new InvalidOperationException(
                "The Official and Full phases require --accept-minecraft-eula-for-certification.");
        if (Phase == CurseForgeRuntimeCertificationPhase.Full)
            (FullCampaign ?? throw new InvalidOperationException(
                "The Full phase requires every exact campaign selection.")).Validate();
    }

    public string Sanitize(string value)
    {
        var redacted = SecretRedactor.Redact(value);
        if (!string.IsNullOrWhiteSpace(ApprovedKeyFilePath))
            redacted = redacted.Replace(
                Path.GetFullPath(ApprovedKeyFilePath), "[approved-key-file]",
                StringComparison.OrdinalIgnoreCase);
        foreach (var (path, token) in new[]
                 {
                     (RepositoryRoot, "[repository]"),
                     (RuntimeRoot, "[certification-runtime]"),
                     (DataRoot, "[isolated-data]"),
                     (ManagedServersRoot, "[isolated-servers]"),
                     (TemporaryRoot, "[isolated-temp]")
                 })
        {
            if (!string.IsNullOrWhiteSpace(path))
                redacted = redacted.Replace(
                    Path.GetFullPath(path), token, StringComparison.OrdinalIgnoreCase);
        }
        redacted = Regex.Replace(redacted, "https?://[^\\s\\\"'<>]+", "[provider-url]",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(redacted, "(?<![0-9a-f])[0-9a-f]{40}(?![0-9a-f])",
            "[provider-integrity]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal sealed record CertificationStepEvidence
{
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public double ElapsedMilliseconds { get; init; }
    public Guid? OperationId { get; init; }
    public string State { get; init; } = "";
}

internal sealed record CertificationPayloadEvidence
{
    public string Kind { get; init; } = "";
    public Guid OperationId { get; init; }
    public string ProjectId { get; init; } = "";
    public string FileId { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public long DownloadedBytes { get; init; }
    public bool ProviderSha1Verified { get; init; }
    public string LocalSha256 { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public string State { get; init; } = "";
}

internal sealed record CertificationOperationStateEvidence(
    Guid OperationId,
    long Revision,
    string InstallState,
    string CreationStage,
    string CurrentStep,
    long BytesDownloaded,
    long? TotalBytes,
    bool Terminal,
    bool Success);

internal sealed record CertificationLifecycleEvidence(
    string Action,
    string State,
    bool Success,
    string Detail,
    double ElapsedMilliseconds);

internal sealed record CertificationConnectionEvidence
{
    public FindingSeverity ProcessRunning { get; init; }
    public FindingSeverity PortListening { get; init; }
    public FindingSeverity MinecraftResponds { get; init; }
    public string LocalAddress { get; init; } = "";
    public string ExternalResult { get; init; } = "";
}

internal sealed record CertificationLoopbackBindingEvidence
{
    public int ConfiguredPort { get; init; }
    public bool TaskOwnedPropertiesPath { get; init; }
    public bool ServerIpIsExactLoopback { get; init; }
    public bool ServerPortMatches { get; init; }
    public bool TcpListenerObserved { get; init; }
    public bool TcpListenersOnlyLoopback { get; init; }
    public int ObservedListenerCount { get; init; }
    public int ObservedListenerOwnerCount { get; init; }
    public bool ListenerPidOwnershipVerified { get; init; }
    public bool OwnedEndpointInventorySucceeded { get; init; }
    public bool JobAccountingGenerationStable { get; init; }
    public bool JobProcessIdentitySetStable { get; init; }
    public bool OwnedEndpointInventoriesStable { get; init; }
    public bool TaskRootProcessIdentityVerified { get; init; }
    public bool OwnedEndpointIdentitySnapshotsStable { get; init; }
    public bool OwnedEndpointProcessIdentitiesVerified { get; init; }
    public bool OwnedEndpointsWithinTaskServerSubtree { get; init; }
    public bool NoUnexpectedOwnedEndpoints { get; init; }
    public int ExpectedMinecraftListenerCount { get; init; }
    public int ObservedOwnedEndpointCount { get; init; }
    public int ObservedOwnedTcpListenerCount { get; init; }
    public int ObservedOwnedUdpEndpointCount { get; init; }
    public int ObservedOwnedIpv4EndpointCount { get; init; }
    public int ObservedOwnedIpv6EndpointCount { get; init; }
    public int ObservedOwnedNonLoopbackEndpointCount { get; init; }
    public int ObservedOwnedWildcardEndpointCount { get; init; }
    public int CaptureRaceEndpointCount { get; init; }
    public int EndpointInventoryDifferenceCount { get; init; }
    public int UnexpectedOwnedEndpointCount { get; init; }
    public bool EndpointPolicyPassed { get; init; }
    public string EndpointPolicyDetail { get; init; } =
        "Owned endpoint inventory was not attempted.";
}

internal sealed record CertificationOwnedEndpointPolicyEvaluation
{
    public bool InventorySucceeded { get; init; }
    public bool JobAccountingGenerationStable { get; init; }
    public bool JobProcessIdentitySetStable { get; init; }
    public bool OwnedEndpointInventoriesStable { get; init; }
    public bool TaskRootProcessIdentityVerified { get; init; }
    public bool OwnedEndpointIdentitySnapshotsStable { get; init; }
    public bool OwnedEndpointProcessIdentitiesVerified { get; init; }
    public bool OwnedEndpointsWithinTaskServerSubtree { get; init; }
    public bool OwnedTcpListenersOnlyLoopback { get; init; }
    public int ExpectedMinecraftListenerCount { get; init; }
    public int ObservedOwnedEndpointCount { get; init; }
    public int ObservedOwnedTcpListenerCount { get; init; }
    public int ObservedOwnedUdpEndpointCount { get; init; }
    public int ObservedOwnedIpv4EndpointCount { get; init; }
    public int ObservedOwnedIpv6EndpointCount { get; init; }
    public int ObservedOwnedNonLoopbackEndpointCount { get; init; }
    public int ObservedOwnedWildcardEndpointCount { get; init; }
    public int ObservedOwnedProcessCount { get; init; }
    public int CaptureRaceEndpointCount { get; init; }
    public int EndpointInventoryDifferenceCount { get; init; }
    public int UnexpectedOwnedEndpointCount { get; init; }
    public string Detail { get; init; } = "Owned endpoint inventory was not attempted.";

    public bool OwnershipVerified =>
        InventorySucceeded &&
        JobAccountingGenerationStable &&
        JobProcessIdentitySetStable &&
        OwnedEndpointInventoriesStable &&
        TaskRootProcessIdentityVerified &&
        OwnedEndpointIdentitySnapshotsStable &&
        OwnedEndpointProcessIdentitiesVerified &&
        OwnedEndpointsWithinTaskServerSubtree &&
        ExpectedMinecraftListenerCount > 0;

    public bool Passed =>
        OwnershipVerified &&
        UnexpectedOwnedEndpointCount == 0;
}

internal static class CertificationOwnedEndpointPolicy
{
    public static CertificationOwnedEndpointPolicyEvaluation CaptureAndEvaluate(
        IWindowsOwnedNetworkEndpointSource source,
        int expectedPort,
        int taskServerProcessId,
        long taskServerProcessCreationTicks,
        IReadOnlyDictionary<int, long>? exactOwnedProcessIdentities,
        IReadOnlyDictionary<int, int>? processParents,
        Func<int, long, bool>? exactOwnedProcessIdentityStillMatches) =>
        CaptureAndEvaluate(
            source,
            expectedPort,
            taskServerProcessId,
            taskServerProcessCreationTicks,
            () => CreateSyntheticSnapshot(exactOwnedProcessIdentities),
            () => processParents,
            exactOwnedProcessIdentityStillMatches);

    public static CertificationOwnedEndpointPolicyEvaluation CaptureAndEvaluate(
        IWindowsOwnedNetworkEndpointSource source,
        int expectedPort,
        int taskServerProcessId,
        long taskServerProcessCreationTicks,
        Func<WindowsOwnedProcessJobSnapshot?> captureExactOwnedProcessSnapshot,
        Func<IReadOnlyDictionary<int, int>?> captureProcessParents,
        Func<int, long, bool>? exactOwnedProcessIdentityStillMatches)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(captureExactOwnedProcessSnapshot);
        ArgumentNullException.ThrowIfNull(captureProcessParents);
        try
        {
            var snapshotBefore = captureExactOwnedProcessSnapshot();
            var endpointsFirst = source.Capture() ?? throw new InvalidDataException(
                "Windows returned no endpoint ownership inventory.");
            var snapshotBetween = captureExactOwnedProcessSnapshot();
            var endpointsSecond = source.Capture() ?? throw new InvalidDataException(
                "Windows returned no endpoint ownership inventory.");
            // Bracket two full four-table inventories with exact Job PID+creation/accounting
            // snapshots. The same owned endpoint multiset and process generation must survive both
            // observations; PID entry, exit, reuse, or stable-process socket mutation is rejected.
            var snapshotAfter = captureExactOwnedProcessSnapshot();
            var processParents = captureProcessParents();
            return Evaluate(
                endpointsFirst,
                endpointsSecond,
                expectedPort,
                taskServerProcessId,
                taskServerProcessCreationTicks,
                snapshotBefore,
                snapshotBetween,
                snapshotAfter,
                processParents,
                exactOwnedProcessIdentityStillMatches);
        }
        catch (Exception exception) when (exception is
                   Win32Exception or InvalidDataException or InvalidOperationException or
                   ArgumentException or PlatformNotSupportedException or NotSupportedException)
        {
            return new CertificationOwnedEndpointPolicyEvaluation
            {
                Detail =
                    "Windows endpoint inventory failed; certification rejected the binding without recording endpoint identities."
            };
        }
    }

    internal static CertificationOwnedEndpointPolicyEvaluation Evaluate(
        IReadOnlyList<WindowsOwnedNetworkEndpoint> endpointsFirst,
        IReadOnlyList<WindowsOwnedNetworkEndpoint> endpointsSecond,
        int expectedPort,
        int taskServerProcessId,
        long taskServerProcessCreationTicks,
        WindowsOwnedProcessJobSnapshot? snapshotBeforeCapture,
        WindowsOwnedProcessJobSnapshot? snapshotBetweenCaptures,
        WindowsOwnedProcessJobSnapshot? snapshotAfterCapture,
        IReadOnlyDictionary<int, int>? processParents,
        Func<int, long, bool>? exactOwnedProcessIdentityStillMatches)
    {
        ArgumentNullException.ThrowIfNull(endpointsFirst);
        ArgumentNullException.ThrowIfNull(endpointsSecond);
        if (expectedPort is < 1 or > 65_535)
            throw new ArgumentOutOfRangeException(nameof(expectedPort));

        var identitiesBeforeCapture = snapshotBeforeCapture?.ProcessIdentities;
        var identitiesBetweenCaptures = snapshotBetweenCaptures?.ProcessIdentities;
        var identitiesAfterCapture = snapshotAfterCapture?.ProcessIdentities;
        var jobAccountingGenerationStable =
            snapshotBeforeCapture is not null &&
            snapshotBetweenCaptures is not null &&
            snapshotAfterCapture is not null &&
            snapshotBeforeCapture.TotalProcesses == snapshotBetweenCaptures.TotalProcesses &&
            snapshotBetweenCaptures.TotalProcesses == snapshotAfterCapture.TotalProcesses &&
            snapshotBeforeCapture.ActiveProcesses == snapshotBetweenCaptures.ActiveProcesses &&
            snapshotBetweenCaptures.ActiveProcesses == snapshotAfterCapture.ActiveProcesses &&
            snapshotBeforeCapture.TotalTerminatedProcesses ==
            snapshotBetweenCaptures.TotalTerminatedProcesses &&
            snapshotBetweenCaptures.TotalTerminatedProcesses ==
            snapshotAfterCapture.TotalTerminatedProcesses;
        var jobProcessIdentitySetStable =
            jobAccountingGenerationStable &&
            snapshotBeforeCapture!.ActiveProcesses ==
            checked((uint)snapshotBeforeCapture.ProcessIdentities.Count) &&
            snapshotBetweenCaptures!.ActiveProcesses ==
            checked((uint)snapshotBetweenCaptures.ProcessIdentities.Count) &&
            snapshotAfterCapture!.ActiveProcesses ==
            checked((uint)snapshotAfterCapture.ProcessIdentities.Count) &&
            SameProcessIdentitySet(
                snapshotBeforeCapture.ProcessIdentities,
                snapshotBetweenCaptures.ProcessIdentities) &&
            SameProcessIdentitySet(
                snapshotBetweenCaptures.ProcessIdentities,
                snapshotAfterCapture.ProcessIdentities);
        var taskRootIdentityVerified =
            identitiesBeforeCapture is not null &&
            identitiesBetweenCaptures is not null &&
            identitiesAfterCapture is not null &&
            exactOwnedProcessIdentityStillMatches is not null &&
            taskServerProcessId > 0 &&
            taskServerProcessCreationTicks != ProcessCreationIdentity.Unknown &&
            identitiesBeforeCapture.TryGetValue(
                taskServerProcessId, out var taskRootIdentityBefore) &&
            identitiesBetweenCaptures.TryGetValue(
                taskServerProcessId, out var taskRootIdentityBetween) &&
            identitiesAfterCapture.TryGetValue(
                taskServerProcessId, out var taskRootIdentityAfter) &&
            ProcessCreationIdentity.Matches(
                taskRootIdentityBefore, taskRootIdentityBetween) &&
            ProcessCreationIdentity.Matches(
                taskRootIdentityBetween, taskRootIdentityAfter) &&
            ProcessCreationIdentity.Matches(
                taskRootIdentityAfter, taskServerProcessCreationTicks) &&
            exactOwnedProcessIdentityStillMatches(taskServerProcessId, taskRootIdentityAfter);

        var stableOwnedIdentities = new Dictionary<int, long>();
        if (identitiesBeforeCapture is not null &&
            identitiesBetweenCaptures is not null &&
            identitiesAfterCapture is not null)
        {
            foreach (var (processId, identityAfter) in identitiesAfterCapture)
            {
                if (identityAfter != ProcessCreationIdentity.Unknown &&
                    identitiesBeforeCapture.TryGetValue(processId, out var identityBefore) &&
                    identitiesBetweenCaptures.TryGetValue(processId, out var identityBetween) &&
                    ProcessCreationIdentity.Matches(identityBefore, identityBetween) &&
                    ProcessCreationIdentity.Matches(identityBetween, identityAfter))
                    stableOwnedIdentities[processId] = identityAfter;
            }
        }
        var observedJobProcessIds = new HashSet<int>();
        if (identitiesBeforeCapture is not null)
            observedJobProcessIds.UnionWith(identitiesBeforeCapture.Keys);
        if (identitiesBetweenCaptures is not null)
            observedJobProcessIds.UnionWith(identitiesBetweenCaptures.Keys);
        if (identitiesAfterCapture is not null)
            observedJobProcessIds.UnionWith(identitiesAfterCapture.Keys);
        var allEndpoints = endpointsFirst.Concat(endpointsSecond).ToArray();
        var captureRaceEndpoints = allEndpoints.Where(endpoint =>
            observedJobProcessIds.Contains(endpoint.ProcessId) &&
            !stableOwnedIdentities.ContainsKey(endpoint.ProcessId)).ToArray();
        var ownedEndpointsFirst = endpointsFirst.Where(endpoint =>
            stableOwnedIdentities.ContainsKey(endpoint.ProcessId)).ToArray();
        var ownedEndpoints = endpointsSecond.Where(endpoint =>
            stableOwnedIdentities.ContainsKey(endpoint.ProcessId)).ToArray();
        var endpointInventoryDifferenceCount = EndpointMultisetDifferenceCount(
            ownedEndpointsFirst, ownedEndpoints);
        var ownedEndpointsAcrossInventories = ownedEndpointsFirst.Concat(ownedEndpoints).ToArray();
        var endpointIdentitiesVerified =
            identitiesBeforeCapture is not null &&
            identitiesBetweenCaptures is not null &&
            identitiesAfterCapture is not null &&
            exactOwnedProcessIdentityStillMatches is not null &&
            ownedEndpointsAcrossInventories.All(endpoint =>
                stableOwnedIdentities.TryGetValue(endpoint.ProcessId, out var identity) &&
                exactOwnedProcessIdentityStillMatches(endpoint.ProcessId, identity));
        var endpointsWithinTaskSubtree =
            processParents is not null &&
            exactOwnedProcessIdentityStillMatches is not null &&
            ownedEndpointsAcrossInventories.All(endpoint =>
                WindowsProcessParents.IsLiveStableRootOrDescendant(
                    endpoint.ProcessId,
                    taskServerProcessId,
                    processParents,
                    stableOwnedIdentities,
                    exactOwnedProcessIdentityStillMatches));
        var expectedListeners = ownedEndpoints.Where(endpoint =>
            endpoint.Transport == WindowsOwnedEndpointTransport.Tcp &&
            endpoint.Port == expectedPort &&
            endpoint.IsExactLoopback).ToArray();
        var unexpectedEndpoints = ownedEndpoints.Where(endpoint =>
            endpoint.Transport != WindowsOwnedEndpointTransport.Tcp ||
            endpoint.Port != expectedPort ||
            !endpoint.IsExactLoopback).ToArray();
        var tcpListeners = ownedEndpoints.Where(endpoint =>
            endpoint.Transport == WindowsOwnedEndpointTransport.Tcp).ToArray();

        var detail = !jobAccountingGenerationStable
            ? "The exact Agent Job process accounting changed while endpoint tables were captured."
            : !jobProcessIdentitySetStable
                ? "The exact Agent Job process identity set was not stable across endpoint capture."
                : captureRaceEndpoints.Length > 0
                    ? "One or more endpoint PIDs did not retain the same Job process creation across the bracketing snapshots."
                    : endpointInventoryDifferenceCount > 0
                        ? "The exact Job-owned endpoint multiset changed between the two full inventory observations."
                        : !taskRootIdentityVerified
                            ? "The exact task-server process identity was not verified in the Agent Job."
                            : expectedListeners.Length == 0
                                ? "The expected Job-owned Minecraft TCP listener was not observed on exact loopback and the selected port."
                                : !endpointIdentitiesVerified
                                    ? "One or more owned endpoint process creation identities could not be revalidated."
                                    : !endpointsWithinTaskSubtree
                                        ? "One or more Job-owned endpoints lacked a live, generation-stable, creation-ordered ancestry chain to the exact task-server process."
                                        : unexpectedEndpoints.Length > 0
                                            ? "One or more additional Job-owned TCP or UDP endpoints were not approved for certification."
                                            : "Two full Job-owned endpoint inventories matched and contained only approved loopback Minecraft TCP listeners on the selected port.";

        return new CertificationOwnedEndpointPolicyEvaluation
        {
            InventorySucceeded = true,
            JobAccountingGenerationStable = jobAccountingGenerationStable,
            JobProcessIdentitySetStable = jobProcessIdentitySetStable,
            OwnedEndpointInventoriesStable = jobProcessIdentitySetStable &&
                                             endpointInventoryDifferenceCount == 0,
            TaskRootProcessIdentityVerified = taskRootIdentityVerified,
            OwnedEndpointIdentitySnapshotsStable = jobProcessIdentitySetStable &&
                                                   captureRaceEndpoints.Length == 0,
            OwnedEndpointProcessIdentitiesVerified = endpointIdentitiesVerified,
            OwnedEndpointsWithinTaskServerSubtree = endpointsWithinTaskSubtree,
            OwnedTcpListenersOnlyLoopback = tcpListeners.Length > 0 &&
                                            tcpListeners.All(endpoint => endpoint.IsExactLoopback),
            ExpectedMinecraftListenerCount = expectedListeners.Length,
            ObservedOwnedEndpointCount = ownedEndpoints.Length,
            ObservedOwnedTcpListenerCount = tcpListeners.Length,
            ObservedOwnedUdpEndpointCount = ownedEndpoints.Count(endpoint =>
                endpoint.Transport == WindowsOwnedEndpointTransport.Udp),
            ObservedOwnedIpv4EndpointCount = ownedEndpoints.Count(endpoint =>
                endpoint.AddressFamily == AddressFamily.InterNetwork),
            ObservedOwnedIpv6EndpointCount = ownedEndpoints.Count(endpoint =>
                endpoint.AddressFamily == AddressFamily.InterNetworkV6),
            ObservedOwnedNonLoopbackEndpointCount = ownedEndpoints.Count(endpoint =>
                !endpoint.IsExactLoopback),
            ObservedOwnedWildcardEndpointCount = ownedEndpoints.Count(endpoint => endpoint.IsWildcard),
            ObservedOwnedProcessCount = ownedEndpoints.Select(endpoint => endpoint.ProcessId).Distinct().Count(),
            CaptureRaceEndpointCount = captureRaceEndpoints.Length,
            EndpointInventoryDifferenceCount = endpointInventoryDifferenceCount,
            UnexpectedOwnedEndpointCount = unexpectedEndpoints.Length,
            Detail = detail
        };
    }

    private static bool SameProcessIdentitySet(
        IReadOnlyDictionary<int, long> left,
        IReadOnlyDictionary<int, long> right) =>
        left.Count == right.Count &&
        right.All(entry =>
            left.TryGetValue(entry.Key, out var identityLeft) &&
            ProcessCreationIdentity.Matches(identityLeft, entry.Value));

    private static int EndpointMultisetDifferenceCount(
        IReadOnlyList<WindowsOwnedNetworkEndpoint> first,
        IReadOnlyList<WindowsOwnedNetworkEndpoint> second)
    {
        var counts = new Dictionary<OwnedEndpointMultisetKey, int>();
        foreach (var endpoint in first)
        {
            var key = OwnedEndpointMultisetKey.From(endpoint);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        foreach (var endpoint in second)
        {
            var key = OwnedEndpointMultisetKey.From(endpoint);
            counts[key] = counts.GetValueOrDefault(key) - 1;
        }
        return counts.Values.Sum(count => Math.Abs(count));
    }

    private readonly record struct OwnedEndpointMultisetKey(
        WindowsOwnedEndpointTransport Transport,
        AddressFamily AddressFamily,
        string AddressBytes,
        long ScopeId,
        int Port,
        int ProcessId)
    {
        public static OwnedEndpointMultisetKey From(WindowsOwnedNetworkEndpoint endpoint) =>
            new(
                endpoint.Transport,
                endpoint.AddressFamily,
                Convert.ToHexString(endpoint.LocalAddress.GetAddressBytes()),
                endpoint.AddressFamily == AddressFamily.InterNetworkV6
                    ? endpoint.LocalAddress.ScopeId
                    : 0,
                endpoint.Port,
                endpoint.ProcessId);
    }

    private static WindowsOwnedProcessJobSnapshot? CreateSyntheticSnapshot(
        IReadOnlyDictionary<int, long>? identities)
    {
        if (identities is null)
            return null;
        var count = checked((uint)identities.Count);
        return new WindowsOwnedProcessJobSnapshot(
            new Dictionary<int, long>(identities),
            count,
            count,
            TotalTerminatedProcesses: 0);
    }
}

internal sealed record CertificationCleanupPostconditionsEvidence
{
    public bool TaskServerInactive { get; init; }
    public bool TaskServerProcessIdentityCaptured { get; init; }
    public bool TaskServerRootProcessExited { get; init; }
    public bool PortListenerAbsent { get; init; }
    public bool NoPartialArtifacts { get; init; }
    public bool NoUnsafeStagingResidue { get; init; }
    public bool ExactOwnedAgentTreeExited { get; set; }
    public bool ExactOwnedAgentExitCodeZero { get; set; }
    public bool ListenerPidOwnershipVerified { get; init; }
    public string ListenerPidOwnershipDetail { get; init; } = "Not verified";
}

internal sealed class CurseForgeRuntimeCertificationReport
{
    public string DocumentType { get; init; } =
        "ChunkPilot.CurseForgeRuntimeCertificationReport";
    public int SchemaVersion { get; init; } = 1;
    public string Phase { get; init; } = "";
    public string RunId { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public string Result { get; set; } = "FAILED";
    public string Error { get; set; } = "";
    public string CandidateGitSha { get; init; } = "";
    public string CandidateProductVersion { get; set; } = "";
    public int? AgentProcessId { get; set; }
    public bool AgentBelowNormalPriority { get; set; }
    public bool SessionRegistered { get; set; }
    public bool SafeApplicationExitAccepted { get; set; }
    public bool AgentExited { get; set; }
    public int? AgentExitCode { get; set; }
    public bool AgentRootProcessIdentityVerified { get; set; }
    public int AgentJobActiveProcessesAfterCleanup { get; set; }
    public int? BootstrapAgentProcessId { get; set; }
    public bool BootstrapAgentBelowNormalPriority { get; set; }
    public bool BootstrapSessionRegistered { get; set; }
    public bool BootstrapProtectedCredentialConfigured { get; set; }
    public bool BootstrapProviderConfigured { get; set; }
    public bool BootstrapSafeApplicationExitAccepted { get; set; }
    public bool BootstrapAgentExited { get; set; }
    public int? BootstrapAgentExitCode { get; set; }
    public bool BootstrapAgentRootProcessIdentityVerified { get; set; }
    public int BootstrapAgentJobActiveProcessesAfterCleanup { get; set; }
    public bool BootstrapCleanupSucceeded { get; set; }
    public bool DpapiRelaunchProtectedCredentialConfigured { get; set; }
    public bool DpapiRelaunchProviderConfigured { get; set; }
    public bool DpapiRelaunchAuthenticatedCatalogResolve { get; set; }
    public bool ExactOwnedTreeForceKillAttempted { get; set; }
    public bool ExactOwnedTreeForceKillSucceeded { get; set; }
    public bool CertifiedCleanupSucceeded { get; set; }
    public bool CertifiedTaskServerCleanupApplicable { get; set; }
    public bool CertifiedTaskServerCleanupSucceeded { get; set; }
    public bool CleanupSucceeded { get; set; }
    public string ProjectId { get; set; } = "";
    public string ClientFileId { get; set; } = "";
    public string ServerPackFileId { get; set; } = "";
    public string ResolvedMinecraftVersion { get; set; } = "";
    public string ResolvedLoader { get; set; } = "";
    public string ResolvedLoaderVersion { get; set; } = "";
    public bool ResolvedHasServerPackage { get; set; }
    public bool ResolvedCanGenerateServerCandidate { get; set; }
    public bool ResolvedDistributionAllowed { get; set; }
    public int ResolvedRequiredJavaMajor { get; set; }
    public long? ResolvedClientSizeBytes { get; set; }
    public long? ResolvedServerPackSizeBytes { get; set; }
    public Guid? PreflightOperationId { get; set; }
    public Guid? CreationOperationId { get; set; }
    public Guid? ServerId { get; set; }
    public long PayloadLimitBytes { get; set; } = CurseForgePayloadBudget.MaximumBytes;
    public long GuardedCumulativeCurseForgeBytes { get; set; }
    public long CompletedCumulativeCurseForgeBytes { get; set; }
    public int UnknownOrInterruptedReservationCount { get; set; }
    public long UnknownOrInterruptedReservedBytes { get; set; }
    public long ObservedIncompleteCurseForgeBytes { get; set; }
    public string CertificationInterface { get; init; } =
        "Packaged Agent named-pipe protocol";
    public bool PackagedWebUiExercised { get; init; }
    public CertificationConnectionEvidence? ConnectionTest { get; set; }
    public CertificationLoopbackBindingEvidence? LoopbackBinding { get; set; }
    public CertificationCleanupPostconditionsEvidence? CleanupPostconditions { get; set; }
    public CurseForgeFullCampaignEvidence? FullCampaign { get; set; }
    public CertificationRunRecycleEvidence FreshRunRecycle { get; set; } = new()
    {
        Outcome = "Fresh task data/server cleanup was not reached; the run remains available for review."
    };
    public List<CertificationStepEvidence> Steps { get; } = [];
    public List<CertificationPayloadEvidence> Payloads { get; } = [];
    public List<CertificationOperationStateEvidence> OperationStates { get; } = [];
    public List<CertificationLifecycleEvidence> Lifecycle { get; } = [];
    public List<string> Warnings { get; } = [];
}

internal sealed class CurseForgeRuntimeCertificationController
{
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly ICertificationRecycleBin recycleBin;
    private readonly CurseForgeRuntimeCertificationControllerOperations operations;
    private readonly ICertificationEvidenceJournal? evidenceJournal;

    public CurseForgeRuntimeCertificationController(
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ICertificationRecycleBin? recycleBin = null,
        CurseForgeRuntimeCertificationControllerOperations? operations = null,
        ICertificationEvidenceJournal? evidenceJournal = null)
    {
        this.delay = delay ?? Task.Delay;
        this.recycleBin = recycleBin ?? new SilentWindowsCertificationRecycleBin();
        this.operations = operations ?? new CurseForgeRuntimeCertificationControllerOperations();
        this.evidenceJournal = evidenceJournal;
    }

    public async Task<CurseForgeRuntimeCertificationReport> RunAsync(
        CurseForgeRuntimeCertificationOptions options,
        CancellationToken cancellationToken,
        FileStream? externallyHeldRuntimeLease = null)
    {
        var journal = evidenceJournal ?? throw new InvalidOperationException(
            "Headless certification requires a durable pending evidence journal.");
        var report = new CurseForgeRuntimeCertificationReport
        {
            Phase = options.Phase.ToString(),
            RunId = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(options.DataRoot))) ?? "",
            CandidateGitSha = options.ExpectedGitSha
        };
        FileStream? ownedRuntimeLease = null;
        var workflowCompletedThroughFinalFreshness = false;
        try
        {
            operations.ValidateOptions(options);
            if (externallyHeldRuntimeLease is null)
            {
                ownedRuntimeLease = OwnedCertificationRuntime.AcquireExclusiveLease(
                    options.RepositoryRoot, options.RuntimeRoot);
            }
            else
            {
                var expectedLease = Path.GetFullPath(Path.Combine(
                    options.RuntimeRoot, OwnedCertificationRuntime.LeaseFileName));
                if (!externallyHeldRuntimeLease.CanRead ||
                    !Path.GetFullPath(externallyHeldRuntimeLease.Name).Equals(
                        expectedLease, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "The caller did not retain the exact certification runtime lease.");
            }
            await journal.WritePendingAsync(report, CancellationToken.None).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetFullPath(options.DataRoot));
            Directory.CreateDirectory(Path.GetFullPath(options.ManagedServersRoot));
            var agentPath = Path.GetFullPath(options.AgentExecutablePath);
            report.CandidateProductVersion = FileVersionInfo.GetVersionInfo(agentPath).ProductVersion ?? "";
            var budget = new CurseForgePayloadBudget(options.PayloadLedgerPath);
            var controllerCreationTicks = ProcessCreationIdentity.OfCurrentProcess();
            if (controllerCreationTicks == ProcessCreationIdentity.Unknown)
                throw new InvalidOperationException(
                    "The controller could not prove its raw Windows process-creation identity.");

            if (operations.ExecuteAgentCampaignAsync is { } executeAgentCampaign)
            {
                await executeAgentCampaign(options, report, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunAgentLifetimeAsync(
                    options, report, budget, options.ApprovedKeyFilePath, bootstrap: true,
                    static (_, _) => Task.CompletedTask,
                    controllerCreationTicks, cancellationToken).ConfigureAwait(false);
                if (!report.BootstrapProtectedCredentialConfigured || !report.BootstrapProviderConfigured ||
                    !report.BootstrapCleanupSucceeded)
                    throw new InvalidOperationException(
                        "The credential-bootstrap Agent lifetime did not authenticate, persist, and exit cleanly.");

                var missingSentinel = OwnedCertificationRuntime.RequireOwnedDescendant(
                    options.RuntimeRoot,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DataRoot))!,
                        ".missing-curseforge-key-source"),
                    "DPAPI relaunch sentinel");
                if (File.Exists(missingSentinel) || Directory.Exists(missingSentinel))
                    throw new InvalidOperationException(
                        "The explicit DPAPI relaunch sentinel must remain nonexistent.");

                await RunAgentLifetimeAsync(
                    options, report, budget, missingSentinel, bootstrap: false,
                    (session, token) => session.ExecuteCertifiedWorkAsync(options, report, token),
                    controllerCreationTicks, cancellationToken).ConfigureAwait(false);
            }
            if (!report.BootstrapCleanupSucceeded || !report.CertifiedCleanupSucceeded ||
                report.CertifiedTaskServerCleanupApplicable &&
                !report.CertifiedTaskServerCleanupSucceeded)
                throw new InvalidOperationException(
                    "The exact Agent/server cleanup gate failed; the fresh run was retained for review.");
            var finalFreshness = Stopwatch.StartNew();
            operations.ValidateFinalPackageFreshness(options);
            finalFreshness.Stop();
            workflowCompletedThroughFinalFreshness = true;
            report.Steps.Add(new CertificationStepEvidence
            {
                Name = "post-execution package and source freshness",
                Status = "PASSED",
                Detail = "The clean repository identity and complete HEAD-bound package manifest still matched after exact process-tree exit.",
                ElapsedMilliseconds = finalFreshness.Elapsed.TotalMilliseconds
            });
            await journal.WritePendingAsync(report, CancellationToken.None).ConfigureAwait(false);
            report.FreshRunRecycle = await OwnedCertificationRunCleanup.MoveToRecycleBinAsync(
                    options, recycleBin, CancellationToken.None)
                .ConfigureAwait(false);
            if (!report.FreshRunRecycle.Success)
                throw new InvalidOperationException(report.FreshRunRecycle.Outcome);
        }
        catch (OperationCanceledException)
        {
            workflowCompletedThroughFinalFreshness = false;
            report.Error = "Certification was cancelled; exact-session cleanup was attempted.";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            workflowCompletedThroughFinalFreshness = false;
            report.Error = options.Sanitize(exception.Message);
        }
        finally
        {
            try
            {
                if (!report.FreshRunRecycle.Attempted &&
                    Directory.Exists(Path.GetFullPath(options.DataRoot)) &&
                    Directory.Exists(Path.GetFullPath(options.ManagedServersRoot)))
                {
                    var portGone = !IPGlobalProperties.GetIPGlobalProperties()
                        .GetActiveTcpListeners().Any(endpoint => endpoint.Port == options.Port);
                    if (CanRecycleFreshRunAfterStoppedJobs(report, portGone))
                    {
                        try
                        {
                            await journal.WritePendingAsync(report, CancellationToken.None)
                                .ConfigureAwait(false);
                            report.FreshRunRecycle = await OwnedCertificationRunCleanup
                                .MoveToRecycleBinAsync(
                                    options, recycleBin, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OutOfMemoryException)
                        {
                            report.FreshRunRecycle = new CertificationRunRecycleEvidence
                            {
                                Outcome = "Fresh run cleanup was refused because exact ownership or bounded inventory could not be proven."
                            };
                            report.Warnings.Add(options.Sanitize(exception.Message));
                        }
                    }
                    else
                    {
                        report.FreshRunRecycle = new CertificationRunRecycleEvidence
                        {
                            Outcome = "Fresh run retained because exact process-tree exit or selected-port disappearance was not proven."
                        };
                    }
                }
                report.CompletedAtUtc = DateTimeOffset.UtcNow;
                report.CleanupSucceeded = report.BootstrapCleanupSucceeded &&
                                          report.CertifiedCleanupSucceeded &&
                                          report.FreshRunRecycle.Success;
                report.Success = workflowCompletedThroughFinalFreshness &&
                                 string.IsNullOrWhiteSpace(report.Error) &&
                                 report.Success &&
                                 report.DpapiRelaunchAuthenticatedCatalogResolve &&
                                 report.CleanupSucceeded &&
                                 (!report.CertifiedTaskServerCleanupApplicable ||
                                  report.CertifiedTaskServerCleanupSucceeded);
                if (!report.Success && string.IsNullOrWhiteSpace(report.Error))
                    report.Error = "The headless certification or its exact-owned cleanup did not complete successfully.";
                report.Result = report.Success ? "PASSED" : cancellationToken.IsCancellationRequested ? "CANCELLED" : "FAILED";
                await journal.WritePendingAsync(report, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                ownedRuntimeLease?.Dispose();
            }
        }
        return report;
    }

    internal static bool CanRecycleFreshRunAfterStoppedJobs(
        CurseForgeRuntimeCertificationReport report,
        bool selectedPortAbsent) =>
        selectedPortAbsent &&
        (report.BootstrapAgentProcessId is null || report.BootstrapAgentExited) &&
        (report.AgentProcessId is null || report.AgentExited);

    private async Task RunAgentLifetimeAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CurseForgePayloadBudget budget,
        string credentialSourcePath,
        bool bootstrap,
        Func<CurseForgeRuntimeCertificationSession, CancellationToken, Task> execute,
        long controllerCreationTicks,
        CancellationToken cancellationToken)
    {
        HeadlessAgentProcess? agent = null;
        NamedPipeCertificationAgentTransport? transport = null;
        CurseForgeRuntimeCertificationSession? session = null;
        var forceKillAttempted = false;
        try
        {
            CertificationPackageFreshness.Validate(
                options.RepositoryRoot, options.ExpectedGitSha, options.AgentExecutablePath);
            var instanceId = "cf-cert-" + Guid.NewGuid().ToString("N");
            var updateFaultToken = bootstrap
                ? null
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            agent = HeadlessAgentProcess.Start(new HeadlessAgentLaunchOptions(
                options.AgentExecutablePath,
                options.DataRoot,
                options.ManagedServersRoot,
                options.TemporaryRoot,
                credentialSourcePath,
                instanceId,
                updateFaultToken,
                bootstrap ? null : options.RuntimeRoot));
            if (bootstrap)
            {
                report.BootstrapAgentProcessId = agent.ProcessId;
                report.BootstrapAgentBelowNormalPriority = agent.BelowNormalPriorityApplied;
            }
            else
            {
                report.AgentProcessId = agent.ProcessId;
                report.AgentBelowNormalPriority = agent.BelowNormalPriorityApplied;
            }
            if (agent.PriorityWarning.Length > 0)
                report.Warnings.Add(options.Sanitize(agent.PriorityWarning));

            transport = new NamedPipeCertificationAgentTransport(ChunkPilotConstants.PipeNameFor(instanceId));
            CertificationPackageFreshness.Validate(
                options.RepositoryRoot, options.ExpectedGitSha, options.AgentExecutablePath);
            await transport.WaitForReadyAsync(options.AgentStartupTimeout, cancellationToken)
                .ConfigureAwait(false);
            session = new CurseForgeRuntimeCertificationSession(
                transport, budget, delay, agent, updateFaultToken);
            await session.RegisterAndAuthenticateAsync(
                options,
                report,
                Environment.ProcessId,
                controllerCreationTicks,
                bootstrap,
                cancellationToken).ConfigureAwait(false);
            CertificationPackageFreshness.ValidateRepositoryIdentity(
                options.RepositoryRoot, options.ExpectedGitSha);
            await execute(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (session is not null)
            {
                await session.TryCancelActiveCreationAsync(options, report).ConfigureAwait(false);
                if (session.SessionRegistered)
                    await session.SafeExitAsync(options, report, bootstrap).ConfigureAwait(false);
                else if (transport is not null)
                    await TryShutdownUnregisteredAgentAsync(transport, options, report, bootstrap)
                        .ConfigureAwait(false);
            }
            else if (transport is not null)
            {
                await TryShutdownUnregisteredAgentAsync(transport, options, report, bootstrap)
                    .ConfigureAwait(false);
            }

            if (agent is not null)
            {
                try
                {
                    var gracefulDeadline = session is null
                    ? TimeSpan.FromSeconds(30)
                    : options.AgentExitTimeout;
                    var rootExited = await agent.WaitForExitAsync(
                    gracefulDeadline, CancellationToken.None).ConfigureAwait(false);
                    var exactTreeExited = rootExited && await agent.WaitForExactOwnedTreeExitAsync(
                    TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                    if (!exactTreeExited)
                    {
                        var termination = await agent.TryTerminateExactOwnedTreeAsync(
                        TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                        forceKillAttempted = termination.Attempted;
                        report.ExactOwnedTreeForceKillAttempted |= termination.Attempted;
                        report.ExactOwnedTreeForceKillSucceeded |= termination.Attempted && termination.Succeeded;
                        report.Warnings.Add((bootstrap ? "Bootstrap" : "Certified") +
                                        " Agent cleanup: " + options.Sanitize(termination.Detail));
                        rootExited = termination.Succeeded && agent.HasExited;
                        exactTreeExited = termination.Succeeded &&
                                      agent.ActiveExactOwnedProcessCount == 0;
                    }

                    var safeExitAccepted = bootstrap
                    ? report.BootstrapSafeApplicationExitAccepted
                    : report.SafeApplicationExitAccepted;
                    var exitCode = agent.ExitCode;
                    var rootIdentityVerified = agent.ExactRootProcessIdentityMatches;
                    var activeProcesses = agent.ActiveExactOwnedProcessCount;
                    exactTreeExited = exactTreeExited && activeProcesses == 0;
                    var clean = safeExitAccepted && rootIdentityVerified &&
                            rootExited && exactTreeExited &&
                            !forceKillAttempted && exitCode == 0;
                    if (!bootstrap && (options.Phase is CurseForgeRuntimeCertificationPhase.Official or
                        CurseForgeRuntimeCertificationPhase.Full) &&
                        report.CertifiedTaskServerCleanupApplicable)
                    {
                        if (report.CleanupPostconditions is { } postconditions)
                        {
                            postconditions.ExactOwnedAgentTreeExited = exactTreeExited;
                            postconditions.ExactOwnedAgentExitCodeZero = exitCode == 0;
                            report.CertifiedTaskServerCleanupSucceeded =
                                CurseForgeRuntimeCertificationSession.TaskServerCleanupPassed(postconditions);
                        }
                        else
                        {
                            report.CertifiedTaskServerCleanupSucceeded = false;
                        }
                    }
                    if (bootstrap)
                    {
                        report.BootstrapAgentExited = rootExited && exactTreeExited;
                        report.BootstrapAgentExitCode = exitCode;
                        report.BootstrapAgentRootProcessIdentityVerified = rootIdentityVerified;
                        report.BootstrapAgentJobActiveProcessesAfterCleanup = activeProcesses;
                        report.BootstrapCleanupSucceeded = clean;
                    }
                    else
                    {
                        report.AgentExited = rootExited && exactTreeExited;
                        report.AgentExitCode = exitCode;
                        report.AgentRootProcessIdentityVerified = rootIdentityVerified;
                        report.AgentJobActiveProcessesAfterCleanup = activeProcesses;
                        report.CertifiedCleanupSucceeded = clean;
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    report.Warnings.Add((bootstrap ? "Bootstrap" : "Certified") +
                                        " exact Job cleanup proof failed: " +
                                        options.Sanitize(exception.Message));
                    try
                    {
                        var termination = await agent.TryTerminateExactOwnedTreeAsync(
                            TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                        report.ExactOwnedTreeForceKillAttempted |= termination.Attempted;
                        report.ExactOwnedTreeForceKillSucceeded |=
                            termination.Attempted && termination.Succeeded;
                    }
                    catch (Exception terminationException) when (
                        terminationException is not OutOfMemoryException)
                    {
                        report.Warnings.Add("Bounded exact Job termination also failed: " +
                                            options.Sanitize(terminationException.Message));
                    }
                    if (bootstrap)
                    {
                        report.BootstrapAgentExited = false;
                        report.BootstrapCleanupSucceeded = false;
                    }
                    else
                    {
                        report.AgentExited = false;
                        report.CertifiedCleanupSucceeded = false;
                    }
                }
                finally
                {
                    await agent.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task TryShutdownUnregisteredAgentAsync(
        ICertificationAgentTransport transport,
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        bool bootstrap)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            _ = await transport.SendAsync<OperationResult>(
                "ShutdownAgent", cancellationToken: cleanup.Token).ConfigureAwait(false);
            report.Warnings.Add((bootstrap ? "Bootstrap" : "Certified") +
                                " Agent never registered exact session authority; headless shutdown was requested.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Warnings.Add((bootstrap ? "Bootstrap" : "Certified") +
                                " Agent could not accept unregistered headless shutdown: " +
                                options.Sanitize(exception.Message));
        }
    }
}

internal sealed record CurseForgeRuntimeCertificationControllerOperations
{
    public Action<CurseForgeRuntimeCertificationOptions> ValidateOptions { get; init; } =
        static options => options.Validate();

    public Func<CurseForgeRuntimeCertificationOptions, CurseForgeRuntimeCertificationReport,
        CancellationToken, Task>? ExecuteAgentCampaignAsync { get; init; }

    public Action<CurseForgeRuntimeCertificationOptions> ValidateFinalPackageFreshness { get; init; } =
        static options => CertificationPackageFreshness.Validate(
            options.RepositoryRoot, options.ExpectedGitSha, options.AgentExecutablePath);
}

internal sealed partial class CurseForgeRuntimeCertificationSession(
    ICertificationAgentTransport transport,
    CurseForgePayloadBudget payloadBudget,
    Func<TimeSpan, CancellationToken, Task> delay,
    HeadlessAgentProcess? exactOwnedProcesses = null,
    string? certificationUpdateFaultToken = null,
    IWindowsOwnedNetworkEndpointSource? networkEndpointSource = null)
{
    private readonly IWindowsOwnedNetworkEndpointSource ownedNetworkEndpointSource =
        networkEndpointSource ?? WindowsOwnedNetworkEndpointSource.Instance;
    private UiSessionCredential session = new();
    private Guid activeCreationOperationId;
    private bool activeCreationTerminal;
    private readonly HashSet<Guid> terminalCreationOperationIds = [];
    private readonly HashSet<Guid> terminalUpdateOperationIds = [];
    private int taskServerProcessId;
    private long taskServerProcessCreationTicks;
    private bool listenerPidOwnershipVerified;
    public bool SessionRegistered => session.IsPresent;

    public async Task ArmCertificationUpdateFailureAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid serverId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (!session.IsPresent || certificationUpdateFaultToken is not { Length: 64 } token ||
            token.Any(character => !Uri.IsHexDigit(character)) ||
            serverId == Guid.Empty || operationId == Guid.Empty)
            throw new InvalidOperationException(
                "The exact certification update-failure authority is unavailable.");
        var result = await StepAsync(
            report, options, "arm exact one-shot update rollback failure",
            async () =>
            {
                try
                {
                    return await transport.SendAsync<OperationResult>(
                        "ArmCertificationUpdateFailure",
                        new ArmCertificationUpdateFailureRequest
                        {
                            ServerId = serverId,
                            OperationId = operationId,
                            Token = token,
                            SessionId = session.SessionId,
                            SessionCapability = session.Capability
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is not (OutOfMemoryException or OperationCanceledException))
                {
                    throw new InvalidOperationException(
                        "The Agent did not accept the exact one-shot update rollback failure authority.");
                }
            },
            operationId).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException(
                "The Agent refused the exact one-shot update rollback failure authority.");
    }

    public async Task RegisterAndAuthenticateAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        int controllerProcessId,
        long controllerCreationTicks,
        bool bootstrap,
        CancellationToken cancellationToken)
    {
        var prefix = bootstrap ? "bootstrap " : "DPAPI relaunch ";
        var registration = await StepAsync(
            report, options, prefix + "exact session authority",
            () => transport.SendAsync<UiSessionRegistrationResult>(
                "RegisterUiSession",
                new UiSessionRegistrationRequest(controllerProcessId, controllerCreationTicks),
                cancellationToken)).ConfigureAwait(false);
        session = new UiSessionCredential
        {
            SessionId = registration.Session.SessionId,
            Capability = registration.SessionCapability
        };
        if (!session.IsPresent || registration.Session.ProcessId != controllerProcessId ||
            registration.Session.ProcessCreationTicks != controllerCreationTicks)
            throw new InvalidDataException("The Agent returned contradictory exact-session authority.");
        if (bootstrap)
            report.BootstrapSessionRegistered = true;
        else
            report.SessionRegistered = true;

        var credentialStatus = await StepAsync(
            report, options, prefix + "credential status",
            () => transport.SendAsync<TextResponse>("HasCurseForgeApiKey", session, cancellationToken))
            .ConfigureAwait(false);
        var credentialAvailable = credentialStatus.Value.Equals("configured", StringComparison.Ordinal);
        if (bootstrap)
            report.BootstrapProtectedCredentialConfigured = credentialAvailable;
        else
            report.DpapiRelaunchProtectedCredentialConfigured = credentialAvailable;
        if (!credentialAvailable)
            throw new InvalidOperationException(
                bootstrap
                    ? "The packaged Agent did not authenticate and protect the approved CurseForge credential."
                    : "The fresh packaged Agent did not recover the Windows-protected CurseForge credential.");

        var statuses = await StepAsync(
            report, options, prefix + "provider status",
            () => transport.SendAsync<IReadOnlyList<CatalogProviderStatus>>(
                "CatalogProviderStatuses", cancellationToken: cancellationToken)).ConfigureAwait(false);
        var providerAvailable = statuses.SingleOrDefault(status =>
                                    status.Provider == CatalogProvider.CurseForge) is { Available: true };
        if (bootstrap)
            report.BootstrapProviderConfigured = providerAvailable;
        else
            report.DpapiRelaunchProviderConfigured = providerAvailable;
        if (!providerAvailable)
            throw new InvalidOperationException(
                "CurseForge is unavailable after native credential authentication.");
    }

    public async Task ExecuteCertifiedWorkAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken)
    {
        if (options.Phase == CurseForgeRuntimeCertificationPhase.Full)
        {
            await ExecuteFullCampaignAsync(options, report, cancellationToken).ConfigureAwait(false);
            return;
        }
        var project = await ResolveExactAsync(options, report, cancellationToken).ConfigureAwait(false);
        report.DpapiRelaunchAuthenticatedCatalogResolve = true;
        var release = ExactRelease(project, options.ClientFileId);
        BindResolvedIdentity(report, project, release);

        if (options.Phase == CurseForgeRuntimeCertificationPhase.Metadata)
        {
            var snapshot = await payloadBudget.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            ApplyBudget(report, snapshot);
            report.Success = true;
            return;
        }

        if (!release.HasServerPackage || release.SizeBytes is not > 0 ||
            release.Sha1.Length != 40 || string.IsNullOrWhiteSpace(release.ServerPackFileId) ||
            release.ClientSizeBytes is not > 0 || release.ClientSha1.Length != 40)
            throw new InvalidOperationException(
                "The exact release has no integrity-verifiable official CurseForge server pack.");
        var portWatch = Stopwatch.StartNew();
        EnsurePortAvailable(options.Port);
        portWatch.Stop();
        report.Steps.Add(new CertificationStepEvidence
        {
            Name = "pre-payload TCP port ownership",
            Status = "PASSED",
            Detail = "The selected IPv4 loopback and wildcard TCP port was bind-available before payload work.",
            ElapsedMilliseconds = portWatch.Elapsed.TotalMilliseconds
        });

        var preflightOperationId = Guid.NewGuid();
        report.PreflightOperationId = preflightOperationId;
        var clientReservation = await payloadBudget.ReserveAsync(
            preflightOperationId, "client-preflight", project.ProjectId, release.ClientFileId,
            release.ClientSizeBytes.Value, release.ClientSha1, cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, clientReservation);

        var preflight = await StepAsync(
            report, options, "exact client-manifest preflight",
            () => transport.SendAsync<CurseForgeModpackPreflightResult>(
                "PreflightCurseForgeModpack",
                new CurseForgeModpackPreflightRequest(
                    preflightOperationId, project.ProjectId, release.ClientFileId, release.ServerPackFileId),
                cancellationToken),
            preflightOperationId).ConfigureAwait(false);
        ValidatePreflight(preflightOperationId, project, release, preflight);
        var clientComplete = await payloadBudget.CompleteAsync(
            preflightOperationId, "client-preflight", preflight.ClientSizeBytes,
            preflight.ClientSha256, preflight.State.ToString(), transferCompleted: true,
            cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, clientComplete);
        report.Payloads.Add(new CertificationPayloadEvidence
        {
            Kind = "client-preflight",
            OperationId = preflightOperationId,
            ProjectId = project.ProjectId,
            FileId = release.ClientFileId,
            ExpectedBytes = release.ClientSizeBytes.Value,
            DownloadedBytes = preflight.ClientSizeBytes,
            ProviderSha1Verified = true,
            LocalSha256 = preflight.ClientSha256,
            State = preflight.State.ToString()
        });
        if (preflight.State != CatalogReleasePreflightState.Ready)
            throw new InvalidOperationException(
                "The exact CurseForge client-manifest preflight did not reach Ready.");

        // Re-resolve immediately before consuming the Agent-lifetime preflight authorization and
        // require every raw provider identity to remain unchanged in memory.
        var refreshedProject = await ResolveExactAsync(options, report, cancellationToken,
            "provider identity recheck").ConfigureAwait(false);
        var refreshedRelease = ExactRelease(refreshedProject, options.ClientFileId);
        if (!SameProviderIdentity(project, release, refreshedProject, refreshedRelease))
            throw new InvalidDataException(
                "The exact CurseForge project, file, relationship, size, URL, or integrity identity changed after preflight.");
        project = refreshedProject;
        release = refreshedRelease;

        var creationOperationId = Guid.NewGuid();
        report.CreationOperationId = creationOperationId;
        activeCreationOperationId = creationOperationId;
        var serverReservation = await payloadBudget.ReserveAsync(
            creationOperationId, "official-server-pack", project.ProjectId, release.ServerPackFileId,
            preflight.ServerPackSizeBytes!.Value, preflight.ServerPackSha1, cancellationToken).ConfigureAwait(false);
        ApplyBudget(report, serverReservation);

        var plan = BuildPlan(options, creationOperationId, project, release, preflight);
        var problems = plan.Problems();
        if (problems.Count > 0)
            throw new InvalidDataException(string.Join(" ", problems));
        var started = await StepAsync(
            report, options, "begin official server-pack creation",
            () => transport.SendAsync<InstallOperationRequest>(
                "BeginModpackCreation", new BeginModpackCreationRequest(plan), cancellationToken),
            creationOperationId).ConfigureAwait(false);
        if (started.OperationId != creationOperationId)
            throw new InvalidDataException("The Agent registered creation under a different operation identity.");

        var creation = await PollCreationAsync(options, report, creationOperationId, cancellationToken)
            .ConfigureAwait(false);
        activeCreationTerminal = true;
        terminalCreationOperationIds.Add(creationOperationId);
        if (!creation.Success || creation.Result is null)
        {
            var expectedServerBytes = preflight.ServerPackSizeBytes.Value;
            var observed = Math.Min(expectedServerBytes,
                report.OperationStates.Where(item => item.OperationId == creationOperationId)
                    .Select(item => item.BytesDownloaded).DefaultIfEmpty(0).Max());
            var failedBudget = await payloadBudget.CompleteAsync(
                creationOperationId, "official-server-pack", observed, "", "Failed",
                transferCompleted: false,
                CancellationToken.None).ConfigureAwait(false);
            ApplyBudget(report, failedBudget);
            throw new InvalidOperationException("Official server-pack creation reached a failed terminal state.");
        }
        var serverComplete = await payloadBudget.CompleteAsync(
            creationOperationId, "official-server-pack", preflight.ServerPackSizeBytes.Value,
            creation.Result.Sha256, creation.Progress.State.ToString(), transferCompleted: true,
            cancellationToken)
            .ConfigureAwait(false);
        ApplyBudget(report, serverComplete);
        report.Payloads.Add(new CertificationPayloadEvidence
        {
            Kind = "official-server-pack",
            OperationId = creationOperationId,
            ProjectId = project.ProjectId,
            FileId = release.ServerPackFileId,
            ExpectedBytes = preflight.ServerPackSizeBytes.Value,
            DownloadedBytes = preflight.ServerPackSizeBytes.Value,
            ProviderSha1Verified = true,
            LocalSha256 = creation.Result.Sha256,
            State = creation.Progress.State.ToString()
        });
        var serverId = creation.Result.Definition.Id;
        report.ServerId = serverId;
        await ExerciseLifecycleAsync(options, report, serverId, cancellationToken).ConfigureAwait(false);
        _ = await VerifyCertifiedPostconditionsAsync(options, report, serverId, cancellationToken)
            .ConfigureAwait(false);
        report.Success = true;
    }

    public async Task TryCancelActiveCreationAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report)
    {
        if (activeCreationOperationId == Guid.Empty || activeCreationTerminal)
            return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            _ = await transport.SendAsync<OperationResult>(
                "CancelInstall", new InstallOperationRequest(activeCreationOperationId),
                cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Warnings.Add("Exact creation cancellation could not be confirmed: " +
                                options.Sanitize(exception.Message));
        }
    }

    private async Task<CatalogItem> ResolveExactAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        CancellationToken cancellationToken,
        string stepName = "exact project and file resolution")
    {
        var project = await StepAsync(
            report, options, stepName,
            () => transport.SendAsync<CatalogItem?>(
                "ResolveCatalogProject",
                new CatalogProjectRequest(
                    CatalogProvider.CurseForge, options.ProjectReference, options.ClientFileId),
                cancellationToken)).ConfigureAwait(false);
        return project is { Provider: CatalogProvider.CurseForge }
            ? project
            : throw new InvalidOperationException("The exact CurseForge project/file could not be resolved.");
    }

    private async Task<InstallOperationSnapshot> PollCreationAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid operationId,
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
                throw new InvalidDataException("InstallProgress returned a different operation identity.");
            var evidence = new CertificationOperationStateEvidence(
                snapshot.OperationId,
                snapshot.Revision,
                snapshot.Progress.State.ToString(),
                snapshot.Progress.Stage.ToString(),
                options.Sanitize(snapshot.Progress.CurrentStep),
                snapshot.Progress.BytesDownloaded,
                snapshot.Progress.TotalBytes,
                snapshot.IsTerminal,
                snapshot.Success);
            if (previous != evidence && report.OperationStates.Count < 256)
            {
                report.OperationStates.Add(evidence);
                previous = evidence;
            }
            if (snapshot.IsTerminal)
            {
                watch.Stop();
                report.Steps.Add(new CertificationStepEvidence
                {
                    Name = "official server-pack creation poll",
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
        throw new TimeoutException("The exact creation operation did not become terminal before its deadline.");
    }

    private async Task ExerciseLifecycleAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        report.ServerId = serverId;
        report.CleanupPostconditions = null;
        report.CertifiedTaskServerCleanupApplicable = true;
        report.CertifiedTaskServerCleanupSucceeded = false;
        var startWatch = Stopwatch.StartNew();
        var started = await transport.SendAsync<OperationResult>(
            "Start",
            new ServerIdRequest(serverId)
            {
                Session = session,
                ConnectivityOperation = PublicConnectivityOperation.StartServer
            }, cancellationToken).ConfigureAwait(false);
        var running = await WaitForServerStateAsync(
            options, serverId, ServerState.Running, cancellationToken).ConfigureAwait(false);
        startWatch.Stop();
        var startPassed = started.Success && running.LastStartReachedReadiness;
        report.Lifecycle.Add(new CertificationLifecycleEvidence(
            "Start", running.State.ToString(), startPassed,
            options.Sanitize(started.Message), startWatch.Elapsed.TotalMilliseconds));
        if (!startPassed)
            throw new InvalidOperationException(
                "The created server did not reach its configured readiness signal.");
        CaptureTaskServerProcessIdentity(running);
        await VerifyLoopbackBindingAsync(options, report, running.Definition, cancellationToken)
            .ConfigureAwait(false);

        var queryWatch = Stopwatch.StartNew();
        var connection = await transport.SendAsync<ConnectionTestResult>(
            "ConnectionTest", new ConnectionTestRequest(serverId, IncludeExternalProbe: false),
            cancellationToken).ConfigureAwait(false);
        queryWatch.Stop();
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
        report.Lifecycle.Add(new CertificationLifecycleEvidence(
            "Local Minecraft status query", running.State.ToString(), queryPassed,
            queryPassed ? "Loopback TCP and Minecraft status response were confirmed; no external probe ran."
                : "The local-only connection test did not confirm every required signal.",
            queryWatch.Elapsed.TotalMilliseconds));
        if (!queryPassed)
            throw new InvalidOperationException(
                "The local-only Minecraft status query did not confirm the running server.");

        var stopWatch = Stopwatch.StartNew();
        var stoppedResult = await transport.SendAsync<OperationResult>(
            "Stop",
            new StopRequest(serverId, SaveFirst: true)
            {
                Session = session,
                ConnectivityOperation = PublicConnectivityOperation.StopServer
            }, cancellationToken).ConfigureAwait(false);
        var stopped = await WaitForServerStateAsync(
            options, serverId, ServerState.Stopped, cancellationToken).ConfigureAwait(false);
        stopWatch.Stop();
        report.Lifecycle.Add(new CertificationLifecycleEvidence(
            "Stop", stopped.State.ToString(), stoppedResult.Success,
            options.Sanitize(stoppedResult.Message), stopWatch.Elapsed.TotalMilliseconds));
        if (!stoppedResult.Success)
            throw new InvalidOperationException("The created server did not stop cleanly.");
    }

    private async Task VerifyLoopbackBindingAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        ServerDefinition definition,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var taskOwnedPath = false;
        var exactIp = false;
        var exactPort = false;
        var endpointPolicy = new CertificationOwnedEndpointPolicyEvaluation();
        try
        {
            var root = OwnedCertificationRuntime.RequireOwnedDescendant(
                options.ManagedServersRoot, definition.RootPath, "created server root");
            var propertiesPath = OwnedCertificationRuntime.RequireOwnedDescendant(
                root, Path.Combine(root, "server.properties"), "created server properties file");
            taskOwnedPath = true;
            if (!File.Exists(propertiesPath))
                throw new FileNotFoundException(
                    "The created server did not produce its task-owned server.properties file.");
            var info = new FileInfo(propertiesPath);
            if (info.Length is < 1 or > 1024 * 1024)
                throw new InvalidDataException(
                    "The created server.properties file has an invalid bounded size.");
            var document = ServerPropertiesDocument.Parse(
                await File.ReadAllTextAsync(propertiesPath, cancellationToken).ConfigureAwait(false));
            exactIp = document.Get("server-ip")?.Equals("127.0.0.1", StringComparison.Ordinal) == true;
            exactPort = int.TryParse(document.Get("server-port"), out var configuredPort) &&
                        configuredPort == options.Port && definition.Port == options.Port;
            Func<int, long, bool>? identityStillMatches = exactOwnedProcesses is null
                ? null
                : exactOwnedProcesses.ExactOwnedProcessIdentityStillMatches;
            endpointPolicy = CertificationOwnedEndpointPolicy.CaptureAndEvaluate(
                ownedNetworkEndpointSource,
                options.Port,
                taskServerProcessId,
                taskServerProcessCreationTicks,
                () => exactOwnedProcesses?.CaptureExactOwnedProcessSnapshot(),
                WindowsProcessParents.Capture,
                identityStillMatches);
            listenerPidOwnershipVerified = endpointPolicy.OwnershipVerified;
            report.LoopbackBinding = CreateLoopbackBindingEvidence(
                options.Port, taskOwnedPath, exactIp, exactPort, endpointPolicy);
            if (!exactIp || !exactPort || !endpointPolicy.Passed)
                throw new InvalidOperationException(
                    "The created server did not prove its exact approved Job-owned endpoint policy.");
            watch.Stop();
            report.Steps.Add(new CertificationStepEvidence
            {
                Name = "complete owned server endpoint policy",
                Status = "PASSED",
                Detail = endpointPolicy.Detail,
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
            });
        }
        catch
        {
            watch.Stop();
            report.LoopbackBinding ??= CreateLoopbackBindingEvidence(
                options.Port, taskOwnedPath, exactIp, exactPort, endpointPolicy);
            report.Steps.Add(new CertificationStepEvidence
            {
                Name = "complete owned server endpoint policy",
                Status = "FAILED",
                Detail = endpointPolicy.Detail,
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
            });
            throw;
        }
    }

    private static CertificationLoopbackBindingEvidence CreateLoopbackBindingEvidence(
        int configuredPort,
        bool taskOwnedPath,
        bool exactIp,
        bool exactPort,
        CertificationOwnedEndpointPolicyEvaluation endpointPolicy) =>
        new()
        {
            ConfiguredPort = configuredPort,
            TaskOwnedPropertiesPath = taskOwnedPath,
            ServerIpIsExactLoopback = exactIp,
            ServerPortMatches = exactPort,
            TcpListenerObserved = endpointPolicy.ExpectedMinecraftListenerCount > 0,
            TcpListenersOnlyLoopback = endpointPolicy.OwnedTcpListenersOnlyLoopback,
            ObservedListenerCount = endpointPolicy.ObservedOwnedTcpListenerCount,
            ObservedListenerOwnerCount = endpointPolicy.ObservedOwnedProcessCount,
            ListenerPidOwnershipVerified = endpointPolicy.OwnershipVerified,
            OwnedEndpointInventorySucceeded = endpointPolicy.InventorySucceeded,
            JobAccountingGenerationStable = endpointPolicy.JobAccountingGenerationStable,
            JobProcessIdentitySetStable = endpointPolicy.JobProcessIdentitySetStable,
            OwnedEndpointInventoriesStable = endpointPolicy.OwnedEndpointInventoriesStable,
            TaskRootProcessIdentityVerified = endpointPolicy.TaskRootProcessIdentityVerified,
            OwnedEndpointIdentitySnapshotsStable =
                endpointPolicy.OwnedEndpointIdentitySnapshotsStable,
            OwnedEndpointProcessIdentitiesVerified =
                endpointPolicy.OwnedEndpointProcessIdentitiesVerified,
            OwnedEndpointsWithinTaskServerSubtree =
                endpointPolicy.OwnedEndpointsWithinTaskServerSubtree,
            NoUnexpectedOwnedEndpoints = endpointPolicy.InventorySucceeded &&
                                         endpointPolicy.JobAccountingGenerationStable &&
                                         endpointPolicy.JobProcessIdentitySetStable &&
                                         endpointPolicy.OwnedEndpointInventoriesStable &&
                                         endpointPolicy.OwnedEndpointIdentitySnapshotsStable &&
                                         endpointPolicy.UnexpectedOwnedEndpointCount == 0,
            ExpectedMinecraftListenerCount = endpointPolicy.ExpectedMinecraftListenerCount,
            ObservedOwnedEndpointCount = endpointPolicy.ObservedOwnedEndpointCount,
            ObservedOwnedTcpListenerCount = endpointPolicy.ObservedOwnedTcpListenerCount,
            ObservedOwnedUdpEndpointCount = endpointPolicy.ObservedOwnedUdpEndpointCount,
            ObservedOwnedIpv4EndpointCount = endpointPolicy.ObservedOwnedIpv4EndpointCount,
            ObservedOwnedIpv6EndpointCount = endpointPolicy.ObservedOwnedIpv6EndpointCount,
            ObservedOwnedNonLoopbackEndpointCount =
                endpointPolicy.ObservedOwnedNonLoopbackEndpointCount,
            ObservedOwnedWildcardEndpointCount = endpointPolicy.ObservedOwnedWildcardEndpointCount,
            CaptureRaceEndpointCount = endpointPolicy.CaptureRaceEndpointCount,
            EndpointInventoryDifferenceCount = endpointPolicy.EndpointInventoryDifferenceCount,
            UnexpectedOwnedEndpointCount = endpointPolicy.UnexpectedOwnedEndpointCount,
            EndpointPolicyPassed = endpointPolicy.Passed,
            EndpointPolicyDetail = endpointPolicy.Detail
        };

    private void CaptureTaskServerProcessIdentity(ServerSnapshot running)
    {
        if (running.RootProcessId is not > 0)
            throw new InvalidDataException(
                "The authoritative running snapshot did not expose the task server root process.");
        try
        {
            using var process = Process.GetProcessById(running.RootProcessId.Value);
            var creationTicks = ProcessCreationIdentity.Of(process.SafeHandle);
            if (creationTicks == ProcessCreationIdentity.Unknown)
                throw new InvalidDataException(
                    "The task server root process creation identity could not be proven.");
            taskServerProcessId = running.RootProcessId.Value;
            taskServerProcessCreationTicks = creationTicks;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "The task server root process ended before exact identity capture.", exception);
        }
    }

    private async Task<CertificationCleanupPostconditionsEvidence> VerifyCertifiedPostconditionsAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        Guid serverId,
        CancellationToken cancellationToken)
    {
        const int maximumEntries = 100_000;
        var watch = Stopwatch.StartNew();
        var dashboard = await transport.SendAsync<DashboardSnapshot>(
            "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
        var exact = dashboard.Servers.SingleOrDefault(server => server.Definition.Id == serverId)
                    ?? throw new InvalidDataException(
                        "The exact task server disappeared before cleanup postconditions were checked.");
        var inactive = exact.State == ServerState.Stopped;
        var processCaptured = taskServerProcessId > 0 &&
                              taskServerProcessCreationTicks != ProcessCreationIdentity.Unknown;
        var processExited = processCaptured && ExactTaskProcessHasExited();

        var portExitWatch = Stopwatch.StartNew();
        while (PortHasListener(options.Port) &&
               portExitWatch.Elapsed < TimeSpan.FromSeconds(30))
            await delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        var portAbsent = !PortHasListener(options.Port);

        var runtime = Path.GetFullPath(options.RuntimeRoot);
        var runRoot = Path.GetDirectoryName(Path.GetFullPath(options.DataRoot))
                      ?? throw new InvalidOperationException(
                          "The exact fresh run root is unavailable for cleanup proof.");
        _ = OwnedCertificationRuntime.RequireOwnedDescendant(
            runtime, runRoot, "cleanup-postcondition run root");
        var entries = BoundedNoFollowFileTree.Inventory(
            runRoot, maximumEntries, cancellationToken);
        var partials = entries.Where(entry => !entry.IsDirectory).Where(entry =>
        {
            var name = Path.GetFileName(entry.Path);
            return name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains(".partial-", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains(".partial.", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        var transactionStaging = entries.Where(entry => entry.IsDirectory).Where(entry =>
        {
            var name = Path.GetFileName(entry.Path);
            return name.StartsWith(".chunkpilot-staging-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(".staging-", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith(".chunkpilot-world-import-", StringComparison.OrdinalIgnoreCase) ||
                   name.Contains(".mrpack-staging-", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        var canonicalStaging = Path.Combine(options.DataRoot, "Staging");
        var noUnsafeStagingResidue = transactionStaging.Length == 0 &&
                                     CanonicalStagingContainsOnlyTerminalLogs(
                                         canonicalStaging, terminalCreationOperationIds,
                                         terminalUpdateOperationIds);

        var postconditions = new CertificationCleanupPostconditionsEvidence
        {
            TaskServerInactive = inactive,
            TaskServerProcessIdentityCaptured = processCaptured,
            TaskServerRootProcessExited = processExited,
            PortListenerAbsent = portAbsent,
            NoPartialArtifacts = partials.Length == 0,
            NoUnsafeStagingResidue = noUnsafeStagingResidue,
            ListenerPidOwnershipVerified = listenerPidOwnershipVerified,
            ListenerPidOwnershipDetail = listenerPidOwnershipVerified
                ? "Every observed Job-owned TCP or UDP endpoint PID matched a live captured process identity in the exact task-server subtree."
                : "Complete owned TCP and UDP endpoint process identity was not proven."
        };
        report.CleanupPostconditions = postconditions;
        var passed = inactive && processCaptured && processExited && portAbsent &&
                     partials.Length == 0 && noUnsafeStagingResidue &&
                     listenerPidOwnershipVerified;
        report.CertifiedTaskServerCleanupSucceeded = passed;
        watch.Stop();
        report.Steps.Add(new CertificationStepEvidence
        {
            Name = "certified server cleanup postconditions",
            Status = passed ? "PASSED" : "FAILED",
            Detail = passed
                ? "The exact task server stopped, its process and listener disappeared, and staging contained no unsafe residue beyond its bounded terminal journal."
                : "One or more exact task-server cleanup postconditions were not proven.",
            ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
        });
        if (!passed)
            throw new InvalidOperationException(
                "The exact task server did not satisfy every bounded cleanup postcondition.");
        return postconditions;
    }

    internal static bool TaskServerCleanupPassed(
        CertificationCleanupPostconditionsEvidence? postconditions) => postconditions is
    {
        TaskServerInactive: true,
        TaskServerProcessIdentityCaptured: true,
        TaskServerRootProcessExited: true,
        PortListenerAbsent: true,
        NoPartialArtifacts: true,
        NoUnsafeStagingResidue: true,
        ListenerPidOwnershipVerified: true,
        ExactOwnedAgentTreeExited: true,
        ExactOwnedAgentExitCodeZero: true
    };

    private static bool CanonicalStagingContainsOnlyTerminalLogs(
        string canonicalStaging,
        IReadOnlySet<Guid> creationOperationIds,
        IReadOnlySet<Guid> updateOperationIds)
    {
        if (!Directory.Exists(canonicalStaging))
            return true;
        var entries = Directory.EnumerateFileSystemEntries(canonicalStaging).ToArray();
        if (entries.Length == 0)
            return true;
        if (creationOperationIds.Count + updateOperationIds.Count == 0 ||
            entries.Length > creationOperationIds.Count + updateOperationIds.Count)
            return false;
        var expectedCreations = creationOperationIds.Select(operationId => $"{operationId:N}.log")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedUpdates = updateOperationIds.Select(operationId => $"update-{operationId:N}.log")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var terminalLog in entries)
        {
            var name = Path.GetFileName(terminalLog);
            var creationLog = expectedCreations.Contains(name);
            if (!creationLog && !expectedUpdates.Contains(name) || !File.Exists(terminalLog))
                return false;
            var info = new FileInfo(terminalLog);
            if (info.Length is < 1 or > 16 * 1024 * 1024 ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;
            var lastLine = File.ReadLines(terminalLog)
                .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
            if (string.IsNullOrWhiteSpace(lastLine) ||
                creationLog && !lastLine.StartsWith("Completed:", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private bool ExactTaskProcessHasExited()
    {
        try
        {
            using var process = Process.GetProcessById(taskServerProcessId);
            var observed = ProcessCreationIdentity.Of(process.SafeHandle);
            return !ProcessCreationIdentity.Matches(observed, taskServerProcessCreationTicks) ||
                   process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static bool PortHasListener(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port);

    private async Task<ServerSnapshot> WaitForServerStateAsync(
        CurseForgeRuntimeCertificationOptions options,
        Guid serverId,
        ServerState expected,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < options.LifecycleTimeout)
        {
            var dashboard = await transport.SendAsync<DashboardSnapshot>(
                "Dashboard", cancellationToken: cancellationToken).ConfigureAwait(false);
            var server = dashboard.Servers.SingleOrDefault(item => item.Definition.Id == serverId)
                         ?? throw new InvalidDataException(
                             "The exact created server disappeared from the authoritative dashboard.");
            if (server.State == expected)
                return server;
            if (server.State is ServerState.Crashed or ServerState.Unresponsive)
                throw new InvalidOperationException(
                    $"The exact created server entered {server.State} while waiting for {expected}. " +
                    options.Sanitize(server.LastError));
            await delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException($"The exact created server did not reach {expected} before its deadline.");
    }

    public async Task SafeExitAsync(
        CurseForgeRuntimeCertificationOptions options,
        CurseForgeRuntimeCertificationReport report,
        bool bootstrap)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            IReadOnlyList<Guid> runningIds = [];
            try
            {
                var dashboard = await transport.SendAsync<DashboardSnapshot>(
                    "Dashboard", cancellationToken: cleanup.Token).ConfigureAwait(false);
                runningIds = dashboard.Servers
                    .Where(server => server.State is not ServerState.Stopped and not ServerState.Crashed)
                    .Select(server => server.Definition.Id).ToArray();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                report.Warnings.Add("The final dashboard snapshot was unavailable; the Agent will derive running IDs itself. " +
                                    options.Sanitize(exception.Message));
            }
            var result = await transport.SendAsync<OperationResult>(
                "SafeApplicationExit",
                new SafeApplicationExitRequest(session.SessionId, runningIds, DateTimeOffset.UtcNow)
                {
                    SessionCapability = session.Capability
                }, cleanup.Token).ConfigureAwait(false);
            if (bootstrap)
                report.BootstrapSafeApplicationExitAccepted = result.Success;
            else
                report.SafeApplicationExitAccepted = result.Success;
            report.Steps.Add(new CertificationStepEvidence
            {
                Name = (bootstrap ? "bootstrap " : "certified ") + "safe application exit",
                Status = result.Success ? "PASSED" : "FAILED",
                Detail = options.Sanitize(result.Message)
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            report.Warnings.Add("SafeApplicationExit was not confirmed: " + options.Sanitize(exception.Message));
        }
    }

    private static CatalogVersion ExactRelease(CatalogItem project, string clientFileId) =>
        project.Versions.SingleOrDefault(version =>
            version.VersionId.Equals(clientFileId, StringComparison.Ordinal) &&
            version.ClientFileId.Equals(clientFileId, StringComparison.Ordinal))
        ?? throw new InvalidDataException("The provider response did not contain the exact requested client file.");

    internal static void BindResolvedIdentity(
        CurseForgeRuntimeCertificationReport report,
        CatalogItem project,
        CatalogVersion release)
    {
        if (!long.TryParse(project.ProjectId, out var projectId) || projectId <= 0 ||
            !long.TryParse(release.ClientFileId, out var clientFileId) || clientFileId <= 0)
            throw new InvalidDataException(
                "CurseForge did not return exact positive numeric project and client-file IDs.");
        var hasServerFileId = long.TryParse(
            release.ServerPackFileId,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var serverPackFileId) && serverPackFileId > 0;
        if (release.HasServerPackage != hasServerFileId)
            throw new InvalidDataException(
                "CurseForge returned contradictory server-package identity evidence.");
        report.ProjectId = project.ProjectId;
        report.ClientFileId = release.ClientFileId;
        report.ServerPackFileId = hasServerFileId ? release.ServerPackFileId : "";
        report.ResolvedMinecraftVersion = release.MinecraftVersion;
        report.ResolvedLoader = release.Loader;
        report.ResolvedLoaderVersion = release.LoaderVersion;
        report.ResolvedHasServerPackage = release.HasServerPackage;
        report.ResolvedCanGenerateServerCandidate = release.CanGenerateServerCandidate;
        report.ResolvedDistributionAllowed = release.DistributionAllowed;
        report.ResolvedRequiredJavaMajor = release.RequiredJavaMajor;
        report.ResolvedClientSizeBytes = release.ClientSizeBytes;
        report.ResolvedServerPackSizeBytes = release.SizeBytes;
    }

    internal static void ValidatePreflight(
        Guid expectedOperationId,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult result)
    {
        if (result.OperationId != expectedOperationId ||
            !result.ProjectId.Equals(project.ProjectId, StringComparison.Ordinal) ||
            !result.ClientFileId.Equals(release.ClientFileId, StringComparison.Ordinal) ||
            !result.ServerPackFileId.Equals(release.ServerPackFileId, StringComparison.Ordinal) ||
            !result.ClientSha1.Equals(release.ClientSha1, StringComparison.OrdinalIgnoreCase) ||
            result.ClientSizeBytes != release.ClientSizeBytes ||
            result.ClientSha256.Length != 64 || result.ClientSha256.Any(character => !Uri.IsHexDigit(character)) ||
            !Uri.TryCreate(result.ClientDownloadUrl, UriKind.Absolute, out var clientUri) ||
            !CurseForgeApiClient.IsApprovedDownloadUri(clientUri) ||
            !result.ClientDownloadUrl.Equals(release.ClientDownloadUrl, StringComparison.Ordinal) ||
            !result.ServerPackSha1.Equals(release.Sha1, StringComparison.OrdinalIgnoreCase) ||
            result.ServerPackSizeBytes != release.SizeBytes ||
            !Uri.TryCreate(result.ServerPackDownloadUrl, UriKind.Absolute, out var serverPackUri) ||
            !CurseForgeApiClient.IsApprovedDownloadUri(serverPackUri) ||
            !result.ServerPackDownloadUrl.Equals(release.DownloadUrl, StringComparison.Ordinal))
            throw new InvalidDataException(
                "CurseForge preflight returned an operation, identity, size, URL, or integrity value that did not match the exact release.");
    }

    private static bool SameProviderIdentity(
        CatalogItem firstProject,
        CatalogVersion first,
        CatalogItem secondProject,
        CatalogVersion second) =>
        firstProject.ProjectId.Equals(secondProject.ProjectId, StringComparison.Ordinal) &&
        first.ClientFileId.Equals(second.ClientFileId, StringComparison.Ordinal) &&
        first.ServerPackFileId.Equals(second.ServerPackFileId, StringComparison.Ordinal) &&
        first.ClientDownloadUrl.Equals(second.ClientDownloadUrl, StringComparison.Ordinal) &&
        first.DownloadUrl.Equals(second.DownloadUrl, StringComparison.Ordinal) &&
        first.ClientSha1.Equals(second.ClientSha1, StringComparison.OrdinalIgnoreCase) &&
        first.Sha1.Equals(second.Sha1, StringComparison.OrdinalIgnoreCase) &&
        first.ClientSizeBytes == second.ClientSizeBytes &&
        first.SizeBytes == second.SizeBytes &&
        second.HasServerPackage;

    internal static ModpackCreationPlan BuildPlan(
        CurseForgeRuntimeCertificationOptions options,
        Guid operationId,
        CatalogItem project,
        CatalogVersion release,
        CurseForgeModpackPreflightResult preflight) => new()
        {
            OperationId = operationId,
            SourceKind = ModpackCreationSource.CurseForgeOfficialServerPack,
            Source = preflight.ServerPackDownloadUrl,
            Provider = UpdateProvider.CurseForge,
            ProjectId = project.ProjectId,
            ProjectSlug = project.Slug,
            ProjectName = project.Name,
            VersionId = release.VersionId,
            ServerPackFileId = release.ServerPackFileId,
            VersionName = release.VersionName,
            ReleaseChannel = release.ReleaseChannel,
            MinecraftVersion = preflight.MinecraftVersion,
            Loader = preflight.Loader,
            LoaderVersion = preflight.LoaderVersion,
            RequiredJavaMajor = preflight.RequiredJavaMajor,
            ExpectedSha1 = preflight.ServerPackSha1,
            ExpectedSizeBytes = preflight.ServerPackSizeBytes,
            VerifiedClientArchiveSha256 = preflight.ClientSha256,
            PreflightOperationId = preflight.OperationId,
            ServerName = options.ServerName,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            MinimumRamMb = options.MinimumRamMb,
            MaximumRamMb = options.MaximumRamMb,
            Port = options.Port,
            MaxPlayers = options.MaxPlayers,
            InstanceRoot = Path.GetFullPath(options.ManagedServersRoot),
            NetworkingPreference = VanillaNetworkingPreference.ThisComputerOnly,
            ExperimentalRuntimeRiskAccepted = true
        };

    internal static void EnsurePortAvailable(int port)
    {
        if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Any(endpoint => endpoint.Port == port))
            throw new InvalidOperationException(
                "The selected certification port is already owned by a listening process; payload work was refused.");

        BindProbe(IPAddress.Loopback, port);
        BindProbe(IPAddress.Any, port);

        static void BindProbe(IPAddress address, int value)
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    ExclusiveAddressUse = true
                };
                socket.Bind(new IPEndPoint(address, value));
            }
            catch (SocketException exception)
            {
                throw new InvalidOperationException(
                    "The selected certification port cannot be bound exclusively; payload work was refused.",
                    exception);
            }
        }
    }

    private static void ApplyBudget(
        CurseForgeRuntimeCertificationReport report,
        CurseForgePayloadBudgetSnapshot snapshot)
    {
        report.PayloadLimitBytes = snapshot.LimitBytes;
        report.GuardedCumulativeCurseForgeBytes = snapshot.GuardedCumulativeBytes;
        report.CompletedCumulativeCurseForgeBytes = snapshot.CompletedCumulativeBytes;
        report.UnknownOrInterruptedReservationCount = snapshot.UnknownOrInterruptedReservationCount;
        report.UnknownOrInterruptedReservedBytes = snapshot.UnknownOrInterruptedReservedBytes;
        report.ObservedIncompleteCurseForgeBytes = snapshot.ObservedIncompleteBytes;
    }

    private static async Task<T> StepAsync<T>(
        CurseForgeRuntimeCertificationReport report,
        CurseForgeRuntimeCertificationOptions options,
        string name,
        Func<Task<T>> action,
        Guid? operationId = null)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var result = await action().ConfigureAwait(false);
            watch.Stop();
            report.Steps.Add(new CertificationStepEvidence
            {
                Name = name,
                Status = "PASSED",
                Detail = "Completed through the packaged Agent named-pipe contract.",
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                OperationId = operationId
            });
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            watch.Stop();
            report.Steps.Add(new CertificationStepEvidence
            {
                Name = name,
                Status = "FAILED",
                Detail = options.Sanitize(exception.Message),
                ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                OperationId = operationId
            });
            throw;
        }
    }
}
