using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// The HTTPS provider, against a recording handler. No test here opens a socket: the transport is
/// replaced, so nothing reaches a network.
/// </summary>
public sealed class HttpExternalReachabilityProbeTests
{
    private const string Endpoint = "https://probe.example.workers.dev";
    private const string Public = "93.184.216.34";
    private const string RequestId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    // ── The request ──

    [Fact]
    public async Task The_request_is_a_post_of_exactly_the_four_contract_fields()
    {
        var handler = new RecordingHandler(Ok("reachable"));
        using var probe = Probe(handler);

        _ = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("https://probe.example.workers.dev/v1/probe", handler.LastUri!.ToString());
        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.Equal(4, root.EnumerateObject().Count());
        Assert.Equal(1, root.GetProperty("apiVersion").GetInt32());
        Assert.Equal(RequestId, root.GetProperty("requestId").GetString());
        Assert.Equal(Public, root.GetProperty("expectedAddress").GetString());
        Assert.Equal(25566, root.GetProperty("port").GetInt32());
    }

    /// <summary>Nothing that could identify the machine, the user or the server travels with a check.</summary>
    [Fact]
    public async Task The_request_carries_no_identity_beyond_the_contract()
    {
        var handler = new RecordingHandler(Ok("reachable"));
        using var probe = Probe(handler);

        _ = await probe.ProbeAsync(Request(), CancellationToken.None);

        var body = handler.LastBody!;
        foreach (var forbidden in new[] { "server", "world", "player", "user", "machine", "name", "token" })
            Assert.DoesNotContain(forbidden, body, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.LastHeaders!.Authorization);
        Assert.DoesNotContain(handler.LastHeaders.Select(header => header.Key),
            key => key.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
                   key.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unconfigured_build_sends_nothing_at_all()
    {
        var handler = new RecordingHandler(Ok("reachable"));
        using var probe = new HttpExternalReachabilityProbe(
            ExternalReachabilityProbeOptions.Configure(null), new HttpClient(handler));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.NotConfigured, result.Outcome);
        Assert.Equal(0, handler.Calls);
        Assert.False(probe.IsConfigured);
    }

    // ── Mapping the service's vocabulary ──

    [Theory]
    [InlineData("reachable", ExternalProbeOutcome.Reachable)]
    [InlineData("unreachable", ExternalProbeOutcome.Unreachable)]
    [InlineData("source_mismatch", ExternalProbeOutcome.SourceMismatch)]
    [InlineData("unsupported_address_family", ExternalProbeOutcome.UnsupportedAddressFamily)]
    [InlineData("rate_limited", ExternalProbeOutcome.RateLimited)]
    [InlineData("probe_error", ExternalProbeOutcome.ProbeError)]
    [InlineData("invalid_request", ExternalProbeOutcome.InvalidRequest)]
    public async Task Every_contract_result_maps_to_exactly_one_outcome(string result, ExternalProbeOutcome expected)
    {
        using var probe = Probe(new RecordingHandler(Ok(result)));

        Assert.Equal(expected, (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_result_this_build_does_not_recognise_is_refused_rather_than_guessed_at()
    {
        using var probe = Probe(new RecordingHandler(Ok("probably_reachable")));

        Assert.Equal(ExternalProbeOutcome.MalformedResponse,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_reachable_answer_carries_the_evidence_the_interface_shows()
    {
        using var probe = Probe(new RecordingHandler(Ok("reachable", connectMilliseconds: 118)));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.Reachable, result.Outcome);
        Assert.Equal(Public, result.ObservedAddress);
        Assert.Equal("ipv4", result.ObservedFamily);
        Assert.Equal(25566, result.Port);
        Assert.Equal(118, result.ConnectMilliseconds);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 19, 42, 0, TimeSpan.Zero), result.CheckedAt);
    }

    [Fact]
    public async Task A_source_mismatch_keeps_the_observed_address_so_both_sides_can_be_shown()
    {
        using var probe = Probe(new RecordingHandler(Ok("source_mismatch", observed: "198.51.100.42")));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.SourceMismatch, result.Outcome);
        Assert.Equal("198.51.100.42", result.ObservedAddress);
    }

    // ── Nothing is trusted on arrival ──

    [Fact]
    public async Task An_answer_to_a_different_request_is_refused()
    {
        using var probe = Probe(new RecordingHandler(Ok("reachable", requestId: "ffffffffffffffffffffffffffffffff")));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse, result.Outcome);
        Assert.Contains("different request", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_about_a_different_port_is_refused()
    {
        using var probe = Probe(new RecordingHandler(Ok("reachable", port: 25567)));

        Assert.Equal(ExternalProbeOutcome.MalformedResponse,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_success_for_another_address_is_refused_rather_than_believed()
    {
        using var probe = Probe(new RecordingHandler(Ok("reachable", observed: "198.51.100.42")));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse, result.Outcome);
        Assert.Contains("other than the one that was checked", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The contract fixes one status per result. A disagreement is something no conforming service can
    /// produce, so it is refused rather than reconciled in the client's favour.
    /// </summary>
    [Theory]
    [InlineData("reachable", HttpStatusCode.TooManyRequests)]
    [InlineData("reachable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("reachable", HttpStatusCode.Accepted)]
    [InlineData("unreachable", HttpStatusCode.ServiceUnavailable)]
    [InlineData("source_mismatch", HttpStatusCode.BadRequest)]
    [InlineData("rate_limited", HttpStatusCode.OK)]
    [InlineData("probe_error", HttpStatusCode.OK)]
    [InlineData("invalid_request", HttpStatusCode.OK)]
    public async Task A_status_the_contract_does_not_pair_with_the_result_is_refused(
        string result, HttpStatusCode status)
    {
        using var probe = Probe(new RecordingHandler(Body(result, status: status, connectMilliseconds: 118)));

        var outcome = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse, outcome.Outcome);
        Assert.Contains("status", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reachable is the only answer that lets ChunkPilot claim anything, so every field the contract
    /// fixes for it has to be exactly right before the claim is allowed.
    /// </summary>
    [Fact]
    public async Task A_success_over_a_non_ipv4_family_is_refused()
    {
        using var probe = Probe(new RecordingHandler(
            Body("reachable", observedFamily: "ipv6", connectMilliseconds: 118)));

        Assert.Equal(ExternalProbeOutcome.MalformedResponse,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_success_without_a_usable_connection_time_is_refused()
    {
        foreach (var elapsed in new[] { "null", "-1", "-0.5", "1e400", "999999" })
        {
            var body = $$"""
                {"apiVersion":1,"requestId":"{{RequestId}}","result":"reachable","observedAddress":"{{Public}}",
                 "observedFamily":"ipv4","port":25566,"checkedAt":"2026-08-08T19:42:00.000Z",
                 "connectMilliseconds":{{elapsed}}}
                """;
            using var probe = Probe(new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }));

            var outcome = await probe.ProbeAsync(Request(), CancellationToken.None);
            Assert.Equal(ExternalProbeOutcome.MalformedResponse, outcome.Outcome);
        }
    }

    [Fact]
    public async Task An_instant_handshake_is_a_measurement_and_is_accepted()
    {
        using var probe = Probe(new RecordingHandler(Body("reachable", connectMilliseconds: 0)));

        var outcome = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.Reachable, outcome.Outcome);
        Assert.Equal(0, outcome.ConnectMilliseconds);
    }

    /// <summary>
    /// A private, shared or reserved address cannot be a public endpoint, whatever a service claims —
    /// and the expected address was already validated as routable before the request was sent.
    /// </summary>
    [Fact]
    public async Task A_success_for_an_address_that_is_not_publicly_routable_is_refused()
    {
        foreach (var address in new[] { "10.0.0.140", "192.168.1.50", "100.64.0.1", "127.0.0.1" })
        {
            using var probe = Probe(new RecordingHandler(
                Body("reachable", observed: address, connectMilliseconds: 118)));
            var request = new ExternalProbeRequest
            {
                RequestId = RequestId,
                ExpectedAddress = address,
                Port = 25566
            };

            Assert.Equal(ExternalProbeOutcome.MalformedResponse,
                (await probe.ProbeAsync(request, CancellationToken.None)).Outcome);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public async Task An_answer_in_another_contract_version_is_refused(int version)
    {
        using var probe = Probe(new RecordingHandler(Ok("reachable", apiVersion: version)));

        Assert.Equal(ExternalProbeOutcome.MalformedResponse,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task A_redirect_is_refused_and_never_followed()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            Headers = { Location = new Uri("https://elsewhere.example.com/v1/probe") }
        });
        using var probe = Probe(handler);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse, result.Outcome);
        Assert.Contains("redirect", result.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_non_json_answer_is_refused()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>service down</html>", Encoding.UTF8, "text/html")
        });
        using var probe = Probe(handler);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Unreadable_or_empty_json_is_refused()
    {
        foreach (var body in new[] { "", "{", "not json", "null" })
        {
            var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
            using var probe = Probe(handler);

            Assert.Equal(ExternalProbeOutcome.MalformedResponse,
                (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
        }
    }

    [Fact]
    public async Task An_oversized_answer_is_refused_rather_than_buffered()
    {
        var padded = "{\"apiVersion\":1,\"requestId\":\"" + RequestId + "\",\"result\":\"reachable\",\"padding\":\"" +
                     new string('x', 20_000) + "\"}";
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(padded, Encoding.UTF8, "application/json")
        });
        using var probe = Probe(handler);

        Assert.Equal(ExternalProbeOutcome.MalformedResponse,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    /// <summary>
    /// The service's own words never reach the interface. Only values the client validated appear in
    /// the evidence line.
    /// </summary>
    [Fact]
    public async Task Nothing_the_service_invents_is_reflected_into_the_detail()
    {
        var body = $$"""
            {"apiVersion":1,"requestId":"{{RequestId}}","result":"invalid_request","observedAddress":"",
             "observedFamily":"unknown","port":0,"checkedAt":"2026-08-08T19:42:00.000Z",
             "reason":"<script>alert(1)</script>"}
            """;
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var probe = Probe(handler);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.InvalidRequest, result.Outcome);
        Assert.DoesNotContain("script", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The observed address is shown to the user, so free text from the service must never reach the
    /// interface as if it were an address.
    /// </summary>
    [Fact]
    public async Task An_observed_address_that_is_not_an_address_is_discarded()
    {
        foreach (var invented in new[] { "victim.example.com", "call your bank", "<b>203.0.113.7</b>", "" })
        {
            using var probe = Probe(new RecordingHandler(Ok("source_mismatch", observed: invented)));

            var result = await probe.ProbeAsync(Request(), CancellationToken.None);

            Assert.Equal(ExternalProbeOutcome.SourceMismatch, result.Outcome);
            Assert.Equal("", result.ObservedAddress);
        }
    }

    [Fact]
    public async Task An_address_family_the_contract_does_not_define_is_discarded()
    {
        var body = $$"""
            {"apiVersion":1,"requestId":"{{RequestId}}","result":"unsupported_address_family",
             "observedAddress":"","observedFamily":"martian","port":25566,
             "checkedAt":"2026-08-08T19:42:00.000Z","connectMilliseconds":0}
            """;
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var probe = Probe(handler);

        Assert.Equal("", (await probe.ProbeAsync(Request(), CancellationToken.None)).ObservedFamily);
    }

    [Fact]
    public async Task A_well_formed_reason_code_is_kept_as_evidence()
    {
        var body = $$"""
            {"apiVersion":1,"requestId":"{{RequestId}}","result":"invalid_request","observedAddress":"",
             "observedFamily":"unknown","port":0,"checkedAt":"2026-08-08T19:42:00.000Z","reason":"expected_address"}
            """;
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        using var probe = Probe(handler);

        Assert.Contains("expected_address",
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Detail, StringComparison.Ordinal);
    }

    // ── Transport failures are never network verdicts ──

    [Fact]
    public async Task A_service_that_cannot_be_reached_is_never_an_unreachable_server()
    {
        using var probe = Probe(new RecordingHandler(() => throw new HttpRequestException("no route")));

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(ExternalProbeOutcome.ServiceUnavailable, result.Outcome);
        Assert.DoesNotContain("no route", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_timeout_is_reported_as_a_timeout()
    {
        using var probe = Probe(new RecordingHandler(() => throw new TaskCanceledException("timed out")));

        Assert.Equal(ExternalProbeOutcome.Timeout,
            (await probe.ProbeAsync(Request(), CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Cancellation_is_told_apart_from_a_timeout()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RecordingHandler(() =>
        {
            cancellation.Cancel();
            throw new TaskCanceledException("cancelled");
        });
        using var probe = Probe(handler);

        Assert.Equal(ExternalProbeOutcome.Cancelled,
            (await probe.ProbeAsync(Request(), cancellation.Token)).Outcome);
    }

    private static HttpExternalReachabilityProbe Probe(RecordingHandler handler) =>
        new(ExternalReachabilityProbeOptions.Configure(Endpoint), new HttpClient(handler));

    private static ExternalProbeRequest Request() => new()
    {
        RequestId = RequestId,
        ExpectedAddress = Public,
        Port = 25566
    };

    /// <summary>A conforming answer: the status the contract pairs with the result it carries.</summary>
    private static HttpResponseMessage Ok(
        string result,
        string? requestId = null,
        string? observed = null,
        int port = 25566,
        int apiVersion = 1,
        int connectMilliseconds = 0) =>
        Body(result, requestId, observed, port, apiVersion, connectMilliseconds);

    private static HttpResponseMessage Body(
        string result,
        string? requestId = null,
        string? observed = null,
        int port = 25566,
        int apiVersion = 1,
        int connectMilliseconds = 0,
        string observedFamily = "ipv4",
        HttpStatusCode? status = null)
    {
        var address = observed ?? (result is "reachable" or "unreachable" ? Public : "");
        var body = $$"""
            {"apiVersion":{{apiVersion}},"requestId":"{{requestId ?? RequestId}}","result":"{{result}}",
             "observedAddress":"{{address}}","observedFamily":"{{observedFamily}}","port":{{port}},
             "checkedAt":"2026-08-08T19:42:00.000Z","connectMilliseconds":{{connectMilliseconds}}}
            """;
        return new HttpResponseMessage(status ?? ContractStatus(result))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static HttpStatusCode ContractStatus(string result) => result switch
    {
        "invalid_request" => HttpStatusCode.BadRequest,
        "rate_limited" => HttpStatusCode.TooManyRequests,
        "probe_error" => HttpStatusCode.ServiceUnavailable,
        _ => HttpStatusCode.OK
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> factory;

        public RecordingHandler(HttpResponseMessage response) => factory = () => response;
        public RecordingHandler(Func<HttpResponseMessage> factory) => this.factory = factory;

        public int Calls { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? LastBody { get; private set; }
        public HttpRequestHeaders? LastHeaders { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastMethod = request.Method;
            LastUri = request.RequestUri;
            LastHeaders = request.Headers;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return factory();
        }
    }
}
