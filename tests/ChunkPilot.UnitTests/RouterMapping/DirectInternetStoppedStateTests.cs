using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests.RouterMapping;

/// <summary>
/// What a stopped server with Direct internet set up is allowed to say, and the refresh that makes it
/// say it.
/// </summary>
/// <remarks>
/// On a real router, a mapping for TCP 25566 was created, the owner pressed Stop, Minecraft released
/// the port, and the card went on reporting "Router port is open" with the old lease and the old
/// AddPortMapping result. Durable intent and a live mapping are separate facts; these tests hold them
/// apart in the one place the user actually reads.
/// </remarks>
public sealed class DirectInternetStoppedStateTests
{
    private static readonly Guid ServerId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    [Fact]
    public void A_configured_server_with_no_mapping_never_says_the_port_is_open()
    {
        var model = Model(Inactive());

        Assert.Equal("Direct internet is set up", model.DirectInternetTitle);
        Assert.Equal("Inactive", model.DirectInternetBadge);
        foreach (var text in new[] { model.DirectInternetTitle, model.DirectInternetSummary, model.DirectInternetBadge })
        {
            Assert.DoesNotContain("port is open", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("is sending this port", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("publicly reachable", text, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Contains("Nothing is open", model.DirectInternetSummary, StringComparison.Ordinal);
        Assert.Contains("opens when this server starts", model.DirectInternetSummary, StringComparison.Ordinal);
    }

    /// <summary>Intent stays on; only the live mapping went away.</summary>
    [Fact]
    public void A_configured_server_with_no_mapping_keeps_direct_internet_on()
    {
        var model = Model(Inactive());

        Assert.True(model.RouterMapping.Enabled);
        Assert.True(model.ShowsDirectInternetTurnOff);
        // Nothing left to set up, and no cleanup owed.
        Assert.False(model.ShowsDirectInternetPrimaryAction);
        Assert.False(model.ShowsDirectInternetRetry);
    }

    /// <summary>The three rows that were still describing the mapping that had been withdrawn.</summary>
    [Fact]
    public void A_withdrawn_mapping_is_not_described_as_a_current_one()
    {
        var model = Model(Inactive());

        Assert.Equal("Not established", model.DirectInternetLeaseLabel);
        Assert.DoesNotContain("expires", model.DirectInternetLeaseLabel, StringComparison.OrdinalIgnoreCase);
        // The address a later start would use is still worth showing, but only as a candidate — never
        // as a mapping that exists.
        Assert.Contains("candidate, not mapped", model.DirectInternetInternalEndpoint, StringComparison.Ordinal);
        // The configured port is still worth showing under technical details; it is what a later start
        // will ask for. It must not be joined to the router's address as though it were forwarding.
        Assert.Equal("25566", model.DirectInternetExternalPortLabel);
        Assert.Equal("73.203.43.174", model.RouterReportedEndpoint);
    }

    [Fact]
    public void A_running_server_with_a_mapping_still_reports_it_as_open()
    {
        var model = Model(Active());

        Assert.Equal("Router port is open", model.DirectInternetTitle);
        Assert.Equal("Port open", model.DirectInternetBadge);
        Assert.Contains("expires", model.DirectInternetLeaseLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("10.0.0.140:25566", model.DirectInternetInternalEndpoint);
    }

    [Fact]
    public void A_cleanup_that_failed_asks_for_attention_and_offers_a_retry()
    {
        var model = Model(Inactive() with
        {
            Phase = RouterMappingPhase.NeedsAttention,
            RemovalPending = true,
            Failure = RouterMappingFailure.RemovalFailed,
            LastOperationDetail = "UPnP DeletePortMapping failed with error 501 (ActionFailed)."
        });

        Assert.Equal("Needs attention", model.DirectInternetTitle);
        Assert.Equal(AppTone.Warning, model.DirectInternetTone);
        Assert.Contains("couldn't confirm", model.DirectInternetSummary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stopped", model.DirectInternetSummary, StringComparison.OrdinalIgnoreCase);
        Assert.True(model.ShowsDirectInternetRetry);
        Assert.True(model.ShowsDirectInternetTurnOff);
        // Re-checking the router does not close a port, so it is not what is offered here.
        Assert.False(model.ShowsDirectInternetPrimaryAction);
        Assert.Contains("501", model.DirectInternetTechnicalDetail, StringComparison.Ordinal);
    }

    /// <summary>No phase may describe a stopped, configured server as exposed.</summary>
    [Theory]
    [InlineData(RouterMappingPhase.Off)]
    [InlineData(RouterMappingPhase.Supported)]
    [InlineData(RouterMappingPhase.Inactive)]
    [InlineData(RouterMappingPhase.Unavailable)]
    [InlineData(RouterMappingPhase.Undetermined)]
    [InlineData(RouterMappingPhase.Conflict)]
    [InlineData(RouterMappingPhase.NeedsAttention)]
    [InlineData(RouterMappingPhase.Removing)]
    [InlineData(RouterMappingPhase.Reconciling)]
    [InlineData(RouterMappingPhase.Checking)]
    [InlineData(RouterMappingPhase.Creating)]
    public void Only_an_active_mapping_may_report_a_lease_or_an_open_port(RouterMappingPhase phase)
    {
        var state = Active() with { Phase = phase };

        Assert.Equal("Not established", DirectInternetPresentation.LeaseLabel(state));
        Assert.NotEqual("Port open", DirectInternetPresentation.Badge(phase));
    }

    [Fact]
    public void Every_phase_including_the_new_one_has_copy_free_of_protocol_jargon()
    {
        foreach (var phase in Enum.GetValues<RouterMappingPhase>())
        {
            var state = new RouterMappingState { Phase = phase, InternalPort = 25566 };
            var copy = $"{DirectInternetPresentation.Title(state)} {DirectInternetPresentation.Summary(state)} " +
                       $"{DirectInternetPresentation.Badge(phase)}";

            Assert.NotEqual("", DirectInternetPresentation.Title(state));
            Assert.NotEqual("", DirectInternetPresentation.Summary(state));
            Assert.NotEqual("", DirectInternetPresentation.Badge(phase));
            foreach (var word in new[] { "UPnP", "PCP", "NAT-PMP", "SOAP", "SSDP", "IGD", "lease", "gateway" })
                Assert.DoesNotContain(word, copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ═══ The refresh that makes the screen follow the Agent ═══

    /// <summary>
    /// The other half of the real defect: the Agent withdrew the mapping and nothing ever asked it
    /// again, so the card kept the state it was handed when the port was opened.
    /// </summary>
    [Fact]
    public async Task Stopping_a_server_re_reads_the_mapping_state()
    {
        var client = new SequencedClient(ServerId) { State = Active() };
        var model = await ReadyAsync(client);
        Assert.Equal(RouterMappingPhase.Active, model.RouterMapping.Phase);

        client.State = Inactive();
        await model.StopServerCommand.ExecuteAsync(model.SelectedServer);

        Assert.Equal(RouterMappingPhase.Inactive, model.RouterMapping.Phase);
        Assert.Contains("GetRouterMapping", client.OperationsAfter("Stop"), StringComparer.Ordinal);
    }

    [Fact]
    public async Task A_periodic_refresh_picks_up_a_mapping_the_agent_withdrew_on_its_own()
    {
        var client = new SequencedClient(ServerId) { State = Active() };
        var model = await ReadyAsync(client);
        Assert.Equal(RouterMappingPhase.Active, model.RouterMapping.Phase);

        client.State = Inactive();
        await model.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(RouterMappingPhase.Inactive, model.RouterMapping.Phase);
    }

    /// <summary>A slower earlier read must not put a withdrawn mapping back on screen.</summary>
    [Fact]
    public async Task A_stale_read_cannot_resurrect_a_mapping_that_was_removed()
    {
        var client = new SequencedClient(ServerId) { State = Active() };
        var model = await ReadyAsync(client);

        // A background read starts while the mapping is still active and is held mid-flight.
        client.HoldNextRead();
        var background = model.RefreshCommand.ExecuteAsync(null);

        // Turning Direct internet off completes first and is authoritative.
        client.State = Off();
        await model.TurnOffDirectInternetCommand.ExecuteAsync(null);
        Assert.Equal(RouterMappingPhase.Off, model.RouterMapping.Phase);

        client.ReleaseHeldRead(Active());
        await background;

        Assert.Equal(RouterMappingPhase.Off, model.RouterMapping.Phase);
    }

    private static MainViewModel Model(RouterMappingState state) =>
        new(new SequencedClient(ServerId), new SilentDialogs()) { RouterMapping = state };

    private static async Task<MainViewModel> ReadyAsync(SequencedClient client)
    {
        var model = new MainViewModel(client, new SilentDialogs());
        await model.InitializeAsync();
        model.SelectedServer = model.Servers[0];
        for (var attempt = 0; attempt < 300 && model.RouterMapping.ServerId != ServerId; attempt++)
            await Task.Delay(10);
        return model;
    }

    private static RouterMappingState Active() => new()
    {
        ServerId = ServerId,
        Enabled = true,
        ConsentGranted = true,
        Phase = RouterMappingPhase.Active,
        Mechanism = RouterMappingMechanism.UpnpIgd,
        AvailableMechanism = RouterMappingMechanism.UpnpIgd,
        Transport = MappingTransport.Tcp,
        GatewayAddress = "10.0.0.1",
        InternalClient = "10.0.0.140",
        CandidateInternalClient = "10.0.0.140",
        InternalPort = 25566,
        ExternalPort = 25566,
        RouterReportedExternalAddress = "73.203.43.174",
        RouterReportedAddressClass = RoutableAddressClass.GloballyRoutable,
        LeaseIsFinite = true,
        LeaseExpiresAt = DateTimeOffset.Now.AddMinutes(59),
        LastCheckedAt = DateTimeOffset.Now,
        LastOperationDetail = "UPnP AddPortMapping accepted TCP 25566 to 10.0.0.140:25566 for 3600 seconds."
    };

    /// <summary>Exactly what the Agent leaves behind after a deliberate stop withdraws the mapping.</summary>
    private static RouterMappingState Inactive() => Active() with
    {
        Phase = RouterMappingPhase.Inactive,
        Mechanism = RouterMappingMechanism.UpnpIgd,
        InternalClient = "",
        LeaseExpiresAt = null,
        LastOperationDetail = "UPnP DeletePortMapping removed TCP 25566."
    };

    private static RouterMappingState Off() => new()
    {
        ServerId = ServerId,
        Phase = RouterMappingPhase.Off,
        InternalPort = 25566
    };

    private sealed class SilentDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }

    private sealed class SequencedClient(Guid serverId) : IAgentClient
    {
        private readonly List<string> operations = [];
        private TaskCompletionSource<RouterMappingState>? held;

        public RouterMappingState State { get; set; } = new();

        public void HoldNextRead() =>
            held = new TaskCompletionSource<RouterMappingState>(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseHeldRead(RouterMappingState state)
        {
            var pending = held;
            held = null;
            pending?.TrySetResult(state);
        }

        public IReadOnlyList<string> OperationsAfter(string marker)
        {
            lock (operations)
            {
                var index = operations.LastIndexOf(marker);
                return index < 0 ? [] : operations.Skip(index + 1).ToArray();
            }
        }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            lock (operations)
                operations.Add(operation);
            if (operation == "GetRouterMapping" && held is { } pending)
                return (TResponse)(object)await pending.Task.ConfigureAwait(false);
            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot { LanAddress = "10.0.0.140" },
                    Servers =
                    [
                        new ServerSnapshot
                        {
                            Definition = new ServerDefinition
                            {
                                Id = serverId, Name = "Clean port", RootPath = @"C:\fixture", Port = 25566
                            },
                            State = ServerState.Stopped
                        }
                    ]
                },
                "GetRouterMapping" or "CheckRouterMapping" or "EnableRouterMapping" or
                    "DisableRouterMapping" or "CancelRouterMapping" or "RetryRouterMapping" => State,
                // Read-only, and local. The external layer is polled alongside the router card and
                // never contacts anything remote by itself.
                "GetExternalReachability" or "CancelExternalReachability" =>
                    new ExternalReachabilityState { ServerId = serverId },
                "GetCapabilities" => new ServerCapabilityProfile(),
                // Direct internet is the selected method, which is what puts the card on screen and
                // makes its state worth polling.
                "GetNetworkConfiguration" => new NetworkConfiguration { Mode = NetworkMode.PortForwarding },
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
            return (TResponse)response;
        }
    }
}
