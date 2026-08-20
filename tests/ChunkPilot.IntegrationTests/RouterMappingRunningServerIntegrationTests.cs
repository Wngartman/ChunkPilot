using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// Router mapping against a genuinely running server process, so the coupling between the Minecraft
/// lifecycle and the exposure is proven rather than simulated.
/// </summary>
/// <remarks>
/// The server is the repository's fake console server; the router is an in-process scripted gateway.
/// No real router, firewall or Minecraft installation is involved.
/// </remarks>
public sealed class RouterMappingRunningServerIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkpilot-router-live-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;
    private ServerSupervisor supervisor = null!;
    private RouterMappingCoordinator coordinator = null!;
    private ScriptedGateway gateway = null!;
    private ILoggerFactory loggerFactory = null!;

    /// <summary>
    /// A controllable clock. Lease renewal is deliberately floored at a minute, so proving that a lost
    /// mapping is re-established has to move time rather than wait for it.
    /// </summary>
    private readonly AdvanceableClock clock = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "appdata"), Path.Combine(root, "servers"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
        supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), loggerFactory);
        await supervisor.InitializeAsync();
        gateway = new ScriptedGateway();
        var service = new RouterMappingService(new FixedView(), [gateway], new RouterMappingOptions(),
            NullLogger<RouterMappingService>.Instance);
        coordinator = new RouterMappingCoordinator(store, supervisor, service,
            NullLogger<RouterMappingCoordinator>.Instance, clock);
    }

    [Fact(Timeout = 60_000)]
    public async Task Starting_a_server_with_direct_internet_off_leaves_the_router_alone()
    {
        var (id, server) = await AddAsync();

        Assert.True((await server.StartAsync()).Success);
        await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Empty(gateway.Table);
        Assert.Equal(0, gateway.Discoveries);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_running_server_with_direct_internet_on_keeps_its_mapping_across_a_synchronize()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, consentGranted: true, CancellationToken.None);

        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Single(gateway.Table);
        Assert.Equal(1, gateway.Creates);
        await server.StopAsync();
    }

    /// <summary>Power loss or a router reboot empties the table; renewal must re-create it.</summary>
    [Fact(Timeout = 60_000)]
    public async Task A_router_that_forgot_its_table_is_re_established_when_renewal_falls_due()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);
        gateway.Table.Clear();
        clock.Advance(TimeSpan.FromSeconds(RouterMappingPolicy.RequestedLeaseSeconds / 2 + 5));

        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Single(gateway.Table);
        Assert.Equal(2, gateway.Creates);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task A_deliberate_stop_of_a_running_server_closes_the_port_and_keeps_the_intent()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);

        Assert.True((await server.StopAsync()).Success);
        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Empty(gateway.Table);
        Assert.Equal(1, gateway.Removes);
        // Configured, nothing open. The stopped server used to keep reporting "Router port is open".
        Assert.Equal(RouterMappingPhase.Inactive, state.Phase);
        Assert.True(state.Enabled);
        Assert.False(state.LeaseExpiresAt.HasValue);
        Assert.True((await store.GetRouterMappingAsync(id))!.DirectInternetEnabled);
    }

    /// <summary>Stop, then start again: the surviving intent rebuilds the mapping with no new consent.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Starting_again_after_a_manual_stop_re_establishes_the_mapping()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);
        Assert.True((await server.StopAsync()).Success);
        Assert.Equal(RouterMappingPhase.Inactive,
            (await coordinator.SynchronizeAsync(id, CancellationToken.None)).Phase);

        Assert.True((await server.StartAsync()).Success);
        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Single(gateway.Table);
        Assert.True(state.ConsentGranted);
        await server.StopAsync();
    }

    /// <summary>Repeated Stop must not issue a second delete for a mapping already withdrawn.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Repeated_stops_do_not_delete_twice()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);
        Assert.True((await server.StopAsync()).Success);

        await coordinator.SynchronizeAsync(id, CancellationToken.None);
        await coordinator.SynchronizeAsync(id, CancellationToken.None);
        await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(1, gateway.Removes);
    }

    /// <summary>
    /// A cleanup the router refused leaves the exposure unproven, so it is reported rather than
    /// claimed closed, keeps the evidence a retry needs, and succeeds once the router cooperates.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_stop_whose_cleanup_fails_reports_it_and_can_be_retried()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);
        Assert.True((await server.StopAsync()).Success);
        gateway.RemoveFails = true;

        var failed = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.NeedsAttention, failed.Phase);
        Assert.True(failed.RemovalPending);
        Assert.NotEqual(RouterMappingPhase.Active, failed.Phase);
        // The evidence a retry needs to prove the entry is ChunkPilot's own is kept, not discarded.
        var retained = await store.GetRouterMappingAsync(id);
        Assert.True(retained!.RemovalPending);
        Assert.Equal(RouterMappingMechanism.UpnpIgd, retained.Mechanism);
        Assert.Equal(server.Definition.Port, retained.ExternalPort);
        Assert.NotEqual("", retained.InternalClient);

        gateway.RemoveFails = false;
        var retried = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Inactive, retried.Phase);
        Assert.False(retried.RemovalPending);
        Assert.Empty(gateway.Table);
    }

    [Fact(Timeout = 60_000)]
    public async Task A_safe_restart_preserves_the_mapping_instead_of_churning_it()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);

        Assert.True((await server.RestartAsync()).Success);
        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Equal(0, gateway.Removes);
        Assert.Equal(1, gateway.Creates);
        Assert.Single(gateway.Table);
        await server.StopAsync();
    }

    [Fact(Timeout = 60_000)]
    public async Task Restarting_onto_a_new_port_moves_the_mapping_and_leaves_nothing_behind()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);
        var originalPort = server.Definition.Port;

        var newPort = GetFreePort();
        await supervisor.ImportAsync(server.Definition with { Port = newPort });
        Assert.True((await server.RestartAsync()).Success);
        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(newPort, state.ExternalPort);
        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Single(gateway.Table);
        Assert.True(gateway.Table.ContainsKey($"TCP:{newPort}"));
        Assert.False(gateway.Table.ContainsKey($"TCP:{originalPort}"));
        await server.StopAsync();
    }

    /// <summary>
    /// A server that is simply not running holds no exposure, whoever stopped it and however it
    /// stopped. Nothing waits for a timer.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_server_that_is_not_running_and_not_restarting_closes_its_exposure_at_once()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        await coordinator.EnableAsync(id, true, CancellationToken.None);
        Assert.True((await server.StopAsync()).Success);
        Assert.False(server.IsRestartInProgress);

        var state = await coordinator.SynchronizeAsync(id, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Inactive, state.Phase);
        Assert.Empty(gateway.Table);
        Assert.Equal(1, gateway.Removes);
    }

    /// <summary>
    /// The signal that separates a restart from a stop. It is true only while the restart operation is
    /// running, so it cannot leave a failed restart holding a port open indefinitely.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Restart_in_progress_is_true_only_while_the_restart_runs()
    {
        var (id, server) = await AddAsync();
        Assert.True((await server.StartAsync()).Success);
        Assert.False(server.IsRestartInProgress);

        var observedDuringRestart = false;
        var restart = server.RestartAsync();
        for (var attempt = 0; attempt < 400 && !restart.IsCompleted; attempt++)
        {
            if (server.IsRestartInProgress)
                observedDuringRestart = true;
            await Task.Delay(5);
        }
        Assert.True((await restart).Success);

        Assert.True(observedDuringRestart, "A restart never reported itself as in progress.");
        Assert.False(server.IsRestartInProgress);
        await server.StopAsync();
        Assert.False(server.IsRestartInProgress);
        _ = id;
    }

    private async Task<(Guid Id, ManagedServer Server)> AddAsync()
    {
        var id = Guid.NewGuid();
        var serverRoot = Path.Combine(root, "servers", id.ToString("N"));
        Directory.CreateDirectory(serverRoot);
        var port = GetFreePort();
        var definition = new ServerDefinition
        {
            Id = id,
            Name = "Router fixture",
            RootPath = serverRoot,
            WorkingDirectory = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal",
            ReadinessPattern = @"Done \(.+?\)!|For help, type",
            StartupTimeoutSeconds = 20,
            ShutdownTimeoutSeconds = 10,
            SaveTimeoutSeconds = 10,
            RestartDelaySeconds = 0,
            Port = port,
            Environment = new Dictionary<string, string>
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(CultureInfo.InvariantCulture)
            }
        };
        await supervisor.ImportAsync(definition);
        return (id, supervisor.Get(id));
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate ChunkPilot repository root.");
    }

    private static string DotnetPath() => Path.Combine(RepositoryRoot(), ".tools", "dotnet", "dotnet.exe");

    private static string FakeServerDll() => Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer",
        "bin", "Release", "net10.0", "ChunkPilot.FakeServer.dll");

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        await supervisor.DisposeAsync();
        await store.DisposeAsync();
        loggerFactory.Dispose();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class AdvanceableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan amount) => now += amount;
    }

    private sealed class FixedView : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() =>
        [
            new(new LanInterfaceCandidate(
                    "eth", "Ethernet", "Fixture Ethernet",
                    System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                    System.Net.NetworkInformation.OperationalStatus.Up,
                    1_000, true, true,
                    [new LanAddressCandidate(IPAddress.Parse("192.168.1.50"), 24)]),
                [IPAddress.Parse("192.168.1.1")])
        ];
    }

    private sealed class ScriptedGateway : IRouterMappingProvider
    {
        public ConcurrentDictionary<string, ExistingRouterMapping> Table { get; } = new(StringComparer.Ordinal);
        public int Discoveries { get; private set; }
        public int Creates { get; private set; }
        public int Removes { get; private set; }
        public int LeaseSeconds { get; set; } = 3600;
        public bool RemoveFails { get; set; }

        public RouterMappingMechanism Mechanism => RouterMappingMechanism.UpnpIgd;
        public bool CanQueryExistingMappings => true;

        public Task<RouterDiscoveryResult> DiscoverAsync(
            RouterLanBinding binding, CancellationToken cancellationToken)
        {
            Discoveries++;
            return Task.FromResult(new RouterDiscoveryResult
            {
                Mechanism = Mechanism,
                Supported = true,
                ExternalAddress = "203.0.113.4",
                ControlUrl = "http://192.168.1.1/ctl",
                ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
                Detail = "Scripted gateway."
            });
        }

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult(Table.GetValueOrDefault(Key(transport, externalPort)));

        public Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            Creates++;
            Table[Key(request.Transport, request.ExternalPort)] = new ExistingRouterMapping
            {
                ExternalPort = request.ExternalPort,
                Transport = request.Transport,
                InternalClient = binding.LocalAddress.ToString(),
                InternalPort = request.InternalPort,
                Description = request.Description,
                LeaseSeconds = LeaseSeconds
            };
            return Task.FromResult(new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                LeaseSeconds = LeaseSeconds,
                LeaseIsFinite = true,
                ExternalAddress = discovery.ExternalAddress,
                ControlUrl = discovery.ControlUrl,
                ServiceType = discovery.ServiceType,
                Detail = $"Scripted create for {request.Transport} {request.ExternalPort}."
            });
        }

        public Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            if (RemoveFails)
                return Task.FromResult(RouterMappingOutcome.Failed(Mechanism,
                    RouterMappingFailure.RemovalFailed,
                    "UPnP DeletePortMapping failed with error 501 (ActionFailed)."));
            Removes++;
            Table.TryRemove(Key(request.Transport, request.ExternalPort), out _);
            return Task.FromResult(new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                Detail = "Scripted remove."
            });
        }

        private static string Key(MappingTransport transport, int port) =>
            $"{(transport == MappingTransport.Tcp ? "TCP" : "UDP")}:{port}";
    }
}
