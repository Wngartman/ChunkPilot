using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// Displays the face from a player's official Minecraft skin. Missing, offline or invalid profiles
/// simply leave the source empty so the view's local fallback icon remains visible.
/// </summary>
public sealed class AppPlayerHead : System.Windows.Controls.Image
{
    private const int MaximumSkinBytes = 1_048_576;
    private static readonly HttpClient Client = CreateClient();
    private static readonly ConcurrentDictionary<Guid, Lazy<Task<ImageSource?>>> Cache = new();

    public static readonly DependencyProperty UuidProperty = DependencyProperty.Register(
        nameof(Uuid), typeof(Guid?), typeof(AppPlayerHead),
        new PropertyMetadata(null, OnUuidChanged));

    public Guid? Uuid
    {
        get => (Guid?)GetValue(UuidProperty);
        set => SetValue(UuidProperty, value);
    }

    public AppPlayerHead()
    {
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChunkPilot/1.0 (local Minecraft server manager)");
        return client;
    }

    private static async void OnUuidChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (AppPlayerHead)dependencyObject;
        control.Source = null;
        if (args.NewValue is not Guid uuid)
            return;

        try
        {
            var image = await Cache.GetOrAdd(uuid,
                key => new Lazy<Task<ImageSource?>>(() => DownloadAsync(key),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value.ConfigureAwait(true);
            if (control.Uuid == uuid)
                control.Source = image;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or
                                           JsonException or FormatException or IOException or ArgumentException or
                                           InvalidOperationException or NotSupportedException)
        {
            Cache.TryRemove(uuid, out _);
            // The fallback icon is intentional; skin availability never blocks player moderation.
        }
    }

    private static async Task<ImageSource?> DownloadAsync(Guid uuid)
    {
        var profileUri = new Uri(
            $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid:N}", UriKind.Absolute);
        using var profileResponse = await Client.GetAsync(profileUri, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        if (!profileResponse.IsSuccessStatusCode)
            return null;
        var profileJson = await profileResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!MinecraftSkinProfileParser.TryGetTextureUri(profileJson, out var textureUri))
            return null;

        using var imageResponse = await Client.GetAsync(textureUri, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        if (!imageResponse.IsSuccessStatusCode || imageResponse.Content.Headers.ContentLength > MaximumSkinBytes)
            return null;
        var bytes = await imageResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        if (bytes.Length == 0 || bytes.Length > MaximumSkinBytes)
            return null;

        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        if (bitmap.PixelWidth < 8 || bitmap.PixelHeight < 8)
            return null;

        var unit = bitmap.PixelWidth / 8;
        if (unit <= 0 || unit * 2 > bitmap.PixelHeight)
            return null;
        var face = new CroppedBitmap(bitmap, new Int32Rect(unit, unit, unit, unit));
        face.Freeze();
        return face;
    }
}

/// <summary>Small, testable parser for Mojang's signed textures property.</summary>
internal static class MinecraftSkinProfileParser
{
    public static bool TryGetTextureUri(string profileJson, out Uri textureUri)
    {
        textureUri = null!;
        using var profile = JsonDocument.Parse(profileJson);
        if (!profile.RootElement.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Array)
            return false;

        var encoded = properties.EnumerateArray()
            .FirstOrDefault(property => property.TryGetProperty("name", out var name) &&
                                        name.GetString() == "textures");
        if (encoded.ValueKind == JsonValueKind.Undefined ||
            !encoded.TryGetProperty("value", out var valueElement))
            return false;
        var value = valueElement.GetString();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        using var textures = JsonDocument.Parse(decoded);
        if (!textures.RootElement.TryGetProperty("textures", out var textureSet) ||
            !textureSet.TryGetProperty("SKIN", out var skin) ||
            !skin.TryGetProperty("url", out var urlElement) ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var parsed))
            return false;

        // Mojang still emits an http URL in some signed profiles; fetch the same official texture
        // over TLS. No arbitrary host from profile data is ever requested.
        if (!string.Equals(parsed.Host, "textures.minecraft.net", StringComparison.OrdinalIgnoreCase))
            return false;
        var builder = new UriBuilder(parsed) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        textureUri = builder.Uri;
        return true;
    }
}
