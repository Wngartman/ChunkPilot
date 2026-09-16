using System.Text.RegularExpressions;

namespace ChunkPilot.UnitTests;

public sealed class PublicSignatureMetadataContractTests
{
    private static readonly string[] PublicFields = ["Path", "Status", "Signed", "Timestamped", "Subject"];

    [Fact]
    public void Public_signature_metadata_projects_only_complete_exact_shipped_file_evidence()
    {
        var source = File.ReadAllText(Path.Combine(Root(), "scripts", "package-release.ps1"));
        var function = Regex.Match(source,
            @"(?ms)^function ConvertTo-PublicSignatureReport\b.*?^}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(function.Success);
        var projection = function.Value;
        Assert.Contains("@('ChunkPilot.exe', 'ChunkPilot.FirewallHelper.exe', 'Agent/ChunkPilot.Agent.exe')", projection, StringComparison.Ordinal);
        Assert.Contains("[StringComparer]::OrdinalIgnoreCase", projection, StringComparison.Ordinal);
        Assert.Contains("$expected.Add($absolute, $relative)", projection, StringComparison.Ordinal);
        Assert.Contains("$expected.Add($installerFull, $installerName)", projection, StringComparison.Ordinal);
        Assert.Contains("$expected.ContainsKey($installerFull) -or -not $publicNames.Add($installerName)", projection, StringComparison.Ordinal);
        Assert.Contains("[string]::IsNullOrWhiteSpace([string]$signature.Path)", projection, StringComparison.Ordinal);
        Assert.Contains("-not $expected.ContainsKey($absolute) -or -not $seen.Add($absolute)", projection, StringComparison.Ordinal);
        Assert.Contains("$seen.Count -ne $expected.Count", projection, StringComparison.Ordinal);
        Assert.Contains("Path = $expected[$absolute]", projection, StringComparison.Ordinal);
        foreach (var field in PublicFields.Skip(1))
            Assert.Contains(field + " = $signature." + field, projection, StringComparison.Ordinal);
        var fields = Regex.Match(projection, @"(?s)\[pscustomobject\]@\{(.*?)\}",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.True(fields.Success);
        var fieldNames = Regex.Matches(fields.Groups[1].Value, @"(?m)^\s*(\w+)\s*=",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.Equal(PublicFields, fieldNames);
        Assert.Contains("$signatureReport = @(ConvertTo-PublicSignatureReport $localSignatureReport $selfContained $installer)", source, StringComparison.Ordinal);
        Assert.Contains("Signatures = $signatureReport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Signatures = $localSignatureReport", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("$signatureReport = @(ConvertTo-PublicSignatureReport", StringComparison.Ordinal) <
                    source.IndexOf("Signatures = $signatureReport", StringComparison.Ordinal));
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChunkPilot.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
