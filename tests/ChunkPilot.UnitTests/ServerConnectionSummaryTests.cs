using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class ServerConnectionSummaryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-05T20:00:00Z");
    private static ServerSnapshot Running(string bind = "127.0.0.1") => new()
    {
        Definition = new() { Port = 25586 }, State = ServerState.Running,
        RootProcessId = 42, RootProcessCreationTicks = 1234, LastStartReachedReadiness = true,
        ConnectionEvidence = new()
        {
            Saved = new() { Known = true, BindAddress = bind, Port = 25586 },
            AtLaunch = new() { Known = true, BindAddress = bind, Port = 25586 },
            Listener = new() { ExactOwnerVerified = true, ProcessId = 42, ProcessCreationTicks = 1234,
                CheckedAt = Now, Port = 25586, BindAddresses = [bind] }
        }
    };
    private static ServerConnectionSummary Summary(ServerSnapshot server, NetworkMode mode = NetworkMode.HomeNetwork) =>
        ServerConnectionSummaryPolicy.Build(server, mode, "10.0.0.141", Now, true, true,
            "203.0.113.1:25586", "203.0.113.1:25586");

    [Theory]
    [InlineData(NetworkMode.HomeNetwork)]
    [InlineData(NetworkMode.PortForwarding)]
    public void Loopback_overrides_saved_audience_and_router_evidence(NetworkMode mode)
    {
        var value = Summary(Running(), mode);
        Assert.Equal("Only on this computer", value.Badge);
        Assert.Equal("127.0.0.1:25586", value.Address);
        Assert.Null(value.LanAddress);
        Assert.Null(value.ConfiguredLanAddress);
        Assert.Null(value.PublicVerifiedAddress);
        Assert.True(value.RequestedAudienceNotApplied);
        Assert.Contains("not applied", value.Explanation);
        Assert.True(value.FirewallConfigured); // setup evidence remains separate, not discarded.
    }

    [Theory]
    [InlineData(ServerState.Stopped, "Server stopped")]
    [InlineData(ServerState.Starting, "Server starting")]
    [InlineData(ServerState.Restarting, "Server starting")]
    [InlineData(ServerState.Stopping, "Server stopping")]
    [InlineData(ServerState.Unknown, "Connection not verified")]
    [InlineData(ServerState.Crashed, "Connection not verified")]
    [InlineData(ServerState.Unresponsive, "Connection not verified")]
    public void Lifecycle_wins_over_retained_readiness_and_public_evidence(ServerState state, string badge)
    {
        var value = Summary(Running("0.0.0.0") with { State = state }, NetworkMode.PortForwarding);
        Assert.Equal(badge, value.Badge);
        Assert.False(value.ListenerVerified);
        Assert.Null(value.PublicVerifiedAddress);
        Assert.Null(value.LocalAddress);
        Assert.Null(value.LanAddress);
        if (state == ServerState.Stopped) Assert.Contains("not currently available", value.Explanation);
        else Assert.Null(value.Address);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.141")]
    public void Synthetic_lan_listener_is_not_remote_reachability_proof(string bind)
    {
        var value = Summary(Running(bind));
        Assert.Equal("Listening for home-network connections", value.Badge);
        Assert.Equal("10.0.0.141:25586", value.Address);
        Assert.Contains("LAN access has not been tested", value.Explanation);
        Assert.NotEqual("success", value.Tone);
    }

    [Fact]
    public void Synthetic_ipv6_wildcard_does_not_prove_ipv4_dual_stack()
    {
        var value = Summary(Running("::"));
        Assert.Null(value.LanAddress);
        Assert.Null(value.ConfiguredLanAddress);
        Assert.Equal("[::1]:25586", value.LocalAddress);
        Assert.Equal("[::1]:25586", value.ConfiguredLocalAddress);
    }

    [Theory]
    [InlineData("::1", "[::1]:25586")]
    [InlineData("::ffff:127.0.0.1", "[::ffff:127.0.0.1]:25586")]
    public void Ipv6_loopback_remains_local(string bind, string endpoint)
    {
        var value = Summary(Running(bind));
        Assert.Equal("Only on this computer", value.Badge);
        Assert.Equal(endpoint, value.Address);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("pid")]
    [InlineData("reused-pid")]
    [InlineData("unknown-creation")]
    [InlineData("foreign")]
    [InlineData("port")]
    [InlineData("missing")]
    [InlineData("unknown-address")]
    [InlineData("empty-address")]
    public void Unresolved_or_mismatched_evidence_never_becomes_lan(string fault)
    {
        var server = Running("0.0.0.0");
        var listener = server.ConnectionEvidence.Listener;
        listener = fault switch
        {
            "stale" => listener with { CheckedAt = Now.AddSeconds(-16) },
            "future" => listener with { CheckedAt = Now.AddSeconds(1) },
            "pid" => listener with { ProcessId = 43 },
            "reused-pid" => listener with { ProcessCreationTicks = 5678 },
            "unknown-creation" => listener with { ProcessCreationTicks = 0 },
            "foreign" => listener with { ExactOwnerVerified = false },
            "port" => listener with { Port = 25565 },
            "missing" => listener with { BindAddresses = [] },
            "empty-address" => listener with { BindAddresses = [""] },
            _ => listener with { BindAddresses = ["unresolved.invalid"] }
        };
        var value = Summary(server with { ConnectionEvidence = server.ConnectionEvidence with { Listener = listener } });
        Assert.Equal("Connection not verified", value.Badge);
        Assert.Null(value.Address);
        Assert.Null(value.LanAddress);
    }

    [Fact]
    public void Saved_changes_are_pending_not_a_replacement_for_live_binding()
    {
        var server = Running();
        server = server with { ConnectionEvidence = server.ConnectionEvidence with
            { Saved = new() { Known = true, BindAddress = "", Port = 25587 } } };
        var value = Summary(server);
        Assert.True(value.PendingRestart);
        Assert.True(value.RequestedAudienceNotApplied);
        Assert.Equal("127.0.0.1:25586", value.Address);
        Assert.Equal("10.0.0.141:25587", value.ConfiguredLanAddress);
        Assert.Contains("require a restart", value.Explanation);
    }

    [Fact]
    public void Unknown_binding_does_not_inherit_host_lan_address()
    {
        var value = Summary(new() { Definition = new(), State = ServerState.Stopped });
        Assert.Null(value.ConfiguredLanAddress);
        Assert.Null(value.Address);
    }

    [Fact]
    public void Readiness_is_required_even_with_a_matching_listener()
    {
        Assert.Null(Summary(Running() with { LastStartReachedReadiness = false }).Address);
    }

    [Fact]
    public void Public_diagnostic_and_configured_setup_remain_distinct()
    {
        var server = Running("0.0.0.0");
        var configured = ServerConnectionSummaryPolicy.Build(server, NetworkMode.PortForwarding, "10.0.0.141", Now,
            true, true, "203.0.113.1:25586");
        Assert.Equal("Internet sharing configured", configured.Badge);
        Assert.Null(configured.PublicVerifiedAddress);
        Assert.Equal("router", configured.Kind);
        Assert.Equal("Connection confirmed", Summary(server, NetworkMode.PortForwarding).Badge);
    }
}
