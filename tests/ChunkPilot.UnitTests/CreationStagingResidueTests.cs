using ChunkPilot.Certification;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using System.Text.Json;

namespace ChunkPilot.UnitTests;

public sealed class CreationStagingResidueTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Retained_report_requires_terminal_cleanup_and_completed_transfer_not_creation_success(bool succeeded)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-retained-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        try
        {
            var operation = Guid.NewGuid();
            var path = Path.Combine(root, "evidence", "certification-fixture.json");
            var report = new
            {
                documentType = "ChunkPilot.CurseForgeRuntimeCertificationReport", freshRunIntentionallyRetained = true,
                cleanupSucceeded = true, terminalSelectedPortAbsent = true, agentJobActiveProcessesAfterCleanup = 0,
                agentExitCode = 0, creationOperationId = operation, serverId = succeeded ? (Guid?)Guid.NewGuid() : null,
                runId = "run-fixture", candidateGitSha = new string('a', 40), projectId = "1", clientFileId = "2", serverPackFileId = "3",
                operationStates = new[] { new { operationId = operation, terminal = true, success = succeeded } },
                payloads = new[] { new { kind = "official-server-pack", operationId = operation, expectedBytes = 4,
                    downloadedBytes = 4, localSha256 = new string('b', 64), providerSha1Verified = true } }
            };
            var json = JsonSerializer.Serialize(report);
            File.WriteAllText(path, json);
            Assert.Equal(succeeded, RetainedControlSelection.Read(root, path).CreationSucceeded);
            File.WriteAllText(path, json.Replace("\"downloadedBytes\":4", "\"downloadedBytes\":3", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => RetainedControlSelection.Read(root, path));
            File.WriteAllText(path, json.Replace("\"agentJobActiveProcessesAfterCleanup\":0", "\"agentJobActiveProcessesAfterCleanup\":1", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => RetainedControlSelection.Read(root, path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

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

    [Theory]
    [InlineData("Completed: fixture")]
    [InlineData("2026-09-05T10:15:31.5846769-06:00 Completed: Completed")]
    public void Terminal_evidence_and_empty_input_store_are_not_mutable_residue(string terminalLine)
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-residue-" + Guid.NewGuid().ToString("N"));
        var operation = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(root, "creation-inputs"));
        try
        {
            File.WriteAllText(Path.Combine(root, $"{operation:N}.log"), terminalLine);
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
