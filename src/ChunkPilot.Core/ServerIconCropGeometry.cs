namespace ChunkPilot.Core;

/// <summary>A pixel-exact square crop shared by the WPF preview and the Agent output pipeline.</summary>
public readonly record struct ServerIconPixelCrop(int X, int Y, int Size)
{
    public static ServerIconPixelCrop FromNormalized(
        int imageWidth,
        int imageHeight,
        double cropX,
        double cropY,
        double cropSize)
    {
        if (imageWidth < 1 || imageHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(imageWidth), "An icon source must contain pixels.");

        var normalizedSize = Math.Clamp(cropSize, 0.08, 1);
        var size = Math.Clamp(
            (int)Math.Round(Math.Min(imageWidth, imageHeight) * normalizedSize),
            1,
            Math.Min(imageWidth, imageHeight));
        var availableX = Math.Max(0, imageWidth - size);
        var availableY = Math.Max(0, imageHeight - size);
        var x = (int)Math.Round(Math.Clamp(cropX, 0, 1) * availableX);
        var y = (int)Math.Round(Math.Clamp(cropY, 0, 1) * availableY);
        return new ServerIconPixelCrop(x, y, size);
    }
}
