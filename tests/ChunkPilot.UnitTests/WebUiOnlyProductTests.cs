namespace ChunkPilot.UnitTests;

public sealed class WebUiOnlyProductTests
{
    private static readonly string Root = RepositoryRoot();

    [Fact]
    public void Normal_startup_constructs_only_the_WebUi_product_shell()
    {
        var app = File.ReadAllText(Path.Combine(Root, "src", "ChunkPilot.App", "App.xaml.cs"));
        Assert.Contains("new WebUiWindow(viewModel, agentClient)", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new MainWindow(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("--webui-preview", app, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(Root, "src", "ChunkPilot.App", "MainWindow.xaml")));
        Assert.False(File.Exists(Path.Combine(Root, "src", "ChunkPilot.App", "MainWindow.xaml.cs")));
    }

    [Fact]
    public void Recovery_surface_has_no_legacy_fallback()
    {
        var xaml = File.ReadAllText(Path.Combine(
            Root, "src", "ChunkPilot.App", "WebUi", "WebUiWindow.xaml"));
        Assert.Contains("Repair or install WebView2", xaml, StringComparison.Ordinal);
        Assert.Contains("Open diagnostics", xaml, StringComparison.Ordinal);
        Assert.Contains("Retry", xaml, StringComparison.Ordinal);
        Assert.Contains("Exit", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preview", xaml, StringComparison.OrdinalIgnoreCase);
        var host = File.ReadAllText(Path.Combine(
            Root, "src", "ChunkPilot.App", "WebUi", "WebUiWindow.xaml.cs"));
        Assert.Contains("core.Settings.AreDefaultScriptDialogsEnabled = false", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_pages_are_not_active_source()
    {
        var pages = Path.Combine(Root, "src", "ChunkPilot.App", "Pages");
        Assert.False(Directory.Exists(pages) && Directory.EnumerateFiles(pages).Any());
        Assert.True(File.Exists(Path.Combine(Root, "archive", "legacy-wpf-ui", "README.md")));
    }

    [Fact]
    public void Server_navigation_preserves_status_identity_when_names_are_long()
    {
        var css = File.ReadAllText(Path.Combine(
            Root, "src", "ChunkPilot.WebUi", "src", "app", "Shell.module.css"));
        var app = File.ReadAllText(Path.Combine(
            Root, "src", "ChunkPilot.WebUi", "src", "app", "App.tsx"));

        Assert.Contains(".navItem > span { min-width: 0; flex: 1 1 auto;", css, StringComparison.Ordinal);
        Assert.Contains(".serverDot { width: 8px; height: 8px; flex: 0 0 8px;", css, StringComparison.Ordinal);
        Assert.Contains("<ServerWorkspace key={activeServerId!}", app, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "ChunkPilot.sln")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
