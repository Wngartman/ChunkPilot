using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;

namespace ChunkPilot.App.CreateServer;

/// <summary>
/// Command-line entry point for the Create Server v2 preview. DEVELOPMENT AND REVIEW ONLY.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// ChunkPilot.exe --create-server-v2-preview
/// </code>
/// </para>
/// <para>
/// Like the Design Gallery, this path deliberately runs before the single-instance mutex, the tray
/// icon, the agent connection and UI session registration, and returns before any of them are set
/// up. Reviewing the new creation experience must not be able to disturb a running ChunkPilot
/// session, a managed server, the database or any server folder.
/// </para>
/// <para>
/// Normal product Create Server actions use the separate live Vanilla composition. No product
/// control reaches this synthetic launcher.
/// </para>
/// </remarks>
public static class CreateServerPreviewLauncher
{
    /// <summary>The one switch that opens the preview.</summary>
    public const string PreviewSwitch = "--create-server-v2-preview";

    /// <summary>Optional capture switch, used to produce review images deterministically.</summary>
    public const string RenderSwitch = "--render";

    private const double CaptureDpi = 96d;

    /// <summary>True when the arguments ask for the preview.</summary>
    public static bool IsRequested(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(argument, PreviewSwitch, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Opens the preview if the switch is present.
    /// </summary>
    /// <returns>True when the preview took over startup and normal startup must not continue.</returns>
    public static bool TryRun(Application application, string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!IsRequested(arguments))
            return false;

        var renderDirectory = ReadOption(arguments, RenderSwitch);
        if (renderDirectory is not null)
        {
            // A WinExe has no console, so the outcome goes to a file beside the images. Without it a
            // broken template would produce a silent no-op that looked like success.
            try
            {
                var written = Render(renderDirectory);
                File.WriteAllLines(Path.Combine(renderDirectory, "create-server-preview-render.log"), written);
            }
            catch (Exception exception)
            {
                Directory.CreateDirectory(renderDirectory);
                File.WriteAllText(
                    Path.Combine(renderDirectory, "create-server-preview-render.log"),
                    exception.ToString());
            }

            application.Shutdown();
            return true;
        }

        var window = new CreateServerPreviewWindow(new CreateServerPreviewViewModel());
        AppTheme.Attach(window);
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        application.MainWindow = window;
        window.Show();
        return true;
    }

    /// <summary>
    /// Drives the real preview window through every reviewable state and rasterises each one.
    /// </summary>
    /// <remarks>
    /// The real window is shown and resized rather than a detached copy being measured, because the
    /// responsive layout mode is derived from the window's own width and a detached host would let a
    /// broken breakpoint pass unnoticed. Still side-effect free: the same view model, the same
    /// synthetic data, no agent and no filesystem beyond the image directory.
    /// </remarks>
    public static IReadOnlyList<string> Render(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);

        var viewModel = new CreateServerPreviewViewModel();
        var window = new CreateServerPreviewWindow(viewModel);
        AppTheme.Attach(window);
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 0;
        window.Top = 0;
        window.Show();

        var written = new List<string>();
        try
        {
            foreach (var capture in Captures())
            {
                Resize(window, capture.Width, capture.Height);
                capture.Arrange(viewModel);
                if (capture.HighContrast || !capture.Motion)
                    AppTheme.ApplyPreview(window, capture.HighContrast, capture.Motion);
                else
                    AppTheme.ApplyPreview(window, AppTheme.IsHighContrastPreferred, AppTheme.IsMotionPreferred);
                Settle(window);
                written.Add(Save(window, directory, capture.Name));
            }
        }
        finally
        {
            window.Close();
        }

        return written;
    }

    private sealed record Capture(
        string Name,
        double Width,
        double Height,
        Action<CreateServerPreviewViewModel> Arrange,
        bool HighContrast = false,
        bool Motion = true);

    /// <summary>The reviewable states, in a fixed order so a rerun produces the same set.</summary>
    private static IEnumerable<Capture> Captures()
    {
        const double w = 1440d;
        const double h = 900d;

        yield return new("01-intent-initial", w, h, Reset);
        foreach (var card in CreationIntentCatalog.Cards)
        {
            var intent = card.Intent;
            yield return new(
                $"02-intent-{intent.ToString().ToLowerInvariant()}", w, h,
                model => { Reset(model); Choose(model, intent); });
        }

        yield return new("03-setup-vanilla", w, h, model => Setup(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release"));
        yield return new("04-setup-vanilla-snapshot-warning", w, h, model => Setup(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-snapshot"));
        yield return new("05-setup-vanilla-unavailable", w, h, model => Setup(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-unavailable"));
        yield return new("06-setup-plugins", w, h, model => Setup(model, CreationIntent.Plugins, "Village square", "synthetic-paper-1214"));
        yield return new("07-setup-mods", w, h, model => Setup(model, CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214"));
        yield return new("08-setup-mods-incompatible", w, h, model => Setup(model, CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1710"));
        yield return new("09-setup-modpack-server-pack", w, h, model => Modpack(model, "synthetic-pack-skyward", "synthetic-pack-skyward-14"));
        yield return new("10-setup-modpack-no-server-pack", w, h, model => Modpack(model, "synthetic-pack-lantern", "synthetic-pack-lantern-20"));
        yield return new("11-setup-modpack-unknown", w, h, model => Modpack(model, "synthetic-pack-driftwood", "synthetic-pack-driftwood-07"));
        yield return new("12-setup-modpack-needs-key", w, h, model => Modpack(model, "synthetic-pack-vault", "synthetic-pack-vault-31"));
        yield return new("13-setup-crossplay", w, h, model => Setup(model, CreationIntent.Crossplay, "Family world", "synthetic-crossplay-paper"));
        yield return new("14-setup-advanced", w, h, model => Setup(model, CreationIntent.Advanced, "Test rig", ""));
        yield return new("15-setup-invalid-name", w, h, model => Setup(model, CreationIntent.Vanilla, "CON:my server.", "synthetic-vanilla-release"));

        yield return new("16-review-vanilla", w, h, model => Review(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release"));
        yield return new("17-review-modpack", w, h, model => ReviewModpack(model, "synthetic-pack-skyward", "synthetic-pack-skyward-14"));
        yield return new("18-review-crossplay", w, h, model => Review(model, CreationIntent.Crossplay, "Family world", "synthetic-crossplay-paper"));
        yield return new("19-completion", w, h, model =>
        {
            Review(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release");
            model.FinishPreviewCommand.Execute(null);
        });

        yield return new("20-size-800x600", 800d, 600d, model => Setup(model, CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214"));
        yield return new("21-size-1000x700", 1000d, 700d, model => Setup(model, CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214"));
        yield return new("22-size-1280x720", 1280d, 720d, model => Setup(model, CreationIntent.Mods, "Machines and magic", "synthetic-fabric-1214"));
        yield return new("23-size-1440x900-intent", 1440d, 900d, Reset);
        yield return new("24-size-1920x1080-intent", 1920d, 1080d, Reset);
        yield return new("25-size-1920x1080-review", 1920d, 1080d, model => Review(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release"));
        yield return new("26-size-800x600-intent", 800d, 600d, Reset);

        yield return new("27-high-contrast-intent", w, h, Reset, HighContrast: true);
        yield return new("28-high-contrast-setup", w, h,
            model => Setup(model, CreationIntent.Modpack, "Family world", ""), HighContrast: true);
        yield return new("29-high-contrast-review", w, h,
            model => Review(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release"),
            HighContrast: true);
        yield return new("30-reduced-motion-setup", w, h,
            model => Setup(model, CreationIntent.Vanilla, "Sunday survival", "synthetic-vanilla-release"),
            Motion: false);
    }

    private static void Reset(CreateServerPreviewViewModel model)
    {
        // Set the step directly rather than walking Back: Back is unavailable on the completion
        // step, so a capture that followed a completed run would otherwise stay there.
        model.CurrentStep = CreationWizardStep.Intent;
        model.SelectedIntent = null;
        model.ServerName = "";
    }

    private static void Choose(CreateServerPreviewViewModel model, CreationIntent intent) =>
        model.SelectedIntent = CreationIntentCatalog.For(intent);

    private static void Setup(
        CreateServerPreviewViewModel model, CreationIntent intent, string name, string optionId)
    {
        Reset(model);
        Choose(model, intent);
        model.ServerName = name;
        if (!string.IsNullOrEmpty(optionId))
            model.SelectedOption = model.Options.FirstOrDefault(option => option.Id == optionId);
        model.NextCommand.Execute(null);
    }

    private static void Modpack(CreateServerPreviewViewModel model, string projectId, string optionId)
    {
        Reset(model);
        Choose(model, CreationIntent.Modpack);
        model.ServerName = "Weekend pack";
        model.NextCommand.Execute(null);
        model.SelectedProject = model.Projects.FirstOrDefault(project => project.Id == projectId);
        model.SelectedOption = model.ProjectVersions.FirstOrDefault(option => option.Id == optionId);
    }

    private static void Review(
        CreateServerPreviewViewModel model, CreationIntent intent, string name, string optionId)
    {
        Setup(model, intent, name, optionId);
        if (intent == CreationIntent.Advanced)
            model.AdvancedAcknowledged = true;
        model.NextCommand.Execute(null);
    }

    private static void ReviewModpack(CreateServerPreviewViewModel model, string projectId, string optionId)
    {
        Modpack(model, projectId, optionId);
        model.NextCommand.Execute(null);
    }

    private static void Resize(Window window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
    }

    private static void Settle(Window window)
    {
        // Two passes with the dispatcher drained between them: responsive triggers act on the width
        // measured by the first pass, and item containers realise during the second.
        for (var pass = 0; pass < 2; pass++)
        {
            window.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }
    }

    private static string Save(Window window, string directory, string name)
    {
        var root = (FrameworkElement)window.Content;
        var width = Math.Max(1, (int)Math.Ceiling(root.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(root.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, CaptureDpi, CaptureDpi, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture, $"{name}.png"));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

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
