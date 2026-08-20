using ChunkPilot.App.DesignSystem;

namespace ChunkPilot.App;

/// <summary>
/// A navigation destination.
/// </summary>
/// <param name="Page">Stable semantic identifier. Never a tab index; persisted settings depend on it.</param>
/// <param name="Label">The rail label, also used as the accessible name and the page title.</param>
/// <param name="Description">Plain-language tooltip, and the only label available in Compact mode.</param>
/// <param name="Icon">Semantic icon; the design system decides which glyph that is.</param>
public sealed record NavigationItem(string Page, string Label, string Description, AppIconKind Icon);
