using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public enum CurseForgeFailureKind
{
    Authentication,
    NotFound,
    RateLimited,
    Server,
    Offline,
    Timeout,
    Redirect,
    WrongContentType,
    OversizedResponse,
    MalformedResponse,
    UnapprovedHost
}

public sealed class CurseForgeApiException : HttpRequestException
{
    public CurseForgeApiException(
        CurseForgeFailureKind kind,
        string message,
        HttpStatusCode? statusCode = null,
        Exception? innerException = null)
        : base(message, innerException, statusCode)
    {
        Kind = kind;
    }

    public CurseForgeFailureKind Kind { get; }
}

/// <summary>
/// The sole native HTTP and credential boundary for CurseForge. It deliberately retains no
/// completed API response cache: current third-party terms require ChunkPilot to obtain separate
/// written permission before storing CurseForge API data. Identical in-flight reads are still
/// coalesced so one deliberate UI action cannot create a request storm.
/// </summary>
public sealed class CurseForgeApiClient : IDisposable
{
    public const string ApiHost = "api.curseforge.com";
    public const int MaximumJsonBytes = 8 * 1024 * 1024;
    internal const int MaximumDownloadRedirects = 5;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    // Broker downloads re-resolve exact file/project permission and optionally the URL before CDN
    // headers. Four individually bounded upstream steps fit here; direct requests retain 20 s.
    public static readonly TimeSpan ServiceDownloadHeaderTimeout = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(5);

    private readonly ISecretStore secrets;
    private readonly HttpClient http;
    private readonly Uri? serviceEndpoint;
    private sealed record DownloadIdentity(Uri Source, Uri Transport);
    private readonly ConditionalWeakTable<HttpResponseMessage, DownloadIdentity> downloadResponses = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> inFlight =
        new(StringComparer.Ordinal);

    public CurseForgeApiClient(ISecretStore secrets)
        : this(secrets, CreateProductionHandler(), CurseForgeServiceConfiguration.Endpoint)
    {
    }

    /// <summary>
    /// Test-only transport seam. Production callers cannot supply an HttpClient whose redirect
    /// policy is unknown; the public constructor always owns a no-redirect SocketsHttpHandler.
    /// </summary>
    internal CurseForgeApiClient(ISecretStore secrets, HttpMessageHandler fixtureTransport)
        : this(secrets, fixtureTransport, null)
    {
    }

    internal CurseForgeApiClient(ISecretStore secrets, HttpMessageHandler fixtureTransport, Uri? serviceEndpoint)
    {
        this.secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        ArgumentNullException.ThrowIfNull(fixtureTransport);
        this.serviceEndpoint = CurseForgeServiceConfiguration.ValidateEndpoint(serviceEndpoint?.OriginalString);
        http = new HttpClient(fixtureTransport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ChunkPilot/1.3.0 (local Windows Minecraft server manager; CurseForge client)");
        if (http.DefaultRequestHeaders.Accept.Count == 0)
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool HasCredential => secrets.Contains(CurseForgeUpdateProvider.ApiKeyName);
    public bool CanAccess => serviceEndpoint is not null || HasCredential;
    public CurseForgeAccessMode AccessMode => serviceEndpoint is not null
        ? CurseForgeAccessMode.ApplicationService
        : HasCredential ? CurseForgeAccessMode.PersonalKey : CurseForgeAccessMode.Unavailable;
    public string AccessDetail => AccessMode switch
    {
        CurseForgeAccessMode.ApplicationService => "CurseForge application access is configured. The application API key stays on the remote service, never on this PC. Availability depends on the service and CurseForge.",
        CurseForgeAccessMode.PersonalKey => "A personal CurseForge API key is protected for this Windows account. Live provider availability has not been checked.",
        _ => "CurseForge application access is not configured in this build. An approved personal API key can be configured in native setup."
    };

    internal int InFlightRequestCount => inFlight.Count;

    internal async Task ValidateCredentialAsync(
        string candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        var uri = ValidateApiUri("/v1/games/432");
        var bytes = await GetJsonBytesAsync(uri, candidate, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("id", out var id) ||
            !id.TryGetInt64(out var gameId) || gameId != 432)
            throw new CurseForgeApiException(CurseForgeFailureKind.MalformedResponse,
                "CurseForge authentication returned an unexpected Minecraft identity.");
    }

    private static SocketsHttpHandler CreateProductionHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    public async Task<JsonDocument> GetJsonAsync(
        string relativePathAndQuery,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = ValidateApiUri(relativePathAndQuery);
        var key = uri.PathAndQuery;
        var lazy = inFlight.GetOrAdd(key, _ => CreateInFlightRequest(key, uri));
        try
        {
            var bytes = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException exception)
        {
            throw new CurseForgeApiException(CurseForgeFailureKind.MalformedResponse,
                "CurseForge returned malformed JSON.", innerException: exception);
        }
    }

    private Lazy<Task<byte[]>> CreateInFlightRequest(string key, Uri uri)
    {
        Lazy<Task<byte[]>>? request = null;
        request = new Lazy<Task<byte[]>>(async () =>
        {
            try
            {
                return await GetJsonBytesAsync(uri).ConfigureAwait(false);
            }
            finally
            {
                inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, request!));
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        return request;
    }

    public async Task<HttpResponseMessage> SendDownloadAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApprovedDownloadUri(uri))
            throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                "CurseForge returned an unapproved download destination.");
        if (serviceEndpoint is not null)
            return await SendServiceDownloadAsync(uri, cancellationToken).ConfigureAwait(false);
        var credential = RequireCredential();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var current = uri;
            var redirectsFollowed = 0;
            while (true)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.TryAddWithoutValidation("x-api-key", credential);
                var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var final = response.RequestMessage?.RequestUri ?? request.RequestUri;
                if (final is null || !IsApprovedDownloadUri(final))
                {
                    response.Dispose();
                    throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                        "The CurseForge download left the approved CDN boundary.");
                }
                if (IsFollowableDownloadRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    var status = response.StatusCode;
                    response.Dispose();
                    if (location is null)
                        throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                            "A CurseForge download redirect did not include a destination.", status);
                    if (redirectsFollowed >= MaximumDownloadRedirects)
                        throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                            $"A CurseForge download exceeded {MaximumDownloadRedirects} redirects.", status);

                    Uri destination;
                    try
                    {
                        destination = location.IsAbsoluteUri ? location : new Uri(current, location);
                    }
                    catch (UriFormatException exception)
                    {
                        throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                            "A CurseForge download redirect contained an invalid destination.", status, exception);
                    }
                    if (!IsApprovedDownloadUri(destination))
                        throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                            "A CurseForge download redirect left the approved CDN boundary.", status);

                    current = destination;
                    redirectsFollowed++;
                    continue;
                }
                if (IsRedirectionStatus(response.StatusCode))
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                        "CurseForge returned an unsupported download redirect status.", status);
                }
                if (!response.IsSuccessStatusCode)
                {
                    var error = MapStatus(response.StatusCode);
                    response.Dispose();
                    throw error;
                }
                response.RequestMessage ??= request;
                downloadResponses.Add(response, new DownloadIdentity(uri, final));
                return response;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CurseForgeApiException(CurseForgeFailureKind.Timeout,
                "The CurseForge download request timed out.", innerException: exception);
        }
        catch (HttpRequestException exception) when (exception is not CurseForgeApiException)
        {
            throw new CurseForgeApiException(CurseForgeFailureKind.Offline,
                "CurseForge could not be reached.", exception.StatusCode, exception);
        }
    }

    /// <summary>
    /// Proves that this client emitted the response for the selected official source. The response
    /// retains its real transport URI, which can be the application service rather than the CDN.
    /// </summary>
    public bool IsApprovedDownloadResponse(HttpResponseMessage response, Uri officialSource) =>
        response.IsSuccessStatusCode && IsApprovedDownloadUri(officialSource) &&
        downloadResponses.TryGetValue(response, out var identity) && identity.Source == officialSource &&
        response.RequestMessage?.RequestUri == identity.Transport;

    internal Uri CreateServiceDownloadUri(Uri source)
    {
        if (serviceEndpoint is null)
            throw new InvalidOperationException("CurseForge application access is not configured.");
        if (!TryGetStandardDownloadFileId(source, out var fileId))
            throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                "The CurseForge file URL does not identify a supported exact CDN file.");
        // Protocol v1 uses a single decoded path so .NET and WHATWG URL implementations agree
        // for escaped unreserved characters, spaces, and Unicode. Validation above rejects paths
        // whose decoded filename contains a separator/control; never decode a second time.
        var canonicalSource = "https://" + source.IdnHost.ToLowerInvariant() + Uri.UnescapeDataString(source.AbsolutePath);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSource))).ToLowerInvariant();
        return new Uri(serviceEndpoint,
            $"downloads/{fileId.ToString(CultureInfo.InvariantCulture)}?sourceSha256={digest}");
    }

    internal static bool TryGetStandardDownloadFileId(Uri source, out int fileId)
    {
        fileId = 0;
        if (!IsApprovedDownloadUri(source) || source.Query.Length != 0 || source.Fragment.Length != 0)
            return false;
        var parts = source.AbsolutePath.Split('/');
        if (parts.Length != 5 || parts[0].Length != 0 || parts[1] != "files" ||
            parts[2].Length is < 1 or > 7 || parts[3].Length is < 1 or > 3 || parts[4].Length is < 1 or > 1024 ||
            !parts[2].All(char.IsAsciiDigit) || !parts[3].All(char.IsAsciiDigit) ||
            (parts[2].Length > 1 && parts[2][0] == '0'))
            return false;
        var fileName = Uri.UnescapeDataString(parts[4]);
        if (fileName is "." or ".." || fileName.Any(character => character is '/' or '\\' || char.IsControl(character)))
            return false;
        return int.TryParse(parts[2] + parts[3].PadLeft(3, '0'), NumberStyles.None,
            CultureInfo.InvariantCulture, out fileId) && fileId > 0;
    }

    private async Task<HttpResponseMessage> SendServiceDownloadAsync(Uri source, CancellationToken cancellationToken)
    {
        var transportUri = CreateServiceDownloadUri(source);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ServiceDownloadHeaderTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, transportUri);
        // Deliberately no credential lookup and no x-api-key header, even with a personal key saved.
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            try
            {
                if (IsRedirectionStatus(response.StatusCode))
                    throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                        "CurseForge application-service redirects are not followed.", response.StatusCode);
                var final = response.RequestMessage?.RequestUri ?? request.RequestUri;
                if (final != transportUri)
                    throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                        "The CurseForge download left its configured application-service route.");
                if (!response.IsSuccessStatusCode) throw MapStatus(response.StatusCode, applicationService: true);
                response.RequestMessage ??= request;
                downloadResponses.Add(response, new DownloadIdentity(source, transportUri));
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new CurseForgeApiException(CurseForgeFailureKind.Timeout,
                "The CurseForge application-service download request timed out.", innerException: exception);
        }
        catch (HttpRequestException exception) when (exception is not CurseForgeApiException)
        {
            throw new CurseForgeApiException(CurseForgeFailureKind.Offline,
                "The CurseForge application service could not be reached.", exception.StatusCode, exception);
        }
    }

    public static bool IsApprovedDownloadUri(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        return host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
    }

    private Task<byte[]> GetJsonBytesAsync(Uri uri) =>
        serviceEndpoint is null
            ? GetJsonBytesAsync(uri, RequireCredential(), CancellationToken.None)
            : GetJsonBytesAsync(new Uri(serviceEndpoint, uri.PathAndQuery.TrimStart('/')), null, CancellationToken.None);

    private async Task<byte[]> GetJsonBytesAsync(
        Uri uri,
        string? credential,
        CancellationToken callerToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (credential is not null)
                request.Headers.TryAddWithoutValidation("x-api-key", credential);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (IsRedirectionStatus(response.StatusCode))
                    throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                        "CurseForge API redirects are not followed automatically.", response.StatusCode);
                // The owned production handler always supplies RequestMessage and cannot redirect.
                // Minimal fixture handlers may omit it, so the already-validated original URI is
                // the only safe fallback available through the internal test seam.
                var final = response.RequestMessage?.RequestUri ?? request.RequestUri;
                if (final is null || (credential is not null ? !IsApprovedApiUri(final) : final != uri))
                    throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                        "The CurseForge API response left the approved host boundary.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0 &&
                    RetryDelay(response.Headers.RetryAfter) is { } delay)
                {
                    await Task.Delay(delay, callerToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw MapStatus(response.StatusCode, applicationService: credential is null);
                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null ||
                    !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
                      mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                    throw new CurseForgeApiException(CurseForgeFailureKind.WrongContentType,
                        "CurseForge returned an unexpected response content type.");
                if (response.Content.Headers.ContentLength is > MaximumJsonBytes)
                    throw new CurseForgeApiException(CurseForgeFailureKind.OversizedResponse,
                        "The CurseForge response exceeded ChunkPilot's bounded response limit.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[32 * 1024];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (output.Length + read > MaximumJsonBytes)
                        throw new CurseForgeApiException(CurseForgeFailureKind.OversizedResponse,
                            "The CurseForge response exceeded ChunkPilot's bounded response limit.");
                    output.Write(buffer, 0, read);
                }
                var bytes = output.ToArray();
                try
                {
                    using var validation = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
                }
                catch (JsonException exception)
                {
                    throw new CurseForgeApiException(CurseForgeFailureKind.MalformedResponse,
                        "CurseForge returned malformed JSON.", innerException: exception);
                }
                return bytes;
            }
            catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                throw new CurseForgeApiException(CurseForgeFailureKind.Timeout,
                    "The CurseForge API request timed out.", innerException: exception);
            }
            catch (HttpRequestException exception) when (exception is not CurseForgeApiException)
            {
                throw new CurseForgeApiException(CurseForgeFailureKind.Offline,
                    "CurseForge could not be reached.", exception.StatusCode, exception);
            }
        }
        throw new CurseForgeApiException(CurseForgeFailureKind.RateLimited,
            "The CurseForge rate limit remains active.", HttpStatusCode.TooManyRequests);
    }

    private string RequireCredential()
    {
        var credential = secrets.GetSecret(CurseForgeUpdateProvider.ApiKeyName);
        if (string.IsNullOrWhiteSpace(credential))
            throw new InvalidOperationException(
                "CurseForge is unavailable until the approved native credential is provisioned.");
        return credential;
    }

    private static Uri ValidateApiUri(string relativePathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(relativePathAndQuery) ||
            !relativePathAndQuery.StartsWith("/v1/", StringComparison.Ordinal) ||
            !Uri.TryCreate($"https://{ApiHost}{relativePathAndQuery}", UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !uri.IdnHost.Equals(ApiHost, StringComparison.OrdinalIgnoreCase))
            throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                "The CurseForge API request target is not approved.");
        return uri;
    }

    private static bool IsApprovedApiUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort &&
        uri.IdnHost.TrimEnd('.').Equals(ApiHost, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null) return null;
        var delay = retryAfter.Delta ??
                    (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        return delay <= MaximumRetryAfter ? delay : null;
    }

    private static bool IsRedirectionStatus(HttpStatusCode status) => (int)status is >= 300 and <= 399;

    private static bool IsFollowableDownloadRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static CurseForgeApiException MapStatus(HttpStatusCode status, bool applicationService = false) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new CurseForgeApiException(CurseForgeFailureKind.Authentication,
                applicationService
                    ? "CurseForge application access did not authorize this request. The service may be unavailable or this content may be restricted."
                    : "CurseForge rejected the configured credential.", status),
        HttpStatusCode.NotFound =>
            new CurseForgeApiException(CurseForgeFailureKind.NotFound,
                "The CurseForge project or file is unavailable.", status),
        HttpStatusCode.TooManyRequests =>
            new CurseForgeApiException(CurseForgeFailureKind.RateLimited,
                "The CurseForge rate limit is active.", status),
        >= HttpStatusCode.InternalServerError =>
            new CurseForgeApiException(CurseForgeFailureKind.Server,
                "CurseForge returned a server error.", status),
        _ => new CurseForgeApiException(CurseForgeFailureKind.Offline,
            "The CurseForge request failed.", status)
    };

    public void Dispose()
    {
        inFlight.Clear();
        http.Dispose();
    }
}
