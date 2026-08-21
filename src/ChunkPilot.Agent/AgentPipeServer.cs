using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.Agent;

public sealed class AgentPipeServer
{
    private readonly ServerSupervisor supervisor;
    private readonly ChunkPilotStore store;
    private readonly ServerDetectionService detector;
    private readonly SafeFileService files;
    private readonly BackupService backups;
    private readonly JarInventoryService jars;
    private readonly PluginManagementService plugins;
    private readonly ManagedContentOperationCoordinator contentOperations;
    private readonly DiagnosticsService diagnostics;
    private readonly InstallationCoordinator installations;
    private readonly WorldManager worlds;
    private readonly ServerIconService icons;
    private readonly WhitelistService whitelist;
    private readonly ServerDownloadCatalog installCatalog;
    private readonly VanillaVersionCatalogService vanillaCatalog;
    private readonly PaperVersionCatalogService paperCatalog;
    private readonly ManagedLoaderCatalogService loaderCatalog;
    private readonly ConnectionTestService connectionTests;
    private readonly RamArgumentService ramArguments;
    private readonly ServerUpdateCoordinator updates;
    private readonly ServerCapabilityDetectionService capabilities;
    private readonly GuidedCatalogService guidedCatalog;
    private readonly ManagedJavaRuntimeService managedJava;
    private readonly DatapackService datapacks;
    private readonly DatapackManagementService packContent;
    private readonly CrossplayPackageService crossplay;
    private readonly RouterMappingCoordinator routerMappings;
    private readonly PublicConnectivityCoordinator publicConnectivity;
    private readonly UiSessionAuthority uiSessions;
    private readonly WindowsFirewallCoordinator firewallAccess;
    private readonly ISecretStore secrets;
    private readonly AppDataPaths paths;
    private readonly ServerDeletionCoordinator deletions;
    private readonly ServerImportInspectionService importInspection = new();
    private readonly ILogger<AgentPipeServer> logger;
    private readonly SemaphoreSlim handlerLimit = new(16, 16);
    private readonly ConcurrentDictionary<int, Task> handlers = new();
    private int handlerId;
    private int applicationExitStarted;

    public AgentPipeServer(
        ServerSupervisor supervisor,
        ChunkPilotStore store,
        ServerDetectionService detector,
        SafeFileService files,
        BackupService backups,
        JarInventoryService jars,
        PluginManagementService plugins,
        ManagedContentOperationCoordinator contentOperations,
        DiagnosticsService diagnostics,
        InstallationCoordinator installations,
        WorldManager worlds,
        ServerIconService icons,
        WhitelistService whitelist,
        ServerDownloadCatalog installCatalog,
        VanillaVersionCatalogService vanillaCatalog,
        PaperVersionCatalogService paperCatalog,
        ManagedLoaderCatalogService loaderCatalog,
        ConnectionTestService connectionTests,
        RamArgumentService ramArguments,
        ServerUpdateCoordinator updates,
        ServerCapabilityDetectionService capabilities,
        GuidedCatalogService guidedCatalog,
        ManagedJavaRuntimeService managedJava,
        DatapackService datapacks,
        DatapackManagementService packContent,
        CrossplayPackageService crossplay,
        RouterMappingCoordinator routerMappings,
        PublicConnectivityCoordinator publicConnectivity,
        UiSessionAuthority uiSessions,
        WindowsFirewallCoordinator firewallAccess,
        ISecretStore secrets,
        AppDataPaths paths,
        ServerDeletionCoordinator deletions,
        ILogger<AgentPipeServer> logger)
    {
        this.supervisor = supervisor;
        this.store = store;
        this.detector = detector;
        this.files = files;
        this.backups = backups;
        this.jars = jars;
        this.plugins = plugins;
        this.contentOperations = contentOperations;
        this.diagnostics = diagnostics;
        this.installations = installations;
        this.worlds = worlds;
        this.icons = icons;
        this.whitelist = whitelist;
        this.installCatalog = installCatalog;
        this.vanillaCatalog = vanillaCatalog;
        this.paperCatalog = paperCatalog;
        this.loaderCatalog = loaderCatalog;
        this.connectionTests = connectionTests;
        this.ramArguments = ramArguments;
        this.updates = updates;
        this.capabilities = capabilities;
        this.guidedCatalog = guidedCatalog;
        this.managedJava = managedJava;
        this.datapacks = datapacks;
        this.packContent = packContent;
        this.crossplay = crossplay;
        this.routerMappings = routerMappings;
        this.publicConnectivity = publicConnectivity;
        this.uiSessions = uiSessions;
        this.firewallAccess = firewallAccess;
        this.secrets = secrets;
        this.paths = paths;
        this.deletions = deletions;
        this.logger = logger;
    }

    public event EventHandler? ShutdownRequested;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sessionMonitor = MonitorUiSessionsAsync(cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                ChunkPilotConstants.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                64 * 1024,
                64 * 1024);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                break;
            }
            var id = Interlocked.Increment(ref handlerId);
            var task = HandleLimitedAsync(pipe, cancellationToken);
            handlers[id] = task;
            _ = task.ContinueWith(completed => handlers.TryRemove(id, out _),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        await Task.WhenAll(handlers.Values).ConfigureAwait(false);
        await sessionMonitor.ConfigureAwait(false);
    }

    private async Task MonitorUiSessionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                if (uiSessions.Observe() != UiSessionObservation.GoneOrUnprovable)
                    continue;

                var observedExit = uiSessions.BeginObservedApplicationExit();
                var session = observedExit.Session;
                if (session is null)
                    continue;
                var runningIds = GetRunningServerIds();
                try
                {
                    await store.CloseUiSessionAsync(session.SessionId, ApplicationExitKind.Unexpected,
                        runningIds, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception,
                        "The ended UI session could not be recorded; safe shutdown still proceeds.");
                }
                logger.LogWarning(
                    "Exact UI process {ProcessId}/{CreationTicks} ended without SafeApplicationExit; safe shutdown begins.",
                    session.ProcessId, session.ProcessCreationTicks);
                BeginBackgroundApplicationExit(ApplicationExitKind.Unexpected, observedExit.Epoch);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Observation is a safety monitor. One unexpected store/observer failure must not kill it.
                logger.LogWarning(exception, "UI process observation pass failed and will be retried.");
            }
        }
    }

    private async Task HandleLimitedAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await handlerLimit.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await HandleAsync(pipe, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            logger.LogDebug(exception, "Agent client disconnected before the response was written.");
        }
        finally
        {
            handlerLimit.Release();
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false,
            bufferSize: 64 * 1024, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
        {
            AutoFlush = true
        };
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(line))
            return;

        AgentRequest? request = null;
        AgentResponse response;
        try
        {
            request = JsonSerializer.Deserialize<AgentRequest>(line, ProtocolJson.Options)
                      ?? throw new JsonException("Request was empty.");
            var payload = await DispatchAsync(
                request, NamedPipeClientProcessId(pipe), cancellationToken).ConfigureAwait(false);
            response = new AgentResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Payload = payload
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Agent request {Operation} failed", request?.Operation ?? "unknown");
            response = new AgentResponse
            {
                RequestId = request?.RequestId ?? "",
                Success = false,
                Error = SecretRedactor.Redact(exception.Message)
            };
        }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, ProtocolJson.Options)).ConfigureAwait(false);
    }

    private async Task<JsonElement> DispatchAsync(
        AgentRequest request,
        int clientProcessId,
        CancellationToken cancellationToken)
    {
        switch (request.Operation)
        {
            case "Ping":
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Agent connected."), ProtocolJson.Options);
            case "Dashboard":
                return JsonSerializer.SerializeToElement(await supervisor.DashboardAsync(cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            case "Detect":
            {
                var input = Deserialize<DetectServerRequest>(request);
                return JsonSerializer.SerializeToElement(await detector.DetectAsync(input.Folder, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "Import":
            {
                var definition = Deserialize<ServerDefinition>(request);
                await supervisor.ImportAsync(definition, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Server imported by reference. No server files were changed."), ProtocolJson.Options);
            }
            case "BeginInstall":
            {
                var input = Deserialize<ServerInstallRequest>(request);
                return JsonSerializer.SerializeToElement(new InstallOperationRequest(installations.Begin(input)), ProtocolJson.Options);
            }
            case "VanillaVersions":
            {
                // Read-only official metadata. Returns a catalog that says how fresh it is and
                // whether it came from the cache, rather than an unlabelled list.
                var input = Deserialize<VanillaCatalogRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await vanillaCatalog.GetCatalogAsync(input.IncludeSnapshots, input.ForceRefresh, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "BeginVanillaCreation":
            {
                var input = Deserialize<BeginVanillaCreationRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new InstallOperationRequest(installations.BeginVanilla(input.Plan)), ProtocolJson.Options);
            }
            case "VanillaDestination":
            {
                // Read-only. Answers where a name would land and whether that is allowed; creates and
                // reserves nothing, and the transaction re-checks the same policy before promoting.
                var input = Deserialize<VanillaDestinationRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await installations.PreviewDestinationAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "VanillaCreations":
            {
                // Lets a reopened window find work the Agent is already doing rather than starting a
                // second attempt. Reporting only.
                return JsonSerializer.SerializeToElement(
                    new VanillaCreationsResult(installations.VanillaOperations()), ProtocolJson.Options);
            }
            case "PaperVersions":
            {
                var input = Deserialize<PaperCatalogRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await paperCatalog.GetVersionsAsync(input.ForceRefresh, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "PaperBuilds":
            {
                var input = Deserialize<PaperBuildsRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await paperCatalog.GetBuildsAsync(input.MinecraftVersion, input.ForceRefresh, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "BeginPaperCreation":
            {
                var input = Deserialize<BeginPaperCreationRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new InstallOperationRequest(installations.BeginPaper(input.Plan)), ProtocolJson.Options);
            }
            case "PaperCreations":
            {
                return JsonSerializer.SerializeToElement(
                    new PaperCreationsResult(installations.PaperOperations()), ProtocolJson.Options);
            }
            case "ManagedLoaderVersions":
            {
                var input = Deserialize<ManagedLoaderCatalogRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await loaderCatalog.GetVersionsAsync(input.Platform, input.ForceRefresh, cancellationToken)
                        .ConfigureAwait(false), ProtocolJson.Options);
            }
            case "ManagedLoaderBuilds":
            {
                var input = Deserialize<ManagedLoaderBuildsRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await loaderCatalog.GetBuildsAsync(input.Platform, input.MinecraftVersion,
                        input.ForceRefresh, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "BeginManagedLoaderCreation":
            {
                var input = Deserialize<BeginManagedLoaderCreationRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new InstallOperationRequest(installations.BeginManagedLoader(input.Plan)), ProtocolJson.Options);
            }
            case "ManagedLoaderCreations":
            {
                return JsonSerializer.SerializeToElement(
                    new ManagedLoaderCreationsResult(installations.ManagedLoaderOperations()), ProtocolJson.Options);
            }
            case "InspectModrinthPack":
            {
                var input = Deserialize<ModrinthPackInspectRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await new ModrinthPackServerService().InspectAsync(input.ArchivePath, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "InspectServerImport":
            {
                var input = Deserialize<ServerImportInspectRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await InspectServerImportAsync(input.NativePath, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "BeginModpackCreation":
            {
                var input = Deserialize<BeginModpackCreationRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new InstallOperationRequest(installations.BeginModpack(input.Plan)), ProtocolJson.Options);
            }
            case "ModpackCreations":
            {
                return JsonSerializer.SerializeToElement(
                    new ModpackCreationsResult(installations.ModpackOperations()), ProtocolJson.Options);
            }
            case "BeginServerImport":
            {
                var input = Deserialize<BeginServerImportRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new InstallOperationRequest(installations.BeginImport(input.Plan)), ProtocolJson.Options);
            }
            case "ServerImportOperations":
            {
                return JsonSerializer.SerializeToElement(
                    new ServerImportOperationsResult(installations.ImportOperations()), ProtocolJson.Options);
            }
            case "InstallVersions":
            {
                var input = Deserialize<InstallVersionsRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await installCatalog.GetVersionsAsync(input.SourceType, input.IncludeSnapshots, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "QuickStartPresets":
                return JsonSerializer.SerializeToElement(
                    Enum.GetValues<QuickStartKind>().Select(kind => QuickStartPresetFactory.Create(kind)).ToArray(),
                    ProtocolJson.Options);
            case "GetCapabilities":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await capabilities.DetectAsync(supervisor.Get(input.ServerId).Definition, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CatalogProviderStatuses":
                return JsonSerializer.SerializeToElement(guidedCatalog.GetProviderStatuses(), ProtocolJson.Options);
            case "CatalogProviderVersions":
            {
                var input = Deserialize<CatalogVersionInventoryRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await guidedCatalog.GetVersionInventoryAsync(
                        input.Provider, input.CacheOnly, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "BrowseCatalogCache":
            {
                var input = Deserialize<CatalogQuery>(request);
                return JsonSerializer.SerializeToElement(
                    await guidedCatalog.BrowseCacheAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "BrowseCatalogDetailed":
            {
                var input = Deserialize<CatalogQuery>(request);
                return JsonSerializer.SerializeToElement(
                    await guidedCatalog.BrowseDetailedAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "BrowseCatalog":
            {
                var input = Deserialize<CatalogQuery>(request);
                return JsonSerializer.SerializeToElement(
                    await guidedCatalog.BrowseAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ManagedJavaRuntimes":
                return JsonSerializer.SerializeToElement(
                    await store.GetManagedJavaRuntimesAsync(cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            case "InstallManagedJava":
            {
                var input = Deserialize<ManagedJavaInstallRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await managedJava.InstallAsync(input.MajorVersion, cancellationToken: cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "RemoveManagedJava":
            {
                var input = Deserialize<ManagedJavaRemoveRequest>(request);
                var runtime = (await store.GetManagedJavaRuntimesAsync(cancellationToken).ConfigureAwait(false))
                    .SingleOrDefault(item => item.Id == input.RuntimeId)
                    ?? throw new KeyNotFoundException("The managed Java runtime was not found.");
                await managedJava.RemoveUnusedAsync(runtime, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Unused managed Java runtime moved to Recovery."), ProtocolJson.Options);
            }
            case "InspectDatapack":
            {
                var input = Deserialize<DatapackInspectRequest>(request);
                return JsonSerializer.SerializeToElement(
                    datapacks.Inspect(input.Path, input.MinecraftVersion),
                    ProtocolJson.Options);
            }
            case "ListDatapacks":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await packContent.ListAsync(input.ServerId, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "InstallDatapack":
            {
                var input = Deserialize<DatapackInstallRequest>(request);
                var server = supervisor.Get(input.ServerId);
                var profile = await capabilities.DetectAsync(
                    server.Definition, cancellationToken).ConfigureAwait(false);
                if (!profile.SupportsDatapacks)
                    throw new InvalidOperationException("The selected server does not support Java datapacks.");
                var wasRunning = server.State == ServerState.Running;
                var item = await server.RunExclusiveDataOperationAsync(
                    "installing a datapack",
                    requireStopped: false,
                    saveIfRunning: true,
                    freezeWorldSaving: false,
                    token => packContent.InstallAsync(server.Definition, input, token),
                    cancellationToken).ConfigureAwait(false);
                if (wasRunning)
                {
                    var reload = await server.SendCommandAsync(
                        "reload", "Datapack", cancellationToken).ConfigureAwait(false);
                    if (!reload.Success)
                        throw new InvalidOperationException(
                            $"Datapack installed, but reload failed: {reload.Message}");
                }
                return JsonSerializer.SerializeToElement(item, ProtocolJson.Options);
            }
            case "CalculateResourcePackSha1":
            {
                var input = Deserialize<ResourcePackHashRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new TextResponse(DatapackManagementService.CalculateResourcePackSha1(input.Path)),
                    ProtocolJson.Options);
            }
            case "GetResourcePackConfiguration":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await store.GetResourcePackConfigurationAsync(input.ServerId, cancellationToken)
                        .ConfigureAwait(false)
                    ?? new ResourcePackConfiguration { ServerId = input.ServerId },
                    ProtocolJson.Options);
            }
            case "ConfigureResourcePack":
            {
                var input = Deserialize<ResourcePackConfigureRequest>(request);
                var server = supervisor.Get(input.ServerId);
                var profile = await capabilities.DetectAsync(
                    server.Definition, cancellationToken).ConfigureAwait(false);
                if (!profile.SupportsServerResourcePacks)
                    throw new InvalidOperationException(
                        "The selected server does not support Java server resource packs.");
                var result = await server.RunExclusiveDataOperationAsync(
                    "configuring a server resource pack",
                    requireStopped: true,
                    saveIfRunning: false,
                    freezeWorldSaving: false,
                    token => packContent.ConfigureResourcePackAsync(
                        server.Definition, input.Configuration, token),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
            }
            case "ApplyGamerules":
            {
                var input = Deserialize<GameruleApplyRequest>(request);
                var server = supervisor.Get(input.ServerId);
                var supported = GamerulePolicy.Supported(server.Definition.MinecraftVersion)
                    .Select(rule => rule.Name).ToHashSet(StringComparer.Ordinal);
                foreach (var change in input.Changes)
                {
                    if (!supported.Contains(change.Key))
                        throw new InvalidOperationException(
                            $"{change.Key} is not supported by Minecraft {server.Definition.MinecraftVersion}.");
                    if (GamerulePolicy.Validate(change.Key, change.Value) is { } warning)
                        throw new InvalidOperationException(warning);
                }
                if (server.State == ServerState.Running)
                {
                    foreach (var change in input.Changes)
                    {
                        var result = await server.SendCommandAsync(
                            $"gamerule {change.Key} {change.Value}", "Gameplay", cancellationToken)
                            .ConfigureAwait(false);
                        if (!result.Success)
                            throw new InvalidOperationException(result.Message);
                    }
                    return JsonSerializer.SerializeToElement(
                        OperationResult.Ok("Gamerules applied live."), ProtocolJson.Options);
                }
                await store.SetSettingAsync(
                    $"pending-gamerules:{input.ServerId:D}",
                    JsonSerializer.Serialize(input.Changes, ProtocolJson.Options),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Gamerules queued for the next successful startup."),
                    ProtocolJson.Options);
            }
            case "ApplyCustomGamerule":
            {
                var input = Deserialize<GameruleApplyRequest>(request);
                var server = supervisor.Get(input.ServerId);
                if (input.Changes.Count != 1)
                    throw new InvalidOperationException("Apply one custom game rule at a time.");
                var change = input.Changes.Single();
                if (GamerulePolicy.ValidateCustom(change.Key, change.Value) is { } warning)
                    throw new InvalidOperationException(warning);
                if (server.State != ServerState.Running)
                    throw new InvalidOperationException(
                        "Start the server first so it can validate this rule for its exact Minecraft version.");

                var sent = await server.SendCommandAsync(
                    $"gamerule {change.Key} {change.Value}", "Gameplay", cancellationToken)
                    .ConfigureAwait(false);
                if (!sent.Success)
                    throw new InvalidOperationException(sent.Message);
                var verified = await server.QueryGamerulesAsync([change.Key], cancellationToken)
                    .ConfigureAwait(false);
                if (verified.Rejected.Contains(change.Key))
                    return JsonSerializer.SerializeToElement(
                        OperationResult.Fail($"Minecraft {server.Definition.MinecraftVersion} does not recognize {change.Key}."),
                        ProtocolJson.Options);
                if (!verified.Reported.TryGetValue(change.Key, out var reported))
                    return JsonSerializer.SerializeToElement(
                        OperationResult.Fail("The server did not report the rule back, so ChunkPilot could not verify the change."),
                        ProtocolJson.Options);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok($"{change.Key} is now {reported}."), ProtocolJson.Options);
            }
            case "GetNetworkConfiguration":
            {
                var input = Deserialize<ServerIdRequest>(request);
                var definition = supervisor.Get(input.ServerId).Definition;
                var configuration = await store.GetNetworkConfigurationAsync(input.ServerId, cancellationToken)
                    .ConfigureAwait(false) ?? new NetworkConfiguration
                {
                    ServerId = input.ServerId,
                    Mode = VanillaNetworkingPreferencePolicy.ToNetworkMode(definition.CreationNetworkingPreference),
                    JavaPort = definition.Port
                };
                return JsonSerializer.SerializeToElement(configuration, ProtocolJson.Options);
            }
            case "SetNetworkConfiguration":
            {
                var input = Deserialize<NetworkConfiguration>(request);
                _ = supervisor.Get(input.ServerId);
                if (input.PublicAddressExternallyConfirmed)
                    _ = NetworkPolicy.CopyPublicAddress(input);
                await store.UpsertNetworkConfigurationAsync(input, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Networking method saved; local, LAN, public, and tunnel addresses remain separate."),
                    ProtocolJson.Options);
            }
            case "GetRouterMapping":
            {
                // Reporting only. Reading Direct internet state never contacts the router and never
                // creates an exposure.
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.GetRouterStateAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CheckRouterMapping":
            {
                // A bounded, cancellable capability check the user asked for. Mutates nothing.
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.CheckRouterAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "EnableRouterMapping":
            {
                var input = Deserialize<EnableRouterMappingRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.EnableAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "DisableRouterMapping":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.DisableAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "RetryRouterMapping":
            {
                // Re-runs the same reconciliation the Agent performs on its own, so a cleanup that
                // failed can be retried on request instead of only on the next scheduled pass.
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.RetryAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CancelRouterMapping":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.CancelRouterAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "GetFirewallAccess":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await firewallAccess.GetStateAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CheckFirewallAccess":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await firewallAccess.CheckAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "PrepareFirewallAccess":
            {
                var input = Deserialize<PrepareFirewallAccessRequest>(request);
                _ = supervisor.Get(input.ServerId);
                DemandSessionOperation(input.Session, input.ConnectivityOperation,
                    PublicConnectivityOperation.PrepareFirewallAccess, "Preparing Windows Firewall access");
                return JsonSerializer.SerializeToElement(
                    await firewallAccess.PrepareAsync(input.ServerId, input.Operation, input.PublicApproved,
                        cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "CompleteFirewallAccess":
            {
                var input = Deserialize<CompleteFirewallAccessRequest>(request);
                DemandSessionOperation(input.Session, input.ConnectivityOperation,
                    PublicConnectivityOperation.CompleteFirewallAccess, "Completing Windows Firewall access");
                return JsonSerializer.SerializeToElement(
                    await firewallAccess.CompleteAsync(input.ServerId, input.OperationId, input.Cancelled,
                        input.ExitCode, input.LauncherDetail, input.ElevationFailure,
                        cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CancelFirewallAccess":
            {
                var input = Deserialize<ServerIdRequest>(request);
                DemandSessionOperation(input.Session, input.ConnectivityOperation,
                    PublicConnectivityOperation.CancelFirewallAccess, "Cancelling Windows Firewall access");
                return JsonSerializer.SerializeToElement(
                    await firewallAccess.CancelAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            // External reachability. Only "CheckExternalReachability" leaves this machine, and only
            // because somebody pressed the button that reaches it.
            case "GetExternalReachability":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.GetExternalStateAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CheckExternalReachability":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.CheckExternalAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CancelExternalReachability":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await publicConnectivity.CancelExternalAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "GetCrossplayConfiguration":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                var configuration = await store.GetCrossplayConfigurationAsync(
                    input.ServerId, cancellationToken).ConfigureAwait(false)
                    ?? new CrossplayConfiguration { ServerId = input.ServerId };
                return JsonSerializer.SerializeToElement(configuration, ProtocolJson.Options);
            }
            case "InstallCrossplay":
            {
                var input = Deserialize<CrossplayInstallRequest>(request);
                var managedServer = supervisor.Get(input.ServerId);
                if (managedServer.State != ServerState.Stopped)
                    throw new InvalidOperationException(
                        "Safely stop the server before installing or updating crossplay packages.");
                var profile = await capabilities.DetectAsync(
                    managedServer.Definition, cancellationToken).ConfigureAwait(false);
                var occupiedPorts = (await store.GetCrossplayConfigurationsAsync(cancellationToken)
                        .ConfigureAwait(false))
                    .Where(item => item.ServerId != input.ServerId && item.GeyserEnabled)
                    .Select(item => item.BedrockPort)
                    .ToArray();
                var result = await crossplay.InstallAsync(
                    managedServer.Definition, profile, input, occupiedPorts, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
            }
            case "RemoveCrossplay":
            {
                var input = Deserialize<CrossplayRemoveRequest>(request);
                var managedServer = supervisor.Get(input.ServerId);
                if (managedServer.State != ServerState.Stopped)
                    throw new InvalidOperationException(
                        "Safely stop the server before removing crossplay packages.");
                return JsonSerializer.SerializeToElement(
                    await crossplay.RemoveAsync(managedServer.Definition, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ListAutomationRecipes":
                return JsonSerializer.SerializeToElement(
                    await store.GetAutomationRecipesAsync(cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            case "AutomationRecipeTemplates":
            {
                var input = Deserialize<ServerIdRequest>(request);
                _ = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    AutomationRecipeFactory.BuiltIns(input.ServerId), ProtocolJson.Options);
            }
            case "UpsertAutomationRecipe":
            {
                var input = Deserialize<AutomationRecipe>(request);
                _ = supervisor.Get(input.ServerId);
                await store.UpsertAutomationRecipeAsync(input, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("No-code automation recipe saved."), ProtocolJson.Options);
            }
            case "DeleteAutomationRecipe":
            {
                var input = Deserialize<AutomationRecipeIdRequest>(request);
                await store.DeleteAutomationRecipeAsync(input.RecipeId, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Automation recipe removed."), ProtocolJson.Options);
            }
            case "InstallProgress":
            {
                var input = Deserialize<InstallOperationRequest>(request);
                return JsonSerializer.SerializeToElement(installations.Get(input.OperationId), ProtocolJson.Options);
            }
            case "CancelInstall":
            {
                var input = Deserialize<InstallOperationRequest>(request);
                installations.Cancel(input.OperationId);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Install cancellation requested."), ProtocolJson.Options);
            }
            case "Remove":
            {
                var input = Deserialize<ServerIdRequest>(request);
                // Deletion is attempted first because it can legitimately refuse — a running server is
                // not removable — and closing a live server's port on the way to a refused deletion
                // would be a surprise. Once the server is provably gone its mapping is closed; if that
                // fails the evidence is retained and reconciliation keeps retrying.
                await firewallAccess.EnsureDeletionSafeAsync(input.ServerId, cancellationToken)
                    .ConfigureAwait(false);
                var mappingNote = await routerMappings
                    .PrepareForDeletionAsync(input.ServerId, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(mappingNote))
                    throw new InvalidOperationException(mappingNote.Trim());
                await supervisor.RemoveAsync(input.ServerId, cancellationToken).ConfigureAwait(false);
                var firewallNote = await firewallAccess
                    .PrepareForDeletionAsync(input.ServerId, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Server was removed from ChunkPilot; its folder was not changed." +
                                       mappingNote + firewallNote),
                    ProtocolJson.Options);
            }
            case "Start":
            {
                var input = Deserialize<ServerIdRequest>(request);
                publicConnectivity.DemandLifecycleAuthority(input.ServerId, input.Session, input.Lease,
                    input.ConnectivityOperation, PublicConnectivityOperation.StartServer, "Starting a server");
                var started = await supervisor.Get(input.ServerId)
                    .StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await SynchronizePublicConnectivityAsync(input.ServerId).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(started, ProtocolJson.Options);
            }
            case "Save":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(await supervisor.Get(input.ServerId).SaveAsync(cancellationToken: cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "Stop":
            {
                var input = Deserialize<StopRequest>(request);
                publicConnectivity.DemandLifecycleAuthority(input.ServerId, input.Session, input.Lease,
                    input.ConnectivityOperation, PublicConnectivityOperation.StopServer, "Stopping a server");
                var stopped = await supervisor.Get(input.ServerId)
                    .StopAsync(input.SaveFirst, cancellationToken: cancellationToken).ConfigureAwait(false);
                // The server has settled, so the exposure it was opened for is withdrawn now. The
                // coordinator reads the lifecycle itself; this call only makes it immediate rather
                // than waiting for the next reconciliation pass.
                await SynchronizePublicConnectivityAsync(input.ServerId).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(stopped, ProtocolJson.Options);
            }
            case "Restart":
            {
                var input = Deserialize<ServerIdRequest>(request);
                publicConnectivity.DemandLifecycleAuthority(input.ServerId, input.Session, input.Lease,
                    input.ConnectivityOperation, PublicConnectivityOperation.RestartServer, "Restarting a server");
                // A safe restart deliberately leaves the mapping in place: the server is coming back on
                // the same port, and removing and recreating it is churn with no safety benefit.
                var restarted = await supervisor.Get(input.ServerId)
                    .RestartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                await SynchronizePublicConnectivityAsync(input.ServerId).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(restarted, ProtocolJson.Options);
            }
            case "ForceTerminate":
            {
                var input = Deserialize<ServerIdRequest>(request);
                publicConnectivity.DemandLifecycleAuthority(input.ServerId, input.Session, input.Lease,
                    input.ConnectivityOperation, PublicConnectivityOperation.ForceTerminateServer,
                    "Force terminating a server");
                var terminated = await supervisor.Get(input.ServerId)
                    .ForceTerminateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                // A force-terminated server is as stopped as a cleanly stopped one, and its exposure
                // has just as little reason to stay open.
                await SynchronizePublicConnectivityAsync(input.ServerId).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(terminated, ProtocolJson.Options);
            }
            case "SendCommand":
            {
                var input = Deserialize<CommandRequest>(request);
                return JsonSerializer.SerializeToElement(await supervisor.Get(input.ServerId).SendCommandAsync(input.Command, cancellationToken: cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "StartAll":
            {
                var input = Deserialize<AllServersLifecycleRequest>(request);
                publicConnectivity.DemandAllLifecycleAuthority(input,
                    PublicConnectivityOperation.StartAllServers, "Starting all servers");
                var results = await supervisor.StartAllAsync(cancellationToken).ConfigureAwait(false);
                await ReconcilePublicConnectivityAsync().ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(results, ProtocolJson.Options);
            }
            case "StopAll":
            {
                var input = Deserialize<AllServersLifecycleRequest>(request);
                publicConnectivity.DemandAllLifecycleAuthority(input,
                    PublicConnectivityOperation.StopAllServers, "Stopping all servers");
                var results = await supervisor.StopAllAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await ReconcilePublicConnectivityAsync().ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(results, ProtocolJson.Options);
            }
            case "RegisterUiSession":
            {
                var input = Deserialize<UiSessionRegistrationRequest>(request);
                if (clientProcessId <= 0 || input.ProcessId != clientProcessId)
                    throw new UnauthorizedAccessException(
                        "UI session registration was refused because the named-pipe caller did not match the claimed process identity.");
                var registration = uiSessions.Register(input);
                try
                {
                    var stored = await store.RegisterUiSessionAsync(registration.Session, cancellationToken)
                        .ConfigureAwait(false);
                    return JsonSerializer.SerializeToElement(stored with
                    {
                        Session = registration.Session,
                        SessionCapability = registration.Capability
                    }, ProtocolJson.Options);
                }
                catch
                {
                    _ = uiSessions.End(new UiSessionCredential
                    {
                        SessionId = registration.Session.SessionId,
                        Capability = registration.Capability
                    });
                    throw;
                }
            }
            case "HeartbeatUiSession":
            {
                var input = Deserialize<UiSessionHeartbeatRequest>(request);
                uiSessions.Demand(Credential(input.SessionId, input.SessionCapability),
                    "Recording the UI session heartbeat");
                await store.HeartbeatUiSessionAsync(input.SessionId, input.RunningServerIds, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("UI session heartbeat recorded."), ProtocolJson.Options);
            }
            case "SafeApplicationExit":
            {
                var input = Deserialize<SafeApplicationExitRequest>(request);
                var credential = Credential(input.SessionId, input.SessionCapability);
                uiSessions.Demand(credential, "Safe application exit");
                var exitEpoch = uiSessions.BeginApplicationExit(credential);
                var runningIds = GetRunningServerIds();
                try
                {
                    await store.CloseUiSessionAsync(input.SessionId, ApplicationExitKind.Normal, runningIds,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Safe exit could not be recorded; shutdown still proceeds.");
                }
                BeginBackgroundApplicationExit(ApplicationExitKind.Normal, exitEpoch);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Safe application exit accepted; save and stop continue in the background."),
                    ProtocolJson.Options);
            }
            case "WindowsShutdown":
            {
                var input = Deserialize<WindowsShutdownRequest>(request);
                var credential = Credential(input.SessionId, input.SessionCapability);
                uiSessions.Demand(credential, "Windows shutdown handoff");
                var exitEpoch = uiSessions.BeginApplicationExit(credential);
                var runningIds = GetRunningServerIds();
                try
                {
                    await store.CloseUiSessionAsync(input.SessionId, ApplicationExitKind.WindowsShutdown, runningIds,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Windows shutdown handoff could not be recorded.");
                }
                BeginBackgroundApplicationExit(ApplicationExitKind.WindowsShutdown, exitEpoch);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Windows shutdown handoff accepted."), ProtocolJson.Options);
            }
            case "ListFiles":
            {
                var input = Deserialize<FilesRequest>(request);
                var server = supervisor.Get(input.ServerId).Definition;
                return JsonSerializer.SerializeToElement(files.List(server.RootPath, input.RelativePath), ProtocolJson.Options);
            }
            case "ReadFile":
            {
                var input = Deserialize<FilesRequest>(request);
                var server = supervisor.Get(input.ServerId).Definition;
                return JsonSerializer.SerializeToElement(await files.ReadTextAsync(server.RootPath, input.RelativePath, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "WriteFile":
            {
                var input = Deserialize<WriteFileRequest>(request);
                var server = supervisor.Get(input.ServerId).Definition;
                await files.WriteTextAtomicAsync(server.RootPath, input.Content, createRecoveryCopy: true, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("File saved atomically; important configuration was recovery-copied."), ProtocolJson.Options);
            }
            case "GetServerProperties":
            {
                var input = Deserialize<ServerIdRequest>(request);
                var server = supervisor.Get(input.ServerId).Definition;
                var relativePath = "server.properties";
                var content = await files.ReadTextAsync(server.RootPath, relativePath, cancellationToken).ConfigureAwait(false);
                var document = ServerPropertiesDocument.Parse(content.Content);
                return JsonSerializer.SerializeToElement(
                    new ServerPropertiesResponse(new Dictionary<string, string>(document.Values, StringComparer.OrdinalIgnoreCase), content.Content),
                    ProtocolJson.Options);
            }
            case "UpdateServerProperties":
            {
                var input = Deserialize<ServerPropertiesRequest>(request);
                var validation = ServerPropertyValidation.Validate(input.Values);
                if (validation.Count > 0)
                    throw new ArgumentException(string.Join("; ", validation.Select(item => $"{item.Key}: {item.Value}")));
                var server = supervisor.Get(input.ServerId).Definition;
                publicConnectivity.DemandLifecycleAuthority(
                    input.ServerId,
                    input.Session,
                    input.Lease,
                    input.ConnectivityOperation,
                    PublicConnectivityOperation.UpdateServerProperties,
                    "Updating server properties");
                var content = await files.ReadTextAsync(server.RootPath, "server.properties", cancellationToken).ConfigureAwait(false);
                var document = ServerPropertiesDocument.Parse(content.Content);
                foreach (var pair in input.Values)
                    document.Set(pair.Key, pair.Value);
                await files.WriteTextAtomicAsync(server.RootPath, content with { Content = document.ToString() },
                    createRecoveryCopy: true, cancellationToken).ConfigureAwait(false);
                if (input.Values.TryGetValue("server-port", out var portText) &&
                    int.TryParse(portText, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var port) && port != server.Port)
                {
                    // server.properties and ChunkPilot's definition must agree. The managed server
                    // applies this definition immediately while stopped, or on the next start when
                    // a running process still owns the old port.
                    await supervisor.ImportAsync(server with { Port = port }, cancellationToken).ConfigureAwait(false);
                }
                return JsonSerializer.SerializeToElement(OperationResult.Ok("server.properties updated without changing comments or unrelated lines. Restart may be required."), ProtocolJson.Options);
            }
            case "ListBackups":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(await store.GetBackupsAsync(input.ServerId, cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "ListAllBackups":
                return JsonSerializer.SerializeToElement(await store.GetBackupsAsync(cancellationToken: cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            case "Backup":
            {
                var input = Deserialize<BackupRequest>(request);
                return JsonSerializer.SerializeToElement(await supervisor.BackupAsync(input.ServerId, "Manual", cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            }
            case "VerifyBackup":
            {
                var input = Deserialize<BackupIdRequest>(request);
                var record = await FindBackupAsync(input.BackupId, cancellationToken).ConfigureAwait(false);
                var verified = await backups.VerifyAsync(record, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(verified ? "Backup verified." : "Backup verification failed."), ProtocolJson.Options);
            }
            case "Restore":
            {
                var input = Deserialize<RestoreRequest>(request);
                var record = await FindBackupAsync(input.BackupId, cancellationToken).ConfigureAwait(false);
                await supervisor.RestoreAsync(input.ServerId, record, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Pre-restore safety backup completed and selected backup restored."), ProtocolJson.Options);
            }
            case "DeleteBackup":
            {
                var input = Deserialize<BackupIdRequest>(request);
                var record = await FindBackupAsync(input.BackupId, cancellationToken).ConfigureAwait(false);
                await backups.DeleteAsync(record, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Backup archive and record deleted after confirmation."), ProtocolJson.Options);
            }
            case "ListSchedules":
                return JsonSerializer.SerializeToElement(await store.GetSchedulesAsync(cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            case "UpsertSchedule":
            {
                var schedule = Deserialize<ScheduleEntry>(request);
                _ = ScheduleCalculator.NextRun(schedule, DateTimeOffset.Now)
                    ?? throw new ArgumentException("The schedule does not produce a valid future run time.");
                await store.UpsertScheduleAsync(schedule, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Schedule saved; the agent will execute it."), ProtocolJson.Options);
            }
            case "DeleteSchedule":
            {
                var input = Deserialize<ScheduleIdRequest>(request);
                await store.DeleteScheduleAsync(input.ScheduleId, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Schedule deleted."), ProtocolJson.Options);
            }
            case "Inventory":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(jars.Inventory(supervisor.Get(input.ServerId).Definition), ProtocolJson.Options);
            }
            case "PluginProviders":
            {
                _ = supervisor.Get(Deserialize<ServerIdRequest>(request).ServerId);
                return JsonSerializer.SerializeToElement(plugins.ProviderStatuses, ProtocolJson.Options);
            }
            case "PluginSearch":
            {
                var input = Deserialize<PluginSearchRequest>(request);
                var server = supervisor.Get(input.ServerId).Definition;
                return JsonSerializer.SerializeToElement(
                    await plugins.SearchAsync(server, input.Search, input.Limit, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ServerDeletionPreflight":
            {
                var input = Deserialize<ServerDeletionPreflightRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await deletions.PreflightAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "DeleteServer":
            {
                var input = Deserialize<ServerDeletionRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await deletions.DeleteAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "CreateManagedCopy":
            {
                var input = Deserialize<ManagedCopyConversionRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await deletions.CreateManagedCopyAsync(input, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "PluginRelease":
            {
                var input = Deserialize<PluginReleaseRequest>(request);
                var server = supervisor.Get(input.ServerId).Definition;
                return JsonSerializer.SerializeToElement(
                    await plugins.ResolveAsync(server, input.ProjectId, cancellationToken: cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ListWorlds":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(worlds.List(supervisor.Get(input.ServerId).Definition), ProtocolJson.Options);
            }
            case "SwitchWorld":
            {
                var input = Deserialize<WorldRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                var path = await managed.RunExclusiveDataOperationAsync("switching worlds", requireStopped: true,
                    saveIfRunning: false, freezeWorldSaving: false,
                    token => worlds.SwitchActiveAsync(managed.Definition, input.WorldName, managed.State, token),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Active world changed. The previous world was not moved or deleted.", path), ProtocolJson.Options);
            }
            case "ImportWorld":
            {
                var input = Deserialize<WorldImportRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                if (managed.State != ServerState.Stopped)
                    throw new InvalidOperationException("Stop the server before importing a world.");
                var world = await managed.RunExclusiveDataOperationAsync("importing a world", requireStopped: true,
                    saveIfRunning: false, freezeWorldSaving: false,
                    token => worlds.ImportAsync(managed.Definition, input.ZipPath, input.WorldName, token),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(world, ProtocolJson.Options);
            }
            case "ExportWorld":
            {
                var input = Deserialize<WorldExportRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                var world = worlds.List(managed.Definition)
                    .FirstOrDefault(item => item.Name.Equals(input.WorldName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"World {input.WorldName} was not found.");
                var path = await managed.RunExclusiveDataOperationAsync("exporting a world", requireStopped: false,
                    saveIfRunning: true, freezeWorldSaving: true,
                    token => worlds.ExportAsync(managed.Definition, world, input.DestinationDirectory, token),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("World ZIP created with a hash manifest.", path), ProtocolJson.Options);
            }
            case "InstallServerIcon":
            {
                var input = Deserialize<IconInstallRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                var path = await icons.ConvertAndInstallAsync(
                    managed.Definition, input.SourcePath, input.CropX, input.CropY, input.CropSize,
                    input.SaveToLibrary, cancellationToken).ConfigureAwait(false);
                await store.AddActivityAsync(new ActivityEntry
                {
                    ServerId = managed.Definition.Id,
                    ServerName = managed.Definition.Name,
                    Action = "Server icon updated",
                    Result = "Success",
                    Source = "Manual"
                }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Server icon converted to a 64×64 PNG and installed atomically.", path), ProtocolJson.Options);
            }
            case "ListServerIcons":
                return JsonSerializer.SerializeToElement(icons.ListLibrary(), ProtocolJson.Options);
            case "ListWhitelist":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await whitelist.ReadAsync(supervisor.Get(input.ServerId).Definition, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ListPlayerAccess":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await whitelist.ReadUnifiedAsync(
                        supervisor.Get(input.ServerId).Definition, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "GetPlayerAccess":
            {
                // One answer for the whole Access page. The rows, the online count, the slot count and
                // the whitelist switch have to agree with each other, and separate reads can disagree
                // the moment somebody joins between them.
                var input = Deserialize<ServerIdRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                var players = await whitelist.ReadUnifiedAsync(managed.Definition, cancellationToken)
                    .ConfigureAwait(false);
                var snapshot = managed.Snapshot(consoleLines: 0);
                var online = snapshot.OnlinePlayerNames;
                var known = players.ToDictionary(
                    player => player.Name, player => player, StringComparer.OrdinalIgnoreCase);
                foreach (var name in online)
                {
                    // A player can be connected without appearing in any of the JSON files - not
                    // whitelisted, not an operator, never banned. They are still a real row.
                    known[name] = known.TryGetValue(name, out var existing)
                        ? existing with { Online = true }
                        : new UnifiedPlayerAccess { Name = name, Online = true };
                }

                // Last seen is applied here and nowhere else, from the sessions this server reported.
                // A player with no observed session keeps a null LastSeenAt, and the UI says so
                // rather than filling the column in.
                var lastSeen = managed.LastSeenByPlayer;
                foreach (var name in known.Keys.ToArray())
                {
                    if (lastSeen.TryGetValue(name, out var seenAt))
                        known[name] = known[name] with { LastSeenAt = seenAt };
                }
                return JsonSerializer.SerializeToElement(new PlayerAccessSnapshot
                {
                    ServerId = input.ServerId,
                    ServerRunning = managed.State == ServerState.Running,
                    WhitelistEnabled = await ReadWhitelistEnabledAsync(managed.Definition, cancellationToken)
                        .ConfigureAwait(false),
                    OnlineCount = online.Count,
                    MaxPlayers = snapshot.MaxPlayers,
                    Players = known.Values
                        .OrderByDescending(player => player.Online)
                        .ThenBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    Stamp = snapshot.PlayerAccessStamp
                }, ProtocolJson.Options);
            }
            case "ModeratePlayer":
            {
                var input = Deserialize<PlayerModerationRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await managed.ModeratePlayerAsync(input.Action, input.PlayerName, input.Reason, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ReadGamerules":
            {
                var input = Deserialize<GameruleReadRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await ReadGamerulesAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "ConnectionTest":
            {
                var input = Deserialize<ConnectionTestRequest>(request);
                var dashboard = await supervisor.DashboardAsync(cancellationToken).ConfigureAwait(false);
                var snapshot = dashboard.Servers.First(server => server.Definition.Id == input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await connectionTests.TestAsync(snapshot, dashboard.Host.LanAddress, input.IncludeExternalProbe, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "UpdateRam":
            {
                var input = Deserialize<RamUpdateRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                if (managed.State is ServerState.Starting or ServerState.Stopping or ServerState.Restarting)
                    throw new InvalidOperationException("Wait for the current server transition before changing memory allocation.");
                if (MemoryAllocationPolicy.ValidatePair(input.MinimumRamMb, input.MaximumRamMb) is { } memoryProblem)
                    throw new ArgumentException(memoryProblem);
                var host = (await supervisor.DashboardAsync(cancellationToken).ConfigureAwait(false)).Host;
                var totalConfigured = supervisor.Definitions.Where(item => item.Id != input.ServerId).Sum(item => item.MaximumRamMb)
                                      + input.MaximumRamMb;
                var warnings = RamRecommendationCalculator.Validate(
                    input.MinimumRamMb, input.MaximumRamMb, host.TotalMemoryBytes, totalConfigured);
                if (warnings.Any(message => message.Contains("cannot", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(string.Join(" ", warnings));
                var updated = await ramArguments.ApplyAsync(managed.Definition, input.MinimumRamMb, input.MaximumRamMb, cancellationToken)
                    .ConfigureAwait(false);
                await supervisor.ImportAsync(updated, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(
                    warnings.Count == 0
                        ? $"RAM updated in {updated.RamArgumentSource}; restart required."
                        : $"RAM updated in {updated.RamArgumentSource}; restart required. {string.Join(" ", warnings)}"),
                    ProtocolJson.Options);
            }
            case "RenameServer":
            {
                var input = Deserialize<RenameServerRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await supervisor.RenameAsync(input.ServerId, input.DisplayName, cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "WhitelistEnable":
            {
                var input = Deserialize<JarEnabledRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                if (managed.State == ServerState.Running)
                    return JsonSerializer.SerializeToElement(
                        await managed.SendCommandAsync(WhitelistService.EnableCommand(input.Enabled), cancellationToken: cancellationToken).ConfigureAwait(false),
                        ProtocolJson.Options);
                _ = await managed.RunExclusiveDataOperationAsync("changing whitelist settings", requireStopped: true,
                    saveIfRunning: false, freezeWorldSaving: false, async token =>
                    {
                        var content = await files.ReadTextAsync(managed.Definition.RootPath, "server.properties", token).ConfigureAwait(false);
                        var document = ServerPropertiesDocument.Parse(content.Content);
                        document.Set("white-list", input.Enabled ? "true" : "false");
                        await files.WriteTextAtomicAsync(managed.Definition.RootPath, content with { Content = document.ToString() },
                            createRecoveryCopy: true, token).ConfigureAwait(false);
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Whitelist setting saved atomically. It applies at next start."), ProtocolJson.Options);
            }
            case "WhitelistAdd":
            case "WhitelistRemove":
            {
                var input = Deserialize<WhitelistPlayerRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                if (managed.State != ServerState.Running)
                    throw new InvalidOperationException("Start the server to resolve a player name to its real UUID.");
                // Routed through moderation so the reply is awaited: the server writes whitelist.json as
                // it answers, and returning before that answer is what made a freshly added player
                // absent from the reloaded list.
                var action = request.Operation == "WhitelistAdd"
                    ? PlayerModerationAction.AddToWhitelist
                    : PlayerModerationAction.RemoveFromWhitelist;
                return JsonSerializer.SerializeToElement(
                    await managed.ModeratePlayerAsync(action, input.PlayerName, cancellationToken: cancellationToken)
                        .ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "WhitelistReload":
            {
                var input = Deserialize<ServerIdRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                if (managed.State != ServerState.Running)
                    throw new InvalidOperationException("Whitelist reload is a live server command.");
                return JsonSerializer.SerializeToElement(
                    await managed.SendCommandAsync(WhitelistService.ReloadCommand(), cancellationToken: cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "InspectJar":
            {
                var input = Deserialize<JarInstallRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    jars.Inspect(managed.Definition, input.SourcePath), ProtocolJson.Options);
            }
            case "InstallJar":
            {
                var input = Deserialize<JarInstallRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                _ = await managed.RunExclusiveRestartableDataOperationAsync(
                    "installing or replacing a mod/plugin",
                    input.RestartIfRunning,
                    token => jars.InstallWithReceiptAsync(managed.Definition, input.SourcePath,
                        cancellationToken: token),
                    (receipt, _) =>
                    {
                        jars.RollbackInstall(managed.Definition, receipt);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(input.RestartIfRunning
                    ? "Local JAR installed and the server restarted successfully."
                    : "Local JAR installed with recovery of any replaced file."), ProtocolJson.Options);
            }
            case "SetJarEnabled":
            {
                var input = Deserialize<JarEnabledRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                _ = await managed.RunExclusiveRestartableDataOperationAsync(
                    "changing mod or plugin state",
                    input.RestartIfRunning,
                    token => Task.FromResult(jars.SetEnabledWithReceipt(
                        managed.Definition, input.RelativePath, input.Enabled)),
                    (receipt, _) =>
                    {
                        jars.RollbackMove(managed.Definition, receipt);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(
                    input.RestartIfRunning
                        ? input.Enabled ? "JAR enabled and the server restarted successfully." : "JAR disabled and the server restarted successfully."
                        : input.Enabled ? "JAR enabled." : "JAR disabled reversibly."), ProtocolJson.Options);
            }
            case "RemoveJar":
            {
                var input = Deserialize<PluginRemoveRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                _ = await managed.RunExclusiveRestartableDataOperationAsync(
                    "removing a mod or plugin",
                    input.RestartIfRunning,
                    token => Task.FromResult(jars.RemoveWithReceipt(managed.Definition, input.RelativePath)),
                    (receipt, _) =>
                    {
                        jars.RollbackMove(managed.Definition, receipt);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(
                    input.RestartIfRunning
                        ? "The JAR moved to ChunkPilot Recovery, its configuration was preserved, and the server restarted successfully."
                        : "The JAR was moved to ChunkPilot Recovery. Plugin configuration was preserved."), ProtocolJson.Options);
            }
            case "WriteAddonConfig":
            {
                var input = Deserialize<AddonConfigWriteRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                jars.ValidateConfigOwnership(
                    managed.Definition, input.AddonRelativePath, input.Content.RelativePath);
                _ = await managed.RunExclusiveRestartableDataOperationAsync(
                    "saving add-on configuration",
                    input.RestartIfRunning,
                    token => files.WriteTextAtomicWithReceiptAsync(
                        managed.Definition.RootPath, input.Content, createRecoveryCopy: true, token),
                    (receipt, token) => files.RollbackTextWriteAsync(
                        managed.Definition.RootPath, receipt, token),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(
                    input.RestartIfRunning
                        ? "Configuration saved atomically and the server restarted successfully."
                        : "Configuration saved atomically with a recovery copy."), ProtocolJson.Options);
            }
            case "InstallPluginProviderRelease":
            {
                var input = Deserialize<PluginProviderInstallRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                var installed = await managed.RunExclusiveRestartableDataOperationAsync(
                    "installing a verified plugin release",
                    input.RestartIfRunning,
                    token => plugins.InstallWithReceiptAsync(
                        managed.Definition, input.ProjectId, input.VersionId, null, token),
                    (result, _) =>
                    {
                        jars.RollbackInstall(managed.Definition, result.Receipt);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(
                    input.RestartIfRunning
                        ? $"Installed {installed.Release.FileName} after SHA-512 verification and restarted the server successfully."
                        : $"Installed {installed.Release.FileName} after SHA-512 verification."), ProtocolJson.Options);
            }
            case "PlanPluginProviderRelease":
            {
                var input = Deserialize<PluginProviderPlanRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                return JsonSerializer.SerializeToElement(
                    await plugins.PlanAsync(managed.Definition, input.ProjectId, input.VersionId, cancellationToken)
                        .ConfigureAwait(false), ProtocolJson.Options);
            }
            case "InstallPluginProviderPlan":
            {
                var input = Deserialize<PluginProviderInstallPlanRequest>(request);
                var managed = supervisor.Get(input.ServerId);
                var installed = await managed.RunExclusiveRestartableDataOperationAsync(
                    "installing a verified add-on dependency plan",
                    input.RestartIfRunning,
                    token => plugins.InstallPlanWithReceiptsAsync(
                        managed.Definition, input.ProjectId, input.VersionId, null, token),
                    (result, _) =>
                    {
                        plugins.RollbackPlan(managed.Definition, result);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok(
                    input.RestartIfRunning
                        ? $"Installed {installed.Installed.Count} verified add-on files and restarted the server successfully."
                        : $"Installed {installed.Installed.Count} verified add-on files as one reversible plan."),
                    ProtocolJson.Options);
            }
            case "BeginManagedContentInstall":
            {
                var input = Deserialize<BeginManagedContentInstallRequest>(request);
                return JsonSerializer.SerializeToElement(contentOperations.BeginInstall(input), ProtocolJson.Options);
            }
            case "ManagedContentOperation":
            {
                var input = Deserialize<ManagedContentOperationRequest>(request);
                return JsonSerializer.SerializeToElement(contentOperations.Get(input.OperationId), ProtocolJson.Options);
            }
            case "ManagedContentOperations":
            {
                var input = Deserialize<ManagedContentOperationsRequest>(request);
                return JsonSerializer.SerializeToElement(contentOperations.List(input.ServerId), ProtocolJson.Options);
            }
            case "CancelManagedContentOperation":
            {
                var input = Deserialize<ManagedContentOperationRequest>(request);
                contentOperations.Cancel(input.OperationId);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Cancellation was requested."), ProtocolJson.Options);
            }
            case "Diagnostics":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await diagnostics.AnalyzeAsync(supervisor.Get(input.ServerId).Definition, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "Troubleshoot":
            {
                var input = Deserialize<ServerIdRequest>(request);
                var snapshot = supervisor.Get(input.ServerId).Snapshot();
                return JsonSerializer.SerializeToElement(
                    await diagnostics.TroubleshootAsync(snapshot, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "DiagnosticBundle":
            {
                var input = Deserialize<ServerIdRequest>(request);
                var path = await diagnostics.CreateDiagnosticBundleAsync(
                    supervisor.Get(input.ServerId).Definition,
                    await store.GetActivityAsync(200, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Redacted diagnostic bundle created.", path), ProtocolJson.Options);
            }
            case "DetectUpdateSource":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await updates.DetectSourceAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "GetUpdateSource":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new UpdateSourceResponse(
                        await updates.GetSourceAsync(input.ServerId, cancellationToken).ConfigureAwait(false)),
                    ProtocolJson.Options);
            }
            case "LinkUpdateSource":
            {
                var input = Deserialize<LinkUpdateSourceRequest>(request);
                await updates.LinkSourceAsync(input.Source, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Update source linked. ChunkPilot will use the recorded installed version as its comparison baseline."),
                    ProtocolJson.Options);
            }
            case "CheckUpdates":
            {
                var input = Deserialize<CheckUpdatesRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await updates.CheckAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "GetLatestUpdateCheck":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new UpdateCheckResponse(
                        await store.GetLatestUpdateCheckAsync(input.ServerId, cancellationToken).ConfigureAwait(false)),
                    ProtocolJson.Options);
            }
            case "BeginPackUpdate":
            {
                var input = Deserialize<UpdateInstallRequest>(request);
                return JsonSerializer.SerializeToElement(
                    new UpdateOperationRequest(updates.Begin(input)),
                    ProtocolJson.Options);
            }
            case "GetPackUpdate":
            {
                var input = Deserialize<UpdateOperationRequest>(request);
                return JsonSerializer.SerializeToElement(updates.Get(input.OperationId), ProtocolJson.Options);
            }
            case "CancelPackUpdate":
            {
                var input = Deserialize<UpdateOperationRequest>(request);
                updates.Cancel(input.OperationId);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Update cancellation requested. An active filesystem switch or rollback will still finish safely."),
                    ProtocolJson.Options);
            }
            case "ListVersions":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await updates.ListVersionsAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "GetUpdateHistory":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await store.GetUpdateHistoryAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "MarkVersionHealthy":
            {
                var input = Deserialize<MarkVersionHealthyRequest>(request);
                await updates.MarkHealthyAsync(input.ServerId, input.SnapshotId, input.RetentionDays, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("The active version is marked healthy; previous verified versions remain available under the selected retention policy."),
                    ProtocolJson.Options);
            }
            case "RollbackVersion":
            {
                var input = Deserialize<VersionSnapshotRequest>(request);
                await updates.RollbackAsync(input.ServerId, input.SnapshotId, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Rollback completed from a verified full-version snapshot."),
                    ProtocolJson.Options);
            }
            case "DeleteVersion":
            {
                var input = Deserialize<VersionSnapshotRequest>(request);
                await updates.DeleteVersionAsync(input.ServerId, input.SnapshotId, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Inactive snapshot removed from the version list and moved into ChunkPilot Recovery. Active and last-usable-version protections were enforced."),
                    ProtocolJson.Options);
            }
            case "VerifyVersion":
            {
                var input = Deserialize<VersionSnapshotRequest>(request);
                var verified = await updates.VerifyVersionAsync(
                    input.ServerId, input.SnapshotId, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    verified
                        ? OperationResult.Ok("Snapshot archive and every manifest entry verified.")
                        : OperationResult.Fail("Snapshot verification failed."),
                    ProtocolJson.Options);
            }
            case "UpdateVersionMetadata":
            {
                var input = Deserialize<VersionMetadataRequest>(request);
                await updates.UpdateVersionMetadataAsync(input, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Version retention and description updated."),
                    ProtocolJson.Options);
            }
            case "GetUpdateCenter":
                return JsonSerializer.SerializeToElement(
                    await updates.GetUpdateCenterAsync(cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            case "GetUpdatePreferences":
            {
                var input = Deserialize<ServerIdRequest>(request);
                return JsonSerializer.SerializeToElement(
                    await store.GetUpdatePreferencesAsync(input.ServerId, cancellationToken).ConfigureAwait(false),
                    ProtocolJson.Options);
            }
            case "SetUpdatePreferences":
            {
                var input = Deserialize<UpdatePreferences>(request);
                await store.UpsertUpdatePreferencesAsync(input, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("Update preferences saved."),
                    ProtocolJson.Options);
            }
            case "SetCurseForgeApiKey":
            {
                var input = Deserialize<SettingsValueRequest>(request);
                if (!input.Key.Equals("curseforge-api-key", StringComparison.Ordinal))
                    throw new ArgumentException("The secret key name is invalid.");
                cancellationToken.ThrowIfCancellationRequested();
                secrets.SetSecret(input.Key, input.Value);
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("CurseForge API key encrypted for the current Windows user."),
                    ProtocolJson.Options);
            }
            case "HasCurseForgeApiKey":
            {
                cancellationToken.ThrowIfCancellationRequested();
                var configured = !string.IsNullOrWhiteSpace(secrets.GetSecret("curseforge-api-key"));
                return JsonSerializer.SerializeToElement(new TextResponse(configured ? "configured" : ""), ProtocolJson.Options);
            }
            case "RemoveCurseForgeApiKey":
            {
                cancellationToken.ThrowIfCancellationRequested();
                secrets.Delete("curseforge-api-key");
                return JsonSerializer.SerializeToElement(
                    OperationResult.Ok("CurseForge connection removed."), ProtocolJson.Options);
            }
            case "SelfTest":
                return JsonSerializer.SerializeToElement(await SelfTestAsync(cancellationToken).ConfigureAwait(false), ProtocolJson.Options);
            case "GetSetting":
            {
                var input = Deserialize<SettingsValueRequest>(request);
                return JsonSerializer.SerializeToElement(new TextResponse(await store.GetSettingAsync(input.Key, cancellationToken).ConfigureAwait(false) ?? ""), ProtocolJson.Options);
            }
            case "SetSetting":
            {
                var input = Deserialize<SettingsValueRequest>(request);
                await store.SetSettingAsync(input.Key, input.Value, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Setting saved."), ProtocolJson.Options);
            }
            case "ShutdownAgent":
            {
                var running = supervisor.Definitions.Select(definition => supervisor.Get(definition.Id))
                    .Where(server => server.State is not ServerState.Stopped and not ServerState.Crashed)
                    .ToArray();
                if (running.Length > 0)
                    throw new InvalidOperationException("The agent will not exit while a managed server is running.");
                ShutdownRequested?.Invoke(this, EventArgs.Empty);
                return JsonSerializer.SerializeToElement(OperationResult.Ok("Agent shutdown accepted."), ProtocolJson.Options);
            }
            default:
                throw new InvalidOperationException($"Unknown agent operation: {request.Operation}");
        }
    }

    /// <summary>
    /// Brings the router into line after a lifecycle command. Never fails the command it follows: a
    /// server that started must not be reported as failed because a router did not answer.
    /// </summary>
    private async Task SynchronizePublicConnectivityAsync(Guid serverId)
    {
        try
        {
            await publicConnectivity.SynchronizeAfterLifecycleAsync(serverId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Router mapping could not be synchronized for {ServerId}.", serverId);
        }
    }

    private async Task ReconcilePublicConnectivityAsync()
    {
        try
        {
            await publicConnectivity.ReconcileAllAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Public connectivity could not be reconciled after a lifecycle action.");
        }
    }

    private IReadOnlyList<Guid> GetRunningServerIds() =>
        supervisor.Definitions
            .Select(definition => supervisor.Get(definition.Id))
            .Where(server => server.State is not ServerState.Stopped and not ServerState.Crashed)
            .Select(server => server.Definition.Id)
            .ToArray();

    private void BeginBackgroundApplicationExit(ApplicationExitKind exitKind, long exitEpoch)
    {
        if (Interlocked.Exchange(ref applicationExitStarted, 1) != 0)
            return;
        // This is deliberately synchronous and first: no slow save, router or store operation may
        // leave renewal or an in-flight external verification authoritative after UI death.
        var revokedLeases = publicConnectivity.RevokeAllImmediately();
        _ = Task.Run(async () =>
        {
            var source = exitKind switch
            {
                ApplicationExitKind.WindowsShutdown => "Windows shutdown",
                ApplicationExitKind.Unexpected => "Unexpected UI exit",
                _ => "Application exit"
            };
            while (true)
            {
                IReadOnlyDictionary<Guid, OperationResult> stopResults;
                try
                {
                    using var stopAttemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    stopResults = await supervisor.StopAllForApplicationExitAsync(
                        source, cancellationToken: stopAttemptTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    logger.LogError(exception, "Bounded save/stop attempt failed during {ExitKind}", exitKind);
                    stopResults = new Dictionary<Guid, OperationResult>();
                }

                using var mappingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try
                {
                    await publicConnectivity.CleanupRevokedAsync(
                        revokedLeases, exitEpoch, mappingTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogWarning(
                        "Router cleanup reached its bounded deadline during {ExitKind}; exact evidence remains pending.",
                        exitKind);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    logger.LogError(exception, "Exact router cleanup failed during {ExitKind}", exitKind);
                }

                var stillAlive = supervisor.ExactOwnedProcessesStillAlive();
                if (stillAlive.Count == 0)
                {
                    ShutdownRequested?.Invoke(this, EventArgs.Empty);
                    return;
                }
                if (logger.IsEnabled(LogLevel.Critical))
                {
                    var failures = string.Join("; ", stopResults.Where(pair => !pair.Value.Success)
                        .Select(pair => $"{pair.Key}: {pair.Value.Message}"));
                    logger.LogCritical(
                        "The Agent remains alive with public renewal prohibited because managed process instances remain after {ExitKind}: {ServerIds}. {Failures}",
                        exitKind, string.Join(", ", stillAlive), failures);
                }
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None).ConfigureAwait(false);
            }
        });
    }

    private static UiSessionCredential Credential(Guid sessionId, string capability) =>
        new() { SessionId = sessionId, Capability = capability };

    private static int NamedPipeClientProcessId(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows() ||
            !NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) ||
            processId == 0 || processId > int.MaxValue)
        {
            return 0;
        }
        return checked((int)processId);
    }

    private void DemandSessionOperation(
        UiSessionCredential credential,
        PublicConnectivityOperation presented,
        PublicConnectivityOperation expected,
        string action)
    {
        if (presented != expected)
            throw new UnauthorizedAccessException(
                $"{action} was refused because the capability was issued for a different operation.");
        uiSessions.Demand(credential, action);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetNamedPipeClientProcessId(
            Microsoft.Win32.SafeHandles.SafePipeHandle pipe,
            out uint clientProcessId);
    }

    private async Task<IReadOnlyList<SelfTestItem>> SelfTestAsync(CancellationToken cancellationToken)
    {
        var results = new List<SelfTestItem>
        {
            new("Agent connection", FindingSeverity.Pass, "Named-pipe request reached the active per-user agent.")
        };
        var testKey = "self-test-" + Guid.NewGuid().ToString("N");
        await store.SetSettingAsync(testKey, "ok", cancellationToken).ConfigureAwait(false);
        results.Add(new("SQLite read/write", await store.GetSettingAsync(testKey, cancellationToken).ConfigureAwait(false) == "ok"
            ? FindingSeverity.Pass : FindingSeverity.Error, paths.DatabasePath));
        var permissionPath = Path.Combine(paths.Root, $".permission-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(permissionPath, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(permissionPath);
            results.Add(new("App-data permissions", FindingSeverity.Pass, paths.Root));
        }
        catch (IOException exception)
        {
            results.Add(new("App-data permissions", FindingSeverity.Error, exception.Message));
        }
        foreach (var definition in supervisor.Definitions)
        {
            results.Add(new($"{definition.Name}: server path", Directory.Exists(definition.RootPath) ? FindingSeverity.Pass : FindingSeverity.Error, definition.RootPath));
            results.Add(new($"{definition.Name}: launch executable", File.Exists(definition.Executable) ? FindingSeverity.Pass : FindingSeverity.Error, definition.Executable));
            results.Add(new($"{definition.Name}: working directory", Directory.Exists(definition.WorkingDirectory) ? FindingSeverity.Pass : FindingSeverity.Error, definition.WorkingDirectory));
            try
            {
                BackupService.ValidateDestination(definition.RootPath, backups.GetDefaultProfile(definition).DestinationPath);
                results.Add(new($"{definition.Name}: backup safety", FindingSeverity.Pass, backups.GetDefaultProfile(definition).DestinationPath));
            }
            catch (InvalidOperationException exception)
            {
                results.Add(new($"{definition.Name}: backup safety", FindingSeverity.Error, exception.Message));
            }
            var source = await updates.GetSourceAsync(definition.Id, cancellationToken).ConfigureAwait(false);
            results.Add(new($"{definition.Name}: update source",
                source is null || !source.HasIdentifiedBaseline ? FindingSeverity.Unavailable : FindingSeverity.Pass,
                source is null
                    ? "No source is linked; updates remain disabled."
                    : $"{source.Provider} / {source.ProjectId} / installed {source.InstalledVersionName}"));
            if (source is not null && source.HasIdentifiedBaseline &&
                (source.Provider != UpdateProvider.CurseForge || secrets.Contains("curseforge-api-key")))
            {
                var check = await updates.CheckAsync(definition.Id, cancellationToken).ConfigureAwait(false);
                results.Add(new($"{definition.Name}: provider connectivity",
                    check.Status == ServerUpdateStatus.CheckUnavailable
                        ? FindingSeverity.Unavailable : FindingSeverity.Pass,
                    check.Message));
            }
            var versionSnapshots = await updates.ListVersionsAsync(definition.Id, cancellationToken)
                .ConfigureAwait(false);
            results.Add(new($"{definition.Name}: rollback readiness",
                versionSnapshots.Any(item => item.Verified && File.Exists(item.SnapshotPath))
                    ? FindingSeverity.Pass : FindingSeverity.Unavailable,
                versionSnapshots.Any(item => item.Verified && File.Exists(item.SnapshotPath))
                    ? "At least one verified rollback snapshot is available."
                    : "No verified pre-update snapshot exists yet."));
        }
        var schedule = new ScheduleEntry { Kind = ScheduleKind.Interval, IntervalMinutes = 5, Enabled = true };
        results.Add(new("Scheduler calculation", ScheduleCalculator.NextRun(schedule, DateTimeOffset.Now) is not null
            ? FindingSeverity.Pass : FindingSeverity.Error, "Five-minute interval produced a future time."));
        results.Add(new("Console pipe functionality", FindingSeverity.Pass, "Request/response transport is active for the current Windows user."));
        results.Add(new("Icon resources", File.Exists(Path.Combine(AppContext.BaseDirectory, "ChunkPilot.ico"))
            ? FindingSeverity.Pass : FindingSeverity.Warning, Path.Combine(AppContext.BaseDirectory, "ChunkPilot.ico")));
        results.Add(new("Notification/tray integration", OperatingSystem.IsWindows()
            ? FindingSeverity.Pass : FindingSeverity.Unavailable, OperatingSystem.IsWindows() ? "Windows notification area is available." : "Not running on Windows."));
        foreach (var updatePath in new[] { paths.VersionSnapshots, paths.UpdateCache, paths.Staging })
        {
            var probe = Path.Combine(updatePath, $".self-test-{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllTextAsync(probe, "ChunkPilot", cancellationToken).ConfigureAwait(false);
                File.Delete(probe);
                results.Add(new($"Update storage: {Path.GetFileName(updatePath)}",
                    FindingSeverity.Pass, $"{updatePath} (write verified)"));
            }
            catch (IOException exception)
            {
                results.Add(new($"Update storage: {Path.GetFileName(updatePath)}",
                    FindingSeverity.Error, exception.Message));
            }
        }
        var root = Path.GetPathRoot(paths.Root);
        if (!string.IsNullOrWhiteSpace(root))
        {
            var drive = new DriveInfo(root);
            results.Add(new("Update staging free space",
                drive.AvailableFreeSpace >= 2L * 1024 * 1024 * 1024 ? FindingSeverity.Pass : FindingSeverity.Warning,
                $"{drive.AvailableFreeSpace / (1024 * 1024 * 1024):N1} GB available on {root}"));
        }
        results.Add(new("Update rollback engine", FindingSeverity.Pass,
            "Full snapshot hashing, safe extraction, atomic directory switching, and last-usable-version deletion guards are active."));
        var hashProbe = Encoding.UTF8.GetBytes("ChunkPilot hash self-test");
        var actualHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(hashProbe));
        results.Add(new("Update hash implementation",
            actualHash.Equals("0B21B4591A6A37A5C1B636C3C713E7425270E1A59E9929E08039FABAC5D55EC0",
                StringComparison.Ordinal)
                ? FindingSeverity.Pass : FindingSeverity.Error,
            "SHA-256 known-answer test."));
        _ = await store.GetUpdatePreferencesAsync(Guid.Empty, cancellationToken).ConfigureAwait(false);
        results.Add(new("Update database migration", FindingSeverity.Pass,
            "Update source, check history, version snapshot, migration, rollback, and preference tables are readable."));
        results.Add(new("CurseForge API access",
            string.IsNullOrWhiteSpace(secrets.GetSecret("curseforge-api-key"))
                ? FindingSeverity.Unavailable : FindingSeverity.Pass,
            "Optional API key is stored with current-user Windows data protection and is never returned by the agent."));
        return results;
    }

    /// <summary>Reads <c>white-list</c> from server.properties, defaulting to off when it is absent.</summary>
    private async Task<bool> ReadWhitelistEnabledAsync(
        ServerDefinition definition,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(definition.RootPath, "server.properties");
        if (!File.Exists(path))
            return false;
        try
        {
            var content = await files.ReadTextAsync(definition.RootPath, "server.properties", cancellationToken)
                .ConfigureAwait(false);
            var document = ServerPropertiesDocument.Parse(content.Content);
            return bool.TryParse(document.Get("white-list"), out var enabled) && enabled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The game rules for one server, with the provenance of every value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A running server is asked for its own values, because that is the only authority on what the
    /// world is actually doing. A stopped server is not guessed at: rules ChunkPilot holds for the next
    /// start are reported as queued, and everything else is reported as unknown, so the UI can disable
    /// its controls rather than show a switch that means nothing.
    /// </para>
    /// <para>
    /// The rule set is version-gated, so a control never appears for a rule the selected Minecraft
    /// version does not have.
    /// </para>
    /// </remarks>
    private async Task<GameruleStateResponse> ReadGamerulesAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var managed = supervisor.Get(serverId);
        var definition = managed.Definition;
        var profile = await capabilities.DetectAsync(definition, cancellationToken).ConfigureAwait(false);
        var supported = GamerulePolicy.Supported(definition.MinecraftVersion);
        if (!profile.SupportsGamerules || supported.Count == 0)
        {
            // No probe. Reaching this point means ChunkPilot has no rule names it can claim for this
            // version, and sending a list of guesses at the server would only put its refusals in the
            // user's Console. One sentence, no dead controls, no failed-probe history.
            return new GameruleStateResponse
            {
                ServerId = serverId,
                ServerRunning = managed.State == ServerState.Running,
                CanChange = false,
                UnavailableReason =
                    "Game rules are not available for this server's Minecraft version.",
                Rules = []
            };
        }

        var queued = await ReadQueuedGamerulesAsync(serverId, cancellationToken).ConfigureAwait(false);
        var running = managed.State == ServerState.Running;
        var query = running
            ? await managed.QueryGamerulesAsync(
                supported.Select(rule => rule.Name).ToArray(), cancellationToken).ConfigureAwait(false)
            : GameruleQueryResult.Empty;
        var reported = query.Reported;

        // A rule this server refused to parse is not offered at all. Minecraft versions rename and
        // retire game rules, and a switch for a rule the server rejects is a control that fails on use.
        var offered = supported.Where(rule => !query.Rejected.Contains(rule.Name)).ToArray();
        if (running && offered.Length == 0)
        {
            return new GameruleStateResponse
            {
                ServerId = serverId,
                ServerRunning = true,
                CanChange = false,
                UnavailableReason =
                    "This server did not accept any of the game rules ChunkPilot knows, so none are offered. " +
                    "Its Minecraft version names them differently.",
                Rules = []
            };
        }

        var rules = offered.Select(rule =>
        {
            var provenance = GameruleProvenance.Unknown;
            var value = "";
            if (reported.TryGetValue(rule.Name, out var live))
            {
                provenance = GameruleProvenance.ReportedByServer;
                value = live;
            }
            else if (queued.TryGetValue(rule.Name, out var pending))
            {
                provenance = GameruleProvenance.QueuedForNextStart;
                value = pending;
            }
            return new GameruleState
            {
                Name = rule.Name,
                Label = rule.Label,
                Description = rule.Description,
                Kind = rule.Kind,
                Value = value,
                DefaultValue = rule.DefaultValue,
                Provenance = provenance,
                Minimum = rule.Minimum,
                Maximum = rule.Maximum
            };
        }).ToArray();

        return new GameruleStateResponse
        {
            ServerId = serverId,
            ServerRunning = running,
            CanChange = running,
            UnavailableReason = running
                ? ""
                : "Game rules are read from the running server. Start the server to see and change them.",
            Rules = rules
        };
    }

    private async Task<Dictionary<string, string>> ReadQueuedGamerulesAsync(
        Guid serverId,
        CancellationToken cancellationToken)
    {
        var json = await store.GetSettingAsync($"pending-gamerules:{serverId:D}", cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, ProtocolJson.Options)
                is { } parsed
                ? new Dictionary<string, string>(parsed, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task<BackupRecord> FindBackupAsync(Guid id, CancellationToken cancellationToken)
    {
        return (await store.GetBackupsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(record => record.Id == id)
            ?? throw new KeyNotFoundException($"Backup {id} was not found.");
    }

    private async Task<ServerImportInspection> InspectServerImportAsync(
        string nativePath,
        CancellationToken cancellationToken)
    {
        if (File.Exists(nativePath))
            return await importInspection.InspectFileAsync(nativePath, cancellationToken).ConfigureAwait(false);
        if (!Directory.Exists(nativePath))
            throw new FileNotFoundException("The selected server file or folder no longer exists.");
        var result = await detector.DetectAsync(nativePath, cancellationToken).ConfigureAwait(false);
        var relativeCandidates = result.Candidates.Select(candidate => Path.GetRelativePath(result.RootPath,
            candidate.SourcePath).Replace('\\', '/')).ToArray();
        var files = EnumerateImportFiles(result.RootPath).ToArray();
        long size = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("The selected server folder contains a link or reparse point and cannot be copied safely.");
            size = checked(size + new FileInfo(file).Length);
            if (size > ServerImportInspectionService.MaximumExpandedBytes)
                throw new InvalidDataException("The selected folder exceeds ChunkPilot's 16 GB managed-copy review limit.");
        }
        return new ServerImportInspection
        {
            SourceKind = ServerImportSourceKind.ServerFolder,
            DisplayName = result.SuggestedName,
            Platform = result.Ecosystem.ToString(),
            MinecraftVersion = result.MinecraftVersion,
            LoaderVersion = result.LoaderVersion,
            RequiredJavaMajor = JavaRuntimePolicy.TryRequiredMajorForMinecraft(result.MinecraftVersion) ?? 21,
            SourceSizeBytes = size,
            ExpandedSizeBytes = size,
            FileCount = files.Length,
            ModCount = files.Count(file => Path.GetDirectoryName(file)?.Contains($"{Path.DirectorySeparatorChar}mods", StringComparison.OrdinalIgnoreCase) == true),
            PluginCount = files.Count(file => Path.GetDirectoryName(file)?.Contains($"{Path.DirectorySeparatorChar}plugins", StringComparison.OrdinalIgnoreCase) == true),
            ContainsWorld = files.Any(file => Path.GetFileName(file).Equals("level.dat", StringComparison.OrdinalIgnoreCase)),
            ServerRoot = ".",
            LaunchCandidates = relativeCandidates,
            CanInstall = relativeCandidates.Length > 0,
            CanReference = relativeCandidates.Length > 0,
            Limitation = relativeCandidates.Length > 0 ? "" : "No safe server launcher was found in the selected folder.",
            Warnings = ["By-reference keeps every source file in place. Managed copy leaves the source unchanged."]
        };
    }

    private static IEnumerable<string> EnumerateImportFiles(string root)
    {
        var pending = new Queue<string>();
        pending.Enqueue(Path.GetFullPath(root));
        var count = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("The selected server folder contains a directory link or reparse point and cannot be copied safely.");
                pending.Enqueue(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (++count > 200_000)
                    throw new InvalidDataException("The selected folder exceeds ChunkPilot's 200,000-file import review limit.");
                yield return file;
            }
        }
    }

    private static T Deserialize<T>(AgentRequest request) =>
        request.Payload.Deserialize<T>(ProtocolJson.Options) ??
        throw new JsonException($"Request payload for {request.Operation} was invalid.");
}
