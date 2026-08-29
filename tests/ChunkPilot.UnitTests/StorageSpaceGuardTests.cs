using System.Diagnostics;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class StorageSpaceGuardTests
{
    [Fact]
    public void Requirements_on_the_same_volume_are_combined_before_acceptance()
    {
        var probe = new FixtureStorageSpaceProbe(_ => new StorageVolumeSpace("fixture", 1_000));

        var error = Assert.Throws<IOException>(() => StorageSpaceGuard.EnsureAvailable(
            probe,
            [
                new StorageSpaceRequirement("first", "Snapshot", 600),
                new StorageSpaceRequirement("second", "Candidate", 500)
            ]));

        Assert.Contains("Snapshot", error.Message, StringComparison.Ordinal);
        Assert.Contains("Candidate", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Requirements_on_different_volumes_are_evaluated_independently()
    {
        var probe = new FixtureStorageSpaceProbe(path =>
            new StorageVolumeSpace(path, 1_000));

        StorageSpaceGuard.EnsureAvailable(
            probe,
            [
                new StorageSpaceRequirement("first", "Snapshot", 600),
                new StorageSpaceRequirement("second", "Candidate", 600)
            ]);
    }

    [Fact]
    public void Requirement_arithmetic_saturates_instead_of_wrapping_or_throwing()
    {
        Assert.Equal(long.MaxValue,
            StorageSpaceGuard.SaturatingAdd(long.MaxValue - 5, 10));
        Assert.Equal(long.MaxValue,
            StorageSpaceGuard.SaturatingMultiply(long.MaxValue / 2 + 1, 2));
        var probe = new FixtureStorageSpaceProbe(_ =>
            new StorageVolumeSpace("fixture", long.MaxValue - 1));

        Assert.Throws<IOException>(() => StorageSpaceGuard.EnsureAvailable(
            probe,
            [
                new StorageSpaceRequirement("first", "Snapshot", long.MaxValue - 5),
                new StorageSpaceRequirement("second", "Candidate", 10)
            ]));
    }

    [Fact]
    public void System_probe_groups_mounted_folder_aliases_by_authoritative_volume_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot.StorageProbeTests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            var api = new FixtureWindowsStorageVolumeApi(
                path => path.Equals(first, StringComparison.OrdinalIgnoreCase)
                    ? "mounted-volume-path-a"
                    : "mounted-volume-path-b",
                _ => @"\\?\volume{11111111-1111-1111-1111-111111111111}\",
                _ => 1_000);
            var probe = new SystemStorageSpaceProbe(api);

            var error = Assert.Throws<IOException>(() => StorageSpaceGuard.EnsureAvailable(
                probe,
                [
                    new StorageSpaceRequirement(
                        Path.Combine(first, "future", "archive.zip"), "First mounted target", 600),
                    new StorageSpaceRequirement(
                        Path.Combine(second, "future", "candidate"), "Second mounted target", 500)
                ]));

            Assert.Contains("First mounted target", error.Message, StringComparison.Ordinal);
            Assert.Contains("Second mounted target", error.Message, StringComparison.Ordinal);
            Assert.Contains(first, api.ResolvedExistingPaths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(second, api.ResolvedExistingPaths, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void System_probe_fails_closed_when_windows_cannot_resolve_the_target_volume()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot.StorageProbeTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var probe = new SystemStorageSpaceProbe(new FixtureWindowsStorageVolumeApi(
                _ => throw new IOException("unresolved target volume"),
                _ => throw new InvalidOperationException(),
                _ => throw new InvalidOperationException()));

            var error = Assert.Throws<IOException>(() => probe.GetSpace(
                Path.Combine(root, "future", "archive.zip")));

            Assert.Contains("unresolved target volume", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void System_probe_resolves_the_real_local_windows_volume()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var space = SystemStorageSpaceProbe.Instance.GetSpace(Path.GetTempPath());

        Assert.StartsWith(@"\\?\volume{", space.VolumeIdentity,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("}\\", space.VolumeIdentity, StringComparison.Ordinal);
        Assert.True(space.AvailableBytes >= 0);
    }

    [Fact]
    public void Bounded_server_inventory_rejects_reparse_directories_without_traversing_them()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot.InventoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var server = Path.Combine(root, "server");
            var foreign = Path.Combine(root, "foreign");
            Directory.CreateDirectory(server);
            Directory.CreateDirectory(foreign);
            var foreignWorld = Path.Combine(foreign, "r.0.0.mca");
            File.WriteAllBytes(foreignWorld, new byte[4_096]);
            CreateJunction(Path.Combine(server, "world"), foreign);

            using var exclusive = new FileStream(
                foreignWorld, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var error = Assert.Throws<InvalidDataException>(() =>
                BoundedServerFileInventory.Capture(server));

            Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteJunction(Path.Combine(root, "server", "world"));
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Bounded_server_inventory_honors_pre_cancellation_and_the_entry_limit()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot.InventoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "first.txt"), "first");
            File.WriteAllText(Path.Combine(root, "second.txt"), "second");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(() =>
                BoundedServerFileInventory.Capture(root, cancellationToken: cancellation.Token));
            var error = Assert.Throws<InvalidDataException>(() =>
                BoundedServerFileInventory.Capture(root, maximumEntries: 1));
            Assert.Contains("more than 1 filesystem", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Locked_world_metadata_is_counted_for_snapshot_and_candidate_on_the_same_volume()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot.InventoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            var world = Path.Combine(root, "world", "region");
            Directory.CreateDirectory(world);
            var region = Path.Combine(world, "r.0.0.mca");
            File.WriteAllBytes(region, new byte[4_096]);
            using var exclusive = new FileStream(region, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var inventory = BoundedServerFileInventory.Capture(root);
            var forecast = ServerPackUpdateService.CalculateStorageForecast(
                inventory.TotalBytes,
                packageBytes: 64L * 1024 * 1024,
                generatedPlan: null,
                cacheDownloadRequired: false,
                verifiedPackageStagingBytes: 0);
            var combined = StorageSpaceGuard.SaturatingAdd(
                forecast.SnapshotRequiredBytes, forecast.CandidateRequiredBytes);
            var probe = new FixtureStorageSpaceProbe(_ =>
                new StorageVolumeSpace("same-volume", combined - 1));

            Assert.Equal(4_096, inventory.TotalBytes);
            Assert.Equal(
                StorageSpaceGuard.SaturatingAdd(4_096, StorageSpaceGuard.SafetyReserveBytes),
                forecast.SnapshotRequiredBytes);
            Assert.Equal(
                StorageSpaceGuard.SaturatingAdd(4_096, StorageSpaceGuard.SafetyReserveBytes),
                forecast.CandidateRequiredBytes);
            Assert.Throws<IOException>(() => StorageSpaceGuard.EnsureAvailable(
                probe,
                [
                    new StorageSpaceRequirement(root, "Snapshot", forecast.SnapshotRequiredBytes),
                    new StorageSpaceRequirement(root, "Candidate", forecast.CandidateRequiredBytes)
                ]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("cmd.exe could not be started to create a junction.");
        process.WaitForExit(20_000);
        if (process.ExitCode != 0 || !Directory.Exists(link) ||
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) == 0)
            throw new InvalidOperationException("The test junction could not be created on this filesystem.");
    }

    private static void DeleteJunction(string path)
    {
        if (Directory.Exists(path) &&
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            Directory.Delete(path);
    }

    private sealed class FixtureStorageSpaceProbe(
        Func<string, StorageVolumeSpace> resolve) : IStorageSpaceProbe
    {
        public StorageVolumeSpace GetSpace(string path) => resolve(path);
    }

    private sealed class FixtureWindowsStorageVolumeApi(
        Func<string, string> getVolumePath,
        Func<string, string> getVolumeIdentity,
        Func<string, long> getAvailableBytes) : IWindowsStorageVolumeApi
    {
        public List<string> ResolvedExistingPaths { get; } = [];

        public string GetVolumePathName(string path)
        {
            ResolvedExistingPaths.Add(path);
            return getVolumePath(path);
        }

        public string GetVolumeIdentity(string volumePath) => getVolumeIdentity(volumePath);

        public long GetAvailableBytes(string volumePath) => getAvailableBytes(volumePath);
    }
}
