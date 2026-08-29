using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChunkPilot.Certification;

internal sealed record CertificationRuntimeOwnershipMarker
{
    public const int CurrentSchemaVersion = 1;
    public const string ExpectedPurpose = "ChunkPilot CurseForge runtime certification";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Purpose { get; init; } = ExpectedPurpose;
    public string RepositoryFingerprint { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Proves that every mutable certification path belongs to this worktree and refuses shared or
/// redirected runtime roots. The marker deliberately stores only a one-way repository fingerprint.
/// </summary>
internal static class OwnedCertificationRuntime
{
    public const string MarkerFileName = ".chunkpilot-curseforge-certification-root.json";
    public const string LeaseFileName = ".chunkpilot-curseforge-certification.lock";

    public static string Prepare(string repositoryRoot, string runtimeRoot)
    {
        var repository = CanonicalDirectory(repositoryRoot);
        var artifacts = Path.GetFullPath(Path.Combine(repository, "artifacts"));
        var runtime = Path.GetFullPath(runtimeRoot);
        if (!IsStrictDescendant(runtime, artifacts))
            throw new InvalidOperationException(
                "The certification runtime root must be a scoped directory under this worktree's artifacts directory.");

        RejectReparsePoint(repository);
        Directory.CreateDirectory(artifacts);
        RejectExistingReparsePoints(repository, artifacts);
        RejectExistingReparsePoints(artifacts, runtime);
        var markerPath = Path.Combine(runtime, MarkerFileName);
        if (Directory.Exists(runtime) && !File.Exists(markerPath) &&
            Directory.EnumerateFileSystemEntries(runtime).Any())
            throw new InvalidOperationException(
                "A nonempty unmarked certification runtime root is not owned and was refused.");
        Directory.CreateDirectory(runtime);
        RejectExistingReparsePoints(artifacts, runtime);

        if (File.Exists(markerPath) &&
            (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The certification ownership marker cannot be a reparse point.");

        var expectedFingerprint = RepositoryFingerprint(repository);
        if (!File.Exists(markerPath))
        {
            var marker = new CertificationRuntimeOwnershipMarker
            {
                RepositoryFingerprint = expectedFingerprint
            };
            try
            {
                using var stream = new FileStream(
                    markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4 * 1024,
                    FileOptions.WriteThrough);
                JsonSerializer.Serialize(stream, marker,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
                stream.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                // A racing controller may have created the same marker. Exact validation below is
                // still required before this process may use the directory.
            }
        }

        CertificationRuntimeOwnershipMarker existing;
        try
        {
            using var stream = new FileStream(
                markerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4 * 1024,
                FileOptions.SequentialScan);
            existing = JsonSerializer.Deserialize<CertificationRuntimeOwnershipMarker>(
                           stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                       ?? throw new InvalidDataException("The certification ownership marker is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The certification ownership marker is malformed.", exception);
        }

        if (existing.SchemaVersion != CertificationRuntimeOwnershipMarker.CurrentSchemaVersion ||
            !existing.Purpose.Equals(
                CertificationRuntimeOwnershipMarker.ExpectedPurpose, StringComparison.Ordinal) ||
            !existing.RepositoryFingerprint.Equals(expectedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The certification runtime root is not owned by this exact ChunkPilot worktree.");
        return runtime;
    }

    public static FileStream AcquireExclusiveLease(string repositoryRoot, string runtimeRoot)
    {
        var runtime = Prepare(repositoryRoot, runtimeRoot);
        var leasePath = Path.Combine(runtime, LeaseFileName);
        if (Directory.Exists(leasePath) ||
            File.Exists(leasePath) &&
            (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "The exact certification runtime lease path is unsafe.");
        try
        {
            return new FileStream(
                leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "Another controller owns this exact certification runtime root; concurrent execution is refused.",
                exception);
        }
    }

    public static string RequireOwnedDescendant(string runtimeRoot, string candidate, string description)
    {
        var runtime = Path.GetFullPath(runtimeRoot);
        var path = Path.GetFullPath(candidate);
        if (!IsStrictDescendant(path, runtime))
            throw new InvalidOperationException(
                $"The {description} must remain inside the owned certification runtime root.");
        RejectExistingReparsePoints(runtime, path);
        return path;
    }

    internal static bool IsSameOrDescendant(string candidate, string parent)
    {
        var candidateFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return candidateFull.Equals(parentFull, StringComparison.OrdinalIgnoreCase) ||
               IsStrictDescendant(candidateFull, parentFull);
    }

    internal static string RepositoryFingerprint(string repositoryRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot)).ToLowerInvariant())))
            .ToLowerInvariant();

    private static string CanonicalDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException("The current ChunkPilot repository root is unavailable.");
        return Path.TrimEndingDirectorySeparator(new DirectoryInfo(full).FullName);
    }

    private static bool IsStrictDescendant(string candidate, string parent)
    {
        var parentPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) +
                           Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(parentPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectExistingReparsePoints(string parent, string candidate)
    {
        var parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        var candidateFull = Path.GetFullPath(candidate);
        if (!candidateFull.Equals(parentFull, StringComparison.OrdinalIgnoreCase) &&
            !IsStrictDescendant(candidateFull, parentFull))
            throw new InvalidOperationException("The certification path escaped its proven parent directory.");

        var relative = Path.GetRelativePath(parentFull, candidateFull);
        var current = parentFull;
        RejectReparsePoint(current);
        if (relative.Equals(".", StringComparison.Ordinal))
            return;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current) && !File.Exists(current))
                break;
            RejectReparsePoint(current);
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException(
                "Certification runtime paths cannot traverse junctions, symbolic links, or other reparse points.");
    }
}
