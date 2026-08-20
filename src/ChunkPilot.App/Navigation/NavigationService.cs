using System.Collections.ObjectModel;
using ChunkPilot.App.DesignSystem;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ChunkPilot.App.Navigation;

/// <summary>
/// Semantic navigation destinations for the global shell.
/// Never use numeric indexes; these identifiers are persisted in settings.
/// </summary>
public static class GlobalDestination
{
    public const string Dashboard = "Dashboard";
    public const string Servers = "Servers";
    public const string Automation = "Automation";
    public const string Activity = "Activity";
    public const string Settings = "Settings";
}

/// <summary>
/// Semantic navigation destinations for the selected-server workspace.
/// Maximum six primary destinations as required by the shell.
/// </summary>
public static class ServerDestination
{
    public const string Overview = "Overview";
    public const string Console = "Console";
    public const string Manage = "Manage";
    public const string Access = "Access";
    public const string Protection = "Protection";
    public const string Settings = "Settings";

    /// <summary>All possible destinations in display order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Overview, Console, Manage, Access, Protection, Settings];
}

/// <summary>
/// Centralized navigation state. Solves the reversion bug by tracking the user's navigation
/// intent with a monotonically-increasing version counter. Stale async updates (refreshes,
/// reconnects) cannot overwrite a newer user choice because they cannot advance the counter.
/// </summary>
/// <remarks>
/// <para>The root cause of the old bug was:</para>
/// <list type="number">
///   <item>RefreshAsync fires every 1s, replaces the Servers collection, and reassigns SelectedServer.</item>
///   <item>OnSelectedServerChanged was bidirectionally coupled with SelectedServerTabIndex and SelectedServerDestination.</item>
///   <item>WPF TabControl SelectedValue binding triggered cascading property changes during a collection refresh.</item>
///   <item>LoadSettingsAsync restored an old tab index that raced with user navigation.</item>
/// </list>
/// <para>Fix: navigation state is now a single authoritative property per level (global + server),
/// guarded by a version stamp. Only user-initiated navigation increments the version. Refresh logic
/// checks the version before touching navigation state.</para>
/// </remarks>
public sealed partial class NavigationService : ObservableObject
{
    private long _globalVersion;
    private long _serverVersion;

    // Per-server last-used destination memory (server ID -> destination)
    private readonly Dictionary<Guid, string> _serverDestinationMemory = new();

    public NavigationService()
    {
        GlobalItems =
        [
            new NavigationItem(GlobalDestination.Dashboard, "Dashboard", "Host resources and managed servers", AppIconKind.Home),
            new NavigationItem(GlobalDestination.Servers, "Servers", "All managed and imported servers", AppIconKind.Server),
            new NavigationItem(GlobalDestination.Automation, "Automation", "Schedules, recipes, and update center", AppIconKind.Calendar),
            new NavigationItem(GlobalDestination.Activity, "Activity", "Audited operations and history", AppIconKind.History),
            new NavigationItem(GlobalDestination.Settings, "Settings", "Application preferences", AppIconKind.Settings)
        ];

        ServerItems =
        [
            new NavigationItem(ServerDestination.Overview, "Overview", "Server status, controls, and metrics", AppIconKind.Home),
            new NavigationItem(ServerDestination.Console, "Console", "Live server console with commands", AppIconKind.Terminal),
            new NavigationItem(ServerDestination.Manage, "Manage", "Files, configuration, versions, and add-ons", AppIconKind.Box),
            new NavigationItem(ServerDestination.Access, "Access", "Players, sharing, crossplay, and networking", AppIconKind.People),
            new NavigationItem(ServerDestination.Protection, "Protection", "Backups, worlds, diagnostics, and recovery", AppIconKind.Shield),
            new NavigationItem(ServerDestination.Settings, "Settings", "Launch profile and server-specific preferences", AppIconKind.Settings)
        ];
    }

    public ObservableCollection<NavigationItem> GlobalItems { get; }
    public ObservableCollection<NavigationItem> ServerItems { get; }

    /// <summary>The current global page. Null when a server is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGlobalItem))]
    private string _currentGlobalPage = GlobalDestination.Dashboard;

    /// <summary>The current server-level destination.</summary>
    [ObservableProperty]
    private string _currentServerDestination = ServerDestination.Overview;

    /// <summary>Whether a server is currently open (server workspace is visible).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedGlobalItem))]
    private bool _isServerWorkspaceActive;

    /// <summary>
    /// The rail item that is current, or null while a server workspace is open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The single answer to "what is selected in the global rail", derived from navigation state
    /// rather than reconstructed by whoever happens to notice a change. The shell used to recompute
    /// it only when <see cref="IsServerWorkspaceActive"/> changed, so a Dashboard deep link to
    /// Servers - which moves between two global destinations and never touches that flag - opened the
    /// right page and left Dashboard highlighted. Deriving it from both properties means every route
    /// into a destination selects it, whether it came from the rail, a dashboard action, the keyboard
    /// or a restored session.
    /// </para>
    /// </remarks>
    public NavigationItem? SelectedGlobalItem => IsServerWorkspaceActive
        ? null
        : GlobalItems.FirstOrDefault(item => item.Page == CurrentGlobalPage);

    /// <summary>The version counter for the current global navigation state.</summary>
    public long GlobalVersion => _globalVersion;

    /// <summary>The version counter for the current server navigation state.</summary>
    public long ServerVersion => _serverVersion;

    /// <summary>
    /// User-initiated global navigation. Increments version to reject stale async updates.
    /// </summary>
    public void NavigateGlobal(string destination)
    {
        Interlocked.Increment(ref _globalVersion);
        IsServerWorkspaceActive = false;
        CurrentGlobalPage = destination;
    }

    /// <summary>
    /// User-initiated server destination navigation. Increments version to reject stale updates.
    /// </summary>
    public void NavigateServer(string destination, Guid? serverId = null)
    {
        if (!ServerDestination.All.Contains(destination))
            destination = ServerDestination.Overview;

        Interlocked.Increment(ref _serverVersion);
        IsServerWorkspaceActive = true;
        CurrentServerDestination = destination;

        if (serverId.HasValue)
            _serverDestinationMemory[serverId.Value] = destination;
    }

    /// <summary>
    /// Raised whenever a (possibly new) server workspace is entered via <see cref="OpenServer"/> -
    /// i.e. the user opened a server that was not already the open one. Unlike the
    /// <see cref="CurrentServerDestination"/> property-changed notification, this fires even when the
    /// resolved destination happens to equal the value already in place (e.g. two different servers
    /// both opening on Overview), which is what the shell's lazy page host needs to know it must
    /// (re)attach content rather than assume nothing changed. Does not fire when refresh reaffirms the
    /// server workspace that is already open.
    /// </summary>
    public event EventHandler<Guid>? ServerOpened;

    /// <summary>
    /// Open a server workspace, restoring the last-used destination for that server.
    /// </summary>
    public void OpenServer(Guid serverId)
    {
        Interlocked.Increment(ref _serverVersion);
        IsServerWorkspaceActive = true;

        if (_serverDestinationMemory.TryGetValue(serverId, out var remembered) && ServerDestination.All.Contains(remembered))
            CurrentServerDestination = remembered;
        else
            CurrentServerDestination = ServerDestination.Overview;

        ServerOpened?.Invoke(this, serverId);
    }

    /// <summary>
    /// Called by async refresh logic. Only updates navigation if no user action has occurred since
    /// the given version snapshot.
    /// </summary>
    public bool TryRestoreServerDestination(string destination, long atVersion)
    {
        if (Interlocked.Read(ref _serverVersion) != atVersion)
            return false; // User navigated since this async work started

        if (!ServerDestination.All.Contains(destination))
            destination = ServerDestination.Overview;

        CurrentServerDestination = destination;
        return true;
    }

    /// <summary>
    /// Called when a destination becomes unsupported (e.g., capability removed). Falls back safely.
    /// </summary>
    public void FallbackIfUnsupported(IReadOnlySet<string> supportedDestinations)
    {
        if (!supportedDestinations.Contains(CurrentServerDestination))
        {
            Interlocked.Increment(ref _serverVersion);
            CurrentServerDestination = ServerDestination.Overview;
        }
    }

    /// <summary>Remember the current destination for a specific server.</summary>
    public void RememberDestination(Guid serverId, string destination) =>
        _serverDestinationMemory[serverId] = destination;

    /// <summary>Get the remembered destination for a server, or Overview.</summary>
    public string GetRememberedDestination(Guid serverId) =>
        _serverDestinationMemory.TryGetValue(serverId, out var d) ? d : ServerDestination.Overview;
}
