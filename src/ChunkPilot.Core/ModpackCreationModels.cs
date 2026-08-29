namespace ChunkPilot.Core;

public enum ModpackCreationSource
{
    Modrinth,
    CurseForgeOfficialServerPack,
    CurseForgeGeneratedCandidate,
    LocalCurseForgeManifest,
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
    public string ProjectSlug { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string ServerPackFileId { get; init; } = "";
    public string VersionName { get; init; } = "";
    public ReleaseChannel ReleaseChannel { get; init; } = ReleaseChannel.Stable;
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public string ExpectedSha1 { get; init; } = "";
    public string ExpectedSha256 { get; init; } = "";
    public string ExpectedSha512 { get; init; } = "";
    public long? ExpectedSizeBytes { get; init; }
    /// <summary>
    /// Native preflight evidence for the exact CurseForge client manifest archive. Official
    /// server packs still require this client-side manifest proof because loader identity is not
    /// present in the server-pack relationship or the API game-version labels.
    /// </summary>
    public string VerifiedClientArchiveSha256 { get; init; } = "";
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
        if (SourceKind is ModpackCreationSource.Modrinth or ModpackCreationSource.CurseForgeOfficialServerPack or
            ModpackCreationSource.CurseForgeGeneratedCandidate)
        {
            var expectedProvider = SourceKind == ModpackCreationSource.Modrinth
                ? UpdateProvider.Modrinth : UpdateProvider.CurseForge;
            if (Provider != expectedProvider || string.IsNullOrWhiteSpace(ProjectId) ||
                string.IsNullOrWhiteSpace(VersionId))
                problems.Add("The exact provider project and release identity is incomplete.");
            if (!Uri.TryCreate(Source, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                (SourceKind == ModpackCreationSource.Modrinth
                    ? !uri.IdnHost.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)
                    : !CurseForgeDownloadHost(uri.IdnHost)))
                problems.Add("The selected release does not use its trusted provider CDN.");
            if (ExpectedSizeBytes is null or <= 0 || ExpectedSha1.Length != 40 ||
                SourceKind == ModpackCreationSource.Modrinth && ExpectedSha512.Length != 128)
                problems.Add("The selected provider release has incomplete integrity metadata.");
            if (SourceKind == ModpackCreationSource.CurseForgeOfficialServerPack &&
                string.IsNullOrWhiteSpace(ServerPackFileId))
                problems.Add("The official CurseForge server-pack relationship is incomplete.");
            if ((SourceKind is ModpackCreationSource.CurseForgeOfficialServerPack or
                    ModpackCreationSource.CurseForgeGeneratedCandidate) &&
                (VerifiedClientArchiveSha256.Length != 64 ||
                 VerifiedClientArchiveSha256.Any(character => !Uri.IsHexDigit(character))))
                problems.Add("The exact CurseForge client manifest was not verified before creation.");
        }
        else if (SourceKind == ModpackCreationSource.LocalCurseForgeManifest)
        {
            if (Provider != UpdateProvider.CurseForge)
                problems.Add("A local CurseForge manifest must retain CurseForge provenance.");
            if (!File.Exists(Source))
                problems.Add("The selected local CurseForge archive was not found.");
            if (ExpectedSizeBytes is null or <= 0 || ExpectedSha256.Length != 64)
                problems.Add("The selected local CurseForge archive is not bound to its reviewed identity.");
        }
        else
        {
            if (Provider != UpdateProvider.LocalPackageHistory)
                problems.Add("A local pack must use local package-history provenance.");
            if (ExpectedSizeBytes is null or <= 0 || ExpectedSha512.Length != 128)
                problems.Add("The selected local pack is not bound to its inspected archive identity.");
        }
        if (string.IsNullOrWhiteSpace(MinecraftVersion) || string.IsNullOrWhiteSpace(Loader) ||
            string.IsNullOrWhiteSpace(LoaderVersion) || RequiredJavaMajor <= 0)
            problems.Add("The pack's exact Minecraft, loader, and Java requirements were not established.");
        if (!Eula.IsAuthorised)
            problems.Add("The Minecraft EULA was not accepted.");
        var memory = MemoryAllocationPolicy.ValidatePair(MinimumRamMb, MaximumRamMb);
        if (memory is not null) problems.Add(memory);
        var port = ServerPortPolicy.Validate(Port);
        if (port is not null) problems.Add(port);
        if (InitialWorld is { } world) problems.AddRange(world.Problems());
        return problems.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static bool CurseForgeDownloadHost(string host) =>
        host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
}

public sealed record BeginModpackCreationRequest(ModpackCreationPlan Plan);
public sealed record ModpackCreationsResult(IReadOnlyList<InstallOperationSnapshot> Operations);

public sealed record CurseForgeModpackPreflightRequest(
    Guid OperationId,
    string ProjectId,
    string ClientFileId,
    string ExpectedServerPackFileId);

public sealed record CurseForgeModpackPreflightResult
{
    public Guid OperationId { get; init; }
    public string ProjectId { get; init; } = "";
    public string ClientFileId { get; init; } = "";
    public string ServerPackFileId { get; init; } = "";
    public CatalogReleasePreflightState State { get; init; }
    public string Detail { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string LoaderVersion { get; init; } = "";
    public int RequiredJavaMajor { get; init; }
    public string ClientDownloadUrl { get; init; } = "";
    public string ClientSha1 { get; init; } = "";
    public string ClientSha256 { get; init; } = "";
    public long ClientSizeBytes { get; init; }
}

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
