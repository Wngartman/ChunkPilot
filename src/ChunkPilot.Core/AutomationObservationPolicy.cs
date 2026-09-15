namespace ChunkPilot.Core;

/// <summary>Unattended player actions require fresh, exact evidence from one process attempt.</summary>
public static class AutomationObservationPolicy
{
    public static bool SameRunningAttempt(ServerSnapshot current, ServerSnapshot? prior) =>
        prior is not null && current.Definition.Id == prior.Definition.Id &&
        current.State == ServerState.Running && prior.State == ServerState.Running &&
        current.RootProcessId is > 0 && current.RootProcessId == prior.RootProcessId &&
        current.RootProcessCreationTicks > 0 && current.RootProcessCreationTicks == prior.RootProcessCreationTicks;

    public static bool HasFreshCount(ServerSnapshot snapshot, DateTimeOffset now) =>
        snapshot.State == ServerState.Running && snapshot.OnlinePlayers is >= 0 &&
        snapshot.PlayerStatus is { Exact: true, Online: >= 0 } evidence &&
        evidence.Online == snapshot.OnlinePlayers &&
        evidence.CheckedAt <= now && now - evidence.CheckedAt <= TimeSpan.FromSeconds(15);

    public static bool KnownEmpty(ServerSnapshot current, ServerSnapshot original, DateTimeOffset now) =>
        SameRunningAttempt(current, original) && HasFreshCount(current, now) && current.OnlinePlayers == 0;

    public static bool PlayerTransitionMatches(AutomationRecipe recipe, ServerSnapshot current,
        ServerSnapshot? prior, DateTimeOffset now)
    {
        if (!SameRunningAttempt(current, prior) || !HasFreshCount(current, now) || !HasFreshCount(prior!, now))
            return false;
        var players = current.OnlinePlayers!.Value;
        var priorPlayers = prior!.OnlinePlayers!.Value;
        return recipe.Trigger switch
        {
            AutomationTriggerKind.PlayerJoined => players > priorPlayers,
            AutomationTriggerKind.FirstPlayerJoined => priorPlayers == 0 && players > 0,
            AutomationTriggerKind.LastPlayerLeft => priorPlayers > 0 && players == 0,
            AutomationTriggerKind.PlayerCountThreshold => int.TryParse(recipe.TriggerValue, out var threshold) &&
                threshold > 0 && players >= threshold && priorPlayers < threshold,
            _ => false
        };
    }
}
