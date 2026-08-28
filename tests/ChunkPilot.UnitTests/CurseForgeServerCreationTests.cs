using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeServerCreationTests
{
    [Fact]
    public async Task Official_server_pack_is_verified_validated_and_promoted_transactionally()
    {
        var fixture = await Fixture.CreateAsync(validationSucceeds: true);
        await using var cleanup = fixture;

        var result = await fixture.Installer.InstallAsync(fixture.Request);

        Assert.True(Directory.Exists(result.Definition.RootPath));
        Assert.Equal(ServerEcosystem.Forge, result.Definition.Ecosystem);
        Assert.Equal("47.3.0", result.Definition.LoaderVersion);
        Assert.Equal(1, fixture.Validator.Calls);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, ".chunkpilot", "update-source.json")));
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, ".chunkpilot", "staged-validation.json")));
        var source = JsonSerializer.Deserialize<UpdateSource>(await File.ReadAllTextAsync(
            Path.Combine(result.Definition.RootPath, ".chunkpilot", "update-source.json")), ProtocolJson.Options)!;
        Assert.Equal("123", source.ProjectId);
        Assert.Equal("fixture-pack", source.ProjectSlug);
        Assert.Contains(await fixture.Store.GetServersAsync(), server => server.Id == result.Definition.Id);
        Assert.DoesNotContain("fixture-key", await File.ReadAllTextAsync(
            Path.Combine(result.Definition.RootPath, ".chunkpilot", "update-source.json")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_staged_validation_does_not_promote_or_register_server()
    {
        var fixture = await Fixture.CreateAsync(validationSucceeds: false);
        await using var cleanup = fixture;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Installer.InstallAsync(fixture.Request));

        Assert.Contains("could not build a working server", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ManagedServers,
            ManagedServerInstaller.MakeSafeInstanceName(fixture.Request.ServerName))));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.Paths.ManagedServers), path =>
            Path.GetFileName(path).StartsWith(".chunkpilot-creating-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cancellation_during_staged_validation_is_safe_and_never_promotes()
    {
        var fixture = await Fixture.CreateAsync(validationSucceeds: true, cancelValidation: true);
        await using var cleanup = fixture;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Installer.InstallAsync(fixture.Request, cancellationToken: cancellation.Token));

        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.Empty(Directory.EnumerateDirectories(fixture.Paths.ManagedServers));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly CurseForgeApiClient api;
        private readonly HttpClient http;

        private Fixture(string root, AppDataPaths paths, ChunkPilotStore store, ManagedServerInstaller installer,
            ServerInstallRequest request, FakeValidator validator, CurseForgeApiClient api, HttpClient http)
        {
            this.root = root;
            Paths = paths;
            Store = store;
            Installer = installer;
            Request = request;
            Validator = validator;
            this.api = api;
            this.http = http;
        }

        public AppDataPaths Paths { get; }
        public ChunkPilotStore Store { get; }
        public ManagedServerInstaller Installer { get; }
        public ServerInstallRequest Request { get; }
        public FakeValidator Validator { get; }

        public static async Task<Fixture> CreateAsync(bool validationSucceeds, bool cancelValidation = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-create-" + Guid.NewGuid().ToString("N"));
            var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
            paths.EnsureCreated();
            var store = new ChunkPilotStore(paths);
            await store.InitializeAsync();
            var serverPack = ServerPack();
            var sha1 = Sha1(serverPack);
            var secrets = new MemorySecrets();
            secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
            var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(serverPack)
            }));
            var api = new CurseForgeApiClient(secrets, http);
            var validator = new FakeValidator(validationSucceeds, cancelValidation);
            var installer = new ManagedServerInstaller(paths, store, new ServerDownloadCatalog(http), http,
                curseForge: api, stagedValidator: validator);
            var java = Path.Combine(root, "java.exe");
            await File.WriteAllBytesAsync(java, [1]);
            var request = new ServerInstallRequest
            {
                SourceType = InstallSourceType.CurseForgeServerPack,
                Source = "https://mediafilez.forgecdn.net/files/222/fixture-server.zip",
                MinecraftVersion = "1.20.1",
                Build = "47.3.0",
                ServerName = "CurseForge Fixture",
                InstanceRoot = paths.ManagedServers,
                JavaPath = java,
                MinimumRamMb = 1024,
                MaximumRamMb = 2048,
                Port = 25565,
                EulaAccepted = true,
                EulaAcceptedAt = DateTimeOffset.UtcNow,
                ExpectedSha1 = sha1,
                ExpectedSizeBytes = serverPack.LongLength,
                PackProvider = UpdateProvider.CurseForge,
                PackProjectId = "123",
                PackProjectSlug = "fixture-pack",
                PackProjectName = "Fixture Pack",
                PackVersionId = "111",
                PackServerFileId = "222",
                PackVersionName = "1.0.0",
                PackLoader = "Forge",
                PackLoaderVersion = "47.3.0"
            };
            return new Fixture(root, paths, store, installer, request, validator, api, http);
        }

        public async ValueTask DisposeAsync()
        {
            api.Dispose();
            http.Dispose();
            await Store.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        private static byte[] ServerPack()
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("server.jar", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write("synthetic-server-jar"u8);
            }
            return output.ToArray();
        }

        private static string Sha1(byte[] value)
        {
#pragma warning disable CA5350 // Fixture mirrors CurseForge's published provider digest.
            return Convert.ToHexString(SHA1.HashData(value)).ToLowerInvariant();
#pragma warning restore CA5350
        }
    }

    private sealed class FakeValidator(bool succeeds, bool cancel) : IStagedServerValidator
    {
        public int Calls { get; private set; }

        public Task<StagedServerValidationResult> ValidateAsync(string javaPath, string stagingRoot,
            string launchRelativePath, bool usesArgumentFile, TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (cancel) throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(new StagedServerValidationResult(succeeds, succeeds, succeeds, succeeds,
                succeeds, succeeds ? "Fixture validation passed." : "Fixture validation failed.", []));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
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
