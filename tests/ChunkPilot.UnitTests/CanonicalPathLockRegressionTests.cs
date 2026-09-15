using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CanonicalPathLockRegressionTests
{
    [Fact]
    public async Task Racing_reservations_never_split_one_path_lock_and_dispose_is_idempotent()
    {
        var manager = new CanonicalPathLockManager();
        var path = Path.Combine(Path.GetTempPath(), "ChunkPilot-lock-" + Guid.NewGuid().ToString("N"));
        var active = 0;
        var maximum = 0;
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            for (var iteration = 0; iteration < 80; iteration++)
            {
                var lease = await manager.AcquireAsync(path);
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref maximum, Math.Max(Volatile.Read(ref maximum), current));
                await Task.Yield();
                Interlocked.Decrement(ref active);
                await lease.DisposeAsync();
                await lease.DisposeAsync();
            }
        }));
        Assert.Equal(1, maximum);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.AcquireAsync(path, cancelled.Token));
        await using var recovered = await manager.AcquireAsync(path);
    }

    [Fact]
    public async Task Strict_file_writer_rejects_deleted_loaded_target()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var files = new SafeFileService(new AppDataPaths(Path.Combine(root, "data")));
            var path = Path.Combine(root, "server.properties");
            await File.WriteAllTextAsync(path, "motd=original\n");
            var loaded = await files.ReadTextAsync(root, "server.properties");
            File.Delete(path);
            await Assert.ThrowsAsync<IOException>(() => files.WriteTextAtomicIfUnchangedAsync(root,
                loaded with { Content = "motd=changed\n" }));
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
