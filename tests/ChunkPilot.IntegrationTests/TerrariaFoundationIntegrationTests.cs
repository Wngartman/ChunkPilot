using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using System.Text.Json;

namespace ChunkPilot.IntegrationTests;

public sealed class TerrariaFoundationIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "ChunkPilot-terraria-integration-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;

    public async Task InitializeAsync()
    {
        paths = new AppDataPaths(Path.Combine(root, "appdata"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    [Fact]
    public async Task Terraria_installer_reuses_the_creation_transaction_without_minecraft_eula_state()
    {
        var provider = new FixtureTerrariaProvider();
        var installer = new TerrariaServerInstaller(paths, store, provider);
        var operationId = Guid.NewGuid();
        var result = await installer.InstallAsync(new TerrariaInstallRequest
        {
            OperationId = operationId,
            ServerName = "Terraria fixture",
            Configuration = new TerrariaServerConfiguration
            {
                WorldName = "Disposable World",
                Port = 7_779,
                BindAddress = "127.0.0.1"
            }
        });

        var definition = result.Definition;
        Assert.Equal(ServerGameKind.Terraria, definition.GameKind);
        Assert.Equal("1.4.5.6", definition.GameVersion);
        Assert.Equal("", definition.MinecraftVersion);
        Assert.Equal("save", definition.SaveCommand);
        Assert.Equal("exit", definition.StopCommand);
        Assert.Equal(VanillaNetworkingPreference.ThisComputerOnly, definition.CreationNetworkingPreference);
        Assert.True(File.Exists(definition.Executable));
        Assert.True(File.Exists(Path.Combine(definition.RootPath, "serverconfig.txt")));
        Assert.False(File.Exists(Path.Combine(definition.RootPath, "eula.txt")));
        Assert.True(ManagedInstanceOwnershipMarker.Proves(definition.RootPath, definition.Id));
        Assert.Contains(await store.GetServersAsync(), saved =>
            saved.Id == definition.Id && saved.GameKind == ServerGameKind.Terraria &&
            saved.GameVersion == "1.4.5.6" && saved.Executable == definition.Executable);
        Assert.Equal(provider.Release.ArtifactUrl, result.SourceUrl);
        Assert.Equal(FixtureTerrariaProvider.LocalHash, result.Sha256);
    }

    [Fact]
    public async Task Terraria_backup_manifest_retains_game_identity()
    {
        var serverRoot = Path.Combine(root, "backup-server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "Worlds"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "Worlds", "fixture.wld"), "world");
        var definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Terraria backup fixture",
            RootPath = serverRoot,
            WorkingDirectory = serverRoot,
            Executable = Path.Combine(serverRoot, "TerrariaServer.exe"),
            GameKind = ServerGameKind.Terraria,
            GameVersion = "1.4.5.6",
            MinecraftVersion = "",
            Ecosystem = ServerEcosystem.Custom,
            IsManaged = true
        };
        var backups = new BackupService(paths, store);
        var backup = await backups.CreateAsync(definition, backups.GetDefaultProfile(definition));
        var manifest = JsonSerializer.Deserialize<BackupManifest>(
            await File.ReadAllTextAsync(backup.ManifestPath), ProtocolJson.Options)!;
        Assert.Equal(ServerGameKind.Terraria, manifest.GameKind);
        Assert.Equal("1.4.5.6", manifest.GameVersion);
        Assert.Contains(manifest.Files, file => file.RelativePath.Replace('\\', '/') == "Worlds/fixture.wld");
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FixtureTerrariaProvider : ITerrariaServerProvider
    {
        internal const string LocalHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public TerrariaReleaseDescriptor Release => OfficialTerrariaProvider.CurrentRelease();

        public async Task<TerrariaMaterializationResult> DownloadAndMaterializeAsync(
            string cacheRoot,
            string destination,
            IProgress<InstallProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(destination);
            var executable = Path.Combine(destination, "TerrariaServer.exe");
            var bytes = new byte[65 * 1024];
            bytes[0] = (byte)'M';
            bytes[1] = (byte)'Z';
            await File.WriteAllBytesAsync(executable, bytes, cancellationToken);
            return new TerrariaMaterializationResult
            {
                Release = Release,
                ExecutablePath = executable,
                LocalSha256 = LocalHash,
                DownloadedBytes = bytes.Length,
                CachePath = Path.Combine(cacheRoot, "fixture.zip")
            };
        }
    }
}
