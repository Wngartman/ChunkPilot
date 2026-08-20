using System.Windows.Markup;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// Explains why a surface has no content and offers the one real next step.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: icon, title, explanation, optional action. Used for first run, a filter with no
/// matches, and a capability that genuinely does not apply to the selected server.
/// </para>
/// <para>
/// An empty state must not offer an action that is not implemented, and must never be replaced by a
/// fixed-height empty table or placeholder rows. "No backups yet" plus a real backup button is
/// honest; a greyed-out fake timeline is not.
/// </para>
/// </remarks>
[ContentProperty(nameof(ActionContent))]
public sealed class AppEmptyState : Control
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(AppIconKind), typeof(AppEmptyState), new PropertyMetadata(AppIconKind.Info));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppEmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AppEmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(AppEmptyState), new PropertyMetadata(null));

    /// <summary>Illustrative icon matching the subject of the surface.</summary>
    public AppIconKind Icon
    {
        get => (AppIconKind)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>What is missing, stated as fact.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Why it is missing and what happens if the user acts.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>A real, enabled next action. Omit rather than disable.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}
