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
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.image"));
        Assert.True(WebUiMethodPolicy.IsAllowed("modpacks.chooseLocal"));
        Assert.True(WebUiMethodPolicy.IsAllowed("creation.chooseLegacyArtifact"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.status"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.save"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.disconnect"));
        Assert.False(WebUiMethodPolicy.IsAllowed("settings.curseforge.openConsole"));
        Assert.True(WebUiMethodPolicy.IsAllowed("versions.install"));
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
        Assert.False(WebUiWindow.RequiresFullPresentationRefresh("connectivity.setMode"));
        Assert.True(WebUiWindow.RequiresFullPresentationRefresh("servers.start"));
        Assert.True(WebUiWindow.RequiresFullPresentationRefresh("backups.create"));
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
}
