using ChunkPilot.App;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using ChunkPilot.UnitTests.DesignSystem;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace ChunkPilot.UnitTests;

public sealed class VanillaCreationIconSweepTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-icon-sweep-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Repeated_A_B_C_replacements_finalize_exact_icons_and_release_every_source()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"));
        paths.EnsureCreated();
        var serverRoot = Path.Combine(root, "server");
        Directory.CreateDirectory(serverRoot);
        var server = new ServerDefinition { Id = Guid.NewGuid(), RootPath = serverRoot };
        var service = new ServerIconService(paths);

        foreach (var (name, colour) in new[]
                 {
                     ("A", Color.Red), ("B", Color.Green), ("C", Color.Blue)
                 })
        {
            var source = await WriteImageAsync(name + ".webp", colour, 150, 90);
            var installed = await service.ConvertAndInstallAsync(server, source, 0.5, 0.5, 0.75);

            using var output = await Image.LoadAsync<Rgba32>(installed);
            Assert.Equal(64, output.Width);
            Assert.Equal(64, output.Height);
            Assert.Equal(colour.ToPixel<Rgba32>(), output[32, 32]);

            // Neither ImageSharp nor the WPF preview may retain the original source.
            File.Move(source, source + ".moved");
        }

        Assert.Equal(3, service.ListLibrary().Count);
        Assert.Empty(Directory.EnumerateFiles(serverRoot, ".server-icon.*.tmp"));

        // Reopen the library and reuse the same saved source twice. Content addressing must avoid
        // duplicates, and a preview or prior conversion must not hold the source open.
        var reopened = new ServerIconService(paths);
        var saved = reopened.ListLibrary()[0];
        await reopened.ConvertAndInstallAsync(server, saved.Path, saveToLibrary: false);
        await reopened.ConvertAndInstallAsync(server, saved.Path, saveToLibrary: false);
        Assert.Equal(3, reopened.ListLibrary().Count);

        var beforeCancel = await File.ReadAllBytesAsync(Path.Combine(serverRoot, "server-icon.png"));
        var cancelledSource = await WriteImageAsync("cancelled.png", Color.Yellow, 80, 80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reopened.ConvertAndInstallAsync(
            server, cancelledSource, cancellationToken: new CancellationToken(true)));
        Assert.Equal(beforeCancel, await File.ReadAllBytesAsync(Path.Combine(serverRoot, "server-icon.png")));
    }

    [Fact]
    public async Task A_detached_WPF_preview_does_not_lock_the_server_or_library_file()
    {
        var image = await WriteImageAsync("preview.png", Color.Purple, 64, 64);
        var preview = ServerIconImageLoader.LoadDetached(image);
        Assert.NotNull(preview);

        var replacement = await WriteImageAsync("replacement.png", Color.Orange, 64, 64);
        File.Move(replacement, image, overwrite: true);
        File.Delete(image);
        Assert.False(File.Exists(image));
        GC.KeepAlive(preview);
    }

    [Fact]
    public async Task Failed_finalization_keeps_the_previous_icon_and_publishes_no_library_record()
    {
        var paths = new AppDataPaths(Path.Combine(root, "failure-data"));
        paths.EnsureCreated();
        var serverRoot = Path.Combine(root, "failure-server");
        Directory.CreateDirectory(serverRoot);
        var target = await WriteImageAsync(Path.Combine("failure-server", "server-icon.png"), Color.Red, 64, 64);
        var source = await WriteImageAsync("new.png", Color.Blue, 100, 100);
        var service = new ServerIconService(paths);

        await using (File.Open(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => service.ConvertAndInstallAsync(
                new ServerDefinition { Id = Guid.NewGuid(), RootPath = serverRoot }, source));
        }

        using var unchanged = await Image.LoadAsync<Rgba32>(target);
        Assert.Equal(Color.Red.ToPixel<Rgba32>(), unchanged[32, 32]);
        Assert.Empty(service.ListLibrary());
        Assert.Empty(Directory.EnumerateFiles(serverRoot, ".server-icon.*.tmp"));
        Assert.Empty(Directory.EnumerateFiles(paths.ServerIcons, ".icon-*.tmp"));
    }

    [Fact]
    public void Crop_geometry_is_clamped_and_pixel_exact_for_preview_and_output()
    {
        Assert.Equal(new ServerIconPixelCrop(200, 0, 200),
            ServerIconPixelCrop.FromNormalized(400, 200, 1, 0, 1));
        Assert.Equal(new ServerIconPixelCrop(300, 100, 100),
            ServerIconPixelCrop.FromNormalized(400, 200, 1, 1, 0.5));
        Assert.Equal(new ServerIconPixelCrop(0, 0, 8),
            ServerIconPixelCrop.FromNormalized(100, 100, -5, -5, 0));
    }

    [Fact]
    public void Crop_and_connectivity_surfaces_expose_the_complete_keyboard_and_beginner_paths()
    {
        var app = DesignSystemFiles.AppProjectDirectory;
        var crop = File.ReadAllText(Path.Combine(app, "ServerIconCropWindow.xaml"));
        var access = File.ReadAllText(Path.Combine(app, "Pages", "ServerAccessPage.xaml"));
        var overview = File.ReadAllText(Path.Combine(app, "Pages", "ServerOverviewPage.xaml"));

        Assert.Contains("64 x 64 preview", crop, StringComparison.Ordinal);
        Assert.Contains("Content=\"Fit\"", crop, StringComparison.Ordinal);
        Assert.Contains("Content=\"Reset\"", crop, StringComparison.Ordinal);
        Assert.Contains("Choose another image", crop, StringComparison.Ordinal);
        Assert.Contains("IsCancel=\"True\"", crop, StringComparison.Ordinal);
        Assert.Contains("IsDefault=\"True\"", crop, StringComparison.Ordinal);
        Assert.DoesNotContain("Source=\"{Binding Path}\"", crop, StringComparison.Ordinal);

        Assert.True(access.IndexOf("Header=\"Networking\"", StringComparison.Ordinal) <
                    access.IndexOf("Header=\"Players\"", StringComparison.Ordinal));
        Assert.Contains("NetworkChoiceSummary", access, StringComparison.Ordinal);
        Assert.Contains("OpenOverviewCommand", access, StringComparison.Ordinal);
        Assert.Contains("Content=\"Manage connectivity\"", overview, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding OpenAccessCommand}\"", overview, StringComparison.Ordinal);
        Assert.Contains("IsExpanded=\"False\"", overview, StringComparison.Ordinal);
    }

    private async Task<string> WriteImageAsync(string relativePath, Color colour, int width, int height)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new Image<Rgba32>(width, height, colour.ToPixel<Rgba32>());
        await image.SaveAsync(path, new PngEncoder());
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
