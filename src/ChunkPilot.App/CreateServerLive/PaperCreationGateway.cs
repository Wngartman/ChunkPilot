using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>Narrow named-pipe presentation adapter for exact-build Paper creation.</summary>
public sealed class AgentPaperCreationGateway
{
    public static class Operations
    {
        public const string Versions = "PaperVersions";
        public const string Builds = "PaperBuilds";
        public const string Begin = "BeginPaperCreation";
        public const string Progress = "InstallProgress";
        public const string Cancel = "CancelInstall";
        public const string Creations = "PaperCreations";
    }

    private readonly IAgentClient client;

    public AgentPaperCreationGateway(IAgentClient client) => this.client = client;

    public Task<PaperVersionCatalog> GetVersionsAsync(
        bool forceRefresh,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<PaperVersionCatalog>(
            Operations.Versions, new PaperCatalogRequest(forceRefresh), cancellationToken);

    public Task<PaperBuildCatalog> GetBuildsAsync(
        string minecraftVersion,
        bool forceRefresh,
        CancellationToken cancellationToken = default) =>
        client.SendAsync<PaperBuildCatalog>(
            Operations.Builds, new PaperBuildsRequest(minecraftVersion, forceRefresh), cancellationToken);

    public async Task<Guid> BeginAsync(PaperCreationPlan plan, CancellationToken cancellationToken = default)
    {
        var started = await client.SendAsync<InstallOperationRequest>(
            Operations.Begin, new BeginPaperCreationRequest(plan), cancellationToken).ConfigureAwait(false);
        return started.OperationId;
    }
}
