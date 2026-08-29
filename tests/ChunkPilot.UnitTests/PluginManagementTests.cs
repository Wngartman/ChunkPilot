using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class PluginManagementTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-plugins-" + Guid.NewGuid().ToString("N"));

    public PluginManagementTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Inventory_reads_bounded_plugin_metadata_and_dependencies_without_loading_code()
    {
        var server = PaperServer("inventory");
        var jar = Path.Combine(server.RootPath, "plugins", "fixture.jar");
        CreateJar(jar, "plugin.yml", "name: Fixture\nversion: 2.3.1\ndepend: [Vault, WorldEdit]\nsoftdepend: [BlueMap]\nloadbefore: [CompatLayer]\n");
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Update))
        {
            var code = archive.CreateEntry("com/example/Plugin.class");
            using var stream = code.Open();
            stream.Write([0xCA, 0xFE, 0xBA, 0xBE]);
        }

        var item = Assert.Single(Service().Inventory(server));

        Assert.Equal("Fixture", item.Name);
        Assert.Equal("2.3.1", item.Version);
        Assert.Equal("Bukkit", item.Loader);
        Assert.Equal(["Vault", "WorldEdit", "BlueMap", "CompatLayer"], item.Dependencies);
        Assert.Contains(item.DependencyDetails, item => item == new ContentDependency("Vault", ContentDependencyKind.Required));
        Assert.Contains(item.DependencyDetails, item => item == new ContentDependency("BlueMap", ContentDependencyKind.Optional));
        Assert.Contains(item.DependencyDetails, item => item == new ContentDependency("CompatLayer", ContentDependencyKind.LoadBefore));
        Assert.Equal(CompatibilityState.LikelyCompatible, item.Compatibility);
    }

    [Fact]
    public void Fabric_inventory_rejects_client_only_and_cross_loader_mods()
    {
        var fabric = ModServer("fabric-client", ServerEcosystem.Fabric);
        CreateJar(Path.Combine(fabric.RootPath, "mods", "client.jar"), "fabric.mod.json",
            """{"schemaVersion":1,"id":"client","version":"1.0","name":"Client","environment":"client"}""");
        CreateJar(Path.Combine(fabric.RootPath, "mods", "wrong.jar"), "META-INF/neoforge.mods.toml",
            "modLoader=\"javafml\"\n[[mods]]\nmodId=\"wrong\"\nversion=\"1.0\"\ndisplayName=\"Wrong\"");

        var inventory = Service().Inventory(fabric);

        Assert.All(inventory, item => Assert.Equal(CompatibilityState.Incompatible, item.Compatibility));
        Assert.Contains(inventory, item => item.ClientRequirement == "ClientOnly");
        Assert.Contains(inventory, item => item.Loader == "NeoForge");
    }

    [Fact]
    public void NeoForge_inventory_parses_required_optional_and_incompatible_dependencies()
    {
        var server = ModServer("neoforge-metadata", ServerEcosystem.NeoForge);
        CreateJar(Path.Combine(server.RootPath, "mods", "fixture.jar"), "META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            loaderVersion="[4,)"
            [[mods]]
            modId="fixture"
            version="2.0"
            displayName="Fixture"
            [[dependencies.fixture]]
            modId="requiredlib"
            type="required"
            versionRange="[1,)"
            side="BOTH"
            [[dependencies.fixture]]
            modId="optionalmap"
            type="optional"
            versionRange="[1,)"
            side="BOTH"
            [[dependencies.fixture]]
            modId="oldapi"
            type="incompatible"
            versionRange="*"
            side="BOTH"
            """);

        var item = Assert.Single(Service().Inventory(server));

        Assert.Equal("fixture", item.Id);
        Assert.Equal(CompatibilityState.LikelyCompatible, item.Compatibility);
        Assert.Contains(new ContentDependency("requiredlib", ContentDependencyKind.Required), item.DependencyDetails);
        Assert.Contains(new ContentDependency("optionalmap", ContentDependencyKind.Optional), item.DependencyDetails);
        Assert.Contains(new ContentDependency("oldapi", ContentDependencyKind.Incompatible), item.DependencyDetails);
    }

    [Fact]
    public async Task Modrinth_mod_provider_requires_exact_loader_game_and_server_environment()
    {
        var hash = new string('b', 128);
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/search" => Json("""{"hits":[{"project_id":"mod","slug":"mod","title":"Server Mod","author":"Author","description":"Exact mod","server_side":"required","client_side":"optional","downloads":9}]}"""),
            "/v2/project/mod" => Json("""{"id":"mod","project_type":"mod","server_side":"required","client_side":"optional"}"""),
            _ => Json(("""[{"project_id":"mod","id":"fabric-exact","version_number":"1.0","version_type":"release","date_published":"2026-08-17T12:00:00Z","game_versions":["26.2"],"loaders":["fabric"],"files":[{"primary":true,"filename":"mod.jar","url":"https://cdn.modrinth.com/data/mod/mod.jar","size":99,"hashes":{"sha512":"__HASH__"}}],"dependencies":[]},{"project_id":"mod","id":"neoforge-wrong","version_number":"1.1","version_type":"release","date_published":"2026-08-18T12:00:00Z","game_versions":["26.2"],"loaders":["neoforge"],"files":[{"primary":true,"filename":"wrong.jar","url":"https://cdn.modrinth.com/data/mod/wrong.jar","size":99,"hashes":{"sha512":"__HASH__"}}],"dependencies":[]}]""").Replace("__HASH__", hash, StringComparison.Ordinal))
        });
        var provider = new ModrinthPluginProvider(new HttpClient(handler));

        var projects = await provider.SearchAsync(new PluginCatalogQuery
        {
            Kind = ManagedAddonKind.Mod,
            Search = "mod",
            MinecraftVersion = "26.2",
            Loader = "fabric"
        });
        var release = await provider.ResolveReleaseAsync("mod", "26.2", "fabric");

        Assert.Equal(ManagedAddonKind.Mod, Assert.Single(projects).Kind);
        Assert.Equal("ClientOptional", Assert.Single(projects).ClientRequirement);
        Assert.NotNull(release);
        Assert.Equal("fabric-exact", release!.VersionId);
        Assert.Equal("fabric", release.Loader);
        Assert.Equal("ClientOptional", release.ClientRequirement);
    }

    [Fact]
    public void Inspect_reads_sanitized_metadata_without_installing_the_selected_jar()
    {
        var server = PaperServer("inspect");
        var source = Path.Combine(root, "selected", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 4.2\ndepend: [Vault]\n");

        var item = Service().Inspect(server, source);

        Assert.Equal("Fixture", item.Name);
        Assert.Equal("4.2", item.Version);
        Assert.Equal(["Vault"], item.Dependencies);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "plugins")));
    }

    [Fact]
    public void Oversized_metadata_is_reported_unreadable_instead_of_expanded()
    {
        var server = PaperServer("oversized");
        var jar = Path.Combine(server.RootPath, "plugins", "oversized.jar");
        CreateJar(jar, "plugin.yml", "name: Fixture\n" + new string('x', 600 * 1024));

        var item = Assert.Single(Service().Inventory(server));

        Assert.Equal("Unreadable metadata", item.Loader);
        Assert.Equal(CompatibilityState.Unknown, item.Compatibility);
    }

    [Fact]
    public void Remove_moves_only_jar_to_recovery_and_preserves_configuration()
    {
        var server = PaperServer("remove");
        var jar = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(jar, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var config = Path.Combine(server.RootPath, "plugins", "Fixture", "config.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "enabled: true");

        Service().Remove(server, Path.Combine("plugins", "Fixture.jar"));

        Assert.False(File.Exists(jar));
        Assert.Equal("enabled: true", File.ReadAllText(config));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(root, "data", "Recovery"), "Fixture.jar", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Modrinth_provider_filters_exact_paper_release_keeps_hash_dependencies_and_caches()
    {
        var hash = new string('a', 128);
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/search" => Json("""
                {"hits":[{"project_id":"fixture","slug":"fixture","title":"Fixture","author":"Author","description":"Server plugin","server_side":"required","downloads":42,"date_modified":"2026-08-17T12:00:00Z"}]}
                """),
            "/v2/project/fixture" => Json("""
                {"id":"fixture","project_type":"plugin","server_side":"required","client_side":"unsupported"}
                """),
            _ => Json("""
                [{"project_id":"fixture","id":"paper-release","version_number":"2.0","version_type":"release","date_published":"2026-08-17T12:00:00Z","game_versions":["1.21.8"],"loaders":["paper"],"files":[{"primary":true,"filename":"Fixture.jar","url":"https://cdn.modrinth.com/data/fixture/Fixture.jar","size":1234,"hashes":{"sha1":"abc","sha512":"__HASH__"}}],"dependencies":[{"project_id":"vault","version_id":null,"file_name":null,"dependency_type":"required"}]},
                 {"project_id":"fixture","id":"fabric-release","version_number":"3.0","version_type":"release","date_published":"2026-08-18T12:00:00Z","game_versions":["1.21.8"],"loaders":["fabric"],"files":[{"primary":true,"filename":"Wrong.jar","url":"https://cdn.modrinth.com/wrong.jar","size":10,"hashes":{"sha512":"__HASH__"}}],"dependencies":[]}]
                """.Replace("__HASH__", hash, StringComparison.Ordinal))
        });
        var provider = new ModrinthPluginProvider(new HttpClient(handler));

        var first = await provider.SearchAsync(new PluginCatalogQuery { Search = "fixture", MinecraftVersion = "1.21.8", Loader = "paper" });
        var second = await provider.SearchAsync(new PluginCatalogQuery { Search = "fixture", MinecraftVersion = "1.21.8", Loader = "paper" });
        var release = await provider.ResolveReleaseAsync("fixture", "1.21.8", "paper");
        var cachedRelease = await provider.ResolveReleaseAsync("fixture", "1.21.8", "paper");

        Assert.Single(first);
        Assert.Single(second);
        Assert.NotNull(release);
        Assert.Equal("paper-release", release!.VersionId);
        Assert.Equal(hash, release.Sha512);
        Assert.Equal("vault", Assert.Single(release.Dependencies).ProjectId);
        Assert.NotNull(cachedRelease);
        Assert.Equal(release.VersionId, cachedRelease!.VersionId);
        Assert.Equal(release.Sha512, cachedRelease.Sha512);
        Assert.Equal(3, handler.RequestCount);
    }

    [Fact]
    public async Task Modrinth_provider_uses_bounded_stale_disk_cache_during_an_outage()
    {
        var paths = new AppDataPaths(Path.Combine(root, "offline-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var first = new ModrinthPluginProvider(paths, new HttpClient(new StubHandler(_ => Json("""
            {"hits":[{"project_id":"fixture","slug":"fixture","title":"Fixture","author":"Author","description":"Offline-capable plugin","server_side":"required","downloads":42,"date_modified":"2026-08-17T12:00:00Z"}]}
            """))));
        Assert.Single(await first.SearchAsync(new PluginCatalogQuery
            { Search = "fixture", MinecraftVersion = "1.21.8", Loader = "paper" }));
        var cacheFile = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.CatalogCache, "plugins", "modrinth"), "*.json"));
        File.SetLastWriteTimeUtc(cacheFile, DateTime.UtcNow - TimeSpan.FromHours(1));

        var offline = new ModrinthPluginProvider(paths, new HttpClient(new StubHandler(_ =>
            throw new HttpRequestException("fixture provider offline"))));
        var restored = await offline.SearchAsync(new PluginCatalogQuery
            { Search = "fixture", MinecraftVersion = "1.21.8", Loader = "paper" });

        Assert.Equal("Fixture", Assert.Single(restored).Name);
    }

    [Fact]
    public async Task Hangar_is_explicitly_unavailable_without_network_fallback()
    {
        var provider = new HangarUnavailablePluginProvider();
        Assert.False(provider.Status.Available);
        Assert.Contains("does not scrape", provider.Status.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await provider.SearchAsync(new PluginCatalogQuery { Search = "anything" }));
        Assert.Null(await provider.ResolveReleaseAsync("project", "1.21.8", "paper"));
    }

    [Fact]
    public async Task Provider_install_verifies_sha512_before_placing_jar()
    {
        var server = PaperServer("provider-install");
        var bytes = Encoding.UTF8.GetBytes("fixture plugin jar bytes");
        var release = new PluginRelease
        {
            Provider = PluginProviderKind.Modrinth,
            ProjectId = "fixture",
            VersionId = "release-1",
            VersionName = "1.0",
            MinecraftVersion = "1.21.8",
            Loader = "paper",
            DownloadUrl = "https://cdn.modrinth.com/data/fixture/Fixture.jar",
            FileName = "Fixture.jar",
            SizeBytes = bytes.Length,
            Sha512 = Convert.ToHexString(SHA512.HashData(bytes))
        };
        var paths = new AppDataPaths(Path.Combine(root, "provider-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        var provider = new FakeProvider(release);
        var service = new PluginManagementService(new PluginProviderRegistry([provider]), jars, paths,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            })));

        await service.InstallAsync(server, release.ProjectId, release.VersionId);

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(server.RootPath, "plugins", "Fixture.jar")));
        var installed = Assert.Single(jars.Inventory(server));
        Assert.Equal(PluginProviderKind.Modrinth, installed.Provider);
        Assert.Equal("fixture", installed.ProviderProjectId);
        Assert.Equal("release-1", installed.ProviderVersionId);
        Assert.Equal("Modrinth", installed.InstallSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_release_provider_install_preserves_user_or_different_project_destination(
        bool hasDifferentProjectProvenance)
    {
        var suffix = hasDifferentProjectProvenance ? "different-project" : "user-owned";
        var server = PaperServer("single-release-filename-" + suffix);
        var existingPath = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(existingPath, "plugin.yml", "name: Existing\nversion: 1.0\n");
        var existingBytes = File.ReadAllBytes(existingPath);
        var incomingBytes = Encoding.UTF8.GetBytes("incoming provider bytes");
        var incoming = Release("incoming-project", "incoming-version", "fixture.JAR", incomingBytes);
        var paths = new AppDataPaths(Path.Combine(root, "single-release-filename-" + suffix + "-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        if (hasDifferentProjectProvenance)
            jars.RecordProviderProvenance(server, existingPath, new PluginRelease
            {
                Provider = PluginProviderKind.Modrinth,
                ProjectId = "different-project",
                VersionId = "existing-version"
            });
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(incomingBytes)
        });
        var service = new PluginManagementService(
            new PluginProviderRegistry([new FakeProvider(incoming)]),
            jars,
            paths,
            new HttpClient(downloads));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(server, incoming.ProjectId, incoming.VersionId));

        Assert.Contains("already owned", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, downloads.RequestCount);
        Assert.Equal(existingBytes, File.ReadAllBytes(existingPath));
        Assert.Empty(Directory.EnumerateFiles(paths.Recovery, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void CurseForge_mod_provenance_tags_the_minimum_API_derived_operational_identity()
    {
        var server = PaperServer("curseforge-provenance");
        var jar = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(jar, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var paths = new AppDataPaths(Path.Combine(root, "curseforge-provenance-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);

        jars.RecordProviderProvenance(server, jar, new PluginRelease
        {
            Provider = PluginProviderKind.CurseForge,
            ProjectId = "123",
            VersionId = "456",
            VersionName = "provider-label-must-not-persist"
        });

        var provenancePath = Assert.Single(Directory.EnumerateFiles(paths.PluginProvenance, "*.json"));
        using var document = JsonDocument.Parse(File.ReadAllText(provenancePath));
        var entry = Assert.Single(document.RootElement.EnumerateArray().ToArray());
        Assert.Equal("123", entry.GetProperty("projectId").GetString());
        Assert.Equal("456", entry.GetProperty("versionId").GetString());
        Assert.Equal("ApiDerivedOperationalIdentity", entry.GetProperty("identityOrigin").GetString());
        Assert.Equal("", entry.GetProperty("versionName").GetString());
    }

    [Fact]
    public async Task Provider_update_matches_persisted_identity_replaces_changed_filename_and_retains_recovery()
    {
        var server = PaperServer("provider-update");
        var firstBytes = Encoding.UTF8.GetBytes("fixture plugin jar v1");
        var secondBytes = Encoding.UTF8.GetBytes("fixture plugin jar v2");
        var paths = new AppDataPaths(Path.Combine(root, "update-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);

        static PluginRelease Release(string versionId, string versionName, string fileName, byte[] bytes) => new()
        {
            Provider = PluginProviderKind.Modrinth,
            ProjectId = "fixture",
            VersionId = versionId,
            VersionName = versionName,
            MinecraftVersion = "1.21.8",
            Loader = "paper",
            DownloadUrl = $"https://cdn.modrinth.com/data/fixture/{fileName}",
            FileName = fileName,
            SizeBytes = bytes.Length,
            Sha512 = Convert.ToHexString(SHA512.HashData(bytes))
        };

        var first = Release("release-1", "1.0", "Fixture-1.0.jar", firstBytes);
        await new PluginManagementService(new PluginProviderRegistry([new FakeProvider(first)]), jars, paths,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(firstBytes)
            }))).InstallAsync(server, first.ProjectId, first.VersionId);

        var second = Release("release-2", "2.0", "Fixture-2.0.jar", secondBytes);
        await new PluginManagementService(new PluginProviderRegistry([new FakeProvider(second)]), jars, paths,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(secondBytes)
            }))).InstallAsync(server, second.ProjectId, second.VersionId);

        Assert.False(File.Exists(Path.Combine(server.RootPath, "plugins", "Fixture-1.0.jar")));
        Assert.Equal(secondBytes, File.ReadAllBytes(Path.Combine(server.RootPath, "plugins", "Fixture-2.0.jar")));
        Assert.Single(Directory.EnumerateFiles(paths.Recovery, "Fixture-1.0.jar", SearchOption.AllDirectories));
        var installed = Assert.Single(jars.Inventory(server));
        Assert.Equal("release-2", installed.ProviderVersionId);
        Assert.Equal("fixture", installed.ProviderProjectId);
    }

    [Fact]
    public async Task Provider_install_blocks_unresolved_required_dependencies_before_download()
    {
        var server = PaperServer("provider-dependency-block");
        var release = new PluginRelease
        {
            Provider = PluginProviderKind.Modrinth,
            ProjectId = "fixture",
            VersionId = "release-1",
            MinecraftVersion = "1.21.8",
            Loader = "paper",
            DownloadUrl = "https://cdn.modrinth.com/data/fixture/Fixture.jar",
            FileName = "Fixture.jar",
            SizeBytes = 20,
            Sha512 = new string('a', 128),
            Dependencies = [new PluginDependency { ProjectId = "vault", Type = "required" }]
        };
        var paths = new AppDataPaths(Path.Combine(root, "dependency-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var handler = new StubHandler(_ => throw new Xunit.Sdk.XunitException("Download must not start."));
        var service = new PluginManagementService(
            new PluginProviderRegistry([new FakeProvider(release)]),
            new JarInventoryService(new SafeFileService(paths), paths), paths, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallAsync(server, release.ProjectId, release.VersionId));

        Assert.Contains("vault", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "plugins")));
    }

    [Fact]
    public async Task Dependency_plan_resolves_recursively_and_installs_as_one_ordered_operation()
    {
        var server = PaperServer("dependency-plan");
        var dependencyBytes = Encoding.UTF8.GetBytes("dependency jar");
        var rootBytes = Encoding.UTF8.GetBytes("root jar");
        var dependency = Release("library", "library-v1", "Library.jar", dependencyBytes);
        var rootRelease = Release("root", "root-v1", "Root.jar", rootBytes) with
        {
            Dependencies = [new PluginDependency { ProjectId = "library", Type = "required" }]
        };
        var paths = new AppDataPaths(Path.Combine(root, "plan-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var handler = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("Library.jar", StringComparison.Ordinal)
                ? dependencyBytes : rootBytes)
        });
        var service = new PluginManagementService(
            new PluginProviderRegistry([new PlanProvider([dependency, rootRelease])]),
            new JarInventoryService(new SafeFileService(paths), paths), paths, new HttpClient(handler));

        var plan = await service.PlanAsync(server, "root", "root-v1");
        var installed = await service.InstallPlanWithReceiptsAsync(server, "root", "root-v1");

        Assert.True(plan.CanInstall);
        Assert.Equal(["library", "root"], plan.Releases.Select(item => item.ProjectId));
        Assert.Equal(2, installed.Installed.Count);
        Assert.True(File.Exists(Path.Combine(server.RootPath, "plugins", "Library.jar")));
        Assert.True(File.Exists(Path.Combine(server.RootPath, "plugins", "Root.jar")));
    }

    [Fact]
    public async Task Dependency_plan_rolls_back_every_applied_file_when_a_later_download_fails()
    {
        var server = PaperServer("dependency-plan-rollback");
        var dependencyBytes = Encoding.UTF8.GetBytes("dependency jar");
        var rootBytes = Encoding.UTF8.GetBytes("root jar");
        var dependency = Release("library", "library-v1", "Library.jar", dependencyBytes);
        var rootRelease = Release("root", "root-v1", "Root.jar", rootBytes) with
        {
            Dependencies = [new PluginDependency { ProjectId = "library", Type = "required" }]
        };
        var paths = new AppDataPaths(Path.Combine(root, "plan-rollback-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var service = new PluginManagementService(
            new PluginProviderRegistry([new PlanProvider([dependency, rootRelease])]),
            new JarInventoryService(new SafeFileService(paths), paths), paths,
            new HttpClient(new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("Library.jar", StringComparison.Ordinal)
                    ? dependencyBytes : Encoding.UTF8.GetBytes("wrong"))
            })));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallPlanWithReceiptsAsync(server, "root", "root-v1"));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "plugins"), "*.jar"));
        Assert.Single(Directory.EnumerateFiles(paths.Recovery, "Library.jar", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Authorized_CurseForge_plan_installs_exact_review_without_a_second_provider_resolution()
    {
        var server = ModServer("authorized-curseforge-plan", ServerEcosystem.NeoForge) with
        {
            MinecraftVersion = "1.21.1"
        };
        var dependencyBytes = Encoding.UTF8.GetBytes("dependency jar");
        var rootBytes = Encoding.UTF8.GetBytes("root addon jar");
        var dependency = CurseForgeRelease("20", "200", "library.jar", dependencyBytes);
        var rootRelease = CurseForgeRelease("10", "100", "root.jar", rootBytes) with
        {
            Dependencies = [new PluginDependency { ProjectId = "20", Type = "required" }]
        };
        var paths = new AppDataPaths(Path.Combine(root, "authorized-curseforge-plan-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        var provider = new PlanProvider([dependency, rootRelease], PluginProviderKind.CurseForge);
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("library.jar", StringComparison.Ordinal)
                ? dependencyBytes : rootBytes)
        });
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        using var api = new CurseForgeApiClient(secrets, downloads);
        var service = new PluginManagementService(
            new PluginProviderRegistry([provider]), jars, paths, curseForge: api);

        var reviewed = await service.PlanAsync(
            server, "10", "100", PluginProviderKind.CurseForge);
        var resolveCountAfterReview = provider.ResolveCount;
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        var authorization = registry.Register(
            server.Id, PluginProviderKind.CurseForge, "10", "100", reviewed);
        var exactPlan = registry.Consume(
            server.Id, PluginProviderKind.CurseForge, "10", "100", authorization);

        var installed = await service.InstallExactPlanWithReceiptsAsync(
            server, "10", "100", exactPlan);

        Assert.Equal(2, resolveCountAfterReview);
        Assert.Equal(resolveCountAfterReview, provider.ResolveCount);
        Assert.Equal(2, downloads.RequestCount);
        Assert.Equal(["20", "10"], installed.Installed.Select(result => result.Release.ProjectId));
        Assert.True(File.Exists(Path.Combine(server.RootPath, "mods", "library.jar")));
        Assert.True(File.Exists(Path.Combine(server.RootPath, "mods", "root.jar")));
    }

    [Fact]
    public async Task Exact_CurseForge_plan_rejects_case_insensitive_intra_plan_filename_collision_before_download()
    {
        var server = ModServer("exact-plan-filename-collision", ServerEcosystem.NeoForge) with
        {
            MinecraftVersion = "1.21.1"
        };
        var dependencyBytes = Encoding.UTF8.GetBytes("dependency jar");
        var rootBytes = Encoding.UTF8.GetBytes("root addon jar");
        var dependency = CurseForgeRelease("20", "200", "shared-name.jar", dependencyBytes);
        var rootRelease = CurseForgeRelease("10", "100", "SHARED-NAME.JAR", rootBytes) with
        {
            Dependencies = [new PluginDependency { ProjectId = "20", Type = "required" }]
        };
        var plan = new PluginInstallPlan { Releases = [dependency, rootRelease] };
        var paths = new AppDataPaths(Path.Combine(root, "exact-plan-filename-collision-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(rootBytes)
        });
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        using var api = new CurseForgeApiClient(secrets, downloads);
        var service = new PluginManagementService(
            new PluginProviderRegistry([new PlanProvider([dependency, rootRelease], PluginProviderKind.CurseForge)]),
            jars, paths, curseForge: api);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallExactPlanWithReceiptsAsync(server, "10", "100", plan));

        Assert.Contains("destination JAR filename", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, downloads.RequestCount);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "mods"), "*.jar"));
        Assert.Empty(Directory.EnumerateFiles(paths.Recovery, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_CurseForge_plan_preserves_user_or_different_project_file_with_same_destination_name(
        bool hasDifferentProjectProvenance)
    {
        var suffix = hasDifferentProjectProvenance ? "different-project" : "user-owned";
        var server = ModServer("exact-plan-existing-filename-" + suffix, ServerEcosystem.NeoForge) with
        {
            MinecraftVersion = "1.21.1"
        };
        var existingPath = Path.Combine(server.RootPath, "mods", "shared-name.jar");
        CreateJar(existingPath, "META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            [[mods]]
            modId="existing"
            version="1.0"
            displayName="Existing"
            """);
        var existingBytes = File.ReadAllBytes(existingPath);
        var incomingBytes = Encoding.UTF8.GetBytes("new project bytes");
        var incoming = CurseForgeRelease("10", "100", "SHARED-NAME.JAR", incomingBytes);
        var plan = new PluginInstallPlan { Releases = [incoming] };
        var paths = new AppDataPaths(Path.Combine(root, "exact-plan-existing-filename-" + suffix + "-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        if (hasDifferentProjectProvenance)
            jars.RecordProviderProvenance(
                server,
                existingPath,
                CurseForgeRelease("20", "200", "shared-name.jar", existingBytes));
        var downloads = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(incomingBytes)
        });
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        using var api = new CurseForgeApiClient(secrets, downloads);
        var service = new PluginManagementService(
            new PluginProviderRegistry([new PlanProvider([incoming], PluginProviderKind.CurseForge)]),
            jars, paths, curseForge: api);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallExactPlanWithReceiptsAsync(server, "10", "100", plan));

        Assert.Contains("already owned", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, downloads.RequestCount);
        Assert.Equal(existingBytes, File.ReadAllBytes(existingPath));
        Assert.Empty(Directory.EnumerateFiles(paths.Recovery, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Exact_CurseForge_plan_revalidates_destination_after_download_and_preserves_new_user_file()
    {
        var server = ModServer("exact-plan-late-filename-collision", ServerEcosystem.NeoForge) with
        {
            MinecraftVersion = "1.21.1"
        };
        var targetPath = Path.Combine(server.RootPath, "mods", "root.jar");
        var incomingBytes = Encoding.UTF8.GetBytes("incoming provider bytes");
        var incoming = CurseForgeRelease("10", "100", "root.jar", incomingBytes);
        var plan = new PluginInstallPlan { Releases = [incoming] };
        var paths = new AppDataPaths(Path.Combine(root, "exact-plan-late-filename-collision-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        byte[]? userBytes = null;
        var downloads = new StubHandler(_ =>
        {
            CreateJar(targetPath, "META-INF/neoforge.mods.toml", """
                modLoader="javafml"
                [[mods]]
                modId="userfile"
                version="1.0"
                displayName="User File"
                """);
            userBytes = File.ReadAllBytes(targetPath);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(incomingBytes)
            };
        });
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        using var api = new CurseForgeApiClient(secrets, downloads);
        var service = new PluginManagementService(
            new PluginProviderRegistry([new PlanProvider([incoming], PluginProviderKind.CurseForge)]),
            jars, paths, curseForge: api);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.InstallExactPlanWithReceiptsAsync(server, "10", "100", plan));

        Assert.Contains("already owned", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, downloads.RequestCount);
        Assert.NotNull(userBytes);
        Assert.Equal(userBytes, File.ReadAllBytes(targetPath));
        Assert.Empty(Directory.EnumerateFiles(paths.Recovery, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(paths.PluginProvenance, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Authorized_CurseForge_plan_rolls_back_prior_files_when_later_exact_download_fails()
    {
        var server = ModServer("authorized-curseforge-plan-rollback", ServerEcosystem.NeoForge) with
        {
            MinecraftVersion = "1.21.1"
        };
        var dependencyBytes = Encoding.UTF8.GetBytes("dependency jar");
        var rootBytes = Encoding.UTF8.GetBytes("root addon jar");
        var dependency = CurseForgeRelease("20", "200", "library.jar", dependencyBytes);
        var rootRelease = CurseForgeRelease("10", "100", "root.jar", rootBytes) with
        {
            Dependencies = [new PluginDependency { ProjectId = "20", Type = "required" }]
        };
        var plan = new PluginInstallPlan { Releases = [dependency, rootRelease] };
        var paths = new AppDataPaths(Path.Combine(root, "authorized-curseforge-plan-rollback-data"),
            Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        var downloads = new StubHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith("library.jar", StringComparison.Ordinal)
                ? dependencyBytes : new byte[rootBytes.Length])
        });
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "fixture-approved-key");
        using var api = new CurseForgeApiClient(secrets, downloads);
        var service = new PluginManagementService(
            new PluginProviderRegistry([new PlanProvider([dependency, rootRelease], PluginProviderKind.CurseForge)]),
            jars, paths, curseForge: api);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.InstallExactPlanWithReceiptsAsync(server, "10", "100", plan));

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "mods"), "*.jar"));
        Assert.Single(Directory.EnumerateFiles(paths.Recovery, "library.jar", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inventory_refuses_active_mod_or_plugin_junction_without_reading_the_foreign_target(
        bool modServer)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = modServer
            ? ModServer("inventory-mod-junction", ServerEcosystem.Fabric)
            : PaperServer("inventory-plugin-junction");
        var folderName = modServer ? "mods" : "plugins";
        var active = Path.Combine(server.RootPath, folderName);
        Directory.Delete(active);
        var foreign = Path.Combine(root, "foreign-inventory-" + folderName);
        Directory.CreateDirectory(foreign);
        CreateJar(
            Path.Combine(foreign, "Foreign.jar"),
            modServer ? "fabric.mod.json" : "plugin.yml",
            modServer
                ? "{\"schemaVersion\":1,\"id\":\"foreign\",\"version\":\"1.0\",\"name\":\"Foreign\",\"environment\":\"server\"}"
                : "name: Foreign\nversion: 1.0\n");
        CreateJunction(active, foreign);

        var failure = Assert.Throws<UnauthorizedAccessException>(() => Service().Inventory(server));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(foreign, "Foreign.jar")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Local_install_refuses_mod_or_plugin_junction_and_preserves_foreign_directory(
        bool modServer)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = modServer
            ? ModServer("install-mod-junction", ServerEcosystem.Fabric)
            : PaperServer("install-plugin-junction");
        var folderName = modServer ? "mods" : "plugins";
        var active = Path.Combine(server.RootPath, folderName);
        Directory.Delete(active);
        var foreign = Path.Combine(root, "foreign-install-" + folderName);
        Directory.CreateDirectory(foreign);
        var sentinel = Path.Combine(foreign, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "preserve");
        CreateJunction(active, foreign);
        var source = Path.Combine(root, "selected", modServer ? "IncomingMod.jar" : "IncomingPlugin.jar");
        CreateJar(
            source,
            modServer ? "fabric.mod.json" : "plugin.yml",
            modServer
                ? "{\"schemaVersion\":1,\"id\":\"incoming\",\"version\":\"1.0\",\"name\":\"Incoming\",\"environment\":\"server\"}"
                : "name: Incoming\nversion: 1.0\n");

        var failure = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service().InstallAsync(server, source));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinel));
        Assert.Single(Directory.EnumerateFiles(foreign));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Disable_refuses_reparse_in_disabled_destination_chain_and_preserves_source(
        bool junctionAtDisabledRoot)
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = PaperServer("disabled-junction-" + junctionAtDisabledRoot);
        var source = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var foreign = Path.Combine(root, "foreign-disabled-" + junctionAtDisabledRoot);
        Directory.CreateDirectory(foreign);
        var disabledRoot = Path.Combine(server.RootPath, ".chunkpilot-disabled");
        if (junctionAtDisabledRoot)
        {
            CreateJunction(disabledRoot, foreign);
        }
        else
        {
            Directory.CreateDirectory(disabledRoot);
            CreateJunction(Path.Combine(disabledRoot, "plugins"), foreign);
        }

        var failure = Assert.Throws<UnauthorizedAccessException>(() =>
            Service().SetEnabled(server, Path.Combine("plugins", "Fixture.jar"), enabled: false));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(foreign));
    }

    [Fact]
    public void Disable_refuses_a_directory_swapped_into_the_exact_jar_destination()
    {
        var server = PaperServer("disabled-file-directory-swap");
        var source = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var destination = Path.Combine(
            server.RootPath, ".chunkpilot-disabled", "plugins", "Fixture.jar");
        Directory.CreateDirectory(destination);

        var failure = Assert.Throws<IOException>(() =>
            Service().SetEnabled(server, Path.Combine("plugins", "Fixture.jar"), enabled: false));

        Assert.Contains("replaced by a directory", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public void Remove_refuses_reparse_in_recovery_chain_and_preserves_the_selected_jar()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = PaperServer("remove-recovery-junction");
        var source = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var paths = new AppDataPaths(Path.Combine(root, "remove-recovery-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        Directory.Delete(paths.Recovery);
        var foreign = Path.Combine(root, "foreign-recovery");
        Directory.CreateDirectory(foreign);
        CreateJunction(paths.Recovery, foreign);
        var service = new JarInventoryService(new SafeFileService(paths), paths);

        var failure = Assert.Throws<UnauthorizedAccessException>(() =>
            service.Remove(server, Path.Combine("plugins", "Fixture.jar")));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(source));
        Assert.Empty(Directory.EnumerateFileSystemEntries(foreign));
    }

    [Fact]
    public void Provider_provenance_refuses_reparse_storage_and_does_not_write_the_foreign_target()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = PaperServer("provenance-junction");
        var source = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var paths = new AppDataPaths(Path.Combine(root, "provenance-junction-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        Directory.Delete(paths.PluginProvenance);
        var foreign = Path.Combine(root, "foreign-provenance");
        Directory.CreateDirectory(foreign);
        CreateJunction(paths.PluginProvenance, foreign);
        var service = new JarInventoryService(new SafeFileService(paths), paths);

        var failure = Assert.Throws<UnauthorizedAccessException>(() =>
            service.RecordProviderProvenance(
                server, source, Release("fixture", "v1", "Fixture.jar", File.ReadAllBytes(source))));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(foreign));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void Config_ownership_refuses_a_reparse_path_without_reading_the_foreign_config()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = PaperServer("config-junction");
        CreateJar(
            Path.Combine(server.RootPath, "plugins", "Fixture.jar"),
            "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var foreign = Path.Combine(root, "foreign-config");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "config.yml"), "secret: preserve");
        CreateJunction(Path.Combine(server.RootPath, "plugins", "Fixture"), foreign);

        var failure = Assert.Throws<UnauthorizedAccessException>(() =>
            Service().ValidateConfigOwnership(
                server,
                Path.Combine("plugins", "Fixture.jar"),
                Path.Combine("plugins", "Fixture", "config.yml")));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("secret: preserve", File.ReadAllText(Path.Combine(foreign, "config.yml")));
    }

    [Fact]
    public void Rollback_refuses_reparse_destination_chain_and_keeps_the_recovery_source()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var server = PaperServer("rollback-active-junction");
        var source = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var service = Service();
        var receipt = service.SetEnabledWithReceipt(
            server, Path.Combine("plugins", "Fixture.jar"), enabled: false);
        Directory.Delete(Path.Combine(server.RootPath, "plugins"));
        var foreign = Path.Combine(root, "foreign-rollback");
        Directory.CreateDirectory(foreign);
        CreateJunction(Path.Combine(server.RootPath, "plugins"), foreign);

        var failure = Assert.Throws<UnauthorizedAccessException>(() => service.RollbackMove(server, receipt));

        Assert.Contains("reparse", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(receipt.DestinationPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(foreign));
    }

    [Fact]
    public void Rollback_recreates_a_missing_content_folder_without_weakening_containment()
    {
        var server = PaperServer("rollback-missing-folder");
        var source = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var sourceBytes = File.ReadAllBytes(source);
        var service = Service();
        var receipt = service.SetEnabledWithReceipt(
            server, Path.Combine("plugins", "Fixture.jar"), enabled: false);
        Directory.Delete(Path.Combine(server.RootPath, "plugins"));

        service.RollbackMove(server, receipt);

        Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        Assert.False(File.Exists(receipt.DestinationPath));
    }

    [Fact]
    public async Task Reviewed_provider_activation_refuses_a_target_that_appears_while_waiting_for_the_content_lock()
    {
        var server = PaperServer("provider-boundary-late-target");
        var paths = new AppDataPaths(Path.Combine(root, "provider-boundary-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var locks = new CanonicalPathLockManager();
        var jars = new JarInventoryService(new SafeFileService(paths, locks), paths, locks);
        Assert.Empty(jars.Inventory(server));
        var incomingBytes = Encoding.UTF8.GetBytes("reviewed provider payload");
        var release = Release("reviewed-project", "reviewed-version", "Boundary.jar", incomingBytes);
        var staged = Path.Combine(root, "provider-boundary-stage", release.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllBytesAsync(staged, incomingBytes);
        var lockPath = Path.Combine(server.RootPath, ".chunkpilot-addon-content.lock-key");
        var lease = await locks.AcquireAsync(lockPath);
        var activation = jars.InstallReviewedProviderWithReceiptAsync(server, staged, release, reviewedExisting: null);
        var target = Path.Combine(server.RootPath, "plugins", release.FileName);
        CreateJar(target, "plugin.yml", "name: User File\nversion: 1.0\n");
        var userBytes = await File.ReadAllBytesAsync(target);
        await lease.DisposeAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => activation);

        Assert.Contains("appeared after review", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(userBytes, await File.ReadAllBytesAsync(target));
        Assert.Empty(Directory.EnumerateFiles(paths.Recovery, "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(paths.PluginProvenance, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Reviewed_same_project_update_refuses_a_file_swap_and_preserves_the_new_user_file()
    {
        var server = PaperServer("provider-boundary-same-project-swap");
        var paths = new AppDataPaths(Path.Combine(root, "provider-boundary-update-data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var jars = new JarInventoryService(new SafeFileService(paths), paths);
        var current = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(current, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var oldBytes = await File.ReadAllBytesAsync(current);
        var oldRelease = Release("fixture", "v1", "Fixture.jar", oldBytes);
        jars.RecordProviderProvenance(server, current, oldRelease);
        var reviewed = Assert.Single(jars.Inventory(server));
        File.Delete(current);
        CreateJar(current, "plugin.yml", "name: User Replacement\nversion: 9.0\n");
        var userBytes = await File.ReadAllBytesAsync(current);
        var incomingBytes = Encoding.UTF8.GetBytes("provider v2 payload");
        var incomingRelease = Release("fixture", "v2", "Fixture.jar", incomingBytes);
        var staged = Path.Combine(root, "provider-boundary-update-stage", incomingRelease.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        await File.WriteAllBytesAsync(staged, incomingBytes);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jars.InstallReviewedProviderWithReceiptAsync(server, staged, incomingRelease, reviewed));

        Assert.Contains("changed before activation", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(userBytes, await File.ReadAllBytesAsync(current));
        Assert.Empty(Directory.EnumerateFiles(paths.Recovery, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Cancelled_local_install_removes_partial_destination_and_keeps_current_inventory()
    {
        var server = PaperServer("cancelled-local-install");
        var current = Path.Combine(server.RootPath, "plugins", "Current.jar");
        CreateJar(current, "plugin.yml", "name: Current\nversion: 1.0\n");
        var source = Path.Combine(root, "selected", "Incoming.jar");
        CreateJar(source, "plugin.yml", "name: Incoming\nversion: 2.0\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service().InstallAsync(server, source, cancellation.Token));

        Assert.True(File.Exists(current));
        Assert.False(File.Exists(Path.Combine(server.RootPath, "plugins", "Incoming.jar")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(server.RootPath, "plugins"), "*.tmp"));
    }

    [Fact]
    public async Task Install_receipt_restores_the_known_good_jar_after_failed_activation()
    {
        var server = PaperServer("install-rollback");
        var current = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(current, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var currentBytes = File.ReadAllBytes(current);
        var source = Path.Combine(root, "selected", "Fixture.jar");
        CreateJar(source, "plugin.yml", "name: Fixture\nversion: 2.0\n");
        var service = Service();

        var receipt = await service.InstallWithReceiptAsync(server, source);
        Assert.NotEqual(currentBytes, File.ReadAllBytes(current));

        service.RollbackInstall(server, receipt);

        Assert.Equal(currentBytes, File.ReadAllBytes(current));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(root, "data", "Recovery", server.Id.ToString("N"), "failed-plugin-activation"),
            "Fixture.jar", SearchOption.AllDirectories));
    }

    [Fact]
    public void Enable_disable_and_remove_receipts_restore_exact_jar_without_touching_configuration()
    {
        var server = PaperServer("move-rollback");
        var jar = Path.Combine(server.RootPath, "plugins", "Fixture.jar");
        CreateJar(jar, "plugin.yml", "name: Fixture\nversion: 1.0\n");
        var config = Path.Combine(server.RootPath, "plugins", "Fixture", "config.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "keep: true");
        var service = Service();

        var disabled = service.SetEnabledWithReceipt(server, Path.Combine("plugins", "Fixture.jar"), enabled: false);
        service.RollbackMove(server, disabled);
        Assert.True(File.Exists(jar));

        var removed = service.RemoveWithReceipt(server, Path.Combine("plugins", "Fixture.jar"));
        service.RollbackMove(server, removed);

        Assert.True(File.Exists(jar));
        Assert.Equal("keep: true", File.ReadAllText(config));
    }

    [Fact]
    public void Config_ownership_accepts_only_exact_identity_based_paths()
    {
        var paper = PaperServer("config-ownership-paper");
        CreateJar(Path.Combine(paper.RootPath, "plugins", "Fixture.jar"), "plugin.yml",
            "name: Fixture\nversion: 1.0\n");
        var owned = Path.Combine(paper.RootPath, "plugins", "Fixture", "config.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(owned)!);
        File.WriteAllText(owned, "enabled: true\n");
        var foreign = Path.Combine(paper.RootPath, "plugins", "Other", "config.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!);
        File.WriteAllText(foreign, "enabled: true\n");
        var service = Service();

        service.ValidateConfigOwnership(paper, Path.Combine("plugins", "Fixture.jar"),
            Path.Combine("plugins", "Fixture", "config.yml"));
        Assert.Throws<UnauthorizedAccessException>(() => service.ValidateConfigOwnership(
            paper, Path.Combine("plugins", "Fixture.jar"), Path.Combine("plugins", "Other", "config.yml")));

        var fabric = ModServer("config-ownership-fabric", ServerEcosystem.Fabric);
        CreateJar(Path.Combine(fabric.RootPath, "mods", "fixture.jar"), "fabric.mod.json",
            "{\"schemaVersion\":1,\"id\":\"fixture\",\"version\":\"1.0\",\"name\":\"Fixture\",\"environment\":\"server\"}");
        Directory.CreateDirectory(Path.Combine(fabric.RootPath, "config"));
        File.WriteAllText(Path.Combine(fabric.RootPath, "config", "fixture.toml"), "enabled=true\n");
        service.ValidateConfigOwnership(fabric, Path.Combine("mods", "fixture.jar"),
            Path.Combine("config", "fixture.toml"));
    }

    [Fact]
    public async Task Config_write_receipt_preserves_recovery_and_restores_known_good_content()
    {
        var server = PaperServer("config-write-rollback");
        var config = Path.Combine(server.RootPath, "plugins", "Fixture", "config.yml");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        await File.WriteAllTextAsync(config, "enabled: true\n");
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        var files = new SafeFileService(paths);
        var loaded = await files.ReadTextAsync(server.RootPath,
            Path.Combine("plugins", "Fixture", "config.yml"));

        var receipt = await files.WriteTextAtomicWithReceiptAsync(server.RootPath,
            loaded with { Content = "enabled: false\n" }, createRecoveryCopy: true);
        Assert.Equal("enabled: false\n", await File.ReadAllTextAsync(config));
        Assert.NotNull(receipt.RecoveryPath);
        Assert.True(File.Exists(receipt.RecoveryPath));

        await files.RollbackTextWriteAsync(server.RootPath, receipt);
        Assert.Equal("enabled: true\n", await File.ReadAllTextAsync(config));
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(paths.Recovery, "failed-activation"), "config.yml", SearchOption.AllDirectories));
    }

    private JarInventoryService Service()
    {
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "managed"));
        paths.EnsureCreated();
        return new JarInventoryService(new SafeFileService(paths), paths);
    }

    private ServerDefinition PaperServer(string name)
    {
        var serverRoot = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(serverRoot, "plugins"));
        return new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            RootPath = serverRoot,
            Ecosystem = ServerEcosystem.Paper,
            MinecraftVersion = "1.21.8"
        };
    }

    private ServerDefinition ModServer(string name, ServerEcosystem ecosystem)
    {
        var serverRoot = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(serverRoot, "mods"));
        return new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = name,
            RootPath = serverRoot,
            Ecosystem = ecosystem,
            MinecraftVersion = "26.2"
        };
    }

    private static void CreateJar(string path, string entryName, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("cmd.exe could not create the test junction.");
        process.WaitForExit(20_000);
        if (process.ExitCode != 0 || !Directory.Exists(link) ||
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("The test junction could not be created on this filesystem.");
    }

    private static PluginRelease Release(
        string projectId, string versionId, string fileName, byte[] bytes) => new()
    {
        Provider = PluginProviderKind.Modrinth,
        ProjectId = projectId,
        VersionId = versionId,
        VersionName = versionId,
        MinecraftVersion = "1.21.8",
        Loader = "paper",
        DownloadUrl = $"https://cdn.modrinth.com/data/{projectId}/{fileName}",
        FileName = fileName,
        SizeBytes = bytes.Length,
        Sha512 = Convert.ToHexString(SHA512.HashData(bytes))
    };

    private static PluginRelease CurseForgeRelease(
        string projectId, string versionId, string fileName, byte[] bytes) => new()
    {
        Kind = ManagedAddonKind.Mod,
        Provider = PluginProviderKind.CurseForge,
        ProjectId = projectId,
        VersionId = versionId,
        VersionName = versionId,
        MinecraftVersion = "1.21.1",
        Loader = "neoforge",
        DownloadUrl = $"https://mediafilez.forgecdn.net/files/{versionId}/{fileName}",
        FileName = fileName,
        SizeBytes = bytes.Length,
#pragma warning disable CA5350 // CurseForge publishes SHA-1 as provider identity evidence; production also records a local SHA-256 baseline.
        Sha1 = Convert.ToHexString(SHA1.HashData(bytes))
#pragma warning restore CA5350
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var result = response(request);
            result.RequestMessage ??= request;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeProvider(PluginRelease release) : IPluginCatalogProvider
    {
        public PluginProviderKind Provider => PluginProviderKind.Modrinth;
        public PluginProviderStatus Status => new(Provider, true, "Fixture");
        public Task<IReadOnlyList<PluginProject>> SearchAsync(PluginCatalogQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PluginProject>>([]);
        public Task<PluginRelease?> ResolveReleaseAsync(string projectId, string minecraftVersion, string loader,
            string? versionId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<PluginRelease?>(release);
    }

    private sealed class PlanProvider(
        IReadOnlyList<PluginRelease> releases,
        PluginProviderKind provider = PluginProviderKind.Modrinth) : IPluginCatalogProvider
    {
        public int ResolveCount { get; private set; }
        public PluginProviderKind Provider => provider;
        public PluginProviderStatus Status => new(Provider, true, "Fixture");
        public Task<IReadOnlyList<PluginProject>> SearchAsync(PluginCatalogQuery query,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<PluginProject>>([]);
        public Task<PluginRelease?> ResolveReleaseAsync(string projectId, string minecraftVersion, string loader,
            string? versionId = null, CancellationToken cancellationToken = default)
        {
            ResolveCount++;
            return Task.FromResult(releases.FirstOrDefault(item =>
                item.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(versionId) || item.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase))));
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
