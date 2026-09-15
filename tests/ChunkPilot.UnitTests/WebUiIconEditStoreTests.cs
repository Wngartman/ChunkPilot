using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChunkPilot.UnitTests;

public sealed class WebUiIconEditStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-icon-edit-" + Guid.NewGuid().ToString("N"));
    private readonly ServerDefinition server;
    private readonly WebUiIconEditStore store;

    public WebUiIconEditStoreTests()
    {
        Directory.CreateDirectory(root);
        var serverRoot = Path.Combine(root, "server");
        Directory.CreateDirectory(serverRoot);
        server = new() { Id = Guid.NewGuid(), RootPath = serverRoot, WorkingDirectory = serverRoot,
            MinecraftVersion = "1.21.1", Ecosystem = ServerEcosystem.NeoForge };
        store = new(Path.Combine(root, "data"));
    }

    [Fact]
    public void Opening_and_preparing_are_read_only_and_cancel_has_nothing_to_restore()
    {
        var selected = store.Select(server, Source(), "source.png");
        _ = store.Prepare(server, selected.Token, new(Zoom: 2, Rotation: 90));
        Assert.Empty(Directory.GetFiles(server.RootPath));
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
    }

    [Fact]
    public async Task Reopening_three_edits_reuses_original_and_recipe_without_compounding_effects()
    {
        var source = Source();
        var selected = store.Select(server, source, "original.png");
        var recipe = new WebUiIconRecipe(Zoom: 2, PanX: .3, Rotation: 90, Brightness: 1.2, Contrast: .8, Saturation: 1.5);
        byte[]? first = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rendered = store.Prepare(server, selected.Token, recipe);
            first ??= rendered.Output;
            Assert.Equal(first, rendered.Output);
            File.WriteAllBytes(Path.Combine(server.RootPath, "server-icon.png"), rendered.Output);
            await store.SaveRecipeAsync(server, rendered);
            selected = store.OpenExisting(server);
            Assert.True(selected.OriginalAvailable);
            Assert.Equal(recipe, selected.Recipe);
            Assert.Equal(source, Convert.FromBase64String(selected.SourceUrl.Split(',')[1]));
        }
        Assert.Single(Directory.GetFiles(Path.Combine(root, "data", "ServerIcons", "Edits")));
    }

    [Fact]
    public void Missing_original_is_explicit_and_current_pixels_are_retained()
    {
        File.WriteAllBytes(Path.Combine(server.RootPath, "server-icon.png"), Source(64, 64));
        var selected = store.OpenExisting(server);
        Assert.False(selected.OriginalAvailable);
        Assert.Contains("64 × 64", selected.Detail);
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
    }

    [Fact]
    public void Stale_external_icon_changes_and_foreign_server_tokens_are_rejected()
    {
        var selected = store.Select(server, Source(), "source.png");
        Assert.Throws<InvalidDataException>(() => store.Prepare(server with { Id = Guid.NewGuid() }, selected.Token, new()));
        File.WriteAllBytes(Path.Combine(server.RootPath, "server-icon.png"), Source(64, 64));
        Assert.Throws<IOException>(() => store.Prepare(server, selected.Token, new()));
        Assert.False(Directory.Exists(Path.Combine(root, "data")));
    }

    [Fact]
    public void Cancelled_replacement_does_not_invalidate_an_earlier_staged_crop()
    {
        var original = store.Select(server, Source(), "first.png");
        _ = store.Select(server, Source(64, 64), "cancelled.png");
        Assert.NotEmpty(store.Prepare(server, original.Token, new()).Output);
    }

    [Fact]
    public async Task Externally_replaced_icon_does_not_inherit_a_previous_original()
    {
        var selected = store.Select(server, Source(), "first.png");
        var prepared = store.Prepare(server, selected.Token, new(Zoom: 2));
        File.WriteAllBytes(Path.Combine(server.RootPath, "server-icon.png"), prepared.Output);
        await store.SaveRecipeAsync(server, prepared);
        File.WriteAllBytes(Path.Combine(server.RootPath, "server-icon.png"), Source(64, 64));
        Assert.False(store.OpenExisting(server).OriginalAvailable);
    }

    [Fact]
    public void Native_output_is_exact_deterministic_and_preserves_transparency()
    {
        var source = Source();
        var output = WebUiIconEditStore.Render(source, new());
        Assert.Equal(output, WebUiIconEditStore.Render(source, new()));
        using var image = Image.Load<Rgba32>(output);
        Assert.Equal(64, image.Width);
        Assert.Equal(64, image.Height);
        Assert.Equal(0, image[0, 0].A);
        Assert.Equal(255, image[32, 32].A);
    }

    [Fact]
    public void Invalid_or_unbounded_sources_and_recipes_are_rejected_before_rendering()
    {
        Assert.Throws<InvalidDataException>(() => store.Select(server, Source(257, 10), "wide.png"));
        Assert.ThrowsAny<Exception>(() => store.Select(server, [1, 2, 3], "invalid.png"));
        Assert.Throws<InvalidDataException>(() => WebUiIconEditStore.Render(Source(), new(Zoom: double.NaN)));
        Assert.Throws<InvalidDataException>(() => WebUiIconEditStore.Render(Source(), new(Rotation: 12)));
        Assert.Throws<InvalidDataException>(() => WebUiIconEditStore.Render(Source(), new(Saturation: 3)));
    }

    private static byte[] Source(int width = 256, int height = 256)
    {
        using var image = new Image<Rgba32>(width, height);
        for (var y = height / 4; y < 3 * height / 4; y++)
            for (var x = width / 4; x < 3 * width / 4; x++) image[x, y] = new((byte)x, (byte)y, 64, 255);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
