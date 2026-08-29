using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeCreationAuthorizationRegistryTests
{
    [Fact]
    public void Official_server_pack_consumes_matching_exact_preflight_evidence()
    {
        var result = ReadyOfficial();
        var registry = new CurseForgeCreationAuthorizationRegistry();
        registry.Register(result);

        registry.Consume(OfficialPlan(result));
    }

    [Fact]
    public void Generated_candidate_consumes_matching_client_archive_evidence()
    {
        var result = ReadyGenerated();
        var registry = new CurseForgeCreationAuthorizationRegistry();
        registry.Register(result);
        var publicPlan = GeneratedPlan(result);

        Assert.Null(publicPlan.CurseForgeGeneratedPlan);
        Assert.DoesNotContain(publicPlan.Problems(), problem =>
            problem.Contains("generated CurseForge file plan", StringComparison.OrdinalIgnoreCase));

        var authorized = registry.Consume(publicPlan);

        Assert.NotNull(authorized.CurseForgeGeneratedPlan);
        Assert.Equal(result.GeneratedPackPlan!.Digest, authorized.CurseForgeGeneratedPlan.Digest);
    }

    [Fact]
    public void Identity_mismatch_fails_without_consuming_the_matching_authorization()
    {
        var result = ReadyOfficial();
        var registry = new CurseForgeCreationAuthorizationRegistry();
        registry.Register(result);

        var mismatch = OfficialPlan(result) with { ExpectedSha1 = new string('f', 40) };
        var failure = Assert.Throws<InvalidOperationException>(() => registry.Consume(mismatch));
        Assert.Contains("no longer matches", failure.Message, StringComparison.OrdinalIgnoreCase);

        registry.Consume(OfficialPlan(result));
    }

    [Fact]
    public void Matching_authorization_is_one_time_and_replay_fails()
    {
        var result = ReadyOfficial();
        var plan = OfficialPlan(result);
        var registry = new CurseForgeCreationAuthorizationRegistry();
        registry.Register(result);
        registry.Consume(plan);

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Consume(plan));
        Assert.Contains("already used", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expired_authorization_fails_closed()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 29, 18, 0, 0, TimeSpan.Zero));
        var result = ReadyOfficial();
        var registry = new CurseForgeCreationAuthorizationRegistry(clock, TimeSpan.FromMinutes(2));
        registry.Register(result);
        clock.Advance(TimeSpan.FromMinutes(2));

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Consume(OfficialPlan(result)));
        Assert.Contains("expired", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Outstanding_authorizations_are_bounded()
    {
        var registry = new CurseForgeCreationAuthorizationRegistry();
        for (var index = 0; index < 64; index++)
            registry.Register(ReadyOfficial() with { OperationId = Guid.NewGuid() });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(ReadyOfficial() with { OperationId = Guid.NewGuid() }));
        Assert.Contains("Too many", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exact_revoke_is_idempotent_and_rapid_invalidations_do_not_fill_the_bound()
    {
        var registry = new CurseForgeCreationAuthorizationRegistry();
        for (var index = 0; index < 65; index++)
        {
            var result = ReadyOfficial() with { OperationId = Guid.NewGuid() };
            registry.Register(result);
            Assert.True(registry.Revoke(result.OperationId));
            Assert.False(registry.Revoke(result.OperationId));
        }

        Assert.Equal(0, registry.OutstandingCount);
        registry.Register(ReadyOfficial());
    }

    [Fact]
    public void Revoke_arriving_before_slow_preflight_completion_prevents_late_authorization()
    {
        var result = ReadyOfficial();
        var registry = new CurseForgeCreationAuthorizationRegistry();

        Assert.True(registry.Revoke(result.OperationId));
        var failure = Assert.Throws<InvalidOperationException>(() => registry.Register(result));

        Assert.Contains("revoked before", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.OutstandingCount);
    }

    [Fact]
    public void Expiry_uses_monotonic_time_not_wall_clock_changes()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 8, 29, 18, 0, 0, TimeSpan.Zero));
        var result = ReadyOfficial();
        var registry = new CurseForgeCreationAuthorizationRegistry(clock, TimeSpan.FromMinutes(2));
        registry.Register(result);
        clock.ChangeWallClock(TimeSpan.FromDays(30));

        registry.Consume(OfficialPlan(result));
    }

    [Fact]
    public void Injected_generated_plan_is_rejected_without_consuming_registry_copy()
    {
        var result = ReadyGenerated();
        var registry = new CurseForgeCreationAuthorizationRegistry();
        registry.Register(result);
        var injected = GeneratedPlan(result) with { CurseForgeGeneratedPlan = result.GeneratedPackPlan };

        Assert.Throws<InvalidOperationException>(() => registry.Consume(injected));
        var authorized = registry.Consume(GeneratedPlan(result));
        Assert.Equal(result.GeneratedPackPlan!.Digest, authorized.CurseForgeGeneratedPlan!.Digest);
    }

    [Fact]
    public void Agent_route_leaves_local_CurseForge_manifest_creation_unaffected()
    {
        var registry = new CurseForgeCreationAuthorizationRegistry();
        var localPlan = new ModpackCreationPlan
        {
            SourceKind = ModpackCreationSource.LocalCurseForgeManifest,
            Provider = UpdateProvider.CurseForge,
            Source = @"C:\Synthetic\reviewed-pack.zip"
        };

        registry.DemandAndConsumeIfRequired(localPlan);
    }

    private static CurseForgeModpackPreflightResult ReadyOfficial() => new()
    {
        OperationId = Guid.NewGuid(),
        ProjectId = "10",
        ClientFileId = "111",
        ServerPackFileId = "222",
        State = CatalogReleasePreflightState.Ready,
        MinecraftVersion = "1.20.1",
        Loader = "Forge",
        LoaderVersion = "47.3.0",
        RequiredJavaMajor = 17,
        ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/111/client.zip",
        ClientSha1 = new string('a', 40),
        ClientSha256 = new string('b', 64),
        ClientSizeBytes = 4_096,
        ServerPackDownloadUrl = "https://mediafilez.forgecdn.net/files/222/server.zip",
        ServerPackSha1 = new string('c', 40),
        ServerPackSizeBytes = 8_192
    };

    private static CurseForgeModpackPreflightResult ReadyGenerated() => ReadyOfficial() with
    {
        OperationId = Guid.NewGuid(),
        ServerPackFileId = "",
        ServerPackDownloadUrl = "",
        ServerPackSha1 = "",
        ServerPackSizeBytes = null,
        GeneratedPackPlan = GeneratedPackPlan()
    };

    private static CurseForgeGeneratedPackPlan GeneratedPackPlan() =>
        CurseForgeGeneratedPackPlanService.Seal(new CurseForgeGeneratedPackPlan
        {
            MinecraftVersion = "1.20.1",
            Loader = "Forge",
            LoaderVersion = "47.3.0",
            RequiredFiles =
            [
                new CurseForgeGeneratedFilePlan
                {
                    ProjectId = "20",
                    FileId = "200",
                    FileName = "required.jar",
                    DownloadUrl = "https://mediafilez.forgecdn.net/files/200/required.jar",
                    SizeBytes = 2_048,
                    ProviderSha1 = new string('d', 40),
                    RequiredBy =
                    [
                        new CurseForgeGeneratedFileEvidence
                        {
                            Relation = CurseForgeGeneratedFileRelation.ManifestRequired,
                            RequestedFileId = "200"
                        }
                    ]
                }
            ],
            TotalResolvedBytes = 2_048
        });

    private static ModpackCreationPlan OfficialPlan(CurseForgeModpackPreflightResult result) => new()
    {
        OperationId = Guid.NewGuid(),
        PreflightOperationId = result.OperationId,
        SourceKind = ModpackCreationSource.CurseForgeOfficialServerPack,
        Source = result.ServerPackDownloadUrl,
        Provider = UpdateProvider.CurseForge,
        ProjectId = result.ProjectId,
        VersionId = result.ClientFileId,
        ServerPackFileId = result.ServerPackFileId,
        MinecraftVersion = result.MinecraftVersion,
        Loader = result.Loader,
        LoaderVersion = result.LoaderVersion,
        RequiredJavaMajor = result.RequiredJavaMajor,
        ExpectedSha1 = result.ServerPackSha1,
        ExpectedSizeBytes = result.ServerPackSizeBytes,
        VerifiedClientArchiveSha256 = result.ClientSha256
    };

    private static ModpackCreationPlan GeneratedPlan(CurseForgeModpackPreflightResult result) => new()
    {
        OperationId = Guid.NewGuid(),
        PreflightOperationId = result.OperationId,
        SourceKind = ModpackCreationSource.CurseForgeGeneratedCandidate,
        Source = result.ClientDownloadUrl,
        Provider = UpdateProvider.CurseForge,
        ProjectId = result.ProjectId,
        VersionId = result.ClientFileId,
        ServerPackFileId = "",
        MinecraftVersion = result.MinecraftVersion,
        Loader = result.Loader,
        LoaderVersion = result.LoaderVersion,
        RequiredJavaMajor = result.RequiredJavaMajor,
        ExpectedSha1 = result.ClientSha1,
        ExpectedSha256 = result.ClientSha256,
        ExpectedSizeBytes = result.ClientSizeBytes,
        VerifiedClientArchiveSha256 = result.ClientSha256
    };

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => current;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed)
        {
            current += elapsed;
            timestamp += elapsed.Ticks;
        }
        public void ChangeWallClock(TimeSpan elapsed) => current += elapsed;
    }
}
