using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using var mutex = new Mutex(initiallyOwned: true, ChunkPilotConstants.AgentMutexName, out var createdNew);
if (!createdNew)
    return;

var services = new ServiceCollection();
services.AddLogging(builder => builder
    .SetMinimumLevel(LogLevel.Information)
    .AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
    }));
services.AddSingleton(_ => new AppDataPaths(
    Environment.GetEnvironmentVariable("CHUNKPILOT_DATA_ROOT"),
    Environment.GetEnvironmentVariable("CHUNKPILOT_MANAGED_SERVERS_ROOT")));
services.AddSingleton<ChunkPilotStore>();
services.AddSingleton<ProcessStatisticsProvider>();
services.AddSingleton<MinecraftStatusClient>();
services.AddSingleton<JavaDiscoveryService>();
services.AddSingleton<ServerDetectionService>();
services.AddSingleton<SafeFileService>();
services.AddSingleton<BackupService>();
services.AddSingleton<JarInventoryService>();
services.AddSingleton<IPluginCatalogProvider>(provider =>
    new ModrinthPluginProvider(provider.GetRequiredService<AppDataPaths>()));
services.AddSingleton<IPluginCatalogProvider, HangarUnavailablePluginProvider>();
services.AddSingleton<PluginProviderRegistry>();
services.AddSingleton<PluginManagementService>();
services.AddSingleton<DiagnosticsService>();
services.AddSingleton<ServerDownloadCatalog>();
services.AddSingleton<VanillaVersionCatalogService>();
services.AddSingleton<PaperVersionCatalogService>();
services.AddSingleton<ManagedLoaderCatalogService>();
services.AddSingleton<ManagedServerInstaller>();
services.AddSingleton<ServerIconService>();
services.AddSingleton<WorldManager>();
services.AddSingleton<WhitelistService>();
services.AddSingleton<ConnectionTestService>();
services.AddSingleton<RamArgumentService>();
services.AddSingleton<ISecretStore, DpapiSecretStore>();
services.AddSingleton<ServerCapabilityDetectionService>();
services.AddSingleton<CanonicalPathLockManager>();
services.AddSingleton<DatapackService>();
services.AddSingleton<DatapackManagementService>();
services.AddSingleton<ICrossplayPackageProvider, OfficialCrossplayPackageProvider>();
services.AddSingleton<CrossplayPackageService>();
services.AddSingleton<IManagedJavaPackageProvider, AdoptiumTemurinProvider>();
services.AddSingleton<ManagedJavaRuntimeService>();
services.AddSingleton<LoaderMetadataService>();
services.AddSingleton<LoaderInstallationService>();
services.AddSingleton<IGuidedCatalogProvider>(provider =>
    new BuiltInServerCatalogProvider(CatalogProvider.Mojang, InstallSourceType.Vanilla,
        provider.GetRequiredService<ServerDownloadCatalog>()));
services.AddSingleton<IGuidedCatalogProvider>(provider =>
    new BuiltInServerCatalogProvider(CatalogProvider.Paper, InstallSourceType.Paper,
        provider.GetRequiredService<ServerDownloadCatalog>()));
services.AddSingleton<IGuidedCatalogProvider>(provider =>
    new BuiltInServerCatalogProvider(CatalogProvider.Purpur, InstallSourceType.Purpur,
        provider.GetRequiredService<ServerDownloadCatalog>()));
services.AddSingleton<IGuidedCatalogProvider, ModrinthCatalogProvider>();
services.AddSingleton<IGuidedCatalogProvider, CurseForgeCatalogProvider>();
services.AddSingleton<IGuidedCatalogProvider>(_ =>
    new UnavailableCatalogProvider(CatalogProvider.Ftb,
        "Official FTB browsing is unavailable because no supported public server-pack API is configured."));
services.AddSingleton<GuidedCatalogService>();
services.AddSingleton<IUpdateProviderAdapter, PaperMcUpdateProvider>();
services.AddSingleton<IUpdateProviderAdapter, ManagedLoaderUpdateProvider>();
services.AddSingleton<IUpdateProviderAdapter, ModrinthUpdateProvider>();
services.AddSingleton<IUpdateProviderAdapter, CurseForgeUpdateProvider>();
services.AddSingleton<IUpdateProviderAdapter, GitHubReleasesUpdateProvider>();
services.AddSingleton<IUpdateProviderAdapter, DirectManifestUpdateProvider>();
services.AddSingleton<IUpdateProviderAdapter, LocalPackageHistoryUpdateProvider>();
services.AddSingleton<UpdateProviderRegistry>();
services.AddSingleton<UpdateSourceDetector>();
services.AddSingleton<PackUpdateCompatibilityService>();
services.AddSingleton<PackMigrationPlanner>();
services.AddSingleton<VersionSnapshotService>();
services.AddSingleton<ServerPackUpdateService>();
services.AddSingleton<ServerCreationTransaction>();
services.AddSingleton<ServerCreationRecoveryService>();
// Router port mapping. Nothing here acts on its own: the providers are pure protocol implementations,
// and the coordinator only ever mutates a router for a server whose owner explicitly chose Direct
// internet and separately confirmed the exposure.
services.AddSingleton(_ => new RouterMappingOptions());
services.AddSingleton<IRouterNetworkView, SystemRouterNetworkView>();
services.AddSingleton<IGatewayDatagramChannel, UdpGatewayDatagramChannel>();
services.AddSingleton<ISsdpSearchChannel, SsdpSearchChannel>();
services.AddSingleton<IUpnpControlChannel>(provider =>
    new UpnpControlChannel(provider.GetRequiredService<RouterMappingOptions>()));
services.AddSingleton<IRouterMappingProvider>(provider => new PcpMappingProvider(
    provider.GetRequiredService<IGatewayDatagramChannel>(),
    provider.GetRequiredService<RouterMappingOptions>()));
services.AddSingleton<IRouterMappingProvider>(provider => new NatPmpMappingProvider(
    provider.GetRequiredService<IGatewayDatagramChannel>(),
    provider.GetRequiredService<RouterMappingOptions>()));
services.AddSingleton<IRouterMappingProvider>(provider => new UpnpIgdMappingProvider(
    provider.GetRequiredService<ISsdpSearchChannel>(),
    provider.GetRequiredService<IUpnpControlChannel>(),
    provider.GetRequiredService<RouterMappingOptions>()));
services.AddSingleton<RouterMappingService>();
services.AddSingleton<RouterMappingCoordinator>();
services.AddSingleton<IUiProcessObserver, SystemUiProcessObserver>();
services.AddSingleton<UiSessionAuthority>();
services.AddSingleton<PublicConnectivityLeaseRegistry>();
// Windows Firewall. The Agent only ever reads the firewall; every change goes through a separate
// one-shot elevated helper that the user has to approve, and nothing here can start one on its own.
services.AddSingleton<IWindowsFirewallPolicyReader, NetFwPolicyReader>();
services.AddSingleton<INetworkCategoryView, NetworkCategoryView>();
services.AddSingleton(provider => new WindowsFirewallTargetResolver(
    provider.GetRequiredService<IRouterNetworkView>(),
    provider.GetRequiredService<INetworkCategoryView>()));
services.AddSingleton<WindowsFirewallCoordinator>();
// External reachability. Optional, account-free and stateless: without a configured probe endpoint
// the feature truthfully reports that external verification is unavailable in this build, and even
// with one, nothing here contacts the service except the one deliberate "Check from outside" action.
services.AddSingleton(_ => ExternalReachabilityProbeOptions.FromEnvironment());
services.AddSingleton<IExternalReachabilityProbe>(provider =>
    new HttpExternalReachabilityProbe(provider.GetRequiredService<ExternalReachabilityProbeOptions>()));
services.AddSingleton<ExternalReachabilityCoordinator>();
services.AddSingleton<PublicConnectivityCoordinator>();
services.AddSingleton<RouterMappingWorker>();
services.AddSingleton<ServerSupervisor>();
services.AddSingleton<ManagedInstanceCopyService>();
services.AddSingleton<ServerDeletionCoordinator>();
services.AddSingleton<ManagedContentOperationCoordinator>();
services.AddSingleton<InstallationCoordinator>();
services.AddSingleton<ServerUpdateCoordinator>();
services.AddSingleton<AgentPipeServer>();
services.AddSingleton<SchedulerWorker>();
services.AddSingleton<AutomationWorker>();

await using var provider = services.BuildServiceProvider();
var store = provider.GetRequiredService<ChunkPilotStore>();
await store.InitializeAsync().ConfigureAwait(false);
var updateService = provider.GetRequiredService<ServerPackUpdateService>();
_ = await updateService.RecoverInterruptedOperationsAsync().ConfigureAwait(false);

// Creation is reconciled before the supervisor reads the server list, so an interrupted creation is
// either finished or provably discarded before anything could present it as a working server.
var creationRecovery = provider.GetRequiredService<ServerCreationRecoveryService>();
var creationReports = await creationRecovery.RecoverAsync().ConfigureAwait(false);
if (creationReports.Count > 0)
{
    var recoveryLog = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CreationRecovery");
    if (recoveryLog.IsEnabled(LogLevel.Information))
    {
        foreach (var report in creationReports)
            recoveryLog.LogInformation("Creation {Operation}: {Outcome}. {Detail}",
                report.OperationId, report.Outcome, report.Detail);
    }
}

var supervisor = provider.GetRequiredService<ServerSupervisor>();
await supervisor.InitializeAsync().ConfigureAwait(false);
var deletionRecovery = provider.GetRequiredService<ServerDeletionCoordinator>();
var ownershipReports = await deletionRecovery.ReconcileManagedOwnershipAsync().ConfigureAwait(false);
if (ownershipReports.Count > 0)
{
    var ownershipLog = provider.GetRequiredService<ILoggerFactory>().CreateLogger("ManagedOwnershipReconciliation");
    if (ownershipLog.IsEnabled(LogLevel.Information))
        foreach (var report in ownershipReports)
            ownershipLog.LogInformation("{Report}", report);
}
var deletionReports = await deletionRecovery.RecoverInterruptedAsync().ConfigureAwait(false);
if (deletionReports.Count > 0)
{
    var deletionLog = provider.GetRequiredService<ILoggerFactory>().CreateLogger("ServerDeletionRecovery");
    if (deletionLog.IsEnabled(LogLevel.Information))
        foreach (var report in deletionReports)
            deletionLog.LogInformation("{Report}", report);
}
var firewallCoordinator = provider.GetRequiredService<WindowsFirewallCoordinator>();
await firewallCoordinator.ReconcileAllAsync(CancellationToken.None).ConfigureAwait(false);
var publicConnectivity = provider.GetRequiredService<PublicConnectivityCoordinator>();
var staleRecovery = await publicConnectivity.RecoverStaleExposureAsync(CancellationToken.None)
    .ConfigureAwait(false);
var pipeServer = provider.GetRequiredService<AgentPipeServer>();
var scheduler = provider.GetRequiredService<SchedulerWorker>();
var automation = provider.GetRequiredService<AutomationWorker>();
var routerMappingWorker = provider.GetRequiredService<RouterMappingWorker>();
using var shutdown = new CancellationTokenSource();
pipeServer.ShutdownRequested += (_, _) => shutdown.Cancel();

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        supervisor.StopAllAsync(escalateOnFailure: true, cancellationToken: timeout.Token).GetAwaiter().GetResult();
    }
    catch (Exception) { }
};

try
{
    var restoration = staleRecovery.MayRestoreOrdinaryStartup
        ? supervisor.RestoreStartupStateAsync(staleRecovery.SuppressedServerIds, shutdown.Token)
        : Task.CompletedTask;
    await Task.WhenAll(
        pipeServer.RunAsync(shutdown.Token),
        scheduler.RunAsync(shutdown.Token),
        automation.RunAsync(shutdown.Token),
        routerMappingWorker.RunAsync(shutdown.Token),
        restoration).ConfigureAwait(false);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
