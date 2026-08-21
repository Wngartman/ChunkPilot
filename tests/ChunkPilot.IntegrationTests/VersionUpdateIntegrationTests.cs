using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
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
        var request = Request(definition.Id, package, "v2");

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
        Assert.Equal(VersionHealth.PendingValidation, active.Health);
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

    private ManagedServer CreateManaged(ServerDefinition definition) =>
        new(definition, new ProcessStatisticsProvider(), new MinecraftStatusClient(),
            store, paths, loggerFactory.CreateLogger<ManagedServer>(), consoleCapacity: 2_000);

    private ServerPackUpdateService CreateUpdateService()
    {
        var snapshots = new VersionSnapshotService(paths, store);
        return new ServerPackUpdateService(paths, store, snapshots, new PackMigrationPlanner(),
            new ServerDetectionService(new JavaDiscoveryService()),
            new WorldManager(paths, new SafeFileService(paths)));
    }

    private string CreateUpdatePackage(string name, string mode)
    {
        var staging = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "mods"));
        Directory.CreateDirectory(Path.Combine(staging, "defaultconfigs"));
        File.WriteAllText(Path.Combine(staging, "mods", "new-pack.jar"), "pack-v2");
        File.WriteAllText(Path.Combine(staging, "defaultconfigs", "pack.txt"), "v2-default");
        File.WriteAllText(Path.Combine(staging, "run.bat"),
            $"@echo off\r\n{CommandLineQuoter.QuoteWindowsArgument(DotnetPath())} " +
            $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} {mode}\r\n");
        var package = Path.Combine(root, name);
        ZipFile.CreateFromDirectory(staging, package);
        Directory.Delete(staging, recursive: true);
        return package;
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

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        loggerFactory.Dispose();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
