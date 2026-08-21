using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>
/// The WebUI's narrow named-pipe gateway for managed Vanilla creation.
/// </summary>
/// <remarks>
/// Every call is a single request over the existing local named pipe. No HTTP, no TCP and no second
/// transport is introduced by this feature.
/// </remarks>
public sealed class AgentVanillaCreationGateway
{
    /// <summary>The Agent operation names this gateway uses. Named once so a test can pin them.</summary>
    public static class Operations
    {
        public const string Catalog = "VanillaVersions";
        public const string Destination = "VanillaDestination";
        public const string Begin = "BeginVanillaCreation";
        public const string Progress = "InstallProgress";
        public const string Cancel = "CancelInstall";
    }

    private readonly IAgentClient client;

    public AgentVanillaCreationGateway(IAgentClient client) => this.client = client;

    public async Task<VanillaVersionCatalog> GetCatalogAsync(
        bool includeSnapshots,
        bool forceRefresh,
        CancellationToken cancellationToken = default) =>
        await client.SendAsync<VanillaVersionCatalog>(
            Operations.Catalog,
            new VanillaCatalogRequest(includeSnapshots, forceRefresh),
            cancellationToken).ConfigureAwait(false);

    public async Task<VanillaDestinationPreview> PreviewDestinationAsync(
        string serverName,
        string instanceRoot = "",
        CancellationToken cancellationToken = default) =>
        await client.SendAsync<VanillaDestinationPreview>(
            Operations.Destination,
            new VanillaDestinationRequest(serverName, instanceRoot),
            cancellationToken).ConfigureAwait(false);

    public async Task<Guid> BeginAsync(VanillaCreationPlan plan, CancellationToken cancellationToken = default)
    {
        var started = await client.SendAsync<InstallOperationRequest>(
            Operations.Begin, new BeginVanillaCreationRequest(plan), cancellationToken).ConfigureAwait(false);
        return started.OperationId;
    }

    public async Task<InstallOperationSnapshot> GetSnapshotAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        await client.SendAsync<InstallOperationSnapshot>(
            Operations.Progress, new InstallOperationRequest(operationId), cancellationToken).ConfigureAwait(false);

    public async Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        _ = await client.SendAsync<OperationResult>(
            Operations.Cancel, new InstallOperationRequest(operationId), cancellationToken).ConfigureAwait(false);

}
