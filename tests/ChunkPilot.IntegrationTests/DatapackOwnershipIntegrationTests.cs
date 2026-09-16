using System.IO.Compression;
using System.Diagnostics;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.IntegrationTests;

public sealed class DatapackOwnershipIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-datapack-ownership-" + Guid.NewGuid().ToString("N"));

    [Fact(Timeout = 30_000)]
    public async Task Datapack_folder_junction_is_rejected_without_reading_or_copying_its_target()
    {
        var serverRoot = Path.Combine(root, "server");
        var world = Path.Combine(serverRoot, "world");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "preserved world");
        var source = Path.Combine(root, "source-pack");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "pack.mcmeta"),
            """{"pack":{"pack_format":48,"description":"fixture"}}""");
        var outside = Path.Combine(root, "outside-pack");
        Directory.CreateDirectory(outside);
        var privateFile = Path.Combine(outside, "must-not-read.txt");
        await File.WriteAllTextAsync(privateFile, "preserved private fixture");
        var link = Path.Combine(source, "outside-link");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!)
        {
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var paths = new AppDataPaths(Path.Combine(root, "data"));
            await using var store = new ChunkPilotStore(paths);
            await store.InitializeAsync();
            var locks = new CanonicalPathLockManager();
            var service = new DatapackManagementService(paths, store, new BackupService(paths, store),
                new DatapackService(), new SafeFileService(paths, locks), locks);
            var server = new ServerDefinition
            {
                Id = Guid.NewGuid(), Name = "Datapack link fixture", RootPath = serverRoot, MinecraftVersion = "1.21.1"
            };
            // An accidental traversal would hit a sharing violation, not the expected reparse refusal.
            await using (var held = new FileStream(privateFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    service.InstallAsync(server, new DatapackInstallRequest(server.Id, "world", source)));
                Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
            }
            Assert.Equal("preserved private fixture", await File.ReadAllTextAsync(privateFile));
            Assert.False(Directory.Exists(Path.Combine(world, "datapacks", "source-pack")));
            Assert.Empty(await store.GetDatapackInventoryAsync(server.Id));
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
        }
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Existing_user_pack_survives_collision_rejection_and_failed_replacement(
        bool zip, bool failAfterActivation)
    {
        var serverRoot = Path.Combine(root, "server");
        var world = Path.Combine(serverRoot, "world");
        var packs = Path.Combine(world, "datapacks");
        Directory.CreateDirectory(packs);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "preserved fixture world");
        var sourceFolder = Path.Combine(root, "owner-pack");
        Directory.CreateDirectory(sourceFolder);
        const string sourceMetadata = """{"pack":{"pack_format":48,"description":"new fixture content"}}""";
        await File.WriteAllTextAsync(Path.Combine(sourceFolder, "pack.mcmeta"), sourceMetadata);
        var source = sourceFolder;
        if (zip)
        {
            source += ".zip";
            ZipFile.CreateFromDirectory(sourceFolder, source);
        }
        var target = Path.Combine(packs, Path.GetFileName(source));
        if (!zip) Directory.CreateDirectory(target);
        var originalFile = zip ? target : Path.Combine(target, "owner-content.txt");
        await File.WriteAllTextAsync(originalFile, "irreplaceable original pack");
        var unrelatedFile = Path.Combine(packs, "unrelated.zip");
        await File.WriteAllTextAsync(unrelatedFile, "unrelated content");

        var paths = new AppDataPaths(Path.Combine(root, "data"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        if (failAfterActivation)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath
            }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER fixture_fail_datapack_insert BEFORE INSERT ON datapack_inventory
                BEGIN SELECT RAISE(ABORT, 'fixture failure after activation'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }
        var locks = new CanonicalPathLockManager();
        var service = new DatapackManagementService(paths, store, new BackupService(paths, store),
            new DatapackService(), new SafeFileService(paths, locks), locks);
        var server = new ServerDefinition
        {
            Id = Guid.NewGuid(), Name = "Datapack ownership fixture",
            RootPath = serverRoot, MinecraftVersion = "1.21.1"
        };
        var request = new DatapackInstallRequest(server.Id, "world", source, ReplaceExisting: failAfterActivation);

        if (failAfterActivation)
            await Assert.ThrowsAsync<SqliteException>(() => service.InstallAsync(server, request));
        else
            await Assert.ThrowsAsync<IOException>(() => service.InstallAsync(server, request));

        Assert.Equal("irreplaceable original pack", await File.ReadAllTextAsync(originalFile));
        Assert.Equal("unrelated content", await File.ReadAllTextAsync(unrelatedFile));
        Assert.Equal("preserved fixture world", await File.ReadAllTextAsync(Path.Combine(world, "level.dat")));
        Assert.Equal(sourceMetadata, await File.ReadAllTextAsync(Path.Combine(sourceFolder, "pack.mcmeta")));
        Assert.Empty(await store.GetDatapackInventoryAsync(server.Id));
        Assert.Single(await store.GetBackupsAsync(server.Id));
        Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
