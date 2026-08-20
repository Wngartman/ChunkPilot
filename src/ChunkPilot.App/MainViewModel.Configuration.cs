using System.Globalization;
using ChunkPilot.App.Presentation;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Manage › Configuration: the beginner-facing server.properties editor.
//
// One owner for these values. They used to appear on both Manage and Settings, with two Apply
// buttons writing the same file from two sets of controls, so whichever page the user had not been
// looking at silently decided what was saved. Settings keeps runtime concerns - Java, memory, the
// launch profile - and nothing that lives in server.properties.
//
// The Agent still owns the file. Everything here builds a value set, hands it to the atomic
// UpdateServerProperties path, and then re-reads what the file actually says.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public sealed partial class MainViewModel
{
    /// <summary>Difficulty choices, shown in title case and stored exactly as Minecraft spells them.</summary>
    public IReadOnlyList<ServerPropertyChoice> DifficultyChoices { get; } = ServerPropertyPresentation.Difficulties;

    /// <summary>Game mode choices, shown in title case and stored exactly as Minecraft spells them.</summary>
    public IReadOnlyList<ServerPropertyChoice> GameModeChoices { get; } = ServerPropertyPresentation.GameModes;

    /// <summary>The values last read from <c>server.properties</c>, used to detect a real edit.</summary>
    private Dictionary<string, string> loadedServerProperties = new(StringComparer.OrdinalIgnoreCase);
    private bool serverPropertiesRestartPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerPropertySaveError))]
    private string serverPropertySaveError = "";

    [ObservableProperty]
    private string serverPropertySavedNotice = "";

    public bool HasServerPropertySaveError => ServerPropertySaveError.Length > 0;

    /// <summary>
    /// True when the controls differ from the file that was read.
    /// </summary>
    /// <remarks>
    /// Comparing against the file rather than tracking a dirty flag means typing a value and typing
    /// it back leaves Apply disabled, which is the honest answer: there is nothing to apply.
    /// </remarks>
    public bool HasServerPropertyChanges
    {
        get
        {
            if (loadedServerProperties.Count == 0)
                return false;
            foreach (var pair in BuildServerPropertyValues())
            {
                if (!loadedServerProperties.TryGetValue(pair.Key, out var original) ||
                    !string.Equals(original, pair.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Names the pending changes a running server will not notice until it restarts.
    /// </summary>
    /// <remarks>
    /// Stated, never acted on. ChunkPilot does not restart somebody's server because a number changed.
    /// </remarks>
    public string RestartRequiredNotice
    {
        get
        {
            if (serverPropertiesRestartPending)
                return "Settings saved. Restart the server to apply them.";
            if (loadedServerProperties.Count == 0)
                return "";
            var pending = BuildServerPropertyValues()
                .Where(pair => ServerPropertyPresentation.RestartRequiredKeys.Contains(pair.Key))
                .Where(pair => loadedServerProperties.TryGetValue(pair.Key, out var original) &&
                               !string.Equals(original, pair.Value, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Key)
                .ToArray();
            return pending.Length == 0
                ? ""
                : $"Restart is required after saving: {string.Join(", ", pending)}.";
        }
    }

    public bool ShowsRestartRequiredNotice => RestartRequiredNotice.Length > 0;

    /// <summary>The exact value set that would be written, in the file's own vocabulary.</summary>
    private Dictionary<string, string> BuildServerPropertyValues() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["motd"] = PropertyMotd,
            ["server-port"] = PropertyPort.ToString(CultureInfo.InvariantCulture),
            ["max-players"] = PropertyMaxPlayers.ToString(CultureInfo.InvariantCulture),
            ["difficulty"] = PropertyDifficulty,
            ["gamemode"] = PropertyGameMode,
            ["view-distance"] = PropertyViewDistance.ToString(CultureInfo.InvariantCulture),
            ["simulation-distance"] = PropertySimulationDistance.ToString(CultureInfo.InvariantCulture),
            ["allow-flight"] = Lower(PropertyAllowFlight),
            ["pvp"] = Lower(PropertyPvp),
            ["online-mode"] = Lower(PropertyOnlineMode),
            ["white-list"] = Lower(PropertyWhiteList),
            ["enforce-whitelist"] = Lower(PropertyEnforceWhitelist),
            ["hide-online-players"] = Lower(PropertyHideOnlinePlayers),
            ["spawn-protection"] = PropertySpawnProtection.ToString(CultureInfo.InvariantCulture),
            ["enable-command-block"] = Lower(PropertyCommandBlocks),
            ["hardcore"] = Lower(PropertyHardcore),
            ["force-gamemode"] = Lower(PropertyForceGameMode),
            ["player-idle-timeout"] = PropertyPlayerIdleTimeout.ToString(CultureInfo.InvariantCulture)
        };

    private static string Lower(bool value) => value ? "true" : "false";

    private bool HasPendingRestartPropertyChanges() =>
        BuildServerPropertyValues()
            .Where(pair => ServerPropertyPresentation.RestartRequiredKeys.Contains(pair.Key))
            .Any(pair => loadedServerProperties.TryGetValue(pair.Key, out var original) &&
                         !string.Equals(original, pair.Value, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the changed values through the Agent, then re-reads the file.</summary>
    [RelayCommand(CanExecute = nameof(HasServerPropertyChanges))]
    private async Task SaveServerPropertiesAsync()
    {
        if (SelectedServer is null)
            return;
        var values = BuildServerPropertyValues();
        var errors = ServerPropertyValidation.Validate(values);
        if (errors.Count > 0)
        {
            // Inline and themed, beside the controls that are wrong - not a modal that hides them.
            ServerPropertySavedNotice = "";
            ServerPropertySaveError = string.Join(" ", errors.Select(error => $"{error.Key}: {error.Value}"));
            return;
        }

        var serverId = SelectedServer.Definition.Id;
        var needsRestart = SelectedServer.State == ServerState.Running && HasPendingRestartPropertyChanges();
        ServerPropertySaveError = "";
        ServerPropertySavedNotice = "";
        await RunBusyAsync("Updating server.properties safely…", async () =>
        {
            try
            {
                var result = await client.SendAsync<OperationResult>("UpdateServerProperties",
                    new ServerPropertiesRequest(serverId, values)
                    {
                        Session = uiSession,
                        Lease = LeaseFor(serverId),
                        ConnectivityOperation = PublicConnectivityOperation.UpdateServerProperties
                    }).ConfigureAwait(true);
                StatusMessage = result.Message;
                if (!result.Success)
                {
                    // The edits stay on screen. A failed write is not a reason to throw away what the
                    // user typed and show them the old file again.
                    ServerPropertySaveError = result.Message;
                    return;
                }
                ServerPropertySavedNotice = "Saved.";
                serverPropertiesRestartPending |= needsRestart;
                await LoadPropertiesAsync(force: true).ConfigureAwait(true);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or InvalidOperationException or ArgumentException)
            {
                ServerPropertySaveError = exception.Message;
            }
        }).ConfigureAwait(true);
        NotifyServerPropertyState();
    }

    private bool CanSaveServerPropertiesAndRestart() =>
        HasServerPropertyChanges && SelectedServer?.State == ServerState.Running && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSaveServerPropertiesAndRestart))]
    private async Task SaveServerPropertiesAndRestartAsync()
    {
        await SaveServerPropertiesAsync().ConfigureAwait(true);
        if (SelectedServer is null || HasServerPropertySaveError || HasServerPropertyChanges)
            return;
        serverPropertiesRestartPending = false;
        NotifyServerPropertyState();
        await RestartServerAsync(SelectedServer).ConfigureAwait(true);
    }

    /// <summary>
    /// Reads the authoritative values from the file through the Agent.
    /// </summary>
    /// <param name="force">
    /// True after a successful save, when the file is the truth and the controls must follow it.
    /// A background read leaves unsaved edits alone: overwriting what somebody is typing is the
    /// defect, not the refresh.
    /// </param>
    private async Task LoadPropertiesAsync(bool force = false)
    {
        var selected = SelectedServer;
        if (selected is null || !File.Exists(Path.Combine(selected.Definition.RootPath, "server.properties")))
            return;
        if (!force && HasServerPropertyChanges)
            return;
        var response = await client.SendAsync<ServerPropertiesResponse>("GetServerProperties",
            new ServerIdRequest(selected.Definition.Id)).ConfigureAwait(true);
        PropertyMotd = Value(response, "motd", "A Minecraft Server");
        PropertyPort = IntValue(response, "server-port", 25_565);
        PropertyMaxPlayers = IntValue(response, "max-players", 20);
        PropertyDifficulty = Value(response, "difficulty", "easy");
        PropertyGameMode = Value(response, "gamemode", "survival");
        PropertyOnlineMode = BoolValue(response, "online-mode", true);
        PropertyPvp = BoolValue(response, "pvp", true);
        PropertyWhiteList = BoolValue(response, "white-list", false);
        PropertyEnforceWhitelist = BoolValue(response, "enforce-whitelist", false);
        PropertyHideOnlinePlayers = BoolValue(response, "hide-online-players", false);
        PropertyViewDistance = IntValue(response, "view-distance", 10);
        PropertySimulationDistance = IntValue(response, "simulation-distance", 10);
        PropertyAllowFlight = BoolValue(response, "allow-flight", false);
        PropertyCommandBlocks = BoolValue(response, "enable-command-block", false);
        PropertySpawnProtection = IntValue(response, "spawn-protection", 16);
        PropertyHardcore = BoolValue(response, "hardcore", false);
        PropertyForceGameMode = BoolValue(response, "force-gamemode", false);
        PropertyPlayerIdleTimeout = IntValue(response, "player-idle-timeout", 0);
        PropertyRaw = response.Raw;
        loadedServerProperties = BuildServerPropertyValues();
        NotifyServerPropertyState();
    }

    /// <summary>
    /// Forgets the previous server's file, so the next read is not mistaken for an unsaved edit.
    /// </summary>
    private void ResetServerPropertyEditor()
    {
        loadedServerProperties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ServerPropertySaveError = "";
        ServerPropertySavedNotice = "";
        serverPropertiesRestartPending = false;
        NotifyServerPropertyState();
    }

    private void NotifyServerPropertyState()
    {
        OnPropertyChanged(nameof(HasServerPropertyChanges));
        OnPropertyChanged(nameof(RestartRequiredNotice));
        OnPropertyChanged(nameof(ShowsRestartRequiredNotice));
        SaveServerPropertiesCommand.NotifyCanExecuteChanged();
        SaveServerPropertiesAndRestartCommand.NotifyCanExecuteChanged();
    }

    partial void OnPropertyMotdChanged(string value) => NotifyServerPropertyState();
    partial void OnPropertyPortChanged(int value) => NotifyServerPropertyState();
    partial void OnPropertyMaxPlayersChanged(int value) => NotifyServerPropertyState();
    partial void OnPropertyDifficultyChanged(string value) => NotifyServerPropertyState();
    partial void OnPropertyGameModeChanged(string value) => NotifyServerPropertyState();
    partial void OnPropertyOnlineModeChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyPvpChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyWhiteListChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyViewDistanceChanged(int value) => NotifyServerPropertyState();
    partial void OnPropertySimulationDistanceChanged(int value) => NotifyServerPropertyState();
    partial void OnPropertyAllowFlightChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyCommandBlocksChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertySpawnProtectionChanged(int value) => NotifyServerPropertyState();
    partial void OnPropertyHardcoreChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyForceGameModeChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyEnforceWhitelistChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyHideOnlinePlayersChanged(bool value) => NotifyServerPropertyState();
    partial void OnPropertyPlayerIdleTimeoutChanged(int value) => NotifyServerPropertyState();
}
