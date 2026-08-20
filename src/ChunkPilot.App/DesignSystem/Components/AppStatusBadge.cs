namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// A compact, truthful state label.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: tone dot or icon, then text. The text is mandatory: a coloured dot on its own is not
/// readable to a colour-blind or screen-reader user, and it is exactly how an interface ends up
/// claiming "green means fine" without evidence.
/// </para>
/// <para>
/// Default tone is <see cref="AppTone.Neutral"/> so an unset badge reads as "unknown" rather than
/// as healthy.
/// </para>
/// </remarks>
public sealed class AppStatusBadge : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AppStatusBadge), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(AppTone), typeof(AppStatusBadge), new PropertyMetadata(AppTone.Neutral));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(AppIconKind?), typeof(AppStatusBadge), new PropertyMetadata(null));

    public static readonly DependencyProperty IsSubtleProperty = DependencyProperty.Register(
        nameof(IsSubtle), typeof(bool), typeof(AppStatusBadge), new PropertyMetadata(false));

    /// <summary>The state, written out. Required.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Semantic tone driving the badge tokens.</summary>
    public AppTone Tone
    {
        get => (AppTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>Optional glyph replacing the tone dot, for states that benefit from a symbol.</summary>
    public AppIconKind? Icon
    {
        get => (AppIconKind?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Removes the tinted background for use inside dense rows.</summary>
    public bool IsSubtle
    {
        get => (bool)GetValue(IsSubtleProperty);
        set => SetValue(IsSubtleProperty, value);
    }
}
