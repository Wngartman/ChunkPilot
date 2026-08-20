using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed partial class RamArgumentService
{
    private readonly SafeFileService files;

    public RamArgumentService(SafeFileService files) => this.files = files;

    public async Task<ServerDefinition> ApplyAsync(
        ServerDefinition server,
        int minimumMb,
        int maximumMb,
        CancellationToken cancellationToken = default)
    {
        if (MemoryAllocationPolicy.ValidatePair(minimumMb, maximumMb) is { } memoryProblem)
            throw new ArgumentException(memoryProblem);
        if (Path.GetFileName(server.Executable).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("javaw.exe is not supported because it breaks reliable console capture.");
        var jvmArgs = Path.Combine(server.RootPath, "user_jvm_args.txt");
        if (File.Exists(jvmArgs) && server.Arguments.Contains("@user_jvm_args.txt", StringComparison.OrdinalIgnoreCase))
        {
            var content = await files.ReadTextAsync(server.RootPath, "user_jvm_args.txt", cancellationToken).ConfigureAwait(false);
            var updated = UpdateArguments(content.Content, minimumMb, maximumMb, content.LineEnding);
            await files.WriteTextAtomicAsync(server.RootPath, content with { Content = updated },
                createRecoveryCopy: true, cancellationToken).ConfigureAwait(false);
            return server with
            {
                MinimumRamMb = minimumMb,
                MaximumRamMb = maximumMb,
                RamArgumentSource = "user_jvm_args.txt"
            };
        }
        if (Path.GetExtension(server.Executable) is ".bat" or ".cmd" ||
            Path.GetFileName(server.Executable).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The launch script may override JVM memory. Choose a supported user_jvm_args.txt or app-managed direct Java profile; ChunkPilot will not blindly rewrite a complex script.");
        var arguments = UpdateArguments(server.Arguments, minimumMb, maximumMb, " ");
        return server with
        {
            Arguments = arguments,
            MinimumRamMb = minimumMb,
            MaximumRamMb = maximumMb,
            RamArgumentSource = "Launch profile"
        };
    }

    public static string UpdateArguments(string arguments, int minimumMb, int maximumMb, string separator)
    {
        var result = XmsRegex().Replace(arguments, "");
        result = XmxRegex().Replace(result, "");
        result = MultiSpaceRegex().Replace(result, separator == " " ? " " : separator).Trim();
        var prefix = $"-Xms{minimumMb}M{separator}-Xmx{maximumMb}M";
        return result.Length == 0 ? prefix : $"{prefix}{separator}{result}";
    }

    [GeneratedRegex(@"(?<!\S)-Xms\d+[kKmMgG](?!\S)", RegexOptions.CultureInvariant)]
    private static partial Regex XmsRegex();

    [GeneratedRegex(@"(?<!\S)-Xmx\d+[kKmMgG](?!\S)", RegexOptions.CultureInvariant)]
    private static partial Regex XmxRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();
}
