using System.IO.Compression;
using System.Net;
using System.Text;
using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CreationWorldSourceTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-world-source-tests", Guid.NewGuid().ToString("N"));

    public CreationWorldSourceTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Folder_world_and_separate_dimensions_are_copied_without_touching_source()
    {
        var source = Path.Combine(root, "selected");
        WriteFile(Path.Combine(source, "Copper Harbor", "level.dat"), "main-world");
        WriteFile(Path.Combine(source, "Copper Harbor", "region", "r.0.0.mca"), "region");
        WriteFile(Path.Combine(source, "Copper Harbor", "session.lock"), "stale-lock");
        WriteFile(Path.Combine(source, "Copper Harbor_nether", "level.dat"), "nether");
        WriteFile(Path.Combine(source, "Copper Harbor_the_end", "level.dat"), "end");
        WriteFile(Path.Combine(source, "unrelated.txt"), "do not copy");

        var service = new CreationWorldSourceService();
        var reviewed = await service.InspectAsync(source, CreationWorldSourceKind.Folder);

        Assert.Equal("Copper Harbor", reviewed.WorldName);
        Assert.Equal("Copper Harbor", reviewed.MainWorldRelativePath);
        Assert.Equal("Copper Harbor_nether", reviewed.NetherWorldRelativePath);
        Assert.Equal("Copper Harbor_the_end", reviewed.EndWorldRelativePath);
        Assert.Equal(4, reviewed.FileCount);

        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        var updates = new List<CreationWorldCopyProgress>();
        await service.MaterializeAsync(reviewed, staging, new CollectingProgress<CreationWorldCopyProgress>(updates.Add));

        Assert.Equal("main-world", await File.ReadAllTextAsync(Path.Combine(staging, "Copper Harbor", "level.dat")));
        Assert.Equal("nether", await File.ReadAllTextAsync(Path.Combine(staging, "Copper Harbor_nether", "level.dat")));
        Assert.Equal("end", await File.ReadAllTextAsync(Path.Combine(staging, "Copper Harbor_the_end", "level.dat")));
        Assert.False(File.Exists(Path.Combine(staging, "Copper Harbor", "session.lock")));
        Assert.False(File.Exists(Path.Combine(staging, "unrelated.txt")));
        Assert.Equal("stale-lock", await File.ReadAllTextAsync(Path.Combine(source, "Copper Harbor", "session.lock")));
        var completed = Assert.Single(updates, update => update.CopiedFiles == reviewed.FileCount);
        Assert.Equal(reviewed.ExpandedSizeBytes, completed.CopiedBytes);
        Assert.Equal(reviewed.ExpandedSizeBytes, completed.TotalBytes);
    }

    [Fact]
    public async Task World_zip_is_safely_extracted_and_materialized_from_one_wrapper()
    {
        var zip = Path.Combine(root, "adventure-backup.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            WriteEntry(archive, "backup/Adventure/level.dat", "main");
            WriteEntry(archive, "backup/Adventure/data/map.dat", "map");
            WriteEntry(archive, "backup/Adventure_nether/level.dat", "nether");
        }

        var service = new CreationWorldSourceService();
        var reviewed = await service.InspectAsync(zip, CreationWorldSourceKind.ZipArchive);
        Assert.Equal("Adventure", reviewed.WorldName);
        Assert.Equal(3, reviewed.FileCount);
        Assert.Equal(64, reviewed.SourceFingerprint.Length);

        var staging = Path.Combine(root, "zip-staging");
        Directory.CreateDirectory(staging);
        await service.MaterializeAsync(reviewed, staging);

        Assert.Equal("main", await File.ReadAllTextAsync(Path.Combine(staging, "Adventure", "level.dat")));
        Assert.Equal("map", await File.ReadAllTextAsync(Path.Combine(staging, "Adventure", "data", "map.dat")));
        Assert.Equal("nether", await File.ReadAllTextAsync(Path.Combine(staging, "Adventure_nether", "level.dat")));
        Assert.True(File.Exists(zip));
        Assert.Empty(Directory.EnumerateDirectories(staging, ".chunkpilot-world-import-*"));
    }

    [Fact]
    public async Task Ambiguous_world_source_is_rejected()
    {
        var source = Path.Combine(root, "ambiguous");
        WriteFile(Path.Combine(source, "World One", "level.dat"), "one");
        WriteFile(Path.Combine(source, "World Two", "level.dat"), "two");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CreationWorldSourceService().InspectAsync(source, CreationWorldSourceKind.Folder));
        Assert.Contains("More than one main", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changed_source_is_rejected_before_copy_and_native_token_is_single_use()
    {
        var source = Path.Combine(root, "changing-world");
        WriteFile(Path.Combine(source, "level.dat"), "initial");
        var service = new CreationWorldSourceService();
        var reviewed = await service.InspectAsync(source, CreationWorldSourceKind.Folder);
        var store = new WebUiWorldSourceTokenStore(service);
        var token = store.Issue(reviewed);

        WriteFile(Path.Combine(source, "region", "r.0.0.mca"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ConsumeAsync(token.Token, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ConsumeAsync(token.Token, CancellationToken.None));

        var staging = Path.Combine(root, "changed-staging");
        Directory.CreateDirectory(staging);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.MaterializeAsync(reviewed, staging));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    [Fact]
    public async Task Managed_install_sets_level_name_and_activates_world_inside_transaction()
    {
        var source = Path.Combine(root, "Established World");
        WriteFile(Path.Combine(source, "level.dat"), "established");
        var world = await new CreationWorldSourceService().InspectAsync(source, CreationWorldSourceKind.Folder);
        var package = Path.Combine(root, "server.zip");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            WriteEntry(archive, "server.jar", "fixture server");
        var java = Path.Combine(root, "java.exe");
        await File.WriteAllTextAsync(java, "");
        var paths = new AppDataPaths(Path.Combine(root, "appdata"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var rejectNetwork = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No network expected.")));
        var installer = new ManagedServerInstaller(
            paths,
            store,
            new ServerDownloadCatalog(rejectNetwork),
            rejectNetwork);

        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            OperationId = Guid.NewGuid(),
            SourceType = InstallSourceType.LocalZip,
            Source = package,
            ServerName = "World Import Test",
            InstanceRoot = Path.Combine(root, "instances"),
            JavaPath = java,
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow,
            InitialWorld = world
        });

        Assert.Equal("established", await File.ReadAllTextAsync(
            Path.Combine(result.Definition.RootPath, "Established World", "level.dat")));
        Assert.Contains("level-name=Established World",
            await File.ReadAllTextAsync(Path.Combine(result.Definition.RootPath, "server.properties")),
            StringComparison.Ordinal);
        Assert.Equal("established", await File.ReadAllTextAsync(Path.Combine(source, "level.dat")));
    }

    private static void WriteFile(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value, new UTF8Encoding(false));
    }

    private static void WriteEntry(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class CollectingProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
