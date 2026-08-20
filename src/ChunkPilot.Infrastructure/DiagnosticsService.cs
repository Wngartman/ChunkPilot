using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class DiagnosticsService
{
    private readonly AppDataPaths paths;
    private readonly JarInventoryService jars;

    public DiagnosticsService(AppDataPaths paths, JarInventoryService jars)
    {
        this.paths = paths;
        this.jars = jars;
    }

    public async Task<IReadOnlyList<DiagnosticFinding>> AnalyzeAsync(
        ServerDefinition server,
        CancellationToken cancellationToken = default)
    {
        var results = new List<DiagnosticFinding>();
        results.Add(CheckPath("server.path", "Server folder", server.RootPath, directory: true));
        results.Add(CheckPath("launch.executable", "Launch executable", server.Executable, directory: false));
        results.Add(CheckPath("launch.working", "Working directory", server.WorkingDirectory, directory: true));

        var eula = Path.Combine(server.RootPath, "eula.txt");
        if (File.Exists(eula) && File.ReadAllText(eula).Contains("eula=false", StringComparison.OrdinalIgnoreCase))
            results.Add(Finding("eula.rejected", FindingSeverity.Error, "EULA is not accepted",
                "eula.txt contains eula=false.", "The server will stop until the user reviews and accepts the Minecraft EULA.", eula, 100));

        results.Add(await CheckPortAsync(server.Port, cancellationToken).ConfigureAwait(false));
        results.Add(CheckDisk(server.RootPath));
        results.AddRange(CheckRecentLogs(server.RootPath));

        var inventory = jars.Inventory(server);
        foreach (var duplicate in inventory.Where(entry => entry.DuplicateId).GroupBy(entry => entry.Id, StringComparer.OrdinalIgnoreCase))
            results.Add(Finding("jar.duplicate", FindingSeverity.Warning, $"Duplicate mod/plugin ID: {duplicate.Key}",
                string.Join(", ", duplicate.Select(item => item.FileName)),
                "Multiple enabled jars claim the same ID and may prevent startup.", server.RootPath, 90));

        if (server.Executable.EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
            server.Arguments.Contains(" start ", StringComparison.OrdinalIgnoreCase))
            results.Add(Finding("console.detached", FindingSeverity.Warning, "Launch may detach the server console",
                server.Arguments, "A START command can detach Java from captured stdin/stdout.",
                server.Executable, 75));
        return results;
    }

    /// <summary>
    /// Reads the bounded tail of the live console, latest logs, and newest crash report, then ranks
    /// known fixes. Files are opened read/shared so diagnosis never blocks or changes the server.
    /// </summary>
    public async Task<TroubleshootingReport> TroubleshootAsync(
        ServerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var evidence = new StringBuilder(snapshot.LastError);
        foreach (var line in snapshot.Console.TakeLast(500))
            evidence.AppendLine().Append(line.Text);

        var latest = await ReadTailAsync(Path.Combine(snapshot.Definition.RootPath, "logs", "latest.log"),
            2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        if (latest.Length > 0)
            evidence.AppendLine().Append(latest);

        // Prefer the live failure and current latest.log. Older crash reports are a fallback,
        // never allowed to outrank a more recent exact signature.
        var current = TroubleshootingService.Analyze(evidence.ToString(), snapshot.Definition.Port);
        if (current.HasLikelyFix)
            return current;

        var candidates = new[] { Path.Combine(snapshot.Definition.RootPath, "logs", "debug.log") }
            .Concat(NewestCrashReports(snapshot.Definition.RootPath, 2));

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tail = await ReadTailAsync(path, 2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            if (tail.Length > 0)
                evidence.AppendLine().Append(tail);
        }

        return TroubleshootingService.Analyze(evidence.ToString(), snapshot.Definition.Port);
    }

    public async Task<string> CreateDiagnosticBundleAsync(
        ServerDefinition server,
        IReadOnlyList<ActivityEntry> activity,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.DiagnosticBundles);
        var target = Path.Combine(paths.DiagnosticBundles,
            $"{Sanitize(server.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        await using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
        await AddTextAsync(archive, "launch.txt", BuildLaunchSummary(server), cancellationToken).ConfigureAwait(false);
        await AddTextAsync(archive, "activity.json",
            System.Text.Json.JsonSerializer.Serialize(activity, new System.Text.Json.JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        await AddTextAsync(archive, "inventory.json",
            System.Text.Json.JsonSerializer.Serialize(jars.Inventory(server), new System.Text.Json.JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);

        foreach (var file in DiagnosticFiles(server.RootPath).Take(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (info.Length > 10 * 1024 * 1024)
                continue;
            var entry = archive.CreateEntry("server/" + Path.GetRelativePath(server.RootPath, file).Replace('\\', '/'), CompressionLevel.Optimal);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, FileOptions.Asynchronous);
            await using var output = entry.Open();
            using var reader = new StreamReader(input, detectEncodingFromByteOrderMarks: true);
            using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
            var redacted = SecretRedactor.Redact(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
            await writer.WriteAsync(redacted.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        return target;
    }

    private static DiagnosticFinding CheckPath(string code, string label, string path, bool directory)
    {
        var exists = directory ? Directory.Exists(path) : File.Exists(path);
        return exists
            ? Finding(code, FindingSeverity.Pass, $"{label} exists", path, "", path, 100)
            : Finding(code, FindingSeverity.Error, $"{label} is missing", path,
                "The launch profile points to a path that does not exist.", path, 100);
    }

    private static async Task<DiagnosticFinding> CheckPortAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            await Task.CompletedTask.ConfigureAwait(false);
            return Finding("port.available", FindingSeverity.Pass, $"Port {port} is available",
                "A local bind test succeeded.", "", "", 95);
        }
        catch (SocketException exception)
        {
            return Finding("port.conflict", FindingSeverity.Warning, $"Port {port} is already in use",
                exception.Message, "Another server or application may own the configured port.", "", 90);
        }
        finally
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static DiagnosticFinding CheckDisk(string path)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            var low = drive.AvailableFreeSpace < 5L * 1024 * 1024 * 1024 ||
                      drive.AvailableFreeSpace < drive.TotalSize * 0.05;
            return Finding("disk.free", low ? FindingSeverity.Warning : FindingSeverity.Pass,
                low ? "Disk space is low" : "Disk space is adequate",
                $"{drive.AvailableFreeSpace / 1024d / 1024 / 1024:F1} GiB free on {drive.Name}",
                low ? "Backups, logs, or world saves may fail if free space is exhausted." : "", drive.Name, 100);
        }
        catch (IOException exception)
        {
            return Finding("disk.unavailable", FindingSeverity.Unavailable, "Disk space is unavailable",
                exception.Message, "", path, 70);
        }
    }

    private static IEnumerable<DiagnosticFinding> CheckRecentLogs(string root)
    {
        var latest = Path.Combine(root, "logs", "latest.log");
        if (!File.Exists(latest))
            yield break;
        string text;
        try
        {
            using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length > 2 * 1024 * 1024)
                stream.Seek(-2 * 1024 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            yield break;
        }
        foreach (var pattern in LogPatterns())
        {
            if (pattern.Regex.IsMatch(text))
                yield return Finding(pattern.Code, pattern.Severity, pattern.Title,
                    pattern.Regex.Match(text).Value, pattern.Cause, latest, pattern.Confidence);
        }
    }

    private static IEnumerable<string> DiagnosticFiles(string root)
    {
        foreach (var relative in new[] { "logs/latest.log", "logs/debug.log", "server.properties", "eula.txt" })
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
                yield return path;
        }
        foreach (var directoryName in new[] { "crash-reports" })
        {
            var directory = Path.Combine(root, directoryName);
            if (!Directory.Exists(directory))
                continue;
            foreach (var file in Directory.EnumerateFiles(directory).OrderByDescending(File.GetLastWriteTimeUtc).Take(10))
                yield return file;
        }
        foreach (var file in Directory.EnumerateFiles(root, "hs_err_pid*.log").Take(10))
            yield return file;
    }

    private static IEnumerable<string> NewestCrashReports(string root, int count)
    {
        var directory = Path.Combine(root, "crash-reports");
        if (!Directory.Exists(directory))
            return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*.txt")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(count)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task<string> ReadTailAsync(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return "";
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > maximumBytes)
                stream.Seek(-maximumBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static string BuildLaunchSummary(ServerDefinition server) => SecretRedactor.Redact($"""
        Name: {server.Name}
        Root: {server.RootPath}
        Ecosystem: {server.Ecosystem}
        Minecraft: {server.MinecraftVersion}
        Working directory: {server.WorkingDirectory}
        Executable: {server.Executable}
        Arguments: {server.Arguments}
        Environment:
        {string.Join(Environment.NewLine, SecretRedactor.RedactEnvironment(server.Environment).Select(pair => $"{pair.Key}={pair.Value}"))}
        """);

    private static async Task AddTextAsync(ZipArchive archive, string name, string text, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: false);
        await writer.WriteAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static DiagnosticFinding Finding(
        string code, FindingSeverity severity, string title, string evidence,
        string cause, string path, int confidence) =>
        new()
        {
            Code = code,
            Severity = severity,
            Title = title,
            Evidence = SecretRedactor.Redact(evidence),
            LikelyCause = cause,
            SuggestedAction = SuggestedAction(code),
            RelevantPath = path
        };

    private static string SuggestedAction(string code) => code switch
    {
        "port.conflict" => "Stop the conflicting process or configure a different server-port.",
        "disk.free" => "Free disk space or move backups to another local drive.",
        "java.oom" => "Review -Xmx, available host RAM, and the crash report before increasing memory.",
        "mixin.failure" => "Check the named mod and its required Minecraft/loader versions.",
        "dependency.missing" => "Install the exact missing dependency version required by this modpack.",
        "eula.rejected" => "Review Mojang's EULA and edit eula.txt manually if accepted.",
        _ => "Review the cited evidence and relevant file before changing the server."
    };

    private static IReadOnlyList<LogPattern> LogPatterns() =>
    [
        new("java.oom", OutOfMemoryRegex(), FindingSeverity.Error, "Java ran out of memory",
            "The JVM reported OutOfMemoryError; the cause may be heap sizing, native memory pressure, or a leak.", 100),
        new("mixin.failure", MixinRegex(), FindingSeverity.Error, "Mixin transformation failed",
            "A mod mixin could not apply, often due to a version mismatch or conflict.", 95),
        new("dependency.missing", MissingDependencyRegex(), FindingSeverity.Error, "A mod dependency appears to be missing",
            "The loader reported a missing or incompatible dependency.", 90),
        new("world.load", WorldLoadRegex(), FindingSeverity.Error, "World loading failed",
            "The log contains evidence that the configured world could not load.", 85)
    ];

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }

    private sealed record LogPattern(
        string Code, Regex Regex, FindingSeverity Severity, string Title, string Cause, int Confidence);

    [GeneratedRegex(@"OutOfMemoryError[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex OutOfMemoryRegex();

    [GeneratedRegex(@"(?:Mixin apply failed|InvalidMixinException|MixinTransformerError)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MixinRegex();

    [GeneratedRegex(@"(?:requires|depends on).{0,120}(?:missing|not found|incompatible)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex MissingDependencyRegex();

    [GeneratedRegex(@"(?:Failed to load world|Exception loading level|world.*corrupt)[^\r\n]*", RegexOptions.IgnoreCase)]
    private static partial Regex WorldLoadRegex();
}
