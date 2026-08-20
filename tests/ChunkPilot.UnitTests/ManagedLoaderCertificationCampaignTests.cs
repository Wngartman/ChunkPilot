using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ManagedLoaderCertificationCampaignTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "ChunkPilot-loader-campaign-" + Guid.NewGuid().ToString("N"));

    public ManagedLoaderCertificationCampaignTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task Campaign_requires_explicit_disposable_EULA_authorization_before_catalog_or_runtime_work()
    {
        var catalog = Catalog(Version("1.21.8"));
        var runtime = new RecordingRuntime();
        var campaign = new ManagedLoaderCertificationCampaign(catalog, runtime);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RunAsync(
            ManagedLoaderPlatform.Fabric, Options(explicitEula: false)));

        Assert.Contains("explicit disposable EULA authorization", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, catalog.VersionCalls);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Campaign_certifies_all_stable_versions_atomically_and_resume_skips_exact_passes()
    {
        var catalog = Catalog(Version("1.21.8"), Version("1.20.6"),
            Version("26.2-snapshot", stable: false));
        catalog.Builds["1.21.8"] = Build("1.21.8", "0.17.2", "1.1.0", 'a');
        catalog.Builds["1.20.6"] = Build("1.20.6", "0.16.14", "1.0.3", 'b');
        var runtime = new RecordingRuntime();
        var campaign = new ManagedLoaderCertificationCampaign(catalog, runtime);
        var options = Options(explicitEula: true);

        var first = await campaign.RunAsync(ManagedLoaderPlatform.Fabric, options);
        var resumed = await campaign.RunAsync(ManagedLoaderPlatform.Fabric, options);

        Assert.Equal(2, first.Entries.Count);
        Assert.All(first.Entries, entry => Assert.Equal(VanillaCertificationResult.Passed, entry.Result));
        Assert.Equal(2, runtime.Calls);
        Assert.Equal(first.CampaignId, resumed.CampaignId);
        Assert.False(File.Exists(options.LedgerPath + ".partial"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(options.LedgerPath));
        Assert.Equal(ManagedLoaderCertificationLedger.CurrentSchemaVersion,
            json.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Campaign_retries_failed_identity_only_when_requested()
    {
        var catalog = Catalog(Version("1.21.8"));
        catalog.Builds["1.21.8"] = Build("1.21.8", "0.17.2", "1.1.0", 'a');
        var runtime = new RecordingRuntime { Result = VanillaCertificationResult.FailedReadiness };
        var campaign = new ManagedLoaderCertificationCampaign(catalog, runtime);
        var options = Options(explicitEula: true);

        await campaign.RunAsync(ManagedLoaderPlatform.Fabric, options);
        await campaign.RunAsync(ManagedLoaderPlatform.Fabric, options);
        runtime.Result = VanillaCertificationResult.Passed;
        var retried = await campaign.RunAsync(ManagedLoaderPlatform.Fabric,
            options with { RetryFailed = true });

        Assert.Equal(2, runtime.Calls);
        Assert.Equal(VanillaCertificationResult.Passed, Assert.Single(retried.Entries).Result);
        Assert.Equal(2, retried.Entries[0].RetryCount);
    }

    [Fact]
    public async Task Catalog_only_historical_target_records_official_artifact_blocker_without_runtime_execution()
    {
        var version = new ManagedLoaderMinecraftVersion
        {
            Platform = ManagedLoaderPlatform.Ornithe,
            MinecraftVersion = "1.0",
            ProviderMinecraftVersion = "1.0.0",
            StableMinecraft = true,
            RequiredJavaMajor = 8,
            RequiresUserSuppliedMinecraftServerJar = true,
            UnavailableReason = "Mojang publishes no official dedicated-server artifact for Minecraft 1.0."
        };
        var catalog = new RecordingCatalog(new ManagedLoaderVersionCatalog
        {
            Platform = ManagedLoaderPlatform.Ornithe,
            ProviderAvailable = true,
            Versions = [version]
        });
        var runtime = new RecordingRuntime();

        var ledger = await new ManagedLoaderCertificationCampaign(catalog, runtime).RunAsync(
            ManagedLoaderPlatform.Ornithe, Options(explicitEula: true) with { ExactVersion = "1.0" });

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(VanillaCertificationResult.BlockedMissingOfficialArtifact, entry.Result);
        Assert.Contains("no official dedicated-server artifact", entry.Reason,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, catalog.BuildCalls);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Exact_historical_prerelease_can_record_a_terminal_blocker()
    {
        var version = new ManagedLoaderMinecraftVersion
        {
            Platform = ManagedLoaderPlatform.Ornithe,
            MinecraftVersion = "b1.8",
            ProviderMinecraftVersion = "b1.8",
            StableMinecraft = false,
            RequiredJavaMajor = 8,
            RequiresUserSuppliedMinecraftServerJar = true,
            UnavailableReason = "Mojang publishes no official dedicated-server artifact for Minecraft b1.8."
        };
        var catalog = new RecordingCatalog(new ManagedLoaderVersionCatalog
        {
            Platform = ManagedLoaderPlatform.Ornithe,
            ProviderAvailable = true,
            Versions = [version]
        });
        var runtime = new RecordingRuntime();

        var ledger = await new ManagedLoaderCertificationCampaign(catalog, runtime).RunAsync(
            ManagedLoaderPlatform.Ornithe, Options(explicitEula: true) with { ExactVersion = "b1.8" });

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(VanillaCertificationResult.BlockedMissingOfficialArtifact, entry.Result);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Cancellation_is_terminally_recorded_and_preserves_the_atomic_ledger()
    {
        var catalog = Catalog(Version("1.21.8"));
        catalog.Builds["1.21.8"] = Build("1.21.8", "0.17.2", "1.1.0", 'a');
        using var cancellation = new CancellationTokenSource();
        var campaign = new ManagedLoaderCertificationCampaign(catalog,
            new CancellingRuntime(cancellation));
        var options = Options(explicitEula: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => campaign.RunAsync(
            ManagedLoaderPlatform.Fabric, options, cancellationToken: cancellation.Token));

        var ledger = JsonSerializer.Deserialize<ManagedLoaderCertificationLedger>(
            await File.ReadAllTextAsync(options.LedgerPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(VanillaCertificationResult.Cancelled, Assert.Single(ledger!.Entries).Result);
        Assert.False(File.Exists(options.LedgerPath + ".partial"));
    }

    [Fact]
    public async Task Storage_preflight_stops_before_runtime_when_the_required_reserve_is_unavailable()
    {
        var catalog = Catalog(Version("1.21.8"));
        catalog.Builds["1.21.8"] = Build("1.21.8", "0.17.2", "1.1.0", 'a');
        var runtime = new RecordingRuntime();

        var error = await Assert.ThrowsAsync<IOException>(() =>
            new ManagedLoaderCertificationCampaign(catalog, runtime).RunAsync(
                ManagedLoaderPlatform.Fabric,
                Options(explicitEula: true) with { MinimumFreeSpaceBytes = long.MaxValue }));

        Assert.Contains("free", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public async Task Missing_stable_exact_build_is_not_misclassified_as_unresolved_Java()
    {
        var catalog = Catalog(Version("1.21.8"));
        var runtime = new RecordingRuntime();

        var ledger = await new ManagedLoaderCertificationCampaign(catalog, runtime).RunAsync(
            ManagedLoaderPlatform.Fabric, Options(explicitEula: true));

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal(VanillaCertificationResult.BlockedMissingOfficialArtifact, entry.Result);
        Assert.Equal(0, runtime.Calls);
    }

    [Fact]
    public void Evidence_exports_only_passes_and_keeps_one_recommendation_per_platform()
    {
        var ledger = new ManagedLoaderCertificationLedger
        {
            Platform = ManagedLoaderPlatform.Fabric,
            Entries =
            [
                Entry("1.21.8", "0.17.2", 'a', VanillaCertificationResult.Passed),
                Entry("1.20.6", "0.16.14", 'b', VanillaCertificationResult.Passed),
                Entry("1.19.4", "0.15.11", 'c', VanillaCertificationResult.FailedReadiness)
            ]
        };

        var additions = ManagedLoaderCertificationCampaign.PassedEvidence(ledger);
        var json = ManagedLoaderRuntimeCertificationEvidence.MergeAndExport(null, additions);

        Assert.Equal(2, additions.Count);
        Assert.Single(additions, entry => entry.Recommended);
        Assert.DoesNotContain("1.19.4", json, StringComparison.Ordinal);
    }

    private ManagedLoaderCertificationCampaignOptions Options(bool explicitEula) => new()
    {
        LedgerPath = Path.Combine(root, "ledger.json"),
        ExplicitEulaAuthorization = explicitEula,
        PerVersionTimeout = TimeSpan.FromSeconds(30)
    };

    private static RecordingCatalog Catalog(params ManagedLoaderMinecraftVersion[] versions) =>
        new(new ManagedLoaderVersionCatalog
        {
            Platform = ManagedLoaderPlatform.Fabric,
            ProviderAvailable = true,
            RetrievedUtc = DateTimeOffset.UtcNow,
            Versions = versions
        });

    private static ManagedLoaderMinecraftVersion Version(string id, bool stable = true) => new()
    {
        Platform = ManagedLoaderPlatform.Fabric,
        MinecraftVersion = id,
        ProviderMinecraftVersion = id,
        StableMinecraft = stable,
        RequiredJavaMajor = 21
    };

    private static ManagedLoaderBuild Build(
        string minecraft,
        string loader,
        string installer,
        char hash) => new()
    {
        Platform = ManagedLoaderPlatform.Fabric,
        MinecraftVersion = minecraft,
        LoaderVersion = loader,
        InstallerVersion = installer,
        Channel = ManagedLoaderChannel.Stable,
        ArtifactUrl = $"https://meta.fabricmc.net/{minecraft}/{loader}/server.jar",
        ArtifactSha256 = new string(hash, 64),
        RequiredJavaMajor = 21
    };

    private static ManagedLoaderCertificationEntry Entry(
        string minecraft,
        string loader,
        char hash,
        VanillaCertificationResult result) => new()
    {
        Platform = ManagedLoaderPlatform.Fabric,
        MinecraftVersion = minecraft,
        LoaderVersion = loader,
        InstallerVersion = "1.1.0",
        ArtifactSha256 = new string(hash, 64),
        JavaMajor = 21,
        CompletedAt = DateTimeOffset.UtcNow,
        Result = result,
        CleanupSucceeded = true
    };

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RecordingCatalog(ManagedLoaderVersionCatalog versions) : IManagedLoaderCertificationCatalog
    {
        public int VersionCalls { get; private set; }
        public int BuildCalls { get; private set; }
        public Dictionary<string, ManagedLoaderBuild> Builds { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ManagedLoaderVersionCatalog> GetVersionsAsync(
            ManagedLoaderPlatform platform,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            VersionCalls++;
            return Task.FromResult(versions);
        }

        public Task<ManagedLoaderBuildCatalog> GetBuildsAsync(
            ManagedLoaderPlatform platform,
            string minecraftVersion,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            BuildCalls++;
            var builds = Builds.TryGetValue(minecraftVersion, out var build)
                ? new[] { build }
                : [];
            return Task.FromResult(new ManagedLoaderBuildCatalog
            {
                Platform = platform,
                MinecraftVersion = minecraftVersion,
                ProviderAvailable = true,
                Builds = builds,
                UnavailableDetail = builds.Length == 0 ? "No official stable build." : ""
            });
        }
    }

    private sealed class RecordingRuntime : IManagedLoaderExactRuntimeCertifier
    {
        public int Calls { get; private set; }
        public VanillaCertificationResult Result { get; set; } = VanillaCertificationResult.Passed;

        public Task<ManagedLoaderRuntimeCertificationOutcome> CertifyAsync(
            ManagedLoaderBuild build,
            bool explicitEulaAuthorization,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ManagedLoaderRuntimeCertificationOutcome
            {
                Result = Result,
                Reason = Result == VanillaCertificationResult.Passed ? "Passed." : "Fixture failure.",
                ArtifactSha256 = build.ArtifactSha256,
                JavaPath = "fixture-java",
                RuntimeLaunched = true,
                ReadinessConfirmed = Result == VanillaCertificationResult.Passed,
                StatusPingConfirmed = Result == VanillaCertificationResult.Passed,
                CleanStopConfirmed = Result == VanillaCertificationResult.Passed,
                ExpectedFilesConfirmed = Result == VanillaCertificationResult.Passed,
                NoUnexpectedGuiConfirmed = true,
                CleanupSucceeded = true
            });
        }
    }

    private sealed class CancellingRuntime(CancellationTokenSource source) : IManagedLoaderExactRuntimeCertifier
    {
        public Task<ManagedLoaderRuntimeCertificationOutcome> CertifyAsync(
            ManagedLoaderBuild build,
            bool explicitEulaAuthorization,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            source.Cancel();
            return Task.FromCanceled<ManagedLoaderRuntimeCertificationOutcome>(cancellationToken);
        }
    }
}
