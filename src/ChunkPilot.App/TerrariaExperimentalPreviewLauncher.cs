using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChunkPilot.App.DesignSystem;
using ChunkPilot.Core;
using ChunkPilot.Infrastructure;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;

namespace ChunkPilot.App;

/// <summary>Development-only review surface for the non-public Terraria backend foundation.</summary>
internal static class TerrariaExperimentalPreviewLauncher
{
    internal const string PreviewArgument = "--experimental-terraria-preview";

    internal static bool IsRequested(IEnumerable<string> arguments) =>
        arguments.Any(argument => argument.Equals(PreviewArgument, StringComparison.OrdinalIgnoreCase));

    internal static bool TryRun(App app, IReadOnlyList<string> arguments)
    {
        if (!IsRequested(arguments)) return false;
        var window = BuildWindow(ReadEvidence(), ReadOption(arguments, "--render"));
        AppTheme.Attach(window);
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;
        app.MainWindow = window;
        window.Show();
        return true;
    }

    private static Window BuildWindow(TerrariaCertificationEvidence? evidence, string? renderPath)
    {
        var release = OfficialTerrariaProvider.CurrentRelease();
        var window = new Window
        {
            Title = "ChunkPilot — Experimental Terraria foundation",
            Width = 960,
            Height = 680,
            MinWidth = 760,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        window.SetResourceReference(Control.BackgroundProperty, "AppSurfaceCanvas");
        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 22), Orientation = Orientation.Vertical };
        panel.Children.Add(Text("Experimental Terraria foundation", "AppDisplayText"));
        panel.Children.Add(Text(
            "Engineering proof only. Terraria is not available in ordinary Create Server and this window never starts the Agent.",
            "AppBodyText"));
        panel.Children.Add(Section("Official provider",
            $"Terraria {release.Version} · release {release.ReleaseId}\n" +
            $"{release.ArtifactUrl}\n{release.ExpectedSizeBytes:N0} bytes\n{release.IntegrityEvidence}"));
        panel.Children.Add(Section("Shared backend",
            "Install: journalled creation transaction\n" +
            "Lifecycle: exact owned process, shared operation queue, console, save, stop and crash state\n" +
            "Storage: managed world path and verified backup manifest\n" +
            "Networking: TCP model reused, but this proof forces 127.0.0.1 and disables UPnP\n" +
            "Diagnostics: shared local crash evidence; no public Terraria support claim"));
        var certificationText = evidence is null
            ? "No local certification report was found. Run the documented certify-terraria command from the repository."
            : TerrariaRuntimeCertifier.Passed(evidence)
                ? $"PASSED · {evidence.TestedAt.LocalDateTime:g}\nSHA-256 (local evidence): {evidence.LocalArtifactSha256}\n{evidence.ReadinessEvidence}"
                : $"BLOCKED · {evidence.TestedAt.LocalDateTime:g}\n{FailureSummary(evidence)}\n" +
                  $"SHA-256 (local evidence): {evidence.LocalArtifactSha256}\n" +
                  $"Disposable-root cleanup: {(evidence.CleanupConfirmed ? "confirmed" : "requires attention")}\n" +
                  "The official artifact remains cached and verified; no runtime or public-network support is claimed.";
        panel.Children.Add(Section("Exact isolated certification", certificationText));
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        close.SetResourceReference(FrameworkElement.StyleProperty, "AppSecondaryButton");
        close.Click += (_, _) => window.Close();
        panel.Children.Add(close);
        window.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (!string.IsNullOrWhiteSpace(renderPath))
        {
            window.ContentRendered += (_, _) =>
            {
                var path = Path.GetFullPath(renderPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var dpi = VisualTreeHelper.GetDpi(window);
                var bitmap = new RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX)),
                    Math.Max(1, (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY)),
                    dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(path);
                encoder.Save(stream);
                window.Close();
            };
        }
        return window;
    }

    private static FrameworkElement Section(string title, string detail)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        panel.Children.Add(Text(title, "AppTitleText"));
        panel.Children.Add(Text(detail, "AppBodyText"));
        return panel;
    }

    private static TextBlock Text(string value, string style)
    {
        var block = new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        block.SetResourceReference(FrameworkElement.StyleProperty, style);
        return block;
    }

    private static string? ReadOption(IReadOnlyList<string> arguments, string name)
    {
        for (var index = 0; index < arguments.Count - 1; index++)
            if (arguments[index].Equals(name, StringComparison.OrdinalIgnoreCase)) return arguments[index + 1];
        return null;
    }

    private static string FailureSummary(TerrariaCertificationEvidence evidence) => evidence.FailureKind switch
    {
        TerrariaCertificationFailureKind.MissingRuntimePrerequisite =>
            "This PC does not have Microsoft XNA Framework 4.0, which the official Windows server requires. ChunkPilot did not install or change that system prerequisite.",
        TerrariaCertificationFailureKind.ArtifactValidation =>
            "The official package did not pass acquisition or archive validation. See the certification evidence for details.",
        TerrariaCertificationFailureKind.Readiness =>
            "The owned loopback server did not become ready before the bounded deadline.",
        TerrariaCertificationFailureKind.Startup =>
            "The official server process exited before it became ready.",
        TerrariaCertificationFailureKind.Save =>
            "The isolated server did not produce complete save and world evidence.",
        TerrariaCertificationFailureKind.Stop =>
            "The isolated server did not produce a clean bounded stop.",
        TerrariaCertificationFailureKind.Cleanup =>
            "The disposable certification root or exact owned process needs cleanup attention.",
        TerrariaCertificationFailureKind.Cancelled =>
            "Certification was cancelled and exact-owned cleanup was requested.",
        _ => "Exact runtime certification did not pass. See the local evidence report for details."
    };

    private static TerrariaCertificationEvidence? ReadEvidence()
    {
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
                current = current.Parent;
            if (current is null) return null;
            var path = Path.Combine(current.FullName, "artifacts", "terraria-certification",
                "terraria-certification-evidence.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TerrariaCertificationEvidence>(File.ReadAllText(path), ProtocolJson.Options)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
