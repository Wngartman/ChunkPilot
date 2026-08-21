using System.Net.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpPoint = SixLabors.ImageSharp.Point;
using ImageSharpRectangle = SixLabors.ImageSharp.Rectangle;
using ImageSharpSize = SixLabors.ImageSharp.Size;

namespace ChunkPilot.App.WebUi;

/// <summary>Fetches and crops official Mojang skins without exposing remote URLs to the renderer.</summary>
internal sealed class PlayerHeadImageService : IDisposable
{
    internal const int MaximumSkinBytes = 1_048_576;
    internal const int MaximumCachedPlayers = 128;
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly object cacheLock = new();
    private readonly Dictionary<Guid, Task<string?>> cache = [];
    private readonly LinkedList<Guid> lru = [];

    internal PlayerHeadImageService(HttpClient? client = null)
    {
        this.client = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        ownsClient = client is null;
        this.client.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.3 (local Minecraft server manager)");
    }

    internal Task<string?> GetDataUrlAsync(Guid uuid, CancellationToken cancellationToken = default)
    {
        lock (cacheLock)
        {
            if (cache.TryGetValue(uuid, out var cached))
            {
                lru.Remove(uuid);
                lru.AddLast(uuid);
                return cached;
            }

            var pending = DownloadAsync(uuid, cancellationToken);
            cache[uuid] = pending;
            lru.AddLast(uuid);
            while (lru.Count > MaximumCachedPlayers)
            {
                var oldest = lru.First!.Value;
                lru.RemoveFirst();
                cache.Remove(oldest);
            }
            return pending;
        }
    }

    private async Task<string?> DownloadAsync(Guid uuid, CancellationToken cancellationToken)
    {
        try
        {
            var profileUri = new Uri($"https://sessionserver.mojang.com/session/minecraft/profile/{uuid:N}");
            using var profileResponse = await client.GetAsync(profileUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!profileResponse.IsSuccessStatusCode || profileResponse.Content.Headers.ContentLength > MaximumSkinBytes)
                return null;
            var profileBytes = await ReadBoundedAsync(profileResponse.Content, MaximumSkinBytes, cancellationToken).ConfigureAwait(false);
            if (profileBytes is null || !DesignSystem.Components.MinecraftSkinProfileParser.TryGetTextureUri(
                    System.Text.Encoding.UTF8.GetString(profileBytes), out var textureUri))
                return null;

            using var imageResponse = await client.GetAsync(textureUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!imageResponse.IsSuccessStatusCode || imageResponse.Content.Headers.ContentLength > MaximumSkinBytes)
                return null;
            var bytes = await ReadBoundedAsync(imageResponse.Content, MaximumSkinBytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
                return null;

            using var skin = ImageSharpImage.Load<Rgba32>(bytes);
            var unit = skin.Width / 8;
            if (unit <= 0 || skin.Height < unit * 2 || skin.Width < unit * 6)
                return null;
            using var face = skin.Clone(context => context.Crop(new ImageSharpRectangle(unit, unit, unit, unit)));
            using var overlay = skin.Clone(context => context.Crop(new ImageSharpRectangle(unit * 5, unit, unit, unit)));
            face.Mutate(context => context
                .DrawImage(overlay, new ImageSharpPoint(0, 0), 1f)
                .Resize(new ResizeOptions { Size = new ImageSharpSize(32, 32), Sampler = KnownResamplers.NearestNeighbor }));
            await using var output = new MemoryStream();
            await face.SaveAsync(output, new PngEncoder(), cancellationToken).ConfigureAwait(false);
            return $"data:image/png;base64,{Convert.ToBase64String(output.ToArray())}";
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                           System.Text.Json.JsonException or FormatException or IOException or
                                           ArgumentException or InvalidOperationException or NotSupportedException or UnknownImageFormatException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return output.Length == 0 ? null : output.ToArray();
            if (output.Length + read > maximum)
                return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (ownsClient)
            client.Dispose();
    }
}
