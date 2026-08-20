namespace ChunkPilot.Infrastructure;

public enum ModrinthPackEnvironmentSupport
{
    Required,
    Optional,
    Unsupported
}

public enum ModrinthPackSourceLayer
{
    ManifestFile,
    CommonOverride,
    ServerOverride
}

public enum ModrinthPackDownloadOrigin
{
    ModrinthCdn,
    GitHub,
    GitHubRaw,
    GitLab
}

public sealed record ModrinthPackHashes
{
    public string Sha1 { get; init; } = "";
    public string Sha512 { get; init; } = "";
}

public sealed record ModrinthPackDownload
{
    public required Uri Uri { get; init; }
    public ModrinthPackDownloadOrigin Origin { get; init; }
}

public sealed record ModrinthPackFile
{
    public string RelativePath { get; init; } = "";
    public long FileSize { get; init; }
    public ModrinthPackHashes Hashes { get; init; } = new();
    public IReadOnlyList<ModrinthPackDownload> Downloads { get; init; } = [];
    public ModrinthPackEnvironmentSupport ClientEnvironment { get; init; } =
        ModrinthPackEnvironmentSupport.Required;
    public ModrinthPackEnvironmentSupport ServerEnvironment { get; init; } =
        ModrinthPackEnvironmentSupport.Required;

    public bool ShouldInstallOnServer(bool includeOptional) => ServerEnvironment switch
    {
        ModrinthPackEnvironmentSupport.Required => true,
        ModrinthPackEnvironmentSupport.Optional => includeOptional,
        ModrinthPackEnvironmentSupport.Unsupported => false,
        _ => false
    };
}

public sealed record ModrinthPackOverrideEntry
{
    public string ArchivePath { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public ModrinthPackSourceLayer Layer { get; init; }
    public long FileSize { get; init; }
}

public sealed record ModrinthPackManifest
{
    public int FormatVersion { get; init; }
    public string Game { get; init; } = "";
    public string VersionId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
    public IReadOnlyList<ModrinthPackFile> Files { get; init; } = [];
}

/// <summary>
/// A validated archive description. VersionId belongs to the pack index and is not a Modrinth API
/// project or version identity; provider provenance must be recorded separately by the caller.
/// </summary>
public sealed record ModrinthPackArchive
{
    public string ArchivePath { get; init; } = "";
    public ModrinthPackManifest Manifest { get; init; } = new();
    public IReadOnlyList<ModrinthPackOverrideEntry> CommonOverrides { get; init; } = [];
    public IReadOnlyList<ModrinthPackOverrideEntry> ServerOverrides { get; init; } = [];
}

public sealed record ModrinthPackLimits
{
    public long MaximumArchiveBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public int MaximumArchiveEntries { get; init; } = 100_000;
    public long MaximumManifestBytes { get; init; } = 4L * 1024 * 1024;
    public int MaximumManifestFiles { get; init; } = 100_000;
    public int MaximumDownloadsPerFile { get; init; } = 8;
    public long MaximumIndexedBytes { get; init; } = 100L * 1024 * 1024 * 1024;
    public int MaximumOverrideFiles { get; init; } = 50_000;
    public long MaximumOverrideFileBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaximumOverrideBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public double MaximumCompressionRatio { get; init; } = 1_000;
    public int MaximumRelativePathCharacters { get; init; } = 1_024;
    public int MaximumPathDepth { get; init; } = 64;

    internal void Validate()
    {
        if (MaximumArchiveBytes <= 0 || MaximumArchiveEntries <= 0 || MaximumManifestBytes <= 0 ||
            MaximumManifestFiles <= 0 || MaximumDownloadsPerFile <= 0 || MaximumIndexedBytes <= 0 ||
            MaximumOverrideFiles <= 0 || MaximumOverrideFileBytes <= 0 || MaximumOverrideBytes <= 0 ||
            MaximumCompressionRatio <= 0 || MaximumRelativePathCharacters <= 0 || MaximumPathDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(ModrinthPackLimits), "All pack safety limits must be positive.");
    }
}

public sealed record ModrinthPackMaterializationOptions
{
    public bool IncludeOptionalServerFiles { get; init; }
    public int MaximumConcurrentDownloads { get; init; } = 4;
}

public sealed record ModrinthMaterializedFile
{
    public string RelativePath { get; init; } = "";
    public long FileSize { get; init; }
    public string Sha1 { get; init; } = "";
    public string Sha512 { get; init; } = "";
    public ModrinthPackSourceLayer SourceLayer { get; init; }
}

public sealed record ModrinthPackMaterializationResult
{
    public string DestinationRoot { get; init; } = "";
    public ModrinthPackManifest Manifest { get; init; } = new();
    public IReadOnlyList<ModrinthMaterializedFile> Files { get; init; } = [];
    public IReadOnlyList<string> SkippedOptionalFiles { get; init; } = [];
    public IReadOnlyList<string> SkippedUnsupportedFiles { get; init; } = [];
}

public interface IModrinthPackDownloadSource
{
    Task<Stream> OpenReadAsync(Uri uri, CancellationToken cancellationToken = default);
}
