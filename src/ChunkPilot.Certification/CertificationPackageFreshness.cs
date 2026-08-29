using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChunkPilot.Certification;

internal static class CertificationPackageFreshness
{
    internal const string IntegrityManifestFileName = ".chunkpilot-build-manifest.json";
    private const int MaximumManifestBytes = 4 * 1024 * 1024;
    private const int MaximumManifestFiles = 10_000;
    private const int MaximumManifestTreeEntries = 20_000;
    internal static readonly string[] PackageAffectingPaths =
    [
        "src",
        "assets",
        ".editorconfig",
        ".gitattributes",
        "ChunkPilot.sln",
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.Config",
        "nuget.config",
        "scripts/build-webui.ps1",
        "scripts/dev-build.ps1",
        "scripts/certify-curseforge-runtime.ps1"
    ];

    public static void Validate(string repositoryRoot, string expectedGitSha, string agentExecutablePath)
    {
        var repository = Path.GetFullPath(repositoryRoot);
        ValidateRepositoryIdentity(repository, expectedGitSha);
        var currentHead = expectedGitSha.ToLowerInvariant();

        var agent = Path.GetFullPath(agentExecutablePath);
        var app = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(agent)!, "..", "ChunkPilot.exe"));
        var firewallHelper = Path.Combine(Path.GetDirectoryName(app)!, "ChunkPilot.FirewallHelper.exe");
        if (!File.Exists(agent) || !File.Exists(app) || !File.Exists(firewallHelper))
            throw new FileNotFoundException(
                "The self-contained App/Agent/FirewallHelper candidate is missing. " +
                "Run scripts\\dev-build.ps1 -Tier Quick first.");
        var certification = Path.Combine(
            Path.GetDirectoryName(app)!, "Certification", "ChunkPilot.Certification.exe");
        if (!File.Exists(certification))
            throw new FileNotFoundException(
                "The packaged headless Certification controller is missing. " +
                "Run scripts\\dev-build.ps1 -Tier Quick first.");
        foreach (var executable in new[] { app, agent, firewallHelper, certification })
        {
            var productVersion = FileVersionInfo.GetVersionInfo(executable).ProductVersion ?? "";
            if (!productVersion.Contains(currentHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The packaged App/Agent candidate is stale for the current repository HEAD. " +
                    "Run scripts\\dev-build.ps1 -Tier Quick before certification.");
        }
        var inputProof = ComputePackageInputProof(repository);
        RequireCleanPackageInputProof(inputProof);
        ValidateIntegrityManifest(Path.GetDirectoryName(app)!, currentHead, inputProof);
    }

    public static void ValidateRepositoryIdentity(string repositoryRoot, string expectedGitSha)
    {
        var repository = Path.GetFullPath(repositoryRoot);
        var currentHead = ReadHead(repository);
        if (!currentHead.Equals(expectedGitSha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The repository HEAD changed after the candidate was selected; certification was refused.");
        EnsurePackageSourcesClean(repository);
        EnsureTrackedHeadBlobMatches(repository, "scripts/dev-build.ps1");
        EnsureTrackedHeadBlobMatches(repository, "scripts/certify-curseforge-runtime.ps1");
    }

    public static string ReadHead(string repositoryRoot)
    {
        var result = RunGit(repositoryRoot, ["rev-parse", "HEAD"]);
        var head = result.Output.Trim();
        if (result.ExitCode != 0 || head.Length != 40 || head.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("The current repository HEAD could not be read.");
        return head.ToLowerInvariant();
    }

    internal static void EnsurePackageSourcesClean(string repositoryRoot)
    {
        RequireCleanPackageInputProof(ComputePackageInputProof(repositoryRoot));
    }

    internal static CertificationPackageInputProof ComputePackageInputProof(string repositoryRoot)
    {
        var repository = Path.GetFullPath(repositoryRoot);
        var head = ParseHeadTree(RunGit(
            repository,
            ["ls-tree", "-r", "--full-tree", "HEAD", "--", .. PackageAffectingPaths]));
        if (head.Count == 0)
            throw new InvalidOperationException(
                "The current HEAD contains no package-affecting inputs.");
        var index = ParseIndex(RunGit(
            repository,
            ["ls-files", "--stage", "--", .. PackageAffectingPaths]));
        var untracked = Lines(RunGit(
                repository,
                ["ls-files", "--others", "--exclude-standard", "--", .. PackageAffectingPaths]))
            .Select(NormalizeGitPath)
            .ToArray();
        var unexpectedIgnored = Lines(RunGit(
                repository,
                ["ls-files", "--others", "--ignored", "--exclude-standard", "--",
                    .. PackageAffectingPaths]))
            .Select(NormalizeGitPath)
            .Where(path => !IsAllowedGeneratedPackageArtifact(path))
            .ToArray();

        var worktreePaths = head.Keys.Concat(index.Keys).Concat(untracked).Concat(unexpectedIgnored)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var existingPaths = new List<string>(worktreePaths.Length);
        foreach (var relativePath in worktreePaths)
        {
            var path = Path.GetFullPath(Path.Combine(
                repository, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var repositoryPrefix = Path.TrimEndingDirectorySeparator(repository) +
                                   Path.DirectorySeparatorChar;
            if (!path.StartsWith(repositoryPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "A package input path escaped the current repository.");
            if (File.Exists(path) && !Directory.Exists(path))
                existingPaths.Add(relativePath);
        }

        var worktree = new Dictionary<string, string>(StringComparer.Ordinal);
        if (existingPaths.Count > 0)
        {
            var hashResult = RunGit(
                repository,
                ["hash-object", "--stdin-paths"],
                string.Join('\n', existingPaths) + "\n");
            var hashes = Lines(hashResult).ToArray();
            if (hashes.Length != existingPaths.Count)
                throw new InvalidOperationException(
                    "Git returned a contradictory package-input hash set.");
            for (var indexValue = 0; indexValue < existingPaths.Count; indexValue++)
            {
                ValidateObjectId(hashes[indexValue]);
                worktree.Add(existingPaths[indexValue], hashes[indexValue].ToLowerInvariant());
            }
        }
        foreach (var missing in worktreePaths.Except(existingPaths, StringComparer.Ordinal))
            worktree.Add(missing, "missing");

        var headDigest = Digest(head);
        var worktreeDigest = Digest(worktree);
        var indexDigest = Digest(index);
        return new CertificationPackageInputProof
        {
            HeadSha256 = headDigest,
            WorktreeSha256 = worktreeDigest,
            IndexSha256 = indexDigest,
            FileCount = head.Count,
            MatchesHead = headDigest.Equals(worktreeDigest, StringComparison.OrdinalIgnoreCase) &&
                          headDigest.Equals(indexDigest, StringComparison.OrdinalIgnoreCase)
        };
    }

    internal static void RequireCleanPackageInputProof(CertificationPackageInputProof proof)
    {
        if (!proof.MatchesHead || proof.FileCount < 1 ||
            !Sha256(proof.HeadSha256) || !Sha256(proof.WorktreeSha256) ||
            !Sha256(proof.IndexSha256))
            throw new InvalidOperationException(
                "Package-affecting HEAD, index, and actual worktree inputs do not match exactly. " +
                "Commit or revert every staged, untracked, ignored-source, skip-worktree, and " +
                "assume-unchanged difference, " +
                "then run scripts\\dev-build.ps1 -Tier Quick.");
    }

    private static bool IsAllowedGeneratedPackageArtifact(string relativePath)
    {
        if (!relativePath.StartsWith("src/", StringComparison.OrdinalIgnoreCase))
            return false;
        var segments = relativePath.Split('/');
        if (segments.Any(segment =>
                segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (relativePath.StartsWith(
                "src/ChunkPilot.WebUi/node_modules/", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith(
                "src/ChunkPilot.WebUi/dist/", StringComparison.OrdinalIgnoreCase))
            return true;
        return relativePath.StartsWith(
                   "src/ChunkPilot.WebUi/", StringComparison.OrdinalIgnoreCase) &&
               relativePath.EndsWith(".tsbuildinfo", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureTrackedHeadBlobMatches(string repositoryRoot, string relativePath)
    {
        var tracked = RunGit(repositoryRoot, ["ls-files", "--error-unmatch", "--", relativePath]);
        var expected = RunGit(repositoryRoot, ["rev-parse", $"HEAD:{relativePath}"]);
        var actual = RunGit(repositoryRoot,
            ["hash-object", $"--path={relativePath}", "--", relativePath]);
        var expectedBlob = expected.Output.Trim();
        var actualBlob = actual.Output.Trim();
        if (tracked.ExitCode != 0 || expected.ExitCode != 0 || actual.ExitCode != 0 ||
            expectedBlob.Length is < 40 or > 64 || actualBlob.Length != expectedBlob.Length ||
            expectedBlob.Any(character => !Uri.IsHexDigit(character)) ||
            actualBlob.Any(character => !Uri.IsHexDigit(character)) ||
            !expectedBlob.Equals(actualBlob, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "A credential or package-bearing certification script does not exactly match its tracked HEAD blob.");
    }

    internal static void ValidateIntegrityManifest(
        string packageRoot,
        string expectedGitSha,
        CertificationPackageInputProof? expectedInputProof = null)
    {
        var root = Path.GetFullPath(packageRoot);
        RejectReparsePoint(root);
        var manifestPath = Path.Combine(root, IntegrityManifestFileName);
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length is < 1 or > MaximumManifestBytes)
            throw new InvalidDataException(
                "The HEAD-bound development package integrity manifest is missing or invalid. " +
                "Run scripts\\dev-build.ps1 -Tier Quick first.");
        RejectReparsePoint(manifestPath);

        DevelopmentPackageIntegrityManifest manifest;
        try
        {
            using var stream = new FileStream(
                manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.SequentialScan);
            manifest = JsonSerializer.Deserialize<DevelopmentPackageIntegrityManifest>(
                           stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? throw new InvalidDataException("The development package integrity manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The development package integrity manifest is malformed.", exception);
        }
        if (manifest.SchemaVersion != 3 ||
            !manifest.GitSha.Equals(expectedGitSha, StringComparison.OrdinalIgnoreCase) ||
            !manifest.PackageSourceKind.Equals(
                "isolated-head-archive", StringComparison.Ordinal) ||
            !manifest.PackageInputsMatchHead ||
            manifest.PackageInputFileCount < 1 ||
            !Sha256(manifest.PackageInputHeadSha256) ||
            !Sha256(manifest.PackageInputWorktreeSha256) ||
            !Sha256(manifest.PackageInputIndexSha256) ||
            !manifest.PackageInputHeadSha256.Equals(
                manifest.PackageInputWorktreeSha256, StringComparison.OrdinalIgnoreCase) ||
            !manifest.PackageInputHeadSha256.Equals(
                manifest.PackageInputIndexSha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Files.Count is < 3 or > MaximumManifestFiles)
            throw new InvalidDataException(
                "The development package integrity manifest is not bound to the current candidate.");
        if (expectedInputProof is not null &&
            (!expectedInputProof.MatchesHead ||
             manifest.PackageInputFileCount != expectedInputProof.FileCount ||
             !manifest.PackageInputHeadSha256.Equals(
                 expectedInputProof.HeadSha256, StringComparison.OrdinalIgnoreCase) ||
             !manifest.PackageInputWorktreeSha256.Equals(
                 expectedInputProof.WorktreeSha256, StringComparison.OrdinalIgnoreCase) ||
             !manifest.PackageInputIndexSha256.Equals(
                 expectedInputProof.IndexSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "The packaged candidate was not built from the exact current HEAD input tree.");

        IReadOnlyList<BoundedFileTreeEntry> inventory;
        try
        {
            inventory = BoundedNoFollowFileTree.Inventory(root, MaximumManifestTreeEntries);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                "The development package contains an unsafe or oversized file tree.", exception);
        }
        var actualFiles = inventory.Where(entry => !entry.IsDirectory)
            .Select(entry => entry.Path)
            .Where(path => !path.Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var entries = new Dictionary<string, DevelopmentPackageIntegrityFile>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (string.IsNullOrWhiteSpace(entry.RelativePath) ||
                Path.IsPathRooted(entry.RelativePath) || entry.RelativePath.Contains('\\') ||
                entry.RelativePath.Split('/').Any(segment => segment is "" or "." or "..") ||
                entry.SizeBytes < 0 || entry.Sha256.Length != 64 ||
                entry.Sha256.Any(character => !Uri.IsHexDigit(character)) ||
                !entries.TryAdd(entry.RelativePath, entry))
                throw new InvalidDataException(
                    "The development package integrity manifest contains an unsafe or duplicate entry.");
        }
        if (entries.Count != actualFiles.Length)
            throw new InvalidDataException(
                "The development package file set does not match its HEAD-bound integrity manifest.");

        foreach (var path in actualFiles)
        {
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (!entries.TryGetValue(relative, out var expected))
                throw new InvalidDataException(
                    "The development package contains a file absent from its integrity manifest.");
            var before = new FileInfo(path);
            if (before.Length != expected.SizeBytes)
                throw new InvalidDataException(
                    "A development package file size does not match its integrity manifest.");
            using var input = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.SequentialScan);
            var actualSha256 = Convert.ToHexString(SHA256.HashData(input));
            var after = new FileInfo(path);
            if (after.Length != before.Length ||
                !actualSha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "A development package file does not match its HEAD-bound SHA-256 manifest.");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "Development package integrity validation refuses reparse points.");
    }

    private static Dictionary<string, string> ParseHeadTree(GitResult result)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(result))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
                throw new InvalidDataException("Git returned a malformed HEAD package-input entry.");
            var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = NormalizeGitPath(line[(tab + 1)..]);
            if (metadata.Length != 3 || !metadata[1].Equals("blob", StringComparison.Ordinal) ||
                !entries.TryAdd(path, metadata[2].ToLowerInvariant()))
                throw new InvalidDataException("Git returned a duplicate or unsupported HEAD package input.");
            ValidateObjectId(metadata[2]);
        }
        return entries;
    }

    private static Dictionary<string, string> ParseIndex(GitResult result)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(result))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0)
                throw new InvalidDataException("Git returned a malformed index package-input entry.");
            var metadata = line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var path = NormalizeGitPath(line[(tab + 1)..]);
            if (metadata.Length != 3 || !metadata[2].Equals("0", StringComparison.Ordinal) ||
                !entries.TryAdd(path, metadata[1].ToLowerInvariant()))
                throw new InvalidDataException(
                    "The package-affecting index is unmerged, duplicated, or unsupported.");
            ValidateObjectId(metadata[1]);
        }
        return entries;
    }

    private static IEnumerable<string> Lines(GitResult result)
    {
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                "Git could not compute the exact package-input proof.");
        return result.Output.Split(
            ['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string NormalizeGitPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.Contains('\\') || value.Contains('\0') || value.Contains('\r') || value.Contains('\n') ||
            value.Split('/').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Git returned an unsafe package-input path.");
        return value;
    }

    private static void ValidateObjectId(string value)
    {
        if (value.Length is < 40 or > 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Git returned an invalid package-input object identity.");
    }

    private static string Digest(IReadOnlyDictionary<string, string> entries)
    {
        var canonical = new StringBuilder(entries.Count * 96);
        foreach (var (path, identity) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            canonical.Append(path).Append('\0').Append(identity.ToLowerInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static bool Sha256(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static GitResult RunGit(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        string? standardInput = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = Path.GetFullPath(repositoryRoot),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(Path.GetFullPath(repositoryRoot));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException(
                                "Git did not start for package freshness validation.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }
        if (!process.WaitForExit(10_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the bounded wait and the owned cleanup attempt.
            }
            throw new TimeoutException("Git package freshness validation exceeded its bounded deadline.");
        }
        Task.WaitAll(output, error);
        return new GitResult(process.ExitCode, output.Result);
    }

    private sealed record GitResult(int ExitCode, string Output);

    private sealed record DevelopmentPackageIntegrityManifest
    {
        public int SchemaVersion { get; init; }
        public string GitSha { get; init; } = "";
        public string PackageSourceKind { get; init; } = "";
        public bool PackageInputsMatchHead { get; init; }
        public int PackageInputFileCount { get; init; }
        public string PackageInputHeadSha256 { get; init; } = "";
        public string PackageInputWorktreeSha256 { get; init; } = "";
        public string PackageInputIndexSha256 { get; init; } = "";
        public IReadOnlyList<DevelopmentPackageIntegrityFile> Files { get; init; } = [];
    }

    private sealed record DevelopmentPackageIntegrityFile
    {
        public string RelativePath { get; init; } = "";
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = "";
    }
}

internal sealed record CertificationPackageInputProof
{
    public string HeadSha256 { get; init; } = "";
    public string WorktreeSha256 { get; init; } = "";
    public string IndexSha256 { get; init; } = "";
    public int FileCount { get; init; }
    public bool MatchesHead { get; init; }
}
