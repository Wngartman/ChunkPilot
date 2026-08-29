using System.IO.Compression;
using System.Security.Cryptography;
using ChunkPilot.Agent;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Extensions.Logging;

namespace ChunkPilot.IntegrationTests;

public sealed class CertificationUpdateFaultIntegrationTests : IAsyncLifetime
{
    private const string FaultToken =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-update-fault-integration-" + Guid.NewGuid().ToString("N"));
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
    public async Task Exact_one_shot_fault_after_switch_runs_real_rollback_and_restores_running_server()
    {
        var definition = await CreateOldServerAsync();
        var source = Source(definition.Id);
        await store.UpsertServerAsync(definition);
        await store.UpsertUpdateSourceAsync(source);
        var faults = CertificationUpdateFaultInjector.CreateForTesting(FaultToken);
        await using var managed = new ManagedServer(
            definition,
            new ProcessStatisticsProvider(),
            new MinecraftStatusClient(),
            store,
            paths,
            loggerFactory.CreateLogger<ManagedServer>(),
            consoleCapacity: 2_000,
            certificationUpdateFaults: faults);
        Assert.True((await managed.StartAsync()).Success);

        var package = CreateUpdatePackage();
        var request = Request(definition.Id, package);
        Assert.True(faults.Arm(definition.Id, request.OperationId, FaultToken).Success);
        var service = CreateUpdateService();

        var result = await managed.RunExclusivePackUpdateAsync(
            request,
            (server, token) => service.PrepareAndSwitchAsync(
                server, source, request, cancellationToken: token),
            (server, snapshot, operation, token) => service.RollbackAsync(
                server, snapshot, operation, token),
            (prepared, token) => service.FinalizeOperationAsync(prepared, token));

        Assert.False(result.Success);
        Assert.True(result.RolledBack, result.Message);
        Assert.Equal(ServerState.Running, managed.State);
        Assert.Contains("injected", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("world-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "world", "level.dat")));
        Assert.Equal("old-pack-v1", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "mods", "old-pack.jar")));
        Assert.Equal("user-note", await File.ReadAllTextAsync(
            Path.Combine(definition.RootPath, "notes", "mine.txt")));
        Assert.False(File.Exists(Path.Combine(definition.RootPath, "mods", "new-pack.jar")));
        Assert.False(faults.TryConsume(definition.Id, request.OperationId));

        var snapshots = await store.GetVersionSnapshotsAsync(definition.Id);
        Assert.Contains(snapshots, snapshot => snapshot.IsActive && snapshot.VersionId == "v1");
        Assert.DoesNotContain(snapshots, snapshot =>
            snapshot.IsActive && snapshot.VersionId == "v2-controlled-failure");
        Assert.True((await managed.StopAsync()).Success);
    }

    private async Task<ServerDefinition> CreateOldServerAsync()
    {
        var serverRoot = Path.Combine(root, "servers", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "world", "playerdata"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(serverRoot, "notes"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "level.dat"), "world-v1");
        await File.WriteAllTextAsync(
            Path.Combine(serverRoot, "world", "playerdata", "player.dat"), "player-v1");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "motd=user-setting");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "mods", "old-pack.jar"), "old-pack-v1");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "notes", "mine.txt"), "user-note");
        var port = GetFreePort();
        return new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Controlled update failure fixture",
            RootPath = serverRoot,
            Executable = DotnetPath(),
            Arguments = $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal",
            WorkingDirectory = serverRoot,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CHUNKPILOT_FAKE_STATUS_PORT"] =
                    port.ToString(System.Globalization.CultureInfo.InvariantCulture)
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

    private ServerPackUpdateService CreateUpdateService()
    {
        var snapshots = new VersionSnapshotService(paths, store);
        return new ServerPackUpdateService(
            paths,
            store,
            snapshots,
            new PackMigrationPlanner(),
            new ServerDetectionService(new JavaDiscoveryService()),
            new WorldManager(paths, new SafeFileService(paths)));
    }

    private string CreateUpdatePackage()
    {
        var staging = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(staging, "mods"));
        Directory.CreateDirectory(Path.Combine(staging, "defaultconfigs"));
        File.WriteAllText(Path.Combine(staging, "mods", "new-pack.jar"), "pack-v2");
        File.WriteAllText(Path.Combine(staging, "defaultconfigs", "pack.txt"), "v2-default");
        File.WriteAllText(Path.Combine(staging, "run.bat"),
            $"@echo off\r\n{CommandLineQuoter.QuoteWindowsArgument(DotnetPath())} " +
            $"{CommandLineQuoter.QuoteWindowsArgument(FakeServerDll())} normal\r\n");
        var package = Path.Combine(root, "v2-controlled-failure.zip");
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

    private static UpdateInstallRequest Request(Guid serverId, string package)
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
                VersionId = "v2-controlled-failure",
                VersionName = "v2-controlled-failure",
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
        return current?.FullName ??
               throw new DirectoryNotFoundException("Could not locate ChunkPilot repository root.");
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
