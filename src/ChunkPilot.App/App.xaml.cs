using ChunkPilot.App.CreateServer;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.DesignSystem.Gallery;
using ChunkPilot.App.WebUi;
using ChunkPilot.Core;
using Forms = System.Windows.Forms;

namespace ChunkPilot.App;

public partial class App : Application
{
    private Mutex? mutex;
    private Forms.NotifyIcon? trayIcon;
    private AgentClient? agentClient;
    private Guid sessionId;
    private string sessionCapability = "";

    internal bool IsUnexpectedExit { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppTheme.Initialize(this);

        if (BuildIdentity.IsVersionRequest(e.Args))
        {
            BuildIdentity.WriteToParentConsole();
            Shutdown();
            return;
        }

        // Development-only branch. Takes no single-instance lock, starts no tray icon and never
        // contacts the agent, so running the gallery cannot disturb a real ChunkPilot session or
        // any managed server. There is no product control that reaches this path.
        if (DesignGalleryLauncher.TryRun(this, e.Args))
            return;

        // Development/review-only branch, on the same terms as the gallery above: no single-instance
        // lock, no tray icon, no agent connection, no database, and entirely synthetic data. No
        // product control reaches it, and the normal Create Server path is unchanged.
        if (CreateServerPreviewLauncher.TryRun(this, e.Args))
            return;

        // Development-only WebView2 fixtures and deterministic captures. This branch has no Agent,
        // tray, mutex, database, or access to live server data.
        if (WebUiFixtureLauncher.TryRun(this, e.Args))
            return;

        // Engineering-only Terraria proof. It is intentionally evaluated before normal startup so
        // it cannot contact the Agent, real data, or networking state and cannot appear in product UI.
        if (TerrariaExperimentalPreviewLauncher.TryRun(this, e.Args))
            return;

        mutex = new Mutex(initiallyOwned: true, ChunkPilotConstants.AppMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("ChunkPilot is already open for this Windows user.",
                "ChunkPilot", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                var dataRoot = Environment.GetEnvironmentVariable("CHUNKPILOT_DATA_ROOT");
                var logDirectory = Path.Combine(
                    string.IsNullOrWhiteSpace(dataRoot)
                        ? Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "ChunkPilot")
                        : Path.GetFullPath(dataRoot),
                    "Logs");
                Directory.CreateDirectory(logDirectory);
                File.AppendAllText(Path.Combine(logDirectory, "ui-crash.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{SecretRedactor.Redact(args.Exception.ToString())}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (IOException) { }
            IsUnexpectedExit = true;
            args.Handled = true;
            RemoveTrayIcon();
            Shutdown(-1);
        };

        agentClient = new AgentClient();
        var viewModel = new MainViewModel(agentClient, new DialogService());
        // The locally bundled WebUI is ChunkPilot's only product interface. Native WPF remains the
        // window/recovery host, but there is deliberately no legacy product-shell fallback.
        Window window = new WebUiWindow(viewModel, agentClient);
        AppTheme.Attach(window);
        MainWindow = window;

        // Show compact transparent splash while initializing
        var splash = new SplashWindow(viewModel.ReducedMotion);
        splash.Show();

        ConfigureTray(window);
        SessionEnding += (_, _) =>
        {
            if (sessionId == Guid.Empty)
                return;
            var running = viewModel.Servers
                .Where(server => server.State is not ServerState.Stopped and not ServerState.Crashed)
                .Select(server => server.Definition.Id)
                .ToArray();
            agentClient.TrySendOneWay(
                "WindowsShutdown",
                new WindowsShutdownRequest(sessionId, running, DateTimeOffset.UtcNow)
                {
                    SessionCapability = sessionCapability
                },
                150);
        };

        try
        {
            await agentClient.EnsureConnectedAsync().ConfigureAwait(true);
            var registration = await agentClient.SendAsync<UiSessionRegistrationResult>(
                "RegisterUiSession", new UiSessionRegistrationRequest(
                    Environment.ProcessId, ProcessCreationIdentity.OfCurrentProcess())).ConfigureAwait(true);
            sessionId = registration.Session.SessionId;
            sessionCapability = registration.SessionCapability;
            viewModel.ConfigureSession(sessionId, sessionCapability);
            ((WebUiWindow)window).ConfigureSession(sessionId, sessionCapability);
            await viewModel.InitializeAsync().ConfigureAwait(true);
            if (registration.PreviousExitWasUnexpected)
                viewModel.ShowRecoveryNotice(registration.RecoveryMessage);

            // Main window is ready - show it and close splash
            splash.CloseSplash();
            ((WebUiWindow)window).TransitionToShell();
            window.Show();
            if (viewModel.StartMinimized)
                window.WindowState = WindowState.Minimized;
            else
                window.Activate();

        }
        catch (Exception exception)
        {
            // Close splash and show main window with error state
            splash.CloseSplash();
            viewModel.Startup.Fail(exception.Message);
            window.Show();
            ((WebUiWindow)window).ShowStartupFailure();
        }
    }

    /// <summary>
    /// Sets up the notification-area icon and how it restores the existing window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A single left click restores. That is what people expect of a tray icon, and requiring a double
    /// click was the defect: the window appeared not to respond at all. <c>MouseClick</c> fires for the
    /// first click of a double click as well, so a short guard makes the second click a no-op rather
    /// than a second restoration.
    /// </para>
    /// <para>
    /// Restoration always presents the window that already exists - never a second shell - through the
    /// same foreground helper the rest of the application uses, so it is un-minimized, shown, focused
    /// where Windows permits, and flashes in the taskbar where it does not. Nothing is left topmost.
    /// </para>
    /// </remarks>
    private void ConfigureTray(Window window)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open ChunkPilot", null, (_, _) => RestoreFromTray(window));
        menu.Items.Add("Safe stop all and exit", null, (_, _) => window.Close());
        trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "ChunkPilot",
            Visible = window.DataContext is MainViewModel { MinimizeToTray: true },
            ContextMenuStrip = menu
        };
        trayIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                RestoreFromTray(window);
        };
        window.StateChanged += (_, _) =>
        {
            var trayEnabled = window.DataContext is MainViewModel { MinimizeToTray: true };
            if (ShouldHideToTray(window.WindowState, trayEnabled))
            {
                window.Hide();
                trayIcon?.ShowBalloonTip(2_000, "ChunkPilot is still running",
                    "The dashboard is in the notification area. Managed servers remain under agent control.",
                    Forms.ToolTipIcon.Info);
            }
        };
        if (window.DataContext is MainViewModel viewModel)
        {
            viewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainViewModel.MinimizeToTray) && trayIcon is not null)
                    trayIcon.Visible = viewModel.MinimizeToTray;
            };
        }
        // The icon goes the moment the window closes, before the agent is asked to stop anything. A
        // tray icon that outlives its window is indistinguishable from an application that did not exit.
        window.Closing += (_, _) => RemoveTrayIcon();
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/ChunkPilot.ico"))
            ?? throw new InvalidOperationException("The embedded ChunkPilot tray icon is unavailable.");
        using var stream = resource.Stream;
        using var source = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)source.Clone();
    }

    /// <summary>The minimize gesture is converted into a tray hide only when the preference is on.</summary>
    internal static bool ShouldHideToTray(WindowState state, bool trayEnabled) =>
        trayEnabled && state == WindowState.Minimized;

    /// <summary>Shortest interval treated as one interaction, so a double click restores once.</summary>
    internal static TimeSpan TrayClickCoalescingWindow { get; } = TimeSpan.FromMilliseconds(600);

    private DateTimeOffset lastTrayRestoreAt = DateTimeOffset.MinValue;

    private void RestoreFromTray(Window window)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastTrayRestoreAt < TrayClickCoalescingWindow)
            return;
        lastTrayRestoreAt = now;
        WindowForegroundPresenter.Present(window);
    }

    private void RemoveTrayIcon()
    {
        if (trayIcon is null)
            return;
        trayIcon.Visible = false;
        trayIcon.Dispose();
        trayIcon = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        RemoveTrayIcon();
        mutex?.Dispose();
        base.OnExit(e);
    }

}
