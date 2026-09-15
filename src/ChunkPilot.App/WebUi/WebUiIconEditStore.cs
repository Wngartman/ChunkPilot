using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Image = SixLabors.ImageSharp.Image;

namespace ChunkPilot.App.WebUi;

internal sealed record WebUiIconRecipe(
    double Zoom = 1, double PanX = 0, double PanY = 0, int Rotation = 0,
    double Brightness = 1, double Contrast = 1, double Saturation = 1);
internal sealed record WebUiIconSource(string Token, string SourceUrl, string FileName,
    WebUiIconRecipe Recipe, bool OriginalAvailable, string Detail);
internal sealed record WebUiPreparedIcon(Guid ServerId, byte[] Source, byte[] Output,
    WebUiIconRecipe Recipe, string ExpectedIconHash, string FileName, bool OriginalAvailable);

/// <summary>Bounded native edit sources. Opening/previewing never writes; only a successful icon save
/// stores one source/recipe per server. The exact current icon hash fences stale and external edits.</summary>
internal sealed class WebUiIconEditStore
{
    private const int MaximumSourceBytes = 384 * 1024;
    private const int MaximumRecordBytes = 600 * 1024;
    private readonly AppDataPaths paths;
    private readonly SafeFileService files;
    private readonly Dictionary<string, Selection> selections = new(StringComparer.Ordinal);

    public WebUiIconEditStore(string? dataRoot = null)
    {
        paths = new AppDataPaths(dataRoot ?? Environment.GetEnvironmentVariable("CHUNKPILOT_DATA_ROOT"));
        files = new SafeFileService(paths);
    }

    public WebUiIconSource Select(ServerDefinition server, byte[] source, string fileName)
    {
        ValidateSource(source);
        return Add(server, source, new(), Path.GetFileName(fileName), true, Hash(ReadCurrentIcon(server)),
            "A bounded original is preserved at up to 256 pixels. Edits do not change it.");
    }

    public WebUiIconSource OpenExisting(ServerDefinition server)
    {
        var current = ReadCurrentIcon(server);
        if (current.Length == 0) throw new InvalidDataException("This server has no saved icon. Choose an image first.");
        var hash = Hash(current);
        var recordPath = RecordPath(server.Id);
        try
        {
            if (File.Exists(recordPath) && new FileInfo(recordPath).Length <= MaximumRecordBytes)
            {
                var record = JsonSerializer.Deserialize<SavedRecord>(ReadBounded(recordPath, MaximumRecordBytes), WebUiProtocol.Json);
                if (record is { Version: 1 } && record.ServerId == server.Id && record.OutputHash == hash)
                {
                    var source = Convert.FromBase64String(record.SourceBase64);
                    ValidateSource(source);
                    ValidateRecipe(record.Recipe);
                    return Add(server, source, record.Recipe, record.FileName, record.OriginalAvailable, hash,
                        record.OriginalAvailable ? "Editing from the preserved bounded original; earlier effects are not compounded."
                            : "Only the existing 64 × 64 image was available. Its original pixels cannot be recovered.");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or FormatException or InvalidDataException or ArgumentException)
        {
            // Missing, corrupt or externally superseded edit metadata never replaces the real icon.
        }
        _ = WebUiIconPayload.Decode64Png(Convert.ToBase64String(current));
        return Add(server, current, new(), "Current server icon", false, hash,
            "Only the existing 64 × 64 image is available. Enlarging or recropping cannot recover discarded pixels.");
    }

    public WebUiPreparedIcon Prepare(ServerDefinition server, string token, WebUiIconRecipe recipe)
    {
        ValidateRecipe(recipe);
        if (!selections.TryGetValue(token, out var selected) || selected.ServerId != server.Id ||
            selected.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidDataException("The icon edit expired or belongs to another server. Reopen the editor.");
        if (Hash(ReadCurrentIcon(server)) != selected.ExpectedIconHash)
            throw new IOException("The server icon changed after this editor opened. Reopen it before saving.");
        return new(server.Id, selected.Source, Render(selected.Source, recipe), recipe,
            selected.ExpectedIconHash, selected.FileName, selected.OriginalAvailable);
    }

    public async Task SaveRecipeAsync(ServerDefinition server, WebUiPreparedIcon prepared)
    {
        if (prepared.ServerId != server.Id) throw new InvalidDataException("The icon edit belongs to another server.");
        var current = ReadCurrentIcon(server);
        // The existing Agent encoder can produce different PNG bytes. Compare decoded pixels before
        // associating its exact output with our original, so an outside replacement cannot inherit it.
        ValidateSource(current);
        using var actual = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, current);
        using var expected = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, prepared.Output);
        var actualPixels = new byte[64 * 64 * 4];
        var expectedPixels = new byte[actualPixels.Length];
        if (actual.Width != 64 || actual.Height != 64) throw new IOException("The saved icon changed before its edit recipe could be recorded.");
        actual.CopyPixelDataTo(actualPixels);
        expected.CopyPixelDataTo(expectedPixels);
        if (!actualPixels.AsSpan().SequenceEqual(expectedPixels))
            throw new IOException("The saved icon changed before its edit recipe could be recorded.");
        var target = RecordPath(server.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var record = new SavedRecord(1, server.Id, Hash(current), Convert.ToBase64String(prepared.Source),
            prepared.Recipe, prepared.FileName, prepared.OriginalAvailable);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(record, WebUiProtocol.Json)).ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static byte[] Render(byte[] source, WebUiIconRecipe recipe)
    {
        ValidateSource(source);
        ValidateRecipe(recipe);
        using var image = Image.Load<Rgba32>(new DecoderOptions { MaxFrames = 1 }, source);
        var rotation = ((recipe.Rotation % 360) + 360) % 360;
        image.Mutate(context => context.Rotate(rotation));
        var crop = ServerIconPixelCrop.FromNormalized(image.Width, image.Height,
            (recipe.PanX + 1) / 2, (recipe.PanY + 1) / 2, 1 / recipe.Zoom);
        image.Mutate(context => context.Crop(new SixLabors.ImageSharp.Rectangle(crop.X, crop.Y, crop.Size, crop.Size))
            .Brightness((float)recipe.Brightness).Contrast((float)recipe.Contrast).Saturate((float)recipe.Saturation)
            .Resize(new ResizeOptions { Size = new SixLabors.ImageSharp.Size(64, 64), Mode = SixLabors.ImageSharp.Processing.ResizeMode.Stretch, Sampler = KnownResamplers.NearestNeighbor }));
        image.Metadata.ExifProfile = null;
        image.Metadata.IccProfile = null;
        image.Metadata.XmpProfile = null;
        using var output = new MemoryStream();
        image.Save(output, new PngEncoder());
        return output.ToArray();
    }

    private WebUiIconSource Add(ServerDefinition server, byte[] source, WebUiIconRecipe recipe,
        string fileName, bool originalAvailable, string expectedIconHash, string detail)
    {
        foreach (var key in selections.Where(pair => pair.Value.ExpiresAt <= DateTimeOffset.UtcNow)
                     .Select(pair => pair.Key).ToArray()) selections.Remove(key);
        while (selections.Count >= 8) selections.Remove(selections.Keys.First());
        var token = Guid.NewGuid().ToString("N");
        selections[token] = new(server.Id, source.ToArray(), recipe, expectedIconHash, fileName,
            originalAvailable, DateTimeOffset.UtcNow.AddMinutes(30));
        return new(token, $"data:image/png;base64,{Convert.ToBase64String(source)}", fileName, recipe, originalAvailable, detail);
    }

    private string RecordPath(Guid serverId) => files.ResolveWithinRoot(paths.Root,
        Path.Combine("ServerIcons", "Edits", $"{serverId:N}.json"), mustExist: false);

    private byte[] ReadCurrentIcon(ServerDefinition server)
    {
        var target = files.ResolveWithinRoot(server.RootPath, "server-icon.png", mustExist: false);
        if (!File.Exists(target)) return [];
        return ReadBounded(target, WebUiIconPayload.MaximumDecodedBytes);
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("The saved icon or edit record exceeds its supported size.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new IOException("The icon changed while it was being read.");
        return bytes;
    }

    private static void ValidateSource(byte[] source)
    {
        if (source.Length is 0 or > MaximumSourceBytes) throw new InvalidDataException("The bounded icon source is invalid.");
        using var stream = new MemoryStream(source, writable: false);
        var format = Image.DetectFormat(stream);
        if (!string.Equals(format.Name, "PNG", StringComparison.Ordinal)) throw new InvalidDataException("The icon source must be a PNG.");
        stream.Position = 0;
        var info = Image.Identify(new DecoderOptions { MaxFrames = 1 }, stream);
        if (info.Width is <= 0 or > 256 || info.Height is <= 0 or > 256)
            throw new InvalidDataException("The icon edit source must be bounded to 256 pixels.");
    }

    private static void ValidateRecipe(WebUiIconRecipe? recipe)
    {
        if (recipe is null || !double.IsFinite(recipe.Zoom) || recipe.Zoom is < 1 or > 8 ||
            !double.IsFinite(recipe.PanX) || recipe.PanX is < -1 or > 1 ||
            !double.IsFinite(recipe.PanY) || recipe.PanY is < -1 or > 1 || recipe.Rotation % 90 != 0 ||
            !double.IsFinite(recipe.Brightness) || recipe.Brightness is < .5 or > 1.5 ||
            !double.IsFinite(recipe.Contrast) || recipe.Contrast is < .5 or > 1.5 ||
            !double.IsFinite(recipe.Saturation) || recipe.Saturation is < 0 or > 2)
            throw new InvalidDataException("The icon adjustments are outside their supported range.");
    }

    private static string Hash(byte[] bytes) => bytes.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Selection(Guid ServerId, byte[] Source, WebUiIconRecipe Recipe, string ExpectedIconHash,
        string FileName, bool OriginalAvailable, DateTimeOffset ExpiresAt);
    private sealed record SavedRecord(int Version, Guid ServerId, string OutputHash, string SourceBase64,
        WebUiIconRecipe Recipe, string FileName, bool OriginalAvailable);
}
