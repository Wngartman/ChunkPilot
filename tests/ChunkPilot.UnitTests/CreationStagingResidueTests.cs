using ChunkPilot.Certification;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CreationStagingResidueTests
{
    [Fact]
    public void New_acceptance_selection_pins_dated_official_relationship_not_an_undated_or_generated_release()
    {
        CatalogVersion Release(string id, int day, bool official = true) => new()
        {
            ClientFileId = id, PublishedAt = DateTimeOffset.UtcNow.AddDays(day), HasServerPackage = official,
            SizeBytes = 10, Sha1 = new string('a', 40), ClientSha1 = new string('b', 40)
        };
        var project = new CatalogItem { Versions = [Release("1", -2), Release("2", -1), Release("3", 0, false)] };
        Assert.Equal("2", CurseForgeRuntimeCertificationSession.SelectLatestOfficial(project).ClientFileId);
        Assert.Throws<InvalidDataException>(() => CurseForgeRuntimeCertificationSession.SelectLatestOfficial(
            project with { Versions = [] }));
    }

    [Fact]
    public async Task Incomplete_inputs_reserve_their_declared_bytes_before_download()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-input-cap-" + Guid.NewGuid().ToString("N"));
        var paths = new AppDataPaths(Path.Combine(root, "data"), Path.Combine(root, "servers"));
        paths.EnsureCreated();
        try
        {
            var store = new VerifiedCreationArchiveStore(paths);
            CreationOwnershipMarker Owner() => new(1, Guid.NewGuid(), Guid.NewGuid(), Path.Combine(paths.ManagedServers, "fixture"), DateTimeOffset.UtcNow);
            await store.PrepareAsync(Owner(), 4L * 1024 * 1024 * 1024, CancellationToken.None);
            await store.PrepareAsync(Owner(), 4L * 1024 * 1024 * 1024, CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => store.PrepareAsync(Owner(), 1, CancellationToken.None));
            Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(paths.Staging, "creation-inputs")).Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Terminal_evidence_and_empty_input_store_are_not_mutable_residue()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-residue-" + Guid.NewGuid().ToString("N"));
        var operation = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(root, "creation-inputs"));
        try
        {
            File.WriteAllText(Path.Combine(root, $"{operation:N}.log"), "Completed: fixture");
            File.WriteAllText(Path.Combine(root, $"{operation:N}.log.validation-{Guid.NewGuid():N}.json"), "{}");
            File.WriteAllText(Path.Combine(root, ".creation-inputs.lock"), "");
            Assert.True(CurseForgeRuntimeCertificationSession.CanonicalStagingContainsOnlyTerminalLogs(root,
                new HashSet<Guid> { operation }, new HashSet<Guid>()));
            var unknown = Path.Combine(root, "creation-inputs", "unowned");
            Directory.CreateDirectory(unknown);
            Assert.False(CurseForgeRuntimeCertificationSession.CanonicalStagingContainsOnlyTerminalLogs(root,
                new HashSet<Guid> { operation }, new HashSet<Guid>()));
            Assert.True(CurseForgeRuntimeCertificationSession.CanonicalStagingContainsOnlyTerminalLogs(root,
                new HashSet<Guid> { operation }, new HashSet<Guid>(), unknown));
            File.WriteAllText(Path.Combine(root, "unknown.jar"), "fixture");
            Assert.False(CurseForgeRuntimeCertificationSession.CanonicalStagingContainsOnlyTerminalLogs(root,
                new HashSet<Guid> { operation }, new HashSet<Guid>(), unknown));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
