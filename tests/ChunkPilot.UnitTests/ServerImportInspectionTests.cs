using System.IO.Compression;
using System.Text;
using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ServerImportInspectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-import-tests", Guid.NewGuid().ToString("N"));

    public ServerImportInspectionTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Generic_server_zip_is_reviewed_and_extracted_without_executing_scripts()
    {
        var zip = Path.Combine(root, "paper-1.21.8.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Write(archive, "server/server.jar", "fixture server bytes");
            Write(archive, "server/run.bat", "java -jar server.jar");
            Write(archive, "server/world/level.dat", "world");
            Write(archive, "server/plugins/Example.jar", "plugin");
        }
        var reviewed = await new ServerImportInspectionService().InspectFileAsync(zip);
        Assert.Equal(ServerImportSourceKind.ServerArchive, reviewed.SourceKind);
        Assert.True(reviewed.CanInstall);
        Assert.Equal("Paper", reviewed.Platform);
        Assert.Equal("1.21.8", reviewed.MinecraftVersion);
        Assert.True(reviewed.ContainsWorld);
        Assert.Equal(1, reviewed.PluginCount);
        Assert.Equal("server/server.jar", Assert.Single(reviewed.LaunchCandidates));
        Assert.Contains(reviewed.Warnings, warning => warning.Contains("never executed", StringComparison.Ordinal));

        var destination = Path.Combine(root, "extract");
        await ServerImportInspectionService.ExtractAsync(zip, destination);
        Assert.True(File.Exists(Path.Combine(destination, "server", "server.jar")));
        Assert.Equal("java -jar server.jar", await File.ReadAllTextAsync(Path.Combine(destination, "server", "run.bat")));
    }

    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("C:/outside.jar")]
    [InlineData("server/CON.txt")]
    [InlineData("server/name. ")]
    public async Task Unsafe_archive_paths_are_rejected(string path)
    {
        var zip = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) Write(archive, path, "payload");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ServerImportInspectionService().InspectFileAsync(zip));
    }

    [Fact]
    public async Task Case_colliding_and_link_entries_are_rejected()
    {
        var collision = Path.Combine(root, "collision.zip");
        using (var archive = ZipFile.Open(collision, ZipArchiveMode.Create))
        {
            Write(archive, "Server/server.jar", "one");
            Write(archive, "server/SERVER.jar", "two");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ServerImportInspectionService().InspectFileAsync(collision));

        var link = Path.Combine(root, "link.zip");
        using (var archive = ZipFile.Open(link, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("server/link");
            entry.ExternalAttributes = 0xA000 << 16;
            await using var stream = entry.Open();
            await stream.WriteAsync("target"u8.ToArray());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ServerImportInspectionService().InspectFileAsync(link));
    }

    [Fact]
    public void Provider_archive_forecast_uses_the_exact_bounded_central_directory_total()
    {
        var archive = Path.Combine(root, "expanded-provider.zip");
        const uint compressedBytes = 4 * 1024 * 1024;
        const uint expandedBytes = 768 * 1024 * 1024;
        CreateSyntheticArchive(
            archive,
            "server/server.jar",
            compressedBytes,
            expandedBytes);

        var forecast = ServerImportInspectionService.ForecastArchiveExpansion(archive);

        Assert.Equal(expandedBytes, forecast.ExpandedSizeBytes);
        Assert.Equal(1, forecast.FileCount);
    }

    [Fact]
    public void Provider_archive_forecast_rejects_encrypted_and_unsafe_metadata()
    {
        var encrypted = Path.Combine(root, "encrypted-provider.zip");
        CreateSyntheticArchive(
            encrypted,
            "server/server.jar",
            compressedBytes: 1,
            expandedBytes: 1,
            generalPurposeBitFlag: 1,
            compressionMethod: 0);
        Assert.Throws<InvalidDataException>(() =>
            ServerImportInspectionService.ForecastArchiveExpansion(encrypted));

        var unsupported = Path.Combine(root, "unsupported-provider.zip");
        CreateSyntheticArchive(
            unsupported,
            "server/server.jar",
            compressedBytes: 1,
            expandedBytes: 1,
            compressionMethod: 99);
        Assert.Throws<InvalidDataException>(() =>
            ServerImportInspectionService.ForecastArchiveExpansion(unsupported));

        var unsafeArchive = Path.Combine(root, "unsafe-provider.zip");
        using (var archive = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
            Write(archive, "../outside.jar", "payload");
        Assert.Throws<InvalidDataException>(() =>
            ServerImportInspectionService.ForecastArchiveExpansion(unsafeArchive));
    }

    [Fact]
    public void Native_import_tokens_are_single_use_and_expire()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new WebUiServerImportTokenStore(() => now);
        var file = Path.Combine(root, "server.zip");
        File.WriteAllText(file, "fixture");
        var token = store.Issue(file, new ServerImportInspection
        {
            SourceKind = ServerImportSourceKind.ServerArchive,
            SourceSizeBytes = 7,
            Sha256 = new string('a', 64),
            CanInstall = true
        });
        Assert.Equal(Path.GetFullPath(file), store.Consume(token.Token).Path);
        Assert.Throws<ArgumentException>(() => store.Consume(token.Token));

        var expiring = store.Issue(file, new ServerImportInspection { CanInstall = true });
        now = now.AddMinutes(6);
        Assert.Throws<ArgumentException>(() => store.Consume(expiring.Token));
    }

    private static void Write(ZipArchive archive, string path, string value)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(value);
    }

    private static void CreateSyntheticArchive(
        string path,
        string entryName,
        uint compressedBytes,
        uint expandedBytes,
        ushort generalPurposeBitFlag = 0,
        ushort compressionMethod = 8)
    {
        var name = Encoding.UTF8.GetBytes(entryName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(0x04034b50u);
        writer.Write((ushort)20);
        writer.Write(generalPurposeBitFlag);
        writer.Write(compressionMethod);
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
        writer.Write(generalPurposeBitFlag);
        writer.Write(compressionMethod);
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

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
