using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// The pure rules: when a check may run at all, and what a result is bound to.
/// </summary>
public sealed class ExternalReachabilityPolicyTests
{
    private static readonly Guid ServerId = Guid.Parse("4c1e0a5e-7a53-4a5f-9a52-2f0f9d1b6f21");
    private const string Public = "93.184.216.34";

    /// <summary>Two establishments of a mapping that is identical in every observable value.</summary>
    private const string FirstMapping = "aaaaaaaaaaaa4aaa8aaaaaaaaaaaaaaa";
    private const string SecondMapping = "bbbbbbbbbbbb4bbb8bbbbbbbbbbbbbbb";

    // ── Eligibility ──

    [Fact]
    public void A_complete_setup_with_a_running_server_is_eligible()
    {
        Assert.Equal(ExternalReachabilityBlocker.None,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566, ActiveRouter()));
    }

    [Fact]
    public void A_build_without_a_probe_endpoint_is_reported_before_anything_else()
    {
        // Telling somebody to start their server when the feature cannot run at all would be a lie
        // by omission, so this comes first.
        Assert.Equal(ExternalReachabilityBlocker.ProbeNotConfigured,
            ExternalReachabilityPolicy.Evaluate(false, ServerState.Stopped, 0, new RouterMappingState()));
    }

    [Theory]
    [InlineData(ServerState.Stopped)]
    [InlineData(ServerState.Crashed)]
    [InlineData(ServerState.Starting)]
    [InlineData(ServerState.Stopping)]
    [InlineData(ServerState.Unknown)]
    public void Only_a_running_server_may_be_checked(ServerState state)
    {
        Assert.Equal(ExternalReachabilityBlocker.ServerNotRunning,
            ExternalReachabilityPolicy.Evaluate(true, state, 25566, ActiveRouter()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void An_unknown_local_port_blocks_the_check(int port)
    {
        Assert.Equal(ExternalReachabilityBlocker.LocalPortUnknown,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, port, ActiveRouter()));
    }

    [Fact]
    public void Direct_internet_being_off_is_named_rather_than_called_a_failed_check()
    {
        Assert.Equal(ExternalReachabilityBlocker.DirectInternetOff,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566,
                ActiveRouter() with { Enabled = false }));
    }

    [Theory]
    [InlineData(RouterMappingPhase.Inactive)]
    [InlineData(RouterMappingPhase.Supported)]
    [InlineData(RouterMappingPhase.Conflict)]
    [InlineData(RouterMappingPhase.NeedsAttention)]
    [InlineData(RouterMappingPhase.Creating)]
    public void A_mapping_that_is_not_open_blocks_the_check(RouterMappingPhase phase)
    {
        Assert.Equal(ExternalReachabilityBlocker.RouterMappingInactive,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566,
                ActiveRouter() with { Phase = phase }));
    }

    [Fact]
    public void A_mapping_with_no_external_port_blocks_the_check()
    {
        Assert.Equal(ExternalReachabilityBlocker.ExternalPortUnknown,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566,
                ActiveRouter() with { ExternalPort = 0 }));
    }

    [Fact]
    public void A_router_that_reported_no_address_blocks_the_check()
    {
        Assert.Equal(ExternalReachabilityBlocker.PublicAddressUnknown,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566,
                ActiveRouter() with { RouterReportedExternalAddress = "" }));
    }

    /// <summary>
    /// The one place the classification produces a stronger diagnosis than a timeout ever could.
    /// </summary>
    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.20.0.1")]
    [InlineData("100.64.9.12")]
    [InlineData("169.254.4.4")]
    [InlineData("127.0.0.1")]
    [InlineData("203.0.113.7")]
    [InlineData("not an address")]
    public void A_router_without_a_public_address_blocks_the_check(string address)
    {
        Assert.Equal(ExternalReachabilityBlocker.PublicAddressNotRoutable,
            ExternalReachabilityPolicy.Evaluate(true, ServerState.Running, 25566,
                ActiveRouter() with { RouterReportedExternalAddress = address }));
    }

    // ── Endpoint identity ──

    [Fact]
    public void A_complete_endpoint_names_the_mapping_instance_and_the_exact_run()
    {
        var endpoint = Endpoint();

        Assert.True(endpoint.IsComplete);
        Assert.Equal(Public, endpoint.PublicAddress);
        Assert.Equal(25566, endpoint.ExternalPort);
        Assert.Contains(FirstMapping, endpoint.MappingIdentity, StringComparison.Ordinal);
        Assert.Contains("UpnpIgd", endpoint.MappingIdentity, StringComparison.Ordinal);
        Assert.Contains("192.168.1.50:25566->25566", endpoint.MappingIdentity, StringComparison.Ordinal);
        Assert.Contains("4114@", endpoint.RunIdentity, StringComparison.Ordinal);
    }

    /// <summary>
    /// The defect this identity exists for: a router that drops an entry and is asked for the same one
    /// again produces something equal in mechanism, transport, client, both ports and address.
    /// </summary>
    [Fact]
    public void A_second_establishment_with_identical_values_is_a_different_mapping()
    {
        var first = Endpoint();
        var second = Endpoint(router: ActiveRouter() with { MappingInstanceId = SecondMapping });

        Assert.Equal(first.PublicAddress, second.PublicAddress);
        Assert.Equal(first.ExternalPort, second.ExternalPort);
        Assert.Equal(first.InternalPort, second.InternalPort);
        Assert.Equal(first.RunIdentity, second.RunIdentity);
        Assert.NotEqual(first.MappingIdentity, second.MappingIdentity);
        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// Fail closed: an open mapping ChunkPilot cannot tell apart from another one yields no identity
    /// at all, rather than an identity made of values that repeat.
    /// </summary>
    [Fact]
    public void An_active_mapping_with_no_instance_identity_yields_no_identity()
    {
        var router = ActiveRouter() with { MappingInstanceId = "" };

        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(router));
        Assert.False(Endpoint(router: router).IsComplete);
    }

    [Fact]
    public void The_same_situation_always_produces_the_same_identity()
    {
        Assert.Equal(Endpoint(), Endpoint());
    }

    /// <summary>Each of these is a change that must end a verified claim, and each one alone does.</summary>
    [Fact]
    public void Every_change_that_invalidates_evidence_changes_the_identity()
    {
        var original = Endpoint();

        Assert.NotEqual(original, Endpoint(state: ServerState.Stopped));
        Assert.NotEqual(original, Endpoint(processId: 4115));
        Assert.NotEqual(original, Endpoint(startedAt: DateTimeOffset.UnixEpoch.AddMinutes(1)));
        Assert.NotEqual(original, Endpoint(internalPort: 25567));
        Assert.NotEqual(original, Endpoint(router: ActiveRouter() with { ExternalPort = 25567 }));
        Assert.NotEqual(original, Endpoint(router: ActiveRouter() with { RouterReportedExternalAddress = "93.184.216.99" }));
        Assert.NotEqual(original, Endpoint(router: ActiveRouter() with { Phase = RouterMappingPhase.Inactive }));
        Assert.NotEqual(original, Endpoint(router: ActiveRouter() with { InternalClient = "192.168.1.51" }));
        Assert.NotEqual(original, Endpoint(router: ActiveRouter() with { Mechanism = RouterMappingMechanism.Pcp }));
        Assert.NotEqual(original, Endpoint(router: ActiveRouter() with { MappingInstanceId = SecondMapping }));
        Assert.NotEqual(original, Endpoint(serverId: Guid.NewGuid()));
    }

    [Fact]
    public void An_incomplete_endpoint_can_never_equal_a_complete_one()
    {
        var stopped = Endpoint(state: ServerState.Stopped);

        Assert.False(stopped.IsComplete);
        Assert.Equal("", stopped.RunIdentity);
        Assert.NotEqual(Endpoint(), stopped);
    }

    [Fact]
    public void A_mapping_that_is_not_active_has_no_identity_at_all()
    {
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(
            ActiveRouter() with { Phase = RouterMappingPhase.Inactive }));
        Assert.Equal("", ExternalReachabilityPolicy.DescribeMapping(new RouterMappingState()));
    }

    // ── Correlation ids ──

    [Fact]
    public void Correlation_ids_are_128_bits_of_lowercase_hex_and_never_repeat()
    {
        var ids = Enumerable.Range(0, 256).Select(_ => ExternalReachabilityPolicy.NewRequestId()).ToArray();

        Assert.All(ids, id =>
        {
            Assert.Equal(32, id.Length);
            Assert.All(id, character => Assert.True(char.IsAsciiDigit(character) ||
                                                    character is >= 'a' and <= 'f'));
        });
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_client_composes_the_probe_path_itself()
    {
        Assert.Equal("/v1/probe", ExternalReachabilityPolicy.ProbePath);
        Assert.Equal(1, ExternalReachabilityPolicy.ApiVersion);
    }

    // ── The rendered state ──

    [Fact]
    public void A_verified_endpoint_is_only_offered_for_a_reachable_result()
    {
        var reachable = new ExternalReachabilityState
        {
            Phase = ExternalReachabilityPhase.Reachable,
            ObservedAddress = Public,
            Port = 25566
        };

        Assert.Equal($"{Public}:25566", reachable.VerifiedEndpoint);
        Assert.True(reachable.IsVerified);
        foreach (var phase in Enum.GetValues<ExternalReachabilityPhase>()
                     .Where(value => value != ExternalReachabilityPhase.Reachable))
        {
            var other = reachable with { Phase = phase };
            Assert.False(other.IsVerified);
            Assert.Equal("", other.VerifiedEndpoint);
        }
    }

    [Fact]
    public void The_check_action_is_only_enabled_when_every_prerequisite_is_met()
    {
        var ready = new ExternalReachabilityState { ProbeConfigured = true };

        Assert.True(ready.CanCheck);
        Assert.False((ready with { Busy = true }).CanCheck);
        Assert.False((ready with { ProbeConfigured = false }).CanCheck);
        Assert.False((ready with { Blocker = ExternalReachabilityBlocker.ServerNotRunning }).CanCheck);
    }

    private static RouterMappingState ActiveRouter() => new()
    {
        ServerId = ServerId,
        Enabled = true,
        ConsentGranted = true,
        Phase = RouterMappingPhase.Active,
        Mechanism = RouterMappingMechanism.UpnpIgd,
        Transport = MappingTransport.Tcp,
        InternalClient = "192.168.1.50",
        InternalPort = 25566,
        ExternalPort = 25566,
        RouterReportedExternalAddress = Public,
        RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable,
        MappingInstanceId = FirstMapping
    };

    private static ExternalReachabilityEndpoint Endpoint(
        Guid? serverId = null,
        ServerState state = ServerState.Running,
        int internalPort = 25566,
        int? processId = 4114,
        DateTimeOffset? startedAt = null,
        RouterMappingState? router = null) =>
        ExternalReachabilityPolicy.ComposeEndpoint(
            serverId ?? ServerId, state, internalPort, processId,
            startedAt ?? DateTimeOffset.UnixEpoch, router ?? ActiveRouter());
}
