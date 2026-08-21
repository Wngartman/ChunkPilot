using System.Net;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class OrnitheHeadlessProviderTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-ornithe-" + Guid.NewGuid().ToString("N"));

    public OrnitheHeadlessProviderTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData("1.0", "1.0.0", HistoricalMinecraftServerArtifactSource.UserSupplied)]
    [InlineData("1.2.5", "1.2.5", HistoricalMinecraftServerArtifactSource.OfficialMojang)]
    [InlineData("b1.8", "b1.8", HistoricalMinecraftServerArtifactSource.UserSupplied)]
    [InlineData("b1.8.1", "b1.8.1", HistoricalMinecraftServerArtifactSource.UserSupplied)]
    public async Task Official_provider_exposes_typed_Fabric_and_Quilt_headless_builds(
        string minecraft,
        string providerMinecraft,
        HistoricalMinecraftServerArtifactSource source)
    {
        var service = Service(Fixture());

        var catalog = await service.GetBuildsAsync(
            ManagedLoaderPlatform.Ornithe, minecraft, forceRefresh: true);

        Assert.True(catalog.ProviderAvailable);
        Assert.Equal(2, catalog.Builds.Count);
        Assert.Contains(catalog.Builds, build => build.OrnitheLoaderFamily == OrnitheLoaderFamily.Fabric &&
            build.LoaderVersion == "0.19.3");
        Assert.Contains(catalog.Builds, build => build.OrnitheLoaderFamily == OrnitheLoaderFamily.Quilt &&
            build.LoaderVersion == "0.30.1-beta.2");
        Assert.All(catalog.Builds, build =>
        {
            Assert.Equal(providerMinecraft, build.ProviderMinecraftVersion);
            Assert.Equal(2, build.IntermediaryGeneration);
            Assert.Equal(8, build.RequiredJavaMajor);
            Assert.True(build.HasHeadlessProfileContract);
            Assert.Equal(source, build.MinecraftServerArtifact!.Source);
            Assert.False(build.IsSelectable);
            Assert.Contains("meta.ornithemc.net", build.HeadlessProfileUrl, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Exact_profile_and_official_base_produce_a_path_free_headless_plan()
    {
        var service = Service(Fixture());
        var builds = await service.GetBuildsAsync(
            ManagedLoaderPlatform.Ornithe, "1.2.5", forceRefresh: true);
        var build = Assert.Single(builds.Builds, item => item.OrnitheLoaderFamily == OrnitheLoaderFamily.Fabric);
        var profile = await service.GetOrnitheHeadlessProfileAsync(
            "1.2.5", OrnitheLoaderFamily.Fabric, build.LoaderVersion);

        var plan = OrnitheHeadlessMaterializationPlanner.Create(build, profile);

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotServer", plan.MainClass);
        Assert.Equal("d8321edc9470e56b8ad5c67bbd16beba25843336",
            plan.MinecraftServerArtifact.OfficialSha1);
        Assert.True(plan.MinecraftServerArtifact.IsAutomaticallyAcquirable);
        Assert.Contains("nogui", plan.GameArguments);
        Assert.Equal("minecraft-server.jar", plan.ClassPath[0]);
        Assert.All(plan.Libraries, library =>
        {
            Assert.StartsWith("https://", library.DownloadUrl, StringComparison.Ordinal);
            Assert.StartsWith("libraries", library.RelativePath, StringComparison.Ordinal);
        });
        Assert.Contains(plan.Libraries, library =>
            library.IntegrityRequirement == HeadlessArtifactIntegrityRequirement.ProviderSha256);
        Assert.Contains(plan.Libraries, library =>
            library.IntegrityRequirement == HeadlessArtifactIntegrityRequirement.OfficialMavenSidecar);
        Assert.DoesNotContain(root, string.Join('|', plan.ClassPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task User_supplied_historical_base_requires_an_opaque_native_token()
    {
        var service = Service(Fixture());
        var builds = await service.GetBuildsAsync(
            ManagedLoaderPlatform.Ornithe, "b1.8", forceRefresh: true);
        var build = Assert.Single(builds.Builds, item => item.OrnitheLoaderFamily == OrnitheLoaderFamily.Fabric);
        var profile = await service.GetOrnitheHeadlessProfileAsync(
            "b1.8", OrnitheLoaderFamily.Fabric, build.LoaderVersion);

        var error = Assert.Throws<InvalidOperationException>(() =>
            OrnitheHeadlessMaterializationPlanner.Create(build, profile));
        Assert.Contains("native server-artifact token", error.Message, StringComparison.OrdinalIgnoreCase);

        var plan = OrnitheHeadlessMaterializationPlanner.Create(build, profile, "opaque-single-use-token");
        Assert.True(plan.RequiresUserSuppliedArtifact);
        Assert.Equal("opaque-single-use-token", plan.UserSuppliedArtifactToken);
        Assert.DoesNotContain(':', plan.UserSuppliedArtifactToken);
    }

    [Fact]
    public async Task Profile_parser_rejects_an_unapproved_library_origin()
    {
        var service = Service(Fixture(unapprovedLibrary: true));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.GetOrnitheHeadlessProfileAsync(
                "1.2.5", OrnitheLoaderFamily.Fabric, "0.19.3"));

        Assert.Contains("unapproved library source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Certification_preflight_enforces_disposable_EULA_and_exports_compact_path_free_evidence()
    {
        var service = Service(Fixture());
        var builds = await service.GetBuildsAsync(
            ManagedLoaderPlatform.Ornithe, "1.2.5", forceRefresh: true);
        var build = Assert.Single(builds.Builds, item => item.OrnitheLoaderFamily == OrnitheLoaderFamily.Fabric);
        var profile = await service.GetOrnitheHeadlessProfileAsync(
            "1.2.5", OrnitheLoaderFamily.Fabric, build.LoaderVersion);
        var plan = OrnitheHeadlessMaterializationPlanner.Create(build, profile);

        Assert.Equal(HeadlessCertificationResult.BlockedEulaAuthorization,
            OrnitheHeadlessCertificationPolicy.Preflight(new OrnitheHeadlessCertificationRequest
            {
                Plan = plan,
                ExplicitDisposableEulaAuthorization = false
            }));
        Assert.Null(OrnitheHeadlessCertificationPolicy.Preflight(new OrnitheHeadlessCertificationRequest
        {
            Plan = plan,
            ExplicitDisposableEulaAuthorization = true
        }));

        var json = OrnitheHeadlessCertificationPolicy.Export(
        [
            new OrnitheHeadlessCertificationEvidence
            {
                MinecraftVersion = "1.2.5",
                ProviderMinecraftVersion = "1.2.5",
                IntermediaryGeneration = 2,
                LoaderFamily = OrnitheLoaderFamily.Fabric,
                LoaderVersion = "0.19.3",
                ProfileMetadataSha256 = profile.MetadataSha256,
                MinecraftServerSha256 = new string('a', 64),
                JavaMajor = 8,
                Result = HeadlessCertificationResult.Passed,
                RuntimeLaunched = true,
                ReadinessConfirmed = true,
                PlayerStatusSource = PlayerStatusSource.LegacySimpleStatus,
                CleanStopConfirmed = true,
                NoUnexpectedGuiConfirmed = true,
                CleanupSucceeded = true,
                CompletedUtc = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero)
            }
        ]);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Single(document.RootElement.GetProperty("entries").EnumerateArray());
        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javaPath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serverRoot", json, StringComparison.OrdinalIgnoreCase);
    }

    private ManagedLoaderCatalogService Service(HttpMessageHandler handler) =>
        new(new AppDataPaths(root), new HttpClient(handler));

    private static HttpMessageHandler Fixture(bool unapprovedLibrary = false) =>
        new FixtureHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == ManagedLoaderCatalogService.OrnitheGenerationUrl)
                return Json("""{"latestIntermediaryGeneration":2,"stableIntermediaryGeneration":2}""");
            foreach (var providerVersion in ProviderVersions)
            {
                if (url == $"https://meta.ornithemc.net/v3/versions/gen2/fabric-loader/{providerVersion}")
                    return Json($$$"""
                        [{"loader":{"version":"0.19.3","stable":true},
                          "intermediary":{"version":"{{{providerVersion}}}","stable":true},
                          "launcherMeta":{"min_java_version":8}}]
                        """);
                if (url == $"https://meta.ornithemc.net/v3/versions/gen2/quilt-loader/{providerVersion}")
                    return Json($$$"""
                        [{"loader":{"version":"0.30.1-beta.2","stable":true},
                          "intermediary":{"version":"{{{providerVersion}}}","stable":true},
                          "launcherMeta":{"min_java_version":8}}]
                        """);
            }
            if (url.Contains("/server/json", StringComparison.Ordinal))
            {
                var providerVersion = url.Contains("/1.0.0/", StringComparison.Ordinal) ? "1.0.0" :
                    url.Contains("/b1.8.1/", StringComparison.Ordinal) ? "b1.8.1" :
                    url.Contains("/b1.8/", StringComparison.Ordinal) ? "b1.8" : "1.2.5";
                var fabric = url.Contains("/fabric-loader/", StringComparison.Ordinal);
                var loader = fabric ? "0.19.3" : "0.30.1-beta.2";
                var main = fabric
                    ? "net.fabricmc.loader.impl.launch.knot.KnotServer"
                    : "org.quiltmc.loader.impl.launch.knot.KnotServer";
                var repository = unapprovedLibrary ? "https://downloads.invalid/" : "https://maven.fabricmc.net/";
                return Json($$$"""
                    {
                      "id":"{{{(fabric ? "fabric" : "quilt")}}}-loader-{{{loader}}}-{{{providerVersion}}}-ornithe-gen2",
                      "inheritsFrom":"{{{providerVersion}}}-vanilla",
                      "mainClass":"{{{main}}}",
                      "arguments":{"jvm":["-Dfabric.gameVersion={{{providerVersion}}}"],"game":[]},
                      "libraries":[
                        {"name":"org.ow2.asm:asm:9.10.1","url":"{{{repository}}}",
                         "sha1":"ada2141c0cc52ee8f5c48cd5fa4ce0e794f22236",
                         "sha256":"ed825d10ab1399c8c0cb669e688cf0c8c82629b4c8399b58352b68e92ca10fcb",
                         "size":126151},
                        {"name":"net.ornithemc:calamus-intermediary-gen2:{{{providerVersion}}}",
                         "url":"https://maven.ornithemc.net/releases/"}
                      ]
                    }
                    """);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, new UTF8Encoding(false), "application/json")
    };

    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responder(request));
    }

    private static readonly string[] ProviderVersions = ["1.0.0", "1.2.5", "b1.8", "b1.8.1"];

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
