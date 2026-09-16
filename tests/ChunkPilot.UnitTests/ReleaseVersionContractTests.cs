using System.Text.RegularExpressions;
using System.Text.Json;
using System.Xml.Linq;

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

    [Theory]
    [InlineData("", true)]
    [InlineData("https://service.example", true)]
    [InlineData("https://service.example:443/", true)]
    [InlineData("http://service.example", false)]
    [InlineData("https://user:fixture@service.example", false)]
    [InlineData("https://service.example?key=fixture", false)]
    [InlineData("https://service.example/#fixture", false)]
    [InlineData("https://service.example/path", false)]
    [InlineData("https://service.example:8443", false)]
    [InlineData("https://service.example;OtherProperty=fixture", false)]
    [InlineData("https://service.example,OtherProperty=fixture", false)]
    [InlineData("https://service.example\n", false)]
    public void Release_service_input_rejects_credentials_and_property_injection(string endpoint, bool accepted)
    {
        foreach (var script in new[] { "publish.ps1", "publish-release.ps1" })
        {
            var source = File.ReadAllText(Path.Combine(Root(), "scripts", script));
            var pattern = Regex.Match(source,
                "ValidatePattern\\('([^']+)'\\)\\]\\s*\\[string\\]\\$CurseForgeServiceEndpoint",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.True(pattern.Success, script);
            Assert.Equal(accepted, Regex.IsMatch(endpoint, pattern.Groups[1].Value,
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
            Assert.Contains("$serviceUri.IsLoopback", source, StringComparison.Ordinal);
            Assert.Contains("[UriHostNameType]::Dns", source, StringComparison.Ordinal);
            Assert.Contains(".EndsWith('.local'", source, StringComparison.Ordinal);
            Assert.Contains(".EndsWith('.localhost'", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Release_endpoint_is_explicit_through_dispatch_and_every_native_build()
    {
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "release.yml"));
        var dispatch = File.ReadAllText(Path.Combine(Root(), "scripts", "publish-release.ps1"));
        var publish = File.ReadAllText(Path.Combine(Root(), "scripts", "publish.ps1"));
        Assert.Contains("CURSEFORGE_SERVICE_ENDPOINT: ${{ inputs.curseforge_service_endpoint }}", workflow, StringComparison.Ordinal);
        Assert.Contains("-CurseForgeServiceEndpoint $env:CURSEFORGE_SERVICE_ENDPOINT", workflow, StringComparison.Ordinal);
        Assert.Contains("-f \"curseforge_service_endpoint=$CurseForgeServiceEndpoint\"", dispatch, StringComparison.Ordinal);
        Assert.Contains("$configurationProperties = @(\"-p:CurseForgeServiceEndpoint=$CurseForgeServiceEndpoint\")", publish, StringComparison.Ordinal);
        Assert.Contains("$identityProperties += $configurationProperties", publish, StringComparison.Ordinal);
        foreach (var line in publish.Split('\n').Where(line => line.StartsWith("& $dotnet ", StringComparison.Ordinal)))
        {
            var expected = line.Contains(" publish ", StringComparison.Ordinal) ? "@identityProperties" : "@configurationProperties";
            Assert.Contains(expected, line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Previous_release_upgrade_is_independent_from_prerelease_note_mutation()
    {
        var workflow = File.ReadAllText(Path.Combine(Root(), ".github", "workflows", "release.yml"));
        var dispatch = File.ReadAllText(Path.Combine(Root(), "scripts", "publish-release.ps1"));
        Assert.Contains("UPGRADE_FROM_TAG: ${{ inputs.previous_release || inputs.supersedes }}", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release download $env:UPGRADE_FROM_TAG", workflow, StringComparison.Ordinal);
        Assert.Contains("$arguments.PreviousInstallerPath = $previous", workflow, StringComparison.Ordinal);
        Assert.Contains("The explicitly requested previous release installer is missing.", workflow, StringComparison.Ordinal);
        Assert.Contains("-f \"previous_release=$previousReleaseTag\"", dispatch, StringComparison.Ordinal);
        var edit = workflow[workflow.IndexOf("- name: Mark the previous prerelease superseded", StringComparison.Ordinal)..];
        Assert.Contains("if: inputs.supersedes != ''", edit, StringComparison.Ordinal);
        Assert.Contains("-not $previous.prerelease", edit, StringComparison.Ordinal);
        Assert.Contains("gh release edit $env:SUPERSEDES_TAG", edit, StringComparison.Ordinal);
        Assert.DoesNotContain("PREVIOUS_RELEASE_TAG", edit, StringComparison.Ordinal);
        Assert.DoesNotContain("UPGRADE_FROM_TAG", edit, StringComparison.Ordinal);
    }

    [Fact]
    public void Product_and_installer_versions_agree_and_upgrade_the_public_one_zero_release()
    {
        var props = XDocument.Load(Path.Combine(Root(), "Directory.Build.props"));
        string Property(string name) => Assert.Single(props.Descendants(name)).Value;
        var product = Property("Version");
        var numeric = Property("ApplicationVersion");
        Assert.True(Version.Parse(product) > new Version(1, 0, 0));
        Assert.True(Version.Parse(numeric) > new Version(1, 3, 1));
        Assert.Equal(numeric + ".0", Property("AssemblyVersion"));
        Assert.Equal(numeric + ".0", Property("FileVersion"));
        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root(), "src", "ChunkPilot.WebUi", "package.json")));
        Assert.Equal(product, package.RootElement.GetProperty("version").GetString());
        var installer = File.ReadAllText(Path.Combine(Root(), "installer", "ChunkPilot.iss"));
        Assert.Contains($"#define MyAppVersion \"{numeric}\"", installer, StringComparison.Ordinal);
        Assert.Contains($"#define MyReleaseTag \"v{product}\"", installer, StringComparison.Ordinal);
        Assert.Contains("AppId={{C609C59D-FD5A-4A18-91C8-2D04F7177A69}", installer, StringComparison.Ordinal);
        Assert.Contains("DefaultDirName={localappdata}\\Programs\\ChunkPilot", installer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Valid", true)]
    [InlineData("NotSigned", true)]
    [InlineData("HashMismatch", false)]
    [InlineData("NotTrusted", false)]
    [InlineData("NotSupportedFileFormat", false)]
    [InlineData("Incompatible", false)]
    [InlineData("UnknownError", false)]
    public void Unsigned_release_permission_does_not_accept_invalid_signature_statuses(string status, bool accepted)
    {
        var source = File.ReadAllText(Path.Combine(Root(), "scripts", "verify-release-signatures.ps1"));
        var allowed = Regex.Match(source, @"\$Status -cnotin @\(([^)]+)\)",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(allowed.Success);
        Assert.Equal(accepted, allowed.Groups[1].Value.Split(',').Select(value => value.Trim().Trim('\'')).Contains(status, StringComparer.Ordinal));
        Assert.Contains("$Status -ceq 'Valid' -and -not $HasSigner", source, StringComparison.Ordinal);
        Assert.Contains("$RequireSigned -and ($Status -cne 'Valid' -or -not $HasSigner -or -not $HasTimestamp)", source, StringComparison.Ordinal);
        Assert.Contains("Assert-ReleaseSignaturePolicy -Status ([string]$signature.Status)", source, StringComparison.Ordinal);
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
