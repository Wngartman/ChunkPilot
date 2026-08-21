namespace ChunkPilot.Core;

public sealed class LifecycleStateMachine
{
    private static readonly Dictionary<ServerState, HashSet<ServerState>> Allowed =
        new Dictionary<ServerState, HashSet<ServerState>>
        {
            [ServerState.Stopped] = [ServerState.Starting, ServerState.Restoring, ServerState.BackingUp, ServerState.Unknown],
            [ServerState.Starting] = [ServerState.Running, ServerState.Stopping, ServerState.Crashed, ServerState.Unresponsive],
            [ServerState.Running] = [ServerState.Saving, ServerState.Stopping, ServerState.Restarting, ServerState.BackingUp, ServerState.Crashed, ServerState.Unresponsive],
            [ServerState.Saving] = [ServerState.Running, ServerState.Stopping, ServerState.Restarting, ServerState.Crashed, ServerState.Unresponsive],
            [ServerState.Stopping] = [ServerState.Stopped, ServerState.Unresponsive, ServerState.Crashed],
            [ServerState.Restarting] = [ServerState.Saving, ServerState.Stopping, ServerState.Stopped, ServerState.Starting, ServerState.Running, ServerState.Crashed, ServerState.Unresponsive],
            [ServerState.BackingUp] = [ServerState.Running, ServerState.Stopped, ServerState.Crashed],
            [ServerState.Restoring] = [ServerState.Stopped, ServerState.Crashed],
            [ServerState.Crashed] = [ServerState.Starting, ServerState.Stopped, ServerState.BackingUp, ServerState.Restoring],
            [ServerState.Unresponsive] = [ServerState.Stopping, ServerState.Stopped, ServerState.Crashed],
            [ServerState.Unknown] = [ServerState.Stopped, ServerState.Running, ServerState.Crashed]
        };

    private readonly object gate = new();
    private ServerState state;

    public LifecycleStateMachine(ServerState initial = ServerState.Stopped) => state = initial;

    public ServerState State
    {
        get { lock (gate) return state; }
    }

    public bool CanTransitionTo(ServerState next)
    {
        lock (gate)
            return next == state || Allowed.TryGetValue(state, out var states) && states.Contains(next);
    }

    public void TransitionTo(ServerState next)
    {
        lock (gate)
        {
            if (next == state)
                return;
            if (!Allowed.TryGetValue(state, out var states) || !states.Contains(next))
                throw new InvalidOperationException($"Invalid lifecycle transition: {state} -> {next}.");
            state = next;
        }
    }
}

/// <summary>
/// Decides whether Agent startup has explicit authority to launch a server.
/// Runtime evidence from a previous Windows session is deliberately not launch intent.
/// </summary>
public static class StartupRestorationPolicy
{
    public static bool IsAuthorized(ServerDefinition definition, ServerRunningState? state) =>
        definition.AutoStart ||
        state?.AutostartMode is AutostartMode.AgentStart or AutostartMode.WindowsLoginWithDelay;

    public static AutostartMode EffectiveMode(ServerDefinition definition, ServerRunningState? state)
    {
        if (definition.AutoStart)
            return AutostartMode.AgentStart;
        return state?.AutostartMode is AutostartMode.AgentStart or AutostartMode.WindowsLoginWithDelay
            ? state.AutostartMode
            : AutostartMode.Never;
    }
}
