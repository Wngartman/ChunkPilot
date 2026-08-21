using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ChunkPilot.App;

internal sealed record BuildIdentity(
    string ProductVersion,
    string ReleaseTag,
    string GitSha,
    string BuildTimestampUtc,
    string SchemaVersion,
    string Architecture,
    string DefaultUi)
{
    internal static BuildIdentity Current { get; } = Read();

    internal static bool IsVersionRequest(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.Equals("--version", StringComparison.OrdinalIgnoreCase));

    internal static void WriteToParentConsole()
    {
        var text = $"ChunkPilot {Current.ProductVersion}\r\n" +
                   $"Release tag: {Current.ReleaseTag}\r\n" +
                   $"Git SHA: {Current.GitSha}\r\n" +
                   $"Built: {Current.BuildTimestampUtc}\r\n" +
                   $"Schema: {Current.SchemaVersion}\r\n" +
                   $"Architecture: {Current.Architecture}\r\n" +
                   $"Default UI: {Current.DefaultUi}\r\n";
        var handle = GetStdHandle(StandardOutputHandle);
        if (handle is 0 or -1)
        {
            _ = AttachConsole(AttachParentProcess);
            handle = GetStdHandle(StandardOutputHandle);
        }
        if (handle is 0 or -1)
            return;
        var bytes = Encoding.UTF8.GetBytes(text);
        _ = WriteFile(handle, bytes, (uint)bytes.Length, out _, 0);
    }

    private static BuildIdentity Read()
    {
        var assembly = typeof(BuildIdentity).Assembly;
        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(item => !string.IsNullOrWhiteSpace(item.Key))
            .ToDictionary(item => item.Key, item => item.Value ?? "unknown", StringComparer.Ordinal);
        string Get(string key) => metadata.TryGetValue(key, out var value) ? value : "unknown";
        var productVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString(3) ?? "unknown";
        return new BuildIdentity(productVersion, Get("ChunkPilotReleaseTag"), Get("ChunkPilotGitSha"),
            Get("ChunkPilotBuildTimestampUtc"), Get("ChunkPilotSchemaVersion"),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), Get("ChunkPilotDefaultUi"));
    }

    private const uint AttachParentProcess = 0xffffffff;
    private const int StandardOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(nint file, byte[] buffer, uint bytesToWrite,
        out uint bytesWritten, nint overlapped);
}
