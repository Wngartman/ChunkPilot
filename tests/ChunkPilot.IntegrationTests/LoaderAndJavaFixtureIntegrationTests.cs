using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class LoaderAndJavaFixtureIntegrationTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "ChunkPilot-loader-fixture-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("reviewed installer fixture");

    public LoaderAndJavaFixtureIntegrationTests() => Directory.CreateDirectory(root);

    [Theory(Timeout = 30_000)]
    [InlineData(InstallSourceType.Fabric)]
    [InlineData(InstallSourceType.Quilt)]
    [InlineData(InstallSourceType.Forge)]
    [InlineData(InstallSourceType.NeoForge)]
    public async Task Official_loader_adapters_install_transactionally_with_safe_fixtures(
        InstallSourceType loader)
    {
        using var http = new HttpClient(new LoaderHandler());
        var service = new LoaderInstallationService(
            new LoaderMetadataService(http),
            http);
        var staging = Path.Combine(root, loader.ToString());
        var result = await service.InstallAsync(
            loader,
            "1.21.1",
            "",
            FakeJavaPath(),
            staging,
            Path.Combine(root, loader + ".log"));
        Assert.True(File.Exists(result.LaunchFile));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(InstallerBytes)), result.DownloadSha256);
        if (loader is InstallSourceType.Forge or InstallSourceType.NeoForge)
            Assert.EndsWith("win_args.txt", result.ArgumentsFile, StringComparison.OrdinalIgnoreCase);
        if (loader == InstallSourceType.Quilt)
            Assert.EndsWith("quilt-server-launch.jar", result.LaunchFile, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(Timeout = 30_000)]
    public async Task Official_loader_ignores_preplanted_argument_profile_even_with_future_timestamp()
    {
        var staging = Path.Combine(root, "preplanted-arguments");
        var malicious = Path.Combine(staging, "libraries", "attacker", "win_args.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(malicious)!);
        await File.WriteAllTextAsync(malicious, "provider-controlled arguments");
        File.SetLastWriteTimeUtc(malicious, DateTime.UtcNow.AddDays(2));
        using var http = new HttpClient(new LoaderHandler());
        var service = new LoaderInstallationService(new LoaderMetadataService(http), http);

        var result = await service.InstallExactAsync(
            ForgeInstallerPlan("--installServer"),
            FakeJavaPath(),
            staging,
            Path.Combine(root, "preplanted-arguments.log"));

        Assert.Equal(
            Path.Combine(staging, "libraries", "fixture", "win_args.txt"),
            result.ArgumentsFile);
        Assert.Equal("provider-controlled arguments", await File.ReadAllTextAsync(malicious));
    }

    [Fact(Timeout = 30_000)]
    public async Task Official_loader_rejects_preplanted_server_jar_when_installer_creates_no_output()
    {
        var staging = Path.Combine(root, "preplanted-server-jar");
        Directory.CreateDirectory(staging);
        var malicious = Path.Combine(staging, "minecraft_server.1.21.1.jar");
        await File.WriteAllTextAsync(malicious, "provider-controlled server jar");
        using var http = new HttpClient(new LoaderHandler());
        var service = new LoaderInstallationService(new LoaderMetadataService(http), http);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallExactAsync(
                ForgeInstallerPlan("--fixture-no-installer-output"),
                FakeJavaPath(),
                staging,
                Path.Combine(root, "preplanted-server-jar.log")));

        Assert.Contains("newly created or content-changed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("provider-controlled server jar", await File.ReadAllTextAsync(malicious));
    }

    [Fact(Timeout = 30_000)]
    public async Task Quilt_creation_uses_separate_installer_Java_without_changing_server_runtime()
    {
        var paths = new AppDataPaths(Path.Combine(root, "quilt-split-java-data"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var http = new HttpClient(new LoaderHandler());
        var loader = new LoaderInstallationService(new LoaderMetadataService(http), http);
        var installer = new ManagedServerInstaller(
            paths, store, new ServerDownloadCatalog(http), http, loader);
        var runtimeJava = Path.Combine(root, "java-16-runtime.exe");
        await File.WriteAllTextAsync(runtimeJava, "fixture runtime identity");

        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            SourceType = InstallSourceType.Quilt,
            Source = "https://maven.quiltmc.org/repository/release/org/quiltmc/quilt-installer/0.15.1/quilt-installer-0.15.1.jar",
            MinecraftVersion = "1.17.1",
            Build = "0.30.0",
            InstallerVersion = "0.15.1",
            ServerName = "Quilt split Java fixture",
            InstanceRoot = Path.Combine(root, "quilt-split-java-servers"),
            JavaPath = runtimeJava,
            InstallerJavaPath = FakeJavaPath(),
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow,
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(InstallerBytes))
        });

        Assert.Equal(Path.GetFullPath(runtimeJava), result.Definition.Executable);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, "quilt-server-launch.jar")));
    }

    [Fact(Timeout = 30_000)]
    public async Task Managed_Java_fixture_is_verified_healthy_private_and_does_not_change_environment()
    {
        var archive = BuildRuntimeArchive();
        var expected = Convert.ToHexString(SHA256.HashData(archive));
        var paths = new AppDataPaths(Path.Combine(root, "appdata"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var http = new HttpClient(new BytesHandler(archive));
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalJavaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        var service = new ManagedJavaRuntimeService(
            paths,
            store,
            new FixtureJavaProvider(expected),
            http);
        var runtime = await service.InstallAsync(21);
        Assert.Equal(RuntimeHealth.Healthy, runtime.Health);
        Assert.Equal("x64", runtime.Architecture);
        Assert.Equal(21, runtime.MajorVersion);
        Assert.True(runtime.IsManaged);
        Assert.StartsWith(Path.GetFullPath(paths.ManagedJava), runtime.JavaPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PATH"));
        Assert.Equal(originalJavaHome, Environment.GetEnvironmentVariable("JAVA_HOME"));
        Assert.Single(await store.GetManagedJavaRuntimesAsync());
        Assert.Empty(Directory.EnumerateDirectories(paths.ManagedJava, ".staging-*"));
        Assert.Empty(Directory.EnumerateFiles(paths.Staging));
    }

    [Fact(Timeout = 30_000)]
    public async Task Wrong_Java_major_is_rejected_before_runtime_activation_or_registration()
    {
        var archive = BuildRuntimeArchive(); // The synthetic executable reports Java 21.
        var paths = new AppDataPaths(Path.Combine(root, "wrong-major-data"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var http = new HttpClient(new BytesHandler(archive));
        var service = new ManagedJavaRuntimeService(paths, store,
            new FixtureJavaProvider(Convert.ToHexString(SHA256.HashData(archive))), http);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(8));
        Assert.Contains("64-bit Java 8", error.Message, StringComparison.Ordinal);
        Assert.Empty(await store.GetManagedJavaRuntimesAsync());
        Assert.Empty(Directory.EnumerateDirectories(paths.ManagedJava));
        Assert.Empty(Directory.EnumerateFiles(paths.Staging));
    }

    [Fact(Timeout = 30_000)]
    public async Task Beginner_Vanilla_flow_installs_managed_Java_and_exact_server_release()
    {
        var runtimeArchive = BuildRuntimeArchive();
        var serverJar = BuildServerPackage();
        var paths = new AppDataPaths(Path.Combine(root, "beginner-vanilla-data"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var http = new HttpClient(
            new BeginnerVanillaHandler(runtimeArchive, serverJar));
        var java = await new ManagedJavaRuntimeService(
            paths,
            store,
            new FixtureJavaProvider(Convert.ToHexString(SHA256.HashData(runtimeArchive))),
            http).InstallAsync(21);
        var preset = QuickStartPresetFactory.Create(QuickStartKind.VanillaWithFriends);
        var result = await new ManagedServerInstaller(
            paths, store, new ServerDownloadCatalog(http), http).InstallAsync(
            new ServerInstallRequest
            {
                SourceType = preset.SourceType,
                MinecraftVersion = "1.21.1",
                ServerName = "Beginner Vanilla Fixture",
                InstanceRoot = Path.Combine(root, "beginner-servers"),
                JavaPath = java.JavaPath,
                MinimumRamMb = 1_024,
                MaximumRamMb = 4_096,
                MaxPlayers = preset.MaxPlayers,
                InitialProperties = preset.Properties,
                EnableDailyBackup = preset.DailyBackup,
                EulaAccepted = true,
                EulaAcceptedAt = DateTimeOffset.UtcNow
            });
        Assert.Equal("1.21.1", result.Definition.MinecraftVersion);
        Assert.Equal(Path.GetFullPath(java.JavaPath), result.Definition.Executable);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, "server.jar")));
        Assert.Contains("white-list=true",
            await File.ReadAllTextAsync(
                Path.Combine(result.Definition.RootPath, "server.properties")));
        Assert.Equal("eula=true\r\n",
            await File.ReadAllTextAsync(Path.Combine(result.Definition.RootPath, "eula.txt")));
    }

    [Fact(Timeout = 30_000)]
    public async Task Mocked_provider_server_pack_installs_with_SHA512_and_exact_version()
    {
        var package = BuildServerPackage();
        var paths = new AppDataPaths(Path.Combine(root, "pack-appdata"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var http = new HttpClient(new BytesHandler(package));
        var installer = new ManagedServerInstaller(paths, store, new ServerDownloadCatalog(http), http);
        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            SourceType = InstallSourceType.DirectUrl,
            Source = "https://fixture.invalid/pack-server.zip",
            MinecraftVersion = "1.21.1",
            Build = "2.4.0",
            ServerName = "Provider Fixture",
            InstanceRoot = Path.Combine(root, "servers"),
            JavaPath = FakeJavaPath(),
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow,
            ExpectedSha512 = Convert.ToHexString(SHA512.HashData(package))
        });
        Assert.Equal("1.21.1", result.Definition.MinecraftVersion);
        Assert.Equal("2.4.0", result.Definition.LoaderVersion);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath, "server.jar")));
        Assert.Contains("white-list=true",
            await File.ReadAllTextAsync(Path.Combine(result.Definition.RootPath, "server.properties")),
            StringComparison.Ordinal);

        // Registration is part of the creation transaction, not a step the caller performs
        // afterwards: the window between an activated folder and a persisted record is exactly where
        // an interrupted creation used to become unrecoverable.
        var registered = Assert.Single(
            await store.GetServersAsync(), server => server.Id == result.Definition.Id);
        Assert.Equal(result.Definition.RootPath, registered.RootPath);
        Assert.True(registered.IsManaged);
        Assert.Equal(CreationOutcome.Completed, result.Outcome);
        Assert.Empty(await store.GetCreationJournalsAsync());
    }

    [Fact(Timeout = 30_000)]
    public async Task Official_CurseForge_pack_without_launcher_uses_exact_managed_loader_not_mod_jar()
    {
        var package = BuildContentOnlyServerPack();
        var paths = new AppDataPaths(Path.Combine(root, "curseforge-loader-fallback-data"),
            Path.Combine(root, "curseforge-loader-fallback-servers"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var loaderHttp = new HttpClient(new LoaderHandler());
        using var packageHandler = new BytesHandler(package);
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-key");
        using var api = new CurseForgeApiClient(secrets, packageHandler);
        var validator = new CapturingStagedValidator();
        var installer = new ManagedServerInstaller(
            paths,
            store,
            new ServerDownloadCatalog(loaderHttp),
            loaderHttp,
            new LoaderInstallationService(new LoaderMetadataService(loaderHttp), loaderHttp),
            curseForge: api,
            stagedValidator: validator);

#pragma warning disable CA5350 // CurseForge publishes SHA-1 as provider artifact identity.
        var sha1 = Convert.ToHexString(SHA1.HashData(package));
#pragma warning restore CA5350
        var result = await installer.InstallAsync(new ServerInstallRequest
        {
            SourceType = InstallSourceType.CurseForgeServerPack,
            Source = "https://mediafilez.forgecdn.net/files/222/fixture-server.zip",
            MinecraftVersion = "1.20.1",
            Build = "47.3.0",
            ServerName = "CurseForge managed loader fallback",
            InstanceRoot = paths.ManagedServers,
            JavaPath = FakeJavaPath(),
            MinimumRamMb = 2_048,
            MaximumRamMb = 6_144,
            Port = 25_587,
            CreationNetworkingPreference = VanillaNetworkingPreference.ThisComputerOnly,
            EulaAccepted = true,
            EulaAcceptedAt = DateTimeOffset.UtcNow,
            ExpectedSha1 = sha1,
            ExpectedSizeBytes = package.LongLength,
            PackProvider = UpdateProvider.CurseForge,
            PackProjectId = "123",
            PackVersionId = "111",
            PackServerFileId = "222",
            PackLoader = "Forge",
            PackLoaderVersion = "47.3.0"
        });

        Assert.True(validator.Called);
        Assert.True(validator.UsesArgumentFile);
        Assert.EndsWith("win_args.txt", validator.LaunchRelativePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mods", validator.LaunchRelativePath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("@", result.Definition.Arguments, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(result.Definition.RootPath,
            "mods", "additionallanterns-1.1.1a-forge-mc1.20.jar")));
        var argumentsPath = Path.Combine(
            result.Definition.RootPath, "libraries", "fixture", "win_args.txt");
        Assert.Contains(argumentsPath, result.Definition.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("-cp fixture server.Main\r\n", await File.ReadAllTextAsync(argumentsPath));
    }

    [Fact(Timeout = 30_000)]
    public async Task Crossplay_packages_are_hash_verified_backed_up_and_removed_by_ownership()
    {
        var payload = Encoding.UTF8.GetBytes("verified crossplay fixture");
        var serverRoot = Path.Combine(root, "crossplay-server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "world"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "level.dat"), "world fixture");
        Directory.CreateDirectory(Path.Combine(serverRoot, "plugins", "Geyser-Spigot"));
        await File.WriteAllTextAsync(
            Path.Combine(serverRoot, "plugins", "Geyser-Spigot", "config.yml"),
            "generated configuration must survive removal");
        var paths = new AppDataPaths(Path.Combine(root, "crossplay-appdata"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        using var http = new HttpClient(new BytesHandler(payload));
        var service = new CrossplayPackageService(
            paths,
            store,
            new BackupService(paths, store),
            new CanonicalPathLockManager(),
            new FixtureCrossplayProvider(payload),
            http);
        var definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Crossplay Fixture",
            RootPath = serverRoot,
            Ecosystem = ServerEcosystem.Paper,
            MinecraftVersion = "1.21.1",
            Port = 25_565,
            Executable = FakeJavaPath()
        };
        var capabilities = ServerCapabilityPolicy.Build(definition, new ServerCapabilityEvidence
        {
            Edition = ServerEdition.Java,
            Ecosystem = ServerEcosystem.Paper,
            HasPluginsDirectory = true
        });
        var result = await service.InstallAsync(
            definition,
            capabilities,
            new CrossplayInstallRequest(definition.Id, true, true, 19_132));
        Assert.NotEqual(Guid.Empty, result.BackupId);
        Assert.Equal(3, result.Configuration.OwnedFiles.Count);
        Assert.All(result.Configuration.OwnedFiles,
            relative => Assert.True(File.Exists(Path.Combine(serverRoot, relative))));
        Assert.Single(await store.GetBackupsAsync(definition.Id));
        Assert.True((await store.GetCrossplayConfigurationAsync(definition.Id))!.GeyserEnabled);

        var removal = await service.RemoveAsync(definition);
        Assert.True(removal.Success);
        Assert.All(result.Configuration.OwnedFiles,
            relative => Assert.False(File.Exists(Path.Combine(serverRoot, relative))));
        Assert.True(File.Exists(
            Path.Combine(serverRoot, "plugins", "Geyser-Spigot", "config.yml")));
    }

    [Theory(Timeout = 30_000)]
    [InlineData("1.21.1", "\"pack_format\":48")]
    [InlineData("26.1", "\"min_format\":[101,1],\"max_format\":101")]
    [InlineData("26.3", "\"min_format\":121,\"max_format\":121")]
    public async Task Datapack_and_resource_pack_changes_are_validated_backed_up_and_persisted(
        string minecraftVersion, string packFormatFields)
    {
        var serverRoot = Path.Combine(root, "pack-content-server");
        var world = Path.Combine(serverRoot, "world");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "fixture");
        await File.WriteAllTextAsync(
            Path.Combine(serverRoot, "server.properties"),
            "motd=fixture\r\n");
        var source = Path.Combine(root, "fixture-datapack");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "pack.mcmeta"),
            "{\"pack\":{" + packFormatFields + ",\"description\":\"fixture\"}}");
        var paths = new AppDataPaths(Path.Combine(root, "pack-content-data"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var safeFiles = new SafeFileService(paths, new CanonicalPathLockManager());
        var service = new DatapackManagementService(
            paths,
            store,
            new BackupService(paths, store),
            new DatapackService(),
            safeFiles,
            new CanonicalPathLockManager());
        var definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Pack Content Fixture",
            RootPath = serverRoot,
            MinecraftVersion = minecraftVersion
        };
        var installed = await service.InstallAsync(
            definition,
            new DatapackInstallRequest(definition.Id, "world", source));
        Assert.Equal(CompatibilityState.Compatible, installed.Compatibility);
        Assert.True(Directory.Exists(Path.Combine(serverRoot, installed.RelativePath)));
        Assert.Single(await store.GetDatapackInventoryAsync(definition.Id));

        const string sha1 = "0123456789abcdef0123456789abcdef01234567";
        await service.ConfigureResourcePackAsync(
            definition,
            new ResourcePackConfiguration
            {
                ServerId = definition.Id,
                Url = "https://example.invalid/resource-pack.zip",
                Sha1 = sha1,
                Required = true,
                Prompt = "Fixture pack"
            });
        var properties = await File.ReadAllTextAsync(
            Path.Combine(serverRoot, "server.properties"));
        Assert.Contains("resource-pack=https://example.invalid/resource-pack.zip", properties);
        Assert.Contains($"resource-pack-sha1={sha1}", properties);
        Assert.True((await store.GetResourcePackConfigurationAsync(definition.Id))!.Required);
        Assert.Equal(2, (await store.GetBackupsAsync(definition.Id)).Count);
    }

    private byte[] BuildRuntimeArchive()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var outputDirectory = Path.GetDirectoryName(FakeJavaPath())!;
            foreach (var source in Directory.EnumerateFiles(outputDirectory)
                         .Where(path => Path.GetFileName(path).StartsWith("ChunkPilot.FakeServer",
                             StringComparison.OrdinalIgnoreCase)))
            {
                var fileName = Path.GetFileName(source);
                if (fileName.Equals("ChunkPilot.FakeServer.exe", StringComparison.OrdinalIgnoreCase))
                    fileName = "java.exe";
                var entry = archive.CreateEntry("temurin/bin/" + fileName);
                using var input = File.OpenRead(source);
                using var target = entry.Open();
                input.CopyTo(target);
            }
        }
        return output.ToArray();
    }

    private static LoaderInstallPlan ForgeInstallerPlan(string installerArgument) => new()
    {
        Loader = InstallSourceType.Forge,
        MinecraftVersion = "1.21.1",
        LoaderVersion = "52.0.1",
        InstallerVersion = "52.0.1",
        DownloadUrl = "https://maven.minecraftforge.net/net/minecraftforge/forge/1.21.1-52.0.1/forge-1.21.1-52.0.1-installer.jar",
        Sha256 = Convert.ToHexString(SHA256.HashData(InstallerBytes)),
        InstallerArgument = installerArgument,
        ExpectedLaunchFile = "run.bat",
        RequiredJavaMajor = 21,
        RunsInstaller = true
    };

    private static byte[] BuildServerPackage()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("server.jar");
            using (var target = entry.Open())
                target.Write([0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            var properties = archive.CreateEntry("server.properties");
            using var writer = new StreamWriter(properties.Open(), new UTF8Encoding(false));
            writer.Write("motd=fixture\r\nwhite-list=false\r\n");
        }
        return output.ToArray();
    }

    private static byte[] BuildContentOnlyServerPack()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(
                "mods/additionallanterns-1.1.1a-forge-mc1.20.jar",
                CompressionLevel.NoCompression);
            using (var target = entry.Open())
                target.Write(InstallerBytes);
            var config = archive.CreateEntry("config/fixture.toml", CompressionLevel.NoCompression);
            using (var configTarget = new StreamWriter(config.Open(), new UTF8Encoding(false)))
                configTarget.Write("fixture=true\n");
            var providerArguments = archive.CreateEntry(
                "libraries/fixture/win_args.txt", CompressionLevel.NoCompression);
            using var argumentTarget = new StreamWriter(
                providerArguments.Open(), new UTF8Encoding(false));
            argumentTarget.Write("provider-controlled argument sentinel");
        }
        return output.ToArray();
    }

    private static string FakeJavaPath() =>
        Path.Combine(RepositoryRoot(), "tests", "ChunkPilot.FakeServer", "bin", "Release", "net10.0",
            "ChunkPilot.FakeServer.exe");

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FixtureJavaProvider(string sha256) : IManagedJavaPackageProvider
    {
        public Task<ManagedJavaPackage> ResolveAsync(
            int majorVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ManagedJavaPackage
            {
                MajorVersion = majorVersion,
                Version = "21.0.8-fixture",
                DownloadUrl = "https://fixture.invalid/temurin.zip",
                FileName = "temurin.zip",
                Sha256 = sha256
            });
    }

    private sealed class FixtureCrossplayProvider(byte[] payload) : ICrossplayPackageProvider
    {
        public Task<CrossplayPackage> ResolveAsync(
            CrossplayPackageKind kind,
            string platform,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CrossplayPackage
            {
                Kind = kind,
                Version = "fixture-1",
                Platform = platform,
                FileName = kind + ".jar",
                DownloadUrl = $"https://fixture.invalid/{kind}.jar",
                Sha256 = Convert.ToHexString(SHA256.HashData(payload))
            });
    }

    private sealed class BytesHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
    }

    private sealed class LoaderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/server/jar", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(InstallerBytes)
                });
            if (url.EndsWith("/v2/versions/installer", StringComparison.OrdinalIgnoreCase))
                return Json("""[{"version":"1.0.3","stable":true}]""");
            if (url.Contains("meta.fabricmc.net", StringComparison.OrdinalIgnoreCase))
                return Json("""[{"loader":{"version":"0.16.14"}}]""");
            if (url.Contains("meta.quiltmc.org", StringComparison.OrdinalIgnoreCase))
                return Json("""[{"loader":{"version":"0.27.1"}}]""");
            if (url.Contains("/api/maven/versions/releases/net/neoforged/neoforge", StringComparison.OrdinalIgnoreCase))
                return Json("""{"isSnapshot":false,"versions":["21.1.100"]}""");
            if (url.EndsWith("maven-metadata.xml", StringComparison.OrdinalIgnoreCase))
            {
                if (url.Contains("minecraftforge", StringComparison.OrdinalIgnoreCase))
                    return Text("""
                        <metadata><versioning><release>1.21.1-52.0.1</release>
                        <versions><version>1.20.1-47.3.0</version><version>1.21.1-52.0.1</version></versions></versioning></metadata>
                        """);
                return Text("""
                    <metadata><versioning><release>0.12.0</release>
                    <versions><version>0.12.0</version></versions></versioning></metadata>
                    """);
            }
            if (url.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase))
            {
#pragma warning disable CA5350
                return Text(Convert.ToHexString(SHA1.HashData(InstallerBytes)));
#pragma warning restore CA5350
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(InstallerBytes)
            });
        }

        private static Task<HttpResponseMessage> Json(string value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }

        private static Task<HttpResponseMessage> Text(string value)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "text/plain")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class CapturingStagedValidator : IStagedServerValidator
    {
        public bool Called { get; private set; }
        public string LaunchRelativePath { get; private set; } = "";
        public bool UsesArgumentFile { get; private set; }

        public Task<StagedServerValidationResult> ValidateAsync(
            string javaPath,
            string stagingRoot,
            string launchRelativePath,
            bool usesArgumentFile,
            int minimumRamMb,
            int maximumRamMb,
            TimeSpan timeout,
            IProgress<StagedValidationProgress>? progress = null, ServerEcosystem ecosystem = ServerEcosystem.Custom,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            LaunchRelativePath = launchRelativePath;
            UsesArgumentFile = usesArgumentFile;
            return Task.FromResult(new StagedServerValidationResult(
                true, true, true, true, true,
                "Fixture validation passed.", []));
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

    private sealed class BeginnerVanillaHandler(
        byte[] runtimeArchive,
        byte[] serverJar) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("temurin", StringComparison.OrdinalIgnoreCase))
                return Bytes(runtimeArchive);
            if (url.Contains("version_manifest", StringComparison.OrdinalIgnoreCase))
                return Json(
                    """{"versions":[{"id":"1.21.1","type":"release","url":"https://fixture.invalid/vanilla-version.json"}]}""");
            if (url.EndsWith("vanilla-version.json", StringComparison.OrdinalIgnoreCase))
            {
#pragma warning disable CA5350 // Mojang's official server metadata uses SHA-1.
                return Json(
                    "{\"downloads\":{\"server\":{\"url\":\"https://fixture.invalid/server.jar\"," +
                    "\"sha1\":\"" + Convert.ToHexString(SHA1.HashData(serverJar)) + "\"," +
                    "\"size\":" + serverJar.Length + "}}}");
#pragma warning restore CA5350
            }
            if (url.EndsWith("server.jar", StringComparison.OrdinalIgnoreCase))
                return Bytes(serverJar);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static Task<HttpResponseMessage> Json(string value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            });

        private static Task<HttpResponseMessage> Bytes(byte[] value) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(value)
            });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
