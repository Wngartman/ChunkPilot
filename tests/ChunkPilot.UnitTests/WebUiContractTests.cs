using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChunkPilot.App.WebUi;
using ChunkPilot.App;
using ChunkPilot.Core;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Microsoft.Web.WebView2.Core;

namespace ChunkPilot.UnitTests;

public sealed class WebUiContractTests
{
    [Fact]
    public void AgentStartupProbeIsBoundedForAnAbsentAgent()
    {
        Assert.InRange(AgentClient.InitialProbeTimeoutMilliseconds, 25, 500);
    }

    [Fact]
    public void SnapshotChangeDetectionIgnoresOnlyVolatileEnvelopeFields()
    {
        var detector = new WebUiSnapshotChangeDetector();
        Assert.True(detector.HasMeaningfulChanges(JsonNode.Parse("""{"revision":1,"capturedAt":"2026-08-15T00:00:00Z","status":"ready"}""")!));
        Assert.False(detector.HasMeaningfulChanges(JsonNode.Parse("""{"revision":2,"capturedAt":"2026-08-15T00:00:01Z","status":"ready"}""")!));
        Assert.True(detector.HasMeaningfulChanges(JsonNode.Parse("""{"revision":3,"capturedAt":"2026-08-15T00:00:02Z","status":"running"}""")!));
        Assert.Equal(2_000, WebUiSnapshotMapper.MaximumConsoleLines);
    }

    [Fact]
    public void Plugin_load_health_requires_current_log_evidence_instead_of_jar_presence()
    {
        var plugin = new ModPluginEntry { Name = "FixturePlugin", Enabled = true };

        var unknown = WebUiSnapshotMapper.PluginLoadEvidence(plugin, ServerState.Running, []);
        var loaded = WebUiSnapshotMapper.PluginLoadEvidence(plugin, ServerState.Running,
            [new ConsoleLine(1, DateTimeOffset.UtcNow, "stdout", "[Server thread/INFO]: Enabling FixturePlugin v1.0")]);
        var failed = WebUiSnapshotMapper.PluginLoadEvidence(plugin, ServerState.Running,
            [new ConsoleLine(2, DateTimeOffset.UtcNow, "stderr", "Error occurred while enabling FixturePlugin")]);

        Assert.Equal("Unknown", unknown.State);
        Assert.Equal("Loaded", loaded.State);
        Assert.Equal("Failed", failed.State);
        Assert.Equal("Disabled", WebUiSnapshotMapper.PluginLoadEvidence(plugin with { Enabled = false }, ServerState.Stopped, []).State);
    }

    [Fact]
    public void Version_workspace_is_selected_by_the_central_capability_profile()
    {
        Assert.Equal("vanilla", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.Vanilla));
        Assert.Equal("paper", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.Paper));
        Assert.Equal("fabric", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.Fabric));
        Assert.Equal("quilt", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.Quilt));
        Assert.Equal("forge", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.Forge));
        Assert.Equal("neoforge", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.NeoForge));
        Assert.Equal("unsupported", WebUiSnapshotMapper.VersioningCapability(ServerEcosystem.Custom));
    }

    [Fact]
    public void Pack_install_action_requires_an_authoritative_update_available_state()
    {
        var latest = new PackVersionInfo { VersionId = "release-2", VersionName = "Release 2" };
        var check = new UpdateCheckResult
        {
            Status = ServerUpdateStatus.UpToDate,
            LatestVersion = latest,
            Compatibility = UpdateCompatibility.Compatible
        };

        Assert.False(WebUiSnapshotMapper.IsInstallableUpdate(check));
        Assert.True(WebUiSnapshotMapper.IsInstallableUpdate(check with { Status = ServerUpdateStatus.UpdateAvailable }));
        Assert.False(WebUiSnapshotMapper.IsInstallableUpdate(check with
        {
            Status = ServerUpdateStatus.UpdateAvailable,
            Compatibility = UpdateCompatibility.Incompatible
        }));
        Assert.False(WebUiSnapshotMapper.IsInstallableUpdate(check with
        {
            Status = ServerUpdateStatus.UpdateAvailable,
            LatestVersion = null
        }));
    }

    [Fact]
    public void Mark_healthy_requires_confirmation_and_the_exact_selected_pending_version()
    {
        var serverId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();
        var pending = new VersionSnapshot
        {
            Id = snapshotId,
            ServerId = serverId,
            IsActive = true,
            Health = VersionHealth.PendingValidation
        };
        var parameters = new JsonObject
        {
            ["serverId"] = serverId.ToString(),
            ["versionId"] = snapshotId.ToString(),
            ["confirmed"] = true
        };

        Assert.Equal((serverId, snapshotId), WebUiWindow.ValidateMarkHealthyParameters(parameters));
        Assert.Same(pending, WebUiWindow.RequireSelectedPendingValidation(
            serverId, snapshotId, serverId, [pending]));

        var unconfirmed = parameters.DeepClone().AsObject();
        unconfirmed["confirmed"] = false;
        Assert.Throws<ArgumentException>(() => WebUiWindow.ValidateMarkHealthyParameters(unconfirmed));
        Assert.Throws<InvalidOperationException>(() => WebUiWindow.RequireSelectedPendingValidation(
            serverId, snapshotId, Guid.NewGuid(), [pending]));
        Assert.Throws<InvalidOperationException>(() => WebUiWindow.RequireSelectedPendingValidation(
            serverId, Guid.NewGuid(), serverId, [pending]));
        Assert.Throws<InvalidOperationException>(() => WebUiWindow.RequireSelectedPendingValidation(
            serverId, snapshotId, serverId, [pending with { Health = VersionHealth.Healthy }]));
        Assert.Throws<InvalidOperationException>(() => WebUiWindow.RequireSelectedPendingValidation(
            serverId, snapshotId, serverId, [pending with { IsActive = false }]));
    }

    [Fact]
    public void Migration_review_mapping_is_bounded_and_fenced_to_server_target_and_operation()
    {
        var serverId = Guid.NewGuid();
        var operation = MigrationReviewOperation(serverId, "release-2");

        var review = WebUiSnapshotMapper.MapMigrationReview(serverId, "release-2", operation);

        Assert.NotNull(review);
        Assert.Equal(operation.OperationId, review.ReviewOperationId);
        Assert.Equal(2, review.ConflictCount);
        Assert.Equal(3, review.ChangeCount);
        Assert.True(review.CanResolve);
        Assert.All(review.Changes.Take(2), change => Assert.True(change.RequiresResolution));
        Assert.Null(WebUiSnapshotMapper.MapMigrationReview(Guid.NewGuid(), "release-2", operation));
        Assert.Null(WebUiSnapshotMapper.MapMigrationReview(serverId, "release-3", operation));
        Assert.Null(WebUiSnapshotMapper.MapMigrationReview(serverId, "release-2",
            operation with { OperationId = Guid.NewGuid(), Progress = operation.Progress with { State = UpdateOperationState.Failed } }));
    }

    [Fact]
    public void Migration_review_resubmission_requires_exact_complete_non_merged_resolutions()
    {
        var serverId = Guid.NewGuid();
        var operation = MigrationReviewOperation(serverId, "release-2");
        var target = new PackVersionInfo { VersionId = "release-2", VersionName = "Release 2" };
        var parameters = new JsonObject
        {
            ["targetVersionId"] = "release-2",
            ["confirmedMigrationWarnings"] = true,
            ["reviewedOperationId"] = operation.OperationId.ToString(),
            ["migrationResolutions"] = new JsonObject
            {
                ["config/a.toml"] = "KeepOld",
                ["mods/removed.jar"] = "NewBaseline"
            }
        };

        var resolutions = WebUiWindow.ValidateMigrationReviewParameters(parameters, serverId, target, operation);

        Assert.Equal(MigrationResolutionKind.KeepOld, resolutions["config/a.toml"].Kind);
        Assert.Equal(MigrationResolutionKind.NewBaseline, resolutions["mods/removed.jar"].Kind);
        var missing = parameters.DeepClone().AsObject();
        missing["migrationResolutions"]!.AsObject().Remove("mods/removed.jar");
        Assert.Throws<ArgumentException>(() =>
            WebUiWindow.ValidateMigrationReviewParameters(missing, serverId, target, operation));
        var merged = parameters.DeepClone().AsObject();
        merged["migrationResolutions"]!["config/a.toml"] = "UseMergedText";
        Assert.Throws<ArgumentException>(() =>
            WebUiWindow.ValidateMigrationReviewParameters(merged, serverId, target, operation));
        var stale = parameters.DeepClone().AsObject();
        stale["targetVersionId"] = "release-3";
        Assert.Throws<InvalidOperationException>(() =>
            WebUiWindow.ValidateMigrationReviewParameters(stale, serverId, target, operation));
    }

    [Fact]
    public void Players_workspace_follows_game_kind_not_detected_minecraft_ecosystem()
    {
        Assert.True(WebUiSnapshotMapper.HasPlayersWorkspace(new ServerDefinition
        {
            GameKind = ServerGameKind.Minecraft,
            Ecosystem = ServerEcosystem.Custom
        }));
        Assert.True(WebUiSnapshotMapper.HasPlayersWorkspace(new ServerDefinition
        {
            GameKind = ServerGameKind.Minecraft,
            Ecosystem = ServerEcosystem.Unknown
        }));
        Assert.False(WebUiSnapshotMapper.HasPlayersWorkspace(new ServerDefinition
        {
            GameKind = ServerGameKind.Terraria,
            Ecosystem = ServerEcosystem.Custom
        }));
    }

    [Fact]
    public void PresentationRefreshIsAdaptiveWithoutWeakeningActiveServerUpdates()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WebUiWindow.ActivePresentationRefreshInterval);
        Assert.Equal(TimeSpan.FromSeconds(3), WebUiWindow.QuiescentPresentationRefreshInterval);
    }

    [Fact]
    public void WebViewEnvironmentDoesNotLeaveTheCrashUploaderBehind()
    {
        var options = WebUiWindow.CreateEnvironmentOptions("--force-renderer-accessibility");

        Assert.True(options.IsCustomCrashReportingEnabled);
        Assert.Equal("--force-renderer-accessibility", options.AdditionalBrowserArguments);
    }

    [Fact]
    public void RendererRecoveryRetriesOnlyOneLiveRendererFailure()
    {
        Assert.True(WebUiWindow.ShouldRetryRendererFailure(
            false, false, CoreWebView2ProcessFailedKind.RenderProcessExited));
        Assert.True(WebUiWindow.ShouldRetryRendererFailure(
            false, false, CoreWebView2ProcessFailedKind.FrameRenderProcessExited));
        Assert.False(WebUiWindow.ShouldRetryRendererFailure(
            true, false, CoreWebView2ProcessFailedKind.RenderProcessExited));
        Assert.False(WebUiWindow.ShouldRetryRendererFailure(
            false, true, CoreWebView2ProcessFailedKind.RenderProcessExited));
        Assert.False(WebUiWindow.ShouldRetryRendererFailure(
            false, false, CoreWebView2ProcessFailedKind.BrowserProcessExited));
    }

    [Fact]
    public void MethodPolicyIsAnExplicitAllowlist()
    {
        Assert.True(WebUiMethodPolicy.IsAllowed("servers.start"));
        Assert.True(WebUiMethodPolicy.IsAllowed("servers.createManagedCopy"));
        Assert.True(WebUiMethodPolicy.IsAllowed("bridge.cancel"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.begin"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.operations"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.cancel"));
        Assert.True(WebUiMethodPolicy.IsAllowed("files.read"));
        Assert.True(WebUiMethodPolicy.IsAllowed("files.write"));
        Assert.True(WebUiMethodPolicy.IsAllowed("schedules.upsert"));
        Assert.True(WebUiMethodPolicy.IsAllowed("schedules.delete"));
        Assert.True(WebUiMethodPolicy.IsAllowed("appearance.chooseIcon"));
        Assert.True(WebUiMethodPolicy.IsAllowed("players.addAllowlist"));
        Assert.True(WebUiMethodPolicy.IsAllowed("players.setWhitelist"));
        Assert.True(WebUiMethodPolicy.IsAllowed("players.head"));
        Assert.True(WebUiMethodPolicy.IsAllowed("help.openExternal"));
        Assert.True(WebUiMethodPolicy.IsAllowed("plugins.openFolder"));
        Assert.True(WebUiMethodPolicy.IsAllowed("plugins.configFiles"));
        Assert.True(WebUiMethodPolicy.IsAllowed("plugins.saveConfig"));
        Assert.True(WebUiMethodPolicy.IsAllowed("mods.saveConfig"));
        Assert.True(WebUiMethodPolicy.IsAllowed("content.operations"));
        Assert.True(WebUiMethodPolicy.IsAllowed("content.cancel"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.search"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.cache"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.providers"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.versions"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.resolveLink"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.preflight"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.image"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.chooseLocal"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.chooseLegacyArtifact"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.chooseWorld"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.status"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.save"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.disconnect"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.openConsole"));
        Assert.True(WebUiMethodPolicy.IsAllowed("versions.install"));
        Assert.True(WebUiMethodPolicy.IsAllowed("versions.markHealthy"));
        Assert.True(WebUiMethodPolicy.IsAllowed("versions.rollback"));
        Assert.True(WebUiMethodPolicy.IsAllowed("versions.verify"));
        Assert.True(WebUiMethodPolicy.IsAllowed("versions.cancel"));
        Assert.True(WebUiMethodPolicy.IsAllowed("connectivity.setMode"));
        Assert.True(WebUiMethodPolicy.IsAllowed("connectivity.router.confirm"));
        Assert.True(WebUiMethodPolicy.IsAllowed("connectivity.external.check"));
        Assert.True(WebUiMethodPolicy.IsAllowed("connectivity.firewall.confirm"));
        Assert.True(WebUiMethodPolicy.IsAllowed("diagnostics.openLogs"));
        Assert.True(WebUiMethodPolicy.IsAllowed("diagnostics.bundle"));
        Assert.False(WebUiMethodPolicy.IsAllowed("reflection.invoke"));
        Assert.False(WebUiMethodPolicy.IsAllowed("shell.execute"));
        Assert.DoesNotContain(WebUiMethodPolicy.Methods, method => method.Contains('*'));
        Assert.DoesNotContain("servers.create", WebUiMethodPolicy.Methods);
    }

    [Fact]
    public void Ordinary_navigation_and_connectivity_commands_do_not_force_a_full_dashboard_refresh()
    {
        Assert.False(WebUiWindow.RequiresFullPresentationRefresh("workspace.load"));
        Assert.False(WebUiWindow.RequiresFullPresentationRefresh("connectivity.external.check"));
        Assert.False(WebUiWindow.RequiresFullPresentationRefresh("players.head"));
        Assert.False(WebUiWindow.RequiresFullPresentationRefresh("help.openExternal"));
        Assert.Equal("docs.papermc.io", WebUiWindow.RequireAllowedHelpSource("https://docs.papermc.io/paper/basic-troubleshooting/").Host);
        Assert.Throws<ArgumentException>(() => WebUiWindow.RequireAllowedHelpSource("http://docs.papermc.io/"));
        Assert.Throws<ArgumentException>(() => WebUiWindow.RequireAllowedHelpSource("https://example.com/help"));
        Assert.False(WebUiWindow.RequiresFullPresentationRefresh("connectivity.setMode"));
        Assert.True(WebUiWindow.RequiresFullPresentationRefresh("servers.start"));
        Assert.True(WebUiWindow.RequiresFullPresentationRefresh("backups.create"));
    }

    [Fact]
    public void Modpack_images_are_restricted_to_exact_provider_https_hosts()
    {
        Assert.True(WebUiWindow.IsApprovedModpackImageUri(CatalogProvider.CurseForge,
            new Uri("https://media.forgecdn.net/avatars/fixture.png")));
        Assert.True(WebUiWindow.IsApprovedModpackImageUri(CatalogProvider.Modrinth,
            new Uri("https://cdn.modrinth.com/data/fixture.png")));
        Assert.False(WebUiWindow.IsApprovedModpackImageUri(CatalogProvider.CurseForge,
            new Uri("https://forgecdn.net.evil.example/fixture.png")));
        Assert.False(WebUiWindow.IsApprovedModpackImageUri(CatalogProvider.CurseForge,
            new Uri("http://media.forgecdn.net/fixture.png")));
        Assert.False(WebUiWindow.IsApprovedModpackImageUri(CatalogProvider.Modrinth,
            new Uri("https://media.forgecdn.net/fixture.png")));
    }

    [Fact]
    public void Lifecycle_requests_use_prompt_acceptance_instead_of_waiting_for_server_readiness()
    {
        Assert.True(WebUiWindow.IsDeferredLifecycleMethod("servers.start"));
        Assert.True(WebUiWindow.IsDeferredLifecycleMethod("servers.stop"));
        Assert.True(WebUiWindow.IsDeferredLifecycleMethod("servers.restart"));
        Assert.False(WebUiWindow.IsDeferredLifecycleMethod("backups.create"));
        Assert.True(WebUiWindow.IsDeferredOperationMethod("servers.delete"));
        Assert.True(WebUiWindow.IsDeferredOperationMethod("servers.createManagedCopy"));
        Assert.True(WebUiWindow.IsDeferredOperationMethod("servers.start"));
        Assert.True(WebUiWindow.IsDeferredOperationMethod("versions.install"));
        Assert.False(WebUiWindow.IsDeferredOperationMethod("backups.create"));
    }

    [Fact]
    public void Creation_prompt_acceptance_preserves_the_client_operation_identity()
    {
        var operationId = Guid.NewGuid();

        var accepted = WebUiWindow.PromptAcceptedOperation(operationId);

        Assert.True(accepted["accepted"]!.GetValue<bool>());
        Assert.Equal(operationId, accepted["operationId"]!.GetValue<Guid>());
    }

    [Fact]
    public void Managed_content_prompt_acceptance_is_nonterminal_and_client_correlated()
    {
        var operationId = Guid.NewGuid();
        var serverId = Guid.NewGuid();
        var request = new BeginManagedContentInstallRequest(
            serverId, "lithium", "lithium-exact", IncludeDependencies: false,
            RestartIfRunning: true, OperationId: operationId);
        var accepted = new ManagedContentOperationSnapshot
        {
            OperationId = request.OperationId,
            ServerId = request.ServerId,
            Kind = ManagedContentOperationKind.InstallAddon,
            Provider = "Modrinth",
            ProjectId = request.ProjectId,
            VersionId = request.VersionId,
            Progress = new ManagedContentProgress
            {
                Stage = ManagedContentOperationStage.Queued,
                Message = "Queued behind the server's serialized operation gate."
            }
        };

        Assert.Equal(operationId, accepted.OperationId);
        Assert.Equal(serverId, accepted.ServerId);
        Assert.Equal(ManagedContentOperationStage.Queued, accepted.Progress.Stage);
        Assert.False(accepted.IsTerminal);
        Assert.Null(accepted.Success);
        Assert.True(accepted.IsCancellable);
        var json = JsonSerializer.Serialize(accepted, WebUiProtocol.Json);
        Assert.Contains($"\"operationId\":\"{operationId}\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"stage\":\"Queued\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Snapshot_icon_contract_returns_bounded_data_and_explicit_invalidation_refreshes_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChunkPilot-webui-icon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var definition = new ServerDefinition { Id = Guid.NewGuid(), RootPath = root };
            var path = Path.Combine(root, "server-icon.png");
            using (var red = new Image<Rgba32>(64, 64, Color.Red))
                red.SaveAsPng(path);
            var mapper = new WebUiSnapshotMapper();

            var first = mapper.ReadServerIcon(definition);
            Assert.StartsWith("data:image/png;base64,", first, StringComparison.Ordinal);
            Assert.DoesNotContain(root, first, StringComparison.OrdinalIgnoreCase);

            using (var blue = new Image<Rgba32>(64, 64, Color.Blue))
                blue.SaveAsPng(path);
            mapper.InvalidateServerIcon(definition.Id);
            var second = mapper.ReadServerIcon(definition);

            Assert.NotEqual(first, second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WebUi_icon_payload_accepts_only_a_bounded_64_pixel_image()
    {
        using var valid = new Image<Rgba32>(64, 64, Color.Purple);
        using var validStream = new MemoryStream();
        valid.SaveAsPng(validStream);
        var encoded = Convert.ToBase64String(validStream.ToArray());

        Assert.Equal((int)validStream.Length, WebUiIconPayload.Decode64Png(encoded).Length);
        Assert.Throws<InvalidDataException>(() => WebUiIconPayload.Decode64Png("not-base64"));

        using var wrongSize = new Image<Rgba32>(32, 64, Color.Purple);
        using var wrongStream = new MemoryStream();
        wrongSize.SaveAsPng(wrongStream);
        Assert.Throws<InvalidDataException>(() =>
            WebUiIconPayload.Decode64Png(Convert.ToBase64String(wrongStream.ToArray())));

        using var wrongFormat = new MemoryStream();
        valid.SaveAsJpeg(wrongFormat);
        Assert.Throws<InvalidDataException>(() =>
            WebUiIconPayload.Decode64Png(Convert.ToBase64String(wrongFormat.ToArray())));
    }

    [Fact]
    public void RequestAndStructuredErrorRoundTripWithProtocolVersion()
    {
        var request = new WebUiRequest(1, "request-42", "servers.start", new JsonObject { ["serverId"] = Guid.NewGuid() });
        var json = JsonSerializer.Serialize(request, WebUiProtocol.Json);
        var parsed = JsonSerializer.Deserialize<WebUiRequest>(json, WebUiProtocol.Json);
        Assert.NotNull(parsed);
        Assert.Equal(1, parsed.ProtocolVersion);
        Assert.Equal("request-42", parsed.Id);

        var response = new WebUiResponse(1, parsed.Id, false, Error: new("validation", "Select a server first."));
        var responseJson = JsonSerializer.Serialize(response, WebUiProtocol.Json);
        Assert.Contains("\"code\":\"validation\"", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("result", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InboundMessageLimitIsBounded()
    {
        Assert.InRange(WebUiProtocol.MaximumInboundBytes, 16 * 1024, 512 * 1024);
        var oversized = new string('x', WebUiProtocol.MaximumInboundBytes + 1);
        Assert.True(Encoding.UTF8.GetByteCount(oversized) > WebUiProtocol.MaximumInboundBytes);
    }

    [Fact]
    public void FixtureLauncherRemainsAnExplicitDevelopmentRoute()
    {
        Assert.Equal("--webui-fixture", WebUiFixtureLauncher.FixtureArgument);
        Assert.Equal("--render", WebUiFixtureLauncher.RenderArgument);
        Assert.True(WebUiFixtureLauncher.IsTrustedFixtureSource("https://fixture.chunkpilot.local/index.html?fixture=running"));
        Assert.False(WebUiFixtureLauncher.IsTrustedFixtureSource("https://chunkpilot.local/index.html"));
        Assert.False(WebUiFixtureLauncher.IsTrustedFixtureSource("https://fixture.chunkpilot.local.evil.example/index.html"));
        Assert.False(WebUiFixtureLauncher.IsTrustedFixtureSource("http://fixture.chunkpilot.local/index.html"));
    }

    [Fact]
    public void ProductionOriginIsHttpsAndDoesNotUseLocalhost()
    {
        var origin = new Uri(WebUiProtocol.Origin);
        var entryPoint = new Uri(WebUiProtocol.EntryPoint);
        Assert.Equal(Uri.UriSchemeHttps, origin.Scheme);
        Assert.Equal("chunkpilot.local", origin.Host);
        Assert.NotEqual("localhost", origin.Host);
        Assert.NotEqual("127.0.0.1", origin.Host);
        Assert.Equal(origin, new Uri(entryPoint, "."));
        Assert.Equal("/index.html", entryPoint.AbsolutePath);
        Assert.True(WebUiProtocol.IsTrustedSource(WebUiProtocol.Origin));
        Assert.True(WebUiProtocol.IsTrustedSource(WebUiProtocol.EntryPoint));
        Assert.True(WebUiProtocol.IsTrustedSource(WebUiProtocol.EntryPoint + "?fixture=running"));
        Assert.False(WebUiProtocol.IsTrustedSource("http://chunkpilot.local/index.html"));
        Assert.False(WebUiProtocol.IsTrustedSource("https://chunkpilot.local.evil/index.html"));
        Assert.False(WebUiProtocol.IsTrustedSource("https://user@chunkpilot.local/index.html"));
        Assert.False(WebUiProtocol.IsTrustedSource("https://example.com/"));
    }

    [Fact]
    public async Task Server_switch_fences_unstamped_details_until_the_new_identity_finishes_loading()
    {
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        var alpha = SelectionServer(alphaId, "Repeated name");
        var bravo = SelectionServer(bravoId, "Repeated name");
        var client = new SelectionFenceClient(alpha);
        var viewModel = new MainViewModel(client, new SelectionDialogs());
        await viewModel.InitializeAsync();
        viewModel.Servers.Add(bravo);

        viewModel.SelectedServer = alpha;
        await client.FirstCapabilitiesRequest.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.PlayerRows.Add(new ChunkPilot.App.Access.PlayerAccessRow(
            new UnifiedPlayerAccess { Name = "AlphaPlayer", Online = true, Whitelisted = true },
            whitelistEnabled: true,
            serverRunning: true,
            (_, _) => Task.FromResult(true),
            _ => { }));

        viewModel.SelectedServer = bravo;
        var opening = new WebUiSnapshotMapper().Capture(viewModel);

        Assert.Equal(bravoId, opening["selectedServerId"]!.GetValue<Guid>());
        Assert.Equal("Loading", opening["workspace"]!["state"]!.GetValue<string>());
        Assert.Empty(opening["players"]!.AsArray());
        Assert.Empty(viewModel.PlayerRows);

        client.ReleaseCapabilities();
        await WaitUntilAsync(() => viewModel.WebUiDetailsServerId == bravoId, TimeSpan.FromSeconds(2));
        var ready = new WebUiSnapshotMapper().Capture(viewModel);
        Assert.Equal("Ready", ready["workspace"]!["state"]!.GetValue<string>());
        Assert.Empty(ready["players"]!.AsArray());

        viewModel.SelectedServer = null;
        viewModel.SelectedServer = bravo;
        await WaitUntilAsync(() => viewModel.WebUiDetailsServerId == bravoId, TimeSpan.FromSeconds(2));
        Assert.Equal(new[] { alphaId, bravoId, bravoId }, client.CapabilityServerIds);
    }

    [Fact]
    public async Task Update_check_cannot_commit_after_the_selected_server_changes()
    {
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        var alpha = SelectionServer(alphaId, "Alpha");
        var bravo = SelectionServer(bravoId, "Bravo");
        var client = new UpdateCheckFenceClient();
        var viewModel = new MainViewModel(client, new SelectionDialogs());
        viewModel.Servers.Add(alpha);
        viewModel.Servers.Add(bravo);
        viewModel.SelectedServer = alpha;

        var pending = viewModel.CheckForUpdatesForServerAsync(alphaId);
        await client.CheckStarted.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedServer = bravo;
        await WaitUntilAsync(() => viewModel.WebUiDetailsServerId == bravoId, TimeSpan.FromSeconds(2));
        var bravoCheck = new UpdateCheckResult
        {
            ServerId = bravoId,
            Status = ServerUpdateStatus.UpToDate,
            Message = "Bravo is current."
        };
        var bravoVersion = new VersionSnapshot
        {
            ServerId = bravoId,
            VersionId = "bravo-current",
            VersionName = "Bravo current",
            IsActive = true
        };
        viewModel.CurrentUpdateCheck = bravoCheck;
        viewModel.Versions.Add(bravoVersion);

        client.ReleaseCheck();

        Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(bravoCheck, viewModel.CurrentUpdateCheck);
        Assert.Equal(bravoId, viewModel.CurrentUpdateCheck!.ServerId);
        Assert.Equal(new[] { bravoVersion }, viewModel.Versions);
        Assert.Equal(new[] { alphaId }, client.CheckedServerIds);
    }

    [Fact]
    public async Task Mark_healthy_response_cannot_replace_the_newly_selected_servers_version_state()
    {
        var alphaId = Guid.NewGuid();
        var bravoId = Guid.NewGuid();
        var alpha = SelectionServer(alphaId, "Alpha");
        var bravo = SelectionServer(bravoId, "Bravo");
        var client = new UpdateCheckFenceClient();
        var viewModel = new MainViewModel(client, new SelectionDialogs());
        viewModel.Servers.Add(alpha);
        viewModel.Servers.Add(bravo);
        viewModel.SelectedServer = alpha;
        await WaitUntilAsync(() => viewModel.WebUiDetailsServerId == alphaId, TimeSpan.FromSeconds(2));
        var alphaPending = new VersionSnapshot
        {
            ServerId = alphaId,
            VersionId = "alpha-v2",
            VersionName = "Alpha v2",
            IsActive = true,
            Health = VersionHealth.PendingValidation
        };
        viewModel.Versions.Add(alphaPending);

        var pending = viewModel.MarkVersionHealthyFromWebUiAsync(alphaId, alphaPending.Id, retentionDays: 30);
        await client.MarkHealthyStarted.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.SelectedServer = bravo;
        await WaitUntilAsync(() => viewModel.WebUiDetailsServerId == bravoId, TimeSpan.FromSeconds(2));
        var bravoCheck = new UpdateCheckResult
        {
            ServerId = bravoId,
            Status = ServerUpdateStatus.UpToDate,
            Message = "Bravo is current."
        };
        var bravoVersion = new VersionSnapshot
        {
            ServerId = bravoId,
            VersionId = "bravo-current",
            VersionName = "Bravo current",
            IsActive = true,
            Health = VersionHealth.Healthy
        };
        viewModel.CurrentUpdateCheck = bravoCheck;
        viewModel.Versions.Add(bravoVersion);

        client.ReleaseMarkHealthy();

        Assert.True((await pending.WaitAsync(TimeSpan.FromSeconds(2))).Success);
        Assert.Same(bravoCheck, viewModel.CurrentUpdateCheck);
        Assert.Equal(new[] { bravoVersion }, viewModel.Versions);
        Assert.Equal(new[] { (alphaId, alphaPending.Id, 30) }, client.MarkHealthyRequests);
    }

    private static UpdateOperationSnapshot MigrationReviewOperation(Guid serverId, string targetVersionId)
    {
        var operationId = Guid.NewGuid();
        var plan = new MigrationPlan
        {
            Changes =
            [
                new PackFileChange { RelativePath = "config/a.toml", Ownership = FileOwnership.Unknown, Change = "Review", Reason = "Values differ" },
                new PackFileChange { RelativePath = "mods/removed.jar", Ownership = FileOwnership.PackManaged, Change = "Removed", Reason = "Target removed it" },
                new PackFileChange { RelativePath = "mods/new.jar", Ownership = FileOwnership.PackManaged, Change = "Added", Reason = "Target added it" }
            ],
            Conflicts =
            [
                "config/a.toml: old and new pack versions differ.",
                "mods/removed.jar: removed JAR will not be copied into the new active pack."
            ]
        };
        return new UpdateOperationSnapshot
        {
            OperationId = operationId,
            IsTerminal = true,
            Success = false,
            Progress = new UpdateProgress
            {
                OperationId = operationId,
                State = UpdateOperationState.PlanningMigration
            },
            Result = new UpdateExecutionResult
            {
                OperationId = operationId,
                ServerId = serverId,
                TargetVersionId = targetVersionId,
                MigrationPlan = plan
            }
        };
    }

    private static ServerSnapshot SelectionServer(Guid id, string name) => new()
    {
        Definition = new ServerDefinition
        {
            Id = id,
            Name = name,
            RootPath = Path.Combine(Path.GetTempPath(), id.ToString("N")),
            WorkingDirectory = Path.Combine(Path.GetTempPath(), id.ToString("N")),
            Executable = "java.exe"
        },
        State = ServerState.Stopped
    };

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "The selected server detail pass did not reach its identity fence.");
    }

    private sealed class SelectionFenceClient(ServerSnapshot initial) : IAgentClient
    {
        private readonly TaskCompletionSource releaseCapabilities =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource firstCapabilitiesRequest =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int capabilityRequests;

        public Task FirstCapabilitiesRequest => firstCapabilitiesRequest.Task;
        public List<Guid> CapabilityServerIds { get; } = [];

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ReleaseCapabilities() => releaseCapabilities.TrySetResult();

        public async Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default)
        {
            object response;
            switch (operation)
            {
                case "Dashboard":
                    response = new DashboardSnapshot { AgentConnected = true, Servers = [initial] };
                    break;
                case "GetSetting":
                    response = new TextResponse("");
                    break;
                case "GetCapabilities":
                {
                    var serverId = ((ServerIdRequest)payload!).ServerId;
                    CapabilityServerIds.Add(serverId);
                    if (Interlocked.Increment(ref capabilityRequests) == 1)
                        firstCapabilitiesRequest.TrySetResult();
                    await releaseCapabilities.Task.WaitAsync(cancellationToken);
                    response = new ServerCapabilityProfile();
                    break;
                }
                case "GetNetworkConfiguration":
                    response = new NetworkConfiguration { ServerId = ((ServerIdRequest)payload!).ServerId };
                    break;
                default:
                    response = OperationResult.Ok("fixture");
                    break;
            }
            return (TResponse)response;
        }
    }

    private sealed class UpdateCheckFenceClient : IAgentClient
    {
        private readonly TaskCompletionSource checkStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseCheck =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource markHealthyStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseMarkHealthy =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CheckStarted => checkStarted.Task;
        public Task MarkHealthyStarted => markHealthyStarted.Task;
        public List<Guid> CheckedServerIds { get; } = [];
        public List<(Guid ServerId, Guid SnapshotId, int RetentionDays)> MarkHealthyRequests { get; } = [];

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ReleaseCheck() => releaseCheck.TrySetResult();
        public void ReleaseMarkHealthy() => releaseMarkHealthy.TrySetResult();

        public async Task<TResponse> SendAsync<TResponse>(string operation, object? payload = null,
            CancellationToken cancellationToken = default)
        {
            if (operation == "CheckUpdates")
            {
                var serverId = ((CheckUpdatesRequest)payload!).ServerId;
                CheckedServerIds.Add(serverId);
                checkStarted.TrySetResult();
                await releaseCheck.Task.WaitAsync(cancellationToken);
                return (TResponse)(object)new UpdateCheckResult
                {
                    ServerId = serverId,
                    Status = ServerUpdateStatus.UpdateAvailable,
                    Message = "Alpha has an update."
                };
            }
            if (operation == "MarkVersionHealthy")
            {
                var request = (MarkVersionHealthyRequest)payload!;
                MarkHealthyRequests.Add((request.ServerId, request.SnapshotId, request.RetentionDays));
                markHealthyStarted.TrySetResult();
                await releaseMarkHealthy.Task.WaitAsync(cancellationToken);
                return (TResponse)(object)OperationResult.Ok("Alpha update marked healthy.");
            }

            var serverIdFromRequest = payload switch
            {
                ServerIdRequest request => request.ServerId,
                GameruleReadRequest request => request.ServerId,
                _ => Guid.Empty
            };
            object response = operation switch
            {
                "GetCapabilities" => new ServerCapabilityProfile(),
                "GetNetworkConfiguration" => new NetworkConfiguration { ServerId = serverIdFromRequest },
                "ListBackups" => Array.Empty<BackupRecord>(),
                "ListSchedules" => Array.Empty<ScheduleEntry>(),
                "ListFiles" => Array.Empty<FileSystemEntry>(),
                "Inventory" => Array.Empty<ModPluginEntry>(),
                "Diagnostics" => Array.Empty<DiagnosticFinding>(),
                "GetServerProperties" => new ServerPropertiesResponse([], ""),
                "ListWorlds" => Array.Empty<WorldEntry>(),
                "GetPlayerAccess" => new PlayerAccessSnapshot { ServerId = serverIdFromRequest },
                "ReadGamerules" => new GameruleStateResponse { ServerId = serverIdFromRequest },
                "GetUpdateSource" => new UpdateSourceResponse(null),
                "DetectUpdateSource" => new UpdateSourceDetectionResult(),
                "GetUpdatePreferences" => new UpdatePreferences(),
                "GetLatestUpdateCheck" => new UpdateCheckResponse(null),
                "ListVersions" => Array.Empty<VersionSnapshot>(),
                "GetUpdateHistory" => Array.Empty<UpdateHistoryEntry>(),
                "HasCurseForgeApiKey" => new TextResponse("missing"),
                "ListAutomationRecipes" => Array.Empty<AutomationRecipe>(),
                "AutomationRecipeTemplates" => Array.Empty<AutomationRecipe>(),
                "GetCrossplayConfiguration" => new CrossplayConfiguration { ServerId = serverIdFromRequest },
                "ListDatapacks" => Array.Empty<DatapackInventoryItem>(),
                "GetResourcePackConfiguration" => new ResourcePackConfiguration { ServerId = serverIdFromRequest },
                "GetRouterMapping" => new RouterMappingState { ServerId = serverIdFromRequest },
                "GetFirewallAccess" => new WindowsFirewallState { ServerId = serverIdFromRequest },
                "GetExternalReachability" => new ExternalReachabilityState { ServerId = serverIdFromRequest },
                "SetSetting" => OperationResult.Ok("fixture"),
                _ => throw new InvalidOperationException($"Unexpected fixture operation: {operation}")
            };
            return (TResponse)response;
        }
    }

    private sealed class SelectionDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
