namespace ChunkPilot.Core;

/// <summary>
/// Ordered, capability-driven status sources for one server. Query is included only when it was
/// already deliberately enabled; ChunkPilot never enables Query or RCON to collect a count.
/// </summary>
public sealed record MinecraftPlayerStatusStrategy
{
    public string MinecraftVersion { get; init; } = "";
    public IReadOnlyList<PlayerStatusSource> OrderedSources { get; init; } = [];
    public bool QueryAlreadyEnabled { get; init; }
    public bool ConsoleAvailable { get; init; }
    public string Limitation { get; init; } = "";
}

public static class MinecraftPlayerStatusPolicy
{
    public static MinecraftPlayerStatusStrategy For(
        string? minecraftVersion,
        bool queryAlreadyEnabled,
        bool consoleAvailable)
    {
        var historical = IsHistorical(minecraftVersion);
        var sources = new List<PlayerStatusSource>(8);
        if (historical)
        {
            sources.Add(PlayerStatusSource.LegacySimpleStatus);
            sources.Add(PlayerStatusSource.LegacyExtendedStatus);
            sources.Add(PlayerStatusSource.ModernStatus);
        }
        else
        {
            sources.Add(PlayerStatusSource.ModernStatus);
            sources.Add(PlayerStatusSource.LegacyExtendedStatus);
            sources.Add(PlayerStatusSource.LegacySimpleStatus);
        }
        if (queryAlreadyEnabled) sources.Add(PlayerStatusSource.Query);
        if (consoleAvailable) sources.Add(PlayerStatusSource.ConsoleList);
        if (consoleAvailable) sources.Add(PlayerStatusSource.ConsoleRoster);
        sources.Add(PlayerStatusSource.LastExactStatus);
        sources.Add(PlayerStatusSource.StatusCheckFailed);
        return new MinecraftPlayerStatusStrategy
        {
            MinecraftVersion = minecraftVersion ?? "",
            OrderedSources = sources,
            QueryAlreadyEnabled = queryAlreadyEnabled,
            ConsoleAvailable = consoleAvailable,
            Limitation = queryAlreadyEnabled
                ? "Query was already enabled by the server owner and may be used after server-list status."
                : "Query and RCON remain disabled; console evidence is used only through the owned server process."
        };
    }

    public static bool IsHistorical(string? minecraftVersion)
    {
        if (string.IsNullOrWhiteSpace(minecraftVersion)) return false;
        if (minecraftVersion.StartsWith('a') || minecraftVersion.StartsWith('b') ||
            minecraftVersion.StartsWith("rd-", StringComparison.OrdinalIgnoreCase) ||
            minecraftVersion.StartsWith("c0.", StringComparison.OrdinalIgnoreCase))
            return true;
        return Version.TryParse(minecraftVersion, out var version) && version < new Version(1, 4);
    }
}

public enum PlayerPresenceChangeKind
{
    Joined,
    Left
}

public sealed record PlayerPresenceChange(string PlayerName, PlayerPresenceChangeKind Kind);

public sealed record PlayerStatusResolutionRequest
{
    public required MinecraftPlayerStatusStrategy Strategy { get; init; }
    public PlayerStatusEvidence? ServerListEvidence { get; init; }
    public PlayerStatusEvidence? QueryEvidence { get; init; }
    public string ConsoleListLine { get; init; } = "";
    public IReadOnlyCollection<string> SessionRoster { get; init; } = [];
    public PlayerStatusEvidence? LastExactEvidence { get; init; }
    public int? KnownMaximumPlayers { get; init; }
    public DateTimeOffset NowUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan LastExactFreshness { get; init; } = TimeSpan.FromSeconds(30);
}
