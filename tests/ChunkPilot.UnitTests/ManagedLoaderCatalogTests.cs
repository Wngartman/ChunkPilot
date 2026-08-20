using System.Net;
using System.Text;
using System.Xml;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ManagedLoaderCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-loaders-" + Guid.NewGuid().ToString("N"));

    public ManagedLoaderCatalogTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Fabric_catalog_preserves_game_loader_installer_and_official_server_launcher_identity()
    {
        var service = Service(OfficialFixture());
        var versions = await service.GetVersionsAsync(ManagedLoaderPlatform.Fabric, forceRefresh: true);
        var builds = await service.GetBuildsAsync(ManagedLoaderPlatform.Fabric, "1.21.8", forceRefresh: true);

        var version = Assert.Single(versions.Versions, item => item.MinecraftVersion == "1.21.8");
        Assert.True(version.IsSelectable);
        Assert.Equal(21, version.RequiredJavaMajor);
        var stable = Assert.Single(builds.Builds, item => item.LoaderVersion == "0.17.2");
        Assert.Equal("1.1.0", stable.InstallerVersion);
        Assert.Equal(ManagedLoaderChannel.Stable, stable.Channel);
        Assert.True(stable.IsSelectable);
        Assert.Contains("/1.21.8/0.17.2/1.1.0/server/jar", stable.ArtifactUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("20.2.88", "1.20.2")]
    [InlineData("20.4.167", "1.20.4")]
    [InlineData("21.1.207", "1.21.1")]
    [InlineData("21.8.22", "1.21.8")]
    [InlineData("26.1.0.5-beta", "26.1")]
    [InlineData("26.1.1.2", "26.1.1")]
    [InlineData("26.2.0.35-beta", "26.2")]
    public void NeoForge_mapping_follows_the_documented_version_scheme(string loader, string minecraft) =>
        Assert.Equal(minecraft, ManagedLoaderCatalogService.MapNeoForgeMinecraftVersion(loader));

    [Fact]
    public async Task NeoForge_catalog_maps_exact_installers_and_requires_provider_checksum()
    {
        var service = Service(OfficialFixture());
        var versions = await service.GetVersionsAsync(ManagedLoaderPlatform.NeoForge, forceRefresh: true);
        var builds = await service.GetBuildsAsync(ManagedLoaderPlatform.NeoForge, "1.21.1", forceRefresh: true);

        Assert.Contains(versions.Versions, item => item.MinecraftVersion == "1.21.1" && item.IsSelectable);
        var stable = Assert.Single(builds.Builds, item => item.LoaderVersion == "21.1.207");
        var beta = Assert.Single(builds.Builds, item => item.LoaderVersion == "21.1.208-beta");
        Assert.Equal(ManagedLoaderChannel.Stable, stable.Channel);
        Assert.Equal(ManagedLoaderChannel.Beta, beta.Channel);
        Assert.True(stable.HasProviderIntegrity);
        Assert.True(stable.IsSelectable);
        Assert.EndsWith("neoforge-21.1.207-installer.jar", stable.ArtifactUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_managed_loader_platform_has_an_explicit_non_fallthrough_strategy()
    {
        Assert.Equal(0, (int)ManagedLoaderPlatform.Fabric);
        Assert.Equal(1, (int)ManagedLoaderPlatform.NeoForge);
        foreach (var platform in Enum.GetValues<ManagedLoaderPlatform>())
        {
            var strategy = ManagedLoaderPlatformStrategies.For(platform);
            Assert.Equal(platform, strategy.Platform);
            Assert.StartsWith("https://", strategy.OfficialSourceUrl, StringComparison.Ordinal);
        }

        Assert.True(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Fabric).SupportsTypedCreation);
        Assert.True(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.NeoForge).SupportsRuntimeCertification);
        Assert.True(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Forge).SupportsTypedCreation);
        Assert.True(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Forge).SupportsRuntimeCertification);
        Assert.True(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Quilt).SupportsUpdateMaterialization);
        Assert.True(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Quilt).SupportsRuntimeCertification);
        Assert.False(ManagedLoaderPlatformStrategies.For(ManagedLoaderPlatform.Ornithe).SupportsTypedCreation);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ManagedLoaderPlatformStrategies.For((ManagedLoaderPlatform)999));
    }

    [Theory]
    [InlineData("1.7.10-10.13.4.1614-1.7.10", "1.7.10")]
    [InlineData("1.21.1-52.1.16", "1.21.1")]
    [InlineData("26.2-65.1.0", "26.2")]
    [InlineData("not-a-coordinate", null)]
    public void Forge_coordinate_parser_preserves_exact_Minecraft_identity(string coordinate, string? expected) =>
        Assert.Equal(expected, ManagedLoaderCatalogService.MapForgeMinecraftVersion(coordinate));

    [Fact]
    public async Task Forge_catalog_uses_promotions_not_Maven_list_order_and_preserves_legacy_coordinate()
    {
        var service = Service(OfficialFixture());
        var versions = await service.GetVersionsAsync(ManagedLoaderPlatform.Forge, forceRefresh: true);
        var builds = await service.GetBuildsAsync(ManagedLoaderPlatform.Forge, "1.21.1", forceRefresh: true);
        var legacy = await service.GetBuildsAsync(ManagedLoaderPlatform.Forge, "1.7.10", forceRefresh: true);

        Assert.Contains(versions.Versions, item => item.MinecraftVersion == "1.21.1");
        var recommended = Assert.Single(builds.Builds, item => item.ProviderRecommended);
        Assert.Equal("52.1.0", recommended.LoaderVersion);
        Assert.Equal("1.21.1-52.1.0", recommended.InstallerVersion);
        Assert.True(recommended.HasProviderIntegrity);
        Assert.True(recommended.IsSelectable);
        Assert.Empty(builds.CreationUnavailableDetail);
        var legacyRecommended = Assert.Single(legacy.Builds, item => item.ProviderRecommended);
        Assert.Equal("10.13.4.1614", legacyRecommended.LoaderVersion);
        Assert.Equal("1.7.10-10.13.4.1614-1.7.10", legacyRecommended.InstallerVersion);
    }

    [Fact]
    public async Task Quilt_catalog_keeps_provider_recommended_beta_distinct_from_stable_and_creation_support()
    {
        var service = Service(OfficialFixture());
        var versions = await service.GetVersionsAsync(ManagedLoaderPlatform.Quilt, forceRefresh: true);
        var builds = await service.GetBuildsAsync(ManagedLoaderPlatform.Quilt, "1.20.1", forceRefresh: true);

        Assert.Contains(versions.Versions, item => item.MinecraftVersion == "1.20.1");
        var recommended = Assert.Single(builds.Builds, item => item.ProviderRecommended);
        Assert.Equal("0.30.1-beta.2", recommended.LoaderVersion);
        Assert.Equal(ManagedLoaderChannel.Beta, recommended.Channel);
        Assert.False(recommended.ProviderLatest);
        Assert.Equal("0.15.1", recommended.InstallerVersion);
        Assert.Equal(17, recommended.InstallerJavaMajor);
        Assert.True(recommended.HasProviderIntegrity);
        Assert.True(recommended.IsSelectable);
        Assert.Empty(recommended.UnavailableReason);
    }

    [Fact]
    public async Task Quilt_exact_install_plan_targets_the_transaction_staging_root()
    {
        var service = new LoaderMetadataService(new HttpClient(OfficialFixture()));

        var plan = await service.ResolveAsync(InstallSourceType.Quilt, "1.20.1", "0.30.0");

        Assert.Equal("0.30.0", plan.LoaderVersion);
        Assert.Equal("quilt-server-launch.jar", plan.ExpectedLaunchFile);
        Assert.Contains("--install-dir=.", plan.InstallerArgument, StringComparison.Ordinal);
    }

    [Fact]
    public void Quilt_installer_Java_policy_repairs_older_cached_catalogs_without_changing_server_Java()
    {
        Assert.Equal(17, ManagedLoaderInstallerJavaPolicy.Resolve(
            ManagedLoaderPlatform.Quilt, declaredInstallerJavaMajor: null, runtimeJavaMajor: 8));
        Assert.Equal(17, ManagedLoaderInstallerJavaPolicy.Resolve(
            ManagedLoaderPlatform.Quilt, declaredInstallerJavaMajor: null, runtimeJavaMajor: 16));
        Assert.Equal(21, ManagedLoaderInstallerJavaPolicy.Resolve(
            ManagedLoaderPlatform.Quilt, declaredInstallerJavaMajor: null, runtimeJavaMajor: 21));
        Assert.Equal(8, ManagedLoaderInstallerJavaPolicy.Resolve(
            ManagedLoaderPlatform.Forge, declaredInstallerJavaMajor: null, runtimeJavaMajor: 8));
    }

    [Fact]
    public async Task Legacy_Fabric_and_Ornithe_expose_only_truthful_catalog_identities_for_historic_versions()
    {
        var service = Service(OfficialFixture());
        var legacy = await service.GetVersionsAsync(ManagedLoaderPlatform.LegacyFabric, forceRefresh: true);
        var legacyUnsupported = await service.GetBuildsAsync(
            ManagedLoaderPlatform.LegacyFabric, "b1.8", forceRefresh: true);
        var ornithe = await service.GetVersionsAsync(ManagedLoaderPlatform.Ornithe, forceRefresh: true);
        var ornitheBlocked = await service.GetBuildsAsync(
            ManagedLoaderPlatform.Ornithe, "1.0", forceRefresh: true);

        Assert.Contains(legacy.Versions, item => item.MinecraftVersion == "1.3.1");
        Assert.DoesNotContain(legacy.Versions, item => item.MinecraftVersion is "1.0" or "b1.8" or "b1.8.1");
        Assert.Contains("does not support", legacyUnsupported.UnavailableDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("user-supplied original server JAR", legacyUnsupported.UnavailableDetail,
            StringComparison.OrdinalIgnoreCase);

        var release = Assert.Single(ornithe.Versions, item => item.MinecraftVersion == "1.0");
        Assert.Equal("1.0.0", release.ProviderMinecraftVersion);
        Assert.True(release.RequiresUserSuppliedMinecraftServerJar);
        Assert.Equal(8, release.RequiredJavaMajor);
        Assert.False(release.IsSelectable);
        Assert.Contains(ornithe.Versions, item => item.MinecraftVersion == "b1.8" &&
            item.RequiresUserSuppliedMinecraftServerJar && !item.StableMinecraft);
        Assert.Contains(ornithe.Versions, item => item.MinecraftVersion == "b1.8.1" &&
            item.RequiresUserSuppliedMinecraftServerJar);
        Assert.Contains("Mojang's official version metadata publishes no server download", ornitheBlocked.UnavailableDetail,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Catalog_only_platform_cannot_fall_through_to_managed_update_materialization()
    {
        var provider = new ManagedLoaderUpdateProvider(Service(OfficialFixture()));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetVersionsAsync(
            new UpdateSource
            {
                Provider = UpdateProvider.ManagedLoader,
                Loader = "LegacyFabric",
                MinecraftVersion = "1.12.2"
            }, new UpdatePreferences()));

        Assert.Contains("not implemented", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Maven_parser_rejects_DTDs_in_provider_metadata()
    {
        const string xml = "<!DOCTYPE metadata [<!ENTITY xxe SYSTEM 'file:///windows/win.ini'>]>" +
                           "<metadata><versioning><release>&xxe;</release></versioning></metadata>";
        Assert.Throws<XmlException>(() => ManagedLoaderCatalogService.ParseMavenRelease(xml));
    }

    [Fact]
    public async Task Managed_loader_update_provider_returns_only_exact_same_game_version_materialization()
    {
        var catalog = Service(OfficialFixture());
        var provider = new ManagedLoaderUpdateProvider(catalog);
        var versions = await provider.GetVersionsAsync(new UpdateSource
        {
            Provider = UpdateProvider.ManagedLoader,
            ProjectId = "neoforge",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            LoaderVersion = "21.1.207",
            InstalledVersionId = "1.21.1-21.1.207-21.1.207"
        }, new UpdatePreferences());

        Assert.All(versions, item => Assert.Equal("1.21.1", item.MinecraftVersion));
        var stable = Assert.Single(versions, item => item.LoaderVersion == "21.1.207");
        Assert.Equal(UpdateProvider.ManagedLoader, provider.Provider);
        Assert.Equal("neoforge-installer", stable.PackageType);
        Assert.Equal("21.1.207", stable.InstallerVersion);
        Assert.Equal(stable.RequiredJavaMajor, stable.InstallerJavaMajor);
        Assert.Equal(new string('a', 64), stable.Sha256);
    }

    [Fact]
    public async Task Loader_catalog_uses_fresh_cache_and_degrades_to_stale_without_erasing_identity()
    {
        var handler = OfficialFixture();
        var service = Service(handler);
        var current = await service.GetVersionsAsync(ManagedLoaderPlatform.Fabric, forceRefresh: true);
        var firstRequests = handler.RequestCount;
        var cached = await service.GetVersionsAsync(ManagedLoaderPlatform.Fabric);
        var offline = new ManagedLoaderCatalogService(new AppDataPaths(root),
            new HttpClient(new FixtureHandler(_ => throw new HttpRequestException("offline"))), TimeSpan.Zero);
        var stale = await offline.GetVersionsAsync(ManagedLoaderPlatform.Fabric, forceRefresh: true);

        Assert.True(cached.IsFromCache);
        Assert.False(cached.IsStale);
        Assert.Equal(firstRequests, handler.RequestCount);
        Assert.True(stale.IsFromCache);
        Assert.True(stale.IsStale);
        Assert.Equal(current.Versions.Select(item => item.MinecraftVersion),
            stale.Versions.Select(item => item.MinecraftVersion));
    }

    [Fact]
    public void Loader_creation_plan_binds_platform_minecraft_loader_and_EULA()
    {
        var version = new ManagedLoaderMinecraftVersion
        {
            Platform = ManagedLoaderPlatform.Fabric,
            MinecraftVersion = "1.21.8",
            StableMinecraft = true,
            RequiredJavaMajor = 21
        };
        var build = new ManagedLoaderBuild
        {
            Platform = ManagedLoaderPlatform.NeoForge,
            MinecraftVersion = "1.21.8",
            LoaderVersion = "21.8.22",
            ArtifactUrl = "https://maven.neoforged.net/example.jar",
            ArtifactSha256 = new string('a', 64),
            RequiredJavaMajor = 21
        };
        var plan = new ManagedLoaderCreationPlan
        {
            ServerName = "Loader mismatch",
            Version = version,
            Build = build,
            ExperimentalRuntimeRiskAccepted = true,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            }
        };

        Assert.Contains(plan.Problems(), item => item.Contains("exact compatible loader", StringComparison.Ordinal));
        var valid = plan with { Build = build with { Platform = ManagedLoaderPlatform.Fabric } };
        Assert.Empty(valid.Problems());
    }

    [Fact]
    public void Runtime_evidence_promotes_only_the_exact_certified_loader_identity()
    {
        var version = ManagedLoaderRuntimeCertificationEvidence.Apply(new ManagedLoaderMinecraftVersion
        {
            Platform = ManagedLoaderPlatform.Fabric,
            MinecraftVersion = "26.2",
            StableMinecraft = true,
            RequiredJavaMajor = 25
        });
        var exact = ManagedLoaderRuntimeCertificationEvidence.Apply(new ManagedLoaderBuild
        {
            Platform = ManagedLoaderPlatform.Fabric,
            MinecraftVersion = "26.2",
            LoaderVersion = "0.19.3",
            InstallerVersion = "1.1.2",
            ArtifactUrl = "https://meta.fabricmc.net/v2/versions/loader/26.2/0.19.3/1.1.2/server/jar",
            RequiredJavaMajor = 25
        });
        var adjacent = ManagedLoaderRuntimeCertificationEvidence.Apply(exact with
        {
            LoaderVersion = "0.19.2",
            ArtifactSha256 = "",
            SupportTier = MinecraftVersionSupportTier.Experimental,
            Certification = new MinecraftVersionCertification
            {
                Level = MinecraftVersionCertificationLevel.MetadataValidated
            }
        });

        Assert.Equal(MinecraftVersionSupportTier.Recommended, version.SupportTier);
        Assert.Equal(MinecraftVersionSupportTier.Recommended, exact.SupportTier);
        Assert.True(exact.HasProviderIntegrity);
        Assert.Equal(MinecraftVersionCertificationLevel.RuntimeCertified, exact.Certification.Level);
        Assert.Equal(MinecraftVersionSupportTier.Experimental, adjacent.SupportTier);
    }

    [Theory]
    [InlineData(ManagedLoaderPlatform.Quilt, "1.14.4", "0.30.0", "0.15.1", "0a229138caa1b87fd8f5622038410696f98bb85871a279640e7002404c4d0dc2")]
    [InlineData(ManagedLoaderPlatform.Forge, "1.5.2", "7.8.1.738", "1.5.2-7.8.1.738", "b9bb39fa659fcf4f80acfbceca4e327d7750dd502ee5c00520da8a26d8783e84")]
    public void Embedded_campaign_evidence_promotes_exact_historical_identity(
        ManagedLoaderPlatform platform,
        string minecraft,
        string loader,
        string installer,
        string sha256)
    {
        var certified = ManagedLoaderRuntimeCertificationEvidence.Apply(new ManagedLoaderBuild
        {
            Platform = platform,
            MinecraftVersion = minecraft,
            LoaderVersion = loader,
            InstallerVersion = installer,
            ArtifactUrl = "https://official.invalid/exact-loader.jar",
            ArtifactSha256 = sha256,
            RequiredJavaMajor = 8
        });

        Assert.Equal(MinecraftVersionSupportTier.Verified, certified.SupportTier);
        Assert.Equal(MinecraftVersionCertificationLevel.RuntimeCertified, certified.Certification.Level);
    }

    private ManagedLoaderCatalogService Service(HttpMessageHandler handler) =>
        new(new AppDataPaths(root), new HttpClient(handler));

    private static FixtureHandler OfficialFixture() => new(request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Equals("https://meta.fabricmc.net/v2/versions/game", StringComparison.Ordinal))
            return Json("""[{"version":"1.21.8","stable":true},{"version":"26.2-snapshot-1","stable":false}]""");
        if (url.EndsWith("/v2/versions/loader/1.21.8", StringComparison.Ordinal))
            return Json("""
                [{"loader":{"version":"0.17.2","stable":true}},
                 {"loader":{"version":"0.17.1-beta","stable":false}}]
                """);
        if (url.EndsWith("/v2/versions/installer", StringComparison.Ordinal))
            return Json("""[{"version":"1.1.0","stable":true}]""");
        if (url.Contains("/api/maven/versions/", StringComparison.Ordinal))
            return Json("""
                {"isSnapshot":false,"versions":[
                  "20.4.167","21.1.207","21.1.208-beta","26.2.0.35-beta"
                ]}
                """);
        if (url.Equals(ManagedLoaderCatalogService.ForgeMetadataUrl, StringComparison.Ordinal))
            return Xml("""
                <metadata><versioning><versions>
                  <version>1.21.1-52.1.16</version>
                  <version>1.7.10-10.13.4.1614-1.7.10</version>
                  <version>1.21.1-52.1.0</version>
                  <version>1.21.1-52.0.1</version>
                </versions></versioning></metadata>
                """);
        if (url.Equals(ManagedLoaderCatalogService.ForgePromotionsUrl, StringComparison.Ordinal))
            return Json("""
                {"promos":{
                  "1.21.1-recommended":"52.1.0","1.21.1-latest":"52.1.16",
                  "1.7.10-recommended":"10.13.4.1614","1.7.10-latest":"10.13.4.1614"
                }}
                """);
        if (url.Equals("https://meta.quiltmc.org/v3/versions/game", StringComparison.Ordinal))
            return Json("""[{"version":"1.20.1","stable":true},{"version":"1.14.4","stable":true}]""");
        if (url.Equals("https://meta.quiltmc.org/v3/versions/loader/1.20.1", StringComparison.Ordinal))
            return Json("""
                [{"loader":{"version":"0.30.0"},"launcherMeta":{"min_java_version":8}},
                 {"loader":{"version":"0.30.1-beta.2"},"launcherMeta":{"min_java_version":8}}]
                """);
        if (url.Equals(ManagedLoaderCatalogService.QuiltRecommendationsUrl, StringComparison.Ordinal))
            return Json("""{"1.20.1":{"quilt_loader":"0.30.1-beta.2"}}""");
        if (url.Contains("org/quiltmc/quilt-installer/maven-metadata.xml", StringComparison.Ordinal))
            return Xml("""
                <metadata><versioning><latest>0.15.1</latest><release>0.15.1</release>
                <versions><version>0.15.0</version><version>0.15.1</version></versions>
                </versioning></metadata>
                """);
        if (url.Equals("https://meta.legacyfabric.net/v2/versions/game", StringComparison.Ordinal))
            return Json("""[{"version":"1.13.2","stable":true},{"version":"1.3.1","stable":true}]""");
        if (url.Equals(ManagedLoaderCatalogService.OrnitheGenerationUrl, StringComparison.Ordinal))
            return Json("""{"latestIntermediaryGeneration":2,"stableIntermediaryGeneration":2}""");
        if (url.Equals("https://meta.ornithemc.net/v3/versions/gen2/intermediary", StringComparison.Ordinal))
            return Json("""
                [{"version":"1.14.4","stable":true},{"version":"1.0.0","stable":true},
                 {"version":"b1.8","stable":true},{"version":"b1.8.1","stable":true}]
                """);
        if (url.EndsWith(".sha256", StringComparison.Ordinal))
            return Text(new string('a', 64));
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static HttpResponseMessage Json(string value) => Text(value, "application/json");

    private static HttpResponseMessage Xml(string value) => Text(value, "application/xml");

    private static HttpResponseMessage Text(string value, string mediaType = "text/plain") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, new UTF8Encoding(false), mediaType)
    };

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int requests;
        public int RequestCount => requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(responder(request));
        }
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
