using ChunkPilot.App;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class ViewModelTests
{
    [Fact]
    public async Task Lifecycle_buttons_are_enabled_only_for_valid_states()
    {
        var stopped = Snapshot(ServerState.Stopped);
        var client = new FakeAgentClient(stopped);
        var dialogs = new FakeDialogs();
        var viewModel = new MainViewModel(client, dialogs);
        await viewModel.InitializeAsync();
        var current = Assert.Single(viewModel.Servers);
        Assert.True(viewModel.StartServerCommand.CanExecute(current));
        Assert.False(viewModel.SaveServerCommand.CanExecute(current));
        Assert.False(viewModel.RestartServerCommand.CanExecute(current));
        Assert.False(viewModel.StopServerCommand.CanExecute(current));

        client.Snapshot = Snapshot(ServerState.Running);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        current = Assert.Single(viewModel.Servers);
        Assert.False(viewModel.StartServerCommand.CanExecute(current));
        Assert.True(viewModel.SaveServerCommand.CanExecute(current));
        Assert.True(viewModel.RestartServerCommand.CanExecute(current));
        Assert.True(viewModel.StopServerCommand.CanExecute(current));
    }

    [Fact]
    public async Task Dashboard_values_come_from_agent_provider()
    {
        var snapshot = Snapshot(ServerState.Running) with
        {
            CurrentStatistics = new StatisticsSample { CpuPercent = 23.5, WorkingSetBytes = 512 * 1024 * 1024 }
        };
        var client = new FakeAgentClient(snapshot)
        {
            Host = new HostSnapshot { CpuPercent = 44, UsedMemoryBytes = 8L * 1024 * 1024 * 1024 }
        };
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        Assert.Equal(23.5, viewModel.CombinedCpu);
        Assert.Equal(512 * 1024 * 1024, viewModel.CombinedRam);
        Assert.Equal(44, viewModel.Dashboard.Host.CpuPercent);
    }

    [Fact]
    public async Task Busy_state_prevents_duplicate_lifecycle_operation()
    {
        var stopped = Snapshot(ServerState.Stopped);
        var client = new FakeAgentClient(stopped) { BlockStart = true };
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        var current = Assert.Single(viewModel.Servers);
        var first = viewModel.StartServerCommand.ExecuteAsync(current);
        await client.StartEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.StartServerCommand.CanExecute(current));
        client.ReleaseStart.SetResult();
        await first;
        Assert.Equal(1, client.StartCalls);
    }

    /// <summary>
    /// A failed operation is reported in the application, not in a Windows message box.
    /// </summary>
    /// <remarks>
    /// The detail still has to survive intact - that is what makes it a diagnostic - and the notice has
    /// to name the server, because ChunkPilot manages more than one.
    /// </remarks>
    [Fact]
    public async Task Agent_errors_are_presented_in_the_shell_and_not_in_a_dialog()
    {
        var dialogs = new FakeDialogs();
        var client = new FakeAgentClient(Snapshot(ServerState.Stopped, name: "Fixture server"))
        {
            StartError = "missing java"
        };
        var viewModel = new MainViewModel(client, dialogs);
        await viewModel.InitializeAsync();
        await viewModel.StartServerCommand.ExecuteAsync(Assert.Single(viewModel.Servers));

        Assert.True(viewModel.HasOperationNotice);
        Assert.Contains("Fixture server", viewModel.OperationNoticeTitle, StringComparison.Ordinal);
        Assert.Contains("missing java", viewModel.OperationNoticeDetail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing java", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(dialogs.LastError);

        viewModel.DismissOperationNoticeCommand.Execute(null);
        Assert.False(viewModel.HasOperationNotice);
    }

    [Fact]
    public async Task WebUi_lifecycle_completion_preserves_a_failed_authoritative_stop_result()
    {
        var client = new FakeAgentClient(Snapshot(ServerState.Running))
        {
            StopResult = OperationResult.Fail(
                "Stop could not take control because Backup did not cancel.")
        };
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        var server = Assert.Single(viewModel.Servers);

        var result = await viewModel.RunWebUiLifecycleAsync("servers.stop", server);

        Assert.False(result.Success);
        Assert.Contains("did not cancel", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ServerState.Running, Assert.Single(viewModel.Servers).State);
    }

    private static ServerSnapshot Snapshot(ServerState state, string name = "Server", string rootPath = @"C:\fixture", Guid? id = null) => new()
    {
        Definition = new ServerDefinition
        {
            Id = id ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = name,
            RootPath = rootPath,
            Executable = @"C:\fixture\java.exe",
            WorkingDirectory = @"C:\fixture"
        },
        State = state
    };

    [Fact]
    public async Task LibraryServers_returns_all_when_no_filters()
    {
        var running = Snapshot(ServerState.Running, "Alpha");
        var stopped = Snapshot(ServerState.Stopped, "Bravo");
        var client = new FakeAgentClient(running);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.Servers.Add(stopped);
        var library = viewModel.LibraryServers.ToList();
        Assert.Equal(2, library.Count);
    }

    [Fact]
    public async Task LibraryServers_filters_by_state_running()
    {
        var running = Snapshot(ServerState.Running, "Alpha");
        var stopped = Snapshot(ServerState.Stopped, "Bravo");
        var client = new FakeAgentClient(running);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.Servers.Add(stopped);
        viewModel.LibraryStateFilter = "Running";
        var library = viewModel.LibraryServers.ToList();
        Assert.Single(library);
        Assert.Equal("Alpha", library[0].Definition.Name);
    }

    [Fact]
    public async Task LibraryServers_filters_by_state_needs_attention()
    {
        var crashed = Snapshot(ServerState.Crashed, "Delta");
        var client = new FakeAgentClient(crashed);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.LibraryStateFilter = "Needs attention";
        var library = viewModel.LibraryServers.ToList();
        Assert.Single(library);
    }

    [Fact]
    public async Task LibraryServers_sorts_name_ascending()
    {
        var zServer = Snapshot(ServerState.Stopped, "Zebra");
        var aServer = Snapshot(ServerState.Stopped, "Alpha");
        var client = new FakeAgentClient(zServer);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.Servers.Add(aServer);
        viewModel.LibrarySortOrder = "name-ascending";
        var library = viewModel.LibraryServers.ToList();
        Assert.Equal("Alpha", library[0].Definition.Name);
    }

    [Fact]
    public async Task LibraryServers_sorts_name_descending()
    {
        var aServer = Snapshot(ServerState.Stopped, "Alpha");
        var zServer = Snapshot(ServerState.Stopped, "Zebra");
        var client = new FakeAgentClient(aServer);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.Servers.Add(zServer);
        viewModel.LibrarySortOrder = "name-descending";
        var library = viewModel.LibraryServers.ToList();
        Assert.Equal("Zebra", library[0].Definition.Name);
    }

    [Fact]
    public async Task LibraryServers_sorts_state_first()
    {
        var stoppedA = Snapshot(ServerState.Stopped, "Alpha");
        var runningB = Snapshot(ServerState.Running, "Bravo");
        var client = new FakeAgentClient(runningB);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.Servers.Add(stoppedA);
        viewModel.LibrarySortOrder = "state-first";
        var library = viewModel.LibraryServers.ToList();
        Assert.Equal("Bravo", library[0].Definition.Name);
    }

    [Fact]
    public async Task NoSearchResults_is_true_when_no_match()
    {
        var stopped = Snapshot(ServerState.Stopped, "Alpha");
        var client = new FakeAgentClient(stopped);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.SearchText = "zzzzz";
        _ = viewModel.LibraryServers;
        Assert.True(viewModel.NoSearchResults);
    }

    [Fact]
    public async Task NoSearchResults_is_false_when_match_exists()
    {
        var stopped = Snapshot(ServerState.Stopped, "Alpha");
        var client = new FakeAgentClient(stopped);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.SearchText = "Alpha";
        _ = viewModel.LibraryServers;
        Assert.False(viewModel.NoSearchResults);
    }

    [Fact]
    public async Task ClearLibraryFilters_resets_search_and_state()
    {
        var stopped = Snapshot(ServerState.Stopped, "Alpha");
        var client = new FakeAgentClient(stopped);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.SearchText = "Alpha";
        viewModel.LibraryStateFilter = "Running";
        viewModel.ClearLibraryFiltersCommand.Execute(null);
        Assert.Equal("", viewModel.SearchText);
        Assert.Equal("All", viewModel.LibraryStateFilter);
    }

    [Fact]
    public async Task NoSearchResults_is_true_when_state_filter_alone_has_no_match()
    {
        // Regression: a state filter with an empty search box must still surface the
        // "no results" empty state, since the UI's own copy says "or clear your filters".
        var stopped = Snapshot(ServerState.Stopped, "Alpha");
        var client = new FakeAgentClient(stopped);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        viewModel.LibraryStateFilter = "Running";
        _ = viewModel.LibraryServers;
        Assert.True(viewModel.NoSearchResults);
    }

    [Fact]
    public async Task SearchText_change_raises_LibraryServers_property_changed()
    {
        // Regression: the Servers-library search box binds SearchText with
        // UpdateSourceTrigger=PropertyChanged; LibraryServers must be invalidated on
        // every keystroke or the on-screen list goes stale while the user types.
        var stopped = Snapshot(ServerState.Stopped, "Alpha");
        var client = new FakeAgentClient(stopped);
        var viewModel = new MainViewModel(client, new FakeDialogs());
        await viewModel.InitializeAsync();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        viewModel.SearchText = "zzz";
        Assert.Contains(nameof(MainViewModel.LibraryServers), raised);
    }

    [Fact]
    public void ServerEqualsConverter_matches_only_the_selected_server_by_id()
    {
        // Regression: the library row's IsSelected binding compares SelectedServer against
        // the row's own item. That requires two inputs (a MultiBinding), not a single value
        // with a ConverterParameter, since nested {Binding} markup does not update dynamically.
        var converter = new ServerEqualsConverter();
        var selected = Snapshot(ServerState.Running, "Alpha", id: Guid.NewGuid());
        var other = Snapshot(ServerState.Stopped, "Bravo", id: Guid.NewGuid());

        Assert.Equal(true, converter.Convert([selected, selected], typeof(bool), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert([selected, other], typeof(bool), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(false, converter.Convert([null!, other], typeof(bool), null!, System.Globalization.CultureInfo.InvariantCulture));
    }

    private sealed class FakeAgentClient : IAgentClient
    {
        public FakeAgentClient(ServerSnapshot snapshot) => Snapshot = snapshot;

        public ServerSnapshot Snapshot { get; set; }
        public HostSnapshot Host { get; set; } = new();
        public bool BlockStart { get; set; }
        public string StartError { get; set; } = "";
        public OperationResult StopResult { get; set; } = OperationResult.Ok("stopped");
        public int StartCalls { get; private set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<TResponse> SendAsync<TResponse>(
            string operation,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot { AgentConnected = true, Host = Host, Servers = [Snapshot] },
                "ListBackups" => Array.Empty<BackupRecord>(),
                "ListSchedules" => Array.Empty<ScheduleEntry>(),
                "ListFiles" => Array.Empty<FileSystemEntry>(),
                "Inventory" => Array.Empty<ModPluginEntry>(),
                "Diagnostics" => Array.Empty<DiagnosticFinding>(),
                "GetSetting" => new TextResponse(""),
                "Stop" => StopResult,
                _ => OperationResult.Ok("ok")
            };
            if (operation == "Start")
            {
                StartCalls++;
                StartEntered.TrySetResult();
                if (BlockStart)
                    await ReleaseStart.Task.WaitAsync(cancellationToken);
                if (StartError.Length > 0)
                    throw new InvalidOperationException(StartError);
            }
            return (TResponse)response;
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public string? LastError { get; private set; }
        public string? SelectFolder(string title, string? initialPath = null) => null;
        public string? SelectFile(string title, string filter) => null;
        public bool Confirm(string title, string message) => false;
        public void ShowError(string title, string message) => LastError = message;
        public void ShowInformation(string title, string message) { }
    }
}
