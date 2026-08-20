using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>Narrow named-pipe adapter for official Fabric and NeoForge creation.</summary>
public sealed class AgentManagedLoaderCreationGateway
{
    public static class Operations
    {
        public const string Versions = "ManagedLoaderVersions";
        public const string Builds = "ManagedLoaderBuilds";
        public const string Begin = "BeginManagedLoaderCreation";
        public const string Creations = "ManagedLoaderCreations";
    }

    private readonly IAgentClient client;

    public AgentManagedLoaderCreationGateway(IAgentClient client) => this.client = client;

    public Task<ManagedLoaderVersionCatalog> GetVersionsAsync(
        ManagedLoaderPlatform platform,
        bool forceRefresh,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<ManagedLoaderVersionCatalog>(Operations.Versions,
            new ManagedLoaderCatalogRequest(platform, forceRefresh), cancellationToken);

    public Task<ManagedLoaderBuildCatalog> GetBuildsAsync(
        ManagedLoaderPlatform platform,
        string minecraftVersion,
        bool forceRefresh,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<ManagedLoaderBuildCatalog>(Operations.Builds,
            new ManagedLoaderBuildsRequest(platform, minecraftVersion, forceRefresh), cancellationToken);

    public async Task<Guid> BeginAsync(
        ManagedLoaderCreationPlan plan,
        CancellationToken cancellationToken = default)
    {
        var started = await client.SendAsync<InstallOperationRequest>(Operations.Begin,
            new BeginManagedLoaderCreationRequest(plan), cancellationToken).ConfigureAwait(false);
        return started.OperationId;
    }
}
