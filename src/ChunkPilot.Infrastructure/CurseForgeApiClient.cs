using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
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
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan MaximumRetryAfter = TimeSpan.FromSeconds(5);

    private readonly ISecretStore secrets;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> inFlight =
        new(StringComparer.Ordinal);

    public CurseForgeApiClient(ISecretStore secrets, HttpClient? httpClient = null)
    {
        this.secrets = secrets;
        if (httpClient is null)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            ownsHttp = true;
        }
        else
        {
            http = httpClient;
        }

        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "ChunkPilot/1.3.0 (local Windows Minecraft server manager; CurseForge client)");
        if (http.DefaultRequestHeaders.Accept.Count == 0)
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool HasCredential => secrets.Contains(CurseForgeUpdateProvider.ApiKeyName);

    public async Task<JsonDocument> GetJsonAsync(
        string relativePathAndQuery,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateApiUri(relativePathAndQuery);
        var key = uri.PathAndQuery;
        var lazy = inFlight.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(
            () => GetJsonBytesAsync(uri), LazyThreadSafetyMode.ExecutionAndPublication));
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
        finally
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompleted)
                inFlight.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, lazy));
        }
    }

    public async Task<HttpResponseMessage> SendDownloadAsync(
        Uri uri,
        CancellationToken cancellationToken = default)
    {
        if (!IsApprovedDownloadUri(uri))
            throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                "CurseForge returned an unapproved download destination.");
        var credential = RequireCredential();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("x-api-key", credential);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                response.Dispose();
                throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                    "CurseForge download redirects are not followed automatically.", response.StatusCode);
            }
            if (!response.IsSuccessStatusCode)
            {
                var error = MapStatus(response.StatusCode);
                response.Dispose();
                throw error;
            }
            if (response.RequestMessage?.RequestUri is not { } final || !IsApprovedDownloadUri(final))
            {
                response.Dispose();
                throw new CurseForgeApiException(CurseForgeFailureKind.UnapprovedHost,
                    "The CurseForge download left the approved CDN boundary.");
            }
            return response;
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

    public static bool IsApprovedDownloadUri(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        return host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<byte[]> GetJsonBytesAsync(Uri uri)
    {
        var credential = RequireCredential();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("x-api-key", credential);
            using var timeout = new CancellationTokenSource(RequestTimeout);
            try
            {
                using var response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (IsRedirect(response.StatusCode))
                    throw new CurseForgeApiException(CurseForgeFailureKind.Redirect,
                        "CurseForge API redirects are not followed automatically.", response.StatusCode);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0 &&
                    RetryDelay(response.Headers.RetryAfter) is { } delay)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw MapStatus(response.StatusCode);
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

    private static TimeSpan? RetryDelay(RetryConditionHeaderValue? retryAfter)
    {
        if (retryAfter is null) return null;
        var delay = retryAfter.Delta ??
                    (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        if (delay <= TimeSpan.Zero) return TimeSpan.Zero;
        return delay <= MaximumRetryAfter ? delay : null;
    }

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and <= 399;

    private static CurseForgeApiException MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new CurseForgeApiException(CurseForgeFailureKind.Authentication,
                "CurseForge rejected the configured credential.", status),
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
        if (ownsHttp) http.Dispose();
    }
}
