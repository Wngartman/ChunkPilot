using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Talks to the optional external probe service over HTTPS and turns its answer into a typed result.
/// </summary>
/// <remarks>
/// <para>
/// This is the only class in ChunkPilot that knows the probe service's JSON. Everything above it sees
/// <see cref="ExternalProbeResult"/>, so the service could be replaced without the Agent, the App or
/// any state machine changing.
/// </para>
/// <para>
/// Nothing is trusted on arrival. The contract version, the correlation id and the port must all
/// match the request that was sent, redirects are never followed, the body is read under a cap, and
/// an answer that fails any of those checks is <see cref="ExternalProbeOutcome.MalformedResponse"/>
/// rather than a reachability verdict. A service that is confused must not be able to make ChunkPilot
/// claim public access.
/// </para>
/// <para>
/// The request also has to leave over the address family that is being verified. The service can only
/// compare what it observes with what the router reported, so an IPv4 mapping checked from an IPv6
/// request is compared against an address that was never in question and concludes nothing. See
/// <see cref="ExternalProbeTransport"/>.
/// </para>
/// </remarks>
public sealed class HttpExternalReachabilityProbe : IExternalReachabilityProbe, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ExternalReachabilityProbeOptions options;
    private readonly HttpClient http;
    private readonly bool ownsClient;

    /// <summary>
    /// <paramref name="httpClient"/> replaces the whole transport and exists for tests that never open
    /// a socket. <paramref name="transport"/> replaces only how the real transport resolves and
    /// connects, so the handler, the TLS and the request are the production ones.
    /// </summary>
    public HttpExternalReachabilityProbe(
        ExternalReachabilityProbeOptions options,
        HttpClient? httpClient = null,
        ExternalProbeTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
        ownsClient = httpClient is null;
        http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            // A probe answer is never at another address. Following a redirect would hand the request,
            // and the correlation id in it, to a host that was never configured.
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // The connection is opened over the family of the endpoint being verified, and over nothing
            // else. Only the socket changes: the URI, the TLS host, the authority and the proxy setting
            // are untouched, so a proxied or tunnelled path behaves exactly as it did — and when one is
            // in the way, the service's own source comparison is still what refuses to conclude.
            ConnectCallback = (transport ?? new ExternalProbeTransport()).ConnectAsync
        });
        http.Timeout = options.Timeout;
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3.0");
    }

    public bool IsConfigured => options.IsConfigured;

    public string ConfigurationDetail => options.Detail;

    public async Task<ExternalProbeResult> ProbeAsync(
        ExternalProbeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (options.ProbeUrl is not { } endpoint)
            return ExternalProbeResult.Failed(ExternalProbeOutcome.NotConfigured, request.RequestId, options.Detail);

        // What is being verified decides how the request travels. This path covers IPv4 mappings, so
        // anything else is reported as unsupported rather than sent over whichever family the transport
        // would have preferred and then compared against an address nobody asked about.
        if (!IPAddress.TryParse(request.ExpectedAddress, out var expected) ||
            expected.AddressFamily != AddressFamily.InterNetwork)
            return ExternalProbeResult.Failed(ExternalProbeOutcome.UnsupportedAddressFamily, request.RequestId,
                "This check covers IPv4 endpoints, and the address to verify is not an IPv4 address.");

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(
                new ProbeRequestPayload(ExternalReachabilityPolicy.ApiVersion, request.RequestId,
                    request.ExpectedAddress, request.Port),
                options: Json)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        // Read by the transport when it opens the connection. It travels no further than this process.
        message.Options.Set(ExternalProbeTransport.RequiredFamily, expected.AddressFamily);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExternalProbeResult.Failed(ExternalProbeOutcome.Cancelled, request.RequestId,
                "The external check was cancelled before the service answered.");
        }
        catch (TaskCanceledException)
        {
            return ExternalProbeResult.Failed(ExternalProbeOutcome.Timeout, request.RequestId,
                "The external probe service did not answer within the time limit.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return ExternalProbeResult.Failed(ExternalProbeOutcome.ServiceUnavailable, request.RequestId,
                DescribeTransportFailure(exception));
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    "The external probe service answered with a redirect, which is never followed.");

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    $"The external probe service answered with {Describe(response.StatusCode)} and a non-JSON body.");

            ProbeResponsePayload? payload;
            try
            {
                payload = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ExternalProbeResult.Failed(ExternalProbeOutcome.Cancelled, request.RequestId,
                    "The external check was cancelled while the answer was being read.");
            }
            catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException
                                                  or InvalidDataException or TaskCanceledException)
            {
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    $"The external probe service answered with {Describe(response.StatusCode)} and an unreadable body.");
            }

            return payload is null
                ? ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    "The external probe service answered with an empty body.")
                : Validate(request, response.StatusCode, payload);
        }
    }

    /// <summary>
    /// Every field is checked against the request before the answer is allowed to mean anything.
    /// </summary>
    private static ExternalProbeResult Validate(
        ExternalProbeRequest request, HttpStatusCode status, ProbeResponsePayload payload)
    {
        if (payload.ApiVersion != ExternalReachabilityPolicy.ApiVersion)
            return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                $"The external probe service answered with contract version {payload.ApiVersion.ToString(CultureInfo.InvariantCulture)}, " +
                $"which this build does not speak.");

        var outcome = MapResult(payload.Result);
        if (outcome is null)
            return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                "The external probe service answered with a result this build does not recognise.");

        // The correlation id proves the answer belongs to this exact check, and nothing else does.
        if (!string.Equals(payload.RequestId, request.RequestId, StringComparison.Ordinal))
            return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                "The external probe service answered a different request than the one that was sent.");

        // The contract fixes one status per result, so a disagreement between the two is an answer no
        // conforming service can produce and is refused rather than reconciled.
        if (status != ExpectedStatusFor(outcome.Value))
            return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                $"The external probe service answered {Describe(status)} for a result the contract does not " +
                "carry that status.");

        // A verdict about the network has to be about the port that was asked about.
        var portMatters = outcome is ExternalProbeOutcome.Reachable or ExternalProbeOutcome.Unreachable or
            ExternalProbeOutcome.SourceMismatch or ExternalProbeOutcome.UnsupportedAddressFamily;
        if (portMatters && payload.Port != request.Port)
            return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                "The external probe service answered about a different port than the one that was sent.");

        // Reachable is the only result that lets ChunkPilot claim anything, so every field the contract
        // fixes for it is required to be exactly right. None of this authenticates that a handshake
        // happened — only the service can know that — but a service confused enough to contradict its
        // own contract must not be able to make ChunkPilot claim public access.
        if (outcome == ExternalProbeOutcome.Reachable)
        {
            if (!string.Equals(payload.ObservedAddress, request.ExpectedAddress, StringComparison.Ordinal))
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    "The external probe service reported success for an address other than the one that was checked.");
            if (!IsGloballyRoutableIpv4(payload.ObservedAddress))
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    "The external probe service reported success for an address that is not a public IPv4 address.");
            if (!string.Equals(payload.ObservedFamily, "ipv4", StringComparison.Ordinal))
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    "The external probe service reported success over an address family this path does not cover.");
            if (payload.ConnectMilliseconds is not { } elapsed || !double.IsFinite(elapsed) ||
                elapsed < 0 || elapsed > MaximumConnectMilliseconds)
                return ExternalProbeResult.Failed(ExternalProbeOutcome.MalformedResponse, request.RequestId,
                    "The external probe service reported success without a usable connection time.");
        }

        return new ExternalProbeResult
        {
            Outcome = outcome.Value,
            RequestId = payload.RequestId,
            // The observed address is shown to the user, so only something that actually parses as an
            // address is kept. A service that answers with free text contributes nothing rather than
            // putting its own words on screen.
            ObservedAddress = SanitizeAddress(payload.ObservedAddress),
            ObservedFamily = payload.ObservedFamily switch
            {
                "ipv4" or "ipv6" or "unknown" => payload.ObservedFamily,
                _ => ""
            },
            Port = payload.Port,
            ConnectMilliseconds = payload.ConnectMilliseconds is { } measured && double.IsFinite(measured) &&
                                  measured is >= 0 and <= MaximumConnectMilliseconds
                ? (int)measured
                : 0,
            CheckedAt = ParseTimestamp(payload.CheckedAt),
            Detail = Describe(status, payload)
        };
    }

    /// <summary>
    /// A handshake the service is willing to have taken this long. Comfortably above its own bounded
    /// connect budget, and low enough that a nonsensical figure cannot be presented as a measurement.
    /// </summary>
    private const double MaximumConnectMilliseconds = 60_000;

    /// <summary>The one HTTP status the contract defines for each result.</summary>
    private static HttpStatusCode ExpectedStatusFor(ExternalProbeOutcome outcome) => outcome switch
    {
        ExternalProbeOutcome.InvalidRequest => HttpStatusCode.BadRequest,
        ExternalProbeOutcome.RateLimited => HttpStatusCode.TooManyRequests,
        ExternalProbeOutcome.ProbeError => HttpStatusCode.ServiceUnavailable,
        _ => HttpStatusCode.OK
    };

    private static bool IsGloballyRoutableIpv4(string? address) =>
        address is not null &&
        RouterMappingPolicy.ClassifyExternalAddress(address).Class == RoutableAddressClass.GloballyRoutable;

    /// <summary>
    /// The provider's own vocabulary, mapped once. Unknown values are refused rather than guessed at,
    /// so a future service result can never be silently read as a weaker or stronger one.
    /// </summary>
    private static ExternalProbeOutcome? MapResult(string? result) => result switch
    {
        "reachable" => ExternalProbeOutcome.Reachable,
        "unreachable" => ExternalProbeOutcome.Unreachable,
        "source_mismatch" => ExternalProbeOutcome.SourceMismatch,
        "unsupported_address_family" => ExternalProbeOutcome.UnsupportedAddressFamily,
        "invalid_request" => ExternalProbeOutcome.InvalidRequest,
        "rate_limited" => ExternalProbeOutcome.RateLimited,
        "probe_error" => ExternalProbeOutcome.ProbeError,
        _ => null
    };

    private static string SanitizeAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 45 && IPAddress.TryParse(value, out var parsed)
            ? parsed.ToString()
            : "";

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string Describe(HttpStatusCode status) =>
        ((int)status).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Why the transport failed, when this assembly is the one that knows. The handler wraps whatever
    /// the connection attempt threw, so the chain is walked for the one exception type ChunkPilot
    /// writes the message of; anything else contributes the same neutral sentence as before, because a
    /// platform exception's own words are never shown.
    /// </summary>
    private static string DescribeTransportFailure(Exception exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
            if (current is ExternalProbeTransportException transport)
                return transport.Message;
        return "The external probe service could not be reached.";
    }

    /// <summary>
    /// Bounded, non-reflective evidence. Only values this client already validated appear, so nothing
    /// the service invents can be echoed into the interface or onto the clipboard.
    /// </summary>
    private static string Describe(HttpStatusCode status, ProbeResponsePayload payload)
    {
        var reason = payload.Reason is { Length: > 0 and <= 32 } text &&
                     text.All(character => char.IsAsciiLetterLower(character) || character == '_')
            ? $"; reason {text}"
            : "";
        return $"External probe service answered {Describe(status)} with result {MapResult(payload.Result)}{reason}.";
    }

    private async Task<ProbeResponsePayload?> ReadBoundedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > options.MaximumResponseBytes)
            throw new InvalidDataException("The external probe answer exceeded the size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[options.MaximumResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        if (total > options.MaximumResponseBytes)
            throw new InvalidDataException("The external probe answer exceeded the size limit.");
        return total == 0
            ? null
            : JsonSerializer.Deserialize<ProbeResponsePayload>(buffer.AsSpan(0, total), Json);
    }

    public void Dispose()
    {
        if (ownsClient)
            http.Dispose();
    }

    private sealed record ProbeRequestPayload(
        int ApiVersion, string RequestId, string ExpectedAddress, int Port);

    private sealed record ProbeResponsePayload
    {
        public int ApiVersion { get; init; }
        public string RequestId { get; init; } = "";
        public string? Result { get; init; }
        public string? ObservedAddress { get; init; }
        public string? ObservedFamily { get; init; }
        public int Port { get; init; }
        public string? CheckedAt { get; init; }
        public double? ConnectMilliseconds { get; init; }
        public string? Reason { get; init; }
    }
}
