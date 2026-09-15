using System.Security.Cryptography;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ChunkPilot.UnitTests;

public sealed class ServerIconIdentitySafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-icon-identity-" + Guid.NewGuid().ToString("N"));
    private readonly AppDataPaths paths;
    private readonly ServerDefinition server;
    private readonly string source;
    private string Target => Path.Combine(server.RootPath, "server-icon.png");

    public ServerIconIdentitySafetyTests()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "data"));
        server = new ServerDefinition { RootPath = Path.Combine(root, "server") };
        Directory.CreateDirectory(server.RootPath);
        source = Path.Combine(root, "source.png");
        using var image = new Image<Rgba32>(64, 64, new Rgba32(30, 60, 90));
        image.SaveAsPng(source);
    }

    [Fact]
    public async Task Exact_expected_hash_replaces_with_recovery_and_absence_is_explicit()
    {
        var service = new ServerIconService(paths);
        await service.ConvertAndInstallIfUnchangedAsync(server, source, "");
        var old = await File.ReadAllBytesAsync(Target);
        var expected = Convert.ToHexString(SHA256.HashData(old));
        await service.ConvertAndInstallIfUnchangedAsync(server, source, expected);
        var recovery = Assert.Single(Directory.GetFiles(paths.Recovery, "*.png", SearchOption.AllDirectories));
        Assert.Equal(old, await File.ReadAllBytesAsync(recovery));
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAndInstallIfUnchangedAsync(server, source, ""));
        Assert.Equal(old, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task External_replacement_after_prepare_is_preserved_at_agent_boundary()
    {
        var service = new ServerIconService(paths);
        await service.ConvertAndInstallIfUnchangedAsync(server, source, "");
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Target)));
        const string external = "externally replaced icon";
        await File.WriteAllTextAsync(Target, external);
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAndInstallIfUnchangedAsync(server, source, expected));
        Assert.Equal(external, await File.ReadAllTextAsync(Target));
        Assert.False(Directory.Exists(paths.Recovery));
    }

    [Fact]
    public async Task Concurrent_installs_with_one_empty_expectation_have_only_one_winner()
    {
        var locks = new CanonicalPathLockManager();
        var first = new ServerIconService(paths, locks);
        var second = new ServerIconService(paths, locks);
        var outcomes = await Task.WhenAll(AttemptAsync(first), AttemptAsync(second));
        Assert.Single(outcomes, result => result);
        using var actual = Image.Load(Target);
        Assert.Equal(64, actual.Width);
        Assert.False(Directory.Exists(paths.Recovery));

        async Task<bool> AttemptAsync(ServerIconService service)
        {
            try { await service.ConvertAndInstallIfUnchangedAsync(server, source, ""); return true; }
            catch (IOException) { return false; }
        }
    }

    [Fact]
    public async Task Removed_target_invalid_identity_and_cancelled_write_do_not_create_an_icon()
    {
        var service = new ServerIconService(paths);
        await Assert.ThrowsAsync<IOException>(() => service.ConvertAndInstallIfUnchangedAsync(server, source, new string('a', 64)));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ConvertAndInstallIfUnchangedAsync(server, source, "not-a-hash"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ConvertAndInstallIfUnchangedAsync(
            server, source, "", cancellationToken: cancelled.Token));
        Assert.False(File.Exists(Target));
    }

    public void Dispose() => Directory.Delete(root, true);
}
