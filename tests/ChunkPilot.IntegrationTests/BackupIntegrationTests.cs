using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class BackupIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-backup-" + Guid.NewGuid().ToString("N"));
    private ChunkPilotStore store = null!;
    private AppDataPaths paths = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "appdata"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    [Fact(Timeout = 40_000)]
    public async Task Backup_manifest_verification_and_restore_preserve_unrelated_files()
    {
        var serverRoot = Path.Combine(root, "server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "world", "region"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "# keep\r\nmotd=before\r\n");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "level.dat"), "world-state");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "region", "r.0.0.mca"), "region-state");
        var definition = new ServerDefinition { Name = "Backup Test", RootPath = serverRoot };
        var service = new BackupService(paths, store);
        var profile = service.GetDefaultProfile(definition) with
        {
            Exclusions = ["logs/**", "*.lock"],
            MaximumCount = 3
        };
        var record = await service.CreateAsync(definition, profile);
        Assert.True(record.Verified, record.VerificationMessage);
        Assert.True(File.Exists(record.ArchivePath));
        Assert.True(File.Exists(record.ManifestPath));
        Assert.True(await service.VerifyAsync(record));

        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "motd=changed\r\n");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "unrelated.txt"), "do-not-remove");
        await service.RestoreAsync(definition, record);
        Assert.Contains("motd=before", await File.ReadAllTextAsync(Path.Combine(serverRoot, "server.properties")), StringComparison.Ordinal);
        Assert.Equal("do-not-remove", await File.ReadAllTextAsync(Path.Combine(serverRoot, "unrelated.txt")));
        Assert.Equal("world-state", await File.ReadAllTextAsync(Path.Combine(serverRoot, "world", "level.dat")));
    }

    [Fact(Timeout = 40_000)]
    public async Task Retention_removes_oldest_record_after_count_limit()
    {
        var serverRoot = Path.Combine(root, "retention-server");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "file.txt"), "data");
        var definition = new ServerDefinition { Name = "Retention", RootPath = serverRoot };
        var service = new BackupService(paths, store);
        var profile = service.GetDefaultProfile(definition) with { MaximumCount = 2, VerificationEnabled = false };
        _ = await service.CreateAsync(definition, profile);
        await Task.Delay(10);
        _ = await service.CreateAsync(definition, profile);
        await Task.Delay(10);
        _ = await service.CreateAsync(definition, profile);
        Assert.Equal(2, (await store.GetBackupsAsync(definition.Id)).Count);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
