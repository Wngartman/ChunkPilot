using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

public sealed class UnconfirmedRouterCreateTests
{
    [Theory]
    [InlineData(RouterMappingMechanism.Pcp)]
    [InlineData(RouterMappingMechanism.NatPmp)]
    public async Task Lost_datagram_reply_reports_dispatch_but_never_proves_rejection(RouterMappingMechanism mechanism)
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var provider = Provider(mechanism, gateway);
        var sent = 0;
        var result = await provider.CreateAsync(gateway.Binding(), Discovery(mechanism),
            Request() with { OnCreateDispatched = () => sent++ }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.False(result.CreateConfirmedNotApplied);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, result.Failure);
        Assert.Equal(1, sent);
        Assert.Single(gateway.Received);
    }

    [Theory]
    [InlineData(RouterMappingMechanism.Pcp)]
    [InlineData(RouterMappingMechanism.NatPmp)]
    public async Task Pre_cancelled_datagram_create_never_reports_dispatch(RouterMappingMechanism mechanism)
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var provider = Provider(mechanism, gateway);
        var sent = false;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CreateAsync(gateway.Binding(),
            Discovery(mechanism), Request() with { OnCreateDispatched = () => sent = true }, cancel.Token));
        Assert.False(sent);
        Assert.Empty(gateway.Received);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Upnp_protocol_fault_is_distinct_from_an_unacknowledged_http_failure(bool networkFailure)
    {
        await using var gateway = new FakeUpnpGateway
        {
            AddErrorCode = networkFailure ? 0 : 606,
            AddReturnsBodylessHttpFailure = networkFailure
        };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var dispatched = false;
        var outcome = await provider.CreateAsync(gateway.Binding(), discovery,
            Request() with { OnCreateDispatched = () => dispatched = true }, CancellationToken.None);
        Assert.True(dispatched);
        Assert.False(outcome.Success);
        Assert.Equal(!networkFailure, outcome.CreateConfirmedNotApplied);
    }

    [Fact]
    public void Unknown_create_is_actionable_warning_not_a_retry_or_ownership_claim()
    {
        var state = new RouterMappingState
        {
            Phase = RouterMappingPhase.NeedsAttention,
            UnconfirmedCreate = new UnconfirmedRouterCreate
            {
                Binding = new RouterBindingIdentity { InterfaceId = "fixture", GatewayAddress = "10.0.0.1" },
                InternalClient = "10.0.0.23", InternalPort = 25565
            }
        };
        var summary = DirectInternetPresentation.Summary(state);
        Assert.Contains("may have opened", summary, StringComparison.Ordinal);
        Assert.Contains("10.0.0.1", summary, StringComparison.Ordinal);
        Assert.Contains("will not retry", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed", summary, StringComparison.Ordinal);
    }

    private static IRouterMappingProvider Provider(RouterMappingMechanism mechanism, FakeDatagramGateway gateway) =>
        mechanism == RouterMappingMechanism.Pcp
            ? new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1))
            : new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));

    private static RouterDiscoveryResult Discovery(RouterMappingMechanism mechanism) =>
        new() { Mechanism = mechanism, Supported = true };

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp, InternalPort = 25565, ExternalPort = 25565, LeaseSeconds = 3600
    };
}
