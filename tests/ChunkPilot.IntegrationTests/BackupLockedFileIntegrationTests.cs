using System.IO.Compression;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

/// <summary>
/// The running-server backup failure, reproduced and then prevented.
/// </summary>
/// <remarks>
/// <para>
/// The reported error was "The process cannot access the file because another process has locked a
/// portion of the file." That is ERROR_LOCK_VIOLATION, and Minecraft is the process holding the lock:
/// it takes an exclusive byte-range lock on <c>session.lock</c> in every loaded world folder and keeps
/// it for as long as the world is open. These fixtures reproduce that with a real byte-range lock
/// rather than a mock, because the failure is entirely about how Windows treats a locked range.
/// </para>
/// <para>
/// The default profile had always listed <c>session.lock</c> as an exclusion. The pattern was anchored
/// at the server root, so <c>world/session.lock</c> was never matched - which is what the first test
/// pins - and the read failed on it.
/// </para>
/// </remarks>
public sealed class BackupLockedFileIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "ChunkPilot-lockbackup-" + Guid.NewGuid().ToString("N"));
    private ChunkPilotStore store = null!;
    private AppDataPaths paths = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "appdata"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
    }

    /// <summary>A file-name pattern excludes that name wherever it is, which is the root cause fix.</summary>
    [Fact]
    public void A_file_name_exclusion_matches_at_every_depth()
    {
        string[] patterns = ["logs/**", "crash-reports/**", "*.tmp", "*.lock", "session.lock"];

        Assert.True(BackupService.ShouldExclude("session.lock", patterns));
        Assert.True(BackupService.ShouldExclude("world/session.lock", patterns));
        Assert.True(BackupService.ShouldExclude("world_nether/DIM-1/session.lock", patterns));
        Assert.True(BackupService.ShouldExclude("world/anything.lock", patterns));
        Assert.True(BackupService.ShouldExclude("logs/latest.log", patterns));

        // A rooted pattern stays rooted: logs/** must not start excluding a datapack's own logs folder.
        Assert.False(BackupService.ShouldExclude("world/datapacks/pack/logs/notes.txt", patterns));
        Assert.False(BackupService.ShouldExclude("world/level.dat", patterns));
        Assert.False(BackupService.ShouldExclude("server.properties", patterns));
    }

    /// <summary>
    /// A world with a locked session.lock backs up successfully, and the world data is all there.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_backup_succeeds_while_a_world_holds_its_session_lock()
    {
        var serverRoot = Path.Combine(root, "running-server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "world", "region"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "motd=live\r\n");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "world", "level.dat"), "world-state");
        await File.WriteAllTextAsync(
            Path.Combine(serverRoot, "world", "region", "r.0.0.mca"), "region-state");

        var definition = new ServerDefinition { Name = "Locked world", RootPath = serverRoot };
        var service = new BackupService(paths, store);
        var profile = service.GetDefaultProfile(definition);

        await using var session = LockWorldSession(Path.Combine(serverRoot, "world"));
        var record = await service.CreateAsync(definition, profile);

        Assert.True(record.Verified, record.VerificationMessage);
        Assert.True(File.Exists(record.ArchivePath));
        Assert.False(File.Exists(record.ArchivePath + ".partial"));

        using var archive = ZipFile.OpenRead(record.ArchivePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        // The world is captured; only the lock artifact is absent, and that file is recreated by the
        // server on every start.
        Assert.Contains("world/level.dat", names);
        Assert.Contains("world/region/r.0.0.mca", names);
        Assert.Contains("server.properties", names);
        Assert.DoesNotContain("world/session.lock", names);
        // Nothing is duplicated: a retry that re-added an entry under the same name was its own defect.
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// A locked file that is not a known lock artifact fails the backup, clearly, and leaves nothing.
    /// </summary>
    /// <remarks>
    /// The alternative would be to skip it, which is exactly the silent data loss the safety rules
    /// forbid. The message names the file, and no partial archive survives to be mistaken for a
    /// restore point.
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task A_locked_data_file_fails_the_backup_with_its_path_and_leaves_no_partial()
    {
        var serverRoot = Path.Combine(root, "locked-data-server");
        Directory.CreateDirectory(Path.Combine(serverRoot, "world"));
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "server.properties"), "motd=live\r\n");
        var locked = Path.Combine(serverRoot, "world", "level.dat");
        await File.WriteAllTextAsync(locked, "world-state-that-cannot-be-read");

        var definition = new ServerDefinition { Name = "Locked data", RootPath = serverRoot };
        var service = new BackupService(paths, store);
        var profile = service.GetDefaultProfile(definition);

        await using var handle = LockWholeFile(locked);
        var failure = await Assert.ThrowsAsync<IOException>(
            () => service.CreateAsync(definition, profile));

        Assert.Contains("world/level.dat", failure.Message, StringComparison.Ordinal);
        Assert.Contains("locked", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await store.GetBackupsAsync(definition.Id));
        Assert.Empty(Directory.Exists(profile.DestinationPath)
            ? Directory.GetFiles(profile.DestinationPath)
            : []);
    }

    /// <summary>
    /// A file that grows while it is captured is recorded as what was stored, and still verifies.
    /// </summary>
    /// <remarks>
    /// The current log keeps being written for as long as the server runs. The manifest used to record
    /// the length measured before the read, so an appended byte made verification compare the archive
    /// against a number that was never true and report the backup unverified.
    /// </remarks>
    [Fact(Timeout = 60_000)]
    public async Task A_file_that_grows_during_capture_still_verifies_against_what_was_stored()
    {
        var serverRoot = Path.Combine(root, "growing-server");
        Directory.CreateDirectory(serverRoot);
        var growing = Path.Combine(serverRoot, "notes.txt");
        await File.WriteAllTextAsync(growing, new string('a', 4096));

        var definition = new ServerDefinition { Name = "Growing", RootPath = serverRoot };
        var service = new BackupService(paths, store);
        var profile = service.GetDefaultProfile(definition);

        await using (var appender = new FileStream(growing, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            var pending = service.CreateAsync(definition, profile);
            await appender.WriteAsync(new byte[512]);
            await appender.FlushAsync();
            var record = await pending;

            Assert.True(record.Verified, record.VerificationMessage);
            Assert.True(await service.VerifyAsync(record));
        }
    }

    /// <summary>A backup that fails verification is never finalised and never recorded.</summary>
    [Fact(Timeout = 60_000)]
    public async Task Verification_happens_before_the_archive_is_finalised()
    {
        var serverRoot = Path.Combine(root, "verify-server");
        Directory.CreateDirectory(serverRoot);
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "file.txt"), "data");
        var definition = new ServerDefinition { Name = "Verify order", RootPath = serverRoot };
        var service = new BackupService(paths, store);
        var profile = service.GetDefaultProfile(definition);

        var record = await service.CreateAsync(definition, profile);

        Assert.True(record.Verified);
        Assert.Contains("verified before it was finalised", record.VerificationMessage, StringComparison.Ordinal);
        Assert.EndsWith(".zip", record.ArchivePath, StringComparison.Ordinal);
        Assert.False(File.Exists(record.ArchivePath + ".partial"));
        Assert.Empty(Directory.GetFiles(profile.DestinationPath, "*.partial"));
    }

    /// <summary>
    /// Holds session.lock the way Minecraft does: opened, then an exclusive lock on its whole range.
    /// </summary>
    private static LockedFile LockWorldSession(string worldFolder)
    {
        Directory.CreateDirectory(worldFolder);
        var path = Path.Combine(worldFolder, "session.lock");
        File.WriteAllText(path, "☃");
        return LockWholeFile(path);
    }

    /// <summary>
    /// Takes the same kind of exclusive byte-range lock Minecraft takes on <c>session.lock</c>.
    /// </summary>
    /// <remarks>
    /// Byte-range locking is a Windows API, and so is ChunkPilot, but the analyser needs the guard to
    /// know that. Without a real range lock this fixture would prove nothing: a shared-mode open
    /// succeeds and only the read of the locked range fails.
    /// </remarks>
    private static LockedFile LockWholeFile(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var length = Math.Max(1, stream.Length);
        if (OperatingSystem.IsWindows())
            stream.Lock(0, length);
        return new LockedFile(stream, length);
    }

    private sealed class LockedFile : IAsyncDisposable
    {
        private readonly FileStream stream;
        private readonly long length;

        public LockedFile(FileStream stream, long length)
        {
            this.stream = stream;
            this.length = length;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    stream.Unlock(0, length);
            }
            catch (IOException)
            {
            }
            await stream.DisposeAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
