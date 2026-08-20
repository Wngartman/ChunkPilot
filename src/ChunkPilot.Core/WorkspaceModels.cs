namespace ChunkPilot.Core;

// ═══════════════════════════════════════════════════════════════════════════════════════════════
// Contracts for the daily server workspace: the file editor, player moderation and game rules.
//
// These live in Core because the same rule has to hold in three places at once. Which files the
// integrated editor may open decides what the file list offers, what the Agent will read and what it
// will write; a moderation reply decides whether the UI may claim the change happened. A rule
// duplicated per layer is a rule that will disagree with itself.
// ═══════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>How ChunkPilot may present one file in a server folder.</summary>
public enum ServerFileKind
{
    /// <summary>A folder. Opened by navigating into it.</summary>
    Folder,

    /// <summary>A text format the integrated editor may read and write.</summary>
    EditableText,

    /// <summary>A text format that is too large for the editor.</summary>
    TooLarge,

    /// <summary>Anything else: a JAR, an archive, a region file, an image. Never decoded as text.</summary>
    Binary
}

/// <summary>
/// The one rule for what the integrated text editor may open.
/// </summary>
/// <remarks>
/// <para>
/// Extension-based on purpose. Sniffing content would let the editor open a <c>.dat</c> world file
/// that happens to start with printable bytes, and world data is exactly what must never be edited
/// as text. The Agent additionally rejects anything containing a NUL byte, so a mislabelled binary
/// still cannot reach the editor.
/// </para>
/// <para>
/// This is not a general filesystem editor: it only ever applies inside one server's own folder,
/// through the Agent's path-confined file service.
/// </para>
/// </remarks>
public static class ServerFilePolicy
{
    /// <summary>Largest file the integrated editor will load.</summary>
    public const long MaximumEditableBytes = 10L * 1024 * 1024;

    private static readonly HashSet<string> EditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".properties", ".txt", ".json", ".json5", ".toml", ".yaml", ".yml", ".cfg", ".conf",
        ".ini", ".xml", ".snbt", ".log", ".bat", ".cmd", ".ps1"
    };

    /// <summary>Every extension the editor accepts, for documentation and tests.</summary>
    public static IReadOnlyCollection<string> EditableFileExtensions { get; } =
        EditableExtensions.OrderBy(value => value, StringComparer.Ordinal).ToArray();

    /// <summary>True when the extension is one the editor may read and write.</summary>
    public static bool IsEditableTextName(string fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        EditableExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>Classifies one listed entry so the UI can offer the right action for it.</summary>
    public static ServerFileKind Classify(string fileName, bool isDirectory, long sizeBytes)
    {
        if (isDirectory)
            return ServerFileKind.Folder;
        if (!IsEditableTextName(fileName))
            return ServerFileKind.Binary;
        return sizeBytes > MaximumEditableBytes ? ServerFileKind.TooLarge : ServerFileKind.EditableText;
    }
}

/// <summary>One moderation action against one player.</summary>
public enum PlayerModerationAction
{
    AddToWhitelist,
    RemoveFromWhitelist,
    GrantOperator,
    RemoveOperator,
    Ban,
    Pardon,
    Kick
}

public sealed record PlayerModerationRequest(
    Guid ServerId,
    string PlayerName,
    PlayerModerationAction Action,
    string Reason = "");

/// <summary>
/// The console command each moderation action sends, and the replies that decide whether it worked.
/// </summary>
/// <remarks>
/// <para>
/// Writing a command to the server's stdin is not evidence that anything changed. Vanilla answers
/// every one of these on the console, and the answer is the only authority on the outcome: it is what
/// separates "added to the whitelist" from "that player does not exist". ChunkPilot waits for it, so
/// the row the user sees afterwards reflects the server rather than the click.
/// </para>
/// <para>
/// The reply also decides <em>when</em> to re-read <c>whitelist.json</c> or <c>ops.json</c>: the
/// server writes those files as it reports, so reading before the reply arrives returns the state
/// from before the change. That race is what made an added player fail to appear.
/// </para>
/// </remarks>
public static class PlayerModerationPolicy
{
    /// <summary>Longest ChunkPilot waits for the server's answer before reporting it unconfirmed.</summary>
    public static TimeSpan ReplyTimeout { get; } = TimeSpan.FromSeconds(8);

    /// <summary>The exact console command for an action against a validated player name.</summary>
    public static string CommandFor(PlayerModerationAction action, string playerName, string reason = "")
    {
        var name = ValidatePlayerName(playerName);
        var trimmedReason = (reason ?? "").Trim();
        return action switch
        {
            PlayerModerationAction.AddToWhitelist => $"whitelist add {name}",
            PlayerModerationAction.RemoveFromWhitelist => $"whitelist remove {name}",
            PlayerModerationAction.GrantOperator => $"op {name}",
            PlayerModerationAction.RemoveOperator => $"deop {name}",
            PlayerModerationAction.Ban => trimmedReason.Length > 0
                ? $"ban {name} {SanitizeReason(trimmedReason)}"
                : $"ban {name}",
            PlayerModerationAction.Pardon => $"pardon {name}",
            PlayerModerationAction.Kick => trimmedReason.Length > 0
                ? $"kick {name} {SanitizeReason(trimmedReason)}"
                : $"kick {name}",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unsupported moderation action.")
        };
    }

    /// <summary>True when this console line is the server confirming the action succeeded.</summary>
    public static bool IsSuccessReply(PlayerModerationAction action, string playerName, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        var name = (playerName ?? "").Trim();
        return action switch
        {
            PlayerModerationAction.AddToWhitelist =>
                Mentions(line, name) && Contains(line, "to the whitelist"),
            PlayerModerationAction.RemoveFromWhitelist =>
                Mentions(line, name) && Contains(line, "from the whitelist"),
            PlayerModerationAction.GrantOperator =>
                Mentions(line, name) && Contains(line, "a server operator") &&
                !Contains(line, "no longer"),
            PlayerModerationAction.RemoveOperator =>
                Mentions(line, name) && Contains(line, "no longer a server operator"),
            PlayerModerationAction.Ban =>
                Mentions(line, name) && Contains(line, "banned") && !Contains(line, "unbanned"),
            PlayerModerationAction.Pardon =>
                Mentions(line, name) && (Contains(line, "unbanned") || Contains(line, "pardoned")),
            PlayerModerationAction.Kick =>
                Mentions(line, name) && Contains(line, "kicked"),
            _ => false
        };
    }

    /// <summary>
    /// True when this console line is the server refusing the action. The line itself is the message
    /// the user sees, because the server's own wording is more accurate than any paraphrase.
    /// </summary>
    public static bool IsFailureReply(PlayerModerationAction action, string playerName, string line)
    {
        if (string.IsNullOrWhiteSpace(line) || IsSuccessReply(action, playerName, line))
            return false;
        return Contains(line, "That player does not exist") ||
               Contains(line, "No player was found") ||
               Contains(line, "Nothing changed") ||
               Contains(line, "already whitelisted") ||
               Contains(line, "is not whitelisted") ||
               Contains(line, "already banned") ||
               Contains(line, "is not banned") ||
               Contains(line, "Unknown or incomplete command") ||
               Contains(line, "Incorrect argument");
    }

    /// <summary>What the user is told when the server accepted the command but never answered.</summary>
    public static string UnconfirmedMessage(PlayerModerationAction action, string playerName) =>
        $"{Describe(action)} for {playerName} was sent, but the server did not confirm it. " +
        "The list below shows the server's current state.";

    /// <summary>Plain-language name of an action, for status text and errors.</summary>
    public static string Describe(PlayerModerationAction action) => action switch
    {
        PlayerModerationAction.AddToWhitelist => "Adding to the whitelist",
        PlayerModerationAction.RemoveFromWhitelist => "Removing from the whitelist",
        PlayerModerationAction.GrantOperator => "Granting operator",
        PlayerModerationAction.RemoveOperator => "Removing operator",
        PlayerModerationAction.Ban => "Banning",
        PlayerModerationAction.Pardon => "Pardoning",
        PlayerModerationAction.Kick => "Kicking",
        _ => "This action"
    };

    /// <summary>True when a console command the user typed changes player access.</summary>
    /// <remarks>
    /// Used to re-read authoritative state after a command sent from the Console page, so typing
    /// <c>op Someone</c> there leaves the Access page agreeing with the server.
    /// </remarks>
    public static bool AffectsPlayerAccess(string command)
    {
        var verb = FirstWord(command);
        return verb is "op" or "deop" or "ban" or "ban-ip" or "pardon" or "pardon-ip" or
            "whitelist" or "kick";
    }

    /// <summary>True when a console command the user typed changes a game rule.</summary>
    public static bool AffectsGamerules(string command) => FirstWord(command) is "gamerule";

    /// <summary>Minecraft player names: 1-16 letters, numbers or underscores.</summary>
    public static string ValidatePlayerName(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);
        var name = playerName.Trim();
        if (name.Length > 16 || name.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new ArgumentException(
                "Minecraft player names use 1-16 letters, numbers, or underscores.", nameof(playerName));
        return name;
    }

    /// <summary>True when a name could be typed into a moderation control at all.</summary>
    public static bool IsValidPlayerName(string playerName)
    {
        try
        {
            _ = ValidatePlayerName(playerName);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string FirstWord(string command)
    {
        var trimmed = (command ?? "").Trim().TrimStart('/');
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        return (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
    }

    /// <summary>A reason is free text on a console line, so newlines must not become a second command.</summary>
    private static string SanitizeReason(string reason) =>
        new(reason.Where(character => !char.IsControl(character)).Take(120).ToArray());

    private static bool Mentions(string line, string name) =>
        name.Length > 0 && line.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string line, string fragment) =>
        line.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Everything the Access page needs, read as one authoritative answer.
/// </summary>
/// <remarks>
/// One response rather than several requests: the online count, the whitelist switch and the rows
/// have to agree with each other, and three separate reads can disagree the moment a player joins
/// between them.
/// </remarks>
public sealed record PlayerAccessSnapshot
{
    public Guid ServerId { get; init; }

    /// <summary>True while the server process is running and can answer moderation commands.</summary>
    public bool ServerRunning { get; init; }

    /// <summary>The <c>white-list</c> property as written in server.properties.</summary>
    public bool WhitelistEnabled { get; init; }

    /// <summary>Players the server has reported as connected, from its own console output.</summary>
    public int OnlineCount { get; init; }

    /// <summary>Slots reported by the server, or null when it is not running.</summary>
    public int? MaxPlayers { get; init; }

    public IReadOnlyList<UnifiedPlayerAccess> Players { get; init; } = [];

    /// <summary>
    /// Changes whenever anything above changes. The shell compares it against the last loaded value
    /// to decide whether the page needs re-reading, which is what keeps a Console-typed <c>op</c> or
    /// a player joining from leaving a stale row on screen without polling the files from the UI.
    /// </summary>
    public string Stamp { get; init; } = "";
}

/// <summary>What kind of control a game rule needs.</summary>
public enum GameruleValueKind
{
    Boolean,
    WholeNumber
}

/// <summary>Where a shown game-rule value came from. Never guessed.</summary>
public enum GameruleProvenance
{
    /// <summary>The running server answered <c>gamerule &lt;name&gt;</c> with this value.</summary>
    ReportedByServer,

    /// <summary>ChunkPilot holds this change for the next successful start.</summary>
    QueuedForNextStart,

    /// <summary>Not read. The server is not running, so nothing may be presented as its value.</summary>
    Unknown
}

public sealed record GameruleState
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public GameruleValueKind Kind { get; init; }
    public string Value { get; init; } = "";
    public string DefaultValue { get; init; } = "";
    public GameruleProvenance Provenance { get; init; } = GameruleProvenance.Unknown;
    public int Minimum { get; init; }
    public int Maximum { get; init; }

    public bool BooleanValue =>
        Kind == GameruleValueKind.Boolean && bool.TryParse(Value, out var parsed) && parsed;

    public int IntegerValue =>
        Kind == GameruleValueKind.WholeNumber && int.TryParse(Value, out var parsed) ? parsed : 0;
}

/// <summary>
/// What a running server said about the rules it was asked for.
/// </summary>
/// <param name="Reported">Rules the server answered, with the value it reported.</param>
/// <param name="Rejected">
/// Rules the server refused to parse. Those are not offered as controls: a switch for a rule the
/// server will not accept is a control that fails the moment it is used.
/// </param>
public sealed record GameruleQueryResult(
    IReadOnlyDictionary<string, string> Reported,
    IReadOnlySet<string> Rejected)
{
    public static GameruleQueryResult Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));
}

public sealed record GameruleStateResponse
{
    public Guid ServerId { get; init; }

    /// <summary>True when the values below were read from the running server.</summary>
    public bool ServerRunning { get; init; }

    /// <summary>True when ChunkPilot may change a rule right now.</summary>
    public bool CanChange { get; init; }

    /// <summary>Why the controls are unavailable, when they are. Empty when they are available.</summary>
    public string UnavailableReason { get; init; } = "";

    public IReadOnlyList<GameruleState> Rules { get; init; } = [];
}
