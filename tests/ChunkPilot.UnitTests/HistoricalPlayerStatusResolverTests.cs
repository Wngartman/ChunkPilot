using ChunkPilot.Core;
using ChunkPilot.Infrastructure;

namespace ChunkPilot.UnitTests;

public sealed class HistoricalPlayerStatusResolverTests
{
    [Fact]
    public void Policy_orders_historical_ping_first_and_never_adds_Query_without_existing_consent()
    {
        var strategy = MinecraftPlayerStatusPolicy.For("b1.8.1", queryAlreadyEnabled: false,
            consoleAvailable: true);

        Assert.Equal(PlayerStatusSource.LegacySimpleStatus, strategy.OrderedSources[0]);
        Assert.Equal(PlayerStatusSource.LegacyExtendedStatus, strategy.OrderedSources[1]);
        Assert.Equal(PlayerStatusSource.ModernStatus, strategy.OrderedSources[2]);
        Assert.DoesNotContain(PlayerStatusSource.Query, strategy.OrderedSources);
        Assert.Contains(PlayerStatusSource.ConsoleList, strategy.OrderedSources);
        Assert.Contains("disabled", strategy.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Modern_and_historical_list_responses_preserve_exact_zero_and_unknown_maximum()
    {
        var timestamp = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var modern = HistoricalPlayerStatusResolver.TryParseConsoleList(
            "[Server thread/INFO]: There are 0 of a max of 20 players online:", checkedAt: timestamp);
        var historical = HistoricalPlayerStatusResolver.TryParseConsoleList(
            "2012-03-29 10:00:00 [INFO] Connected players: Alice, Bob", checkedAt: timestamp);

        Assert.NotNull(modern);
        Assert.Equal(0, modern.Online);
        Assert.Equal(20, modern.Maximum);
        Assert.True(modern.Exact);
        Assert.NotNull(historical);
        Assert.Equal(2, historical.Online);
        Assert.Null(historical.Maximum);
        Assert.True(historical.Exact);
    }

    [Theory]
    [InlineData("[Server thread/INFO]: Alice joined the game", "Alice", PlayerPresenceChangeKind.Joined)]
    [InlineData("2012-03-29 10:00:00 [INFO] Alice [/127.0.0.1:50000] logged in with entity id 12 at (0.0, 64.0, 0.0)", "Alice", PlayerPresenceChangeKind.Joined)]
    [InlineData("2012-03-29 10:01:00 [INFO] Alice lost connection: disconnect.endOfStream", "Alice", PlayerPresenceChangeKind.Left)]
    public void Presence_parser_accepts_only_narrow_managed_server_events(
        string line,
        string name,
        PlayerPresenceChangeKind kind)
    {
        var change = HistoricalPlayerStatusResolver.TryParsePresenceChange(line);

        Assert.NotNull(change);
        Assert.Equal(name, change.PlayerName);
        Assert.Equal(kind, change.Kind);
        Assert.Null(HistoricalPlayerStatusResolver.TryParsePresenceChange(
            "[Server thread/INFO]: <Mallory> Alice joined the game yesterday"));
    }

    [Fact]
    public void Resolver_prefers_exact_list_then_roster_then_fresh_exact_and_never_infers_zero()
    {
        var now = new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);
        var strategy = MinecraftPlayerStatusPolicy.For("1.2.5", false, true);
        var list = HistoricalPlayerStatusResolver.Resolve(new PlayerStatusResolutionRequest
        {
            Strategy = strategy,
            ConsoleListLine = "[INFO] Connected players: Alice, Bob",
            SessionRoster = ["Alice"],
            KnownMaximumPlayers = 20,
            NowUtc = now
        });
        var roster = HistoricalPlayerStatusResolver.Resolve(new PlayerStatusResolutionRequest
        {
            Strategy = strategy,
            SessionRoster = ["Alice", "alice", "Bob"],
            NowUtc = now
        });
        var last = HistoricalPlayerStatusResolver.Resolve(new PlayerStatusResolutionRequest
        {
            Strategy = strategy,
            LastExactEvidence = new PlayerStatusEvidence
            {
                Online = 4,
                Maximum = 20,
                Source = PlayerStatusSource.LegacySimpleStatus,
                Exact = true,
                CheckedAt = now.AddSeconds(-10)
            },
            NowUtc = now
        });
        var unknown = HistoricalPlayerStatusResolver.Resolve(new PlayerStatusResolutionRequest
        {
            Strategy = strategy,
            NowUtc = now
        });

        Assert.Equal(PlayerStatusSource.ConsoleList, list.Source);
        Assert.Equal(2, list.Online);
        Assert.Equal(PlayerStatusSource.ConsoleRoster, roster.Source);
        Assert.Equal(2, roster.Online);
        Assert.False(roster.Exact);
        Assert.Equal(PlayerStatusSource.LastExactStatus, last.Source);
        Assert.Equal(4, last.Online);
        Assert.False(last.Exact);
        Assert.Equal(PlayerStatusSource.StatusCheckFailed, unknown.Source);
        Assert.Null(unknown.Online);
        Assert.Null(unknown.Maximum);
        Assert.Contains("not inferred zero", unknown.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_evidence_is_ignored_until_Query_was_already_enabled()
    {
        var query = new PlayerStatusEvidence
        {
            Online = 1,
            Maximum = 10,
            Source = PlayerStatusSource.Query,
            Exact = true
        };

        var disabled = HistoricalPlayerStatusResolver.Resolve(new PlayerStatusResolutionRequest
        {
            Strategy = MinecraftPlayerStatusPolicy.For("b1.8", false, false),
            QueryEvidence = query
        });
        var enabled = HistoricalPlayerStatusResolver.Resolve(new PlayerStatusResolutionRequest
        {
            Strategy = MinecraftPlayerStatusPolicy.For("b1.8", true, false),
            QueryEvidence = query
        });

        Assert.Equal(PlayerStatusSource.StatusCheckFailed, disabled.Source);
        Assert.Equal(PlayerStatusSource.Query, enabled.Source);
        Assert.Equal(1, enabled.Online);
    }
}
