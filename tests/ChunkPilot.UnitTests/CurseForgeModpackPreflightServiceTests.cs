using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeModpackPreflightServiceTests
{
    private static readonly string[] MisleadingApiGameVersions = ["1.20.1", "Fabric"];

    [Fact]
    public async Task Official_server_pack_still_inspects_verified_client_manifest_for_exact_loader()
    {
        var root = TempRoot();
        try
        {
            var archive = ClientArchive("forge-47.3.0");
            var handler = new PreflightHandler(archive, serverPackFileId: 222);
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var api = new CurseForgeApiClient(secrets, handler);
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            var service = new CurseForgeModpackPreflightService(paths, api);
            var operationId = Guid.NewGuid();

            var result = await service.InspectAsync(new CurseForgeModpackPreflightRequest(
                operationId, "10", "111", "222"));

            Assert.Equal(CatalogReleasePreflightState.Ready, result.State);
            Assert.Equal("10", result.ProjectId);
            Assert.Equal("111", result.ClientFileId);
            Assert.Equal("222", result.ServerPackFileId);
            Assert.Equal("1.20.1", result.MinecraftVersion);
            Assert.Equal("Forge", result.Loader);
            Assert.Equal("47.3.0", result.LoaderVersion);
            Assert.Equal(17, result.RequiredJavaMajor);
            Assert.Equal(Sha1(archive), result.ClientSha1);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant(), result.ClientSha256);
            Assert.Equal(archive.LongLength, result.ClientSizeBytes);
            Assert.Equal(1, handler.CdnRequests);
            Assert.Equal("111", handler.DownloadedClientFileId);
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unsupported_manifest_returns_clear_result_and_removes_operation_staging()
    {
        var root = TempRoot();
        try
        {
            var archive = ClientArchive("fabric-0.15.11", primary: false);
            var handler = new PreflightHandler(archive);
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            using var api = new CurseForgeApiClient(secrets, handler);
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));

            var result = await new CurseForgeModpackPreflightService(paths, api).InspectAsync(
                new CurseForgeModpackPreflightRequest(Guid.NewGuid(), "10", "111", ""));

            Assert.Equal(CatalogReleasePreflightState.Unsupported, result.State);
            Assert.Contains("cannot establish a supported server loader", result.Detail,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(result.LoaderVersion);
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Hash_failure_and_cancellation_remove_operation_staging()
    {
        var root = TempRoot();
        try
        {
            var archive = ClientArchive("fabric-0.15.11");
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            using (var badApi = new CurseForgeApiClient(secrets,
                       new PreflightHandler(archive, sha1: new string('a', 40))))
            {
                await Assert.ThrowsAsync<InvalidDataException>(() =>
                    new CurseForgeModpackPreflightService(paths, badApi).InspectAsync(
                        new CurseForgeModpackPreflightRequest(Guid.NewGuid(), "10", "111", "")));
            }
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));

            using var cancelledApi = new CurseForgeApiClient(secrets,
                new PreflightHandler(archive));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new CurseForgeModpackPreflightService(paths, cancelledApi).InspectAsync(
                    new CurseForgeModpackPreflightRequest(Guid.NewGuid(), "10", "111", ""),
                    cancellation.Token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.Staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CurseForge_creation_plan_requires_verified_client_manifest_proof()
    {
        var plan = new ModpackCreationPlan
        {
            SourceKind = ModpackCreationSource.CurseForgeOfficialServerPack,
            Source = "https://mediafilez.forgecdn.net/files/222/server.zip",
            Provider = UpdateProvider.CurseForge,
            ProjectId = "10",
            VersionId = "111",
            ServerPackFileId = "222",
            MinecraftVersion = "1.20.1",
            Loader = "Forge",
            LoaderVersion = "47.3.0",
            RequiredJavaMajor = 17,
            ExpectedSha1 = new string('b', 40),
            ExpectedSizeBytes = 1_024,
            ServerName = "Verified Pack",
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            }
        };

        Assert.Contains(plan.Problems(), problem =>
            problem.Contains("client manifest", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain((plan with { VerifiedClientArchiveSha256 = new string('c', 64) }).Problems(),
            problem => problem.Contains("client manifest", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] ClientArchive(string loader, bool primary = true)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new
            {
                manifestType = "minecraftModpack",
                manifestVersion = 1,
                name = "Fixture Pack",
                version = "1.0.0",
                author = "Fixture",
                overrides = "overrides",
                minecraft = new
                {
                    version = "1.20.1",
                    modLoaders = new[] { new { id = loader, primary } }
                },
                files = new[] { new { projectID = 20, fileID = 200, required = true } }
            });
            stream.Write(manifest);
        }
        return output.ToArray();
    }

    private static string Sha1(byte[] bytes)
    {
#pragma warning disable CA5350 // Fixture mirrors CurseForge's published provider digest.
        return Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA5350
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class PreflightHandler(
        byte[] archive,
        long? serverPackFileId = null,
        string? sha1 = null) : HttpMessageHandler
    {
        private int cdnRequests;
        public int CdnRequests => cdnRequests;
        public string DownloadedClientFileId { get; private set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (uri.Host.Equals(CurseForgeApiClient.ApiHost, StringComparison.OrdinalIgnoreCase))
            {
                if (uri.AbsolutePath == "/v1/mods/10")
                    return Task.FromResult(Json(new
                    {
                        data = new
                        {
                            id = 10, gameId = 432, classId = 4471,
                            isAvailable = true, allowModDistribution = true
                        }
                    }));
                if (uri.AbsolutePath == "/v1/mods/10/files/111")
                {
                    DownloadedClientFileId = "111";
                    return Task.FromResult(Json(new
                    {
                        data = new
                        {
                            id = 111,
                            modId = 10,
                            isAvailable = true,
                            fileName = "fixture-client.zip",
                            fileLength = archive.LongLength,
                            serverPackFileId,
                            gameVersions = MisleadingApiGameVersions,
                            downloadUrl = "https://mediafilez.forgecdn.net/files/111/fixture-client.zip",
                            hashes = new[] { new { algo = 1, value = sha1 ?? Sha1(archive) } }
                        }
                    }));
                }
                throw new InvalidOperationException(uri.ToString());
            }
            if (uri.Host.EndsWith("forgecdn.net", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref cdnRequests);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(archive)
                });
            }
            throw new InvalidOperationException(uri.ToString());
        }

        private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public void SetSecret(string name, string value) => values[name] = value;
        public string? GetSecret(string name) => values.GetValueOrDefault(name);
        public bool Contains(string name) => values.ContainsKey(name);
        public void Delete(string name) => values.Remove(name);
    }
}
