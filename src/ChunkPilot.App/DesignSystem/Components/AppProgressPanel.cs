using System.Windows.Input;

namespace ChunkPilot.App.DesignSystem.Components;

/// <summary>
/// Live progress for a long operation, with its identity and cancellation affordance.
/// </summary>
/// <remarks>
/// <para>
/// Anatomy: operation name, status line, progress track, optional operation identity, optional
/// cancel. Determinate when the total is known, indeterminate when it is not - never a fake
/// determinate bar that creeps to 90% and waits.
/// </para>
/// <para>
/// <see cref="Tone"/> lets a finished panel state the outcome truthfully: success, warning or
/// failure. A completed panel keeps the result visible rather than vanishing.
/// </para>
/// </remarks>
public sealed class AppProgressPanel : Control
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(AppProgressPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StatusTextProperty = DependencyProperty.Register(
        nameof(StatusText), typeof(string), typeof(AppProgressPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DetailTextProperty = DependencyProperty.Register(
        nameof(DetailText), typeof(string), typeof(AppProgressPanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(AppProgressPanel), new PropertyMetadata(0d));

    public static readonly DependencyProperty IsIndeterminateProperty = DependencyProperty.Register(
        nameof(IsIndeterminate), typeof(bool), typeof(AppProgressPanel), new PropertyMetadata(false));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(AppTone), typeof(AppProgressPanel), new PropertyMetadata(AppTone.Accent));

    public static readonly DependencyProperty CancelCommandProperty = DependencyProperty.Register(
        nameof(CancelCommand), typeof(ICommand), typeof(AppProgressPanel), new PropertyMetadata(null));

    /// <summary>What is running, in plain language.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Current step or truthful result.</summary>
    public string StatusText
    {
        get => (string)GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }

    /// <summary>Operation identity, target path or recovery point reference.</summary>
    public string DetailText
    {
        get => (string)GetValue(DetailTextProperty);
        set => SetValue(DetailTextProperty, value);
    }

    /// <summary>Completion percentage from 0 to 100. Meaningful only when determinate.</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>True when the total amount of work is not known.</summary>
    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    /// <summary>Tone of the track and status text; use to report the final outcome.</summary>
    public AppTone Tone
    {
        get => (AppTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    /// <summary>Set only when the operation genuinely supports cancellation.</summary>
    public ICommand? CancelCommand
    {
        get => (ICommand?)GetValue(CancelCommandProperty);
        set => SetValue(CancelCommandProperty, value);
    }
}
