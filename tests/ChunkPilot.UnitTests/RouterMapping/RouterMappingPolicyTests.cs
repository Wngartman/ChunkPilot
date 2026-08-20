using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// Address classification, mapping ownership and lease renewal: the three rules that decide whether
/// ChunkPilot may claim something, delete something, or must leave it alone.
/// </summary>
public sealed class RouterMappingPolicyTests
{
    [Theory]
    [InlineData("203.0.113.10", RoutableAddressClass.Documentation, false)]
    [InlineData("198.51.100.5", RoutableAddressClass.Documentation, false)]
    [InlineData("8.8.8.8", RoutableAddressClass.GloballyRoutable, false)]
    [InlineData("81.2.69.142", RoutableAddressClass.GloballyRoutable, false)]
    [InlineData("192.168.0.1", RoutableAddressClass.PrivateUse, true)]
    [InlineData("10.4.4.4", RoutableAddressClass.PrivateUse, true)]
    [InlineData("172.20.1.1", RoutableAddressClass.PrivateUse, true)]
    [InlineData("172.32.1.1", RoutableAddressClass.GloballyRoutable, false)]
    [InlineData("100.64.0.1", RoutableAddressClass.SharedAddressSpace, true)]
    [InlineData("100.127.255.254", RoutableAddressClass.SharedAddressSpace, true)]
    [InlineData("100.128.0.1", RoutableAddressClass.GloballyRoutable, false)]
    [InlineData("169.254.10.10", RoutableAddressClass.LinkLocal, false)]
    [InlineData("127.0.0.1", RoutableAddressClass.Loopback, false)]
    [InlineData("0.0.0.0", RoutableAddressClass.Reserved, false)]
    [InlineData("239.1.1.1", RoutableAddressClass.Reserved, false)]
    [InlineData("fd00::1", RoutableAddressClass.PrivateUse, true)]
    [InlineData("2001:db8::1", RoutableAddressClass.Documentation, false)]
    [InlineData("2606:4700::1111", RoutableAddressClass.GloballyRoutable, false)]
    public void Addresses_are_classified_and_only_private_or_shared_suggest_another_layer(
        string address, RoutableAddressClass expected, bool suggestsUpstream)
    {
        var assessment = RouterMappingPolicy.ClassifyExternalAddress(address);

        Assert.Equal(expected, assessment.Class);
        Assert.Equal(suggestsUpstream, assessment.SuggestsUpstreamNat);
        Assert.NotEqual("", assessment.Evidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("999.1.1.1")]
    public void An_unreadable_address_is_unknown_and_claims_no_upstream_layer(string address)
    {
        var assessment = RouterMappingPolicy.ClassifyExternalAddress(address);

        Assert.Equal(RoutableAddressClass.Unknown, assessment.Class);
        Assert.False(assessment.SuggestsUpstreamNat);
    }

    /// <summary>Even a perfect match is not ours without persisted evidence that we created it.</summary>
    [Fact]
    public void Without_persisted_evidence_a_matching_entry_is_still_not_owned()
    {
        var record = Owned() with { HasActiveMapping = false, RemovalPending = false };

        Assert.False(RouterMappingPolicy.ProvesOwnership(record, Matching()));
    }

    [Fact]
    public void A_matching_entry_with_persisted_evidence_is_owned()
    {
        Assert.True(RouterMappingPolicy.ProvesOwnership(Owned(), Matching()));
    }

    [Fact]
    public void A_pending_removal_still_counts_as_evidence_so_cleanup_can_retry()
    {
        var record = Owned() with { HasActiveMapping = false, RemovalPending = true };

        Assert.True(RouterMappingPolicy.ProvesOwnership(record, Matching()));
    }

    [Fact]
    public void A_router_that_reports_no_description_does_not_weaken_ownership()
    {
        Assert.True(RouterMappingPolicy.ProvesOwnership(Owned(), Matching() with { Description = "" }));
    }

    [Theory]
    [InlineData("192.168.1.99", 25565, "ChunkPilot Minecraft")]
    [InlineData("192.168.1.50", 25566, "ChunkPilot Minecraft")]
    [InlineData("192.168.1.50", 25565, "Some other application")]
    public void Any_disagreement_about_the_target_or_description_means_it_is_not_ours(
        string internalClient, int internalPort, string description)
    {
        var existing = Matching() with
        {
            InternalClient = internalClient,
            InternalPort = internalPort,
            Description = description
        };

        Assert.False(RouterMappingPolicy.ProvesOwnership(Owned(), existing));
    }

    [Fact]
    public void A_udp_entry_on_the_same_port_is_not_the_tcp_entry_we_created()
    {
        Assert.False(RouterMappingPolicy.ProvesOwnership(Owned(),
            Matching() with { Transport = MappingTransport.Udp }));
    }

    [Fact]
    public void An_entry_on_a_different_public_port_is_never_ours()
    {
        Assert.False(RouterMappingPolicy.ProvesOwnership(Owned(), Matching() with { ExternalPort = 25570 }));
    }

    [Fact]
    public void Renewal_falls_due_at_half_the_lease()
    {
        var established = DateTimeOffset.UnixEpoch;
        var record = Owned() with { LeaseIsFinite = true, LeaseSeconds = 3600, EstablishedAt = established };

        Assert.False(RouterMappingPolicy.IsRenewalDue(record, established.AddMinutes(29)));
        Assert.True(RouterMappingPolicy.IsRenewalDue(record, established.AddMinutes(31)));
    }

    [Fact]
    public void A_very_short_lease_still_respects_the_minimum_renewal_interval()
    {
        var established = DateTimeOffset.UnixEpoch;
        var record = Owned() with { LeaseIsFinite = true, LeaseSeconds = 20, EstablishedAt = established };

        Assert.Equal(established + RouterMappingPolicy.MinimumRenewalInterval,
            RouterMappingPolicy.RenewalDueAt(established, record.LeaseSeconds));
        Assert.False(RouterMappingPolicy.IsRenewalDue(record, established.AddSeconds(30)));
    }

    [Fact]
    public void A_permanent_entry_is_never_renewed()
    {
        var record = Owned() with
        {
            LeaseIsFinite = false,
            LeaseSeconds = 0,
            EstablishedAt = DateTimeOffset.UnixEpoch
        };

        Assert.False(RouterMappingPolicy.IsRenewalDue(record, DateTimeOffset.UnixEpoch.AddDays(30)));
    }

    [Fact]
    public void Renewal_stops_once_direct_internet_is_off()
    {
        var record = Owned() with
        {
            DirectInternetEnabled = false,
            LeaseIsFinite = true,
            LeaseSeconds = 60,
            EstablishedAt = DateTimeOffset.UnixEpoch
        };

        Assert.False(RouterMappingPolicy.IsRenewalDue(record, DateTimeOffset.UnixEpoch.AddDays(1)));
    }

    /// <summary>The description written to a router must never carry anything identifying.</summary>
    [Fact]
    public void The_mapping_description_names_the_application_and_nothing_personal()
    {
        Assert.Equal("ChunkPilot Minecraft", RouterMappingPolicy.MappingDescription);
    }

    private static RouterMappingRecord Owned() => new()
    {
        ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
        DirectInternetEnabled = true,
        ConsentGranted = true,
        Mechanism = RouterMappingMechanism.UpnpIgd,
        Transport = MappingTransport.Tcp,
        ExternalPort = 25565,
        InternalPort = 25565,
        InternalClient = "192.168.1.50",
        Description = RouterMappingPolicy.MappingDescription,
        HasActiveMapping = true
    };

    private static ExistingRouterMapping Matching() => new()
    {
        ExternalPort = 25565,
        Transport = MappingTransport.Tcp,
        InternalClient = "192.168.1.50",
        InternalPort = 25565,
        Description = RouterMappingPolicy.MappingDescription
    };
}
