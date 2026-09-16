using System.Text.RegularExpressions;

namespace ChunkPilot.UnitTests;

public sealed class PortableConsumerBoundaryTests
{
    private static readonly string[] BoundaryKinds = ["Directory", "File", "Extension"];

    [Theory]
    [InlineData("Certification/ChunkPilot.Certification.exe")]
    [InlineData("certification/ChunkPilot.Infrastructure.dll")]
    [InlineData("ChunkPilot.Certification.exe")]
    [InlineData("ChunkPilot.Certification.runtimeconfig.json")]
    [InlineData("Agent/ChunkPilot.FakeServer.exe")]
    [InlineData(".chunkpilot-build-manifest.json")]
    [InlineData("dev-current/ChunkPilot.exe")]
    [InlineData("dev-tools/helper.exe")]
    [InlineData("dev-helpers/helper.dll")]
    [InlineData("development/helper.exe")]
    [InlineData("scripts/certify-curseforge-runtime.ps1")]
    [InlineData("helper.ps1")]
    [InlineData("helper.psm1")]
    [InlineData("tests/fixture.json")]
    [InlineData("TestResults/run.trx")]
    [InlineData("private/fixture.json")]
    [InlineData(".private/fixture.json")]
    [InlineData(".wrangler/state.json")]
    [InlineData(".dev.vars")]
    [InlineData(".dev.vars.local")]
    [InlineData(".secrets/fixture.txt")]
    [InlineData("secrets.dat")]
    [InlineData("chunkpilot.db-wal")]
    [InlineData("curseforge-api-key-fixture.txt")]
    [InlineData("identity.pfx")]
    [InlineData("identity.pem")]
    [InlineData("identity.key")]
    [InlineData("WebView2/Default/Preferences")]
    [InlineData("CurrentProfile/Preferences")]
    [InlineData("node_modules/react/index.js")]
    [InlineData("server.jar")]
    [InlineData("server.mrpack")]
    [InlineData("ChunkPilot.pdb")]
    [InlineData("Program.cs")]
    public void Consumer_gate_rejects_development_private_and_server_payloads(string path)
    {
        Assert.True(IsProhibited(path));
        Assert.True(IsProhibited(path.Replace('/', '\\').ToUpperInvariant()));
    }

    [Theory]
    [InlineData("ChunkPilot.exe")]
    [InlineData("ChunkPilot.FirewallHelper.exe")]
    [InlineData("Agent/ChunkPilot.Agent.exe")]
    [InlineData("Agent/ChunkPilot.Infrastructure.dll")]
    [InlineData("WebUi/index.html")]
    [InlineData("WebUi/assets/index-abc123.js")]
    [InlineData("WebUi/assets/index-abc123.css")]
    [InlineData("WebUi/assets/inter-latin.woff2")]
    [InlineData("Assets/ChunkPilot.ico")]
    [InlineData("runtimes/win-x64/native/WebView2Loader.dll")]
    [InlineData("Microsoft.Web.WebView2.Core.dll")]
    [InlineData("README.txt")]
    [InlineData("THIRD-PARTY-NOTICES.txt")]
    public void Consumer_gate_preserves_legitimate_runtime_assets(string path)
    {
        Assert.False(IsProhibited(path));
        Assert.False(IsProhibited(path.Replace('/', '\\')));
    }

    [Fact]
    public void Consumer_gate_checks_relative_paths_before_any_packaged_process_launch()
    {
        var source = Script();
        Assert.Contains("[IO.Path]::GetRelativePath($testRoot, $_.FullName)", source, StringComparison.Ordinal);
        foreach (var kind in BoundaryKinds)
            Assert.Contains("$relative -match $prohibitedConsumer" + kind + "Pattern", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("if ($prohibited.Count -ne 0)", StringComparison.Ordinal) <
                    source.IndexOf("scripts\\smoke-portable.ps1", StringComparison.Ordinal));
    }

    private static bool IsProhibited(string path)
    {
        var source = Script();
        return BoundaryKinds.Any(kind =>
        {
            var declaration = Regex.Match(source,
                "(?m)^\\$prohibitedConsumer" + kind + "Pattern = '([^']+)'\\r?$",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            Assert.True(declaration.Success, "Missing consumer " + kind + " boundary pattern.");
            return Regex.IsMatch(path, declaration.Groups[1].Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        });
    }

    private static string Script()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        return File.ReadAllText(Path.Combine(root, "scripts", "test-portable-package.ps1"));
    }
}
