using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class CurseForgeManagedContentPlanAuthorizationRegistryTests
{
    [Fact]
    public void Matching_authorization_returns_the_deep_copied_exact_plan_once()
    {
        var serverId = Guid.NewGuid();
        var dependencies = new List<PluginDependency>
        {
            new() { ProjectId = "20", Type = "required" }
        };
        var releases = new List<PluginRelease>
        {
            Release("20", "200", "library.jar"),
            Release("10", "100", "root.jar") with { Dependencies = dependencies }
        };
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        var authorization = registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100",
            new PluginInstallPlan { Releases = releases });

        dependencies.Clear();
        releases[0] = releases[0] with { DownloadUrl = "https://example.invalid/forged.jar" };
        releases.Reverse();

        var trusted = registry.Consume(
            serverId, PluginProviderKind.CurseForge, "10", "100", authorization);

        Assert.Equal(["20", "10"], trusted.Releases.Select(release => release.ProjectId));
        Assert.Equal("20", Assert.Single(trusted.Releases[^1].Dependencies).ProjectId);
        Assert.Equal("https://mediafilez.forgecdn.net/files/200/library.jar",
            trusted.Releases[0].DownloadUrl);
        Assert.Null(trusted.Authorization);
        Assert.Throws<InvalidOperationException>(() => registry.Consume(
            serverId, PluginProviderKind.CurseForge, "10", "100", authorization));
    }

    [Fact]
    public void Forged_digest_and_server_switch_fail_without_consuming_the_exact_review()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        var authorization = registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100", Plan());

        Assert.Throws<InvalidOperationException>(() => registry.Consume(
            serverId,
            PluginProviderKind.CurseForge,
            "10",
            "100",
            authorization with { Digest = new string('f', 64) }));
        Assert.Throws<InvalidOperationException>(() => registry.Consume(
            Guid.NewGuid(), PluginProviderKind.CurseForge, "10", "100", authorization));

        registry.Consume(serverId, PluginProviderKind.CurseForge, "10", "100", authorization);
    }

    [Fact]
    public void Root_project_or_file_mismatch_fails_without_consuming_the_review()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        var authorization = registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100", Plan());

        Assert.Throws<InvalidOperationException>(() => registry.Consume(
            serverId, PluginProviderKind.CurseForge, "11", "100", authorization));
        Assert.Throws<InvalidOperationException>(() => registry.Consume(
            serverId, PluginProviderKind.CurseForge, "10", "101", authorization));

        registry.Consume(serverId, PluginProviderKind.CurseForge, "10", "100", authorization);
    }

    [Fact]
    public void Registration_rejects_tampered_or_misordered_provider_plan()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        var forged = Plan() with
        {
            Releases = Plan().Releases.Select((release, index) => index == 0
                ? release with { Sha1 = new string('f', 40), DownloadUrl = "https://example.invalid/forged.jar" }
                : release).ToArray()
        };
        var misordered = Plan() with { Releases = Plan().Releases.Reverse().ToArray() };

        Assert.Throws<InvalidDataException>(() => registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100", forged));
        Assert.Throws<InvalidDataException>(() => registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100", misordered));
    }

    [Fact]
    public void Registration_rejects_case_insensitive_destination_filename_collisions()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        var colliding = Plan() with
        {
            Releases =
            [
                Release("20", "200", "shared-name.jar"),
                Release("10", "100", "SHARED-NAME.JAR") with
                {
                    Dependencies = [new PluginDependency { ProjectId = "20", Type = "required" }]
                }
            ]
        };

        var failure = Assert.Throws<InvalidDataException>(() => registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100", colliding));

        Assert.Contains("destination JAR filename", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, registry.OutstandingCount);
    }

    [Fact]
    public void Same_key_replaces_old_review_without_consuming_capacity()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry(capacity: 1);
        var first = registry.Register(serverId, PluginProviderKind.CurseForge, "10", "100", Plan());
        var secondPlan = Plan() with
        {
            Releases = Plan().Releases.Select(release => release with
            {
                VersionName = release.VersionName + " refreshed"
            }).ToArray()
        };
        var second = registry.Register(
            serverId, PluginProviderKind.CurseForge, "10", "100", secondPlan);

        Assert.Equal(1, registry.OutstandingCount);
        Assert.NotEqual(first.AuthorizationId, second.AuthorizationId);
        Assert.NotEqual(first.Digest, second.Digest);
        Assert.Throws<InvalidOperationException>(() => registry.Consume(
            serverId, PluginProviderKind.CurseForge, "10", "100", first));
        registry.Consume(serverId, PluginProviderKind.CurseForge, "10", "100", second);
    }

    [Fact]
    public void Capacity_is_bounded_and_exact_revoke_recovers_the_slot()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry(capacity: 2);
        var first = registry.Register(serverId, PluginProviderKind.CurseForge, "10", "100", Plan());
        registry.Register(serverId, PluginProviderKind.CurseForge, "11", "101", Plan("11", "101"));

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Register(
            serverId, PluginProviderKind.CurseForge, "12", "102", Plan("12", "102")));
        Assert.Contains("Too many", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(registry.Revoke(first.AuthorizationId));
        Assert.False(registry.Revoke(first.AuthorizationId));
        registry.Register(serverId, PluginProviderKind.CurseForge, "12", "102", Plan("12", "102"));
    }

    [Fact]
    public void Repeated_cross_key_selection_invalidations_do_not_fill_the_registry()
    {
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry();
        for (var index = 0; index < 65; index++)
        {
            var projectId = (1_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var versionId = (2_000 + index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            registry.Register(
                serverId,
                PluginProviderKind.CurseForge,
                projectId,
                versionId,
                Plan(projectId, versionId));
            Assert.True(registry.Revoke(
                serverId,
                PluginProviderKind.CurseForge,
                projectId,
                versionId));
            Assert.False(registry.Revoke(
                serverId,
                PluginProviderKind.CurseForge,
                projectId,
                versionId));
        }

        Assert.Equal(0, registry.OutstandingCount);
        registry.Register(serverId, PluginProviderKind.CurseForge, "10", "100", Plan());
    }

    [Fact]
    public void Expiry_uses_monotonic_elapsed_time_and_not_wall_clock()
    {
        var clock = new MutableTimeProvider();
        var serverId = Guid.NewGuid();
        var registry = new CurseForgeManagedContentPlanAuthorizationRegistry(
            clock, TimeSpan.FromMinutes(2));
        var first = registry.Register(serverId, PluginProviderKind.CurseForge, "10", "100", Plan());
        clock.ChangeWallClock(TimeSpan.FromDays(30));
        registry.Consume(serverId, PluginProviderKind.CurseForge, "10", "100", first);

        var second = registry.Register(serverId, PluginProviderKind.CurseForge, "10", "100", Plan());
        clock.Advance(TimeSpan.FromMinutes(2));

        var failure = Assert.Throws<InvalidOperationException>(() => registry.Consume(
            serverId, PluginProviderKind.CurseForge, "10", "100", second));
        Assert.Contains("expired", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static PluginInstallPlan Plan(string rootProjectId = "10", string rootVersionId = "100") => new()
    {
        Releases =
        [
            Release("20", "200", "library.jar"),
            Release(rootProjectId, rootVersionId, "root.jar") with
            {
                Dependencies = [new PluginDependency { ProjectId = "20", Type = "required" }]
            }
        ]
    };

    private static PluginRelease Release(string projectId, string versionId, string fileName) => new()
    {
        Kind = ManagedAddonKind.Mod,
        Provider = PluginProviderKind.CurseForge,
        ProjectId = projectId,
        VersionId = versionId,
        VersionName = versionId,
        MinecraftVersion = "1.21.1",
        Loader = "neoforge",
        DownloadUrl = $"https://mediafilez.forgecdn.net/files/{versionId}/{fileName}",
        FileName = fileName,
        SizeBytes = 128,
        Sha1 = new string(projectId == "20" ? 'a' : 'b', 40)
    };

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset now = new(2026, 8, 29, 18, 0, 0, TimeSpan.Zero);
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => now;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            timestamp += elapsed.Ticks;
        }
        public void ChangeWallClock(TimeSpan elapsed) => now += elapsed;
    }
}
