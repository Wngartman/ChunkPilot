using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.UnitTests;

public sealed class Release12Tests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ChunkPilot-12-unit-" + Guid.NewGuid().ToString("N"));

    public Release12Tests() => Directory.CreateDirectory(root);

    [Fact]
    public void Source_detection_requires_real_provider_identity()
    {
        var serverRoot = Path.Combine(root, "Definitely-A-Famous-Pack");
        Directory.CreateDirectory(serverRoot);
        var detector = new UpdateSourceDetector();
        var unknown = detector.Detect(new ServerDefinition { Id = Guid.NewGuid(), RootPath = serverRoot });
        Assert.False(unknown.IsTrustworthy);
        Assert.True(unknown.RequiresBaseline);

        File.WriteAllText(Path.Combine(serverRoot, "modrinth.index.json"), """
            {"formatVersion":1,"game":"minecraft","name":"Fixture","versionId":"v1",
             "dependencies":{"minecraft":"1.21.1"},"files":[]}
            """);
        var indexOnly = detector.Detect(new ServerDefinition
        {
            Id = Guid.NewGuid(),
            RootPath = serverRoot,
            MinecraftVersion = "1.21.1",
            Ecosystem = ServerEcosystem.NeoForge
        });
        Assert.False(indexOnly.IsTrustworthy);
        Assert.Contains(indexOnly.Evidence, item => item.Contains("standard format", StringComparison.OrdinalIgnoreCase));

        var sourceDirectory = Path.Combine(serverRoot, ".chunkpilot");
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, "update-source.json"), JsonSerializer.Serialize(
            Source(UpdateProvider.Modrinth) with
            {
                ProjectId = "fixture-pack",
                InstalledVersionId = "api-version-id"
            }, ProtocolJson.Options));
        var detected = detector.Detect(new ServerDefinition { Id = Guid.NewGuid(), RootPath = serverRoot });
        Assert.True(detected.IsTrustworthy);
        Assert.Equal(UpdateProvider.Modrinth, detected.Source?.Provider);
        Assert.Equal("api-version-id", detected.Source?.InstalledVersionId);
    }

    [Fact]
    public void Incomplete_pack_metadata_never_becomes_a_source()
    {
        var serverRoot = Path.Combine(root, "incomplete");
        Directory.CreateDirectory(serverRoot);
        File.WriteAllText(Path.Combine(serverRoot, "modrinth.index.json"),
            """{"name":"Fixture","projectId":"nonstandard-spoof","versionId":"v1","files":[]}""");
        var result = new UpdateSourceDetector().Detect(new ServerDefinition { RootPath = serverRoot });
        Assert.False(result.IsTrustworthy);
        Assert.Contains(result.Evidence, item => item.Contains("standard format", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Modrinth_filters_channels_and_prefers_server_package()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/fixture", StringComparison.Ordinal))
                return Json("""{"server_side":"required"}""");
            return Json("""
                [
                  {"project_id":"fixture","id":"stable","version_number":"2.0","version_type":"release","date_published":"2026-07-24T12:00:00Z",
                   "game_versions":["1.21.1"],"loaders":["neoforge"],"changelog":"stable",
                   "files":[
                     {"primary":true,"filename":"client.mrpack","url":"https://cdn.modrinth.com/data/fixture/versions/stable/client.mrpack","size":5,"hashes":{"sha1":"client","sha512":"def"}},
                     {"primary":false,"filename":"fixture-server.zip","url":"https://cdn/server.zip","size":10,"hashes":{"sha1":"abc","sha512":"def"}}
                   ]},
                  {"project_id":"fixture","id":"beta","version_number":"3.0-beta","version_type":"beta","date_published":"2026-07-25T12:00:00Z",
                   "game_versions":["1.21.1"],"loaders":["neoforge"],"files":[{"primary":true,"filename":"beta.mrpack","url":"https://cdn.modrinth.com/data/fixture/versions/beta/beta.mrpack","size":10,"hashes":{"sha1":"beta1","sha512":"beta"}}]}
                ]
                """);
        });
        var provider = new ModrinthUpdateProvider(new HttpClient(handler));
        var source = Source(UpdateProvider.Modrinth) with
        {
            ProjectId = "fixture",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge"
        };
        var stable = Assert.Single(await provider.GetVersionsAsync(source, new UpdatePreferences()));
        Assert.Equal("stable", stable.VersionId);
        Assert.Equal("https://cdn.modrinth.com/data/fixture/versions/stable/client.mrpack", stable.DownloadUrl);
        Assert.Equal("def", stable.Sha512);
        Assert.Equal("mrpack", stable.PackageType);
        var all = await provider.GetVersionsAsync(source, new UpdatePreferences { IncludeBeta = true });
        Assert.Equal(2, all.Count);
        Assert.Equal("beta", all[0].VersionId);
    }

    [Fact]
    public async Task Modrinth_catalog_exposes_only_trusted_exact_mrpack_releases_for_review()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? Json("""
                {"hits":[{"project_id":"pack-project","slug":"pack-project","title":"Pack Project","author":"Fixture","description":"A server-capable pack.","icon_url":"https://cdn.modrinth.com/data/pack-project/icon.png","downloads":42,"date_modified":"2026-08-19T12:00:00Z","server_side":"required","categories":["fabric"]}]}
                """)
            : Json("""
                [{"id":"release-id","name":"Release 1","version_type":"release","date_published":"2026-08-19T12:00:00Z","game_versions":["1.21.1"],"loaders":["fabric"],"changelog":"Verified fixture metadata.","files":[
                  {"primary":true,"filename":"release.mrpack","url":"https://cdn.modrinth.com/data/pack-project/versions/release-id/release.mrpack","size":1234,"hashes":{"sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sha512":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}},
                  {"primary":false,"filename":"unsafe.mrpack","url":"https://example.invalid/unsafe.mrpack","size":20,"hashes":{"sha1":"bad","sha512":"bad"}}
                ]}]
                """));
        var provider = new ModrinthCatalogProvider(new HttpClient(handler));

        var item = Assert.Single(await provider.BrowseAsync(new CatalogQuery
        {
            Search = "pack",
            MinecraftVersion = "1.21.1",
            Loader = "fabric",
            Limit = 20,
            Sort = CatalogSort.Downloads
        }));

        Assert.Equal(InstallationSupportState.AutomatedWithReview, item.InstallationSupport);
        var release = Assert.Single(item.Versions);
        Assert.True(release.HasServerPackage);
        Assert.Equal("release-id", release.VersionId);
        Assert.Equal("https://cdn.modrinth.com/data/pack-project/versions/release-id/release.mrpack", release.DownloadUrl);
        Assert.Equal(40, release.Sha1.Length);
        Assert.Equal(128, release.Sha512.Length);
    }

    [Fact]
    public async Task Modrinth_catalog_preserves_the_exact_requested_beta_version_when_it_is_not_first()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? Json("""
                {"hits":[{"project_id":"beta-pack","slug":"beta-pack","title":"Beta Pack","author":"Fixture","description":"Historical pack.","downloads":3,"date_modified":"2026-08-19T12:00:00Z","server_side":"required","categories":["fabric"]}]}
                """)
            : Json("""
                [{"id":"beta-release","name":"Beta release","version_type":"release","date_published":"2026-08-19T12:00:00Z","game_versions":["1.20.1","b1.8.1"],"loaders":["forge","fabric"],"files":[{"primary":true,"filename":"beta.mrpack","url":"https://cdn.modrinth.com/data/beta-pack/versions/beta-release/beta.mrpack","size":1234,"hashes":{"sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sha512":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}]}]
                """));

        var item = Assert.Single(await new ModrinthCatalogProvider(new HttpClient(handler)).BrowseAsync(
            new CatalogQuery { Search = "beta", MinecraftVersion = "b1.8.1", Loader = "fabric" }));
        var release = Assert.Single(item.Versions);

        Assert.Equal("b1.8.1", release.MinecraftVersion);
        Assert.Equal("fabric", release.Loader);
        Assert.Equal(8, release.RequiredJavaMajor);
    }

    [Fact]
    public async Task Modrinth_catalog_resolves_an_exact_pasted_project_link_without_scraping()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/project/pack-project" => Json("""
                {"id":"pack-id","slug":"pack-project","title":"Pack Project","description":"Exact project.","icon_url":"https://cdn.modrinth.com/data/pack-id/icon.png","downloads":9,"updated":"2026-08-19T12:00:00Z","server_side":"required","categories":["fabric"]}
                """),
            "/v2/project/pack-id/version" => Json("""
                [{"id":"release-id","name":"Release 1","version_type":"release","date_published":"2026-08-19T12:00:00Z","game_versions":["1.21.1"],"loaders":["fabric"],"files":[{"primary":true,"filename":"release.mrpack","url":"https://cdn.modrinth.com/data/pack-id/versions/release-id/release.mrpack","size":1234,"hashes":{"sha1":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","sha512":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}}]}]
                """),
            _ => throw new InvalidOperationException("The pasted link must use exact Modrinth API endpoints.")
        });

        var item = Assert.Single(await new ModrinthCatalogProvider(new HttpClient(handler)).BrowseAsync(
            new CatalogQuery { Search = "https://modrinth.com/modpack/pack-project" }));

        Assert.Equal("pack-id", item.ProjectId);
        Assert.Equal("pack-project", item.Slug);
        Assert.Single(item.Versions);
    }

    [Fact]
    public async Task Modrinth_version_inventory_preserves_official_release_snapshot_beta_and_alpha_kinds()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("/v2/tag/game_version", request.RequestUri!.AbsolutePath);
            return Json("""
                [
                  {"version":"1.21.8","version_type":"release","date":"2026-08-01T00:00:00Z","major":true},
                  {"version":"26w33a","version_type":"snapshot","date":"2026-08-13T00:00:00Z","major":false},
                  {"version":"b1.8.1","version_type":"beta","date":"2011-09-19T00:00:00Z","major":false},
                  {"version":"a1.2.6","version_type":"alpha","date":"2010-12-03T00:00:00Z","major":false}
                ]
                """);
        });

        var versions = await new ModrinthCatalogProvider(new HttpClient(handler)).GetGameVersionsAsync();

        Assert.Equal(CatalogGameVersionKind.Release, Assert.Single(versions, item => item.VersionId == "1.21.8").Kind);
        Assert.Equal(CatalogGameVersionKind.Snapshot, Assert.Single(versions, item => item.VersionId == "26w33a").Kind);
        Assert.Equal(CatalogGameVersionKind.Beta, Assert.Single(versions, item => item.VersionId == "b1.8.1").Kind);
        Assert.Equal(CatalogGameVersionKind.Alpha, Assert.Single(versions, item => item.VersionId == "a1.2.6").Kind);
    }

    [Fact]
    public async Task CurseForge_version_inventory_requires_a_saved_key_and_classifies_historical_versions()
    {
        var secrets = new MemorySecrets();
        var handler = new StubHandler(request =>
        {
            Assert.Equal("secret", request.Headers.GetValues("x-api-key").Single());
            Assert.Equal("/v1/minecraft/version", request.RequestUri!.AbsolutePath);
            return Json("""{"data":[{"versionString":"1.21.8","dateModified":"2026-08-01T00:00:00Z"},{"versionString":"b1.8.1","dateModified":"2011-09-19T00:00:00Z"},{"versionString":"a1.2.6","dateModified":"2010-12-03T00:00:00Z"}]}""");
        });
        var provider = new CurseForgeCatalogProvider(secrets, new HttpClient(handler));
        Assert.Empty(await provider.GetGameVersionsAsync());
        Assert.Equal(0, handler.RequestCount);

        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "secret");
        var versions = await provider.GetGameVersionsAsync();

        Assert.Equal(CatalogGameVersionKind.Release, Assert.Single(versions, item => item.VersionId == "1.21.8").Kind);
        Assert.Equal(CatalogGameVersionKind.Beta, Assert.Single(versions, item => item.VersionId == "b1.8.1").Kind);
        Assert.Equal(CatalogGameVersionKind.Alpha, Assert.Single(versions, item => item.VersionId == "a1.2.6").Kind);
    }

    [Fact]
    public async Task CurseForge_requires_key_before_network_access()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Network should not be called."));
        var provider = new CurseForgeUpdateProvider(new MemorySecrets(), new HttpClient(handler));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetVersionsAsync(Source(UpdateProvider.CurseForge) with { ProjectId = "123" },
                new UpdatePreferences()));
        Assert.Contains("API key", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task CurseForge_resolves_official_server_pack_file()
    {
        var secrets = new MemorySecrets();
        secrets.SetSecret(CurseForgeUpdateProvider.ApiKeyName, "secret");
        var handler = new StubHandler(request =>
        {
            Assert.Equal("secret", request.Headers.GetValues("x-api-key").Single());
            if (request.RequestUri!.AbsolutePath.EndsWith("/files/222", StringComparison.Ordinal))
                return Json("""{"data":{"id":222,"fileName":"fixture-server.zip","displayName":"server","fileDate":"2026-07-24T12:00:00Z","releaseType":1,"gameVersions":[],"downloadUrl":"https://cdn/server.zip","fileLength":12,"hashes":[{"algo":1,"value":"abc"}]}}""");
            return Json("""{"data":[{"id":111,"fileName":"client.zip","displayName":"Pack 2","fileDate":"2026-07-24T12:00:00Z","releaseType":1,"gameVersions":["1.21.1","NeoForge"],"serverPackFileId":222,"hashes":[]}]}""");
        });
        var provider = new CurseForgeUpdateProvider(secrets, new HttpClient(handler));
        var result = Assert.Single(await provider.GetVersionsAsync(
            Source(UpdateProvider.CurseForge) with
            {
                ProjectId = "123",
                MinecraftVersion = "1.21.1",
                Loader = "NeoForge"
            }, new UpdatePreferences()));
        Assert.Equal("111", result.VersionId);
        Assert.Equal("https://cdn/server.zip", result.DownloadUrl);
        Assert.Equal("abc", result.Sha1);
    }

    [Fact]
    public async Task GitHub_ignores_drafts_and_filters_prereleases()
    {
        var handler = new StubHandler(_ => Json("""
            [
              {"id":1,"tag_name":"draft","draft":true,"prerelease":false,"published_at":"2026-07-24T10:00:00Z","body":"","assets":[{"name":"server.zip","browser_download_url":"https://x/draft","size":1}]},
              {"id":2,"tag_name":"v2-beta","draft":false,"prerelease":true,"published_at":"2026-07-25T10:00:00Z","body":"","assets":[{"name":"fixture-server.zip","browser_download_url":"https://x/beta","size":2}]},
              {"id":3,"tag_name":"v1","draft":false,"prerelease":false,"published_at":"2026-07-23T10:00:00Z","body":"notes","assets":[{"name":"fixture-server.zip","browser_download_url":"https://x/stable","size":3,"digest":"sha256:abc"}]}
            ]
            """));
        var provider = new GitHubReleasesUpdateProvider(new HttpClient(handler));
        var source = Source(UpdateProvider.GitHubReleases) with
        {
            ProjectId = "owner/repository",
            AssetNamePattern = "server"
        };
        var stable = Assert.Single(await provider.GetVersionsAsync(source, new UpdatePreferences()));
        Assert.Equal("v1", stable.VersionName);
        Assert.Equal("abc", stable.Sha256);
        var beta = await provider.GetVersionsAsync(source, new UpdatePreferences { IncludeBeta = true });
        Assert.Equal(["v2-beta", "v1"], beta.Select(item => item.VersionName));
    }

    [Theory]
    [InlineData(ReleaseChannel.Stable, false, false, true)]
    [InlineData(ReleaseChannel.Beta, false, false, false)]
    [InlineData(ReleaseChannel.Beta, true, false, true)]
    [InlineData(ReleaseChannel.Alpha, true, false, false)]
    [InlineData(ReleaseChannel.Alpha, false, true, true)]
    public void Release_channel_policy_is_explicit(
        ReleaseChannel channel, bool beta, bool alpha, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.Allows(channel,
            new UpdatePreferences { IncludeBeta = beta, IncludeAlpha = alpha }));

    [Fact]
    public async Task Hash_verification_prefers_strong_hash_and_rejects_mismatch()
    {
        var file = Path.Combine(root, "payload.zip");
        await File.WriteAllTextAsync(file, "fixture");
        var sha512 = Convert.ToHexString(SHA512.HashData(await File.ReadAllBytesAsync(file))).ToLowerInvariant();
        await ServerPackUpdateService.VerifyDownloadAsync(file, new PackVersionInfo { Sha512 = sha512 });
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServerPackUpdateService.VerifyDownloadAsync(file,
                new PackVersionInfo { Sha512 = new string('0', 128), Sha1 = "ignored" }));
    }

    [Fact]
    public void Compatibility_detects_upgrade_path_minecraft_and_loader_changes()
    {
        var source = Source(UpdateProvider.Modrinth) with
        {
            ProjectId = "pack",
            InstalledVersionId = "v1",
            MinecraftVersion = "1.20.1",
            Loader = "Forge"
        };
        var installed = Version("v1", DateTimeOffset.Parse("2026-01-01T00:00:00Z")) with
        {
            PackId = "pack",
            MinecraftVersion = "1.20.1",
            Loader = "Forge"
        };
        var latest = Version("v2", DateTimeOffset.Parse("2026-02-01T00:00:00Z")) with
        {
            PackId = "pack",
            MinecraftVersion = "1.21.1",
            Loader = "NeoForge",
            LoaderVersion = "21.1",
            RequiredJavaMajor = 21
        };
        var result = new PackUpdateCompatibilityService().Evaluate(
            new ServerDefinition
            {
                Id = source.ServerId,
                MinecraftVersion = "1.20.1",
                Ecosystem = ServerEcosystem.Forge,
                LoaderVersion = "47"
            }, source, installed, [latest, installed], DateTimeOffset.Now);
        Assert.Equal(ServerUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(UpdateCompatibility.CompatibleWithMigrationWarning, result.Compatibility);
        Assert.Contains(result.CompatibilityReasons, item => item.Contains("Minecraft changes", StringComparison.Ordinal));
        Assert.Contains(result.CompatibilityReasons, item => item.Contains("Loader changes", StringComparison.Ordinal));
        Assert.Contains(result.CompatibilityReasons, item => item.Contains("Java 21", StringComparison.Ordinal));
    }

    [Fact]
    public void Source_link_validation_rejects_unsafe_or_incomplete_links()
    {
        Assert.Throws<ArgumentException>(() => UpdateSourceDetector.ValidateLink(
            Source(UpdateProvider.Modrinth) with { ProjectId = "" }));
        Assert.Throws<ArgumentException>(() => UpdateSourceDetector.ValidateLink(
            Source(UpdateProvider.DirectManifest) with { SourceUrl = "http://example/versions.json" }));
        Assert.Throws<InvalidOperationException>(() => UpdateSourceDetector.ValidateLink(
            Source(UpdateProvider.GitHubReleases) with { ProjectId = "not-a-repository" }));
        UpdateSourceDetector.ValidateLink(
            Source(UpdateProvider.GitHubReleases) with { ProjectId = "owner/repository" });
    }

    [Theory]
    [InlineData("world/playerdata/player.dat", FileOwnership.Persistent)]
    [InlineData("server.properties", FileOwnership.Persistent)]
    [InlineData("mods/pack.jar", FileOwnership.PackManaged)]
    [InlineData("config/example.toml", FileOwnership.Unknown)]
    [InlineData("notes/readme.txt", FileOwnership.UserAdded)]
    public void Persistent_data_classification_is_conservative(string path, FileOwnership expected) =>
        Assert.Equal(expected, PersistentDataClassifier.Classify(path, ["world"]));

    [Fact]
    public async Task Migration_uses_new_pack_baseline_preserves_state_and_reports_jar_removal()
    {
        var oldRoot = Path.Combine(root, "old");
        var candidate = Path.Combine(root, "candidate");
        Directory.CreateDirectory(Path.Combine(oldRoot, "world", "playerdata"));
        Directory.CreateDirectory(Path.Combine(oldRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(oldRoot, "notes"));
        Directory.CreateDirectory(Path.Combine(candidate, "mods"));
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "world", "level.dat"), "world-state");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "server.properties"), "motd=user");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "mods", "obsolete.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "mods", "updated.jar"), "old");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "notes", "mine.txt"), "user");
        await File.WriteAllTextAsync(Path.Combine(candidate, "mods", "updated.jar"), "new");
        var plan = await new PackMigrationPlanner().BuildAndApplyAsync(oldRoot, candidate, ["world"]);
        Assert.Equal("world-state", await File.ReadAllTextAsync(Path.Combine(candidate, "world", "level.dat")));
        Assert.Equal("motd=user", await File.ReadAllTextAsync(Path.Combine(candidate, "server.properties")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(candidate, "mods", "updated.jar")));
        Assert.Equal("user", await File.ReadAllTextAsync(Path.Combine(candidate, "notes", "mine.txt")));
        Assert.False(File.Exists(Path.Combine(candidate, "mods", "obsolete.jar")));
        Assert.Contains(plan.Conflicts, item => item.Contains("obsolete.jar", StringComparison.Ordinal));
        Assert.Contains(plan.Changes, item =>
            item.RelativePath == "mods/updated.jar" && item.Change == "Replaced by new pack baseline");
    }

    [Fact]
    public async Task Explicit_user_persistent_path_survives_pack_update()
    {
        var oldRoot = Path.Combine(root, "explicit-old");
        var candidate = Path.Combine(root, "explicit-new");
        Directory.CreateDirectory(Path.Combine(oldRoot, ".chunkpilot"));
        Directory.CreateDirectory(Path.Combine(oldRoot, "pack-storage"));
        Directory.CreateDirectory(Path.Combine(candidate, "pack-storage"));
        await File.WriteAllTextAsync(Path.Combine(oldRoot, ".chunkpilot", "persistent-paths.json"),
            """["pack-storage"]""");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "pack-storage", "database.db"), "user-state");
        await File.WriteAllTextAsync(Path.Combine(candidate, "pack-storage", "database.db"), "default");
        var plan = await new PackMigrationPlanner().BuildAndApplyAsync(oldRoot, candidate, []);
        Assert.Equal("user-state", await File.ReadAllTextAsync(Path.Combine(candidate, "pack-storage", "database.db")));
        Assert.Contains("pack-storage/database.db", plan.PersistentPaths);
    }

    [Fact]
    public async Task Migration_resolutions_support_old_new_and_user_merged_text()
    {
        var oldRoot = Path.Combine(root, "resolution-old");
        var candidate = Path.Combine(root, "resolution-new");
        Directory.CreateDirectory(Path.Combine(oldRoot, "mods"));
        Directory.CreateDirectory(Path.Combine(oldRoot, "config"));
        Directory.CreateDirectory(Path.Combine(candidate, "config"));
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "mods", "user-choice.jar"), "old-jar");
        await File.WriteAllTextAsync(Path.Combine(oldRoot, "config", "pack.toml"), "old=true");
        await File.WriteAllTextAsync(Path.Combine(candidate, "config", "pack.toml"), "new=true");
        var resolutions = new Dictionary<string, MigrationResolution>(StringComparer.OrdinalIgnoreCase)
        {
            ["mods/user-choice.jar"] = new() { Kind = MigrationResolutionKind.KeepOld },
            ["config/pack.toml"] = new()
            {
                Kind = MigrationResolutionKind.UseMergedText,
                MergedContent = "old=true\nnew=true\n"
            }
        };
        var plan = await new PackMigrationPlanner().BuildAndApplyAsync(
            oldRoot, candidate, [], resolutions);
        Assert.Empty(plan.Conflicts);
        Assert.Equal("old-jar", await File.ReadAllTextAsync(
            Path.Combine(candidate, "mods", "user-choice.jar")));
        Assert.Equal("old=true\nnew=true\n", (await File.ReadAllTextAsync(
            Path.Combine(candidate, "config", "pack.toml"))).Replace("\r\n", "\n", StringComparison.Ordinal));

        var secondCandidate = Path.Combine(root, "resolution-new-baseline");
        Directory.CreateDirectory(secondCandidate);
        var newOnly = new Dictionary<string, MigrationResolution>(StringComparer.OrdinalIgnoreCase)
        {
            ["mods/user-choice.jar"] = new() { Kind = MigrationResolutionKind.NewBaseline }
        };
        var newPlan = await new PackMigrationPlanner().BuildAndApplyAsync(
            oldRoot, secondCandidate, [], newOnly);
        Assert.DoesNotContain(newPlan.Conflicts, item => item.Contains("user-choice.jar", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(secondCandidate, "mods", "user-choice.jar")));
    }

    [Fact]
    public void Snapshot_deletion_guards_active_and_last_usable_version()
    {
        var active = new VersionSnapshot { IsActive = true, Verified = true, Health = VersionHealth.Healthy };
        Assert.False(UpdatePolicy.CanDeleteSnapshot(active, [active], out var activeReason));
        Assert.Contains("active", activeReason, StringComparison.OrdinalIgnoreCase);
        var onlySnapshot = new VersionSnapshot { Verified = true, Health = VersionHealth.Healthy };
        Assert.False(UpdatePolicy.CanDeleteSnapshot(onlySnapshot, [onlySnapshot], out var onlyReason));
        Assert.Contains("one verified", onlyReason, StringComparison.OrdinalIgnoreCase);
        var previous = new VersionSnapshot { Verified = true, Health = VersionHealth.Healthy };
        Assert.True(UpdatePolicy.CanDeleteSnapshot(previous, [active, previous], out _));
    }

    [Fact]
    public void Automatic_install_rules_block_beta_migration_and_version_change()
    {
        var check = new UpdateCheckResult
        {
            Compatibility = UpdateCompatibility.CompatibleWithMigrationWarning,
            InstalledVersion = Version("old", DateTimeOffset.MinValue) with { MinecraftVersion = "1.20.1" },
            LatestVersion = Version("beta", DateTimeOffset.Now) with
            {
                ReleaseChannel = ReleaseChannel.Beta,
                MinecraftVersion = "1.21.1"
            }
        };
        var reasons = UpdatePolicy.ValidateAutomaticInstall(check,
            new UpdatePreferences { AutomaticInstallEnabled = true });
        Assert.Contains(reasons, item => item.Contains("stable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reasons, item => item.Contains("compatibility", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(reasons, item => item.Contains("Minecraft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ui_timestamp_uses_twelve_hour_clock()
    {
        var formatted = UpdatePolicy.FormatUiTimestamp(
            new DateTimeOffset(2026, 7, 24, 18, 5, 0, TimeSpan.Zero).ToLocalTime());
        Assert.Matches(@"\d{1,2}:\d{2} (AM|PM)$", formatted);
        Assert.DoesNotContain("18:05", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_state_and_database_v2_migration_preserve_existing_server()
    {
        var paths = new AppDataPaths(Path.Combine(root, "db-v2"));
        paths.EnsureCreated();
        var server = new ServerDefinition { Id = Guid.NewGuid(), Name = "Preserved", RootPath = @"D:\fixture" };
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE servers (id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO servers(id,json,updated_utc) VALUES($id,$json,$updated);
                PRAGMA user_version=2;
                """;
            command.Parameters.AddWithValue("$id", server.Id.ToString("D"));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(server, ProtocolJson.Options));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        Assert.Equal("Preserved", Assert.Single(await store.GetServersAsync()).Name);
        await store.UpsertVersionSnapshotAsync(new VersionSnapshot
        {
            ServerId = server.Id,
            VersionId = "v2",
            VersionName = "Version 2",
            IsActive = true,
            Health = VersionHealth.PendingValidation,
            Verified = true,
            Definition = server
        });
        var pending = Assert.Single(await store.GetVersionSnapshotsAsync(server.Id));
        Assert.Equal(VersionHealth.PendingValidation, pending.Health);
        await store.UpsertVersionSnapshotAsync(pending with { Health = VersionHealth.Healthy });
        Assert.Equal(VersionHealth.Healthy,
            Assert.Single(await store.GetVersionSnapshotsAsync(server.Id)).Health);
        await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await verify.OpenAsync();
        var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, (long)(await version.ExecuteScalarAsync())!);
    }

    private static UpdateSource Source(UpdateProvider provider) => new()
    {
        ServerId = Guid.NewGuid(),
        Provider = provider,
        ProjectId = "pack",
        InstalledVersionId = "v1",
        InstalledVersionName = "Version 1"
    };

    private static PackVersionInfo Version(string id, DateTimeOffset published) => new()
    {
        VersionId = id,
        VersionName = id,
        PublishedAt = published,
        ReleaseChannel = ReleaseChannel.Stable,
        DownloadUrl = "https://example/server.zip",
        FileName = "server.zip"
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(handler(request));
        }
    }

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
        public bool Contains(string key) => values.ContainsKey(key);
        public void SetSecret(string key, string value) => values[key] = value;
        public string? GetSecret(string key) => values.GetValueOrDefault(key);
        public void Delete(string key) => values.Remove(key);
    }
}
