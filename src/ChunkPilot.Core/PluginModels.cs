namespace ChunkPilot.Core;

public enum PluginProviderKind
{
    Modrinth,
    Hangar
}

/// <summary>
/// The provider-side project type. The legacy Plugin* record names are retained for wire
/// compatibility, while this discriminator keeps the provider and transaction path shared by
/// Paper plugins, Fabric mods, and NeoForge mods.
/// </summary>
public enum ManagedAddonKind
{
    Plugin,
    Mod
}

public sealed record PluginProviderStatus(
    PluginProviderKind Provider,
    bool Available,
    string Detail);

public sealed record PluginCatalogQuery
{
    public ManagedAddonKind Kind { get; init; } = ManagedAddonKind.Plugin;
    public string Search { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "paper";
    public int Limit { get; init; } = 20;
}

public sealed record PluginProject
{
    public ManagedAddonKind Kind { get; init; } = ManagedAddonKind.Plugin;
    public PluginProviderKind Provider { get; init; }
    public string ProjectId { get; init; } = "";
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Summary { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string ProjectUrl { get; init; } = "";
    public long? Downloads { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    public string ServerSide { get; init; } = "unknown";
    public string ClientSide { get; init; } = "unknown";
    public string ClientRequirement { get; init; } = "Unknown";
}

public sealed record PluginDependency
{
    public string ProjectId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Type { get; init; } = "required";
}

public sealed record PluginRelease
{
    public ManagedAddonKind Kind { get; init; } = ManagedAddonKind.Plugin;
    public PluginProviderKind Provider { get; init; }
    public string ProjectId { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string VersionName { get; init; } = "";
    public string MinecraftVersion { get; init; } = "";
    public string Loader { get; init; } = "";
    public string ReleaseChannel { get; init; } = "release";
    public DateTimeOffset PublishedAt { get; init; }
    public string DownloadUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha1 { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public string ServerSide { get; init; } = "unknown";
    public string ClientSide { get; init; } = "unknown";
    public string ClientRequirement { get; init; } = "Unknown";
    public IReadOnlyList<PluginDependency> Dependencies { get; init; } = [];
}

public sealed record PluginSearchRequest(Guid ServerId, string Search, int Limit = 20);
public sealed record PluginReleaseRequest(Guid ServerId, string ProjectId);
public sealed record PluginProviderInstallRequest(
    Guid ServerId,
    string ProjectId,
    string VersionId,
    bool RestartIfRunning = false);
public sealed record PluginProviderPlanRequest(Guid ServerId, string ProjectId, string VersionId);
public sealed record PluginProviderInstallPlanRequest(
    Guid ServerId,
    string ProjectId,
    string VersionId,
    bool RestartIfRunning = false);
public sealed record PluginInstallPlan
{
    public IReadOnlyList<PluginRelease> Releases { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
    public bool CanInstall => Releases.Count > 0 && Problems.Count == 0;
}
public sealed record PluginRemoveRequest(Guid ServerId, string RelativePath, bool RestartIfRunning = false);
