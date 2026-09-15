using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeServicePublicationTests
{
    [Theory]
    [InlineData("", true)]
    [InlineData("https://service.example", true)]
    [InlineData("https://service.example/", true)]
    [InlineData("https://service.example:443", true)]
    [InlineData("http://service.example", false)]
    [InlineData("https://user:secret@service.example", false)]
    [InlineData("https://service.example?key=secret", false)]
    [InlineData("https://service.example/#secret", false)]
    [InlineData("https://service.example:8443", false)]
    [InlineData("https://service.example;OtherProperty=secret", false)]
    [InlineData("https://service.example\n", false)]
    [InlineData("https://service.example/path", false)]
    public void Publisher_accepts_only_a_nonsecret_root_https_service_address(string endpoint, bool accepted)
    {
        var source = File.ReadAllText(Path.Combine(Root, "scripts", "publish.ps1"));
        var pattern = Regex.Match(source,
            "ValidatePattern\\('([^']+)'\\)\\]\\s*\\[string\\]\\$CurseForgeServiceEndpoint",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(pattern.Success);
        Assert.Equal(accepted, Regex.IsMatch(endpoint, pattern.Groups[1].Value,
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        Assert.Contains("$serviceUri.IsLoopback", source, StringComparison.Ordinal);
        Assert.Contains("[UriHostNameType]::Dns", source, StringComparison.Ordinal);
        Assert.Contains("-p:CurseForgeServiceEndpoint=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CURSEFORGE_API_KEY", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Service_endpoint_metadata_is_opt_in_and_contains_no_credential()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Build.props"));
        var metadata = Assert.Single(props.Descendants("AssemblyMetadata"),
            item => (string?)item.Attribute("Include") == "CurseForgeServiceEndpoint");
        Assert.Equal("$(CurseForgeServiceEndpoint)", (string?)metadata.Attribute("Value"));
        Assert.Equal("'$(CurseForgeServiceEndpoint)' != ''", (string?)metadata.Parent!.Attribute("Condition"));
        Assert.DoesNotContain(props.Descendants(), element => element.Name.LocalName == "CurseForgeServiceEndpoint");
    }

    [Fact]
    public void Worker_local_secrets_and_runtime_state_are_ignored_and_rejected_by_publication()
    {
        var ignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        var audit = File.ReadAllText(Path.Combine(Root, "scripts", "audit-publication.ps1"));
        Assert.Contains(".dev.vars", ignore, StringComparison.Ordinal);
        Assert.Contains(".dev.vars.*", ignore, StringComparison.Ordinal);
        Assert.Contains(".wrangler/", ignore, StringComparison.Ordinal);
        Assert.Contains("\\.dev\\.vars", audit, StringComparison.Ordinal);
        Assert.Contains("\\.wrangler", audit, StringComparison.Ordinal);
    }

    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
    }
}
