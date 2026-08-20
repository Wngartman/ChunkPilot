using System.Windows.Markup;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// The single header treatment for every destination: what this page is, what it is for, and the
/// one primary action.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: title, one-line explanation, optional status slot, optional secondary actions, one
/// primary action. The header collapses its description and stacks its action groups in Compact
/// mode; the primary action survives at every width.
/// </para>
/// <para>
/// The description is plain language, not a restatement of the title. If a page needs two primary
/// actions, the page has two jobs.
/// </para>
/// </remarks>
[ContentProperty(nameof(PrimaryContent))]
public sealed class AppPageHeader : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(AppPageHeader), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(AppIconKind?), typeof(AppPageHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty PrimaryContentProperty = DependencyProperty.Register(
        nameof(PrimaryContent), typeof(object), typeof(AppPageHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty SecondaryContentProperty = DependencyProperty.Register(
        nameof(SecondaryContent), typeof(object), typeof(AppPageHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty StatusContentProperty = DependencyProperty.Register(
        nameof(StatusContent), typeof(object), typeof(AppPageHeader), new PropertyMetadata(null));

    /// <summary>The destination name. Matches the navigation label.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>One plain-language line explaining what the user can do here.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Optional destination icon, matching the navigation row.</summary>
    public AppIconKind? Icon
    {
        get => (AppIconKind?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>The single primary action for this destination.</summary>
    public object? PrimaryContent
    {
        get => GetValue(PrimaryContentProperty);
        set => SetValue(PrimaryContentProperty, value);
    }

    /// <summary>Supporting actions, shown before the primary action.</summary>
    public object? SecondaryContent
    {
        get => GetValue(SecondaryContentProperty);
        set => SetValue(SecondaryContentProperty, value);
    }

    /// <summary>Truthful state for the destination, usually a status badge.</summary>
    public object? StatusContent
    {
        get => GetValue(StatusContentProperty);
        set => SetValue(StatusContentProperty, value);
    }
}
