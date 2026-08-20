using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class PaperCreationIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-paper-install-" + Guid.NewGuid().ToString("N"));

    public PaperCreationIntegrationTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Exact_reviewed_Paper_build_uses_the_hardened_creation_transaction()
    {
        var bytes = Encoding.UTF8.GetBytes("deterministic Paper server fixture");
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var handler = new PaperHandler(bytes, sha256);
        using var http = new HttpClient(handler);
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var java = Path.Combine(root, "java.exe");
        await File.WriteAllTextAsync(java, "fixture");
        var installer = new ManagedServerInstaller(paths, store, new ServerDownloadCatalog(http), http);

        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            OperationId = Guid.NewGuid(),
            SourceType = InstallSourceType.Paper,
            MinecraftVersion = "1.21.8",
            Build = "42",
            ExpectedSha256 = sha256,
            ServerName = "Exact Paper",
            InstanceRoot = paths.ManagedServers,
            JavaPath = java,
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(CreationOutcome.Completed, result.Outcome);
        Assert.Equal(ServerEcosystem.Paper, result.Definition.Ecosystem);
        Assert.Equal("1.21.8", result.Definition.MinecraftVersion);
        Assert.Equal("42", result.Definition.LoaderVersion);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, "server.jar")));
        Assert.Empty(await store.GetCreationJournalsAsync());
        Assert.Single(await store.GetServersAsync());
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class PaperHandler(byte[] artifact, string sha256) : HttpMessageHandler
    {
        private int requests;
        public int RequestCount => requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            if (request.RequestUri!.Host.Equals("fill.papermc.io", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        [{ "id": 42, "time": "2026-08-17T12:00:00Z", "channel": "STABLE",
                           "downloads": { "server:default": { "name": "paper-1.21.8-42.jar",
                             "checksums": { "sha256": "{{sha256}}" }, "size": {{artifact.Length}},
                             "url": "https://fill-data.papermc.io/v1/objects/fixture/paper.jar" } } }]
                        """, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(artifact)
            });
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
