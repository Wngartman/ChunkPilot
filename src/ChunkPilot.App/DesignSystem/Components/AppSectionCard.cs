namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// A titled group of related controls on one surface.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: optional icon, header, optional description, optional header actions, content.
/// One surface per idea. Nesting a section inside a section is prohibited - that is the "card wall"
/// the previous interface was made of, and it buries the actual content under chrome.
/// </para>
/// <para>
/// Use <see cref="IsCollapsible"/> for progressive disclosure of advanced controls. A collapsed
/// section must never hide a state the user needs in order to act safely.
/// </para>
/// </remarks>
public sealed class AppSectionCard : HeaderedContentControl
{
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(AppSectionCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(AppIconKind?), typeof(AppSectionCard), new PropertyMetadata(null));

    public static readonly DependencyProperty HeaderContentProperty = DependencyProperty.Register(
        nameof(HeaderContent), typeof(object), typeof(AppSectionCard), new PropertyMetadata(null));

    public static readonly DependencyProperty IsCollapsibleProperty = DependencyProperty.Register(
        nameof(IsCollapsible), typeof(bool), typeof(AppSectionCard), new PropertyMetadata(false));

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(AppSectionCard),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Optional supporting line under the header.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Optional leading icon.</summary>
    public AppIconKind? Icon
    {
        get => (AppIconKind?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Actions or status aligned to the trailing edge of the header.</summary>
    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }

    /// <summary>When true the header exposes a disclosure control.</summary>
    public bool IsCollapsible
    {
        get => (bool)GetValue(IsCollapsibleProperty);
        set => SetValue(IsCollapsibleProperty, value);
    }

    /// <summary>Disclosure state. Ignored when <see cref="IsCollapsible"/> is false.</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }
}
