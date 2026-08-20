using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using System.Windows.Media;

namespace ChunkPilot.App;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAgentClient client;
    private readonly IDialogService dialogs;
    private readonly Func<IFirewallElevationLauncher> firewallLauncherFactory;
    private readonly IFolderLauncher folderLauncher;
    private bool loadingDetails;
    private Guid? detailsServerId;
    private readonly ConsoleFollowState consoleFollow = new();
    private long consoleClearedThroughSequence;
    private Guid? lastSelectedServerId;
    private TroubleshootingReport currentTroubleshootingReport = new();
    private readonly Dictionary<Guid, PublicConnectivityLeaseIdentity> publicConnectivityLeases = [];
    private UiSessionCredential uiSession = new();

    /// <summary>The server id persisted from a previous session, read once by LoadSettingsAsync
    /// and consumed once by InitializeAsync's startup restoration. Deliberately separate from
    /// <see cref="lastSelectedServerId"/>: that field only becomes non-null once a workspace has
    /// actually been opened *in this process*, which is what tells OnSelectedServerChanged whether
    /// to treat a selection as "entering a (possibly new) server workspace" versus "reaffirming the
    /// one already open". Populating it eagerly from settings before startup restoration runs would
    /// make that first, genuine open look like a no-op reaffirmation and the workspace would never render.</summary>
    private Guid? startupRestoreServerId;

    public MainViewModel(
        IAgentClient client,
        IDialogService dialogs,
        Func<IFirewallElevationLauncher>? firewallLauncherFactory = null,
        IFolderLauncher? folderLauncher = null)
    {
        this.client = client;
        this.dialogs = dialogs;
        this.firewallLauncherFactory = firewallLauncherFactory ?? (() => new ShellElevationLauncher(() =>
            FirewallHelperLocator.Resolve(AppContext.BaseDirectory, File.Exists)));
        this.folderLauncher = folderLauncher ?? new WindowsFolderLauncher();
        Navigation = new NavigationService();
        Startup = new StartupState();
    }

    internal void ConfigureSession(Guid id, string capability)
    {
        uiSession = new UiSessionCredential { SessionId = id, Capability = capability };
    }

    private ServerIdRequest ConnectivityRequest(Guid serverId, PublicConnectivityOperation operation) =>
        new(serverId)
        {
            Session = uiSession,
            Lease = LeaseFor(serverId),
            ConnectivityOperation = operation
        };

    private StopRequest AuthorizedStopRequest(Guid serverId, bool saveFirst) =>
        new(serverId, saveFirst)
        {
            Session = uiSession,
            Lease = LeaseFor(serverId),
            ConnectivityOperation = PublicConnectivityOperation.StopServer
        };

    private PublicConnectivityLeaseIdentity LeaseFor(Guid serverId) =>
        publicConnectivityLeases.TryGetValue(serverId, out var lease)
            ? lease
            : new PublicConnectivityLeaseIdentity();

    private RouterMappingState TrackPublicConnectivity(RouterMappingState state)
    {
        if (state.PublicConnectivityLease.IsPresent)
            publicConnectivityLeases[state.ServerId] = state.PublicConnectivityLease;
        else
            publicConnectivityLeases.Remove(state.ServerId);
        return state;
    }

    /// <summary>
    /// Requests the beginner Vanilla creation workflow without coupling application state to its
    /// current WPF presentation. The shell owns composition and may replace the window later.
    /// </summary>
    public event EventHandler? VanillaCreationRequested;

    /// <summary>Centralized navigation state. Prevents the reversion bug.</summary>
    public NavigationService Navigation { get; }

    /// <summary>Startup progress for the splash experience.</summary>
    public StartupState Startup { get; }

    public ObservableCollection<ServerSnapshot> Servers { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<BackupRecord> Backups { get; } = [];
    public ObservableCollection<ScheduleEntry> Schedules { get; } = [];
    public ObservableCollection<FileSystemEntry> FileEntries { get; } = [];
    public ObservableCollection<ModPluginEntry> Inventory { get; } = [];
    public ObservableCollection<DiagnosticFinding> Diagnostics { get; } = [];
    public ObservableCollection<SelfTestItem> SelfTests { get; } = [];
    public ObservableCollection<ConsoleLine> ConsoleLines { get; } = [];
    public ObservableCollection<WorldEntry> Worlds { get; } = [];
    public bool HasWorlds => Worlds.Count > 0;
    public ObservableCollection<AutomationRecipe> AutomationRecipes { get; } = [];
    public ObservableCollection<AutomationRecipe> AutomationTemplates { get; } = [];
    public ObservableCollection<DatapackInventoryItem> Datapacks { get; } = [];

    public TroubleshootingReport CurrentTroubleshootingReport
    {
        get => currentTroubleshootingReport;
        private set
        {
            if (ReferenceEquals(currentTroubleshootingReport, value))
                return;
            currentTroubleshootingReport = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTroubleshootingGuide));
            OnPropertyChanged(nameof(TroubleshootingTitle));
            OnPropertyChanged(nameof(TroubleshootingSummary));
            OnPropertyChanged(nameof(TroubleshootingEvidence));
            OnPropertyChanged(nameof(ShowsTroubleshootingEntry));
            OnPropertyChanged(nameof(TroubleshootingCalloutTitle));
            OnPropertyChanged(nameof(TroubleshootingCalloutSummary));
            OnPropertyChanged(nameof(TroubleshootingTone));
            OnPropertyChanged(nameof(TroubleshootingActionText));
        }
    }

    public bool HasTroubleshootingGuide => CurrentTroubleshootingReport.HasLikelyFix;
    public string TroubleshootingTitle => CurrentTroubleshootingReport.MostLikely?.Title ?? "";
    public string TroubleshootingSummary => CurrentTroubleshootingReport.MostLikely?.Summary ?? "";
    public string TroubleshootingEvidence => CurrentTroubleshootingReport.MostLikely?.Evidence ?? "";
    public bool ShowsTroubleshootingEntry => IsFailedStart(SelectedServer);

    internal static bool IsFailedStart(ServerSnapshot? snapshot) => snapshot is
    {
        State: ServerState.Crashed or ServerState.Unresponsive,
        LastStartReachedReadiness: false,
        LastError.Length: > 0
    };
    public AppTone TroubleshootingTone => CurrentTroubleshootingReport.HasLikelyFix ||
        SelectedServer?.State is ServerState.Crashed or ServerState.Unresponsive
        ? AppTone.Danger
        : AppTone.Info;
    public string TroubleshootingCalloutTitle => CurrentTroubleshootingReport.MostLikely?.Title ?? "Server not starting?";
    public string TroubleshootingCalloutSummary => CurrentTroubleshootingReport.MostLikely?.Summary ??
        "ChunkPilot can read the recent local console, latest logs, and crash report and rank the most likely fixes.";
    public string TroubleshootingActionText => CurrentTroubleshootingReport.HasLikelyFix
        ? "Show likely fix"
        : "Analyze recent logs";

    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
    [
        new("Dashboard", "Dashboard", "Host resources, alerts, and managed servers", AppIconKind.Home),
        new("Servers", "Servers", "All managed and imported servers", AppIconKind.Server),
        new("Automation", "Automation", "Schedules, recipes, and update center", AppIconKind.Calendar),
        new("Activity", "Activity", "Audited lifecycle and data operations", AppIconKind.History),
        new("Settings", "Settings", "ChunkPilot application preferences", AppIconKind.Settings)
    ];

    // ── Server library filter and sort ──

    public IReadOnlyList<string> LibraryStateFilterOptions { get; } =
        ["All", "Running", "Stopped", "Active", "Needs attention"];

    [ObservableProperty]
    private string? libraryStateFilter = "All";

    [ObservableProperty]
    private string librarySortOrder = "name-ascending";

    public string LibrarySortButtonText => LibrarySortOrder switch
    {
        "name-ascending" => "Name A–Z",
        "name-descending" => "Name Z–A",
        "state-first" => "Status first",
        _ => "Name A–Z"
    };

    /// <summary>Filtered and sorted view for the Servers library page.</summary>
    public IEnumerable<ServerSnapshot> LibraryServers
    {
        get
        {
            var query = Servers.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var search = SearchText;
                query = query.Where(server =>
                    server.Definition.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    server.Definition.RootPath.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    server.Definition.Ecosystem.ToString().Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            if (LibraryStateFilter is not null && LibraryStateFilter != "All")
            {
                query = ApplyStateFilter(query, LibraryStateFilter);
            }

            query = ApplyLibrarySort(query);

            NoSearchResults = HasServers && !query.Any();

            return query;
        }
    }

    [ObservableProperty]
    private bool noSearchResults;

    private static IEnumerable<ServerSnapshot> ApplyStateFilter(IEnumerable<ServerSnapshot> query, string filter) => filter switch
    {
        "Running" => query.Where(s => s.State == ServerState.Running),
        "Stopped" => query.Where(s => s.State == ServerState.Stopped),
        "Active" => query.Where(s => s.State is ServerState.Starting or ServerState.Running or ServerState.Stopping or
            ServerState.Saving or ServerState.Restarting or ServerState.BackingUp or ServerState.Restoring),
        "Needs attention" => query.Where(s => s.State is ServerState.Crashed or ServerState.Unresponsive),
        _ => query
    };

    private IEnumerable<ServerSnapshot> ApplyLibrarySort(IEnumerable<ServerSnapshot> query) => LibrarySortOrder switch
    {
        "name-ascending" => query.OrderBy(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase),
        "name-descending" => query.OrderByDescending(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase),
        "state-first" => query.OrderBy(s => GetStatePriority(s.State))
            .ThenBy(s => s.Definition.Name, StringComparer.OrdinalIgnoreCase),
        _ => query
    };

    private static int GetStatePriority(ServerState state) => state switch
    {
        ServerState.Starting or ServerState.Running or ServerState.Stopping or
        ServerState.Saving or ServerState.Restarting or ServerState.BackingUp or ServerState.Restoring => 0,
        ServerState.Crashed or ServerState.Unresponsive => 1,
        _ => 2
    };

    [RelayCommand]
    private void ToggleLibrarySort()
    {
        LibrarySortOrder = LibrarySortOrder switch
        {
            "name-ascending" => "name-descending",
            "name-descending" => "state-first",
            "state-first" => "name-ascending",
            _ => "name-ascending"
        };
        OnPropertyChanged(nameof(LibrarySortButtonText));
        OnPropertyChanged(nameof(LibraryServers));
    }

    [RelayCommand]
    private void ClearLibraryFilters()
    {
        SearchText = "";
        LibraryStateFilter = "All";
    }

    partial void OnLibraryStateFilterChanged(string? value) => OnPropertyChanged(nameof(LibraryServers));

    partial void OnLibrarySortOrderChanged(string value)
    {
        OnPropertyChanged(nameof(LibrarySortButtonText));
        OnPropertyChanged(nameof(LibraryServers));
    }

    public event EventHandler? ConsoleScrollRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboardPage))]
    [NotifyPropertyChangedFor(nameof(IsServersPage))]
    [NotifyPropertyChangedFor(nameof(IsAutomationPage))]
    [NotifyPropertyChangedFor(nameof(IsActivityPage))]
    [NotifyPropertyChangedFor(nameof(IsSettingsPage))]
    [NotifyPropertyChangedFor(nameof(IsServerPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageTitle))]
    private string currentPage = "Dashboard";

    [ObservableProperty]
    private DashboardSnapshot dashboard = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsServerPage))]
    [NotifyPropertyChangedFor(nameof(CurrentPageTitle))]
    [NotifyPropertyChangedFor(nameof(ServerLocalAddress))]
    [NotifyPropertyChangedFor(nameof(ShowsRuntimeMetrics))]
    [NotifyPropertyChangedFor(nameof(ShowsPerformanceCharts))]
    [NotifyPropertyChangedFor(nameof(ShowsStoppedSummary))]
    [NotifyPropertyChangedFor(nameof(StoppedSummaryText))]
    [NotifyPropertyChangedFor(nameof(CanSendConsoleCommand))]
    [NotifyPropertyChangedFor(nameof(ServerLanAddress))]
    [NotifyPropertyChangedFor(nameof(ServerPublicAddress))]
    [NotifyPropertyChangedFor(nameof(HasConfiguredPublicAddress))]
    [NotifyPropertyChangedFor(nameof(ShowsPublicAccessNotVerifiedCaveat))]
    [NotifyPropertyChangedFor(nameof(ActiveServerSummary))]
    [NotifyPropertyChangedFor(nameof(HasServerIcon))]
    [NotifyPropertyChangedFor(nameof(ShareInstructions))]
    [NotifyPropertyChangedFor(nameof(ClientRequirementText))]
    [NotifyPropertyChangedFor(nameof(BedrockAddress))]
    private ServerSnapshot? selectedServer;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool recoveryNoticeVisible;

    [ObservableProperty]
    private string statusMessage = "Connecting to the local agent…";

    [ObservableProperty]
    private string searchText = "";

    /// <summary>Bound to the global navigation ListBox SelectedItem.</summary>
    [ObservableProperty]
    private NavigationItem? selectedGlobalNavItem;

    /// <summary>Bound to the server navigation ListBox SelectedItem.</summary>
    [ObservableProperty]
    private NavigationItem? selectedServerNavItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAddonManagement))]
    [NotifyPropertyChangedFor(nameof(ShowsJavaGameplay))]
    [NotifyPropertyChangedFor(nameof(ShowsWorldManagement))]
    [NotifyPropertyChangedFor(nameof(ShowsVersionManager))]
    [NotifyPropertyChangedFor(nameof(ShareInstructions))]
    [NotifyPropertyChangedFor(nameof(ClientRequirementText))]
    [NotifyPropertyChangedFor(nameof(ShowsCrossplay))]
    private ServerCapabilityProfile? selectedCapabilities;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrossplayStatus))]
    [NotifyPropertyChangedFor(nameof(BedrockAddress))]
    private CrossplayConfiguration selectedCrossplayConfiguration = new();

    [ObservableProperty]
    private bool installFloodgate = true;

    [ObservableProperty]
    private bool installViaVersion;

    [ObservableProperty]
    private int crossplayBedrockPort = 19_132;

    [ObservableProperty]
    private string datapackSourcePath = "";

    [ObservableProperty]
    private string resourcePackUrl = "";

    [ObservableProperty]
    private string resourcePackSha1 = "";

    [ObservableProperty]
    private bool resourcePackRequired;

    [ObservableProperty]
    private string resourcePackPrompt = "";

    [ObservableProperty]
    private string consoleCommand = "";

    [ObservableProperty]
    private string consoleSeverity = "All";

    [ObservableProperty]
    private string consoleSearchText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConsoleFollowStateText))]
    private int unseenConsoleLines;

    [ObservableProperty]
    private bool isConsoleFollowing = true;

    [ObservableProperty]
    private WorldEntry? selectedWorld;

    [ObservableProperty]
    private AutomationRecipe? selectedAutomationRecipe;

    [ObservableProperty]
    private AutomationRecipe? selectedAutomationTemplate;

    [ObservableProperty]
    private ConnectionTestResult? connectionTest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentFolderText))]
    [NotifyCanExecuteChangedFor(nameof(NavigateFolderUpCommand))]
    private string currentFolder = "";

    [ObservableProperty]
    private BackupRecord? selectedBackup;

    [ObservableProperty]
    private ModPluginEntry? selectedInventoryItem;

    [ObservableProperty]
    private string scheduleName = "Daily backup";

    [ObservableProperty]
    private ScheduledAction scheduleAction = ScheduledAction.Backup;

    [ObservableProperty]
    private int scheduleIntervalMinutes = 1_440;

    [ObservableProperty]
    private ScheduleKind scheduleKind = ScheduleKind.Interval;

    [ObservableProperty]
    private string scheduleAt = "04:00";

    [ObservableProperty]
    private DayOfWeek scheduleDayOfWeek = DayOfWeek.Sunday;

    [ObservableProperty]
    private int scheduleDayOfMonth = 1;

    [ObservableProperty]
    private string scheduleCron = "0 4 * * *";

    [ObservableProperty]
    private string scheduleCommand = "";

    [ObservableProperty]
    private int restartCountdownSeconds = 60;

    [ObservableProperty]
    private bool backupBeforeRestart;

    [ObservableProperty]
    private string startupExecutable = "";

    [ObservableProperty]
    private string startupArguments = "";

    [ObservableProperty]
    private string startupWorkingDirectory = "";

    [ObservableProperty]
    private string startupReadinessPattern = "";

    [ObservableProperty]
    private int startupTimeoutSeconds;

    [ObservableProperty]
    private int shutdownTimeoutSeconds;

    [ObservableProperty]
    private bool runInBackground = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MinimumMemoryGb))]
    [NotifyPropertyChangedFor(nameof(MemoryDetailText))]
    private int minimumRamMb = 1_024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MaximumMemoryGb))]
    [NotifyPropertyChangedFor(nameof(MemoryDetailText))]
    private int maximumRamMb = 4_096;

    [ObservableProperty]
    private string userConfiguredHostname = "";

    // The editable server.properties surface. Every one of these notifies HasServerPropertyChanges,
    // which is what keeps Apply disabled until something genuinely differs from the file on disk.
    // The behaviour around them lives in MainViewModel.Configuration.cs.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private string propertyMotd = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private int propertyPort = 25565;

    /// <summary>Familiar alternatives; the editable selector also accepts any valid custom port.</summary>
    public IReadOnlyList<int> CommonServerPorts { get; } = [25565, 25566, 25567, 25568, 25569, 25570, 25600];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private int propertyMaxPlayers = 20;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private string propertyDifficulty = "easy";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private string propertyGameMode = "survival";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyOnlineMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyPvp = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyWhiteList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private int propertyViewDistance = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private int propertySimulationDistance = 10;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyAllowFlight;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyCommandBlocks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private int propertySpawnProtection = 16;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyHardcore;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyForceGameMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyEnforceWhitelist;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private bool propertyHideOnlinePlayers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertyChanges))]
    private int propertyPlayerIdleTimeout;

    [ObservableProperty]
    private bool reducedMotion;

    [ObservableProperty]
    private string propertyRaw = "";

    [ObservableProperty]
    private bool minimizeToTray;

    [ObservableProperty]
    private bool startMinimized;

    [ObservableProperty]
    private bool startWithWindows;

    [ObservableProperty]
    private string defaultBackupDirectory = "";

    [ObservableProperty]
    private int selectedServerTabIndex;

    [ObservableProperty]
    private string selectedServerDestination = "Overview";

    public IReadOnlyList<string> ServerWorkspaceDestinations { get; } =
        ServerDestination.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkGuidance))]
    [NotifyPropertyChangedFor(nameof(NetworkChoiceSummary))]
    [NotifyPropertyChangedFor(nameof(IsDirectInternetSelected))]
    private NetworkMode selectedNetworkMode = NetworkMode.ConfigureLater;

    public IReadOnlyList<NetworkMode> NetworkModes { get; } = Enum.GetValues<NetworkMode>();
    public string NetworkGuidance => string.Join(
        Environment.NewLine,
        NetworkPolicy.Guidance(SelectedNetworkMode, SelectedCapabilities?.Edition == ServerEdition.Bedrock));
    public string NetworkChoiceSummary => SelectedNetworkMode switch
    {
        NetworkMode.ThisComputerOnly => "Only this PC. No network setup is needed.",
        NetworkMode.HomeNetwork => "Players on this Wi-Fi or wired network. Windows Firewall may need deliberate approval.",
        NetworkMode.PortForwarding => "Friends outside your network. Router and Windows Firewall setup still require separate approval.",
        NetworkMode.OfficialTunnel => "Friends connect through an explicitly configured tunnel provider.",
        _ => "No networking method is configured. The server remains local."
    };

    public bool IsDashboardPage => CurrentPage == "Dashboard" && SelectedServer is null;
    public bool IsServersPage => CurrentPage == "Servers" && SelectedServer is null;
    public bool IsAutomationPage => CurrentPage == "Automation" && SelectedServer is null;
    public bool IsActivityPage => CurrentPage == "Activity" && SelectedServer is null;
    public bool IsSettingsPage => CurrentPage == "Settings" && SelectedServer is null;
    public bool IsServerPage => SelectedServer is not null;
    public bool ShowsAddonManagement =>
        SelectedCapabilities is null || SelectedCapabilities.SupportsMods || SelectedCapabilities.SupportsPlugins;
    public bool ShowsJavaGameplay =>
        SelectedCapabilities is null || SelectedCapabilities.Edition == ServerEdition.Java;
    public bool ShowsWorldManagement =>
        SelectedCapabilities is null || SelectedCapabilities.SupportsWorldSwitching;
    public bool ShowsVersionManager =>
        SelectedCapabilities is null || SelectedCapabilities.SupportsServerSoftwareUpdate ||
        SelectedCapabilities.SupportsFullModpackUpdate;
    public bool ShowsCrossplay =>
        SelectedCapabilities is null || SelectedCapabilities.SupportsGeyser;
    public string CurrentPageTitle => SelectedServer?.Definition.Name ?? CurrentPage;
    public string ServerLocalAddress => SelectedServer is null ? "" : $"localhost:{SelectedServer.Definition.Port}";
    public string ActiveServerSummary => SelectedServer is null
        ? ""
        : $"{SelectedServer.Definition.Name}, {SelectedServer.State}";

    /// <summary>
    /// True only when the server is running and the agent has actually sampled it.
    /// </summary>
    /// <remarks>
    /// Runtime metrics exist while a process exists. Showing their cards regardless produced a row of
    /// empty boxes on a stopped server, which reads as a fault rather than as "there is nothing to
    /// measure". One sentence replaces them instead.
    /// </remarks>
    public bool ShowsRuntimeMetrics =>
        SelectedServer is { State: ServerState.Running, CurrentStatistics: not null };

    /// <summary>Charts need at least two real process samples; one point is a value, not a trend.</summary>
    public bool ShowsPerformanceCharts =>
        ShowsRuntimeMetrics && SelectedServer!.RecentStatistics.Count >= 2;

    /// <summary>True when the server is not running, so the metrics are legitimately absent.</summary>
    public bool ShowsStoppedSummary => SelectedServer is not null && !ShowsRuntimeMetrics;

    public string MaximumRamText => SelectedServer is null
        ? "—"
        : FormatMemoryLimit(SelectedServer.Definition.MaximumRamMb);

    public string CurrentMemoryText => SelectedServer?.CurrentStatistics is { } statistics
        ? FormatMemoryBytes(statistics.WorkingSetBytes)
        : "—";

    private static string FormatMemoryLimit(int megabytes) => megabytes % 1024 == 0
        ? $"{megabytes / 1024d:0} GB"
        : $"{megabytes / 1024d:0.#} GB";

    private static string FormatMemoryBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024d * 1024 * 1024):0.0} GB"
        : $"{bytes / (1024d * 1024):0} MB";

    /// <summary>Why there is nothing to measure, stated once rather than as several blank cards.</summary>
    public string StoppedSummaryText => SelectedServer?.State switch
    {
        null => "",
        ServerState.Running => "Waiting for the first sample from the background service.",
        ServerState.Crashed => "The server stopped unexpectedly. Players, CPU, memory and uptime are measured only while it runs.",
        _ => "Players, CPU, memory and uptime are measured only while the server runs."
    };
    public string ServerLanAddress => SelectedServer is null || string.IsNullOrWhiteSpace(Dashboard.Host.LanAddress)
        ? "Unavailable" : FormatEndpoint(Dashboard.Host.LanAddress, SelectedServer.Definition.Port);
    public bool HasConfiguredPublicAddress =>
        SelectedServer is not null && !string.IsNullOrWhiteSpace(SelectedServer.Definition.UserConfiguredHostname);
    public string ServerPublicAddress => SelectedServer is null || string.IsNullOrWhiteSpace(SelectedServer.Definition.UserConfiguredHostname)
        ? "Not configured" : FormatEndpoint(SelectedServer.Definition.UserConfiguredHostname, SelectedServer.Definition.Port);
    public string BedrockAddress =>
        !SelectedCrossplayConfiguration.GeyserEnabled
            ? "Not configured"
            : Dashboard.Host.LanAddress.Length == 0
                ? "LAN address unavailable"
                : FormatEndpoint(Dashboard.Host.LanAddress, SelectedCrossplayConfiguration.BedrockPort);

    private static string FormatEndpoint(string host, int port) =>
        System.Net.IPAddress.TryParse(host, out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]:{port}"
            : $"{host}:{port}";
    public string CrossplayStatus =>
        !SelectedCrossplayConfiguration.GeyserEnabled
            ? "Not installed"
            : $"Geyser {CrossplayVersion(CrossplayPackageKind.Geyser)}; " +
              $"authentication {SelectedCrossplayConfiguration.AuthenticationMode}; " +
              $"Bedrock UDP {SelectedCrossplayConfiguration.BedrockPort}";
    private ImageSource? serverIconImage;
    public ImageSource? ServerIconImage
    {
        get => serverIconImage;
        private set
        {
            if (SetProperty(ref serverIconImage, value))
                OnPropertyChanged(nameof(HasServerIcon));
        }
    }
    public bool HasServerIcon => ServerIconImage is not null;
    public string ClientRequirementText => SelectedCapabilities?.RequiresMatchingClientPack == true
        ? "Friends need the matching client pack and exact version."
        : SelectedCapabilities?.AllowsUnmodifiedClients == true
            ? "Friends can normally use an unmodified Minecraft client."
            : "Client requirements are undetected; review the server or pack documentation.";
    public string ShareInstructions => SelectedServer is null
        ? ""
        : $"""
           {SelectedServer.Definition.Name}
           Minecraft: {SelectedServer.Definition.MinecraftVersion}
           Software: {SelectedServer.Definition.Ecosystem}
           Java address on this network: {ServerLanAddress}
           Public address: {ServerPublicAddress}
           {ClientRequirementText}
           Ask the server owner to add your exact Minecraft name to the whitelist before joining.
           """;
    public IEnumerable<ServerSnapshot> FilteredServers => string.IsNullOrWhiteSpace(SearchText)
        ? Servers
        : Servers.Where(server =>
            server.Definition.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            server.Definition.RootPath.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
            server.Definition.Ecosystem.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase));
    public int RunningCount => Servers.Count(server => server.State == ServerState.Running);
    public int StoppedCount => Servers.Count(server => server.State == ServerState.Stopped);
    public int ProblemCount => Servers.Count(server => server.State is ServerState.Crashed or ServerState.Unresponsive);
    public int ImportedCount => Servers.Count(server => !server.Definition.IsManaged);
    public int ManagedCount => Servers.Count(server => server.Definition.IsManaged);
    public bool HasServers => Servers.Count > 0;
    public int StartingCount => Servers.Count(server => server.State is ServerState.Starting or ServerState.Restarting);
    public int OperationCount => Servers.Count(server => server.State is ServerState.BackingUp or ServerState.Restoring or ServerState.Saving);

    /// <summary>Concise fleet summary for the Dashboard header.</summary>
    public string DashboardSummary
    {
        get
        {
            if (Servers.Count == 0) return "No servers configured";
            var parts = new List<string>();
            if (RunningCount > 0) parts.Add($"{RunningCount} running");
            if (StoppedCount > 0) parts.Add($"{StoppedCount} stopped");
            if (ProblemCount > 0) parts.Add($"{ProblemCount} need attention");
            if (StartingCount > 0) parts.Add($"{StartingCount} starting");
            return parts.Count > 0 ? string.Join(" · ", parts) : $"{Servers.Count} servers";
        }
    }
    public int TotalConfiguredXmxMb => Servers.Sum(server => server.Definition.MaximumRamMb);
    public double CombinedCpu => Servers.Sum(server => server.CurrentStatistics?.CpuPercent ?? 0);
    public long CombinedRam => Servers.Sum(server => server.CurrentStatistics?.WorkingSetBytes ?? 0);
    public IReadOnlyList<ScheduledAction> ScheduleActions { get; } = Enum.GetValues<ScheduledAction>();
    public IReadOnlyList<ScheduleKind> ScheduleKinds { get; } = Enum.GetValues<ScheduleKind>();
    public IReadOnlyList<DayOfWeek> ScheduleDays { get; } = Enum.GetValues<DayOfWeek>();
    public IReadOnlyList<string> GameModes { get; } = ServerPropertyValidation.GameModes;
    public IReadOnlyList<string> Difficulties { get; } = ServerPropertyValidation.Difficulties;
    public IReadOnlyList<string> ConsoleSeverities { get; } = ["All", "Info", "Warning", "Error", "Chat"];
    public IEnumerable<ConsoleLine> FilteredConsoleLines => ConsoleLines.Where(MatchesConsoleFilter);

    /// <summary>
    /// The follow state in words, so it is not conveyed by a check box alone.
    /// </summary>
    /// <remarks>
    /// Deliberately says nothing about whether the server is running: following describes where the
    /// viewport is, not what the server is doing.
    /// </remarks>
    public string ConsoleFollowStateText => IsConsoleFollowing
        ? "Following new output"
        : UnseenConsoleLines > 0 ? "Paused · new output below" : "Paused";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredServers));
        OnPropertyChanged(nameof(LibraryServers));
    }
    partial void OnConsoleSeverityChanged(string value) => OnPropertyChanged(nameof(FilteredConsoleLines));
    partial void OnConsoleSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredConsoleLines));
    partial void OnIsConsoleFollowingChanged(bool value)
    {
        if (value)
        {
            consoleFollow.JumpToLatest();
            UnseenConsoleLines = 0;
            ConsoleScrollRequested?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            consoleFollow.OnViewportChanged(false);
        }
        OnPropertyChanged(nameof(ConsoleFollowStateText));
    }

    partial void OnSelectedServerChanged(ServerSnapshot? value)
    {
        RefreshServerIconImage(value?.Definition);
        if (value?.State is ServerState.Starting or ServerState.Restarting)
        {
            memoryRestartPending = false;
            serverPropertiesRestartPending = false;
        }
        OnPropertyChanged(nameof(IsServerPage));
        OnPropertyChanged(nameof(CurrentPageTitle));
        OnPropertyChanged(nameof(MaximumRamText));
        OnPropertyChanged(nameof(CurrentMemoryText));
        OnPropertyChanged(nameof(ShowsRuntimeMetrics));
        OnPropertyChanged(nameof(ShowsPerformanceCharts));
        OnPropertyChanged(nameof(ShowsStoppedSummary));
        NotifyMemoryState();
        ApplyCustomGameruleCommand.NotifyCanExecuteChanged();
        OpenServerFolderCommand.NotifyCanExecuteChanged();
        RenameServerCommand.NotifyCanExecuteChanged();
        ConnectionTest = null;
        CurrentTroubleshootingReport = TroubleshootingService.Analyze(value);
        if (value is null)
        {
            Navigation.IsServerWorkspaceActive = false;
            return;
        }
        CurrentPage = "Server";
        if (lastSelectedServerId != value.Definition.Id)
        {
            lastSelectedServerId = value.Definition.Id;
            Navigation.OpenServer(value.Definition.Id);
            _ = client.SendAsync<OperationResult>("SetSetting",
                new SettingsValueRequest("lastSelectedServerId", value.Definition.Id.ToString("D")));
        }
        else
        {
            // Same server re-selected (e.g., from refresh) - do NOT change navigation
            Navigation.IsServerWorkspaceActive = true;
        }
        if (detailsServerId != value.Definition.Id)
        {
            detailsServerId = value.Definition.Id;
            // Editable fields are filled from the definition when the workspace opens, and never
            // again from a refresh. Refresh runs every second, so re-assigning them there overwrote
            // whatever the user was in the middle of typing on the Settings page.
            LoadEditableDefinitionFields(value.Definition);
            // A different server means a different server.properties: what was on screen is not an
            // unsaved edit to this one, and must not block the first read.
            ResetServerPropertyEditor();
            ConsoleLines.Clear();
            consoleClearedThroughSequence = 0;
            consoleFollow.JumpToLatest();
            SyncConsole(value.Console);
            _ = LoadServerDetailsAsync();
        }
        SyncPlayerAccessStamp(value);
    }

    [RelayCommand]
    private async Task OpenTroubleshootingGuideAsync(ActivityEntry? activity = null)
    {
        var report = activity is null ? CurrentTroubleshootingReport : TroubleshootingService.Analyze(activity);
        var serverId = activity?.ServerId ?? SelectedServer?.Definition.Id;
        if (serverId is not null && (activity is null || !report.HasLikelyFix))
        {
            try
            {
                var logReport = await client.SendAsync<TroubleshootingReport>(
                    "Troubleshoot", new ServerIdRequest(serverId.Value)).ConfigureAwait(true);
                if (logReport.HasLikelyFix)
                    report = logReport;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
            {
                StatusMessage = $"Could not read the latest logs: {exception.Message}";
            }
        }
        if (!report.HasLikelyFix)
        {
            dialogs.ShowInformation("No known error signature found",
                "ChunkPilot read the available local error text but did not find a safe high-probability match. Open Protection > Diagnostics for the raw findings and log paths.");
            return;
        }

        var window = new TroubleshootingWindow(report)
        {
            Owner = Application.Current.MainWindow
        };
        AppTheme.Attach(window);
        window.ShowDialog();
    }

    private void LoadEditableDefinitionFields(ServerDefinition definition)
    {
        StartupExecutable = definition.Executable;
        StartupArguments = definition.Arguments;
        StartupWorkingDirectory = definition.WorkingDirectory;
        StartupReadinessPattern = definition.ReadinessPattern;
        StartupTimeoutSeconds = definition.StartupTimeoutSeconds;
        ShutdownTimeoutSeconds = definition.ShutdownTimeoutSeconds;
        RunInBackground = definition.RunInBackground;
        MinimumRamMb = definition.MinimumRamMb;
        MaximumRamMb = definition.MaximumRamMb;
        MarkMemoryLoaded(definition.MinimumRamMb, definition.MaximumRamMb);
        UserConfiguredHostname = definition.UserConfiguredHostname;
    }

    partial void OnSelectedServerTabIndexChanged(int value)
    {
        // Legacy compatibility: map tab index to the new semantic destination
        var destinations = ServerDestination.All;
        var index = Math.Clamp(value, 0, destinations.Count - 1);
        var destination = destinations[index];
        if (SelectedServer is not null)
            Navigation.NavigateServer(destination, SelectedServer.Definition.Id);
    }

    partial void OnSelectedServerDestinationChanged(string value)
    {
        // Keep index in sync for any legacy code reading it
        var index = ServerDestination.All.ToList().IndexOf(value);
        if (index >= 0 && index != SelectedServerTabIndex)
            SelectedServerTabIndex = index;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Startup.Advance(StartupPhase.ConnectingAgent, "Connecting to the ChunkPilot service");
        await client.EnsureConnectedAsync(cancellationToken).ConfigureAwait(true);
        Startup.Advance(StartupPhase.RestoringWorkspace, "Restoring your workspace");
        await LoadSettingsAsync(cancellationToken).ConfigureAwait(true);
        Startup.Advance(StartupPhase.LoadingServers, "Loading your servers");
        await RefreshAsync(cancellationToken).ConfigureAwait(true);

        // One-time workspace restoration: only at startup does a remembered server id open a
        // workspace. Ongoing refreshes must never re-select a server the user has navigated away
        // from (see RefreshAsync), or a stale id would keep bouncing the user back into it.
        if (SelectedServer is null && startupRestoreServerId is { } startupServerId)
        {
            var restored = Servers.FirstOrDefault(server => server.Definition.Id == startupServerId);
            if (restored is not null)
                SelectedServer = restored;
        }
        Startup.Advance(StartupPhase.PreparingDashboard, "Preparing the dashboard");
        StatusMessage = "Connected";
        Startup.Complete();
    }

    public void ShowRecoveryNotice(string message)
    {
        RecoveryNoticeVisible = true;
        StatusMessage = message;
    }

    [RelayCommand]
    private static void OpenUiCrashDetails()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChunkPilot", "Logs", "ui-crash.log");
        if (File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        SelectedServer = null;
        detailsServerId = null;
        CurrentPage = page;
        Navigation.NavigateGlobal(page);
        if (page == "Automation")
            _ = LoadSchedulesAsync();
        else if (page == "Activity")
            { /* Activity loaded by refresh */ }
    }

    [RelayCommand]
    private void SelectServer(ServerSnapshot? server)
    {
        if (server is not null)
            SelectedServer = server;
    }

    /// <summary>Navigate to a specific server destination. Used by the shell's server nav.</summary>
    [RelayCommand]
    private void NavigateServerDestination(string destination)
    {
        if (SelectedServer is null)
            return;
        Navigation.NavigateServer(destination, SelectedServer.Definition.Id);
        OnServerDestinationEntered(destination);
    }

    [RelayCommand]
    private void OpenProtection() => NavigateServerDestination(ServerDestination.Protection);

    [RelayCommand]
    private void OpenAccess() => NavigateServerDestination(ServerDestination.Access);

    [RelayCommand]
    private void OpenOverview() => NavigateServerDestination(ServerDestination.Overview);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Snapshot the user's navigation intent before the await. Refresh must only ever
            // reaffirm *this exact* selection (or lack of one) with fresh data - never fall back
            // to lastSelectedServerId, which would resurrect a workspace the user already
            // navigated away from. Capturing both version counters also lets us detect a user
            // navigating away *while* this refresh is in flight, so a stale completion can't
            // clobber newer navigation (e.g. quickly switching servers, or Servers -> Dashboard).
            //
            // We deliberately do NOT compare SelectedServer's value after the await to detect
            // that: Replace() below clears then repopulates the Servers collection, and because
            // the sidebar list's SelectedItem is bound TwoWay to SelectedServer, that Clear() can
            // synchronously null SelectedServer out from under us as a side effect - not because
            // the user navigated anywhere. The version counters only advance on real user-driven
            // navigation (NavigateGlobal, NavigateServer, OpenServer), never on that side effect,
            // so they are what must gate whether it is safe to reassign SelectedServer back.
            var selectedIdAtStart = SelectedServer?.Definition.Id;
            var globalVersionAtStart = Navigation.GlobalVersion;
            var serverVersionAtStart = Navigation.ServerVersion;
            var snapshot = await client.SendAsync<DashboardSnapshot>("Dashboard", cancellationToken: cancellationToken)
                .ConfigureAwait(true);
            Dashboard = snapshot;
            Replace(Servers, snapshot.Servers);
            Replace(Activity, snapshot.RecentActivity);

            var navigationUnchanged = Navigation.GlobalVersion == globalVersionAtStart
                && Navigation.ServerVersion == serverVersionAtStart;
            if (navigationUnchanged && selectedIdAtStart is { } id)
            {
                var refreshed = Servers.FirstOrDefault(server => server.Definition.Id == id);
                if (refreshed is not null)
                {
                    // Same server - refresh the reference with new data, route is untouched.
                    SelectedServer = refreshed;
                }
                else
                {
                    // The server being viewed is gone (deleted/removed). Return to a safe global
                    // destination once; there is no remembered id to keep retrying, so this cannot loop.
                    SelectedServer = null;
                    CurrentPage = GlobalDestination.Dashboard;
                    Navigation.NavigateGlobal(GlobalDestination.Dashboard);
                }
            }

            if (SelectedServer is not null)
            {
                SyncConsole(SelectedServer.Console);
                if (ShouldPollRouterMapping)
                {
                    await LoadRouterMappingAsync().ConfigureAwait(true);
                    // Local only. This reads the Agent's own record of the last external check and
                    // never contacts the probe service, which is what lets a verified badge stop
                    // being current the moment the server or the mapping does.
                    await LoadExternalReachabilityAsync().ConfigureAwait(true);
                }
            }
            OnPropertyChanged(nameof(FilteredServers));
            OnPropertyChanged(nameof(LibraryServers));
            OnPropertyChanged(nameof(RunningCount));
            OnPropertyChanged(nameof(StoppedCount));
            OnPropertyChanged(nameof(ProblemCount));
            OnPropertyChanged(nameof(ImportedCount));
            OnPropertyChanged(nameof(ManagedCount));
            OnPropertyChanged(nameof(HasServers));
            OnPropertyChanged(nameof(StartingCount));
            OnPropertyChanged(nameof(OperationCount));
            OnPropertyChanged(nameof(DashboardSummary));
            OnPropertyChanged(nameof(TotalConfiguredXmxMb));
            OnPropertyChanged(nameof(CombinedCpu));
            OnPropertyChanged(nameof(CombinedRam));
            OnPropertyChanged(nameof(ServerLanAddress));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException)
        {
            StatusMessage = $"Agent disconnected: {exception.Message}";
        }
    }

    [RelayCommand]
    private async Task AddServerAsync()
    {
        var folder = dialogs.SelectFolder("Select an existing Minecraft server folder", null);
        if (folder is null)
            return;
        await RunBusyAsync("Scanning server folder without writing to it…", async () =>
        {
            var detection = await client.SendAsync<ServerDetectionResult>("Detect", new DetectServerRequest(folder)).ConfigureAwait(true);
            var importViewModel = new ImportServerViewModel(detection);
            var window = new ImportServerWindow(importViewModel) { Owner = Application.Current.MainWindow };
            AppTheme.Attach(window);
            if (window.ShowDialog() != true)
                return;
            await client.SendAsync<OperationResult>("Import", importViewModel.BuildDefinition()).ConfigureAwait(true);
            StatusMessage = "Server imported by reference. Its folder was not changed.";
            await RefreshAsync().ConfigureAwait(true);
            SelectedServer = Servers.FirstOrDefault(server => server.Definition.RootPath.Equals(folder, StringComparison.OrdinalIgnoreCase));
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CreateVanillaServer() =>
        VanillaCreationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanStartServer))]
    private async Task StartServerAsync(ServerSnapshot? server) =>
        await LifecycleAsync("Start", server, "Starting server…").ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanSaveServer))]
    private async Task SaveServerAsync(ServerSnapshot? server) =>
        await LifecycleAsync("Save", server, "Waiting for save confirmation…").ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanStopServer))]
    private async Task StopServerAsync(ServerSnapshot? server) =>
        await LifecycleAsync("Stop", server, "Saving and stopping server…",
            AuthorizedStopRequest(server?.Definition.Id ?? Guid.Empty, true)).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanRestartServer))]
    private async Task RestartServerAsync(ServerSnapshot? server) =>
        await LifecycleAsync("Restart", server, "Saving, stopping, and restarting server…").ConfigureAwait(true);

    [RelayCommand]
    private async Task StartAllAsync() =>
        await RunBusyAsync("Starting all stopped servers…", async () =>
        {
            _ = await client.SendAsync<Dictionary<Guid, OperationResult>>("StartAll",
                new AllServersLifecycleRequest
                {
                    Session = uiSession,
                    Leases = publicConnectivityLeases.Values.ToArray(),
                    ConnectivityOperation = PublicConnectivityOperation.StartAllServers
                }).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

    [RelayCommand]
    private async Task StopAllAsync() =>
        await RunBusyAsync("Saving and stopping all running servers…", async () =>
        {
            var results = await client.SendAsync<Dictionary<Guid, OperationResult>>("StopAll",
                new AllServersLifecycleRequest
                {
                    Session = uiSession,
                    Leases = publicConnectivityLeases.Values.ToArray(),
                    ConnectivityOperation = PublicConnectivityOperation.StopAllServers
                }).ConfigureAwait(true);
            var failures = results.Where(pair => !pair.Value.Success).ToArray();
            if (failures.Length > 0)
                dialogs.ShowError("Some servers did not stop", string.Join(Environment.NewLine, failures.Select(pair => pair.Value.Message)));
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);

    /// <summary>
    /// True when there is a server, something to send, and no send already in flight.
    /// </summary>
    /// <remarks>
    /// Bound by both the Send button and the Enter key, so Enter cannot reach past a disabled state
    /// and a held key cannot queue a second copy of the same command.
    /// </remarks>
    public bool CanSendConsoleCommand =>
        SelectedServer is not null && !string.IsNullOrWhiteSpace(ConsoleCommand) && !isSendingConsoleCommand;

    private bool isSendingConsoleCommand;

    [RelayCommand(CanExecute = nameof(CanSendConsoleCommand))]
    private async Task SendConsoleCommandAsync()
    {
        if (!CanSendConsoleCommand)
            return;
        var command = ConsoleCommand.Trim();
        isSendingConsoleCommand = true;
        SendConsoleCommandCommand.NotifyCanExecuteChanged();
        consoleFollow.CommandSent();
        IsConsoleFollowing = true;
        UnseenConsoleLines = 0;
        ConsoleScrollRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            await RunBusyAsync("Sending console command…", async () =>
            {
                var result = await client.SendAsync<OperationResult>("SendCommand",
                    new CommandRequest(SelectedServer!.Definition.Id, command)).ConfigureAwait(true);
                StatusMessage = result.Message;
                // Cleared only once the send has been accepted. A command that could not be sent stays
                // in the box so it can be corrected or retried rather than silently lost.
                if (result.Success)
                    ConsoleCommand = "";
                await RefreshAsync().ConfigureAwait(true);
                // A command typed here can change exactly what the other pages show. The server writes
                // its files as it answers, so a re-read is queued rather than done inline; the access
                // stamp picks up anything that lands later.
                if (result.Success)
                    await ReloadAfterConsoleCommandAsync(command).ConfigureAwait(true);
            }).ConfigureAwait(true);
        }
        finally
        {
            isSendingConsoleCommand = false;
            SendConsoleCommandCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnConsoleCommandChanged(string value)
    {
        OnPropertyChanged(nameof(CanSendConsoleCommand));
        SendConsoleCommandCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void JumpToLatest()
    {
        consoleFollow.JumpToLatest();
        IsConsoleFollowing = true;
        UnseenConsoleLines = 0;
        ConsoleScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearConsoleView()
    {
        consoleClearedThroughSequence = ConsoleLines.LastOrDefault()?.Sequence ?? consoleClearedThroughSequence;
        ConsoleLines.Clear();
        consoleFollow.JumpToLatest();
        IsConsoleFollowing = true;
        UnseenConsoleLines = 0;
        OnPropertyChanged(nameof(FilteredConsoleLines));
        StatusMessage = "Visible console output cleared. Server log files were not changed.";
    }

    [RelayCommand]
    private async Task LoadFilesAsync() => await LoadFilesCoreAsync(CurrentFolder).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanNavigateFolderUp))]
    private async Task NavigateFolderUpAsync()
    {
        if (string.IsNullOrEmpty(CurrentFolder))
            return;
        await LoadFilesCoreAsync(Path.GetDirectoryName(CurrentFolder) ?? "").ConfigureAwait(true);
    }

    private bool CanNavigateFolderUp() => CurrentFolder.Length > 0;

    [RelayCommand(CanExecute = nameof(CanOpenServerFolder))]
    private void OpenServerFolder()
    {
        if (SelectedServer is null)
            return;
        var result = folderLauncher.OpenExisting(SelectedServer.Definition.RootPath);
        StatusMessage = result.Message;
        if (!result.Success)
            ShowOperationFailure("Server folder could not be opened", result.Message);
    }

    private void RefreshServerIconImage(ServerDefinition? definition, bool refreshServerLists = false)
    {
        var path = definition is null ? "" : Path.Combine(definition.RootPath, "server-icon.png");
        try
        {
            ServerIconImage = ServerIconImageLoader.LoadDetached(path);
            if (refreshServerLists)
            {
                OnPropertyChanged(nameof(FilteredServers));
                OnPropertyChanged(nameof(LibraryServers));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or NotSupportedException or InvalidDataException)
        {
            // A refresh must never discard the last confirmed preview. The Agent will report an
            // install failure separately, and this avoids lying about an icon that did not finalize.
            StatusMessage = $"The saved server icon could not be read: {exception.Message}";
        }
    }

    private bool CanOpenServerFolder() => SelectedServer is not null;

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (SelectedServer is null)
            return;
        var logs = Path.Combine(SelectedServer.Definition.RootPath, "logs");
        Directory.CreateDirectory(logs);
        Process.Start(new ProcessStartInfo("explorer.exe", CommandLineQuoter.QuoteWindowsArgument(logs)) { UseShellExecute = true });
    }

    [RelayCommand]
    private void OpenWorldsFolder()
    {
        if (SelectedServer is null)
            return;
        var active = Worlds.FirstOrDefault(world => world.IsActive)?.FolderPath ?? SelectedServer.Definition.RootPath;
        Process.Start(new ProcessStartInfo("explorer.exe", CommandLineQuoter.QuoteWindowsArgument(active)) { UseShellExecute = true });
    }

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text is "Unavailable" or "Not configured")
            return;
        System.Windows.Clipboard.SetText(text);
        StatusMessage = $"Copied: {text}";
    }

    [RelayCommand]
    private void CopyShareInstructions()
    {
        if (!string.IsNullOrWhiteSpace(ShareInstructions))
            System.Windows.Clipboard.SetText(ShareInstructions);
        StatusMessage = "Complete joining instructions copied.";
    }

    [RelayCommand]
    private void OpenPlayers() => SelectedServerTabIndex = 11;

    [RelayCommand]
    private async Task SaveNetworkModeAsync()
    {
        if (SelectedServer is null)
            return;
        var result = await client.SendAsync<OperationResult>(
            "SetNetworkConfiguration",
            new NetworkConfiguration
            {
                ServerId = SelectedServer.Definition.Id,
                Mode = SelectedNetworkMode,
                JavaPort = SelectedServer.Definition.Port,
                LanAddress = ServerLanAddress == "Unavailable" ? "" : ServerLanAddress,
                PublicAddress = ServerPublicAddress == "Not configured" ? "" : ServerPublicAddress,
                PublicAddressExternallyConfirmed = false
            }).ConfigureAwait(true);
        StatusMessage = result.Message;
    }

    [RelayCommand]
    private async Task InstallCrossplayAsync()
    {
        if (SelectedServer is null)
            return;
        await RunBusyAsync("Installing verified crossplay packages…", async () =>
        {
            var result = await client.SendAsync<CrossplayInstallResult>(
                "InstallCrossplay",
                new CrossplayInstallRequest(
                    SelectedServer.Definition.Id,
                    InstallFloodgate,
                    InstallViaVersion,
                    CrossplayBedrockPort)).ConfigureAwait(true);
            SelectedCrossplayConfiguration = result.Configuration;
            StatusMessage = result.Message;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RemoveCrossplayAsync()
    {
        if (SelectedServer is null)
            return;
        var result = await client.SendAsync<OperationResult>(
            "RemoveCrossplay",
            new CrossplayRemoveRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
        StatusMessage = result.Message;
        await LoadCrossplayAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void BrowseDatapack() =>
        DatapackSourcePath = dialogs.SelectFile(
            "Select a datapack ZIP",
            "Datapacks (*.zip)|*.zip") ?? DatapackSourcePath;

    [RelayCommand]
    private async Task InstallDatapackAsync()
    {
        if (SelectedServer is null || SelectedWorld is null ||
            string.IsNullOrWhiteSpace(DatapackSourcePath))
            return;
        var item = await client.SendAsync<DatapackInventoryItem>(
            "InstallDatapack",
            new DatapackInstallRequest(
                SelectedServer.Definition.Id,
                SelectedWorld.Name,
                DatapackSourcePath)).ConfigureAwait(true);
        StatusMessage =
            $"Datapack installed for {item.WorldName}; pack format {item.PackFormat}; {item.Compatibility}.";
        DatapackSourcePath = "";
        await LoadDatapacksAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CalculateResourcePackHashAsync()
    {
        var path = dialogs.SelectFile(
            "Select a local resource-pack ZIP to calculate its SHA-1",
            "Resource packs (*.zip)|*.zip");
        if (string.IsNullOrWhiteSpace(path))
            return;
        ResourcePackSha1 = (await client.SendAsync<TextResponse>(
            "CalculateResourcePackSha1",
            new ResourcePackHashRequest(path)).ConfigureAwait(true)).Value;
        StatusMessage =
            "SHA-1 calculated. The local ZIP was not hosted or shared; enter its real public HTTPS URL separately.";
    }

    [RelayCommand]
    private async Task ConfigureResourcePackAsync()
    {
        if (SelectedServer is null)
            return;
        var configuration = await client.SendAsync<ResourcePackConfiguration>(
            "ConfigureResourcePack",
            new ResourcePackConfigureRequest(
                SelectedServer.Definition.Id,
                new ResourcePackConfiguration
                {
                    ServerId = SelectedServer.Definition.Id,
                    Url = ResourcePackUrl.Trim(),
                    Sha1 = ResourcePackSha1.Trim(),
                    Required = ResourcePackRequired,
                    Prompt = ResourcePackPrompt
                })).ConfigureAwait(true);
        StatusMessage =
            "Resource-pack settings were written atomically. Restart the server to apply them.";
        SetResourcePackFields(configuration);
    }

    [RelayCommand]
    private async Task EnableAutomationTemplateAsync()
    {
        if (SelectedAutomationTemplate is null)
            return;
        var triggerValue = SelectedAutomationTemplate.Trigger switch
        {
            AutomationTriggerKind.ScheduledTime => "04:00",
            AutomationTriggerKind.LowDiskSpace => "10",
            _ => SelectedAutomationTemplate.TriggerValue
        };
        var recipe = SelectedAutomationTemplate with
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            TriggerValue = triggerValue
        };
        var result = await client.SendAsync<OperationResult>("UpsertAutomationRecipe", recipe)
            .ConfigureAwait(true);
        StatusMessage = result.Message;
        await LoadAutomationAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAutomationRecipeAsync()
    {
        if (SelectedAutomationRecipe is null)
            return;
        var result = await client.SendAsync<OperationResult>(
            "DeleteAutomationRecipe",
            new AutomationRecipeIdRequest(SelectedAutomationRecipe.Id)).ConfigureAwait(true);
        StatusMessage = result.Message;
        await LoadAutomationAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ExportShareGuide(string? format)
    {
        if (SelectedServer is null)
            return;
        var markdown = format?.Equals("markdown", StringComparison.OrdinalIgnoreCase) == true;
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChunkPilot", "Shares");
        Directory.CreateDirectory(directory);
        var safeName = string.Concat(SelectedServer.Definition.Name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));
        var path = Path.Combine(directory, $"{safeName}-joining-guide.{(markdown ? "md" : "txt")}");
        var content = markdown
            ? $"# {SelectedServer.Definition.Name}\n\n```\n{ShareInstructions}\n```\n"
            : ShareInstructions;
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
        StatusMessage = $"Joining guide exported: {path}";
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private async Task RunLocalConnectionTestAsync() => await RunConnectionTestCoreAsync(false).ConfigureAwait(true);

    [RelayCommand]
    private async Task RunExternalConnectionTestAsync() => await RunConnectionTestCoreAsync(true).ConfigureAwait(true);

    private async Task RunConnectionTestCoreAsync(bool includeExternalProbe)
    {
        if (SelectedServer is null)
            return;
        if (includeExternalProbe && !dialogs.Confirm("Run external reachability test",
                $"Send the configured public address and port to mcstatus.io for a one-time status probe?\n\n{ServerPublicAddress}\n\nLocal checks alone cannot prove public reachability."))
            return;
        await RunBusyAsync(includeExternalProbe ? "Running local and external connection tests…" : "Running local connection tests…", async () =>
        {
            ConnectionTest = await client.SendAsync<ConnectionTestResult>("ConnectionTest",
                new ConnectionTestRequest(SelectedServer.Definition.Id, includeExternalProbe)).ConfigureAwait(true);
            StatusMessage = ConnectionTest.Interpretation;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CreateBackupAsync()
    {
        if (SelectedServer is null)
            return;
        await RunBusyAsync("Saving and creating a verified backup…", async () =>
        {
            var backup = await client.SendAsync<BackupRecord>("Backup", new BackupRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
            StatusMessage = $"Backup created and verified: {backup.ArchivePath}";
            await LoadBackupsAsync().ConfigureAwait(true);
        }, failureAction: "Backup failed").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task VerifyBackupAsync()
    {
        if (SelectedBackup is null)
            return;
        await RunBusyAsync("Verifying every file hash in the backup…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("VerifyBackup", new BackupIdRequest(SelectedBackup.Id)).ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadBackupsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (SelectedServer is null || SelectedBackup is null)
            return;
        if (SelectedServer.State != ServerState.Stopped)
        {
            dialogs.ShowError("Server must be stopped", "Stop the server before restoring a backup.");
            return;
        }
        if (!dialogs.Confirm("Restore backup",
                $"Restore {SelectedBackup.ArchivePath} into:\n{SelectedServer.Definition.RootPath}\n\nChunkPilot will create a pre-restore safety backup first. Files present in the archive will be replaced."))
            return;
        await RunBusyAsync("Creating safety backup and restoring…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("Restore",
                new RestoreRequest(SelectedServer.Definition.Id, SelectedBackup.Id)).ConfigureAwait(true);
            StatusMessage = result.Message;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteBackupAsync()
    {
        if (SelectedBackup is null ||
            !dialogs.Confirm("Delete backup", $"Permanently delete this ChunkPilot backup archive?\n\n{SelectedBackup.ArchivePath}"))
            return;
        await RunBusyAsync("Deleting selected backup…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("DeleteBackup", new BackupIdRequest(SelectedBackup.Id)).ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadBackupsAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AddScheduleAsync()
    {
        if (SelectedServer is null)
            return;
        if (!TryBuildSchedule(out var schedule, out var validationError))
        {
            StatusMessage = validationError;
            dialogs.ShowError("Schedule is not valid", validationError);
            return;
        }
        await RunBusyAsync("Saving agent schedule…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("UpsertSchedule", schedule).ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadSchedulesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteScheduleAsync(ScheduleEntry? schedule)
    {
        if (schedule is null)
            return;
        await RunBusyAsync("Deleting schedule…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("DeleteSchedule", new ScheduleIdRequest(schedule.Id)).ConfigureAwait(true);
            await LoadSchedulesAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallJarAsync()
    {
        if (SelectedServer is null)
            return;
        var source = dialogs.SelectFile("Select a local mod or plugin jar", "Java archives (*.jar)|*.jar");
        if (source is null)
            return;
        if (!dialogs.Confirm("Install local jar",
                $"Install this file into the detected {SelectedServer.Definition.Ecosystem} environment?\n\n{source}\n\nStop the server first. Replacing an existing file creates a recovery copy."))
            return;
        await RunBusyAsync("Inspecting and installing local jar…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("InstallJar",
                new JarInstallRequest(SelectedServer.Definition.Id, source)).ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadInventoryAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ToggleJarAsync()
    {
        if (SelectedServer is null || SelectedInventoryItem is null)
            return;
        var enabling = !SelectedInventoryItem.Enabled;
        if (!dialogs.Confirm(enabling ? "Enable jar" : "Disable jar",
                $"{(enabling ? "Enable" : "Disable")} {SelectedInventoryItem.FileName}?\n\nWorlds can depend on installed mods. ChunkPilot will not remove dependencies automatically."))
            return;
        await RunBusyAsync("Updating jar state…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("SetJarEnabled",
                new JarEnabledRequest(SelectedServer.Definition.Id, SelectedInventoryItem.RelativePath, enabling)).ConfigureAwait(true);
            await LoadInventoryAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallServerIconAsync()
    {
        if (SelectedServer is null)
            return;
        var source = dialogs.SelectFile("Choose a server icon",
            "Images (*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif;*.tif;*.tiff");
        if (source is null)
            return;
        ServerIconCropSelection? crop;
        try
        {
            crop = dialogs.CropServerIcon(source);
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
        {
            dialogs.ShowError("Image could not be opened", exception.Message);
            return;
        }
        if (crop is null)
            return;
        await RunBusyAsync("Converting and installing server icon…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("InstallServerIcon",
                new IconInstallRequest(SelectedServer.Definition.Id, crop.SourcePath,
                    crop.CropX, crop.CropY, crop.CropSize)).ConfigureAwait(true);
            StatusMessage = result.Message;
            RefreshServerIconImage(SelectedServer?.Definition, refreshServerLists: true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task UseSavedServerIconAsync()
    {
        if (SelectedServer is null)
            return;
        var entries = await client.SendAsync<IReadOnlyList<ServerIconLibraryEntry>>("ListServerIcons").ConfigureAwait(true);
        if (entries.Count == 0)
        {
            dialogs.ShowInformation("No saved icons yet", "Crop an image once and it will appear in this reusable library.");
            return;
        }
        var source = dialogs.SelectSavedServerIcon(entries);
        if (source is null)
            return;
        await RunBusyAsync("Installing saved server icon…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("InstallServerIcon",
                new IconInstallRequest(SelectedServer.Definition.Id, source, SaveToLibrary: false)).ConfigureAwait(true);
            StatusMessage = result.Message;
            RefreshServerIconImage(SelectedServer?.Definition, refreshServerLists: true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportWorldAsync()
    {
        if (SelectedServer is null)
            return;
        if (SelectedServer.State != ServerState.Stopped)
        {
            dialogs.ShowError("Stop server first", "World import and switching require a stopped server.");
            return;
        }
        var zip = dialogs.SelectFile("Select a world ZIP", "ZIP archives (*.zip)|*.zip");
        if (zip is null)
            return;
        var suggested = Path.GetFileNameWithoutExtension(zip);
        if (!dialogs.Confirm("Import world",
                $"Import this ZIP as world “{suggested}”?\n\n{zip}\n\nNo existing world will be replaced or deleted."))
            return;
        await RunBusyAsync("Validating and importing world ZIP…", async () =>
        {
            _ = await client.SendAsync<WorldEntry>("ImportWorld",
                new WorldImportRequest(SelectedServer.Definition.Id, zip, suggested)).ConfigureAwait(true);
            await LoadWorldsAsync().ConfigureAwait(true);
            StatusMessage = "World imported without changing the active world.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SwitchWorldAsync()
    {
        if (SelectedServer is null || SelectedWorld is null)
            return;
        if (!dialogs.Confirm("Switch active world",
                $"Change level-name to “{SelectedWorld.Name}”?\n\nCurrent and selected world folders remain in place. The server must stay stopped until the next start."))
            return;
        await RunBusyAsync("Switching active world safely…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("SwitchWorld",
                new WorldRequest(SelectedServer.Definition.Id, SelectedWorld.Name)).ConfigureAwait(true);
            await LoadWorldsAsync().ConfigureAwait(true);
            StatusMessage = result.Message;
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportWorldAsync()
    {
        if (SelectedServer is null || SelectedWorld is null)
            return;
        var destination = dialogs.SelectFolder("Choose a destination for the world ZIP");
        if (destination is null)
            return;
        await RunBusyAsync("Saving and exporting world with a manifest…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("ExportWorld",
                new WorldExportRequest(SelectedServer.Definition.Id, SelectedWorld.Name, destination)).ConfigureAwait(true);
            StatusMessage = result.Path ?? result.Message;
            if (!string.IsNullOrWhiteSpace(result.Path) && File.Exists(result.Path))
            {
                ClipboardFileDropService.Copy(result.Path);
                dialogs.ShowInformation("World export complete",
                    $"{result.Path}\n\nThe ZIP is on the Windows clipboard as a file, ready to paste.");
            }
            else
            {
                dialogs.ShowInformation("World export complete", result.Message);
            }
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync() => await LoadDiagnosticsAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task CreateDiagnosticBundleAsync()
    {
        if (SelectedServer is null)
            return;
        await RunBusyAsync("Creating redacted diagnostic bundle…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("DiagnosticBundle",
                new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
            dialogs.ShowInformation("Diagnostic bundle created", result.Path ?? result.Message);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveStartupProfileAsync()
    {
        if (SelectedServer is null)
            return;
        var updated = SelectedServer.Definition with
        {
            Executable = StartupExecutable.Trim(),
            Arguments = ServerLaunchPolicy.EnsureNoGui(
                StartupArguments,
                SelectedServer.Definition.Ecosystem,
                RunInBackground),
            WorkingDirectory = StartupWorkingDirectory.Trim(),
            ReadinessPattern = StartupReadinessPattern.Trim(),
            StartupTimeoutSeconds = Math.Clamp(StartupTimeoutSeconds, 5, 900),
            ShutdownTimeoutSeconds = Math.Clamp(ShutdownTimeoutSeconds, 5, 900),
            RunInBackground = RunInBackground,
            MinimumRamMb = MinimumRamMb,
            MaximumRamMb = MaximumRamMb,
            UserConfiguredHostname = UserConfiguredHostname.Trim()
        };
        await RunBusyAsync("Saving launch profile…", async () =>
        {
            _ = await client.SendAsync<OperationResult>("Import", updated).ConfigureAwait(true);
            var ramResult = await client.SendAsync<OperationResult>("UpdateRam",
                new RamUpdateRequest(updated.Id, MinimumRamMb, MaximumRamMb)).ConfigureAwait(true);
            StatusMessage = $"Launch profile saved. {ramResult.Message}";
            await RefreshAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RunSelfTestAsync()
    {
        await RunBusyAsync("Running real self-tests…", async () =>
        {
            Replace(SelfTests, await client.SendAsync<IReadOnlyList<SelfTestItem>>("SelfTest").ConfigureAwait(true));
            StatusMessage = "Self-test completed.";
        }).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        await RunBusyAsync("Saving application settings…", async () =>
        {
            await client.SendAsync<OperationResult>("SetSetting", new SettingsValueRequest("minimizeToTray", MinimizeToTray.ToString())).ConfigureAwait(true);
            await client.SendAsync<OperationResult>("SetSetting", new SettingsValueRequest("startMinimized", StartMinimized.ToString())).ConfigureAwait(true);
            await client.SendAsync<OperationResult>("SetSetting", new SettingsValueRequest("defaultBackupDirectory", DefaultBackupDirectory)).ConfigureAwait(true);
            await client.SendAsync<OperationResult>("SetSetting", new SettingsValueRequest("reducedMotion", ReducedMotion.ToString())).ConfigureAwait(true);
            SetStartupRegistration(StartWithWindows);
            StatusMessage = "Application settings saved. Default close behavior remains safe stop all, then exit.";
        }).ConfigureAwait(true);
    }

    private bool TryBuildSchedule(out ScheduleEntry schedule, out string error)
    {
        schedule = new ScheduleEntry();
        error = "";
        if (SelectedServer is null)
        {
            error = "Select a server first.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(ScheduleName))
        {
            error = "Enter a task name.";
            return false;
        }

        var timeOfDay = TimeSpan.Zero;
        DateTimeOffset? oneTimeAt = null;
        if (ScheduleKind == ScheduleKind.OneTime)
        {
            if (!DateTimeOffset.TryParse(ScheduleAt, CultureInfo.CurrentCulture,
                    DateTimeStyles.AssumeLocal, out var parsed))
            {
                error = "For a one-time schedule, enter a local date and time such as 7/25/2026 4:00 AM.";
                return false;
            }
            oneTimeAt = parsed;
        }
        else if (ScheduleKind is ScheduleKind.Daily or ScheduleKind.Weekly or ScheduleKind.Monthly)
        {
            if (!TimeSpan.TryParse(ScheduleAt, CultureInfo.CurrentCulture, out timeOfDay) ||
                timeOfDay < TimeSpan.Zero || timeOfDay >= TimeSpan.FromDays(1))
            {
                error = "For daily, weekly, or monthly schedules, enter a local 24-hour time such as 04:00.";
                return false;
            }
        }

        schedule = new ScheduleEntry
        {
            ServerId = SelectedServer.Definition.Id,
            Name = ScheduleName.Trim(),
            Action = ScheduleAction,
            Kind = ScheduleKind,
            OneTimeAt = oneTimeAt,
            IntervalMinutes = Math.Clamp(ScheduleIntervalMinutes, 1, 525_600),
            TimeOfDay = timeOfDay,
            DayOfWeek = ScheduleDayOfWeek,
            DayOfMonth = Math.Clamp(ScheduleDayOfMonth, 1, 31),
            CronExpression = ScheduleCron.Trim(),
            Command = ScheduleCommand.Trim(),
            RestartCountdownSeconds = Math.Clamp(RestartCountdownSeconds, 0, 3_600),
            BackupBeforeRestart = BackupBeforeRestart,
            RetryLimit = 1,
            Enabled = true
        };

        if (ScheduleKind == ScheduleKind.Cron &&
            ScheduleCalculator.NextRun(schedule, DateTimeOffset.Now) is null)
        {
            error = "Enter a valid five-field cron expression. Supported values are *, */step, exact values, and comma-separated values.";
            return false;
        }
        return true;
    }

    internal bool TryBuildWebUiSchedule(out ScheduleEntry schedule, out string error) =>
        TryBuildSchedule(out schedule, out error);

    internal async Task<OperationResult> SaveWebUiScheduleAsync(ScheduleEntry schedule)
    {
        if (SelectedServer?.Definition.Id != schedule.ServerId)
            throw new InvalidOperationException("The schedule does not belong to the active server.");
        var result = await client.SendAsync<OperationResult>("UpsertSchedule", schedule).ConfigureAwait(true);
        StatusMessage = result.Message;
        await LoadSchedulesAsync().ConfigureAwait(true);
        return result;
    }

    private async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var minimize = await client.SendAsync<TextResponse>("GetSetting",
            new SettingsValueRequest("minimizeToTray", ""), cancellationToken).ConfigureAwait(true);
        var minimized = await client.SendAsync<TextResponse>("GetSetting",
            new SettingsValueRequest("startMinimized", ""), cancellationToken).ConfigureAwait(true);
        var backupDirectory = await client.SendAsync<TextResponse>("GetSetting",
            new SettingsValueRequest("defaultBackupDirectory", ""), cancellationToken).ConfigureAwait(true);
        var reduced = await client.SendAsync<TextResponse>("GetSetting",
            new SettingsValueRequest("reducedMotion", ""), cancellationToken).ConfigureAwait(true);
        var lastServer = await client.SendAsync<TextResponse>("GetSetting",
            new SettingsValueRequest("lastSelectedServerId", ""), cancellationToken).ConfigureAwait(true);
        var lastDestination = await client.SendAsync<TextResponse>("GetSetting",
            new SettingsValueRequest("lastServerDestination", ""), cancellationToken).ConfigureAwait(true);

        if (bool.TryParse(minimize.Value, out var minimizeValue))
            MinimizeToTray = minimizeValue;
        if (bool.TryParse(minimized.Value, out var minimizedValue))
            StartMinimized = minimizedValue;
        DefaultBackupDirectory = backupDirectory.Value;
        if (bool.TryParse(reduced.Value, out var reducedValue))
            ReducedMotion = reducedValue;
        if (Guid.TryParse(lastServer.Value, out var selectedId))
        {
            startupRestoreServerId = selectedId;
            // Restore the remembered destination for the server, without bumping version
            if (!string.IsNullOrEmpty(lastDestination.Value) && ServerDestination.All.Contains(lastDestination.Value))
                Navigation.RememberDestination(selectedId, lastDestination.Value);
        }
        using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        StartWithWindows = runKey?.GetValue("ChunkPilot") is string;
    }

    private static void SetStartupRegistration(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            runKey.SetValue("ChunkPilot", $"\"{Environment.ProcessPath}\"");
        else
            runKey.DeleteValue("ChunkPilot", false);
    }

    private async Task LifecycleAsync(string operation, ServerSnapshot? server, string message, object? payload = null)
    {
        server ??= SelectedServer;
        if (server is null)
            return;
        SelectedServer = server;
        var optimisticState = operation switch
        {
            "Start" => ServerState.Starting,
            "Stop" => ServerState.Stopping,
            "Restart" => ServerState.Restarting,
            _ => (ServerState?)null
        };
        if (optimisticState is { } state)
        {
            if (state is ServerState.Starting or ServerState.Restarting)
            {
                memoryRestartPending = false;
                serverPropertiesRestartPending = false;
            }
            ApplyOptimisticServerState(server.Definition.Id, state);
            NotifyMemoryState();
        }
        await RunBusyAsync(message, async () =>
        {
            try
            {
                var request = payload ?? operation switch
                {
                    "Start" => ConnectivityRequest(server.Definition.Id,
                        PublicConnectivityOperation.StartServer),
                    "Restart" => ConnectivityRequest(server.Definition.Id,
                        PublicConnectivityOperation.RestartServer),
                    _ => new ServerIdRequest(server.Definition.Id)
                };
                var result = await client.SendAsync<OperationResult>(operation, request).ConfigureAwait(true);
                StatusMessage = result.Message;
                if (!result.Success && result.RequiresForceConfirmation &&
                    dialogs.Confirm("Server did not stop cleanly",
                        $"{result.Message}\n\nForce terminate this server process tree? This can risk world corruption. Choose No to keep ChunkPilot open."))
                {
                    result = await client.SendAsync<OperationResult>("ForceTerminate",
                        ConnectivityRequest(server.Definition.Id,
                            PublicConnectivityOperation.ForceTerminateServer)).ConfigureAwait(true);
                    StatusMessage = result.Message;
                }
            }
            finally
            {
                // An optimistic transition must never stick if the pipe call fails. The Agent remains
                // authoritative and this read restores whatever state actually survived the request.
                await RefreshAsync().ConfigureAwait(true);
                // Starting and stopping a server change whether its router mapping exists, and the
                // Agent has already acted on that by now. Without this read the card kept reporting
                // the port it opened at start as still open long after Stop had closed it.
                if (ShouldPollRouterMapping)
                {
                    await LoadRouterMappingAsync().ConfigureAwait(true);
                    await LoadExternalReachabilityAsync().ConfigureAwait(true);
                }
            }
        }).ConfigureAwait(true);
    }

    private void ApplyOptimisticServerState(Guid serverId, ServerState state)
    {
        var current = Servers.FirstOrDefault(item => item.Definition.Id == serverId);
        if (current is null)
            return;
        var updated = current with { State = state };
        var index = Servers.IndexOf(current);
        if (index >= 0)
            Servers[index] = updated;
        if (SelectedServer?.Definition.Id == serverId)
            SelectedServer = updated;
    }

    private async Task LoadServerDetailsAsync()
    {
        if (SelectedServer is null || loadingDetails)
            return;
        loadingDetails = true;
        try
        {
            SelectedCapabilities = await client.SendAsync<ServerCapabilityProfile>(
                "GetCapabilities", new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
            var network = await client.SendAsync<NetworkConfiguration>(
                "GetNetworkConfiguration", new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
            SelectedNetworkMode = network.Mode;
            ShowsDirectInternetConsent = false;
            await Task.WhenAll(LoadBackupsAsync(), LoadSchedulesAsync(), LoadFilesCoreAsync(""),
                LoadInventoryAsync(), LoadDiagnosticsAsync(), LoadPropertiesAsync(), LoadWorldsAsync(),
                LoadPlayerAccessAsync(), LoadGamerulesAsync(), LoadUpdateDetailsAsync(), LoadAutomationAsync(),
                LoadCrossplayAsync(), LoadDatapacksAsync(), LoadResourcePackAsync(), LoadRouterMappingAsync(),
                LoadFirewallAccessAsync(), LoadExternalReachabilityAsync())
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Some server details are unavailable: {exception.Message}";
        }
        finally
        {
            loadingDetails = false;
        }
    }

    private async Task LoadBackupsAsync()
    {
        if (SelectedServer is null)
            return;
        Replace(Backups, await client.SendAsync<IReadOnlyList<BackupRecord>>("ListBackups",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
    }

    private async Task LoadAllBackupsAsync() =>
        Replace(Backups, await client.SendAsync<IReadOnlyList<BackupRecord>>("ListAllBackups").ConfigureAwait(true));

    private async Task LoadSchedulesAsync() =>
        Replace(Schedules, await client.SendAsync<IReadOnlyList<ScheduleEntry>>("ListSchedules").ConfigureAwait(true));

    private async Task LoadFilesCoreAsync(string relativePath)
    {
        if (SelectedServer is null)
            return;
        var entries = await client.SendAsync<IReadOnlyList<FileSystemEntry>>("ListFiles",
            new FilesRequest(SelectedServer.Definition.Id, relativePath)).ConfigureAwait(true);
        // Replacing the collection clears the list's selection as a side effect, which would otherwise
        // be read as the user deselecting a file and would wipe an editor they are working in.
        suppressFileSelection = true;
        try
        {
            CurrentFolder = relativePath;
            Replace(FileEntries, entries);
        }
        finally
        {
            suppressFileSelection = false;
        }
    }

    private async Task LoadInventoryAsync()
    {
        if (SelectedServer is null)
            return;
        Replace(Inventory, await client.SendAsync<IReadOnlyList<ModPluginEntry>>("Inventory",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
    }

    internal Task LoadWebUiInventoryAsync() => LoadInventoryAsync();

    private async Task LoadDiagnosticsAsync()
    {
        if (SelectedServer is null)
            return;
        Replace(Diagnostics, await client.SendAsync<IReadOnlyList<DiagnosticFinding>>("Diagnostics",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
    }

    private async Task LoadWorldsAsync()
    {
        if (SelectedServer is null)
            return;
        Replace(Worlds, await client.SendAsync<IReadOnlyList<WorldEntry>>("ListWorlds",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
        OnPropertyChanged(nameof(HasWorlds));
    }

    private async Task LoadAutomationAsync()
    {
        if (SelectedServer is null)
            return;
        var all = await client.SendAsync<IReadOnlyList<AutomationRecipe>>("ListAutomationRecipes")
            .ConfigureAwait(true);
        Replace(AutomationRecipes, all.Where(recipe => recipe.ServerId == SelectedServer.Definition.Id));
        Replace(AutomationTemplates,
            await client.SendAsync<IReadOnlyList<AutomationRecipe>>(
                "AutomationRecipeTemplates",
                new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
    }

    private async Task LoadCrossplayAsync()
    {
        if (SelectedServer is null)
            return;
        SelectedCrossplayConfiguration =
            await client.SendAsync<CrossplayConfiguration>(
                "GetCrossplayConfiguration",
                new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true);
        CrossplayBedrockPort = SelectedCrossplayConfiguration.BedrockPort;
        InstallFloodgate = SelectedCrossplayConfiguration.FloodgateEnabled ||
                           !SelectedCrossplayConfiguration.GeyserEnabled;
        InstallViaVersion = SelectedCrossplayConfiguration.ViaVersionEnabled;
    }

    private string CrossplayVersion(CrossplayPackageKind kind) =>
        SelectedCrossplayConfiguration.InstalledVersions.TryGetValue(
            kind.ToString(), out var version)
            ? version
            : "installed";

    private async Task LoadDatapacksAsync()
    {
        if (SelectedServer is null)
            return;
        Replace(Datapacks, await client.SendAsync<IReadOnlyList<DatapackInventoryItem>>(
            "ListDatapacks",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
    }

    private async Task LoadResourcePackAsync()
    {
        if (SelectedServer is null)
            return;
        SetResourcePackFields(await client.SendAsync<ResourcePackConfiguration>(
            "GetResourcePackConfiguration",
            new ServerIdRequest(SelectedServer.Definition.Id)).ConfigureAwait(true));
    }

    private void SetResourcePackFields(ResourcePackConfiguration configuration)
    {
        ResourcePackUrl = configuration.Url;
        ResourcePackSha1 = configuration.Sha1;
        ResourcePackRequired = configuration.Required;
        ResourcePackPrompt = configuration.Prompt;
    }

    /// <summary>
    /// Runs one operation with busy state, and reports a failure in the shell rather than in a dialog.
    /// </summary>
    /// <remarks>
    /// The default Windows message box this used to raise was a white box in a dark application, it
    /// blocked the whole window, and it showed a raw exception message with no indication of which
    /// server it belonged to. The themed notice carries the same detail, names the server, and leaves
    /// the page readable while it is on screen.
    /// </remarks>
    private async Task RunBusyAsync(string message, Func<Task> action, string? failureAction = null)
    {
        if (IsBusy)
            return;
        IsBusy = true;
        StatusMessage = message;
        DismissOperationNotice();
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            StatusMessage = exception.Message;
            ShowOperationFailure(failureAction ?? "Operation failed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Value(ServerPropertiesResponse response, string key, string fallback) =>
        response.Values.TryGetValue(key, out var value) ? value : fallback;

    private static bool BoolValue(ServerPropertiesResponse response, string key, bool fallback) =>
        response.Values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static int IntValue(ServerPropertiesResponse response, string key, int fallback) =>
        response.Values.TryGetValue(key, out var value) && int.TryParse(value, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : fallback;

    /// <summary>
    /// Reported by the console view whenever its scroll position changes.
    /// </summary>
    /// <remarks>
    /// Only the view knows whether the last line is on screen, and follow is decided from that alone:
    /// at the bottom means follow, scrolled away means paused. Nothing else pauses it, so new output
    /// can never pull the viewport off text somebody is reading.
    /// </remarks>
    public void SetConsoleViewport(bool isAtBottom)
    {
        consoleFollow.OnViewportChanged(isAtBottom);
        IsConsoleFollowing = consoleFollow.IsFollowing;
        UnseenConsoleLines = consoleFollow.UnseenLineCount;
    }

    private void SyncConsole(IReadOnlyList<ConsoleLine> snapshot)
    {
        var last = ConsoleLines.LastOrDefault()?.Sequence ?? consoleClearedThroughSequence;
        var additions = snapshot.Where(line => line.Sequence > last && line.Sequence > consoleClearedThroughSequence).ToArray();
        if (additions.Length == 0)
            return;
        foreach (var line in additions)
            ConsoleLines.Add(line);
        while (ConsoleLines.Count > 5_000)
            ConsoleLines.RemoveAt(0);
        var shouldScroll = consoleFollow.OnLinesAdded(additions.Length);
        IsConsoleFollowing = consoleFollow.IsFollowing;
        UnseenConsoleLines = consoleFollow.UnseenLineCount;
        OnPropertyChanged(nameof(FilteredConsoleLines));
        if (shouldScroll)
            ConsoleScrollRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool MatchesConsoleFilter(ConsoleLine line)
    {
        if (!string.IsNullOrWhiteSpace(ConsoleSearchText) &&
            !line.Text.Contains(ConsoleSearchText, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ConsoleSeverity == "All")
            return true;
        var severity = ClassifyConsoleLine(line);
        return severity.Equals(ConsoleSeverity, StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyConsoleLine(ConsoleLine line)
    {
        if (line.Text.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
            line.Stream.Equals("stderr", StringComparison.OrdinalIgnoreCase))
            return "Error";
        if (line.Text.Contains("[WARN]", StringComparison.OrdinalIgnoreCase) ||
            line.Text.Contains("warning", StringComparison.OrdinalIgnoreCase))
            return "Warning";
        if (line.Text.Contains('<') && line.Text.Contains('>'))
            return "Chat";
        return "Info";
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
            collection.Add(value);
    }

    private bool CanStartServer(ServerSnapshot? server) =>
        !IsBusy && (server ?? SelectedServer)?.State is ServerState.Stopped or ServerState.Crashed;

    private bool CanSaveServer(ServerSnapshot? server) =>
        !IsBusy && (server ?? SelectedServer)?.State == ServerState.Running;

    private bool CanStopServer(ServerSnapshot? server) =>
        !IsBusy && (server ?? SelectedServer)?.State is ServerState.Starting or ServerState.Running or
            ServerState.Saving or ServerState.Restarting or ServerState.Unresponsive;

    private bool CanRestartServer(ServerSnapshot? server) =>
        !IsBusy && (server ?? SelectedServer)?.State == ServerState.Running;

    partial void OnIsBusyChanged(bool value)
    {
        StartServerCommand.NotifyCanExecuteChanged();
        SaveServerCommand.NotifyCanExecuteChanged();
        StopServerCommand.NotifyCanExecuteChanged();
        RestartServerCommand.NotifyCanExecuteChanged();
        RenameServerCommand.NotifyCanExecuteChanged();
        NotifyMemoryState();
        SaveServerPropertiesAndRestartCommand.NotifyCanExecuteChanged();
        GrantOperatorToSelectedCommand.NotifyCanExecuteChanged();
        RemoveOperatorFromSelectedCommand.NotifyCanExecuteChanged();
        WhitelistSelectedCommand.NotifyCanExecuteChanged();
        RemoveWhitelistFromSelectedCommand.NotifyCanExecuteChanged();
        KickSelectedCommand.NotifyCanExecuteChanged();
        BanSelectedCommand.NotifyCanExecuteChanged();
    }
}
