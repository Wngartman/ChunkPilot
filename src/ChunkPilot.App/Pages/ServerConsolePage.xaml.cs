using System.Windows.Controls;
using System.Windows.Media;

namespace ChunkPilot.App;

/// <summary>
/// Host for the console output and its command box.
/// </summary>
/// <remarks>
/// Owns one thing beyond layout: reporting where the output viewport is. Live-follow is a view-model
/// decision, but only the view knows whether the last line is on screen, so the page forwards that
/// and nothing else. Previously nothing forwarded it at all, which is why follow never paused when
/// the user scrolled up to read.
/// </remarks>
public partial class ServerConsolePage : UserControl
{
    /// <summary>
    /// How close to the end still counts as "at the bottom", in device-independent pixels.
    /// </summary>
    /// <remarks>
    /// A few pixels of tolerance, because a wrapped last line or a fractional viewport height can
    /// leave the scroll offset just short of its maximum while the newest line is plainly visible.
    /// Demanding an exact match would pause follow for no reason the user could see.
    /// </remarks>
    private const double BottomTolerance = 4d;

    public ServerConsolePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Expose the console ListBox so the shell can scroll it.</summary>
    internal System.Windows.Controls.ListBox ConsoleListBox => ConsoleList;

    private ScrollViewer? consoleScroller;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ConsoleList.ApplyTemplate();
        var scroller = FindVisualDescendant<ScrollViewer>(ConsoleList);
        if (ReferenceEquals(scroller, consoleScroller))
            return;
        if (consoleScroller is not null)
            consoleScroller.ScrollChanged -= OnConsoleScrollChanged;
        consoleScroller = scroller;
        if (consoleScroller is not null)
            consoleScroller.ScrollChanged += OnConsoleScrollChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (consoleScroller is not null)
            consoleScroller.ScrollChanged -= OnConsoleScrollChanged;
        consoleScroller = null;
    }

    private void OnConsoleScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, consoleScroller) ||
            DataContext is not MainViewModel viewModel)
            return;
        // Content that does not overflow is trivially at its end.
        var atBottom = consoleScroller.ScrollableHeight <= 0 ||
                       consoleScroller.VerticalOffset >= consoleScroller.ScrollableHeight - BottomTolerance;
        viewModel.SetConsoleViewport(atBottom);
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                return match;
            var descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }
}
