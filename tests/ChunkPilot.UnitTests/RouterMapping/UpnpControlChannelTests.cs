using System.Net;
using System.Text;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests.RouterMapping;

public sealed class UpnpControlChannelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_description_is_rejected_with_or_without_content_length(bool knownLength)
    {
        using var http = new HttpClient(new ResponseHandler(() => OversizedResponse(knownLength)));
        using var channel = new UpnpControlChannel(new RouterMappingOptions(), http);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            channel.GetDescriptionAsync(new Uri("http://127.0.0.1/fixture"), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Oversized_soap_is_rejected_even_when_it_contains_a_valid_success(bool knownLength)
    {
        using var http = new HttpClient(new ResponseHandler(() => OversizedResponse(knownLength)));
        using var channel = new UpnpControlChannel(new RouterMappingOptions(), http);

        var result = await channel.InvokeAsync(new Uri("http://127.0.0.1/fixture"),
            FakeUpnpGateway.ServiceType, "DeletePortMapping", [], CancellationToken.None);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Response_namespace_must_match_the_requested_service()
    {
        using var http = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Envelope("urn:some-other-service"))
        }));
        using var channel = new UpnpControlChannel(new RouterMappingOptions(), http);

        var result = await channel.InvokeAsync(new Uri("http://127.0.0.1/fixture"),
            FakeUpnpGateway.ServiceType, "DeletePortMapping", [], CancellationToken.None);

        Assert.False(result.Success);
    }

    private static HttpResponseMessage OversizedResponse(bool knownLength)
    {
        var bytes = Encoding.UTF8.GetBytes(Envelope(FakeUpnpGateway.ServiceType) +
                                          new string(' ', 1024 * 1024));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = knownLength
                ? new ByteArrayContent(bytes)
                : new StreamContent(new NonSeekableStream(bytes))
        };
    }

    private static string Envelope(string serviceType) =>
        $"<s:Envelope xmlns:s='http://schemas.xmlsoap.org/soap/envelope/'><s:Body>" +
        $"<u:DeletePortMappingResponse xmlns:u='{serviceType}' /></s:Body></s:Envelope>";

    private sealed class ResponseHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond());
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Fact]
    public async Task The_http_deadline_covers_a_body_that_stalls_after_headers()
    {
        using var http = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StalledContent()
        }));
        using var channel = new UpnpControlChannel(new RouterMappingOptions
        {
            HttpTimeout = TimeSpan.FromMilliseconds(100)
        }, http);

        var result = await channel.InvokeAsync(new Uri("http://127.0.0.1/fixture"),
            FakeUpnpGateway.ServiceType, "DeletePortMapping", [], CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(result.Success);
    }

    private sealed class StalledContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(TimeSpan.FromSeconds(5));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
