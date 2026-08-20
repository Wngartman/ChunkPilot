using ChunkPilot.Core;

namespace ChunkPilot.App.Presentation;

/// <summary>One choice for a constrained <c>server.properties</c> value.</summary>
/// <param name="Value">Exactly what is written to the file, in the file's own spelling.</param>
/// <param name="Label">What the user reads.</param>
/// <remarks>
/// <see cref="ToString"/> is the label, not the record's generated form. A record's default
/// <c>ToString</c> prints its own shape - <c>ServerPropertyChoice { Value = normal, Label = Normal }</c> -
/// and any control that falls back to it renders that at the user. The Configuration dropdowns did
/// exactly that. The templates below bind <see cref="Label"/> explicitly; this makes the fallback
/// harmless as well as unused.
/// </remarks>
public sealed record ServerPropertyChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// How <c>server.properties</c> values are shown, and which of them need a restart.
/// </summary>
/// <remarks>
/// <para>
/// The file's vocabulary is lower case - <c>normal</c>, <c>survival</c> - and it stays that way on
/// disk. Presenting it raw made the Configuration card read like a config dump; presenting a title
/// case label while writing the exact stored value is the whole of the mapping. It lives here, once,
/// rather than as a converter repeated per control.
/// </para>
/// <para>
/// Nothing here invents a property. Every key is one ChunkPilot already reads and writes through the
/// Agent's atomic <c>server.properties</c> path.
/// </para>
/// </remarks>
public static class ServerPropertyPresentation
{
    /// <summary>Difficulty choices, in the order Minecraft orders them.</summary>
    public static IReadOnlyList<ServerPropertyChoice> Difficulties { get; } =
        ServerPropertyValidation.Difficulties.Select(value => new ServerPropertyChoice(value, TitleCase(value))).ToArray();

    /// <summary>Game mode choices, in the order Minecraft orders them.</summary>
    public static IReadOnlyList<ServerPropertyChoice> GameModes { get; } =
        ServerPropertyValidation.GameModes.Select(value => new ServerPropertyChoice(value, TitleCase(value))).ToArray();

    /// <summary>
    /// The properties a running server does not pick up until it is restarted.
    /// </summary>
    /// <remarks>
    /// Stated so the card can say so, never acted on: ChunkPilot does not restart a server because a
    /// setting changed. Difficulty, game mode, PvP and the whitelist flag can take effect through
    /// live server commands in other workflows. The Vanilla MOTD is loaded from server.properties
    /// at startup, so this settings-file editor truthfully marks it as restart-required.
    /// </remarks>
    public static IReadOnlySet<string> RestartRequiredKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "motd",
        "server-port",
        "max-players",
        "online-mode",
        "view-distance",
        "simulation-distance",
        "allow-flight",
        "enable-command-block",
        "spawn-protection",
        "hardcore",
        "force-gamemode",
        "player-idle-timeout"
    };

    /// <summary>The label for a stored value, or the stored value when it is not a known choice.</summary>
    public static string LabelFor(IReadOnlyList<ServerPropertyChoice> choices, string value)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return choices.FirstOrDefault(choice =>
            string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase))?.Label ?? value;
    }

    private static string TitleCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];
}
