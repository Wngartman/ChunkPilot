using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class PackPlatformAssessmentTests
{
    [Theory]
    [InlineData("paper")]
    [InlineData("bukkit")]
    [InlineData("spigot")]
    public void Explicit_plugin_server_pack_is_the_only_paper_eligible_class(string loader)
    {
        var result = PackPlatformPolicy.Assess(new PackPlatformEvidence
        {
            DeclaredLoaders = [loader],
            ExplicitServerBundle = true
        });

        Assert.Equal(PackPlatformKind.PaperCompatible, result.Platform);
        Assert.True(result.PaperEligible);
    }

    [Theory]
    [InlineData("fabric", PackPlatformKind.Fabric)]
    [InlineData("neoforge", PackPlatformKind.NeoForge)]
    [InlineData("forge", PackPlatformKind.Forge)]
    [InlineData("quilt", PackPlatformKind.Quilt)]
    public void Mod_loader_pack_never_receives_paper(string loader, PackPlatformKind expected)
    {
        var result = PackPlatformPolicy.Assess(new PackPlatformEvidence
        {
            DeclaredLoaders = [loader],
            ContainsModJars = true,
            ExplicitServerBundle = true
        });

        Assert.Equal(expected, result.Platform);
        Assert.False(result.PaperEligible);
        Assert.Contains(loader, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plugin_loader_plus_mod_jars_is_rejected_as_unsupported_hybrid()
    {
        var result = PackPlatformPolicy.Assess(new PackPlatformEvidence
        {
            DeclaredLoaders = ["paper"],
            ContainsModJars = true
        });

        Assert.Equal(PackPlatformKind.ConflictingUnsupported, result.Platform);
        Assert.False(result.PaperEligible);
    }

    [Fact]
    public void Unidentified_mod_content_remains_unknown_instead_of_guessing_paper()
    {
        var result = PackPlatformPolicy.Assess(new PackPlatformEvidence { ContainsModJars = true });

        Assert.Equal(PackPlatformKind.Unknown, result.Platform);
        Assert.False(result.PaperEligible);
    }
}
