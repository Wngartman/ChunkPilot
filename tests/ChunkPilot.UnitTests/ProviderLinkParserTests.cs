using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class ProviderLinkParserTests
{
    [Theory]
    [InlineData("https://modrinth.com/modpack/prominence-2-rpg", CatalogProvider.Modrinth, ProviderLinkKind.Project, "prominence-2-rpg", null)]
    [InlineData("https://www.modrinth.com/modpack/prominence-2-rpg/version/abc_DEF-12", CatalogProvider.Modrinth, ProviderLinkKind.ExactRelease, "prominence-2-rpg", "abc_DEF-12")]
    [InlineData("https://www.curseforge.com/minecraft/modpacks/statech-industry-2", CatalogProvider.CurseForge, ProviderLinkKind.Project, "statech-industry-2", null)]
    [InlineData("https://www.curseforge.com/minecraft/modpacks/statech-industry-2/files/6721493", CatalogProvider.CurseForge, ProviderLinkKind.ExactRelease, "statech-industry-2", "6721493")]
    public void Parses_allowlisted_project_and_exact_release_urls(string url, CatalogProvider provider,
        ProviderLinkKind kind, string project, string? release)
    {
        var parsed = ProviderLinkParser.Parse(url);

        Assert.Equal(provider, parsed.Provider);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(project, parsed.ProjectReference);
        Assert.Equal(release, parsed.ReleaseReference);
        Assert.StartsWith("https://", parsed.CanonicalUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://modrinth.com/modpack/example")]
    [InlineData("https://evil.example/modpack/example")]
    [InlineData("https://modrinth.com/plugin/example")]
    [InlineData("https://www.curseforge.com/minecraft/modpacks/example/files/not-a-number")]
    [InlineData("https://www.curseforge.com/minecraft/mods/example")]
    public void Rejects_untrusted_or_unsupported_url_shapes(string url)
    {
        Assert.False(ProviderLinkParser.TryParse(url, out var parsed, out var error));
        Assert.Null(parsed);
        Assert.NotEmpty(error);
    }
}
