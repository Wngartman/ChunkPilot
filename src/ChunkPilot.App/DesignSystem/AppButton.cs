namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Shared button affordances applied by the button templates.
/// </summary>
/// <remarks>
/// <para>
/// <c>AppButton.Icon</c> lets a view write <c>ds:AppButton.Icon="Play"</c> instead of hand-building a
/// horizontal stack of an icon and a label. The design system then owns the glyph size, the gap and
/// the colour relationship in one place, which is the difference between forty consistent buttons
/// and forty nearly-consistent ones.
/// </para>
/// <para>
/// Icons supplement labels. They never replace the label on an unfamiliar or destructive action -
/// use <c>AppIconButton</c> with a tooltip and an accessible name only for well-known, repeated
/// actions such as copy or dismiss.
/// </para>
/// </remarks>
public static class AppButton
{
    /// <summary>Optional leading icon rendered by the shared button templates.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(AppIconKind?), typeof(AppButton), new PropertyMetadata(null));

    public static AppIconKind? GetIcon(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (AppIconKind?)element.GetValue(IconProperty);
    }

    public static void SetIcon(DependencyObject element, AppIconKind? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IconProperty, value);
    }
}
