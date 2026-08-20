using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ServerDeletionSafetyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-deletion-" + Guid.NewGuid().ToString("N"));

    public ServerDeletionSafetyTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Durable_marker_proves_only_the_exact_managed_server()
    {
        var serverId = Guid.NewGuid();
        var managedRoot = Path.Combine(root, "managed");

        await ManagedInstanceOwnershipMarker.WriteAsync(managedRoot, serverId, CancellationToken.None);

        Assert.True(ManagedInstanceOwnershipMarker.Proves(managedRoot, serverId));
        Assert.False(ManagedInstanceOwnershipMarker.Proves(managedRoot, Guid.NewGuid()));
        await File.WriteAllTextAsync(ManagedInstanceOwnershipMarker.PathIn(managedRoot), "{ not valid json");
        Assert.False(ManagedInstanceOwnershipMarker.Proves(managedRoot, serverId));
    }

    [Fact]
    public async Task Store_deletion_is_transactional_and_removes_actionable_server_state()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var serverId = Guid.NewGuid();
        await store.UpsertServerAsync(new ServerDefinition
        {
            Id = serverId,
            Name = "Delete fixture",
            RootPath = Path.Combine(paths.ManagedServers, serverId.ToString("N")),
            ManagedInstanceRoot = paths.ManagedServers,
            IsManaged = true
        });
        await store.UpsertScheduleAsync(new ScheduleEntry
        {
            ServerId = serverId,
            Name = "Must not survive",
            Action = ScheduledAction.Restart,
            Kind = ScheduleKind.Interval
        });
        await store.UpsertBackupAsync(new BackupRecord
        {
            ServerId = serverId,
            ArchivePath = Path.Combine(paths.Backups, "fixture.zip"),
            ManifestPath = Path.Combine(paths.Backups, "fixture.json")
        });

        await store.DeleteServerAsync(serverId);

        Assert.Empty(await store.GetServersAsync());
        Assert.DoesNotContain(await store.GetSchedulesAsync(), item => item.ServerId == serverId);
        Assert.Empty(await store.GetBackupsAsync(serverId));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
