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
        var inspection = ManagedInstanceOwnershipMarker.Inspect(managedRoot, serverId);
        Assert.True(inspection.Proven);
        Assert.Equal("CreationTransaction", inspection.Marker!.OwnershipSource);
        Assert.False(ManagedInstanceOwnershipMarker.Proves(managedRoot, Guid.NewGuid()));
        await File.WriteAllTextAsync(ManagedInstanceOwnershipMarker.PathIn(managedRoot), "{ not valid json");
        Assert.False(ManagedInstanceOwnershipMarker.Proves(managedRoot, serverId));
        Assert.Contains("could not be verified", ManagedInstanceOwnershipMarker.Inspect(managedRoot, serverId).Detail);
    }

    [Fact]
    public async Task Marker_schema_one_from_an_older_build_remains_valid()
    {
        var serverId = Guid.NewGuid();
        var managedRoot = Path.Combine(root, "legacy-managed");
        Directory.CreateDirectory(managedRoot);
        await File.WriteAllTextAsync(ManagedInstanceOwnershipMarker.PathIn(managedRoot), $$"""
            {"schemaVersion":1,"serverId":"{{serverId:D}}","createdAt":"2026-01-01T00:00:00Z","product":"ChunkPilot"}
            """);

        var inspection = ManagedInstanceOwnershipMarker.Inspect(managedRoot, serverId);

        Assert.True(inspection.Proven);
        Assert.Equal("CreationTransaction", inspection.Marker!.OwnershipSource);
    }

    [Fact]
    public void Reconciliation_requires_every_exact_creation_and_path_fact()
    {
        Assert.True(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            true, true, true, true, false, true));
        Assert.False(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            false, true, true, true, false, true));
        Assert.False(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            true, false, true, true, false, true));
        Assert.False(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            true, true, false, true, false, true));
        Assert.False(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            true, true, true, false, false, true));
        Assert.False(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            true, true, true, true, true, true));
        Assert.False(ManagedOwnershipReconciliationPolicy.CanRestoreMissingMarker(
            true, true, true, true, false, false));
    }

    [Fact]
    public async Task Managed_copy_is_hash_verified_and_never_changes_or_claims_the_source()
    {
        var source = Path.Combine(root, "source");
        var staging = Path.Combine(root, "staging");
        var destination = Path.Combine(root, "managed", "copy");
        Directory.CreateDirectory(Path.Combine(source, "world", "region"));
        Directory.CreateDirectory(Path.Combine(source, "plugins", "empty-config"));
        await File.WriteAllBytesAsync(Path.Combine(source, "world", "level.dat"), [1, 2, 3, 4, 5]);
        await File.WriteAllTextAsync(Path.Combine(source, "server.properties"), "motd=Source remains untouched\n");
        var before = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(source, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
        var operationId = Guid.NewGuid();
        var serverId = Guid.NewGuid();

        var result = await new ManagedInstanceCopyService().MaterializeAsync(
            source, staging, destination, operationId, serverId, CancellationToken.None);

        Assert.Equal(2, result.FileCount);
        Assert.Equal(64, result.Sha256.Length);
        Assert.True(CreationOwnershipMarker.Owns(staging, operationId, serverId));
        Assert.True(ManagedInstanceOwnershipMarker.Proves(staging, serverId));
        Assert.True(Directory.Exists(Path.Combine(staging, "plugins", "empty-config")));
        foreach (var item in before)
            Assert.Equal(item.Value, await File.ReadAllBytesAsync(Path.Combine(source, item.Key)));
        Assert.False(File.Exists(ManagedInstanceOwnershipMarker.PathIn(source)));
        Assert.False(File.Exists(CreationOwnershipMarker.PathIn(source)));
    }

    [Fact]
    public async Task Managed_copy_cleanup_requires_the_exact_operation_marker()
    {
        var source = Path.Combine(root, "cleanup-source");
        var staging = Path.Combine(root, "cleanup-staging");
        var destination = Path.Combine(root, "managed", "cleanup-copy");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "world.dat"), "irreplaceable fixture");
        var operationId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        await new ManagedInstanceCopyService().MaterializeAsync(
            source, staging, destination, operationId, serverId, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() =>
            ManagedInstanceCopyService.DeleteOperationOwnedCandidate(staging, Guid.NewGuid(), serverId));
        ManagedInstanceCopyService.DeleteOperationOwnedCandidate(staging, operationId, serverId);

        Assert.False(Directory.Exists(staging));
        Assert.Equal("irreplaceable fixture", await File.ReadAllTextAsync(Path.Combine(source, "world.dat")));
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
        await store.RecordInstanceHistoryAsync(serverId, "Installed", "https://example.invalid/server.jar",
            new string('a', 64), "Exact fixture transaction");

        var evidence = await store.GetManagedInstallEvidenceAsync(serverId);
        Assert.NotNull(evidence);
        Assert.Equal(new string('a', 64), evidence.Sha256);

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
