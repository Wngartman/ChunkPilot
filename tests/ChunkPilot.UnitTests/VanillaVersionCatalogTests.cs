using System.Net;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Official Minecraft version discovery: what is offered, what is refused, and how honest the
/// catalog is about where its data came from.
/// </summary>
/// <remarks>
/// Every request is served by a fake handler. Nothing here reaches Mojang, and no test asserts a
/// particular "latest" version - the fixtures use invented ids precisely so a real release cannot
/// make a passing test start failing.
/// </remarks>
public sealed class VanillaVersionCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-vanilla-" + Guid.NewGuid().ToString("N"));

    public VanillaVersionCatalogTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Exact_runtime_failure_is_unavailable_instead_of_selectable_metadata_only()
    {
        var result = VanillaVersionCatalogService.ApplyExactRuntimeFailure(
            (MinecraftVersionSupportTier.Experimental, VanillaVersionSupport.SupportedWithWarning,
                "Metadata is complete, but exact runtime evidence is absent."),
            "Exact certification result: FailedReadiness. The server did not reach readiness.");

        Assert.Equal(MinecraftVersionSupportTier.Unavailable, result.Tier);
        Assert.Equal(VanillaVersionSupport.UnsupportedByChunkPilot, result.Compatibility);
        Assert.Contains("FailedReadiness", result.Reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- catalog shape

    [Fact]
    public async Task Releases_are_offered_and_snapshots_stay_out_of_the_default_list()
    {
        var service = Service(Fixture());

        var stableOnly = await service.GetCatalogAsync(includeSnapshots: false);

        Assert.True(stableOnly.ProviderAvailable);
        Assert.NotEmpty(stableOnly.Stable);
        Assert.Empty(stableOnly.Snapshots);
        Assert.All(stableOnly.Stable, option =>
            Assert.Equal(VanillaReleaseChannel.Stable, option.Channel));
    }

    [Fact]
    public async Task Snapshots_appear_only_when_asked_for_and_carry_their_own_warning()
    {
        var service = Service(Fixture());

        var withSnapshots = await service.GetCatalogAsync(includeSnapshots: true, forceRefresh: true);

        var snapshot = Assert.Single(withSnapshots.Snapshots);
        Assert.Equal(VanillaVersionSupport.SupportedWithWarning, snapshot.Support);
        Assert.Contains(snapshot.Warnings, warning =>
            warning.Contains("in-development", StringComparison.OrdinalIgnoreCase));
        Assert.True(snapshot.IsSelectable);
    }

    [Fact]
    public async Task A_version_with_no_server_download_is_offered_to_nobody()
    {
        var service = Service(Fixture());

        var catalog = await service.GetCatalogAsync();
        var clientOnly = Assert.Single(catalog.Options, option => option.VersionId == "0.9.0-client-only");

        Assert.False(clientOnly.HasServerDownload);
        Assert.Equal(VanillaVersionSupport.NoServerArtifact, clientOnly.Support);
        Assert.False(clientOnly.IsSelectable);
        Assert.Contains("no server download", VanillaSupportPolicy.Describe(clientOnly.Support),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_java_requirement_comes_from_official_metadata_when_mojang_states_it()
    {
        var service = Service(Fixture());

        var catalog = await service.GetCatalogAsync();
        var stated = Assert.Single(catalog.Options, option => option.VersionId == "9.9.9");

        // Mojang says 25. ChunkPilot's own version rules would have inferred 21 for this id, so a
        // passing assertion here is the difference between believing metadata and guessing.
        Assert.Equal(25, stated.RequiredJavaMajor);
        Assert.Equal(JavaRequirementSource.OfficialMetadata, stated.JavaRequirementSource);
        Assert.NotEqual(JavaRuntimePolicy.RequiredMajorForMinecraft("9.9.9"), stated.RequiredJavaMajor);
        Assert.DoesNotContain(stated.Warnings, warning =>
            warning.Contains("worked it out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task When_metadata_omits_the_java_block_the_fallback_is_used_and_labelled()
    {
        var service = Service(Fixture());

        var catalog = await service.GetCatalogAsync();
        var inferred = Assert.Single(catalog.Options, option => option.VersionId == "1.19.4");

        Assert.Equal(JavaRuntimePolicy.RequiredMajorForMinecraft("1.19.4"), inferred.RequiredJavaMajor);
        Assert.Equal(JavaRequirementSource.ChunkPilotPolicy, inferred.JavaRequirementSource);
        Assert.Contains(inferred.Warnings, warning =>
            warning.Contains("worked it out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pre_2014_week_snapshot_uses_the_documented_Java_8_floor_and_still_requires_runtime_evidence()
    {
        var service = Service(new StubHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("version_manifest_v2", StringComparison.Ordinal)
                ? Json("""
                    {"latest":{"release":"1.7.2","snapshot":"13w24a"},"versions":[
                      {"id":"13w24a","type":"snapshot","url":"https://fixture.invalid/v/13w24a.json",
                       "sha1":"1111111111111111111111111111111111111111","releaseTime":"2013-06-13T12:00:00Z"}]}
                    """)
                : Json("""
                    {"id":"13w24a","type":"snapshot","downloads":{"server":{
                      "url":"https://fixture.invalid/13w24a-server.jar",
                      "sha1":"2222222222222222222222222222222222222222","size":12345678}}}
                    """)));

        var option = Assert.Single((await service.GetCatalogAsync(includeSnapshots: true)).Options);

        Assert.Equal(8, option.RequiredJavaMajor);
        Assert.Equal(JavaRequirementSource.ChunkPilotPolicy, option.JavaRequirementSource);
        Assert.Equal(MinecraftVersionSupportTier.Experimental, option.SupportTier);
        Assert.Contains(option.Warnings, warning => warning.Contains("worked it out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Cached_server_metadata_with_unresolved_derived_profiles_is_rehydrated()
    {
        var stale = new VanillaVersionOption
        {
            VersionId = "13w24a",
            ReleaseKind = MinecraftReleaseKind.Snapshot,
            ReleaseTime = new DateTimeOffset(2013, 6, 13, 12, 0, 0, TimeSpan.Zero),
            HasServerDownload = true,
            RequiredJavaMajor = null,
            LaunchProfile = new MinecraftLaunchProfile
            {
                Kind = MinecraftLaunchProfileKind.Unknown,
                Evidence = "An older cache did not resolve this profile."
            }
        };
        var complete = stale with
        {
            RequiredJavaMajor = 8,
            LaunchProfile = MinecraftLaunchProfileResolver.Resolve(
                "13w24a", MinecraftReleaseKind.Snapshot,
                new DateTimeOffset(2013, 6, 13, 12, 0, 0, TimeSpan.Zero))
        };

        Assert.False(VanillaVersionCatalogService.CachedDerivedProfilesAreComplete(stale));
        Assert.True(VanillaVersionCatalogService.CachedDerivedProfilesAreComplete(complete));
    }

    [Fact]
    public async Task A_version_whose_metadata_cannot_be_read_becomes_unsupported_rather_than_fatal()
    {
        var service = Service(Fixture(brokenVersionMetadata: true));

        var catalog = await service.GetCatalogAsync();

        // One bad entry must not lose the rest of the catalog.
        Assert.True(catalog.ProviderAvailable);
        Assert.NotEmpty(catalog.Options);
        var broken = Assert.Single(catalog.Options, option => option.VersionId == "8.8.8-broken");
        Assert.Equal(VanillaVersionSupport.UnsupportedByChunkPilot, broken.Support);
        Assert.False(broken.IsSelectable);
        Assert.NotEmpty(broken.Warnings);
    }

    [Fact]
    public async Task Every_offered_entry_carries_its_provenance_and_integrity_evidence()
    {
        var service = Service(Fixture());

        var catalog = await service.GetCatalogAsync(includeSnapshots: true);

        Assert.All(catalog.Options.Where(option => option.IsSelectable), option =>
        {
            Assert.Equal("Official Mojang version metadata", option.Provenance);
            Assert.StartsWith("https://", option.ServerDownloadUrl, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(option.ServerSha1));
            Assert.NotNull(option.ServerSizeBytes);
            Assert.NotNull(option.RequiredJavaMajor);
            Assert.Contains(option.VersionId, option.AutomationName, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_complete_inventory_preserves_release_development_and_historical_entries()
    {
        var catalog = await Service(Fixture()).GetCatalogAsync(includeSnapshots: true);

        Assert.Contains(catalog.Options, option => option.ReleaseKind == MinecraftReleaseKind.Release);
        Assert.Contains(catalog.Options, option => option.ReleaseKind == MinecraftReleaseKind.PreRelease);
        Assert.Contains(catalog.Options, option => option.ReleaseKind == MinecraftReleaseKind.Beta);
        Assert.Contains(catalog.Options, option => option.ReleaseKind == MinecraftReleaseKind.Alpha);
        Assert.Equal("9.9.9", catalog.ManifestLatestReleaseId);
        Assert.Equal("9.9.9-pre1", catalog.ManifestLatestSnapshotId);
    }

    [Fact]
    public async Task Support_tiers_separate_inventory_existence_from_creation_support()
    {
        var catalog = await Service(Fixture()).GetCatalogAsync(includeSnapshots: true);

        Assert.Equal(MinecraftVersionSupportTier.Experimental,
            Assert.Single(catalog.Options, option => option.VersionId == "9.9.9").SupportTier);
        Assert.Equal(MinecraftVersionSupportTier.Experimental,
            Assert.Single(catalog.Options, option => option.VersionId == "9.9.9-pre1").SupportTier);
        Assert.Equal(MinecraftVersionSupportTier.Unavailable,
            Assert.Single(catalog.Options, option => option.VersionId == "b1.7.3").SupportTier);
        Assert.Equal(MinecraftVersionSupportTier.Unavailable,
            Assert.Single(catalog.Options, option => option.VersionId == "a1.0.0").SupportTier);
        Assert.Equal(MinecraftVersionCertificationLevel.MetadataValidated,
            Assert.Single(catalog.Options, option => option.VersionId == "9.9.9").Certification.Level);
        Assert.False(Assert.Single(catalog.Options, option => option.VersionId == "9.9.9").Certification.RuntimeLaunched);
        Assert.Empty(catalog.LatestVerifiedReleaseId);
    }

    [Theory]
    [InlineData("1.21.9-pre2", "snapshot", MinecraftReleaseKind.PreRelease)]
    [InlineData("1.14 Pre-Release 5", "snapshot", MinecraftReleaseKind.PreRelease)]
    [InlineData("1.21.9-rc1", "snapshot", MinecraftReleaseKind.ReleaseCandidate)]
    [InlineData("1.16 Release Candidate 1", "snapshot", MinecraftReleaseKind.ReleaseCandidate)]
    [InlineData("26w33a", "snapshot", MinecraftReleaseKind.Snapshot)]
    [InlineData("1.18 experimental snapshot 7", "snapshot", MinecraftReleaseKind.ExperimentalSnapshot)]
    [InlineData("b1.7.3", "old_beta", MinecraftReleaseKind.Beta)]
    [InlineData("a1.2.6", "old_alpha", MinecraftReleaseKind.Alpha)]
    public void Release_kind_classification_is_central_and_explicit(
        string id, string type, MinecraftReleaseKind expected) =>
        Assert.Equal(expected, MinecraftVersionClassification.ReleaseKindFor(id, type));

    [Fact]
    public void Metadata_certification_does_not_claim_runtime_proof()
    {
        var launch = MinecraftLaunchProfileResolver.Resolve(
            "1.21.8", MinecraftReleaseKind.Release,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var certification = MinecraftVersionCertificationPolicy.FromMetadata(true, true, true, 21, launch);

        Assert.Equal(MinecraftVersionCertificationLevel.MetadataValidated, certification.Level);
        Assert.False(certification.RuntimeLaunched);
        Assert.False(certification.ReadinessConfirmed);
        Assert.False(certification.CleanShutdownConfirmed);
        Assert.Contains(certification.Limitations, item => item.Contains("not been launched", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Exact_runtime_evidence_promotes_a_stable_historical_release_even_with_curated_Java_and_launch_rules()
    {
        var exact = new MinecraftVersionCertification
        {
            Level = MinecraftVersionCertificationLevel.RuntimeCertified,
            RuntimeLaunched = true,
            ReadinessConfirmed = true,
            CleanShutdownConfirmed = true,
            ExpectedFilesConfirmed = true,
            NoUnexpectedGuiConfirmed = true
        };
        var launch = new MinecraftLaunchProfile
        {
            Kind = MinecraftLaunchProfileKind.LegacyNogui,
            Arguments = "nogui",
            ReadinessPattern = "Done",
            Evidence = "Curated historical launch rule."
        };

        var assessment = VanillaSupportPolicy.Assess(
            MinecraftReleaseKind.Release, true, true, 8, JavaRequirementSource.ChunkPilotPolicy,
            launch, exact, isManifestLatestRelease: false);

        Assert.Equal(MinecraftVersionSupportTier.Verified, assessment.Tier);
        Assert.Equal(VanillaVersionSupport.Supported, assessment.Compatibility);
        Assert.Contains("exact", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exact_certification_evidence_is_identity_bound_and_offline_safe()
    {
        var option = new VanillaVersionOption
        {
            VersionId = "26.2",
            MetadataSha1 = "c75d82e7fa6eca5a043dab0c6cf77cb8317644f4",
            HasServerDownload = true,
            ServerSha1 = "823e2250d24b3ddac457a60c92a6a941943fcd6a",
            ServerSizeBytes = 60_894_273,
            RequiredJavaMajor = 25,
            JavaRequirementSource = JavaRequirementSource.OfficialMetadata,
            LaunchProfile = MinecraftLaunchProfileResolver.Resolve(
                "26.2", MinecraftReleaseKind.Release,
                new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero))
        };
        var metadata = MinecraftVersionCertificationPolicy.FromMetadata(
            true, true, true, 25, option.LaunchProfile);

        var certified = VanillaRuntimeCertificationEvidence.Apply(option, metadata);
        var changedArtifact = VanillaRuntimeCertificationEvidence.Apply(
            option with { ServerSha1 = "changed" }, metadata);

        Assert.Equal(MinecraftVersionCertificationLevel.RuntimeCertified, certified.Level);
        Assert.True(certified.RuntimeLaunched);
        Assert.True(certified.ReadinessConfirmed);
        Assert.True(certified.CleanShutdownConfirmed);
        Assert.NotNull(certified.RuntimeValidatedAt);
        Assert.Equal(MinecraftVersionCertificationLevel.MetadataValidated, changedArtifact.Level);
        Assert.False(changedArtifact.RuntimeLaunched);
    }

    [Fact]
    public async Task The_exact_passed_latest_release_is_recommended_through_the_catalog()
    {
        var catalog = await Service(CertifiedFixture()).GetCatalogAsync();
        var latest = Assert.Single(catalog.Options);

        Assert.Equal("26.2", latest.VersionId);
        Assert.Equal(MinecraftVersionSupportTier.Recommended, latest.SupportTier);
        Assert.Equal(MinecraftVersionCertificationLevel.RuntimeCertified, latest.Certification.Level);
        Assert.Equal("26.2", catalog.LatestVerifiedReleaseId);
    }

    // ---------------------------------------------------------------- failure and cache

    [Fact]
    public async Task A_malformed_manifest_with_no_cache_reports_the_provider_unavailable()
    {
        var service = Service(new StubHandler(_ => Json("{\"nonsense\":true}")));

        var catalog = await service.GetCatalogAsync();

        Assert.False(catalog.ProviderAvailable);
        Assert.Empty(catalog.Options);
        Assert.Contains("could not reach Mojang", catalog.UnavailableDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_provider_error_with_no_cache_reports_unavailable_rather_than_an_empty_list()
    {
        var service = Service(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var catalog = await service.GetCatalogAsync();

        Assert.False(catalog.ProviderAvailable);
        Assert.Empty(catalog.Options);
        Assert.NotEmpty(catalog.UnavailableDetail);
    }

    [Fact]
    public async Task A_good_catalog_is_reused_from_cache_without_touching_the_provider_again()
    {
        var handler = Fixture();
        var service = Service(handler);
        var first = await service.GetCatalogAsync();
        var requestsAfterFirst = handler.RequestCount;

        var second = await service.GetCatalogAsync();

        Assert.True(first.ProviderAvailable);
        Assert.False(first.IsFromCache);
        Assert.True(second.IsFromCache);
        Assert.False(second.IsStale);
        Assert.Equal(requestsAfterFirst, handler.RequestCount);
        Assert.Equal(first.Options.Count, second.Options.Count);
    }

    [Fact]
    public async Task A_forced_refresh_reuses_unchanged_metadata_documents_by_manifest_sha1()
    {
        var handler = Fixture();
        var service = Service(handler);
        _ = await service.GetCatalogAsync(includeSnapshots: true);
        var requestsAfterFirst = handler.RequestCount;

        _ = await service.GetCatalogAsync(includeSnapshots: true, forceRefresh: true);

        Assert.Equal(requestsAfterFirst + 1, handler.RequestCount);
    }

    [Fact]
    public async Task Concurrent_refresh_requests_share_one_provider_operation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fallback = Fixture();
        var handler = new AsyncStubHandler(async request =>
        {
            if (request.RequestUri!.ToString().Contains("version_manifest_v2", StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await release.Task;
            }
            return await fallback.SendForTestAsync(request);
        });
        var service = Service(handler);

        var first = service.GetCatalogAsync(includeSnapshots: true, forceRefresh: true);
        await entered.Task;
        var second = service.GetCatalogAsync(includeSnapshots: true, forceRefresh: true);
        Assert.Equal(1, handler.RequestCount);
        release.TrySetResult();

        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].Options.Count, results[1].Options.Count);
        Assert.Equal(fallback.RequestCount, handler.RequestCount);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_good_cache_and_says_it_is_stale()
    {
        var paths = new AppDataPaths(Path.Combine(root, "stale"));
        var good = new VanillaVersionCatalogService(paths, new HttpClient(Fixture()), TimeSpan.Zero);
        var populated = await good.GetCatalogAsync();
        Assert.True(populated.ProviderAvailable);

        // A zero lifetime forces a refresh attempt every time, and the provider is now failing.
        var failing = new VanillaVersionCatalogService(paths,
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("network down"))), TimeSpan.Zero);
        var degraded = await failing.GetCatalogAsync();

        Assert.True(degraded.IsFromCache);
        Assert.True(degraded.IsStale);
        Assert.Equal(populated.Options.Count, degraded.Options.Count);
        Assert.Contains("last saw", degraded.UnavailableDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_failed_refresh_never_overwrites_the_cache_with_nothing()
    {
        var paths = new AppDataPaths(Path.Combine(root, "no-overwrite"));
        var good = new VanillaVersionCatalogService(paths, new HttpClient(Fixture()), TimeSpan.Zero);
        var populated = await good.GetCatalogAsync();

        var failing = new VanillaVersionCatalogService(paths,
            new HttpClient(new StubHandler(_ => Json("{\"broken\":1}"))), TimeSpan.Zero);
        _ = await failing.GetCatalogAsync();

        // Reading again through a healthy-but-offline service must still find the original entries.
        var reread = new VanillaVersionCatalogService(paths,
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))), TimeSpan.Zero);
        var recovered = await reread.GetCatalogAsync();

        Assert.Equal(populated.Options.Count, recovered.Options.Count);
        Assert.True(recovered.IsFromCache);
    }

    [Fact]
    public async Task A_corrupt_cache_file_is_ignored_rather_than_presented_as_the_catalog()
    {
        var paths = new AppDataPaths(Path.Combine(root, "corrupt"));
        Directory.CreateDirectory(paths.CatalogCache);
        await File.WriteAllTextAsync(
            Path.Combine(paths.CatalogCache, "vanilla-version-catalog.json"), "{ not json",
            new UTF8Encoding(false));

        var offline = new VanillaVersionCatalogService(paths,
            new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline"))));
        var catalog = await offline.GetCatalogAsync();

        Assert.False(catalog.ProviderAvailable);
        Assert.Empty(catalog.Options);
    }

    [Fact]
    public async Task Refreshing_can_be_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var service = Service(new StubHandler(_ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Json("{}");
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetCatalogAsync(cancellationToken: cancellation.Token));
    }

    // ---------------------------------------------------------------- support policy

    [Theory]
    [InlineData(VanillaReleaseChannel.Stable, true, 21, VanillaVersionSupport.Supported)]
    [InlineData(VanillaReleaseChannel.Snapshot, true, 21, VanillaVersionSupport.SupportedWithWarning)]
    [InlineData(VanillaReleaseChannel.Stable, false, 21, VanillaVersionSupport.NoServerArtifact)]
    [InlineData(VanillaReleaseChannel.Stable, true, null, VanillaVersionSupport.JavaRequirementUnknown)]
    [InlineData(VanillaReleaseChannel.Historic, true, 8, VanillaVersionSupport.UnsupportedByChunkPilot)]
    public void The_support_conclusion_follows_the_evidence_and_not_the_version_age(
        VanillaReleaseChannel channel, bool hasServer, int? java, VanillaVersionSupport expected) =>
        Assert.Equal(expected, VanillaSupportPolicy.Conclude(channel, hasServer, java));

    [Fact]
    public void Every_support_conclusion_and_channel_carries_text()
    {
        foreach (var support in Enum.GetValues<VanillaVersionSupport>())
        {
            Assert.False(string.IsNullOrWhiteSpace(VanillaSupportPolicy.Describe(support)));
            Assert.False(string.IsNullOrWhiteSpace(VanillaSupportPolicy.BadgeLabel(support)));
        }
        Assert.Equal(VanillaReleaseChannel.Stable, VanillaSupportPolicy.ChannelFor("release"));
        Assert.Equal(VanillaReleaseChannel.Snapshot, VanillaSupportPolicy.ChannelFor("snapshot"));
        Assert.Equal(VanillaReleaseChannel.Historic, VanillaSupportPolicy.ChannelFor("old_alpha"));
        Assert.Equal(VanillaReleaseChannel.Historic, VanillaSupportPolicy.ChannelFor("old_beta"));
    }

    [Fact]
    public void No_production_code_pins_a_particular_latest_minecraft_version()
    {
        var sources = Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            // The synthetic preview catalogue is invented sample data by design and never reaches a
            // real creation; it is the one place version-shaped strings are deliberately hard-coded.
            .Where(path => !path.EndsWith("SyntheticPreviewCatalog.cs", StringComparison.Ordinal));

        var offenders = sources
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(path), @"""latest(Release|Version)""\s*:|LatestMinecraftVersion"))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(offenders.Length == 0, "Pinned latest version: " + string.Join(", ", offenders));
    }

    // ---------------------------------------------------------------- fixtures

    private VanillaVersionCatalogService Service(HttpMessageHandler handler) =>
        new(new AppDataPaths(Path.Combine(root, Guid.NewGuid().ToString("N"))), new HttpClient(handler));

    /// <summary>
    /// A manifest and version documents shaped exactly like Mojang's, with invented ids.
    /// </summary>
    private static StubHandler Fixture(bool brokenVersionMetadata = false) => new(request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("version_manifest_v2", StringComparison.Ordinal))
            return Json("""
                {
                  "latest": { "release": "9.9.9", "snapshot": "9.9.9-pre1" },
                  "versions": [
                    { "id": "9.9.9-pre1", "type": "snapshot", "url": "https://fixture.invalid/v/9.9.9-pre1.json", "sha1": "meta-pre1",
                      "releaseTime": "2026-05-02T10:00:00+00:00" },
                    { "id": "9.9.9", "type": "release", "url": "https://fixture.invalid/v/9.9.9.json", "sha1": "meta-999",
                      "releaseTime": "2026-05-01T10:00:00+00:00" },
                    { "id": "8.8.8-broken", "type": "release", "url": "https://fixture.invalid/v/broken.json", "sha1": "meta-broken",
                      "releaseTime": "2026-04-01T10:00:00+00:00" },
                    { "id": "1.19.4", "type": "release", "url": "https://fixture.invalid/v/1.19.4.json", "sha1": "meta-1194",
                      "releaseTime": "2023-03-14T10:00:00+00:00" },
                    { "id": "0.9.0-client-only", "type": "release", "url": "https://fixture.invalid/v/client-only.json", "sha1": "meta-client",
                      "releaseTime": "2011-01-01T10:00:00+00:00" },
                    { "id": "b1.7.3", "type": "old_beta", "url": "https://fixture.invalid/v/b1.7.3.json", "sha1": "meta-beta",
                      "releaseTime": "2011-07-08T10:00:00+00:00" },
                    { "id": "a1.0.0", "type": "old_alpha", "url": "https://fixture.invalid/v/a1.0.0.json", "sha1": "meta-alpha",
                      "releaseTime": "2010-01-01T10:00:00+00:00" }
                  ]
                }
                """);

        if (url.EndsWith("broken.json", StringComparison.Ordinal))
            return brokenVersionMetadata
                ? Json("{ this is not json")
                : new HttpResponseMessage(HttpStatusCode.NotFound);

        if (url.EndsWith("client-only.json", StringComparison.Ordinal))
            return Json("""
                { "id": "0.9.0-client-only", "type": "release",
                  "javaVersion": { "component": "jre-legacy", "majorVersion": 8 },
                  "downloads": { "client": { "url": "https://fixture.invalid/client.jar", "sha1": "aa", "size": 1 } } }
                """);

        if (url.EndsWith("1.19.4.json", StringComparison.Ordinal))
            return Json("""
                { "id": "1.19.4", "type": "release",
                  "downloads": { "server": { "url": "https://fixture.invalid/server-1194.jar",
                    "sha1": "1194aaaabbbbccccddddeeeeffff000011112222", "size": 47000000 } } }
                """);

        if (url.EndsWith("9.9.9-pre1.json", StringComparison.Ordinal))
            return Json("""
                { "id": "9.9.9-pre1", "type": "snapshot",
                  "javaVersion": { "component": "java-runtime-epsilon", "majorVersion": 25 },
                  "downloads": { "server": { "url": "https://fixture.invalid/server-pre1.jar",
                    "sha1": "pre1aaaabbbbccccddddeeeeffff000011112222", "size": 60000001 } } }
                """);

        return Json("""
            { "id": "9.9.9", "type": "release",
              "javaVersion": { "component": "java-runtime-epsilon", "majorVersion": 25 },
              "downloads": { "server": { "url": "https://fixture.invalid/server-999.jar",
                "sha1": "999aaaabbbbccccddddeeeeffff0000111122223", "size": 60894273 } } }
            """);
    });

    private static StubHandler CertifiedFixture() => new(request =>
    {
        var url = request.RequestUri!.ToString();
        if (url.Contains("version_manifest_v2", StringComparison.Ordinal))
            return Json("""
                {
                  "latest": { "release": "26.2", "snapshot": "26.2" },
                  "versions": [
                    { "id": "26.2", "type": "release", "url": "https://fixture.invalid/v/26.2.json",
                      "sha1": "c75d82e7fa6eca5a043dab0c6cf77cb8317644f4",
                      "releaseTime": "2026-08-17T00:00:00+00:00" }
                  ]
                }
                """);
        return Json("""
            {
              "id": "26.2",
              "type": "release",
              "javaVersion": { "component": "java-runtime-epsilon", "majorVersion": 25 },
              "downloads": { "server": {
                "url": "https://piston-data.mojang.com/v1/objects/823e2250d24b3ddac457a60c92a6a941943fcd6a/server.jar",
                "sha1": "823e2250d24b3ddac457a60c92a6a941943fcd6a",
                "size": 60894273
              } }
            }
            """);
    });

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(payload, new UTF8Encoding(false), "application/json")
    };

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int requests;

        public int RequestCount => requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(responder(request));
        }

        public Task<HttpResponseMessage> SendForTestAsync(HttpRequestMessage request) => SendAsync(request, CancellationToken.None);
    }


    private sealed class AsyncStubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        private int requests;
        public int RequestCount => requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            return responder(request);
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
