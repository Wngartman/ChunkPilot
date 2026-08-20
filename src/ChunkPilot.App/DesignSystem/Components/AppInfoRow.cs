using System.Windows.Markup;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// A label and value pair, with an honest representation of "not known".
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: label, value, optional trailing action such as copy. The workhorse of every detail
/// surface in ChunkPilot.
/// </para>
/// <para>
/// <see cref="IsUnknown"/> renders the muted "unknown" treatment instead of the value. This exists
/// so a missing value cannot silently render as an empty string that looks like a real answer -
/// blank space is how an interface accidentally claims a port is closed or an address is reachable.
/// </para>
/// </remarks>
[ContentProperty(nameof(ActionContent))]
public sealed class AppInfoRow : Control
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(AppInfoRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(AppInfoRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsUnknownProperty = DependencyProperty.Register(
        nameof(IsUnknown), typeof(bool), typeof(AppInfoRow), new PropertyMetadata(false));

    public static readonly DependencyProperty UnknownTextProperty = DependencyProperty.Register(
        nameof(UnknownText), typeof(string), typeof(AppInfoRow), new PropertyMetadata("Unknown"));

    public static readonly DependencyProperty IsMonospacedProperty = DependencyProperty.Register(
        nameof(IsMonospaced), typeof(bool), typeof(AppInfoRow), new PropertyMetadata(false));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(AppInfoRow), new PropertyMetadata(null));

    /// <summary>What the value describes.</summary>
    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>The confirmed value. Ignored when <see cref="IsUnknown"/> is true.</summary>
    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>True when ChunkPilot has not confirmed a value.</summary>
    public bool IsUnknown
    {
        get => (bool)GetValue(IsUnknownProperty);
        set => SetValue(IsUnknownProperty, value);
    }

    /// <summary>Wording for the unknown case, for example "Not configured" or "Unavailable".</summary>
    public string UnknownText
    {
        get => (string)GetValue(UnknownTextProperty);
        set => SetValue(UnknownTextProperty, value);
    }

    /// <summary>Use for paths, addresses, hashes and versions so characters stay distinguishable.</summary>
    public bool IsMonospaced
    {
        get => (bool)GetValue(IsMonospacedProperty);
        set => SetValue(IsMonospacedProperty, value);
    }

    /// <summary>Trailing action, typically a copy icon button.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}
