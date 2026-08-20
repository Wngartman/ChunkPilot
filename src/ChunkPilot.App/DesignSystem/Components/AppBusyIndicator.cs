namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// The single busy affordance: a small arc that rotates while work is in flight.
/// </summary>
/// <remarks>
/// The rotation only starts when <see cref="AppMotion.IsEnabledProperty"/> is true for this
/// subtree. With Reduced Motion the arc renders statically and the accompanying text carries the
/// state, so nothing depends on animation to be understood.
/// </remarks>
public sealed class AppBusyIndicator : Control
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(AppBusyIndicator), new PropertyMetadata(true));

    /// <summary>False hides the indicator entirely, for a surface that finished.</summary>
    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }
}
