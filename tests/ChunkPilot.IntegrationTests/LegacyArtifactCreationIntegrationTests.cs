using System.IO.Compression;
using System.Security.Cryptography;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class LegacyArtifactCreationIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "ChunkPilot-legacy-create-" + Guid.NewGuid().ToString("N"));
    private AppDataPaths paths = null!;
    private ChunkPilotStore store = null!;

    public async Task InitializeAsync()
    {
        paths = new AppDataPaths(Path.Combine(root, "appdata"), Path.Combine(root, "servers"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    [Fact]
    public async Task Reviewed_local_server_jar_is_copied_transactionally_without_mutating_or_recording_its_path()
    {
        var sourceFolder = Path.Combine(root, "user-owned-source");
        Directory.CreateDirectory(sourceFolder);
        var source = Path.Combine(sourceFolder, "minecraft_server.b1.8.1.jar");
        CreateServerJar(source);
        var before = await File.ReadAllBytesAsync(source);
        var hash = Convert.ToHexString(SHA256.HashData(before)).ToLowerInvariant();
        var installer = new ManagedServerInstaller(paths, store, new ServerDownloadCatalog());

        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            SourceType = InstallSourceType.LocalServerJar,
            Source = source,
            MinecraftVersion = "b1.8.1",
            Build = "user-supplied",
            ServerName = "Beta fixture",
            JavaPath = Environment.ProcessPath!,
            Port = 25_576,
            ExpectedSha256 = hash,
            ExpectedSizeBytes = before.Length,
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow,
            CreationNetworkingPreference = VanillaNetworkingPreference.ThisComputerOnly
        });

        Assert.Equal(ServerEcosystem.Vanilla, result.Definition.Ecosystem);
        Assert.Equal("b1.8.1", result.Definition.MinecraftVersion);
        Assert.Equal(before, await File.ReadAllBytesAsync(source));
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(result.Definition.RootPath, "server.jar")));
        Assert.StartsWith("user-supplied:", result.SourceUrl, StringComparison.Ordinal);
        Assert.DoesNotContain(sourceFolder, result.SourceUrl, StringComparison.OrdinalIgnoreCase);
        Assert.True(ManagedInstanceOwnershipMarker.Proves(result.Definition.RootPath, result.Definition.Id));
    }

    private static void CreateServerJar(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("META-INF/MANIFEST.MF");
        using (var writer = new StreamWriter(manifest.Open()))
            writer.Write("Manifest-Version: 1.0\nMain-Class: net.minecraft.server.MinecraftServer\n");
        for (var index = 0; index < 30; index++)
        {
            var entry = archive.CreateEntry(index == 0
                ? "net/minecraft/server/MinecraftServer.class"
                : $"net/minecraft/server/Fixture{index}.class");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("fixture");
        }
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
