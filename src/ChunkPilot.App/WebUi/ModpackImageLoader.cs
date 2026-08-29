using System.Net.Http;
using ChunkPilot.Core;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpResizeMode = SixLabors.ImageSharp.Processing.ResizeMode;
using ImageSharpSize = SixLabors.ImageSharp.Size;

namespace ChunkPilot.App.WebUi;

/// <summary>
/// Keeps remote catalog artwork outside the renderer while bounding both network and decode work.
/// Requests for the same approved URL share one operation; each caller retains independent cancellation.
/// </summary>
internal sealed class ModpackImageLoader : IDisposable
{
    internal const int MaximumConcurrentLoads = 4;
    internal const int MaximumEncodedBytes = 512 * 1024;
    internal const long MaximumDecodedPixels = 4_000_000;
    private const int MaximumDimension = 4096;
    private const int MaximumCachedImages = 32;

    private sealed class SharedLoad(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task<string>? Task { get; set; }
        public int Waiters { get; set; }
    }

    private readonly HttpClient client;
    private readonly bool disposeClient;
    private readonly SemaphoreSlim loadGate = new(MaximumConcurrentLoads, MaximumConcurrentLoads);
    private readonly object stateGate = new();
    private readonly Dictionary<string, string> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SharedLoad> activeLoads = new(StringComparer.Ordinal);
    private bool disposed;

    public ModpackImageLoader(HttpClient client, bool disposeClient = false)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.disposeClient = disposeClient;
    }

    public Task<string?> LoadAsync(CatalogProvider provider, Uri uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsApprovedUri(provider, uri))
            return Task.FromResult<string?>(null);

        var key = uri.AbsoluteUri;
        SharedLoad shared;
        lock (stateGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cache.TryGetValue(key, out var cached))
                return Task.FromResult<string?>(cached);

            if (!activeLoads.TryGetValue(key, out shared!))
            {
                shared = new SharedLoad(new CancellationTokenSource());
                activeLoads.Add(key, shared);
                shared.Task = FetchAndDecodeAsync(key, provider, uri, shared.Cancellation.Token);
            }
            shared.Waiters++;
        }

        return AwaitSharedAsync(key, shared, cancellationToken);
    }

    private async Task<string?> AwaitSharedAsync(
        string key,
        SharedLoad shared,
        CancellationToken cancellationToken)
    {
        try
        {
            return await shared.Task!.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseWaiter(key, shared);
        }
    }

    private void ReleaseWaiter(string key, SharedLoad shared)
    {
        var cancel = false;
        var disposeNow = false;
        lock (stateGate)
        {
            if (shared.Waiters > 0)
                shared.Waiters--;
            if (shared.Waiters != 0 ||
                !activeLoads.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, shared))
                return;

            activeLoads.Remove(key);
            cancel = !shared.Task!.IsCompleted;
            disposeNow = !cancel;
        }

        if (cancel)
        {
            shared.Cancellation.Cancel();
            _ = shared.Task!.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                shared.Cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else if (disposeNow)
        {
            shared.Cancellation.Dispose();
        }
    }

    private async Task<string> FetchAndDecodeAsync(
        string key,
        CatalogProvider provider,
        Uri uri,
        CancellationToken cancellationToken)
    {
        await loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("Provider image redirects are not followed outside the approved boundary.");
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } final || !IsApprovedUri(provider, final))
                throw new InvalidDataException("The provider image left its approved HTTPS host boundary.");
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
            if (mediaType is not ("image/png" or "image/jpeg" or "image/webp"))
                throw new InvalidDataException("The provider image did not use a supported image format.");
            if (response.Content.Headers.ContentLength is > MaximumEncodedBytes)
                throw new InvalidDataException("The provider image exceeds ChunkPilot's 512 KB cache limit.");

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var raw = new MemoryStream();
            var buffer = new byte[32 * 1024];
            while (true)
            {
                var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;
                if (raw.Length + count > MaximumEncodedBytes)
                    throw new InvalidDataException("The provider image exceeds ChunkPilot's 512 KB cache limit.");
                await raw.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }

            raw.Position = 0;
            var info = await ImageSharpImage.IdentifyAsync(raw, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The provider image could not be decoded.");
            if (info.Width is <= 0 or > MaximumDimension || info.Height is <= 0 or > MaximumDimension ||
                (long)info.Width * info.Height > MaximumDecodedPixels)
                throw new InvalidDataException("The provider image dimensions exceed ChunkPilot's safe preview limit.");

            raw.Position = 0;
            using var image = await ImageSharpImage.LoadAsync<Rgba32>(raw, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            image.Mutate(context => context.Resize(new ResizeOptions
            {
                Size = new ImageSharpSize(160, 160),
                Mode = ImageSharpResizeMode.Max,
                Sampler = KnownResamplers.Lanczos3
            }));
            cancellationToken.ThrowIfCancellationRequested();
            using var encoded = new MemoryStream();
            await image.SaveAsync(encoded, new PngEncoder(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var dataUrl = $"data:image/png;base64,{Convert.ToBase64String(encoded.ToArray())}";

            lock (stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (cache.Count >= MaximumCachedImages)
                    cache.Remove(cache.Keys.First());
                cache[key] = dataUrl;
            }
            return dataUrl;
        }
        finally
        {
            loadGate.Release();
        }
    }

    internal static bool IsApprovedUri(CatalogProvider provider, Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort)
            return false;
        var host = uri.IdnHost.TrimEnd('.');
        return provider switch
        {
            CatalogProvider.Modrinth => host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase),
            CatalogProvider.CurseForge => host.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase) ||
                                          host.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    public void Dispose()
    {
        List<SharedLoad> active;
        lock (stateGate)
        {
            if (disposed)
                return;
            disposed = true;
            active = activeLoads.Values.Distinct().ToList();
            activeLoads.Clear();
            cache.Clear();
        }

        foreach (var shared in active)
            shared.Cancellation.Cancel();
        foreach (var shared in active)
        {
            _ = shared.Task!.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                shared.Cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        if (disposeClient)
            client.Dispose();
    }
}
