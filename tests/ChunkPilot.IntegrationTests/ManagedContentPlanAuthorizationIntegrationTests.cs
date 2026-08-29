using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

public sealed class ManagedContentPlanAuthorizationIntegrationTests
{
    [Fact]
    public async Task Coordinator_rejects_forgery_and_server_switch_then_installs_consumed_plan_without_resolving_again()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reviewed = await fixture.Plugins.PlanAsync(
            fixture.Primary,
            "10",
            "100",
            PluginProviderKind.CurseForge);
        Assert.Equal(2, fixture.Provider.ResolveCount);
        var authorization = fixture.Authorizations.Register(
            fixture.Primary.Id,
            PluginProviderKind.CurseForge,
            "10",
            "100",
            reviewed);

        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(Request(
            fixture.Secondary.Id, authorization)));
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(Request(
            fixture.Primary.Id, authorization, versionId: "101")));
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(Request(
            fixture.Primary.Id, authorization with { Digest = new string('f', 64) })));

        var request = Request(fixture.Primary.Id, authorization);
        var accepted = fixture.Coordinator.BeginInstall(request);
        var duplicateAcceptance = fixture.Coordinator.BeginInstall(request);
        var wrongServerReplay = Assert.Throws<InvalidOperationException>(() =>
            fixture.Coordinator.BeginInstall(request with { ServerId = fixture.Secondary.Id }));
        var restartMismatch = Assert.Throws<InvalidOperationException>(() =>
            fixture.Coordinator.BeginInstall(request with
            {
                RestartIfRunning = !request.RestartIfRunning
            }));
        var authorizationMismatch = Assert.Throws<InvalidOperationException>(() =>
            fixture.Coordinator.BeginInstall(request with
            {
                PlanAuthorization = authorization with { AuthorizationId = Guid.NewGuid() }
            }));
        var completed = await WaitForTerminalAsync(fixture.Coordinator, accepted.OperationId);

        Assert.Equal(accepted.OperationId, duplicateAcceptance.OperationId);
        Assert.Contains("another exact request", wrongServerReplay.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("another exact request", restartMismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("another exact request", authorizationMismatch.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(fixture.Primary.Id, fixture.Coordinator.Get(accepted.OperationId).ServerId);
        Assert.True(completed.Success, completed.Error);
        Assert.Equal(2, fixture.Provider.ResolveCount);
        Assert.Equal(2, fixture.Downloads.RequestCount);
        Assert.True(File.Exists(Path.Combine(fixture.Primary.RootPath, "mods", "library.jar")));
        Assert.True(File.Exists(Path.Combine(fixture.Primary.RootPath, "mods", "root.jar")));

        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(
            request with { OperationId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task Coordinator_requires_authorization_only_for_CurseForge_dependency_plan()
    {
        await using var fixture = await Fixture.CreateAsync();

        var failure = Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(new(
            fixture.Primary.Id,
            "10",
            "100",
            IncludeDependencies: true,
            OperationId: Guid.NewGuid(),
            Provider: PluginProviderKind.CurseForge)));
        Assert.Contains("authorization", failure.Message, StringComparison.OrdinalIgnoreCase);

        var modrinthRequest = new BeginManagedContentInstallRequest(
            fixture.Primary.Id,
            "project",
            "version",
            IncludeDependencies: true,
            OperationId: Guid.NewGuid(),
            Provider: PluginProviderKind.Modrinth);
        var accepted = fixture.Coordinator.BeginInstall(modrinthRequest);
        Assert.Equal(modrinthRequest.ServerId, accepted.ServerId);
    }

    [Fact]
    public async Task Cancellation_winning_before_delayed_begin_reserves_exact_request_and_revokes_plan()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reviewed = await fixture.Plugins.PlanAsync(
            fixture.Primary,
            "10",
            "100",
            PluginProviderKind.CurseForge);
        var authorization = fixture.Authorizations.Register(
            fixture.Primary.Id,
            PluginProviderKind.CurseForge,
            "10",
            "100",
            reviewed);
        var request = Request(fixture.Primary.Id, authorization);
        var revokeCalls = 0;

        var fenced = fixture.Coordinator.CancelOrFenceInstall(
            request,
            authorizationId =>
            {
                Interlocked.Increment(ref revokeCalls);
                fixture.Authorizations.Revoke(authorizationId);
            });
        var replayedFence = fixture.Coordinator.CancelOrFenceInstall(
            request,
            _ => throw new InvalidOperationException("A replayed fence must not revoke twice."));

        Assert.True(fenced.FenceEstablished);
        Assert.True(fenced.BlockedBeforeStart);
        Assert.NotNull(fenced.Operation);
        Assert.Equal(request.OperationId, fenced.Operation!.OperationId);
        Assert.True(fenced.Operation.IsTerminal);
        Assert.False(fenced.Operation.Success);
        Assert.Equal(ManagedContentOperationStage.Cancelled, fenced.Operation.Progress.Stage);
        Assert.True(replayedFence.FenceEstablished);
        Assert.True(replayedFence.BlockedBeforeStart);
        Assert.Equal(request.OperationId, replayedFence.Operation!.OperationId);
        Assert.Equal(1, revokeCalls);
        Assert.Equal(0, fixture.Authorizations.OutstandingCount);
        Assert.Equal(0, fixture.Downloads.RequestCount);
        Assert.Throws<InvalidOperationException>(() => fixture.Authorizations.Consume(
            request.ServerId,
            request.Provider,
            request.ProjectId,
            request.VersionId,
            authorization));

        // This is the delayed original Begin arriving after the App requested cancellation.
        var lateAcceptance = fixture.Coordinator.BeginInstall(request);
        Assert.Equal(request.OperationId, lateAcceptance.OperationId);
        Assert.True(lateAcceptance.IsTerminal);
        Assert.Equal(ManagedContentOperationStage.Cancelled, lateAcceptance.Progress.Stage);
        Assert.Equal(0, fixture.Downloads.RequestCount);

        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(
            request with { OperationId = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.CancelOrFenceInstall(
            request with { ServerId = fixture.Secondary.Id },
            authorizationId => fixture.Authorizations.Revoke(authorizationId)));
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.CancelOrFenceInstall(
            request with { RestartIfRunning = !request.RestartIfRunning }));
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.CancelOrFenceInstall(
            request with
            {
                PlanAuthorization = authorization with { AuthorizationId = Guid.NewGuid() }
            }));
        Assert.Equal(0, fixture.Downloads.RequestCount);
    }

    [Fact]
    public async Task Begin_winning_before_cancellation_cancels_registered_work_without_revoking_consumed_plan()
    {
        await using var fixture = await Fixture.CreateAsync(pauseDownloads: true);
        var reviewed = await fixture.Plugins.PlanAsync(
            fixture.Primary,
            "10",
            "100",
            PluginProviderKind.CurseForge);
        var authorization = fixture.Authorizations.Register(
            fixture.Primary.Id,
            PluginProviderKind.CurseForge,
            "10",
            "100",
            reviewed);
        var request = Request(fixture.Primary.Id, authorization);
        var revokeCalls = 0;

        var accepted = fixture.Coordinator.BeginInstall(request);
        await fixture.Downloads.WaitUntilRequestedAsync();
        var fenced = fixture.Coordinator.CancelOrFenceInstall(request, authorizationId =>
        {
            Interlocked.Increment(ref revokeCalls);
            fixture.Authorizations.Revoke(authorizationId);
        });
        var completed = await WaitForTerminalAsync(fixture.Coordinator, accepted.OperationId);

        Assert.True(fenced.FenceEstablished);
        Assert.False(fenced.BlockedBeforeStart);
        Assert.Equal(accepted.OperationId, fenced.Operation!.OperationId);
        Assert.Equal(ManagedContentOperationStage.Cancelled, completed.Progress.Stage);
        Assert.False(completed.Success);
        Assert.Equal(0, revokeCalls);
        Assert.Equal(0, fixture.Authorizations.OutstandingCount);
        Assert.Throws<InvalidOperationException>(() => fixture.Authorizations.Consume(
            request.ServerId,
            request.Provider,
            request.ProjectId,
            request.VersionId,
            authorization));

        var replay = fixture.Coordinator.BeginInstall(request);
        Assert.Equal(accepted.OperationId, replay.OperationId);
        Assert.Equal(ManagedContentOperationStage.Cancelled, replay.Progress.Stage);
    }

    [Fact]
    public async Task Cancellation_capacity_seals_unseen_begins_but_keeps_fencing_without_more_retained_state()
    {
        await using var fixture = await Fixture.CreateAsync(maximumPreCancelledOperations: 2);
        var first = new BeginManagedContentInstallRequest(
            fixture.Primary.Id,
            "first-project",
            "first-version",
            IncludeDependencies: false,
            OperationId: Guid.NewGuid(),
            Provider: PluginProviderKind.Modrinth);
        var lastRetained = first with
        {
            OperationId = Guid.NewGuid(),
            ProjectId = "last-retained-project",
            VersionId = "last-retained-version"
        };

        Assert.True(fixture.Coordinator.CancelOrFenceInstall(first).FenceEstablished);
        Assert.True(fixture.Coordinator.CancelOrFenceInstall(lastRetained).FenceEstablished);

        var knownReplay = fixture.Coordinator.BeginInstall(first);
        Assert.True(knownReplay.IsTerminal);
        Assert.Equal(ManagedContentOperationStage.Cancelled, knownReplay.Progress.Stage);
        Assert.Throws<InvalidOperationException>(() => fixture.Coordinator.BeginInstall(
            first with { ProjectId = "mismatched-known-request" }));

        var disappearedServerCancellation = first with
        {
            OperationId = Guid.NewGuid(),
            ServerId = Guid.NewGuid(),
            ProjectId = "disappeared-server-project",
            VersionId = "disappeared-server-version"
        };
        var disappearedServerFence = fixture.Coordinator.CancelOrFenceInstall(
            disappearedServerCancellation);
        Assert.True(disappearedServerFence.FenceEstablished);
        Assert.True(disappearedServerFence.BlockedBeforeStart);
        Assert.True(disappearedServerFence.Operation!.IsTerminal);
        Assert.Equal(
            disappearedServerCancellation.ServerId,
            disappearedServerFence.Operation.ServerId);

        var reviewed = await fixture.Plugins.PlanAsync(
            fixture.Primary,
            "10",
            "100",
            PluginProviderKind.CurseForge);
        var authorization = fixture.Authorizations.Register(
            fixture.Primary.Id,
            PluginProviderKind.CurseForge,
            "10",
            "100",
            reviewed);
        var afterSeal = Request(fixture.Primary.Id, authorization);
        var revokeCalls = 0;

        ManagedContentCancellationFenceResult FenceAfterSeal() =>
            fixture.Coordinator.CancelOrFenceInstall(afterSeal, authorizationId =>
            {
                Interlocked.Increment(ref revokeCalls);
                fixture.Authorizations.Revoke(authorizationId);
            });

        var fenced = FenceAfterSeal();
        var replayedFence = FenceAfterSeal();

        Assert.True(fenced.FenceEstablished);
        Assert.True(fenced.BlockedBeforeStart);
        Assert.True(fenced.Operation!.IsTerminal);
        Assert.False(fenced.Operation.Success);
        Assert.Equal(afterSeal.OperationId, fenced.Operation.OperationId);
        Assert.Equal(ManagedContentOperationStage.Cancelled, fenced.Operation.Progress.Stage);
        Assert.True(replayedFence.FenceEstablished);
        Assert.Equal(afterSeal.OperationId, replayedFence.Operation!.OperationId);
        Assert.Equal(2, revokeCalls);
        Assert.Equal(0, fixture.Authorizations.OutstandingCount);

        var delayedBegin = Assert.Throws<InvalidOperationException>(() =>
            fixture.Coordinator.BeginInstall(afterSeal));
        Assert.Contains("fail-closed", delayedBegin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Restart ChunkPilot", delayedBegin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, fixture.Downloads.RequestCount);
    }

    [Fact]
    public async Task Agent_rejects_an_empty_external_managed_content_operation_identity()
    {
        await using var fixture = await Fixture.CreateAsync();
        var authorization = new ManagedContentPlanAuthorization
        {
            AuthorizationId = Guid.NewGuid(),
            Digest = new string('a', 64)
        };

        Assert.Throws<ArgumentException>(() => fixture.Coordinator.BeginInstall(
            Request(fixture.Primary.Id, authorization) with { OperationId = Guid.Empty }));
    }

    private static BeginManagedContentInstallRequest Request(
        Guid serverId,
        ManagedContentPlanAuthorization authorization,
        string versionId = "100") => new(
        serverId,
        "10",
        versionId,
        IncludeDependencies: true,
        OperationId: Guid.NewGuid(),
        Provider: PluginProviderKind.CurseForge,
        PlanAuthorization: authorization);

    private static async Task<ManagedContentOperationSnapshot> WaitForTerminalAsync(
        ManagedContentOperationCoordinator coordinator,
        Guid operationId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            var snapshot = coordinator.Get(operationId);
            if (snapshot.IsTerminal) return snapshot;
            await Task.Delay(10, timeout.Token);
        }
        throw new TimeoutException("The managed-content operation did not finish within the fixture deadline.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly ChunkPilotStore store;
        private readonly ServerSupervisor supervisor;
        private readonly CurseForgeApiClient api;

        private Fixture(
            string root,
            ChunkPilotStore store,
            ServerSupervisor supervisor,
            CurseForgeApiClient api,
            ServerDefinition primary,
            ServerDefinition secondary,
            CountingProvider provider,
            DownloadHandler downloads,
            PluginManagementService plugins,
            CurseForgeManagedContentPlanAuthorizationRegistry authorizations,
            ManagedContentOperationCoordinator coordinator)
        {
            this.root = root;
            this.store = store;
            this.supervisor = supervisor;
            this.api = api;
            Primary = primary;
            Secondary = secondary;
            Provider = provider;
            Downloads = downloads;
            Plugins = plugins;
            Authorizations = authorizations;
            Coordinator = coordinator;
        }

        public ServerDefinition Primary { get; }
        public ServerDefinition Secondary { get; }
        public CountingProvider Provider { get; }
        public DownloadHandler Downloads { get; }
        public PluginManagementService Plugins { get; }
        public CurseForgeManagedContentPlanAuthorizationRegistry Authorizations { get; }
        public ManagedContentOperationCoordinator Coordinator { get; }

        public static async Task<Fixture> CreateAsync(
            bool pauseDownloads = false,
            int maximumPreCancelledOperations =
                ManagedContentOperationCoordinator.DefaultMaximumPreCancelledOperations)
        {
            var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-managed-plan-auth-" + Guid.NewGuid().ToString("N"));
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));
            paths.EnsureCreated();
            var store = new ChunkPilotStore(paths);
            await store.InitializeAsync();
            var primary = Server(Path.Combine(root, "primary"));
            var secondary = Server(Path.Combine(root, "secondary"));
            await store.UpsertServerAsync(primary);
            await store.UpsertServerAsync(secondary);
            var jars = new JarInventoryService(new SafeFileService(paths), paths);
            var supervisor = new ServerSupervisor(
                store,
                paths,
                new ProcessStatisticsProvider(),
                new MinecraftStatusClient(),
                new BackupService(paths, store),
                NullLoggerFactory.Instance,
                jars);
            await supervisor.InitializeAsync();

            var dependencyBytes = Encoding.UTF8.GetBytes("dependency jar");
            var rootBytes = Encoding.UTF8.GetBytes("root addon jar");
            var dependency = Release("20", "200", "library.jar", dependencyBytes);
            var rootRelease = Release("10", "100", "root.jar", rootBytes) with
            {
                Dependencies = [new PluginDependency { ProjectId = "20", Type = "required" }]
            };
            var provider = new CountingProvider([dependency, rootRelease]);
            var downloads = new DownloadHandler(async (request, cancellationToken) =>
            {
                if (pauseDownloads)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(
                        "library.jar", StringComparison.Ordinal)
                        ? dependencyBytes : rootBytes)
                };
            });
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
            var api = new CurseForgeApiClient(secrets, downloads);
            var plugins = new PluginManagementService(
                new PluginProviderRegistry([provider]), jars, paths, curseForge: api);
            var authorizations = new CurseForgeManagedContentPlanAuthorizationRegistry();
            var coordinator = maximumPreCancelledOperations ==
                              ManagedContentOperationCoordinator.DefaultMaximumPreCancelledOperations
                ? new ManagedContentOperationCoordinator(supervisor, plugins, jars, authorizations)
                : new ManagedContentOperationCoordinator(
                    supervisor, plugins, jars, authorizations, maximumPreCancelledOperations);
            return new Fixture(
                root, store, supervisor, api, primary, secondary, provider, downloads,
                plugins, authorizations, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            await supervisor.DisposeAsync();
            api.Dispose();
            await store.DisposeAsync();
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static ServerDefinition Server(string serverRoot)
        {
            Directory.CreateDirectory(Path.Combine(serverRoot, "mods"));
            return new ServerDefinition
            {
                Id = Guid.NewGuid(),
                Name = Path.GetFileName(serverRoot),
                RootPath = serverRoot,
                Ecosystem = ServerEcosystem.NeoForge,
                MinecraftVersion = "1.21.1"
            };
        }

        private static PluginRelease Release(
            string projectId,
            string versionId,
            string fileName,
            byte[] bytes) => new()
        {
            Kind = ManagedAddonKind.Mod,
            Provider = PluginProviderKind.CurseForge,
            ProjectId = projectId,
            VersionId = versionId,
            VersionName = versionId,
            MinecraftVersion = "1.21.1",
            Loader = "neoforge",
            DownloadUrl = $"https://mediafilez.forgecdn.net/files/{versionId}/{fileName}",
            FileName = fileName,
            SizeBytes = bytes.Length,
#pragma warning disable CA5350 // CurseForge provider identity is SHA-1; production also records a local SHA-256 baseline.
            Sha1 = Convert.ToHexString(SHA1.HashData(bytes))
#pragma warning restore CA5350
        };
    }

    private sealed class CountingProvider(IReadOnlyList<PluginRelease> releases) : IPluginCatalogProvider
    {
        private int resolveCount;
        public int ResolveCount => resolveCount;
        public PluginProviderKind Provider => PluginProviderKind.CurseForge;
        public PluginProviderStatus Status => new(Provider, true, "Fixture");
        public Task<IReadOnlyList<PluginProject>> SearchAsync(
            PluginCatalogQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PluginProject>>([]);
        public Task<PluginRelease?> ResolveReleaseAsync(
            string projectId,
            string minecraftVersion,
            string loader,
            string? versionId = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref resolveCount);
            return Task.FromResult(releases.FirstOrDefault(release =>
                release.ProjectId.Equals(projectId, StringComparison.Ordinal) &&
                (string.IsNullOrWhiteSpace(versionId) || release.VersionId.Equals(versionId, StringComparison.Ordinal))));
        }
    }

    private sealed class DownloadHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int requestCount;
        private readonly TaskCompletionSource requestObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount => requestCount;
        public async Task WaitUntilRequestedAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await requestObserved.Task.WaitAsync(timeout.Token);
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            requestObserved.TrySetResult();
            var result = await response(request, cancellationToken);
            result.RequestMessage ??= request;
            return result;
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public void SetSecret(string name, string value) => values[name] = value;
        public string? GetSecret(string name) => values.GetValueOrDefault(name);
        public bool Contains(string name) => values.ContainsKey(name);
        public void Delete(string name) => values.Remove(name);
    }
}
