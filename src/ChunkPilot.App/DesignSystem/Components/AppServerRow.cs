using System.Windows.Markup;
using System.Windows.Media;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// The canonical presentation of one server in any list: switcher, dashboard or picker.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: state indicator, name, subtitle (software and version), optional state text, trailing
/// slot for measured values or actions. Selection is shown with an accent edge and a surface change,
/// never with colour alone.
/// </para>
/// <para>
/// Deliberately built from plain strings and an <see cref="AppTone"/>. The design system does not
/// reference server models, so a row cannot start reaching into lifecycle state or invent a metric
/// it has not been given.
/// </para>
/// </remarks>
[ContentProperty(nameof(TrailingContent))]
public sealed class AppServerRow : Control
{
    public static readonly DependencyProperty ServerNameProperty = DependencyProperty.Register(
        nameof(ServerName), typeof(string), typeof(AppServerRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(AppServerRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(
        nameof(StateText), typeof(string), typeof(AppServerRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(AppTone), typeof(AppServerRow), new PropertyMetadata(AppTone.Neutral));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(AppServerRow), new PropertyMetadata(false));

    public static readonly DependencyProperty TrailingContentProperty = DependencyProperty.Register(
        nameof(TrailingContent), typeof(object), typeof(AppServerRow), new PropertyMetadata(null));

    public static readonly DependencyProperty IconSourceProperty = DependencyProperty.Register(
        nameof(IconSource), typeof(ImageSource), typeof(AppServerRow), new PropertyMetadata(null));

    /// <summary>
    /// The server name as the user named it. Deliberately not called <c>Name</c>, which WPF
    /// reserves for the element name scope.
    /// </summary>
    public string ServerName
    {
        get => (string)GetValue(ServerNameProperty);
        set => SetValue(ServerNameProperty, value);
    }

    /// <summary>Software and version, or the folder when that is more useful.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Lifecycle state, written out. Required whenever a tone is set.</summary>
    public string StateText
    {
        get => (string)GetValue(StateTextProperty);
        set => SetValue(StateTextProperty, value);
    }

    /// <summary>Tone of the state indicator.</summary>
    public AppTone Tone
    {
        get => (AppTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>True when this row is the current server.</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>Measured values or contextual actions on the trailing edge.</summary>
    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    /// <summary>Optional detached server-icon image. Mutable file paths never enter the template.</summary>
    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }
}
