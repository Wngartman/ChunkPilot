using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ChunkPilot.IntegrationTests;

public sealed class BackupSummaryIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-backup-summary-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset baseline = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;

    public async Task InitializeAsync()
    {
        paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    [Fact]
    public async Task Projection_selects_latest_verified_record_for_each_exact_registered_server()
    {
        var first = await AddServerAsync("First");
        var second = await AddServerAsync("Second");
        var empty = await AddServerAsync("No backups");
        await store.UpsertBackupAsync(Record(first.Id, baseline));
        await store.UpsertBackupAsync(Record(first.Id, baseline.AddHours(1)));
        await store.UpsertBackupAsync(Record(first.Id, baseline.AddHours(2)) with { Verified = false });
        await store.UpsertBackupAsync(Record(second.Id, baseline.AddHours(-1)));
        await store.UpsertBackupAsync(Record(Guid.NewGuid(), baseline.AddHours(3)));

        var summary = await store.GetLatestVerifiedBackupTimesAsync();
        Assert.Equal(2, summary.Count);
        Assert.Equal(baseline.AddHours(1), summary[first.Id]);
        Assert.Equal(baseline.AddHours(-1), summary[second.Id]);
        Assert.False(summary.ContainsKey(empty.Id));
        Assert.Empty(Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unfinished.zip.partial")]
    [InlineData("unfinished.zip.PARTIAL")]
    public async Task Incomplete_record_references_never_claim_verified_recovery(string archivePath)
    {
        var server = await AddServerAsync("Incomplete");
        await store.UpsertBackupAsync(Record(server.Id, baseline) with { ArchivePath = archivePath });
        Assert.Empty(await store.GetLatestVerifiedBackupTimesAsync());
    }

    [Fact]
    public async Task Inconsistent_embedded_identity_cannot_attribute_a_backup_to_either_server()
    {
        var first = await AddServerAsync("First");
        var second = await AddServerAsync("Second");
        var original = Record(first.Id, baseline);
        await store.UpsertBackupAsync(original);
        // An ID conflict updates the JSON, not the record's immutable indexed server identity.
        await store.UpsertBackupAsync(original with { ServerId = second.Id });
        Assert.Empty(await store.GetLatestVerifiedBackupTimesAsync());
    }

    [Fact]
    public async Task Verification_failure_deletion_and_pre_restore_records_refresh_without_restart()
    {
        var server = await AddServerAsync("Mutation evidence");
        var older = Record(server.Id, baseline);
        var newest = Record(server.Id, baseline.AddHours(1)) with { Source = "Pre-restore safety backup" };
        await store.UpsertBackupAsync(older);
        await store.UpsertBackupAsync(newest);
        await using var supervisor = CreateSupervisor();
        await supervisor.InitializeAsync();
        Assert.Equal(newest.CreatedAt, (await SnapshotAsync(supervisor, server.Id)).LastBackupAt);

        await store.UpsertBackupAsync(newest with { Verified = false });
        Assert.Equal(older.CreatedAt, (await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
        await store.DeleteBackupRecordAsync(older.Id);
        Assert.Null((await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
        await store.UpsertBackupAsync(newest);
        Assert.Equal(newest.CreatedAt, (await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
        await store.DeleteBackupRecordAsync(newest.Id);
        Assert.Null((await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
    }

    [Fact]
    public async Task Recreated_supervisor_restores_persisted_verified_time_before_opening_backups()
    {
        var server = await AddServerAsync("Reopened Agent");
        await using (var original = CreateSupervisor())
        {
            await original.InitializeAsync();
            Assert.Null((await SnapshotAsync(original, server.Id)).LastBackupAt);
            await store.UpsertBackupAsync(Record(server.Id, baseline));
            Assert.Equal(baseline, (await SnapshotAsync(original, server.Id)).LastBackupAt);
        }
        await using var reopenedStore = new ChunkPilotStore(paths);
        await using var reopened = new ServerSupervisor(reopenedStore, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, reopenedStore), NullLoggerFactory.Instance);
        await reopened.InitializeAsync();
        Assert.Equal(baseline, (await SnapshotAsync(reopened, server.Id)).LastBackupAt);
    }

    [Fact(Timeout = 40_000)]
    public async Task Actual_backup_pre_restore_verification_failure_and_deletion_update_dashboard()
    {
        var server = await AddServerAsync("Real synthetic archive");
        await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "motd=synthetic\n");
        await using var supervisor = CreateSupervisor();
        await supervisor.InitializeAsync();
        var manual = await supervisor.BackupAsync(server.Id, "Manual");
        Assert.True(manual.Verified);
        Assert.Equal(manual.CreatedAt, (await SnapshotAsync(supervisor, server.Id)).LastBackupAt);

        var service = new BackupService(paths, store);
        var recovery = await service.CreatePreRestoreRecoveryAsync(server);
        Assert.True(recovery.Verified);
        Assert.Equal(recovery.CreatedAt, (await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
        await File.WriteAllTextAsync(recovery.ArchivePath, "synthetic damaged archive");
        Assert.False(await service.VerifyAsync(recovery));
        Assert.Equal(manual.CreatedAt, (await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
        await service.DeleteAsync(manual);
        // An in-memory successful-backup timestamp must not mask the persisted absence of recovery.
        Assert.Null((await SnapshotAsync(supervisor, server.Id)).LastBackupAt);
    }

    private async Task<ServerDefinition> AddServerAsync(string name)
    {
        var folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var server = new ServerDefinition { Name = name, RootPath = folder, WorkingDirectory = folder };
        await store.UpsertServerAsync(server);
        return server;
    }

    private BackupRecord Record(Guid serverId, DateTimeOffset createdAt) => new()
    {
        ServerId = serverId,
        CreatedAt = createdAt,
        Verified = true,
        ArchivePath = Path.Combine(root, "not-opened-" + Guid.NewGuid().ToString("N") + ".zip")
    };

    private ServerSupervisor CreateSupervisor() => new(store, paths, new ProcessStatisticsProvider(),
        new MinecraftStatusClient(), new BackupService(paths, store), NullLoggerFactory.Instance);

    private static async Task<ServerSnapshot> SnapshotAsync(ServerSupervisor supervisor, Guid serverId) =>
        Assert.Single((await supervisor.DashboardAsync()).Servers, server => server.Definition.Id == serverId);

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
