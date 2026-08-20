using System.Windows.Input;
using System.Windows.Markup;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// An inline, non-modal message about the current surface: information, a risk, or a failure.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: tone icon, title, message, optional actions, optional dismiss. Stays in the layout flow
/// so it cannot be missed and cannot steal focus.
/// </para>
/// <para>
/// This is the replacement for a default <c>MessageBox</c> on a product error path. An alert must
/// say what happened, what is still true, and what the user can safely do next - a modal box with
/// an OK button does none of that.
/// </para>
/// </remarks>
[ContentProperty(nameof(ActionContent))]
public sealed class AppAlert : Control
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(AppTone), typeof(AppAlert), new PropertyMetadata(AppTone.Info));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppAlert), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AppAlert), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(AppAlert), new PropertyMetadata(null));

    public static readonly DependencyProperty DetailContentProperty = DependencyProperty.Register(
        nameof(DetailContent), typeof(object), typeof(AppAlert), new PropertyMetadata(null));

    public static readonly DependencyProperty DismissCommandProperty = DependencyProperty.Register(
        nameof(DismissCommand), typeof(ICommand), typeof(AppAlert), new PropertyMetadata(null));

    /// <summary>Semantic tone driving icon and token selection.</summary>
    public AppTone Tone
    {
        get => (AppTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>Short summary of what is true.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Plain-language explanation and the safe next step.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Recovery actions offered by the alert.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>Technical detail kept available but out of the way.</summary>
    public object? DetailContent
    {
        get => GetValue(DetailContentProperty);
        set => SetValue(DetailContentProperty, value);
    }

    /// <summary>When set, the alert shows a dismiss control. Errors normally are not dismissible.</summary>
    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }
}
