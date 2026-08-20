namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Publishes accessibility state that shared templates need to react to.
/// </summary>
/// <remarks>
/// High contrast is handled mainly by swapping token brushes for Windows system brushes in
/// <c>Themes/Overlays/HighContrast.xaml</c>. This flag covers the cases a brush swap cannot: a
/// component that must show a border it normally omits, or replace a colour-only indicator with a
/// glyph. Minimal-border styling is attractive until the user turns high contrast on and the
/// boundaries disappear.
/// </remarks>
public static class AppAccessibility
{
    /// <summary>True when Windows reports a high-contrast theme (or a preview override is active).</summary>
    public static readonly DependencyProperty IsHighContrastProperty = DependencyProperty.RegisterAttached(
        "IsHighContrast", typeof(bool), typeof(AppAccessibility),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsHighContrast(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsHighContrastProperty);
    }

    public static void SetIsHighContrast(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsHighContrastProperty, value);
    }
}
