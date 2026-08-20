using System.Windows.Input;

namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Shared input affordances that WPF does not provide out of the box.
/// </summary>
/// <remarks>
/// A placeholder is a hint, never a label. It disappears while typing, so it cannot be the only
/// description of a field; pair it with a visible label or an accessible name.
/// </remarks>
public static class AppInput
{
    /// <summary>Hint text shown by the shared text input templates while the field is empty.</summary>
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(AppInput), new PropertyMetadata(string.Empty));

    public static string GetPlaceholder(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(PlaceholderProperty);
    }

    public static void SetPlaceholder(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PlaceholderProperty, value);
    }

    /// <summary>
    /// True while the user is driving the window from the keyboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WPF's <c>IsKeyboardFocused</c> is also true after a mouse click, because clicking focuses.
    /// A focus ring bound to it alone therefore draws a permanent outline around whatever the user
    /// last clicked - which is what made the navigation rail's selected row look like it had a
    /// stray purple box around it, and made keyboard focus indistinguishable from selection.
    /// </para>
    /// <para>
    /// This inherited attached property separates the two. Templates draw their focus ring only
    /// when the element is keyboard-focused <em>and</em> this is true. It is never used to hide
    /// focus from a keyboard user: any key that can move focus turns it back on before the focus
    /// actually moves, because the tracking handlers are preview-tunnelling.
    /// </para>
    /// </remarks>
    public static readonly DependencyProperty IsKeyboardModeProperty = DependencyProperty.RegisterAttached(
        "IsKeyboardMode", typeof(bool), typeof(AppInput),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsKeyboardMode(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsKeyboardModeProperty);
    }

    public static void SetIsKeyboardMode(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsKeyboardModeProperty, value);
    }

    /// <summary>
    /// Starts tracking keyboard versus pointer input for a window.
    /// </summary>
    /// <remarks>
    /// Handlers are preview-tunnelling and never mark the event handled, so they cannot change
    /// input behaviour - they only observe it. Only keys that can move focus flip the mode, so
    /// typing into a text box does not make focus rings appear across the window.
    /// </remarks>
    public static void TrackInputMode(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.PreviewKeyDown += (sender, e) =>
        {
            if (MovesFocus(e.Key))
                SetIsKeyboardMode(window, true);
        };
        window.PreviewMouseDown += (sender, e) => SetIsKeyboardMode(window, false);
    }

    private static bool MovesFocus(Key key) => key
        is Key.Tab or Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Home or Key.End or Key.PageUp or Key.PageDown;
}
