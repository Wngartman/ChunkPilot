using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.IntegrationTests;

public sealed class BackupSafetyIntegrationTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-backup-safety-" + Guid.NewGuid().ToString("N"));
    private ChunkPilotStore store = null!;
    private AppDataPaths paths = null!;
    private ServerDefinition server = null!;
    private BackupService service = null!;
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        paths = new AppDataPaths(Path.Combine(root, "data"));
        store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var folder = Path.Combine(root, "server");
        Directory.CreateDirectory(folder);
        server = new() { Name = "Synthetic backup safety", RootPath = folder, WorkingDirectory = folder };
        service = new(paths, store);
        await File.WriteAllTextAsync(Path.Combine(folder, "server.properties"), "motd=before\n");
    }

    [Fact]
    public async Task Unverified_profile_does_not_claim_hash_verification()
    {
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server) with { VerificationEnabled = false });
        Assert.False(record.Verified);
        Assert.True(File.Exists(record.ArchivePath));
    }

    [Fact]
    public async Task Backup_destination_above_server_does_not_silently_exclude_its_contents()
    {
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server) with { DestinationPath = root });
        Assert.True(record.Verified);
        using var zip = ZipFile.OpenRead(record.ArchivePath);
        Assert.NotNull(zip.GetEntry("server.properties"));
        await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "motd=current\n");
        await service.RestoreAsync(server, record);
        Assert.Equal("motd=before\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
    }

    [Fact]
    public async Task Legacy_manifest_and_historical_default_profile_remain_restorable()
    {
        var record = new BackupRecord
        {
            ServerId = server.Id, ProfileId = Guid.NewGuid(), ArchivePath = Path.Combine(root, "legacy.zip"),
            ManifestPath = Path.Combine(root, "legacy.manifest.json"), CreatedAt = DateTimeOffset.UtcNow.AddDays(-60)
        };
        var bytes = Encoding.UTF8.GetBytes("# legacy\r\nmotd=historical\r\n");
        using (var zip = ZipFile.Open(record.ArchivePath, ZipArchiveMode.Create))
        {
            await using (var content = zip.CreateEntry("server.properties").Open())
                await content.WriteAsync(bytes);
            // Original archives carried these identity and inventory fields, with optional newer
            // server metadata absent. Do not regenerate this fixture using the current writer.
            await using var manifest = zip.CreateEntry(".chunkpilot/manifest.json").Open();
            await JsonSerializer.SerializeAsync(manifest, new
            {
                backupId = record.Id, serverId = server.Id, createdAt = record.CreatedAt,
                serverName = server.Name, sourceRoot = server.RootPath,
                files = new[] { new { relativePath = "server.properties", sizeBytes = bytes.Length,
                    sha256 = Convert.ToHexString(SHA256.HashData(bytes)) } }
            }, ProtocolJson.Options);
        }
        await store.UpsertBackupAsync(record);
        Assert.True(await service.VerifyAsync(record));
        _ = await service.CreateAsync(server, service.GetDefaultProfile(server) with { MaximumCount = 1 });
        Assert.Contains(await store.GetBackupsAsync(server.Id), backup => backup.Id == record.Id);
        await service.RestoreAsync(server, record);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(server.RootPath, "server.properties")));
    }

    [Fact]
    public async Task Retention_never_deletes_the_newest_or_only_recovery_point_over_budget()
    {
        var profile = service.GetDefaultProfile(server) with { MaximumStorageBytes = 1, MaximumCount = 1 };
        var first = await service.CreateAsync(server, profile);
        Assert.True(File.Exists(first.ArchivePath));
        var second = await service.CreateAsync(server, profile);
        var retained = Assert.Single(await store.GetBackupsAsync(server.Id));
        Assert.Equal(second.Id, retained.Id);
        Assert.True(File.Exists(second.ArchivePath));
    }

    [Fact]
    public async Task Default_profile_identity_is_stable_and_future_manual_backups_apply_retention()
    {
        Assert.Equal(server.Id, service.GetDefaultProfile(server).Id);
        Assert.Equal(service.GetDefaultProfile(server).Id, service.GetDefaultProfile(server).Id);
        _ = await service.CreateAsync(server, service.GetDefaultProfile(server) with { MaximumCount = 1 });
        var newest = await service.CreateAsync(server, service.GetDefaultProfile(server) with { MaximumCount = 1 });
        Assert.Equal(newest.Id, Assert.Single(await store.GetBackupsAsync(server.Id)).Id);
    }

    [Fact]
    public async Task Pre_restore_recovery_does_not_expire_selected_source_and_unverified_backup_keeps_verified_recovery()
    {
        var profile = service.GetDefaultProfile(server) with { MaximumCount = 1 };
        var original = await service.CreateAsync(server, profile);
        await store.UpsertBackupAsync(original with { CreatedAt = DateTimeOffset.UtcNow.AddDays(-31) });
        var unverified = await service.CreateAsync(server, profile with { VerificationEnabled = false });
        Assert.True(File.Exists(original.ArchivePath));
        Assert.True(File.Exists(unverified.ArchivePath));
        await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "motd=pre-restore\n");
        var recovery = await service.CreatePreRestoreRecoveryAsync(server);
        Assert.True(recovery.Verified);
        Assert.True(File.Exists(original.ArchivePath));
        Assert.True(File.Exists(unverified.ArchivePath));
        await service.RestoreAsync(server, original);
        Assert.Equal("motd=before\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        await service.RestoreAsync(server, recovery);
        Assert.Equal("motd=pre-restore\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
    }

    [Theory]
    [InlineData("extra.txt")]
    [InlineData("server.properties")]
    [InlineData("SERVER.PROPERTIES")]
    public async Task Unlisted_or_duplicate_archive_members_fail_closed_before_restore(string entryName)
    {
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        using (var zip = ZipFile.Open(record.ArchivePath, ZipArchiveMode.Update))
        {
            var entry = zip.CreateEntry(entryName);
            await using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            await writer.WriteAsync("not in the manifest");
        }
        Assert.False(await service.VerifyAsync(record));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(server, record));
        Assert.Equal("motd=before\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        Assert.False(File.Exists(Path.Combine(server.RootPath, "extra.txt")));
    }

    [Fact]
    public async Task A_backup_for_another_server_is_not_restored()
    {
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        var otherRoot = Path.Combine(root, "other-server");
        Directory.CreateDirectory(otherRoot);
        var other = server with { Id = Guid.NewGuid(), RootPath = otherRoot };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(other, record));
        Assert.Empty(Directory.GetFiles(otherRoot));
    }

    [Fact]
    public async Task Archive_identity_must_match_both_backup_and_server_record()
    {
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        Assert.False(await service.VerifyAsync(record with { Id = Guid.NewGuid() }));
        Assert.False(await service.VerifyAsync(record with { ServerId = Guid.NewGuid() }));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(server, record with { Id = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("world/../escape.txt")]
    [InlineData("world/file.txt:stream")]
    [InlineData("world/CON.txt")]
    [InlineData("world/file. ")]
    [InlineData("world\\file.txt")]
    public async Task Even_hash_valid_manifest_cannot_authorize_unsafe_windows_paths(string unsafeName)
    {
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        using (var zip = ZipFile.Open(record.ArchivePath, ZipArchiveMode.Update))
        {
            var manifestEntry = zip.GetEntry(".chunkpilot/manifest.json")!;
            BackupManifest manifest;
            await using (var stream = manifestEntry.Open())
                manifest = (await JsonSerializer.DeserializeAsync<BackupManifest>(stream, ProtocolJson.Options))!;
            var bytes = Encoding.UTF8.GetBytes("unsafe");
            await using (var content = zip.CreateEntry(unsafeName).Open())
                await content.WriteAsync(bytes);
            manifestEntry.Delete();
            await using var target = zip.CreateEntry(".chunkpilot/manifest.json").Open();
            await JsonSerializer.SerializeAsync(target, manifest with
            {
                Files = manifest.Files.Append(new BackupManifestEntry(unsafeName, bytes.Length,
                    Convert.ToHexString(SHA256.HashData(bytes)))).ToArray()
            }, ProtocolJson.Options);
        }
        Assert.False(await service.VerifyAsync(record));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(server, record));
        Assert.Equal("motd=before\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
    }

    [Fact]
    public async Task Locked_target_fails_before_any_file_is_replaced()
    {
        var second = Path.Combine(server.RootPath, "z-locked.txt");
        await File.WriteAllTextAsync(second, "snapshot");
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        await File.WriteAllTextAsync(second, "current");
        await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "motd=current\n");
        await using (var held = new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(server, record));
        Assert.Equal("current", await File.ReadAllTextAsync(second));
        Assert.Equal("motd=current\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        Assert.Empty(Directory.GetDirectories(root, ".chunkpilot-restore-*"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Cancellation_rolls_back_activated_files_but_preserves_external_changes_and_recovery(bool externalChange, bool initiallyAbsent)
    {
        var first = Path.Combine(server.RootPath, "a-first.txt");
        var second = Path.Combine(server.RootPath, "z-held.txt");
        await File.WriteAllTextAsync(first, "snapshot-first");
        await File.WriteAllTextAsync(second, "snapshot-second");
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        await File.WriteAllTextAsync(first, "current-first");
        if (initiallyAbsent)
            File.Delete(first);
        await File.WriteAllTextAsync(second, "current-second");
        var locks = new CanonicalPathLockManager();
        service = new BackupService(paths, store, pathLocks: locks);
        await using var held = await locks.AcquireAsync(second);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var restore = service.RestoreAsync(server, record, cancellation.Token);
        var wait = Stopwatch.StartNew();
        while ((!File.Exists(first) || await File.ReadAllTextAsync(first) != "snapshot-first") && wait.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.Equal("snapshot-first", await File.ReadAllTextAsync(first));
        if (externalChange)
            await File.WriteAllTextAsync(first, "external-current");
        cancellation.Cancel();
        if (externalChange)
        {
            var error = await Assert.ThrowsAsync<IOException>(() => restore);
            Assert.Contains("recovery files were retained", error.Message, StringComparison.Ordinal);
            Assert.Equal("external-current", await File.ReadAllTextAsync(first));
            var retained = Assert.Single(Directory.GetDirectories(root, ".chunkpilot-restore-*"));
            Assert.Contains(Directory.GetFiles(retained, "original-*"), file => File.ReadAllText(file) == "current-first");
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
            if (initiallyAbsent)
                Assert.False(File.Exists(first));
            else
                Assert.Equal("current-first", await File.ReadAllTextAsync(first));
            Assert.Empty(Directory.GetDirectories(root, ".chunkpilot-restore-*"));
        }
        Assert.Equal("current-second", await File.ReadAllTextAsync(second));
    }

    [Fact]
    public async Task Source_and_backup_destination_junctions_are_rejected_without_reading_external_data()
    {
        var external = Path.Combine(root, "external");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "private.txt"), "external-private");
        CreateJunction(alias, external);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(server with { RootPath = alias }, service.GetDefaultProfile(server)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(server, service.GetDefaultProfile(server) with { DestinationPath = alias }));
            Assert.Equal("external-private", await File.ReadAllTextAsync(Path.Combine(external, "private.txt")));
            Assert.Single(Directory.GetFiles(external));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public async Task Restore_preflights_all_target_ancestry_before_any_write()
    {
        var world = Path.Combine(server.RootPath, "world");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(Path.Combine(world, "level.dat"), "snapshot-world");
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        var originalWorld = Path.Combine(root, "original-world");
        Directory.Move(world, originalWorld);
        var external = Path.Combine(root, "external-world");
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "level.dat"), "external-owned");
        CreateJunction(world, external);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "motd=current\n");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RestoreAsync(server, record));
            Assert.Equal("external-owned", await File.ReadAllTextAsync(Path.Combine(external, "level.dat")));
            Assert.Equal("motd=current\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        }
        finally { Directory.Delete(world); }
    }

    [Fact]
    public async Task Restore_rechecks_a_destination_replaced_by_a_junction_while_waiting_for_its_lock()
    {
        var world = Path.Combine(server.RootPath, "z-world");
        var target = Path.Combine(world, "level.dat");
        Directory.CreateDirectory(world);
        await File.WriteAllTextAsync(target, "snapshot-world");
        var record = await service.CreateAsync(server, service.GetDefaultProfile(server));
        await File.WriteAllTextAsync(Path.Combine(server.RootPath, "server.properties"), "motd=current\n");
        await File.WriteAllTextAsync(target, "current-world");
        var locks = new CanonicalPathLockManager();
        service = new BackupService(paths, store, pathLocks: locks);
        await using var held = await locks.AcquireAsync(target);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var restore = service.RestoreAsync(server, record, cancellation.Token);
        var wait = Stopwatch.StartNew();
        while (await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")) != "motd=before\n" &&
               wait.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(20);
        Assert.Equal("motd=before\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
        var preserved = Path.Combine(root, "preserved-world");
        var external = Path.Combine(root, "external-world");
        Directory.Move(world, preserved);
        Directory.CreateDirectory(external);
        await File.WriteAllTextAsync(Path.Combine(external, "level.dat"), "external-owned");
        CreateJunction(world, external);
        try
        {
            await held.DisposeAsync();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => restore);
            Assert.Equal("motd=current\n", await File.ReadAllTextAsync(Path.Combine(server.RootPath, "server.properties")));
            Assert.Equal("external-owned", await File.ReadAllTextAsync(Path.Combine(external, "level.dat")));
            Assert.Equal("current-world", await File.ReadAllTextAsync(Path.Combine(preserved, "level.dat")));
            Assert.Empty(Directory.GetDirectories(root, ".chunkpilot-restore-*"));
        }
        finally { Directory.Delete(world); }
    }

    [Fact]
    public void Safe_paths_reject_root_and_ancestor_junctions()
    {
        var actual = Path.Combine(root, "actual");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(Path.Combine(actual, "nested"));
        CreateJunction(alias, actual);
        try
        {
            var files = new SafeFileService(paths);
            Assert.Throws<UnauthorizedAccessException>(() => files.ResolveWithinRoot(alias, "file.json", false));
            Assert.Throws<UnauthorizedAccessException>(() => files.ResolveWithinRoot(Path.Combine(alias, "nested"), "file.json", false));
        }
        finally { Directory.Delete(alias); }
    }

    [FileSymbolicLinkFact]
    public void Safe_paths_reject_existing_and_dangling_file_symbolic_links()
    {
        var actual = Path.Combine(root, "external-file.json");
        var link = Path.Combine(server.RootPath, "linked.json");
        File.WriteAllText(actual, "external-file");
        File.CreateSymbolicLink(link, actual);
        try
        {
            var files = new SafeFileService(paths);
            Assert.Throws<UnauthorizedAccessException>(() => files.ResolveWithinRoot(server.RootPath, "linked.json", false));
            File.Delete(actual);
            Assert.Throws<UnauthorizedAccessException>(() => files.ResolveWithinRoot(server.RootPath, "linked.json", false));
        }
        finally { File.Delete(link); }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Junction fixture could not start.");
        process.WaitForExit(20_000);
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    public async Task DisposeAsync()
    {
        await store.DisposeAsync();
        Directory.Delete(root, true);
    }
}

// xUnit 2 has no runtime Assert.Skip. Probe only this disposable fixture at discovery so lack of
// Windows symlink privilege is an explicit skipped result; junction coverage still runs everywhere.
public sealed class FileSymbolicLinkFactAttribute : FactAttribute
{
    public FileSymbolicLinkFactAttribute()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "ChunkPilot-file-link-capability-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(fixture, "link");
        Directory.CreateDirectory(fixture);
        try { File.CreateSymbolicLink(link, Path.Combine(fixture, "missing-target")); }
        catch (IOException exception) when (exception.HResult == unchecked((int)0x80070522))
        {
            Skip = "UNAVAILABLE: this Windows test process lacks file-symbolic-link privilege. Run this case on a runner already permitted to create file symlinks.";
        }
        finally
        {
            File.Delete(link);
            Directory.Delete(fixture);
        }
    }
}
