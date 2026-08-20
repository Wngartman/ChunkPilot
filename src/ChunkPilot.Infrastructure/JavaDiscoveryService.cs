using System.Diagnostics;
using System.Text.RegularExpressions;
using ChunkPilot.Core;
using Microsoft.Win32;

namespace ChunkPilot.Infrastructure;

public sealed partial class JavaDiscoveryService
{
    public async Task<IReadOnlyList<JavaRuntimeInfo>> DiscoverAsync(
        string serverRoot,
        IReadOnlyList<LaunchCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var match = JavaPathRegex().Match(candidate.Arguments);
            if (match.Success)
                Add(paths, match.Groups["path"].Value.Trim('"'), "Launch script/profile");
            if (candidate.Executable.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase))
                Add(paths, candidate.Executable, "Launch profile");
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            Add(paths, Path.Combine(javaHome, "bin", "java.exe"), "JAVA_HOME");

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var item in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Add(paths, Path.Combine(item, "java.exe"), "PATH");

        DiscoverRegistry(paths);
        DiscoverDirectory(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"), "Program Files");
        DiscoverDirectory(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"), "Eclipse Adoptium");
        DiscoverDirectory(paths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), "User programs");
        DiscoverBundled(paths, serverRoot);

        var results = new List<JavaRuntimeInfo>();
        foreach (var pair in paths.Take(30))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await InspectAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false));
        }
        return results.OrderByDescending(info => info.Exists).ThenBy(info => info.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Add(IDictionary<string, string> paths, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        try { paths.TryAdd(Path.GetFullPath(path), source); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
    }

    private static void DiscoverRegistry(IDictionary<string, string> paths)
    {
        if (!OperatingSystem.IsWindows())
            return;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                foreach (var keyName in new[]
                         {
                             @"SOFTWARE\JavaSoft\Java Runtime Environment",
                             @"SOFTWARE\JavaSoft\JDK",
                             @"SOFTWARE\Eclipse Adoptium\JDK"
                         })
                {
                    using var key = baseKey.OpenSubKey(keyName);
                    foreach (var versionName in key?.GetSubKeyNames() ?? [])
                    {
                        using var version = key?.OpenSubKey(versionName);
                        if (version?.GetValue("JavaHome") is string home)
                            Add(paths, Path.Combine(home, "bin", "java.exe"), $"Registry {hive}/{view}");
                        if (version?.GetValue("Path") is string path)
                            Add(paths, Path.Combine(path, "bin", "java.exe"), $"Registry {hive}/{view}");
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
        }
    }

    private static void DiscoverDirectory(IDictionary<string, string> paths, string root, string source)
    {
        if (!Directory.Exists(root))
            return;
        try
        {
            foreach (var java in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories).Take(20))
                Add(paths, java, source);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
    }

    private static void DiscoverBundled(IDictionary<string, string> paths, string root)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Dequeue();
            var java = Path.Combine(directory, "bin", "java.exe");
            if (File.Exists(java))
                Add(paths, java, "Bundled server/modpack runtime");
            if (depth >= 3)
                continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory).Take(100))
                {
                    var name = Path.GetFileName(child);
                    if (name.Contains("java", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("jre", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("jdk", StringComparison.OrdinalIgnoreCase) ||
                        depth == 0)
                        pending.Enqueue((child, depth + 1));
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
        }
    }

    private static async Task<JavaRuntimeInfo> InspectAsync(string path, string source, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new JavaRuntimeInfo { Path = path, Source = source, Exists = false, Compatibility = "Executable not found" };
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            var output = (await stderr.ConfigureAwait(false)) + "\n" + (await stdout.ConfigureAwait(false));
            var version = VersionOutputRegex().Match(output).Groups["version"].Value;
            var vendor = output.Contains("OpenJDK", StringComparison.OrdinalIgnoreCase) ? "OpenJDK-compatible" :
                output.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ? "Oracle" : "Unknown";
            var architecture = output.Contains("64-Bit", StringComparison.OrdinalIgnoreCase) ? "x64" :
                output.Contains("32-Bit", StringComparison.OrdinalIgnoreCase) ? "x86" : "Unknown";
            return new JavaRuntimeInfo
            {
                Path = path,
                Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version,
                Vendor = vendor,
                Architecture = architecture,
                Source = source,
                Exists = true,
                Compatibility = architecture == "x86" ? "32-bit Java is not recommended for modern or large servers." : "Compatibility depends on the server/modpack."
            };
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            return new JavaRuntimeInfo
            {
                Path = path,
                Source = source,
                Exists = true,
                Compatibility = $"Could not query version: {exception.Message}"
            };
        }
    }

    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:\\|\.{1,2}\\)[^""\s]*java(?:w)?\.exe|java(?:w)?(?:\.exe)?)", RegexOptions.IgnoreCase)]
    private static partial Regex JavaPathRegex();

    [GeneratedRegex(@"version\s+""(?<version>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex VersionOutputRegex();
}

