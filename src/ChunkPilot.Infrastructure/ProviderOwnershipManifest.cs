using System.Text;
using System.Text.Json;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

public sealed record ProviderOwnedFileBaseline(string RelativePath, string Sha256, long SizeBytes);

public sealed record ProviderOwnershipEvidence(
    int SchemaVersion,
    string Provider,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<ProviderOwnedFileBaseline> Files);

/// <summary>Local SHA-256 baselines used to distinguish unchanged pack files from user modifications.</summary>
public static class ProviderOwnershipManifest
{
    public const string RelativePath = ".chunkpilot/provider-owned-files.json";

    private static readonly HashSet<string> ExcludedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "eula.txt", "server.properties", "whitelist.json", "ops.json", "banned-players.json",
        "banned-ips.json", "server-icon.png"
    };

    public static async Task WriteAsync(
        string root,
        string provider,
        CancellationToken cancellationToken = default)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var entries = new List<ProviderOwnedFileBaseline>();
        foreach (var path in Directory.EnumerateFiles(fullRoot, "*", SearchOption.AllDirectories)
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Provider-owned candidate files cannot be links or reparse points.");
            var relative = PersistentDataClassifier.Normalize(Path.GetRelativePath(fullRoot, path));
            if (relative.StartsWith(".chunkpilot/", StringComparison.OrdinalIgnoreCase) ||
                ExcludedFiles.Contains(relative) || relative.StartsWith("world/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("world_nether/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("world_the_end/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("crash-reports/", StringComparison.OrdinalIgnoreCase))
                continue;
            entries.Add(new ProviderOwnedFileBaseline(relative,
                await PackMigrationPlanner.Sha256Async(path, cancellationToken).ConfigureAwait(false), info.Length));
        }
        var evidence = new ProviderOwnershipEvidence(1, provider, DateTimeOffset.UtcNow, entries);
        var destination = Path.Combine(fullRoot, RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partial = destination + $".{Guid.NewGuid():N}.partial";
        await File.WriteAllTextAsync(partial, JsonSerializer.Serialize(evidence, ProtocolJson.Options),
            new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(partial, destination, overwrite: true);
    }

    public static IReadOnlyDictionary<string, string> Read(string root)
    {
        var path = Path.Combine(Path.GetFullPath(root), RelativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return new Dictionary<string, string>();
            var evidence = JsonSerializer.Deserialize<ProviderOwnershipEvidence>(File.ReadAllText(path), ProtocolJson.Options);
            if (evidence is null || evidence.SchemaVersion != 1 || evidence.Files.Count > 200_000)
                return new Dictionary<string, string>();
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in evidence.Files)
            {
                var relative = PersistentDataClassifier.Normalize(file.RelativePath);
                if (relative.Length == 0 || relative.Contains("..", StringComparison.Ordinal) || file.Sha256.Length != 64 ||
                    !result.TryAdd(relative, file.Sha256))
                    return new Dictionary<string, string>();
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
