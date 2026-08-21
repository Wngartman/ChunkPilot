namespace ChunkPilot.Core;

/// <summary>How a user supplied the initial world for a newly managed server.</summary>
public enum CreationWorldSourceKind
{
    Folder,
    ZipArchive
}

/// <summary>
/// Read-only evidence for a user-selected world. The source is copied into the managed creation
/// transaction; it is never moved, renamed, or modified.
/// </summary>
public sealed record CreationWorldSource
{
    public CreationWorldSourceKind Kind { get; init; }
    public string NativePath { get; init; } = "";
    public string WorldName { get; init; } = "";
    public string MainWorldRelativePath { get; init; } = ".";
    public string NetherWorldRelativePath { get; init; } = "";
    public string EndWorldRelativePath { get; init; } = "";
    public string SourceFingerprint { get; init; } = "";
    public long SourceSizeBytes { get; init; }
    public long ExpandedSizeBytes { get; init; }
    public int FileCount { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(NativePath))
            problems.Add("No existing world source was selected.");
        if (string.IsNullOrWhiteSpace(WorldName) || WorldName is "." or ".." ||
            WorldName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            problems.Add("The selected world does not have a safe Windows folder name.");
        if (!IsSafeRelativePath(MainWorldRelativePath))
            problems.Add("The selected main world path is unsafe.");
        if (!string.IsNullOrWhiteSpace(NetherWorldRelativePath) && !IsSafeRelativePath(NetherWorldRelativePath))
            problems.Add("The selected Nether world path is unsafe.");
        if (!string.IsNullOrWhiteSpace(EndWorldRelativePath) && !IsSafeRelativePath(EndWorldRelativePath))
            problems.Add("The selected End world path is unsafe.");
        if (SourceFingerprint.Length != 64 || SourceFingerprint.Any(character => !Uri.IsHexDigit(character)))
            problems.Add("The selected world identity is incomplete.");
        if (SourceSizeBytes <= 0 || ExpandedSizeBytes <= 0 || FileCount <= 0)
            problems.Add("The selected world contains no reviewed files.");
        return problems;
    }

    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            return false;
        return value.Replace('\\', '/').Split('/').All(segment => segment is not ".." && segment.Length > 0);
    }
}
