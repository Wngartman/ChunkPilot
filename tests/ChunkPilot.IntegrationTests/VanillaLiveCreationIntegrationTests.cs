using System.IO.Compression;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The whole live Vanilla path, end to end, with nothing real behind it except the code under test.
/// </summary>
/// <remarks>
/// <para>
/// A real <see cref="AgentPipeServer"/> on a real named pipe, the real
/// <see cref="InstallationCoordinator"/>, the real hardened creation transaction and the real store —
/// with fake HTTP standing in for Mojang and Adoptium and a temporary root standing in for the user's
/// PC. No provider is contacted, and the only files written are under the test's own directory.
/// </para>
/// <para>
/// What this proves that a unit test cannot: the operation names and payload shapes the App sends
/// really are the ones the Agent answers, one submission really does produce exactly one registered
/// server, and an interrupted attempt really is finished — with the same version and the same runtime
/// — by the recovery pass rather than by a second creation.
/// </para>
/// </remarks>
[Collection("Agent pipe")]
public sealed class VanillaLiveCreationIntegrationTests : IDisposable
{
    private const string Version = "9.4";
    private const string MetadataUrl = "https://piston-meta.mojang.com/v1/packages/fixture/9.4.json";
    private const string ServerUrl = "https://piston-data.mojang.com/v1/objects/fixture/server.jar";

    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-live-vanilla-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    // ------------------------------------------------------------------ the whole path

    [Fact(Timeout = 60_000)]
    public async Task One_approved_plan_becomes_exactly_one_verified_registered_server()
    {
        await using var agent = await Harness.StartAsync(root);

        var catalog = await agent.SendAsync<VanillaVersionCatalog>("VanillaVersions", new VanillaCatalogRequest());
        var chosen = catalog.Stable.Single(option => option.VersionId == Version);
        Assert.True(catalog.ProviderAvailable);
        Assert.True(chosen.IsSelectable);
        Assert.Equal(25, chosen.RequiredJavaMajor);
        Assert.Equal(JavaRequirementSource.OfficialMetadata, chosen.JavaRequirementSource);
        Assert.Equal(agent.ServerSha1, chosen.ServerSha1);

        var destination = await agent.SendAsync<VanillaDestinationPreview>(
            "VanillaDestination", new VanillaDestinationRequest("Sunday survival"));
        Assert.True(destination.IsAvailable);
        Assert.Equal("Sunday-survival", destination.FolderName);

        var plan = new VanillaCreationPlan
        {
            ServerName = "Sunday survival",
            Version = chosen,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            },
            Port = 25_570,
            NetworkingPreference = VanillaNetworkingPreference.FriendsOverInternet,
            MetadataRetrievedUtc = catalog.RetrievedUtc
        };

        var started = await agent.SendAsync<InstallOperationRequest>(
            "BeginVanillaCreation", new BeginVanillaCreationRequest(plan));
        var snapshot = await agent.WaitForTerminalAsync(started.OperationId);

        Assert.True(snapshot.Success, snapshot.Error);
        Assert.Equal(CreationOutcome.Completed, snapshot.Outcome);
        Assert.Equal(CreationStage.Completed, snapshot.Progress.Stage);

        // Exactly one server, with the exact version that was chosen.
        var servers = await agent.Store.GetServersAsync();
        var created = Assert.Single(servers);
        Assert.Equal("Sunday survival", created.Name);
        Assert.Equal(Version, created.MinecraftVersion);
        Assert.Equal(ServerEcosystem.Vanilla, created.Ecosystem);
        Assert.True(created.IsManaged);
        Assert.Equal(25_570, created.Port);
        Assert.Equal(VanillaNetworkingPreference.FriendsOverInternet, created.CreationNetworkingPreference);
        Assert.Equal(destination.CanonicalDestination, Path.GetFullPath(created.RootPath));
        Assert.Contains("server-port=25570", await File.ReadAllTextAsync(
            Path.Combine(created.RootPath, "server.properties")), StringComparison.Ordinal);

        // The creation preference becomes the next visible method, not consent or exposure.
        var network = await agent.SendAsync<NetworkConfiguration>(
            "GetNetworkConfiguration", new ServerIdRequest(created.Id));
        Assert.Equal(NetworkMode.PortForwarding, network.Mode);
        Assert.Equal(25_570, network.JavaPort);
        Assert.Empty(await agent.Store.GetRouterMappingsAsync());
        Assert.Empty(await agent.Store.GetFirewallAccessRecordsAsync());

        // Exactly one managed runtime was obtained, it is the one the server launches with, and it
        // lives inside ChunkPilot's own folder rather than anywhere on the system. Its reported major
        // version is whatever the fixture binary says, so that is not asserted here; the requirement
        // that drove the request is asserted above, from the metadata.
        var runtimes = await agent.Store.GetManagedJavaRuntimesAsync();
        var runtime = Assert.Single(runtimes);
        Assert.True(runtime.IsManaged);
        Assert.Equal(Path.GetFullPath(runtime.JavaPath), created.Executable);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(root, "data", "ManagedJava")), runtime.JavaPath,
            StringComparison.OrdinalIgnoreCase);

        // eula=true exists only inside the created server, and nowhere else under the root.
        var eulaFiles = Directory.EnumerateFiles(root, "eula.txt", SearchOption.AllDirectories).ToArray();
        var eulaFile = Assert.Single(eulaFiles);
        Assert.Equal(Path.Combine(created.RootPath, "eula.txt"), eulaFile);
        Assert.Contains("eula=true", await File.ReadAllTextAsync(eulaFile), StringComparison.Ordinal);

        // Cleanup followed policy: no journal, no marker, no staging left behind.
        Assert.Empty(await agent.Store.GetCreationJournalsAsync());
        Assert.False(File.Exists(Path.Combine(created.RootPath, CreationOwnershipMarker.FileName)));
        Assert.Empty(Directory.EnumerateDirectories(agent.ManagedServersRoot, ".chunkpilot-staging-*"));

        // The server exists and is stopped: nothing was started on the user's behalf.
        Assert.True(File.Exists(Path.Combine(created.RootPath, "server.jar")));
        var dashboard = await agent.SendAsync<DashboardSnapshot>("Dashboard");
        Assert.All(dashboard.Servers, entry => Assert.Equal(ServerState.Stopped, entry.State));
    }

    [Fact(Timeout = 60_000)]
    public async Task The_same_operation_cannot_be_submitted_twice()
    {
        await using var agent = await Harness.StartAsync(root);
        var plan = await agent.ApprovedPlanAsync("Double click");

        var first = await agent.SendAsync<InstallOperationRequest>(
            "BeginVanillaCreation", new BeginVanillaCreationRequest(plan));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.SendAsync<InstallOperationRequest>(
                "BeginVanillaCreation", new BeginVanillaCreationRequest(plan)));
        Assert.Contains("already been started", refusal.Message, StringComparison.OrdinalIgnoreCase);

        var snapshot = await agent.WaitForTerminalAsync(first.OperationId);
        Assert.True(snapshot.Success, snapshot.Error);
        Assert.Single(await agent.Store.GetServersAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task An_unaccepted_plan_is_refused_before_anything_is_downloaded_or_written()
    {
        await using var agent = await Harness.StartAsync(root);
        var plan = await agent.ApprovedPlanAsync("No consent");
        var unaccepted = plan with { Eula = new VanillaEulaAcceptance() };

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.SendAsync<InstallOperationRequest>(
                "BeginVanillaCreation", new BeginVanillaCreationRequest(unaccepted)));

        Assert.Contains("EULA was not accepted", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await agent.Store.GetServersAsync());
        Assert.Empty(await agent.Store.GetCreationJournalsAsync());
        Assert.Empty(Directory.EnumerateFiles(root, "eula.txt", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(agent.ManagedServersRoot, "No-consent")));
    }

    [Fact(Timeout = 60_000)]
    public async Task An_acceptance_without_a_timestamp_is_not_an_acceptance()
    {
        await using var agent = await Harness.StartAsync(root);
        var plan = await agent.ApprovedPlanAsync("Ticked but unrecorded");
        var incomplete = plan with
        {
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.SendAsync<InstallOperationRequest>(
                "BeginVanillaCreation", new BeginVanillaCreationRequest(incomplete)));
        Assert.Empty(await agent.Store.GetServersAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task A_running_creation_can_be_found_again_after_the_app_disconnects()
    {
        await using var agent = await Harness.StartAsync(root);
        var plan = await agent.ApprovedPlanAsync("Reattach");

        var started = await agent.SendAsync<InstallOperationRequest>(
            "BeginVanillaCreation", new BeginVanillaCreationRequest(plan));
        await agent.WaitForTerminalAsync(started.OperationId);

        // A brand-new connection, exactly as a reopened window would make.
        var creations = await agent.SendAsync<VanillaCreationsResult>("VanillaCreations");
        var found = Assert.Single(creations.Operations);
        Assert.Equal(started.OperationId, found.OperationId);
        Assert.True(found.IsTerminal);
        Assert.Single(await agent.Store.GetServersAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task A_name_whose_folder_is_taken_is_refused_rather_than_quietly_renamed()
    {
        await using var agent = await Harness.StartAsync(root);
        Directory.CreateDirectory(Path.Combine(agent.ManagedServersRoot, "Taken"));
        await File.WriteAllTextAsync(
            Path.Combine(agent.ManagedServersRoot, "Taken", "something.txt"), "not ours");

        var preview = await agent.SendAsync<VanillaDestinationPreview>(
            "VanillaDestination", new VanillaDestinationRequest("Taken"));

        Assert.False(preview.IsAvailable);
        Assert.Equal(CreationDestinationVerdict.BlockedNotEmpty, preview.Verdict);
        Assert.Equal("Taken", preview.FolderName);
        Assert.DoesNotContain("Taken-2", preview.CanonicalDestination, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ interruption and recovery

    [Fact(Timeout = 60_000)]
    public async Task An_interruption_after_promotion_is_finished_by_recovery_with_the_same_version_and_runtime()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();

        var entry = await InterruptedAfterPromotionAsync(store, paths, withEulaEvidence: true);

        var reports = await new ServerCreationRecoveryService(store).RecoverAsync();

        var report = Assert.Single(reports);
        Assert.Equal(CreationOutcome.Completed, report.Outcome);
        var created = Assert.Single(await store.GetServersAsync());
        Assert.Equal(entry.ServerId, created.Id);
        Assert.Equal(Version, created.MinecraftVersion);
        Assert.Equal(entry.PlannedDefinition!.Executable, created.Executable);
        Assert.Empty(await store.GetCreationJournalsAsync());
    }

    [Fact(Timeout = 60_000)]
    public async Task Recovery_refuses_to_finish_a_creation_whose_consent_evidence_is_missing()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();

        var entry = await InterruptedAfterPromotionAsync(store, paths, withEulaEvidence: false);

        var reports = await new ServerCreationRecoveryService(store).RecoverAsync();

        var report = Assert.Single(reports);
        Assert.Equal(CreationRecoveryDisposition.AttentionRequired, report.Disposition);
        Assert.Equal(CreationOutcome.ActivatedRegistrationIncomplete, report.Outcome);
        Assert.Contains("EULA", report.Detail, StringComparison.OrdinalIgnoreCase);

        // Nothing was registered and nothing was deleted: the evidence is preserved for a person.
        Assert.Empty(await store.GetServersAsync());
        Assert.True(Directory.Exists(entry.CanonicalDestination));
        var preserved = Assert.Single(await store.GetCreationJournalsAsync());
        Assert.Equal(CreationPhase.RecoveryRequired, preserved.Entry!.Phase);
    }

    [Fact(Timeout = 60_000)]
    public async Task Running_recovery_twice_produces_the_same_single_server()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        _ = await InterruptedAfterPromotionAsync(store, paths, withEulaEvidence: true);

        _ = await new ServerCreationRecoveryService(store).RecoverAsync();
        _ = await new ServerCreationRecoveryService(store).RecoverAsync();

        Assert.Single(await store.GetServersAsync());
    }

    /// <summary>
    /// Reproduces the exact state a crash between promotion and registration leaves behind.
    /// </summary>
    /// <remarks>
    /// The folder is in place and carries this operation's marker, the journal says activation
    /// completed, and no server row exists. Whether the accepted EULA file travelled with the
    /// candidate is the variable under test.
    /// </remarks>
    private static async Task<CreationJournalEntry> InterruptedAfterPromotionAsync(
        ChunkPilotStore store,
        AppDataPaths paths,
        bool withEulaEvidence)
    {
        var operationId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var destination = Path.Combine(paths.ManagedServers, "Interrupted");
        var staging = Path.Combine(paths.ManagedServers, ServerCreationTransaction.StagingFolderName(operationId));
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "server.jar"), "fixture jar");
        if (withEulaEvidence)
            await File.WriteAllTextAsync(Path.Combine(destination, "eula.txt"), "eula=true\r\n");
        await CreationOwnershipMarker.WriteAsync(destination, new CreationOwnershipMarker(
            CreationOwnershipMarker.CurrentSchemaVersion, operationId, serverId,
            Path.GetFullPath(destination), DateTimeOffset.UtcNow), CancellationToken.None);

        var entry = new CreationJournalEntry
        {
            OperationId = operationId,
            ServerId = serverId,
            CreationKind = "Vanilla",
            ServerName = "Interrupted",
            CanonicalDestination = Path.GetFullPath(destination),
            CanonicalStaging = Path.GetFullPath(staging),
            InstanceRoot = Path.GetFullPath(paths.ManagedServers),
            StartedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Phase = CreationPhase.Activated,
            LastCompletedCheckpoint = CreationPhase.Activated,
            ActivationBegan = true,
            ActivationCompleted = true,
            ActivationMode = CreationActivationMode.DirectoryMove,
            OwnershipMarkerFile = CreationOwnershipMarker.FileName,
            EulaAcceptedUtc = DateTimeOffset.UtcNow,
            EulaSourceUrl = VanillaEulaAcceptance.OfficialSourceUrl,
            PlannedDefinition = new ServerDefinition
            {
                Id = serverId,
                Name = "Interrupted",
                RootPath = Path.GetFullPath(destination),
                WorkingDirectory = Path.GetFullPath(destination),
                Executable = Path.Combine(paths.ManagedJava, "temurin-25", "bin", "java.exe"),
                Arguments = "-jar server.jar nogui",
                Ecosystem = ServerEcosystem.Vanilla,
                MinecraftVersion = Version,
                IsManaged = true,
                ManagedInstanceRoot = Path.GetFullPath(paths.ManagedServers)
            }
        };
        await store.UpsertCreationJournalAsync(entry);
        return entry;
    }

    // ------------------------------------------------------------------ harness

    /// <summary>
    /// A real Agent pipe server, wired exactly as the Agent's own bootstrap wires it, with fake HTTP.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task server;
        private readonly string previousInstanceId;

        private Harness(ServiceProvider provider, string pipeName, string previousInstanceId, string sha1)
        {
            this.provider = provider;
            this.previousInstanceId = previousInstanceId;
            PipeName = pipeName;
            ServerSha1 = sha1;
            Store = provider.GetRequiredService<ChunkPilotStore>();
            ManagedServersRoot = provider.GetRequiredService<AppDataPaths>().ManagedServers;
            server = provider.GetRequiredService<AgentPipeServer>().RunAsync(shutdown.Token);
        }

        public string PipeName { get; }

        public string ServerSha1 { get; }

        public ChunkPilotStore Store { get; }

        public string ManagedServersRoot { get; }

        public static async Task<Harness> StartAsync(string root)
        {
            var instanceId = Guid.NewGuid().ToString("N");
            var previous = Environment.GetEnvironmentVariable("CHUNKPILOT_INSTANCE_ID") ?? "";
            // The pipe name is derived from this variable, so the in-process server and the client
            // agree on a name no other test or real session uses. Restored on disposal.
            Environment.SetEnvironmentVariable("CHUNKPILOT_INSTANCE_ID", instanceId);

            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            paths.EnsureCreated();
            Directory.CreateDirectory(paths.ManagedServers);

            var runtimeArchive = BuildRuntimeArchive();
            var serverJar = Encoding.UTF8.GetBytes("fixture minecraft server jar");
#pragma warning disable CA5350 // Mojang publishes SHA-1 for server jars; the fixture mirrors that.
            var sha1 = Convert.ToHexString(SHA1.HashData(serverJar));
#pragma warning restore CA5350
            var handler = new MojangAndAdoptiumHandler(runtimeArchive, serverJar, sha1);
            var http = new HttpClient(handler);

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Warning));
            services.AddSingleton(paths);
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
            services.AddSingleton(_ => new ServerDownloadCatalog(http));
            services.AddSingleton(serviceProvider => new PaperVersionCatalogService(
                serviceProvider.GetRequiredService<AppDataPaths>(), http));
            services.AddSingleton(serviceProvider => new ManagedLoaderCatalogService(
                serviceProvider.GetRequiredService<AppDataPaths>(), http));
            services.AddSingleton(serviceProvider => new VanillaVersionCatalogService(
                serviceProvider.GetRequiredService<AppDataPaths>(), http));
            services.AddSingleton(serviceProvider => new ManagedServerInstaller(
                serviceProvider.GetRequiredService<AppDataPaths>(),
                serviceProvider.GetRequiredService<ChunkPilotStore>(),
                serviceProvider.GetRequiredService<ServerDownloadCatalog>(),
                http,
                serviceProvider.GetRequiredService<LoaderInstallationService>(),
                serviceProvider.GetRequiredService<ServerCreationTransaction>()));
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
            services.AddSingleton<IManagedJavaPackageProvider>(_ => new FixtureJavaProvider(
                Convert.ToHexString(SHA256.HashData(runtimeArchive))));
            services.AddSingleton(serviceProvider => new ManagedJavaRuntimeService(
                serviceProvider.GetRequiredService<AppDataPaths>(),
                serviceProvider.GetRequiredService<ChunkPilotStore>(),
                serviceProvider.GetRequiredService<IManagedJavaPackageProvider>(),
                http));
            services.AddSingleton<LoaderMetadataService>();
            services.AddSingleton<LoaderInstallationService>();
            services.AddSingleton<IGuidedCatalogProvider>(serviceProvider =>
                new BuiltInServerCatalogProvider(CatalogProvider.Mojang, InstallSourceType.Vanilla,
                    serviceProvider.GetRequiredService<ServerDownloadCatalog>()));
            services.AddSingleton<GuidedCatalogService>();
            services.AddSingleton<IUpdateProviderAdapter, LocalPackageHistoryUpdateProvider>();
            services.AddSingleton<UpdateProviderRegistry>();
            services.AddSingleton<UpdateSourceDetector>();
            services.AddSingleton<PackUpdateCompatibilityService>();
            services.AddSingleton<PackMigrationPlanner>();
            services.AddSingleton<VersionSnapshotService>();
            services.AddSingleton<ServerPackUpdateService>();
            services.AddSingleton<ServerCreationTransaction>();
            services.AddSingleton<ServerCreationRecoveryService>();
            services.AddSingleton<ServerSupervisor>();
            services.AddSingleton<ServerDeletionCoordinator>();
            services.AddSingleton<InstallationCoordinator>();
            services.AddSingleton<ManagedContentOperationCoordinator>();
            services.AddSingleton<ServerUpdateCoordinator>();
            // Router mapping is wired so the pipe surface is complete, but the network view is empty:
            // no gateway can be resolved, so this harness cannot send a packet to any real router.
            services.AddSingleton(_ => new RouterMappingOptions());
            services.AddSingleton<IRouterNetworkView, NoRoutersView>();
            services.AddSingleton<RouterMappingService>();
            services.AddSingleton<RouterMappingCoordinator>();
            services.AddSingleton<IUiProcessObserver, SystemUiProcessObserver>();
            services.AddSingleton<UiSessionAuthority>();
            services.AddSingleton<PublicConnectivityLeaseRegistry>();
            // Firewall routes are part of the Agent surface too. Keep this harness entirely read-only:
            // the policy is unavailable, there are no network categories, and no executable is trusted.
            services.AddSingleton<IWindowsFirewallPolicyReader, NoFirewallPolicy>();
            services.AddSingleton<INetworkCategoryView, NoNetworkCategories>();
            services.AddSingleton(serviceProvider => new WindowsFirewallTargetResolver(
                serviceProvider.GetRequiredService<IRouterNetworkView>(),
                serviceProvider.GetRequiredService<INetworkCategoryView>(),
                _ => false));
            services.AddSingleton<WindowsFirewallCoordinator>();
            // External reachability completes the pipe surface with no endpoint configured at all, so
            // this harness cannot contact any service even if a route were exercised.
            services.AddSingleton(_ => ExternalReachabilityProbeOptions.Configure(null));
            services.AddSingleton<IExternalReachabilityProbe>(serviceProvider =>
                new HttpExternalReachabilityProbe(
                    serviceProvider.GetRequiredService<ExternalReachabilityProbeOptions>()));
            services.AddSingleton<ExternalReachabilityCoordinator>();
            services.AddSingleton<PublicConnectivityCoordinator>();
            services.AddSingleton<AgentPipeServer>();

            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<ChunkPilotStore>().InitializeAsync();
            await provider.GetRequiredService<ServerSupervisor>().InitializeAsync();

            var harness = new Harness(provider, ChunkPilotConstants.PipeNameFor(instanceId), previous, sha1);
            await harness.WaitForPipeAsync();
            return harness;
        }

        /// <summary>Builds a plan that would pass every check, so a test can vary exactly one thing.</summary>
        public async Task<VanillaCreationPlan> ApprovedPlanAsync(string serverName)
        {
            var catalog = await SendAsync<VanillaVersionCatalog>("VanillaVersions", new VanillaCatalogRequest());
            return new VanillaCreationPlan
            {
                ServerName = serverName,
                Version = catalog.Stable.Single(option => option.VersionId == Version),
                Eula = new VanillaEulaAcceptance
                {
                    Accepted = true,
                    AcceptedAtUtc = DateTimeOffset.UtcNow,
                    SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
                }
            };
        }

        public async Task<InstallOperationSnapshot> WaitForTerminalAsync(Guid operationId)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var snapshot = await SendAsync<InstallOperationSnapshot>(
                    "InstallProgress", new InstallOperationRequest(operationId));
                if (snapshot.IsTerminal)
                    return snapshot;
                await Task.Delay(50);
            }
            throw new TimeoutException($"Operation {operationId} did not finish.");
        }

        public async Task<T> SendAsync<T>(string operation, object? payload = null)
        {
            using var pipe = new NamedPipeClientStream(".", PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5_000);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65_536, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 65_536, leaveOpen: true)
            {
                AutoFlush = true
            };
            var request = new AgentRequest
            {
                Operation = operation,
                Payload = JsonSerializer.SerializeToElement(payload ?? new { }, ProtocolJson.Options)
            };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, ProtocolJson.Options));
            var response = JsonSerializer.Deserialize<AgentResponse>(await reader.ReadLineAsync() ?? "",
                               ProtocolJson.Options) ?? throw new IOException("Invalid response.");
            if (!response.Success)
                throw new InvalidOperationException(response.Error);
            return response.Payload!.Value.Deserialize<T>(ProtocolJson.Options)
                   ?? throw new IOException("Invalid payload.");
        }

        private async Task WaitForPipeAsync()
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    _ = await SendAsync<OperationResult>("Ping");
                    return;
                }
                catch (Exception exception) when (exception is IOException or TimeoutException)
                {
                    await Task.Delay(50);
                }
            }
            throw new TimeoutException("The in-process agent did not open its pipe.");
        }

        public async ValueTask DisposeAsync()
        {
            await shutdown.CancelAsync();
            try
            {
                // One connection so the accept loop notices the cancellation instead of blocking.
                using var poke = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                await poke.ConnectAsync(200);
            }
            catch (Exception exception) when (exception is IOException or TimeoutException) { }
            try
            {
                await server.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
            shutdown.Dispose();
            await provider.DisposeAsync();
            Environment.SetEnvironmentVariable("CHUNKPILOT_INSTANCE_ID",
                previousInstanceId.Length == 0 ? null : previousInstanceId);
        }

        /// <summary>
        /// A runtime archive whose java.exe is a real executable the runtime service can inspect.
        /// </summary>
        /// <remarks>
        /// The managed-runtime service runs the binary to read its version, so a text file named
        /// java.exe would fail for the wrong reason. This reuses the repository's existing fake-server
        /// executable, exactly as the older loader fixtures do.
        /// </remarks>
        private static byte[] BuildRuntimeArchive()
        {
            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var directory = Path.GetDirectoryName(FakeJavaPath())!;
                foreach (var source in Directory.EnumerateFiles(directory)
                             .Where(path => Path.GetFileName(path)
                                 .StartsWith("ChunkPilot.FakeServer", StringComparison.OrdinalIgnoreCase)))
                {
                    var fileName = Path.GetFileName(source);
                    if (fileName.Equals("ChunkPilot.FakeServer.exe", StringComparison.OrdinalIgnoreCase))
                        fileName = "java.exe";
                    var entry = archive.CreateEntry("temurin/bin/" + fileName);
                    using var input = File.OpenRead(source);
                    using var target = entry.Open();
                    input.CopyTo(target);
                }
            }
            return buffer.ToArray();
        }

        private static string FakeJavaPath() =>
            Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0",
                "ChunkPilot.FakeServer.exe");

        private static string RepositoryRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
                current = current.Parent;
            return current?.FullName ?? throw new DirectoryNotFoundException();
        }
    }

    /// <summary>No adapters, so no gateway, so no router can be contacted from this harness.</summary>
    private sealed class NoRoutersView : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() => [];
    }

    private sealed class NoFirewallPolicy : IWindowsFirewallPolicyReader
    {
        public FirewallPolicySnapshot Read() => FirewallPolicySnapshot.Unavailable("Fixture has no firewall.");
    }

    private sealed class NoNetworkCategories : INetworkCategoryView
    {
        public IReadOnlyList<NetworkCategoryBinding> Enumerate() => [];
    }

    private sealed class FixtureJavaProvider(string sha256) : IManagedJavaPackageProvider
    {
        public Task<ManagedJavaPackage> ResolveAsync(
            int majorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedJavaPackage
            {
                MajorVersion = majorVersion,
                Version = $"jdk-{majorVersion}-fixture",
                Architecture = "x64",
                DownloadUrl = "https://fixture.invalid/temurin.zip",
                FileName = "temurin.zip",
                Sha256 = sha256
            });
    }

    /// <summary>
    /// Stands in for Mojang's manifest and version metadata and for the runtime download.
    /// </summary>
    /// <remarks>
    /// Shaped exactly like the real documents, including the <c>javaVersion.majorVersion</c> block
    /// and the <c>downloads.server</c> entry, and deliberately including one version that publishes
    /// no server download at all — the case the catalogue has to refuse rather than assume.
    /// </remarks>
    private sealed class MojangAndAdoptiumHandler(byte[] runtimeArchive, byte[] serverJar, string sha1)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("version_manifest", StringComparison.OrdinalIgnoreCase))
                return Json($$"""
                    {
                      "latest": { "release": "{{Version}}", "snapshot": "9.5-pre1" },
                      "versions": [
                        { "id": "{{Version}}", "type": "release", "url": "{{MetadataUrl}}",
                          "releaseTime": "2026-06-01T00:00:00+00:00" },
                        { "id": "9.5-pre1", "type": "snapshot", "url": "{{MetadataUrl}}",
                          "releaseTime": "2026-06-15T00:00:00+00:00" },
                        { "id": "0.1", "type": "old_alpha", "url": "{{MetadataUrl}}",
                          "releaseTime": "2010-06-15T00:00:00+00:00" }
                      ]
                    }
                    """);
            if (url.Equals(MetadataUrl, StringComparison.OrdinalIgnoreCase))
                return Json($$"""
                    {
                      "id": "{{Version}}",
                      "javaVersion": { "component": "java-runtime-fixture", "majorVersion": 25 },
                      "downloads": {
                        "server": { "sha1": "{{sha1}}", "size": {{serverJar.Length}}, "url": "{{ServerUrl}}" }
                      }
                    }
                    """);
            if (url.Equals(ServerUrl, StringComparison.OrdinalIgnoreCase))
                return Bytes(serverJar);
            if (url.Contains("temurin", StringComparison.OrdinalIgnoreCase))
                return Bytes(runtimeArchive);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            });

        private static Task<HttpResponseMessage> Bytes(byte[] value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(value)
            });
    }
}
