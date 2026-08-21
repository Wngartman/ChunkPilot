using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ChunkPilot.App.WebUi;

/// <summary>Development-only deterministic WebView2 fixture and capture path.</summary>
internal static class WebUiFixtureLauncher
{
    internal const string FixtureArgument = "--webui-fixture";
    internal const string RenderArgument = "--render";

    public static bool TryRun(Application application, string[] arguments)
    {
        if (!arguments.Any(argument => string.Equals(argument, FixtureArgument, StringComparison.OrdinalIgnoreCase)))
            return false;

        var fixture = ReadOption(arguments, FixtureArgument) ?? "several";
        var page = ReadOption(arguments, "--webui-page") ?? "dashboard";
        var stage = ReadOption(arguments, "--webui-stage");
        var tab = ReadOption(arguments, "--webui-tab");
        var settingsSection = ReadOption(arguments, "--webui-settings");
        var dirty = arguments.Any(argument => string.Equals(argument, "--webui-dirty", StringComparison.OrdinalIgnoreCase));
        var mode = ReadOption(arguments, "--webui-mode");
        var render = ReadOption(arguments, RenderArgument);
        var renderSet = ReadOption(arguments, "--render-set");
        var width = ReadNumber(arguments, "--width", 1280, 920, 3840);
        var height = ReadNumber(arguments, "--height", 820, 620, 2160);
        var scale = ReadNumber(arguments, "--scale", 1, 1, 2);
        var highContrast = arguments.Any(argument => string.Equals(argument, "--forced-colors", StringComparison.OrdinalIgnoreCase));
        var reducedMotion = arguments.Any(argument => string.Equals(argument, "--reduced-motion", StringComparison.OrdinalIgnoreCase));
        var window = new FixtureWindow(fixture, page, stage, tab, settingsSection, mode, dirty, render, renderSet, width, height, scale, highContrast, reducedMotion);
        application.ShutdownMode = ShutdownMode.OnMainWindowClose;
        application.MainWindow = window;
        window.Show();
        return true;
    }

    private static string? ReadOption(string[] arguments, string option)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
            if (string.Equals(arguments[index], option, StringComparison.OrdinalIgnoreCase) &&
                !arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
                return arguments[index + 1];
        return null;
    }

    private static double ReadNumber(string[] arguments, string option, double fallback, double minimum, double maximum) =>
        double.TryParse(ReadOption(arguments, option), out var value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private sealed class FixtureWindow : Window
    {
        private readonly WebView2 browser = new()
        {
            DefaultBackgroundColor = WebUiNativeTheme.ResolveWebViewColor("AppSurfaceCanvas")
        };
        private readonly string fixture;
        private readonly string page;
        private readonly string? stage;
        private readonly string? tab;
        private readonly string? settingsSection;
        private readonly string? mode;
        private readonly bool dirty;
        private readonly string? renderPath;
        private readonly string? renderSetDirectory;
        private readonly double scale;
        private readonly bool highContrast;
        private readonly bool reducedMotion;

        public FixtureWindow(string fixture, string page, string? stage, string? tab, string? settingsSection, string? mode, bool dirty, string? renderPath, string? renderSetDirectory, double width, double height, double scale, bool highContrast, bool reducedMotion)
        {
            this.fixture = fixture;
            this.page = page;
            this.stage = stage;
            this.tab = tab;
            this.settingsSection = settingsSection;
            this.mode = mode;
            this.dirty = dirty;
            this.renderPath = renderPath;
            this.renderSetDirectory = renderSetDirectory;
            this.scale = scale;
            this.highContrast = highContrast;
            this.reducedMotion = reducedMotion;
            Title = $"ChunkPilot WebUI fixture - {page}";
            Width = width;
            Height = height;
            MinWidth = 920;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.CanResize;
            Background = WebUiNativeTheme.ResolveBrush("AppSurfaceCanvas");
            Content = browser;
            Loaded += OnLoaded;
            Closed += (_, _) => browser.Dispose();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var configuredRoot = Environment.GetEnvironmentVariable("CHUNKPILOT_DATA_ROOT");
                var dataRoot = string.IsNullOrWhiteSpace(configuredRoot) ? Path.GetTempPath() : Path.GetFullPath(configuredRoot);
                var profile = Path.Combine(dataRoot, "WebView2", $"Fixture-{Environment.ProcessId}");
                var arguments = new List<string>();
                if (Math.Abs(scale - 1) > .001)
                    arguments.Add($"--force-device-scale-factor={scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                if (highContrast)
                    arguments.Add("--force-high-contrast");
                if (reducedMotion)
                    arguments.Add("--force-prefers-reduced-motion");
                var options = WebUiWindow.CreateEnvironmentOptions(string.Join(' ', arguments));
                var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile, options: options).ConfigureAwait(true);
                await browser.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
                Configure(browser.CoreWebView2);
                var assetRoot = Path.Combine(AppContext.BaseDirectory, "WebUi");
                browser.CoreWebView2.SetVirtualHostNameToFolderMapping("chunkpilot.local", assetRoot, CoreWebView2HostResourceAccessKind.DenyCors);
                if (renderSetDirectory is not null)
                {
                    await CaptureReviewSetAsync(Path.GetFullPath(renderSetDirectory)).ConfigureAwait(true);
                    Close();
                    return;
                }
                await NavigateAsync(fixture, page, tab, stage, settingsSection, mode, dirty).ConfigureAwait(true);
                await Task.Delay(350).ConfigureAwait(true);
                if (renderPath is null)
                    return;
                var path = Path.GetFullPath(renderPath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var stream = File.Create(path);
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).ConfigureAwait(true);
                Close();
            }
            catch (Exception exception)
            {
                var diagnosticPath = renderPath is not null
                    ? Path.GetFullPath(renderPath) + ".error.txt"
                    : renderSetDirectory is not null
                        ? Path.Combine(Path.GetFullPath(renderSetDirectory), "capture-set.error.txt")
                        : null;
                if (diagnosticPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(diagnosticPath)!);
                    File.WriteAllText(diagnosticPath, exception.ToString());
                }
                Close();
            }
        }

        private async Task NavigateAsync(string fixtureName, string pageName, string? tabName, string? stageName, string? settingsName, string? modeName, bool isDirty)
        {
            var query = $"?fixture={Uri.EscapeDataString(fixtureName)}&page={Uri.EscapeDataString(pageName)}" +
                (stageName is null ? "" : $"&stage={Uri.EscapeDataString(stageName)}") +
                (tabName is null ? "" : $"&tab={Uri.EscapeDataString(tabName)}") +
                (settingsName is null ? "" : $"&settings={Uri.EscapeDataString(settingsName)}") +
                (modeName is null ? "" : $"&mode={Uri.EscapeDataString(modeName)}") +
                (isDirty ? "&dirty=1" : "");
            browser.CoreWebView2.Navigate(WebUiProtocol.EntryPoint + query);
            var expires = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < expires)
            {
                await Task.Delay(100).ConfigureAwait(true);
                try
                {
                    var state = await browser.CoreWebView2.ExecuteScriptAsync("`${location.origin}|${document.readyState}|${document.getElementById('root')?.childElementCount ?? 0}`").ConfigureAwait(true);
                    if (state.Contains("https://chunkpilot.local|complete|", StringComparison.Ordinal) && !state.EndsWith("|0\"", StringComparison.Ordinal))
                        return;
                }
                catch (COMException)
                {
                    // The renderer is between documents. Keep the bounded readiness check alive.
                }
            }
            throw new TimeoutException($"The fixture '{pageName}/{tabName ?? stageName ?? "root"}' did not render within 20 seconds.");
        }

        private async Task CaptureReviewSetAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            var captures = new (string Name, string Fixture, string Page, string? Tab, string? Stage, string? Mode, bool Dirty, double Width, double Height)[]
            {
                ("dashboard-zero", "zero", "dashboard", null, null, null, false, 1280, 820),
                ("dashboard-several", "several", "dashboard", null, null, null, false, 1280, 820),
                ("overview-running", "running", "servers", "overview", null, null, false, 1280, 820),
                ("overview-stopped", "stopped", "servers", "overview", null, null, false, 1280, 820),
                ("health-attention", "attention", "servers", "overview", null, null, false, 1280, 820),
                ("server-menu", "running", "servers", "overview", null, "menu", false, 1280, 820),
                ("share", "running", "servers", "overview", null, "share", false, 1280, 820),
                ("connectivity-local", "running", "servers", "overview", null, "connectivity-local", false, 1280, 820),
                ("connectivity-pending", "running", "servers", "settings", null, "connectivity-pending", false, 1280, 820),
                ("connectivity-owned", "running", "servers", "settings", null, "share-unverified", false, 1280, 820),
                ("connectivity-public", "running", "servers", "overview", null, "connectivity-public", false, 1280, 820),
                ("connectivity-failure", "running", "servers", "overview", null, "connectivity-failure", false, 1280, 820),
                ("console-wrapped", "running", "servers", "console", null, null, false, 1280, 820),
                ("console-unwrapped", "running", "servers", "console", null, "console-unwrapped", false, 1280, 820),
                ("players", "running", "servers", "players", null, null, false, 1280, 820),
                ("files", "running", "servers", "files", null, null, false, 1280, 820),
                ("backups", "running", "servers", "backups", null, null, false, 1280, 820),
                ("versions", "running", "servers", "versions", null, null, false, 1280, 820),
                ("server-appearance", "running", "servers", "settings", null, null, false, 1280, 820),
                ("icon-editor", "running", "servers", "settings", null, "icon-editor", false, 1280, 820),
                ("motd-rich", "running", "servers", "settings", null, "motd-rich", false, 1280, 820),
                ("motd-raw", "running", "servers", "settings", null, "motd-raw", false, 1280, 820),
                ("global-settings", "running", "settings", null, null, null, false, 1280, 820),
                ("help-center", "running", "settings", null, null, null, false, 1280, 820),
                ("create-game", "running", "create", null, "0", null, false, 1280, 820),
                ("create-world-upload", "running", "create", null, "4", null, false, 1280, 820),
                ("create-paper-game", "running", "create", null, "0", "paper", false, 1280, 820),
                ("create-paper-version", "running", "create", null, "1", "paper", false, 1280, 820),
                ("create-paper-review", "running", "create", null, "6", "paper", false, 1280, 820),
                ("create-fabric-version", "running", "create", null, "1", "fabric", false, 1280, 820),
                ("create-fabric-review", "running", "create", null, "6", "fabric", false, 1280, 820),
                ("create-neoforge-version", "running", "create", null, "1", "neoforge", false, 1280, 820),
                ("create-neoforge-review", "running", "create", null, "6", "neoforge", false, 1280, 820),
                ("create-forge-version", "running", "create", null, "1", "forge", false, 1280, 820),
                ("create-quilt-version", "running", "create", null, "1", "quilt", false, 1280, 820),
                ("create-modpack-version", "running", "create", null, "1", "modpack", false, 1280, 820),
                ("modpack-installed", "modpack", "servers", "content", null, null, false, 1280, 820),
                ("create-performance", "running", "create", null, "2", null, false, 1280, 820),
                ("create-review", "running", "create", null, "6", null, false, 1280, 820),
                ("plugins-installed", "plugins", "servers", "content", null, "plugins-installed", false, 1280, 820),
                ("plugins-updates", "plugins", "servers", "content", null, "plugins-updates", false, 1280, 820),
                ("plugin-config-simple", "plugins", "servers", "content", null, "plugins-installed", false, 1280, 820),
                ("plugin-config-raw", "plugins", "servers", "content", null, "plugins-installed", false, 1280, 820),
                ("mods-installed", "fabric", "servers", "content", null, "mods-installed", false, 1280, 820),
                ("mods-browse", "fabric", "servers", "content", null, "mods-browse", false, 1280, 820),
                ("mods-updates", "neoforge", "servers", "content", null, "mods-updates", false, 1280, 820),
                ("overview-narrow-1100x700", "running", "servers", "overview", null, null, false, 1100, 700),
                ("server-list-long-names", "longnames", "dashboard", null, null, null, false, 920, 700),
                ("dashboard-large-1440x900", "several", "dashboard", null, null, null, false, 1440, 900),
                // Keep the dirty state last: its real beforeunload guard intentionally blocks a
                // subsequent navigation, while still allowing the capture itself to be reviewed.
                ("server-settings-dirty", "running", "servers", "settings", null, null, true, 1280, 820)
            };
            var log = new List<string>();
            foreach (var capture in captures)
            {
                Width = capture.Width;
                Height = capture.Height;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                await NavigateAsync(capture.Fixture, capture.Page, capture.Tab, capture.Stage,
                    capture.Name is "connectivity-pending" or "connectivity-owned" ? "Connectivity"
                        : capture.Name == "help-center" ? "Help & troubleshooting"
                        : null,
                    capture.Mode, capture.Dirty).ConfigureAwait(true);
                if (capture.Name == "files-editor")
                {
                    await Task.Delay(250).ConfigureAwait(true);
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "[...document.querySelectorAll('button')].find(button => button.textContent?.includes('server.properties'))?.click()").ConfigureAwait(true);
                }
                if (capture.Name is "plugin-config-simple" or "plugin-config-raw")
                {
                    await Task.Delay(250).ConfigureAwait(true);
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "[...document.querySelectorAll('button')].find(button => button.textContent?.trim() === 'Configure')?.click()").ConfigureAwait(true);
                    await Task.Delay(250).ConfigureAwait(true);
                    if (capture.Name == "plugin-config-raw")
                        _ = await browser.CoreWebView2.ExecuteScriptAsync(
                            "[...document.querySelectorAll('button')].find(button => button.textContent?.trim() === 'Raw')?.click()").ConfigureAwait(true);
                }
                if (capture.Name == "mods-browse")
                {
                    await Task.Delay(200).ConfigureAwait(true);
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "(() => { const input = document.querySelector('input[aria-label=\"Search official Modrinth mods\"]'); if (!input) return; const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set; setter.call(input, 'lithium'); input.dispatchEvent(new Event('input', { bubbles: true })); [...document.querySelectorAll('button')].find(button => button.textContent?.trim() === 'Search')?.click(); })()").ConfigureAwait(true);
                    await Task.Delay(250).ConfigureAwait(true);
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "[...document.querySelectorAll('button')].find(button => button.textContent?.includes('Lithium'))?.click()").ConfigureAwait(true);
                }
                if (capture.Name is "create-fabric-version" or "create-neoforge-version" or
                    "create-forge-version" or "create-quilt-version")
                {
                    // The loader catalog is supplied asynchronously after the base Minecraft
                    // catalog. Wait for the exact-version picker instead of capturing the
                    // transient base-catalog-only state on slower packaged starts.
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        var ready = await browser.CoreWebView2.ExecuteScriptAsync(
                            "Boolean(document.getElementById('loader-build-heading'))").ConfigureAwait(true);
                        if (string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase))
                            break;
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "document.getElementById('loader-build-heading')?.scrollIntoView({ block: 'center' })").ConfigureAwait(true);
                }
                if (capture.Name == "create-modpack-version")
                {
                    for (var attempt = 0; attempt < 30; attempt++)
                    {
                        var selected = await browser.CoreWebView2.ExecuteScriptAsync(
                            "(() => { const button = [...document.querySelectorAll('button')].find(candidate => candidate.textContent?.includes('Copper Trails')); if (!button) return false; button.click(); return true; })()").ConfigureAwait(true);
                        if (string.Equals(selected, "true", StringComparison.OrdinalIgnoreCase))
                            break;
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        var ready = await browser.CoreWebView2.ExecuteScriptAsync(
                            "document.body.innerText.includes('Exact release')").ConfigureAwait(true);
                        if (string.Equals(ready, "true", StringComparison.OrdinalIgnoreCase))
                            break;
                        await Task.Delay(100).ConfigureAwait(true);
                    }
                }
                if (capture.Name == "create-world-upload")
                {
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "[...document.querySelectorAll('button')].find(button => button.textContent?.includes('Upload World'))?.click()").ConfigureAwait(true);
                    await Task.Delay(100).ConfigureAwait(true);
                    _ = await browser.CoreWebView2.ExecuteScriptAsync(
                        "[...document.querySelectorAll('button')].find(button => button.textContent?.includes('Choose world folder'))?.click()").ConfigureAwait(true);
                }
                await Task.Delay(250).ConfigureAwait(true);
                var path = Path.Combine(directory, capture.Name + ".png");
                await using var stream = File.Create(path);
                await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream).ConfigureAwait(true);
                log.Add($"{capture.Name}\t{capture.Width}x{capture.Height}\t{new FileInfo(path).Length}");
            }
            File.WriteAllLines(Path.Combine(directory, "capture-set.log"), log);
        }

        private static void Configure(CoreWebView2 core)
        {
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.NavigationStarting += (_, args) =>
            {
                if (!WebUiProtocol.IsTrustedSource(args.Uri))
                    args.Cancel = true;
            };
        }
    }
}
