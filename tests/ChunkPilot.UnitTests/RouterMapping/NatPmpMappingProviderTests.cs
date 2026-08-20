using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// NAT-PMP against a controlled loopback gateway that speaks the real RFC 6886 wire format.
/// </summary>
public sealed class NatPmpMappingProviderTests
{
    [Fact]
    public async Task Discovery_reads_the_external_address_without_creating_anything()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request) ? NatPmp.AddressReply(0, "198.51.100.7") : null);
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options());

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal("198.51.100.7", result.ExternalAddress);
        // The capability check is a two-byte opcode 0 request and nothing else.
        Assert.All(gateway.Received, request => Assert.True(NatPmp.IsAddressRequest(request)));
    }

    [Fact]
    public async Task A_silent_gateway_is_reported_as_unconfirmed_rather_than_unsupported()
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(),
            gateway.Options(attempts: 2, attemptTimeout: TimeSpan.FromMilliseconds(300)));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, result.Failure);
        Assert.Equal(2, gateway.Received.Count);
    }

    [Fact]
    public async Task A_short_reply_is_malformed_and_claims_nothing()
    {
        await using var gateway = FakeDatagramGateway.Start(_ => [0, 128, 0]);
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal(RouterMappingFailure.MalformedReply, result.Failure);
    }

    [Fact]
    public async Task An_unsupported_version_result_reports_the_mechanism_as_unsupported()
    {
        await using var gateway = FakeDatagramGateway.Start(_ => NatPmp.AddressReply(1, "0.0.0.0"));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.Equal(RouterMappingFailure.MechanismUnsupported, result.Failure);
        Assert.Contains("Unsupported Version", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_stops_the_retry_loop()
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 8));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.DiscoverAsync(gateway.Binding(), cancellation.Token));
        Assert.Empty(gateway.Received);
    }

    [Fact]
    public async Task A_java_mapping_asks_for_tcp_and_never_udp()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "198.51.100.7")
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.SuggestedExternalPort(request), NatPmp.Lifetime(request)));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(25565, outcome.ExternalPort);
        Assert.Equal(3600, outcome.LeaseSeconds);
        Assert.True(outcome.LeaseIsFinite);
        var mapRequests = gateway.Received.Where(request => !NatPmp.IsAddressRequest(request)).ToArray();
        Assert.All(mapRequests, request => Assert.Equal(2, NatPmp.Opcode(request))); // 2 is Map TCP.
    }

    [Fact]
    public async Task A_rejected_mapping_reports_the_gateway_refusal()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "198.51.100.7")
                : NatPmp.MapReply(NatPmp.Opcode(request), 2, NatPmp.InternalPort(request), 0, 0));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.NotAuthorized, outcome.Failure);
    }

    /// <summary>
    /// A different public port means the requested one is taken. ChunkPilot must not present the
    /// substitute as the user's address, and must withdraw the mapping it just created.
    /// </summary>
    [Fact]
    public async Task A_substituted_public_port_is_reported_as_a_conflict_and_withdrawn()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "198.51.100.7")
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.Lifetime(request) == 0 ? 0 : 40001, NatPmp.Lifetime(request)));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, outcome.Failure);
        Assert.NotEqual(40001, outcome.ExternalPort);
        // The last request is the withdrawal: lifetime 0 and suggested external port 0.
        var last = gateway.Received[^1];
        Assert.Equal(0u, NatPmp.Lifetime(last));
        Assert.Equal(0, NatPmp.SuggestedExternalPort(last));
        Assert.Equal(25565, NatPmp.InternalPort(last));
    }

    [Fact]
    public async Task Removal_sends_a_zero_lifetime_request_scoped_to_this_computers_internal_port()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request), 0, 0));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.NatPmp, Supported = true };

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        var request = gateway.Received.Single();
        Assert.Equal(0u, NatPmp.Lifetime(request));
        Assert.Equal(0, NatPmp.SuggestedExternalPort(request));
        Assert.Equal(25565, NatPmp.InternalPort(request));
    }

    [Fact]
    public async Task Removal_failure_is_reported_rather_than_assumed()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.MapReply(NatPmp.Opcode(request), 3, NatPmp.InternalPort(request), 0, 0));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.NatPmp, Supported = true };

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.NetworkFailure, outcome.Failure);
    }

    [Fact]
    public async Task Renewal_repeats_the_same_request_and_accepts_a_shortened_lease()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            NatPmp.IsAddressRequest(request)
                ? NatPmp.AddressReply(0, "198.51.100.7")
                : NatPmp.MapReply(NatPmp.Opcode(request), 0, NatPmp.InternalPort(request),
                    NatPmp.SuggestedExternalPort(request), 120));
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        _ = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        var renewal = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(renewal.Success);
        Assert.Equal(120, renewal.LeaseSeconds);
    }

    /// <summary>NAT-PMP cannot read the table, so it must say so instead of guessing.</summary>
    [Fact]
    public async Task The_mechanism_reports_that_it_cannot_read_existing_mappings()
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var provider = new NatPmpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));

        Assert.False(provider.CanQueryExistingMappings);
        Assert.Null(await provider.QueryAsync(gateway.Binding(),
            new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.NatPmp, Supported = true },
            MappingTransport.Tcp, 25565, CancellationToken.None));
    }

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp,
        InternalPort = 25565,
        ExternalPort = 25565,
        LeaseSeconds = 3600
    };
}
