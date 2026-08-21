using System.IO.Compression;
using System.Text;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Microsoft.Data.Sqlite;

namespace ChunkPilot.UnitTests;

public sealed class Release13GuidedPlatformTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), "ChunkPilot-13-unit-" + Guid.NewGuid().ToString("N"));

    public Release13GuidedPlatformTests() => Directory.CreateDirectory(root);

    [Theory]
    [InlineData(ServerEcosystem.Paper, true, false, true)]
    [InlineData(ServerEcosystem.Fabric, false, true, false)]
    [InlineData(ServerEcosystem.Hybrid, true, true, false)]
    [InlineData(ServerEcosystem.Vanilla, false, false, true)]
    public void Capabilities_are_centralized_by_evidence(
        ServerEcosystem ecosystem,
        bool plugins,
        bool mods,
        bool vanillaClients)
    {
        var profile = ServerCapabilityPolicy.Build(
            new ServerDefinition { Id = Guid.NewGuid(), Ecosystem = ecosystem, IsManaged = true },
            new ServerCapabilityEvidence
            {
                Edition = ServerEdition.Java,
                Ecosystem = ecosystem,
                HasManagedLaunchProfile = true
            });
        Assert.Equal(plugins, profile.SupportsPlugins);
        Assert.Equal(mods, profile.SupportsMods);
        Assert.Equal(vanillaClients, profile.AllowsUnmodifiedClients);
        Assert.True(profile.SupportsManagedJava);
    }

    [Fact]
    public void Bedrock_hides_Java_only_capabilities()
    {
        var profile = ServerCapabilityPolicy.Build(
            new ServerDefinition { Id = Guid.NewGuid() },
            new ServerCapabilityEvidence { Edition = ServerEdition.Bedrock });
        Assert.False(profile.SupportsManagedJava);
        Assert.False(profile.SupportsMods);
        Assert.False(profile.SupportsPlugins);
        Assert.Contains("managedJava", profile.UnavailableReasons.Keys);
    }

    [Fact]
    public void Vanilla_with_friends_is_private_and_managed()
    {
        var preset = QuickStartPresetFactory.Create(QuickStartKind.VanillaWithFriends);
        Assert.Equal("Vanilla With Friends", preset.ToString());
        Assert.Equal(InstallSourceType.Vanilla, preset.SourceType);
        Assert.True(preset.ManagedJava);
        Assert.True(preset.WhitelistEnabled);
        Assert.True(preset.OnlineMode);
        Assert.True(preset.DailyBackup);
        Assert.Equal("true", preset.Properties["white-list"]);
    }

    [Fact]
    public void Catalog_excludes_client_only_and_selects_newest_stable_exact_version()
    {
        var item = new CatalogItem
        {
            Name = "Fixture",
            InstallationSupport = InstallationSupportState.FullyAutomated,
            Versions =
            [
                new CatalogVersion { VersionId = "old", MinecraftVersion = "1.21.1", Loader = "fabric",
                    HasServerPackage = true, PublishedAt = DateTimeOffset.Parse("2026-01-01") },
                new CatalogVersion { VersionId = "new", MinecraftVersion = "1.21.1", Loader = "fabric",
                    HasServerPackage = true, PublishedAt = DateTimeOffset.Parse("2026-02-01") },
                new CatalogVersion { VersionId = "beta", MinecraftVersion = "1.21.1", Loader = "fabric",
                    HasServerPackage = true, ReleaseChannel = ReleaseChannel.Beta,
                    PublishedAt = DateTimeOffset.Parse("2026-03-01") }
            ]
        };
        var clientOnly = item with
        {
            Name = "Client only",
            InstallationSupport = InstallationSupportState.ClientOnly
        };
        var query = new CatalogQuery { MinecraftVersion = "1.21.1", Loader = "fabric" };
        var results = CatalogPolicy.Filter([clientOnly, item], query);
        Assert.Single(results);
        Assert.Equal("new", CatalogPolicy.SelectDefaultVersion(item, query)?.VersionId);
    }

    [Theory]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 16)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.1", 21)]
    public void Java_requirement_tracks_Minecraft(string minecraft, int java) =>
        Assert.Equal(java, JavaRuntimePolicy.RequiredMajorForMinecraft(minecraft));

    [Fact]
    public void Java_selection_rejects_32_bit_and_uses_absolute_compatible_runtime()
    {
        var x86 = Runtime(21, "x86", "bad");
        var x64 = Runtime(21, "x64", "good");
        var selected = JavaRuntimePolicy.Select([x86, x64],
            new JavaRuntimeRequirement { MinimumMajor = 21, Require64Bit = true });
        Assert.NotNull(selected);
        Assert.Equal(x64.Id, selected.Id);
        Assert.True(Path.IsPathFullyQualified(selected.JavaPath));
    }

    [Fact]
    public void Class_file_scan_uses_highest_class_not_only_main_class()
    {
        var jar = Path.Combine(root, "fixture.jar");
        using (var archive = ZipFile.Open(jar, ZipArchiveMode.Create))
        {
            WriteClass(archive, "Main.class", 61);
            WriteClass(archive, "nested/Modern.class", 65);
        }
        Assert.Equal(21, JarClassVersionInspector.GetRequiredJavaMajor(jar));
    }

    [Fact]
    public void Lan_address_can_never_be_copied_as_public()
    {
        var configuration = new NetworkConfiguration
        {
            LanAddress = "192.168.1.20:25565",
            PublicAddress = "192.168.1.20:25565",
            PublicAddressExternallyConfirmed = true
        };
        Assert.Throws<InvalidOperationException>(() => NetworkPolicy.CopyPublicAddress(configuration));
        Assert.Contains("UDP", NetworkPolicy.Guidance(NetworkMode.PortForwarding, bedrock: true)[0]);
        Assert.Contains("TCP", NetworkPolicy.Guidance(NetworkMode.PortForwarding, bedrock: false)[0]);
    }

    [Fact]
    public void Access_center_explains_the_blocking_rule()
    {
        var player = new UnifiedPlayerAccess { Name = "玩家", PlayerBanned = true, BanReason = "fixture" };
        Assert.Contains("player ban", AccessControlPolicy.ExplainJoin(player, whitelistEnabled: true));
        Assert.Contains("fixture", AccessControlPolicy.ExplainJoin(player, whitelistEnabled: true));
    }

    [Fact]
    public async Task Access_center_unifies_whitelist_operators_player_and_IP_bans()
    {
        var serverRoot = Path.Combine(root, "access-unified");
        Directory.CreateDirectory(serverRoot);
        var playerId = Guid.NewGuid();
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "whitelist.json"),
            $$"""[{"uuid":"{{playerId:D}}","name":"Player_One"}]""");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "ops.json"),
            $$"""[{"uuid":"{{playerId:D}}","name":"Player_One","level":4}]""");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "banned-players.json"),
            $$"""[{"uuid":"{{playerId:D}}","name":"Player_One","reason":"fixture","expires":"forever"}]""");
        await File.WriteAllTextAsync(Path.Combine(serverRoot, "banned-ips.json"),
            """[{"ip":"203.0.113.10","reason":"fixture IP","expires":"forever"}]""");
        var service = new WhitelistService(new SafeFileService(
            new AppDataPaths(Path.Combine(root, "access-data"))));
        var entries = await service.ReadUnifiedAsync(new ServerDefinition
        {
            Id = Guid.NewGuid(),
            RootPath = serverRoot
        });
        var player = Assert.Single(entries, entry => entry.Uuid == playerId);
        Assert.True(player.Whitelisted);
        Assert.True(player.Operator);
        Assert.True(player.PlayerBanned);
        Assert.Contains(entries, entry =>
            entry.IpAddress == "203.0.113.10" && entry.IpBanned);
    }

    [Fact]
    public void Gamerules_are_versioned_and_dangerous_values_warn()
    {
        Assert.DoesNotContain(GamerulePolicy.Supported("1.16.5"),
            rule => rule.Name == "playersSleepingPercentage");
        Assert.Contains(GamerulePolicy.Supported("1.21.1"),
            rule => rule.Name == "playersSleepingPercentage");
        Assert.NotNull(GamerulePolicy.Validate("randomTickSpeed", "1000"));
    }

    [Fact]
    public void Automation_is_no_code_and_external_programs_require_approval()
    {
        var recipe = new AutomationRecipe
        {
            Name = "Fixture",
            ServerId = Guid.NewGuid(),
            Trigger = AutomationTriggerKind.ServerReady,
            Actions = [new AutomationStep { Action = AutomationActionKind.ExternalProgram, Value = "tool.exe" }]
        };
        Assert.Contains(AutomationPolicy.Validate(recipe),
            error => error.Contains("disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Built_in_automation_recipes_are_disabled_reviewable_and_script_free()
    {
        var recipes = AutomationRecipeFactory.BuiltIns(Guid.NewGuid());
        Assert.Equal(7, recipes.Count);
        Assert.All(recipes, recipe =>
        {
            Assert.False(recipe.Enabled);
            Assert.DoesNotContain(recipe.Actions,
                action => action.Action == AutomationActionKind.ExternalProgram);
            Assert.Empty(AutomationPolicy.Validate(recipe));
        });
    }

    [Fact]
    public void Crossplay_requires_compatible_Java_server_Geyser_and_free_UDP_port()
    {
        var capabilities = ServerCapabilityPolicy.Build(
            new ServerDefinition { Id = Guid.NewGuid(), Ecosystem = ServerEcosystem.Paper },
            new ServerCapabilityEvidence
            {
                Edition = ServerEdition.Java,
                Ecosystem = ServerEcosystem.Paper
            });
        var configuration = new CrossplayConfiguration
        {
            ServerId = capabilities.ServerId,
            GeyserEnabled = true,
            FloodgateEnabled = true,
            BedrockPort = 19132
        };
        Assert.Empty(CrossplayPolicy.Validate(capabilities, configuration));
        Assert.Contains(CrossplayPolicy.Validate(capabilities, configuration, [19132]),
            error => error.Contains("already assigned", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Game rules replaced the preset selector, so the rules themselves are what must hold: a version
    /// gate, a typed control per rule, and validation that refuses a value the server would reject.
    /// </summary>
    [Fact]
    public void Game_rules_are_version_gated_typed_and_validated()
    {
        var modern = GamerulePolicy.Supported("1.21.1");
        Assert.Contains(modern, rule => rule.Name == "playersSleepingPercentage");
        Assert.Contains(modern, rule => rule.Name == "keepInventory");

        // playersSleepingPercentage arrived in 1.17; keepInventory has been there since 1.4.2.
        var ancient = GamerulePolicy.Supported("1.16.5");
        Assert.DoesNotContain(ancient, rule => rule.Name == "playersSleepingPercentage");
        Assert.Contains(ancient, rule => rule.Name == "keepInventory");

        Assert.Equal(GameruleValueKind.Boolean, GamerulePolicy.Find("keepInventory")!.Kind);
        Assert.Equal(GameruleValueKind.WholeNumber, GamerulePolicy.Find("randomTickSpeed")!.Kind);

        Assert.Null(GamerulePolicy.Validate("keepInventory", "true"));
        Assert.NotNull(GamerulePolicy.Validate("keepInventory", "yes"));
        Assert.Null(GamerulePolicy.Validate("randomTickSpeed", "3"));
        Assert.NotNull(GamerulePolicy.Validate("randomTickSpeed", "1000"));
        Assert.NotNull(GamerulePolicy.Validate("notARule", "true"));
    }

    /// <summary>The reply to a gamerule query is the only source of a shown value.</summary>
    [Fact]
    public void A_gamerule_reply_is_parsed_and_anything_else_is_ignored()
    {
        var parsed = GamerulePolicy.ParseReportedValue(
            "[19:55:01] [Server thread/INFO]: Gamerule keepInventory is currently set to: false");
        Assert.Equal("keepInventory", parsed!.Value.Name);
        Assert.Equal("false", parsed.Value.Value);

        Assert.Null(GamerulePolicy.ParseReportedValue(
            "[19:55:01] [Server thread/INFO]: <Someone> gamerule keepInventory is great"));
        Assert.Null(GamerulePolicy.ParseReportedValue(
            "Gamerule notARealRule is currently set to: false"));
    }

    /// <summary>
    /// A refusal names the rule, which is how ChunkPilot learns a server does not have it.
    /// </summary>
    /// <remarks>
    /// Minecraft 26.2 answers every rule name in the policy this way, so this parse is the difference
    /// between offering sixteen dead switches and saying the version names them differently.
    /// </remarks>
    [Fact]
    public void A_refused_gamerule_is_recognised_from_the_servers_echo()
    {
        Assert.Equal("keepInventory", GamerulePolicy.ParseRejectedRule(
            "[21:49:26] [Server thread/INFO]: gamerule keepInventory<--[HERE]"));
        Assert.Equal("randomTickSpeed", GamerulePolicy.ParseRejectedRule(
            "[21:49:26] [Server thread/INFO]: gamerule randomTickSpeed 3<--[HERE]"));

        Assert.Null(GamerulePolicy.ParseRejectedRule(
            "[21:49:26] [Server thread/INFO]: Incorrect argument for command"));
        Assert.Null(GamerulePolicy.ParseRejectedRule(
            "[21:49:26] [Server thread/INFO]: gamerule notARealRule<--[HERE]"));
        Assert.Null(GamerulePolicy.ParseRejectedRule(
            "[21:49:26] [Server thread/INFO]: Gamerule keepInventory is currently set to: false"));
    }

    [Fact]
    public void Manual_stop_suppresses_restart_and_safe_restart_allows_one_intended_start()
    {
        Assert.False(LifecycleIntentPolicy.ShouldCrashRestart(
            LifecycleIntentKind.ManualStop, true, true, 0, 3));
        Assert.False(LifecycleIntentPolicy.ShouldCrashRestart(
            LifecycleIntentKind.ApplicationExit, true, true, 0, 3));
        Assert.Equal(1, LifecycleIntentPolicy.IntendedRestartAllowance(LifecycleIntentKind.SafeRestart));
        Assert.Equal(0, LifecycleIntentPolicy.IntendedRestartAllowance(LifecycleIntentKind.ManualStop));
    }

    [Theory]
    [InlineData(LifecycleIntentKind.None)]
    [InlineData(LifecycleIntentKind.ManualStart)]
    [InlineData(LifecycleIntentKind.CrashRecovery)]
    [InlineData(LifecycleIntentKind.SafeRestart)]
    [InlineData(LifecycleIntentKind.ScheduledRestart)]
    public void Previous_running_evidence_never_authorizes_startup(LifecycleIntentKind intent)
    {
        var definition = new ServerDefinition { AutoStart = false };
        var stale = new ServerRunningState(definition.Id, AutostartMode.RestorePreviousRunningState,
            true, intent, DateTimeOffset.UtcNow);

        Assert.False(StartupRestorationPolicy.IsAuthorized(definition, stale));
        Assert.Equal(AutostartMode.Never, StartupRestorationPolicy.EffectiveMode(definition, stale));
    }

    [Theory]
    [InlineData(AutostartMode.AgentStart)]
    [InlineData(AutostartMode.WindowsLoginWithDelay)]
    public void Explicit_autostart_modes_authorize_startup(AutostartMode mode)
    {
        var definition = new ServerDefinition { AutoStart = false };
        var policy = new ServerRunningState(definition.Id, mode, false,
            LifecycleIntentKind.ManualStop, DateTimeOffset.UtcNow);

        Assert.True(StartupRestorationPolicy.IsAuthorized(definition, policy));
        Assert.Equal(mode, StartupRestorationPolicy.EffectiveMode(definition, policy));
    }

    [Fact]
    public void Server_autostart_setting_is_explicit_startup_authority_without_runtime_evidence()
    {
        var definition = new ServerDefinition { AutoStart = true };

        Assert.True(StartupRestorationPolicy.IsAuthorized(definition, null));
        Assert.Equal(AutostartMode.AgentStart, StartupRestorationPolicy.EffectiveMode(definition, null));
    }

    [Fact]
    public void Process_identity_rejects_PID_reuse()
    {
        var expected = Identity();
        var observed = new ObservedProcessIdentity
        {
            ProcessId = expected.ProcessId,
            ProcessStartTime = expected.ProcessStartTime.AddMinutes(1),
            ProcessCreationTicks = expected.ProcessCreationTicks + 1,
            ExecutablePath = expected.ExecutablePath,
            WorkingDirectory = expected.WorkingDirectory,
            CommandSignature = expected.CommandSignature
        };
        Assert.False(ProcessIdentityPolicy.Matches(expected, observed, out var reason));
        Assert.Contains("reused", reason);
    }

    [Fact]
    public void Live_process_identity_requires_exact_raw_creation_identity_and_executable()
    {
        var expected = Identity();

        Assert.True(ProcessIdentityPolicy.MatchesProcessInstance(
            expected, expected.ProcessId, expected.ProcessCreationTicks, expected.ExecutablePath, out _));
        Assert.False(ProcessIdentityPolicy.MatchesProcessInstance(
            expected, expected.ProcessId, expected.ProcessCreationTicks + 1, expected.ExecutablePath,
            out var reusedReason));
        Assert.Contains("reused", reusedReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(ProcessIdentityPolicy.MatchesProcessInstance(
            expected, expected.ProcessId, expected.ProcessCreationTicks, expected.ExecutablePath + ".other",
            out var executableReason));
        Assert.Contains("Executable", executableReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(ProcessIdentityPolicy.MatchesProcessInstance(
            expected with { ProcessCreationTicks = ProcessCreationIdentity.Unknown }, expected.ProcessId,
            expected.ProcessCreationTicks, expected.ExecutablePath, out var legacyReason));
        Assert.Contains("legacy", legacyReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Content_reconciliation_finds_duplicate_ID_hash_and_sideload()
    {
        var result = ContentReconciliationPolicy.Reconcile(
        [
            new ContentInventoryIdentity { Id = "mod", Sha256 = "A", ProviderManaged = true },
            new ContentInventoryIdentity { Id = "mod", Sha256 = "B", ProviderManaged = false },
            new ContentInventoryIdentity { Id = "other", Sha256 = "A", ProviderManaged = true }
        ]);
        Assert.Single(result.DuplicateIds);
        Assert.Single(result.DuplicateHashes);
        Assert.Single(result.Sideloaded);
    }

    [Fact]
    public async Task Canonical_path_lock_serializes_equivalent_paths()
    {
        var manager = new CanonicalPathLockManager();
        var path = Path.Combine(root, "folder", "..", "file.txt");
        await using var first = await manager.AcquireAsync(path);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.AcquireAsync(Path.Combine(root, "file.txt"), timeout.Token));
    }

    [Fact]
    public async Task File_editor_rejects_external_change_after_load()
    {
        var paths = new AppDataPaths(Path.Combine(root, "file-appdata"));
        var serverRoot = Path.Combine(root, "file-server");
        Directory.CreateDirectory(serverRoot);
        var path = Path.Combine(serverRoot, "server.properties");
        await File.WriteAllTextAsync(path, "motd=one\n", Encoding.UTF8);
        var service = new SafeFileService(paths);
        var loaded = await service.ReadTextAsync(serverRoot, "server.properties");
        await File.WriteAllTextAsync(path, "motd=external\n", Encoding.UTF8);
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            service.WriteTextAtomicAsync(serverRoot, loaded with { Content = "motd=mine\n" }));
        Assert.Contains("changed outside", exception.Message);
        Assert.Equal("motd=external\n", await File.ReadAllTextAsync(path, Encoding.UTF8));
    }

    [Fact]
    public void Datapack_validation_reads_pack_metadata_and_version()
    {
        var pack = Path.Combine(root, "datapack");
        Directory.CreateDirectory(pack);
        File.WriteAllText(Path.Combine(pack, "pack.mcmeta"),
            """{"pack":{"pack_format":48,"description":"世界"}}""", Encoding.UTF8);
        var result = new DatapackService().Inspect(pack, "1.21.1");
        Assert.True(result.Valid);
        Assert.Equal("世界", result.Description);
        Assert.Equal(CompatibilityState.Compatible, result.Compatibility);
    }

    [Fact]
    public async Task Current_schema_preserves_existing_server_and_round_trips_Unicode()
    {
        var paths = new AppDataPaths(Path.Combine(root, "migration"));
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        var definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "世界 Сервер",
            RootPath = Path.Combine(root, "世界"),
            Executable = "java.exe",
            WorkingDirectory = Path.Combine(root, "世界")
        };
        await store.UpsertServerAsync(definition);
        await store.InitializeAsync();
        Assert.Equal(definition.Name, (await store.GetServersAsync()).Single().Name);
        await using var connection = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Current_schema_migrates_a_1_2_schema_v3_fixture_without_losing_server()
    {
        var paths = new AppDataPaths(Path.Combine(root, "migration-v3"));
        paths.EnsureCreated();
        var definition = new ServerDefinition
        {
            Id = Guid.NewGuid(),
            Name = "1.2 Preserved",
            RootPath = @"D:\fixture-1-2"
        };
        await using (var connection = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE servers (id TEXT PRIMARY KEY, json TEXT NOT NULL, updated_utc TEXT NOT NULL);
                INSERT INTO servers(id,json,updated_utc) VALUES($id,$json,$updated);
                PRAGMA user_version=3;
                """;
            command.Parameters.AddWithValue("$id", definition.Id.ToString("D"));
            command.Parameters.AddWithValue(
                "$json",
                System.Text.Json.JsonSerializer.Serialize(definition, ProtocolJson.Options));
            command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await using var store = new ChunkPilotStore(paths);
        await store.InitializeAsync();
        Assert.Equal("1.2 Preserved", Assert.Single(await store.GetServersAsync()).Name);
        await using var verify = new SqliteConnection($"Data Source={paths.DatabasePath}");
        await verify.OpenAsync();
        await using var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        Assert.Equal(6L, (long)(await version.ExecuteScalarAsync())!);
    }

    private ManagedJavaRuntime Runtime(int major, string architecture, string name) => new()
    {
        MajorVersion = major,
        Architecture = architecture,
        JavaPath = Path.GetFullPath(Path.Combine(root, name, "bin", "java.exe")),
        Health = RuntimeHealth.Healthy
    };

    private ProcessIdentity Identity()
    {
        var executable = Path.Combine(root, "java.exe");
        var working = Path.Combine(root, "server");
        return new ProcessIdentity
        {
            ServerId = Guid.NewGuid(),
            ProcessId = 1234,
            ProcessStartTime = DateTimeOffset.UtcNow,
            ProcessCreationTicks = 638907102030405060,
            ExecutablePath = executable,
            WorkingDirectory = working,
            CommandSignature = ProcessIdentityPolicy.Signature(executable, "-jar server.jar", working)
        };
    }

    private static void WriteClass(ZipArchive archive, string name, ushort major)
    {
        var entry = archive.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write([0xCA, 0xFE, 0xBA, 0xBE, 0, 0, (byte)(major >> 8), (byte)major]);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
