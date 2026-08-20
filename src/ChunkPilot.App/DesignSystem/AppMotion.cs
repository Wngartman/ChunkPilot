namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Publishes the effective animation preference to the element tree.
/// </summary>
/// <remarks>
/// Reduced Motion is enforced twice on purpose. <c>Themes/Overlays/ReducedMotion.xaml</c> zeroes
/// every duration token, and this inherited flag lets a storyboard trigger decline to start at all.
/// Belt and braces, because a storyboard that runs with a zero duration still churns the
/// composition thread and can still land the user somewhere unexpected.
/// </remarks>
public static class AppMotion
{
    /// <summary>True when non-essential animation is permitted for this element subtree.</summary>
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AppMotion),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsEnabled(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsEnabledProperty, value);
    }
}
