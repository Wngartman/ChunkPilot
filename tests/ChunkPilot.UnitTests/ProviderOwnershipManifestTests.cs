using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class ProviderOwnershipManifestTests
{
    [Fact]
    public async Task Modified_obsolete_provider_file_is_preserved_and_requires_review()
    {
        var root = TempRoot();
        try
        {
            var current = Path.Combine(root, "current");
            var candidate = Path.Combine(root, "candidate");
            Directory.CreateDirectory(Path.Combine(current, "mods"));
            Directory.CreateDirectory(candidate);
            var mod = Path.Combine(current, "mods", "fixture.jar");
            await File.WriteAllTextAsync(mod, "provider baseline");
            await ProviderOwnershipManifest.WriteAsync(current, "CurseForge");
            await File.WriteAllTextAsync(mod, "locally modified");

            var plan = await new PackMigrationPlanner().BuildAndApplyAsync(current, candidate, []);

            Assert.True(File.Exists(Path.Combine(candidate, "mods", "fixture.jar")));
            Assert.Equal("locally modified", await File.ReadAllTextAsync(Path.Combine(candidate, "mods", "fixture.jar")));
            Assert.Contains(plan.Conflicts, conflict => conflict.Contains("locally modified", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(plan.Changes, change => change.RelativePath == "mods/fixture.jar" &&
                                                   change.Change == "Preserved locally modified provider file");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Unchanged_obsolete_provider_file_is_removed_but_remains_described()
    {
        var root = TempRoot();
        try
        {
            var current = Path.Combine(root, "current");
            var candidate = Path.Combine(root, "candidate");
            Directory.CreateDirectory(Path.Combine(current, "mods"));
            Directory.CreateDirectory(candidate);
            await File.WriteAllTextAsync(Path.Combine(current, "mods", "fixture.jar"), "provider baseline");
            await ProviderOwnershipManifest.WriteAsync(current, "CurseForge");

            var plan = await new PackMigrationPlanner().BuildAndApplyAsync(current, candidate, []);

            Assert.False(File.Exists(Path.Combine(candidate, "mods", "fixture.jar")));
            Assert.Contains(plan.Changes, change => change.RelativePath == "mods/fixture.jar" &&
                                                   change.Change == "Removed from active pack");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Modified_provider_file_can_be_replaced_only_by_explicit_new_baseline_resolution()
    {
        var root = TempRoot();
        try
        {
            var current = Path.Combine(root, "current");
            var candidate = Path.Combine(root, "candidate");
            Directory.CreateDirectory(Path.Combine(current, "mods"));
            Directory.CreateDirectory(Path.Combine(candidate, "mods"));
            var old = Path.Combine(current, "mods", "fixture.jar");
            var target = Path.Combine(candidate, "mods", "fixture.jar");
            await File.WriteAllTextAsync(old, "provider baseline");
            await ProviderOwnershipManifest.WriteAsync(current, "CurseForge");
            await File.WriteAllTextAsync(old, "locally modified");
            await File.WriteAllTextAsync(target, "new provider baseline");

            var plan = await new PackMigrationPlanner().BuildAndApplyAsync(current, candidate, [],
                new Dictionary<string, MigrationResolution>
                {
                    ["mods/fixture.jar"] = new() { Kind = MigrationResolutionKind.NewBaseline }
                });

            Assert.Equal("new provider baseline", await File.ReadAllTextAsync(target));
            Assert.Empty(plan.Conflicts);
            Assert.Contains(plan.Changes, change => change.Change == "Selected new pack baseline");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "ChunkPilot-provider-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
