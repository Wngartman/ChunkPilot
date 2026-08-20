using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace ChunkPilot.App.WebUi;

internal static class WebUiIconPayload
{
    internal const int MaximumBase64Characters = 256 * 1024;
    internal const int MaximumDecodedBytes = 192 * 1024;

    public static byte[] Decode64Png(string base64)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > MaximumBase64Characters)
            throw new InvalidDataException("The cropped icon payload is too large.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The cropped icon payload was not valid base64 data.", exception);
        }
        if (bytes.Length is 0 or > MaximumDecodedBytes)
            throw new InvalidDataException("The cropped icon payload is too large.");
        using var stream = new MemoryStream(bytes, writable: false);
        var format = ImageSharpImage.DetectFormat(stream);
        if (format is null || !string.Equals(format.Name, "PNG", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The cropped icon payload must be a PNG image.");
        stream.Position = 0;
        using var image = ImageSharpImage.Load(stream);
        if (image.Width != 64 || image.Height != 64)
            throw new InvalidDataException("Minecraft server icons must be exactly 64 x 64 pixels.");
        return bytes;
    }
}
