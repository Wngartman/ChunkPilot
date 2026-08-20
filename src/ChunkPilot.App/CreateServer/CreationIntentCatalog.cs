using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// One top-level choice on the first step: what the user wants to run, in their words.
/// </summary>
/// <param name="Intent">The domain value this card selects.</param>
/// <param name="Title">Plain-language name. Never an internal preset identifier.</param>
/// <param name="Description">One line explaining who this is for.</param>
/// <param name="Icon">Semantic icon from the shared vocabulary.</param>
/// <param name="HelpText">The longer explanation shown in the details panel.</param>
/// <param name="PreviewAvailability">
/// What this preview can and cannot show for this intent. Truthful, and never implies that choosing
/// the intent installs anything.
/// </param>
/// <param name="IsFullyPreviewable">
/// False when the preview deliberately shows a summary instead of the real controls, so the card can
/// say so rather than appearing finished.
/// </param>
public sealed record CreationIntentCard(
    CreationIntent Intent,
    string Title,
    string Description,
    AppIconKind Icon,
    string HelpText,
    string PreviewAvailability,
    bool IsFullyPreviewable)
{
    /// <summary>Composed name for assistive technology, so the card reads as one thing.</summary>
    public string AutomationName => $"{Title}. {Description}";
}

/// <summary>
/// The six intents Create Server v2 offers, with their product copy.
/// </summary>
/// <remarks>
/// <para>
/// This is product vocabulary, not preview data: the same titles, descriptions and icons carry
/// forward when the wizard is connected to the real creation pipeline. Only
/// <see cref="CreationIntentCard.PreviewAvailability"/> is specific to this milestone.
/// </para>
/// <para>
/// "Add an existing server" is deliberately absent. Importing a folder someone already owns is a
/// separate, safety-sensitive workflow with its own read-only guarantees, and it keeps its own
/// window.
/// </para>
/// </remarks>
public static class CreationIntentCatalog
{
    public static IReadOnlyList<CreationIntentCard> Cards { get; } =
    [
        new(CreationIntent.Vanilla,
            "Just Minecraft",
            "The official game, exactly as Mojang ships it.",
            AppIconKind.World,
            "Everyone joins with an ordinary Minecraft client and nothing extra to install. This is the "
            + "safest starting point, and you can add plugins or mods later without starting again.",
            "This preview shows the real version choices and the checks ChunkPilot would run.",
            IsFullyPreviewable: true),

        new(CreationIntent.Plugins,
            "Server with plugins",
            "Add features on the server. Players still use an ordinary client.",
            AppIconKind.Plugin,
            "A plugin-capable server runs extra code on your machine only. Homes, land claims, mini-games "
            + "and moderation tools all work this way, and nobody has to change their Minecraft "
            + "installation to join.",
            "This preview shows the plugin-capable implementations and how they relate to a Minecraft version.",
            IsFullyPreviewable: true),

        new(CreationIntent.Mods,
            "Server with mods",
            "New blocks, mobs and machines. Everyone needs the same mods.",
            AppIconKind.Mod,
            "Mods change the game itself, so the server and every player must run the same loader and the "
            + "same mod versions. ChunkPilot tracks the loader and Minecraft version together so a "
            + "mismatch is caught before anyone tries to join.",
            "This preview shows loader choices including a combination ChunkPilot rejects.",
            IsFullyPreviewable: true),

        new(CreationIntent.Modpack,
            "Play a modpack",
            "A ready-made collection someone else assembled and tested.",
            AppIconKind.Box,
            "A modpack bundles a loader, mods and configuration into one release. Some publish a dedicated "
            + "server pack and some publish a client pack only; ChunkPilot says which, and never pretends "
            + "it can turn a client pack into a server.",
            "This preview browses built-in example projects, including ones that cannot be installed.",
            IsFullyPreviewable: true),

        new(CreationIntent.Crossplay,
            "Java and Bedrock together",
            "Let phone, console and Windows edition players join a Java server.",
            AppIconKind.People,
            "A Java server with a crossplay layer accepts Bedrock clients as well. The two editions connect "
            + "differently, so ChunkPilot keeps their addresses separate and never claims a server is "
            + "reachable from the internet.",
            "This preview explains the components and the networking caveat. Nothing is installed or opened.",
            IsFullyPreviewable: true),

        new(CreationIntent.Advanced,
            "Set it up myself",
            "Choose your own files, runtime and launch settings.",
            AppIconKind.Options,
            "Expert control over the server files, the Java runtime and the launch command. Custom choices "
            + "are yours to verify: once you replace what ChunkPilot resolved, it can no longer confirm the "
            + "combination works.",
            "This preview describes the expert categories only. The editors themselves arrive in a later update.",
            IsFullyPreviewable: false)
    ];

    /// <summary>Looks up one intent's card. Every enum member has one.</summary>
    public static CreationIntentCard For(CreationIntent intent) =>
        Cards.First(card => card.Intent == intent);
}
