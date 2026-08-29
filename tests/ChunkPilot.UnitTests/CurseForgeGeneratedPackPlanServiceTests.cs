using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeGeneratedPackPlanServiceTests
{
    [Fact]
    public void Seal_is_deterministic_and_digest_binds_every_exact_file_field()
    {
        var first = RawPlan();
        var sealedPlan = CurseForgeGeneratedPackPlanService.Seal(first);
        var second = CurseForgeGeneratedPackPlanService.Seal(first);

        Assert.Equal(sealedPlan.Digest, second.Digest);
        Assert.Equal(64, sealedPlan.Digest.Length);
        Assert.Equal(sealedPlan.Digest,
            CurseForgeGeneratedPackPlanService.ValidateAndClone(sealedPlan).Digest);

        var drifted = sealedPlan with
        {
            RequiredFiles =
            [
                sealedPlan.RequiredFiles[0] with
                {
                    ProviderSha1 = new string('f', 40)
                }
            ]
        };
        var failure = Assert.Throws<InvalidDataException>(() =>
            CurseForgeGeneratedPackPlanService.ValidateAndClone(drifted));
        Assert.Contains("digest", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Optional_review_contains_only_explicit_provider_or_manifest_relationships()
    {
        var raw = RawPlan() with
        {
            OptionalExclusions =
            [
                new CurseForgeGeneratedOptionalExclusion
                {
                    ProjectId = "30",
                    FileId = "300",
                    Relation = CurseForgeGeneratedOptionalRelation.ManifestOptional
                },
                new CurseForgeGeneratedOptionalExclusion
                {
                    ProjectId = "40",
                    Relation = CurseForgeGeneratedOptionalRelation.OptionalDependency,
                    DeclaredByProjectId = "20"
                }
            ]
        };

        var sealedPlan = CurseForgeGeneratedPackPlanService.Seal(raw);

        Assert.Equal(2, sealedPlan.OptionalExclusions.Count);
        Assert.Contains("1 manifest entry", sealedPlan.OptionalReviewSummary, StringComparison.Ordinal);
        Assert.Contains("1 dependency relationship", sealedPlan.OptionalReviewSummary, StringComparison.Ordinal);
        Assert.Contains("No client-only exclusions", sealedPlan.OptionalReviewSummary, StringComparison.Ordinal);

        var invented = sealedPlan with
        {
            OptionalReviewSummary = "Excluded a guessed client-only mod."
        };
        Assert.Throws<InvalidDataException>(() =>
            CurseForgeGeneratedPackPlanService.ValidateAndClone(invented));
    }

    [Fact]
    public void Resolved_file_budget_fails_closed_above_eight_gibibytes()
    {
        var files = Enumerable.Range(1, 17).Select(index => new CurseForgeGeneratedFilePlan
        {
            ProjectId = index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FileId = (index + 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture),
            FileName = $"file-{index}.jar",
            DownloadUrl = $"https://mediafilez.forgecdn.net/files/{index}/file-{index}.jar",
            SizeBytes = JarInventoryService.MaximumJarBytes,
            ProviderSha1 = new string('a', 40),
            RequiredBy =
            [
                new CurseForgeGeneratedFileEvidence
                {
                    Relation = index == 1
                        ? CurseForgeGeneratedFileRelation.ManifestRequired
                        : CurseForgeGeneratedFileRelation.RequiredDependency,
                    DeclaredByProjectId = index == 1 ? "" : "1",
                    RequestedFileId = index == 1
                        ? (index + 10_000).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : ""
                }
            ]
        }).ToArray();
        var raw = RawPlan() with
        {
            RequiredFiles = files,
            TotalResolvedBytes = files.Sum(file => file.SizeBytes)
        };

        var failure = Assert.Throws<InvalidDataException>(() =>
            CurseForgeGeneratedPackPlanService.Seal(raw));
        Assert.Contains("8 GiB", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CurseForgeGeneratedPackPlan RawPlan() => new()
    {
        MinecraftVersion = "1.20.1",
        Loader = "Fabric",
        LoaderVersion = "0.15.11",
        RequiredFiles =
        [
            new CurseForgeGeneratedFilePlan
            {
                ProjectId = "20",
                FileId = "200",
                FileName = "required.jar",
                DownloadUrl = "https://mediafilez.forgecdn.net/files/200/required.jar",
                SizeBytes = 2_048,
                ProviderSha1 = new string('a', 40),
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
    };
}
