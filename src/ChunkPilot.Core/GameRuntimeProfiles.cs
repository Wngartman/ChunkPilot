using System.Text.RegularExpressions;

namespace ChunkPilot.Core;

/// <summary>
/// Central game-specific behavior used by the shared Agent lifecycle. Adding a game does not add a
/// second process owner, operation queue, backup engine, or networking stack.
/// </summary>
public sealed record GameServerRuntimeProfile
{
    public required ServerGameKind GameKind { get; init; }
    public required string DisplayName { get; init; }
    public bool UsesMinecraftStatusProtocol { get; init; }
    public bool TracksMinecraftJoinLeaveLines { get; init; }
    public bool FreezeAutomaticSavingDuringBackup { get; init; }
    public string SaveConfirmationPattern { get; init; } = "";
    public string UnavailablePlayerStatusDetail { get; init; } = "Player status is unavailable.";
}

public static class GameServerRuntimeProfiles
{
    private static readonly GameServerRuntimeProfile Minecraft = new()
    {
        GameKind = ServerGameKind.Minecraft,
        DisplayName = "Minecraft",
        UsesMinecraftStatusProtocol = true,
        TracksMinecraftJoinLeaveLines = true,
        FreezeAutomaticSavingDuringBackup = true,
        SaveConfirmationPattern = @"Saved the game|Saved the world|Saving is already turned on|Saved.*chunks",
        UnavailablePlayerStatusDetail = "Player status is unavailable; no count has been inferred as zero."
    };

    private static readonly GameServerRuntimeProfile Terraria = new()
    {
        GameKind = ServerGameKind.Terraria,
        DisplayName = "Terraria",
        UsesMinecraftStatusProtocol = false,
        TracksMinecraftJoinLeaveLines = false,
        FreezeAutomaticSavingDuringBackup = false,
        // Terraria confirms the native `save` command with this bounded family of messages. An exact
        // certification fixture must still prove the concrete line before public support is claimed.
        SaveConfirmationPattern = @"World saved|Saving world|Backing up world file",
        UnavailablePlayerStatusDetail = "Terraria player status requires current-session console evidence; no count has been inferred as zero."
    };

    public static GameServerRuntimeProfile For(ServerDefinition definition) =>
        definition.GameKind == ServerGameKind.Terraria ? Terraria : Minecraft;

    public static bool IsSaveConfirmation(ServerDefinition definition, string line)
    {
        var pattern = string.IsNullOrWhiteSpace(definition.SaveConfirmationPattern)
            ? For(definition).SaveConfirmationPattern
            : definition.SaveConfirmationPattern;
        return Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
