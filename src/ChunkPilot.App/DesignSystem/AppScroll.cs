using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Boundary-aware mouse-wheel routing for scroll regions inside a scrolling page.
/// </summary>
/// <remarks>
/// <para>
/// WPF's <see cref="ScrollViewer"/> marks every wheel event handled, whether or not it could do
/// anything with it. A file list, a table with four rows, an empty backup grid or a text editor
/// therefore swallows the wheel: the page underneath stops moving the moment the pointer crosses the
/// control, and the only way out is to find a margin. That is the dead zone the workspace had.
/// </para>
/// <para>
/// The correction is a class handler that runs before the ScrollViewer's own, and does exactly one
/// thing: if this scroller cannot move any further in the direction asked for, the event is re-raised
/// on the nearest ancestor that can. A scroller that <em>can</em> scroll is left completely alone, so
/// a list still scrolls under the pointer, momentum is untouched, nothing accelerates and nothing
/// scrolls twice. When no ancestor can scroll either - the Console, whose page scroller is disabled
/// so the output viewport owns its own follow behaviour - the event is not handled at all and the
/// original control keeps it.
/// </para>
/// <para>
/// Vertical only. Horizontal wheel input, tilt wheels and horizontally scrolling regions are never
/// touched.
/// </para>
/// </remarks>
public static class AppScroll
{
    private static bool registered;

    /// <summary>
    /// Registers the boundary-aware wheel handler for every <see cref="ScrollViewer"/> in the
    /// process. Safe to call more than once.
    /// </summary>
    public static void EnableBoundaryAwareWheel()
    {
        if (registered)
            return;
        registered = true;
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerWheel),
            handledEventsToo: false);
    }

    /// <summary>
    /// True when this scroller can still move in the direction the wheel asked for.
    /// </summary>
    /// <remarks>
    /// A positive delta is a request to move towards the top. A scroller with nothing to scroll -
    /// an empty table, a list shorter than its box - answers false for both directions, which is what
    /// stops an empty region from trapping the wheel.
    /// </remarks>
    public static bool CanScroll(ScrollViewer viewer, int delta)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (delta == 0 || viewer.ScrollableHeight <= 0)
            return false;
        const double tolerance = 0.5;
        return delta > 0
            ? viewer.VerticalOffset > tolerance
            : viewer.VerticalOffset < viewer.ScrollableHeight - tolerance;
    }

    /// <summary>The nearest ancestor scroller that can absorb this wheel movement, or null.</summary>
    public static ScrollViewer? FindScrollableAncestor(DependencyObject from, int delta)
    {
        ArgumentNullException.ThrowIfNull(from);
        var current = VisualTreeHelper.GetParent(from);
        while (current is not null)
        {
            if (current is ScrollViewer candidate && CanScroll(candidate, delta))
                return candidate;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static void OnScrollViewerWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer || e.Delta == 0)
            return;
        if (CanScroll(viewer, e.Delta))
            return;
        if (FindScrollableAncestor(viewer, e.Delta) is not { } target)
            return;

        e.Handled = true;
        target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = target
        });
    }
}
