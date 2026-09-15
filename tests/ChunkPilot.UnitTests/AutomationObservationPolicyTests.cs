using ChunkPilot.Core;

namespace ChunkPilot.UnitTests;

public sealed class AutomationObservationPolicyTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-15T03:20:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly ServerDefinition Definition = new() { Id = Guid.NewGuid(), Name = "Synthetic automation" };

    [Theory]
    [InlineData(AutomationTriggerKind.LastPlayerLeft, 1, 0, true)]
    [InlineData(AutomationTriggerKind.LastPlayerLeft, 1, null, false)]
    [InlineData(AutomationTriggerKind.LastPlayerLeft, null, 0, false)]
    [InlineData(AutomationTriggerKind.FirstPlayerJoined, null, 1, false)]
    [InlineData(AutomationTriggerKind.FirstPlayerJoined, 0, 1, true)]
    [InlineData(AutomationTriggerKind.PlayerJoined, 1, 2, true)]
    [InlineData(AutomationTriggerKind.PlayerCountThreshold, 1, 2, true)]
    public void Unknown_counts_are_not_player_transitions(AutomationTriggerKind trigger, int? before, int? after, bool expected)
    {
        var recipe = new AutomationRecipe { Trigger = trigger, TriggerValue = "2" };
        Assert.Equal(expected, AutomationObservationPolicy.PlayerTransitionMatches(recipe, Snapshot(after), Snapshot(before), Now));
    }

    [Fact]
    public void Stale_nonexact_future_and_other_attempt_counts_cannot_stop_a_server()
    {
        var original = Snapshot(0);
        Assert.True(AutomationObservationPolicy.KnownEmpty(original, original, Now));
        foreach (var current in new[]
        {
            Snapshot(null), Snapshot(1), original with { State = ServerState.Starting },
            original with { RootProcessCreationTicks = 11 }, original with { RootProcessId = 50 },
            original with { PlayerStatus = original.PlayerStatus! with { Exact = false } },
            original with { PlayerStatus = original.PlayerStatus! with { CheckedAt = Now.AddSeconds(-16) } },
            original with { PlayerStatus = original.PlayerStatus! with { CheckedAt = Now.AddSeconds(1) } }
        }) Assert.False(AutomationObservationPolicy.KnownEmpty(current, original, Now));
    }

    private static ServerSnapshot Snapshot(int? players) => new()
    {
        Definition = Definition, State = ServerState.Running, RootProcessId = 42, RootProcessCreationTicks = 10,
        OnlinePlayers = players, PlayerStatus = new() { Online = players, Exact = players.HasValue, CheckedAt = Now }
    };
}
