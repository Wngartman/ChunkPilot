using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeApiClientTests
{
    [Fact]
    public async Task Successful_request_is_native_bounded_authenticated_and_exact_host()
    {
        var secrets = WithKey();
        var handler = new Handler(request =>
        {
            Assert.Equal(Uri.UriSchemeHttps, request.RequestUri!.Scheme);
            Assert.Equal(CurseForgeApiClient.ApiHost, request.RequestUri.Host);
            Assert.Equal("fixture-approved-key", request.Headers.GetValues("x-api-key").Single());
            Assert.Contains("ChunkPilot", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
            return Json("""{"data":{"id":432}}""");
        });
        using var client = new CurseForgeApiClient(secrets, handler);

        using var result = await client.GetJsonAsync("/v1/games/432");

        Assert.Equal(432, result.RootElement.GetProperty("data").GetProperty("id").GetInt32());
        Assert.Equal(1, handler.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, CurseForgeFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, CurseForgeFailureKind.Authentication)]
    [InlineData(HttpStatusCode.NotFound, CurseForgeFailureKind.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError, CurseForgeFailureKind.Server)]
    public async Task Provider_statuses_are_mapped_without_response_body_or_credential(
        HttpStatusCode status,
        CurseForgeFailureKind expected)
    {
        var handler = new Handler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent("sensitive-provider-body")
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.GetJsonAsync("/v1/games/432"));

        Assert.Equal(expected, exception.Kind);
        Assert.DoesNotContain("fixture-approved-key", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-provider-body", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rate_limit_retries_once_only_for_a_bounded_retry_after()
    {
        var handler = new Handler(_ =>
        {
            if (_.Headers.UserAgent.Count == 0) throw new InvalidOperationException();
            if (_.RequestUri is null) throw new InvalidOperationException();
            if (_.Method != HttpMethod.Get) throw new InvalidOperationException();
            if (_.Headers.GetValues("x-api-key").Single() != "fixture-approved-key")
                throw new InvalidOperationException();
            return Json("""{"data":[]}""");
        });
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) }
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        using var result = await client.GetJsonAsync("/v1/mods/search?gameId=432");

        Assert.Equal(2, handler.Count);
        Assert.Equal(0, result.RootElement.GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Identical_concurrent_reads_are_deduplicated_only_while_in_flight()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return Json("""{"data":[]}""");
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var first = client.GetJsonAsync("/v1/mods/search?gameId=432");
        var second = client.GetJsonAsync("/v1/mods/search?gameId=432");
        await WaitForAsync(() => handler.Count == 1);
        release.SetResult();
        using var firstResult = await first;
        using var secondResult = await second;

        Assert.Equal(1, handler.Count);
        using var third = await client.GetJsonAsync("/v1/mods/search?gameId=432");
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Cancellation_does_not_become_a_false_provider_failure()
    {
        var handler = new Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return Json("""{"data":[]}""");
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetJsonAsync("/v1/games/432", cancellation.Token));
    }

    [Fact]
    public async Task Cancelled_waiter_cannot_turn_a_completed_request_into_a_response_cache()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return Json("""{"data":[]}""");
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);
        using var cancellation = new CancellationTokenSource();

        var cancelled = client.GetJsonAsync("/v1/mods/search?gameId=432", cancellation.Token);
        await WaitForAsync(() => handler.Count == 1);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        await WaitForAsync(() => client.InFlightRequestCount == 0);
        using var second = await client.GetJsonAsync("/v1/mods/search?gameId=432");

        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Transport_timeout_is_distinct()
    {
        var handler = new Handler((_, _) => throw new TaskCanceledException("fixture timeout"));
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.GetJsonAsync("/v1/games/432"));

        Assert.Equal(CurseForgeFailureKind.Timeout, exception.Kind);
    }

    [Theory]
    [InlineData("malformed", "application/json", CurseForgeFailureKind.MalformedResponse)]
    [InlineData("{}", "text/html", CurseForgeFailureKind.WrongContentType)]
    public async Task Malformed_or_wrong_content_is_rejected(
        string body,
        string contentType,
        CurseForgeFailureKind kind)
    {
        var handler = new Handler(_ => Response(HttpStatusCode.OK, body, contentType));
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.GetJsonAsync("/v1/games/432"));

        Assert.Equal(kind, exception.Kind);
    }

    [Fact]
    public async Task Declared_oversized_response_is_rejected_before_reading()
    {
        var handler = new Handler(_ =>
        {
            var response = Json("{}");
            response.Content.Headers.ContentLength = CurseForgeApiClient.MaximumJsonBytes + 1;
            return response;
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.GetJsonAsync("/v1/games/432"));

        Assert.Equal(CurseForgeFailureKind.OversizedResponse, exception.Kind);
    }

    [Fact]
    public async Task Redirect_and_unapproved_hosts_are_rejected()
    {
        var requests = new List<(string Host, bool SentCredential)>();
        var handler = new Handler(request =>
        {
            requests.Add((request.RequestUri!.IdnHost, request.Headers.Contains("x-api-key")));
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/escape") }
            };
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var redirect = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.GetJsonAsync("/v1/games/432"));
        var unapproved = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.SendDownloadAsync(new Uri("https://evil.example/file.jar")));

        Assert.Equal(CurseForgeFailureKind.Redirect, redirect.Kind);
        Assert.Equal(CurseForgeFailureKind.UnapprovedHost, unapproved.Kind);
        Assert.Equal(1, handler.Count);
        var request = Assert.Single(requests);
        Assert.Equal(CurseForgeApiClient.ApiHost, request.Host);
        Assert.True(request.SentCredential);
        Assert.DoesNotContain(requests, seen =>
            seen.Host.Equals("evil.example", StringComparison.OrdinalIgnoreCase));
        Assert.True(CurseForgeApiClient.IsApprovedDownloadUri(
            new Uri("https://mediafilez.forgecdn.net/files/1/file.jar")));
        Assert.False(CurseForgeApiClient.IsApprovedDownloadUri(
            new Uri("https://user@mediafilez.forgecdn.net/files/1/file.jar")));
        Assert.False(CurseForgeApiClient.IsApprovedDownloadUri(
            new Uri("https://evilforgecdn.net/files/1/file.jar")));
    }

    [Fact]
    public async Task Download_follows_bounded_approved_redirect_and_authenticates_each_cdn_request()
    {
        var requests = new List<(Uri Uri, string Credential)>();
        var intermediateContent = new TrackingContent();
        var handler = new Handler(request =>
        {
            requests.Add((request.RequestUri!, request.Headers.GetValues("x-api-key").Single()));
            if (requests.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
                {
                    Headers = { Location = new Uri("/files/1/middle.jar", UriKind.Relative) },
                    Content = intermediateContent
                };
            if (requests.Count == 2)
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers = { Location = new Uri("https://edge.forgecdn.net/files/1/final.jar") }
                };
            return Response(HttpStatusCode.OK, "fixture", "application/octet-stream");
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        using var response = await client.SendDownloadAsync(
            new Uri("https://mediafilez.forgecdn.net/files/1/start.jar"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("fixture", await response.Content.ReadAsStringAsync());
        Assert.Equal("edge.forgecdn.net", response.RequestMessage!.RequestUri!.IdnHost);
        Assert.True(intermediateContent.Disposed);
        Assert.Equal(3, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.True(CurseForgeApiClient.IsApprovedDownloadUri(request.Uri));
            Assert.Equal("fixture-approved-key", request.Credential);
        });
        Assert.Equal("mediafilez.forgecdn.net", requests[0].Uri.IdnHost);
        Assert.Equal("mediafilez.forgecdn.net", requests[1].Uri.IdnHost);
        Assert.Equal("edge.forgecdn.net", requests[2].Uri.IdnHost);
    }

    [Theory]
    [InlineData("https://evil.example/stolen.jar")]
    [InlineData("http://mediafilez.forgecdn.net/files/1/stolen.jar")]
    [InlineData("https://mediafilez.forgecdn.net:444/files/1/stolen.jar")]
    [InlineData("https://user@mediafilez.forgecdn.net/files/1/stolen.jar")]
    [InlineData("https://evilforgecdn.net/files/1/stolen.jar")]
    public async Task Download_redirect_never_forwards_credential_to_an_unapproved_host(string destination)
    {
        var requests = new List<(string Host, bool SentCredential)>();
        var handler = new Handler(request =>
        {
            requests.Add((request.RequestUri!.IdnHost, request.Headers.Contains("x-api-key")));
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri(destination) }
            };
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.SendDownloadAsync(new Uri("https://mediafilez.forgecdn.net/files/1/start.jar")));

        Assert.Equal(CurseForgeFailureKind.UnapprovedHost, exception.Kind);
        var request = Assert.Single(requests);
        Assert.Equal("mediafilez.forgecdn.net", request.Host);
        Assert.True(request.SentCredential);
    }

    [Fact]
    public async Task Download_redirect_chain_is_bounded_and_requires_a_destination()
    {
        var redirectCount = 0;
        var handler = new Handler(_ =>
        {
            redirectCount++;
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers =
                {
                    Location = new Uri($"/files/1/redirect-{redirectCount}.jar", UriKind.Relative)
                }
            };
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var excessive = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.SendDownloadAsync(new Uri("https://mediafilez.forgecdn.net/files/1/start.jar")));

        Assert.Equal(CurseForgeFailureKind.Redirect, excessive.Kind);
        Assert.Equal(CurseForgeApiClient.MaximumDownloadRedirects + 1, redirectCount);

        using var missingDestinationClient = new CurseForgeApiClient(WithKey(),
            new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)));
        var missing = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            missingDestinationClient.SendDownloadAsync(
                new Uri("https://mediafilez.forgecdn.net/files/1/start.jar")));
        Assert.Equal(CurseForgeFailureKind.Redirect, missing.Kind);
    }

    [Fact]
    public async Task Download_rejects_nonstandard_redirection_without_following_it()
    {
        var content = new TrackingContent();
        var handler = new Handler(_ => new HttpResponseMessage((HttpStatusCode)305)
        {
            Headers = { Location = new Uri("https://edge.forgecdn.net/files/1/final.jar") },
            Content = content
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);

        var exception = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            client.SendDownloadAsync(new Uri("https://mediafilez.forgecdn.net/files/1/start.jar")));

        Assert.Equal(CurseForgeFailureKind.Redirect, exception.Kind);
        Assert.Equal(1, handler.Count);
        Assert.True(content.Disposed);
    }

    [Fact]
    public async Task Download_distinguishes_caller_cancellation_from_transport_timeout()
    {
        var handler = new Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return Response(HttpStatusCode.OK, "fixture", "application/octet-stream");
        });
        using var client = new CurseForgeApiClient(WithKey(), handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.SendDownloadAsync(
                new Uri("https://mediafilez.forgecdn.net/files/1/start.jar"), cancellation.Token));

        using var timeoutClient = new CurseForgeApiClient(WithKey(),
            new Handler((_, _) => throw new TaskCanceledException("fixture timeout")));
        var timeout = await Assert.ThrowsAsync<CurseForgeApiException>(() =>
            timeoutClient.SendDownloadAsync(
                new Uri("https://mediafilez.forgecdn.net/files/1/start.jar")));
        Assert.Equal(CurseForgeFailureKind.Timeout, timeout.Kind);
    }

    [Fact]
    public async Task Missing_credential_fails_before_network_access()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("network must not run"));
        using var client = new CurseForgeApiClient(new MemorySecrets(), handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetJsonAsync("/v1/games/432"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendDownloadAsync(
            new Uri("https://mediafilez.forgecdn.net/files/1/file.jar")));

        Assert.Equal(0, handler.Count);
    }

    private static MemorySecrets WithKey()
    {
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        return secrets;
    }

    private static HttpResponseMessage Json(string body) =>
        Response(HttpStatusCode.OK, body, "application/json");

    private static HttpResponseMessage Response(HttpStatusCode status, string body, string contentType) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType)
    };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public void SetSecret(string name, string value) => values[name] = value;
        public string? GetSecret(string name) => values.GetValueOrDefault(name);
        public bool Contains(string name) => values.ContainsKey(name);
        public void Delete(string name) => values.Remove(name);
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response;
        private readonly Queue<HttpResponseMessage> queued = new();
        private int count;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> response)
            : this((request, _) => Task.FromResult(response(request)))
        {
        }

        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) =>
            this.response = response;

        public int Count => Volatile.Read(ref count);
        public void Enqueue(HttpResponseMessage value) => queued.Enqueue(value);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref count);
            if (queued.TryDequeue(out var next)) return Task.FromResult(next);
            return response(request, cancellationToken);
        }
    }

    private sealed class TrackingContent : ByteArrayContent
    {
        public TrackingContent() : base([]) { }

        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
