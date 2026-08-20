using System.Collections.ObjectModel;
using System.ComponentModel;
using ChunkPilot.App.Access;
using ChunkPilot.App.Navigation;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Player and access management.
//
// One coherent list replaces the four sections the page used to have. The rows come from the Agent,
// which reads the server's own files and its own console output; the UI never decides who is online,
// whitelisted, an operator or banned.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

public sealed partial class MainViewModel
{
    /// <summary>The players, one row each, in the order the Agent returned them.</summary>
    public ObservableCollection<PlayerAccessRow> PlayerRows { get; } = [];

    /// <summary>Online-only view used by Overview for quick moderation.</summary>
    public ObservableCollection<PlayerAccessRow> OnlinePlayerRows { get; } = [];

    public bool HasOnlinePlayerRows => OnlinePlayerRows.Count > 0;

    [ObservableProperty]
    private string onlinePlayerSearchText = "";

    public IEnumerable<PlayerAccessRow> FilteredOnlinePlayerRows =>
        string.IsNullOrWhiteSpace(OnlinePlayerSearchText)
            ? OnlinePlayerRows
            : OnlinePlayerRows.Where(row => row.Name.Contains(
                OnlinePlayerSearchText.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool HasFilteredOnlinePlayerRows => FilteredOnlinePlayerRows.Any();
    public bool ShowsOnlinePlayerSearchEmpty => HasOnlinePlayerRows && !HasFilteredOnlinePlayerRows;
    public int SelectedOnlinePlayerCount => OnlinePlayerRows.Count(row => row.IsSelected);
    public bool HasSelectedOnlinePlayers => SelectedOnlinePlayerCount > 0;
    public string SelectedOnlinePlayerText => SelectedOnlinePlayerCount == 1
        ? "1 player selected"
        : $"{SelectedOnlinePlayerCount} players selected";
    public double OnlinePlayerHeadSize => OnlinePlayerRows.Count switch { > 40 => 24, > 20 => 28, > 10 => 32, _ => 38 };
    public double OnlinePlayerNameFontSize => OnlinePlayerRows.Count switch { > 40 => 12, > 20 => 13, _ => 14 };
    public double OnlinePlayerRowHeight => OnlinePlayerRows.Count switch { > 40 => 36, > 20 => 42, > 10 => 48, _ => 56 };

    partial void OnOnlinePlayerSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredOnlinePlayerRows));
        OnPropertyChanged(nameof(HasFilteredOnlinePlayerRows));
        OnPropertyChanged(nameof(ShowsOnlinePlayerSearchEmpty));
    }

    /// <summary>
    /// The banned players, as a second view of the same authoritative rows.
    /// </summary>
    /// <remarks>
    /// The same <see cref="PlayerAccessRow"/> instances as <see cref="PlayerRows"/>, filtered rather
    /// than copied, so a pardon carried out from either view updates both and no row can disagree
    /// with itself. Nothing is added here that the server's ban files do not record.
    /// </remarks>
    public ObservableCollection<PlayerAccessRow> BannedRows { get; } = [];

    /// <summary>
    /// Which view of the Access page is showing. Two views of one subject, not a second page.
    /// </summary>
    [ObservableProperty]
    private bool showsPlayersList = true;

    [ObservableProperty]
    private bool showsBannedList;

    partial void OnShowsPlayersListChanged(bool value)
    {
        if (value)
            ShowsBannedList = false;
    }

    partial void OnShowsBannedListChanged(bool value)
    {
        if (value)
            ShowsPlayersList = false;
    }

    public bool HasBannedRows => BannedRows.Count > 0;

    public string BannedCountText => BannedRows.Count == 1 ? "1 banned" : $"{BannedRows.Count} banned";

    public string BannedEmptyStateMessage => PlayerModerationAvailable
        ? "Nobody is banned from this server."
        : "Bans are read from the server's own files. Start the server to manage them.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlayerRows))]
    [NotifyPropertyChangedFor(nameof(PlayerEmptyStateMessage))]
    private int knownPlayerCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineCountText))]
    private int onlinePlayerCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlineCountText))]
    [NotifyPropertyChangedFor(nameof(SlotCountText))]
    private int? playerSlotCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhitelistStateText))]
    private bool whitelistEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModeratePlayers))]
    [NotifyPropertyChangedFor(nameof(PlayerEmptyStateMessage))]
    [NotifyPropertyChangedFor(nameof(BannedEmptyStateMessage))]
    [NotifyCanExecuteChangedFor(nameof(AddWhitelistPlayerCommand))]
    private bool playerModerationAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAccessError))]
    private string accessErrorMessage = "";

    public bool HasPlayerRows => PlayerRows.Count > 0;

    public bool HasAccessError => AccessErrorMessage.Length > 0;

    /// <summary>True while the server can carry out a moderation command.</summary>
    public bool CanModeratePlayers => PlayerModerationAvailable;

    /// <summary>
    /// How many players are connected. Deliberately separate from the slot count.
    /// </summary>
    /// <remarks>
    /// "0 / 10 online" above a list of known players was the misleading part: the rows were not online,
    /// and the count was not about them. Connected players and the server's capacity are two facts and
    /// are now stated as two.
    /// </remarks>
    public string OnlineCountText => OnlinePlayerCount == 1 ? "1 player online" : $"{OnlinePlayerCount} players online";

    public string SlotCountText => PlayerSlotCount is { } slots
        ? $"{slots} slots"
        : "Slot count available while running";

    /// <summary>
    /// What the whitelist switch means right now, as a statement rather than a sentence about the
    /// user. The control is labelled "Whitelist"; this says which way it is set and what follows.
    /// </summary>
    public string WhitelistStateText => WhitelistEnabled
        ? "On · only listed players can join"
        : "Off · anyone with the address can join";

    public string PlayerEmptyStateMessage => PlayerModerationAvailable
        ? "Nobody has joined, and nobody has been whitelisted, made an operator or banned."
        : "Player management needs a running server.";

    [ObservableProperty]
    private string newWhitelistPlayerName = "";

    /// <summary>The stamp of the access state currently on screen, used to detect a real change.</summary>
    private string loadedPlayerAccessStamp = "";

    private bool loadingPlayerAccess;

    /// <summary>
    /// Reloads player access when the Agent reports that something changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Event-driven in the sense that matters: the Agent's snapshot carries a stamp that changes when a
    /// player joins or leaves, when a moderation reply arrives, or when one of the access files is
    /// written - including by an <c>op</c> or <c>whitelist</c> command the user typed into the Console.
    /// The shell already reads that snapshot once a second, so no new polling is introduced, and a
    /// re-read only happens when the stamp actually differs.
    /// </para>
    /// <para>
    /// Bounded to the page being looked at. Reading five JSON files for a server whose Access page is
    /// not open would be work nobody asked for.
    /// </para>
    /// </remarks>
    private void SyncPlayerAccessStamp(ServerSnapshot snapshot)
    {
        if (Navigation.CurrentServerDestination is not (ServerDestination.Access or ServerDestination.Overview))
            return;
        if (snapshot.PlayerAccessStamp.Length == 0 ||
            snapshot.PlayerAccessStamp == loadedPlayerAccessStamp)
            return;
        _ = LoadPlayerAccessAsync();
    }

    /// <summary>Reloads the page's authoritative state, whatever triggered it.</summary>
    private async Task LoadPlayerAccessAsync()
    {
        if (SelectedServer is null || loadingPlayerAccess)
            return;
        loadingPlayerAccess = true;
        var serverId = SelectedServer.Definition.Id;
        try
        {
            var snapshot = await client.SendAsync<PlayerAccessSnapshot>(
                "GetPlayerAccess", new ServerIdRequest(serverId)).ConfigureAwait(true);
            if (SelectedServer?.Definition.Id != serverId)
                return;
            AccessErrorMessage = "";
            loadedPlayerAccessStamp = snapshot.Stamp;
            WhitelistEnabled = snapshot.WhitelistEnabled;
            OnlinePlayerCount = snapshot.OnlineCount;
            PlayerSlotCount = snapshot.MaxPlayers;
            PlayerModerationAvailable = snapshot.ServerRunning;
            ApplyPlayerRows(snapshot);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException)
        {
            AccessErrorMessage = exception.Message;
        }
        finally
        {
            loadingPlayerAccess = false;
        }
    }

    private void ApplyPlayerRows(PlayerAccessSnapshot snapshot)
    {
        // Existing rows are updated in place. Rebuilding the collection would drop keyboard focus and
        // close an open row menu every time somebody joined the server.
        for (var index = 0; index < snapshot.Players.Count; index++)
        {
            var player = snapshot.Players[index];
            var existing = PlayerRows.FirstOrDefault(row =>
                string.Equals(row.Name, player.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var row = new PlayerAccessRow(
                    player, snapshot.WhitelistEnabled, snapshot.ServerRunning, ModeratePlayerAsync, CopyPlayerText);
                row.PropertyChanged += PlayerRow_PropertyChanged;
                PlayerRows.Insert(Math.Min(index, PlayerRows.Count), row);
                continue;
            }
            existing.Adopt(player, snapshot.WhitelistEnabled, snapshot.ServerRunning);
            var current = PlayerRows.IndexOf(existing);
            if (current != index && index < PlayerRows.Count)
                PlayerRows.Move(current, index);
        }
        foreach (var stale in PlayerRows
                     .Where(row => snapshot.Players.All(player =>
                         !string.Equals(player.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                     .ToArray())
        {
            stale.PropertyChanged -= PlayerRow_PropertyChanged;
            PlayerRows.Remove(stale);
        }
        KnownPlayerCount = PlayerRows.Count;
        OnPropertyChanged(nameof(HasPlayerRows));
        SyncBannedRows();
        SyncOnlinePlayerRows();
    }

    /// <summary>Keeps Overview on the same row instances and commands as the full Access page.</summary>
    private void SyncOnlinePlayerRows()
    {
        var online = PlayerRows.Where(row => row.Online).ToArray();
        foreach (var stale in OnlinePlayerRows.Where(row => !online.Contains(row)).ToArray())
            OnlinePlayerRows.Remove(stale);
        for (var index = 0; index < online.Length; index++)
        {
            var row = online[index];
            var current = OnlinePlayerRows.IndexOf(row);
            if (current < 0)
                OnlinePlayerRows.Insert(Math.Min(index, OnlinePlayerRows.Count), row);
            else if (current != index && index < OnlinePlayerRows.Count)
                OnlinePlayerRows.Move(current, index);
        }
        OnPropertyChanged(nameof(HasOnlinePlayerRows));
        OnPropertyChanged(nameof(FilteredOnlinePlayerRows));
        OnPropertyChanged(nameof(HasFilteredOnlinePlayerRows));
        OnPropertyChanged(nameof(ShowsOnlinePlayerSearchEmpty));
        OnPropertyChanged(nameof(OnlinePlayerHeadSize));
        OnPropertyChanged(nameof(OnlinePlayerNameFontSize));
        OnPropertyChanged(nameof(OnlinePlayerRowHeight));
        NotifyOnlinePlayerSelectionState();
    }

    private void PlayerRow_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PlayerAccessRow.IsSelected))
            NotifyOnlinePlayerSelectionState();
    }

    private void NotifyOnlinePlayerSelectionState()
    {
        OnPropertyChanged(nameof(SelectedOnlinePlayerCount));
        OnPropertyChanged(nameof(HasSelectedOnlinePlayers));
        OnPropertyChanged(nameof(SelectedOnlinePlayerText));
        GrantOperatorToSelectedCommand.NotifyCanExecuteChanged();
        RemoveOperatorFromSelectedCommand.NotifyCanExecuteChanged();
        WhitelistSelectedCommand.NotifyCanExecuteChanged();
        RemoveWhitelistFromSelectedCommand.NotifyCanExecuteChanged();
        KickSelectedCommand.NotifyCanExecuteChanged();
        BanSelectedCommand.NotifyCanExecuteChanged();
        ClearOnlinePlayerSelectionCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void SelectAllVisibleOnlinePlayers()
    {
        foreach (var row in FilteredOnlinePlayerRows)
            row.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(HasSelectedOnlinePlayers))]
    private void ClearOnlinePlayerSelection()
    {
        foreach (var row in OnlinePlayerRows)
            row.IsSelected = false;
    }

    private bool CanBatchModerateOnlinePlayers() =>
        !IsBusy && PlayerModerationAvailable && HasSelectedOnlinePlayers;

    [RelayCommand(CanExecute = nameof(CanBatchModerateOnlinePlayers))]
    private async Task GrantOperatorToSelectedAsync() =>
        await ModerateSelectedOnlinePlayersAsync(PlayerModerationAction.GrantOperator, row => !row.Operator).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanBatchModerateOnlinePlayers))]
    private async Task RemoveOperatorFromSelectedAsync() =>
        await ModerateSelectedOnlinePlayersAsync(PlayerModerationAction.RemoveOperator, row => row.Operator).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanBatchModerateOnlinePlayers))]
    private async Task WhitelistSelectedAsync() =>
        await ModerateSelectedOnlinePlayersAsync(PlayerModerationAction.AddToWhitelist, row => !row.Whitelisted).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanBatchModerateOnlinePlayers))]
    private async Task RemoveWhitelistFromSelectedAsync() =>
        await ModerateSelectedOnlinePlayersAsync(PlayerModerationAction.RemoveFromWhitelist, row => row.Whitelisted).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanBatchModerateOnlinePlayers))]
    private async Task KickSelectedAsync() =>
        await ModerateSelectedOnlinePlayersAsync(PlayerModerationAction.Kick, row => row.Online).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanBatchModerateOnlinePlayers))]
    private async Task BanSelectedAsync() =>
        await ModerateSelectedOnlinePlayersAsync(PlayerModerationAction.Ban, row => !row.Banned).ConfigureAwait(true);

    private async Task ModerateSelectedOnlinePlayersAsync(
        PlayerModerationAction action,
        Func<PlayerAccessRow, bool> applies)
    {
        if (SelectedServer is null)
            return;
        var selected = OnlinePlayerRows.Where(row => row.IsSelected && applies(row)).ToArray();
        if (selected.Length == 0)
            return;
        var serverId = SelectedServer.Definition.Id;
        await RunBusyAsync($"Applying {action} to {selected.Length} players…", async () =>
        {
            var failures = new List<string>();
            foreach (var row in selected)
            {
                row.IsPending = true;
                try
                {
                    var result = await client.SendAsync<OperationResult>("ModeratePlayer",
                        new PlayerModerationRequest(serverId, row.Name, action)).ConfigureAwait(true);
                    if (!result.Success)
                        failures.Add($"{row.Name}: {result.Message}");
                }
                finally
                {
                    row.IsPending = false;
                }
            }
            foreach (var row in OnlinePlayerRows)
                row.IsSelected = false;
            await LoadPlayerAccessAsync().ConfigureAwait(true);
            StatusMessage = failures.Count == 0
                ? $"{action} applied to {selected.Length} players."
                : $"{selected.Length - failures.Count} succeeded; {failures.Count} failed. {string.Join(" ", failures)}";
        }, "Player action failed").ConfigureAwait(true);
    }

    /// <summary>
    /// Brings the Banned view in step with the rows the Agent just confirmed.
    /// </summary>
    /// <remarks>
    /// A row leaves this collection only because the refreshed snapshot no longer records the player
    /// as banned - which happens after the Agent has confirmed the pardon, never optimistically.
    /// </remarks>
    private void SyncBannedRows()
    {
        var banned = PlayerRows.Where(row => row.Banned).ToArray();
        foreach (var stale in BannedRows.Where(row => !banned.Contains(row)).ToArray())
            BannedRows.Remove(stale);
        for (var index = 0; index < banned.Length; index++)
        {
            var row = banned[index];
            var current = BannedRows.IndexOf(row);
            if (current < 0)
                BannedRows.Insert(Math.Min(index, BannedRows.Count), row);
            else if (current != index && index < BannedRows.Count)
                BannedRows.Move(current, index);
        }
        OnPropertyChanged(nameof(HasBannedRows));
        OnPropertyChanged(nameof(BannedCountText));
    }

    private void CopyPlayerText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        System.Windows.Clipboard.SetText(text);
        StatusMessage = $"Copied: {text}";
    }

    /// <summary>
    /// Carries out one moderation action and refreshes authoritative state.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every action is applied immediately. The Ban control is already labelled as destructive and the
    /// row stays disabled until the server answers, so a second modal confirmation only interrupts
    /// routine moderation without adding useful context.
    /// </para>
    /// <para>
    /// The Agent waits for the server's own reply, so a false return means the server refused - and its
    /// wording is what the row shows.
    /// </para>
    /// </remarks>
    private async Task<bool> ModeratePlayerAsync(PlayerModerationAction action, PlayerAccessRow row)
    {
        if (SelectedServer is null)
            return false;
        var serverId = SelectedServer.Definition.Id;
        try
        {
            var result = await client.SendAsync<OperationResult>("ModeratePlayer",
                new PlayerModerationRequest(serverId, row.Name, action)).ConfigureAwait(true);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                row.ErrorMessage = result.Message;
                await LoadPlayerAccessAsync().ConfigureAwait(true);
                return false;
            }
            await LoadPlayerAccessAsync().ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            row.ErrorMessage = exception.Message;
            return false;
        }
    }

    /// <summary>Adds a player to the whitelist by name, then re-reads the list.</summary>
    [RelayCommand(CanExecute = nameof(CanAddWhitelistPlayer))]
    private async Task AddWhitelistPlayerAsync()
    {
        if (SelectedServer is null)
            return;
        var name = NewWhitelistPlayerName.Trim();
        if (!PlayerModerationPolicy.IsValidPlayerName(name))
        {
            AccessErrorMessage = "Minecraft player names use 1-16 letters, numbers, or underscores.";
            return;
        }
        var serverId = SelectedServer.Definition.Id;
        AccessErrorMessage = "";
        try
        {
            var result = await client.SendAsync<OperationResult>("ModeratePlayer",
                new PlayerModerationRequest(serverId, name, PlayerModerationAction.AddToWhitelist))
                .ConfigureAwait(true);
            StatusMessage = result.Message;
            if (result.Success)
                NewWhitelistPlayerName = "";
            else
                AccessErrorMessage = result.Message;
            // Re-read either way: a refused add still tells the truth about the current list.
            await LoadPlayerAccessAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            AccessErrorMessage = exception.Message;
        }
    }

    private bool CanAddWhitelistPlayer() =>
        PlayerModerationAvailable && NewWhitelistPlayerName.Trim().Length > 0;

    partial void OnNewWhitelistPlayerNameChanged(string value) =>
        AddWhitelistPlayerCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// The whitelist switch. Reads authoritative state; writing it asks the Agent to change it.
    /// </summary>
    /// <remarks>
    /// The getter is the server's state, so reverting a refused change is simply raising the property
    /// again: the binding re-reads it and the switch returns to the truth without a second field to
    /// keep in step.
    /// </remarks>
    public bool WhitelistSwitch
    {
        get => WhitelistEnabled;
        set
        {
            if (value == WhitelistEnabled)
                return;
            _ = SetWhitelistEnabledAsync(value);
        }
    }

    /// <summary>Turns the whitelist on or off, then re-reads the resulting state.</summary>
    private async Task SetWhitelistEnabledAsync(bool enabled)
    {
        if (SelectedServer is null)
            return;
        var serverId = SelectedServer.Definition.Id;
        await RunBusyAsync(enabled ? "Turning the whitelist on…" : "Turning the whitelist off…", async () =>
        {
            var result = await client.SendAsync<OperationResult>("WhitelistEnable",
                new JarEnabledRequest(serverId, "", enabled)).ConfigureAwait(true);
            StatusMessage = result.Message;
            if (!result.Success)
                AccessErrorMessage = result.Message;
            await LoadPlayerAccessAsync().ConfigureAwait(true);
        }, failureAction: "Whitelist change failed").ConfigureAwait(true);
        // Whatever happened, the switch shows what the server reports now.
        OnPropertyChanged(nameof(WhitelistSwitch));
    }

    partial void OnWhitelistEnabledChanged(bool value) => OnPropertyChanged(nameof(WhitelistSwitch));

    /// <summary>Re-reads player access on demand, for the page's refresh action.</summary>
    [RelayCommand]
    private async Task RefreshPlayerAccessAsync()
    {
        loadedPlayerAccessStamp = "";
        await LoadPlayerAccessAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Re-reads whatever a command typed into the Console could have changed.
    /// </summary>
    /// <remarks>
    /// Only for commands that genuinely affect a page: <c>op</c>, <c>deop</c>, <c>whitelist</c>,
    /// <c>ban</c>, <c>pardon</c>, <c>kick</c> and <c>gamerule</c>. Everything else is left alone, so
    /// typing <c>say hello</c> does not trigger file reads.
    /// </remarks>
    private async Task ReloadAfterConsoleCommandAsync(string command)
    {
        if (PlayerModerationPolicy.AffectsPlayerAccess(command))
        {
            loadedPlayerAccessStamp = "";
            await LoadPlayerAccessAsync().ConfigureAwait(true);
        }
        if (PlayerModerationPolicy.AffectsGamerules(command))
            await LoadGamerulesAsync().ConfigureAwait(true);
    }

    /// <summary>Re-reads the destination's authoritative state when the user arrives on it.</summary>
    /// <remarks>
    /// Returning to a page is a request to see the current state. The stamp comparison covers changes
    /// that happen while the page is open; this covers everything that happened while it was not.
    /// </remarks>
    internal void OnServerDestinationEntered(string destination)
    {
        if (SelectedServer is null)
            return;
        switch (destination)
        {
            case ServerDestination.Overview:
            case ServerDestination.Access:
                loadedPlayerAccessStamp = "";
                _ = LoadPlayerAccessAsync();
                break;
            case ServerDestination.Settings:
                _ = LoadGamerulesAsync();
                break;
        }
    }
}
