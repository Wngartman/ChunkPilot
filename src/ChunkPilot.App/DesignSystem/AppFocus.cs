using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Clears transient keyboard focus when the user clicks empty page space.
/// </summary>
/// <remarks>
/// <para>
/// A desktop application drops its focus ring when you click away from a control. WPF does not: the
/// last control keeps keyboard focus, and with it its bright focus edge, until something else takes
/// it. A memory dropdown the user had opened, chosen from, and clicked away from stayed outlined as
/// though it were still being edited.
/// </para>
/// <para>
/// This clears <em>focus</em> and nothing else. The dropdown keeps its chosen value, the navigation
/// rail keeps its destination, the shell keeps its selected server. Focus chrome is a statement
/// about where typing goes; a chosen value is a statement about the server, and the two must not be
/// confused. Clicks that land on any interactive control are left completely alone, so this can
/// never take focus away from something the user is using.
/// </para>
/// </remarks>
public static class AppFocus
{
    /// <summary>Attaches background-click focus clearing to one window. Safe to call repeatedly.</summary>
    public static void ClearFocusOnBackgroundClick(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.PreviewMouseDown -= OnPreviewMouseDown;
        window.PreviewMouseDown += OnPreviewMouseDown;
    }

    /// <summary>
    /// True when a click on this element should leave keyboard focus exactly where it is.
    /// </summary>
    /// <remarks>
    /// Deliberately generous. Anything focusable, anything that behaves like a control, and anything
    /// inside one counts as interactive; only genuinely inert chrome - a card's background, a page
    /// margin, a label - falls through to the clear.
    /// </remarks>
    public static bool IsInteractive(DependencyObject? source)
    {
        var current = source;
        while (current is not null and not Window)
        {
            // The walk stops at the Window. A Window is focusable, so without this every click
            // anywhere would find it and nothing would ever count as background.
            // Fully qualified: the project references WinForms for the tray icon, and almost every
            // one of these names exists in both toolkits.
            if (current is System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.Primitives.Selector
                or System.Windows.Controls.ListBoxItem
                or System.Windows.Controls.MenuItem
                or System.Windows.Controls.Slider
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.PasswordBox
                or System.Windows.Controls.Primitives.Popup)
                return true;
            if (current is IInputElement { Focusable: true } and UIElement { IsEnabled: true })
                return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window || IsInteractive(e.OriginalSource as DependencyObject))
            return;
        // Not e.Handled: the click still reaches whatever it landed on. Only focus moves.
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(window, null);
    }
}
