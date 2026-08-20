using System.Windows.Input;
using System.Windows.Markup;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// Transient, non-modal confirmation that something finished.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: tone icon, title, optional message, optional action, dismiss. Rendered on the overlay
/// surface by the shell's toast host, never by a page.
/// </para>
/// <para>
/// A toast reports a completed outcome. It must not be the only place a failure is reported and it
/// must not be used to ask a question - it can disappear before it is read.
/// </para>
/// </remarks>
[ContentProperty(nameof(ActionContent))]
public sealed class AppToast : Control
{
    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(AppTone), typeof(AppToast), new PropertyMetadata(AppTone.Info));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppToast), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(AppToast), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(AppToast), new PropertyMetadata(null));

    public static readonly DependencyProperty DismissCommandProperty = DependencyProperty.Register(
        nameof(DismissCommand), typeof(ICommand), typeof(AppToast), new PropertyMetadata(null));

    /// <summary>Semantic tone driving icon and token selection.</summary>
    public AppTone Tone
    {
        get => (AppTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>The completed outcome.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Optional supporting detail.</summary>
    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Optional follow-up, such as revealing the produced file.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>Always provide a dismiss path; a toast must be closable by keyboard.</summary>
    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }
}
