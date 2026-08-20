using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using ChunkPilot.App.CreateServerLive;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;

namespace ChunkPilot.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly AgentClient client;
    private readonly DispatcherTimer refreshTimer;
    private CreateServerLiveWindow? vanillaCreationWindow;
    private bool refreshInProgress;
    private bool programmaticConsoleScroll;
    private bool suppressNavEvents;
    private Guid sessionId;
    private string sessionCapability = "";
    private DateTimeOffset lastHeartbeatAt;

    // Lazy-loaded page content for the server workspace
    private readonly Dictionary<string, FrameworkElement> _serverPages = new();

    public MainWindow(MainViewModel viewModel, AgentClient client)
    {
        InitializeComponent();
        // Caption and icon come from the shared window chrome, which every ChunkPilot window uses.
        AppWindowChrome.Apply(this);
        this.viewModel = viewModel;
        this.client = client;
        DataContext = viewModel;
        refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, RefreshTimer_Tick, Dispatcher);
        refreshTimer.Stop(); // Don't start until initialization completes
        viewModel.ConsoleScrollRequested += (_, _) => Dispatcher.BeginInvoke(ScrollConsoleToLatest, DispatcherPriority.Background);
        viewModel.Navigation.PropertyChanged += Navigation_PropertyChanged;
        viewModel.Navigation.ServerOpened += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            SyncServerNavSelection();
            UpdateServerPageContent();
        });
        viewModel.VanillaCreationRequested += ViewModel_VanillaCreationRequested;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
    }

    public void ConfigureSession(Guid id, string capability)
    {
        sessionId = id;
        sessionCapability = capability;
        lastHeartbeatAt = DateTimeOffset.MinValue;
    }

    /// <summary>Called after successful initialization to transition from splash to shell.</summary>
    public void TransitionToShell()
    {
        refreshTimer.Start();
        // Sync initial navigation state
        SyncGlobalNavSelection();
        SyncServerNavSelection();
    }

    /// <summary>Called when startup fails. Shows a themed error in the shell's connectivity banner.</summary>
    public void ShowStartupFailure()
    {
        ConnectivityBanner.Visibility = Visibility.Visible;
        ConnectivityText.Text = viewModel.Startup.FailureDetail ?? "Could not connect to the ChunkPilot service.";
    }

    // ═══ Navigation event handling ═══

    private void Navigation_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NavigationService.CurrentServerDestination))
        {
            Dispatcher.BeginInvoke(() =>
            {
                SyncServerNavSelection();
                UpdateServerPageContent();
            });
        }
        else if (e.PropertyName is nameof(NavigationService.IsServerWorkspaceActive)
                 or nameof(NavigationService.CurrentGlobalPage)
                 or nameof(NavigationService.SelectedGlobalItem))
        {
            // Both properties matter. Watching only the workspace flag meant moving between two
            // global destinations - Dashboard's "Open servers" is exactly that - never re-read the
            // rail selection, so the page changed underneath a highlight that did not.
            Dispatcher.BeginInvoke(SyncGlobalNavSelection);
        }
    }

    private void GlobalNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressNavEvents) return;
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem is NavigationItem item)
        {
            viewModel.NavigateCommand.Execute(item.Page);
        }
    }

    private void ServerNav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressNavEvents) return;
        if (sender is System.Windows.Controls.ListBox lb && lb.SelectedItem is NavigationItem item)
        {
            viewModel.NavigateServerDestinationCommand.Execute(item.Page);
        }
    }

    private void SyncGlobalNavSelection()
    {
        suppressNavEvents = true;
        try
        {
            // The rail shows which global destination is current. While a server workspace is
            // open the global rail carries no selection, because the current destination is a
            // server page rather than a global one. The navigation service decides which that is;
            // the shell only mirrors it.
            viewModel.SelectedGlobalNavItem = viewModel.Navigation.SelectedGlobalItem;
        }
        finally
        {
            suppressNavEvents = false;
        }
    }

    private void SyncServerNavSelection()
    {
        suppressNavEvents = true;
        try
        {
            var destination = viewModel.Navigation.CurrentServerDestination;
            var item = viewModel.Navigation.ServerItems
                .FirstOrDefault(i => i.Page == destination);
            if (item is not null && !Equals(ServerNavBar.SelectedItem, item))
                ServerNavBar.SelectedItem = item;
        }
        finally
        {
            suppressNavEvents = false;
        }
    }

    private void UpdateServerPageContent()
    {
        var destination = viewModel.Navigation.CurrentServerDestination;
        if (!_serverPages.TryGetValue(destination, out var page))
        {
            page = CreateServerPage(destination);
            _serverPages[destination] = page;
        }
        // Console owns its bounded output viewport. Leaving the shell scroller enabled measures the
        // page with infinite height, expands the ListBox to every row and makes scrolling the outer
        // page incapable of pausing follow.
        PageScroller.VerticalScrollBarVisibility = destination == ServerDestination.Console
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
        ServerPageHost.Content = page;
        // Reset scroll to top when changing destinations
        PageScroller.ScrollToTop();
        // Arriving at the Console means wanting to see what the server is saying now. Without this the
        // viewport starts at the oldest buffered line and stays there until the next line arrives, which
        // on a quiet server is indistinguishable from output that has stopped. Scrolling up still pauses
        // follow, because the user has not scrolled anywhere yet at this point.
        if (destination == ServerDestination.Console)
            Dispatcher.BeginInvoke(ScrollConsoleToLatest, DispatcherPriority.Loaded);
    }

    private FrameworkElement CreateServerPage(string destination) => destination switch
    {
        ServerDestination.Overview => new ServerOverviewPage(),
        ServerDestination.Console => new ServerConsolePage(),
        ServerDestination.Manage => new ServerManagePage(),
        ServerDestination.Access => new ServerAccessPage(),
        ServerDestination.Protection => new ServerProtectionPage(),
        ServerDestination.Settings => new ServerSettingsPage(),
        _ => new ServerOverviewPage()
    };

    /// <summary>Restores and presents this existing shell after the user chooses Open server.</summary>
    public void PresentServerWorkspace() =>
        WindowForegroundPresenter.Present(this, ServerNavBar);

    /// <summary>
    /// Opens the authoritative beginner Vanilla workflow. All product entry points and the retained
    /// development switch use this composition root, so only one live wizard can be open at a time.
    /// </summary>
    public CreateServerLiveWindow OpenVanillaCreation()
    {
        if (vanillaCreationWindow is { IsVisible: true } existing)
        {
            WindowForegroundPresenter.Present(existing);
            return existing;
        }

        var window = CreateServerLiveLauncher.Open(
            this,
            new AgentVanillaCreationGateway(client),
            new ShellCreatedServerNavigator(viewModel, this),
            new DialogServerLocationChooser(new DialogService()));
        vanillaCreationWindow = window;
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(vanillaCreationWindow, window))
                vanillaCreationWindow = null;
        };
        return window;
    }

    private void ViewModel_VanillaCreationRequested(object? sender, EventArgs e) =>
        OpenVanillaCreation();

    // ═══ Console scroll ═══

    public void ConsoleScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (programmaticConsoleScroll) return;
        if (sender is not ScrollViewer viewer) return;
        var isAtBottom = viewer.ScrollableHeight <= 0 ||
                         viewer.VerticalOffset >= viewer.ScrollableHeight - 1;
        viewModel.SetConsoleViewport(isAtBottom);
    }

    private void ScrollConsoleToLatest()
    {
        // Find the console ListBox within the server console page
        if (ServerPageHost.Content is ServerConsolePage consolePage && consolePage.ConsoleListBox is { } list)
        {
            if (list.Items.Count == 0) return;
            programmaticConsoleScroll = true;
            try
            {
                list.ScrollIntoView(list.Items[^1]);
                viewModel.SetConsoleViewport(true);
            }
            finally
            {
                programmaticConsoleScroll = false;
            }
        }
    }

    // ═══ Refresh timer ═══

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (refreshInProgress || viewModel.IsBusy)
            return;
        refreshInProgress = true;
        try
        {
            await viewModel.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            if (sessionId != Guid.Empty &&
                DateTimeOffset.UtcNow - lastHeartbeatAt >= TimeSpan.FromSeconds(2))
            {
                lastHeartbeatAt = DateTimeOffset.UtcNow;
                try
                {
                    _ = await client.SendAsync<OperationResult>(
                        "HeartbeatUiSession",
                        new UiSessionHeartbeatRequest(sessionId, RunningServerIds())
                        {
                            SessionCapability = sessionCapability
                        })
                        .ConfigureAwait(true);
                    // Hide connectivity banner on successful heartbeat
                    if (ConnectivityBanner.Visibility == Visibility.Visible)
                        ConnectivityBanner.Visibility = Visibility.Collapsed;
                }
                catch (Exception exception) when (
                    exception is IOException or TimeoutException or InvalidOperationException)
                {
                    ConnectivityBanner.Visibility = Visibility.Visible;
                }
            }
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    // ═══ Window close ═══

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        refreshTimer.Stop();
        if (Application.Current is not App application ||
            application.IsUnexpectedExit ||
            sessionId == Guid.Empty)
            return;
        // Persist last server destination
        if (viewModel.SelectedServer is not null)
        {
            var dest = viewModel.Navigation.CurrentServerDestination;
            client.TrySendOneWay("SetSetting",
                new SettingsValueRequest("lastServerDestination", dest));
        }
        client.TrySendOneWay(
            "SafeApplicationExit",
            new SafeApplicationExitRequest(sessionId, RunningServerIds(), DateTimeOffset.UtcNow)
            {
                SessionCapability = sessionCapability
            });
    }

    private void MainWindow_Closed(object? sender, EventArgs e) =>
        viewModel.VanillaCreationRequested -= ViewModel_VanillaCreationRequested;

    private IReadOnlyList<Guid> RunningServerIds() =>
        viewModel.Servers
            .Where(server => server.State is not ServerState.Stopped and not ServerState.Crashed)
            .Select(server => server.Definition.Id)
            .ToArray();
}
