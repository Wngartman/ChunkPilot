namespace ChunkPilot.Core;

/// <summary>Exact first-party Terraria dedicated-server release metadata.</summary>
public sealed record TerrariaReleaseDescriptor
{
    public string Version { get; init; } = "";
    public string ReleaseId { get; init; } = "";
    public string ArtifactUrl { get; init; } = "";
    public long ExpectedSizeBytes { get; init; }
    public DateTimeOffset? LastModifiedAt { get; init; }
    public string Provenance { get; init; } = "";
    public string IntegrityEvidence { get; init; } = "";
    public TerrariaServerArtifact Artifact { get; init; } = new();
    public TerrariaLaunchProfile LaunchProfile { get; init; } = new();
    public TerrariaCapabilityProfile Capabilities { get; init; } = new();
    public TerrariaUpdateIdentity UpdateIdentity { get; init; } = new();
}

public sealed record TerrariaServerArtifact
{
    public string Platform { get; init; } = "Windows";
    public string ArchiveSubtree { get; init; } = "";
    public string ExecutableName { get; init; } = "TerrariaServer.exe";
    public string OfficialChecksum { get; init; } = "";
    public string ChecksumLimitation { get; init; } = "";
}

public sealed record TerrariaLaunchProfile
{
    public string ConfigurationArgument { get; init; } = "-config";
    public string ReadinessPattern { get; init; } = "";
    public string SaveCommand { get; init; } = "save";
    public string SaveConfirmationPattern { get; init; } = "";
    public string StopCommand { get; init; } = "exit";
    public string StatusCommand { get; init; } = "playing";
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromMinutes(10);
}

public sealed record TerrariaCapabilityProfile
{
    public bool Console { get; init; } = true;
    public bool Backups { get; init; } = true;
    public bool DirectTcpNetworking { get; init; } = true;
    public bool AutomaticUpnp { get; init; }
    public bool NativePlayerStatusProtocol { get; init; }
    public string PlayerStatusLimitation { get; init; } = "Player presence is derived from bounded console evidence.";
}

public sealed record TerrariaUpdateIdentity
{
    public string Product { get; init; } = "Terraria Dedicated Server";
    public string Version { get; init; } = "";
    public string ReleaseId { get; init; } = "";
    public string Provider { get; init; } = "Re-Logic";
}

public sealed record TerrariaWorldOwnership
{
    public string CanonicalWorldPath { get; init; } = "";
    public bool IsInsideManagedServerRoot { get; init; }
    public bool CreatedByChunkPilot { get; init; }
}

public enum TerrariaReadinessState
{
    NotStarted,
    GeneratingWorld,
    Listening,
    Failed,
    Stopped
}

public enum TerrariaCertificationFailureKind
{
    None,
    MissingRuntimePrerequisite,
    ArtifactValidation,
    Startup,
    Readiness,
    Save,
    Stop,
    Cleanup,
    Cancelled,
    Unknown
}

public sealed record TerrariaServerConfiguration
{
    public string WorldPath { get; init; } = "";
    public string WorldName { get; init; } = "ChunkPilot World";
    public int AutoCreateSize { get; init; } = 1;
    public int Difficulty { get; init; }
    public int MaximumPlayers { get; init; } = 8;
    public int Port { get; init; } = 7777;
    public string Motd { get; init; } = "Hosted with ChunkPilot";
    public string Password { get; init; } = "";
    public string Seed { get; init; } = "";
    public bool Secure { get; init; } = true;
    public bool EnableUpnp { get; init; }
    public string BindAddress { get; init; } = "127.0.0.1";
}

public sealed record TerrariaInstallRequest
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public string ServerName { get; init; } = "Terraria server";
    public string InstanceRoot { get; init; } = "";
    public TerrariaServerConfiguration Configuration { get; init; } = new();
    public VanillaNetworkingPreference NetworkingPreference { get; init; } =
        VanillaNetworkingPreference.ThisComputerOnly;
}

public sealed record TerrariaMaterializationResult
{
    public required TerrariaReleaseDescriptor Release { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string LocalSha256 { get; init; } = "";
    public long DownloadedBytes { get; init; }
    public string CachePath { get; init; } = "";
}

public sealed record TerrariaCertificationEvidence
{
    public string Version { get; init; } = "";
    public string LocalArtifactSha256 { get; init; } = "";
    public DateTimeOffset TestedAt { get; init; }
    public bool ArtifactValidated { get; init; }
    public bool ReadinessConfirmed { get; init; }
    public bool LocalConnectionConfirmed { get; init; }
    public bool ConsoleCommandConfirmed { get; init; }
    public bool SaveConfirmed { get; init; }
    public bool CleanStopConfirmed { get; init; }
    public bool WorldCreated { get; init; }
    public bool PortReleased { get; init; }
    public bool CleanupConfirmed { get; init; }
    public bool NoUnexpectedGuiConfirmed { get; init; }
    public int? ExitCode { get; init; }
    public TimeSpan Elapsed { get; init; }
    public string ReadinessEvidence { get; init; } = "";
    public string SaveEvidence { get; init; } = "";
    public string StopEvidence { get; init; } = "";
    public string Limitation { get; init; } = "";
    public TerrariaCertificationFailureKind FailureKind { get; init; }
    public string Failure { get; init; } = "";
}
