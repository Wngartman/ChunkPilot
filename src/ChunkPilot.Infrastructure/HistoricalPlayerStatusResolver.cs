using System.Text.RegularExpressions;
using ChunkPilot.Core;

namespace ChunkPilot.Infrastructure;

/// <summary>
/// Combines protocol, deliberately enabled Query, owned-console, and current-session evidence while
/// preserving the difference between unknown and zero.
/// </summary>
public static partial class HistoricalPlayerStatusResolver
{
    public static PlayerStatusEvidence Resolve(PlayerStatusResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Strategy);
        foreach (var source in request.Strategy.OrderedSources)
        {
            if (source is PlayerStatusSource.ModernStatus or PlayerStatusSource.LegacyExtendedStatus or
                PlayerStatusSource.LegacySimpleStatus &&
                request.ServerListEvidence is { } status && status.Source == source)
                return status;
            if (source == PlayerStatusSource.Query && request.Strategy.QueryAlreadyEnabled &&
                request.QueryEvidence is { Source: PlayerStatusSource.Query } query)
                return query;
            if (source == PlayerStatusSource.ConsoleList && request.Strategy.ConsoleAvailable &&
                TryParseConsoleList(request.ConsoleListLine, request.KnownMaximumPlayers, request.NowUtc) is { } list)
                return list;
            if (source == PlayerStatusSource.ConsoleRoster && request.Strategy.ConsoleAvailable &&
                request.SessionRoster.Count > 0)
                return new PlayerStatusEvidence
                {
                    Online = request.SessionRoster.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Maximum = request.KnownMaximumPlayers,
                    Source = PlayerStatusSource.ConsoleRoster,
                    Exact = false,
                    CheckedAt = request.NowUtc,
                    Detail =
                        "Current-session console roster; players already online before observation may be absent."
                };
            if (source == PlayerStatusSource.LastExactStatus && request.LastExactEvidence is { Exact: true } prior &&
                request.NowUtc >= prior.CheckedAt && request.NowUtc - prior.CheckedAt <= request.LastExactFreshness)
                return prior with
                {
                    Source = PlayerStatusSource.LastExactStatus,
                    Exact = false,
                    CheckedAt = request.NowUtc,
                    Detail = "Last exact count retained briefly while a newer status strategy did not answer."
                };
        }

        return new PlayerStatusEvidence
        {
            Source = request.Strategy.OrderedSources.Contains(PlayerStatusSource.StatusCheckFailed)
                ? PlayerStatusSource.StatusCheckFailed
                : PlayerStatusSource.Unsupported,
            Exact = false,
            CheckedAt = request.NowUtc,
            Detail = "Player status is unknown; ChunkPilot has not inferred zero or server unreachability."
        };
    }

    public static PlayerStatusEvidence? TryParseConsoleList(
        string line,
        int? knownMaximumPlayers = null,
        DateTimeOffset? checkedAt = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var modern = ModernListPattern().Match(line);
        if (modern.Success && int.TryParse(modern.Groups["online"].Value, out var online) &&
            int.TryParse(modern.Groups["maximum"].Value, out var maximum) && ValidCounts(online, maximum))
            return new PlayerStatusEvidence
            {
                Online = online,
                Maximum = maximum,
                Source = PlayerStatusSource.ConsoleList,
                Exact = true,
                CheckedAt = checkedAt ?? DateTimeOffset.UtcNow,
                Detail = "Exact count from the owned server's list command."
            };

        var historical = HistoricalListPattern().Match(line);
        if (!historical.Success) return null;
        var names = historical.Groups["players"].Value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(PlayerModerationPolicy.IsValidPlayerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var historicalMaximum = knownMaximumPlayers is >= 0 ? knownMaximumPlayers : null;
        if (historicalMaximum is { } known && names.Length > known) return null;
        return new PlayerStatusEvidence
        {
            Online = names.Length,
            Maximum = historicalMaximum,
            Source = PlayerStatusSource.ConsoleList,
            Exact = true,
            CheckedAt = checkedAt ?? DateTimeOffset.UtcNow,
            Detail = historicalMaximum is null
                ? "Exact online roster from the owned historical server's list command; maximum is unavailable."
                : "Exact online roster from the owned historical server's list command."
        };
    }

    public static PlayerPresenceChange? TryParsePresenceChange(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        foreach (var (pattern, kind) in PresencePatterns)
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            var name = match.Groups["name"].Value;
            if (PlayerModerationPolicy.IsValidPlayerName(name))
                return new PlayerPresenceChange(name, kind);
        }
        return null;
    }

    private static bool ValidCounts(int online, int maximum) =>
        online >= 0 && maximum >= 0 && online <= maximum;

    private static readonly (Regex Pattern, PlayerPresenceChangeKind Kind)[] PresencePatterns =
    [
        (ModernJoinPattern(), PlayerPresenceChangeKind.Joined),
        (ModernLeavePattern(), PlayerPresenceChangeKind.Left),
        (HistoricalLoginPattern(), PlayerPresenceChangeKind.Joined),
        (HistoricalDisconnectPattern(), PlayerPresenceChangeKind.Left)
    ];

    [GeneratedRegex(@"(?:^|:\s|\]\s)There are\s+(?<online>\d+)\s+of a max of\s+(?<maximum>\d+)\s+players online(?::|\.|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernListPattern();

    [GeneratedRegex(@"(?:^|:\s|\]\s)Connected players:\s*(?<players>[A-Za-z0-9_, ]*)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalListPattern();

    [GeneratedRegex(@"(?:^|:\s|\]\s)(?<name>[A-Za-z0-9_]{1,16}) joined the game\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernJoinPattern();

    [GeneratedRegex(@"(?:^|:\s|\]\s)(?<name>[A-Za-z0-9_]{1,16}) left the game\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModernLeavePattern();

    [GeneratedRegex(@"(?:^|:\s|\]\s)(?<name>[A-Za-z0-9_]{1,16})\s+\[[^\]\r\n]{1,160}\]\s+logged in with entity id\s+\d+\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalLoginPattern();

    [GeneratedRegex(@"(?:^|:\s|\]\s)(?<name>[A-Za-z0-9_]{1,16}) lost connection:\s*[^\r\n]{0,256}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HistoricalDisconnectPattern();
}
