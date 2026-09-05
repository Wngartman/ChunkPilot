using System.Net;
using System.Net.NetworkInformation;

namespace ChunkPilot.Core;

public enum StartupEndpointTransport { Tcp, Udp }
public enum StartupEndpointClassification
{
    ExpectedGameListener,
    AdditionalLoopbackEndpoint,
    UnexpectedInboundListener,
    NonListeningConnectionDirectionUnknown,
    OutboundConnectionAttemptObserved,
    UdpPurposeUnknown,
    ObservationUnresolved
}

/// <summary>One observation, not a claim about sockets between observations or traffic contents.</summary>
public sealed record StartupEndpointObservation
{
    public Guid AttemptId { get; init; }
    public StartupEndpointTransport Transport { get; init; }
    public string AddressFamily { get; init; } = "";
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string? RemoteAddress { get; init; }
    public int? RemotePort { get; init; }
    public TcpState? State { get; init; }
    public int ProcessId { get; init; }
    public long ProcessCreationTicks { get; init; }
    public string Executable { get; init; } = "";
    public bool ExactJobOwnershipVerified { get; init; }
    public DateTimeOffset ObservedAtUtc { get; init; }
    public string Source { get; init; } = "Windows IP Helper owner-PID table";
}

public sealed record StartupEndpointFinding(
    StartupEndpointObservation Observation, StartupEndpointClassification Classification, string Explanation);

public sealed record StartupNetworkDecision(
    bool InventorySucceeded, bool OwnershipVerified, bool ExpectedGameListener,
    IReadOnlyList<StartupEndpointFinding> Findings)
{
    public bool UnexpectedInboundListener => Findings.Any(f =>
        f.Classification == StartupEndpointClassification.UnexpectedInboundListener);
    public bool Unresolved => !InventorySucceeded || !OwnershipVerified || Findings.Any(f =>
        f.Classification is StartupEndpointClassification.UdpPurposeUnknown or
            StartupEndpointClassification.ObservationUnresolved);
    public bool Passed => ExpectedGameListener && !UnexpectedInboundListener && !Unresolved;
    public string Summary => UnexpectedInboundListener
        ? "Unexpected inbound TCP listener detected outside loopback."
        : Unresolved ? "Startup check could not verify endpoint purpose or exact process ownership."
        : !ExpectedGameListener ? "The expected local game listener was not observed."
        : "Expected local game listener verified; no non-loopback inbound service was established by these observations. Connection purpose and traffic contents are not certified.";
}

/// <summary>
/// Shared production/certifier inbound-startup decision. This is not an air gap, egress allowlist,
/// preventive sandbox, or assurance about mod behavior. Established alone never establishes direction.
/// </summary>
public static class StartupNetworkPolicy
{
    public static StartupNetworkDecision Evaluate(Guid attemptId,
        IReadOnlyList<StartupEndpointObservation> endpoints, IPAddress expectedAddress, int expectedPort,
        bool inventorySucceeded = true, bool ownershipVerified = true)
    {
        if (attemptId == Guid.Empty) throw new ArgumentException("An exact validation attempt is required.", nameof(attemptId));
        if (!IPAddress.IsLoopback(expectedAddress) || expectedPort is < 1 or > 65535)
            throw new ArgumentException("Startup validation requires an exact loopback game endpoint.");
        var findings = endpoints.Select(endpoint => Classify(endpoint)).ToArray();
        return new(inventorySucceeded, ownershipVerified,
            inventorySucceeded && ownershipVerified && findings.Any(f =>
                f.Classification == StartupEndpointClassification.ExpectedGameListener), findings);

        StartupEndpointFinding Classify(StartupEndpointObservation endpoint)
        {
            StartupEndpointFinding Result(StartupEndpointClassification classification, string explanation) =>
                new(endpoint, classification, explanation);
            if (endpoint.AttemptId != attemptId || !endpoint.ExactJobOwnershipVerified ||
                endpoint.ProcessId <= 0 || endpoint.ProcessCreationTicks <= 0 ||
                string.IsNullOrWhiteSpace(endpoint.Executable) || endpoint.ObservedAtUtc == default ||
                !IPAddress.TryParse(endpoint.LocalAddress, out var address) ||
                endpoint.LocalPort is < 1 or > 65535 || !Enum.IsDefined(endpoint.Transport))
                return Result(StartupEndpointClassification.ObservationUnresolved,
                    "Observation does not prove the current attempt and exact live Job-owned process generation.");
            var local = IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
            if (endpoint.Transport == StartupEndpointTransport.Udp)
            {
                if (endpoint.State is not null || endpoint.RemoteAddress is not null || endpoint.RemotePort is not null)
                    return Result(StartupEndpointClassification.ObservationUnresolved,
                        "The owner-PID UDP table does not establish a remote peer or connection state.");
                return local
                    ? Result(StartupEndpointClassification.AdditionalLoopbackEndpoint,
                        "UDP binding is local; its application purpose is not established.")
                    : Result(StartupEndpointClassification.UdpPurposeUnknown,
                        "Non-loopback UDP binding may be send-only; this table cannot verify its inbound purpose.");
            }
            if (endpoint.State is null or TcpState.Unknown || !Enum.IsDefined(endpoint.State.Value))
                return Result(StartupEndpointClassification.ObservationUnresolved, "TCP state was not established.");
            if (endpoint.State == TcpState.Listen)
                return !local
                    ? Result(StartupEndpointClassification.UnexpectedInboundListener,
                        "Exact-owned TCP LISTEN row is bound outside loopback; remote fields have no meaning.")
                    : address.Equals(expectedAddress) && endpoint.LocalPort == expectedPort
                        ? Result(StartupEndpointClassification.ExpectedGameListener, "Exact loopback game address and port observed in TCP LISTEN state.")
                        : Result(StartupEndpointClassification.AdditionalLoopbackEndpoint, "Additional TCP listener is bound only to loopback.");
            return endpoint.State == TcpState.SynSent
                ? Result(StartupEndpointClassification.OutboundConnectionAttemptObserved,
                    "SYN-SENT establishes a connection attempt initiated here, not its destination hostname, purpose, or safety.")
                : Result(StartupEndpointClassification.NonListeningConnectionDirectionUnknown,
                    "Non-listening TCP state; direction, destination hostname, and purpose are not established by this row.");
        }
    }
}
