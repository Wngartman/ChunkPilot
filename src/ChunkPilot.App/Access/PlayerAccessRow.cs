using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ChunkPilot.App.Access;

/// <summary>
/// One player, as a row that can be acted on.
/// </summary>
/// <remarks>
/// <para>
/// The switches bind two-way to <see cref="Whitelisted"/> and <see cref="Operator"/>. A user gesture
/// sends the matching Agent operation, disables the row until the server answers, and reverts the
/// switch with an inline reason if the answer is no. Authoritative state arrives through
/// <see cref="Adopt"/>, which writes the backing fields directly so a refresh can never look like a
/// gesture and echo the command back to the server.
/// </para>
/// <para>
/// Nothing here touches a server, a file or a process: it calls the delegate the view model supplies,
/// which is the Agent surface.
/// </para>
/// </remarks>
public sealed partial class PlayerAccessRow : ObservableObject
{
    private readonly Func<PlayerModerationAction, PlayerAccessRow, Task<bool>> moderate;
    private readonly Action<string> copy;
    private bool suppressCommands;

    public PlayerAccessRow(
        UnifiedPlayerAccess player,
        bool whitelistEnabled,
        bool serverRunning,
        Func<PlayerModerationAction, PlayerAccessRow, Task<bool>> moderate,
        Action<string> copy)
    {
        ArgumentNullException.ThrowIfNull(player);
        this.moderate = moderate;
        this.copy = copy;
        Name = player.Name;
        Adopt(player, whitelistEnabled, serverRunning);
    }

    /// <summary>The player's exact name, as the server records it.</summary>
    public string Name { get; }

    /// <summary>Overview-only batch selection. It is UI state, never server state.</summary>
    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUuid))]
    [NotifyPropertyChangedFor(nameof(UuidText))]
    private Guid? uuid;

    /// <summary>True only when the server has resolved a real UUID for this player.</summary>
    public bool HasUuid => Uuid is not null;

    public string UuidText => Uuid?.ToString("D") ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusTone))]
    [NotifyPropertyChangedFor(nameof(CanKick))]
    private bool online;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusTone))]
    [NotifyPropertyChangedFor(nameof(CanBan))]
    [NotifyPropertyChangedFor(nameof(CanPardon))]
    private bool banned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DateTimeOffset? lastSeenAt;

    // ── Ban detail, exactly as the server's own ban file records it ──
    // Nothing here is inferred. A field the file does not carry stays empty, and the Banned view
    // shows only what is present rather than filling the gap with a plausible sentence.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BanReasonText))]
    private string banReason = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BanSourceText))]
    private string banSource = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BanCreatedText))]
    private DateTimeOffset? banCreatedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BanExpiryText))]
    private DateTimeOffset? banExpiresAt;

    public string BanReasonText => BanReason.Length > 0 ? BanReason : "No reason recorded";

    public string BanSourceText => BanSource.Length > 0 ? $"Banned by {BanSource}" : "Source not recorded";

    public string BanCreatedText => BanCreatedAt is { } created
        ? $"Banned {created.ToLocalTime():M/d/yyyy h:mm tt}"
        : "Date not recorded";

    /// <summary>
    /// When the ban lifts. Minecraft writes <c>forever</c> for a permanent ban, which the reader
    /// turns into no expiry at all - so the absence of a date means permanent, not unknown.
    /// </summary>
    public string BanExpiryText => BanExpiresAt is { } expires
        ? $"Expires {expires.ToLocalTime():M/d/yyyy h:mm tt}"
        : "Permanent";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    private bool isPending;

    partial void OnIsPendingChanged(bool value) => RefreshCommands();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInteractive))]
    [NotifyPropertyChangedFor(nameof(CanKick))]
    [NotifyPropertyChangedFor(nameof(CanBan))]
    [NotifyPropertyChangedFor(nameof(CanPardon))]
    private bool serverRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool whitelistEnabled;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>
    /// Controls are live only while the server can answer and no change is in flight.
    /// </summary>
    /// <remarks>
    /// Every one of these actions is a console command, so a stopped server cannot perform any of them.
    /// A switch that looks operable while the server is stopped is a promise ChunkPilot cannot keep.
    /// </remarks>
    public bool IsInteractive => ServerRunning && !IsPending;

    public bool CanKick => IsInteractive && Online;

    public bool CanBan => IsInteractive && !Banned;

    public bool CanPardon => IsInteractive && Banned;

    /// <summary>
    /// Status in words, never colour alone: online, banned, or known but not connected.
    /// </summary>
    /// <remarks>
    /// The distinction the previous page lost. Every known player sat under a heading that said
    /// "0 / 10 online", which read as a contradiction: rows that looked online under a count of none.
    /// </remarks>
    public string StatusText
    {
        get
        {
            if (Banned)
                return "Banned";
            if (Online)
                return "Online";
            if (WhitelistEnabled && !Whitelisted)
                return "Cannot join";
            return LastSeenAt is { } seen && !IsImplausible(seen)
                ? $"Offline · seen {seen.ToLocalTime():M/d/yyyy h:mm tt}"
                : "Offline · no activity recorded";
        }
    }

    /// <summary>
    /// True for a timestamp that cannot describe a session that has already happened.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The last line of defence for the defect that produced "seen 9/7 10:05 AM" in August. Its
    /// cause was upstream - <c>usercache.json</c>'s cache-expiry date was being read as activity, and
    /// merely whitelisting somebody created that entry - and that mapping is gone. This stays because
    /// a future date is never a fact about the past whatever produced it, and showing one is worse
    /// than showing nothing. A small allowance absorbs clock skew between the server's clock and
    /// this one; anything beyond it is not a rounding error.
    /// </para>
    /// </remarks>
    private static bool IsImplausible(DateTimeOffset value) =>
        value > DateTimeOffset.Now.AddMinutes(5) || value < MinecraftEpoch;

    /// <summary>Before Minecraft existed, so no session can predate it.</summary>
    private static readonly DateTimeOffset MinecraftEpoch = new(2009, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public AppTone StatusTone => Banned
        ? AppTone.Danger
        : Online ? AppTone.Success : AppTone.Neutral;

    public bool Whitelisted
    {
        get => whitelisted;
        set => SetSwitch(ref whitelisted, value, nameof(Whitelisted),
            PlayerModerationAction.AddToWhitelist, PlayerModerationAction.RemoveFromWhitelist);
    }

    public bool Operator
    {
        get => @operator;
        set => SetSwitch(ref @operator, value, nameof(Operator),
            PlayerModerationAction.GrantOperator, PlayerModerationAction.RemoveOperator);
    }

    private bool whitelisted;
    private bool @operator;

    /// <summary>
    /// The menu offers only what is genuinely possible for this player right now.
    /// </summary>
    /// <remarks>
    /// "Add to whitelist" for someone already whitelisted, or "Pardon" for someone who is not banned,
    /// are not disabled items to reason about - they are absent.
    /// </remarks>
    public bool CanAddToWhitelist => IsInteractive && !Whitelisted;

    public bool CanRemoveFromWhitelist => IsInteractive && Whitelisted;

    public bool CanGrantOperator => IsInteractive && !Operator;

    public bool CanRemoveOperator => IsInteractive && Operator;

    [RelayCommand(CanExecute = nameof(CanAddToWhitelist))]
    private async Task WhitelistAsync() =>
        await RunAsync(PlayerModerationAction.AddToWhitelist).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanRemoveFromWhitelist))]
    private async Task UnwhitelistAsync() =>
        await RunAsync(PlayerModerationAction.RemoveFromWhitelist).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanGrantOperator))]
    private async Task GrantOperatorAsync() =>
        await RunAsync(PlayerModerationAction.GrantOperator).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanRemoveOperator))]
    private async Task RemoveOperatorAsync() =>
        await RunAsync(PlayerModerationAction.RemoveOperator).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanKick))]
    private async Task KickAsync() => await RunAsync(PlayerModerationAction.Kick).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanBan))]
    private async Task BanAsync() => await RunAsync(PlayerModerationAction.Ban).ConfigureAwait(true);

    [RelayCommand(CanExecute = nameof(CanPardon))]
    private async Task PardonAsync() => await RunAsync(PlayerModerationAction.Pardon).ConfigureAwait(true);

    [RelayCommand]
    private void CopyName() => copy(Name);

    [RelayCommand(CanExecute = nameof(HasUuid))]
    private void CopyUuid() => copy(UuidText);

    [RelayCommand]
    private void DismissError() => ErrorMessage = "";

    /// <summary>Takes authoritative state from the Agent without triggering any command.</summary>
    public void Adopt(UnifiedPlayerAccess player, bool whitelistIsEnabled, bool isRunning)
    {
        ArgumentNullException.ThrowIfNull(player);
        suppressCommands = true;
        try
        {
            Uuid = player.Uuid;
            Online = player.Online;
            Banned = player.PlayerBanned || player.IpBanned;
            BanReason = player.BanReason;
            BanSource = player.BanSource;
            BanCreatedAt = player.BanCreatedAt;
            BanExpiresAt = player.BanExpiresAt;
            LastSeenAt = player.LastSeenAt;
            WhitelistEnabled = whitelistIsEnabled;
            ServerRunning = isRunning;
            SetProperty(ref whitelisted, player.Whitelisted, nameof(Whitelisted));
            SetProperty(ref @operator, player.Operator, nameof(Operator));
            IsPending = false;
            RefreshCommands();
        }
        finally
        {
            suppressCommands = false;
        }
    }

    private void SetSwitch(
        ref bool field,
        bool value,
        string propertyName,
        PlayerModerationAction whenTrue,
        PlayerModerationAction whenFalse)
    {
        if (field == value)
            return;
        var previous = field;
        SetProperty(ref field, value, propertyName);
        RefreshCommands();
        if (suppressCommands)
            return;
        var action = value ? whenTrue : whenFalse;
        _ = RunAsync(action, () => RevertSwitch(propertyName, previous));
    }

    private void RevertSwitch(string propertyName, bool previous)
    {
        suppressCommands = true;
        try
        {
            if (propertyName == nameof(Whitelisted))
                SetProperty(ref whitelisted, previous, nameof(Whitelisted));
            else
                SetProperty(ref @operator, previous, nameof(Operator));
        }
        finally
        {
            suppressCommands = false;
        }
    }

    private async Task RunAsync(PlayerModerationAction action, Action? revert = null)
    {
        // One change at a time per row. Without this, a double click on a switch queues two opposite
        // commands and whichever the server answers last decides the state.
        if (IsPending)
            return;
        IsPending = true;
        ErrorMessage = "";
        RefreshCommands();
        try
        {
            if (!await moderate(action, this).ConfigureAwait(true))
                revert?.Invoke();
        }
        finally
        {
            IsPending = false;
            RefreshCommands();
        }
    }

    private void RefreshCommands()
    {
        foreach (var name in new[]
                 {
                     nameof(CanAddToWhitelist), nameof(CanRemoveFromWhitelist),
                     nameof(CanGrantOperator), nameof(CanRemoveOperator)
                 })
            OnPropertyChanged(name);
        WhitelistCommand.NotifyCanExecuteChanged();
        UnwhitelistCommand.NotifyCanExecuteChanged();
        GrantOperatorCommand.NotifyCanExecuteChanged();
        RemoveOperatorCommand.NotifyCanExecuteChanged();
        KickCommand.NotifyCanExecuteChanged();
        BanCommand.NotifyCanExecuteChanged();
        PardonCommand.NotifyCanExecuteChanged();
        CopyUuidCommand.NotifyCanExecuteChanged();
    }
}
