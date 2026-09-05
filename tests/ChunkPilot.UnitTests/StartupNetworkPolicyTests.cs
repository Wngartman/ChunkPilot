using System.Net;
using System.Net.NetworkInformation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class StartupNetworkPolicyTests
{
    private static readonly Guid Attempt = Guid.Parse("67c27692-3398-4d34-a66c-6b651a180ad4");
    private static StartupEndpointObservation Endpoint(string local = "127.0.0.1", int port = 25585,
        TcpState state = TcpState.Listen) => new()
    {
        AttemptId = Attempt, LocalAddress = local, LocalPort = port, State = state,
        ProcessId = 200, ProcessCreationTicks = 123456, Executable = "fixture-java.exe",
        ObservedAtUtc = DateTimeOffset.UtcNow, ExactJobOwnershipVerified = true
    };
    private static StartupNetworkDecision Evaluate(params StartupEndpointObservation[] endpoints) =>
        StartupNetworkPolicy.Evaluate(Attempt, endpoints, IPAddress.Loopback, 25585);

    [Fact]
    public void Established_external_connection_does_not_reject_expected_listener_or_invent_direction()
    {
        var decision = Evaluate(Endpoint(), Endpoint("192.0.2.10", 49000, TcpState.Established) with
        { RemoteAddress = "203.0.113.20", RemotePort = 443 });
        Assert.True(decision.Passed);
        Assert.False(decision.UnexpectedInboundListener);
        Assert.Contains(decision.Findings, f => f.Classification ==
            StartupEndpointClassification.NonListeningConnectionDirectionUnknown);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("192.0.2.10")]
    [InlineData("2001:db8::10")]
    public void Owned_nonloopback_listener_blocks(string address)
    {
        var decision = Evaluate(Endpoint(), Endpoint(address, 25586));
        Assert.False(decision.Passed);
        Assert.True(decision.UnexpectedInboundListener);
    }

    [Theory]
    [InlineData(TcpState.Closed)]
    [InlineData(TcpState.SynReceived)]
    [InlineData(TcpState.Established)]
    [InlineData(TcpState.FinWait1)]
    [InlineData(TcpState.FinWait2)]
    [InlineData(TcpState.CloseWait)]
    [InlineData(TcpState.Closing)]
    [InlineData(TcpState.LastAck)]
    [InlineData(TcpState.TimeWait)]
    [InlineData(TcpState.DeleteTcb)]
    public void Nonlistening_state_alone_never_becomes_listener_or_direction(TcpState state)
    {
        var decision = Evaluate(Endpoint(), Endpoint("192.0.2.10", 49000, state));
        Assert.True(decision.Passed);
        Assert.Equal(StartupEndpointClassification.NonListeningConnectionDirectionUnknown,
            decision.Findings[1].Classification);
    }

    [Fact]
    public void SynSent_supports_only_outbound_attempt_not_purpose()
    {
        var decision = Evaluate(Endpoint(), Endpoint("192.0.2.10", 49000, TcpState.SynSent));
        Assert.True(decision.Passed);
        Assert.Equal(StartupEndpointClassification.OutboundConnectionAttemptObserved, decision.Findings[1].Classification);
    }

    [Theory]
    [InlineData("0.0.0.0", 53000)]
    [InlineData("::", 53)]
    [InlineData("192.0.2.10", 65000)]
    public void Udp_purpose_remains_unknown_regardless_of_port(string local, int port)
    {
        var decision = Evaluate(Endpoint(), Endpoint(local, port) with
        { Transport = StartupEndpointTransport.Udp, State = null });
        Assert.False(decision.Passed);
        Assert.True(decision.Unresolved);
        Assert.False(decision.UnexpectedInboundListener);
        Assert.Equal(StartupEndpointClassification.UdpPurposeUnknown, decision.Findings[1].Classification);
    }

    [Fact]
    public void Missing_ownership_stale_attempt_and_collector_error_cannot_prove_safe()
    {
        Assert.False(Evaluate(Endpoint() with { ExactJobOwnershipVerified = false }).Passed);
        Assert.False(Evaluate(Endpoint() with { ProcessCreationTicks = 0 }).Passed);
        Assert.False(Evaluate(Endpoint() with { AttemptId = Guid.NewGuid() }).Passed);
        Assert.False(Evaluate(Endpoint() with { State = (TcpState)999 }).Passed);
        Assert.False(StartupNetworkPolicy.Evaluate(Attempt, [], IPAddress.Loopback, 25585, false, false).Passed);
        Assert.False(Evaluate(Endpoint("::1")).ExpectedGameListener);
        Assert.True(Evaluate(Endpoint(), Endpoint("::ffff:127.0.0.1", 25586)).Passed);
    }
}
