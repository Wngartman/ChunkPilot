using System.Net;
using System.Net.Sockets;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// CP-2026-018. A check of an IPv4 router mapping has to leave this computer over IPv4, because the
/// service can only compare the address it observes with the one the router reported. On a dual-stack
/// machine the ordinary transport is free to prefer IPv6, and then a correctly forwarded server is
/// answered with <c>unsupported_address_family</c> and can never be verified.
/// </summary>
/// <remarks>
/// Nothing here opens a socket or resolves a name. The resolver and the connector are fakes, so the
/// answers do not depend on this machine's DNS order, its IPv6 preference or its connectivity, and no
/// test reaches Cloudflare. The provider tests use the real <see cref="SocketsHttpHandler"/> and the
/// real connect callback, so what is proven is the production path with its transport instrumented.
/// </remarks>
public sealed class ExternalProbeIpv4AffinityTests
{
    private const string Endpoint = "https://probe.example.workers.dev";
    private const string Host = "probe.example.workers.dev";
    private const string Public = "93.184.216.34";
    private const string RequestId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static readonly DnsEndPoint Service = new(Host, 443);

    // ── Family selection ──

    /// <summary>Requirement 1: A and AAAA both available, an IPv4 mapping, an IPv4 connection.</summary>
    [Fact]
    public async Task A_dual_stack_service_is_connected_to_over_ipv4_when_ipv4_is_being_verified()
    {
        var resolver = new FakeResolver("2606:4700::6810:85e5", "104.16.132.229");
        var connector = new RecordingConnector();
        var transport = new ExternalProbeTransport(resolver, connector);

        using var stream = await transport.ConnectAsync(
            AddressFamily.InterNetwork, Service, CancellationToken.None);

        Assert.Equal(Host, Assert.Single(resolver.Hosts));
        var attempt = Assert.Single(connector.Attempts);
        Assert.Equal(AddressFamily.InterNetwork, attempt.AddressFamily);
        Assert.Equal("104.16.132.229", attempt.Address.ToString());
        Assert.Equal(443, attempt.Port);
    }

    /// <summary>
    /// Requirement 2: the answer must not turn on which record the resolver happened to list first,
    /// which is exactly what a dual-stack machine varies.
    /// </summary>
    [Theory]
    [InlineData("2606:4700::6810:85e5", "104.16.132.229")]
    [InlineData("104.16.132.229", "2606:4700::6810:85e5")]
    [InlineData("2606:4700::6810:85e5", "2606:4700::6810:84e5", "104.16.132.229")]
    [InlineData("104.16.132.229", "2606:4700::6810:85e5", "104.16.133.229")]
    public async Task Dns_ordering_never_decides_the_address_family(params string[] resolved)
    {
        var connector = new RecordingConnector();
        var transport = new ExternalProbeTransport(new FakeResolver(resolved), connector);

        using var stream = await transport.ConnectAsync(
            AddressFamily.InterNetwork, Service, CancellationToken.None);

        Assert.All(connector.Attempts,
            attempt => Assert.Equal(AddressFamily.InterNetwork, attempt.AddressFamily));
        Assert.Equal("104.16.132.229", connector.Attempts[0].Address.ToString());
    }

    /// <summary>Requirement 3: one dead IPv4 address is tried past, and never past IPv4 itself.</summary>
    [Fact]
    public async Task A_failed_ipv4_address_falls_back_to_the_next_ipv4_address_and_never_to_ipv6()
    {
        var connector = new RecordingConnector((endpoint, _) =>
            endpoint.Address.ToString() == "104.16.132.229"
                ? Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused))
                : Task.FromResult<Stream>(new CapturingStream()));
        var transport = new ExternalProbeTransport(
            new FakeResolver("2606:4700::6810:85e5", "104.16.132.229", "104.16.133.229"), connector);

        using var stream = await transport.ConnectAsync(
            AddressFamily.InterNetwork, Service, CancellationToken.None);

        Assert.Equal("104.16.132.229, 104.16.133.229",
            string.Join(", ", connector.Attempts.Select(attempt => attempt.Address.ToString())));
        Assert.DoesNotContain(connector.Attempts,
            attempt => attempt.AddressFamily == AddressFamily.InterNetworkV6);
    }

    /// <summary>
    /// An address whose own bounded wait expires is not the caller giving up: the next address is
    /// tried. This is the per-address timeout's path, driven directly so it costs no wall-clock time.
    /// </summary>
    [Fact]
    public async Task An_address_that_times_out_on_its_own_budget_does_not_end_the_connection()
    {
        var connector = new RecordingConnector((endpoint, _) =>
            endpoint.Address.ToString() == "104.16.132.229"
                ? Task.FromException<Stream>(new OperationCanceledException())
                : Task.FromResult<Stream>(new CapturingStream()));
        var transport = new ExternalProbeTransport(
            new FakeResolver("104.16.132.229", "104.16.133.229"), connector);

        using var stream = await transport.ConnectAsync(
            AddressFamily.InterNetwork, Service, CancellationToken.None);

        Assert.Equal(2, connector.Attempts.Count);
    }

    /// <summary>A long record set is a bounded number of attempts, never a loop over all of them.</summary>
    [Fact]
    public async Task The_number_of_addresses_tried_is_bounded()
    {
        var connector = new RecordingConnector((_, _) =>
            Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)));
        var transport = new ExternalProbeTransport(
            new FakeResolver("104.16.130.229", "104.16.131.229", "104.16.132.229", "104.16.133.229",
                "104.16.134.229", "104.16.135.229"),
            connector);

        var failure = await Assert.ThrowsAsync<ExternalProbeTransportException>(async () =>
            await transport.ConnectAsync(AddressFamily.InterNetwork, Service, CancellationToken.None));

        Assert.Equal(ExternalProbeTransport.MaximumAddressAttempts, connector.Attempts.Count);
        Assert.Contains("IPv4", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Requirement 4: an IPv6-only service is a failure, never a check made over IPv6.</summary>
    [Fact]
    public async Task An_ipv6_only_service_is_never_connected_to_for_an_ipv4_check()
    {
        var connector = new RecordingConnector();
        var transport = new ExternalProbeTransport(
            new FakeResolver("2606:4700::6810:85e5", "2606:4700::6810:84e5"), connector);

        var failure = await Assert.ThrowsAsync<ExternalProbeTransportException>(async () =>
            await transport.ConnectAsync(AddressFamily.InterNetwork, Service, CancellationToken.None));

        Assert.Empty(connector.Attempts);
        Assert.Contains("IPv4", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The family is a parameter, not an assumption. Nothing in this build asks for IPv6 — the probe
    /// client refuses anything else before it gets here — but the mechanism is not IPv4-shaped, so a
    /// later family needs a caller that asks for it rather than a process-wide switch.
    /// </summary>
    [Fact]
    public async Task The_family_is_the_caller_s_choice_rather_than_a_hard_coded_one()
    {
        var connector = new RecordingConnector();
        var transport = new ExternalProbeTransport(
            new FakeResolver("104.16.132.229", "2606:4700::6810:85e5"), connector);

        using var stream = await transport.ConnectAsync(
            AddressFamily.InterNetworkV6, Service, CancellationToken.None);

        var attempt = Assert.Single(connector.Attempts);
        Assert.Equal(AddressFamily.InterNetworkV6, attempt.AddressFamily);
    }

    [Fact]
    public async Task A_resolution_failure_is_a_transport_failure_rather_than_a_connection()
    {
        var connector = new RecordingConnector();
        var transport = new ExternalProbeTransport(
            new FakeResolver { Failure = new SocketException((int)SocketError.HostNotFound) }, connector);

        _ = await Assert.ThrowsAsync<ExternalProbeTransportException>(async () =>
            await transport.ConnectAsync(AddressFamily.InterNetwork, Service, CancellationToken.None));

        Assert.Empty(connector.Attempts);
    }

    // ── Cancellation (requirement 5) ──

    [Fact]
    public async Task Cancellation_during_resolution_ends_the_attempt_before_anything_is_connected()
    {
        using var cancellation = new CancellationTokenSource();
        var connector = new RecordingConnector();
        var resolver = new FakeResolver("104.16.132.229") { WhenCalled = cancellation.Cancel };
        var transport = new ExternalProbeTransport(resolver, connector);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ConnectAsync(AddressFamily.InterNetwork, Service, cancellation.Token));

        Assert.Empty(connector.Attempts);
    }

    [Fact]
    public async Task Cancellation_during_connect_ends_the_attempt_rather_than_trying_the_next_address()
    {
        using var cancellation = new CancellationTokenSource();
        var connector = new RecordingConnector((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromException<Stream>(new OperationCanceledException());
        });
        var transport = new ExternalProbeTransport(
            new FakeResolver("104.16.132.229", "104.16.133.229"), connector);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ConnectAsync(AddressFamily.InterNetwork, Service, cancellation.Token));

        // The second address is deliberately never reached: the caller stopped, so nothing continues.
        Assert.Single(connector.Attempts);
    }

    [Fact]
    public async Task An_already_cancelled_check_resolves_nothing_and_connects_to_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var resolver = new FakeResolver("104.16.132.229");
        var connector = new RecordingConnector();
        var transport = new ExternalProbeTransport(resolver, connector);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await transport.ConnectAsync(AddressFamily.InterNetwork, Service, cancellation.Token));

        Assert.Empty(resolver.Hosts);
        Assert.Empty(connector.Attempts);
    }

    // ── The production client, with only its resolver and socket replaced ──

    /// <summary>
    /// Requirement 8. The address family is pinned at the socket and nowhere else: the handler is
    /// still asked for the configured hostname, and the TLS ClientHello the production path actually
    /// wrote carries that hostname as its server name rather than the resolved numeric address. The
    /// certificate that would be validated, the SNI and the authority are therefore unchanged.
    /// </summary>
    [Fact]
    public async Task The_production_client_pins_the_socket_and_keeps_the_hostname_as_the_tls_authority()
    {
        var stream = new CapturingStream();
        var resolver = new FakeResolver("2606:4700::6810:85e5", "104.16.132.229");
        var connector = new RecordingConnector((_, _) => Task.FromResult<Stream>(stream));
        using var probe = Probe(resolver, connector);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        // The socket went to an IPv4 address...
        var attempt = Assert.Single(connector.Attempts);
        Assert.Equal(AddressFamily.InterNetwork, attempt.AddressFamily);
        Assert.Equal("104.16.132.229", attempt.Address.ToString());
        // ...and everything above it still speaks to the configured host.
        Assert.Equal(Host, Assert.Single(resolver.Hosts));
        Assert.Equal(Host, ServerNameIndication(stream.Written));
        // The handshake could not complete against a stream that answers nothing, which is a transport
        // failure and never a verdict about the server.
        Assert.Equal(ExternalProbeOutcome.ServiceUnavailable, result.Outcome);
    }

    /// <summary>
    /// Requirement 4 through the production path: no IPv4 address means no check, not a check over
    /// IPv6. The failure is the narrowest truthful one, and it says which family was missing.
    /// </summary>
    [Fact]
    public async Task An_ipv6_only_service_fails_truthfully_rather_than_being_checked_over_ipv6()
    {
        var connector = new RecordingConnector();
        using var probe = Probe(new FakeResolver("2606:4700::6810:85e5"), connector);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.ServiceUnavailable, result.Outcome);
        Assert.NotEqual(ExternalProbeOutcome.Reachable, result.Outcome);
        Assert.Contains("IPv4", result.Detail, StringComparison.Ordinal);
        Assert.Empty(connector.Attempts);
    }

    [Fact]
    public async Task A_service_with_no_reachable_ipv4_address_fails_truthfully()
    {
        var connector = new RecordingConnector((_, _) =>
            Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)));
        using var probe = Probe(new FakeResolver("104.16.132.229", "104.16.133.229"), connector);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.ServiceUnavailable, result.Outcome);
        Assert.Equal(2, connector.Attempts.Count);
        Assert.All(connector.Attempts,
            attempt => Assert.Equal(AddressFamily.InterNetwork, attempt.AddressFamily));
    }

    /// <summary>An address family this path cannot verify is refused before anything is sent.</summary>
    [Theory]
    [InlineData("2606:4700::6810:85e5")]
    [InlineData("")]
    [InlineData("probe.example.com")]
    public async Task An_expected_address_that_is_not_ipv4_never_reaches_the_service(string expected)
    {
        var resolver = new FakeResolver("104.16.132.229");
        var connector = new RecordingConnector();
        using var probe = Probe(resolver, connector);

        var result = await probe.ProbeAsync(Request(expected), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.UnsupportedAddressFamily, result.Outcome);
        Assert.Empty(resolver.Hosts);
        Assert.Empty(connector.Attempts);
    }

    /// <summary>
    /// Cancelling while the connection is being established ends the check and concludes nothing.
    /// </summary>
    /// <remarks>
    /// The socket itself is deliberately not asserted on. A connection is owned by the handler's pool
    /// rather than by the request that triggered it, so cancelling the check stops the check while the
    /// pool's own attempt finishes or expires against its bounded connect budget. That is the
    /// framework's existing behaviour and is unchanged here; what matters is that no result is
    /// produced. Cancellation of the resolve and the connect themselves is proven against the
    /// transport directly, above.
    /// </remarks>
    [Fact]
    public async Task Cancelling_while_the_connection_is_being_made_concludes_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new FakeResolver("104.16.132.229") { WhenCalled = cancellation.Cancel };
        using var probe = Probe(resolver, new RecordingConnector());

        var result = await probe.ProbeAsync(Request(), cancellation.Token);

        Assert.Equal(ExternalProbeOutcome.Cancelled, result.Outcome);
        Assert.Equal("", result.ObservedAddress);
        Assert.Equal(0, result.ConnectMilliseconds);
        Assert.Null(result.CheckedAt);
    }

    // ── The behaviour that was already reviewed, with the pin in place ──

    /// <summary>Requirement 6: an answer about another source address still concludes nothing.</summary>
    [Fact]
    public async Task A_source_mismatch_is_still_no_conclusion()
    {
        using var probe = new HttpExternalReachabilityProbe(
            ExternalReachabilityProbeOptions.Configure(Endpoint),
            new HttpClient(new StubHandler(Answer("source_mismatch", observed: "198.51.100.42"))));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.SourceMismatch, result.Outcome);
        Assert.NotEqual(ExternalProbeOutcome.Reachable, result.Outcome);
        Assert.Equal("198.51.100.42", result.ObservedAddress);
    }

    /// <summary>Requirement 7: a success that does not match the request is still refused.</summary>
    [Theory]
    [InlineData("reachable", "198.51.100.42", "ipv4")]
    [InlineData("reachable", Public, "ipv6")]
    [InlineData("probably_reachable", Public, "ipv4")]
    public async Task A_reachable_answer_that_contradicts_the_request_is_still_refused(
        string result, string observed, string family)
    {
        using var probe = new HttpExternalReachabilityProbe(
            ExternalReachabilityProbeOptions.Configure(Endpoint),
            new HttpClient(new StubHandler(Answer(result, observed, family))));

        var outcome = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse, outcome.Outcome);
    }

    private static HttpExternalReachabilityProbe Probe(
        FakeResolver resolver, RecordingConnector connector) =>
        new(ExternalReachabilityProbeOptions.Configure(Endpoint), httpClient: null,
            transport: new ExternalProbeTransport(resolver, connector));

    private static ExternalProbeRequest Request(string expected = Public) => new()
    {
        RequestId = RequestId,
        ExpectedAddress = expected,
        Port = 25566
    };

    private static HttpResponseMessage Answer(string result, string observed, string family = "ipv4")
    {
        var body = $$"""
            {"apiVersion":1,"requestId":"{{RequestId}}","result":"{{result}}",
             "observedAddress":"{{observed}}","observedFamily":"{{family}}","port":25566,
             "checkedAt":"2026-08-11T19:42:00.000Z","connectMilliseconds":118}
            """;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// The host name in the TLS ClientHello's <c>server_name</c> extension (RFC 6066), or null. It
    /// reads the bytes the production handler actually wrote, so what is asserted is what left the
    /// process rather than what the test arranged.
    /// </summary>
    private static string? ServerNameIndication(byte[] hello)
    {
        // TLS record header (5), handshake header (4), client version (2), random (32).
        var cursor = 43;
        if (hello.Length < cursor || hello[0] != 0x16 || hello[5] != 0x01)
            return null;
        // Session id, cipher suites and compression methods are length-prefixed vectors.
        if (!Skip(hello, ref cursor, 1) || !Skip(hello, ref cursor, 2) || !Skip(hello, ref cursor, 1))
            return null;
        if (cursor + 2 > hello.Length)
            return null;
        var end = Math.Min(hello.Length, cursor + 2 + ((hello[cursor] << 8) | hello[cursor + 1]));
        cursor += 2;
        while (cursor + 4 <= end)
        {
            var type = (hello[cursor] << 8) | hello[cursor + 1];
            var length = (hello[cursor + 2] << 8) | hello[cursor + 3];
            cursor += 4;
            if (type != 0x0000)
            {
                cursor += length;
                continue;
            }
            // server_name_list length (2), name_type (1, zero for host_name), host name length (2).
            if (cursor + 5 > hello.Length || hello[cursor + 2] != 0x00)
                return null;
            var name = (hello[cursor + 3] << 8) | hello[cursor + 4];
            return cursor + 5 + name <= hello.Length
                ? Encoding.ASCII.GetString(hello, cursor + 5, name)
                : null;
        }
        return null;
    }

    private static bool Skip(byte[] buffer, ref int cursor, int prefix)
    {
        if (cursor + prefix > buffer.Length)
            return false;
        var length = 0;
        for (var index = 0; index < prefix; index++)
            length = (length << 8) | buffer[cursor + index];
        cursor += prefix + length;
        return cursor <= buffer.Length;
    }

    private sealed class FakeResolver : IExternalProbeAddressResolver
    {
        private readonly IPAddress[] addresses;

        public FakeResolver(params string[] addresses) =>
            this.addresses = [.. addresses.Select(IPAddress.Parse)];

        public List<string> Hosts { get; } = [];
        public Exception? Failure { get; init; }
        public Action? WhenCalled { get; init; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Hosts.Add(host);
            WhenCalled?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Failure is { } failure
                ? Task.FromException<IReadOnlyList<IPAddress>>(failure)
                : Task.FromResult<IReadOnlyList<IPAddress>>(addresses);
        }
    }

    private sealed class RecordingConnector : IExternalProbeSocketConnector
    {
        private readonly Func<IPEndPoint, CancellationToken, Task<Stream>> behaviour;

        public RecordingConnector(Func<IPEndPoint, CancellationToken, Task<Stream>>? behaviour = null) =>
            this.behaviour = behaviour ?? ((_, _) => Task.FromResult<Stream>(new CapturingStream()));

        public List<IPEndPoint> Attempts { get; } = [];

        public Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
        {
            Attempts.Add(endpoint);
            return behaviour(endpoint, cancellationToken);
        }
    }

    /// <summary>
    /// A transport that records what was written to it and answers nothing, so the handshake the real
    /// handler starts can be inspected and then fails without a network.
    /// </summary>
    private sealed class CapturingStream : Stream
    {
        private readonly MemoryStream written = new();

        public byte[] Written
        {
            get
            {
                lock (written)
                    return written.ToArray();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => 0;

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromResult(0);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (written)
                written.Write(buffer, offset, count);
        }

        public override Task WriteAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (written)
                written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage response;

        public StubHandler(HttpResponseMessage response) => this.response = response;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(response);
    }
}
