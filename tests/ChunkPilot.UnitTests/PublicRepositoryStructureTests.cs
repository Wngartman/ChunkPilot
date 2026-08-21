using System.Diagnostics;

namespace ChunkPilot.UnitTests;

public sealed class PublicRepositoryStructureTests
{
    private static readonly string Root = RepositoryRoot();

    [Fact]
    public void Tracked_root_contains_only_conventional_product_entries()
    {
        var allowedDirectories = new HashSet<string>(StringComparer.Ordinal)
        {
            ".github", "archive", "assets", "docs", "installer", "legal", "release", "scripts",
            "services", "src", "tests"
        };
        var allowedFiles = new HashSet<string>(StringComparer.Ordinal)
        {
            ".editorconfig", ".gitattributes", ".gitignore", "AGENTS.md", "CHANGELOG.md",
            "ChunkPilot.sln", "CONTRIBUTING.md", "Directory.Build.props", "Directory.Build.targets",
            "Directory.Packages.props", "global.json", "README.md", "SECURITY.md"
        };
        var tracked = RunGit("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unexpected = tracked.Select(path => path.Replace('\\', '/'))
            .Where(path =>
            {
                var slash = path.IndexOf('/');
                return slash < 0 ? !allowedFiles.Contains(path) : !allowedDirectories.Contains(path[..slash]);
            })
            .ToArray();

        Assert.True(unexpected.Length == 0, "Unexpected tracked root entries: " + string.Join(", ", unexpected));
        Assert.DoesNotContain(tracked, path => path.StartsWith(".kiro/", StringComparison.Ordinal));
        Assert.DoesNotContain(tracked, path => path.StartsWith("artifacts/", StringComparison.Ordinal));
        Assert.DoesNotContain(tracked, path => path.StartsWith("temp/", StringComparison.Ordinal));
    }

    [Fact]
    public void Public_documentation_has_no_obsolete_task_artifacts()
    {
        var tracked = RunGit("ls-files docs").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.DoesNotContain(tracked, path => path.Contains("WORKLOG", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tracked, path => path.Contains("COMPETITOR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tracked, path => path.Contains("MIGRATION-ROADMAP", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(tracked, path => path.Contains("OVERHAUL-STATE", StringComparison.OrdinalIgnoreCase));
    }

    private static string RunGit(string arguments)
    {
        var startInfo = new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return output;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
