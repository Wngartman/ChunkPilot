using System.Net;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class PaperVersionCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-paper-" + Guid.NewGuid().ToString("N"));

    public PaperVersionCatalogTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Official_project_inventory_is_typed_and_preview_versions_are_not_selectable()
    {
        var catalog = await Service(Fixture()).GetVersionsAsync(forceRefresh: true);

        Assert.True(catalog.ProviderAvailable);
        var stable = Assert.Single(catalog.Versions, item => item.VersionId == "1.21.8");
        var preview = Assert.Single(catalog.Versions, item => item.VersionId == "1.21.9-rc1");
        Assert.True(stable.IsSelectable);
        Assert.Equal(21, stable.RequiredJavaMajor);
        Assert.False(preview.IsSelectable);
        Assert.Equal(MinecraftReleaseKind.ReleaseCandidate, preview.ReleaseKind);
    }

    [Fact]
    public async Task Exact_builds_preserve_channel_hash_size_url_and_identity()
    {
        var catalog = await Service(Fixture()).GetBuildsAsync("1.21.8", forceRefresh: true);

        Assert.True(catalog.ProviderAvailable);
        var stable = Assert.Single(catalog.Builds, item => item.BuildId == 42);
        var beta = Assert.Single(catalog.Builds, item => item.BuildId == 41);
        Assert.Equal(PaperBuildChannel.Stable, stable.Channel);
        Assert.True(stable.HasIntegrityMetadata);
        Assert.True(stable.IsSelectable);
        Assert.Equal("1.21.8", stable.MinecraftVersion);
        Assert.Equal(64, stable.ServerSha256.Length);
        Assert.Equal(54_846_016, stable.ServerSizeBytes);
        Assert.StartsWith("https://", stable.DownloadUrl, StringComparison.Ordinal);
        Assert.True(beta.IsSelectable);
        Assert.Equal(MinecraftVersionSupportTier.Experimental, beta.SupportTier);
    }

    [Fact]
    public async Task Fresh_version_and_build_caches_avoid_repeat_provider_requests()
    {
        var handler = Fixture();
        var service = Service(handler);
        _ = await service.GetVersionsAsync(forceRefresh: true);
        _ = await service.GetBuildsAsync("1.21.8", forceRefresh: true);
        var firstRequestCount = handler.RequestCount;

        var versions = await service.GetVersionsAsync();
        var builds = await service.GetBuildsAsync("1.21.8");

        Assert.True(versions.IsFromCache);
        Assert.True(builds.IsFromCache);
        Assert.Equal(firstRequestCount, handler.RequestCount);
    }

    [Fact]
    public async Task Failed_forced_refresh_keeps_exact_cached_builds_and_marks_them_stale()
    {
        var paths = new AppDataPaths(root);
        var good = new PaperVersionCatalogService(paths, new HttpClient(Fixture()), TimeSpan.Zero);
        var populated = await good.GetBuildsAsync("1.21.8", forceRefresh: true);
        var failing = new PaperVersionCatalogService(paths,
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))), TimeSpan.Zero);

        var degraded = await failing.GetBuildsAsync("1.21.8", forceRefresh: true);

        Assert.True(degraded.IsFromCache);
        Assert.True(degraded.IsStale);
        Assert.Equal(populated.Builds.Select(item => item.BuildId), degraded.Builds.Select(item => item.BuildId));
    }

    [Fact]
    public async Task Paper_update_provider_keeps_build_updates_on_the_installed_Minecraft_version()
    {
        var adapter = new PaperMcUpdateProvider(Service(Fixture()));
        var source = new UpdateSource
        {
            Provider = UpdateProvider.PaperMC,
            ProjectId = "paper",
            MinecraftVersion = "1.21.8",
            Loader = "Paper",
            LoaderVersion = "41"
        };

        var stableOnly = await adapter.GetVersionsAsync(source, new UpdatePreferences());
        var withBeta = await adapter.GetVersionsAsync(source, new UpdatePreferences { IncludeBeta = true });

        var stable = Assert.Single(stableOnly);
        Assert.Equal("Paper build 42", stable.VersionName);
        Assert.Equal("1.21.8", stable.MinecraftVersion);
        Assert.Equal("42", stable.LoaderVersion);
        Assert.Equal(21, stable.RequiredJavaMajor);
        Assert.Equal("jar", stable.PackageType);
        Assert.Equal(2, withBeta.Count);
        Assert.Contains(withBeta, version => version.ReleaseChannel == ReleaseChannel.Beta && version.LoaderVersion == "41");
    }

    [Theory]
    [InlineData("26.1", 25)]
    [InlineData("26.0", 21)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.18.2", 17)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.7.10", 8)]
    public void Paper_java_policy_is_explicit_and_does_not_guess_preview_ids(string version, int expected) =>
        Assert.Equal(expected, PaperJavaRuntimePolicy.RequiredMajor(version));

    [Theory]
    [InlineData("1.7.9")]
    [InlineData("26w33a")]
    public void Paper_java_policy_refuses_versions_outside_its_explicit_boundaries(string version) =>
        Assert.Null(PaperJavaRuntimePolicy.RequiredMajor(version));

    [Fact]
    public void Paper_plan_requires_the_exact_stable_build_for_the_selected_version()
    {
        var version = new PaperVersionOption
        {
            VersionId = "1.21.8",
            VersionGroup = "1.21",
            ReleaseKind = MinecraftReleaseKind.Release,
            RequiredJavaMajor = 21
        };
        var build = Build("1.21.7", 42, PaperBuildChannel.Stable);
        var plan = new PaperCreationPlan
        {
            ServerName = "Mismatch",
            Version = version,
            Build = build,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true,
                AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            }
        };

        Assert.Contains(plan.Problems(), item => item.Contains("exact supported Paper build", StringComparison.Ordinal));
        var exactBuild = plan with { Build = build with { MinecraftVersion = "1.21.8" } };
        Assert.Contains(exactBuild.Problems(), item => item.Contains("not been runtime-certified", StringComparison.Ordinal));
        Assert.Empty((exactBuild with { ExperimentalRuntimeRiskAccepted = true }).Problems());
    }

    [Fact]
    public void Exact_Paper_certification_is_build_identity_bound_and_removes_the_experimental_gate()
    {
        var version = PaperRuntimeCertificationEvidence.Apply(new PaperVersionOption
        {
            VersionId = "26.2", VersionGroup = "26.2", ReleaseKind = MinecraftReleaseKind.Release,
            RequiredJavaMajor = 25
        });
        var metadataBuild = new PaperBuildOption
        {
            MinecraftVersion = "26.2", BuildId = 112, Channel = PaperBuildChannel.Stable,
            FileName = "paper-26.2-112.jar",
            DownloadUrl = "https://fill-data.papermc.io/v1/objects/certified/paper.jar",
            ServerSha256 = "bd3a58cf96874e5ea6643f5f6fe9b4f5bf9e34b795fa078c2f0ee8b98b2f907e",
            ServerSizeBytes = 61_859_678
        };
        var build = PaperRuntimeCertificationEvidence.Apply(metadataBuild);
        var changed = PaperRuntimeCertificationEvidence.Apply(metadataBuild with { ServerSha256 = new string('f', 64) });
        var plan = new PaperCreationPlan
        {
            ServerName = "Certified Paper", Version = version, Build = build,
            Eula = new VanillaEulaAcceptance
            {
                Accepted = true, AcceptedAtUtc = DateTimeOffset.UtcNow,
                SourceUrl = VanillaEulaAcceptance.OfficialSourceUrl
            }
        };

        Assert.Equal(MinecraftVersionSupportTier.Recommended, version.SupportTier);
        Assert.Equal(MinecraftVersionSupportTier.Recommended, build.SupportTier);
        Assert.Equal(MinecraftVersionCertificationLevel.RuntimeCertified, build.Certification.Level);
        Assert.Equal(MinecraftVersionSupportTier.Experimental, changed.SupportTier);
        Assert.Empty(plan.Problems());
    }

    [Fact]
    public async Task Paper_campaign_requires_explicit_disposable_EULA_authorization()
    {
        var runtime = new RecordingRuntimeCertifier();
        var campaign = new PaperCertificationCampaign(Service(Fixture()), runtime);
        var catalog = await Service(Fixture()).GetVersionsAsync(forceRefresh: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RunAsync(catalog,
            new PaperCertificationCampaignOptions
            {
                CacheRoot = Path.Combine(root, "cert-no-eula"),
                LedgerPath = Path.Combine(root, "cert-no-eula", "ledger.json")
            }));

        Assert.Contains("explicit disposable EULA authorization", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Paper_campaign_is_hash_bound_resumable_and_exports_only_complete_passes()
    {
        var service = Service(Fixture());
        var catalog = await service.GetVersionsAsync(forceRefresh: true);
        var build = Assert.Single((await service.GetBuildsAsync("1.21.8", forceRefresh: true)).Builds,
            item => item.BuildId == 42);
        var cacheRoot = Path.Combine(root, "cert-resume");
        var artifactRoot = Path.Combine(cacheRoot, "paper-artifacts");
        Directory.CreateDirectory(artifactRoot);
        // The fixture catalog advertises a large production-sized payload. Bind a compact deterministic
        // payload to its real hash/size for this campaign test rather than making a network request.
        var bytes = Encoding.UTF8.GetBytes("deterministic-paper-fixture");
        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        build = build with { ServerSha256 = sha256, ServerSizeBytes = bytes.LongLength };
        await File.WriteAllBytesAsync(Path.Combine(artifactRoot, sha256 + ".jar"), bytes);
        var adjusted = catalog with
        {
            Versions = catalog.Versions.Where(item => item.VersionId == "1.21.8").ToArray()
        };
        var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.EndsWith("/projects/paper/versions/1.21.8/builds", StringComparison.Ordinal))
                return Json("""
                    [{ "id": 42, "time": "2026-08-17T12:00:00Z", "channel": "STABLE",
                      "downloads": { "server:default": { "name": "paper-1.21.8-42.jar",
                        "checksums": { "sha256": "__HASH__" }, "size": __SIZE__,
                        "url": "https://fill-data.papermc.io/v1/objects/fixture/paper.jar" } } }]
                    """.Replace("__HASH__", sha256, StringComparison.Ordinal)
                          .Replace("__SIZE__", bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
                              StringComparison.Ordinal));
            return Fixture().Responder(request);
        });
        var runtime = new RecordingRuntimeCertifier();
        var campaignCatalog = new PaperVersionCatalogService(
            new AppDataPaths(Path.Combine(root, "campaign-appdata")), new HttpClient(handler));
        var campaign = new PaperCertificationCampaign(campaignCatalog, runtime);
        var options = new PaperCertificationCampaignOptions
        {
            CacheRoot = cacheRoot,
            LedgerPath = Path.Combine(cacheRoot, "ledger.json"),
            ExplicitEulaAuthorization = true
        };

        var first = await campaign.RunAsync(adjusted, options);
        var resumed = await campaign.RunAsync(adjusted, options);

        var entry = Assert.Single(first.Entries);
        Assert.True(entry.Result == VanillaCertificationResult.Passed, entry.Reason);
        Assert.Equal(42, entry.BuildId);
        Assert.Equal(sha256, entry.ArtifactSha256);
        Assert.True(entry.RuntimeLaunched && entry.ReadinessConfirmed && entry.CleanStopConfirmed &&
                    entry.ExpectedFilesConfirmed && entry.NoUnexpectedGuiConfirmed && entry.CleanupSucceeded);
        Assert.Single(resumed.Entries);
        Assert.Equal(1, runtime.Calls);
        var evidence = PaperRuntimeCertificationEvidence.Export(resumed);
        Assert.Contains("\"minecraftVersion\": \"1.21.8\"", evidence, StringComparison.Ordinal);
        Assert.Contains("\"buildId\": 42", evidence, StringComparison.Ordinal);
    }

    private PaperVersionCatalogService Service(HttpMessageHandler handler) =>
        new(new AppDataPaths(root), new HttpClient(handler));

    private static StubHandler Fixture() => new(request =>
    {
        Assert.Contains("ChunkPilot", request.Headers.UserAgent.ToString(), StringComparison.Ordinal);
        var url = request.RequestUri!.ToString();
        if (url.EndsWith("/projects/paper", StringComparison.Ordinal))
            return Json("""
                { "project": { "id": "paper", "name": "Paper" }, "versions": {
                  "1.21": ["1.21.9-rc1", "1.21.8"], "1.20": ["1.20.6"]
                } }
                """);
        return Json("""
            [
              { "id": 42, "time": "2026-08-17T12:00:00Z", "channel": "STABLE",
                "downloads": { "server:default": { "name": "paper-1.21.8-42.jar",
                  "checksums": { "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                  "size": 54846016, "url": "https://fill-data.papermc.io/v1/objects/aa/paper.jar" } } },
              { "id": 41, "time": "2026-08-16T12:00:00Z", "channel": "BETA",
                "downloads": { "server:default": { "name": "paper-1.21.8-41.jar",
                  "checksums": { "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" },
                  "size": 54800000, "url": "https://fill-data.papermc.io/v1/objects/bb/paper.jar" } } }
            ]
            """);
    });

    private static PaperBuildOption Build(string version, int id, PaperBuildChannel channel) => new()
    {
        MinecraftVersion = version,
        BuildId = id,
        Channel = channel,
        FileName = $"paper-{version}-{id}.jar",
        DownloadUrl = "https://fill-data.papermc.io/v1/objects/aa/paper.jar",
        ServerSha256 = new string('a', 64),
        ServerSizeBytes = 54_846_016
    };

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, new UTF8Encoding(false), "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int requests;
        public int RequestCount => requests;
        internal Func<HttpRequestMessage, HttpResponseMessage> Responder => responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(responder(request));
        }
    }

    private sealed class RecordingRuntimeCertifier : IVanillaRuntimeCertifier
    {
        public int Calls { get; private set; }

        public Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
            VanillaVersionOption version,
            VanillaCertificationCampaignOptions options,
            CancellationToken cancellationToken)
        {
            Calls++;
            Assert.True(options.ExplicitEulaAuthorization);
            Assert.Equal("plugins", options.ExpectedGeneratedDirectory);
            Assert.StartsWith("paper-", version.VersionId, StringComparison.Ordinal);
            return Task.FromResult(new VanillaRuntimeCertificationOutcome
            {
                Result = VanillaCertificationResult.Passed,
                Reason = "Deterministic exact-runtime fixture passed.",
                RuntimeLaunched = true,
                ReadinessConfirmed = true,
                StatusPingConfirmed = true,
                CleanStopConfirmed = true,
                ExpectedFilesConfirmed = true,
                NoUnexpectedGuiConfirmed = true,
                CleanupSucceeded = true,
                Evidence = ["fixture"]
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
