using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ChunkPilot.App.DesignSystem;

/// <summary>
/// Gives every ChunkPilot window the same dark title bar and the application icon.
/// </summary>
/// <remarks>
/// <para>
/// The title bar is Windows', not a custom imitation. Asking DWM for immersive dark mode keeps the
/// real minimize, maximize, close, snap layouts, Alt+F4, Alt+Tab and taskbar behaviour, and keeps the
/// active and inactive states Windows draws; a hand-built caption would have to reimplement all of it
/// and would still be wrong for high contrast and for users who change their system accent.
/// </para>
/// <para>
/// One shared entry point rather than a per-window copy. The Create Server window shipped with a
/// light native caption strip against a dark application precisely because the dark-mode call lived
/// privately in <c>MainWindow</c>. It is now applied by <see cref="AppTheme.Attach"/>, so a window
/// that joins the design system gets the chrome by joining it.
/// </para>
/// <para>
/// Windows 10 builds before 1809 have no such attribute and Windows returns a failure code, which is
/// ignored: the window is simply drawn with the system caption, which is the correct graceful result.
/// While Windows reports high contrast, dark mode is deliberately not requested, because the caption
/// belongs to the high-contrast theme then.
/// </para>
/// </remarks>
public static class AppWindowChrome
{
    /// <summary>Documented DWM attribute for the dark caption. 20 on 1809+, 19 on early 1809 builds.</summary>
    private const int UseImmersiveDarkMode = 20;
    private const int UseImmersiveDarkModeBefore20H1 = 19;

    /// <summary>
    /// Applies the shared chrome to one window. Safe to call more than once and before the window is
    /// shown; the attribute is re-applied if the handle is created later.
    /// </summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        ApplyIcon(window);
        if (TryApplyDarkCaption(window))
            return;
        // No handle yet, which is the normal case for a window built but not shown. SourceInitialized
        // runs before the first frame is drawn, so the caption is never painted light first.
        window.SourceInitialized -= OnSourceInitialized;
        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// True when the dark caption was requested and accepted for this window. Reported honestly:
    /// a false result means Windows kept its own caption, not that something silently failed.
    /// </summary>
    public static bool IsDarkCaptionApplied(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return appliedTo.TryGetValue(window, out var applied) && applied;
    }

    private static readonly Dictionary<Window, bool> appliedTo = [];

    private static void OnSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not Window window)
            return;
        window.SourceInitialized -= OnSourceInitialized;
        _ = TryApplyDarkCaption(window);
    }

    private static bool TryApplyDarkCaption(Window window)
    {
        if (AppTheme.IsHighContrastPreferred)
            return false;
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return false;
            var dark = 1;
            var applied =
                DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref dark, sizeof(int)) == 0 ||
                DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref dark, sizeof(int)) == 0;
            appliedTo[window] = applied;
            window.Closed -= OnWindowClosed;
            window.Closed += OnWindowClosed;
            return applied;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM on this system. The window keeps the system caption.
            return false;
        }
    }

    private static void OnWindowClosed(object? sender, EventArgs args)
    {
        if (sender is Window window)
            appliedTo.Remove(window);
    }

    /// <summary>
    /// Gives the window the application icon, for its caption, the taskbar and Alt+Tab.
    /// </summary>
    /// <remarks>
    /// A window without an icon shows the generic Windows placeholder in Alt+Tab, which reads as a
    /// different application. An icon already set by the window is left alone, and a missing icon file
    /// is not an error worth failing a window over.
    /// </remarks>
    private static void ApplyIcon(Window window)
    {
        if (window.Icon is not null)
            return;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "ChunkPilot.ico");
            if (File.Exists(path))
                window.Icon = BitmapFrame.Create(new Uri(path, UriKind.Absolute));
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or UriFormatException)
        {
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
