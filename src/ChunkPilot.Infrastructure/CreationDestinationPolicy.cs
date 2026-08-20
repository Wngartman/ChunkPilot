using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// The one canonical-path vocabulary used by server creation.
/// </summary>
/// <remarks>
/// Deliberately not a second path-safety subsystem: it is the shared implementation that
/// <see cref="ManagedServerInstaller"/> and <see cref="CreationDestinationPolicy"/> both call, so
/// "is this path inside that one" is answered the same way everywhere. Windows path comparison is
/// case-insensitive, and a trailing separator never changes which directory is meant.
/// </remarks>
public static class CreationPathSafety
{
    /// <summary>
    /// Full path with separators normalised, relative segments resolved and any trailing separator
    /// removed, so two spellings of the same directory compare equal.
    /// </summary>
    public static string Canonical(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // A volume root ("C:\") loses meaning without its separator, so it keeps it.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>True when the two paths name the same directory.</summary>
    public static bool IsSamePath(string left, string right) =>
        Canonical(left).Equals(Canonical(right), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="candidate"/> is inside <paramref name="root"/>, but not equal to it.</summary>
    public static bool IsUnder(string root, string candidate)
    {
        var normalizedRoot = Canonical(root);
        var normalizedCandidate = Canonical(candidate);
        if (normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return false;
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the two paths are the same directory or one contains the other.</summary>
    public static bool Overlaps(string left, string right) =>
        IsSamePath(left, right) || IsUnder(left, right) || IsUnder(right, left);

    /// <summary>Throws when <paramref name="candidate"/> is not the root itself or inside it.</summary>
    public static void EnsureWithin(string root, string candidate)
    {
        if (!IsSamePath(root, candidate) && !IsUnder(root, candidate))
            throw new InvalidDataException($"Path escapes its allowed root: {Canonical(candidate)}");
    }

    /// <summary>
    /// True when the path exists and is a junction, symlink or other reparse point.
    /// </summary>
    /// <remarks>
    /// A reparse point makes "where does this write actually land" a question ChunkPilot cannot
    /// answer, so creation refuses one as a destination rather than following it.
    /// </remarks>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
                return false;
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If the attributes cannot be read, the path is not provably safe.
            return true;
        }
    }

    /// <summary>True when the directory exists and contains nothing at all.</summary>
    public static bool IsEmptyDirectory(string path) =>
        Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any();

    /// <summary>True when the two paths sit on the same volume, so a rename can be atomic.</summary>
    public static bool IsSameVolume(string left, string right)
    {
        var leftRoot = Path.GetPathRoot(Canonical(left));
        var rightRoot = Path.GetPathRoot(Canonical(right));
        return !string.IsNullOrEmpty(leftRoot) &&
               leftRoot.Equals(rightRoot, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Everything the destination policy needs in order to decide, gathered by the caller.</summary>
/// <param name="OperationId">The operation asking. Its own journal entry never blocks it.</param>
/// <param name="RequestedDestination">The path the server would occupy.</param>
/// <param name="StagingPath">The operation-owned staging directory.</param>
/// <param name="KnownServers">Every server ChunkPilot has registered, managed and imported alike.</param>
/// <param name="ActiveOperations">Creation journal entries that have not been finalised.</param>
public sealed record CreationDestinationQuery(
    Guid OperationId,
    string RequestedDestination,
    string StagingPath,
    IReadOnlyList<ServerDefinition> KnownServers,
    IReadOnlyList<CreationJournalEntry> ActiveOperations);

/// <summary>
/// The single deterministic answer to "may a new managed server be created here?".
/// </summary>
/// <remarks>
/// <para>
/// One policy, one answer, and a message that says what is true rather than quoting a path at the
/// user. Nothing here mutates the filesystem: the caller decides what to do with the verdict, which
/// is what lets activation re-run exactly the same check immediately before it promotes anything.
/// </para>
/// <para>
/// The rules are ordered from most specific to least, so a path that is both inside a known server
/// and non-empty is reported as the former: the reason a user needs is the ownership, not the
/// file count.
/// </para>
/// </remarks>
public static class CreationDestinationPolicy
{
    private const string NothingChanged = "Nothing was changed.";

    public static CreationDestinationDecision Evaluate(CreationDestinationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        string destination;
        try
        {
            destination = CreationPathSafety.Canonical(query.RequestedDestination);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CreationDestinationDecision(
                CreationDestinationVerdict.BlockedUnsafePath,
                query.RequestedDestination ?? "",
                $"That folder path cannot be used: {exception.Message} {NothingChanged}",
                DestinationExisted: false);
        }

        var staging = CreationPathSafety.Canonical(query.StagingPath);
        var existed = Directory.Exists(destination);

        // 1. The destination must not overlap its own staging directory in either direction.
        if (CreationPathSafety.Overlaps(destination, staging))
            return Blocked(CreationDestinationVerdict.BlockedUnsafePath, destination, existed,
                "The server folder and ChunkPilot's temporary working folder would overlap, which would "
                + $"make the installation impossible to undo safely. {NothingChanged}");

        // 2. A file sitting on the path is never silently replaced.
        if (File.Exists(destination))
            return Blocked(CreationDestinationVerdict.BlockedFileExists, destination, existed,
                $"A file already uses that name, so a server folder cannot be created there. {NothingChanged} "
                + "Choose another name or another folder.");

        // 3. A junction or symlink hides where writes really land.
        if (CreationPathSafety.IsReparsePoint(destination))
            return Blocked(CreationDestinationVerdict.BlockedReparsePoint, destination, existed,
                "That folder is a shortcut to somewhere else (a junction or symbolic link), so ChunkPilot "
                + $"cannot tell where the files would really go. {NothingChanged} Choose a normal folder.");

        // 4. Ownership by a server ChunkPilot already knows about.
        foreach (var server in query.KnownServers)
        {
            if (string.IsNullOrWhiteSpace(server.RootPath))
                continue;
            string root;
            try
            {
                root = CreationPathSafety.Canonical(server.RootPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (root.Equals(destination, StringComparison.OrdinalIgnoreCase))
                return server.IsManaged
                    ? Blocked(CreationDestinationVerdict.BlockedManagedServer, destination, existed,
                        $"\"{server.Name}\" already uses that folder. {NothingChanged} "
                        + "Choose a different name, or remove that server first.")
                    : Blocked(CreationDestinationVerdict.BlockedImportedServer, destination, existed,
                        $"\"{server.Name}\" was added from that folder and ChunkPilot only reads it. "
                        + $"{NothingChanged} Creating a server never takes over a folder you already had.");

            if (CreationPathSafety.IsUnder(root, destination))
                return Blocked(CreationDestinationVerdict.BlockedInsideKnownServer, destination, existed,
                    $"That folder is inside \"{server.Name}\", which ChunkPilot already manages. "
                    + $"{NothingChanged} Servers are never nested inside one another.");

            if (CreationPathSafety.IsUnder(destination, root))
                return Blocked(CreationDestinationVerdict.BlockedContainsKnownServer, destination, existed,
                    $"That folder contains \"{server.Name}\", which ChunkPilot already knows about. "
                    + $"{NothingChanged} Choose a folder that does not contain an existing server.");
        }

        // 5. Another creation operation already owns the path, whether running or awaiting recovery.
        foreach (var operation in query.ActiveOperations)
        {
            if (operation.OperationId == query.OperationId ||
                string.IsNullOrWhiteSpace(operation.CanonicalDestination))
                continue;
            if (CreationPathSafety.IsSamePath(operation.CanonicalDestination, destination))
                return Blocked(CreationDestinationVerdict.BlockedActiveOperation, destination, existed,
                    "Another server is already being created in that folder, or a previous attempt there "
                    + $"has not finished. {NothingChanged} Wait for it to finish, or choose another name.");
        }

        // 6. Existing contents are never merged into.
        if (existed)
            return CreationPathSafety.IsEmptyDirectory(destination)
                ? new CreationDestinationDecision(CreationDestinationVerdict.AvailableEmpty, destination,
                    "That folder already exists and is empty, so it can be used.", DestinationExisted: true)
                : Blocked(CreationDestinationVerdict.BlockedNotEmpty, destination, existed,
                    $"That folder already contains files. {NothingChanged} Choose another folder, or use "
                    + "\"Add an existing server\" if the files there are already a server.");

        return new CreationDestinationDecision(CreationDestinationVerdict.Available, destination,
            "That folder is free.", DestinationExisted: false);
    }

    private static CreationDestinationDecision Blocked(
        CreationDestinationVerdict verdict,
        string destination,
        bool existed,
        string message) =>
        new(verdict, destination, message, existed);
}

/// <summary>
/// Raised when creation cannot start or continue because the destination policy refused the path.
/// </summary>
/// <remarks>
/// Carries the decision so callers can report the policy's own wording rather than inventing their
/// own, and so a caller can distinguish a refusal from a genuine I/O failure.
/// </remarks>
public sealed class CreationDestinationBlockedException : InvalidOperationException
{
    public CreationDestinationBlockedException(CreationDestinationDecision decision)
        : base(decision?.Message ?? "The destination folder cannot be used.") =>
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));

    public CreationDestinationBlockedException()
        : this(new CreationDestinationDecision(
            CreationDestinationVerdict.BlockedUnsafePath, "", "The destination folder cannot be used.", false))
    {
    }

    public CreationDestinationBlockedException(string message)
        : this(new CreationDestinationDecision(CreationDestinationVerdict.BlockedUnsafePath, "", message, false))
    {
    }

    public CreationDestinationBlockedException(string message, Exception innerException)
        : base(message, innerException) =>
        Decision = new CreationDestinationDecision(
            CreationDestinationVerdict.BlockedUnsafePath, "", message, false);

    public CreationDestinationDecision Decision { get; }
}
