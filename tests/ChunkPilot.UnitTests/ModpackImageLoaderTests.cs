using System.Net;
using System.Net.Http;
using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ChunkPilot.UnitTests;

public sealed class ModpackImageLoaderTests
{
    [Fact]
    public async Task Same_url_is_coalesced_and_one_waiter_cannot_cancel_another()
    {
        var entered = NewSignal();
        var release = NewSignal();
        var requests = 0;
        var png = Png(32, 32);
        using var client = new HttpClient(new AsyncHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return Response(png);
        }));
        using var loader = new ModpackImageLoader(client);
        using var firstCancellation = new CancellationTokenSource();
        var uri = new Uri("https://cdn.modrinth.com/data/shared/icon.png");

        var first = loader.LoadAsync(CatalogProvider.Modrinth, uri, firstCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = loader.LoadAsync(CatalogProvider.Modrinth, uri, CancellationToken.None);
        firstCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(1, Volatile.Read(ref requests));
        release.TrySetResult(true);
        var dataUrl = await second;

        Assert.StartsWith("data:image/png;base64,", dataUrl, StringComparison.Ordinal);
        Assert.Equal(dataUrl, await loader.LoadAsync(CatalogProvider.Modrinth, uri, CancellationToken.None));
        Assert.Equal(1, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task Last_waiter_cancellation_reaches_the_underlying_http_request()
    {
        var entered = NewSignal();
        var cancelled = NewSignal();
        using var client = new HttpClient(new AsyncHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Observe cancellation of the actual HTTP operation, not a registration that its
                // unwinding continuation can dispose before the cancellation callback is visited.
                cancelled.TrySetResult(true);
                throw;
            }
            return Response(Png(8, 8));
        }));
        using var loader = new ModpackImageLoader(client);
        using var cancellation = new CancellationTokenSource();

        var load = loader.LoadAsync(CatalogProvider.Modrinth,
            new Uri("https://cdn.modrinth.com/data/cancel/icon.png"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Distinct_fetch_and_decode_pipelines_are_limited_to_four()
    {
        var fourEntered = NewSignal();
        var release = NewSignal();
        var active = 0;
        var maximum = 0;
        var requests = 0;
        var png = Png(16, 16);
        using var client = new HttpClient(new AsyncHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requests);
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            if (current == ModpackImageLoader.MaximumConcurrentLoads)
                fourEntered.TrySetResult(true);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return Response(png);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }));
        using var loader = new ModpackImageLoader(client);

        var loads = Enumerable.Range(0, 8)
            .Select(index => loader.LoadAsync(CatalogProvider.Modrinth,
                new Uri($"https://cdn.modrinth.com/data/concurrency-{index}/icon.png"),
                CancellationToken.None))
            .ToArray();
        await fourEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ModpackImageLoader.MaximumConcurrentLoads, Volatile.Read(ref requests));
        Assert.Equal(ModpackImageLoader.MaximumConcurrentLoads, Volatile.Read(ref maximum));
        release.TrySetResult(true);
        await Task.WhenAll(loads);

        Assert.Equal(8, Volatile.Read(ref requests));
        Assert.Equal(ModpackImageLoader.MaximumConcurrentLoads, Volatile.Read(ref maximum));
    }

    [Fact]
    public async Task Image_over_the_thumbnail_pixel_budget_is_rejected_before_decode()
    {
        var oversized = Png(2001, 2000);
        Assert.True(oversized.Length < ModpackImageLoader.MaximumEncodedBytes);
        using var client = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(Response(oversized))));
        using var loader = new ModpackImageLoader(client);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => loader.LoadAsync(
            CatalogProvider.Modrinth,
            new Uri("https://cdn.modrinth.com/data/oversized/icon.png"),
            CancellationToken.None));

        Assert.Contains("dimensions", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Animated_catalog_artwork_decodes_only_one_preview_frame()
    {
        using var animated = new Image<Rgba32>(24, 24, new Rgba32(45, 60, 90, 255));
        using var second = new Image<Rgba32>(24, 24, new Rgba32(90, 60, 45, 255));
        animated.Frames.AddFrame(second.Frames.RootFrame);
        using var encoded = new MemoryStream();
        animated.Save(encoded, new PngEncoder());
        using var decodedInput = Image.Load(encoded.ToArray());
        Assert.Equal(2, decodedInput.Frames.Count);
        using var client = new HttpClient(new AsyncHandler((_, _) => Task.FromResult(Response(encoded.ToArray()))));
        using var loader = new ModpackImageLoader(client);

        var result = await loader.LoadAsync(CatalogProvider.Modrinth,
            new Uri("https://cdn.modrinth.com/data/animated/icon.png"), CancellationToken.None);

        Assert.NotNull(result);
        using var preview = Image.Load(Convert.FromBase64String(result[(result.IndexOf(',') + 1)..]));
        Assert.Single(preview.Frames);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, candidate, observed);
            if (previous == observed)
                return;
            observed = previous;
        }
    }

    private static byte[] Png(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(45, 60, 90, 255));
        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        return output.ToArray();
    }

    private static HttpResponseMessage Response(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class AsyncHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await send(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }
}
