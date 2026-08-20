using ChunkPilot.App;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

/// <summary>
/// Regression tests for the navigation-reversion bug.
/// Root cause: bidirectional sync between SelectedServerTabIndex/SelectedServerDestination
/// combined with RefreshAsync reassigning SelectedServer every 1s allowed stale async
/// updates to overwrite the user's navigation intent.
/// Fix: NavigationService with version-guarded semantic destinations.
/// </summary>
public sealed class NavigationRegressionTests
{
    private static readonly Guid ServerId1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ServerId2 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Rapid_navigation_preserves_final_destination()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);

        // Simulate rapid clicks through destinations
        nav.NavigateServer(ServerDestination.Console, ServerId1);
        nav.NavigateServer(ServerDestination.Manage, ServerId1);
        nav.NavigateServer(ServerDestination.Access, ServerId1);
        nav.NavigateServer(ServerDestination.Protection, ServerId1);

        Assert.Equal(ServerDestination.Protection, nav.CurrentServerDestination);
        Assert.True(nav.IsServerWorkspaceActive);
    }

    [Fact]
    public void Stale_refresh_cannot_overwrite_newer_user_navigation()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);

        // User navigates to Console
        nav.NavigateServer(ServerDestination.Console, ServerId1);
        var versionAtRefreshStart = nav.ServerVersion;

        // User navigates again while "refresh" is in-flight
        nav.NavigateServer(ServerDestination.Protection, ServerId1);

        // The stale refresh completes and tries to restore the old destination
        var accepted = nav.TryRestoreServerDestination(ServerDestination.Overview, versionAtRefreshStart);

        Assert.False(accepted, "Stale refresh must not overwrite newer user navigation");
        Assert.Equal(ServerDestination.Protection, nav.CurrentServerDestination);
    }

    [Fact]
    public void Current_version_restore_is_accepted()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);

        var currentVersion = nav.ServerVersion;

        // No user navigation since version was captured - restore should work
        var accepted = nav.TryRestoreServerDestination(ServerDestination.Manage, currentVersion);

        Assert.True(accepted);
        Assert.Equal(ServerDestination.Manage, nav.CurrentServerDestination);
    }

    [Fact]
    public async Task Agent_reconnect_does_not_reset_navigation()
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        // User selects server and navigates to Console
        vm.SelectedServer = vm.Servers[0];
        vm.Navigation.NavigateServer(ServerDestination.Console, ServerId1);

        // Simulate multiple refreshes (agent reconnect scenario)
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(ServerDestination.Console, vm.Navigation.CurrentServerDestination);
        Assert.True(vm.Navigation.IsServerWorkspaceActive);
    }

    [Fact]
    public async Task Server_start_stop_does_not_change_destination()
    {
        var server = Snapshot(ServerState.Stopped);
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];
        vm.Navigation.NavigateServer(ServerDestination.Manage, ServerId1);

        // Server starts (state changes on next refresh)
        client.ServerState = ServerState.Running;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(ServerDestination.Manage, vm.Navigation.CurrentServerDestination);

        // Server stops
        client.ServerState = ServerState.Stopped;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(ServerDestination.Manage, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public async Task Switching_servers_restores_remembered_destination()
    {
        var server1 = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var server2 = Snapshot(ServerState.Stopped, ServerId2, "Server 2");
        var client = new NavigationFakeClient(server1, server2);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        // Navigate server 1 to Console
        vm.SelectedServer = vm.Servers[0];
        vm.Navigation.NavigateServer(ServerDestination.Console, ServerId1);
        Assert.Equal(ServerDestination.Console, vm.Navigation.CurrentServerDestination);

        // Switch to server 2, navigate to Protection
        vm.SelectedServer = vm.Servers[1];
        vm.Navigation.NavigateServer(ServerDestination.Protection, ServerId2);
        Assert.Equal(ServerDestination.Protection, vm.Navigation.CurrentServerDestination);

        // Switch back to server 1 - should remember Console
        vm.SelectedServer = vm.Servers[0];
        Assert.Equal(ServerDestination.Console, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public void Unsupported_destination_falls_back_to_overview()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);
        nav.NavigateServer(ServerDestination.Access, ServerId1);

        // Simulate capability change making Access unsupported
        var supported = new HashSet<string>
        {
            ServerDestination.Overview,
            ServerDestination.Console,
            ServerDestination.Settings
        };
        nav.FallbackIfUnsupported(supported);

        Assert.Equal(ServerDestination.Overview, nav.CurrentServerDestination);
    }

    [Fact]
    public void Unsupported_destination_no_fallback_when_current_is_supported()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);
        nav.NavigateServer(ServerDestination.Console, ServerId1);

        var supported = new HashSet<string>(ServerDestination.All);
        nav.FallbackIfUnsupported(supported);

        Assert.Equal(ServerDestination.Console, nav.CurrentServerDestination);
    }

    [Fact]
    public void Invalid_destination_string_falls_back_to_overview()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);
        nav.NavigateServer("NonExistent", ServerId1);

        Assert.Equal(ServerDestination.Overview, nav.CurrentServerDestination);
    }

    [Fact]
    public void Global_navigation_clears_server_workspace()
    {
        var nav = new NavigationService();
        nav.OpenServer(ServerId1);
        Assert.True(nav.IsServerWorkspaceActive);

        nav.NavigateGlobal(GlobalDestination.Activity);

        Assert.False(nav.IsServerWorkspaceActive);
        Assert.Equal(GlobalDestination.Activity, nav.CurrentGlobalPage);
    }

    [Fact]
    public async Task Reopening_server_after_global_nav_restores_destination()
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        // Select server, go to Protection
        vm.SelectedServer = vm.Servers[0];
        vm.Navigation.NavigateServer(ServerDestination.Protection, ServerId1);

        // Navigate globally
        vm.NavigateCommand.Execute("Activity");
        Assert.False(vm.Navigation.IsServerWorkspaceActive);

        // Re-select the same server
        vm.SelectedServer = vm.Servers[0];
        Assert.Equal(ServerDestination.Protection, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public async Task Delayed_refresh_during_initialization_does_not_overwrite_navigation()
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server) { DelayMs = 50 };
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];
        vm.Navigation.NavigateServer(ServerDestination.Settings, ServerId1);

        // Simulate concurrent delayed refresh
        var refreshTask = vm.RefreshCommand.ExecuteAsync(null);
        // Navigate while refresh is in-flight
        vm.Navigation.NavigateServer(ServerDestination.Console, ServerId1);
        await refreshTask;

        // Console (user's latest choice) must win
        Assert.Equal(ServerDestination.Console, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public void Version_counter_is_monotonically_increasing()
    {
        var nav = new NavigationService();
        var versions = new List<long>();

        nav.OpenServer(ServerId1);
        versions.Add(nav.ServerVersion);

        nav.NavigateServer(ServerDestination.Console, ServerId1);
        versions.Add(nav.ServerVersion);

        nav.NavigateServer(ServerDestination.Manage, ServerId1);
        versions.Add(nav.ServerVersion);

        nav.NavigateServer(ServerDestination.Protection, ServerId1);
        versions.Add(nav.ServerVersion);

        for (int i = 1; i < versions.Count; i++)
            Assert.True(versions[i] > versions[i - 1], $"Version[{i}] must be > Version[{i - 1}]");
    }

    // ═══ Global-route bounce-back regression (lastSelectedServerId + periodic refresh) ═══
    //
    // Root cause: RefreshAsync fell back to a never-cleared `lastSelectedServerId` field
    // whenever SelectedServer was null, so once any server had ever been selected, every
    // 1-second refresh tick re-selected it - even after the user explicitly navigated to a
    // global destination. These tests assert on the ViewModel's own route surface
    // (SelectedServer / CurrentPage / Is*Page), which is what the shell actually binds to -
    // the pre-existing NavigationRegressionTests above only asserted on NavigationService,
    // which was never the part that was broken.

    [Fact]
    public async Task Navigating_to_Servers_survives_repeated_delayed_refreshes()
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server) { DelayMs = 20 };
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];
        vm.NavigateCommand.Execute("Servers");
        Assert.True(vm.IsServersPage);

        for (var i = 0; i < 5; i++)
            await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsServersPage);
        Assert.Null(vm.SelectedServer);
        Assert.Equal("Servers", vm.CurrentPage);
    }

    [Fact]
    public async Task Navigating_to_Dashboard_survives_reconnect_refreshes()
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];
        vm.NavigateCommand.Execute("Dashboard");
        Assert.True(vm.IsDashboardPage);

        // Simulate an agent reconnect: several refreshes fire back to back.
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsDashboardPage);
        Assert.Null(vm.SelectedServer);
    }

    [Theory]
    [InlineData("Activity")]
    [InlineData("Settings")]
    [InlineData("Automation")]
    public async Task Global_route_survives_full_snapshot_replacement(string destination)
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];
        vm.NavigateCommand.Execute(destination);
        Assert.Equal(destination, vm.CurrentPage);

        // Refresh replaces every ServerSnapshot instance with a brand-new one (and adds a server).
        client.ReplaceServers(
            Snapshot(ServerState.Running, ServerId1, "Test server"),
            Snapshot(ServerState.Stopped, ServerId2, "Second server"));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(destination, vm.CurrentPage);
        Assert.Null(vm.SelectedServer);
    }

    [Fact]
    public async Task Server_lifecycle_state_change_during_global_route_does_not_reselect_server()
    {
        var server = Snapshot(ServerState.Stopped);
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];
        vm.NavigateCommand.Execute("Servers");

        client.ServerState = ServerState.Starting;
        await vm.RefreshCommand.ExecuteAsync(null);
        client.ServerState = ServerState.Running;
        await vm.RefreshCommand.ExecuteAsync(null);
        client.ServerState = ServerState.Crashed;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsServersPage);
        Assert.Null(vm.SelectedServer);
    }

    [Fact]
    public async Task Switching_between_two_servers_reflects_the_newly_selected_server()
    {
        var server1 = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var server2 = Snapshot(ServerState.Stopped, ServerId2, "Server 2");
        var client = new NavigationFakeClient(server1, server2);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers.First(s => s.Definition.Id == ServerId1);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(ServerId1, vm.SelectedServer?.Definition.Id);

        vm.SelectedServer = vm.Servers.First(s => s.Definition.Id == ServerId2);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(ServerId2, vm.SelectedServer?.Definition.Id);
        Assert.True(vm.IsServerPage);
    }

    [Fact]
    public async Task Deleted_remembered_server_falls_back_to_dashboard_without_looping()
    {
        var server = Snapshot(ServerState.Running, ServerId1, "Gone tomorrow");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        // Open the server, then remove it entirely from the next snapshot (simulating deletion).
        vm.SelectedServer = vm.Servers[0];
        client.ReplaceServers();
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsDashboardPage);

        // Further refreshes must not keep re-attempting to restore the deleted server.
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsDashboardPage);
    }

    [Fact]
    public async Task Invalid_remembered_server_id_at_startup_defaults_to_dashboard()
    {
        var server = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        client.Settings["lastSelectedServerId"] = "not-a-guid";
        var vm = new MainViewModel(client, new FakeDialogs());

        await vm.InitializeAsync();

        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsDashboardPage);
    }

    [Fact]
    public async Task Missing_remembered_server_at_startup_defaults_to_dashboard()
    {
        var server = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        client.Settings["lastSelectedServerId"] = ServerId2.ToString("D"); // never existed / already deleted
        var vm = new MainViewModel(client, new FakeDialogs());

        await vm.InitializeAsync();

        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsDashboardPage);
    }

    [Fact]
    public async Task No_servers_at_startup_stays_on_a_valid_global_destination()
    {
        var client = new NavigationFakeClient();
        var vm = new MainViewModel(client, new FakeDialogs());

        await vm.InitializeAsync();

        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsDashboardPage);
        Assert.False(vm.HasServers);
    }

    [Fact]
    public async Task Stale_delayed_refresh_does_not_override_newer_global_navigation()
    {
        var server = Snapshot(ServerState.Running);
        var client = new NavigationFakeClient(server) { DelayMs = 100 };
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();

        vm.SelectedServer = vm.Servers[0];

        // Refresh starts while a server is selected...
        var refreshTask = vm.RefreshCommand.ExecuteAsync(null);
        // ...but the user navigates to a global destination before it completes.
        vm.NavigateCommand.Execute("Servers");
        await refreshTask;

        Assert.True(vm.IsServersPage);
        Assert.Null(vm.SelectedServer);
    }

    [Fact]
    public async Task Reopening_app_with_remembered_server_restores_workspace_then_honors_later_global_nav()
    {
        var server = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        client.Settings["lastSelectedServerId"] = ServerId1.ToString("D");
        var vm = new MainViewModel(client, new FakeDialogs());

        await vm.InitializeAsync();

        // Startup restoration opens the remembered server's workspace.
        Assert.Equal(ServerId1, vm.SelectedServer?.Definition.Id);
        Assert.True(vm.IsServerPage);

        // The user then explicitly navigates to a global destination.
        vm.NavigateCommand.Execute("Servers");
        Assert.True(vm.IsServersPage);

        // Periodic refreshes afterward must not bounce back into the server workspace.
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.True(vm.IsServersPage);
        Assert.Null(vm.SelectedServer);
    }

    // ═══ First-server-workspace-render regression (blank workspace on first open) ═══
    //
    // Root cause: the shell's lazy page host was only refreshed by MainWindow.xaml.cs when
    // NavigationService.CurrentServerDestination *changed value*. CurrentServerDestination
    // defaults to Overview, and opening any server for the first time in a session also resolves
    // to Overview, so the very first open was a no-op assignment that fired no PropertyChanged and
    // therefore never attached page content. NavigationService.OpenServer now raises a dedicated
    // ServerOpened event unconditionally (regardless of whether the resolved destination's value
    // actually changed), which is the signal these tests assert on - it is the same signal
    // MainWindow.xaml.cs subscribes to in order to (re)attach page content.

    [Fact]
    public async Task First_server_open_fires_ServerOpened_even_though_destination_already_equals_overview()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        Assert.Equal(ServerDestination.Overview, vm.Navigation.CurrentServerDestination);
        vm.SelectedServer = vm.Servers[0];

        Assert.Equal([ServerId1], opened);
        Assert.Equal(ServerDestination.Overview, vm.Navigation.CurrentServerDestination);
        Assert.True(vm.Navigation.IsServerWorkspaceActive);
    }

    [Fact]
    public async Task First_server_open_with_remembered_destination_opens_directly_to_it()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.Navigation.RememberDestination(ServerId1, ServerDestination.Manage);
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        vm.SelectedServer = vm.Servers[0];

        Assert.Equal([ServerId1], opened);
        Assert.Equal(ServerDestination.Manage, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public async Task Switching_servers_both_on_overview_fires_ServerOpened_for_new_server()
    {
        var server1 = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var server2 = Snapshot(ServerState.Stopped, ServerId2, "Server 2");
        var client = new NavigationFakeClient(server1, server2);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        vm.SelectedServer = vm.Servers.First(s => s.Definition.Id == ServerId1);
        vm.SelectedServer = vm.Servers.First(s => s.Definition.Id == ServerId2);

        // Both resolve to Overview (neither has a remembered destination) - the value never
        // changes, yet the second, different server must still be reported as freshly opened.
        Assert.Equal([ServerId1, ServerId2], opened);
        Assert.Equal(ServerDestination.Overview, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public async Task Switching_servers_on_same_non_overview_destination_fires_ServerOpened_for_new_server()
    {
        var server1 = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var server2 = Snapshot(ServerState.Stopped, ServerId2, "Server 2");
        var client = new NavigationFakeClient(server1, server2);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.Navigation.RememberDestination(ServerId1, ServerDestination.Manage);
        vm.Navigation.RememberDestination(ServerId2, ServerDestination.Manage);
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        vm.SelectedServer = vm.Servers.First(s => s.Definition.Id == ServerId1);
        vm.SelectedServer = vm.Servers.First(s => s.Definition.Id == ServerId2);

        Assert.Equal([ServerId1, ServerId2], opened);
        Assert.Equal(ServerDestination.Manage, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public async Task Global_page_to_server_workspace_fires_ServerOpened_exactly_once()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.NavigateCommand.Execute("Servers");
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        vm.SelectedServer = vm.Servers[0];

        Assert.Equal([ServerId1], opened);
    }

    [Fact]
    public async Task Reopening_the_same_server_after_a_global_detour_does_not_refire_ServerOpened()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        vm.SelectedServer = vm.Servers[0];
        vm.NavigateCommand.Execute("Dashboard");
        vm.SelectedServer = vm.Servers[0];

        // The content host never lost its page for this server while it was hidden behind the
        // global page, so re-entering it must not (re)open it a second time.
        Assert.Equal([ServerId1], opened);
        Assert.True(vm.Navigation.IsServerWorkspaceActive);
        Assert.Equal(ServerDestination.Overview, vm.Navigation.CurrentServerDestination);
    }

    [Fact]
    public async Task Reopening_the_same_server_via_refresh_does_not_refire_ServerOpened()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(opened);
    }

    [Fact]
    public async Task Snapshot_replacement_for_the_active_server_does_not_refire_ServerOpened()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        // The Dashboard fetch always returns fresh ServerSnapshot instances (a "with" copy) even
        // when nothing about the server has changed, exercising the same reference-replacement
        // refresh does in production.
        client.ServerState = ServerState.Running;
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(opened);
        Assert.Equal(ServerState.Running, vm.SelectedServer?.State);
        Assert.True(vm.Navigation.IsServerWorkspaceActive);
    }

    [Fact]
    public async Task Startup_restoration_fires_ServerOpened_exactly_once()
    {
        var server = Snapshot(ServerState.Running, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server);
        client.Settings["lastSelectedServerId"] = ServerId1.ToString("D");
        var vm = new MainViewModel(client, new FakeDialogs());
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        await vm.InitializeAsync();

        Assert.Equal([ServerId1], opened);
        Assert.True(vm.IsServerPage);
    }

    [Fact]
    public void Unsupported_remembered_destination_falls_back_to_overview_on_open()
    {
        var nav = new NavigationService();
        nav.RememberDestination(ServerId1, "NonExistent");
        var opened = new List<Guid>();
        nav.ServerOpened += (_, id) => opened.Add(id);

        nav.OpenServer(ServerId1);

        Assert.Equal(ServerDestination.Overview, nav.CurrentServerDestination);
        Assert.Equal([ServerId1], opened);
    }

    [Fact]
    public async Task Deleted_selected_server_does_not_cause_repeated_ServerOpened_attempts()
    {
        var server = Snapshot(ServerState.Running, ServerId1, "Gone tomorrow");
        var client = new NavigationFakeClient(server);
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        client.ReplaceServers();
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(opened);
        Assert.Null(vm.SelectedServer);
        Assert.True(vm.IsDashboardPage);
    }

    [Fact]
    public async Task No_configured_servers_never_fires_ServerOpened()
    {
        var client = new NavigationFakeClient();
        var vm = new MainViewModel(client, new FakeDialogs());
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        await vm.InitializeAsync();

        Assert.Empty(opened);
        Assert.False(vm.HasServers);
    }

    [Fact]
    public async Task No_duplicate_ServerOpened_during_periodic_background_refresh()
    {
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server) { DelayMs = 5 };
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        for (var i = 0; i < 10; i++)
            await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(opened);
    }

    [Fact]
    public async Task Refresh_restores_selection_despite_a_transient_null_mid_flight()
    {
        // The sidebar server list's SelectedItem is bound TwoWay to SelectedServer. Replace()
        // clearing then repopulating the Servers collection can synchronously null SelectedServer
        // via that binding as a side effect - not because the user navigated anywhere. Simulate
        // that exact race: something (not the version-tracked navigation APIs) nulls SelectedServer
        // while a refresh is in flight. Refresh must still restore the correct server afterward
        // instead of treating the transient null as "the user left".
        var server = Snapshot(ServerState.Stopped, ServerId1, "Server 1");
        var client = new NavigationFakeClient(server) { DelayMs = 50 };
        var vm = new MainViewModel(client, new FakeDialogs());
        await vm.InitializeAsync();
        vm.SelectedServer = vm.Servers[0];
        var opened = new List<Guid>();
        vm.Navigation.ServerOpened += (_, id) => opened.Add(id);

        var refreshTask = vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedServer = null;
        await refreshTask;

        Assert.Equal(ServerId1, vm.SelectedServer?.Definition.Id);
        Assert.True(vm.IsServerPage);
        Assert.Empty(opened);
    }

    // ═══ Helpers ═══

    private static ServerSnapshot Snapshot(ServerState state, Guid? id = null, string name = "Test server") => new()
    {
        Definition = new ServerDefinition
        {
            Id = id ?? ServerId1,
            Name = name,
            RootPath = @"C:\fixture",
            Executable = @"C:\fixture\java.exe",
            WorkingDirectory = @"C:\fixture"
        },
        State = state
    };

    private sealed class NavigationFakeClient : IAgentClient
    {
        private ServerSnapshot[] _servers;
        public ServerState ServerState { get; set; }
        public int DelayMs { get; set; }
        public Dictionary<string, string> Settings { get; } = new();

        public NavigationFakeClient(params ServerSnapshot[] servers)
        {
            _servers = servers;
            ServerState = servers.Length > 0 ? servers[0].State : ServerState.Stopped;
        }

        /// <summary>Replace the server list entirely with new instances, simulating a fresh
        /// snapshot fetch (e.g. a deletion, or a plain refresh that swaps object references).</summary>
        public void ReplaceServers(params ServerSnapshot[] servers) => _servers = servers;

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<TResponse> SendAsync<TResponse>(
            string operation,
            object? payload = null,
            CancellationToken cancellationToken = default)
        {
            if (DelayMs > 0)
                await Task.Delay(DelayMs, cancellationToken);

            object response = operation switch
            {
                "Dashboard" => new DashboardSnapshot
                {
                    AgentConnected = true,
                    Host = new HostSnapshot(),
                    Servers = _servers.Select(s => s with { State = ServerState }).ToArray()
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
                "GetSetting" => new TextResponse(
                    payload is SettingsValueRequest request && Settings.TryGetValue(request.Key, out var value)
                        ? value
                        : ""),
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
