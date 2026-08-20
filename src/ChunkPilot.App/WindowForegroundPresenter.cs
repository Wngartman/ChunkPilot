using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ChunkPilot.App;

/// <summary>
/// Presents one existing application window after an explicit user action.
/// </summary>
/// <remarks>
/// Restores and activates through WPF first. The brief topmost toggle only raises the window in the
/// ordinary window band; the previous value is restored immediately. If Windows still refuses the
/// foreground request, the taskbar flashes instead of using input-thread attachment or a retry loop.
/// </remarks>
internal static class WindowForegroundPresenter
{
    public static void Present(Window window, IInputElement? focusTarget = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        var handle = new WindowInteropHelper(window).Handle;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        // WindowState can briefly lag the native state when Windows, the taskbar, or an owner
        // relationship minimized the window. IsIconic closes that gap without relying on a delay.
        if (handle != IntPtr.Zero && IsIconic(handle))
            _ = ShowWindow(handle, RestoreWindow);
        window.Show();
        window.Activate();

        if (handle != IntPtr.Zero && GetForegroundWindow() != handle)
        {
            var wasTopmost = window.Topmost;
            window.Topmost = true;
            window.Topmost = wasTopmost;
            window.Activate();

            if (!SetForegroundWindow(handle) && GetForegroundWindow() != handle)
            {
                var flash = new FlashInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashInfo>(),
                    Window = handle,
                    Flags = FlashTray | FlashTimerNoForeground,
                    Count = 3,
                    Timeout = 0
                };
                _ = FlashWindowEx(ref flash);
            }
        }

        if (focusTarget is not null)
            window.Dispatcher.BeginInvoke(DispatcherPriority.Input, focusTarget.Focus);
    }

    private const uint FlashTray = 0x00000002;
    private const uint FlashTimerNoForeground = 0x0000000C;
    private const int RestoreWindow = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
