using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ChunkPilot.App.WebUi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ChunkPilot.UnitTests;

public sealed class PlayerHeadImageServiceTests
{
    [Fact]
    public async Task Official_skin_is_cropped_to_a_data_url_and_cached_by_uuid()
    {
        var uuid = Guid.NewGuid();
        var requests = 0;
        var skin = SkinPng();
        var handler = new DelegateHandler(request =>
        {
            requests++;
            if (request.RequestUri!.Host == "sessionserver.mojang.com")
                return Response(Profile("https://textures.minecraft.net/texture/authoritative"), "application/json");
            Assert.Equal("textures.minecraft.net", request.RequestUri.Host);
            return Response(skin, "image/png");
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };
        using var service = new PlayerHeadImageService(client);

        var first = await service.GetDataUrlAsync(uuid);
        var second = await service.GetDataUrlAsync(uuid);

        Assert.NotNull(first);
        Assert.StartsWith("data:image/png;base64,", first, StringComparison.Ordinal);
        Assert.Equal(first, second);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task Third_party_texture_host_and_offline_failure_return_the_local_fallback_contract()
    {
        var requests = 0;
        var thirdParty = new DelegateHandler(_ =>
        {
            requests++;
            return Response(Profile("https://example.com/skin.png"), "application/json");
        });
        using var thirdPartyClient = new HttpClient(thirdParty);
        using var service = new PlayerHeadImageService(thirdPartyClient);
        Assert.Null(await service.GetDataUrlAsync(Guid.NewGuid()));
        Assert.Equal(1, requests);

        using var offlineClient = new HttpClient(new DelegateHandler(_ => throw new HttpRequestException("offline")));
        using var offline = new PlayerHeadImageService(offlineClient);
        Assert.Null(await offline.GetDataUrlAsync(Guid.NewGuid()));
    }

    private static string Profile(string textureUrl)
    {
        var payload = JsonSerializer.Serialize(new { textures = new { SKIN = new { url = textureUrl } } });
        var textures = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        return JsonSerializer.Serialize(new { properties = new[] { new { name = "textures", value = textures } } });
    }

    private static byte[] SkinPng()
    {
        using var image = new Image<Rgba32>(64, 64, new Rgba32(20, 30, 40, 255));
        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        return output.ToArray();
    }

    private static HttpResponseMessage Response(string content, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, mediaType) };

    private static HttpResponseMessage Response(byte[] content, string mediaType)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
