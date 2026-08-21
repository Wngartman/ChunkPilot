namespace ChunkPilot.Core;

public enum ModpackCreationSource
{
    Modrinth,
    LocalMrpack
}

/// <summary>
/// One exact, reviewed Modrinth-format server-pack selection. Provider identity belongs to the
/// outer catalog selection and is intentionally separate from modrinth.index.json, which does not
/// contain a Modrinth API project ID.
/// </summary>
public sealed record ModpackCreationPlan
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public ModpackCreationSource SourceKind { get; init; }
    public string Source { get; init; } = "";
    public UpdateProvider Provider { get; init; }
    public string ProjectId { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string VersionName { get; init; } = "";
    public ReleaseChannel ReleaseChannel { get; init; } = ReleaseChannel.Stable;
    public string MinecraftVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public string ExpectedSha1 { get; init; } = "";
    public string ExpectedSha512 { get; init; } = "";
    public long? ExpectedSizeBytes { get; init; }
    public string ServerName { get; init; } = "";
    public VanillaEulaAcceptance Eula { get; init; } = new();
    public int MaxPlayers { get; init; } = 10;
    public int MinimumRamMb { get; init; } = 2_048;
    public int MaximumRamMb { get; init; } = 6_144;
    public int Port { get; init; } = ServerPortPolicy.DefaultPort;
    public VanillaNetworkingPreference NetworkingPreference { get; init; } =
        VanillaNetworkingPreference.HomeNetwork;
    public string InstanceRoot { get; init; } = "";
    public bool ExperimentalRuntimeRiskAccepted { get; init; }
    public CreationWorldSource? InitialWorld { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(ServerName))
            problems.Add("The server has no name.");
        if (string.IsNullOrWhiteSpace(Source))
            problems.Add("No Modrinth pack archive was selected.");
        if (SourceKind == ModpackCreationSource.Modrinth)
        {
            if (Provider != UpdateProvider.Modrinth || string.IsNullOrWhiteSpace(ProjectId) ||
                string.IsNullOrWhiteSpace(VersionId))
                problems.Add("The exact Modrinth project and release identity is incomplete.");
            if (!Uri.TryCreate(Source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase))
                problems.Add("The selected Modrinth release does not use the trusted Modrinth CDN.");
            if (ExpectedSizeBytes is null or <= 0 || string.IsNullOrWhiteSpace(ExpectedSha1) ||
                string.IsNullOrWhiteSpace(ExpectedSha512))
                problems.Add("The selected Modrinth release has incomplete integrity metadata.");
        }
        else
        {
            if (Provider != UpdateProvider.LocalPackageHistory)
                problems.Add("A local pack must use local package-history provenance.");
            if (ExpectedSizeBytes is null or <= 0 || ExpectedSha512.Length != 128)
                problems.Add("The selected local pack is not bound to its inspected archive identity.");
        }
        if (string.IsNullOrWhiteSpace(MinecraftVersion) || RequiredJavaMajor <= 0)
            problems.Add("The pack's exact Minecraft and Java requirements were not established.");
        if (!Eula.IsAuthorised)
            problems.Add("The Minecraft EULA was not accepted.");
        var memory = MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb);
        if (memory is not null) problems.Add(memory);
        var port = ServerPortPolicy.Validate(Port);
        if (port is not null) problems.Add(port);
        if (InitialWorld is { } world) problems.AddRange(world.Problems());
        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed record BeginModpackCreationRequest(ModpackCreationPlan Plan);
public sealed record ModpackCreationsResult(IReadOnlyList<InstallOperationSnapshot> Operations);

public sealed record ModrinthPackInspectRequest(string ArchivePath);

public sealed record ModrinthPackInspection
{
    public string Name { get; init; } = "";
    public string VersionName { get; init; } = "";
    public string Summary { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public int RequiredServerFiles { get; init; }
    public int OptionalServerFiles { get; init; }
    public int ExcludedClientFiles { get; init; }
    public long IndexedServerBytes { get; init; }
    /// <summary>Native-only archive identity used to bind selection to the later Agent operation.</summary>
    public string ArchiveSha512 { get; init; } = "";
    public long ArchiveSizeBytes { get; init; }
    public bool CanCreate { get; init; }
    public string Limitation { get; init; } = "";
}
