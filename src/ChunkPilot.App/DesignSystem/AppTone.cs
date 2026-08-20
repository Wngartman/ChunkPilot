namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// The semantic tone of a state-bearing component (badge, alert, toast, progress, row).
/// </summary>
/// <remarks>
/// Tone selects a token triple - fill, subtle surface and readable foreground - so the same
/// meaning looks the same everywhere. Tone alone never carries meaning: components that accept a
/// tone also require text, because colour is not accessible on its own.
/// </remarks>
public enum AppTone
{
    /// <summary>
    /// No claim is made. Use for unknown, not-configured, not-applicable and idle states.
    /// This is the default precisely because it never implies health.
    /// </summary>
    Neutral,

    /// <summary>Informational context the user did not ask about but should see.</summary>
    Info,

    /// <summary>A confirmed good outcome. Never use for "probably" or "should be".</summary>
    Success,

    /// <summary>A confirmed risk, degraded state, or an action needing review.</summary>
    Warning,

    /// <summary>A confirmed failure or a destructive action.</summary>
    Danger,

    /// <summary>Branded emphasis for the current selection or an in-progress operation.</summary>
    Accent
}
