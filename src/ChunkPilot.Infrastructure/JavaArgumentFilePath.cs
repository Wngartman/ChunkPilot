using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Adapts an already-authorized absolute argument-file path for Java's native Windows launcher.
/// This is not a containment or ownership check: callers must establish those before using it.
/// Unlike Java's filesystem APIs, its launcher cannot open a normal @path at MAX_PATH.
/// </summary>
internal static class JavaArgumentFilePath
{
    internal static string ForLauncher(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Any(char.IsControl) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("A Java argument file requires an absolute regular filesystem path.", nameof(path));
        if (OperatingSystem.IsWindows() &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
             path.StartsWith(@"\\.\", StringComparison.Ordinal)))
            throw new ArgumentException("Use the canonical filesystem identity, not a device path.", nameof(path));

        var canonical = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows() || canonical.Length < 260)
            return canonical;
        return canonical.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + canonical[2..]
            : @"\\?\" + canonical;
    }

    internal static string LaunchArguments(string path) =>
        $"@{CommandLineQuoter.QuoteWindowsArgument(ForLauncher(path))} nogui";
}
