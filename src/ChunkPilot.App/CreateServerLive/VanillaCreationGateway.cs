using System.Diagnostics;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServerLive;

/// <summary>
/// Everything the live Vanilla wizard is allowed to ask the Agent to do.
/// </summary>
/// <remarks>
/// <para>
/// The whole App-to-Agent surface of this feature, in one interface, deliberately narrow: read the
/// catalogue, ask where a name would land, submit one approved plan, watch it, ask it to stop. There
/// is no method here that downloads, writes, installs or registers anything, because the App does
/// none of those things — the Agent owns them.
/// </para>
/// <para>
/// The wizard depends on this rather than on <see cref="IAgentClient"/> so the view model holds no
/// transport, no provider client and no serialisation, and so every state the wizard can reach is
/// reproducible in a test without a pipe.
/// </para>
/// </remarks>
public interface IVanillaCreationGateway
{
    /// <summary>Reads the official version catalogue, including how fresh it is.</summary>
    Task<VanillaVersionCatalog> GetCatalogAsync(
        bool includeSnapshots,
        bool forceRefresh,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks where a named server would be created and whether that is allowed.
    /// </summary>
    /// <param name="serverName">The name exactly as typed. The folder identity is derived from it.</param>
    /// <param name="instanceRoot">
    /// The folder new servers are created inside. Empty means ChunkPilot's managed root.
    /// </param>
    Task<VanillaDestinationPreview> PreviewDestinationAsync(
        string serverName,
        string instanceRoot = "",
        CancellationToken cancellationToken = default);

    /// <summary>Submits one approved plan and returns the operation it became.</summary>
    Task<Guid> BeginAsync(VanillaCreationPlan plan, CancellationToken cancellationToken = default);

    /// <summary>Reads the current state of one operation.</summary>
    Task<InstallOperationSnapshot> GetSnapshotAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Asks for an operation to stop. Idempotent; the Agent decides when it is safe.</summary>
    Task CancelAsync(Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Every Vanilla creation the Agent knows about, so a reopened window can reattach.</summary>
    Task<IReadOnlyList<InstallOperationSnapshot>> GetCreationsAsync(CancellationToken cancellationToken = default);

    /// <summary>The managed Java runtimes the Agent has, used to name the one a server was given.</summary>
    Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedRuntimesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The named-pipe implementation. One operation name per method and nothing else.
/// </summary>
/// <remarks>
/// Every call is a single request over the existing local named pipe. No HTTP, no TCP and no second
/// transport is introduced by this feature.
/// </remarks>
public sealed class AgentVanillaCreationGateway : IVanillaCreationGateway
{
    /// <summary>The Agent operation names this gateway uses. Named once so a test can pin them.</summary>
    public static class Operations
    {
        public const string Catalog = "VanillaVersions";
        public const string Destination = "VanillaDestination";
        public const string Begin = "BeginVanillaCreation";
        public const string Progress = "InstallProgress";
        public const string Cancel = "CancelInstall";
        public const string Creations = "VanillaCreations";
        public const string ManagedRuntimes = "ManagedJavaRuntimes";
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

    public async Task<IReadOnlyList<InstallOperationSnapshot>> GetCreationsAsync(
        CancellationToken cancellationToken = default) =>
        (await client.SendAsync<VanillaCreationsResult>(
            Operations.Creations, null, cancellationToken).ConfigureAwait(false)).Operations;

    public async Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedRuntimesAsync(
        CancellationToken cancellationToken = default) =>
        await client.SendAsync<ManagedJavaRuntime[]>(
            Operations.ManagedRuntimes, null, cancellationToken).ConfigureAwait(false);
}

/// <summary>Opens an official document in the user's browser, and does nothing else.</summary>
/// <remarks>
/// Kept behind an interface for one reason: a test has to be able to prove that opening the EULA is
/// not the same as accepting it. That assertion is impossible if the view model shells out directly.
/// </remarks>
public interface ISafeLinkOpener
{
    void Open(string url);
}

/// <summary>
/// Hands an HTTPS address to Windows to open in the default browser.
/// </summary>
/// <remarks>
/// The scheme is checked rather than assumed, so a value that ever came from anywhere but a compiled
/// constant still cannot become a process launch. Nothing is downloaded and nothing is executed.
/// </remarks>
public sealed class ShellLinkOpener : ISafeLinkOpener
{
    public void Open(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Only an HTTPS address can be opened.", nameof(url));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>
/// Asks the user to choose the folder new servers are created inside.
/// </summary>
/// <remarks>
/// A parent folder, never the server's own folder. ChunkPilot creates a new child inside whatever is
/// chosen, so picking a folder is not consent to write into it, and the destination policy still
/// decides whether that child may be created.
/// </remarks>
public interface IServerLocationChooser
{
    /// <summary>Returns the chosen folder, or null when the user cancelled.</summary>
    string? Choose(string title, string startingPath);
}

/// <summary>The standard Windows folder picker, through the application's existing dialog service.</summary>
public sealed class DialogServerLocationChooser(IDialogService dialogs) : IServerLocationChooser
{
    public string? Choose(string title, string startingPath) => dialogs.SelectFolder(title, startingPath);
}

/// <summary>Opens a server that already exists, using the shell's own navigation.</summary>
/// <remarks>
/// Implemented by the application bootstrap over the existing <c>MainViewModel</c> refresh and
/// selection path, so the created server is opened exactly the way any other server is and the
/// navigation service's <c>ServerOpened</c> event still fires. The wizard never navigates itself.
/// </remarks>
public interface ICreatedServerNavigator
{
    Task OpenAsync(Guid serverId);
}
