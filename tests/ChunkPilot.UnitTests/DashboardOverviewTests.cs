using System.Globalization;
using ChunkPilot.App;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

/// <summary>
/// State-presentation tests for the Dashboard and Overview redesign.
/// Verifies that computed properties, converters, and state summaries produce
/// correct beginner-friendly output for all server states.
/// </summary>
public sealed class DashboardOverviewTests
{
    private static readonly Guid ServerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    // ═══ DashboardSummary tests ═══

    [Fact]
    public async Task Dashboard_summary_shows_no_servers_when_empty()
    {
        var client = new DashboardFakeClient();
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        Assert.Equal("No servers configured", vm.DashboardSummary);
        Assert.False(vm.HasServers);
    }

    [Fact]
    public async Task Dashboard_summary_shows_running_count()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Running));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        Assert.Contains("1 running", vm.DashboardSummary);
        Assert.True(vm.HasServers);
    }

    [Fact]
    public async Task Dashboard_summary_shows_problem_count()
    {
        var client = new DashboardFakeClient(
            Snapshot(ServerState.Running, "Server A"),
            Snapshot(ServerState.Crashed, "Server B"));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        Assert.Contains("1 running", vm.DashboardSummary);
        Assert.Contains("1 need attention", vm.DashboardSummary);
        Assert.Equal(1, vm.ProblemCount);
    }

    [Fact]
    public async Task Dashboard_summary_shows_multiple_states()
    {
        var client = new DashboardFakeClient(
            Snapshot(ServerState.Running, "A"),
            Snapshot(ServerState.Stopped, "B"),
            Snapshot(ServerState.Starting, "C"));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        Assert.Contains("1 running", vm.DashboardSummary);
        Assert.Contains("1 stopped", vm.DashboardSummary);
        Assert.Contains("1 starting", vm.DashboardSummary);
    }

    [Fact]
    public async Task HasServers_is_false_when_no_servers_exist()
    {
        var client = new DashboardFakeClient();
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        Assert.False(vm.HasServers);
    }

    [Fact]
    public async Task HasServers_is_true_with_one_server()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Stopped));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        Assert.True(vm.HasServers);
    }

    // ═══ ServerStateTextConverter tests ═══

    [Theory]
    [InlineData(ServerState.Running, "Running")]
    [InlineData(ServerState.Stopped, "Stopped")]
    [InlineData(ServerState.Starting, "Starting…")]
    [InlineData(ServerState.Stopping, "Stopping…")]
    [InlineData(ServerState.Crashed, "Crashed")]
    [InlineData(ServerState.Unresponsive, "Not responding")]
    [InlineData(ServerState.BackingUp, "Backing up…")]
    [InlineData(ServerState.Restoring, "Restoring…")]
    [InlineData(ServerState.Restarting, "Restarting…")]
    [InlineData(ServerState.Saving, "Saving…")]
    [InlineData(ServerState.Unknown, "Unknown")]
    public void StateText_converter_maps_all_states(ServerState state, string expected)
    {
        var converter = new ServerStateTextConverter();
        var result = converter.Convert(state, typeof(string), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    // ═══ ServerStateToneConverter tests ═══

    [Theory]
    [InlineData(ServerState.Running, AppTone.Success)]
    [InlineData(ServerState.Stopped, AppTone.Neutral)]
    [InlineData(ServerState.Starting, AppTone.Info)]
    [InlineData(ServerState.Restarting, AppTone.Info)]
    [InlineData(ServerState.Stopping, AppTone.Danger)]
    [InlineData(ServerState.Crashed, AppTone.Danger)]
    [InlineData(ServerState.Unresponsive, AppTone.Danger)]
    [InlineData(ServerState.BackingUp, AppTone.Warning)]
    [InlineData(ServerState.Unknown, AppTone.Neutral)]
    public void StateTone_converter_maps_states_to_tones(ServerState state, AppTone expected)
    {
        var converter = new ServerStateToneConverter();
        var result = converter.Convert(state, typeof(AppTone), null!, CultureInfo.InvariantCulture);
        Assert.Equal(expected, result);
    }

    // ═══ Lifecycle command enablement for Overview ═══

    [Fact]
    public async Task Start_enabled_when_stopped()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Stopped));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var server = Assert.Single(vm.Servers);
        Assert.True(vm.StartServerCommand.CanExecute(server));
        Assert.False(vm.StopServerCommand.CanExecute(server));
    }

    [Fact]
    public async Task Stop_enabled_when_running()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Running));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var server = Assert.Single(vm.Servers);
        Assert.False(vm.StartServerCommand.CanExecute(server));
        Assert.True(vm.StopServerCommand.CanExecute(server));
    }

    [Fact]
    public async Task Stop_still_available_during_starting_transition()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Starting));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var server = Assert.Single(vm.Servers);
        // Start is disabled during Starting, but Stop is allowed (to cancel)
        Assert.False(vm.StartServerCommand.CanExecute(server));
        Assert.True(vm.StopServerCommand.CanExecute(server));
    }

    // ═══ Connection address presentation ═══

    [Fact]
    public async Task Local_address_uses_localhost_and_port()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Running));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        Assert.Equal("localhost:25565", vm.ServerLocalAddress);
    }

    [Fact]
    public async Task Public_address_shows_not_configured_when_empty()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Running));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        Assert.Equal("Not configured", vm.ServerPublicAddress);
        Assert.False(vm.HasConfiguredPublicAddress);
    }

    [Fact]
    public async Task Restart_changes_the_visible_state_before_the_agent_reply_arrives()
    {
        var gate = new TaskCompletionSource<OperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new DashboardFakeClient(Snapshot(ServerState.Running)) { LifecycleGate = gate };
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var server = Assert.Single(vm.Servers);

        var restart = vm.RestartServerCommand.ExecuteAsync(server);
        await Task.Yield();

        Assert.Equal(ServerState.Restarting, vm.SelectedServer?.State);
        gate.SetResult(OperationResult.Ok("restarted"));
        await restart;
    }

    [Fact]
    public async Task Memory_has_a_dedicated_apply_path_while_running()
    {
        var client = new DashboardFakeClient(Snapshot(ServerState.Running));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        vm.MaximumMemoryGb = 6;

        Assert.True(vm.HasMemoryChanges);
        Assert.True(vm.ApplyMemoryCommand.CanExecute(null));
        await vm.ApplyMemoryCommand.ExecuteAsync(null);

        Assert.Contains("UpdateRam", client.Operations);
        Assert.False(vm.HasMemoryChanges);
        Assert.True(vm.MemoryNeedsRestart);
    }

    [Theory]
    [InlineData("192.168.1.100", "192.168.1.100:25565")]
    [InlineData("fd12:3456::44", "[fd12:3456::44]:25565")]
    public async Task Lan_endpoint_formats_ipv4_and_ipv6_without_ambiguity(string address, string expected)
    {
        var client = new DashboardFakeClient(address, Snapshot(ServerState.Running));
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];

        Assert.Equal(expected, vm.ServerLanAddress);
        Assert.Contains("Test Server", vm.ActiveServerSummary, StringComparison.Ordinal);
        Assert.Contains("Running", vm.ActiveServerSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Configured_public_address_is_one_progressive_state()
    {
        var server = Snapshot(ServerState.Running) with
        {
            Definition = Snapshot(ServerState.Running).Definition with { UserConfiguredHostname = "play.example.test" }
        };
        var vm = new MainViewModel(new DashboardFakeClient(server), new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];

        Assert.True(vm.HasConfiguredPublicAddress);
        Assert.Equal("play.example.test:25565", vm.ServerPublicAddress);
    }

    // ═══ Helpers ═══

    private static ServerSnapshot Snapshot(ServerState state, string name = "Test Server") => new()
    {
        Definition = new ServerDefinition
        {
            Id = ServerId,
            Name = name,
            RootPath = @"C:\fixture",
            Executable = @"C:\fixture\java.exe",
            WorkingDirectory = @"C:\fixture",
            Port = 25565
        },
        State = state
    };

    private sealed class DashboardFakeClient : IAgentClient
    {
        private readonly ServerSnapshot[] _servers;
        private readonly string lanAddress;
        public TaskCompletionSource<OperationResult>? LifecycleGate { get; init; }
        public List<string> Operations { get; } = [];

        public DashboardFakeClient(params ServerSnapshot[] servers) : this("192.168.1.100", servers) { }

        public DashboardFakeClient(string lanAddress, params ServerSnapshot[] servers)
        {
            this.lanAddress = lanAddress;
            _servers = servers;
        }

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<TResponse> SendAsync<TResponse>(
            string operation, object? payload = null, CancellationToken cancellationToken = default)
        {
            Operations.Add(operation);
            if ((operation is "Start" or "Stop" or "Restart") && LifecycleGate is not null)
                return (TResponse)(object)await LifecycleGate.Task.WaitAsync(cancellationToken);
            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot { LanAddress = lanAddress },
                    Servers = _servers
                },
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
            return (TResponse)response;
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) { }
        public void ShowInformation(string title, string message) { }
    }
}
