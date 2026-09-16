using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeServiceTransportTests
{
    private static readonly Uri Endpoint = new("https://curseforge.fixture.example/relay/");
    private static readonly Uri Source = new("https://mediafilez.forgecdn.net/files/8764/245/ATM10.zip");

    [Theory]
    [InlineData("client_rate_limited", "from this network")]
    [InlineData("provider_rate_limited", "temporarily limiting")]
    [InlineData("service_capacity_reached", "daily capacity")]
    [InlineData("service_concurrency_reached", "slots are busy")]
    public async Task Service_rate_limits_explain_the_actual_gate_without_remote_error_text(string code, string expected)
    {
        var handler = new Handler(_ =>
        {
            var response = Json($$"""{"protocolVersion":1,"error":"{{code}}","message":"untrusted-secret-detail"}""");
            response.StatusCode = HttpStatusCode.TooManyRequests;
            return response;
        });
        using var api = new CurseForgeApiClient(new Secrets { ThrowOnRead = true }, handler, Endpoint);
        var metadata = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.GetJsonAsync("/v1/games/432"));
        var download = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.SendDownloadAsync(Source));
        foreach (var failure in new[] { metadata, download })
        {
            Assert.Equal(CurseForgeFailureKind.RateLimited, failure.Kind);
            Assert.Contains(expected, failure.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("untrusted-secret-detail", failure.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("{\"protocolVersion\":2,\"error\":\"service_capacity_reached\"}")]
    [InlineData("{\"protocolVersion\":1,\"error\":\"untrusted-secret-detail\"}")]
    [InlineData("{\"protocolVersion\":\"untrusted-secret-detail\",\"error\":\"service_capacity_reached\"}")]
    [InlineData("[\"untrusted-secret-detail\"]")]
    [InlineData("not-json-untrusted-secret-detail")]
    public async Task Invalid_service_error_documents_fall_back_without_echo(string body)
    {
        var handler = new Handler(_ =>
        {
            var response = Json(body);
            response.StatusCode = HttpStatusCode.TooManyRequests;
            return response;
        });
        using var api = new CurseForgeApiClient(new Secrets { ThrowOnRead = true }, handler, Endpoint);
        var failure = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.SendDownloadAsync(Source));
        Assert.Equal("The CurseForge rate limit is active.", failure.Message);
    }

    [Fact]
    public async Task Oversized_service_error_is_not_read_or_echoed()
    {
        var handler = new Handler(_ =>
        {
            var response = Json(new string('x', 4097));
            response.StatusCode = HttpStatusCode.TooManyRequests;
            return response;
        });
        using var api = new CurseForgeApiClient(new Secrets { ThrowOnRead = true }, handler, Endpoint);
        var failure = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.SendDownloadAsync(Source));
        Assert.Equal("The CurseForge rate limit is active.", failure.Message);
    }

    [Fact]
    public async Task Application_service_needs_no_local_key_and_preserves_metadata_query_and_shape()
    {
        var secrets = new Secrets { ThrowOnRead = true };
        var handler = new Handler(request =>
        {
            Assert.Equal("https://curseforge.fixture.example/relay/v1/mods/search?gameId=432&searchFilter=ATM%2010", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("x-api-key"));
            Assert.Equal(HttpMethod.Get, request.Method);
            return Json("""{"data":[{"id":925200}],"pagination":{"totalCount":1}}""");
        });
        using var api = new CurseForgeApiClient(secrets, handler, Endpoint);
        Assert.True(api.CanAccess);
        Assert.False(api.HasCredential);
        Assert.Equal(CurseForgeAccessMode.ApplicationService, api.AccessMode);
        Assert.Contains("remote service", api.AccessDetail, StringComparison.Ordinal);

        using var result = await api.GetJsonAsync("/v1/mods/search?gameId=432&searchFilter=ATM%2010");

        Assert.Equal(925200, result.RootElement.GetProperty("data")[0].GetProperty("id").GetInt32());
        Assert.Equal(1, result.RootElement.GetProperty("pagination").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, handler.Count);
        Assert.Equal(0, secrets.Reads);
    }

    [Fact]
    public async Task Application_service_is_preferred_without_reading_or_sending_a_saved_personal_key()
    {
        var secrets = new Secrets { Key = "fixture-personal-secret", ThrowOnRead = true };
        var handler = new Handler(request =>
        {
            Assert.Equal(Endpoint.Host, request.RequestUri!.Host);
            Assert.False(request.Headers.Contains("x-api-key"));
            return request.RequestUri.AbsolutePath.Contains("downloads", StringComparison.Ordinal)
                ? Binary() : Json("""{"data":{"id":432}}""");
        });
        using var api = new CurseForgeApiClient(secrets, handler, Endpoint);

        using var metadata = await api.GetJsonAsync("/v1/games/432");
        using var payload = await api.SendDownloadAsync(Source);

        Assert.True(api.HasCredential);
        Assert.Equal(CurseForgeAccessMode.ApplicationService, api.AccessMode);
        Assert.Equal(0, secrets.Reads);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task Native_personal_credential_validation_still_contacts_only_the_official_api()
    {
        var handler = new Handler(request =>
        {
            Assert.Equal("https://api.curseforge.com/v1/games/432", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-candidate-secret", request.Headers.GetValues("x-api-key").Single());
            return Json("""{"data":{"id":432}}""");
        });
        using var api = new CurseForgeApiClient(new Secrets { ThrowOnRead = true }, handler, Endpoint);

        await api.ValidateCredentialAsync("fixture-candidate-secret");

        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task Download_route_is_exact_id_and_source_hash_and_response_keeps_actual_transport_uri()
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source.AbsoluteUri))).ToLowerInvariant();
        var expected = new Uri($"{Endpoint}downloads/8764245?sourceSha256={digest}");
        var handler = new Handler(request =>
        {
            Assert.Equal(expected, request.RequestUri);
            Assert.False(request.Headers.Contains("x-api-key"));
            return Binary();
        });
        using var api = new CurseForgeApiClient(new Secrets(), handler, Endpoint);

        using var response = await api.SendDownloadAsync(Source);

        Assert.Equal(expected, response.RequestMessage!.RequestUri);
        Assert.Equal("fixture-bytes", await response.Content.ReadAsStringAsync());
        Assert.True(api.IsApprovedDownloadResponse(response, Source));
        Assert.False(CurseForgeApiClient.IsApprovedDownloadUri(expected));
        Assert.False(api.IsApprovedDownloadResponse(response, new Uri("https://mediafilez.forgecdn.net/files/8764/246/other.zip")));
        using var otherClient = new CurseForgeApiClient(new Secrets(), new Handler(_ => Binary()), Endpoint);
        Assert.False(otherClient.IsApprovedDownloadResponse(response, Source));
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, Source);
        Assert.False(api.IsApprovedDownloadResponse(response, Source));
    }

    [Theory]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/ATM10.zip", 8764245)]
    [InlineData("https://edge.forgecdn.net/files/8764/5/file.jar", 8764005)]
    [InlineData("https://edge.forgecdn.net/files/8764/05/file.jar", 8764005)]
    [InlineData("https://media.forgecdn.net/files/0/1/file.jar", 1)]
    [InlineData("https://mediafilez.forgecdn.net/files/2147483/647/file.jar", int.MaxValue)]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/pack%20name.zip", 8764245)]
    public void Standard_cdn_file_identity_is_exact_and_remainder_is_padded(string source, int expected)
    {
        Assert.True(CurseForgeApiClient.TryGetStandardDownloadFileId(new Uri(source), out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("pack name.zip", "pack name.zip")]
    [InlineData("pack%20name.zip", "pack name.zip")]
    [InlineData("café.zip", "café.zip")]
    [InlineData("caf%C3%A9.zip", "café.zip")]
    [InlineData("%41.zip", "A.zip")]
    [InlineData("A.zip", "A.zip")]
    [InlineData("literal%2520.zip", "literal%20.zip")]
    public void Source_digest_uses_cross_runtime_single_decoded_path_canonicalization(string fileName, string canonicalName)
    {
        const string prefix = "https://mediafilez.forgecdn.net/files/8764/245/";
        using var api = new CurseForgeApiClient(new Secrets(), new Handler(_ => Binary()), Endpoint);
        var transport = api.CreateServiceDownloadUri(new Uri(prefix + fileName));
        var expectedDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prefix + canonicalName))).ToLowerInvariant();
        Assert.Equal($"?sourceSha256={expectedDigest}", transport.Query);
    }

    [Theory]
    [InlineData("https://evil.example/files/8764/245/file.jar")]
    [InlineData("https://evilforgecdn.net/files/8764/245/file.jar")]
    [InlineData("http://mediafilez.forgecdn.net/files/8764/245/file.jar")]
    [InlineData("https://user@mediafilez.forgecdn.net/files/8764/245/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net:444/files/8764/245/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/file.jar?token=arbitrary")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/file.jar#fragment")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/folder/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/file%2Fname.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/file%5Cname.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/245/file%00.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/8764/1000/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/0/0/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/2147483/648/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/08764/245/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/+8764/245/file.jar")]
    [InlineData("https://mediafilez.forgecdn.net/files/1/file.jar")]
    public async Task Unsupported_download_identity_fails_before_service_network_access(string source)
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Network must not run."));
        using var api = new CurseForgeApiClient(new Secrets(), handler, Endpoint);

        await Assert.ThrowsAsync<CurseForgeApiException>(() => api.SendDownloadAsync(new Uri(source)));

        Assert.Equal(0, handler.Count);
    }

    [Theory]
    [InlineData("http://service.example/")]
    [InlineData("https://user:secret@service.example/")]
    [InlineData("https://service.example/?key=secret")]
    [InlineData("https://service.example/#secret")]
    [InlineData("https://service.example:444/")]
    [InlineData("https://localhost/")]
    [InlineData("https://example.localhost/")]
    [InlineData("https://example.local/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://192.168.1.1/")]
    [InlineData("https://[::1]/")]
    [InlineData("https://service/")]
    [InlineData("https://service.example./")]
    [InlineData("https://service.example/%2Fescape")]
    [InlineData("https://service.example/ key")]
    public void Unsafe_build_time_endpoint_is_rejected(string endpoint) =>
        Assert.Throws<InvalidOperationException>(() => CurseForgeServiceConfiguration.ValidateEndpoint(endpoint));

    [Fact]
    public void Empty_build_configuration_preserves_personal_key_and_unavailable_states()
    {
        Assert.Null(CurseForgeServiceConfiguration.ValidateEndpoint(""));
        Assert.Null(CurseForgeServiceConfiguration.ValidateEndpoint(null));
        Assert.Equal(Endpoint, CurseForgeServiceConfiguration.ValidateEndpoint(Endpoint.AbsoluteUri.TrimEnd('/')));
        using var unavailable = new CurseForgeApiClient(new Secrets(), new Handler(_ => Json("{}")), null);
        using var personal = new CurseForgeApiClient(new Secrets { Key = "fixture-key" }, new Handler(_ => Json("{}")), null);
        Assert.False(unavailable.CanAccess);
        Assert.Equal(CurseForgeAccessMode.Unavailable, unavailable.AccessMode);
        Assert.True(personal.CanAccess);
        Assert.Equal(CurseForgeAccessMode.PersonalKey, personal.AccessMode);
    }

    [Fact]
    public void Broker_download_header_budget_is_bounded_without_changing_direct_or_metadata_timeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), CurseForgeApiClient.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(90), CurseForgeApiClient.ServiceDownloadHeaderTimeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_redirects_are_never_followed_or_given_any_credential(bool download)
    {
        var handler = new Handler(request =>
        {
            Assert.False(request.Headers.Contains("x-api-key"));
            return new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://evil.example/credential-collector") }
            };
        });
        using var api = new CurseForgeApiClient(new Secrets { Key = "fixture-personal-secret", ThrowOnRead = true }, handler, Endpoint);

        var error = await Assert.ThrowsAsync<CurseForgeApiException>(async () =>
        {
            if (download) { using var response = await api.SendDownloadAsync(Source); }
            else { using var response = await api.GetJsonAsync("/v1/games/432"); }
        });

        Assert.Equal(CurseForgeFailureKind.Redirect, error.Kind);
        Assert.Equal(1, handler.Count);
        Assert.DoesNotContain("fixture-personal-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_or_changed_final_transport_route_is_rejected(bool download)
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, "unexpected"))
        });
        using var api = new CurseForgeApiClient(new Secrets(), handler, Endpoint);

        var error = await Assert.ThrowsAsync<CurseForgeApiException>(async () =>
        {
            if (download) { using var response = await api.SendDownloadAsync(Source); }
            else { using var response = await api.GetJsonAsync("/v1/games/432"); }
        });

        Assert.Equal(CurseForgeFailureKind.UnapprovedHost, error.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Service_timeout_and_caller_cancellation_are_distinct(bool download)
    {
        var handler = new Handler((_, _) => throw new TaskCanceledException("fixture timeout"));
        using var api = new CurseForgeApiClient(new Secrets(), handler, Endpoint);
        var error = await Assert.ThrowsAsync<CurseForgeApiException>(async () =>
        {
            if (download) { using var response = await api.SendDownloadAsync(Source); }
            else { using var response = await api.GetJsonAsync("/v1/games/432"); }
        });
        Assert.Equal(CurseForgeFailureKind.Timeout, error.Kind);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (download) { using var response = await api.SendDownloadAsync(Source, cancelled.Token); }
            else { using var response = await api.GetJsonAsync("/v1/games/432", cancelled.Token); }
        });
    }

    [Fact]
    public async Task Service_metadata_reuses_bounded_rate_limit_and_content_checks()
    {
        var handler = new Handler(request => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero) },
            Content = new StringContent("not returned sensitive body")
        });
        using var api = new CurseForgeApiClient(new Secrets(), handler, Endpoint);
        var limited = await Assert.ThrowsAsync<CurseForgeApiException>(() => api.GetJsonAsync("/v1/games/432"));
        Assert.Equal(CurseForgeFailureKind.RateLimited, limited.Kind);
        Assert.Equal(2, handler.Count);
        Assert.DoesNotContain("sensitive body", limited.ToString(), StringComparison.Ordinal);

        using var oversized = new CurseForgeApiClient(new Secrets(), new Handler(_ =>
        {
            var response = Json("{}");
            response.Content.Headers.ContentLength = CurseForgeApiClient.MaximumJsonBytes + 1;
            return response;
        }), Endpoint);
        var sizeError = await Assert.ThrowsAsync<CurseForgeApiException>(() => oversized.GetJsonAsync("/v1/games/432"));
        Assert.Equal(CurseForgeFailureKind.OversizedResponse, sizeError.Kind);
    }

    [Fact]
    public void Catalog_and_addon_availability_do_not_require_a_personal_credential_in_service_mode()
    {
        using var api = new CurseForgeApiClient(new Secrets(), new Handler(_ => Json("{}")), Endpoint);
        using var catalog = new CurseForgeCatalogProvider(api);
        var plugins = new CurseForgePluginProvider(api);
        Assert.True(catalog.IsAvailable);
        Assert.True(plugins.Status.Available);
        Assert.Equal(api.AccessDetail, catalog.AvailabilityDetail);
        Assert.Equal(api.AccessDetail, plugins.Status.Detail);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Binary() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("fixture-bytes", Encoding.UTF8, "application/octet-stream")
    };

    private sealed class Secrets : ISecretStore
    {
        public string? Key { get; set; }
        public bool ThrowOnRead { get; init; }
        public int Reads { get; private set; }
        public void SetSecret(string name, string value) => Key = value;
        public string? GetSecret(string name)
        {
            Reads++;
            if (ThrowOnRead) throw new InvalidOperationException("The service must not read any local credential.");
            return Key;
        }
        public bool Contains(string name) => Key is not null;
        public void Delete(string name) => Key = null;
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send;
        public int Count { get; private set; }
        public Handler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request))) { }
        public Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => this.send = send;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            cancellationToken.ThrowIfCancellationRequested();
            return send(request, cancellationToken);
        }
    }
}
