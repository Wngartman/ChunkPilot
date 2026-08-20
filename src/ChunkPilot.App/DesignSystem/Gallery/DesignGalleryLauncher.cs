using System.Globalization;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ChunkPilot.App.DesignSystem.Gallery;

/// <summary>
/// Command-line entry point for the Design Gallery. DEVELOPMENT ONLY.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// ChunkPilot.exe --design-gallery
/// ChunkPilot.exe --design-gallery --render &lt;directory&gt;
/// </code>
/// </para>
/// <para>
/// This path deliberately bypasses the single-instance mutex, the tray icon, the agent connection
/// and UI session registration. Reviewing the design system must never be able to disturb a running
/// ChunkPilot session or a managed server.
/// </para>
/// <para>
/// The render mode measures and arranges the gallery body off-tree and rasterises it, so a capture
/// needs no visible window and produces the same image every run. That also means the captured
/// visual is unambiguously ChunkPilot's own element tree rather than whatever the compositor happened
/// to have on screen.
/// </para>
/// </remarks>
public static class DesignGalleryLauncher
{
    private const string GallerySwitch = "--design-gallery";
    private const string RenderSwitch = "--render";
    private const double CaptureDpi = 96d;
    private const double MaximumCaptureHeight = 12_000d;

    /// <summary>Widths captured by render mode, one per documented layout mode.</summary>
    private static readonly (string Name, double Width, AppLayoutMode Mode)[] CaptureSizes =
    [
        ("wide-1440", 1440d, AppLayoutMode.Wide),
        ("standard-1100", 1100d, AppLayoutMode.Standard),
        ("compact-840", 840d, AppLayoutMode.Compact)
    ];

    /// <summary>
    /// Handles the gallery switches if present.
    /// </summary>
    /// <returns>True when the gallery took over startup and normal startup must not continue.</returns>
    public static bool TryRun(Application application, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.Any(argument => string.Equals(argument, GallerySwitch, StringComparison.OrdinalIgnoreCase)))
            return false;

        var renderDirectory = ReadOption(arguments, RenderSwitch);
        if (renderDirectory is not null)
        {
            // A WinExe has no console, so the outcome is written to a file beside the images.
            // Without this, a broken template would produce a silent no-op and look like success.
            try
            {
                var written = Render(renderDirectory);
                File.WriteAllLines(Path.Combine(renderDirectory, "design-gallery-render.log"), written);
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(renderDirectory);
                File.WriteAllText(
                    Path.Combine(renderDirectory, "design-gallery-render.log"),
                    exception.ToString());
            }

            application.Shutdown();
            return true;
        }

        var window = new DesignGalleryWindow();
        AppTheme.Attach(window);
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        application.MainWindow = window;
        window.Show();
        return true;
    }

    /// <summary>
    /// Rasterises the gallery at every documented layout width.
    /// </summary>
    /// <param name="directory">Destination directory. Created if missing.</param>
    /// <returns>The paths written.</returns>
    public static IReadOnlyList<string> Render(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        var written = new List<string>();

        foreach (var (name, width, mode) in CaptureSizes)
        {
            var body = new DesignGalleryContent();
            var host = BuildCaptureHost(body, mode);

            // Two passes with the dispatcher drained in between. Virtualising controls (the table,
            // the console) realise their rows during a layout pass that already knows the viewport,
            // so a single Measure/Arrange captures them as empty.
            double height = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                host.Measure(new Size(width, double.PositiveInfinity));
                height = Math.Min(Math.Ceiling(host.DesiredSize.Height), MaximumCaptureHeight);
                host.Arrange(new Rect(0, 0, width, height));
                host.UpdateLayout();
                DrainDispatcher();
            }

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(width), (int)Math.Ceiling(height),
                CaptureDpi, CaptureDpi, PixelFormats.Pbgra32);
            bitmap.Render(host);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"design-gallery-{name}.png"));
            using var stream = File.Create(path);
            encoder.Save(stream);
            written.Add(path);
        }

        return written;
    }

    /// <summary>
    /// Wraps the gallery body in a self-sufficient root.
    /// </summary>
    /// <remarks>
    /// A capture has no window above it, so the values a window would normally supply by inheritance
    /// - background, font, text colour - are set here explicitly, and the layout mode is pinned
    /// instead of being derived from a width that nothing is watching.
    /// </remarks>
    private static FrameworkElement BuildCaptureHost(DesignGalleryContent body, AppLayoutMode mode)
    {
        var host = new Border
        {
            Background = Resolve("AppSurfaceCanvas"),
            Child = body
        };
        TextElement.SetForeground(host, Resolve("AppTextPrimary"));
        if (Application.Current?.TryFindResource("AppFontFamily") is FontFamily family)
            TextElement.SetFontFamily(host, family);
        if (Application.Current?.TryFindResource("AppFontSizeBody") is double size)
            TextElement.SetFontSize(host, size);
        AppLayout.SetMode(host, mode);
        AppMotion.SetIsEnabled(host, false);
        AppAccessibility.SetIsHighContrast(host, AppTheme.IsHighContrastPreferred);
        return host;
    }

    /// <summary>Lets queued layout, binding and container-generation work complete.</summary>
    private static void DrainDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private static Brush Resolve(string tokenKey) =>
        Application.Current?.TryFindResource(tokenKey) as Brush ?? Brushes.Black;

    private static string? ReadOption(string[] arguments, string name)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], name, StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        }
        return null;
    }
}
