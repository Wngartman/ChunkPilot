using System.Diagnostics;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

/// <summary>
/// The rules that decide whether a new managed server may occupy a folder.
/// </summary>
/// <remarks>
/// Every case here is one the creation transaction relies on before it promotes anything, and the
/// same evaluation runs again immediately before promotion. A gap here is a folder that gets merged
/// into or taken over, which is why the awkward spellings - case, trailing separators, nesting - are
/// tested as first-class cases rather than assumed.
/// </remarks>
public sealed class CreationDestinationPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-destination-" + Guid.NewGuid().ToString("N"));

    public CreationDestinationPolicyTests() => Directory.CreateDirectory(root);

    // ---------------------------------------------------------------- canonical path vocabulary

    [Fact]
    public void Canonical_paths_ignore_trailing_separators_and_alternate_separators()
    {
        var expected = CreationPathSafety.Canonical(Path.Combine(root, "servers"));

        Assert.Equal(expected, CreationPathSafety.Canonical(Path.Combine(root, "servers") + Path.DirectorySeparatorChar));
        Assert.Equal(expected, CreationPathSafety.Canonical(root + "/servers"));
        Assert.Equal(expected, CreationPathSafety.Canonical(Path.Combine(root, "other", "..", "servers")));
    }

    [Fact]
    public void Canonical_paths_compare_case_insensitively_as_windows_does()
    {
        Assert.True(CreationPathSafety.IsSamePath(Path.Combine(root, "Alpha"), Path.Combine(root, "ALPHA")));
        Assert.False(CreationPathSafety.IsSamePath(Path.Combine(root, "Alpha"), Path.Combine(root, "Beta")));
    }

    [Fact]
    public void Containment_distinguishes_equal_inside_and_unrelated_paths()
    {
        var parent = Path.Combine(root, "parent");
        var child = Path.Combine(parent, "child");
        var sibling = Path.Combine(root, "parent-two");

        Assert.False(CreationPathSafety.IsUnder(parent, parent));
        Assert.True(CreationPathSafety.IsUnder(parent, child));
        Assert.False(CreationPathSafety.IsUnder(parent, sibling));
        Assert.True(CreationPathSafety.Overlaps(parent, child));
        Assert.True(CreationPathSafety.Overlaps(child, parent));
        Assert.False(CreationPathSafety.Overlaps(parent, sibling));
    }

    [Fact]
    public void A_relative_escape_is_rejected_by_the_shared_containment_check()
    {
        var instanceRoot = Path.Combine(root, "instances");

        Assert.Throws<InvalidDataException>(() =>
            CreationPathSafety.EnsureWithin(instanceRoot, Path.Combine(instanceRoot, "..", "elsewhere")));
        CreationPathSafety.EnsureWithin(instanceRoot, Path.Combine(instanceRoot, "server"));
        CreationPathSafety.EnsureWithin(instanceRoot, instanceRoot);
    }

    [Fact]
    public void Volume_comparison_recognises_the_same_drive()
    {
        Assert.True(CreationPathSafety.IsSameVolume(
            Path.Combine(root, "a"), Path.Combine(root, "b", "c")));
        Assert.False(CreationPathSafety.IsSameVolume(@"C:\one", @"Z:\two"));
    }

    // ---------------------------------------------------------------- destination verdicts

    [Fact]
    public void An_absent_destination_is_available()
    {
        var decision = Evaluate(Path.Combine(root, "fresh"));

        Assert.Equal(CreationDestinationVerdict.Available, decision.Verdict);
        Assert.True(decision.IsAllowed);
        Assert.False(decision.DestinationExisted);
    }

    [Fact]
    public void An_existing_empty_destination_is_available_and_recorded_as_pre_existing()
    {
        var destination = Path.Combine(root, "empty");
        Directory.CreateDirectory(destination);

        var decision = Evaluate(destination);

        Assert.Equal(CreationDestinationVerdict.AvailableEmpty, decision.Verdict);
        Assert.True(decision.IsAllowed);
        Assert.True(decision.DestinationExisted);
    }

    [Fact]
    public void An_existing_non_empty_destination_is_blocked_and_points_at_the_import_workflow()
    {
        var destination = Path.Combine(root, "occupied");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "world.dat"), "someone else's files");

        var decision = Evaluate(destination);

        Assert.Equal(CreationDestinationVerdict.BlockedNotEmpty, decision.Verdict);
        Assert.False(decision.IsAllowed);
        Assert.Contains("already contains files", decision.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was changed.", decision.Message, StringComparison.Ordinal);
        Assert.Contains("Add an existing server", decision.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(destination, "world.dat")));
    }

    [Fact]
    public void A_file_on_the_path_is_never_replaced()
    {
        var destination = Path.Combine(root, "afile");
        File.WriteAllText(destination, "not a folder");

        var decision = Evaluate(destination);

        Assert.Equal(CreationDestinationVerdict.BlockedFileExists, decision.Verdict);
        Assert.Contains("Nothing was changed.", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registered_managed_server_blocks_its_own_folder_even_after_the_folder_is_deleted()
    {
        var destination = Path.Combine(root, "managed");
        var decision = Evaluate(destination, servers: [Managed("Existing", destination)]);

        // The directory deliberately does not exist: a registered server whose folder was removed
        // still owns the path, and the old "does the directory exist" check missed exactly this.
        Assert.False(Directory.Exists(destination));
        Assert.Equal(CreationDestinationVerdict.BlockedManagedServer, decision.Verdict);
        Assert.Contains("Existing", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_imported_server_folder_is_never_taken_over()
    {
        var destination = Path.Combine(root, "imported");
        Directory.CreateDirectory(destination);

        var decision = Evaluate(destination, servers: [Imported("Old World", destination)]);

        Assert.Equal(CreationDestinationVerdict.BlockedImportedServer, decision.Verdict);
        Assert.Contains("only reads it", decision.Message, StringComparison.Ordinal);
        Assert.Contains("never takes over", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ownership_is_matched_regardless_of_case_or_a_trailing_separator()
    {
        var registered = Path.Combine(root, "CaseServer");

        var upper = Evaluate(Path.Combine(root, "CASESERVER"), servers: [Managed("Case", registered)]);
        var trailing = Evaluate(Path.Combine(root, "CaseServer") + Path.DirectorySeparatorChar,
            servers: [Managed("Case", registered)]);

        Assert.Equal(CreationDestinationVerdict.BlockedManagedServer, upper.Verdict);
        Assert.Equal(CreationDestinationVerdict.BlockedManagedServer, trailing.Verdict);
    }

    [Fact]
    public void A_destination_inside_a_known_server_is_blocked()
    {
        var existing = Path.Combine(root, "existing");
        var decision = Evaluate(Path.Combine(existing, "nested"), servers: [Managed("Existing", existing)]);

        Assert.Equal(CreationDestinationVerdict.BlockedInsideKnownServer, decision.Verdict);
        Assert.Contains("never nested", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_destination_that_would_contain_a_known_server_is_blocked()
    {
        var parent = Path.Combine(root, "parent");
        var existing = Path.Combine(parent, "existing");
        var decision = Evaluate(parent, servers: [Managed("Existing", existing)]);

        Assert.Equal(CreationDestinationVerdict.BlockedContainsKnownServer, decision.Verdict);
    }

    [Fact]
    public void A_destination_that_overlaps_its_own_staging_folder_is_blocked_in_both_directions()
    {
        var staging = Path.Combine(root, "instances", ServerCreationTransaction.StagingFolderName(Guid.NewGuid()));

        var destinationInsideStaging = Evaluate(Path.Combine(staging, "server"), staging: staging);
        var stagingInsideDestination = Evaluate(Path.Combine(root, "instances"), staging: staging);
        var destinationIsStaging = Evaluate(staging, staging: staging);

        Assert.Equal(CreationDestinationVerdict.BlockedUnsafePath, destinationInsideStaging.Verdict);
        Assert.Equal(CreationDestinationVerdict.BlockedUnsafePath, stagingInsideDestination.Verdict);
        Assert.Equal(CreationDestinationVerdict.BlockedUnsafePath, destinationIsStaging.Verdict);
    }

    [Fact]
    public void Another_unfinished_creation_operation_blocks_the_same_folder()
    {
        var destination = Path.Combine(root, "contested");
        var other = new CreationJournalEntry
        {
            OperationId = Guid.NewGuid(),
            CanonicalDestination = CreationPathSafety.Canonical(destination),
            Phase = CreationPhase.Activating
        };

        var blocked = Evaluate(destination, operations: [other]);
        var ownEntry = Evaluate(destination, operations: [other with { OperationId = OperationId }]);

        Assert.Equal(CreationDestinationVerdict.BlockedActiveOperation, blocked.Verdict);
        // An operation is never blocked by its own journal entry, or it could never resume.
        Assert.True(ownEntry.IsAllowed);
    }

    [Fact]
    public void A_junction_destination_is_refused_because_its_real_location_is_unknown()
    {
        var target = Path.Combine(root, "junction-target");
        var link = Path.Combine(root, "junction-link");
        Directory.CreateDirectory(target);
        CreateJunction(link, target);

        Assert.True(CreationPathSafety.IsReparsePoint(link));
        Assert.False(CreationPathSafety.IsReparsePoint(target));

        var decision = Evaluate(link);

        Assert.Equal(CreationDestinationVerdict.BlockedReparsePoint, decision.Verdict);
        Assert.Contains("junction", decision.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_plain_directory_is_not_mistaken_for_a_reparse_point()
    {
        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);

        Assert.False(CreationPathSafety.IsReparsePoint(plain));
        Assert.False(CreationPathSafety.IsReparsePoint(Path.Combine(root, "missing")));
    }

    // ---------------------------------------------------------------- helpers

    private static readonly Guid OperationId = Guid.NewGuid();

    private CreationDestinationDecision Evaluate(
        string destination,
        string? staging = null,
        IReadOnlyList<ServerDefinition>? servers = null,
        IReadOnlyList<CreationJournalEntry>? operations = null) =>
        CreationDestinationPolicy.Evaluate(new CreationDestinationQuery(
            OperationId,
            destination,
            staging ?? Path.Combine(root, "instances", ServerCreationTransaction.StagingFolderName(OperationId)),
            servers ?? [],
            operations ?? []));

    private static ServerDefinition Managed(string name, string rootPath) =>
        new() { Id = Guid.NewGuid(), Name = name, RootPath = rootPath, IsManaged = true };

    private static ServerDefinition Imported(string name, string rootPath) =>
        new() { Id = Guid.NewGuid(), Name = name, RootPath = rootPath, IsManaged = false };

    /// <summary>
    /// Creates an NTFS junction, which needs no elevation, unlike a directory symbolic link.
    /// </summary>
    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("cmd.exe could not be started to create a junction.");
        process.WaitForExit(20_000);
        if (!Directory.Exists(link))
            throw new InvalidOperationException("The test junction could not be created on this filesystem.");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
