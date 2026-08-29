using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgePreflightEvidenceStoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Official_review_binds_exact_preflight_operation_and_server_pack_evidence()
    {
        var store = new CurseForgePreflightEvidenceStore();
        var reviewId = Guid.NewGuid();
        var creationOperationId = Guid.NewGuid();
        var project = Project(OfficialRelease());
        var result = ReadyOfficial();
        var generation = store.Invalidate();

        Assert.True(store.TryCommit(
            generation, reviewId, "Browse", project, project.Versions[0], result, Now,
            out var evidence));
        var reviewed = evidence.ApplyTo(project.Versions[0]);
        var required = store.Require(reviewId, "Browse", "10", "111", Now);
        var plan = required.Bind(new ModpackCreationPlan { OperationId = creationOperationId });

        Assert.Equal(result.OperationId, plan.PreflightOperationId);
        Assert.Equal(creationOperationId, plan.OperationId);
        Assert.NotEqual(plan.OperationId, plan.PreflightOperationId);
        Assert.Equal(ModpackCreationSource.CurseForgeOfficialServerPack, plan.SourceKind);
        Assert.Equal(result.ServerPackDownloadUrl, plan.Source);
        Assert.Equal(result.ServerPackSha1, plan.ExpectedSha1);
        Assert.Equal(result.ServerPackSizeBytes, plan.ExpectedSizeBytes);
        Assert.Equal(result.ClientSha256, plan.VerifiedClientArchiveSha256);
        Assert.Equal(result.ServerPackDownloadUrl, reviewed.DownloadUrl);
        Assert.Equal(result.ServerPackSha1, reviewed.Sha1);
        Assert.Equal(result.ServerPackSizeBytes, reviewed.SizeBytes);
    }

    [Fact]
    public void Generated_review_binds_verified_client_archive_without_server_pack_evidence()
    {
        var store = new CurseForgePreflightEvidenceStore();
        var reviewId = Guid.NewGuid();
        var project = Project(GeneratedRelease());
        var result = ReadyGenerated();
        var generation = store.Invalidate();

        Assert.True(store.TryCommit(
            generation, reviewId, "Link", project, project.Versions[0], result, Now,
            out var evidence));
        var plan = evidence.Bind(new ModpackCreationPlan { OperationId = Guid.NewGuid() });

        Assert.Equal(ModpackCreationSource.CurseForgeGeneratedCandidate, plan.SourceKind);
        Assert.Equal(result.OperationId, plan.PreflightOperationId);
        Assert.Equal(result.ClientDownloadUrl, plan.Source);
        Assert.Equal(result.ClientSha1, plan.ExpectedSha1);
        Assert.Equal(result.ClientSha256, plan.ExpectedSha256);
        Assert.Equal(result.ClientSizeBytes, plan.ExpectedSizeBytes);
        Assert.Equal("", plan.ServerPackFileId);
        Assert.Equal("", plan.ExpectedSha512);
    }

    [Fact]
    public void Changed_review_identity_cannot_erase_the_newer_current_selection()
    {
        var store = ReadyStore(out var reviewId, out _, out _);

        Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Require(Guid.NewGuid(), "Browse", "10", "111", Now));
        Assert.Equal(reviewId,
            store.Require(reviewId, "Browse", "10", "111", Now).ReviewId);
    }

    [Theory]
    [InlineData("Link", "10", "111")]
    [InlineData("Browse", "different-project", "111")]
    [InlineData("Browse", "10", "different-file")]
    public void Method_project_or_file_mismatch_cannot_reuse_review(
        string method,
        string projectId,
        string clientFileId)
    {
        var store = ReadyStore(out var reviewId, out _, out _);

        Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Require(reviewId, method, projectId, clientFileId, Now));
    }

    [Fact]
    public void Expired_or_consumed_review_cannot_be_reused()
    {
        var store = new CurseForgePreflightEvidenceStore(TimeSpan.FromMinutes(1));
        var reviewId = Guid.NewGuid();
        var project = Project(OfficialRelease());
        var result = ReadyOfficial();
        var generation = store.Invalidate();
        Assert.True(store.TryCommit(
            generation, reviewId, "Browse", project, project.Versions[0], result, Now,
            out _));

        Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Require(reviewId, "Browse", "10", "111", Now.AddMinutes(1)));

        generation = store.Invalidate();
        Assert.True(store.TryCommit(
            generation, reviewId, "Browse", project, project.Versions[0], result, Now,
            out _));
        var consumed = store.Consume(reviewId, "Browse", "10", "111", Now);
        Assert.Equal(result.OperationId, consumed.OperationId);
        Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Consume(reviewId, "Browse", "10", "111", Now));
    }

    [Fact]
    public void Expired_matching_review_surfaces_the_exact_agent_authorization_for_revocation()
    {
        var store = new CurseForgePreflightEvidenceStore(TimeSpan.FromMinutes(1));
        var reviewId = Guid.NewGuid();
        var project = Project(OfficialRelease());
        var result = ReadyOfficial();
        var generation = store.Invalidate();
        Assert.True(store.TryCommit(
            generation, reviewId, "Browse", project, project.Versions[0], result, Now,
            out _));

        var failure = Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Require(reviewId, "Browse", "10", "111", Now.AddMinutes(1)));

        Assert.Equal(result.OperationId, failure.OperationId);
        Assert.Null(store.InvalidateWithEvidence().OperationId);
    }

    [Fact]
    public void Stale_consume_racing_a_new_review_does_not_erase_the_new_authorization()
    {
        var store = ReadyStore(out var oldReviewId, out var project, out _);
        var newReviewId = Guid.NewGuid();
        var newResult = ReadyOfficial();
        var generation = store.InvalidateWithEvidence().Generation;
        Assert.True(store.TryCommit(
            generation, newReviewId, "Browse", project, project.Versions[0], newResult, Now,
            out _));

        var failure = Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Consume(oldReviewId, "Browse", "10", "111", Now));

        Assert.Null(failure.OperationId);
        Assert.Equal(newResult.OperationId,
            store.Require(newReviewId, "Browse", "10", "111", Now).OperationId);
    }

    [Fact]
    public async Task Cancellation_before_a_ready_response_pre_revokes_65_late_authorizations()
    {
        var registry = new CurseForgeCreationAuthorizationRegistry();
        for (var index = 0; index < 65; index++)
        {
            var result = ReadyOfficial();
            await using (var revocation = new CurseForgePreflightRevocationLease(
                             result.OperationId,
                             operationId =>
                             {
                                 registry.Revoke(operationId);
                                 return Task.CompletedTask;
                             }))
            {
                // The App cancels before a Ready response is available, so the lease remains armed.
            }

            Assert.Throws<InvalidOperationException>(() => registry.Register(result));
        }

        Assert.Equal(0, registry.OutstandingCount);
        registry.Register(ReadyOfficial());
        Assert.Equal(1, registry.OutstandingCount);
    }

    [Fact]
    public void Invalidated_inflight_preflight_cannot_commit_stale_result()
    {
        var store = new CurseForgePreflightEvidenceStore();
        var reviewId = Guid.NewGuid();
        var project = Project(OfficialRelease());
        var staleGeneration = store.Invalidate();
        store.Invalidate();

        Assert.False(store.TryCommit(
            staleGeneration, reviewId, "Browse", project, project.Versions[0], ReadyOfficial(), Now,
            out _));
        Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Require(reviewId, "Browse", "10", "111", Now));
    }

    [Fact]
    public void Invalidation_returns_the_exact_agent_authorization_for_revocation()
    {
        var store = ReadyStore(out var reviewId, out _, out var result);

        var invalidation = store.InvalidateWithEvidence();

        Assert.Equal(result.OperationId, invalidation.OperationId);
        Assert.Throws<StaleCurseForgePreflightException>(() =>
            store.Require(reviewId, "Browse", "10", "111", Now));
    }

    [Fact]
    public void Late_cleanup_for_an_old_operation_cannot_invalidate_a_new_review()
    {
        var store = ReadyStore(out _, out var project, out var first);
        var nextReview = Guid.NewGuid();
        var next = ReadyOfficial();
        var generation = store.InvalidateWithEvidence().Generation;
        Assert.True(store.TryCommit(
            generation, nextReview, "Browse", project, project.Versions[0], next, Now,
            out _));

        Assert.False(store.InvalidateIfOperationId(first.OperationId));
        Assert.Equal(next.OperationId,
            store.Require(nextReview, "Browse", "10", "111", Now).OperationId);
        Assert.True(store.InvalidateIfOperationId(next.OperationId));
    }

    [Fact]
    public void Background_catalog_refresh_does_not_replace_the_native_exact_evidence()
    {
        var store = ReadyStore(out var reviewId, out _, out var result);
        var refreshedSummary = Project(OfficialRelease() with
        {
            DownloadUrl = "",
            Sha1 = "",
            SizeBytes = null,
            CreationPreflightState = CatalogReleasePreflightState.Required
        });

        var evidence = store.Require(
            reviewId, "Browse", refreshedSummary.ProjectId,
            refreshedSummary.Versions[0].VersionId, Now);
        var plan = evidence.Bind(new ModpackCreationPlan { OperationId = Guid.NewGuid() });

        Assert.Equal(result.OperationId, plan.PreflightOperationId);
        Assert.Equal(result.ServerPackDownloadUrl, plan.Source);
        Assert.Equal(result.ServerPackSha1, plan.ExpectedSha1);
    }

    [Fact]
    public void Ready_official_result_without_exact_server_pack_evidence_is_rejected()
    {
        var store = new CurseForgePreflightEvidenceStore();
        var project = Project(OfficialRelease());
        var generation = store.Invalidate();

        Assert.Throws<InvalidDataException>(() => store.TryCommit(
            generation, Guid.NewGuid(), "Browse", project, project.Versions[0],
            ReadyOfficial() with { ServerPackDownloadUrl = "" }, Now, out _));
    }

    private static CurseForgePreflightEvidenceStore ReadyStore(
        out Guid reviewId,
        out CatalogItem project,
        out CurseForgeModpackPreflightResult result)
    {
        var store = new CurseForgePreflightEvidenceStore();
        reviewId = Guid.NewGuid();
        project = Project(OfficialRelease());
        result = ReadyOfficial();
        var generation = store.Invalidate();
        Assert.True(store.TryCommit(
            generation, reviewId, "Browse", project, project.Versions[0], result, Now,
            out _));
        return store;
    }

    private static CatalogItem Project(CatalogVersion release) => new()
    {
        Provider = CatalogProvider.CurseForge,
        ContentType = CatalogContentType.Modpack,
        ProjectId = "10",
        Slug = "fixture-pack",
        Name = "Fixture Pack",
        Versions = [release]
    };

    private static CatalogVersion OfficialRelease() => new()
    {
        VersionId = "111",
        VersionName = "1.0.0",
        MinecraftVersion = "1.20.1",
        Loader = "Forge",
        ReleaseChannel = ReleaseChannel.Stable,
        DownloadUrl = "https://mediafilez.forgecdn.net/files/222/server.zip",
        Sha1 = new string('c', 40),
        SizeBytes = 8_192,
        HasServerPackage = true,
        ClientFileId = "111",
        ServerPackFileId = "222",
        ClientDownloadUrl = "https://mediafilez.forgecdn.net/files/111/client.zip",
        ClientSha1 = new string('a', 40),
        ClientSizeBytes = 4_096,
        CreationPreflightState = CatalogReleasePreflightState.Required,
        RequiredJavaMajor = 17
    };

    private static CatalogVersion GeneratedRelease() => OfficialRelease() with
    {
        DownloadUrl = "",
        Sha1 = "",
        SizeBytes = null,
        HasServerPackage = false,
        ServerPackFileId = "",
        CanGenerateServerCandidate = true
    };

    private static CurseForgeModpackPreflightResult ReadyOfficial() => new()
    {
        OperationId = Guid.NewGuid(),
        ProjectId = "10",
        ClientFileId = "111",
        ServerPackFileId = "222",
        State = CatalogReleasePreflightState.Ready,
        Detail = "Verified exact manifest.",
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
        ServerPackSizeBytes = null
    };
}
