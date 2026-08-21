namespace ChunkPilot.Core;

public enum ServerImportSourceKind
{
    ModrinthPack,
    CurseForgePack,
    ServerArchive,
    ServerJar,
    ServerFolder
}

public enum ServerImportManagementMode
{
    ManagedCopy,
    ByReference
}

public sealed record ServerImportInspectRequest(string NativePath);

/// <summary>
/// A path-free review of one native file or folder selected by the user. Native paths stay between
/// the App and Agent and are represented in WebUI by a short-lived, single-use token.
/// </summary>
public sealed record ServerImportInspection
{
    public ServerImportSourceKind SourceKind { get; init; }
    public string DisplayName { get; init; } = "";
    public string Platform { get; init; } = "Unknown";
    public string MinecraftVersion { get; init; } = "Unknown";
    public string LoaderVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public long SourceSizeBytes { get; init; }
    public long ExpandedSizeBytes { get; init; }
    public int FileCount { get; init; }
    public int ModCount { get; init; }
    public int PluginCount { get; init; }
    public bool ContainsWorld { get; init; }
    public string ServerRoot { get; init; } = ".";
    public IReadOnlyList<string> LaunchCandidates { get; init; } = [];
    public string Sha256 { get; init; } = "";
    public bool CanInstall { get; init; }
    public bool CanReference { get; init; }
    public string Limitation { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record ServerImportCreationPlan
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public string NativePath { get; init; } = "";
    public ServerImportInspection Inspection { get; init; } = new();
    public ServerImportManagementMode ManagementMode { get; init; } = ServerImportManagementMode.ManagedCopy;
    public string LaunchRelativePath { get; init; } = "";
    public string ServerName { get; init; } = "";
    public VanillaEulaAcceptance Eula { get; init; } = new();
    public int MinimumRamMb { get; init; } = 1_024;
    public int MaximumRamMb { get; init; } = 4_096;
    public int Port { get; init; } = ServerPortPolicy.DefaultPort;
    public int MaxPlayers { get; init; } = 10;
    public VanillaNetworkingPreference NetworkingPreference { get; init; } = VanillaNetworkingPreference.DecideLater;
    public string InstanceRoot { get; init; } = "";

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (OperationId == Guid.Empty) problems.Add("The import operation has no identity.");
        if (string.IsNullOrWhiteSpace(NativePath)) problems.Add("No local server source was selected.");
        if (string.IsNullOrWhiteSpace(ServerName)) problems.Add("The server has no name.");
        if (!Inspection.CanInstall) problems.Add(string.IsNullOrWhiteSpace(Inspection.Limitation)
            ? "The selected source does not have a complete managed-server path." : Inspection.Limitation);
        if (ManagementMode == ServerImportManagementMode.ByReference && !Inspection.CanReference)
            problems.Add("Only a complete selected server folder can be managed by reference.");
        if (Inspection.LaunchCandidates.Count > 1 && string.IsNullOrWhiteSpace(LaunchRelativePath))
            problems.Add("Choose which reviewed server launcher ChunkPilot should use.");
        if (!string.IsNullOrWhiteSpace(LaunchRelativePath) &&
            !Inspection.LaunchCandidates.Contains(LaunchRelativePath, StringComparer.OrdinalIgnoreCase))
            problems.Add("The selected launcher is not part of the reviewed source.");
        if (!Eula.IsAuthorised) problems.Add("The Minecraft EULA was not accepted.");
        var memory = MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb);
        if (memory is not null) problems.Add(memory);
        var port = ServerPortPolicy.Validate(Port);
        if (port is not null) problems.Add(port);
        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record BeginServerImportRequest(ServerImportCreationPlan Plan);
public sealed record ServerImportOperationsResult(IReadOnlyList<InstallOperationSnapshot> Operations);
