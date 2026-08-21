using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// The Direct internet surface: what it offers, what it refuses to offer, and what it is allowed to say.
/// </summary>
public sealed class DirectInternetViewModelTests
{
    private static readonly Guid ServerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Router_mapping_is_off_for_a_server_that_has_never_opted_in()
    {
        var (model, client) = await ReadyAsync();

        Assert.False(model.RouterMapping.Enabled);
        Assert.Equal(RouterMappingPhase.Off, model.RouterMapping.Phase);
        Assert.Equal("Not set up", model.DirectInternetTitle);
        Assert.Equal(AppTone.Neutral, model.DirectInternetTone);
        // Opening the workspace reads state and nothing else.
        Assert.Contains("GetRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.DoesNotContain("CheckRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.DoesNotContain("EnableRouterMapping", client.Operations, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Selecting_direct_internet_reveals_the_surface_without_touching_the_router()
    {
        var (model, client) = await ReadyAsync();

        Assert.False(model.IsDirectInternetSelected);
        model.SelectedNetworkMode = NetworkMode.PortForwarding;

        Assert.True(model.IsDirectInternetSelected);
        Assert.DoesNotContain("CheckRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.DoesNotContain("EnableRouterMapping", client.Operations, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_capability_check_never_enables_anything_by_itself()
    {
        var (model, client) = await ReadyAsync();
        client.State = State(RouterMappingPhase.Supported);

        await model.CheckDirectInternetCommand.ExecuteAsync(null);

        Assert.Contains("CheckRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.DoesNotContain("EnableRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.Equal("Your router can be set up automatically", model.DirectInternetTitle);
    }

    [Fact]
    public async Task A_supported_router_opens_the_confirmation_and_stops_there()
    {
        var (model, client) = await ReadyAsync();
        client.State = State(RouterMappingPhase.Supported);

        await model.CheckDirectInternetCommand.ExecuteAsync(null);

        Assert.True(model.ShowsDirectInternetConsent);
        Assert.False(model.ShowsDirectInternetPrimaryAction);
        Assert.Equal(4, model.DirectInternetConsentPoints.Count);
        Assert.Contains(model.DirectInternetConsentPoints,
            point => point.Contains("25565", StringComparison.Ordinal));
        Assert.Contains(model.DirectInternetConsentPoints,
            point => point.Contains("does not guarantee", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("EnableRouterMapping", client.Operations, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Only_the_confirmation_creates_a_mapping_and_it_carries_consent()
    {
        var (model, client) = await ReadyAsync();
        client.State = State(RouterMappingPhase.Supported);
        await model.CheckDirectInternetCommand.ExecuteAsync(null);
        client.State = State(RouterMappingPhase.Active, enabled: true, external: "203.0.113.7");

        await model.ConfirmDirectInternetCommand.ExecuteAsync(null);

        Assert.Contains("EnableRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.True(client.LastEnableRequest!.ConsentGranted);
        Assert.False(model.ShowsDirectInternetConsent);
        Assert.Equal(RouterMappingPhase.Active, model.RouterMapping.Phase);
    }

    [Fact]
    public async Task Dismissing_the_confirmation_leaves_the_router_untouched()
    {
        var (model, client) = await ReadyAsync();
        client.State = State(RouterMappingPhase.Supported);
        await model.CheckDirectInternetCommand.ExecuteAsync(null);

        model.CancelDirectInternetConsentCommand.Execute(null);

        Assert.False(model.ShowsDirectInternetConsent);
        Assert.True(model.ShowsDirectInternetPrimaryAction);
        Assert.DoesNotContain("EnableRouterMapping", client.Operations, StringComparer.Ordinal);
    }

    /// <summary>The central product rule, asserted on the copy the user actually sees.</summary>
    [Fact]
    public async Task An_active_mapping_is_never_described_as_reachable()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true, external: "203.0.113.7");

        Assert.Equal("Router port is open", model.DirectInternetTitle);
        Assert.Equal("Port open", model.DirectInternetBadge);
        foreach (var text in new[] { model.DirectInternetTitle, model.DirectInternetSummary, model.DirectInternetBadge })
        {
            Assert.DoesNotContain("publicly reachable", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("friends can connect", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reachable from the internet", text, StringComparison.OrdinalIgnoreCase);
        }
        // Success tone would imply a confirmed end-to-end result, which nothing here has confirmed.
        Assert.NotEqual(AppTone.Success, model.DirectInternetTone);
    }

    [Fact]
    public async Task The_router_reported_address_is_labelled_as_the_routers_claim()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true, external: "203.0.113.7");

        Assert.True(model.ShowsRouterReportedAddress);
        Assert.Equal("Router-reported address", model.RouterReportedAddressLabel);
        Assert.Equal("203.0.113.7:25565", model.RouterReportedEndpoint);
        Assert.Contains("not verified", model.RouterReportedAddressCaveat, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_router_reported_address_means_no_address_row_at_all()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true);

        Assert.False(model.ShowsRouterReportedAddress);
        Assert.Equal("", model.RouterReportedEndpoint);
    }

    [Fact]
    public async Task A_shared_address_space_result_warns_about_another_network_layer_without_certainty()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true, external: "100.72.4.9");

        Assert.True(model.ShowsUpstreamNetworkNotice);
        Assert.Contains("appears to be behind another network layer", model.UpstreamNetworkNotice,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CGNAT", model.UpstreamNetworkNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Shared address space (RFC 6598)", model.DirectInternetAddressClassLabel);
    }

    [Fact]
    public async Task A_conflict_explains_that_chunkpilot_will_not_change_someone_elses_entry()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Conflict, enabled: true) with
        {
            Failure = RouterMappingFailure.ForeignMappingPresent
        };

        Assert.Equal("That port is already in use on your router", model.DirectInternetTitle);
        Assert.Contains("won't change a setting it didn't create", model.DirectInternetSummary,
            StringComparison.Ordinal);
        Assert.Equal(AppTone.Warning, model.DirectInternetTone);
    }

    [Fact]
    public async Task An_unavailable_router_offers_a_retry_and_does_not_pretend_manual_setup_exists()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Unavailable, enabled: true) with
        {
            Failure = RouterMappingFailure.MechanismUnsupported
        };

        Assert.Equal("Check again", model.DirectInternetPrimaryActionText);
        Assert.Contains("didn't offer automatic port forwarding", model.DirectInternetSummary,
            StringComparison.Ordinal);
        Assert.Contains("in your router's own settings", model.DirectInternetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_silent_router_says_capability_is_unknown_rather_than_unsupported()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Undetermined, enabled: true) with
        {
            Failure = RouterMappingFailure.GatewayDidNotRespond
        };

        Assert.Equal("Router did not answer", model.DirectInternetTitle);
        Assert.Contains("can't tell whether", model.DirectInternetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_retained_removal_is_reported_rather_than_hidden()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.NeedsAttention, enabled: true) with
        {
            RemovalPending = true
        };

        Assert.Contains("couldn't confirm", model.DirectInternetSummary, StringComparison.Ordinal);
        Assert.Contains("closed again", model.DirectInternetSummary, StringComparison.Ordinal);
        // Cleanup that has not completed offers a retry and a way out, not another capability check.
        Assert.True(model.ShowsDirectInternetRetry);
        Assert.True(model.ShowsDirectInternetTurnOff);
        Assert.False(model.ShowsDirectInternetPrimaryAction);
    }

    [Fact]
    public async Task While_an_operation_runs_only_cancel_is_offered()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Creating, enabled: true);

        Assert.True(model.IsDirectInternetBusy);
        Assert.True(model.ShowsDirectInternetCancel);
        Assert.False(model.ShowsDirectInternetPrimaryAction);
        Assert.False(model.ShowsDirectInternetTurnOff);
    }

    [Fact]
    public async Task Cancelling_an_operation_asks_the_agent_and_does_not_guess_the_result()
    {
        var (model, client) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Creating, enabled: true);
        client.State = State(RouterMappingPhase.Off);

        await model.CancelDirectInternetOperationCommand.ExecuteAsync(null);

        Assert.Contains("CancelRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.Equal(RouterMappingPhase.Off, model.RouterMapping.Phase);
    }

    [Fact]
    public async Task Turn_off_is_offered_only_once_direct_internet_is_on()
    {
        var (model, client) = await ReadyAsync();
        Assert.False(model.ShowsDirectInternetTurnOff);

        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true);
        Assert.True(model.ShowsDirectInternetTurnOff);

        client.State = State(RouterMappingPhase.Off);
        await model.TurnOffDirectInternetCommand.ExecuteAsync(null);

        Assert.Contains("DisableRouterMapping", client.Operations, StringComparer.Ordinal);
        Assert.False(model.RouterMapping.Enabled);
    }

    [Fact]
    public async Task Technical_details_are_collapsed_until_asked_for()
    {
        var (model, _) = await ReadyAsync();

        Assert.False(model.ShowsDirectInternetTechnicalDetails);
        Assert.Equal("Technical details", model.DirectInternetTechnicalDetailsToggleText);

        model.ToggleDirectInternetTechnicalDetailsCommand.Execute(null);

        Assert.True(model.ShowsDirectInternetTechnicalDetails);
        Assert.Equal("Hide technical details", model.DirectInternetTechnicalDetailsToggleText);
    }

    [Fact]
    public async Task Technical_details_carry_the_exact_mechanism_and_the_providers_own_words()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true, external: "203.0.113.7") with
        {
            Mechanism = RouterMappingMechanism.UpnpIgd,
            GatewayAddress = "192.168.1.1",
            InternalClient = "192.168.1.50",
            LastOperationDetail = "UPnP AddPortMapping accepted TCP 25565."
        };

        Assert.Equal("UPnP IGD", model.DirectInternetMechanismLabel);
        Assert.Equal("TCP", model.DirectInternetTransportLabel);
        Assert.Equal("192.168.1.1", model.DirectInternetGateway);
        Assert.Equal("192.168.1.50:25565", model.DirectInternetInternalEndpoint);
        Assert.Equal("25565", model.DirectInternetExternalPortLabel);
        Assert.Contains("AddPortMapping", model.DirectInternetTechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_permanent_entry_is_described_as_permanent_rather_than_as_a_lease()
    {
        var (model, _) = await ReadyAsync();
        model.RouterMapping = State(RouterMappingPhase.Active, enabled: true) with { LeaseIsFinite = false };

        Assert.Contains("Permanent", model.DirectInternetLeaseLabel, StringComparison.Ordinal);
    }

    /// <summary>Protocol words are expert-only; the everyday copy must stay free of them.</summary>
    [Theory]
    [InlineData(RouterMappingPhase.Off)]
    [InlineData(RouterMappingPhase.Checking)]
    [InlineData(RouterMappingPhase.Supported)]
    [InlineData(RouterMappingPhase.Unavailable)]
    [InlineData(RouterMappingPhase.Undetermined)]
    [InlineData(RouterMappingPhase.Creating)]
    [InlineData(RouterMappingPhase.Active)]
    [InlineData(RouterMappingPhase.Conflict)]
    [InlineData(RouterMappingPhase.NeedsAttention)]
    [InlineData(RouterMappingPhase.Removing)]
    [InlineData(RouterMappingPhase.Reconciling)]
    public void Primary_copy_uses_no_protocol_jargon(RouterMappingPhase phase)
    {
        var state = new RouterMappingState { ServerId = ServerId, Phase = phase, InternalPort = 25565 };
        var copy = $"{DirectInternetPresentation.Title(phase)} {DirectInternetPresentation.Summary(state)} " +
                   $"{DirectInternetPresentation.Badge(phase)}";

        foreach (var word in new[] { "UPnP", "PCP", "NAT-PMP", "SOAP", "SSDP", "IGD", "NAT", "lease", "gateway" })
            Assert.DoesNotContain(word, copy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_phase_has_a_title_a_summary_and_a_badge()
    {
        foreach (var phase in Enum.GetValues<RouterMappingPhase>())
        {
            var state = new RouterMappingState { Phase = phase };
            Assert.NotEqual("", DirectInternetPresentation.Title(phase));
            Assert.NotEqual("", DirectInternetPresentation.Summary(state));
            Assert.NotEqual("", DirectInternetPresentation.Badge(phase));
        }
    }

    private static async Task<(MainViewModel Model, RouterFakeClient Client)> ReadyAsync()
    {
        var client = new RouterFakeClient(ServerId);
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();
        model.SelectedServer = model.Servers[0];
        // The workspace load is fire-and-forget from the selection setter; let it settle.
        for (var attempt = 0; attempt < 200 && !client.Operations.Contains("GetRouterMapping"); attempt++)
            await Task.Delay(10);
        return (model, client);
    }

    private static RouterMappingState State(
        RouterMappingPhase phase, bool enabled = false, string external = "") => new()
    {
        ServerId = ServerId,
        Enabled = enabled,
        ConsentGranted = enabled,
        Phase = phase,
        Mechanism = enabled ? RouterMappingMechanism.UpnpIgd : RouterMappingMechanism.None,
        Transport = MappingTransport.Tcp,
        InternalPort = 25565,
        ExternalPort = 25565,
        RouterReportedExternalAddress = external,
        RouterReportedAddressClass = RouterMappingPolicy.ClassifyExternalAddress(external).Class,
        UpstreamNatSuspected = RouterMappingPolicy.ClassifyExternalAddress(external).SuggestsUpstreamNat,
        LeaseIsFinite = true
    };

    private sealed class RouterFakeClient(Guid serverId) : IAgentClient
    {
        public List<string> Operations { get; } = [];
        public RouterMappingState State { get; set; } = new();
        public EnableRouterMappingRequest? LastEnableRequest { get; private set; }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            lock (Operations)
                Operations.Add(operation);
            if (operation == "EnableRouterMapping")
                LastEnableRequest = (EnableRouterMappingRequest)payload!;
            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot { LanAddress = "192.168.1.50" },
                    Servers = [Snapshot()]
                },
                "GetRouterMapping" or "CheckRouterMapping" or "EnableRouterMapping" or
                    "DisableRouterMapping" or "CancelRouterMapping" => State,
                "GetExternalReachability" or "CancelExternalReachability" =>
                    new ExternalReachabilityState { ServerId = serverId },
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
                Name = "Fixture",
                RootPath = @"C:\fixture",
                Port = 25565
            },
            State = ServerState.Stopped
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
