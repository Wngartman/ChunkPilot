using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

public sealed class VersionUpdateIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-version-integration-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;
    private ILoggerFactory loggerFactory = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "appdata"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));
    }

    [Fact(Timeout = 90_000)]
    public async Task Fake_pack_update_starts_preserves_world_and_rolls_back_exactly()
    {
        var definition = await CreateOldServerAsync();
        await using var managed = CreateManaged(definition);
        var source = Source(definition.Id);
        await store.UpsertServerAsync(definition);
        await store.UpsertUpdateSourceAsync(source);
        var package = CreateUpdatePackage("v2-success.zip", "normal");
        var service = CreateUpdateService();
        var request = Request(definition.Id, package, "v2") with
        {
            TargetVersion = Request(definition.Id, package, "v2").TargetVersion with { LoaderVersion = "" }
        };

        var result = await managed.RunExclusivePackUpdateAsync(
            request,
            (server, token) => service.PrepareAndSwitchAsync(server, source, request, cancellationToken: token),
            (server, snapshot, operation, token) => service.RollbackAsync(server, snapshot, operation, token),
            (prepared, token) => service.FinalizeOperationAsync(prepared, token));

        Assert.True(result.Success, result.Message);
        Assert.False(result.RolledBack);
        Assert.Equal(ServerState.Running, managed.State);
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("motd=user-setting", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "server.properties")));
        Assert.Equal("pack-v2", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "new-pack.jar")));
        Assert.False(File.Exists(Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        var versions = await store.GetVersionSnapshotsAsync(definition.Id);
        var active = Assert.Single(versions, item => item.IsActive);
        Assert.Equal("v2", active.VersionId);
        Assert.Equal("fixture", active.LoaderVersion);
        Assert.Equal(VersionHealth.PendingValidation, active.Health);
        Assert.Equal("fixture", (await store.GetUpdateSourceAsync(definition.Id))!.LoaderVersion);
        var previous = Assert.Single(versions, item => !item.IsActive && item.Verified);
        Assert.True(previous.IncludesWorldData);
        Assert.True(await VersionSnapshotService.VerifyAsync(previous.SnapshotPath));

        Assert.True((await managed.StopAsync()).Success);
        await service.RollbackAsync(managed.Definition, previous, Guid.NewGuid());
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.False(File.Exists(Path.Combine(definition.RootPath, "mods", "new-pack.jar")));
        Assert.Equal("user-note", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "notes", "mine.txt")));
    }

    [Fact(Timeout = 45_000)]
    public async Task Provider_script_only_pack_materializes_exact_loader_and_persists_no_script_launch()
    {
        var definition = await CreateOldServerAsync();
        var fakeJava = Path.Combine(root, "fixture-java", "bin", "java.exe");
        var newerJava = Path.Combine(root, "fixture-java-22", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(fakeJava)!);
        Directory.CreateDirectory(Path.GetDirectoryName(newerJava)!);
        await File.WriteAllTextAsync(fakeJava, "fixture Java identity");
        await File.WriteAllTextAsync(newerJava, "incompatible newer fixture Java identity");
        definition = definition with
        {
            Executable = newerJava,
            Arguments = "-jar old-server.jar nogui"
        };
        await store.UpsertManagedJavaRuntimeAsync(new ManagedJavaRuntime
        {
            Vendor = "Fixture managed Java",
            Version = "21.0.8-fixture",
            MajorVersion = 21,
            Architecture = "x64",
            JavaPath = Path.GetFullPath(fakeJava),
            InstallationRoot = Path.Combine(root, "fixture-java"),
            IsManaged = true,
            Health = RuntimeHealth.Healthy
        });
        await store.UpsertManagedJavaRuntimeAsync(new ManagedJavaRuntime
        {
            Vendor = "Fixture managed Java",
            Version = "22.0.2-fixture",
            MajorVersion = 22,
            Architecture = "x64",
            JavaPath = Path.GetFullPath(newerJava),
            InstallationRoot = Path.Combine(root, "fixture-java-22"),
            IsManaged = true,
            Health = RuntimeHealth.Healthy
        });
        var source = Source(definition.Id) with { Provider = UpdateProvider.DirectManifest };
        await store.UpsertServerAsync(definition);
        await store.UpsertUpdateSourceAsync(source);
        var package = CreateUpdatePackage(
            "provider-script-only.zip", "never-executed", includeProviderArgumentFile: true);
        var request = Request(definition.Id, package, "provider-v2") with
        {
            TargetVersion = Request(definition.Id, package, "provider-v2").TargetVersion with
            {
                Loader = "Fabric",
                LoaderVersion = "0.16.14",
                RequiredJavaMajor = 21
            }
        };
        using var loaderHttp = new HttpClient(new FabricLoaderHandler());
        var loader = new LoaderInstallationService(new LoaderMetadataService(loaderHttp), loaderHttp);
        var service = CreateUpdateService(loaderInstaller: loader);

        var prepared = await service.PrepareAndSwitchAsync(definition, source, request);
        try
        {
            var updated = prepared.Result.UpdatedDefinition;
            Assert.Equal(Path.GetFullPath(fakeJava), updated.Executable);
            Assert.True(Path.IsPathFullyQualified(updated.Executable));
            Assert.Equal("java.exe", Path.GetFileName(updated.Executable), ignoreCase: true);
            Assert.Contains("fabric-server-launch.jar", updated.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("run.bat", updated.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("win_args.txt", updated.Arguments, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(definition.RootPath, "run.bat")));
            Assert.True(File.Exists(Path.Combine(definition.RootPath, "fabric-server-launch.jar")));
            Assert.Equal("provider-controlled argument sentinel", await File.ReadAllTextAsync(
                Path.Combine(definition.RootPath, "libraries", "provider", "win_args.txt")));
            var active = Assert.Single(
                await store.GetVersionSnapshotsAsync(definition.Id), version => version.IsActive);
            Assert.Equal(updated.Executable, active.Definition.Executable);
            Assert.Equal(updated.Arguments, active.Definition.Arguments);
            Assert.Equal("21.0.8-fixture", active.JavaVersion);
        }
        finally
        {
            await service.FinalizeOperationAsync(prepared);
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task Installer_java_rejects_unmanaged_identity_then_selects_exact_managed_installer_major()
    {
        var definition = await CreateOldServerAsync();
        var unmanagedJava = Path.Combine(root, "unmanaged-java-21", "bin", "java.exe");
        var managedJava17 = Path.Combine(root, "managed-java-17", "bin", "java.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(unmanagedJava)!);
        Directory.CreateDirectory(Path.GetDirectoryName(managedJava17)!);
        await File.WriteAllTextAsync(unmanagedJava, "unmanaged Java 21 fixture");
        await File.WriteAllTextAsync(managedJava17, "managed Java 17 fixture");
        definition = definition with { Executable = unmanagedJava };
        await store.UpsertManagedJavaRuntimeAsync(new ManagedJavaRuntime
        {
            Vendor = "Unmanaged fixture",
            Version = "21.0.8-unmanaged",
            MajorVersion = 21,
            Architecture = "x64",
            JavaPath = unmanagedJava,
            InstallationRoot = Path.Combine(root, "unmanaged-java-21"),
            IsManaged = false,
            Health = RuntimeHealth.Healthy
        });
        var service = CreateUpdateService();
        var target = new PackVersionInfo
        {
            MinecraftVersion = "1.21.1",
            Loader = "Forge",
            LoaderVersion = "52.0.1",
            InstallerVersion = "52.0.1",
            RequiredJavaMajor = 21,
            InstallerJavaMajor = 21
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResolveInstallerJavaAsync(definition, target, CancellationToken.None));
        Assert.Contains("managed Java 21", error.Message, StringComparison.OrdinalIgnoreCase);

        await store.UpsertManagedJavaRuntimeAsync(new ManagedJavaRuntime
        {
            Vendor = "Managed fixture",
            Version = "17.0.14-managed",
            MajorVersion = 17,
            Architecture = "x64",
            JavaPath = managedJava17,
            InstallationRoot = Path.Combine(root, "managed-java-17"),
            IsManaged = true,
            Health = RuntimeHealth.Healthy
        });
        var resolved = await service.ResolveInstallerJavaAsync(
            definition,
            target with { InstallerJavaMajor = 17 },
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(managedJava17), resolved);
    }

    [Fact(Timeout = 45_000)]
    public async Task CurseForge_snapshot_and_rollback_restore_the_exact_installed_file_identity()
    {
        var definition = await CreateOldServerAsync();
        await store.UpsertServerAsync(definition);
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            InstalledVersionId = "456-client",
            InstalledVersionName = "provider-label",
            InstalledFileId = "789-server",
            IdentityOrigin = ProviderIdentityOrigin.ApiDerivedOperationalIdentity,
            SourceUrl = "https://mediafilez.forgecdn.net/files/789/server.zip"
        };
        await store.UpsertUpdateSourceAsync(source);
        var snapshots = new VersionSnapshotService(paths, store);

        _ = await snapshots.CreateAsync(definition, source, "exact identity fixture");

        var snapshot = Assert.Single(await store.GetVersionSnapshotsAsync(definition.Id));
        Assert.Equal("456-client", snapshot.VersionId);
        Assert.Equal("123", snapshot.ProviderProjectId);
        Assert.Equal("789-server", snapshot.ProviderFileId);
        Assert.Equal(ProviderIdentityOrigin.ApiDerivedOperationalIdentity, snapshot.IdentityOrigin);
        var manifest = JsonSerializer.Deserialize<VersionSnapshotManifest>(
            await File.ReadAllTextAsync(snapshot.ManifestPath), ProtocolJson.Options)!;
        Assert.Equal(UpdateProvider.CurseForge, manifest.SourceProvider);
        Assert.Equal("123", manifest.ProviderProjectId);
        Assert.Equal("789-server", manifest.ProviderFileId);
        Assert.Equal(ProviderIdentityOrigin.ApiDerivedOperationalIdentity, manifest.IdentityOrigin);

        await store.UpsertUpdateSourceAsync(source with
        {
            InstalledVersionId = "999-new-client",
            InstalledFileId = "1000-new-server"
        });
        await CreateUpdateService().RollbackAsync(definition, snapshot, Guid.NewGuid());

        var restored = Assert.IsType<UpdateSource>(await store.GetUpdateSourceAsync(definition.Id));
        Assert.Equal("123", restored.ProjectId);
        Assert.Equal("456-client", restored.InstalledVersionId);
        Assert.Equal("789-server", restored.InstalledFileId);
        Assert.Equal(ProviderIdentityOrigin.ApiDerivedOperationalIdentity, restored.IdentityOrigin);
    }

    [Fact(Timeout = 45_000)]
    public async Task CurseForge_update_requires_exact_client_manifest_preflight_before_snapshot_or_switch()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            InstalledVersionId = "400-client",
            InstalledFileId = "401-server"
        };
        await store.UpsertUpdateSourceAsync(source);
        var package = CreateUpdatePackage("cf-preflight-gate.zip", "normal");
        var request = Request(definition.Id, package, "500") with
        {
            TargetVersion = Request(definition.Id, package, "500").TargetVersion with
            {
                PackId = "123",
                ProviderFileId = "501",
                PackageType = "curseforge-server-pack",
                LoaderVersion = ""
            }
        };
        var preflight = new RejectingCurseForgePreflight();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateUpdateService(preflight).PrepareAndSwitchAsync(definition, source, request));

        Assert.Contains("fixture exact manifest rejection", error.Message, StringComparison.Ordinal);
        Assert.Equal(request.OperationId, preflight.Request!.OperationId);
        Assert.Equal("123", preflight.Request.ProjectId);
        Assert.Equal("500", preflight.Request.ClientFileId);
        Assert.Equal("501", preflight.Request.ExpectedServerPackFileId);
        Assert.Empty(await store.GetVersionSnapshotsAsync(definition.Id));
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
    }

    [Fact(Timeout = 30_000)]
    public async Task Mark_healthy_accepts_only_the_exact_active_pending_version()
    {
        var serverId = Guid.NewGuid();
        var source = Source(serverId);
        await store.UpsertUpdateSourceAsync(source);
        var pending = new VersionSnapshot
        {
            ServerId = serverId,
            VersionId = "v2",
            VersionName = "Version 2",
            SourceProvider = source.Provider,
            ProviderProjectId = source.ProjectId,
            IsActive = true,
            Health = VersionHealth.PendingValidation,
            Verified = true
        };
        var previous = new VersionSnapshot
        {
            ServerId = serverId,
            VersionId = "v1",
            VersionName = "Version 1",
            IsActive = false,
            Health = VersionHealth.Healthy,
            Verified = true
        };
        await store.UpsertVersionSnapshotAsync(pending);
        await store.UpsertVersionSnapshotAsync(previous);
        await using var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), loggerFactory);
        await supervisor.InitializeAsync();
        var coordinator = new ServerUpdateCoordinator(store, supervisor, new UpdateSourceDetector(),
            new UpdateProviderRegistry([new LocalPackageHistoryUpdateProvider()]),
            new PackUpdateCompatibilityService(), CreateUpdateService(), new VersionSnapshotService(paths, store));

        await coordinator.MarkHealthyAsync(serverId, pending.Id, retentionDays: 30);

        var updated = Assert.Single(await store.GetVersionSnapshotsAsync(serverId), item => item.Id == pending.Id);
        Assert.Equal(VersionHealth.Healthy, updated.Health);
        Assert.Contains("User confirmed", updated.LastStartupResult, StringComparison.Ordinal);
        Assert.NotNull(Assert.Single(await store.GetVersionSnapshotsAsync(serverId), item => item.Id == previous.Id)
            .RetainUntil);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkHealthyAsync(serverId, pending.Id, retentionDays: 30));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.MarkHealthyAsync(serverId, Guid.NewGuid(), retentionDays: 30));
    }

    [Fact(Timeout = 30_000)]
    public async Task Exact_local_archive_is_up_to_date_and_repairs_legacy_pack_display_name()
    {
        var definition = await CreateOldServerAsync();
        await store.UpsertServerAsync(definition);
        var package = CreateUpdatePackage("StaTech Industry-2.0.0-rc4-serverpack.zip", "normal");
        var archiveHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(package)))
            .ToLowerInvariant();
        await store.UpsertUpdateSourceAsync(Source(definition.Id) with
        {
            InstalledVersionId = archiveHash,
            InstalledVersionName = "6.0.2",
            InstalledFileId = "",
            SourceUrl = package
        });
        await using var supervisor = new ServerSupervisor(store, paths, new ProcessStatisticsProvider(),
            new MinecraftStatusClient(), new BackupService(paths, store), loggerFactory);
        await supervisor.InitializeAsync();
        var snapshots = new VersionSnapshotService(paths, store);
        var coordinator = new ServerUpdateCoordinator(store, supervisor, new UpdateSourceDetector(),
            new UpdateProviderRegistry([new LocalPackageHistoryUpdateProvider()]),
            new PackUpdateCompatibilityService(), CreateUpdateService(), snapshots);

        var result = await coordinator.CheckAsync(definition.Id);

        Assert.Equal(ServerUpdateStatus.UpToDate, result.Status);
        var repaired = Assert.IsType<UpdateSource>(await store.GetUpdateSourceAsync(definition.Id));
        Assert.Equal("StaTech Industry-2.0.0-rc4-serverpack", repaired.InstalledVersionName);
        Assert.Equal(archiveHash, repaired.InstalledFileId);
    }

    [Fact(Timeout = 90_000)]
    public async Task Failed_updated_server_automatically_restores_and_restarts_previous_version()
    {
        var definition = await CreateOldServerAsync();
        await using var managed = CreateManaged(definition);
        var source = Source(definition.Id);
        await store.UpsertServerAsync(definition);
        await store.UpsertUpdateSourceAsync(source);
        Assert.True((await managed.StartAsync()).Success);
        var package = CreateUpdatePackage("v2-crash.zip", "immediate-crash");
        var service = CreateUpdateService();
        var request = Request(definition.Id, package, "v2-broken");

        var result = await managed.RunExclusivePackUpdateAsync(
            request,
            (server, token) => service.PrepareAndSwitchAsync(server, source, request, cancellationToken: token),
            (server, snapshot, operation, token) => service.RollbackAsync(server, snapshot, operation, token),
            (prepared, token) => service.FinalizeOperationAsync(prepared, token));

        Assert.False(result.Success);
        Assert.True(result.RolledBack, result.Message);
        Assert.Equal(ServerState.Running, managed.State);
        Assert.Contains("Automatic rollback completed", result.Message, StringComparison.Ordinal);
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.False(File.Exists(Path.Combine(definition.RootPath, "mods", "new-pack.jar")));
        var versions = await store.GetVersionSnapshotsAsync(definition.Id);
        Assert.DoesNotContain(versions, item => item.IsActive && item.VersionId == "v2-broken");
        Assert.Contains(versions, item => item.IsActive && item.VersionId == "v1");
        Assert.True((await managed.StopAsync()).Success);
    }

    [Fact(Timeout = 45_000)]
    public async Task Migration_conflict_returns_preview_before_active_switch()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id);
        await store.UpsertUpdateSourceAsync(source);
        var package = CreateUpdatePackage("migration-preview.zip", "normal");
        var request = Request(definition.Id, package, "v2-preview") with
        {
            ConfirmedMigrationWarnings = false
        };
        var exception = await Assert.ThrowsAsync<MigrationReviewRequiredException>(() =>
            CreateUpdateService().PrepareAndSwitchAsync(definition, source, request));
        Assert.Contains(exception.Plan.Changes, item =>
            item.RelativePath == "mods/old-pack.jar" && item.Change == "Removed from active pack");
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
    }

    [Fact(Timeout = 45_000)]
    public async Task CurseForge_preflight_rejection_occurs_before_reviewed_download_reuse_lookup()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            InstalledVersionId = "400",
            InstalledFileId = "401"
        };
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "501",
            VersionName = "reviewed target",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            DownloadUrl = "https://forged.invalid/not-used.zip",
            FileSize = 1,
            Sha1 = new string('0', 40),
            PackageType = "jar"
        };
        var reviewedOperationId = Guid.NewGuid();
        var request = new UpdateInstallRequest
        {
            OperationId = Guid.NewGuid(),
            ServerId = definition.Id,
            TargetVersion = target,
            ReviewedOperationId = reviewedOperationId,
            ConfirmedMigrationWarnings = true
        };
        var authorization = new ReviewedUpdateArtifactAuthorization(
            reviewedOperationId,
            definition.Id,
            UpdateProvider.CurseForge,
            source.ProjectId,
            target.VersionId,
            target.ProviderFileId);
        var preflight = new RejectingCurseForgePreflight();

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            CreateUpdateService(preflight).PrepareAndSwitchAsync(
                definition, source, request, reuseAuthorization: authorization));

        Assert.Contains("fixture exact manifest rejection", error.Message, StringComparison.Ordinal);
        Assert.NotNull(preflight.Request);
        Assert.Null(await store.GetRecordedUpdateDownloadAsync(reviewedOperationId));
        Assert.Empty(await store.GetVersionSnapshotsAsync(definition.Id));
    }

    [Fact]
    public void CurseForge_official_preflight_replaces_forged_operational_artifact_fields()
    {
        var operationId = Guid.NewGuid();
        var source = Source(Guid.NewGuid()) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123"
        };
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "501",
            VersionName = "display label",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            DownloadUrl = "https://attacker.invalid/forged.jar",
            FileSize = 1,
            Sha1 = new string('1', 40),
            Sha256 = new string('2', 64),
            Sha512 = new string('3', 128),
            FileName = "forged.jar",
            PackageType = "jar",
            DeclaredFiles = ["untrusted-entry"]
        };
        var result = ReadyOfficialPreflight(operationId) with
        {
            ServerPackDownloadUrl =
                "https://mediafilez.forgecdn.net/files/501/actual-server.zip",
            ServerPackSha1 = new string('a', 40),
            ServerPackSizeBytes = 42
        };

        var canonical = ServerPackUpdateService.CanonicalizeCurseForgeTarget(
            source, target, result, operationId);

        Assert.Equal("123", canonical.PackId);
        Assert.Equal("500", canonical.VersionId);
        Assert.Equal("501", canonical.ProviderFileId);
        Assert.Equal(result.ServerPackDownloadUrl, canonical.DownloadUrl);
        Assert.Equal(42, canonical.FileSize);
        Assert.Equal(new string('a', 40), canonical.Sha1);
        Assert.Empty(canonical.Sha256);
        Assert.Empty(canonical.Sha512);
        Assert.Equal("actual-server.zip", canonical.FileName);
        Assert.Equal("zip", canonical.PackageType);
        Assert.Empty(canonical.DeclaredFiles);
        Assert.Equal("display label", canonical.VersionName);
    }

    [Fact]
    public void CurseForge_official_preflight_rejects_a_contradictory_generated_dependency_plan()
    {
        var operationId = Guid.NewGuid();
        var source = Source(Guid.NewGuid()) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123"
        };
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "501",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge"
        };
        var result = ReadyOfficialPreflight(operationId) with
        {
            GeneratedPackPlan = GeneratedPlan()
        };

        Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.CanonicalizeCurseForgeTarget(
                source, target, result, operationId));
    }

    [Fact]
    public void Trusted_CurseForge_generated_plan_is_neither_emitted_to_nor_accepted_from_json()
    {
        var trustedPlan = GeneratedPlan();
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "500",
            TrustedCurseForgeGeneratedPlan = trustedPlan
        };

        var serialized = JsonSerializer.Serialize(target, ProtocolJson.Options);
        Assert.DoesNotContain("trustedCurseForgeGeneratedPlan", serialized, StringComparison.OrdinalIgnoreCase);

        var forged = JsonSerializer.Serialize(new
        {
            packId = "123",
            versionId = "500",
            providerFileId = "500",
            trustedCurseForgeGeneratedPlan = trustedPlan
        }, ProtocolJson.Options);
        var deserialized = JsonSerializer.Deserialize<PackVersionInfo>(forged, ProtocolJson.Options);

        Assert.NotNull(deserialized);
        Assert.Null(deserialized.TrustedCurseForgeGeneratedPlan);
    }

    [Fact]
    public void CurseForge_generated_preflight_canonicalizes_to_the_verified_client_artifact()
    {
        var operationId = Guid.NewGuid();
        var source = Source(Guid.NewGuid()) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123"
        };
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "500",
            VersionName = "generated target",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            DownloadUrl = "https://attacker.invalid/forged.zip",
            FileSize = 1,
            Sha1 = new string('1', 40),
            Sha256 = new string('2', 64),
            Sha512 = new string('3', 128),
            FileName = "forged.jar",
            PackageType = "jar"
        };
        var result = ReadyOfficialPreflight(operationId) with
        {
            ServerPackFileId = "",
            ServerPackDownloadUrl = "",
            ServerPackSha1 = "",
            ServerPackSizeBytes = null,
            GeneratedPackPlan = GeneratedPlan()
        };

        var canonical = ServerPackUpdateService.CanonicalizeCurseForgeTarget(
            source, target, result, operationId);

        Assert.Equal(result.ClientFileId, canonical.ProviderFileId);
        Assert.Equal(result.ClientDownloadUrl, canonical.DownloadUrl);
        Assert.Equal(result.ClientSizeBytes, canonical.FileSize);
        Assert.Equal(result.ClientSha1, canonical.Sha1);
        Assert.Equal(result.ClientSha256, canonical.Sha256);
        Assert.Empty(canonical.Sha512);
        Assert.Equal("actual-client.zip", canonical.FileName);
        Assert.Equal("curseforge-manifest", canonical.PackageType);
        Assert.Equal(result.GeneratedPackPlan!.Digest,
            canonical.TrustedCurseForgeGeneratedPlan!.Digest);
    }

    [Fact]
    public void CurseForge_generated_update_without_exact_dependency_plan_is_rejected()
    {
        var operationId = Guid.NewGuid();
        var source = Source(Guid.NewGuid()) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123"
        };
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "500",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge"
        };
        var result = ReadyOfficialPreflight(operationId) with
        {
            ServerPackFileId = "",
            ServerPackDownloadUrl = "",
            ServerPackSha1 = "",
            ServerPackSizeBytes = null,
            GeneratedPackPlan = null
        };

        Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.CanonicalizeCurseForgeTarget(
                source, target, result, operationId));
    }

    [Fact(Timeout = 30_000)]
    public async Task CurseForge_generated_update_forecast_includes_the_sealed_dependency_plan_before_snapshot()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            InstalledVersionId = "400",
            InstalledFileId = "401"
        };
        await store.UpsertUpdateSourceAsync(source);
        var operationId = Guid.NewGuid();
        var ordering = new List<string>();
        const long generatedBytes = 2L * 1024 * 1024 * 1024;
        var preflight = new RecordingCurseForgePreflight(
            request => ReadyOfficialPreflight(request.OperationId) with
            {
                ServerPackFileId = "",
                ServerPackDownloadUrl = "",
                ServerPackSha1 = "",
                ServerPackSizeBytes = null,
                GeneratedPackPlan = GeneratedPlan(generatedBytes)
            }, ordering);
        var request = new UpdateInstallRequest
        {
            OperationId = operationId,
            ServerId = definition.Id,
            TargetVersion = new PackVersionInfo
            {
                PackId = "123",
                VersionId = "500",
                ProviderFileId = "500",
                VersionName = "Generated target",
                MinecraftVersion = "1.21.1",
                Loader = "NeoForge"
            }
        };
        var storage = new FixedStorageSpaceProbe(2L * 1024 * 1024 * 1024);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CreateUpdateService(preflight, storageSpace: storage)
                .PrepareAndSwitchAsync(definition, source, request));

        Assert.Contains("Server update candidate staging", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["preflight"], ordering);
        Assert.Empty(await store.GetVersionSnapshotsAsync(definition.Id));
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
    }

    [Fact(Timeout = 45_000)]
    public async Task CurseForge_archive_expansion_is_forecast_exactly_before_snapshot_or_server_mutation()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            InstalledVersionId = "400",
            InstalledFileId = "401"
        };
        await store.UpsertUpdateSourceAsync(source);

        var package = Path.Combine(root, "high-expansion-server-pack.zip");
        const uint compressedBytes = 4 * 1024 * 1024;
        const uint expandedBytes = 768 * 1024 * 1024;
        CreateSyntheticArchive(
            package,
            "server/server.jar",
            compressedBytes,
            expandedBytes);
        var packageBytes = await File.ReadAllBytesAsync(package);
#pragma warning disable CA5350 // The fixture mirrors CurseForge's provider SHA-1; local SHA-256 is retained for reuse evidence.
        var packageSha1 = Convert.ToHexString(SHA1.HashData(packageBytes)).ToLowerInvariant();
#pragma warning restore CA5350
        var packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var packageSize = packageBytes.LongLength;
        var reviewedOperationId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        var ordering = new List<string>();
        var providerUrl = "https://mediafilez.forgecdn.net/files/501/high-expansion-server-pack.zip";
        var reviewedTarget = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "501",
            VersionName = "High expansion target",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            LoaderVersion = "21.1.200",
            DownloadUrl = providerUrl,
            FileName = "high-expansion-server-pack.zip",
            FileSize = packageSize,
            Sha1 = packageSha1,
            PackageType = "zip"
        };
        var request = new UpdateInstallRequest
        {
            OperationId = operationId,
            ServerId = definition.Id,
            ReviewedOperationId = reviewedOperationId,
            ConfirmedMigrationWarnings = true,
            TargetVersion = reviewedTarget
        };
        var authorization = new ReviewedUpdateArtifactAuthorization(
            reviewedOperationId,
            definition.Id,
            UpdateProvider.CurseForge,
            source.ProjectId,
            reviewedTarget.VersionId,
            reviewedTarget.ProviderFileId);
        var reviewedCache = Path.Combine(paths.UpdateCache, $"local-{reviewedOperationId:N}.package");
        File.Copy(package, reviewedCache);
        await store.RecordUpdateDownloadAsync(
            reviewedOperationId,
            definition.Id,
            UpdateProvider.CurseForge,
            reviewedTarget,
            packageSize,
            packageSha256,
            "Verified");
        var preflight = new RecordingCurseForgePreflight(
            incoming => ReadyOfficialPreflight(incoming.OperationId) with
            {
                ProjectId = incoming.ProjectId,
                ClientFileId = incoming.ClientFileId,
                ServerPackFileId = incoming.ExpectedServerPackFileId,
                ServerPackDownloadUrl = providerUrl,
                ServerPackSha1 = packageSha1,
                ServerPackSizeBytes = packageSize
            }, ordering);
        var storage = new FixedStorageSpaceProbe(1_500L * 1024 * 1024);
        var candidate = Path.Combine(
            Directory.GetParent(definition.RootPath)!.FullName,
            $".chunkpilot-update-{operationId:N}");
        var previous = Path.Combine(
            Directory.GetParent(definition.RootPath)!.FullName,
            $".chunkpilot-previous-{operationId:N}");

        var error = await Assert.ThrowsAsync<IOException>(() =>
            CreateUpdateService(preflight, storageSpace: storage)
                .PrepareAndSwitchAsync(
                    definition,
                    source,
                    request,
                    reuseAuthorization: authorization));

        Assert.Contains("Server update candidate staging", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["preflight"], ordering);
        var verifiedReuse = Assert.IsType<RecordedUpdateDownload>(
            await store.GetRecordedUpdateDownloadAsync(operationId));
        Assert.Equal("Verified reuse from migration review", verifiedReuse.Status);
        Assert.Empty(await store.GetVersionSnapshotsAsync(definition.Id));
        Assert.Empty(Directory.EnumerateFiles(paths.VersionSnapshots, "*", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(candidate));
        Assert.False(Directory.Exists(previous));
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.True(File.Exists(reviewedCache));
    }

    [Fact]
    public void CurseForge_canonicalization_rejects_project_client_and_server_file_relationship_mismatches()
    {
        var operationId = Guid.NewGuid();
        var source = Source(Guid.NewGuid()) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123"
        };
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500",
            ProviderFileId = "501",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge"
        };
        var ready = ReadyOfficialPreflight(operationId);

        Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.CanonicalizeCurseForgeTarget(
                source, target with { PackId = "999" }, ready, operationId));
        Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.CanonicalizeCurseForgeTarget(
                source, target, ready with { ClientFileId = "999" }, operationId));
        Assert.Throws<InvalidDataException>(() =>
            ServerPackUpdateService.CanonicalizeCurseForgeTarget(
                source, target, ready with { ServerPackFileId = "999" }, operationId));
    }

    [Fact]
    public void Agent_authorizes_reviewed_download_reuse_only_for_the_same_server_and_target()
    {
        var serverId = Guid.NewGuid();
        var reviewedOperationId = Guid.NewGuid();
        var target = new PackVersionInfo
        {
            PackId = "123",
            VersionId = "500-client",
            ProviderFileId = "501-server",
            VersionName = "Reviewed release",
            FileSize = 4096,
            Sha1 = new string('a', 40),
            PackageType = "curseforge-server-pack"
        };
        var source = Source(serverId) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123"
        };
        var reviewedRequest = new UpdateInstallRequest
        {
            OperationId = reviewedOperationId,
            ServerId = serverId,
            TargetVersion = target,
            ConfirmedMigrationWarnings = false
        };
        var currentRequest = reviewedRequest with
        {
            OperationId = Guid.NewGuid(),
            ReviewedOperationId = reviewedOperationId,
            ConfirmedMigrationWarnings = true,
            MigrationResolutions = new Dictionary<string, MigrationResolution>
            {
                ["config/reviewed.toml"] = new() { Kind = MigrationResolutionKind.KeepOld }
            }
        };
        var plan = new MigrationPlan { Conflicts = ["config/reviewed.toml"] };
        var reviewedSnapshot = new UpdateOperationSnapshot
        {
            OperationId = reviewedOperationId,
            IsTerminal = true,
            Success = false,
            Progress = new UpdateProgress
            {
                OperationId = reviewedOperationId,
                State = UpdateOperationState.PlanningMigration
            },
            Result = new UpdateExecutionResult
            {
                OperationId = reviewedOperationId,
                ServerId = serverId,
                TargetVersionId = target.VersionId,
                MigrationPlan = plan
            }
        };

        var authorization = ServerUpdateCoordinator.ValidateReviewedOperationForReuse(
            currentRequest, source, reviewedRequest, source, reviewedSnapshot);

        Assert.Equal(reviewedOperationId, authorization.ReviewedOperationId);
        Assert.Equal(serverId, authorization.ServerId);
        var otherServerId = Guid.NewGuid();
        Assert.Throws<InvalidOperationException>(() =>
            ServerUpdateCoordinator.ValidateReviewedOperationForReuse(
                currentRequest with { ServerId = otherServerId },
                source with { ServerId = otherServerId },
                reviewedRequest,
                source,
                reviewedSnapshot));
        Assert.Throws<InvalidOperationException>(() =>
            ServerUpdateCoordinator.ValidateReviewedOperationForReuse(
                currentRequest with
                {
                    TargetVersion = target with { ProviderFileId = "different-server-pack" }
                },
                source,
                reviewedRequest,
                source,
                reviewedSnapshot));
    }

    [Fact(Timeout = 45_000)]
    public async Task Reviewed_curseforge_download_requires_exact_verified_artifact_evidence()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            InstalledVersionId = "400-client",
            InstalledFileId = "401-server"
        };
        var package = CreateUpdatePackage("reviewed-reuse.zip", "normal");
        var packageBytes = await File.ReadAllBytesAsync(package);
        var localSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var target = Request(definition.Id, package, "500-client").TargetVersion with
        {
            PackId = "123",
            ProviderFileId = "501-server",
            FileSize = packageBytes.LongLength,
            Sha256 = localSha256,
            PackageType = "curseforge-server-pack"
        };
        var reviewedOperationId = Guid.NewGuid();
        var request = new UpdateInstallRequest
        {
            OperationId = Guid.NewGuid(),
            ServerId = definition.Id,
            TargetVersion = target,
            ReviewedOperationId = reviewedOperationId,
            ConfirmedMigrationWarnings = true
        };
        var authorization = new ReviewedUpdateArtifactAuthorization(
            reviewedOperationId,
            definition.Id,
            UpdateProvider.CurseForge,
            source.ProjectId,
            target.VersionId,
            target.ProviderFileId);
        var reviewedCache = Path.Combine(paths.UpdateCache, $"local-{reviewedOperationId:N}.package");
        File.Copy(package, reviewedCache);
        await store.RecordUpdateDownloadAsync(
            reviewedOperationId,
            definition.Id,
            UpdateProvider.CurseForge,
            target,
            packageBytes.LongLength,
            localSha256,
            "Verified");
        var service = CreateUpdateService();

        var resolved = await service.ResolveReviewedDownloadAsync(
            definition, source, target, request, authorization);

        Assert.Equal(reviewedCache, resolved.Path);
        Assert.Equal(localSha256, resolved.LocalSha256);
        await ServerPackUpdateService.VerifyDownloadAsync(resolved.Path, target);

        File.Delete(reviewedCache);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.ResolveReviewedDownloadAsync(definition, source, target, request, authorization));

        var tampered = packageBytes.ToArray();
        tampered[0] ^= 0xff;
        await File.WriteAllBytesAsync(reviewedCache, tampered);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ResolveReviewedDownloadAsync(definition, source, target, request, authorization));
        Assert.False(File.Exists(reviewedCache));
    }

    [Fact(Timeout = 45_000)]
    public async Task Download_only_verifies_cache_without_touching_active_server()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id);
        await store.UpsertUpdateSourceAsync(source);
        var package = CreateUpdatePackage("download-only.zip", "normal");
        var request = Request(definition.Id, package, "v2-download") with { DownloadOnly = true };
        var result = await CreateUpdateService().DownloadAndVerifyOnlyAsync(
            definition, source, request);
        Assert.True(result.Success, result.Message);
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.NotEmpty(Directory.EnumerateFiles(paths.UpdateCache));
        Assert.Empty(Directory.EnumerateDirectories(
            Directory.GetParent(definition.RootPath)!.FullName, ".chunkpilot-previous-*"));
    }

    [Fact(Timeout = 45_000)]
    public async Task CurseForge_download_persists_only_local_digest_and_opaque_cache_identity()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id) with
        {
            Provider = UpdateProvider.CurseForge,
            ProjectId = "123",
            ProjectName = "cf-project-label-sentinel",
            ProjectSlug = "cf-project-slug-sentinel",
            InstalledVersionId = "456",
            InstalledVersionName = "cf-old-version-label-sentinel",
            InstalledFileId = "789",
            SourceUrl = "https://provider.invalid/cf-source-url-sentinel.zip"
        };
        var package = CreateUpdatePackage("cf-file-name-sentinel.zip", "normal");
        var packageBytes = await File.ReadAllBytesAsync(package);
#pragma warning disable CA5350 // The fixture mirrors CurseForge's provider SHA-1; local SHA-256 is asserted below.
        var packageSha1 = Convert.ToHexString(SHA1.HashData(packageBytes)).ToLowerInvariant();
#pragma warning restore CA5350
        var packageSha256 = Convert.ToHexString(SHA256.HashData(packageBytes)).ToLowerInvariant();
        var canonicalUrl = "https://mediafilez.forgecdn.net/files/501/canonical-server.zip";
        var ordering = new List<string>();
        var request = Request(definition.Id, package, "500") with
        {
            TargetVersion = Request(definition.Id, package, "500").TargetVersion with
            {
                PackId = "123",
                ProviderFileId = "501",
                VersionName = "cf-new-version-label-sentinel",
                Loader = "NeoForge",
                DownloadUrl = "https://attacker.invalid/forged.jar",
                FileName = "forged.jar",
                FileSize = 1,
                Sha1 = new string('a', 40),
                Sha256 = new string('b', 64),
                Sha512 = new string('c', 128),
                PackageType = "jar"
            }
        };
        var preflight = new RecordingCurseForgePreflight(
            incoming => ReadyOfficialPreflight(incoming.OperationId) with
            {
                ProjectId = incoming.ProjectId,
                ClientFileId = incoming.ClientFileId,
                ServerPackFileId = incoming.ExpectedServerPackFileId,
                ClientSizeBytes = packageBytes.LongLength,
                ServerPackDownloadUrl = canonicalUrl,
                ServerPackSha1 = packageSha1,
                ServerPackSizeBytes = packageBytes.LongLength
            }, ordering);
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
        using var api = new CurseForgeApiClient(
            secrets,
            new RecordingDownloadHandler(requestMessage =>
            {
                ordering.Add("download");
                Assert.Equal(canonicalUrl, requestMessage.RequestUri!.AbsoluteUri);
                Assert.Equal("fixture-key", requestMessage.Headers.GetValues("x-api-key").Single());
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    RequestMessage = requestMessage,
                    Content = new ByteArrayContent(packageBytes)
                };
            }));

        var result = await CreateUpdateService(preflight, api)
            .DownloadAndVerifyOnlyAsync(definition, source, request);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["preflight", "download"], ordering);
        var cache = Assert.Single(Directory.EnumerateFiles(paths.UpdateCache));
        Assert.Equal($"local-{request.OperationId:N}.package", Path.GetFileName(cache));
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version_id, source_url, file_name, sha256, provider_hash
            FROM update_downloads WHERE operation_id=$operation
            """;
        command.Parameters.AddWithValue("$operation", request.OperationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Empty(reader.GetString(0));
        Assert.Empty(reader.GetString(1));
        Assert.Empty(reader.GetString(2));
        Assert.Equal(packageSha256, reader.GetString(3));
        Assert.Empty(reader.GetString(4));
    }

    [Fact(Timeout = 45_000)]
    public async Task Invalid_hash_never_modifies_active_server()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id);
        await store.UpsertUpdateSourceAsync(source);
        var package = CreateUpdatePackage("bad-hash.zip", "normal");
        var service = CreateUpdateService();
        var request = Request(definition.Id, package, "bad") with
        {
            TargetVersion = Request(definition.Id, package, "bad").TargetVersion with
            {
                Sha256 = new string('0', 64)
            }
        };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.PrepareAndSwitchAsync(definition, source, request));
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.Empty(Directory.EnumerateDirectories(
            Directory.GetParent(definition.RootPath)!.FullName, ".chunkpilot-update-*"));
    }

    [Fact(Timeout = 45_000)]
    public async Task Snapshot_deletion_moves_only_archive_and_never_world()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id);
        var snapshots = new VersionSnapshotService(paths, store);
        var previous = await snapshots.CreateAsync(definition, source, "deletion fixture");
        var active = new VersionSnapshot
        {
            ServerId = definition.Id,
            VersionId = "v2",
            VersionName = "v2",
            IsActive = true,
            Verified = true,
            Health = VersionHealth.Healthy,
            Definition = definition
        };
        await store.UpsertVersionSnapshotAsync(active);
        await snapshots.DeleteAsync(definition.Id, previous.Id);
        Assert.True(File.Exists(Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.False(File.Exists(previous.SnapshotPath));
        Assert.NotEmpty(Directory.EnumerateFiles(
            Path.Combine(paths.Recovery, "DeletedVersionSnapshots"), "*.zip", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<InvalidOperationException>(() => snapshots.DeleteAsync(definition.Id, active.Id));
        Assert.True(Directory.Exists(definition.RootPath));
    }

    [Fact(Timeout = 45_000)]
    public async Task Snapshot_reuses_verified_jar_objects_and_restores_the_complete_tree()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id);
        var snapshots = new VersionSnapshotService(paths, store);

        var first = await snapshots.CreateAsync(definition, source, "first object snapshot");
        await File.WriteAllTextAsync(Path.Combine(definition.RootPath, "world", "level.dat"), "world-v2");
        var second = await snapshots.CreateAsync(definition, source, "second object snapshot");

        var firstManifest = JsonSerializer.Deserialize<VersionSnapshotManifest>(
            await File.ReadAllTextAsync(first.ManifestPath), ProtocolJson.Options)!;
        var secondManifest = JsonSerializer.Deserialize<VersionSnapshotManifest>(
            await File.ReadAllTextAsync(second.ManifestPath), ProtocolJson.Options)!;
        var firstJar = Assert.Single(firstManifest.ContentObjects);
        var secondJar = Assert.Single(secondManifest.ContentObjects);
        Assert.Equal("mods/old-pack.jar", firstJar.RelativePath);
        Assert.Equal(firstJar.ObjectKey, secondJar.ObjectKey);
        Assert.Equal(firstJar.Sha256, secondJar.Sha256);

        using (var archive = ZipFile.OpenRead(second.SnapshotPath))
            Assert.Null(archive.GetEntry("mods/old-pack.jar"));
        Assert.True(await VersionSnapshotService.VerifyAsync(second.SnapshotPath));

        var restored = Path.Combine(root, "object-restored-" + Guid.NewGuid().ToString("N"));
        await VersionSnapshotService.ExtractVerifiedAsync(second.SnapshotPath, restored);
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(Path.Combine(restored, "mods", "old-pack.jar")));
        Assert.Equal("world-v2", await File.ReadAllTextAsync(Path.Combine(restored, "world", "level.dat")));
        Assert.True(await VersionSnapshotService.VerifyExtractedAsync(second.SnapshotPath, restored));
    }

    [Fact(Timeout = 45_000)]
    public async Task Snapshot_rejects_a_reparse_tree_before_creating_any_output()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var definition = await CreateOldServerAsync();
        var foreign = Path.Combine(root, "foreign-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreign);
        var foreignWorld = Path.Combine(foreign, "r.0.0.mca");
        await File.WriteAllBytesAsync(foreignWorld, new byte[4_096]);
        var link = Path.Combine(definition.RootPath, "linked-world");
        CreateJunction(link, foreign);
        try
        {
            using var exclusive = new FileStream(
                foreignWorld, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new VersionSnapshotService(paths, store).CreateAsync(
                    definition, Source(definition.Id), "unsafe reparse fixture"));

            Assert.False(Directory.Exists(
                Path.Combine(paths.VersionSnapshots, definition.Id.ToString("D"))));
        }
        finally
        {
            DeleteJunction(link);
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task Snapshot_rejects_an_oversized_inventory_before_creating_any_output()
    {
        var definition = await CreateOldServerAsync();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new VersionSnapshotService(paths, store, maximumInventoryEntries: 1).CreateAsync(
                definition, Source(definition.Id), "entry limit fixture"));

        Assert.False(Directory.Exists(
            Path.Combine(paths.VersionSnapshots, definition.Id.ToString("D"))));
    }

    [Fact(Timeout = 45_000)]
    public async Task Snapshot_honors_pre_cancellation_before_creating_any_output()
    {
        var definition = await CreateOldServerAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new VersionSnapshotService(paths, store).CreateAsync(
                definition, Source(definition.Id), "cancelled fixture", cancellation.Token));

        Assert.False(Directory.Exists(
            Path.Combine(paths.VersionSnapshots, definition.Id.ToString("D"))));
    }

    [Fact(Timeout = 45_000)]
    public async Task Interrupted_post_switch_update_recovers_retained_previous_directory()
    {
        var definition = await CreateOldServerAsync();
        await store.UpsertServerAsync(definition);
        var source = Source(definition.Id);
        await store.UpsertUpdateSourceAsync(source);
        var snapshots = new VersionSnapshotService(paths, store);
        _ = await snapshots.CreateAsync(definition, source, "interrupted fixture");
        var operation = Guid.NewGuid();
        var parent = Directory.GetParent(definition.RootPath)!.FullName;
        var previous = Path.Combine(parent, $".chunkpilot-previous-{operation:N}");
        Directory.Move(definition.RootPath, previous);
        Directory.CreateDirectory(definition.RootPath);
        await File.WriteAllTextAsync(Path.Combine(definition.RootPath, "failed-candidate.txt"), "bad");
        await store.UpsertOperationAsync(operation, "ServerPackUpdate", InstallState.Installing,
            definition.RootPath, Path.Combine(parent, $".chunkpilot-update-{operation:N}"), "Switching");

        var recovered = await CreateUpdateService().RecoverInterruptedOperationsAsync();
        Assert.Single(recovered);
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.False(File.Exists(Path.Combine(definition.RootPath, "failed-candidate.txt")));
        Assert.Empty(await store.GetInterruptedOperationsAsync());
    }

    private async Task<ServerDefinition> CreateOldServerAsync()
    {
        var serverRoot = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "world", "playerdata"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "notes"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "level.dat"), "world-v1");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "playerdata", "player.dat"), "player-v1");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "motd=user-setting");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "mods", "old-pack.jar"), "old-pack-v1");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "notes", "mine.txt"), "user-note");
        var port = GetFreePort();
        return new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Version fixture",
            RootPath = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal",
            WorkingDirectory = serverRoot,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            },
            ReadinessPattern = @"Done \(.+?\)!|For help, type",
            StartupTimeoutSeconds = 10,
            ShutdownTimeoutSeconds = 5,
            SaveTimeoutSeconds = 5,
            Port = port,
            Ecosystem = ServerEcosystem.Custom,
            MinecraftVersion = "1.21.1",
            LoaderVersion = "fixture"
        };
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("cmd.exe could not be started to create a junction.");
        process.WaitForExit(20_000);
        if (process.ExitCode != 0 || !Directory.Exists(link) ||
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("The test junction could not be created on this filesystem.");
    }

    private static void DeleteJunction(string path)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(path);
    }

    private ManagedServer CreateManaged(ServerDefinition definition) =>
        new(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, loggerFactory.CreateLogger<ManagedServer>(), consoleCapacity: 2_000);

    private ServerPackUpdateService CreateUpdateService(
        ICurseForgeModpackPreflightService? curseForgePreflight = null,
        CurseForgeApiClient? curseForge = null,
        IStorageSpaceProbe? storageSpace = null,
        LoaderInstallationService? loaderInstaller = null)
    {
        var snapshots = new VersionSnapshotService(paths, store);
        return new ServerPackUpdateService(paths, store, snapshots, new PackMigrationPlanner(),
            new ServerDetectionService(new JavaDiscoveryService()),
            new WorldManager(paths, new SafeFileService(paths)),
            loaderInstaller: loaderInstaller,
            curseForge: curseForge,
            curseForgePreflight: curseForgePreflight,
            storageSpace: storageSpace);
    }

    private static CurseForgeModpackPreflightResult ReadyOfficialPreflight(Guid operationId) => new()
    {
        OperationId = operationId,
        ProjectId = "123",
        ClientFileId = "500",
        ServerPackFileId = "501",
        State = CatalogReleasePreflightState.Ready,
        Detail = "fixture ready",
        MinecraftVersion = "1.21.1",
        Loader = "NeoForge",
        LoaderVersion = "21.1.200",
        RequiredJavaMajor = 21,
        ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/500/actual-client.zip",
        ClientSha1 = new string('d', 40),
        ClientSha256 = new string('e', 64),
        ClientSizeBytes = 21,
        ServerPackDownloadUrl = "https://mediafilez.forgecdn.net/files/501/actual-server.zip",
        ServerPackSha1 = new string('f', 40),
        ServerPackSizeBytes = 22
    };

    private static CurseForgeGeneratedPackPlan GeneratedPlan(long totalBytes = 1_024)
    {
        const long maximumFileBytes = 512L * 1024 * 1024;
        var files = new List<CurseForgeGeneratedFilePlan>();
        var remaining = totalBytes;
        var index = 0;
        while (remaining > 0)
        {
            var size = Math.Min(remaining, maximumFileBytes);
            files.Add(new CurseForgeGeneratedFilePlan
            {
                ProjectId = (700 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FileId = (701 + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                FileName = $"generated-fixture-{index}.jar",
                DownloadUrl = $"https://mediafilez.forgecdn.net/files/{701 + index}/generated-fixture.jar",
                SizeBytes = size,
                ProviderSha1 = new string((char)('a' + index), 40),
                RequiredBy =
                [
                    new CurseForgeGeneratedFileEvidence
                    {
                        Relation = CurseForgeGeneratedFileRelation.ManifestRequired,
                        RequestedFileId = (701 + index).ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                    }
                ]
            });
            remaining -= size;
            index++;
        }
        return CurseForgeGeneratedPackPlanService.Seal(new CurseForgeGeneratedPackPlan
        {
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            LoaderVersion = "21.1.200",
            TotalResolvedBytes = totalBytes,
            RequiredFiles = files
        });
    }

    private string CreateUpdatePackage(
        string name,
        string mode,
        bool includeProviderArgumentFile = false)
    {
        var staging = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "mods"));
        Directory.CreateDirectory(Path.Combine(staging, "defaultconfigs"));
        File.WriteAllText(Path.Combine(staging, "mods", "new-pack.jar"), "pack-v2");
        File.WriteAllText(Path.Combine(staging, "defaultconfigs", "pack.txt"), "v2-default");
        File.WriteAllText(Path.Combine(staging, "run.bat"),
            $"@echo off\r\n{CommandLineQuoter.QuoteWindowsArgument(DotnetPath())} " +
            $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} {mode}\r\n");
        if (includeProviderArgumentFile)
        {
            var argumentDirectory = Path.Combine(staging, "libraries", "provider");
            Directory.CreateDirectory(argumentDirectory);
            File.WriteAllText(
                Path.Combine(argumentDirectory, "win_args.txt"),
                "provider-controlled argument sentinel");
        }
        var package = Path.Combine(root, name);
        ZipFile.CreateFromDirectory(staging, package);
        Directory.Delete(staging, recursive: true);
        return package;
    }

    private static void CreateSyntheticArchive(
        string path,
        string entryName,
        uint compressedBytes,
        uint expandedBytes)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)8);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(compressedBytes);
        writer.Write(expandedBytes);
        writer.Write(checked((ushort)name.Length));
        writer.Write((ushort)0);
        writer.Write(name);
        writer.Flush();
        stream.Position = checked(stream.Position + compressedBytes);

        var centralOffset = checked((uint)stream.Position);
        writer.Write(0x02014b50u);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)8);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(compressedBytes);
        writer.Write(expandedBytes);
        writer.Write(checked((ushort)name.Length));
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(name);
        writer.Flush();
        var centralSize = checked((uint)(stream.Position - centralOffset));

        writer.Write(0x06054b50u);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(centralSize);
        writer.Write(centralOffset);
        writer.Write((ushort)0);
    }

    private static UpdateSource Source(Guid serverId) => new()
    {
        ServerId = serverId,
        Provider = UpdateProvider.LocalPackageHistory,
        ProjectName = "Fixture Pack",
        ProjectId = "fixture-pack",
        InstalledVersionId = "v1",
        InstalledVersionName = "v1",
        MinecraftVersion = "1.21.1",
        Loader = "Custom",
        LoaderVersion = "fixture",
        InstalledAt = DateTimeOffset.UtcNow.AddDays(-1),
        IsUserLinked = true
    };

    private static UpdateInstallRequest Request(Guid serverId, string package, string version)
    {
        var bytes = File.ReadAllBytes(package);
        return new UpdateInstallRequest
        {
            OperationId = Guid.NewGuid(),
            ServerId = serverId,
            PlayerCountdownSeconds = 0,
            ConfirmedMigrationWarnings = true,
            StartForValidation = true,
            TargetVersion = new PackVersionInfo
            {
                PackId = "fixture-pack",
                VersionId = version,
                VersionName = version,
                PublishedAt = DateTimeOffset.UtcNow,
                ReleaseChannel = ReleaseChannel.Stable,
                MinecraftVersion = "1.21.1",
                Loader = "Custom",
                LoaderVersion = "fixture",
                DownloadUrl = package,
                FileName = Path.GetFileName(package),
                FileSize = bytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                PackageType = "zip"
            }
        };
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate ChunkPilot repository root.");
    }

    private static string DotnetPath() => IntegrationTestRuntime.DotnetPath(RepositoryRoot());

    private static string FakeServerDll()
    {
        var configuration = AppContext.BaseDirectory.Contains(@"\Release\", StringComparison.OrdinalIgnoreCase)
            ? "Release" : "Debug";
        return Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin",
            configuration, "net10.0", "ChunkPilot.FakeServer.dll");
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class RejectingCurseForgePreflight : ICurseForgeModpackPreflightService
    {
        public CurseForgeModpackPreflightRequest? Request { get; private set; }

        public Task<CurseForgeModpackPreflightResult> InspectAsync(
            CurseForgeModpackPreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new CurseForgeModpackPreflightResult
            {
                OperationId = request.OperationId,
                ProjectId = request.ProjectId,
                ClientFileId = request.ClientFileId,
                ServerPackFileId = request.ExpectedServerPackFileId,
                State = CatalogReleasePreflightState.Unsupported,
                Detail = "fixture exact manifest rejection"
            });
        }
    }

    private sealed class RecordingCurseForgePreflight(
        Func<CurseForgeModpackPreflightRequest, CurseForgeModpackPreflightResult> result,
        ICollection<string> ordering) : ICurseForgeModpackPreflightService
    {
        public Task<CurseForgeModpackPreflightResult> InspectAsync(
            CurseForgeModpackPreflightRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordering.Add("preflight");
            return Task.FromResult(result(request));
        }
    }

    private sealed class RecordingDownloadHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class FabricLoaderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath ?? "";
            HttpContent content = path.EndsWith("/server/jar", StringComparison.Ordinal)
                ? new ByteArrayContent("exact Fabric server launcher fixture"u8.ToArray())
                : path.Equals("/v2/versions/installer", StringComparison.Ordinal)
                    ? new StringContent("[{\"version\":\"1.0.3\",\"stable\":true}]", Encoding.UTF8,
                        "application/json")
                    : path.Equals("/v2/versions/loader/1.21.1", StringComparison.Ordinal)
                        ? new StringContent("[{\"loader\":{\"version\":\"0.16.14\"}}]", Encoding.UTF8,
                            "application/json")
                        : throw new InvalidOperationException($"Unexpected Fabric fixture request: {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = content
            });
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public bool Contains(string key) => values.ContainsKey(key);
        public void SetSecret(string key, string value) => values[key] = value;
        public string? GetSecret(string key) => values.GetValueOrDefault(key);
        public void Delete(string key) => values.Remove(key);
    }

    private sealed class FixedStorageSpaceProbe(long availableBytes) : IStorageSpaceProbe
    {
        public StorageVolumeSpace GetSpace(string path) =>
            new("fixture-volume", availableBytes);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        loggerFactory.Dispose();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
