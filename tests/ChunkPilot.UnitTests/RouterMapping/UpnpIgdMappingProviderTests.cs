using System.Net;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// UPnP IGD against a controlled loopback gateway that serves a real device description and answers
/// real SOAP envelopes.
/// </summary>
public sealed class UpnpIgdMappingProviderTests
{
    [Fact]
    public async Task Discovery_resolves_the_control_url_and_reads_the_external_address()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal(FakeUpnpGateway.ServiceType, result.ServiceType);
        Assert.EndsWith("/ctl/IPConn", result.ControlUrl, StringComparison.Ordinal);
        Assert.Equal("203.0.113.10", result.ExternalAddress);
    }

    /// <summary>Any LAN device can answer an SSDP search. Only the router's answer may be used.</summary>
    [Fact]
    public async Task An_answer_from_a_device_other_than_the_gateway_is_ignored()
    {
        await using var gateway = new FakeUpnpGateway();
        var impostor = gateway.Response() with { Source = IPAddress.Parse("192.168.1.77") };
        var provider = gateway.Provider(new StubSsdpChannel([impostor]));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal(RouterMappingFailure.MechanismUnsupported, result.Failure);
    }

    [Fact]
    public async Task No_ssdp_answer_at_all_is_reported_as_no_response()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider(new StubSsdpChannel([]));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.Equal(RouterMappingFailure.GatewayDidNotRespond, result.Failure);
    }

    [Fact]
    public async Task Duplicate_ssdp_answers_produce_one_discovery()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider(
            new StubSsdpChannel([gateway.Response(), gateway.Response(), gateway.Response()]));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Single(gateway.Actions, action => action == "GetExternalIPAddress");
    }

    [Fact]
    public async Task A_location_that_serves_no_readable_description_is_reported_as_unsupported()
    {
        await using var gateway = new FakeUpnpGateway();
        var dead = gateway.Response() with { Location = "http://127.0.0.1:1/rootDesc.xml" };
        var provider = gateway.Provider(new StubSsdpChannel([dead]));

        var result = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        Assert.False(result.Supported);
        Assert.Equal(RouterMappingFailure.MechanismUnsupported, result.Failure);
    }

    [Fact]
    public async Task A_free_port_is_reported_as_having_no_existing_mapping()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var existing = await provider.QueryAsync(gateway.Binding(), discovery, MappingTransport.Tcp, 25565,
            CancellationToken.None);

        Assert.Null(existing);
        Assert.Contains("GetSpecificPortMappingEntry", gateway.Actions, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_created_mapping_is_tcp_wildcard_sourced_enabled_and_described()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.True(outcome.LeaseIsFinite);
        Assert.Equal(3600, outcome.LeaseSeconds);
        var stored = gateway.Mappings["TCP:25565"];
        Assert.Equal(MappingTransport.Tcp, stored.Transport);
        Assert.Equal(25565, stored.InternalPort);
        Assert.Equal("127.0.0.1", stored.InternalClient);
        Assert.True(stored.Enabled);
        Assert.Equal(RouterMappingPolicy.MappingDescription, stored.Description);
        Assert.DoesNotContain("UDP:25565", gateway.Mappings.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_foreign_entry_on_the_port_is_read_and_reported_rather_than_overwritten()
    {
        await using var gateway = new FakeUpnpGateway();
        gateway.Mappings["TCP:25565"] = new ExistingRouterMapping
        {
            ExternalPort = 25565,
            Transport = MappingTransport.Tcp,
            InternalClient = "192.168.1.90",
            InternalPort = 25565,
            Description = "Someone else's console"
        };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var existing = await provider.QueryAsync(gateway.Binding(), discovery, MappingTransport.Tcp, 25565,
            CancellationToken.None);

        Assert.NotNull(existing);
        Assert.Equal("192.168.1.90", existing.InternalClient);
        Assert.Equal("Someone else's console", existing.Description);
        // Reading must not have changed anything.
        Assert.Equal("Someone else's console", gateway.Mappings["TCP:25565"].Description);
    }

    [Fact]
    public async Task Error_718_is_translated_into_a_conflict()
    {
        await using var gateway = new FakeUpnpGateway { AddErrorCode = 718 };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.ForeignMappingPresent, outcome.Failure);
        Assert.Contains("ConflictInMappingEntry", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Error_606_is_translated_into_a_refusal()
    {
        await using var gateway = new FakeUpnpGateway { AddErrorCode = 606 };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.Equal(RouterMappingFailure.NotAuthorized, outcome.Failure);
    }

    /// <summary>
    /// A router that only does permanent entries is a materially different state and must be reported
    /// as such, not silently treated as a timed lease.
    /// </summary>
    [Fact]
    public async Task Error_725_retries_once_as_a_permanent_entry_and_says_so()
    {
        await using var gateway = new FakeUpnpGateway
        {
            AddErrorSelector = body =>
                int.Parse(FakeUpnpGateway.Argument(body, "NewLeaseDuration")) == 0 ? 0 : 725
        };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.False(outcome.LeaseIsFinite);
        Assert.Equal(0, outcome.LeaseSeconds);
        Assert.Contains("725", outcome.Detail, StringComparison.Ordinal);
        Assert.Equal(0, gateway.Mappings["TCP:25565"].LeaseSeconds);
    }

    [Fact]
    public async Task Removal_deletes_the_entry()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        _ = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Empty(gateway.Mappings);
    }

    [Fact]
    public async Task Removing_an_entry_that_is_already_gone_succeeds()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Contains("714", outcome.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removal_failure_is_reported_rather_than_assumed()
    {
        await using var gateway = new FakeUpnpGateway { DeleteErrorCode = 501 };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        _ = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        var outcome = await provider.RemoveAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.RemovalFailed, outcome.Failure);
        Assert.Single(gateway.Mappings);
    }

    [Fact]
    public async Task Renewal_refreshes_the_same_entry_rather_than_adding_another()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        _ = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);
        var renewal = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(renewal.Success);
        Assert.Single(gateway.Mappings);
    }

    [Fact]
    public async Task An_unreachable_gateway_fails_without_throwing()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        await gateway.DisposeAsync();

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.NetworkFailure, outcome.Failure);
    }

    /// <summary>
    /// The exact order a real router saw: describe, read the external address, read the port, then ask
    /// for the mapping. The acceptance failure stopped after the second step, so this pins the whole run.
    /// </summary>
    [Fact]
    public async Task Discovery_then_add_port_mapping_performs_the_real_router_sequence()
    {
        await using var gateway = new FakeUpnpGateway();
        var provider = gateway.Provider();

        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var existing = await provider.QueryAsync(gateway.Binding(), discovery, MappingTransport.Tcp, 25565,
            CancellationToken.None);
        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.True(discovery.Supported);
        Assert.Null(existing);
        Assert.True(outcome.Success);
        Assert.Equal(
            ["GetExternalIPAddress", "GetSpecificPortMappingEntry", "AddPortMapping"],
            gateway.Actions);
    }

    /// <summary>An HTTP failure with no UPnPError body must still be a reported failure, not silence.</summary>
    [Fact]
    public async Task An_http_failure_without_a_soap_fault_is_reported_as_a_network_failure()
    {
        await using var gateway = new FakeUpnpGateway { AddReturnsBodylessHttpFailure = true };
        var provider = gateway.Provider();
        var discovery = await provider.DiscoverAsync(gateway.Binding(), CancellationToken.None);

        var outcome = await provider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.NetworkFailure, outcome.Failure);
        Assert.NotEqual("", outcome.Detail);
        Assert.Empty(gateway.Mappings);
    }

    [Fact]
    public async Task A_gateway_that_stops_answering_times_out_and_says_so()
    {
        await using var gateway = new FakeUpnpGateway();
        // Prove discovery with the fixture's normal response window first. The short timeout below
        // belongs only to the deliberately stalled control call; using it for discovery lets a busy
        // hosted runner misclassify a delayed loopback response as unsupported.
        var discoveryProvider = gateway.Provider();
        var discovery = await discoveryProvider.DiscoverAsync(gateway.Binding(), CancellationToken.None);
        var options = new RouterMappingOptions
        {
            SsdpMaximumWait = TimeSpan.FromMilliseconds(200),
            HttpTimeout = TimeSpan.FromSeconds(1)
        };
        var timeoutProvider = gateway.Provider(options: options);
        gateway.ResponseDelay = TimeSpan.FromSeconds(5);

        var outcome = await timeoutProvider.CreateAsync(gateway.Binding(), discovery, Request(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(RouterMappingFailure.NetworkFailure, outcome.Failure);
        Assert.Empty(gateway.Mappings);
    }

    [Fact]
    public void An_http_failure_carrying_no_upnp_error_is_not_mistaken_for_success()
    {
        var parsed = UpnpControlChannel.ParseResponse("AddPortMapping", httpSuccess: false,
            "<html><body>Internal error</body></html>");

        Assert.False(parsed.Success);
        Assert.Equal(0, parsed.ErrorCode);
    }

    [Fact]
    public void The_soap_envelope_carries_the_action_and_arguments_in_order()
    {
        var envelope = UpnpControlChannel.BuildEnvelope(FakeUpnpGateway.ServiceType, "AddPortMapping",
        [
            new KeyValuePair<string, string>("NewRemoteHost", ""),
            new KeyValuePair<string, string>("NewExternalPort", "25565")
        ]);

        Assert.Contains($"<u:AddPortMapping xmlns:u=\"{FakeUpnpGateway.ServiceType}\">", envelope,
            StringComparison.Ordinal);
        Assert.True(envelope.IndexOf("NewRemoteHost", StringComparison.Ordinal) <
                    envelope.IndexOf("NewExternalPort", StringComparison.Ordinal));
    }

    [Fact]
    public void A_response_that_is_not_xml_fails_without_throwing()
    {
        var parsed = UpnpControlChannel.ParseResponse("AddPortMapping", httpSuccess: true, "<not xml");

        Assert.False(parsed.Success);
        Assert.Equal(0, parsed.ErrorCode);
    }

    private static RouterMappingRequest Request() => new()
    {
        Transport = MappingTransport.Tcp,
        InternalPort = 25565,
        ExternalPort = 25565,
        LeaseSeconds = 3600,
        Description = RouterMappingPolicy.MappingDescription
    };
}
