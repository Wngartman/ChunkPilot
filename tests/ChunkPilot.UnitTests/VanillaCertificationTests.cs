using System.Text.Json;
using System.Security.Cryptography;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class VanillaCertificationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "chunkpilot-certification-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("artifact", VanillaCertificationResult.BlockedMissingOfficialArtifact)]
    [InlineData("integrity", VanillaCertificationResult.BlockedIncompleteIntegrityMetadata)]
    [InlineData("java", VanillaCertificationResult.BlockedUnresolvedJava)]
    [InlineData("launch", VanillaCertificationResult.BlockedUnresolvedLaunchProfile)]
    [InlineData("eula", VanillaCertificationResult.BlockedEulaAuthorization)]
    public void Preflight_reports_the_first_exact_blocker(string state, VanillaCertificationResult expected)
    {
        var version = Version() with
        {
            HasServerDownload = state != "artifact",
            ServerSha1 = state == "integrity" ? "" : new string('a', 40),
            ServerSizeBytes = state == "integrity" ? null : 1024,
            RequiredJavaMajor = state == "java" ? null : 21,
            LaunchProfile = state == "launch" ? new MinecraftLaunchProfile() : Profile()
        };

        var result = VanillaCertificationCampaign.Preflight(version, explicitEulaAuthorization: state != "eula");

        Assert.NotNull(result);
        Assert.Equal(expected, result.Result);
        Assert.False(result.RuntimeLaunched);
        Assert.True(result.CleanupSucceeded);
    }

    [Fact]
    public async Task Missing_eula_authorization_records_every_entry_without_invoking_runtime()
    {
        var runtime = new RecordingRuntime();
        var campaign = new VanillaCertificationCampaign(runtime);
        var options = Options(explicitEula: false);

        var ledger = await campaign.RunAsync(Catalog(Version("1.21.8"), Version("1.20.6")), options);

        Assert.Equal(2, ledger.Entries.Count);
        Assert.All(ledger.Entries, entry => Assert.Equal(VanillaCertificationResult.BlockedEulaAuthorization, entry.Result));
        Assert.Equal(0, runtime.Calls);
        Assert.True(File.Exists(options.LedgerPath));
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(options.LedgerPath));
        Assert.Equal(VanillaCertificationLedger.CurrentSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(Directory.Exists(Path.Combine(root, "artifacts")));
        Assert.False(Directory.Exists(Path.Combine(root, "work")));
    }

    [Fact]
    public async Task Resumable_campaign_retains_matching_terminal_evidence()
    {
        var runtime = new RecordingRuntime();
        var campaign = new VanillaCertificationCampaign(runtime);
        var options = Options(explicitEula: true);
        var catalog = Catalog(Version("1.21.8"));

        var first = await campaign.RunAsync(catalog, options);
        var second = await campaign.RunAsync(catalog, options);

        Assert.Single(first.Entries);
        Assert.Single(second.Entries);
        Assert.Equal(VanillaCertificationResult.Passed, second.Entries[0].Result);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal(first.CampaignId, second.CampaignId);
    }

    [Fact]
    public async Task Resume_retries_only_the_stale_historical_JRE_resolution_failure_without_a_broad_retry_flag()
    {
        var runtime = new HistoricalJavaFallbackRuntime();
        var campaign = new VanillaCertificationCampaign(runtime);
        var options = Options(explicitEula: true);
        var catalog = Catalog(Version("1.17") with { RequiredJavaMajor = 16 });

        var first = await campaign.RunAsync(catalog, options);
        var second = await campaign.RunAsync(catalog, options);

        Assert.Equal(VanillaCertificationResult.FailedRuntimeStartup, Assert.Single(first.Entries).Result);
        Assert.Equal(VanillaCertificationResult.Passed, Assert.Single(second.Entries).Result);
        Assert.Equal(2, runtime.Calls);
    }

    [Fact]
    public async Task Resume_retries_the_recorded_legacy_Mojang_socket_denial_without_reopening_other_failures()
    {
        var runtime = new LegacyMojangSocketRuntime();
        var campaign = new VanillaCertificationCampaign(runtime);
        var options = Options(explicitEula: true);
        var catalog = Catalog(Version("1.4"));

        var first = await campaign.RunAsync(catalog, options);
        var second = await campaign.RunAsync(catalog, options);

        Assert.Equal(VanillaCertificationResult.BlockedEnvironment, Assert.Single(first.Entries).Result);
        Assert.Equal(VanillaCertificationResult.Passed, Assert.Single(second.Entries).Result);
        Assert.Equal(2, runtime.Calls);
    }

    [Fact]
    public async Task Force_rechecks_matching_terminal_evidence_and_keeps_ledger_parseable()
    {
        var runtime = new RecordingRuntime();
        var campaign = new VanillaCertificationCampaign(runtime);
        var options = Options(explicitEula: true) with { MaximumConcurrency = 4 };
        var catalog = Catalog(Enumerable.Range(0, 12).Select(index => Version($"1.21.{index}")).ToArray());

        await campaign.RunAsync(catalog, options);
        var forced = await campaign.RunAsync(catalog, options with { Force = true });

        Assert.Equal(24, runtime.Calls);
        Assert.Equal(12, forced.Entries.Count);
        Assert.All(forced.Entries, entry => Assert.Equal(1, entry.RetryCount));
        Assert.NotNull(JsonSerializer.Deserialize<VanillaCertificationLedger>(await File.ReadAllTextAsync(options.LedgerPath)));
        Assert.False(File.Exists(options.LedgerPath + ".partial"));
    }

    [Fact]
    public async Task Runtime_exception_is_recorded_without_aborting_the_campaign()
    {
        var campaign = new VanillaCertificationCampaign(new ThrowingRuntime());
        var options = Options(explicitEula: true);

        var ledger = await campaign.RunAsync(Catalog(Version("1.21.8"), Version("1.20.6")), options);

        Assert.Equal(2, ledger.Entries.Count);
        Assert.All(ledger.Entries, entry =>
        {
            Assert.Equal(VanillaCertificationResult.FailedRuntimeStartup, entry.Result);
            Assert.Equal("fixture provider unavailable", entry.Reason);
            Assert.False(entry.RuntimeLaunched);
            Assert.False(entry.CleanupSucceeded);
        });
        Assert.NotNull(JsonSerializer.Deserialize<VanillaCertificationLedger>(
            await File.ReadAllTextAsync(options.LedgerPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task Cancellation_is_terminal_in_the_resumable_ledger()
    {
        var runtime = new RecordingRuntime(delay: TimeSpan.FromSeconds(5));
        var campaign = new VanillaCertificationCampaign(runtime);
        var options = Options(explicitEula: true);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            campaign.RunAsync(Catalog(Version("1.21.8"), Version("1.20.6")), options, cancellationToken: cancellation.Token));

        var saved = JsonSerializer.Deserialize<VanillaCertificationLedger>(
            await File.ReadAllTextAsync(options.LedgerPath),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(saved);
        Assert.Equal(2, saved.Entries.Count);
        Assert.All(saved.Entries, entry => Assert.Equal(VanillaCertificationResult.Cancelled, entry.Result));
    }

    [Fact]
    public async Task Fresh_artifact_download_is_closed_before_integrity_verification()
    {
        var payload = "official-server-fixture"u8.ToArray();
#pragma warning disable CA5350 // Mojang publishes SHA-1 for server artifact integrity; this mirrors that contract.
        var hash = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
#pragma warning restore CA5350
        var version = Version() with
        {
            ServerDownloadUrl = "https://piston-data.mojang.com/fixture/server.jar",
            ServerSha1 = hash,
            ServerSizeBytes = payload.Length
        };
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        await using var runtime = new VanillaRuntimeCertifier(root, http);

        var artifact = await runtime.GetArtifactAsync(version, root, CancellationToken.None);

        Assert.True(File.Exists(artifact));
        Assert.Equal(payload, await File.ReadAllBytesAsync(artifact));
        Assert.False(File.Exists(artifact + ".partial"));
    }

    [Fact]
    public void Storage_forecast_counts_only_missing_or_size_invalid_hash_addressed_artifacts()
    {
        var artifacts = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(artifacts);
        var cached = Version("cached") with { ServerSha1 = new string('a', 40), ServerSizeBytes = 4 };
        var missing = Version("missing") with { ServerSha1 = new string('b', 40), ServerSizeBytes = 7 };
        File.WriteAllBytes(Path.Combine(artifacts, cached.ServerSha1 + ".jar"), new byte[4]);

        Assert.Equal(7, VanillaCertificationCampaign.AdditionalArtifactBytes([cached, missing], root));
    }

    [Fact]
    public void Cleanup_reconciliation_requires_the_exact_disposable_work_root_to_be_absent()
    {
        var residual = Path.Combine(root, "work", "1.21.8-0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(residual);

        Assert.False(VanillaCertificationCampaign.NoResidualWorkRoot(root, "1.21.8"));

        Directory.Delete(residual, recursive: true);
        Assert.True(VanillaCertificationCampaign.NoResidualWorkRoot(root, "1.21.8"));
    }

    [Fact]
    public void Certification_properties_remain_loopback_only_and_use_a_legacy_safe_view_radius()
    {
        var properties = VanillaRuntimeCertifier.CertificationServerProperties(25_565);

        Assert.Contains("server-ip=127.0.0.1", properties, StringComparison.Ordinal);
        Assert.Contains("server-port=25565", properties, StringComparison.Ordinal);
        Assert.Contains("view-distance=4", properties, StringComparison.Ordinal);
        Assert.Contains("enable-rcon=false", properties, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_evidence_export_keeps_terminal_identity_results_without_local_diagnostics()
    {
        var completed = DateTimeOffset.Parse("2026-08-17T17:00:00Z");
        var ledger = new VanillaCertificationLedger
        {
            Entries =
            [
                new VanillaCertificationEntry
                {
                    VersionId = "1.21.11", ArtifactSha1 = new string('a', 40), MetadataSha1 = new string('b', 40),
                    JavaMajor = 21, CompletedAt = completed, Result = VanillaCertificationResult.Passed,
                    RuntimeLaunched = true, ReadinessConfirmed = true, CleanStopConfirmed = true,
                    ExpectedFilesConfirmed = true, NoUnexpectedGuiConfirmed = true, CleanupSucceeded = true
                },
                new VanillaCertificationEntry
                {
                    VersionId = "unsafe", ArtifactSha1 = new string('c', 40), MetadataSha1 = new string('d', 40),
                    JavaMajor = 21, CompletedAt = completed, Result = VanillaCertificationResult.FailedReadiness
                }
            ]
        };

        using var document = JsonDocument.Parse(VanillaRuntimeCertificationEvidence.Export(ledger));

        var entries = document.RootElement.GetProperty("entries");
        Assert.Equal(2, entries.GetArrayLength());
        Assert.Contains(entries.EnumerateArray(), item => item.GetProperty("versionId").GetString() == "1.21.11");
        Assert.Contains(entries.EnumerateArray(), item => item.GetProperty("versionId").GetString() == "unsafe" &&
                                                       item.GetProperty("result").GetString() == "FailedReadiness");
        Assert.False(document.RootElement.GetRawText().Contains("diagnosticLog", StringComparison.OrdinalIgnoreCase));
    }

    private VanillaCertificationCampaignOptions Options(bool explicitEula) => new()
    {
        CacheRoot = root,
        LedgerPath = Path.Combine(root, "ledger.json"),
        ExplicitEulaAuthorization = explicitEula,
        PerVersionTimeout = TimeSpan.FromSeconds(30)
    };

    private static VanillaVersionCatalog Catalog(params VanillaVersionOption[] versions) => new()
    {
        Options = versions,
        RetrievedUtc = DateTimeOffset.UtcNow,
        ProviderAvailable = true,
        ManifestLatestReleaseId = versions.FirstOrDefault()?.VersionId ?? ""
    };

    private static VanillaVersionOption Version(string id = "1.21.8") => new()
    {
        VersionId = id,
        Channel = VanillaReleaseChannel.Stable,
        ReleaseType = "release",
        ReleaseKind = MinecraftReleaseKind.Release,
        ReleaseTime = DateTimeOffset.UtcNow,
        MetadataUrl = $"https://piston-meta.mojang.com/{id}.json",
        MetadataSha1 = new string('b', 40),
        HasServerDownload = true,
        ServerDownloadUrl = $"https://piston-data.mojang.com/{id}/server.jar",
        ServerSha1 = new string('a', 40),
        ServerSizeBytes = 1024,
        RequiredJavaMajor = 21,
        JavaRequirementSource = JavaRequirementSource.OfficialMetadata,
        Support = VanillaVersionSupport.Supported,
        SupportTier = MinecraftVersionSupportTier.Experimental,
        LaunchProfile = Profile(),
        Provenance = "Official Mojang metadata"
    };

    private static MinecraftLaunchProfile Profile() => new()
    {
        Kind = MinecraftLaunchProfileKind.ModernEulaNogui,
        Arguments = "nogui",
        ReadinessPattern = "Done (",
        StopCommand = "stop",
        RequiresEulaFile = true,
        Evidence = "Test profile",
        Capabilities = new MinecraftVersionCapabilities { StatusQuery = true }
    };

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class RecordingRuntime(TimeSpan? delay = null) : IVanillaRuntimeCertifier
    {
        private int calls;
        public int Calls => calls;

        public async Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
            VanillaVersionOption version,
            VanillaCertificationCampaignOptions options,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            if (delay is { } wait)
                await Task.Delay(wait, cancellationToken);
            return new VanillaRuntimeCertificationOutcome
            {
                Result = VanillaCertificationResult.Passed,
                Reason = "Fixture exact runtime passed.",
                JavaPath = "fixture-java",
                RuntimeLaunched = true,
                ReadinessConfirmed = true,
                CleanStopConfirmed = true,
                ExpectedFilesConfirmed = true,
                NoUnexpectedGuiConfirmed = true,
                StatusPingConfirmed = true,
                CleanupSucceeded = true
            };
        }
    }

    private sealed class ThrowingRuntime : IVanillaRuntimeCertifier
    {
        public Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
            VanillaVersionOption version,
            VanillaCertificationCampaignOptions options,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("fixture provider unavailable");
    }

    private sealed class HistoricalJavaFallbackRuntime : IVanillaRuntimeCertifier
    {
        public int Calls { get; private set; }

        public Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
            VanillaVersionOption version,
            VanillaCertificationCampaignOptions options,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls == 1
                ? new VanillaRuntimeCertificationOutcome
                {
                    Result = VanillaCertificationResult.FailedRuntimeStartup,
                    Reason = "Eclipse Temurin did not return a Windows x64 Java 16 runtime.",
                    CleanupSucceeded = true
                }
                : new VanillaRuntimeCertificationOutcome
                {
                    Result = VanillaCertificationResult.Passed,
                    Reason = "Official JDK fallback passed.",
                    RuntimeLaunched = true,
                    ReadinessConfirmed = true,
                    CleanStopConfirmed = true,
                    ExpectedFilesConfirmed = true,
                    NoUnexpectedGuiConfirmed = true,
                    StatusPingConfirmed = true,
                    CleanupSucceeded = true
                });
        }
    }

    private sealed class LegacyMojangSocketRuntime : IVanillaRuntimeCertifier
    {
        public int Calls { get; private set; }

        public Task<VanillaRuntimeCertificationOutcome> CertifyAsync(
            VanillaVersionOption version,
            VanillaCertificationCampaignOptions options,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls == 1
                ? new VanillaRuntimeCertificationOutcome
                {
                    Result = VanillaCertificationResult.BlockedEnvironment,
                    Reason = "An attempt was made to access a socket in a way forbidden by its access permissions. (launcher.mojang.com:443)",
                    CleanupSucceeded = true
                }
                : new VanillaRuntimeCertificationOutcome
                {
                    Result = VanillaCertificationResult.Passed,
                    Reason = "The exact official server passed after network access was restored.",
                    RuntimeLaunched = true,
                    ReadinessConfirmed = true,
                    CleanStopConfirmed = true,
                    ExpectedFilesConfirmed = true,
                    NoUnexpectedGuiConfirmed = true,
                    StatusPingConfirmed = true,
                    CleanupSucceeded = true
                });
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }
}
