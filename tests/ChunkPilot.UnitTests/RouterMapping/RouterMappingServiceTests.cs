using System.Net;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// Provider selection: deterministic, sequential, and never two mechanisms asking one router at once.
/// </summary>
public sealed class RouterMappingServiceTests
{
    [Fact]
    public async Task Mechanisms_are_probed_in_order_and_the_first_answer_wins()
    {
        var pcp = new RecordingProvider(RouterMappingMechanism.Pcp, supported: true);
        var natPmp = new RecordingProvider(RouterMappingMechanism.NatPmp, supported: true);
        var upnp = new RecordingProvider(RouterMappingMechanism.UpnpIgd, supported: true);
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(),
            upnp, natPmp, pcp);

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.Pcp, report.Selected!.Mechanism);
        Assert.Equal(1, pcp.Discoveries);
        Assert.Equal(0, natPmp.Discoveries);
        Assert.Equal(0, upnp.Discoveries);
    }

    [Fact]
    public async Task A_nat_pmp_only_gateway_falls_through_from_pcp()
    {
        var pcp = new RecordingProvider(RouterMappingMechanism.Pcp, supported: false,
            RouterMappingFailure.MechanismUnsupported);
        var natPmp = new RecordingProvider(RouterMappingMechanism.NatPmp, supported: true);
        var upnp = new RecordingProvider(RouterMappingMechanism.UpnpIgd, supported: true);
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(),
            pcp, natPmp, upnp);

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.NatPmp, report.Selected!.Mechanism);
        Assert.Equal(0, upnp.Discoveries);
    }

    [Fact]
    public async Task Upnp_is_reached_when_the_datagram_mechanisms_are_absent()
    {
        var pcp = new RecordingProvider(RouterMappingMechanism.Pcp, supported: false,
            RouterMappingFailure.GatewayDidNotRespond);
        var natPmp = new RecordingProvider(RouterMappingMechanism.NatPmp, supported: false,
            RouterMappingFailure.GatewayDidNotRespond);
        var upnp = new RecordingProvider(RouterMappingMechanism.UpnpIgd, supported: true);
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(),
            pcp, natPmp, upnp);

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.UpnpIgd, report.Selected!.Mechanism);
        Assert.Equal(3, report.Attempts.Count);
    }

    /// <summary>
    /// A mechanism that already owns a mapping is asked first and, if it answers, nothing else is
    /// contacted — so a live mapping never changes mechanism underneath itself.
    /// </summary>
    [Fact]
    public async Task An_established_mechanism_is_preferred_and_short_circuits_the_others()
    {
        var pcp = new RecordingProvider(RouterMappingMechanism.Pcp, supported: true);
        var upnp = new RecordingProvider(RouterMappingMechanism.UpnpIgd, supported: true);
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(), pcp, upnp);

        var report = await service.CheckAsync(RouterMappingMechanism.UpnpIgd, CancellationToken.None);

        Assert.Equal(RouterMappingMechanism.UpnpIgd, report.Selected!.Mechanism);
        Assert.Equal(0, pcp.Discoveries);
    }

    [Fact]
    public async Task Every_mechanism_staying_silent_is_reported_as_unconfirmed_not_unsupported()
    {
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(),
            new RecordingProvider(RouterMappingMechanism.Pcp, false, RouterMappingFailure.GatewayDidNotRespond),
            new RecordingProvider(RouterMappingMechanism.NatPmp, false, RouterMappingFailure.GatewayDidNotRespond),
            new RecordingProvider(RouterMappingMechanism.UpnpIgd, false, RouterMappingFailure.GatewayDidNotRespond));

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.False(report.Supported);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, report.Failure);
    }

    [Fact]
    public async Task A_gateway_that_answers_but_refuses_is_reported_as_unsupported()
    {
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(),
            new RecordingProvider(RouterMappingMechanism.Pcp, false, RouterMappingFailure.MechanismUnsupported),
            new RecordingProvider(RouterMappingMechanism.NatPmp, false, RouterMappingFailure.MechanismUnsupported),
            new RecordingProvider(RouterMappingMechanism.UpnpIgd, false, RouterMappingFailure.MechanismUnsupported));

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.Equal(RouterMappingFailure.MechanismUnsupported, report.Failure);
    }

    [Fact]
    public async Task No_identifiable_router_stops_before_any_mechanism_is_contacted()
    {
        var pcp = new RecordingProvider(RouterMappingMechanism.Pcp, supported: true);
        var service = RouterFixtures.Service(new EmptyNetworkView(), new RouterMappingOptions(), pcp);

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.False(report.Supported);
        Assert.Equal(RouterMappingFailure.NoGatewayFound, report.Failure);
        Assert.Equal(0, pcp.Discoveries);
        Assert.Null(service.ResolveBinding());
    }

    [Fact]
    public async Task Cancellation_is_reported_as_cancellation_and_stops_the_sequence()
    {
        var pcp = new SlowProvider(RouterMappingMechanism.Pcp);
        var natPmp = new RecordingProvider(RouterMappingMechanism.NatPmp, supported: true);
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(), pcp, natPmp);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        var report = await service.CheckAsync(RouterMappingMechanism.None, cancellation.Token);

        Assert.Equal(RouterMappingFailure.Cancelled, report.Failure);
        Assert.Equal(0, natPmp.Discoveries);
    }

    [Fact]
    public async Task The_discovery_budget_bounds_a_router_that_never_finishes_answering()
    {
        var options = new RouterMappingOptions { DiscoveryBudget = TimeSpan.FromMilliseconds(120) };
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), options,
            new SlowProvider(RouterMappingMechanism.Pcp));

        var report = await service.CheckAsync(RouterMappingMechanism.None, CancellationToken.None);

        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, report.Failure);
    }

    [Fact]
    public async Task The_operation_budget_bounds_a_create_that_never_finishes()
    {
        var options = new RouterMappingOptions { OperationBudget = TimeSpan.FromMilliseconds(120) };
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), options,
            new SlowProvider(RouterMappingMechanism.Pcp));
        var discovery = new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.Pcp, Supported = true };

        var outcome = await service.CreateAsync(Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, outcome.Failure);
    }

    [Fact]
    public async Task Querying_a_mechanism_that_cannot_read_returns_nothing_rather_than_guessing()
    {
        var pcp = new RecordingProvider(RouterMappingMechanism.Pcp, supported: true);
        var service = RouterFixtures.Service(RouterFixtures.Loopback(), new RouterMappingOptions(), pcp);
        var discovery = new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.Pcp, Supported = true };

        Assert.Null(await service.QueryAsync(Binding(), discovery, MappingTransport.Tcp, 25565,
            CancellationToken.None));
    }

    private static RouterLanBinding Binding() =>
        new("eth", "Ethernet", IPAddress.Parse("192.168.1.50"), 24, IPAddress.Parse("192.168.1.1"));

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp,
        InternalPort = 25565,
        ExternalPort = 25565,
        LeaseSeconds = 3600
    };

    private sealed class EmptyNetworkView : IRouterNetworkView
    {
        public IReadOnlyList<RouterGatewayCandidate> Enumerate() => [];
    }

    private sealed class RecordingProvider(
        RouterMappingMechanism mechanism,
        bool supported,
        RouterMappingFailure failure = RouterMappingFailure.None) : IRouterMappingProvider
    {
        public int Discoveries { get; private set; }
        public RouterMappingMechanism Mechanism => mechanism;
        public bool CanQueryExistingMappings => false;

        public Task<RouterDiscoveryResult> DiscoverAsync(RouterLanBinding binding, CancellationToken cancellationToken)
        {
            Discoveries++;
            return Task.FromResult(new RouterDiscoveryResult
            {
                Mechanism = mechanism,
                Supported = supported,
                Failure = failure,
                Detail = $"{mechanism} fixture"
            });
        }

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult<ExistingRouterMapping?>(null);

        public Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RouterMappingOutcome { Success = true, Mechanism = mechanism });

        public Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new RouterMappingOutcome { Success = true, Mechanism = mechanism });
    }

    private sealed class SlowProvider(RouterMappingMechanism mechanism) : IRouterMappingProvider
    {
        public RouterMappingMechanism Mechanism => mechanism;
        public bool CanQueryExistingMappings => false;

        public async Task<RouterDiscoveryResult> DiscoverAsync(
            RouterLanBinding binding, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new RouterDiscoveryResult { Mechanism = mechanism, Supported = true };
        }

        public Task<ExistingRouterMapping?> QueryAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, MappingTransport transport,
            int externalPort, CancellationToken cancellationToken) =>
            Task.FromResult<ExistingRouterMapping?>(null);

        public async Task<RouterMappingOutcome> CreateAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return new RouterMappingOutcome { Success = true, Mechanism = mechanism };
        }

        public Task<RouterMappingOutcome> RemoveAsync(
            RouterLanBinding binding, RouterDiscoveryResult discovery, RouterMappingRequest request,
            CancellationToken cancellationToken) =>
            CreateAsync(binding, discovery, request, cancellationToken);
    }
}
