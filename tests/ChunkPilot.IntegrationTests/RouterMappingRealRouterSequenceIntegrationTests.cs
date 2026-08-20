using System.Collections.Concurrent;
using System.Net;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The exact sequence a real home router produced: UPnP discovery answers, GetExternalIPAddress
/// answers, and then the attempt to create the mapping either succeeds or fails.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the acceptance failure. On the user's router, discovery and GetExternalIPAddress both
/// succeeded and the screen still returned to "Not set up" with no explanation, because the projection
/// collapsed every not-yet-enabled state to off — including a check that had just succeeded. These
/// tests pin each outcome of that sequence to a distinct, truthful state.
/// </para>
/// <para>
/// The router here is an in-process scripted gateway. No real router is contacted.
/// </para>
/// </remarks>
public sealed class RouterMappingRealRouterSequenceIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "chunkpilot-router-seq-" + Guid.NewGuid().ToString("N"));

    private ChunkPilotStore store = null!;
    private ServerSupervisor supervisor = null!;
    private RouterMappingCoordinator coordinator = null!;
    private ScriptedGateway gateway = null!;
    private SwitchableView view = null!;
    private Guid serverId;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        var paths = new AppDataPaths(Path.Combine(root, "appdata"), Path.Combine(root, "servers"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), NullLoggerFactory.Instance);
        await supervisor.InitializeAsync();
        gateway = new ScriptedGateway();
        view = new SwitchableView();
        var service = new RouterMappingService(view, [gateway], new RouterMappingOptions(),
            NullLogger<RouterMappingService>.Instance);
        coordinator = new RouterMappingCoordinator(store, supervisor, service,
            NullLogger<RouterMappingCoordinator>.Instance);

        serverId = Guid.NewGuid();
        var serverRoot = Path.Combine(root, "servers", serverId.ToString("N"));
        Directory.CreateDirectory(serverRoot);
        await supervisor.ImportAsync(new ServerDefinition
        {
            Id = serverId,
            Name = "Sunday survival",
            RootPath = serverRoot,
            Port = 25565
        });
    }

    // ═══ The reported failure ═══

    /// <summary>
    /// The headline regression. A router that answers must leave the surface saying so; returning to
    /// "Not set up" is what made the feature unreachable, because the confirmation only ever opens on
    /// a Supported result.
    /// </summary>
    [Fact]
    public async Task A_router_that_answers_the_check_does_not_report_itself_as_not_set_up()
    {
        var state = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Supported, state.Phase);
        Assert.NotEqual(RouterMappingPhase.Off, state.Phase);
        Assert.False(state.Enabled);
        Assert.Equal(RouterMappingFailure.None, state.Failure);
    }

    /// <summary>Everything the check learned must reach the screen, not just the prose detail.</summary>
    [Fact]
    public async Task A_successful_check_records_the_gateway_the_candidate_address_and_the_mechanism()
    {
        var state = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal("10.0.0.1", state.GatewayAddress);
        Assert.Equal("10.0.0.23", state.CandidateInternalClient);
        Assert.Equal(RouterMappingMechanism.UpnpIgd, state.AvailableMechanism);
        // Answering is not owning: nothing may claim an established mechanism yet.
        Assert.Equal(RouterMappingMechanism.None, state.Mechanism);
        Assert.Equal("73.203.43.174", state.RouterReportedExternalAddress);
        Assert.NotNull(state.LastCheckedAt);
        Assert.Equal(0, gateway.Creates);
    }

    [Fact]
    public async Task A_successful_check_survives_a_view_model_refresh()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);

        var refreshed = await coordinator.GetStateAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Supported, refreshed.Phase);
        Assert.Equal("10.0.0.1", refreshed.GatewayAddress);
        Assert.Contains("73.203.43.174", refreshed.LastOperationDetail, StringComparison.Ordinal);
    }

    /// <summary>The router-reported address is shown before any port exists, without a port glued on.</summary>
    [Fact]
    public async Task A_check_reports_the_routers_address_without_inventing_a_port()
    {
        var state = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.True(state.HasRouterReportedAddress);
        Assert.Equal("73.203.43.174", state.RouterReportedEndpoint);
        Assert.Equal(0, state.ExternalPort);
    }

    // ═══ What happens after the check, at the mapping step ═══

    [Fact]
    public async Task Add_port_mapping_success_becomes_an_active_mapping()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);

        var state = await coordinator.EnableAsync(serverId, consentGranted: true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, state.Phase);
        Assert.Equal(RouterMappingMechanism.UpnpIgd, state.Mechanism);
        Assert.Equal("10.0.0.23", state.InternalClient);
        Assert.Equal(25565, state.ExternalPort);
        Assert.True(state.LeaseIsFinite);
        Assert.NotNull(state.LeaseExpiresAt);
        Assert.Equal("73.203.43.174:25565", state.RouterReportedEndpoint);
        Assert.Equal(1, gateway.Creates);
    }

    [Theory]
    [InlineData(RouterMappingFailure.RequestRejected, RouterMappingPhase.NeedsAttention,
        "UPnP AddPortMapping failed for TCP 25565 with error 402 (InvalidArgs).")]
    [InlineData(RouterMappingFailure.NotAuthorized, RouterMappingPhase.NeedsAttention,
        "UPnP AddPortMapping failed for TCP 25565 with error 606 (ActionNotAuthorized).")]
    [InlineData(RouterMappingFailure.NetworkFailure, RouterMappingPhase.NeedsAttention,
        "The gateway did not complete AddPortMapping: connection refused.")]
    [InlineData(RouterMappingFailure.GatewayDidNotRespond, RouterMappingPhase.Undetermined,
        "The router operation reached its 25 second limit.")]
    [InlineData(RouterMappingFailure.OutOfResources, RouterMappingPhase.NeedsAttention,
        "UPnP AddPortMapping failed for TCP 25565 with error 501 (ActionFailed).")]
    public async Task A_rejected_mapping_keeps_the_exact_provider_failure_instead_of_going_quiet(
        RouterMappingFailure failure, RouterMappingPhase expected, string detail)
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.CreateFailure = failure;
        gateway.CreateDetail = detail;

        var state = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(expected, state.Phase);
        Assert.NotEqual(RouterMappingPhase.Off, state.Phase);
        Assert.Equal(failure, state.Failure);
        Assert.Equal(detail, state.LastOperationDetail);
        // Evidence of what was attempted survives, but nothing claims to own a mapping.
        Assert.Equal(RouterMappingMechanism.UpnpIgd, state.AvailableMechanism);
        Assert.Equal(RouterMappingMechanism.None, state.Mechanism);
        Assert.Equal("10.0.0.23", state.CandidateInternalClient);
        Assert.Equal("", state.InternalClient);
        Assert.Empty(gateway.Table);
    }

    [Fact]
    public async Task A_failed_attempt_reaches_the_app_unchanged_through_a_separate_read()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.CreateFailure = RouterMappingFailure.RequestRejected;
        gateway.CreateDetail = "UPnP AddPortMapping failed for TCP 25565 with error 718 (ConflictInMappingEntry).";
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        var refreshed = await coordinator.GetStateAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.NeedsAttention, refreshed.Phase);
        Assert.Equal(RouterMappingFailure.RequestRejected, refreshed.Failure);
        Assert.Contains("718", refreshed.LastOperationDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_attempt_can_be_retried_and_then_succeeds()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.CreateFailure = RouterMappingFailure.NetworkFailure;
        gateway.CreateDetail = "The gateway did not complete AddPortMapping: connection reset.";
        var failed = await coordinator.EnableAsync(serverId, true, CancellationToken.None);
        Assert.Equal(RouterMappingPhase.NeedsAttention, failed.Phase);

        gateway.CreateFailure = RouterMappingFailure.None;
        var retried = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Active, retried.Phase);
        Assert.Equal(RouterMappingFailure.None, retried.Failure);
        Assert.Single(gateway.Table);
    }

    /// <summary>A failed attempt created nothing, so nothing may be deleted on its behalf.</summary>
    [Fact]
    public async Task A_failed_attempt_never_asks_the_router_to_delete_anything()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.CreateFailure = RouterMappingFailure.RequestRejected;
        gateway.CreateDetail = "UPnP AddPortMapping failed for TCP 25565 with error 402 (InvalidArgs).";
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        _ = await coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.Equal(0, gateway.Removes);
    }

    // ═══ Preconditions inside ChunkPilot ═══

    /// <summary>
    /// No trustworthy LAN address means no safe destination, and that must be said rather than shown
    /// as a bare "Not set up". The router is never asked to create anything in this state.
    /// </summary>
    [Fact]
    public async Task No_trustworthy_local_address_is_reported_as_such_and_stops_before_the_router()
    {
        view.HasRouter = false;

        var checkState = await coordinator.CheckAsync(serverId, CancellationToken.None);
        var enableState = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        foreach (var state in new[] { checkState, enableState })
        {
            Assert.Equal(RouterMappingPhase.Unavailable, state.Phase);
            Assert.Equal(RouterMappingFailure.NoGatewayFound, state.Failure);
            Assert.NotEqual(RouterMappingPhase.Off, state.Phase);
        }
        Assert.Equal(0, gateway.Discoveries);
        Assert.Equal(0, gateway.Creates);
    }

    [Fact]
    public async Task A_router_that_answers_but_offers_nothing_is_unavailable_rather_than_off()
    {
        gateway.Supported = false;
        gateway.DiscoveryFailure = RouterMappingFailure.MechanismUnsupported;

        var state = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Unavailable, state.Phase);
        Assert.Equal(RouterMappingFailure.MechanismUnsupported, state.Failure);
        Assert.Equal(RouterMappingMechanism.None, state.AvailableMechanism);
    }

    [Fact]
    public async Task A_silent_router_is_undetermined_rather_than_off()
    {
        gateway.Supported = false;
        gateway.DiscoveryFailure = RouterMappingFailure.GatewayDidNotRespond;

        var state = await coordinator.CheckAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Undetermined, state.Phase);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, state.Failure);
    }

    // ═══ Ownership and lifecycle around the failure path ═══

    [Fact]
    public async Task A_foreign_entry_found_after_a_good_check_is_a_conflict_and_is_left_alone()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.Table["TCP:25565"] = new ExistingRouterMapping
        {
            ExternalPort = 25565,
            Transport = MappingTransport.Tcp,
            InternalClient = "10.0.0.90",
            InternalPort = 25565,
            Description = "Living room console"
        };

        var state = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Conflict, state.Phase);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, state.Failure);
        Assert.Equal(0, gateway.Creates);
        Assert.Equal(0, gateway.Removes);
        Assert.Equal("Living room console", gateway.Table["TCP:25565"].Description);
    }

    [Fact]
    public async Task Cancelling_the_attempt_does_not_leave_the_surface_creating()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.OperationDelay = TimeSpan.FromSeconds(5);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));

        var cancelled = await coordinator.EnableAsync(serverId, true, cancellation.Token);
        var refreshed = await coordinator.GetStateAsync(serverId, CancellationToken.None);

        foreach (var state in new[] { cancelled, refreshed })
        {
            Assert.NotEqual(RouterMappingPhase.Creating, state.Phase);
            Assert.NotEqual(RouterMappingPhase.Checking, state.Phase);
            Assert.False(state.Busy);
        }
        // The check still stands, so the user can simply try again.
        Assert.Equal(RouterMappingPhase.Supported, refreshed.Phase);
    }

    [Fact]
    public async Task Turning_direct_internet_off_after_a_failed_attempt_returns_to_not_set_up()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.CreateFailure = RouterMappingFailure.RequestRejected;
        gateway.CreateDetail = "UPnP AddPortMapping failed for TCP 25565 with error 402 (InvalidArgs).";
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        var state = await coordinator.DisableAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.Off, state.Phase);
        Assert.False(state.Enabled);
        Assert.Equal(RouterMappingFailure.None, state.Failure);
    }

    [Fact]
    public async Task Reconciliation_does_not_erase_a_reported_failure_while_the_server_is_stopped()
    {
        _ = await coordinator.CheckAsync(serverId, CancellationToken.None);
        gateway.CreateFailure = RouterMappingFailure.RequestRejected;
        gateway.CreateDetail = "UPnP AddPortMapping failed for TCP 25565 with error 402 (InvalidArgs).";
        _ = await coordinator.EnableAsync(serverId, true, CancellationToken.None);

        await coordinator.SynchronizeAsync(serverId, CancellationToken.None);
        var state = await coordinator.GetStateAsync(serverId, CancellationToken.None);

        Assert.Equal(RouterMappingPhase.NeedsAttention, state.Phase);
        Assert.Equal(RouterMappingFailure.RequestRejected, state.Failure);
    }

    public async Task DisposeAsync()
    {
        await coordinator.DisposeAsync();
        await supervisor.DisposeAsync();
        await store.DisposeAsync();
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The user's own topology: a 10.0.0.0/24 home network behind 10.0.0.1.</summary>
    private sealed class SwitchableView : IRouterNetworkView
    {
        public bool HasRouter { get; set; } = true;

        public IReadOnlyList<RouterGatewayCandidate> Enumerate() => HasRouter
            ?
            [
                new(new LanInterfaceCandidate(
                        "eth", "Ethernet", "Fixture Ethernet",
                        System.Net.NetworkInformation.NetworkInterfaceType.Ethernet,
                        System.Net.NetworkInformation.OperationalStatus.Up,
                        1_000, true, true,
                        [new LanAddressCandidate(IPAddress.Parse("10.0.0.23"), 24)]),
                    [IPAddress.Parse("10.0.0.1")])
            ]
            : [];
    }

    /// <summary>
    /// A gateway that behaves like the user's: discovery answers and reports an external address, and
    /// the mapping step is scripted independently.
    /// </summary>
    private sealed class ScriptedGateway : IRouterMappingProvider
    {
        public ConcurrentDictionary<string, ExistingRouterMapping> Table { get; } = new(StringComparer.Ordinal);
        public int Discoveries { get; private set; }
        public int Creates { get; private set; }
        public int Removes { get; private set; }
        public bool Supported { get; set; } = true;
        public RouterMappingFailure DiscoveryFailure { get; set; } = RouterMappingFailure.None;
        public RouterMappingFailure CreateFailure { get; set; } = RouterMappingFailure.None;
        public string CreateDetail { get; set; } = "";
        public TimeSpan OperationDelay { get; set; }

        public RouterMappingMechanism Mechanism => RouterMappingMechanism.UpnpIgd;
        public bool CanQueryExistingMappings => true;

        public Task<RouterDiscoveryResult> DiscoverAsync(
            RouterLanBinding binding, CancellationToken cancellationToken)
        {
            Discoveries++;
            return Task.FromResult(new RouterDiscoveryResult
            {
                Mechanism = Mechanism,
                Supported = Supported,
                Failure = DiscoveryFailure,
                ExternalAddress = Supported ? "73.203.43.174" : "",
                ControlUrl = "http://10.0.0.1:49152/upnp/control/WANIPConnection0",
                ServiceType = "urn:schemas-upnp-org:service:WANIPConnection:1",
                Detail = Supported
                    ? "UPnP urn:schemas-upnp-org:service:WANIPConnection:1 answered at " +
                      "http://10.0.0.1:49152/upnp/control/WANIPConnection0 and reported external address " +
                      "73.203.43.174."
                    : "The gateway published no usable WAN connection service."
            });
        }

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult(Table.GetValueOrDefault(Key(transport, externalPort)));

        public async Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            if (OperationDelay > TimeSpan.Zero)
                await Task.Delay(OperationDelay, cancellationToken).ConfigureAwait(false);
            if (CreateFailure != RouterMappingFailure.None)
                return RouterMappingOutcome.Failed(Mechanism, CreateFailure, CreateDetail);
            Creates++;
            Table[Key(request.Transport, request.ExternalPort)] = new ExistingRouterMapping
            {
                ExternalPort = request.ExternalPort,
                Transport = request.Transport,
                InternalClient = binding.LocalAddress.ToString(),
                InternalPort = request.InternalPort,
                Description = request.Description,
                LeaseSeconds = request.LeaseSeconds
            };
            return new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                LeaseSeconds = request.LeaseSeconds,
                LeaseIsFinite = true,
                ExternalAddress = discovery.ExternalAddress,
                ControlUrl = discovery.ControlUrl,
                ServiceType = discovery.ServiceType,
                Detail = $"UPnP AddPortMapping accepted TCP {request.ExternalPort}."
            };
        }

        public Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            Removes++;
            Table.TryRemove(Key(request.Transport, request.ExternalPort), out _);
            return Task.FromResult(new RouterMappingOutcome
            {
                Success = true,
                Mechanism = Mechanism,
                ExternalPort = request.ExternalPort,
                Detail = "UPnP DeletePortMapping removed the entry."
            });
        }

        private static string Key(MappingTransport transport, int port) =>
            $"{(transport == MappingTransport.Tcp ? "TCP" : "UDP")}:{port}";
    }
}
