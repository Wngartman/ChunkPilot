using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ChunkPilot.App;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class TerrariaFoundationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-terraria-unit-" + Guid.NewGuid().ToString("N"));

    public TerrariaFoundationTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Official_descriptor_is_exact_and_calls_local_hash_evidence_local()
    {
        var release = OfficialTerrariaProvider.CurrentRelease();
        Assert.Equal("1.4.5.6", release.Version);
        Assert.Equal("1456", release.ReleaseId);
        Assert.Equal(45_635_619, release.ExpectedSizeBytes);
        Assert.Equal("https://terraria.org/api/download/pc-dedicated-server/terraria-server-1456.zip", release.ArtifactUrl);
        Assert.Empty(release.Artifact.OfficialChecksum);
        Assert.Contains("local SHA-256", release.IntegrityEvidence, StringComparison.OrdinalIgnoreCase);
        Assert.False(release.Capabilities.AutomaticUpnp);
    }

    [Fact]
    public async Task Provider_rejects_an_unapproved_final_response_origin_before_reading_content()
    {
        using var http = new HttpClient(new ResponseHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://example.invalid/server.zip"),
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            response.Content.Headers.ContentLength = OfficialTerrariaProvider.CurrentPackageSize;
            return response;
        }));
        using var provider = new OfficialTerrariaProvider(http);
        await Assert.ThrowsAsync<InvalidDataException>(() => provider.GetArtifactAsync(
            OfficialTerrariaProvider.CurrentRelease(), Path.Combine(root, "redirect-cache")));
    }

    [Fact]
    public async Task Extractor_materializes_only_the_reviewed_windows_subtree()
    {
        var archive = Path.Combine(root, "valid.zip");
        CreateArchive(archive,
            ("1456/Windows/TerrariaServer.exe", PeFixture()),
            ("1456/Windows/serverconfig.txt", "fixture"u8.ToArray()),
            ("1456/Linux/TerrariaServer.bin.x86_64", "linux"u8.ToArray()));
        var destination = Path.Combine(root, "server");

        await OfficialTerrariaProvider.ExtractWindowsServerAsync(
            archive, OfficialTerrariaProvider.CurrentRelease(), destination);

        Assert.True(File.Exists(Path.Combine(destination, "TerrariaServer.exe")));
        Assert.True(File.Exists(Path.Combine(destination, "serverconfig.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "1456")));
        Assert.DoesNotContain(Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories),
            value => value.Contains("Linux", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("1456/Windows/../escape.txt")]
    [InlineData("1456/Windows/CON.txt")]
    [InlineData("1456/Windows/name:stream")]
    [InlineData("/1456/Windows/rooted.txt")]
    [InlineData("1456\\Windows\\backslash.txt")]
    public async Task Extractor_rejects_unsafe_archive_paths(string unsafeName)
    {
        var archive = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        CreateArchive(archive,
            ("1456/Windows/TerrariaServer.exe", PeFixture()),
            (unsafeName, "unsafe"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialTerrariaProvider.ExtractWindowsServerAsync(
                archive, OfficialTerrariaProvider.CurrentRelease(), Path.Combine(root, Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public async Task Extractor_rejects_case_equivalent_duplicates()
    {
        var archive = Path.Combine(root, "duplicates.zip");
        CreateArchive(archive,
            ("1456/Windows/TerrariaServer.exe", PeFixture()),
            ("1456/Windows/Library.dll", "one"u8.ToArray()),
            ("1456/Windows/library.dll", "two"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialTerrariaProvider.ExtractWindowsServerAsync(
                archive, OfficialTerrariaProvider.CurrentRelease(), Path.Combine(root, "duplicates-output")));
    }

    [Fact]
    public async Task Extractor_rejects_symlinks()
    {
        var archive = Path.Combine(root, "link.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            Write(zip.CreateEntry("1456/Windows/TerrariaServer.exe", CompressionLevel.NoCompression), PeFixture());
            var link = zip.CreateEntry("1456/Windows/link", CompressionLevel.NoCompression);
            link.ExternalAttributes = 0xA000 << 16;
            Write(link, "target"u8.ToArray());
        }
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            OfficialTerrariaProvider.ExtractWindowsServerAsync(
                archive, OfficialTerrariaProvider.CurrentRelease(), Path.Combine(root, "link-output")));
    }

    [Fact]
    public async Task Configuration_is_atomic_loopback_only_and_keeps_world_managed()
    {
        var staging = Path.Combine(root, "config-staging");
        var final = Path.Combine(root, "managed", "Terraria");
        var world = Path.Combine(final, "Worlds", "world.wld");
        var path = await TerrariaServerConfigurationWriter.WriteAsync(staging, final,
            new TerrariaServerConfiguration
            {
                WorldPath = world,
                WorldName = "Fixture World",
                Seed = "Seed 123",
                MaximumPlayers = 12,
                Port = 7_778,
                BindAddress = "127.0.0.1",
                EnableUpnp = false
            });
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains($"world={Path.GetFullPath(world)}", text, StringComparison.Ordinal);
        Assert.Contains("ip=127.0.0.1", text, StringComparison.Ordinal);
        Assert.Contains("upnp=0", text, StringComparison.Ordinal);
        Assert.Contains("seed=Seed 123", text, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(staging, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Configuration_rejects_external_world_and_public_binding()
    {
        var final = Path.Combine(root, "managed", "Terraria");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            TerrariaServerConfigurationWriter.WriteAsync(Path.Combine(root, "staging"), final,
                new TerrariaServerConfiguration { WorldPath = Path.Combine(root, "external.wld") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TerrariaServerConfigurationWriter.WriteAsync(Path.Combine(root, "staging2"), final,
                new TerrariaServerConfiguration
                {
                    WorldPath = Path.Combine(final, "Worlds", "world.wld"),
                    BindAddress = "0.0.0.0"
                }));
    }

    [Fact]
    public void Old_server_json_defaults_to_minecraft()
    {
        var value = JsonSerializer.Deserialize<ServerDefinition>(
            """{"id":"00000000-0000-0000-0000-000000000001","name":"Old","rootPath":"C:\\fixture"}""",
            ProtocolJson.Options);
        Assert.NotNull(value);
        Assert.Equal(ServerGameKind.Minecraft, value.GameKind);
        Assert.True(GameServerRuntimeProfiles.For(value).UsesMinecraftStatusProtocol);
    }

    [Fact]
    public void Experimental_preview_is_reachable_only_by_its_exact_flag()
    {
        Assert.True(TerrariaExperimentalPreviewLauncher.IsRequested(["--experimental-terraria-preview"]));
        Assert.True(TerrariaExperimentalPreviewLauncher.IsRequested(["--EXPERIMENTAL-TERRARIA-PREVIEW"]));
        Assert.False(TerrariaExperimentalPreviewLauncher.IsRequested([]));
        Assert.False(TerrariaExperimentalPreviewLauncher.IsRequested(["--webui-preview"]));
        Assert.False(TerrariaExperimentalPreviewLauncher.IsRequested(["--create-server-v2-preview"]));
    }

    private static void CreateArchive(string path, params (string Name, byte[] Bytes)[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries) Write(zip.CreateEntry(entry.Name, CompressionLevel.NoCompression), entry.Bytes);
    }

    private static void Write(ZipArchiveEntry entry, byte[] bytes)
    {
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static byte[] PeFixture()
    {
        var bytes = new byte[65 * 1024];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }


    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
