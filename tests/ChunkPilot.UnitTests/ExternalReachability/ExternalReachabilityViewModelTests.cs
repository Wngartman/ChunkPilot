using System.Xml.Linq;
using ChunkPilot.App;
using ChunkPilot.Core;
using ChunkPilot.UnitTests.DesignSystem;

namespace ChunkPilot.UnitTests.ExternalReachability;

/// <summary>
/// The External access surface: what it offers, what it refuses to offer, and what it never does on
/// its own.
/// </summary>
public sealed class ExternalReachabilityViewModelTests
{
    private static readonly Guid ServerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private const string Public = "93.184.216.34";

    // ── Nothing happens without a press ──

    [Fact]
    public async Task Opening_the_workspace_reads_state_and_never_checks_anything()
    {
        var (model, client) = await ReadyAsync();

        Assert.Contains("GetExternalReachability", client.Operations, StringComparer.Ordinal);
        Assert.DoesNotContain("CheckExternalReachability", client.Operations, StringComparer.Ordinal);
        Assert.Equal(ExternalReachabilityPhase.NotChecked, model.ExternalReachability.Phase);
    }

    [Fact]
    public async Task Selecting_direct_internet_reveals_the_surface_without_checking_anything()
    {
        var (model, client) = await ReadyAsync();

        model.SelectedNetworkMode = NetworkMode.PortForwarding;

        Assert.True(model.IsDirectInternetSelected);
        Assert.DoesNotContain("CheckExternalReachability", client.Operations, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Repeated_refreshes_never_produce_a_remote_check()
    {
        var (model, client) = await ReadyAsync();
        model.SelectedNetworkMode = NetworkMode.PortForwarding;

        for (var attempt = 0; attempt < 10; attempt++)
            await model.RefreshCommand.ExecuteAsync(null);

        Assert.DoesNotContain("CheckExternalReachability", client.Operations, StringComparer.Ordinal);
        Assert.NotNull(model.ExternalReachability);
    }

    [Fact]
    public async Task The_only_command_that_leaves_this_computer_is_the_deliberate_one()
    {
        var (model, client) = await ReadyAsync();
        model.ExternalReachability = Ready();
        client.External = Verified();

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Equal(1, client.Operations.Count(operation =>
            operation.Equals("CheckExternalReachability", StringComparison.Ordinal)));
        Assert.True(model.ExternalReachability.IsVerified);
    }

    [Fact]
    public async Task A_click_while_a_check_is_running_sends_nothing()
    {
        var (model, client) = await ReadyAsync();
        model.ExternalReachability = Ready() with { Phase = ExternalReachabilityPhase.Checking, Busy = true };

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.DoesNotContain("CheckExternalReachability", client.Operations, StringComparer.Ordinal);
    }

    [Fact]
    public async Task An_ineligible_server_never_reaches_the_pipe_either()
    {
        var (model, client) = await ReadyAsync();
        model.ExternalReachability = Ready() with
        {
            Phase = ExternalReachabilityPhase.Ineligible,
            Blocker = ExternalReachabilityBlocker.ServerNotRunning
        };

        await model.CheckExternalReachabilityCommand.ExecuteAsync(null);

        Assert.DoesNotContain("CheckExternalReachability", client.Operations, StringComparer.Ordinal);
        Assert.False(model.IsExternalReachabilityCheckEnabled);
    }

    // ── Command surface ──

    [Fact]
    public async Task The_action_is_visible_but_disabled_when_a_prerequisite_is_missing()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Ready() with
        {
            Phase = ExternalReachabilityPhase.Ineligible,
            Blocker = ExternalReachabilityBlocker.RouterMappingInactive
        };

        Assert.True(model.ShowsExternalReachabilityCheckAction);
        Assert.False(model.IsExternalReachabilityCheckEnabled);
        Assert.Equal("Check from outside", model.ExternalReachabilityActionText);
        Assert.Equal("The router port isn't open right now", model.ExternalReachabilityTitle);
    }

    [Fact]
    public async Task Checking_offers_cancel_instead_of_another_check()
    {
        var (model, client) = await ReadyAsync();
        model.ExternalReachability = Ready() with { Phase = ExternalReachabilityPhase.Checking, Busy = true };

        Assert.True(model.ShowsExternalReachabilityCancel);
        Assert.False(model.ShowsExternalReachabilityCheckAction);

        client.External = Ready() with { Phase = ExternalReachabilityPhase.Cancelled };
        await model.CancelExternalReachabilityCommand.ExecuteAsync(null);

        Assert.Contains("CancelExternalReachability", client.Operations, StringComparer.Ordinal);
        Assert.Equal(ExternalReachabilityPhase.Cancelled, model.ExternalReachability.Phase);
    }

    [Fact]
    public async Task The_first_use_notice_appears_before_the_first_check_and_stops_afterwards()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Ready();

        Assert.True(model.ShowsExternalReachabilityFirstUseNotice);
        Assert.Contains("25566", model.ExternalReachabilityFirstUseNotice, StringComparison.Ordinal);

        model.ExternalReachability = Verified();
        Assert.False(model.ShowsExternalReachabilityFirstUseNotice);
    }

    [Fact]
    public async Task A_build_without_a_probe_shows_no_first_use_notice_and_offers_no_enabled_action()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = new ExternalReachabilityState
        {
            ServerId = ServerId,
            Phase = ExternalReachabilityPhase.Ineligible,
            Blocker = ExternalReachabilityBlocker.ProbeNotConfigured,
            ProbeConfigured = false
        };

        Assert.False(model.ShowsExternalReachabilityFirstUseNotice);
        Assert.False(model.IsExternalReachabilityCheckEnabled);
        Assert.Contains("no outside check available", model.ExternalReachabilitySummary,
            StringComparison.OrdinalIgnoreCase);
    }

    // ── The Connect card ──

    [Fact]
    public async Task The_public_access_tile_only_shows_an_address_a_check_actually_verified()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Ready();

        Assert.False(model.PublicAccessVerified);
        Assert.Equal("", model.PublicAccessVerifiedEndpoint);

        model.ExternalReachability = Verified();

        Assert.True(model.PublicAccessVerified);
        Assert.Equal($"{Public}:25566", model.PublicAccessVerifiedEndpoint);
        Assert.StartsWith("Verified ", model.PublicAccessVerifiedCaption, StringComparison.Ordinal);
    }

    /// <summary>
    /// The firewall layer's summary line sits directly above External access. It must not claim
    /// reachability is unverified while the layer below it shows a verified endpoint.
    /// </summary>
    [Fact]
    public async Task The_firewall_summary_line_stops_contradicting_a_verified_result()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = new RouterMappingState
        {
            ServerId = ServerId, Enabled = true, Phase = RouterMappingPhase.Active, ExternalPort = 25566
        };
        model.FirewallAccess = new WindowsFirewallState
        {
            ServerId = ServerId, Phase = FirewallAccessPhase.Configured, Configured = true
        };
        model.ExternalReachability = Ready();

        Assert.Contains("has not been verified", model.FirewallCombinedStatus, StringComparison.Ordinal);

        model.ExternalReachability = Verified();

        Assert.DoesNotContain("has not been verified", model.FirewallCombinedStatus, StringComparison.Ordinal);
        Assert.Contains("answered from outside your network", model.FirewallCombinedStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stale_result_stops_showing_a_verified_address()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Verified();
        Assert.True(model.PublicAccessVerified);

        model.ExternalReachability = Verified() with { Phase = ExternalReachabilityPhase.Stale };

        Assert.False(model.PublicAccessVerified);
        Assert.Equal("", model.PublicAccessVerifiedEndpoint);
        Assert.Equal("Not verified for the current setup", model.ExternalReachabilityTitle);
    }

    [Fact]
    public async Task The_unverified_caveat_yields_to_genuine_evidence()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Ready();

        // Without a configured public hostname there is no address for the caveat to qualify.
        Assert.False(model.ShowsPublicAccessNotVerifiedCaveat);

        model.ExternalReachability = Verified();
        Assert.False(model.ShowsPublicAccessNotVerifiedCaveat);
    }

    // ── Technical details ──

    [Fact]
    public async Task Technical_details_carry_evidence_and_no_invented_cause()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Verified();

        Assert.Equal(Public, model.ExternalReachabilityObservedAddress);
        Assert.Equal(Public, model.ExternalReachabilityRouterAddress);
        Assert.Equal("TCP 25566", model.ExternalReachabilityPortLabel);
        Assert.Equal("118 ms", model.ExternalReachabilityConnectTimeLabel);
        Assert.Contains("UpnpIgd", model.ExternalReachabilityEndpointLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unmeasured_connection_is_not_reported_as_zero_milliseconds()
    {
        var (model, _) = await ReadyAsync();
        model.ExternalReachability = Ready() with { Phase = ExternalReachabilityPhase.Unreachable };

        Assert.Equal("Not measured", model.ExternalReachabilityConnectTimeLabel);
    }

    // ── The rendered surface ──

    [Fact]
    public void The_overview_carries_the_external_access_layer_inside_direct_internet()
    {
        var xaml = Xaml();

        Assert.Contains("Text=\"External access\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExternalReachabilitySection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CheckExternalReachabilityCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CancelExternalReachabilityCommand", xaml, StringComparison.Ordinal);
        // It lives inside the Direct internet block, which is itself gated on the chosen method.
        Assert.Contains("Visibility=\"{Binding IsDirectInternetSelected, Converter={StaticResource BoolVisibility}}\"",
            xaml, StringComparison.Ordinal);
    }

    /// <summary>The two layers this milestone must not disturb are still rendered as they were.</summary>
    [Fact]
    public void The_router_and_firewall_layers_are_preserved_above_it()
    {
        var xaml = Xaml();

        Assert.Contains("Text=\"Direct internet\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Windows Firewall\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"WindowsFirewallSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Public access\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"Reachability not verified\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_external_access_control_carries_an_accessible_name_or_visible_text()
    {
        var document = XDocument.Load(Path.Combine(
            DesignSystemFiles.AppProjectDirectory, "Pages", "ServerOverviewPage.xaml"));

        var buttons = document.Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .Where(element => (element.Attribute("Command")?.Value ?? "")
                .Contains("ExternalReachability", StringComparison.Ordinal) ||
                (element.Attribute("Command")?.Value ?? "")
                .Contains("VerifiedPublicEndpoint", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(buttons);
        foreach (var button in buttons)
        {
            var named = button.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" &&
                attribute.Name.NamespaceName.Contains("AutomationProperties", StringComparison.Ordinal));
            var hasText = (button.Attribute("Content")?.Value ?? "").Length > 0;
            Assert.True(named || hasText, $"{button.Attribute("Command")?.Value} needs a name or visible text.");
        }
    }

    /// <summary>Status is announced, not only coloured.</summary>
    [Fact]
    public void The_external_access_status_is_announced_and_the_section_is_named()
    {
        var xaml = Xaml();
        var section = xaml[xaml.IndexOf("x:Name=\"ExternalReachabilitySection\"", StringComparison.Ordinal)..];

        Assert.Contains("AutomationProperties.Name=\"External access\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.LiveSetting=\"Polite\"", section, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ExternalReachabilityBadge}\"", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Wide_technical_content_wraps_rather_than_scrolling_sideways()
    {
        var document = XDocument.Load(Path.Combine(
            DesignSystemFiles.AppProjectDirectory, "Pages", "ServerOverviewPage.xaml"));

        var blocks = document.Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Where(element => (element.Attribute("Text")?.Value ?? "")
                .Contains("ExternalReachability", StringComparison.Ordinal))
            .Where(element => (element.Attribute("Style")?.Value ?? "").Contains("AppMonoText", StringComparison.Ordinal) ||
                              (element.Attribute("Style")?.Value ?? "").Contains("AppSecondaryText", StringComparison.Ordinal) ||
                              (element.Attribute("Style")?.Value ?? "").Contains("AppMutedText", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(blocks);
        foreach (var block in blocks)
            Assert.Equal("Wrap", block.Attribute("TextWrapping")?.Value);
    }

    private static string Xaml() => File.ReadAllText(Path.Combine(
        DesignSystemFiles.AppProjectDirectory, "Pages", "ServerOverviewPage.xaml"));

    private static ExternalReachabilityEndpoint Endpoint() => new()
    {
        ServerId = ServerId,
        PublicAddress = Public,
        ExternalPort = 25566,
        InternalPort = 25566,
        MappingIdentity = "UpnpIgd/Tcp/10.0.0.140:25566->25566",
        RunIdentity = "4114@638000000000000000"
    };

    private static ExternalReachabilityState Ready() => new()
    {
        ServerId = ServerId,
        Phase = ExternalReachabilityPhase.NotChecked,
        ProbeConfigured = true,
        Endpoint = Endpoint(),
        RouterReportedAddress = Public,
        Port = 25566
    };

    private static ExternalReachabilityState Verified() => Ready() with
    {
        Phase = ExternalReachabilityPhase.Reachable,
        CheckedEndpoint = Endpoint(),
        ObservedAddress = Public,
        ConnectMilliseconds = 118,
        CheckedAt = new DateTimeOffset(2026, 8, 8, 19, 42, 0, TimeSpan.Zero)
    };

    private static async Task<(MainViewModel Model, ExternalFakeClient Client)> ReadyAsync()
    {
        var client = new ExternalFakeClient(ServerId);
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();
        model.SelectedServer = model.Servers[0];
        for (var attempt = 0; attempt < 200 && !client.Operations.Contains("GetExternalReachability"); attempt++)
            await Task.Delay(10);
        return (model, client);
    }

    private sealed class ExternalFakeClient(Guid serverId) : IAgentClient
    {
        public List<string> Operations { get; } = [];
        public ExternalReachabilityState External { get; set; } = new() { ServerId = serverId };
        public RouterMappingState Router { get; set; } = new() { ServerId = serverId };

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            lock (Operations)
                Operations.Add(operation);
            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot { LanAddress = "10.0.0.140" },
                    Servers = [Snapshot()]
                },
                "GetExternalReachability" or "CheckExternalReachability" or "CancelExternalReachability" => External,
                "GetRouterMapping" or "CheckRouterMapping" or "EnableRouterMapping" or
                    "DisableRouterMapping" or "CancelRouterMapping" or "RetryRouterMapping" => Router,
                "GetFirewallAccess" or "CheckFirewallAccess" or "CancelFirewallAccess" => new WindowsFirewallState(),
                "GetCapabilities" => new ServerCapabilityProfile(),
                "GetNetworkConfiguration" => new NetworkConfiguration(),
                "ListBackups" => Array.Empty<BackupRecord>(),
                "ListSchedules" => Array.Empty<ScheduleEntry>(),
                "ListFiles" => Array.Empty<FileSystemEntry>(),
                "Inventory" => Array.Empty<ModPluginEntry>(),
                "Diagnostics" => Array.Empty<DiagnosticFinding>(),
                "ListWorlds" => Array.Empty<WorldEntry>(),
                "ListWhitelist" => Array.Empty<WhitelistEntry>(),
                "ListPlayerAccess" => Array.Empty<UnifiedPlayerAccess>(),
                "GetPlayerAccess" => new PlayerAccessSnapshot(),
                "ReadGamerules" => new GameruleStateResponse(),
                "ListAutomationRecipes" => Array.Empty<AutomationRecipe>(),
                "GetCrossplayConfiguration" => new CrossplayConfiguration(),
                "ListDatapacks" => Array.Empty<DatapackInventoryItem>(),
                "GetResourcePackConfiguration" => new ResourcePackConfiguration(),
                "GetSetting" => new TextResponse(""),
                "GetUpdateSource" => (object?)null!,
                "GetUpdatePreferences" => new UpdatePreferences(),
                "ListVersions" => Array.Empty<VersionSnapshot>(),
                "ListUpdateHistory" => Array.Empty<UpdateHistoryEntry>(),
                _ => OperationResult.Ok("ok")
            };
            return Task.FromResult((TResponse)response);
        }

        private ServerSnapshot Snapshot() => new()
        {
            Definition = new ServerDefinition
            {
                Id = serverId,
                Name = "External fixture",
                RootPath = @"C:\fixture",
                Port = 25566
            },
            State = ServerState.Running
        };
    }

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
