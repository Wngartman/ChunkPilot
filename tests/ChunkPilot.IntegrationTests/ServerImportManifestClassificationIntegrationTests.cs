using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class ServerImportManifestClassificationIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "ChunkPilot-import-manifest-fixture-" + Guid.NewGuid().ToString("N"));

    private const string ServerPackCreatorManifest = """
        { "files": ["config", "mods/example-fabric.jar"], "minecraftVersion": "1.21.1",
          "modloader": "Fabric", "modloaderVersion": "0.16.10", "serverPackCreatorVersion": "7.1.4" }
        """;
    private const string CurseForgeManifest = """
        { "manifestType": "minecraftModpack", "manifestVersion": 1, "name": "Exact fixture pack",
          "version": "2.0.0", "author": "Fixture", "overrides": "overrides",
          "minecraft": { "version": "1.21.1", "modLoaders": [{ "id": "fabric-0.16.10", "primary": true }] },
          "files": [{ "projectID": 123, "fileID": 456, "required": true }] }
        """;

    public ServerImportManifestClassificationIntegrationTests() => Directory.CreateDirectory(root);

    [Theory(Timeout = 15_000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ServerPackCreator_manifest_is_generic_and_does_not_invent_a_launcher(bool hasLauncher)
    {
        var archive = CreateArchive(ServerPackCreatorManifest, hasLauncher);
        var result = await new ServerImportInspectionService().InspectFileAsync(archive);

        Assert.Equal(ServerImportSourceKind.ServerArchive, result.SourceKind);
        Assert.Equal(hasLauncher, result.CanInstall);
        Assert.Equal(1, result.ModCount);
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("CurseForge client manifest", StringComparison.Ordinal));
        if (hasLauncher)
            Assert.Equal("fabric-server-launch.jar", Assert.Single(result.LaunchCandidates));
        else
        {
            Assert.Empty(result.LaunchCandidates);
            Assert.Contains("No safe standalone server launcher", result.Limitation, StringComparison.Ordinal);
        }

        var destination = Path.Combine(root, "extracted");
        await ServerImportInspectionService.ExtractAsync(archive, destination);
        Assert.Equal(ServerPackCreatorManifest, await File.ReadAllTextAsync(Path.Combine(destination, "manifest.json")));
        Assert.Equal("fixture script remains data", await File.ReadAllTextAsync(Path.Combine(destination, "start.bat")));
    }

    [Theory(Timeout = 15_000)]
    [InlineData("{\"files\":[\"mods/example.jar\"]}")]
    [InlineData("{\"manifestVersion\":1,\"name\":\"Unrelated tool\"}")]
    [InlineData("{\"manifestType\":\"serverManifest\",\"files\":[]}")]
    public async Task Unrelated_manifest_fields_do_not_claim_CurseForge_identity(string manifest)
    {
        var result = await new ServerImportInspectionService().InspectFileAsync(CreateArchive(manifest, true));
        Assert.Equal(ServerImportSourceKind.ServerArchive, result.SourceKind);
        Assert.True(result.CanInstall);
    }

    [Fact(Timeout = 15_000)]
    public async Task Actual_CurseForge_manifest_keeps_exact_provider_classification()
    {
        var result = await new ServerImportInspectionService().InspectFileAsync(CreateArchive(CurseForgeManifest, false));
        Assert.Equal(ServerImportSourceKind.CurseForgePack, result.SourceKind);
        Assert.Equal("Exact fixture pack", result.DisplayName);
        Assert.Equal("1.21.1", result.MinecraftVersion);
        Assert.Equal("Fabric", result.Platform);
        Assert.Equal("0.16.10", result.LoaderVersion);
        Assert.True(result.CanInstall);
    }

    [Theory(Timeout = 15_000)]
    [InlineData("missing-type")]
    [InlineData("wrong-type")]
    [InlineData("wrong-version")]
    [InlineData("missing-minecraft")]
    [InlineData("invalid-files")]
    [InlineData("unsafe-overrides")]
    public async Task Malformed_CurseForge_pack_cannot_fall_back_to_a_generic_launcher(string failure)
    {
        var manifest = JsonNode.Parse(CurseForgeManifest)!.AsObject();
        switch (failure)
        {
            case "missing-type": manifest.Remove("manifestType"); break;
            case "wrong-type": manifest["manifestType"] = "other"; break;
            case "wrong-version": manifest["manifestVersion"] = 2; break;
            case "missing-minecraft": manifest.Remove("minecraft"); break;
            case "invalid-files": manifest["files"] = new JsonArray(); break;
            case "unsafe-overrides": manifest["overrides"] = "../outside"; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerImportInspectionService()
            .InspectFileAsync(CreateArchive(manifest.ToJsonString(), true)));
    }

    [Theory(Timeout = 15_000)]
    [InlineData("{")]
    [InlineData("")]
    public async Task Unclassifiable_manifest_fails_closed(string manifest)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerImportInspectionService()
            .InspectFileAsync(CreateArchive(manifest, true)));
    }

    [Fact(Timeout = 15_000)]
    public async Task Classification_remains_bounded_for_an_oversized_manifest()
    {
        var manifest = new string(' ', CurseForgePackManifestReader.MaximumManifestBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ServerImportInspectionService()
            .InspectFileAsync(CreateArchive(manifest, true)));
    }

    private string CreateArchive(string manifest, bool hasLauncher)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "manifest.json", manifest);
        Write(archive, "mods/example-fabric.jar", "fixture mod bytes");
        Write(archive, "start.bat", "fixture script remains data");
        if (hasLauncher) Write(archive, "fabric-server-launch.jar", "fixture launcher bytes; never executed");
        return path;
    }

    private static void Write(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
