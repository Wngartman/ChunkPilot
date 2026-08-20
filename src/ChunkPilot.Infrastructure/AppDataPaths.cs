namespace ChunkPilot.Infrastructure;

public sealed class AppDataPaths
{
    /// <param name="rootOverride">Where ChunkPilot's own data lives. Null uses the per-user default.</param>
    /// <param name="managedServersOverride">
    /// Where managed server instances are created. Null uses the per-user default.
    /// </param>
    /// <remarks>
    /// The two roots are separate overrides because they are separate things: one holds ChunkPilot's
    /// database, caches and logs, the other holds the user's servers and worlds. An isolated
    /// validation run has to redirect both, and before this existed a run with an isolated data root
    /// would still have created real servers in the real profile.
    /// </remarks>
    public AppDataPaths(string? rootOverride = null, string? managedServersOverride = null)
    {
        Root = Path.GetFullPath(rootOverride ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChunkPilot"));
        DatabasePath = Path.Combine(Root, "chunkpilot.db");
        Logs = Path.Combine(Root, "Logs");
        Backups = Path.Combine(Root, "Backups");
        Recovery = Path.Combine(Root, "Recovery");
        DiagnosticBundles = Path.Combine(Root, "DiagnosticBundles");
        Cache = Path.Combine(Root, "Cache");
        Staging = Path.Combine(Root, "Staging");
        Shares = Path.Combine(Root, "Shares");
        VersionSnapshots = Path.Combine(Root, "VersionSnapshots");
        ServerIcons = Path.Combine(Root, "ServerIcons");
        PluginProvenance = Path.Combine(Root, "PluginProvenance");
        UpdateCache = Path.Combine(Cache, "Updates");
        CatalogCache = Path.Combine(Cache, "Catalog");
        ManagedJava = Path.Combine(Root, "ManagedJava");
        SecretsPath = Path.Combine(Root, "secrets.dat");
        AgentStatePath = Path.Combine(Root, "agent-state.json");
        ManagedServers = Path.GetFullPath(string.IsNullOrWhiteSpace(managedServersOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChunkPilot", "Servers")
            : managedServersOverride);
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string Logs { get; }
    public string Backups { get; }
    public string Recovery { get; }
    public string DiagnosticBundles { get; }
    public string Cache { get; }
    public string Staging { get; }
    public string Shares { get; }
    public string VersionSnapshots { get; }
    public string ServerIcons { get; }
    public string PluginProvenance { get; }
    public string UpdateCache { get; }
    public string CatalogCache { get; }
    public string ManagedJava { get; }
    public string SecretsPath { get; }
    public string ManagedServers { get; }
    public string AgentStatePath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(Recovery);
        Directory.CreateDirectory(DiagnosticBundles);
        Directory.CreateDirectory(Cache);
        Directory.CreateDirectory(Staging);
        Directory.CreateDirectory(Shares);
        Directory.CreateDirectory(VersionSnapshots);
        Directory.CreateDirectory(ServerIcons);
        Directory.CreateDirectory(PluginProvenance);
        Directory.CreateDirectory(UpdateCache);
        Directory.CreateDirectory(CatalogCache);
        Directory.CreateDirectory(ManagedJava);
    }
}
