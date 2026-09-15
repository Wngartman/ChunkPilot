using System.Text.RegularExpressions;

namespace ChunkPilot.UnitTests;

public sealed class ReleaseVersionContractTests
{
    [Theory]
    [InlineData("v1.0.0", true)]
    [InlineData("v1.0.0-rc.1", true)]
    [InlineData("v1.3.0-alpha.6", true)]
    [InlineData("v2.0.0-beta.12", true)]
    [InlineData("v01.0.0", false)]
    [InlineData("v1.0.0-rc.0", false)]
    [InlineData("v1.0.0-rc.01", false)]
    [InlineData("v1.0.0-stable", false)]
    [InlineData("v1.0.0+unchecked", false)]
    [InlineData("v1.0.0;Write-Host", false)]
    [InlineData(" v1.0.0", false)]
    [InlineData("v1.0.0/../../other", false)]
    [InlineData("v1.0.0\n", false)]
    public void Consumer_build_and_publication_accept_only_exact_supported_versions(string tag, bool valid)
    {
        foreach (var script in new[] { "publish.ps1", "package-release.ps1", "publish-release.ps1" })
        {
            var source = File.ReadAllText(Path.Combine(Root(), "scripts", script));
            var match = Regex.Match(source, "ValidatePattern\\('([^']+)'\\)", RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
            Assert.True(match.Success, script);
            Assert.Equal(valid, Regex.IsMatch(tag, match.Groups[1].Value,
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }
    }

    [Fact]
    public void Package_and_installer_identity_come_from_the_source_version()
    {
        var publish = File.ReadAllText(Path.Combine(Root(), "scripts", "publish.ps1"));
        var package = File.ReadAllText(Path.Combine(Root(), "scripts", "package-release.ps1"));
        var targets = File.ReadAllText(Path.Combine(Root(), "Directory.Build.targets"));
        Assert.Contains("$effectiveReleaseTag -cne \"v$sourceVersion\"", publish, StringComparison.Ordinal);
        Assert.Contains("/DMyAppVersion=$installerVersion", publish, StringComparison.Ordinal);
        Assert.Contains("$ReleaseTag.Substring(1)", package, StringComparison.Ordinal);
        Assert.Contains("$(Version)+$(ChunkPilotGitSha)", targets, StringComparison.Ordinal);
        Assert.DoesNotContain("v1.3.0-alpha.5", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void Publication_preserves_the_release_channel_as_one_splatted_argument()
    {
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "release.yml"));
        Assert.Contains(
            "[string[]]$channelArguments = if ($env:RELEASE_TAG.Contains('-')) { @('--prerelease') } else { @('--latest') }",
            workflow, StringComparison.Ordinal);
        Assert.Contains(
            "@channelArguments --title $title --notes-file release/RELEASE_NOTES.md",
            workflow, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
