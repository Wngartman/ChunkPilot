using System.Net;
using System.Text;
using System.Text.Json;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ManagedJavaProviderTests
{
    [Fact]
    public async Task Temurin_provider_prefers_the_smaller_official_JRE_package()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Query.Contains("image_type=jre", StringComparison.Ordinal)
                ? Json(Package("jre.zip"))
                : throw new Xunit.Sdk.XunitException("JDK fallback must not run when a JRE exists."));

        var package = await new AdoptiumTemurinProvider(new HttpClient(handler)).ResolveAsync(21);

        Assert.Equal("jre.zip", package.FileName);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Temurin_provider_uses_the_official_JDK_when_a_historical_JRE_does_not_exist()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Query.Contains("image_type=jre", StringComparison.Ordinal)
                ? Json("[]")
                : Json(Package("jdk-16.zip")));

        var package = await new AdoptiumTemurinProvider(new HttpClient(handler)).ResolveAsync(16);

        Assert.Equal(16, package.MajorVersion);
        Assert.Equal("jdk-16.zip", package.FileName);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Missing_JRE_HTTP_404_uses_the_official_JDK_without_falling_back_on_provider_outages()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Query.Contains("image_type=jre", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Json(Package("jdk-16.zip")));
        var package = await new AdoptiumTemurinProvider(new HttpClient(handler)).ResolveAsync(16);
        Assert.Equal("jdk-16.zip", package.FileName);
        Assert.Equal(2, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Provider_failure_is_not_disguised_as_a_missing_historical_runtime(HttpStatusCode status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(status));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new AdoptiumTemurinProvider(new HttpClient(handler)).ResolveAsync(16));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(1, handler.RequestCount);
    }

    private static string Package(string fileName) => JsonSerializer.Serialize(new[]
    {
        new
        {
            release_name = "jdk-16.0.2+7",
            binary = new
            {
                package = new
                {
                    link = $"https://example.invalid/{fileName}",
                    name = fileName,
                    checksum = new string('a', 64),
                    size = 12_345
                }
            }
        }
    });

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(response(request));
        }
    }
}
