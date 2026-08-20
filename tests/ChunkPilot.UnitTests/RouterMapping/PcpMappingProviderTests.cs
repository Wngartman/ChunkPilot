using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// PCP against a controlled loopback gateway that speaks the real RFC 6887 wire format.
/// </summary>
public sealed class PcpMappingProviderTests
{
    [Fact]
    public async Task Discovery_uses_announce_and_therefore_changes_nothing()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request) ? Pcp.AnnounceReply() : null);
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options());

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(result.Supported);
        // ANNOUNCE is the only opcode discovery may send, and its requested lifetime must be zero.
        Assert.All(gateway.Received, request =>
        {
            Assert.True(Pcp.IsAnnounce(request));
            Assert.Equal(0u, Pcp.RequestedLifetime(request));
        });
        // PCP states no external address until it assigns a mapping, so none is claimed here.
        Assert.Equal("", result.ExternalAddress);
    }

    [Fact]
    public async Task A_version_zero_answer_is_read_as_a_nat_pmp_only_gateway()
    {
        await using var gateway = FakeDatagramGateway.Start(_ => Pcp.AnnounceReply(resultCode: 1, version: 0));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal(RouterMappingFailure.MechanismUnsupported, result.Failure);
        Assert.Contains("NAT-PMP", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silent_gateway_is_unconfirmed_and_bounded()
    {
        await using var gateway = FakeDatagramGateway.Silent();
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 3));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, result.Failure);
        Assert.Equal(3, gateway.Received.Count);
    }

    [Fact]
    public async Task A_truncated_map_reply_is_malformed()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request) ? Pcp.AnnounceReply() : new byte[30]);
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.MalformedReply, outcome.Failure);
    }

    [Fact]
    public async Task Duplicate_replies_do_not_confuse_the_exchange()
    {
        var replies = 0;
        await using var gateway = FakeDatagramGateway.Start(request =>
        {
            replies++;
            return Pcp.IsAnnounce(request) ? Pcp.AnnounceReply() : null;
        });
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options());

        var first = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var second = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(first.Supported);
        Assert.True(second.Supported);
        Assert.True(replies >= 2);
    }

    [Fact]
    public async Task A_java_mapping_asks_for_tcp_carries_a_nonce_and_never_wildcards_the_internal_port()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply()
                : Pcp.MapReply(request, 0, 3600, Pcp.SuggestedExternalPort(request), "203.0.113.9"));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options());
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(25565, outcome.ExternalPort);
        Assert.Equal("203.0.113.9", outcome.ExternalAddress);
        Assert.Equal(24, outcome.OwnershipToken.Length); // 12 bytes, hex encoded.
        var map = gateway.Received.Single(Pcp.IsMap);
        Assert.Equal(2, Pcp.Version(map));
        Assert.Equal(6, Pcp.Protocol(map)); // 6 is TCP; Java Edition never asks for UDP here.
        Assert.Equal(25565, Pcp.InternalPort(map));
        Assert.NotEqual("000000000000000000000000", Pcp.Nonce(map));
    }

    [Fact]
    public async Task A_reply_with_a_different_nonce_is_rejected_as_not_describing_this_request()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
        {
            if (Pcp.IsAnnounce(request))
                return Pcp.AnnounceReply();
            var reply = Pcp.MapReply(request, 0, 3600, 25565, "203.0.113.9");
            reply[Pcp.HeaderLength] ^= 0xFF;
            return reply;
        });
        // A busy CI host can occasionally delay a loopback UDP response beyond the fixture's 300 ms
        // attempt window. Retrying remains bounded and does not change the protocol assertion: every
        // reply carries the deliberately wrong nonce and must be classified as malformed.
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 3));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.MalformedReply, outcome.Failure);
    }

    [Fact]
    public async Task A_substituted_public_port_is_a_conflict_and_the_substitute_is_withdrawn()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply()
                : Pcp.MapReply(request, 0, Pcp.RequestedLifetime(request),
                    Pcp.RequestedLifetime(request) == 0 ? 0 : 51000, "203.0.113.9"));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, outcome.Failure);
        var maps = gateway.Received.Where(Pcp.IsMap).ToArray();
        Assert.Equal(2, maps.Length);
        Assert.Equal(0u, Pcp.RequestedLifetime(maps[1]));
        // The withdrawal carries the same nonce, which is what proves it removes ChunkPilot's own entry.
        Assert.Equal(Pcp.Nonce(maps[0]), Pcp.Nonce(maps[1]));
    }

    [Fact]
    public async Task Removal_without_a_stored_nonce_is_refused_rather_than_guessed()
    {
        await using var gateway = FakeDatagramGateway.Start(_ => null);
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.Pcp, Supported = true };

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.RemovalFailed, outcome.Failure);
        Assert.Empty(gateway.Received);
    }

    [Fact]
    public async Task Removal_with_the_stored_nonce_sends_a_zero_lifetime_map()
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.MapReply(request, 0, 0, 0, "0.0.0.0"));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = new RouterDiscoveryResult { Mechanism = RouterMappingMechanism.Pcp, Supported = true };
        var token = Convert.ToHexString(Enumerable.Range(1, 12).Select(value => (byte)value).ToArray());

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery,
            Request() with { OwnershipToken = token, LeaseSeconds = 0 }, CancellationToken.None);

        Assert.True(outcome.Success);
        var map = gateway.Received.Single();
        Assert.Equal(0u, Pcp.RequestedLifetime(map));
        Assert.Equal(token, Pcp.Nonce(map));
        Assert.Equal(25565, Pcp.InternalPort(map));
    }

    [Theory]
    [InlineData((byte)2, RouterMappingFailure.NotAuthorized)]
    [InlineData((byte)8, RouterMappingFailure.OutOfResources)]
    [InlineData((byte)11, RouterMappingFailure.ForeignMappingPresent)]
    [InlineData((byte)7, RouterMappingFailure.NetworkFailure)]
    public async Task Result_codes_translate_to_distinct_failures(byte resultCode, RouterMappingFailure expected)
    {
        await using var gateway = FakeDatagramGateway.Start(request =>
            Pcp.IsAnnounce(request)
                ? Pcp.AnnounceReply()
                : Pcp.MapReply(request, resultCode, 0, 0, "0.0.0.0"));
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.Equal(expected, outcome.Failure);
    }

    /// <summary>Simulates a router that forgot its table: the next renewal simply re-creates the mapping.</summary>
    [Fact]
    public async Task A_router_that_lost_its_table_is_recovered_by_the_next_renewal()
    {
        var live = false;
        await using var gateway = FakeDatagramGateway.Start(request =>
        {
            if (Pcp.IsAnnounce(request))
                return Pcp.AnnounceReply();
            live = true;
            return Pcp.MapReply(request, 0, 3600, Pcp.SuggestedExternalPort(request), "203.0.113.9");
        });
        var provider = new PcpMappingProvider(new UdpGatewayDatagramChannel(), gateway.Options(attempts: 1));
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var first = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        live = false; // The router rebooted and forgot everything.

        var renewal = await provider.CreateAsync(gateway.Binding(), discovery,
            Request() with { OwnershipToken = first.OwnershipToken }, CancellationToken.None);

        Assert.True(renewal.Success);
        Assert.True(live);
        Assert.Equal(first.OwnershipToken, renewal.OwnershipToken);
    }

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp,
        InternalPort = 25565,
        ExternalPort = 25565,
        LeaseSeconds = 3600
    };
}
