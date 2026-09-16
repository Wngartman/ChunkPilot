using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class HostStatisticsSamplingTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-host-sampling-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Host_sample_leaves_folder_usage_unknown_without_creating_missing_roots()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));

        var sample = new ProcessStatisticsProvider().SampleHost(paths);

        Assert.Null(sample.ManagedServerStorageBytes);
        Assert.Null(sample.BackupStorageBytes);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Cold_and_warm_host_samples_do_not_account_for_populated_synthetic_trees()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));
        var world = Path.Combine(paths.ManagedServers, "fixture", "world", "region");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(paths.Backups);
        var worldFile = Path.Combine(world, "fixture.mca");
        var backupFile = Path.Combine(paths.Backups, "fixture.zip");
        byte[] worldBytes = [1, 2, 3, 4];
        byte[] backupBytes = [5, 6, 7];
        File.WriteAllBytes(worldFile, worldBytes);
        File.WriteAllBytes(backupFile, backupBytes);
        var provider = new ProcessStatisticsProvider();

        foreach (var sample in new[] { provider.SampleHost(paths), provider.SampleHost(paths) })
        {
            Assert.Null(sample.ManagedServerStorageBytes);
            Assert.Null(sample.BackupStorageBytes);
        }

        Assert.Equal(worldBytes, File.ReadAllBytes(worldFile));
        Assert.Equal(backupBytes, File.ReadAllBytes(backupFile));
    }

    [Fact]
    public void Storage_usage_keeps_legacy_json_names_and_numbers_but_defaults_to_unknown()
    {
        var unknown = JsonSerializer.Deserialize<HostSnapshot>("{}", ProtocolJson.Options)!;
        Assert.Null(unknown.ManagedServerStorageBytes);
        Assert.Null(unknown.BackupStorageBytes);

        var legacy = JsonSerializer.Deserialize<HostSnapshot>(
            """{"managedServerStorageBytes":123,"backupStorageBytes":456}""", ProtocolJson.Options)!;
        Assert.Equal(123L, legacy.ManagedServerStorageBytes);
        Assert.Equal(456L, legacy.BackupStorageBytes);
        var serialized = JsonSerializer.SerializeToElement(legacy, ProtocolJson.Options);
        Assert.Equal(123L, serialized.GetProperty("managedServerStorageBytes").GetInt64());
        Assert.Equal(456L, serialized.GetProperty("backupStorageBytes").GetInt64());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
