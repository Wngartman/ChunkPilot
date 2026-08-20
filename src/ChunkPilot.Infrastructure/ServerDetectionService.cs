using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class ServerDetectionService
{
    private static readonly HashSet<string> PreferredScripts = new(StringComparer.OrdinalIgnoreCase)
    {
        "run.bat", "start.bat", "launch.bat", "server.bat", "run.cmd", "start.cmd",
        "run.ps1", "start.ps1"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "world", "world_nether", "world_the_end", "region", "entities", "poi",
        "libraries", "logs", "backups", "cache", "mods", "plugins", ".git"
    };

    private readonly BatchFileParser batchParser = new();
    private readonly JavaDiscoveryService javaDiscovery;

    public ServerDetectionService(JavaDiscoveryService javaDiscovery) => this.javaDiscovery = javaDiscovery;

    public async Task<ServerDetectionResult> DetectAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(root);

        var before = SnapshotWrites(root);
        var files = EnumerateCandidateFiles(root).ToArray();
        var candidates = files
            .Select(path => CreateCandidate(root, path))
            .Where(candidate => candidate is not null)
            .Cast<LaunchCandidate>()
            .OrderBy(candidate => candidate.Recommendation)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var ecosystem = DetectEcosystem(root, files);
        var (version, loaderVersion) = DetectVersions(root, files);
        var port = ReadPort(root);
        var java = await javaDiscovery.DiscoverAsync(root, candidates, cancellationToken).ConfigureAwait(false);
        var findings = BuildFindings(root, candidates, ecosystem);
        var after = SnapshotWrites(root);
        if (!before.SequenceEqual(after))
            throw new IOException("The read-only import scan observed an unexpected write-time change in the selected folder.");

        return new ServerDetectionResult
        {
            RootPath = root,
            SuggestedName = new DirectoryInfo(root).Name,
            Ecosystem = ecosystem,
            MinecraftVersion = version,
            LoaderVersion = loaderVersion,
            Port = port,
            Candidates = candidates,
            JavaRuntimes = java,
            Findings = findings
        };
    }

    private LaunchCandidate? CreateCandidate(string root, string path)
    {
        var extension = Path.GetExtension(path);
        var name = Path.GetFileName(path);
        var relative = Path.GetRelativePath(root, path);
        var score = Path.GetDirectoryName(path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? 20 : 5;
        var reason = "Launch-capable file found in a bounded server-folder scan.";
        var problems = new List<string>();
        var executable = path;
        var arguments = "";
        var detaches = false;

        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            score += PreferredScripts.Contains(name) ? 65 : 35;
            var parsed = batchParser.Parse(path);
            detaches = parsed.Detaches;
            problems.AddRange(parsed.Problems);
            executable = Environment.GetEnvironmentVariable("COMSPEC") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            arguments = CommandLineQuoter.BuildCmdArguments(path);
            reason = parsed.JavaExecutable.Length > 0
                ? $"Script launches {Path.GetFileName(parsed.JavaExecutable)} with parsed JVM/server arguments."
                : "Common server launch script; command is preserved as written.";
            if (detaches)
                score -= 35;
        }
        else if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            score += PreferredScripts.Contains(name) ? 55 : 25;
            executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            arguments = $"-NoLogo -NoProfile -ExecutionPolicy Bypass -File {CommandLineQuoter.QuoteWindowsArgument(path)}";
            problems.Add("PowerShell script behavior is preserved; review it before first launch.");
        }
        else if (extension.Equals(".jar", StringComparison.OrdinalIgnoreCase))
        {
            var lower = name.ToLowerInvariant();
            if (!LooksLikeServerJar(lower))
                return null;
            score += lower is "server.jar" or "fabric-server-launch.jar" ? 65 : 45;
            executable = "java";
            arguments = $"-jar {CommandLineQuoter.QuoteWindowsArgument(path)} nogui";
            reason = "Jar name or manifest resembles a Minecraft server launcher.";
        }
        else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!name.Contains("server", StringComparison.OrdinalIgnoreCase))
                return null;
            score += 25;
            problems.Add("Custom executable detected; verify its console and shutdown behavior.");
        }
        else
        {
            return null;
        }

        return new LaunchCandidate
        {
            DisplayName = relative,
            SourcePath = path,
            Executable = executable,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(path) ?? root,
            Recommendation = detaches || score < 45
                ? RecommendationLevel.ManualConfigurationRequired
                : score >= 70 ? RecommendationLevel.Recommended : RecommendationLevel.Alternative,
            Reason = reason,
            Problems = problems,
            DetachesProcess = detaches
        };
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var yielded = 0;
        while (pending.Count > 0 && yielded < 500)
        {
            var (directory, depth) = pending.Dequeue();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".jar", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(file).Equals("server.properties", StringComparison.OrdinalIgnoreCase))
                {
                    yielded++;
                    yield return file;
                    if (yielded >= 500)
                        yield break;
                }
            }

            if (depth >= 2)
                continue;
            IEnumerable<string> directories;
            try { directories = Directory.EnumerateDirectories(directory); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var child in directories)
            {
                var name = Path.GetFileName(child);
                if (!ExcludedDirectories.Contains(name) && !File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    pending.Enqueue((child, depth + 1));
            }
        }
    }

    private static bool LooksLikeServerJar(string lowerName) =>
        !lowerName.Contains("installer", StringComparison.Ordinal) &&
        !lowerName.Contains("client", StringComparison.Ordinal) &&
        (
            lowerName == "server.jar" ||
            lowerName.Contains("server", StringComparison.Ordinal) ||
            lowerName.Contains("paper", StringComparison.Ordinal) ||
            lowerName.Contains("purpur", StringComparison.Ordinal) ||
            lowerName.Contains("spigot", StringComparison.Ordinal) ||
            lowerName.Contains("fabric-server", StringComparison.Ordinal) ||
            lowerName.Contains("forge-server", StringComparison.Ordinal) ||
            lowerName.Contains("quilt-server", StringComparison.Ordinal)
        );

    private static ServerEcosystem DetectEcosystem(string root, IReadOnlyCollection<string> files)
    {
        var names = string.Join('\n', files.Select(Path.GetFileName)).ToLowerInvariant();
        if (names.Contains("neoforge", StringComparison.Ordinal) || Directory.Exists(Path.Combine(root, "libraries", "net", "neoforged")))
            return ServerEcosystem.NeoForge;
        if (names.Contains("forge", StringComparison.Ordinal) || Directory.Exists(Path.Combine(root, "libraries", "net", "minecraftforge")))
            return ServerEcosystem.Forge;
        if (names.Contains("fabric", StringComparison.Ordinal) || File.Exists(Path.Combine(root, "fabric-server-launch.jar")))
            return ServerEcosystem.Fabric;
        if (names.Contains("quilt", StringComparison.Ordinal))
            return ServerEcosystem.Quilt;
        if (names.Contains("purpur", StringComparison.Ordinal))
            return ServerEcosystem.Purpur;
        if (names.Contains("paper", StringComparison.Ordinal))
            return ServerEcosystem.Paper;
        if (names.Contains("spigot", StringComparison.Ordinal))
            return ServerEcosystem.Spigot;
        if (Directory.Exists(Path.Combine(root, "plugins")) && Directory.Exists(Path.Combine(root, "mods")))
            return ServerEcosystem.Hybrid;
        if (files.Any(path => Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)))
            return ServerEcosystem.Vanilla;
        return ServerEcosystem.Custom;
    }

    private static (string Version, string LoaderVersion) DetectVersions(string root, IReadOnlyCollection<string> files)
    {
        var latestLog = Path.Combine(root, "logs", "latest.log");
        if (File.Exists(latestLog))
        {
            try
            {
                using var stream = new FileStream(latestLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > 2 * 1024 * 1024)
                    stream.Seek(-2 * 1024 * 1024, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                var match = LogVersionRegex().Match(reader.ReadToEnd());
                if (match.Success)
                    return (match.Groups["mc"].Value, match.Groups["loader"].Value);
            }
            catch (IOException) { }
        }

        foreach (var file in files.Where(path => Path.GetExtension(path).Equals(".jar", StringComparison.OrdinalIgnoreCase)).Take(30))
        {
            var match = VersionRegex().Match(Path.GetFileNameWithoutExtension(file));
            if (match.Success)
                return (match.Groups["mc"].Value, match.Groups["loader"].Value);
        }

        var versionJson = Path.Combine(root, "version.json");
        if (File.Exists(versionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(versionJson));
                if (document.RootElement.TryGetProperty("id", out var id))
                    return (id.GetString() ?? "Unknown", "");
            }
            catch (JsonException) { }
        }

        return ("Unknown", "");
    }

    private static int ReadPort(string root)
    {
        var properties = Path.Combine(root, "server.properties");
        if (!File.Exists(properties))
            return 25565;
        foreach (var line in File.ReadLines(properties))
        {
            if (line.StartsWith("server-port=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan("server-port=".Length), out var port) &&
                port is > 0 and <= 65535)
                return port;
        }
        return 25565;
    }

    private static IReadOnlyList<DiagnosticFinding> BuildFindings(
        string root, IReadOnlyList<LaunchCandidate> candidates, ServerEcosystem ecosystem)
    {
        var findings = new List<DiagnosticFinding>();
        findings.Add(new DiagnosticFinding
        {
            Code = "path.exists",
            Severity = FindingSeverity.Pass,
            Title = "Server folder is accessible",
            Evidence = root,
        });
        findings.Add(candidates.Count == 0
            ? new DiagnosticFinding
            {
                Code = "launch.none",
                Severity = FindingSeverity.Warning,
                Title = "No reliable launch candidate found",
                Evidence = "The bounded read-only scan found no common script, server jar, or custom server executable.",
                SuggestedAction = "Create a manual launch profile.",
            }
            : new DiagnosticFinding
            {
                Code = "launch.found",
                Severity = FindingSeverity.Pass,
                Title = $"{candidates.Count} launch candidate(s) found",
                Evidence = candidates[0].DisplayName,
            });

        if (File.Exists(Path.Combine(root, "eula.txt")) &&
            File.ReadAllText(Path.Combine(root, "eula.txt")).Contains("eula=false", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new DiagnosticFinding
            {
                Code = "eula.rejected",
                Severity = FindingSeverity.Error,
                Title = "Minecraft EULA is not accepted",
                Evidence = "eula.txt contains eula=false.",
                SuggestedAction = "Review Mojang's EULA and edit eula.txt yourself if you accept it. ChunkPilot will not accept it automatically.",
                RelevantPath = Path.Combine(root, "eula.txt"),
            });
        }

        findings.Add(new DiagnosticFinding
        {
            Code = "ecosystem.detected",
            Severity = ecosystem == ServerEcosystem.Custom ? FindingSeverity.Information : FindingSeverity.Pass,
            Title = $"Detected ecosystem: {ecosystem}",
            Evidence = "Detection uses launcher names and known folder structure.",
        });
        return findings;
    }

    private static IReadOnlyList<string> SnapshotWrites(string root)
    {
        return Directory.EnumerateFileSystemEntries(root)
            .Select(path => $"{Path.GetFileName(path)}|{File.GetLastWriteTimeUtc(path):O}")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [GeneratedRegex(@"(?<!\d)(?<mc>1\.\d+(?:\.\d+)?)(?:[-_](?<loader>\d+(?:\.\d+)+))?", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"(?:Minecraft(?:\s+server)?(?:\s+version)?|Starting minecraft server version)\s*[:=]?\s*(?<mc>1\.\d+(?:\.\d+)?)(?:[^\r\n]*?(?:NeoForge|Forge|Fabric|Quilt|Paper|Purpur)\s*(?<loader>\d+(?:\.\d+)+))?", RegexOptions.IgnoreCase)]
    private static partial Regex LogVersionRegex();
}
