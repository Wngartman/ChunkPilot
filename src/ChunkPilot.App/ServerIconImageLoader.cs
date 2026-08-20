using System.Windows.Media.Imaging;
using ChunkPilot.Core;
using SixLabors.ImageSharp.PixelFormats;

namespace ChunkPilot.App;

/// <summary>Decodes icon files into memory so WPF never retains a handle to a mutable PNG.</summary>
internal static class ServerIconImageLoader
{
    public static BitmapSource? LoadDetached(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var decoded = SixLabors.ImageSharp.Image.Load<Bgra32>(stream);
        var pixels = new byte[checked(decoded.Width * decoded.Height * 4)];
        decoded.CopyPixelDataTo(pixels);
        var bitmap = BitmapSource.Create(
            decoded.Width, decoded.Height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, decoded.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static BitmapSource? CreateCropPreview(
        BitmapSource? source,
        double cropX,
        double cropY,
        double cropSize)
    {
        if (source is null || source.PixelWidth < 1 || source.PixelHeight < 1)
            return null;
        var crop = ServerIconPixelCrop.FromNormalized(
            source.PixelWidth, source.PixelHeight, cropX, cropY, cropSize);
        var cropped = new CroppedBitmap(source, new Int32Rect(crop.X, crop.Y, crop.Size, crop.Size));
        var scaled = new TransformedBitmap(cropped,
            new System.Windows.Media.ScaleTransform(64d / crop.Size, 64d / crop.Size));
        scaled.Freeze();
        return scaled;
    }
}
