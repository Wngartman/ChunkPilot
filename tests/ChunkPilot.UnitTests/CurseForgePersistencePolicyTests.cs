using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgePersistencePolicyTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-cf-persistence-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;

    public async Task InitializeAsync()
    {
        paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void Fixed_origin_values_distinguish_API_archive_manifest_and_user_entered_identity()
    {
        var api = CurseForgePersistencePolicy.IdentityOriginFor(new ServerInstallRequest
        {
            SourceType = InstallSourceType.CurseForgeServerPack,
            PackProvider = UpdateProvider.CurseForge,
            PackProjectId = "123"
        });
        var archive = CurseForgePersistencePolicy.IdentityOriginFor(new ServerInstallRequest
        {
            SourceType = InstallSourceType.CurseForgeGeneratedPack,
            PackProvider = UpdateProvider.CurseForge
        });
        var userEntered = CurseForgePersistencePolicy.Minimize(Source(Guid.NewGuid()) with
        {
            IdentityOrigin = ProviderIdentityOrigin.UserEnteredReference
        }).IdentityOrigin;

        Assert.Equal(ProviderIdentityOrigin.ApiDerivedOperationalIdentity, api);
        Assert.Equal(ProviderIdentityOrigin.ArchiveManifest, archive);
        Assert.Equal(ProviderIdentityOrigin.UserEnteredReference, userEntered);
    }

    [Fact]
    public void Legacy_CurseForge_snapshot_without_exact_file_identity_fails_closed_on_restore()
    {
        var source = Source(Guid.NewGuid()) with { InstalledFileId = "newer-file-must-not-survive" };
        var legacySnapshot = new VersionSnapshot
        {
            ServerId = source.ServerId,
            SourceProvider = UpdateProvider.CurseForge,
            VersionId = "older-client-file",
            ProviderProjectId = source.ProjectId,
            MinecraftVersion = source.MinecraftVersion,
            Loader = source.Loader,
            LoaderVersion = source.LoaderVersion
        };

        var restored = CurseForgePersistencePolicy.RestoreInstalledIdentity(source, legacySnapshot);

        Assert.Equal("older-client-file", restored.InstalledVersionId);
        Assert.Empty(restored.InstalledFileId);
    }

    [Fact]
    public async Task Live_update_result_is_not_an_offline_cache_and_source_keeps_only_safety_identity()
    {
        var serverId = Guid.NewGuid();
        var source = Source(serverId);
        var result = new UpdateCheckResult
        {
            ServerId = serverId,
            Status = ServerUpdateStatus.UpdateAvailable,
            Source = source,
            InstalledVersion = Version("installed"),
            LatestVersion = Version("latest"),
            Message = "provider-response-sentinel"
        };

        await store.RecordUpdateCheckAsync(result);

        Assert.Null(await store.GetLatestUpdateCheckAsync(serverId));
        var persisted = Assert.IsType<UpdateSource>(await store.GetUpdateSourceAsync(serverId));
        Assert.Equal("123", persisted.ProjectId);
        Assert.Equal("456", persisted.InstalledVersionId);
        Assert.Equal("789", persisted.InstalledFileId);
        Assert.Equal(ProviderIdentityOrigin.ApiDerivedOperationalIdentity, persisted.IdentityOrigin);
        Assert.Empty(persisted.ProjectName);
        Assert.Empty(persisted.ProjectSlug);
        Assert.Empty(persisted.InstalledVersionName);
        Assert.Empty(persisted.SourceUrl);

        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM update_checks WHERE server_id=$server";
        command.Parameters.AddWithValue("$server", serverId.ToString("D"));
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Download_history_keeps_local_digest_but_not_CurseForge_url_name_or_provider_digest()
    {
        var operationId = Guid.NewGuid();
        await store.RecordUpdateDownloadAsync(operationId, Guid.NewGuid(), UpdateProvider.CurseForge,
            Version("target"), 42, new string('b', 64), "Verified");

        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version_id, source_url, file_name, sha256, provider_hash FROM update_downloads WHERE operation_id=$operation";
        command.Parameters.AddWithValue("$operation", operationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Empty(reader.GetString(0));
        Assert.Empty(reader.GetString(1));
        Assert.Empty(reader.GetString(2));
        Assert.Equal(new string('b', 64), reader.GetString(3));
        Assert.Empty(reader.GetString(4));
    }

    [Fact]
    public async Task Snapshot_keeps_local_recovery_state_but_not_provider_labels_or_download_metadata()
    {
        var snapshot = new VersionSnapshot
        {
            ServerId = Guid.NewGuid(),
            VersionId = "456",
            VersionName = "provider-name-sentinel",
            ProviderProjectId = "123",
            ProviderFileId = "789",
            IdentityOrigin = ProviderIdentityOrigin.ApiDerivedOperationalIdentity,
            SourceProvider = UpdateProvider.CurseForge,
            Source = "https://mediafilez.forgecdn.net/files/456/provider.zip",
            MinecraftVersion = "1.20.1",
            Loader = "Forge",
            LoaderVersion = "47.3.0",
            SnapshotPath = Path.Combine(root, "snapshot.zip"),
            ManifestPath = Path.Combine(root, "manifest.json"),
            Changelog = "provider-changelog-sentinel",
            UpdateNotes = "provider-notes-sentinel",
            Description = "User recovery note"
        };

        await store.UpsertVersionSnapshotAsync(snapshot);

        var persisted = Assert.Single(await store.GetVersionSnapshotsAsync(snapshot.ServerId));
        Assert.Equal("456", persisted.VersionId);
        Assert.Equal("123", persisted.ProviderProjectId);
        Assert.Equal("789", persisted.ProviderFileId);
        Assert.Equal(ProviderIdentityOrigin.ApiDerivedOperationalIdentity, persisted.IdentityOrigin);
        Assert.Empty(persisted.VersionName);
        Assert.Empty(persisted.Source);
        Assert.Empty(persisted.Changelog);
        Assert.Empty(persisted.UpdateNotes);
        Assert.Equal("User recovery note", persisted.Description);
        Assert.Equal(snapshot.SnapshotPath, persisted.SnapshotPath);
    }

    private static UpdateSource Source(Guid serverId) => new()
    {
        ServerId = serverId,
        Provider = UpdateProvider.CurseForge,
        ProjectId = "123",
        ProjectName = "provider-project-sentinel",
        ProjectSlug = "provider-slug-sentinel",
        InstalledVersionId = "456",
        InstalledVersionName = "provider-version-sentinel",
        InstalledFileId = "789",
        IdentityOrigin = ProviderIdentityOrigin.ApiDerivedOperationalIdentity,
        MinecraftVersion = "1.20.1",
        Loader = "Forge",
        LoaderVersion = "47.3.0",
        SourceUrl = "https://mediafilez.forgecdn.net/files/789/provider.zip",
        DetectionEvidence = "provider-evidence-sentinel",
        IsUserLinked = true
    };

    private static PackVersionInfo Version(string id) => new()
    {
        PackId = "123",
        VersionId = id,
        ProviderFileId = "789",
        VersionName = "provider-version-sentinel",
        DownloadUrl = "https://mediafilez.forgecdn.net/files/789/provider.zip",
        FileName = "provider.zip",
        Sha1 = new string('a', 40)
    };
}
