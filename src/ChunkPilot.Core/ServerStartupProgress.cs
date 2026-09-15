namespace ChunkPilot.Core;

public enum ServerStartupStage
{
    Preflight, ProcessStarted, LoadingMods, PreparingWorld, WaitingForReadiness,
    Ready, Failed, Cancelled, RestartSaving, RestartStopping, RestartDelay
}

/// <summary>Evidence from one native Start/Restart attempt; never a simulated percentage or ETA.</summary>
public sealed record ServerStartupProgress
{
    public Guid ServerId { get; init; }
    public Guid AttemptId { get; init; }
    public ServerStartupStage Stage { get; init; }
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? LastOutputAt { get; init; }
    public int? ProcessId { get; init; }
    public bool IsActive { get; init; }
}

public static class ServerStartupProgressPolicy
{
    /// <summary>Only explicit phase markers qualify. Ordinary output means output, not progress.</summary>
    public static ServerStartupStage? StageFromOutput(string line)
    {
        if (line.Contains("Preparing level ", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Preparing start region", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Preparing spawn area", StringComparison.OrdinalIgnoreCase))
            return ServerStartupStage.PreparingWorld;
        if (line.Contains("Loading ", StringComparison.OrdinalIgnoreCase) &&
            line.Contains(" mods", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Mod loading:", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Constructing mods", StringComparison.OrdinalIgnoreCase))
            return ServerStartupStage.LoadingMods;
        return null;
    }

    public static string Title(ServerStartupStage stage) => stage switch
    {
        ServerStartupStage.Preflight => "Checking launch settings",
        ServerStartupStage.ProcessStarted => "Server process started",
        ServerStartupStage.LoadingMods => "Server reports loading mods",
        ServerStartupStage.PreparingWorld => "Server reports preparing the world",
        ServerStartupStage.WaitingForReadiness => "Waiting for the server to report ready",
        ServerStartupStage.Ready => "Server reported ready",
        ServerStartupStage.Failed => "Server did not start successfully",
        ServerStartupStage.Cancelled => "Startup wait cancelled",
        ServerStartupStage.RestartSaving => "Saving before restart",
        ServerStartupStage.RestartStopping => "Stopping before restart",
        ServerStartupStage.RestartDelay => "Waiting for the configured restart delay",
        _ => "Starting server"
    };
}
