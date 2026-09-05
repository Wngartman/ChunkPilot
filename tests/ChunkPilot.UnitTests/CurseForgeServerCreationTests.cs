using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeServerCreationTests
{
    [Fact]
    public async Task Late_failure_retains_verified_input_restart_does_not_discard_and_retry_needs_no_archive_request()
    {
        await using var fixture = await Fixture.CreateAsync(validationSucceeds: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        var entry = (await fixture.Store.GetCreationJournalAsync(fixture.Request.OperationId))!.Entry!;
        Assert.Equal(CreationOutcome.StagingResumable, entry.Outcome);
        var inputs = new VerifiedCreationArchiveStore(fixture.Paths);
        var archive = await inputs.VerifyAsync(entry, CancellationToken.None);
        Assert.Equal(fixture.Request.ExpectedSizeBytes, new FileInfo(archive).Length);
        Assert.False(Directory.Exists(entry.CanonicalStaging));
        var report = Assert.Single(await new ServerCreationRecoveryService(fixture.Store).RecoverAsync());
        Assert.Equal(CreationOutcome.StagingResumable, report.Outcome);
        Assert.Equal(0, (await fixture.Store.GetCreationJournalAsync(entry.OperationId))!.Entry!.RecoveryAttempts);
        fixture.Validator.Succeeds = true;
        fixture.RefuseDownloads = true;
        var result = await fixture.Installer.InstallAsync(fixture.Request with { RetryVerifiedInput = true });
        Assert.Equal(entry.ServerId, result.Definition.Id);
        Assert.Single(await fixture.Store.GetServersAsync());
        Assert.Equal(2, fixture.Validator.Calls);
    }

    [Fact]
    public async Task Altered_retained_archive_is_rejected_without_download_or_promotion()
    {
        await using var fixture = await Fixture.CreateAsync(validationSucceeds: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request));
        var archive = new VerifiedCreationArchiveStore(fixture.Paths).ArchiveFor(fixture.Request.OperationId);
        await File.AppendAllTextAsync(archive, "tamper");
        fixture.RefuseDownloads = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Installer.InstallAsync(fixture.Request with { RetryVerifiedInput = true }));
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.True(File.Exists(archive));
    }

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
        Assert.Equal(fixture.Request.MinimumRamMb, fixture.Validator.MinimumRamMb);
        Assert.Equal(fixture.Request.MaximumRamMb, fixture.Validator.MaximumRamMb);
        Assert.Equal(TimeSpan.FromMinutes(10), fixture.Validator.Timeout);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, ".chunkpilot", "update-source.json")));
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, ".chunkpilot", "staged-validation.json")));
        var source = JsonSerializer.Deserialize<UpdateSource>(await File.ReadAllTextAsync(
            Path.Combine(result.Definition.RootPath, ".chunkpilot", "update-source.json")), ProtocolJson.Options)!;
        Assert.Equal("123", source.ProjectId);
        Assert.Empty(source.ProjectSlug);
        Assert.Empty(source.ProjectName);
        Assert.Empty(source.InstalledVersionName);
        Assert.Empty(source.SourceUrl);
        Assert.Contains(await fixture.Store.GetServersAsync(), server => server.Id == result.Definition.Id);
        Assert.DoesNotContain("fixture-key", await File.ReadAllTextAsync(
            Path.Combine(result.Definition.RootPath, ".chunkpilot", "update-source.json")),
            StringComparison.Ordinal);
        var properties = await File.ReadAllTextAsync(Path.Combine(result.Definition.RootPath, "server.properties"));
        Assert.Contains("server-ip=127.0.0.1", properties, StringComparison.Ordinal);
        Assert.Contains("enable-query=false", properties, StringComparison.Ordinal);
        Assert.Contains("enable-rcon=false", properties, StringComparison.Ordinal);
        Assert.Contains("broadcast-rcon-to-ops=false", properties, StringComparison.Ordinal);

        var durable = new StringBuilder(File.Exists(result.StagingLogPath)
            ? await File.ReadAllTextAsync(result.StagingLogPath)
            : "");
        await using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            foreach (var query in new[]
                     {
                         "SELECT source, sha256, detail FROM instance_history",
                         "SELECT json FROM creation_journal"
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = query;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    for (var index = 0; index < reader.FieldCount; index++)
                        durable.Append('|').Append(reader.GetString(index));
            }
        }
        var durableText = durable.ToString();
        Assert.DoesNotContain(fixture.Request.Source, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Request.PackProjectName, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Request.PackVersionName, durableText, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Request.ExpectedSha1, durableText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-validation-tail-sentinel", durableText, StringComparison.Ordinal);
        Assert.Contains(CurseForgePersistencePolicy.LocalCreationHistoryDetail, durableText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_staged_validation_does_not_promote_or_register_server()
    {
        var fixture = await Fixture.CreateAsync(validationSucceeds: false);
        await using var cleanup = fixture;

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Installer.InstallAsync(fixture.Request));

        Assert.Contains("Startup check could not finish", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("provider-validation-tail-sentinel", error.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.Store.GetServersAsync());
        Assert.False(Directory.Exists(Path.Combine(fixture.Paths.ManagedServers,
            ManagedServerInstaller.MakeSafeInstanceName(fixture.Request.ServerName))));
        Assert.DoesNotContain(Directory.EnumerateDirectories(fixture.Paths.ManagedServers), path =>
            Path.GetFileName(path).StartsWith(".chunkpilot-creating-", StringComparison.OrdinalIgnoreCase));

        var durable = new StringBuilder();
        foreach (var log in Directory.EnumerateFiles(
                     fixture.Paths.Root, "*.log", SearchOption.AllDirectories))
            durable.Append('|').Append(await File.ReadAllTextAsync(log));
        await using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            foreach (var query in new[]
                     {
                         "SELECT detail FROM instance_history",
                         "SELECT json FROM creation_journal"
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = query;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                    durable.Append('|').Append(reader.GetString(0));
            }
        }
        Assert.DoesNotContain(
            "provider-validation-tail-sentinel", durable.ToString(), StringComparison.Ordinal);
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

    [Fact]
    public void Official_launch_detection_never_treats_content_or_library_jars_as_the_server()
    {
        var root = TemporaryLaunchRoot();
        try
        {
            WriteFixture(Path.Combine(root, "mods", "additionallanterns-server-helper.jar"));
            WriteFixture(Path.Combine(root, "plugins", "paper-helper.jar"));
            WriteFixture(Path.Combine(root, "libraries", "forge-server.jar"));

            Assert.Null(ManagedServerInstaller.FindKnownServerPackLaunch(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Official_launch_detection_keeps_provider_Forge_argument_profile_inert()
    {
        var root = TemporaryLaunchRoot();
        try
        {
            var arguments = Path.Combine(root, "libraries", "net", "minecraftforge", "forge",
                "1.20.1-47.3.0", "win_args.txt");
            WriteFixture(arguments);
            WriteFixture(Path.Combine(root, "mods", "server-like-mod.jar"));

            Assert.Null(ManagedServerInstaller.FindKnownServerPackLaunch(root));
            Assert.True(File.Exists(arguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Official_launch_detection_selects_root_server_jar_and_rejects_ambiguity()
    {
        var root = TemporaryLaunchRoot();
        try
        {
            var server = Path.Combine(root, "server.jar");
            WriteFixture(server);
            WriteFixture(Path.Combine(root, "mods", "paper-server-addon.jar"));
            var launch = Assert.IsType<(string Path, bool UsesArgumentFile)>(
                ManagedServerInstaller.FindKnownServerPackLaunch(root));
            Assert.Equal(Path.GetFullPath(server), launch.Path);
            Assert.False(launch.UsesArgumentFile);

            WriteFixture(Path.Combine(root, "nested", "server.jar"));
            Assert.Throws<InvalidDataException>(() =>
                ManagedServerInstaller.FindKnownServerPackLaunch(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Official_launch_detection_does_not_trust_a_libraries_named_ancestor()
    {
        var parent = TemporaryLaunchRoot();
        var root = Path.Combine(parent, "libraries", "staging");
        Directory.CreateDirectory(root);
        try
        {
            WriteFixture(Path.Combine(root, "win_args.txt"));
            Assert.Null(ManagedServerInstaller.FindKnownServerPackLaunch(root));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string TemporaryLaunchRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-cf-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteFixture(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly CurseForgeApiClient api;
        private readonly HttpClient http;

        private Fixture(string root, AppDataPaths paths, ChunkPilotStore store, ManagedServerInstaller installer,
            ServerInstallRequest request, FakeValidator validator, CurseForgeApiClient api, HttpClient http, Handler handler)
        {
            this.root = root;
            Paths = paths;
            Store = store;
            Installer = installer;
            Request = request;
            Validator = validator;
            this.api = api;
            this.http = http;
            Handler = handler;
        }

        public AppDataPaths Paths { get; }
        public ChunkPilotStore Store { get; }
        public ManagedServerInstaller Installer { get; }
        public ServerInstallRequest Request { get; }
        public FakeValidator Validator { get; }
        private Handler Handler { get; }
        public bool RefuseDownloads { set => Handler.RefuseRequests = value; }

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
            var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(serverPack)
            });
            var http = new HttpClient(handler, disposeHandler: false);
            var api = new CurseForgeApiClient(secrets, handler);
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
                CreationNetworkingPreference = VanillaNetworkingPreference.ThisComputerOnly,
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
            return new Fixture(root, paths, store, installer, request, validator, api, http, handler);
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
        public bool Succeeds { get; set; } = succeeds;
        public int Calls { get; private set; }
        public int MinimumRamMb { get; private set; }
        public int MaximumRamMb { get; private set; }
        public TimeSpan Timeout { get; private set; }

        public Task<StagedServerValidationResult> ValidateAsync(string javaPath, string stagingRoot,
            string launchRelativePath, bool usesArgumentFile, int minimumRamMb, int maximumRamMb,
            TimeSpan timeout,
            IProgress<StagedValidationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            MinimumRamMb = minimumRamMb;
            MaximumRamMb = maximumRamMb;
            Timeout = timeout;
            if (cancel) throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(new StagedServerValidationResult(Succeeds, Succeeds, Succeeds, Succeeds,
                Succeeds, Succeeds ? "Fixture validation passed." : "Fixture validation failed.",
                ["provider-validation-tail-sentinel fixture-server.zip"]) { AttemptId = Guid.NewGuid() });
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public bool RefuseRequests { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (RefuseRequests)
                throw new InvalidOperationException("Retry must not request an archive payload.");
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
