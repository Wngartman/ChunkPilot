using System.Diagnostics;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Prevents managed or downloaded child code from receiving arbitrary variables from the process
/// that launched ChunkPilot. Server-specific variables may be applied explicitly after the inherited
/// baseline is reduced.
/// </summary>
public static class ChildProcessEnvironmentPolicy
{
    private static readonly HashSet<string> AllowedInheritedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "APPDATA",
        "CLASSPATH",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
        "ComSpec",
        "DOTNET_BUNDLE_EXTRACT_BASE_DIR",
        "HOMEDRIVE",
        "HOMEPATH",
        "JAVA_HOME",
        "LOCALAPPDATA",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "Path",
        "PATHEXT",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "SystemDrive",
        "SystemRoot",
        "TEMP",
        "TMP",
        "USERDOMAIN",
        "USERNAME",
        "USERPROFILE",
        "WINDIR"
    };

    public static void Apply(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? explicitEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (startInfo.UseShellExecute)
            throw new InvalidOperationException("A restricted child environment requires UseShellExecute=false.");

        var baseline = startInfo.Environment
            .Where(pair => AllowedInheritedNames.Contains(pair.Key))
            .ToArray();
        startInfo.Environment.Clear();
        foreach (var pair in baseline)
            startInfo.Environment[pair.Key] = pair.Value;

        if (explicitEnvironment is null) return;
        foreach (var pair in explicitEnvironment)
            startInfo.Environment[pair.Key] = pair.Value;
    }
}
